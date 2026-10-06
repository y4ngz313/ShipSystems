using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Layout;
using Y4NGZCompany.ShipSystems.Fuel;

namespace Y4NGZCompany.ShipSystems.Power
{
    internal sealed partial class ExternalBatteryStation
    {
        private static readonly BatteryPlacement VanillaPlacement = new BatteryPlacement(
            ShipLayoutType.Vanilla,
            new Vector3(-5.35f, 1.55f, -5.72f),
            new Vector3(0f, -90f, 0f));
        private static readonly BatteryPlacement WiderPlacement = new BatteryPlacement(
            ShipLayoutType.Wider,
            new Vector3(-8.65f, 1.55f, -5.72f),
            new Vector3(0f, -90f, 0f));
        private static readonly BatteryPlacement TwoStoryPlacement = new BatteryPlacement(
            ShipLayoutType.TwoStory,
            new Vector3(-5.35f, 1.55f, -10.72f),
            new Vector3(0f, -90f, 0f));
        private static readonly BatteryPlacement WiderAndTwoStoryPlacement = new BatteryPlacement(
            ShipLayoutType.WiderAndTwoStory,
            new Vector3(-8.65f, 1.55f, -10.72f),
            new Vector3(0f, -90f, 0f));
        private static readonly Vector3 SocketLocalPosition = new Vector3(0.34f, -0.26f, 0.16f);
        private static readonly Vector3 SabotageStandLocalPosition = new Vector3(0f, -1.1f, 2.25f);
        private const float PlacementMinScale = 0.25f;
        private const float PlacementMaxScale = 4f;
        private const float PlacementRefreshIntervalSeconds = 2f;
        private const float SabotageNavMeshSampleIntervalSeconds = 2f;
        private const float SabotageNavMeshMoveEpsilon = 0.05f;
        private const float SabotageNavMeshMoveEpsilonSqr = SabotageNavMeshMoveEpsilon * SabotageNavMeshMoveEpsilon;
        private static readonly string[] ExteriorPanelPrimaryNames =
        {
            "SideMachineryLeft",
            "SideMachineryRight"
        };
        private static readonly string[] ExteriorPanelFallbackNames =
        {
            "Pipework2.002",
            "MeterBoxDevice.001"
        };

        private struct BatteryPlacement
        {
            internal readonly ShipLayoutType Layout;
            internal readonly Vector3 LocalPosition;
            internal readonly Vector3 LocalEulerAngles;

            internal BatteryPlacement(ShipLayoutType layout, Vector3 localPosition, Vector3 localEulerAngles)
            {
                Layout = layout;
                LocalPosition = localPosition;
                LocalEulerAngles = localEulerAngles;
            }
        }

        private struct CachedPlacement
        {
            internal ShipLayoutType Layout;
            internal bool UsingPanelAnchor;
            internal string PanelAnchorLabel;
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
            internal Vector3 LocalScale;
        }

        private static readonly string[] PlacementLayouts =
        {
            "Vanilla",
            "Wider",
            "TwoStory",
            "WiderAndTwoStory"
        };
        private static BatteryPlacementProfile _placementProfile;
        private static bool _placementProfileLoaded;
        private static Type _bundySiegeBaseType;
        private static MethodInfo _bundySiegeSwingPulseMethod;
        private static MethodInfo _bundySiegeAnimationStoppedMethod;
        private static bool _bundySiegeMethodsResolved;

        private readonly ShipSystemsController _controller;
        private GameObject _root;
        private GameObject _batteryBody;
        private GameObject _socketObject;
        private GameObject _standMarker;
        private GameObject _apparatusVisual;
        private GameObject _brokenBatteryDrop;
        private ParticleSystem _brokenBatterySmoke;
        private InteractTrigger _socketTrigger;
        private Collider _socketTriggerCollider;
        private string _socketTriggerTipItemName;
        private InteractTrigger _dropRepairTrigger;
        private bool _criticalAnnounced;
        private bool _dropConvertedToItem;
        private const float SabotageHitDecaySeconds = 25f;
        private Vector3 _socketWorldPosition;
        private Vector3 _sabotageWorldPosition;
        private int _sabotageHits;
        private float _lastSabotageHitAt;
        private float _nextMaskedAttackAnimAt;
        private float _nextPlacementRefreshAt;
        private float _nextSabotageNavMeshSampleAt;
        private bool _hasCachedPlacement;
        private CachedPlacement _cachedPlacement;
        private bool _hasSabotageNavMeshSample;
        private bool _loggedMissingSabotageStand;
        private Vector3 _lastSabotageRawPosition;
        private bool? _lastVisualBroken;
        private bool _loggedPlacement;
        private bool _usingPanelAnchor;
        private string _currentPanelAnchorLabel;
        private ShipLayoutType _currentPlacementLayout = (ShipLayoutType)(-1);
        private bool _placementActive;
        private string _placementLayout = "Vanilla";
        private Vector3 _placementStartLocalPosition;
        private Quaternion _placementStartLocalRotation;
        private Vector3 _placementStartLocalScale;
        private PlayerControllerB _placementPlayer;
        private Camera _placementCamera;
        private bool _placementCapturedInputState;
        private CursorLockMode _placementSavedLock;
        private bool _placementSavedCursorVisible;
        private bool _placementSavedDisableLookInput;
        private bool _placementSavedDisableMoveInput;
        private bool _placementCapturedCameraTransform;
        private Vector3 _placementSavedCameraLocalPosition;
        private Quaternion _placementSavedCameraLocalRotation;
        private const float PlacementViewOrbitSensitivity = 0.18f;
        private const float PlacementViewMinPitch = -35f;
        private const float PlacementViewMaxPitch = 55f;
        private float _placementViewYaw;
        private float _placementViewPitch;
        private string _debugStatus = "ready";
        private float _debugStatusVisibleUntil;
        private readonly List<EnemyAI> _activeSaboteurs = new List<EnemyAI>();
        private readonly List<EnemyAI> _currentSaboteurs = new List<EnemyAI>();
        private AudioSource _workingLoopSource;
        private AudioSource _brokenLoopSource;
        private ParticleSystem _workingSparkParticle;
        private BoxCollider _dropRepairTriggerCollider;
        private float _workingLoopDuckUntil;
        private bool _batteryAudioRequested;

        internal ExternalBatteryStation(ShipSystemsController controller)
        {
            _controller = controller;
        }

        internal void Ensure()
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || sor.elevatorTransform == null || !sor.elevatorTransform.gameObject.activeInHierarchy)
            {
                Destroy();
                return;
            }

            if (_root != null)
            {
                ApplyCurrentLayoutPlacement();
                return;
            }

            _root = new GameObject("LGUShipSystems_ExteriorBattery");
            _root.transform.SetParent(sor.elevatorTransform, worldPositionStays: false);

