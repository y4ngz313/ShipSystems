using System;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelChuteAssetLoader
    {
        // NOT ".lethalbundle": LethalLevelLoader auto-loads every *.lethalbundle in the
        // plugins folder, which double-loads this bundle and hard-fails the game load.
        internal const string BundleFileName = "y4ngz-fuel-chute.bundle";
        internal const string ChutePrefabName = "FuelChute_Sliding";
        internal const string OpenCloseClipName = "FuelChute_OpenClose";
        internal const string HissLoopClipName = "FuelChute_ConstantHiss";
        internal const string FlameConsume1ClipName = "FuelChute_FlameConsume1";
        internal const string FlameConsume2ClipName = "FuelChute_FlameConsume2";

        private static bool _loaded;
        private static AssetBundle _bundle;
        private static GameObject _chutePrefab;
        private static AudioClip _openCloseClip;
        private static AudioClip _hissLoopClip;
        private static AudioClip _flameConsumeClip1;
        private static AudioClip _flameConsumeClip2;

        internal static GameObject TryInstantiateChuteVisual(Transform parent)
        {
            EnsureLoaded();
            if (_chutePrefab == null)
                return null;

            GameObject instance = UnityEngine.Object.Instantiate(_chutePrefab, parent, false);
            instance.name = ChutePrefabName + "_Visual";
            return instance;
        }

        internal static AudioClip TryGetOpenCloseClip()
        {
            EnsureLoaded();
            return _openCloseClip;
        }

        internal static AudioClip TryGetHissLoopClip()
        {
            EnsureLoaded();
            return _hissLoopClip;
        }

        internal static AudioClip TryGetConsumeClip(int index)
        {
            EnsureLoaded();
            return (index % 2) == 0 ? _flameConsumeClip1 : _flameConsumeClip2;
        }

        internal static GameObject TryInstantiateVanillaFlame(Transform parent)
        {
            EnsureLoaded();
            if (parent == null)
                return null;

            Transform source = FindBestVanillaFlameSource();
            if (source == null)
                return null;

            var instance = new GameObject("FuelChute_VanillaThruster");
            instance.transform.SetParent(parent, false);
            try
            {
                var flame = instance.AddComponent<VanillaChuteFlame>();
                flame.Initialize(source);
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.FuelChute] Native SpriteRenderer: source={source.name}, shader={flame.NativeShader}, controller={source.GetComponent<Animator>().runtimeAnimatorController.name}; original material drawn through stencil state.");
                return instance;
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning("[LGUShipSystems.FuelChute] Vanilla thruster setup failed: " + ex.Message);
                UnityEngine.Object.Destroy(instance); return null;
            }
        }

        private static Transform FindBestVanillaFlameSource()
        {
            // Verified in the installed game: HangarShip/ThrusterBackLeft/Flame (2)/ThrusterFlame.
            // Restrict the source to the player's ship; moon landing craft and fireplaces
            // also have flames, but are not this effect. Include inactive landed thrusters.
            Transform ship = StartOfRound.Instance?.elevatorTransform;
            if (ship == null) return null;
            foreach (Transform candidate in ship.GetComponentsInChildren<Transform>(true))
                if (candidate.name.StartsWith("ThrusterFlame", StringComparison.OrdinalIgnoreCase)
                    && !IsFuelChuteTransform(candidate) && candidate.GetComponent<SpriteRenderer>() != null
                    && candidate.GetComponent<Animator>() != null)
                    return candidate;
            return null;
        }

        private static bool ParentChainContains(Transform transform, string fragment)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if ((current.name ?? string.Empty).IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsFuelChuteTransform(Transform transform)
        {
            return ParentChainContains(transform, "FuelChute") || ParentChainContains(transform, "LGUShipSystems_FuelChute");
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;
            _bundle = FindLoadedBundleWithAsset(ChutePrefabName);
            if (_bundle == null)
            {
                string bundlePath = ResolveBundlePath();
                if (string.IsNullOrWhiteSpace(bundlePath))
                {
                    ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.FuelChute] Optional bundle '{BundleFileName}' was not found; using generated fallback chute visuals.");
                    return;
                }

                _bundle = AssetBundle.LoadFromFile(bundlePath);
                if (_bundle == null)
                {
                    ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.FuelChute] AssetBundle.LoadFromFile returned null for '{bundlePath}'; using generated fallback chute visuals.");
                    return;
                }

                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.FuelChute] Loaded fuel chute bundle from '{bundlePath}'.");
            }

            _chutePrefab = LoadAssetByName<GameObject>(_bundle, ChutePrefabName);
            _openCloseClip = LoadAssetByName<AudioClip>(_bundle, OpenCloseClipName);
            _hissLoopClip = LoadAssetByName<AudioClip>(_bundle, HissLoopClipName);
            _flameConsumeClip1 = LoadAssetByName<AudioClip>(_bundle, FlameConsume1ClipName);
            _flameConsumeClip2 = LoadAssetByName<AudioClip>(_bundle, FlameConsume2ClipName);

            if (_chutePrefab == null)
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.FuelChute] Bundle did not contain '{ChutePrefabName}'; using generated fallback chute mesh.");
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

            string bundleLeaf = Path.GetFileNameWithoutExtension(bundleAssetName).Replace('\\', '/');
            string requestLeaf = Path.GetFileNameWithoutExtension(requestedName).Replace('\\', '/');
            return string.Equals(bundleLeaf, requestLeaf, StringComparison.OrdinalIgnoreCase)
                   || bundleAssetName.EndsWith("/" + requestedName, StringComparison.OrdinalIgnoreCase)
                   || bundleAssetName.EndsWith("/" + requestLeaf, StringComparison.OrdinalIgnoreCase);
        }
    }
}
