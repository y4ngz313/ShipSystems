using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Layout;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelLootRegistry
    {
        internal static Item SmallItemProperties { get; private set; }
        internal static Item MediumItemProperties { get; private set; }
        internal static Item LargeItemProperties { get; private set; }

        private static bool _registered;
        private static GameObject _holder;

        internal static void EnsureRegistered()
        {
            if (!_registered)
            {
                SmallItemProperties = BuildFuelLoot("Light Fuel", "Small", 4f, 18);
                MediumItemProperties = BuildFuelLoot("Dense Fuel", "Medium", 7f, 32);
                LargeItemProperties = BuildFuelLoot("Natural Fuel", "Large", 12f, 55);
                _registered = true;
            }

            FuelItemSounds.TryAssign(SmallItemProperties);
            FuelItemSounds.TryAssign(MediumItemProperties);
            FuelItemSounds.TryAssign(LargeItemProperties);

            FuelRuntimeUtility.RegisterNetworkPrefab(SmallItemProperties?.spawnPrefab);
            FuelRuntimeUtility.RegisterNetworkPrefab(MediumItemProperties?.spawnPrefab);
            FuelRuntimeUtility.RegisterNetworkPrefab(LargeItemProperties?.spawnPrefab);
            FuelRuntimeUtility.RegisterItemMetadata("small_fuel_canister", SmallItemProperties);
            FuelRuntimeUtility.RegisterItemMetadata("medium_fuel_canister", MediumItemProperties);
            FuelRuntimeUtility.RegisterItemMetadata("large_fuel_canister", LargeItemProperties);
            FuelRuntimeUtility.LogItemPrefabDiagnostics("registered Small fuel source", SmallItemProperties);
            FuelRuntimeUtility.LogItemPrefabDiagnostics("registered Medium fuel source", MediumItemProperties);
            FuelRuntimeUtility.LogItemPrefabDiagnostics("registered Large fuel source", LargeItemProperties);
            RegisterInAllItemsList();
        }

        internal static void InjectIntoCurrentLevelScrap()
        {
            if (!ShipSystemsController.IsFuelSystemEnabled())
                return;
            if (ShipSystemsBootstrap.EnableFuelLootSpawnInjection?.Value != true)
                return;

            EnsureRegistered();
            SelectableLevel level = RoundManager.Instance?.currentLevel ?? StartOfRound.Instance?.currentLevel;
            if (level == null || level.spawnableScrap == null)
                return;

            AddSpawnable(level, SmallItemProperties, ShipSystemsBootstrap.LightFuelWeight?.Value ?? 12);
            AddSpawnable(level, MediumItemProperties, ShipSystemsBootstrap.DenseFuelWeight?.Value ?? 7);
            AddSpawnable(level, LargeItemProperties, ShipSystemsBootstrap.NaturalFuelWeight?.Value ?? 3);
        }

        private static GameObject CreateSpawnPrefabRoot(string prefabName)
        {
            if (_holder == null)
            {
                _holder = new GameObject("LGUShipSystems_FuelLoot_RuntimeHolder");
                _holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_holder);
                _holder.hideFlags = HideFlags.HideAndDontSave;
            }

            GameObject root = new GameObject(prefabName);
            root.transform.SetParent(_holder.transform, false);
            root.SetActive(true);
            root.layer = FuelRuntimeUtility.PropsLayer;
            FuelRuntimeUtility.SetTagSafe(root, "PhysicsProp");
            root.transform.localScale = Vector3.one;
            return root;
        }

        private static Item BuildFuelLoot(string itemName, string sizeKey, float fallbackFuel, int fallbackScrap)
        {
            string prefabName = sizeKey == "Small"
                ? "LGUShipSystems_SmallFuelCanister"
                : sizeKey == "Medium"
                    ? "LGUShipSystems_MediumFuelCanister"
                    : "LGUShipSystems_LargeFuelCanister";

            GameObject root = CreateSpawnPrefabRoot(prefabName);


            float radius = sizeKey == "Large" ? 0.20f : sizeKey == "Medium" ? 0.23f : 0.13f;
            float height = sizeKey == "Large" ? 0.44f : sizeKey == "Medium" ? 0.48f : 0.26f;
            CapsuleCollider collider = root.AddComponent<CapsuleCollider>();
            collider.direction = 1;
            collider.radius = radius;
            collider.height = height;
            collider.center = Vector3.zero;

            NetworkObject networkObject = root.AddComponent<NetworkObject>();
            networkObject.SynchronizeTransform = true;
            networkObject.SceneMigrationSynchronization = true;
            networkObject.SpawnWithObservers = true;
            networkObject.DontDestroyWithOwner = true;
            networkObject.AutoObjectParentSync = true;
            FuelRuntimeUtility.AssignStableNetworkHash(networkObject, root.name);

            Rigidbody body = root.AddComponent<Rigidbody>();
            // Vanilla LC items are kinematic; GrabbableObject moves them with
            // scripted lerps, and a dynamic body fights those writes.
            body.isKinematic = true;
            body.useGravity = false;
            body.mass = sizeKey == "Large" ? 8f : sizeKey == "Medium" ? 4.5f : 2.6f;
            body.drag = 0.08f;
            body.angularDrag = 2.2f;

            MeshRenderer mainRenderer = BuildFuelLootVisuals(root.transform, sizeKey, radius, height);

            FuelItemSounds.EnsureSource(root);
            PhysicsProp prop = root.AddComponent<PhysicsProp>();
            prop.grabbable = true;
            prop.grabbableToEnemies = true;
            prop.isInFactory = true;
            prop.propBody = body;
            prop.rotateObject = false;
            prop.originalScale = Vector3.one;
            prop.customGrabTooltip = "Grab fuel source";
            prop.propColliders = new Collider[] { collider };
            prop.mainObjectRenderer = mainRenderer;

            FuelRuntimeUtility.AddScanNode(root.transform, "ScanNode", itemName.ToUpperInvariant(), Mathf.RoundToInt(fallbackFuel) + " fuel", Vector3.up * (height + 0.22f), new Vector3(1.0f, 1.0f, 1.0f));
            FuelLootItem fuel = root.AddComponent<FuelLootItem>();
            float fuelValue = (sizeKey == "Small" ? ShipSystemsBootstrap.LightFuelUnits?.Value : sizeKey == "Medium" ? ShipSystemsBootstrap.DenseFuelUnits?.Value : ShipSystemsBootstrap.NaturalFuelUnits?.Value) ?? fallbackFuel;
            int scrapValue = Mathf.Max(0, (sizeKey == "Small" ? ShipSystemsBootstrap.LightFuelSale?.Value : sizeKey == "Medium" ? ShipSystemsBootstrap.DenseFuelSale?.Value : ShipSystemsBootstrap.NaturalFuelSale?.Value) ?? fallbackScrap);
            fuel.Initialize(fuelValue, scrapValue, itemName);

            Item item = ScriptableObject.CreateInstance<Item>();
            item.name = "LGU " + itemName;
            item.itemName = itemName;
            item.itemIcon = FuelHotbarIcons.Load(sizeKey);
            item.spawnPrefab = root;
            item.spawnPositionTypes = new List<ItemGroup>();
            item.isScrap = true;
            item.minValue = scrapValue;
            item.maxValue = scrapValue;
            item.creditsWorth = scrapValue;
            item.weight = sizeKey == "Large" ? 1.18f : sizeKey == "Medium" ? 1.09f : 1.04f;
            item.itemSpawnsOnGround = true;
            item.canBeGrabbedBeforeGameStart = false;
            item.requiresBattery = false;
            item.batteryUsage = 0f;
            item.automaticallySetUsingPower = false;
            item.isConductiveMetal = true;
            item.grabAnimationTime = 0.25f;
            item.syncGrabFunction = true;
            item.syncUseFunction = true;
            item.syncDiscardFunction = true;
            item.verticalOffset = height * 0.5f;
            item.floorYOffset = 0;
            item.allowDroppingAheadOfPlayer = true;
            item.restingRotation = Vector3.zero;
            item.rotationOffset = Vector3.zero;
            item.positionOffset = Vector3.zero;
            item.meshOffset = false;
            item.canBeInspected = true;
            // RoundManager.SyncScrapValuesClientRpc reads both variant arrays'
            // Length without a null guard, even when an item has no variants.
            item.meshVariants = FuelScrapSyncInvariant.EnsureArray(item.meshVariants);
            item.materialVariants = FuelScrapSyncInvariant.EnsureArray(item.materialVariants);
            prop.itemProperties = item;
            prop.scrapValue = scrapValue;
            return item;
        }

        private static MeshRenderer BuildFuelLootVisuals(Transform root, string sizeKey, float radius, float height)
        {
            MeshRenderer pickup = FuelLootAssetLoader.TryCreate(root, sizeKey);
            if (pickup != null) return pickup;
            if (string.Equals(sizeKey, "Large", StringComparison.OrdinalIgnoreCase))
            {
                GameObject authoredVisual = FuelItemAssetLoader.TryInstantiateLargeFuelVisual(root);
                MeshRenderer authoredRenderer = FuelRuntimeUtility.GetUsableMeshRenderer(authoredVisual);
                if (authoredRenderer != null)
                {
                    authoredVisual.transform.localScale *= 0.62f;
                    return authoredRenderer;
                }

                if (authoredVisual != null)
                    UnityEngine.Object.DestroyImmediate(authoredVisual);
            }

            Material bodyMaterial = CreateMaterial("LGUShipSystems_FuelCanister_Orange", new Color(0.88f, 0.29f, 0.06f, 1f), 0.18f, 0.24f);
            Material capMaterial = CreateMaterial("LGUShipSystems_FuelCanister_DarkCap", new Color(0.08f, 0.09f, 0.09f, 1f), 0.35f, 0.18f);
            Material bandMaterial = CreateMaterial("LGUShipSystems_FuelCanister_LabelBand", new Color(0.92f, 0.84f, 0.42f, 1f), 0.05f, 0.2f);

            float capHeight = height * (0.035f / 0.48f);
            float bandDepth = radius * (0.012f / 0.23f);
            float bandOffset = radius * (0.006f / 0.23f);
            float gripHeight = height * (0.06f / 0.62f);

            GameObject body = CreateVisualPrimitive(PrimitiveType.Cylinder, "FuelCanisterBody", root, Vector3.zero, new Vector3(radius * 2f, height * 0.5f, radius * 2f), bodyMaterial);
            CreateVisualPrimitive(PrimitiveType.Cylinder, "FuelCanisterTopCap", root, Vector3.up * (height * 0.53f), new Vector3(radius * 1.78f, capHeight, radius * 1.78f), capMaterial);
            CreateVisualPrimitive(PrimitiveType.Cylinder, "FuelCanisterBottomCap", root, Vector3.down * (height * 0.53f), new Vector3(radius * 1.78f, capHeight, radius * 1.78f), capMaterial);
            GameObject band = CreateVisualPrimitive(PrimitiveType.Cube, "FuelCanisterFuelBand", root, new Vector3(0f, height * 0.08f, -radius - bandOffset), new Vector3(radius * 1.45f, height * 0.16f, bandDepth), bandMaterial);
            band.transform.localRotation = Quaternion.identity;

            if (sizeKey == "Large")
            {
                CreateVisualPrimitive(PrimitiveType.Cube, "FuelCanisterGrip", root, new Vector3(0f, height * 0.73f, 0f), new Vector3(radius * 1.25f, gripHeight, radius * 0.34f), capMaterial);
            }

            return body.GetComponent<MeshRenderer>();
        }

        private static GameObject CreateVisualPrimitive(PrimitiveType type, string name, Transform parent, Vector3 localPosition, Vector3 localScale, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = localScale;
            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.Destroy(collider);
            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = material;
            return go;
        }

        private static Material CreateMaterial(string name, Color color, float metallic, float smoothness)
        {
            Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Diffuse");
            Material material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            return material;
        }

        private static void RegisterInAllItemsList()
        {
            List<Item> items = StartOfRound.Instance?.allItemsList?.itemsList;
            if (items == null)
                return;

            AddToAllItemsList(items, SmallItemProperties);
            AddToAllItemsList(items, MediumItemProperties);
            AddToAllItemsList(items, LargeItemProperties);
        }

        private static void AddToAllItemsList(List<Item> items, Item item)
        {
            if (items == null || item == null)
                return;

            bool replaced = false;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                Item existing = items[i];
                if (!IsSameFuelLootEntry(existing, item))
                    continue;

                GameObject existingPrefab = existing.spawnPrefab;
                bool canReplace = existingPrefab == null || IsFuelLootPrefab(existingPrefab) || HasSameSpawnPrefabName(existing, item);
                if (!canReplace)
                    continue;

                if (!replaced)
                {
                    items[i] = item;
                    replaced = true;
                }
                else
                {
                    items.RemoveAt(i);
                }
            }

            if (!replaced)
                items.Add(item);
        }

        private static void AddSpawnable(SelectableLevel level, Item item, int rarity)
        {
            if (level == null || item == null)
                return;

            List<SpawnableItemWithRarity> list = level.spawnableScrap != null
                ? level.spawnableScrap.ToList()
                : new List<SpawnableItemWithRarity>();
            bool replaced = false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                SpawnableItemWithRarity entry = list[i];
                if (!IsSameFuelLootEntry(entry?.spawnableItem, item))
                    continue;

                if (rarity > 0 && !replaced)
                {
                    list[i] = new SpawnableItemWithRarity(item, Mathf.Max(0, rarity));
                    replaced = true;
                }
                else
                {
                    list.RemoveAt(i);
                }
            }

            if (rarity > 0 && !replaced)
                list.Add(new SpawnableItemWithRarity(item, Mathf.Max(0, rarity)));
            level.spawnableScrap = list;
        }

        private static bool IsSameFuelLootEntry(Item existing, Item item)
        {
            if (existing == null || item == null)
                return false;
            if (string.Equals(existing.itemName, item.itemName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (HasSameSpawnPrefabName(existing, item))
                return true;

            return IsFuelLootPrefab(existing.spawnPrefab) && IsLegacyFuelLootDisplayName(existing.itemName);
        }

        private static bool HasSameSpawnPrefabName(Item existing, Item item)
        {
            string existingName = existing?.spawnPrefab != null ? existing.spawnPrefab.name : null;
            string itemName = item?.spawnPrefab != null ? item.spawnPrefab.name : null;
            return !string.IsNullOrWhiteSpace(existingName)
                   && !string.IsNullOrWhiteSpace(itemName)
                   && string.Equals(existingName, itemName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFuelLootPrefab(GameObject prefab)
        {
            return prefab != null && prefab.GetComponentInChildren<FuelLootItem>(true) != null;
        }

        private static bool IsLegacyFuelLootDisplayName(string itemName)
        {
            return string.Equals(itemName, "Small fuel canister", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(itemName, "Fuel canister", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(itemName, "Large fuel source", StringComparison.OrdinalIgnoreCase);
        }

    }
}