            BuildBatteryVisuals();
            BuildDamageReceiver();
            BuildScanNode();
            BuildSocketInteract();
            EnsureWorkingSparkParticle();
            ApplyCurrentLayoutPlacement(force: true);
            RefreshVisuals();
        }

        internal void Tick()
        {
            if (_root == null) return;
            TickDebugPlacementEditor();
            ApplyCurrentLayoutPlacement();
            UpdateWorldPositions();
            TickLocalRestoreInteraction();
            UpdateInteractTriggers();
            UpdateDamageReceiver();

            if (NetworkUtils.IsServer())
                TickSabotage();

            RefreshVisuals();
            TickBatteryLoops();
        }

        internal void ResetStation()
        {
            _sabotageHits = 0;
            _criticalAnnounced = false;
            _dropConvertedToItem = false;
            _nextMaskedAttackAnimAt = 0f;
            ClearActiveSaboteurs();
            if (_apparatusVisual != null)
                UnityEngine.Object.Destroy(_apparatusVisual);
            _apparatusVisual = null;
            DestroyBrokenDrop();
            _lastVisualBroken = null;
            RefreshVisuals();
        }

        internal void ClearRuntimeVisuals()
        {
            _sabotageHits = 0;
            _criticalAnnounced = false;
            _dropConvertedToItem = false;
            _nextMaskedAttackAnimAt = 0f;
            _loggedPlacement = false;
            _currentPlacementLayout = (ShipLayoutType)(-1);
            _hasCachedPlacement = false;
            _nextPlacementRefreshAt = 0f;
            _hasSabotageNavMeshSample = false;
            _nextSabotageNavMeshSampleAt = 0f;
            _lastVisualBroken = null;
        }

        internal void ShowBrokenState()
        {
            RefreshVisuals();
        }

        internal void InterruptSabotage(EnemyAI enemy)
        {
            if (enemy != null && _activeSaboteurs.Contains(enemy))
            {
                StopSabotageAnimation(enemy);
                _activeSaboteurs.Remove(enemy);
                _sabotageHits = Mathf.Max(0, _sabotageHits - 1);
            }
        }

        internal void Destroy()
        {
            _placementActive = false;
            RestorePlacementInputCapture();
            DestroyBrokenDrop();
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
            _batteryBody = null;
            _socketObject = null;
            _standMarker = null;
            _apparatusVisual = null;
            _brokenBatteryDrop = null;
            _brokenBatterySmoke = null;
            _socketTrigger = null;
            _socketTriggerCollider = null;
            _dropRepairTrigger = null;
            _dropRepairTriggerCollider = null;
            _workingLoopSource = null;
            _brokenLoopSource = null;
            _workingSparkParticle = null;
            _workingLoopDuckUntil = 0f;
            _criticalAnnounced = false;
            _dropConvertedToItem = false;
            _placementCamera = null;
            _placementPlayer = null;
            _activeSaboteurs.Clear();
            _currentSaboteurs.Clear();
            _sabotageHits = 0;
            _nextMaskedAttackAnimAt = 0f;
            _nextPlacementRefreshAt = 0f;
            _hasCachedPlacement = false;
            _nextSabotageNavMeshSampleAt = 0f;
            _hasSabotageNavMeshSample = false;
            _lastVisualBroken = null;
        }

        internal bool TryGetSocketWorldPosition(out Vector3 position)
        {
            position = _socketWorldPosition;
            return _root != null;
        }

        internal bool TryGetSabotageWorldPosition(out Vector3 position)
        {
            position = _sabotageWorldPosition;
            return _root != null && _hasSabotageNavMeshSample;
        }

        internal void StartDebugPlacement(string layoutName)
        {
            if (_root == null)
            {
                SetDebugStatus("land the ship before placing the battery", 3f);
                return;
            }

            string targetLayout = string.IsNullOrWhiteSpace(layoutName)
                ? ResolveActiveLayout().ToString()
                : layoutName;
            _placementLayout = NormalizeLayoutName(targetLayout);
            ApplyPlacementForLayout(_placementLayout);
            _placementStartLocalPosition = _root.transform.localPosition;
            _placementStartLocalRotation = _root.transform.localRotation;
            _placementStartLocalScale = _root.transform.localScale;
            _placementActive = true;
            _placementViewYaw = 0f;
            _placementViewPitch = 0f;
            BeginPlacementInputCapture();
            EnsurePlacementCamera();
            UpdatePlacementCameraPose();
            SetDebugStatus($"editing {_placementLayout}", 2f);
            ShipSystemsBootstrap.Log.LogInfo($"[LGUShipSystems.BatteryPlacement] Editing layout={_placementLayout} pos={_root.transform.localPosition} euler={_root.transform.localEulerAngles} scale={_root.transform.localScale}.");
        }

        internal void ShowDebugPreview()
        {
            if (_root == null)
            {
                SetDebugStatus("land the ship before previewing the battery", 3f);
                return;
            }

            ApplyCurrentLayoutPlacement(force: true);
            SetDebugStatus($"preview {NormalizeLayoutName(ResolveActiveLayout().ToString())}", 2f);
        }

        internal void ClearDebugPreview()
        {
            if (_placementActive)
                CancelDebugPlacement();
            else
                SetDebugStatus("battery preview cleared", 1.5f);
        }

        internal string GetDebugStatus()
        {
            string layout = _root != null
                ? NormalizeLayoutName(ResolveActiveLayout().ToString())
                : "unavailable";
            string state = _placementActive
                ? $"editing {_placementLayout}"
                : Time.unscaledTime <= _debugStatusVisibleUntil ? _debugStatus : "ready";
            string status = $"Battery {layout}: {state}\nPresets {BuildPresetStatus()}";
            if (_placementActive)
                status += "\nControls WASD move, Q/E height, F/G pitch X, R/T yaw Y, Z/X roll Z, C/V scale, Enter save, Esc cancel";
            return status;
        }

        internal void RotateDebugPlacement(float xDegrees, float yDegrees, float zDegrees)
        {
            if (!TryUseActiveDebugPlacement("rotating the battery"))
                return;

            NudgePlacementPreview(Vector3.zero, new Vector3(xDegrees, yDegrees, zDegrees), 0f);
        }

        internal void SetDebugPlacementScale(float uniformScale)
        {
            if (!TryUseActiveDebugPlacement("scaling the battery"))
                return;

            if (!IsValidScaleComponent(uniformScale))
            {
                SetDebugStatus("invalid battery scale", 2f);
                return;
            }

            float clamped = Mathf.Clamp(uniformScale, PlacementMinScale, PlacementMaxScale);
            _root.transform.localScale = Vector3.one * clamped;
            UpdateWorldPositions();
            UpdatePlacementCameraPose();
            SetDebugStatus($"editing {_placementLayout} {FormatPlacementSummary()}", 0.35f);
        }

        private bool TryUseActiveDebugPlacement(string action)
        {
            if (_root == null)
            {
                SetDebugStatus("land the ship before editing the battery", 3f);
                return false;
            }

            if (!_placementActive)
            {
                SetDebugStatus($"start battery placement before {action}", 2f);
                return false;
            }

            return true;
        }

        private void TickSabotage()
        {
            if (!_controller.IsSabotageEligible() || _controller.BatteryBroken || !_controller.ShipPowerOnline)
            {
                ClearActiveSaboteurs();
                DecaySabotageHits();
                return;
            }

            FindSaboteurs(_currentSaboteurs);
            if (_currentSaboteurs.Count == 0)
            {
                ClearActiveSaboteurs();
                DecaySabotageHits();
                return;
            }

            StopRemovedSaboteurAnimations(_currentSaboteurs);
            // The first swing waits one full interval after saboteurs arrive, so
            // an unupgraded break takes hits × interval (~9.6 s), not hits-1.
            if (_activeSaboteurs.Count == 0 && Time.unscaledTime >= _nextMaskedAttackAnimAt)
            {
                _nextMaskedAttackAnimAt = Time.unscaledTime + GetHitIntervalSeconds();
                for (int i = 0; i < _currentSaboteurs.Count; i++)
                    PlaySabotageAnimation(_currentSaboteurs[i], true, false);
                _activeSaboteurs.AddRange(_currentSaboteurs);
                return;
            }

            bool poseRpcPulse = Time.unscaledTime >= _nextMaskedAttackAnimAt;
            if (poseRpcPulse)
                _nextMaskedAttackAnimAt = Time.unscaledTime + GetHitIntervalSeconds();

            for (int i = 0; i < _currentSaboteurs.Count; i++)
            {
                EnemyAI enemy = _currentSaboteurs[i];
                bool newlyActive = !_activeSaboteurs.Contains(enemy);
                PlaySabotageAnimation(enemy, newlyActive || poseRpcPulse, poseRpcPulse);
            }

            _activeSaboteurs.Clear();
            _activeSaboteurs.AddRange(_currentSaboteurs);

            // Discrete hit model: each swing pulse lands exactly one hit on the
            // battery, no matter how many saboteurs are swinging. Breaking takes
            // BatteryHitsToBreak swings spaced BatteryHitIntervalSeconds apart.
            if (!poseRpcPulse)
                return;

            ApplyDamage(1, GetEnemyName(_currentSaboteurs[0]) + " sabotage", sabotage: true);
        }

        private void DecaySabotageHits()
        {
            if (_sabotageHits > 0 && Time.unscaledTime - _lastSabotageHitAt >= SabotageHitDecaySeconds)
            {
                _sabotageHits = 0;
                _criticalAnnounced = false;
            }
        }

        private int GetRequiredSabotageHits()
        {
            int hits = Y4NGZCore.Modules.ShipSystems.ShipUpgradeState.Has("BatteryReinforcement")
                ? ShipSystemsBootstrap.BatteryHitsToBreakWithPlating?.Value ?? 9
                : ShipSystemsBootstrap.BatteryHitsToBreak?.Value ?? 6;
            return Mathf.Max(1, hits);
        }

        private static float GetHitIntervalSeconds()
        {
            return Mathf.Max(0.8f, ShipSystemsBootstrap.BatteryHitIntervalSeconds?.Value ?? 1.6f);
        }

        private void ClearActiveSaboteurs()
        {
            for (int i = 0; i < _activeSaboteurs.Count; i++)
                StopSabotageAnimation(_activeSaboteurs[i]);

            _activeSaboteurs.Clear();
            _currentSaboteurs.Clear();
            // Keep _nextMaskedAttackAnimAt: saboteurs flickering in and out of
            // range must not earn instant extra swings.
        }

        private void StopRemovedSaboteurAnimations(List<EnemyAI> currentSaboteurs)
        {
            for (int i = 0; i < _activeSaboteurs.Count; i++)
            {
                EnemyAI enemy = _activeSaboteurs[i];
                if (enemy != null && !currentSaboteurs.Contains(enemy))
                    StopSabotageAnimation(enemy);
            }
        }

        private void PlaySabotageAnimation(EnemyAI enemy, bool sendPoseRpc, bool triggerAttack)
        {
            if (!(enemy is MaskedPlayerEnemy) &&
                enemy != null &&
                !enemy.isEnemyDead &&
                triggerAttack &&
                TryGetBundySiegeMethods(enemy, out MethodInfo swingPulseMethod, out _) &&
                swingPulseMethod != null)
            {
                try
                {
                    swingPulseMethod.Invoke(enemy, new object[] { _socketWorldPosition });
                    if (ShipSystemsBootstrap.VerboseLogging?.Value == true)
                        ShipSystemsBootstrap.Log.LogInfo($"[LGUShipSystems] Drove Bundy battery siege swing pulse at {_socketWorldPosition}.");
                }
                catch
                {
                    // If Bundy is mid-state-change, skip this pulse.
                }

                return;
            }

            if (!(enemy is MaskedPlayerEnemy masked) || masked.isEnemyDead)
                return;

            try
            {
                masked.transform.LookAt(new Vector3(_socketWorldPosition.x, masked.transform.position.y, _socketWorldPosition.z));
                masked.transform.eulerAngles = new Vector3(0f, masked.transform.eulerAngles.y, 0f);

                if (sendPoseRpc)
                {
                    masked.SetRunningClientRpc(false);
                    masked.SetHandsOutClientRpc(true);
                }

                if (triggerAttack)
                {
                    Animator animator = masked.GetComponentInChildren<Animator>();
                    animator?.SetTrigger("HitEnemy");
                }
            }
            catch
            {
                // If the masked enemy is mid-state-change, skip this pulse.
            }
        }

        private static void StopSabotageAnimation(EnemyAI enemy)
        {
            if (!(enemy is MaskedPlayerEnemy) &&
                enemy != null &&
                !enemy.isEnemyDead &&
                TryGetBundySiegeMethods(enemy, out _, out MethodInfo animationStoppedMethod) &&
                animationStoppedMethod != null)
            {
                try
                {
                    animationStoppedMethod.Invoke(enemy, null);
                }
                catch
                {
                    // Ignore teardown failures during despawn/state transitions.
                }

                return;
            }

            if (!(enemy is MaskedPlayerEnemy masked) || masked.isEnemyDead)
                return;

            try
            {
                masked.SetHandsOutClientRpc(false);
            }
            catch
            {
                // Ignore teardown failures during despawn/state transitions.
            }
        }

        private static bool TryGetBundySiegeMethods(
            EnemyAI enemy,
            out MethodInfo swingPulseMethod,
            out MethodInfo animationStoppedMethod)
        {
            swingPulseMethod = null;
            animationStoppedMethod = null;
            if (enemy == null)
                return false;

            Type bundyBaseType = enemy.GetType();
            while (bundyBaseType != null &&
                   !string.Equals(bundyBaseType.Name, "BundyAIBase", StringComparison.Ordinal))
            {
                bundyBaseType = bundyBaseType.BaseType;
            }

            if (bundyBaseType == null)
                return false;

            if (!_bundySiegeMethodsResolved || _bundySiegeBaseType != bundyBaseType)
            {
                _bundySiegeMethodsResolved = true;
                _bundySiegeBaseType = bundyBaseType;
                _bundySiegeSwingPulseMethod = null;
                _bundySiegeAnimationStoppedMethod = null;

                try
                {
                    _bundySiegeSwingPulseMethod = bundyBaseType.GetMethod(
                        "OnBatterySiegeSwingPulse",
                        BindingFlags.Instance | BindingFlags.Public,
                        binder: null,
                        types: new[] { typeof(Vector3) },
                        modifiers: null);
                    _bundySiegeAnimationStoppedMethod = bundyBaseType.GetMethod(
                        "OnBatterySiegeAnimationStopped",
                        BindingFlags.Instance | BindingFlags.Public,
                        binder: null,
                        types: Type.EmptyTypes,
                        modifiers: null);
                }
                catch
                {
                    // Older Bundy builds do not expose the battery siege animation contract.
                }
            }

            swingPulseMethod = _bundySiegeSwingPulseMethod;
            animationStoppedMethod = _bundySiegeAnimationStoppedMethod;
            return true;
        }

        private void FindSaboteurs(List<EnemyAI> saboteurs)
        {
            saboteurs.Clear();
            RoundManager round = RoundManager.Instance;
            if (round == null || round.SpawnedEnemies == null) return;

            for (int i = 0; i < round.SpawnedEnemies.Count; i++)
            {
                EnemyAI enemy = round.SpawnedEnemies[i];
                if (enemy == null || enemy.isEnemyDead) continue;

                if (BatterySabotageDirector.IsEnemySabotagingAtBattery(enemy, _sabotageWorldPosition, out float distance) &&
                    IsEnemyAbleToHitBattery(enemy))
                {
                    saboteurs.Add(enemy);
                    continue;
                }

                distance = Vector3.Distance(enemy.transform.position, _sabotageWorldPosition);

                if (BatterySabotageDirector.IsBatteryTargetActive(enemy) &&
                    !BatterySabotageDirector.IsBatteryInterruptActive(enemy) &&
                    distance <= 180f)
                {
                    TryNudgeEnemyToBattery(enemy);
                }
            }
        }

        private bool IsEnemyAbleToHitBattery(EnemyAI enemy)
        {
            if (enemy == null || _root == null)
                return false;

            // Contact range: swings must land on the battery, not from metres
            // away. The configured radius only governs the approach.
            float configuredRadius = ShipSystemsBootstrap.BatterySabotageRadius?.Value ?? 4.2f;
            float hitRange = Mathf.Clamp(configuredRadius * 0.65f, 2.4f, 3.2f);
            Vector3 attackOrigin = enemy.transform.position + Vector3.up * 1.35f;
            Vector3 socket = _socketWorldPosition;
            if (Vector3.Distance(attackOrigin, socket) > hitRange)
                return false;

            if (BatterySabotageDirector.IsBatteryTargetActive(enemy))
                return true;

            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : Physics.DefaultRaycastLayers;

            try
            {
                if (Physics.Linecast(attackOrigin, socket, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore))
                {
                    Transform hitTransform = hit.collider != null ? hit.collider.transform : null;
                    if (hitTransform == null)
                        return true;

                    if (IsDescendantOf(hitTransform, enemy.transform))
                        return true;

                    if (IsDescendantOf(hitTransform, _root.transform))
                        return true;

                    return false;
                }
            }
            catch
            {
                return true;
            }

            return true;
        }

        private void TryNudgeEnemyToBattery(EnemyAI enemy)
        {
            if (enemy == null || enemy.agent == null) return;
            try
            {
                enemy.SetDestinationToPosition(_sabotageWorldPosition, checkForPath: false);
            }
            catch
            {
                // Enemy subclasses can veto movement during special states.
            }
        }

        private void TickLocalRestoreInteraction()
        {
            if (!_controller.RestoreObjectiveActive && !_controller.BatteryBroken && _controller.ShipPowerOnline) return;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (player == null || player.isPlayerDead)
                return;

            // The socket InteractTrigger owns the hold-to-dock interaction and
            // its own hover/hold tips. No HUD tip spam and no UnityEngine.Input
            // reads here — the legacy Input Manager is disabled in this game.
        }

        // Named ShowDockedRestoreVisual on purpose: Y4NGZUpgrades' legacy
        // ExteriorBatteryRepairPatch resolves "ShowDockedApparatusVisual" in
        // Prepare(); failing that lookup cleanly disables its broken prefix
        // (it reads UnityEngine.Input, which throws under the new Input System
        // and spams the FIELD MECHANIC hold-E tip).
        internal void ShowDockedRestoreVisual()
        {
            if (_apparatusVisual != null || _root == null) return;

            _apparatusVisual = new GameObject("DockedApparatusVisual");
            _apparatusVisual.transform.SetParent(_root.transform, false);
            // Sit exactly where the authored battery placement puts the battery.
            _apparatusVisual.transform.localPosition = _batteryBody != null
                ? _batteryBody.transform.localPosition
                : SocketLocalPosition + new Vector3(0f, 0f, 0.16f);
            if (_batteryBody != null)
                _apparatusVisual.transform.localRotation = _batteryBody.transform.localRotation;

            GameObject lungPrefab = FindLungPropPrefab();
            if (lungPrefab != null)
            {
                CopyMeshVisuals(lungPrefab, _apparatusVisual);
                FitApparatusVisualToBatteryBounds();
            }
            else
            {
                GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                fallback.name = "DockedApparatusFallback";
                DisableCollision(fallback);
                fallback.transform.SetParent(_apparatusVisual.transform, false);
                fallback.transform.localEulerAngles = new Vector3(90f, 0f, 0f);
                fallback.transform.localScale = new Vector3(0.18f, 0.46f, 0.18f);
                ApplyMaterial(fallback, new Color(0.78f, 0.95f, 0.82f, 1f));
            }

            // The apparatus occupies the slot: the battery model must stay
            // hidden until the next break/reset (Task B).
            if (_batteryBody != null)
                _batteryBody.SetActive(false);
        }

        // Scale the apparatus lookalike to the battery's height and align its
        // renderer center with the battery's, so the dock reads like the
        // authored battery placement (round 4: it spawned way oversized).
        private void FitApparatusVisualToBatteryBounds()
        {
            if (_apparatusVisual == null || _batteryBody == null)
                return;

            bool bodyWasActive = _batteryBody.activeSelf;
            if (!bodyWasActive)
                _batteryBody.SetActive(true);
            bool gotTarget = TryGetRendererBounds(_batteryBody.transform, out Bounds target);
            if (!bodyWasActive)
                _batteryBody.SetActive(false);

            if (!gotTarget || target.size.y < 0.01f)
                return;

            if (!TryGetRendererBounds(_apparatusVisual.transform, out Bounds current) || current.size.sqrMagnitude < 0.0001f)
                return;

            // The LungProp prefab is authored lying down (round 5: it stuck out
            // of the wall sideways). Stand its longest world axis upright so it
            // sits like the battery does.
            if (current.size.x >= current.size.y && current.size.x >= current.size.z)
                _apparatusVisual.transform.rotation =
                    Quaternion.FromToRotation(Vector3.right, Vector3.up) * _apparatusVisual.transform.rotation;
            else if (current.size.z >= current.size.y && current.size.z >= current.size.x)
                _apparatusVisual.transform.rotation =
                    Quaternion.FromToRotation(Vector3.forward, Vector3.up) * _apparatusVisual.transform.rotation;

            if (!TryGetRendererBounds(_apparatusVisual.transform, out Bounds upright) || upright.size.y < 0.01f)
                return;

            float factor = Mathf.Clamp(target.size.y / upright.size.y, 0.02f, 20f);
            _apparatusVisual.transform.localScale *= factor;

            if (TryGetRendererBounds(_apparatusVisual.transform, out Bounds scaled))
                _apparatusVisual.transform.position += target.center - scaled.center;
        }

        private static GameObject FindLungPropPrefab()
        {
            try
            {
                var items = StartOfRound.Instance?.allItemsList?.itemsList;
                if (items == null)
                    return null;

                for (int i = 0; i < items.Count; i++)
                {
                    Item item = items[i];
                    if (item == null || item.spawnPrefab == null)
                        continue;
                    if (item.spawnPrefab.GetComponent<LungProp>() != null)
                        return item.spawnPrefab;
                }
            }
            catch
            {
                // Fall through to the primitive lookalike.
            }

            return null;
        }

        // Copies only the renderer hierarchy off a prefab asset (never
        // instantiates it — GrabbableObject/NetworkObject Awake must not run
        // on a cosmetic clone).
        private static void CopyMeshVisuals(GameObject sourceRoot, GameObject destinationRoot)
        {
            MeshFilter[] filters = sourceRoot.GetComponentsInChildren<MeshFilter>(true);
            Transform source = sourceRoot.transform;
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                MeshRenderer renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
                if (filter == null || renderer == null || filter.sharedMesh == null)
                    continue;

                GameObject part = new GameObject(filter.name + "_Visual");
                part.transform.SetParent(destinationRoot.transform, false);
                Matrix4x4 relative = source.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                part.transform.localPosition = relative.MultiplyPoint3x4(Vector3.zero);
                part.transform.localRotation = relative.rotation;
                part.transform.localScale = relative.lossyScale;

                MeshFilter partFilter = part.AddComponent<MeshFilter>();
                partFilter.sharedMesh = filter.sharedMesh;
                MeshRenderer partRenderer = part.AddComponent<MeshRenderer>();
                partRenderer.sharedMaterials = renderer.sharedMaterials;
                partRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }

        private void BuildSocketInteract()
        {
            if (_root == null || _socketTrigger != null)
                return;

            GameObject triggerObject = new GameObject("BatterySocketInteract");
            triggerObject.transform.SetParent(_root.transform, false);
            // Cover the battery body itself: docking should happen exactly
            // where the battery is meant to sit, not off at the socket cube.
            triggerObject.transform.localPosition = new Vector3(0f, 0f, 0.32f);
            triggerObject.layer = FuelRuntimeUtility.InteractLayer;
            FuelRuntimeUtility.SetTagSafe(triggerObject, "InteractTrigger");

            BoxCollider collider = triggerObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = new Vector3(1.8f, 1.5f, 1.2f);
            _socketTriggerCollider = collider;

            _socketTrigger = triggerObject.AddComponent<InteractTrigger>();
            ConfigureHoldTrigger(_socketTrigger, ShipSystemsBootstrap.BatteryRestoreHoldSeconds.Value);
            _socketTrigger.onInteract.AddListener(OnSocketInteract);
            _socketTrigger.interactable = false;
            collider.enabled = false;
        }

        private void AttachDropRepairTrigger(GameObject drop)
        {
            if (drop == null)
                return;

            // The interact ray resolves hit.transform to the rigidbody root, so
            // the trigger stays a standalone rigidbody-free object. The drop is
            // statically placed now, so the trigger can sit at its rest pose
            // with a tight, player-aimed box (round 4: an inflated bounds sweep
            // produced a 4x6x4 m box that hover-tipped from everywhere).
            GameObject triggerObject = new GameObject("BrokenBatteryRepairInteract");
            triggerObject.transform.position = drop.transform.position;
            triggerObject.layer = FuelRuntimeUtility.InteractLayer;
            FuelRuntimeUtility.SetTagSafe(triggerObject, "InteractTrigger");

            BoxCollider trigger = triggerObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            float scale = Mathf.Clamp(_batteryBody != null ? _batteryBody.transform.lossyScale.y : 1f, 0.4f, 1.6f);
            trigger.size = new Vector3(1.5f, 1.0f, 1.5f) * scale;
            _dropRepairTriggerCollider = trigger;

            _dropRepairTrigger = triggerObject.AddComponent<InteractTrigger>();
            ConfigureHoldTrigger(_dropRepairTrigger, ShipSystemsBootstrap.TechnicianRepairHoldSeconds.Value);
            // [LMB] is the placeholder vanilla rewrites with the bound interact key.
            _dropRepairTrigger.hoverTip = "Repair battery : Hold [LMB]";
            _dropRepairTrigger.holdTip = "Repairing battery";
            _dropRepairTrigger.disabledHoverTip = "[ Requires Field Mechanic II ]";
            _dropRepairTrigger.onInteract.AddListener(OnDropRepairInteract);
            ShipSystemsBootstrap.Log.LogInfo(
                "[LGUShipSystems.Battery] Repair trigger spawned: " +
                $"layer={triggerObject.layer}, tag={triggerObject.tag}, size={trigger.size}, " +
                $"interactable={_dropRepairTrigger.interactable}, " +
                $"canRepair={TechnicianUpgradeBridge.CanRepairBatteries()}.");
        }

        private static void ConfigureHoldTrigger(InteractTrigger trigger, float holdSeconds)
        {
            trigger.interactable = true;
            trigger.oneHandedItemAllowed = true;
            trigger.twoHandedItemAllowed = true;
            trigger.holdInteraction = true;
            trigger.timeToHold = Mathf.Max(0.5f, holdSeconds);
            trigger.timeToHoldSpeedMultiplier = 1f;
            trigger.interactCooldown = true;
            trigger.cooldownTime = 0.5f;
            trigger.disableTriggerMesh = true;
            if (trigger.onInteract == null) trigger.onInteract = new InteractEvent();
            if (trigger.onInteractEarly == null) trigger.onInteractEarly = new InteractEvent();
            if (trigger.onInteractEarlyOtherClients == null) trigger.onInteractEarlyOtherClients = new InteractEvent();
            if (trigger.onStopInteract == null) trigger.onStopInteract = new InteractEvent();
            if (trigger.onCancelAnimation == null) trigger.onCancelAnimation = new InteractEvent();
            if (trigger.holdingInteractEvent == null) trigger.holdingInteractEvent = new InteractEventFloat();
            ApplyInteractionIcons(trigger);
        }

        private static void ApplyInteractionIcons(InteractTrigger trigger)
        {
            if (trigger == null || trigger.hoverIcon != null)
                return;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                                       ?? StartOfRound.Instance?.localPlayerController;
            if (player?.grabItemIcon == null)
                return;

            trigger.hoverIcon = player.grabItemIcon;
            trigger.disabledHoverIcon = player.grabItemIcon;
        }

        private void UpdateInteractTriggers()
        {
            UpdateSocketTriggerState();
            UpdateDropRepairTriggerState();
        }

        private void UpdateSocketTriggerState()
        {
            if (_socketTrigger == null)
                return;

            bool emergency = _controller.BatteryBroken || !_controller.ShipPowerOnline;
            if (!emergency)
            {
                _socketTrigger.interactable = false;
                if (_socketTriggerCollider != null)
                    _socketTriggerCollider.enabled = false;
                return;
            }

            if (_socketTriggerCollider != null)
                _socketTriggerCollider.enabled = true;

            _socketTrigger.timeToHold = Mathf.Max(0.5f, ShipSystemsBootstrap.BatteryRestoreHoldSeconds.Value);
            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            bool hasItem = player != null && !player.isPlayerDead && _controller.TryGetHeldRestoreItem(player, out _);
            string itemName = _controller.RestoreItemDisplayName;
            _socketTrigger.interactable = hasItem;
            if (!ReferenceEquals(itemName, _socketTriggerTipItemName))
            {
                _socketTriggerTipItemName = itemName;
                _socketTrigger.hoverTip = $"Dock {itemName} : Hold [LMB]";
                _socketTrigger.holdTip = $"Docking {itemName}";
                _socketTrigger.disabledHoverTip = $"[ Requires {itemName} in hand ]";
            }
        }

        private void UpdateDropRepairTriggerState()
        {
            if (_dropRepairTrigger == null)
                return;

            // ReXuvination "optimises" InteractTrigger collider layers; heal ours.
            if (_dropRepairTrigger.gameObject.layer != FuelRuntimeUtility.InteractLayer)
                _dropRepairTrigger.gameObject.layer = FuelRuntimeUtility.InteractLayer;

            // Only technicians get a repair window at all: without the upgrade
            // the collider is off, so no hover tip of any kind appears.
            bool canRepair = _controller.BatteryBroken && TechnicianUpgradeBridge.CanRepairBatteries()
                && (_brokenBatteryDrop == null || _brokenBatteryDrop.GetComponent<BatteryDropMotion>()?.HasLanded != false);
            if (_dropRepairTriggerCollider != null && _dropRepairTriggerCollider.enabled != canRepair)
                _dropRepairTriggerCollider.enabled = canRepair;

            _dropRepairTrigger.timeToHold = Mathf.Max(1f, ShipSystemsBootstrap.TechnicianRepairHoldSeconds.Value);
            _dropRepairTrigger.interactable = canRepair;
        }

        public void OnSocketInteract(PlayerControllerB player)
        {
            if (player == null || player != GameNetworkManager.Instance?.localPlayerController)
                return;

            _controller.RequestBatteryRestoreFromLocalPlayer();
        }

        public void OnDropRepairInteract(PlayerControllerB player)
        {
            if (player == null || player != GameNetworkManager.Instance?.localPlayerController)
                return;

            if (!TechnicianUpgradeBridge.CanRepairBatteries())
                return;

            _controller.RequestTechnicianBatteryRepairFromLocalPlayer();
        }

        internal bool TryGetBrokenDropPosition(out Vector3 position)
        {
            if (_brokenBatteryDrop != null)
            {
                position = _brokenBatteryDrop.transform.position;
                return true;
            }

            position = _socketWorldPosition;
            return _root != null;
        }

        internal void OnDropRepairedLocal()
        {
            _dropConvertedToItem = true;
            DestroyBrokenDrop();
        }

        internal void PlayLocalSaboteurSwing(Vector3 socketPosition)
        {
            RoundManager round = RoundManager.Instance;
            if (round == null || round.SpawnedEnemies == null)
                return;

            for (int i = 0; i < round.SpawnedEnemies.Count; i++)
            {
                if (!(round.SpawnedEnemies[i] is MaskedPlayerEnemy masked) || masked.isEnemyDead)
                    continue;
                if (Vector3.Distance(masked.transform.position, socketPosition) > 6.5f)
                    continue;

                try
                {
                    masked.transform.LookAt(new Vector3(socketPosition.x, masked.transform.position.y, socketPosition.z));
                    masked.transform.eulerAngles = new Vector3(0f, masked.transform.eulerAngles.y, 0f);
                    Animator animator = masked.creatureAnimator != null
                        ? masked.creatureAnimator
                        : masked.GetComponentInChildren<Animator>();
                    animator?.SetTrigger("HitEnemy");
                }
                catch
                {
                    // Masked mid-state-change can skip one swing pulse.
                }
            }
        }

        private void RefreshVisuals()
        {
            if (_batteryBody == null) return;

            bool broken = _controller.BatteryBroken;
            if (_lastVisualBroken.HasValue && _lastVisualBroken.Value == broken)
            {
                if (broken && _brokenBatteryDrop == null && !_dropConvertedToItem)
                    SpawnBrokenBatteryDrop();
                return;
            }

            _lastVisualBroken = broken;
            ApplyVisualState(broken);
        }

        private void ApplyVisualState(bool broken)
        {
            if (!broken)
                _dropConvertedToItem = false;

            if (broken)
            {
                if (_apparatusVisual != null)
                    UnityEngine.Object.Destroy(_apparatusVisual);
                _apparatusVisual = null;
                SpawnBrokenBatteryDrop();
                _batteryBody.SetActive(false);
                return;
            }

            DestroyBrokenDrop();
            // A docked apparatus owns the slot visual; the battery model stays
            // hidden until it is broken/reset back to a real battery.
            _batteryBody.SetActive(_apparatusVisual == null);
        }

        private void SpawnBrokenBatteryDrop()
        {
            if (_brokenBatteryDrop != null || _batteryBody == null || _dropConvertedToItem)
                return;

            _brokenBatteryDrop = UnityEngine.Object.Instantiate(
                _batteryBody,
                _batteryBody.transform.position,
                _batteryBody.transform.rotation);
            _brokenBatteryDrop.name = "BrokenShipBatteryDrop";
            _brokenBatteryDrop.transform.localScale = _batteryBody.transform.lossyScale;
            _brokenBatteryDrop.SetActive(true);
            BatteryAssetLoader.SetWorking(_brokenBatteryDrop, false);
            SetLayerRecursive(_brokenBatteryDrop, ResolveLayer("Props", 6));

            Vector3 start = _batteryBody.transform.position;
            Quaternion startRotation = _batteryBody.transform.rotation;
            Vector3 groundPoint = ResolveDropGroundPoint();
            Quaternion endRotation = Quaternion.AngleAxis(25f, Vector3.up) * Quaternion.Euler(90f,0f,0f);
            _brokenBatteryDrop.transform.SetPositionAndRotation(groundPoint, endRotation);
            float minY = groundPoint.y;
            bool hasBounds = false;
            foreach (Renderer renderer in _brokenBatteryDrop.GetComponentsInChildren<Renderer>())
            {
                minY = hasBounds ? Mathf.Min(minY, renderer.bounds.min.y) : renderer.bounds.min.y;
                hasBounds = true;
            }
            Vector3 end = groundPoint + Vector3.up * (groundPoint.y - minY + .015f);
            _brokenBatteryDrop.AddComponent<BatteryDropMotion>().Initialize(start, startRotation, end, endRotation);

            ParticleSystem vanillaSmoke = CreateVanillaSteamSmoke(_brokenBatteryDrop.transform);
            _brokenBatterySmoke = vanillaSmoke != null ? vanillaSmoke : CreateSmoke(_brokenBatteryDrop.transform);
            AttachDropRepairTrigger(_brokenBatteryDrop);
        }

        private Vector3 ResolveDropGroundPoint()
        {
            Vector3 planarForward = _root != null ? _root.transform.forward : Vector3.forward;
            planarForward.y = 0f;
            planarForward = planarForward.sqrMagnitude > 0.0001f ? planarForward.normalized : Vector3.forward;
            Vector3 origin = _socketWorldPosition + planarForward * 1.1f + Vector3.up * 0.6f;

            try
            {
                StartOfRound sor = StartOfRound.Instance;
                if (sor != null &&
                    Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 8f, sor.walkableSurfacesMask, QueryTriggerInteraction.Ignore))
                    return hit.point;
            }
            catch
            {
                // Fall back to the station's ground plane below.
            }

            return _root != null
                ? _root.transform.TransformPoint(new Vector3(SocketLocalPosition.x, SabotageStandLocalPosition.y, SocketLocalPosition.z)) + planarForward * 1.1f
                : origin + Vector3.down * 1.5f;
        }

        private void DestroyBrokenDrop()
        {
            if (_brokenBatteryDrop != null)
                UnityEngine.Object.Destroy(_brokenBatteryDrop);
            if (_dropRepairTrigger != null)
                UnityEngine.Object.Destroy(_dropRepairTrigger.gameObject);
            _brokenBatteryDrop = null;
            _brokenBatterySmoke = null;
            _dropRepairTrigger = null;
            _dropRepairTriggerCollider = null;
            _brokenLoopSource = null;
        }

        // Task I: prefer the vanilla steam-valve smoke for the broken drop;
        // clone the loaded asset, never mutate it. Null on maps without one.
        private static ParticleSystem CreateVanillaSteamSmoke(Transform parent)
        {
            try
            {
                SteamValveHazard[] valves = Resources.FindObjectsOfTypeAll<SteamValveHazard>();
                ParticleSystem prefab = null;
                for (int i = 0; i < valves.Length; i++)
                {
                    if (valves[i] != null && valves[i].valveSteamParticle != null)
                    {
                        prefab = valves[i].valveSteamParticle;
                        break;
                    }
                }

                if (prefab == null)
                    return null;

                ParticleSystem ps = UnityEngine.Object.Instantiate(prefab, parent, false);
                ps.gameObject.name = "VanillaBatterySmoke";
                ps.transform.localPosition = new Vector3(0f, 0.28f, 0f);
                ps.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                ps.transform.localScale = Vector3.one * 0.3f;
                ParticleSystem.MainModule main = ps.main;
                main.loop = true;
                AudioSource audio = ps.GetComponent<AudioSource>();
                if (audio != null)
                    audio.enabled = false;
                ps.Play();
                return ps;
            }
            catch
            {
                return null;
            }
        }

        private float _nextArcResolveAt, _nextArcAt, _arcStopAt;
        private bool _arcUnavailableLogged;
        private static ParticleSystem _nativeArcSource;
        private void TickWorkingArcs(bool working)
        {
            if (!working)
            {
                if (_workingSparkParticle != null)
                    _workingSparkParticle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                return;
            }
            if (_workingSparkParticle == null && Time.unscaledTime >= _nextArcResolveAt)
            { _nextArcResolveAt = Time.unscaledTime + 3f; EnsureWorkingSparkParticle(); }
            if (_workingSparkParticle == null) return;
            if (_arcStopAt > 0f && Time.unscaledTime >= _arcStopAt)
            { _workingSparkParticle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); _arcStopAt = 0f; }
            if (Time.unscaledTime < _nextArcAt) return;
            _nextArcAt = Time.unscaledTime + UnityEngine.Random.Range(2.8f, 5.2f);
            _arcStopAt = Time.unscaledTime + .18f;
            _workingSparkParticle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _workingSparkParticle.Play(true);
        }
        private void EnsureWorkingSparkParticle()
        {
            if (_workingSparkParticle != null || _batteryBody == null) return;
            if (_nativeArcSource == null)
            {
                foreach (var storm in Resources.FindObjectsOfTypeAll<StormyWeather>())
                    if (storm != null && storm.staticElectricityParticle != null)
                    { _nativeArcSource = storm.staticElectricityParticle; break; }
            }
            if (_nativeArcSource == null)
            {
                if (!_arcUnavailableLogged)
                { _arcUnavailableLogged = true; ShipSystemsBootstrap.Log?.LogInfo("[LGUShipSystems.Battery] Waiting for vanilla static electricity asset; will retry."); }
                return;
            }
            _workingSparkParticle = UnityEngine.Object.Instantiate(_nativeArcSource, _batteryBody.transform, false);
            _workingSparkParticle.gameObject.name = "BatteryWorkingSpark";
            _workingSparkParticle.gameObject.SetActive(true);
            var bounds = _batteryBody.GetComponent<Renderer>().localBounds;
            _workingSparkParticle.transform.localPosition = new Vector3(bounds.extents.x*.8f, 0f, -bounds.extents.z*.65f);
            _workingSparkParticle.transform.localRotation = Quaternion.identity;
            _workingSparkParticle.transform.localScale = Vector3.one;
            // #994: the vanilla asset is authored as a world-space effect whose shape module
            // emits from whichever MeshRenderer StormyWeather last pointed it at. A plain copy
            // inherits that reference (or none), so it never emits at the battery and its
            // world-sized quads land wherever that stale shape is, whitening the sky and fog
            // every arc. Re-anchor every emitter to a small local sphere, keep the simulation
            // and scaling local, pin the size, and drop any light the asset carries.
            int lightsRemoved = 0;
            foreach (var light in _workingSparkParticle.GetComponentsInChildren<Light>(true))
            { UnityEngine.Object.Destroy(light); lightsRemoved++; }
            string inheritedShape = "none";
            foreach (var particle in _workingSparkParticle.GetComponentsInChildren<ParticleSystem>(true))
            {
                particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var shape = particle.shape;
                if (inheritedShape == "none")
                    inheritedShape = shape.shapeType + (shape.meshRenderer != null ? ":" + shape.meshRenderer.name : ":null");
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.meshRenderer = null;
                shape.radius = .05f;
                shape.position = Vector3.zero;
                shape.scale = Vector3.one;
                var main = particle.main; main.loop = false; main.playOnAwake = false;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.scalingMode = ParticleSystemScalingMode.Local;
                main.startSize = new ParticleSystem.MinMaxCurve(.05f, .11f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0f, .12f);
                main.startLifetime = new ParticleSystem.MinMaxCurve(.08f,.16f);
                var emission = particle.emission; emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)2) });
                var lights = particle.lights; lights.enabled = false;
                var renderer = particle.GetComponent<ParticleSystemRenderer>();
                if (renderer != null) renderer.maxParticleSize = .02f;
            }
            foreach(var audio in _workingSparkParticle.GetComponentsInChildren<AudioSource>(true)) audio.enabled = false;
            _nextArcAt = Time.unscaledTime + 1f;
            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Battery] Vanilla static arcs ready, anchored to the original battery model (inherited shape {inheritedShape}, lights removed {lightsRemoved}).");
        }

        private static ParticleSystem CreateSmoke(Transform parent)
        {
            GameObject smokeObject = new GameObject("SubtleBatterySmoke");
            smokeObject.transform.SetParent(parent, false);
            smokeObject.transform.localPosition = new Vector3(0f, 0.28f, 0f);

            ParticleSystem ps = smokeObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.duration = 2.4f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.1f, 2.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.16f, 0.45f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.22f);
            main.startColor = new Color(0.22f, 0.22f, 0.20f, 0.34f);
            main.gravityModifier = -0.05f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 3.5f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 24f;
            shape.radius = 0.08f;

            ParticleSystemRenderer renderer = smokeObject.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.sortingFudge = 2f;
                Shader shader = ParticleShader();
                if (shader != null)
                {
                    Material material = new Material(shader);
                    Color color = new Color(0.22f, 0.22f, 0.20f, 0.34f);
                    material.color = color;
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                    if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
                    renderer.sharedMaterial = material;
                }
            }

            ps.Play();
            return ps;
        }

        private void BuildBatteryVisuals()
        {
            _batteryBody = BatteryAssetLoader.CreateBatteryVisual(_root.transform, "BatteryCoreVisual", 1.12f);
            DisableCollision(_batteryBody);
            _batteryBody.transform.localPosition = Vector3.zero;
            _batteryBody.transform.localRotation = Quaternion.identity;
            // Keep an interaction anchor, with no invented mounting hardware or floor marker.
            _socketObject = new GameObject("BatteryRestoreAnchor");
            _socketObject.transform.SetParent(_root.transform, false);
            _socketObject.transform.localPosition = SocketLocalPosition;
        }

        private void BuildScanNode()
        {
            if (_root == null) return;

            GameObject scanObject = new GameObject("BatteryScanNode");
            scanObject.transform.SetParent(_root.transform, false);
            scanObject.transform.localPosition = new Vector3(0f, 0.05f, 0.35f);

            int scanLayer = LayerMask.NameToLayer("ScanNode");
            if (scanLayer >= 0)
                scanObject.layer = scanLayer;
            try
            {
                scanObject.tag = "DoNotSet";
            }
            catch (UnityException)
            {
                // Some modded profiles do not expose the tag table consistently during scene load.
            }

            BoxCollider collider = scanObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = new Vector3(1.7f, 1.3f, 0.7f);

            ScanNodeProperties scanNode = scanObject.AddComponent<ScanNodeProperties>();
            scanNode.headerText = "SHIP BATTERY";
            scanNode.subText = _controller.BatteryBroken ? "apparatus required" : "exterior power cell";
            scanNode.maxRange = 24;
            scanNode.minRange = 1;
            scanNode.requiresLineOfSight = false;
            scanNode.nodeType = 0;
            scanNode.scrapValue = 0;
            scanNode.creatureScanID = -1;
        }

        private void UpdateWorldPositions()
        {
            if (_root == null) return;

            _socketWorldPosition = _root.transform.TransformPoint(SocketLocalPosition + new Vector3(0f, 0f, 0.14f));

            // Socket and floor are world-space inputs; the saved model may lie sideways.
            Vector3 rawStandPosition = _socketWorldPosition;
            bool shouldSampleSabotagePosition =
                Time.unscaledTime >= _nextSabotageNavMeshSampleAt ||
                Vector3.SqrMagnitude(rawStandPosition - _lastSabotageRawPosition) > SabotageNavMeshMoveEpsilonSqr;
            if (shouldSampleSabotagePosition)
            {
                _lastSabotageRawPosition = rawStandPosition;
                _hasSabotageNavMeshSample = BatterySabotagePosition.TryResolve(_root.transform, _socketWorldPosition, out _sabotageWorldPosition);
                if (!_hasSabotageNavMeshSample && !_loggedMissingSabotageStand && StartOfRound.Instance?.shipHasLanded == true)
                {
                    _loggedMissingSabotageStand = true;
                    ShipSystemsBootstrap.Log?.LogInfo("[LGUShipSystems.BatterySabotage] Waiting for a walkable floor within reach of the battery socket; no roof fallback will be used.");
                }
                if (_hasSabotageNavMeshSample) _loggedMissingSabotageStand = false;
                _nextSabotageNavMeshSampleAt = Time.unscaledTime + SabotageNavMeshSampleIntervalSeconds;
            }

            if (_standMarker != null)
                _standMarker.transform.position = _sabotageWorldPosition + Vector3.up * 0.04f;
        }

        private void LogPlacementOnce()
        {
            if (_loggedPlacement || _root == null)
                return;

            _loggedPlacement = true;
            StartOfRound sor = StartOfRound.Instance;
            Transform ship = sor != null ? sor.elevatorTransform : null;
            bool shipBoth = ship != null && HasActiveChild(ship, "ShipBoth_LGU");
            bool shipInsideBoth = ship != null && HasActiveChild(ship, "ShipInsideBoth");
            bool melanie = ship != null && HasActiveChild(ship, "MelanieShip_LGU");
            ShipLayoutType computed = SeamLayout();
            ShipSystemsBootstrap.Log.LogInfo(
                $"[LGUShipSystems] Exterior ship battery placed for {_currentPlacementLayout}: local={_root.transform.localPosition}, world={_root.transform.position}, scale={_root.transform.localScale}, socket={_socketWorldPosition}, sabotageStand={_sabotageWorldPosition}, renderers={_root.GetComponentsInChildren<Renderer>(includeInactive: true).Length}, anchor={(_usingPanelAnchor ? _currentPanelAnchorLabel : "fallback")}, computed={computed}, markers=ShipBoth:{shipBoth}/ShipInsideBoth:{shipInsideBoth}/Melanie:{melanie}.");
        }

        private void ApplyCurrentLayoutPlacement(bool force = false)
        {
            if (_root == null)
                return;
            if (_placementActive)
                return;

            if (!ShouldRefreshLayoutPlacement(force))
            {
                ApplyCachedLayoutPlacement();
                return;
            }

            ShipLayoutType activeLayout = ResolveActiveLayout();
            BatteryPlacement placement = GetPlacement(activeLayout);
            string layoutName = NormalizeLayoutName(activeLayout.ToString());
            if (TryGetSavedPlacement(layoutName, out BatteryPlacementRecord savedPlacement))
            {
                const string savedAnchorLabel = "saved";
                bool savedPlacementChanged = force ||
                                             !_hasCachedPlacement ||
                                             _currentPlacementLayout != placement.Layout ||
                                             _usingPanelAnchor ||
                                             !string.Equals(_currentPanelAnchorLabel, savedAnchorLabel, StringComparison.Ordinal);
                if (!force && !savedPlacementChanged)
                {
                    CacheCurrentPlacement(placement.Layout, false, savedAnchorLabel);
                    MarkPlacementRefreshComplete();
                    return;
                }

                _root.transform.localPosition = savedPlacement.localPosition;
                _root.transform.localRotation = Quaternion.Euler(savedPlacement.localEuler);
                _root.transform.localScale = NormalizePlacementScale(savedPlacement.localScale);
                _currentPlacementLayout = placement.Layout;
                _usingPanelAnchor = false;
                _currentPanelAnchorLabel = savedAnchorLabel;
                _loggedPlacement = false;
                CacheCurrentPlacement(placement.Layout, false, savedAnchorLabel);
                MarkPlacementRefreshComplete();
                UpdateWorldPositions();
                LogPlacementOnce();
                return;
            }

            bool placedOnPanel = false;
            string panelLabel = "uncalibrated layout preset";
            bool placementChanged = force ||
                                    !_hasCachedPlacement ||
                                    _currentPlacementLayout != placement.Layout ||
                                    _usingPanelAnchor != placedOnPanel ||
                                    !string.Equals(_currentPanelAnchorLabel, panelLabel, StringComparison.Ordinal);

            if (!placedOnPanel)
            {
                if (!force && !placementChanged)
                {
                    CacheCurrentPlacement(placement.Layout, false, panelLabel);
                    MarkPlacementRefreshComplete();
                    return;
                }

                Vector3 localPosition = ResolveExteriorLocalPosition(placement);
                _root.transform.localPosition = localPosition;
                _root.transform.localEulerAngles = placement.LocalEulerAngles;
                _root.transform.localScale = Vector3.one;
            }

            _currentPlacementLayout = placement.Layout;
            _usingPanelAnchor = placedOnPanel;
            _currentPanelAnchorLabel = panelLabel;
            if (placementChanged)
                _loggedPlacement = false;
            CacheCurrentPlacement(placement.Layout, placedOnPanel, panelLabel);
            MarkPlacementRefreshComplete();
            UpdateWorldPositions();
            LogPlacementOnce();
        }

        private bool ShouldRefreshLayoutPlacement(bool force)
        {
            if (force || !_hasCachedPlacement)
                return true;

            ShipLayoutType layoutHint = ResolveLayoutRefreshHint();
            if (layoutHint != _cachedPlacement.Layout)
                return true;

            return Time.unscaledTime >= _nextPlacementRefreshAt;
        }

        private ShipLayoutType ResolveLayoutRefreshHint()
        {
            // #666: the layout system lives in the command plugin now; the Core seam
            // answers with the applied layout when one is known, the computed one
            // otherwise — the same fallback chain the two direct reads used to make.
            return SeamLayout();
        }

        private static ShipLayoutType SeamLayout()
        {
            return ParseLayoutName(
                Y4NGZCore.Modules.ShipSystems.ShipUpgradeState.CurrentLayoutName);
        }

        private void CacheCurrentPlacement(ShipLayoutType layout, bool usingPanelAnchor, string panelAnchorLabel)
        {
            if (_root == null)
                return;

            _cachedPlacement = new CachedPlacement
            {
                Layout = layout,
                UsingPanelAnchor = usingPanelAnchor,
                PanelAnchorLabel = panelAnchorLabel,
                LocalPosition = _root.transform.localPosition,
                LocalRotation = _root.transform.localRotation,
                LocalScale = _root.transform.localScale,
            };
            _hasCachedPlacement = true;
        }

        private void ApplyCachedLayoutPlacement()
        {
            if (_root == null || !_hasCachedPlacement)
                return;

            _root.transform.localPosition = _cachedPlacement.LocalPosition;
            _root.transform.localRotation = _cachedPlacement.LocalRotation;
            _root.transform.localScale = _cachedPlacement.LocalScale;
            _currentPlacementLayout = _cachedPlacement.Layout;
            _usingPanelAnchor = _cachedPlacement.UsingPanelAnchor;
            _currentPanelAnchorLabel = _cachedPlacement.PanelAnchorLabel;
        }

        private void MarkPlacementRefreshComplete()
        {
            _nextPlacementRefreshAt = Time.unscaledTime + PlacementRefreshIntervalSeconds;
        }

        private bool TryApplyPanelAnchoredPlacement(ShipLayoutType layout, out string panelLabel)
        {
            panelLabel = null;

            StartOfRound sor = StartOfRound.Instance;
            Transform ship = sor != null ? sor.elevatorTransform : null;
            if (ship == null)
                return false;

            if (!TryFindExteriorPanel(ship, out Transform panel, out Bounds bounds, out panelLabel))
                return false;

            Vector3 outward = ResolvePanelOutward(ship, bounds.center);
            Vector3 up = ship.up;
            Vector3 right = Vector3.Cross(up, outward);
            if (right.sqrMagnitude < 0.001f)
                right = ship.right;
            right.Normalize();

            float outwardExtent = ProjectedBoundsExtent(bounds.extents, outward);
            float rightExtent = ProjectedBoundsExtent(bounds.extents, right);
            float upExtent = ProjectedBoundsExtent(bounds.extents, up);

            Vector3 faceCenter = bounds.center + outward * outwardExtent;
            float compartmentRightOffset = -Mathf.Clamp(rightExtent * 0.55f, 0.70f, 1.55f);
            float compartmentUpOffset = Mathf.Clamp(upExtent * 0.18f, 0.18f, 0.48f);
            Vector3 compartmentOffset =
                right * compartmentRightOffset +
                up * compartmentUpOffset;
            Vector3 mountPosition = faceCenter + compartmentOffset + outward * 0.10f;

            _root.transform.position = mountPosition;
            _root.transform.rotation = Quaternion.LookRotation(outward, up);
            _root.transform.localScale = Vector3.one;
            panelLabel = $"{panelLabel}/{layout}/compartment right={compartmentRightOffset:0.00} up={compartmentUpOffset:0.00}";
            return true;
        }

        private static bool TryFindExteriorPanel(Transform ship, out Transform panel, out Bounds bounds, out string label)
        {
            panel = null;
            bounds = default(Bounds);
            label = null;

            for (int i = 0; i < ExteriorPanelPrimaryNames.Length; i++)
            {
                string primaryName = ExteriorPanelPrimaryNames[i];
                Transform candidate = FindChildByName(ship, primaryName);
                if (candidate != null && TryGetRendererBounds(candidate, out bounds))
                {
                    panel = candidate;
                    label = primaryName;
                    return true;
                }
            }

            for (int i = 0; i < ExteriorPanelFallbackNames.Length; i++)
            {
                string fallbackName = ExteriorPanelFallbackNames[i];
                Transform candidate = FindChildByName(ship, fallbackName);
                if (candidate == null)
                    continue;

                Transform parent = candidate.parent != null && candidate.parent != ship
                    ? candidate.parent
                    : candidate;

                if (TryGetRendererBounds(parent, out bounds))
                {
                    panel = parent;
                    label = $"{fallbackName} parent";
                    return true;
                }

                if (TryGetRendererBounds(candidate, out bounds))
                {
                    panel = candidate;
                    label = fallbackName;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetRendererBounds(Transform root, out Bounds bounds)
        {
            bounds = default(Bounds);
            if (root == null)
                return false;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            bool initialized = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.gameObject.activeInHierarchy)
                    continue;

                if (!initialized)
                {
                    bounds = renderer.bounds;
                    initialized = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return initialized && bounds.size.sqrMagnitude > 0.0001f;
        }

        private static Vector3 ResolvePanelOutward(Transform ship, Vector3 panelCenter)
        {
            Vector3 fromShip = panelCenter - ship.position;
            fromShip = Vector3.ProjectOnPlane(fromShip, ship.up);
            if (fromShip.sqrMagnitude < 0.001f)
                fromShip = -ship.right;
            fromShip.Normalize();

            Vector3[] axes =
            {
                ship.right,
                -ship.right,
                ship.forward,
                -ship.forward
            };

            Vector3 best = axes[0];
            float bestDot = -1f;
            for (int i = 0; i < axes.Length; i++)
            {
                float dot = Vector3.Dot(fromShip, axes[i]);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = axes[i];
                }
            }

            return best.normalized;
        }

        private static float ProjectedBoundsExtent(Vector3 extents, Vector3 direction)
        {
            direction = new Vector3(Mathf.Abs(direction.x), Mathf.Abs(direction.y), Mathf.Abs(direction.z));
            return extents.x * direction.x + extents.y * direction.y + extents.z * direction.z;
        }

        private void TickDebugPlacementEditor()
        {
            if (!_placementActive || _root == null)
                return;

            if (UnityEngine.InputSystem.Keyboard.current == null)
                return;

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                SavePlacementFromCurrentTransform();
                return;
            }

            if (keyboard.escapeKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame || keyboard.deleteKey.wasPressedThisFrame)
            {
                CancelDebugPlacement();
                return;
            }

            // Middle-mouse-hold orbits the camera (Interior Edit pattern).
            if (TryReadPlacementOrbitDelta(out Vector2 orbitDelta))
            {
                _placementViewYaw += orbitDelta.x * PlacementViewOrbitSensitivity;
                _placementViewPitch = Mathf.Clamp(
                    _placementViewPitch - orbitDelta.y * PlacementViewOrbitSensitivity,
                    PlacementViewMinPitch,
                    PlacementViewMaxPitch);
                UpdatePlacementCameraPose();
            }

            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
            bool fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
            float moveSpeed = fast ? 8.0f : 1.8f;
            float verticalSpeed = fast ? 2.0f : 0.55f;
            float rotationSpeed = fast ? 120f : 42f;
            float scaleSpeed = fast ? 1.2f : 0.35f;
            Vector3 move = Vector3.zero;

            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move += GetPlacementEditForward();
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move -= GetPlacementEditForward();
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move += GetPlacementEditRight();
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move -= GetPlacementEditRight();

            float vertical = 0f;
            if (keyboard.eKey.isPressed || keyboard.pageUpKey.isPressed) vertical += verticalSpeed * dt;
            if (keyboard.qKey.isPressed || keyboard.pageDownKey.isPressed) vertical -= verticalSpeed * dt;

            Vector3 localEulerDelta = Vector3.zero;
            if (keyboard.fKey.isPressed) localEulerDelta.x -= rotationSpeed * dt;
            if (keyboard.gKey.isPressed) localEulerDelta.x += rotationSpeed * dt;
            if (keyboard.rKey.isPressed) localEulerDelta.y -= rotationSpeed * dt;
            if (keyboard.tKey.isPressed) localEulerDelta.y += rotationSpeed * dt;
            if (keyboard.zKey.isPressed) localEulerDelta.z -= rotationSpeed * dt;
            if (keyboard.xKey.isPressed) localEulerDelta.z += rotationSpeed * dt;

            float scaleDelta = 0f;
            if (keyboard.cKey.isPressed) scaleDelta -= scaleSpeed * dt;
            if (keyboard.vKey.isPressed) scaleDelta += scaleSpeed * dt;

            Vector3 delta = move.sqrMagnitude > 0.0001f ? move.normalized * moveSpeed * dt : Vector3.zero;
            if (Mathf.Abs(vertical) > 0.0001f)
                delta += Vector3.up * vertical;

            if (delta.sqrMagnitude > 0f || localEulerDelta.sqrMagnitude > 0.000001f || Mathf.Abs(scaleDelta) > 0.0001f)
                NudgePlacementPreview(delta, localEulerDelta, scaleDelta);
        }

        private void NudgePlacementPreview(Vector3 worldDelta, Vector3 localEulerDelta, float scaleDelta)
        {
            if (_root == null)
                return;

            Transform shipRoot = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            if (shipRoot != null && _root.transform.parent != shipRoot)
                _root.transform.SetParent(shipRoot, worldPositionStays: true);

            _root.transform.position += worldDelta;
            if (localEulerDelta.sqrMagnitude > 0.000001f)
            {
                Vector3 localEuler = _root.transform.localEulerAngles + localEulerDelta;
                _root.transform.localRotation = Quaternion.Euler(localEuler);
            }

            if (Mathf.Abs(scaleDelta) > 0.0001f)
            {
                float multiplier = Mathf.Max(0.01f, 1f + scaleDelta);
                _root.transform.localScale = NormalizePlacementScale(_root.transform.localScale * multiplier);
            }

            UpdateWorldPositions();
            UpdatePlacementCameraPose();
            SetDebugStatus($"editing {_placementLayout} {FormatPlacementSummary()}", 0.35f);
        }

        private Vector3 GetPlacementEditForward()
        {
            Transform basis = GetPlacementControlBasis();
            Vector3 forward = basis != null ? basis.forward : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            return forward.normalized;
        }

        private Vector3 GetPlacementEditRight()
        {
            Transform basis = GetPlacementControlBasis();
            Vector3 right = basis != null ? basis.right : Vector3.right;
            right.y = 0f;
            if (right.sqrMagnitude < 0.0001f)
                right = Vector3.right;
            return right.normalized;
        }

        private Transform GetPlacementControlBasis()
        {
            return _placementCamera != null
                ? _placementCamera.transform
                : _root != null ? _root.transform : null;
        }

        private void BeginPlacementInputCapture()
        {
            PlayerControllerB player = ResolvePlacementPlayer();
            if (player == null)
                return;

            if (_placementCapturedInputState)
                RestorePlacementInputCapture();

            _placementPlayer = player;
            _placementSavedLock = Cursor.lockState;
            _placementSavedCursorVisible = Cursor.visible;
            _placementSavedDisableLookInput = player.disableLookInput;
            _placementSavedDisableMoveInput = player.disableMoveInput;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            player.disableLookInput = true;
            player.disableMoveInput = true;
            _placementCapturedInputState = true;
        }

        private void RestorePlacementInputCapture()
        {
            RestorePlacementCameraTransform();

            if (_placementCapturedInputState)
            {
                if (_placementPlayer != null)
                {
                    _placementPlayer.disableLookInput = _placementSavedDisableLookInput;
                    _placementPlayer.disableMoveInput = _placementSavedDisableMoveInput;
                }

                Cursor.lockState = _placementSavedLock;
                Cursor.visible = _placementSavedCursorVisible;
            }

            _placementCapturedInputState = false;
            _placementPlayer = null;
            _placementCamera = null;
        }

        private void EnsurePlacementCamera()
        {
            Camera source = ResolvePlacementSourceCamera();
            if (source == null)
                return;

            if (!_placementCapturedCameraTransform)
            {
                _placementSavedCameraLocalPosition = source.transform.localPosition;
                _placementSavedCameraLocalRotation = source.transform.localRotation;
                _placementCapturedCameraTransform = true;
            }

            _placementCamera = source;
            _placementCamera.cullingMask |= 1 << 0;
            _placementCamera.enabled = true;
        }

        private Camera ResolvePlacementSourceCamera()
        {
            PlayerControllerB player = _placementPlayer ?? ResolvePlacementPlayer();
            if (player?.gameplayCamera != null)
                return player.gameplayCamera;

            Camera main = Camera.main;
            if (main != null)
                return main;

            Camera[] cameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (camera != null && camera.enabled)
                    return camera;
            }

            return null;
        }

        private static PlayerControllerB ResolvePlacementPlayer()
        {
            if (GameNetworkManager.Instance != null && GameNetworkManager.Instance.localPlayerController != null)
                return GameNetworkManager.Instance.localPlayerController;
            return StartOfRound.Instance != null ? StartOfRound.Instance.localPlayerController : null;
        }

        private void UpdatePlacementCameraPose()
        {
            if (_placementCamera == null || _root == null)
                return;

            if (!TryGetRendererBounds(_root.transform, out Bounds bounds))
                bounds = new Bounds(_root.transform.position + Vector3.up * 0.5f, new Vector3(1.8f, 1.4f, 1.8f));

            Vector3 focus = bounds.center;
            Vector3 front = _root.transform.forward;
            front.y = 0f;
            if (front.sqrMagnitude < 0.0001f)
                front = Vector3.forward;
            front.Normalize();

            float horizontalSpan = Mathf.Max(bounds.size.x, bounds.size.z);
            float distance = Mathf.Clamp(horizontalSpan * 1.4f + 2.2f, 3.4f, 10.0f);
            float heightOffset = Mathf.Clamp(bounds.size.y * 0.7f + 0.65f, 1.3f, 4.0f);
            Vector3 cameraPosition = focus + BuildPlacementOrbitOffset(
                front, distance, heightOffset, _placementViewYaw, _placementViewPitch);
            Vector3 lookPoint = focus + Vector3.up * Mathf.Clamp(bounds.size.y * 0.04f, 0.05f, 0.35f);
            Vector3 lookDirection = lookPoint - cameraPosition;
            if (lookDirection.sqrMagnitude < 0.0001f)
                lookDirection = -front;

            _placementCamera.transform.SetPositionAndRotation(
                cameraPosition,
                Quaternion.LookRotation(lookDirection.normalized, Vector3.up));
        }

        private static bool TryReadPlacementOrbitDelta(out Vector2 delta)
        {
            delta = Vector2.zero;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || !mouse.middleButton.isPressed)
                return false;

            delta = mouse.delta.ReadValue();
            return delta.sqrMagnitude > 0.0001f;
        }

        private static Vector3 BuildPlacementOrbitOffset(Vector3 planarForward, float distance, float heightOffset, float yaw, float pitch)
        {
            planarForward.y = 0f;
            if (planarForward.sqrMagnitude < 0.0001f)
                planarForward = Vector3.forward;
            planarForward.Normalize();

            Vector3 orbitForward = Quaternion.AngleAxis(yaw, Vector3.up) * planarForward;
            float pitchRadians = Mathf.Clamp(pitch, PlacementViewMinPitch, PlacementViewMaxPitch) * Mathf.Deg2Rad;
            float horizontalDistance = Mathf.Max(0.2f, distance * Mathf.Cos(pitchRadians));
            float verticalOffset = heightOffset + distance * Mathf.Sin(pitchRadians);
            return orbitForward * horizontalDistance + Vector3.up * verticalOffset;
        }

        private void RestorePlacementCameraTransform()
        {
            if (!_placementCapturedCameraTransform)
                return;

            if (_placementCamera != null)
            {
                Transform cameraTransform = _placementCamera.transform;
                cameraTransform.localPosition = _placementSavedCameraLocalPosition;
                cameraTransform.localRotation = _placementSavedCameraLocalRotation;
            }

            _placementCapturedCameraTransform = false;
            _placementSavedCameraLocalPosition = Vector3.zero;
            _placementSavedCameraLocalRotation = Quaternion.identity;
        }

        private void SavePlacementFromCurrentTransform()
        {
            if (_root == null)
                return;

            BatteryPlacementRecord record = new BatteryPlacementRecord
            {
                layout = NormalizeLayoutName(_placementLayout),
                localPosition = _root.transform.localPosition,
                localEuler = _root.transform.localEulerAngles,
                localScale = NormalizePlacementScale(_root.transform.localScale),
            };

            UpsertPlacement(record);
            SavePlacementProfile();
            _placementActive = false;
            _currentPlacementLayout = (ShipLayoutType)(-1);
            RestorePlacementInputCapture();
            ApplyCurrentLayoutPlacement(force: true);
            SetDebugStatus("battery placement saved", 2f);
            ShipSystemsBootstrap.Log.LogInfo($"[LGUShipSystems.BatteryPlacement] Saved layout={record.layout} pos={record.localPosition} euler={record.localEuler} scale={record.localScale}.");
        }

        private void CancelDebugPlacement()
        {
            if (_root != null)
            {
                _root.transform.localPosition = _placementStartLocalPosition;
                _root.transform.localRotation = _placementStartLocalRotation;
                _root.transform.localScale = _placementStartLocalScale;
                UpdateWorldPositions();
            }

            _placementActive = false;
            RestorePlacementInputCapture();
            SetDebugStatus("battery placement cancelled", 1.5f);
        }

        private void ApplyPlacementForLayout(string layout)
        {
            if (_root == null)
                return;

            Transform shipRoot = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            if (shipRoot != null && _root.transform.parent != shipRoot)
                _root.transform.SetParent(shipRoot, worldPositionStays: false);

            layout = NormalizeLayoutName(layout);
            BatteryPlacementRecord record;
            if (!TryGetSavedPlacement(layout, out record))
                record = GetDefaultPlacementRecord(layout);

            _root.transform.localPosition = record.localPosition;
            _root.transform.localRotation = Quaternion.Euler(record.localEuler);
            _root.transform.localScale = NormalizePlacementScale(record.localScale);
            _currentPlacementLayout = ParseLayoutName(layout);
            _usingPanelAnchor = false;
            _currentPanelAnchorLabel = TryGetSavedPlacement(layout, out _) ? "saved-preview" : "default-preview";
            _loggedPlacement = false;
            UpdateWorldPositions();
        }

        private ShipLayoutType ResolveActiveLayout()
        {
            StartOfRound sor = StartOfRound.Instance;
            Transform ship = sor != null ? sor.elevatorTransform : null;
            if (ship != null)
            {
                bool wider = HasActiveChild(ship, "ShipBoth_LGU") || HasActiveChild(ship, "ShipInsideBoth");
                bool twoStory = HasActiveChild(ship, "MelanieShip_LGU");
                ShipLayoutType computed = SeamLayout();
                if (twoStory && computed == ShipLayoutType.WiderAndTwoStory) return ShipLayoutType.WiderAndTwoStory;
                if (wider && twoStory) return ShipLayoutType.WiderAndTwoStory;
                if (wider) return ShipLayoutType.Wider;
                if (twoStory) return ShipLayoutType.TwoStory;
            }

            return SeamLayout();
        }

        private Vector3 ResolveExteriorLocalPosition(BatteryPlacement placement)
        {
            return placement.LocalPosition;
        }

        private static BatteryPlacementRecord GetDefaultPlacementRecord(string layout)
        {
            BatteryPlacement placement = GetPlacement(ParseLayoutName(layout));
            return new BatteryPlacementRecord
            {
                layout = NormalizeLayoutName(layout),
                localPosition = placement.LocalPosition,
                localEuler = placement.LocalEulerAngles,
                localScale = Vector3.one,
            };
        }

        private static ShipLayoutType ParseLayoutName(string layout)
        {
            string normalized = NormalizeLayoutName(layout);
            switch (normalized)
            {
                case "Wider":
                    return ShipLayoutType.Wider;
                case "TwoStory":
                    return ShipLayoutType.TwoStory;
                case "WiderAndTwoStory":
                    return ShipLayoutType.WiderAndTwoStory;
                default:
                    return ShipLayoutType.Vanilla;
            }
        }

        private static string NormalizeLayoutName(string layout)
        {
            if (string.IsNullOrWhiteSpace(layout))
                return "Vanilla";

            string compact = layout.Trim()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .Replace("+", string.Empty);

            bool wider = compact.IndexOf("Wider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         compact.IndexOf("Wide", StringComparison.OrdinalIgnoreCase) >= 0;
            bool twoStory = compact.IndexOf("TwoStory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            compact.IndexOf("2Story", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            compact.IndexOf("2Floor", StringComparison.OrdinalIgnoreCase) >= 0;
            if (wider && twoStory) return "WiderAndTwoStory";
            if (wider) return "Wider";
            if (twoStory) return "TwoStory";
            return "Vanilla";
        }

        private static void EnsurePlacementProfileLoaded()
        {
            if (_placementProfileLoaded)
                return;

            _placementProfileLoaded = true;
            _placementProfile = new BatteryPlacementProfile();

            // Package defaults are captured with DebugTools. Local authoring overrides win
            // per layout, so editing one ship variant never discards the others.
            string packaged = Path.Combine(Path.GetDirectoryName(typeof(ExternalBatteryStation).Assembly.Location), "Assets", "Battery", "placements.json");
            foreach (string path in new[] { packaged, PlacementProfilePath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var loaded = ReadPlacementProfile(File.ReadAllText(path));
                    if (loaded?.placements == null) continue;
                    foreach (var record in loaded.placements) if (record != null) UpsertPlacement(record);
                }
                catch (Exception ex)
                {
                    ShipSystemsBootstrap.Log.LogWarning("[LGUShipSystems.BatteryPlacement] Could not read " + Path.GetFileName(path) + ": " + ex.Message);
                }
            }
        }

        private static bool TryGetSavedPlacement(string layout, out BatteryPlacementRecord placement)
        {
            EnsurePlacementProfileLoaded();
            placement = null;
            if (_placementProfile?.placements == null)
                return false;

            layout = NormalizeLayoutName(layout);
            for (int i = 0; i < _placementProfile.placements.Count; i++)
            {
                BatteryPlacementRecord record = _placementProfile.placements[i];
                if (record == null)
                    continue;

                if (string.Equals(NormalizeLayoutName(record.layout), layout, StringComparison.OrdinalIgnoreCase))
                {
                    placement = record;
                    placement.layout = layout;
                    placement.localScale = NormalizePlacementScale(placement.localScale);
                    return true;
                }
            }

            return false;
        }

        private static void UpsertPlacement(BatteryPlacementRecord record)
        {
            EnsurePlacementProfileLoaded();
            if (_placementProfile.placements == null)
                _placementProfile.placements = new List<BatteryPlacementRecord>();

            record.layout = NormalizeLayoutName(record.layout);
            record.localScale = NormalizePlacementScale(record.localScale);
            for (int i = 0; i < _placementProfile.placements.Count; i++)
            {
                BatteryPlacementRecord existing = _placementProfile.placements[i];
                if (existing != null && string.Equals(NormalizeLayoutName(existing.layout), record.layout, StringComparison.OrdinalIgnoreCase))
                {
                    _placementProfile.placements[i] = record;
                    return;
                }
            }

            _placementProfile.placements.Add(record);
        }

        private static void SavePlacementProfile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PlacementProfilePath));
                File.WriteAllText(PlacementProfilePath, WritePlacementProfile(_placementProfile));
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log.LogWarning($"[LGUShipSystems.BatteryPlacement] Failed writing placement profile: {ex.Message}");
            }
        }

        /// <summary>
        /// #591 — explicit mapping in place of JsonUtility, whose field discovery
        /// depends on a preloader patcher that is not guaranteed to be running.
        /// Field names and order match the previous output.
        /// </summary>
        private static BatteryPlacementProfile ReadPlacementProfile(string json)
        {
            Core.Y4NGZJsonObject root = Core.Y4NGZJson.Parse(json);
            if (root == null)
                return null;

            BatteryPlacementProfile profile = new BatteryPlacementProfile();
            Core.Y4NGZJsonArray placements = root.GetArray("placements");
            if (placements == null)
                return profile;

            for (int i = 0; i < placements.Count; i++)
            {
                Core.Y4NGZJsonObject entry = placements.GetObject(i);
                if (entry == null)
                    continue;

                profile.placements.Add(new BatteryPlacementRecord
                {
                    layout = entry.GetString("layout"),
                    localPosition = entry.GetVector3("localPosition"),
                    localEuler = entry.GetVector3("localEuler"),
                    localScale = entry.GetVector3("localScale", Vector3.one),
                });
            }

            return profile;
        }

        private static string WritePlacementProfile(BatteryPlacementProfile profile)
        {
            Core.Y4NGZJsonObject root = new Core.Y4NGZJsonObject();
            Core.Y4NGZJsonArray placements = root.SetArray("placements");
            if (profile?.placements != null)
            {
                for (int i = 0; i < profile.placements.Count; i++)
                {
                    BatteryPlacementRecord record = profile.placements[i];
                    if (record == null)
                        continue;

                    Core.Y4NGZJsonObject entry = placements.AddObject();
                    entry.SetString("layout", record.layout);
                    entry.SetVector3("localPosition", record.localPosition);
                    entry.SetVector3("localEuler", record.localEuler);
                    entry.SetVector3("localScale", record.localScale);
                }
            }

            return Core.Y4NGZJson.Write(root);
        }

        private static string BuildPresetStatus()
        {
            string result = string.Empty;
            for (int i = 0; i < PlacementLayouts.Length; i++)
            {
                string layout = PlacementLayouts[i];
                bool saved = TryGetSavedPlacement(layout, out _);
                if (i > 0)
                    result += ",";
                result += layout + ":" + (saved ? "saved" : "default");
            }

            return result;
        }

        private void SetDebugStatus(string status, float seconds)
        {
            _debugStatus = status ?? "ready";
            _debugStatusVisibleUntil = Time.unscaledTime + Mathf.Max(0.1f, seconds);
        }

        private static string FormatVector(Vector3 value)
        {
            return $"{value.x:0.0},{value.y:0.0},{value.z:0.0}";
        }

        private static string FormatScale(Vector3 value)
        {
            return $"{value.x:0.00},{value.y:0.00},{value.z:0.00}";
        }

        private string FormatPlacementSummary()
        {
            if (_root == null)
                return string.Empty;

            return $"pos={FormatVector(_root.transform.localPosition)} rot={FormatVector(_root.transform.localEulerAngles)} scale={FormatScale(_root.transform.localScale)}";
        }

        private static Vector3 NormalizePlacementScale(Vector3 scale)
        {
            if (!IsValidScaleComponent(scale.x) || !IsValidScaleComponent(scale.y) || !IsValidScaleComponent(scale.z))
                return Vector3.one;

            return new Vector3(
                Mathf.Clamp(scale.x, PlacementMinScale, PlacementMaxScale),
                Mathf.Clamp(scale.y, PlacementMinScale, PlacementMaxScale),
                Mathf.Clamp(scale.z, PlacementMinScale, PlacementMaxScale));
        }

        private static bool IsValidScaleComponent(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0.001f;
        }

        private static string PlacementProfilePath =>
            Core.Y4NGZCompanyPaths.LocalDataFile("ship-battery-placements.local.json");

        private static bool TryGetShipBoundsMinLocalX(out float minX)
        {
            minX = 0f;

            StartOfRound sor = StartOfRound.Instance;
            Transform ship = sor != null ? sor.elevatorTransform : null;
            if (ship == null)
                return false;

            Collider bounds = sor.shipBounds;
            if (bounds == null)
            {
                Transform boundsTransform = ship.Find("ShipBoundsTrigger");
                if (boundsTransform != null)
                    bounds = boundsTransform.GetComponent<Collider>();
            }

            if (bounds == null)
                return false;

            Transform boundsTransformRef = bounds.transform;
            if (boundsTransformRef != null && IsDescendantOf(boundsTransformRef, ship))
            {
                Vector3 localCenter = ship.InverseTransformPoint(boundsTransformRef.position);
                float localWidth = Mathf.Abs(boundsTransformRef.localScale.x);
                minX = localCenter.x - localWidth * 0.5f;
                return true;
            }

            Vector3 localMin = ship.InverseTransformPoint(bounds.bounds.min);
            Vector3 localMax = ship.InverseTransformPoint(bounds.bounds.max);
            minX = Mathf.Min(localMin.x, localMax.x);
            return true;
        }

        private static bool HasActiveChild(Transform root, string name)
        {
            Transform child = FindChildByName(root, name);
            return child != null && child.gameObject.activeInHierarchy;
        }

        private static Transform FindChildByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
                return null;

            Transform exact = root.Find(name);
            if (exact != null)
                return exact;

            Transform[] all = root.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform child = all[i];
                if (child != null &&
                    string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }

            return null;
        }

        private static bool IsDescendantOf(Transform child, Transform parent)
        {
            Transform current = child;
            while (current != null)
            {
                if (current == parent)
                    return true;

                current = current.parent;
            }

            return false;
        }

        private static BatteryPlacement GetPlacement(ShipLayoutType layout)
        {
            switch (layout)
            {
                case ShipLayoutType.Wider:
                    return WiderPlacement;
                case ShipLayoutType.TwoStory:
                    return TwoStoryPlacement;
                case ShipLayoutType.WiderAndTwoStory:
                    return WiderAndTwoStoryPlacement;
                default:
                    return VanillaPlacement;
            }
        }

        // #716: the station rebuilt every primitive with GameObject.CreatePrimitive
        // on every landing, which allocated a collider only for DisableCollision to
        // destroy it again. The primitive meshes are built-in assets, so grab each
        // one once and build plain MeshFilter/MeshRenderer objects afterwards.
        private static readonly Dictionary<PrimitiveType, Mesh> _primitiveMeshes = new Dictionary<PrimitiveType, Mesh>();

        private static Mesh GetPrimitiveMesh(PrimitiveType type)
        {
            if (_primitiveMeshes.TryGetValue(type, out Mesh cached) && cached != null)
                return cached;

            GameObject temp = GameObject.CreatePrimitive(type);
            MeshFilter filter = temp.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            UnityEngine.Object.DestroyImmediate(temp);
            _primitiveMeshes[type] = mesh;
            return mesh;
        }

        private GameObject CreatePrimitiveVisual(PrimitiveType type, string name, Vector3 localPosition, Color color)
        {
            Mesh mesh = GetPrimitiveMesh(type);
            if (mesh == null)
            {
                // Built-in mesh unavailable (should not happen); fall back to the
                // original path so the station still renders something.
                GameObject legacy = GameObject.CreatePrimitive(type);
                legacy.name = name;
                DisableCollision(legacy);
                legacy.transform.SetParent(_root.transform, false);
                legacy.transform.localPosition = localPosition;
                ApplyMaterial(legacy, color);
                return legacy;
            }

            GameObject go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = localPosition;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = GetSharedMaterial(color);
            return go;
        }

        private GameObject CreateCube(string name, Vector3 localPosition, Vector3 localScale, Color color)
        {
            GameObject go = CreatePrimitiveVisual(PrimitiveType.Cube, name, localPosition, color);
            go.transform.localScale = localScale;
            return go;
        }

        private GameObject CreateCylinder(string name, Vector3 localPosition, Vector3 localEulerAngles, Vector3 localScale, Color color)
        {
            GameObject go = CreatePrimitiveVisual(PrimitiveType.Cylinder, name, localPosition, color);
            go.transform.localEulerAngles = localEulerAngles;
            go.transform.localScale = localScale;
            return go;
        }

        private GameObject CreateSphere(string name, Vector3 localPosition, Vector3 localScale, Color color)
        {
            GameObject go = CreatePrimitiveVisual(PrimitiveType.Sphere, name, localPosition, color);
            go.transform.localScale = localScale;
            return go;
        }

        private static string GetEnemyName(EnemyAI enemy)
        {
            if (enemy == null) return "enemy";
            if (enemy.enemyType != null && !string.IsNullOrWhiteSpace(enemy.enemyType.enemyName))
                return enemy.enemyType.enemyName;
            return enemy.name ?? "enemy";
        }

        // #716: cached shader lookups. Shader.Find walks the whole shader table
        // and the chains below used to run once per primitive, per landing.
        private static Shader _unlitColorShader;
        private static bool _unlitColorShaderResolved;
        private static Shader _particleShader;
        private static bool _particleShaderResolved;

        private static Shader UnlitColorShader()
        {
            if (!_unlitColorShaderResolved)
            {
                _unlitColorShaderResolved = true;
                _unlitColorShader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            }

            return _unlitColorShader;
        }

        private static Shader ParticleShader()
        {
            if (!_particleShaderResolved)
            {
                _particleShaderResolved = true;
                _particleShader = Shader.Find("Particles/Standard Unlit") ?? Shader.Find("HDRP/Unlit") ?? Shader.Find("Standard");
            }

            return _particleShader;
        }

        // #716: the station's static-coloured primitives all used renderer.material,
        // which cloned a material per primitive per landing. They never mutate their
        // colour, so one shared material per colour is enough. The two lights that
        // do change colour (status light, beacon) still take an instance through the
        // out-renderer overload below.
        private static readonly Dictionary<Color, Material> _sharedMaterials = new Dictionary<Color, Material>();

        private static Material GetSharedMaterial(Color color)
        {
            if (_sharedMaterials.TryGetValue(color, out Material cached) && cached != null)
                return cached;

            Shader shader = UnlitColorShader();
            Material material = shader != null ? new Material(shader) : null;
            if (material == null)
                return null;

            material.name = "LGUShipSystems_BatteryShared";
            material.hideFlags = HideFlags.HideAndDontSave;
            ApplyMaterialColor(material, color);
            _sharedMaterials[color] = material;
            return material;
        }

        private static Material ApplyMaterial(GameObject go, Color color)
        {
            if (go == null) return null;
            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer == null) return null;

            Material shared = GetSharedMaterial(color);
            if (shared == null)
                return null;

            renderer.sharedMaterial = shared;
            return shared;
        }

        // Instancing overload: the returned material is mutated later, so it must
        // not be the shared one.
        private static Material ApplyMaterial(GameObject go, Color color, out Renderer renderer)
        {
            renderer = null;
            if (go == null) return null;
            renderer = go.GetComponent<Renderer>();
            if (renderer == null) return null;

            Material material = renderer.material;
            if (material != null)
            {
                // The clone inherits the shared material's hide flags; clear them
                // so it is released with its renderer instead of leaking per landing.
                material.hideFlags = HideFlags.None;
                ApplyMaterialColor(material, color);
                return material;
            }

            Shader shader = UnlitColorShader();
            material = shader != null ? new Material(shader) : null;
            if (material == null)
                return null;

            ApplyMaterialColor(material, color);
            renderer.material = material;
            return material;
        }

        private static void ApplyMaterialColor(Material material, Color color)
        {
            if (material == null) return;
            material.color = color;
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", color * 0.28f);
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", color * 0.28f);
            material.EnableKeyword("_EMISSION");
        }

        private static void DisableCollision(GameObject go)
        {
            if (go == null) return;
            Collider[] colliders = go.GetComponentsInChildren<Collider>(includeInactive: true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                    UnityEngine.Object.Destroy(colliders[i]);
            }
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            if (go == null)
                return;

            Transform[] transforms = go.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                    transforms[i].gameObject.layer = layer;
            }
        }

        private static int ResolveLayer(string name, int fallback)
        {
            int layer = LayerMask.NameToLayer(name);
            return layer >= 0 ? layer : fallback;
        }

        [Serializable]
        private sealed class BatteryPlacementProfile
        {
            public List<BatteryPlacementRecord> placements = new List<BatteryPlacementRecord>();
        }

        [Serializable]
        private sealed class BatteryPlacementRecord
        {
            public string layout;
            public Vector3 localPosition;
            public Vector3 localEuler;
            public Vector3 localScale;
        }
    }

    internal static class NetworkUtils
    {
        internal static bool IsServer()
        {
            return Unity.Netcode.NetworkManager.Singleton != null &&
                   Unity.Netcode.NetworkManager.Singleton.IsServer;
        }
    }
}
