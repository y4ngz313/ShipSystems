using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.ShipSystems.Fuel;
namespace Y4NGZCompany.ShipSystems.Layout
{
    internal sealed partial class ShipSystemsController
    {
        private const string MsgChuteTransfer="LGUShipSystems.ChuteTransfer.v1";
        internal void BroadcastChuteTransfer(GrabbableObject item,int serial,Vector3 destination,bool rejected,bool finish)
        {
            if(!IsServerRuntime() || item==null)return;
            _fuelChute?.ApplyItemTransfer(item,serial,destination,rejected,finish);
            var net=item.GetComponent<NetworkObject>(); if(net==null || !net.IsSpawned)return;
            using(var writer=new FastBufferWriter(40,Allocator.Temp))
            {
                writer.WriteValueSafe(net.NetworkObjectId);writer.WriteValueSafe(serial);writer.WriteValueSafe(destination);
                writer.WriteValueSafe(rejected);writer.WriteValueSafe(finish);SendToAll(MsgChuteTransfer,writer);
            }
        }
        private void OnReceiveChuteTransfer(ulong sender,FastBufferReader reader)
        {
            if(IsServer() || !IsServerMessage(sender) || !reader.TryBeginRead(26))return;
            reader.ReadValueSafe(out ulong id);reader.ReadValueSafe(out int serial);reader.ReadValueSafe(out Vector3 destination);
            reader.ReadValueSafe(out bool rejected);reader.ReadValueSafe(out bool finish);
            var manager=NetworkManager.Singleton;
            if(manager?.SpawnManager==null || !manager.SpawnManager.SpawnedObjects.TryGetValue(id,out var net))return;
            _fuelChute?.ApplyItemTransfer(net.GetComponent<GrabbableObject>(),serial,destination,rejected,finish);
        }
    }
}
