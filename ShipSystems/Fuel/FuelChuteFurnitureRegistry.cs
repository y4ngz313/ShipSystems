using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dawn;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Layout;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelChuteFurnitureRegistry
    {
        internal const string UnlockableName = "Fuel Chute";
        private const string PrefabName = "LGUShipSystems_FuelChuteFurniture";
        private const int PlaceableObjectLayer = 26;
        private const float LowerFloorMinLocalY = -0.35f;
        internal const float LowerFloorMaxLocalY = 1.25f;
        /// <summary>
        /// Fallback ship-local rest pose, used for every non-vanilla ship layout (#677).
        /// Wider / TwoStory hulls move the floor and the back wall, so the vanilla-tuned
        /// spot below is not safe there; this stays where it has always been and the
        /// existing inside-bounds fallback keeps handling the oversized hulls.
        /// </summary>
        private static readonly Vector3 DefaultLocalPosition = new Vector3(-2.65f, 0.025f, -4.1f);

        /// <summary>
        /// The shipped default for the VANILLA hull only (#677) - Lawson's own placement,
        /// recovered from the host save rather than eyeballed.
        ///
        /// <para>Provenance: <c>LCSaveFile2</c> (ES3/AES, the game's own
        /// <c>ES3Defaults</c> password) held
        /// <c>ShipUnlockPos_Fuel Chute = (5.436916, 0.266714, -16.13004)</c> with
        /// <c>ShipUnlockRot_Fuel Chute = (0, 180, 0)</c> - vanilla stores unlockable
        /// placements in WORLD space, so it has to be rebased into the ship frame.
        /// <c>HangarShip</c> rests at world <c>(1.27, 0.28, -7.50)</c> with identity
        /// rotation; that offset is corroborated twice over - once by the delta recorded
        /// in <c>CCTVTerminalUnlockable</c> (#561), and once by this module's own default
        /// pose, which the same session logged as world <c>(-1.38, 0.30, -11.60)</c> for
        /// <see cref="DefaultLocalPosition"/>. Subtracting gives the value below.</para>
        ///
        /// <para>The Y sits ~33 mm under the old default because the ship floor is really
        /// at world Y 0.2417, not 0.275: build mode rests the pivot at
        /// <c>floor + yOffset(0.025)</c>, so this is true floor contact and the old value
        /// was the one floating.</para>
        /// </summary>
        private static readonly Vector3 VanillaDefaultLocalPosition = new Vector3(4.1669f, -0.0083f, -8.63f);

        private static readonly Vector3 DefaultEuler = new Vector3(0f, 180f, 0f);
        private static readonly Vector3 PlacementColliderSize = new Vector3(1.35f, 0.02f, 1.35f);
        private static readonly Vector3 SelectionHandleLongSize = new Vector3(1.32f, 0.035f, 0.16f);
        private static readonly Vector3 SelectionHandleSideSize = new Vector3(0.16f, 0.035f, 1.32f);
        private static readonly Vector3 SelectionHandleNorthPosition = new Vector3(0f, 0.026f, 0.66f);
        private static readonly Vector3 SelectionHandleSouthPosition = new Vector3(0f, 0.026f, -0.66f);
        private static readonly Vector3 SelectionHandleEastPosition = new Vector3(0.66f, 0.026f, 0f);
        private static readonly Vector3 SelectionHandleWestPosition = new Vector3(-0.66f, 0.026f, 0f);

        private const string DawnNamespace = "lgu_ship_systems";
        private const string DawnUnlockableKey = "fuel_chute";

        private static GameObject _prefab;
        private static UnlockableItem _unlockable;
        private static bool _dawnUnlockableDefined;
        private static int _unlockableId = -1;
        private static MethodInfo _spawnUnlockableMethod;

        internal static int UnlockableId => _unlockableId;

        internal static void EnsureRegistered()
        {
            EnsurePrefabBuilt();
            FuelRuntimeUtility.RegisterNetworkPrefab(_prefab);
            EnsureUnlockableDefined();
            EnsureUnlockableRegistered();
            StampUnlockableId(_prefab, _unlockableId);
        }

        internal static bool TryEnsureSpawned(
            ShipSystemsController owner,
            bool isOpen,
            float fuelLevel01,
            out FuelChuteController controller)
        {
            controller = null;
            EnsureRegistered();

            if (TryFindSpawnedChute(out GameObject existingRoot, out controller))
            {
                ConfigureSpawnedChute(existingRoot, controller);
                controller.Configure(owner, isOpen, fuelLevel01);
                return true;
            }

            StartOfRound start = StartOfRound.Instance;
            if (start == null || _unlockableId < 0 || !IsServerRuntime())
                return false;

            if (start.SpawnedShipUnlockables != null && start.SpawnedShipUnlockables.TryGetValue(_unlockableId, out GameObject staleSpawned))
            {
                if (staleSpawned != null)
                    return false;

                start.SpawnedShipUnlockables.Remove(_unlockableId);
            }

            UnlockableItem item = _unlockableId < start.unlockablesList.unlockables.Count
                ? start.unlockablesList.unlockables[_unlockableId]
                : null;
            if (item == null)
                return false;

            item.hasBeenUnlockedByPlayer = true;
            item.alreadyUnlocked = true;
            item.inStorage = false;
            PrepareSpawnPose(item);

            if (!TryInvokeSpawnUnlockable(start, _unlockableId))
                return false;

            if (!TryFindSpawnedChute(out existingRoot, out controller))
                return false;

            ConfigureSpawnedChute(existingRoot, controller);
            controller.Configure(owner, isOpen, fuelLevel01);
            return true;
        }

        internal static void ConfigureSpawnedChute(GameObject root, FuelChuteController controller)
        {
            if (root == null)
                return;

            EnsureRegistered();
            StampUnlockableId(root, _unlockableId);
            EnsureGroundFloorGuard(root);
            EnsureSpawnedInsideShip(root);

            AutoParentToShip parent = root.GetComponent<AutoParentToShip>();
            if (parent != null)
            {
                Vector3 defaultLocal = ResolveDefaultLocalPosition();
                parent.overrideOffset = true;
                if (parent.positionOffset == Vector3.zero)
                    parent.positionOffset = defaultLocal;
                if (parent.rotationOffset == Vector3.zero)
                    parent.rotationOffset = DefaultEuler;
                parent.startingPosition = defaultLocal;
                parent.startingRotation = DefaultEuler;
                parent.disableObject = false;
            }

            LogPlacement(root, "configured");

            PlaceableShipObject[] placeables = root.GetComponentsInChildren<PlaceableShipObject>(true);
            for (int i = 0; i < placeables.Length; i++)
            {
                PlaceableShipObject placeable = placeables[i];
                if (placeable == null)
                    continue;

                placeable.unlockableID = _unlockableId;
                placeable.AllowPlacementOnWalls = false;
                placeable.AllowPlacementOnCounters = false;
                placeable.yOffset = 0.025f;
            }
        }

        internal static bool IsFuelChutePlaceable(PlaceableShipObject placeable)
        {
            return placeable != null && _unlockableId >= 0 && placeable.unlockableID == _unlockableId;
        }

        internal static bool IsGroundFloorPosition(Vector3 worldPosition)
        {
            return IsInsideShipBounds(worldPosition);
        }

        private static bool IsInsideShipBounds(Vector3 worldPosition)
        {
            StartOfRound start = StartOfRound.Instance;
            if (start == null)
                return true;
            if (start.shipInnerRoomBounds != null)
                return start.shipInnerRoomBounds.bounds.Contains(worldPosition);
            return start.shipBounds != null && start.shipBounds.bounds.Contains(worldPosition);
        }

        /// <summary>
        /// The shipped rest pose for the hull that is actually loaded (#677): Lawson's
        /// recovered vanilla placement on the stock hull, the historical offset everywhere
        /// else. This only ever supplies a *default* - <see cref="PrepareSpawnPose"/> still
        /// leaves a genuine player placement alone.
        /// </summary>
        private static Vector3 ResolveDefaultLocalPosition()
        {
            return IsVanillaShipLayout() ? VanillaDefaultLocalPosition : DefaultLocalPosition;
        }

        /// <summary>
        /// Mirrors <c>ExternalBatteryStation.ResolveActiveLayout</c>: trust a live
        /// wider/two-story hull child over the Core layout-name seam, and fall back to the
        /// seam name when the ship is not resolvable yet. Unknown or absent names mean the
        /// stock hull, which is the same default the rest of this module assumes.
        /// </summary>
        private static bool IsVanillaShipLayout()
        {
            try
            {
                StartOfRound sor = StartOfRound.Instance;
                Transform ship = sor != null ? sor.elevatorTransform : null;
                if (ship != null &&
                    (HasActiveChild(ship, "ShipBoth_LGU") ||
                     HasActiveChild(ship, "ShipInsideBoth") ||
                     HasActiveChild(ship, "MelanieShip_LGU")))
                {
                    return false;
                }

                string layout = Y4NGZCore.Modules.ShipSystems.ShipUpgradeState.CurrentLayoutName;
                if (string.IsNullOrWhiteSpace(layout))
                    return true;

                string compact = layout.Trim().Replace(" ", string.Empty).Replace("_", string.Empty);
                return !(compact.Equals("Wider", StringComparison.OrdinalIgnoreCase) ||
                         compact.Equals("TwoStory", StringComparison.OrdinalIgnoreCase) ||
                         compact.Equals("WiderAndTwoStory", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return true;
            }
        }

        private static bool HasActiveChild(Transform ship, string childName)
        {
            Transform child = ship != null ? ship.Find(childName) : null;
            return child != null && child.gameObject.activeInHierarchy;
        }

        private static Vector3 ResolveDefaultWorldPosition()
        {
            Vector3 local = ResolveDefaultLocalPosition();
            Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            return ship != null ? ship.TransformPoint(local) : local;
        }

        private static Vector3 ResolveDefaultWorldEuler()
        {
            Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            Quaternion rotation = ship != null ? ship.rotation * Quaternion.Euler(DefaultEuler) : Quaternion.Euler(DefaultEuler);
            return rotation.eulerAngles;
        }

        private static void PrepareSpawnPose(UnlockableItem item)
        {
            if (item == null)
                return;

            if (item.placedPosition == Vector3.zero || item.placedRotation == Vector3.zero || !IsInsideShipBounds(item.placedPosition))
            {
                item.placedPosition = ResolveDefaultWorldPosition();
                item.placedRotation = ResolveDefaultWorldEuler();
                item.hasBeenMoved = false;
            }
        }

        private static void EnsureSpawnedInsideShip(GameObject root)
        {
            if (root == null || IsInsideShipBounds(root.transform.position))
                return;

            PlaceableShipObject placeable = root.GetComponentInChildren<PlaceableShipObject>(true);
            if (placeable != null && ShipBuildModeManager.Instance != null)
            {
                ShipBuildModeManager.Instance.PlaceShipObject(ResolveDefaultWorldPosition(), ResolveDefaultWorldEuler(), placeable, placementSFX: false);
                return;
            }

            AutoParentToShip parent = root.GetComponent<AutoParentToShip>();
            if (parent != null)
            {
                parent.positionOffset = ResolveDefaultLocalPosition();
                parent.rotationOffset = DefaultEuler;
                parent.MoveToOffset();
            }
        }

        /// <summary>
        /// One line per placement so a future re-tune needs no save decryption (#677):
        /// reports the pose in the ship frame, which is the frame the defaults above are
        /// written in.
        /// </summary>
        private static void LogPlacement(GameObject root, string phase)
        {
            try
            {
                if (root == null)
                    return;

                Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
                Vector3 local = ship != null
                    ? ship.InverseTransformPoint(root.transform.position)
                    : root.transform.position;
                Vector3 localEuler = ship != null
                    ? (Quaternion.Inverse(ship.rotation) * root.transform.rotation).eulerAngles
                    : root.transform.rotation.eulerAngles;

                ShipSystemsBootstrap.Log?.LogInfo(
                    $"[LGUShipSystems.FuelChute] Placed at ship-local ({local.x:F4}, {local.y:F4}, {local.z:F4}) " +
                    $"euler ({localEuler.x:F1}, {localEuler.y:F1}, {localEuler.z:F1}) " +
                    $"phase={phase} layout={(IsVanillaShipLayout() ? "Vanilla" : "Modded")}.");
            }
            catch
            {
                // Diagnostics must never break placement.
            }
        }

        private static void EnsurePrefabBuilt()
        {
            if (_prefab != null)
                return;

            GameObject root = FuelChuteControllerFactory.CreateRuntimeObject(PrefabName);
            root.SetActive(false);
            root.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(root);

            NetworkObject networkObject = root.GetComponent<NetworkObject>() ?? root.AddComponent<NetworkObject>();
            networkObject.SynchronizeTransform = true;
            networkObject.SceneMigrationSynchronization = true;
            networkObject.SpawnWithObservers = true;
            networkObject.DontDestroyWithOwner = true;
            networkObject.AutoObjectParentSync = true;
            FuelRuntimeUtility.AssignStableNetworkHash(networkObject, PrefabName);

            AutoParentToShip parent = root.GetComponent<AutoParentToShip>() ?? root.AddComponent<AutoParentToShip>();
            // Prefab build can run before StartOfRound exists; ResolveDefaultLocalPosition
            // then reports the vanilla hull, and ConfigureSpawnedChute re-applies the real
            // layout's offset at spawn time anyway (#677).
            Vector3 prefabLocal = ResolveDefaultLocalPosition();
            parent.overrideOffset = true;
            parent.positionOffset = prefabLocal;
            parent.rotationOffset = DefaultEuler;
            parent.startingPosition = prefabLocal;
            parent.startingRotation = DefaultEuler;
            parent.disableObject = false;

            ConfigurePlaceableHandle(root, parent);
            EnsureGroundFloorGuard(root);
            _prefab = root;
        }

        private static void ConfigurePlaceableHandle(GameObject root, AutoParentToShip parent)
        {
            Transform existing = root.transform.Find("FuelChute_PlaceableHandle");
            GameObject handle = existing != null ? existing.gameObject : new GameObject("FuelChute_PlaceableHandle");
            handle.transform.SetParent(root.transform, false);
            handle.transform.localPosition = Vector3.zero;
            handle.transform.localRotation = Quaternion.identity;
            handle.transform.localScale = Vector3.one;
            handle.layer = PlaceableObjectLayer;
            FuelRuntimeUtility.SetTagSafe(handle, "PlaceableObject");

            BoxCollider placementCollider = handle.GetComponent<BoxCollider>() ?? handle.AddComponent<BoxCollider>();
            placementCollider.isTrigger = true;
            placementCollider.center = Vector3.zero;
            placementCollider.size = PlacementColliderSize;

            AudioSource audio = handle.GetComponent<AudioSource>() ?? handle.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.minDistance = 1f;
            audio.maxDistance = 12f;

            MeshFilter mesh = CreatePlacementMeshProxy(root.transform);
            ConfigurePlaceableComponent(handle, parent, mesh, placementCollider);
            CreateSelectionColliderHandles(handle, placementCollider);
        }

        private static void CreateSelectionColliderHandles(GameObject handle, BoxCollider placementCollider)
        {
            if (handle == null)
                return;

            BoxCollider[] colliders = handle.GetComponents<BoxCollider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null && colliders[i] != placementCollider)
                    UnityEngine.Object.Destroy(colliders[i]);
            }

            CreateSelectionColliderHandle(handle, "FuelChute_SelectHandle_North", SelectionHandleNorthPosition, SelectionHandleLongSize);
            CreateSelectionColliderHandle(handle, "FuelChute_SelectHandle_South", SelectionHandleSouthPosition, SelectionHandleLongSize);
            CreateSelectionColliderHandle(handle, "FuelChute_SelectHandle_East", SelectionHandleEastPosition, SelectionHandleSideSize);
            CreateSelectionColliderHandle(handle, "FuelChute_SelectHandle_West", SelectionHandleWestPosition, SelectionHandleSideSize);
        }

        private static void CreateSelectionColliderHandle(GameObject handle, string label, Vector3 center, Vector3 size)
        {
            _ = label;
            BoxCollider selectorCollider = handle.AddComponent<BoxCollider>();
            selectorCollider.isTrigger = false;
            selectorCollider.center = center;
            selectorCollider.size = size;
        }

        private static PlaceableShipObject ConfigurePlaceableComponent(GameObject handle, AutoParentToShip parent, MeshFilter mesh, BoxCollider placementCollider)
        {
            PlaceableShipObject placeable = handle.GetComponent<PlaceableShipObject>() ?? handle.AddComponent<PlaceableShipObject>();
            placeable.unlockableID = _unlockableId;
            placeable.parentObject = parent;
            placeable.mainMesh = mesh;
            placeable.mainTransform = mesh != null ? mesh.transform : handle.transform;
            placeable.placeObjectCollider = placementCollider;
            placeable.yOffset = 0.025f;
            placeable.overrideWallOffset = false;
            placeable.wallOffset = 0f;
            placeable.AllowPlacementOnWalls = false;
            placeable.AllowPlacementOnCounters = false;
            placeable.doCollisionPointCheck = false;
            return placeable;
        }

        private static MeshFilter FindPlacementMesh(Transform root)
        {
            if (root == null)
                return null;

            MeshFilter[] meshes = root.GetComponentsInChildren<MeshFilter>(true);
            MeshFilter firstValid = null;
            for (int i = 0; i < meshes.Length; i++)
            {
                MeshFilter mesh = meshes[i];
                if (mesh == null || mesh.sharedMesh == null)
                    continue;

                if (firstValid == null)
                    firstValid = mesh;
                string name = mesh.name ?? string.Empty;
                if (name.IndexOf("Rim", StringComparison.OrdinalIgnoreCase) >= 0)
                    return mesh;
            }

            return firstValid;
        }

        private static MeshFilter CreatePlacementMeshProxy(Transform parent)
        {
            GameObject proxy = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            proxy.name = "FuelChute_PlacementMeshProxy";
            proxy.transform.SetParent(parent, false);
            proxy.transform.localPosition = Vector3.zero;
            proxy.transform.localRotation = Quaternion.identity;
            proxy.transform.localScale = new Vector3(1.4f, 0.05f, 1.4f);
            Collider collider = proxy.GetComponent<Collider>();
            if (collider != null)
                UnityEngine.Object.Destroy(collider);
            Renderer renderer = proxy.GetComponent<Renderer>();
            if (renderer != null)
                renderer.enabled = false;
            return proxy.GetComponent<MeshFilter>();
        }

        // DawnLib owns the ship-unlockable save file. An UnlockableItem that is only pushed into
        // StartOfRound.unlockablesList has no DawnUnlockableInfo attached, which makes DawnLib log
        // "Fuel Chute doesn't have a DawnUnlockableInfo ... cannot be committed to the savefile"
        // on every save and drops the chute's placement between sessions. Defining it through
        // DawnLib at plugin load (before StartOfRound exists) attaches that info to the very same
        // UnlockableItem instance the list-registration path below reuses, so host and clients
        // agree on one entry no matter which of the two paths inserts it into the list first.
        private static void EnsureUnlockableDefined()
        {
            if (_unlockable != null)
                return;

            _unlockable = new UnlockableItem();
            ConfigureUnlockable(_unlockable);
            RegisterDawnUnlockableIfAvailable(_unlockable);
        }

        private static void RegisterDawnUnlockableIfAvailable(UnlockableItem item)
        {
            if (item == null || _dawnUnlockableDefined)
                return;

            try
            {
                DefineDawnUnlockableCore(item);
                _dawnUnlockableDefined = true;
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems] Registered fuel chute furniture with DawnLib ({DawnNamespace}/{DawnUnlockableKey}).");
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems] DawnLib unlockable registration skipped or failed for the fuel chute; DawnLib may be absent: {ex.Message}");
            }
        }

        // NoInlining keeps the typed Dawn.* references out of RegisterDawnUnlockableIfAvailable's
        // JIT scope, so a missing DawnLib assembly lands in the catch above instead of killing
        // plugin startup. Mirrors FuelRuntimeUtility.RegisterDawnItemCore.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DefineDawnUnlockableCore(UnlockableItem item)
        {
            NamespacedKey<DawnUnlockableItemInfo> key =
                NamespacedKey<DawnUnlockableItemInfo>.From(DawnNamespace, DawnUnlockableKey);
            DawnLib.DefineUnlockable(key, item, builder => { });
        }

        private static void EnsureUnlockableRegistered()
        {
            StartOfRound start = StartOfRound.Instance;
            if (start == null || start.unlockablesList == null || start.unlockablesList.unlockables == null)
                return;

            EnsureUnlockableDefined();

            for (int i = 0; i < start.unlockablesList.unlockables.Count; i++)
            {
                UnlockableItem existing = start.unlockablesList.unlockables[i];
                if (existing != null && string.Equals(existing.unlockableName, UnlockableName, StringComparison.OrdinalIgnoreCase))
                {
                    _unlockable = existing;
                    _unlockableId = i;
                    ConfigureUnlockable(existing);
                    return;
                }
            }

            start.unlockablesList.unlockables.Add(_unlockable);
            _unlockableId = start.unlockablesList.unlockables.Count - 1;
            StampUnlockableId(_prefab, _unlockableId);
            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems] Registered hidden fuel chute furniture unlockable id {_unlockableId}.");
        }

        private static void ConfigureUnlockable(UnlockableItem item)
        {
            if (item == null)
                return;

            item.unlockableName = UnlockableName;
            item.prefabObject = _prefab;
            item.unlockableType = 1;
            item.IsPlaceable = true;
            item.hasBeenUnlockedByPlayer = true;
            item.alreadyUnlocked = true;
            item.canBeStored = false;
            item.inStorage = false;
            item.spawnPrefab = true;
            item.maxNumber = 1;
            item.luckValue = 0f;
        }

        private static void StampUnlockableId(GameObject root, int id)
        {
            if (root == null || id < 0)
                return;

            AutoParentToShip parent = root.GetComponent<AutoParentToShip>();
            if (parent != null)
                parent.unlockableID = id;

            PlaceableShipObject[] placeables = root.GetComponentsInChildren<PlaceableShipObject>(true);
            for (int i = 0; i < placeables.Length; i++)
            {
                if (placeables[i] != null)
                    placeables[i].unlockableID = id;
            }
        }

        private static bool TryFindSpawnedChute(out GameObject root, out FuelChuteController controller)
        {
            root = null;
            controller = null;
            StartOfRound start = StartOfRound.Instance;
            if (start != null && start.SpawnedShipUnlockables != null && _unlockableId >= 0 && start.SpawnedShipUnlockables.TryGetValue(_unlockableId, out GameObject spawned) && spawned != null)
            {
                root = spawned;
                controller = spawned.GetComponentInChildren<FuelChuteController>(true);
                return controller != null;
            }

            PlaceableShipObject[] placeables = UnityEngine.Object.FindObjectsOfType<PlaceableShipObject>();
            for (int i = 0; i < placeables.Length; i++)
            {
                PlaceableShipObject placeable = placeables[i];
                if (!IsFuelChutePlaceable(placeable))
                    continue;

                root = placeable.parentObject != null ? placeable.parentObject.gameObject : placeable.gameObject;
                controller = root != null ? root.GetComponentInChildren<FuelChuteController>(true) : null;
                if (controller != null)
                    return true;
            }

            return false;
        }

        private static bool TryInvokeSpawnUnlockable(StartOfRound start, int unlockableId)
        {
            try
            {
                _spawnUnlockableMethod ??= typeof(StartOfRound).GetMethod("SpawnUnlockable", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_spawnUnlockableMethod == null)
                {
                    ShipSystemsBootstrap.Log?.LogWarning("[LGUShipSystems] Could not find StartOfRound.SpawnUnlockable for fuel chute furniture.");
                    return false;
                }

                bool restorePrefabInactive = _prefab != null && !_prefab.activeSelf;
                if (restorePrefabInactive)
                    _prefab.SetActive(true);

                try
                {
                    _spawnUnlockableMethod.Invoke(start, new object[] { unlockableId, false });
                }
                finally
                {
                    if (restorePrefabInactive && _prefab != null)
                        _prefab.SetActive(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems] Failed to spawn fuel chute furniture unlockable: {ex.Message}");
                return false;
            }
        }

        private static void EnsureGroundFloorGuard(GameObject root)
        {
            if (root != null && root.GetComponent<FuelChuteFurniturePlacementGuard>() == null)
                root.AddComponent<FuelChuteFurniturePlacementGuard>();
        }

        private static bool IsServerRuntime()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager == null || manager.IsServer;
        }
    }

    internal sealed class FuelChuteFurniturePlacementGuard : MonoBehaviour
    {
        private static FieldInfo _placingObjectField;
        private static FieldInfo _canConfirmPositionField;
        private static FieldInfo _ghostObjectRendererField;
        private static FieldInfo _ghostObjectRedField;

        private void LateUpdate()
        {
            ShipBuildModeManager manager = ShipBuildModeManager.Instance;
            if (manager == null || !manager.InBuildMode)
                return;

            PlaceableShipObject placingObject = GetPlacingObject(manager);
            if (!FuelChuteFurnitureRegistry.IsFuelChutePlaceable(placingObject))
                return;

            Transform ghost = manager.ghostObject;
            if (ghost == null || FuelChuteFurnitureRegistry.IsGroundFloorPosition(ghost.position))
                return;

            SetCanConfirmPosition(manager, false);
            MarkGhostInvalid(manager);
        }

        private static PlaceableShipObject GetPlacingObject(ShipBuildModeManager manager)
        {
            try
            {
                _placingObjectField ??= typeof(ShipBuildModeManager).GetField("placingObject", BindingFlags.Instance | BindingFlags.NonPublic);
                return _placingObjectField?.GetValue(manager) as PlaceableShipObject;
            }
            catch
            {
                return null;
            }
        }

        private static void SetCanConfirmPosition(ShipBuildModeManager manager, bool value)
        {
            try
            {
                _canConfirmPositionField ??= typeof(ShipBuildModeManager).GetField("CanConfirmPosition", BindingFlags.Instance | BindingFlags.NonPublic);
                _canConfirmPositionField?.SetValue(manager, value);
            }
            catch
            {
                // Best-effort guard; vanilla constraints still apply if reflection fails.
            }
        }

        private static void MarkGhostInvalid(ShipBuildModeManager manager)
        {
            try
            {
                _ghostObjectRendererField ??= typeof(ShipBuildModeManager).GetField("ghostObjectRenderer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _ghostObjectRedField ??= typeof(ShipBuildModeManager).GetField("ghostObjectRed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MeshRenderer renderer = _ghostObjectRendererField?.GetValue(manager) as MeshRenderer;
                Material red = _ghostObjectRedField?.GetValue(manager) as Material;
                if (renderer != null && red != null)
                    renderer.sharedMaterial = red;
            }
            catch
            {
                // Visual feedback is secondary to denying placement.
            }
        }
    }
}