using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
namespace Y4NGZCompany.ShipSystems.Layout
{
    internal sealed partial class ShipSystemsController
    {
        private const string MsgBatteryDamage = "LGUShipSystems.BatteryDamage.v1";
        private const string MsgBatteryDamageFeedback = "LGUShipSystems.BatteryDamageFeedback.v1";
        private readonly Dictionary<ulong,float> _lastPlayerBatteryHit = new Dictionary<ulong,float>();
        internal bool RequestBatteryDamage(int force, Vector3 origin, PlayerControllerB player, bool shotgun)
        {
            if (_batteryStation == null || force <= 0) return false;
            if (IsServerRuntime()) return ApplyPlayerBatteryHit(force, origin, player, shotgun);
            if (player == null || player != GameNetworkManager.Instance?.localPlayerController) return false;
            using (var writer = new FastBufferWriter(32, Allocator.Temp))
            { writer.WriteValueSafe(force); writer.WriteValueSafe(origin); writer.WriteValueSafe(shotgun); SendToServer(MsgBatteryDamage,writer); }
            return true;
        }
        private bool ApplyPlayerBatteryHit(int force, Vector3 origin, PlayerControllerB player, bool shotgun)
        {
            if (player != null)
            {
                if (player.isPlayerDead || !player.isPlayerControlled) return false;
                if (Vector3.Distance(origin, player.gameplayCamera.transform.position) > 2f) return false;
                if (shotgun && !(player.currentlyHeldObjectServer is ShotgunItem)) return false;
                ulong id = player.actualClientId;
                if (_lastPlayerBatteryHit.TryGetValue(id,out float last) && Time.unscaledTime-last < .10f) return false;
                if (!_batteryStation.CanHitFrom(origin,shotgun ? 15f : 3.5f)) return false;
                _lastPlayerBatteryHit[id] = Time.unscaledTime;
            }
            else if (!_batteryStation.CanHitFrom(origin,15f)) return false;
            return _batteryStation.ApplyDamage(Mathf.Clamp(force,1,shotgun ? 5 : 10),shotgun ? "gunfire" : "direct hit");
        }
        private void OnReceiveBatteryDamage(ulong sender, FastBufferReader reader)
        {
            if (!IsServer()) return;
            if (!reader.TryBeginRead(sizeof(int)+sizeof(float)*3+sizeof(bool))) return;
            reader.ReadValueSafe(out int force); reader.ReadValueSafe(out Vector3 origin); reader.ReadValueSafe(out bool shotgun);
            var players=StartOfRound.Instance?.allPlayerScripts;
            if(players==null)return;
            foreach(var player in players) if(player!=null && player.actualClientId==sender)
            { ApplyPlayerBatteryHit(force,origin,player,shotgun); return; }
        }
        internal void HitBatteryWithShotgun(Vector3 origin, Vector3 forward, PlayerControllerB player)
        {
            if (_batteryStation == null || !_batteryStation.DamageAvailable) return;
            // Geometry matches a close-range pellet cone; walls still occlude it.
            Vector3 point=_batteryStation.DamagePoint(origin);
            float distance=Vector3.Distance(origin,point);
            if(distance>15f || Vector3.Angle(forward,point-origin)>15f || !_batteryStation.CanHitFrom(origin,15f))return;
            RequestBatteryDamage(distance<3.7f ? 5 : distance<6f ? 3 : 2,origin,player,true);
        }
        internal void HitBatteryWithExplosion(Vector3 origin,float killRange,float damageRange,int damage)
        {
            if (_batteryStation == null || damageRange <= 0f || !_batteryStation.CanHitFrom(origin,Mathf.Max(killRange,damageRange)))return;
            float distance=Vector3.Distance(origin,_batteryStation.DamagePoint(origin));
            int amount=distance<=killRange ? 100 : Mathf.Clamp(Mathf.CeilToInt(damage/20f),1,10);
            if(damage>0 || distance<=killRange)_batteryStation.ApplyDamage(amount,"explosion");
        }
        internal void PlayBatteryDamageFeedback(bool critical)
        {
            PlayBatteryDamageFeedbackLocal(critical);
            using(var writer=new FastBufferWriter(1,Allocator.Temp))
            { writer.WriteValueSafe(critical); SendToAll(MsgBatteryDamageFeedback,writer); }
        }
        private void OnReceiveBatteryDamageFeedback(ulong sender,FastBufferReader reader)
        {
            if(IsServer() || !IsServerMessage(sender) || !reader.TryBeginRead(1))return;
            reader.ReadValueSafe(out bool critical); PlayBatteryDamageFeedbackLocal(critical);
        }
        private void PlayBatteryDamageFeedbackLocal(bool critical)
        {
            if(_batteryBroken)return;
            EnsureBatteryAudioClips(); PlayShipAudibleOneShot(_batteryThudClip,.8f);
            _batteryStation?.DuckWorkingLoop(1f); _batteryWarnings.Pulse(critical);
        }
    }
}
