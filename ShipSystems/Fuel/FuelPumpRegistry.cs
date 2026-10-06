using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCore.Modules.Sales;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelPumpRegistry
    {
        private const string PrefabName = "LGUShipSystems_FuelPumpItem";
        private const string BuyNodeName = "LGUShipSystems_FuelPumpBuyNode";
        internal const string ItemName = "Fuel Pump";
        private const float PumpVisualScale = 0.6f;
        private const float PumpColliderRadius = 0.17f;
        private const float PumpColliderHeight = 0.47f;

        // The fuel-items bundle builder zeroes the Blender importer's axis
        // correction (localRotation = identity), so the tank mesh's HEIGHT axis is
        // baked along +Z instead of +Y. Rotating the authored visual -90 about X
        // maps mesh +Z (height) -> item +Y so the can stands upright. Same
        // correction the fuel chute visual applies; the bundle is NOT rebuilt.
        private static readonly Quaternion PumpAuthoredUprightCorrection = Quaternion.Euler(-90f, 0f, 0f);

        private static bool _built;
        private static GameObject _holder;

        internal static GameObject Prefab { get; private set; }
        internal static Item ItemProperties { get; private set; }

        internal static bool EnsureRegistered()
        {
            if (!_built)
                BuildPrefab();

            FuelRuntimeUtility.RegisterNetworkPrefab(Prefab);
            FuelRuntimeUtility.RegisterItemMetadata("fuel_pump", ItemProperties);
            FuelRuntimeUtility.LogItemPrefabDiagnostics("registered Fuel Pump", ItemProperties);
            RegisterInAllItemsList();
            return Prefab != null && ItemProperties != null;
        }

        internal static void InjectIntoTerminal(Terminal terminal)
        {
            // Injection stays unconditional even while fuel is disabled. Both
            // buyableItemsList and itemSalesPercentages are index-addressed peer-shared
            // state (BuyItemsServerRpc, sales sync), and this runs at Terminal.Start
            // before the host's fuel flag has replicated - gating it here would size the
            // arrays from each peer's LOCAL config and desync every buy index. The pump
            // is withheld from the player instead: the store listing hides it
            // (ShopDataProvider) and the vanilla buy node is denied at use time
            // (ShipSystems.Layout.Patches).
            if (terminal == null || !EnsureRegistered())
                return;

            try
            {
                int itemIndex = EnsureBuyableItem(terminal, ItemProperties);
                TerminalKeyword buyKeyword = terminal.terminalNodes?.allKeywords?
                    .FirstOrDefault(keyword => keyword != null && keyword.isVerb && string.Equals(keyword.word, "buy", StringComparison.OrdinalIgnoreCase));
                if (buyKeyword == null)
                    return;

                if (buyKeyword.compatibleNouns != null &&
                    buyKeyword.compatibleNouns.Any(noun => noun?.result != null && noun.result.buyItemIndex == itemIndex))
                {
                    return;
                }

                TerminalKeyword nounKeyword = ScriptableObject.CreateInstance<TerminalKeyword>();
                nounKeyword.name = "LGUShipSystems_FuelPumpKeyword";
                nounKeyword.word = "fuelpump";
                nounKeyword.defaultVerb = buyKeyword;

                TerminalNode node = ScriptableObject.CreateInstance<TerminalNode>();
                node.name = BuyNodeName;
                node.creatureName = ItemName;
                node.itemCost = ShipFuelConfig.PumpPrice;
                node.buyItemIndex = itemIndex;
                node.clearPreviousText = true;
                node.displayText = $"Ordered {ItemName}. Drop it at detected oil refineries, then deposit filled pumps in the fuel chute.\n\n";
                node.maxCharactersToType = 35;

                AddKeyword(terminal, nounKeyword);
                AddCompatibleNoun(buyKeyword, nounKeyword, node);
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.FuelPump] Terminal injection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// True for the pump's own buy node. Used to deny the purchase while fuel is
        /// disabled without touching the shared keyword/item arrays.
        /// </summary>
        internal static bool IsPumpBuyNode(TerminalNode node)
        {
            if (node == null)
                return false;

            return string.Equals(node.name, BuyNodeName, StringComparison.Ordinal)
                || string.Equals(node.creatureName, ItemName, StringComparison.OrdinalIgnoreCase);
        }

        private static int EnsureBuyableItem(Terminal terminal, Item item)
        {
            List<Item> items = terminal.buyableItemsList != null
                ? terminal.buyableItemsList.ToList()
                : new List<Item>();

            int existing = items.FindIndex(existingItem =>
                existingItem != null &&
                string.Equals(existingItem.itemName, item.itemName, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                items[existing] = item;
                terminal.buyableItemsList = items.ToArray();
                // Someone else may still have grown the list without resizing.
                SalesCapacity.EnsureCapacity(
                    items.Count,
                    terminal.buyableVehicles != null ? terminal.buyableVehicles.Length : 0);
                return existing;
            }

            int newIndex = items.Count;
            items.Add(item);
            terminal.buyableItemsList = items.ToArray();
            // Vanilla only sizes itemSalesPercentages once. Appending a buyable item
            // without widening it makes the next SetItemSales throw out of
            // PassTimeToNextDay, which kills EndOfGame before SetShipReadyToLand and
            // leaves the terminal refusing every route change.
            SalesCapacity.EnsureCapacityForAppendedItem(
                newIndex,
                terminal.buyableVehicles != null ? terminal.buyableVehicles.Length : 0);
            return newIndex;
        }

        private static void AddKeyword(Terminal terminal, TerminalKeyword keyword)
        {
            if (terminal?.terminalNodes == null || keyword == null)
                return;

            List<TerminalKeyword> keywords = terminal.terminalNodes.allKeywords != null
                ? terminal.terminalNodes.allKeywords.ToList()
                : new List<TerminalKeyword>();
            if (!keywords.Any(existing => existing != null && string.Equals(existing.word, keyword.word, StringComparison.OrdinalIgnoreCase)))
            {
                keywords.Add(keyword);
                terminal.terminalNodes.allKeywords = keywords.ToArray();
            }
        }

        private static void AddCompatibleNoun(TerminalKeyword buyKeyword, TerminalKeyword nounKeyword, TerminalNode node)
        {
            List<CompatibleNoun> nouns = buyKeyword.compatibleNouns != null
                ? buyKeyword.compatibleNouns.ToList()
                : new List<CompatibleNoun>();
            nouns.Add(new CompatibleNoun(nounKeyword, node));
            buyKeyword.compatibleNouns = nouns.ToArray();
        }

        private static GameObject CreateSpawnPrefabRoot()
        {
            if (_holder == null)
            {
                _holder = new GameObject(PrefabName + "_RuntimeHolder");
                _holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_holder);
                _holder.hideFlags = HideFlags.HideAndDontSave;
            }

            GameObject root = new GameObject(PrefabName);
            root.transform.SetParent(_holder.transform, false);
            root.SetActive(true);
            root.layer = FuelRuntimeUtility.PropsLayer;
            FuelRuntimeUtility.SetTagSafe(root, "PhysicsProp");
            root.transform.localScale = Vector3.one;
            return root;
        }

        private static void RegisterInAllItemsList()
        {
            List<Item> items = StartOfRound.Instance?.allItemsList?.itemsList;
            if (items == null)
                return;

            AddToAllItemsList(items, ItemProperties);
        }

        private static void AddToAllItemsList(List<Item> items, Item item)
        {
            if (items == null || item == null)
                return;

            for (int i = 0; i < items.Count; i++)
            {
                Item existing = items[i];
                if (existing != null && string.Equals(existing.itemName, item.itemName, StringComparison.OrdinalIgnoreCase))
                {
                    // Only replace entries that are provably our stale copy;
                    // another mod's same-named item must not be clobbered.
                    GameObject existingPrefab = existing.spawnPrefab;
                    if (existingPrefab == null || existingPrefab.GetComponentInChildren<FuelPumpItem>(true) != null)
                    {
                        items[i] = item;
                        return;
                    }
                }
            }

            items.Add(item);
        }

        private static void BuildPrefab()
        {
            _built = true;

            GameObject root = CreateSpawnPrefabRoot();


            CapsuleCollider collider = root.AddComponent<CapsuleCollider>();
            collider.direction = 1;
            collider.radius = PumpColliderRadius;
            collider.height = PumpColliderHeight;
            collider.center = Vector3.zero;

            NetworkObject networkObject = root.AddComponent<NetworkObject>();
            networkObject.SynchronizeTransform = true;
            networkObject.SceneMigrationSynchronization = true;
            networkObject.SpawnWithObservers = true;
            networkObject.DontDestroyWithOwner = true;
            networkObject.AutoObjectParentSync = true;
            FuelRuntimeUtility.AssignStableNetworkHash(networkObject, PrefabName);

            Rigidbody body = root.AddComponent<Rigidbody>();
            // Vanilla LC items are kinematic; GrabbableObject moves them with
            // scripted lerps, and a dynamic body fights those writes.
            body.isKinematic = true;
            body.useGravity = false;
            body.mass = 10f;
            body.drag = 0.1f;
            body.angularDrag = 4f;

            MeshRenderer mainRenderer = BuildPumpVisuals(root.transform);

            PhysicsProp prop = root.AddComponent<PhysicsProp>();
            prop.grabbable = true;
            prop.grabbableToEnemies = true;
            prop.isInFactory = true;
            prop.scrapValue = 0;
            prop.rotateObject = false;
            prop.propBody = body;
            prop.originalScale = root.transform.localScale;
            prop.customGrabTooltip = "Grab fuel pump";
            prop.propColliders = new Collider[] { collider };
            prop.mainObjectRenderer = mainRenderer;

            root.AddComponent<FuelPumpItem>();
            FuelRuntimeUtility.AddScanNode(root.transform, "ScanNode", "FUEL PUMP", "empty - drop at oil refinery", Vector3.up * 0.5f, new Vector3(1.1f, 1.1f, 1.1f));

            Item item = ScriptableObject.CreateInstance<Item>();
            item.name = "LGU Fuel Pump Item";
            item.itemName = ItemName;
            item.spawnPrefab = root;
            item.spawnPositionTypes = new List<ItemGroup>();
            item.canBeGrabbedBeforeGameStart = true;
            item.disallowUtilitySlot = true;
            item.twoHanded = true;
            item.twoHandedAnimation = true;
            item.weight = 1.22f;
            item.itemSpawnsOnGround = true;
            item.isScrap = false;
            item.creditsWorth = ShipFuelConfig.PumpPrice;
            item.minValue = 0;
            item.maxValue = 0;
            item.requiresBattery = false;
            item.batteryUsage = 0f;
            item.automaticallySetUsingPower = false;
            item.isConductiveMetal = true;
            item.grabAnimationTime = 0.45f;
            item.syncGrabFunction = true;
            item.syncUseFunction = true;
            item.syncDiscardFunction = true;
            item.syncInteractLRFunction = true;
            item.toolTips = new[] { "Drop at oil refinery" };
            item.verticalOffset = 0.26f;
            item.floorYOffset = 0;
            item.allowDroppingAheadOfPlayer = true;
            item.restingRotation = Vector3.zero;
            item.rotationOffset = Vector3.zero;
            item.positionOffset = Vector3.zero;
            item.meshOffset = false;
            item.canBeInspected = true;
            prop.itemProperties = item;

            ItemProperties = item;
            Prefab = root;
        }

        private static MeshRenderer BuildPumpVisuals(Transform root)
        {
            GameObject authoredVisual = FuelItemAssetLoader.TryInstantiatePumpVisual(root);
            if (authoredVisual != null)
            {
                authoredVisual.transform.localScale *= PumpVisualScale;
                // Stand the can upright (mesh height axis is +Z, see the const), then
                // re-center: the bundle's baked centering is stale once we rotate, so
                // shift the visual so its measured bounds center sits at the item root
                // origin. Measured generically from mesh bounds, not hardcoded numbers.
                authoredVisual.transform.localRotation = PumpAuthoredUprightCorrection;
                RecenterVisualBoundsToItemRoot(root, authoredVisual.transform);
            }

            MeshRenderer authoredRenderer = FuelRuntimeUtility.GetUsableMeshRenderer(authoredVisual);
            if (authoredRenderer != null)
                return authoredRenderer;

            if (authoredVisual != null)
                UnityEngine.Object.DestroyImmediate(authoredVisual);

            return BuildFallbackPumpVisual(root);
        }

        // Shift the authored visual so the combined renderer bounds center (measured
        // in item-root space after the upright rotation + scale are applied) sits at
        // the item root origin. Uses mesh bounds transformed through each filter so it
        // works even while the prefab lives under the inactive runtime holder.
        private static void RecenterVisualBoundsToItemRoot(Transform root, Transform visual)
        {
            if (root == null || visual == null)
                return;

            MeshFilter[] filters = visual.GetComponentsInChildren<MeshFilter>(true);
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool found = false;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                    continue;

                Bounds bounds = mesh.bounds;
                Vector3 boundsMin = bounds.min;
                Vector3 boundsMax = bounds.max;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = new Vector3(
                        (c & 1) == 0 ? boundsMin.x : boundsMax.x,
                        (c & 2) == 0 ? boundsMin.y : boundsMax.y,
                        (c & 4) == 0 ? boundsMin.z : boundsMax.z);
                    Vector3 local = root.InverseTransformPoint(filter.transform.TransformPoint(corner));
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    found = true;
                }
            }

            if (!found)
                return;

            Vector3 boundsCenterLocal = (min + max) * 0.5f;
            visual.localPosition -= boundsCenterLocal;
        }

        private static MeshRenderer BuildFallbackPumpVisual(Transform root)
        {
            Material bodyMaterial = CreateMaterial("LGUShipSystems_FuelPumpFallbackBody", new Color(0.72f, 0.12f, 0.06f, 1f), 0.22f, 0.2f);
            Material capMaterial = CreateMaterial("LGUShipSystems_FuelPumpFallbackCap", new Color(0.08f, 0.08f, 0.08f, 1f), 0.35f, 0.18f);

            GameObject visualRoot = new GameObject("FuelPumpFallbackVisual");
            visualRoot.transform.SetParent(root, false);
            visualRoot.transform.localPosition = Vector3.zero;
            visualRoot.transform.localRotation = Quaternion.identity;
            visualRoot.transform.localScale = Vector3.one * PumpVisualScale;

            GameObject body = CreateVisualPrimitive(PrimitiveType.Cylinder, "FuelPumpFallbackBody", visualRoot.transform, Vector3.zero, new Vector3(0.42f, 0.36f, 0.42f), bodyMaterial);
            CreateVisualPrimitive(PrimitiveType.Cube, "FuelPumpFallbackHandle", visualRoot.transform, Vector3.up * 0.44f, new Vector3(0.32f, 0.08f, 0.16f), capMaterial);
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
            Collider visualCollider = go.GetComponent<Collider>();
            if (visualCollider != null)
                UnityEngine.Object.Destroy(visualCollider);
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
    }
}