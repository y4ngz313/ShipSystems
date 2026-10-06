using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
using Dawn;
#endif
using System.Security.Cryptography;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelRuntimeUtility
    {
        internal static int PropsLayer => ResolveLayer("Props", ResolveLayer("InteractableObject", 6));
        internal static int InteractLayer => ResolveLayer("InteractableObject", 6);
        internal static int ScanNodeLayer => ResolveLayer("ScanNode", 22);

        internal static int ResolveLayer(string name, int fallback)
        {
            int layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? layer : fallback;
        }

        internal static void SetTagSafe(GameObject obj, string tag)
        {
            if (obj == null)
                return;

            try
            {
                obj.tag = tag;
            }
            catch
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel] Tag '{tag}' was unavailable.");
            }
        }

        internal static void AssignStableNetworkHash(NetworkObject networkObject, string name)
        {
            if (networkObject == null)
                return;

            try
            {
                uint hash;
                using (MD5 md5 = MD5.Create())
                {
                    byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ShipSystemsBootstrap.PluginGuid + ".fuel." + name));
                    hash = BitConverter.ToUInt32(bytes, 0);
                }

                Type type = typeof(NetworkObject);
                PropertyInfo property = type.GetProperty("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(networkObject, hash);
                    return;
                }

                FieldInfo field = type.GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?? type.GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                field?.SetValue(networkObject, hash);
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel] Failed to assign NetworkObject hash for {name}: {ex.Message}");
            }
        }

        private static readonly HashSet<int> LethalLibRegisteredPrefabIds = new HashSet<int>();
        private static readonly HashSet<int> LethalLibRegisteredItemIds = new HashSet<int>();
        private static readonly HashSet<string> DawnRegisteredItemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoggedPrefabDiagnostics = new HashSet<string>(StringComparer.Ordinal);

        internal static void RegisterNetworkPrefab(GameObject prefab)
        {
            if (prefab == null)
                return;

            TryRegisterNetworkPrefabWithLethalLib(prefab);

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || manager.NetworkConfig == null)
                return;

            try
            {
                if (!manager.NetworkConfig.Prefabs.Contains(prefab))
                    manager.AddNetworkPrefab(prefab);
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel] NetworkPrefab registration failed for {prefab.name}: {ex.Message}");
            }
        }

        private static void TryRegisterNetworkPrefabWithLethalLib(GameObject prefab)
        {
            int id = prefab.GetInstanceID();
            if (LethalLibRegisteredPrefabIds.Contains(id))
                return;

            try
            {
                Type type = Type.GetType("LethalLib.Modules.NetworkPrefabs, LethalLib", throwOnError: false);
                MethodInfo method = type?.GetMethod("RegisterNetworkPrefab", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
                if (method == null)
                    return;

                method.Invoke(null, new object[] { prefab });
                LethalLibRegisteredPrefabIds.Add(id);
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel] LethalLib NetworkPrefab registration failed for {prefab.name}: {ex.Message}");
            }
        }

        internal static void RegisterItemMetadata(string itemKey, Item item)
        {
            if (item == null)
                return;

            TryRegisterItemWithLethalLib(item);
            RegisterDawnItemIfAvailable(itemKey, item);
        }

        private static void TryRegisterItemWithLethalLib(Item item)
        {
            if (item == null)
                return;

            int id = item.GetInstanceID();
            if (LethalLibRegisteredItemIds.Contains(id))
                return;

            try
            {
                Type type = Type.GetType("LethalLib.Modules.Items, LethalLib", throwOnError: false);
                MethodInfo method = type?.GetMethod("RegisterItem", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Item) }, null);
                if (method == null)
                    return;

                method.Invoke(null, new object[] { item });
                LethalLibRegisteredItemIds.Add(id);
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel] LethalLib item registration failed for {item.itemName}: {ex.Message}");
            }
        }

        private static void RegisterDawnItemIfAvailable(string itemKey, Item item)
        {
#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            if (item == null || string.IsNullOrWhiteSpace(itemKey))
                return;

            string normalizedKey = NormalizeDawnItemKey(itemKey);
            if (DawnRegisteredItemKeys.Contains(normalizedKey))
                return;

            try
            {
                RegisterDawnItemCore(normalizedKey, item);
                DawnRegisteredItemKeys.Add(normalizedKey);
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Fuel] Registered DawnLib item lgu_ship_systems/{normalizedKey}.");
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel] DawnLib item registration skipped or failed for {item.itemName}; DawnLib may be absent: {ex.Message}");
            }
#else
            _ = itemKey;
            _ = item;
