using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Power
{
    internal static class SpareBatteryItemRegistry
    {
        private const string PrefabName = "LGUShipSystems_SpareBatteryItem";
        private const string ItemName = "Spare Battery";
        private const float VisualTargetMaxDimension = 1.15f;
        private const float MinimumPickupWidth = 1.1f;
        private const float MinimumPickupHeight = 0.9f;
        private const float MinimumPickupDepth = 1.1f;

        private static bool _registrationAttempted;
        private static bool _registeredWithNetworkManager;
        private static GameObject _holder;

        internal static GameObject Prefab { get; private set; }
        internal static Item ItemProperties { get; private set; }
        internal static float GroundVerticalOffset { get; private set; } = 0.85f;

        internal static bool EnsureRegistered()
        {
            if (Prefab == null || ItemProperties == null)
                BuildPrefab();

            RegisterWithNetworkManager();
            RegisterInAllItemsList();
            return Prefab != null && ItemProperties != null;
        }

        private static void RegisterInAllItemsList()
        {
            List<Item> items = StartOfRound.Instance?.allItemsList?.itemsList;
            if (items == null || ItemProperties == null)
                return;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == ItemProperties)
                    return;
            }

            items.Add(ItemProperties);
        }

        internal static bool IsSpareBatteryObject(GrabbableObject item)
        {
            if (item == null)
                return false;

            string itemName = item.itemProperties != null ? item.itemProperties.itemName : item.name;
            return Contains(itemName, "spare battery") ||
                   Contains(itemName, "ship battery") ||
                   string.Equals(item.name, PrefabName, StringComparison.OrdinalIgnoreCase) ||
                   item.name.IndexOf("SpareBattery", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool TrySpawn(Vector3 position, Quaternion rotation, Transform parent, out NetworkObject networkObject)
        {
            networkObject = null;
            if (!EnsureRegistered() || Prefab == null)
                return false;

            GameObject instance = UnityEngine.Object.Instantiate(Prefab, position + Vector3.up * GroundVerticalOffset, rotation, parent);
            if (instance == null)
                return false;

            instance.SetActive(true);
            PrepareSpawned(instance);
            networkObject = instance.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                UnityEngine.Object.Destroy(instance);
                return false;
            }

            if (!networkObject.IsSpawned)
                networkObject.Spawn();
            if (parent != null)
                networkObject.TrySetParent(parent);

            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.SpareBattery] Spawned spare battery at {position}.");
            return true;
        }

        private static void BuildPrefab()
        {
            if (_registrationAttempted)
                return;

            _registrationAttempted = true;
            try
            {
                // Prefab root stays ACTIVE inside an inactive holder (mirrors
                // FuelLootRegistry); Netcode skips NetworkBehaviours that are
                // disabled at spawn time.
                if (_holder == null)
                {
                    _holder = new GameObject("LGUShipSystems_SpareBattery_RuntimeHolder");
                    _holder.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(_holder);
                    _holder.hideFlags = HideFlags.HideAndDontSave;
                }

                GameObject root = new GameObject(PrefabName);
                root.transform.SetParent(_holder.transform, false);
                root.SetActive(true);
                root.layer = ResolvePropsLayer();
                SetTagSafe(root, "PhysicsProp");

                NetworkObject networkObject = root.AddComponent<NetworkObject>();
                networkObject.SynchronizeTransform = true;
                networkObject.SceneMigrationSynchronization = true;
                networkObject.SpawnWithObservers = true;
                networkObject.DontDestroyWithOwner = true;
                networkObject.AutoObjectParentSync = true;
                AssignStableNetworkHash(networkObject);

                Rigidbody body = root.AddComponent<Rigidbody>();
                // Vanilla LC items are kinematic; GrabbableObject moves them
                // with scripted lerps, and a dynamic body fights those writes.
                body.isKinematic = true;
                body.useGravity = false;
                body.mass = 7f;
                body.drag = 0.1f;
                body.angularDrag = 7f;

                PhysicsProp prop = root.AddComponent<PhysicsProp>();
                prop.grabbable = true;
                prop.grabbableToEnemies = false;
                prop.isInFactory = true;
                prop.scrapValue = 0;
                prop.rotateObject = false;
                prop.propBody = body;
                prop.originalScale = Vector3.one;
                prop.customGrabTooltip = "Grab spare battery";

                AudioSource audio = root.AddComponent<AudioSource>();
                audio.playOnAwake = false;
                audio.spatialBlend = 1f;
                audio.rolloffMode = AudioRolloffMode.Linear;
                audio.minDistance = 2f;
                audio.maxDistance = 22f;
                audio.volume = 0.25f;

                GameObject visual = BatteryAssetLoader.CreateBatteryVisual(root.transform, "SpareBatteryVisual", VisualTargetMaxDimension);
                PositionVisualBelowPickupRoot(root, visual);

                BoxCollider collider = CreatePickupCollider(root, visual);
                prop.propColliders = collider != null ? new Collider[] { collider } : Array.Empty<Collider>();
                prop.mainObjectRenderer = visual != null ? visual.GetComponentInChildren<MeshRenderer>(true) : null;

                SetupScanNode(root, collider);

                ItemProperties = BuildItemProperties(root);
                prop.itemProperties = ItemProperties;
                ItemProperties.spawnPrefab = root;
                Prefab = root;
                ShipSystemsBootstrap.Log?.LogInfo("[LGUShipSystems.SpareBattery] Runtime spare battery item prefab built.");
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.SpareBattery] Failed to build spare battery prefab: {ex.Message}");
                if (Prefab != null)
                    UnityEngine.Object.Destroy(Prefab);
                Prefab = null;
                ItemProperties = null;
            }
        }

        private static Item BuildItemProperties(GameObject prefab)
        {
            Item item = ScriptableObject.CreateInstance<Item>();
            item.name = "LGU Spare Battery Item";
            item.itemName = ItemName;
            item.spawnPositionTypes = new List<ItemGroup>();
            item.twoHanded = true;
            item.twoHandedAnimation = true;
            item.canBeGrabbedBeforeGameStart = true;
            item.disallowUtilitySlot = true;
            item.weight = 1.16f;
            item.itemSpawnsOnGround = true;
            item.isConductiveMetal = true;
            item.isScrap = false;
            item.creditsWorth = 0;
            item.minValue = 0;
            item.maxValue = 0;
            item.spawnPrefab = prefab;
            item.requiresBattery = false;
            item.batteryUsage = 0f;
            item.automaticallySetUsingPower = false;
            item.grabAnimationTime = 0.45f;
            item.syncGrabFunction = true;
            item.syncUseFunction = true;
            item.syncDiscardFunction = true;
            item.syncInteractLRFunction = true;
            item.toolTips = new[] { "Dock at exterior battery" };
            item.verticalOffset = GroundVerticalOffset;
            item.floorYOffset = 0;
            item.allowDroppingAheadOfPlayer = true;
            item.restingRotation = Vector3.zero;
            item.rotationOffset = Vector3.zero;
            item.positionOffset = Vector3.zero;
            item.meshOffset = false;
            item.canBeInspected = true;
            return item;
        }

        private static void RegisterWithNetworkManager()
        {
            if (_registeredWithNetworkManager || Prefab == null)
                return;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || manager.NetworkConfig == null)
                return;

            try
            {
                if (!manager.NetworkConfig.Prefabs.Contains(Prefab))
                    manager.AddNetworkPrefab(Prefab);
                _registeredWithNetworkManager = true;
                ShipSystemsBootstrap.Log?.LogInfo("[LGUShipSystems.SpareBattery] Registered spare battery NetworkPrefab.");
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.SpareBattery] NetworkPrefab registration failed: {ex.Message}");
            }
        }

        private static void PrepareSpawned(GameObject instance)
        {
            GrabbableObject grabbable = instance.GetComponent<GrabbableObject>();
            if (grabbable != null)
            {
                grabbable.itemProperties = ItemProperties;
                grabbable.grabbable = true;
                grabbable.grabbableToEnemies = false;
                grabbable.isInFactory = true;
                grabbable.scrapValue = 0;
                grabbable.customGrabTooltip = "Grab spare battery";
            }

            ScanNodeProperties scan = instance.GetComponentInChildren<ScanNodeProperties>(true);
            if (scan != null)
            {
                scan.headerText = ItemName;
                scan.subText = "ship power replacement";
                scan.scrapValue = 0;
            }
        }

        private static void PositionVisualBelowPickupRoot(GameObject root, GameObject visual)
        {
            if (root == null || visual == null)
                return;

            if (!BatteryAssetLoader.TryGetLocalRendererBounds(root.transform, visual, out Bounds bounds))
                return;

            visual.transform.localPosition -= bounds.center;
            if (!BatteryAssetLoader.TryGetLocalRendererBounds(root.transform, visual, out bounds))
                return;

            GroundVerticalOffset = Mathf.Max(0.72f, bounds.extents.y + 0.35f);
            visual.transform.localPosition += Vector3.up * (bounds.extents.y + 0.03f - GroundVerticalOffset);
        }

        private static BoxCollider CreatePickupCollider(GameObject root, GameObject visual)
        {
            BoxCollider collider = root.AddComponent<BoxCollider>();
            collider.isTrigger = false;
            if (BatteryAssetLoader.TryGetLocalRendererBounds(root.transform, visual, out Bounds bounds))
            {
                collider.center = bounds.center;
                collider.size = new Vector3(
                    Mathf.Max(MinimumPickupWidth, Mathf.Abs(bounds.size.x) + 0.25f),
                    Mathf.Max(MinimumPickupHeight, Mathf.Abs(bounds.size.y) + 0.25f),
                    Mathf.Max(MinimumPickupDepth, Mathf.Abs(bounds.size.z) + 0.25f));
            }
            else
            {
                collider.center = Vector3.down * (GroundVerticalOffset - MinimumPickupHeight * 0.5f);
                collider.size = new Vector3(MinimumPickupWidth, MinimumPickupHeight, MinimumPickupDepth);
            }

            collider.gameObject.layer = ResolvePropsLayer();
            return collider;
        }

        private static void SetupScanNode(GameObject root, BoxCollider pickupCollider)
        {
            GameObject scanObject = new GameObject("ScanNode");
            scanObject.transform.SetParent(root.transform, false);
            scanObject.layer = ResolveLayer("ScanNode", 22);
            scanObject.transform.localPosition = pickupCollider != null
                ? pickupCollider.center + Vector3.up * 0.2f
                : new Vector3(0f, 0.75f, 0f);

            BoxCollider scanCollider = scanObject.AddComponent<BoxCollider>();
            scanCollider.isTrigger = true;
            scanCollider.size = pickupCollider != null
                ? pickupCollider.size + new Vector3(0.2f, 0.2f, 0.2f)
                : new Vector3(1.2f, 1.0f, 1.2f);

            ScanNodeProperties scanNode = scanObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = ItemName;
            scanNode.subText = "ship power replacement";
            scanNode.maxRange = 30;
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 0;
            scanNode.creatureScanID = -1;
            scanNode.scrapValue = 0;
        }

        private static void AssignStableNetworkHash(NetworkObject networkObject)
        {
            if (networkObject == null)
                return;

            try
            {
                uint hash;
                using (MD5 md5 = MD5.Create())
                {
                    byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ShipSystemsBootstrap.PluginGuid + "." + PrefabName));
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
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.SpareBattery] Failed to assign NetworkObject hash: {ex.Message}");
            }
        }

        private static int ResolvePropsLayer()
        {
            return ResolveLayer("Props", ResolveLayer("InteractableObject", 6));
        }

        private static int ResolveLayer(string name, int fallback)
        {
            int layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? layer : fallback;
        }

        private static void SetTagSafe(GameObject obj, string tag)
        {
            if (obj == null)
                return;

            try
            {
                obj.tag = tag;
            }
            catch
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.SpareBattery] Tag '{tag}' was unavailable.");
            }
        }

        private static bool Contains(string value, string fragment)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
