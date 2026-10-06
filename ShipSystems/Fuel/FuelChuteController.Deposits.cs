using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Layout;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal sealed partial class FuelChuteController
    {
        private BoxCollider _lidSupport;
        private GrabbableObject _activeDeposit;
        private int _transferSerial;
        private readonly Dictionary<int,GrabbableObject> _rejectedItems = new Dictionary<int,GrabbableObject>();
        private readonly List<int> _rejectionCleanup = new List<int>();
        private void EnsureLidSupport()
        {
            if (_lidSupport != null) return;
            var lid = new GameObject("FuelChute_LidSupport");
            lid.transform.SetParent(transform,false); lid.layer=8;
            _lidSupport=lid.AddComponent<BoxCollider>();
            _lidSupport.center=new Vector3(0f,.022f,0f);
            _lidSupport.size=new Vector3(1.12f,.025f,1.12f);
        }
        private static bool CanDeposit(GrabbableObject item) => item != null && item.enabled && item.grabbable
            && !item.isHeld && !item.isPocketed && !item.heldByPlayerOnServer && item.playerHeldBy == null
            && !(item is RagdollGrabbableObject);
        private bool IsAtMouth(GrabbableObject item)
        {
            Vector3 p=transform.InverseTransformPoint(item.transform.position);
            if(p.x*p.x+p.z*p.z>.48f*.48f || p.y<-.45f || p.y>.8f)return false;
            // Physical support/contact, not the oversized scan trigger or a nearby center.
            if(item.propColliders==null)return false;
            foreach(var collider in item.propColliders)
            {
                if(collider==null || collider.isTrigger || !collider.enabled)continue;
                Vector3 bottom=collider.bounds.center-Vector3.up*collider.bounds.extents.y;
                float height=transform.InverseTransformPoint(bottom).y;
                if(height<=.13f && height>=-.6f)return true;
            }
            return false;
        }
        private void ClearRejectedItems()
        {
            _rejectionCleanup.Clear();
            foreach(var pair in _rejectedItems)
                if(pair.Value==null || !IsAtMouth(pair.Value))_rejectionCleanup.Add(pair.Key);
            foreach(int id in _rejectionCleanup)_rejectedItems.Remove(id);
        }
        private Vector3 SafeReturnPosition(GrabbableObject item)
        {
            // Check real floor outside the intake; use the existing item's floor offset.
            float radius=.12f;
            if(item.propColliders!=null)foreach(var c in item.propColliders)
                if(c!=null && !c.isTrigger)radius=Mathf.Max(radius,Mathf.Max(c.bounds.extents.x,c.bounds.extents.z));
            radius=Mathf.Clamp(radius,.12f,.65f);
            for(int ring=0;ring<2;ring++)for(int i=0;i<8;i++)
            {
                float angle=i*Mathf.PI/4f;
                Vector3 candidate=transform.TransformPoint(new Vector3(Mathf.Sin(angle)*(1.05f+radius+ring*.5f),.5f,-Mathf.Cos(angle)*(1.05f+radius+ring*.5f)));
                if(!Physics.Raycast(candidate,Vector3.down,out RaycastHit hit,1.5f,268437760,QueryTriggerInteraction.Ignore)
                    || hit.normal.y<.7f || hit.transform.IsChildOf(transform))continue;
                Vector3 center=hit.point+Vector3.up*(radius+.03f);
                bool blocked=false;
                foreach(var c in Physics.OverlapSphere(center,radius*.95f,~0,QueryTriggerInteraction.Ignore))
                    if(c!=hit.collider && !c.transform.IsChildOf(item.transform) && !c.transform.IsChildOf(transform)) { blocked=true;break; }
                if(!blocked)return transform.InverseTransformPoint(hit.point+Vector3.up*(item.itemProperties.verticalOffset+.02f));
            }
            // No safe launch: leave it on the reachable near rim, outside the mouth.
            return new Vector3(0f,.06f+item.itemProperties.verticalOffset,-.67f);
        }
        private IEnumerator TransferItemRoutine(GrabbableObject item,bool rejected,float fuelValue)
        {
            int serial=++_transferSerial;
            bool consumed=false;
            Vector3 safe=SafeReturnPosition(item);
            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.ChuteTransfer] {(rejected ? "Reject" : "Accept")} {item.itemProperties.itemName}, transfer={serial}.");
            try
            {
                Vector3 destination=rejected ? safe : FuelItemSinkLocalPosition;
                _owner.BroadcastChuteTransfer(item,serial,destination,rejected,false);
                yield return new WaitForSecondsRealtime(rejected ? FuelChuteTransferMotion.RejectSeconds : FuelChuteTransferMotion.SinkSeconds);
                if(item==null)yield break;
                if(!rejected && !item.isHeld && !item.heldByPlayerOnServer && item.playerHeldBy==null && !item.isPocketed
                    && _isOpen && ShipSystemsController.IsFuelSystemEnabled() && _owner!=null
                    && _owner.Fuel < _owner.FuelCapacity-.001f)
                {
                    float added=_owner.AddFuel(fuelValue,item.itemProperties.itemName);
                    if(added>0f)
                    {
                        consumed=true;
                        _owner.NotifyFuelChuteFuelDeposited();
                        _owner.BroadcastFuelChuteFeedback(Mathf.RoundToInt(added),Mathf.RoundToInt(fuelValue));
                        var net=item.GetComponent<NetworkObject>();
                        if(net!=null && net.IsSpawned)net.Despawn(true); else Destroy(item.gameObject);
                    }
                }
            }
            finally
            {
                if(!consumed && item!=null)
                {
                    _rejectedItems[item.GetInstanceID()]=item;
                    _owner?.BroadcastChuteTransfer(item,serial,safe,true,true);
                    // Also recover locally if the controller/session disappeared.
                    item.GetComponent<FuelChuteTransfer>()?.Finish(transform.TransformPoint(safe));
                }
                _activeDeposit=null;
            }
        }
        internal void ApplyItemTransfer(GrabbableObject item,int serial,Vector3 destination,bool rejected,bool finish)
        {
            if(item==null)return;
            var transfer=item.GetComponent<FuelChuteTransfer>();
            if(finish)
            {
                if(transfer!=null)transfer.Finish(transform.TransformPoint(destination));
                else FuelChuteTransfer.Place(item,transform.TransformPoint(destination));
                return;
            }
            if(transfer==null)transfer=item.gameObject.AddComponent<FuelChuteTransfer>();
            transfer.Begin(item,transform,destination,rejected,rejected ? (System.Action)PlayRejectionSound : null);
        }
        private void PlayRejectionSound()
        {
            AudioClip clip = _owner != null ? _owner.FuelChuteRejectClip : null;
            if (_audio != null && clip != null) _audio.PlayOneShot(clip, .7f);
        }
        private void OnDisable()
        {
            if(_activeDeposit!=null)
            {
                Vector3 safe=SafeReturnPosition(_activeDeposit);
                _owner?.BroadcastChuteTransfer(_activeDeposit,_transferSerial,safe,true,true);
            }
            StopAllCoroutines();
            _activeDeposit=null;
        }
    }
}
