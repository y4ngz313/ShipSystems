using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelRefineryAssetLoader
    {
        // NOT ".lethalbundle": LethalLevelLoader auto-loads every *.lethalbundle in the
        // plugins folder, which double-loads this bundle and hard-fails the game load.
        internal const string BundleFileName = "y4ngz-oil-refinery.bundle";
        internal const string RefineryPrefabName = "OilRefinery_Pumpjack";

        private const string BeamPrefix = "OilRefinery_Cube_006_";
        private const string RodPrefix = "OilRefinery_Cylinder_044_";

        // The bundle builder bakes the platform-contact offset into ModelRoot, so
        // at runtime the measured offset is normally ~0 (a legitimate no-op). These
        // names/threshold let the runtime alignment stay loud and robust when the
        // deployed bundle differs from what the builder assumes.
        private const string ModelRootName = "ModelRoot";
        private const string PlatformMeshName = "Cube_119";
        private const string AuthoredSnapPointName = "PumpSnapPoint";
        private const float PlatformContactEpsilon = 0.01f;

        private static bool _loaded;
        private static AssetBundle _bundle;
        private static GameObject _refineryPrefab;
        private static Mesh[] _beamFrames = Array.Empty<Mesh>();
        private static Mesh[] _rodFrames = Array.Empty<Mesh>();

        internal static GameObject TryInstantiateRefineryVisual(Transform parent)
        {
            EnsureLoaded();
            if (_refineryPrefab == null)
                return null;

            GameObject instance = UnityEngine.Object.Instantiate(_refineryPrefab, parent, false);
            instance.name = RefineryPrefabName + "_Visual";
            RemoveDisconnectedGear(instance.transform);
            ApplyPlatformContactOffset(instance.transform);
            FuelRefineryAnimationController animation = instance.GetComponent<FuelRefineryAnimationController>()
                ?? instance.AddComponent<FuelRefineryAnimationController>();
            animation.Configure(_beamFrames, _rodFrames);
            return instance;
        }

        // Grounds the instantiated visual so the platform/base mesh rests on the
        // rig root's Y=0 plane. This is always loud: every exit path logs exactly
        // one line stating the chosen strategy (or why it is a no-op) plus the
        // real hierarchy so a mismatched deployed bundle is diagnosable in
        // Player.log. It never silently returns.
        private static void ApplyPlatformContactOffset(Transform root)
        {
            if (root == null)
            {
                ShipSystemsBootstrap.Log?.LogWarning("[LGUShipSystems.Refinery] Platform contact offset skipped: null visual root.");
                return;
            }

            Transform modelRoot = FindChild(root, ModelRootName);
            string visualChildren = DescribeDirectChildren(root);
            string modelChildren = DescribeDirectChildren(modelRoot);
            if (modelRoot == null)
            {
                // No dedicated ModelRoot: shift the whole visual instead. Its anchor
                // children ride along automatically, so nothing separate to move.
                modelRoot = root;
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Refinery] Platform contact: no '{ModelRootName}' child; using the visual root as the shift target. visualChildren=[{visualChildren}].");
            }

            Transform authoredSnap = FindChild(root, AuthoredSnapPointName);

            // Strategy 1 (primary): align the platform/base mesh (Cube_119) bottom.
            Transform platform = FindChild(root, PlatformMeshName);
            if (platform != null && TryGetLocalBoundsMinY(root, platform, out float platformBottomY))
            {
                if (Mathf.Abs(platformBottomY) <= PlatformContactEpsilon)
                {
                    ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Platform contact no-op: '{PlatformMeshName}' bottom already at visual Y={platformBottomY:0.0000} (<= {PlatformContactEpsilon:0.###}; builder pre-baked the offset). visualChildren=[{visualChildren}], modelRootChildren=[{modelChildren}].");
                    return;
                }

                ShiftModelAndAnchors(root, modelRoot, platformBottomY);
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Applied platform contact offset {platformBottomY:0.0000} via '{PlatformMeshName}' bottom. visualChildren=[{visualChildren}], modelRootChildren=[{modelChildren}].");
                return;
            }

            // Strategy 2 (fallback): the authored PumpSnapPoint is baked at ground
            // level by the builder, so use its visual-local height as the reference.
            if (authoredSnap != null)
            {
                float snapY = root.InverseTransformPoint(authoredSnap.position).y;
                if (Mathf.Abs(snapY) <= PlatformContactEpsilon)
                {
                    ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Platform contact no-op: '{PlatformMeshName}' not measurable; authored '{AuthoredSnapPointName}' already at visual Y={snapY:0.0000}. visualChildren=[{visualChildren}], modelRootChildren=[{modelChildren}].");
                    return;
                }

                ShiftModelAndAnchors(root, modelRoot, snapY);
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Applied platform contact offset {snapY:0.0000} via authored '{AuthoredSnapPointName}' ground reference ('{PlatformMeshName}' not found). visualChildren=[{visualChildren}], modelRootChildren=[{modelChildren}].");
                return;
            }

            // Strategy 3 (last resort): largest-horizontal-footprint mesh bottom.
            if (TryGetLargestFootprintMeshBottomY(root, modelRoot, out float footprintBottomY, out string footprintName))
            {
                if (Mathf.Abs(footprintBottomY) <= PlatformContactEpsilon)
                {
                    ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Platform contact no-op: '{PlatformMeshName}' and authored '{AuthoredSnapPointName}' missing; largest-footprint mesh '{footprintName}' already at visual Y={footprintBottomY:0.0000}. visualChildren=[{visualChildren}], modelRootChildren=[{modelChildren}].");
                    return;
                }

                ShiftModelAndAnchors(root, modelRoot, footprintBottomY);
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Applied platform contact offset {footprintBottomY:0.0000} via largest-footprint mesh '{footprintName}' ('{PlatformMeshName}' and authored snap missing). visualChildren=[{visualChildren}], modelRootChildren=[{modelChildren}].");
                return;
            }

            ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Refinery] Platform contact offset unresolved: no '{PlatformMeshName}', no authored '{AuthoredSnapPointName}', no measurable mesh footprint. visualChildren=[{visualChildren}], modelRootChildren=[{modelChildren}].");
        }

        // Shifts the geometry down by shiftY, and moves the authored anchor empties
        // (e.g. PumpSnapPoint) that are NOT under ModelRoot by the same amount so the
        // mesh and its anchors stay coherent. When there is no dedicated ModelRoot the
        // visual itself is shifted and its children ride along, so anchors are skipped.
        private static void ShiftModelAndAnchors(Transform visual, Transform modelRoot, float shiftY)
        {
            Vector3 modelLocal = modelRoot.localPosition;
            modelLocal.y -= shiftY;
            modelRoot.localPosition = modelLocal;

            if (modelRoot == visual)
                return;

            for (int i = 0; i < visual.childCount; i++)
            {
                Transform child = visual.GetChild(i);
                if (child == null || child == modelRoot || !IsAnchorEmpty(child))
                    continue;

                Vector3 childLocal = child.localPosition;
                childLocal.y -= shiftY;
                child.localPosition = childLocal;
            }
        }

        // Anchor empties carry no geometry or colliders (authored PumpSnapPoint and
        // similar); renderers/colliders belong to the model and must not double-shift.
        private static bool IsAnchorEmpty(Transform child)
        {
            return child != null
                   && child.GetComponentInChildren<Renderer>(true) == null
                   && child.GetComponentInChildren<Collider>(true) == null;
        }

        private static bool TryGetLargestFootprintMeshBottomY(Transform root, Transform searchScope, out float bottomY, out string meshName)
        {
            bottomY = float.MaxValue;
            meshName = null;
            if (root == null)
                return false;

            Transform scope = searchScope != null ? searchScope : root;
            MeshFilter[] filters = scope.GetComponentsInChildren<MeshFilter>(true);
            float bestFootprint = -1f;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                    continue;

                Bounds bounds = mesh.bounds;
                Vector3 min = bounds.min;
                Vector3 max = bounds.max;
                Vector3[] corners =
                {
                    new Vector3(min.x, min.y, min.z),
                    new Vector3(min.x, min.y, max.z),
                    new Vector3(min.x, max.y, min.z),
                    new Vector3(min.x, max.y, max.z),
                    new Vector3(max.x, min.y, min.z),
                    new Vector3(max.x, min.y, max.z),
                    new Vector3(max.x, max.y, min.z),
                    new Vector3(max.x, max.y, max.z),
                };

                float localMinX = float.MaxValue;
                float localMaxX = float.MinValue;
                float localMinZ = float.MaxValue;
                float localMaxZ = float.MinValue;
                float localMinY = float.MaxValue;
                for (int c = 0; c < corners.Length; c++)
                {
                    Vector3 local = root.InverseTransformPoint(filter.transform.TransformPoint(corners[c]));
                    localMinX = Mathf.Min(localMinX, local.x);
                    localMaxX = Mathf.Max(localMaxX, local.x);
                    localMinZ = Mathf.Min(localMinZ, local.z);
                    localMaxZ = Mathf.Max(localMaxZ, local.z);
                    localMinY = Mathf.Min(localMinY, local.y);
                }

                float footprint = (localMaxX - localMinX) * (localMaxZ - localMinZ);
                if (footprint > bestFootprint)
                {
                    bestFootprint = footprint;
                    bottomY = localMinY;
                    meshName = filter.gameObject.name;
                }
            }

            return meshName != null;
        }

        private static string DescribeDirectChildren(Transform parent)
        {
            if (parent == null)
                return "<none>";
            if (parent.childCount == 0)
                return "<empty>";

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < parent.childCount; i++)
            {
                if (i > 0)
                    builder.Append(", ");
                Transform child = parent.GetChild(i);
                builder.Append(child != null ? child.name : "<null>");
            }

            return builder.ToString();
        }

        private static bool TryGetLocalBoundsMinY(Transform root, Transform target, out float minY)
        {
            minY = float.MaxValue;
            if (root == null || target == null)
                return false;

            bool found = false;
            MeshFilter[] filters = target.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                    continue;

                Bounds bounds = mesh.bounds;
                Vector3 min = bounds.min;
                Vector3 max = bounds.max;
                Vector3[] corners =
                {
                    new Vector3(min.x, min.y, min.z),
                    new Vector3(min.x, min.y, max.z),
                    new Vector3(min.x, max.y, min.z),
                    new Vector3(min.x, max.y, max.z),
                    new Vector3(max.x, min.y, min.z),
                    new Vector3(max.x, min.y, max.z),
                    new Vector3(max.x, max.y, min.z),
                    new Vector3(max.x, max.y, max.z),
                };

                for (int cornerIndex = 0; cornerIndex < corners.Length; cornerIndex++)
                {
                    Vector3 world = filter.transform.TransformPoint(corners[cornerIndex]);
                    float localY = root.InverseTransformPoint(world).y;
                    minY = Mathf.Min(minY, localY);
                    found = true;
                }
            }

            return found;
        }

        private static void RemoveDisconnectedGear(Transform root)
        {
            Transform gear = FindChild(root, "Gear");
            if (gear == null)
                return;

            ShipSystemsBootstrap.Log?.LogInfo("[LGUShipSystems.Refinery] Removed disconnected Gear mesh from oil refinery visual.");
            UnityEngine.Object.Destroy(gear.gameObject);
        }

        private static Transform FindChild(Transform root, string childName)
        {
            if (root == null || string.IsNullOrWhiteSpace(childName))
                return null;

            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && string.Equals(child.name, childName, StringComparison.OrdinalIgnoreCase))
                    return child;
            }

            return null;
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;

            _loaded = true;
            _bundle = FindLoadedBundleWithAsset(RefineryPrefabName);
            if (_bundle != null)
            {
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Using already-loaded oil refinery bundle '{_bundle.name}'.");
            }
            else
            {
                string bundlePath = ResolveBundlePath();
                if (string.IsNullOrWhiteSpace(bundlePath))
                {
                    ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Refinery] Optional bundle '{BundleFileName}' was not found; using generated fallback refinery visuals.");
                    return;
                }

                _bundle = AssetBundle.LoadFromFile(bundlePath);
                if (_bundle == null)
                {
                    ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Refinery] AssetBundle.LoadFromFile returned null for '{bundlePath}'; using generated fallback refinery visuals.");
                    return;
                }

                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Loaded oil refinery bundle from '{bundlePath}'.");
            }

            _refineryPrefab = LoadAssetByName<GameObject>(_bundle, RefineryPrefabName);
            Mesh[] meshes = LoadAllMeshes(_bundle);
            _beamFrames = FindFrameMeshes(_bundle, meshes, BeamPrefix);
            _rodFrames = FindFrameMeshes(_bundle, meshes, RodPrefix);

            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Refinery animation frames: prefab={_refineryPrefab != null}, meshAssets={meshes.Length}, beamFrames={_beamFrames.Length}, rodFrames={_rodFrames.Length}.");
            if (_refineryPrefab == null)
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Refinery] Bundle did not contain '{RefineryPrefabName}'; using generated fallback mesh.");
            if (_beamFrames.Length == 0 || _rodFrames.Length == 0)
                ShipSystemsBootstrap.Log?.LogWarning("[LGUShipSystems.Refinery] Bundle did not contain refinery animation frame meshes; visual will be static.");
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

        private static Mesh[] LoadAllMeshes(AssetBundle bundle)
        {
            try { return bundle != null ? bundle.LoadAllAssets<Mesh>() : Array.Empty<Mesh>(); }
            catch { return Array.Empty<Mesh>(); }
        }

        private static Mesh[] FindFrameMeshes(AssetBundle bundle, Mesh[] meshes, string prefix)
        {
            List<Mesh> frames = new List<Mesh>();
            HashSet<string> frameNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (meshes != null)
            {
                for (int i = 0; i < meshes.Length; i++)
                    AddFrameMesh(frames, frameNames, meshes[i], prefix);
            }

            string[] assetNames = GetBundleAssetNames(bundle);
            for (int i = 0; i < assetNames.Length; i++)
            {
                string assetName = assetNames[i];
                string leaf = GetAssetLeaf(assetName);
                if (!leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    AddFrameMesh(frames, frameNames, bundle.LoadAsset<Mesh>(assetName), prefix);
                }
                catch
                {
                    // Keep scanning; one bad asset should not disable the entire refinery visual.
                }
            }

            frames.Sort(CompareFrameMeshesByFrameIndex);
            return frames.ToArray();
        }

        private static int CompareFrameMeshesByFrameIndex(Mesh left, Mesh right)
        {
            bool leftHasFrame = TryGetFrameIndex(left?.name, out int leftFrame);
            bool rightHasFrame = TryGetFrameIndex(right?.name, out int rightFrame);
            if (leftHasFrame && rightHasFrame)
            {
                int frameCompare = leftFrame.CompareTo(rightFrame);
                if (frameCompare != 0)
                    return frameCompare;
            }
            else if (leftHasFrame)
            {
                return -1;
            }
            else if (rightHasFrame)
            {
                return 1;
            }

            return string.Compare(left?.name, right?.name, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetFrameIndex(string meshName, out int frameIndex)
        {
            frameIndex = -1;
            if (string.IsNullOrWhiteSpace(meshName))
                return false;

            int underscore = meshName.LastIndexOf('_');
            if (underscore < 0 || underscore >= meshName.Length - 1)
                return false;

            return int.TryParse(meshName.Substring(underscore + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out frameIndex);
        }

        private static void AddFrameMesh(List<Mesh> frames, HashSet<string> frameNames, Mesh mesh, string prefix)
        {
            if (mesh == null || string.IsNullOrWhiteSpace(mesh.name))
                return;
            if (!mesh.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return;
            if (!frameNames.Add(mesh.name))
                return;

            frames.Add(mesh);
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