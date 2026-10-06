using UnityEngine;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    // Temporary, reversible control of vanilla's scripted fall on EVERY peer.
    internal sealed class FuelChuteTransfer : MonoBehaviour
    {
        internal bool InProgress => !_finished && _item != null;
        private GrabbableObject _item;
        private Transform _chute;
        private Vector3 _start,_end,_lastSafe;
        private float _began;
        private System.Action _onEject;
        private bool _ejectionPlayed;
        private bool _rejected,_itemEnabled,_grabbable,_finished;
        private Rigidbody _body;
        private bool _wasKinematic,_hadCollisions;
        private Collider[] _colliders;
        private bool[] _colliderStates;
        internal void Begin(GrabbableObject item,Transform chute,Vector3 end,bool rejected,System.Action onEject=null)
        {
            _onEject=onEject; _ejectionPlayed=false;
            _item=item; _chute=chute; _start=chute.InverseTransformPoint(item.transform.position);
            _end=end; _lastSafe=item.transform.position; _rejected=rejected; _began=Time.unscaledTime;
            _itemEnabled=item.enabled; _grabbable=item.grabbable;
            _body=item.GetComponent<Rigidbody>();
            if(_body!=null){_wasKinematic=_body.isKinematic;_hadCollisions=_body.detectCollisions;_body.isKinematic=true;_body.detectCollisions=false;}
            _colliders=item.GetComponentsInChildren<Collider>(true); _colliderStates=new bool[_colliders.Length];
            for(int i=0;i<_colliders.Length;i++){_colliderStates[i]=_colliders[i].enabled;_colliders[i].enabled=false;}
            item.enabled=false; item.grabbable=false;
        }
        private void LateUpdate()
        {
            if(_finished || _item==null)return;
            if(_chute==null){Finish(_lastSafe);return;}
            float elapsed = Time.unscaledTime - _began;
            if (_rejected && !_ejectionPlayed && elapsed >= FuelChuteTransferMotion.EjectAt)
            {
                _ejectionPlayed = true;
                _onEject?.Invoke();
            }
            Vector3 p = FuelChuteTransferMotion.Position(_start, _end, _rejected, elapsed);
            _item.transform.position=_chute.TransformPoint(p);
            if(_rejected)_lastSafe=_chute.TransformPoint(_end);
            // Lost/disconnected peer cannot strand a pickup with its script disabled.
            if(Time.unscaledTime-_began>FuelChuteTransferMotion.RejectSeconds+1f)Finish(_lastSafe);
        }
        internal static void Place(GrabbableObject item,Vector3 position)
        {
            if(item==null || item.isHeld || item.playerHeldBy!=null)return;
            item.transform.position=position;
            item.startFallingPosition=item.targetFloorPosition=item.transform.localPosition;
            item.fallTime=1f; item.hasHitGround=true; item.reachedFloorTarget=true;
        }
        internal void Finish(Vector3 position)
        {
            if(_finished)return; _finished=true;
            if(_item!=null)
            {
                Place(_item,position);
                _item.enabled=_itemEnabled; _item.grabbable=_grabbable;
                if(_body!=null){_body.isKinematic=_wasKinematic;_body.detectCollisions=_hadCollisions;}
                for(int i=0;i<_colliders.Length;i++)if(_colliders[i]!=null)_colliders[i].enabled=_colliderStates[i];
            }
            Destroy(this);
        }
        private void OnDisable(){if(!_finished && _item!=null)Finish(_lastSafe);}
    }
}
