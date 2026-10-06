using UnityEngine;
using Unity.Netcode;
using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    // Oil rigs are retired. Keep the lifecycle entry points for existing callers,
    // but never build/register a prefab or run placement/spawn/retry work.
    internal static class FuelSourceSpawner
    {
        // Legacy controller geometry retained only for cleanup compatibility.
        internal const float TerrainPlatformWidth = 7.4f;
        internal const float TerrainPlatformDepth = 4.9f;
        internal static bool Enabled => false;
        private static float _nextCleanup;
        internal static void EnsureRegistered() => RemoveExistingRigs();
        internal static bool TrySpawnCurrentLevelSource(bool logSkipped = true)
        {
            RemoveExistingRigs();
            return false;
        }
        internal static void ClearRoundSource()
        {
            _nextCleanup = 0f;
            RemoveExistingRigs();
        }
        internal static void Tick()
        {
            if (Time.unscaledTime < _nextCleanup) return;
            _nextCleanup = Time.unscaledTime + 5f;
            RemoveExistingRigs();
        }
        private static void RemoveExistingRigs()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.IsListening && !manager.IsServer) return;
            int removed = 0;
            foreach (FuelSourceController source in UnityEngine.Object.FindObjectsOfType<FuelSourceController>(true))
            {
                if (source == null || !source.gameObject.scene.IsValid() || !source.gameObject.scene.isLoaded) continue;
                NetworkObject network = source.GetComponent<NetworkObject>();
                if (network != null && network.IsSpawned)
                {
                    if (manager == null || !manager.IsServer) continue;
                    network.Despawn(true);
                }
                else UnityEngine.Object.Destroy(source.gameObject);
                removed++;
            }
            if (removed > 0) ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Fuel] Removed {removed} disabled oil rigs.");
        }
    }
}
