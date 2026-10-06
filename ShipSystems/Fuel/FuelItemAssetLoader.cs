using System;
using System.IO;
using BepInEx;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelItemAssetLoader
    {
        // NOT ".lethalbundle": LethalLevelLoader auto-loads every *.lethalbundle in the
        // plugins folder, which double-loads this bundle and hard-fails the game load.
        internal const string BundleFileName = "y4ngz-fuel-items.bundle";
        internal const string PumpPrefabName = "FuelPump_Canister";
        internal const string LargeFuelPrefabName = "LargeFuelItem";

        private static bool _loaded;
        private static AssetBundle _bundle;
        private static GameObject _pumpPrefab;
        private static GameObject _largeFuelPrefab;

        internal static GameObject TryInstantiatePumpVisual(Transform parent)
        {
            EnsureLoaded();
            return InstantiateVisual(_pumpPrefab, PumpPrefabName, parent);
        }

        internal static GameObject TryInstantiateLargeFuelVisual(Transform parent)
        {
            EnsureLoaded();
            return InstantiateVisual(_largeFuelPrefab, LargeFuelPrefabName, parent);
        }

        private static GameObject InstantiateVisual(GameObject prefab, string visualName, Transform parent)
        {
            if (prefab == null)
                return null;

            GameObject instance = UnityEngine.Object.Instantiate(prefab, parent, false);
            instance.name = visualName + "_Visual";
            FuelRuntimeUtility.PrepareAuthoredVisual(instance, FuelRuntimeUtility.PropsLayer);
            return instance;
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;
            _bundle = FindLoadedBundleWithAsset(PumpPrefabName) ?? FindLoadedBundleWithAsset(LargeFuelPrefabName);
            if (_bundle != null)
            {
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.FuelItems] Using already-loaded fuel item bundle '{_bundle.name}'.");
            }
            else
            {
                string bundlePath = ResolveBundlePath();
                if (string.IsNullOrWhiteSpace(bundlePath))
                {
                    ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.FuelItems] Optional bundle '{BundleFileName}' was not found; using generated fallback fuel item visuals.");
                    return;
                }

                _bundle = AssetBundle.LoadFromFile(bundlePath);
                if (_bundle == null)
                {
                    ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.FuelItems] AssetBundle.LoadFromFile returned null for '{bundlePath}'; using generated fallback fuel item visuals.");
                    return;
                }

                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.FuelItems] Loaded fuel item bundle from '{bundlePath}'.");
            }

            _pumpPrefab = LoadAssetByName<GameObject>(_bundle, PumpPrefabName);
            _largeFuelPrefab = LoadAssetByName<GameObject>(_bundle, LargeFuelPrefabName);

            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.FuelItems] Fuel item visuals: pump={_pumpPrefab != null}, largeFuel={_largeFuelPrefab != null}.");
            if (_pumpPrefab == null)
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.FuelItems] Bundle did not contain '{PumpPrefabName}'; fuel pump will use fallback visuals.");
            if (_largeFuelPrefab == null)
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.FuelItems] Bundle did not contain '{LargeFuelPrefabName}'; large fuel loot will use fallback visuals.");
        }

        private static string ResolveBundlePath()
        {
            string dllDir = null;
            try
            {
                string assemblyLocation = typeof(ShipSystemsBootstrap).Assembly.Location;
                if (!string.IsNullOrWhiteSpace(assemblyLocation))
                    dllDir = Path.GetDirectoryName(assemblyLocation);
            }
            catch
            {
                dllDir = null;
            }

            string[] candidates =
            {
                !string.IsNullOrWhiteSpace(dllDir) ? Path.Combine(dllDir, BundleFileName) : null,
                // #613 task 2.1: this feature ships in its own package now, so the plugin
                // directory holding the payload is named after it. Both Gale layouts are
                // listed for the same reason the deploy target lists both. The legacy
                // y4ngz-Y4NGZCompany entry below stays: it is what finds the payload a
                // pre-#613 install left behind, on a profile that has not redeployed.
                Path.Combine(Paths.PluginPath, "Y4NGZShipSystems", BundleFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZShipSystems", BundleFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", BundleFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", BundleFileName),
                Path.Combine(Paths.PluginPath, BundleFileName)
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static AssetBundle FindLoadedBundleWithAsset(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
                return null;

            foreach (AssetBundle bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (bundle == null)
                    continue;

                try
                {
                    if (bundle.Contains(assetName) || BundleHasMatchingAsset(bundle, assetName))
                        return bundle;
                }
                catch
                {
                    // Best-effort scan only; keep probing other bundles.
                }
            }

            return null;
        }

        private static bool BundleHasMatchingAsset(AssetBundle bundle, string assetName)
        {
            string[] names = GetBundleAssetNames(bundle);
            for (int i = 0; i < names.Length; i++)
            {
                if (AssetNameMatches(names[i], assetName))
                    return true;
            }

            return false;
        }

        private static T LoadAssetByName<T>(AssetBundle bundle, string assetName) where T : UnityEngine.Object
        {
            if (bundle == null || string.IsNullOrWhiteSpace(assetName))
                return null;

            try
            {
                T direct = bundle.LoadAsset<T>(assetName);
                if (direct != null)
                    return direct;
            }
            catch
            {
                // Fall through to path/name scan.
            }

            string[] names = GetBundleAssetNames(bundle);
            for (int i = 0; i < names.Length; i++)
            {
                string candidate = names[i];
                if (!AssetNameMatches(candidate, assetName))
                    continue;

                try
                {
                    T asset = bundle.LoadAsset<T>(candidate);
                    if (asset != null)
                        return asset;
                }
                catch
                {
                    // Keep scanning.
                }
            }

            return null;
        }

        private static string[] GetBundleAssetNames(AssetBundle bundle)
        {
            try { return bundle != null ? bundle.GetAllAssetNames() : Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        private static bool AssetNameMatches(string bundleAssetName, string requestedName)
        {
            if (string.IsNullOrWhiteSpace(bundleAssetName) || string.IsNullOrWhiteSpace(requestedName))
                return false;

            string bundleLeaf = GetAssetLeaf(bundleAssetName);
            string requestLeaf = GetAssetLeaf(requestedName);
            return string.Equals(bundleLeaf, requestLeaf, StringComparison.OrdinalIgnoreCase)
                   || bundleAssetName.EndsWith("/" + requestedName, StringComparison.OrdinalIgnoreCase)
                   || bundleAssetName.EndsWith("/" + requestLeaf, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetAssetLeaf(string assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName))
                return string.Empty;

            string normalized = assetName.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            string leaf = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
            return Path.GetFileNameWithoutExtension(leaf);
        }
    }
}