#endif
        }

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
        // NoInlining keeps the typed Dawn.* references out of RegisterDawnItemIfAvailable's
        // JIT scope, so a missing DawnLib assembly lands in the catch above instead of
        // killing plugin startup.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterDawnItemCore(string normalizedKey, Item item)
        {
            if (item.spawnPrefab != null)
            {
                DawnLib.FixMixerGroups(item.spawnPrefab);
                DawnLib.RegisterNetworkPrefab(item.spawnPrefab);
            }

            NamespacedKey<DawnItemInfo> key = NamespacedKey<DawnItemInfo>.From("lgu_ship_systems", normalizedKey);
            DawnLib.DefineItem(key, item, builder => { });
        }
#endif

        private static string NormalizeDawnItemKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = char.ToLowerInvariant(value[i]);
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            return builder.ToString().Trim('_');
        }

        internal static void PrepareAuthoredVisual(GameObject instance, int layer)
        {
            if (instance == null)
                return;

            Transform[] transforms = instance.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] == null)
                    continue;

                transforms[i].gameObject.SetActive(true);
                transforms[i].gameObject.layer = layer;
            }

            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.DestroyImmediate(collider);
            foreach (Rigidbody body in instance.GetComponentsInChildren<Rigidbody>(true))
                UnityEngine.Object.DestroyImmediate(body);
            foreach (NetworkBehaviour behaviour in instance.GetComponentsInChildren<NetworkBehaviour>(true))
                UnityEngine.Object.DestroyImmediate(behaviour);
            foreach (NetworkObject networkObject in instance.GetComponentsInChildren<NetworkObject>(true))
                UnityEngine.Object.DestroyImmediate(networkObject);
            foreach (AudioSource audio in instance.GetComponentsInChildren<AudioSource>(true))
                UnityEngine.Object.DestroyImmediate(audio);

            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = true;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }

            RetargetAuthoredMaterialsToGameLit(instance);
        }

        // Bundled HDRP/Lit copies ship with stripped variants and draw nothing
        // in the player (no magenta, no log); rebind authored materials to the
        // game's own loaded HDRP/Lit and carry the maps/colors across.
        internal static void RetargetAuthoredMaterialsToGameLit(GameObject root)
        {
            if (root == null)
                return;

            try
            {
                Shader gameLit = Shader.Find("HDRP/Lit");
                if (gameLit == null)
                {
                    ShipSystemsBootstrap.Log?.LogWarning("[LGUShipSystems.Fuel] Could not find runtime shader 'HDRP/Lit'; authored fuel item materials were left unchanged.");
                    return;
                }

                Dictionary<Material, Material> materialCache = new Dictionary<Material, Material>();
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null)
                        continue;

                    Material[] sourceMaterials = renderer.sharedMaterials;
                    if (sourceMaterials == null || sourceMaterials.Length == 0)
                        continue;

                    Material[] rebuiltMaterials = new Material[sourceMaterials.Length];
                    for (int i = 0; i < sourceMaterials.Length; i++)
                    {
                        Material source = sourceMaterials[i];
                        if (source == null)
                            continue;

                        if (!materialCache.TryGetValue(source, out Material rebuilt))
                        {
                            rebuilt = RebuildMaterialWithGameLit(source, gameLit);
                            materialCache[source] = rebuilt;
                        }

                        rebuiltMaterials[i] = rebuilt;
                    }

                    renderer.sharedMaterials = rebuiltMaterials;
                }
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel] Failed to retarget authored fuel item materials to runtime HDRP/Lit: {ex.Message}");
            }
        }

        private static Material RebuildMaterialWithGameLit(Material source, Shader gameLit)
        {
            Material rebuilt = new Material(gameLit)
            {
                name = source.name + "_GameLit",
                renderQueue = source.renderQueue,
                enableInstancing = source.enableInstancing
            };

            TryCopyTexture(source, rebuilt, "_BaseColorMap", "_BaseColorMap");
            if (rebuilt.GetTexture("_BaseColorMap") == null)
                TryCopyTexture(source, rebuilt, "_MainTex", "_BaseColorMap");

            TryCopyColor(source, rebuilt, "_BaseColor", "_BaseColor");
            if (rebuilt.HasProperty("_BaseColor") && !source.HasProperty("_BaseColor"))
                TryCopyColor(source, rebuilt, "_Color", "_BaseColor");

            bool copiedNormal = TryCopyTexture(source, rebuilt, "_NormalMap", "_NormalMap")
                                || TryCopyTexture(source, rebuilt, "_BumpMap", "_NormalMap");
            if (copiedNormal)
                rebuilt.EnableKeyword("_NORMALMAP");

            TryCopyTexture(source, rebuilt, "_MaskMap", "_MaskMap");
            TryCopyFloat(source, rebuilt, "_Metallic", "_Metallic");
            TryCopyFloat(source, rebuilt, "_Smoothness", "_Smoothness");
            return rebuilt;
        }

        private static bool TryCopyTexture(Material source, Material target, string sourceProperty, string targetProperty)
        {
            if (source == null || target == null || !source.HasProperty(sourceProperty) || !target.HasProperty(targetProperty))
                return false;

            Texture texture = source.GetTexture(sourceProperty);
            if (texture == null)
                return false;

            target.SetTexture(targetProperty, texture);
            target.SetTextureScale(targetProperty, source.GetTextureScale(sourceProperty));
            target.SetTextureOffset(targetProperty, source.GetTextureOffset(sourceProperty));
            return true;
        }

        private static bool TryCopyColor(Material source, Material target, string sourceProperty, string targetProperty)
        {
            if (source == null || target == null || !source.HasProperty(sourceProperty) || !target.HasProperty(targetProperty))
                return false;

            target.SetColor(targetProperty, source.GetColor(sourceProperty));
            return true;
        }

        private static bool TryCopyFloat(Material source, Material target, string sourceProperty, string targetProperty)
        {
            if (source == null || target == null || !source.HasProperty(sourceProperty) || !target.HasProperty(targetProperty))
                return false;

            target.SetFloat(targetProperty, source.GetFloat(sourceProperty));
            return true;
        }

        internal static MeshRenderer GetUsableMeshRenderer(GameObject root)
        {
            if (root == null)
                return null;

            MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                MeshFilter filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
                if (renderer != null && renderer.enabled && renderer.gameObject.activeSelf && filter?.sharedMesh != null)
                    return renderer;
            }

            return null;
        }

        internal static void LogItemPrefabDiagnostics(string context, Item item)
        {
            LogPrefabDiagnostics(context, item?.spawnPrefab, item);
        }

        internal static void LogPrefabDiagnostics(string context, GameObject root, Item item)
        {
            if (root == null)
                return;

            string key = context + ":" + root.GetInstanceID();
            if (!LoggedPrefabDiagnostics.Add(key))
                return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            NetworkObject networkObject = root.GetComponent<NetworkObject>();
            GrabbableObject grabbable = root.GetComponent<GrabbableObject>();
            ScanNodeProperties scanNode = root.GetComponentInChildren<ScanNodeProperties>(true);

            int enabledRenderers = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && renderers[i].enabled)
                    enabledRenderers++;
            }

            ShipSystemsBootstrap.Log?.LogInfo(
                $"[LGUShipSystems.Fuel][Diag] {context}: root={root.name}, activeSelf={root.activeSelf}, activeInHierarchy={root.activeInHierarchy}, scene={root.scene.name}, worldPosition={FormatVector3(root.transform.position)}, localScale={FormatVector3(root.transform.localScale)}, visualChildLocalScales={DescribeVisualChildLocalScales(root)}, layer={root.layer}, tag={root.tag}, sameSpawnPrefab={item != null && item.spawnPrefab == root}, item='{item?.itemName ?? "null"}', isScrap={item != null && item.isScrap}, grabbable={grabbable != null}, itemPropertiesAssigned={grabbable?.itemProperties != null}, sameItemProperties={grabbable != null && grabbable.itemProperties == item}, meshVariants={item?.meshVariants?.Length.ToString() ?? "null"}, materialVariants={item?.materialVariants?.Length.ToString() ?? "null"}, scanNode={scanNode != null}, net={networkObject != null}, netHash={ReadNetworkHash(networkObject)}, colliders={colliders.Length}, colliderDetails={DescribeColliders(colliders)}, renderers={renderers.Length}, enabledRenderers={enabledRenderers}, meshes={DescribeMeshes(renderers)}, rendererDetails={DescribeRenderers(renderers)}.");
        }

        private static string ReadNetworkHash(NetworkObject networkObject)
        {
            if (networkObject == null)
                return "null";

            try
            {
                PropertyInfo property = typeof(NetworkObject).GetProperty("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object value = property != null ? property.GetValue(networkObject) : null;
                if (value != null)
                    return value.ToString();

                FieldInfo field = typeof(NetworkObject).GetField("GlobalObjectIdHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?? typeof(NetworkObject).GetField("<GlobalObjectIdHash>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                return field?.GetValue(networkObject)?.ToString() ?? "unreadable";
            }
            catch
            {
                return "unreadable";
            }
        }

        private static string DescribeMeshes(Renderer[] renderers)
        {
            if (renderers == null || renderers.Length == 0)
                return "none";

            StringBuilder builder = new StringBuilder();
            int count = 0;
            for (int i = 0; i < renderers.Length && count < 8; i++)
            {
                Renderer renderer = renderers[i];
                Mesh mesh = null;
                MeshFilter filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
                if (filter != null)
                    mesh = filter.sharedMesh;
                if (mesh == null && renderer is SkinnedMeshRenderer skinned)
                    mesh = skinned.sharedMesh;

                if (count > 0)
                    builder.Append("|");
                builder.Append(renderer != null ? renderer.name : "null");
                builder.Append(":");
                builder.Append(mesh != null ? mesh.name : "no-mesh");
                count++;
            }

            return builder.ToString();
        }

        private static string DescribeRenderers(Renderer[] renderers)
        {
            if (renderers == null || renderers.Length == 0)
                return "none";

            StringBuilder builder = new StringBuilder();
            int count = 0;
            for (int i = 0; i < renderers.Length && count < 8; i++)
            {
                Renderer renderer = renderers[i];
                if (count > 0)
                    builder.Append("|");

                builder.Append(renderer != null ? renderer.name : "null");
                if (renderer != null)
                {
                    builder.Append(":enabled=").Append(renderer.enabled);
                    builder.Append(",bounds=").Append(FormatVector3(renderer.bounds.size));
                    builder.Append(",materials=").Append(DescribeMaterials(renderer.sharedMaterials));
                }

                count++;
            }

            return builder.ToString();
        }

        private static string DescribeMaterials(Material[] materials)
        {
            if (materials == null || materials.Length == 0)
                return "none";

            StringBuilder builder = new StringBuilder();
            int count = 0;
            for (int i = 0; i < materials.Length && count < 8; i++)
            {
                Material material = materials[i];
                if (count > 0)
                    builder.Append(",");

                if (material == null)
                {
                    builder.Append("null");
                }
                else
                {
                    builder.Append(material.name);
                    builder.Append("/shader=").Append(material.shader != null ? material.shader.name : "null");
                    builder.Append("/queue=").Append(material.renderQueue);
                }

                count++;
            }

            return builder.ToString();
        }

        private static string DescribeColliders(Collider[] colliders)
        {
            if (colliders == null || colliders.Length == 0)
                return "none";

            StringBuilder builder = new StringBuilder();
            int count = 0;
            for (int i = 0; i < colliders.Length && count < 12; i++)
            {
                Collider collider = colliders[i];
                if (count > 0)
                    builder.Append("|");

                builder.Append(collider != null ? collider.name : "null");
                if (collider != null)
                {
                    builder.Append(":type=").Append(collider.GetType().Name);
                    builder.Append(",enabled=").Append(collider.enabled);
                    builder.Append(",isTrigger=").Append(collider.isTrigger);
                }

                count++;
            }

            return builder.ToString();
        }

        private static string DescribeVisualChildLocalScales(GameObject root)
        {
            if (root == null || root.transform == null || root.transform.childCount == 0)
                return "none";

            StringBuilder builder = new StringBuilder();
            int count = 0;
            Transform transform = root.transform;
            for (int i = 0; i < transform.childCount && count < 8; i++)
            {
                Transform child = transform.GetChild(i);
                if (child == null || child.GetComponentInChildren<Renderer>(true) == null)
                    continue;

                if (count > 0)
                    builder.Append("|");
                builder.Append(child.name);
                builder.Append(":").Append(FormatVector3(child.localScale));
                count++;
            }

            return count > 0 ? builder.ToString() : "none";
        }

        private static string FormatVector3(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        internal static ScanNodeProperties AddScanNode(Transform parent, string name, string header, string subText, Vector3 localPosition, Vector3 size)
        {
            GameObject scanObject = new GameObject(name);
            scanObject.transform.SetParent(parent, false);
            scanObject.transform.localPosition = localPosition;
            scanObject.layer = ScanNodeLayer;

            BoxCollider scanCollider = scanObject.AddComponent<BoxCollider>();
            scanCollider.isTrigger = true;
            scanCollider.size = size;

            ScanNodeProperties scan = scanObject.AddComponent<ScanNodeProperties>();
            scan.headerText = header;
            scan.subText = subText;
            scan.minRange = 1;
            scan.maxRange = 20;
            scan.requiresLineOfSight = false;
            scan.nodeType = 0;
            scan.creatureScanID = -1;
            return scan;
        }
    }
}
