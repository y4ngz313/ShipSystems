using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.ShipSystems.Layout;
namespace Y4NGZCompany.ShipSystems.Power
{
    // A single trigger on the Hittable/Enemies layer, separate from repair interaction.
    internal sealed class BatteryDamageReceiver : MonoBehaviour, IHittable
    {
        internal ExternalBatteryStation Station;
        public bool Hit(int force, Vector3 direction, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            if (force <= 0 || Station == null) return false;
            var local = GameNetworkManager.Instance?.localPlayerController;
            if (playerWhoHit != null && playerWhoHit != local) return false;
            Vector3 origin = playerWhoHit != null ? playerWhoHit.gameplayCamera.transform.position : transform.position;
            return ShipSystemsController.Instance?.RequestBatteryDamage(force, origin, playerWhoHit, false) == true;
        }
    }
    [HarmonyPatch]
    internal static class BatteryDamagePatches
    {
        // Vanilla shotgun skips IHittable targets without EnemyAICollisionDetect.
        [HarmonyPostfix, HarmonyPatch(typeof(ShotgunItem), nameof(ShotgunItem.ShootGun))]
        private static void Shotgun(ShotgunItem __instance, Vector3 shotgunPosition, Vector3 shotgunForward)
        {
            if (!__instance.IsOwner) return;
            ShipSystemsController.Instance?.HitBatteryWithShotgun(shotgunPosition, shotgunForward, __instance.playerHeldBy);
        }
        // Mines, lightning, eggs, grenades and other vanilla explosions share this entry.
        [HarmonyPostfix, HarmonyPatch(typeof(Landmine), nameof(Landmine.SpawnExplosion))]
        private static void Explosion(Vector3 explosionPosition, float killRange, float damageRange, int nonLethalDamage)
        {
            if (!ShipSystemsController.IsServerRuntime()) return;
            ShipSystemsController.Instance?.HitBatteryWithExplosion(explosionPosition, killRange, damageRange, nonLethalDamage);
        }
    }
}
