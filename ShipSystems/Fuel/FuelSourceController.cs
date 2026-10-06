using System;
using System.Collections;
using System.IO;
using BepInEx;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal sealed class FuelSourceController : NetworkBehaviour
    {
        private const float DropScanIntervalSeconds = 0.25f;
        private const float HookupRadius = 1.35f;
        private const float TerrainPlatformTopLocalY = 0f;
        private const float TerrainPlatformMaxHeight = 2.75f;
        private const string OilRigLoopFileName = "OilRigLoop.ogg";
        private const float OilRigLoopVolume = 0.55f;
        private const float OilRigLoopMinDistance = 5f;
        private const float OilRigLoopMaxDistance = 40f;
        // Release the detached pump 2.0 m back from the snap point: the old -0.9 m
        // dropped it INSIDE the PumpHookupTrigger box (1.1 half-extent) so the interact
        // raycast hit that trigger first and swallowed the grab prompt (first-hit-wins).
        // 2.0 m clears both the trigger box AND HookupRadius (1.35), so the freed pump
        // is immediately grabbable and cannot insta-reattach.
        private const float PumpDetachLocalZOffset = -2.0f;
        private const float PumpDetachRayStartHeight = 2f;
        private const float PumpDetachRayDistance = 8f;

        // Post-spawn ground reconciliation ("the platform is the surface"): after the
        // server spawns the rig, probe straight down from the four base-footprint
        // corners at the rig-local Y=0 support plane. If every corner floats over an
        // air gap, lower the root by the smallest gap so the base rests on the real
        // terrain even when placement rays hit an elevated collider. Buried corners
        // are left to the terrain-platform path; a missed ray means never move.
        private const float GroundReconcileCornerInset = 0.3f;
        private const float GroundReconcileRayStartHeight = 0.5f;
        private const float GroundReconcileRayDistance = 30f;
        private const float GroundReconcileMinAirGap = 0.15f;
        private const float GroundReconcileBuriedEpsilon = 0.02f;
        private const float GroundReconcileResidualPlatformSkirt = 0.35f;
        private const float GroundReconcileResidualThreshold = 0.05f;

        // The oil stream hangs from the faucet nozzle mouth (rig-local) straight
        // down into the attached pump can's filler cap. Visible only while the
        // replicated source state reports active filling.
        private const int OilStreamFillingState = 1;
        private static readonly Vector3 OilStreamMouthLocalPosition = new Vector3(0.4218f, 0.5252f, -3.3634f);

        // The hardcoded rig-root-local constants above/below are only valid when the
        // instantiated visual sits at identity inside the rig root, which it does not.
        // Anchor everything to the authored PumpSnapPoint baked into the visual: these
        // deltas are expressed in the authored visual's local axes relative to that
        // snap point (visual-local). With the pump's -90X upright correction + bounds
        // re-centering, the filler cap center sits at item-local (0.1124, 0.2056, 0.0669)
        // (yaw 0), so the can-root snap = authored + (0.3074, 0, -0.0633);
        // faucet-nozzle stream mouth = authored + (0.4098, 0.5252, 0.0036).
        private const string RefineryVisualName = "OilRefinery_Pumpjack_Visual";
        private const string AuthoredSnapPointName = "PumpSnapPoint";
        private const string PumpHookupTriggerName = "PumpHookupTrigger";
        private static readonly Vector3 AuthoredSnapToPumpSnapVisualOffset = new Vector3(0.3074f, 0f, -0.0633f);
        private static readonly Vector3 AuthoredSnapToStreamMouthVisualOffset = new Vector3(0.4098f, 0.5252f, 0.0036f);
        private const float OilStreamColumnRadiusScale = 0.026f;
        private const float OilStreamColumnHeightScale = 0.045f;
        private const float OilStreamSplashLocalY = -0.075f;
        private const float OilStreamWobbleAmplitude = 0.04f;
        private const float OilStreamWobbleFrequency = 6f;

        private static AudioClip _oilRigLoopClip;
        private static bool _oilRigLoopLoadStarted;
        private static bool _oilRigLoopLoadFailed;
        private static bool _loggedNestedPumpSnapPoint;
        private static bool _loggedAlignmentDiagnostic;

        private readonly NetworkVariable<int> _remainingFills = new NetworkVariable<int>(0);
        private readonly NetworkVariable<int> _sourceState = new NetworkVariable<int>(0);
        private readonly NetworkVariable<float> _attachedFill01 = new NetworkVariable<float>(0f);
        private readonly NetworkVariable<float> _terrainPlatformHeight = new NetworkVariable<float>(0f);
        private readonly Collider[] _dropZoneHits = new Collider[24];

        private FuelPumpItem _attachedPump;
        private AudioSource _loopAudio;
        private InteractTrigger _trigger;
        private ScanNodeProperties _scan;
        private Transform _snapPoint;
        private Transform _terrainPlatform;
        private Transform _refineryVisual;
        private Transform _authoredSnapPoint;
        private bool _anchoredToAuthoredSnap;
        private bool _groundReconcileStarted;
        private float _nextDropScanAt;

        private GameObject _oilStreamEffect;
        private Transform _oilStreamColumn;
        private ParticleSystem _oilStreamSplash;
        private Vector3 _oilStreamColumnBaseScale;
        private bool _oilStreamVisible;

        private void Awake()
        {
            _trigger = GetComponentInChildren<InteractTrigger>(true);
            _scan = GetComponentInChildren<ScanNodeProperties>(true);
            _snapPoint = ResolvePumpSnapPoint();
            AlignAnchorsToAuthoredSnapPoint();
            LogAlignmentDiagnostic();
            _terrainPlatform = FindChild("RefineryTerrainPlatform");
            ConfigureOilRigLoopAudio();
            EnsureOilRigLoopLoading();
            ConfigureTrigger();
            ApplyTerrainPlatformHeight(_terrainPlatformHeight.Value);
            UpdateText();
            TryStartOilRigLoopAudio();
        }

        public override void OnNetworkSpawn()
        {
            _remainingFills.OnValueChanged += OnRemainingChanged;
            _sourceState.OnValueChanged += OnSourceStateChanged;
            _attachedFill01.OnValueChanged += OnAttachedFillChanged;
            _terrainPlatformHeight.OnValueChanged += OnTerrainPlatformHeightChanged;
            if (IsServer && _remainingFills.Value <= 0)
                _remainingFills.Value = ShipFuelConfig.SourceFillCount;
            if (IsServer && !_groundReconcileStarted)
            {
                _groundReconcileStarted = true;
                StartCoroutine(GroundReconcileRoutine());
            }
            ApplyTerrainPlatformHeight(_terrainPlatformHeight.Value);
            UpdateText();
            TryStartOilRigLoopAudio();
        }

        public override void OnNetworkDespawn()
        {
            _remainingFills.OnValueChanged -= OnRemainingChanged;
            _sourceState.OnValueChanged -= OnSourceStateChanged;
            _attachedFill01.OnValueChanged -= OnAttachedFillChanged;
            _terrainPlatformHeight.OnValueChanged -= OnTerrainPlatformHeightChanged;
        }

        private void Update()
        {
            EnsureOilRigLoopLoading();
            TryStartOilRigLoopAudio();

            if (IsServer)
            {
                if (_attachedPump != null)
                {
                    FuelPumpItem attached = _attachedPump;
                    attached.MaintainLockedPose(_snapPoint);
                    attached.ServerTickFill(Time.deltaTime, _snapPoint);
                    if (_attachedPump == attached)
                        _attachedFill01.Value = attached.FillProgress01;
                }
                else
                {
                    if (_attachedFill01.Value != 0f)
                        _attachedFill01.Value = 0f;

                    if (Time.time >= _nextDropScanAt)
                    {
                        _nextDropScanAt = Time.time + DropScanIntervalSeconds;
                        TryAttachDroppedPump();
                    }
                }
            }

            UpdateOilStreamEffect();
            UpdateText();
        }

        // Runs on every client. The stream shows exactly while the replicated
        // source state reports active filling (1); it vanishes when the pump
        // stalls (2), detaches, or completes (0) because those all leave state 1.
        private void UpdateOilStreamEffect()
        {
            bool filling = _sourceState.Value == OilStreamFillingState;
            if (filling)
                EnsureOilStreamEffect();
            if (_oilStreamEffect == null)
                return;

            if (filling != _oilStreamVisible)
                SetOilStreamVisible(filling);

            if (!filling || _oilStreamColumn == null)
                return;

            // Subtle glossy wobble; sampled from the captured base scale so it
            // never accumulates drift across frames.
            float wobbleX = 1f + (Mathf.PerlinNoise(Time.time * OilStreamWobbleFrequency, 0f) - 0.5f) * 2f * OilStreamWobbleAmplitude;
            float wobbleZ = 1f + (Mathf.PerlinNoise(0f, Time.time * OilStreamWobbleFrequency) - 0.5f) * 2f * OilStreamWobbleAmplitude;
            _oilStreamColumn.localScale = new Vector3(
                _oilStreamColumnBaseScale.x * wobbleX,
                _oilStreamColumnBaseScale.y,
                _oilStreamColumnBaseScale.z * wobbleZ);
        }

        private void EnsureOilStreamEffect()
        {
            if (_oilStreamEffect != null)
                return;

            GameObject root = new GameObject("OilStreamEffect");
            root.transform.SetParent(transform, false);
            if (_authoredSnapPoint != null && _refineryVisual != null)
            {
                // Authored-anchor derivation: place the stream mouth at the faucet
                // nozzle relative to the baked snap point instead of the rig-root
                // constant, which is only valid for the primitive fallback visual.
                root.transform.position = _authoredSnapPoint.position + _refineryVisual.TransformVector(AuthoredSnapToStreamMouthVisualOffset);
                root.transform.rotation = transform.rotation;
            }
            else
            {
                root.transform.localPosition = OilStreamMouthLocalPosition;
                root.transform.localRotation = Quaternion.identity;
            }
            root.transform.localScale = Vector3.one;

            // Thin glossy near-black column: top sits at the mouth (root origin),
            // bottom reaches ~9 cm down into the filler cap. Primitive cylinders
            // are 2 units tall / radius 0.5 at unit scale and ship with a
            // collider that would eat the interact raycast, so drop it.
            GameObject column = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            column.name = "OilStreamColumn";
            Collider columnCollider = column.GetComponent<Collider>();
            if (columnCollider != null)
                Destroy(columnCollider);
            column.transform.SetParent(root.transform, false);
            column.transform.localPosition = new Vector3(0f, -OilStreamColumnHeightScale, 0f);
            column.transform.localRotation = Quaternion.identity;
            Vector3 columnScale = new Vector3(OilStreamColumnRadiusScale, OilStreamColumnHeightScale, OilStreamColumnRadiusScale);
            column.transform.localScale = columnScale;
            _oilStreamColumnBaseScale = columnScale;
            MeshRenderer columnRenderer = column.GetComponent<MeshRenderer>();
            if (columnRenderer != null)
                columnRenderer.sharedMaterial = CreateOilStreamMaterial(
                    "LGUShipSystems_OilStreamColumn",
                    new Color(0.015f, 0.012f, 0.010f, 1f),
                    0.1f,
                    0.85f);
            _oilStreamColumn = column.transform;

            _oilStreamSplash = CreateOilStreamSplash(root.transform);

            _oilStreamEffect = root;
            SetOilStreamVisible(false);
        }

        private ParticleSystem CreateOilStreamSplash(Transform parent)
        {
            GameObject splashObject = new GameObject("OilStreamSplash");
            splashObject.transform.SetParent(parent, false);
            splashObject.transform.localPosition = new Vector3(0f, OilStreamSplashLocalY, 0f);
            splashObject.transform.localRotation = Quaternion.identity;
            splashObject.transform.localScale = Vector3.one;

            ParticleSystem particles = splashObject.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = particles.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.35f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.006f, 0.018f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.45f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.03f, 0.025f, 0.02f, 1f),
                new Color(0.05f, 0.03f, 0.02f, 1f));
            main.gravityModifier = 1f;
            main.maxParticles = 80;
            main.playOnAwake = false;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.rateOverTime = 22f;

            ParticleSystem.ShapeModule shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 18f;
            shape.radius = 0.01f;
            shape.rotation = new Vector3(90f, 0f, 0f);

            ParticleSystemRenderer renderer = splashObject.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.sharedMaterial = CreateOilStreamSplashMaterial();
            }

            return particles;
        }

        private void SetOilStreamVisible(bool visible)
        {
            _oilStreamVisible = visible;
            if (_oilStreamEffect == null)
                return;

            if (_oilStreamEffect.activeSelf != visible)
                _oilStreamEffect.SetActive(visible);

            if (_oilStreamSplash == null)
                return;

            if (visible && !_oilStreamSplash.isPlaying)
                _oilStreamSplash.Play(true);
            else if (!visible && _oilStreamSplash.isPlaying)
                _oilStreamSplash.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        // Local copy of the FuelSourceSpawner.CreateMaterial fallback chain; kept
        // in-module to avoid a cross-file dependency. LC exposure blows out mid
        // grays, so the base color stays near-black for a wet-oil read.
        private static Material CreateOilStreamMaterial(string name, Color color, float metallic, float smoothness)
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

        private static Material CreateOilStreamSplashMaterial()
        {
            Shader shader = Shader.Find("HDRP/Unlit")
                ?? Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Unlit/Color");
            Color splashColor = new Color(0.02f, 0.018f, 0.015f, 1f);
            Material material = new Material(shader) { name = "LGUShipSystems_OilStreamSplash" };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", splashColor);
            if (material.HasProperty("_Color")) material.SetColor("_Color", splashColor);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", splashColor);
            return material;
        }

        internal void Initialize(int remainingFills)
        {
            if (IsServer)
                _remainingFills.Value = Mathf.Max(0, remainingFills);
            UpdateText();
        }

        internal void ConfigureTerrainPlatform(float platformHeight)
        {
            float clampedHeight = Mathf.Clamp(platformHeight, 0f, TerrainPlatformMaxHeight);
            if (IsServer)
                _terrainPlatformHeight.Value = clampedHeight;
            ApplyTerrainPlatformHeight(clampedHeight);
        }

        internal void NotifyPumpFilled(FuelPumpItem pump)
        {
            if (!IsServer || pump == null || pump != _attachedPump)
                return;

            _attachedPump = null;
            _remainingFills.Value = Mathf.Max(0, _remainingFills.Value - 1);
            _sourceState.Value = 0;
            _attachedFill01.Value = 0f;
            UpdateText();
        }

        private void ConfigureTrigger()
        {
            if (_trigger == null)
                return;

            _trigger.interactable = true;
            _trigger.oneHandedItemAllowed = true;
            _trigger.twoHandedItemAllowed = true;
            _trigger.holdInteraction = false;
            _trigger.interactCooldown = true;
            _trigger.cooldownTime = 0.5f;
            _trigger.hoverTip = "Drop fuel pump here";
            _trigger.disabledHoverTip = "[ Oil refinery depleted ]";
            _trigger.disableTriggerMesh = true;
            if (_trigger.onInteract == null)
                _trigger.onInteract = new InteractEvent();
            if (_trigger.onInteractEarly == null)
                _trigger.onInteractEarly = new InteractEvent();
            if (_trigger.onInteractEarlyOtherClients == null)
                _trigger.onInteractEarlyOtherClients = new InteractEvent();
            if (_trigger.onStopInteract == null)
                _trigger.onStopInteract = new InteractEvent();
            if (_trigger.onCancelAnimation == null)
                _trigger.onCancelAnimation = new InteractEvent();
            if (_trigger.holdingInteractEvent == null)
                _trigger.holdingInteractEvent = new InteractEventFloat();
            _trigger.onInteract.RemoveListener(OnInteract);
            _trigger.onInteract.AddListener(OnInteract);
            ApplyInteractionIcons(_trigger);
        }

        private void OnInteract(PlayerControllerB player)
        {
            if (IsServer)
            {
                TryInteractServer(player);
                return;
            }

            RequestInteractServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestInteractServerRpc(ServerRpcParams rpcParams = default)
        {
            TryInteractServer(FindPlayerByClientId(rpcParams.Receive.SenderClientId));
        }

        private void TryInteractServer(PlayerControllerB player)
        {
            if (player == null)
                return;

            if (_attachedPump != null)
            {
                if (_attachedPump.IsStalled)
                {
                    _attachedPump.RestartStalled();
                    _sourceState.Value = 1;
                    ShowTipClientRpc("PUMP RESTARTED", "Fuel extraction resumed.", false);
                }
                else
                {
                    DetachAttachedPumpServer();
                }
                return;
            }

            if (_remainingFills.Value <= 0)
            {
                ShowTipClientRpc("OIL REFINERY DEPLETED", "This refinery has no more extractable fuel.", true);
                return;
            }

            ShowTipClientRpc("DROP PUMP", "Drop an empty fuel pump under the refinery hookup.", false);
        }

        private void TryAttachDroppedPump()
        {
            if (_attachedPump != null || _remainingFills.Value <= 0 || _snapPoint == null)
                return;

            int hitCount = Physics.OverlapSphereNonAlloc(
                _snapPoint.position,
                HookupRadius,
                _dropZoneHits,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = _dropZoneHits[i];
                if (hit == null)
                    continue;

                FuelPumpItem pump = hit.GetComponentInParent<FuelPumpItem>();
                if (pump == null || !pump.CanAttachToRefinery)
                    continue;

                if (pump.TryBeginFilling(this, _snapPoint))
                {
                    _attachedPump = pump;
                    _sourceState.Value = 1;
                    _attachedFill01.Value = pump.FillProgress01;
                    ShowTipClientRpc("PUMP ATTACHED", "Fuel extraction started.", false);
                    Vector3 snapWorld = _snapPoint != null ? _snapPoint.position : transform.position;
                    ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Fuel] PumpAttach: pumpPos={pump.transform.position} pumpRotEuler={pump.transform.rotation.eulerAngles} snapPos={snapWorld} snapRotEuler={(_snapPoint != null ? _snapPoint.rotation.eulerAngles : transform.rotation.eulerAngles)}.");
                }
                return;
            }
        }

        private void DetachAttachedPumpServer()
        {
            FuelPumpItem pump = _attachedPump;
            if (pump == null)
                return;

            Vector3 releasePosition = ResolveDetachedPumpPosition(pump);
            Quaternion releaseRotation = _snapPoint != null ? _snapPoint.rotation : transform.rotation;
            _attachedPump = null;
            _sourceState.Value = 0;
            _attachedFill01.Value = 0f;
            pump.DetachFromRefinery(releasePosition, releaseRotation);
            ShowTipClientRpc("PUMP DETACHED", "Fuel pump released beside the refinery.", false);
            UpdateText();
        }

        private Vector3 ResolveDetachedPumpPosition(FuelPumpItem pump)
        {
            Vector3 snapPosition = _snapPoint != null ? _snapPoint.position : transform.position;
            Vector3 releaseFloorPoint = snapPosition + transform.TransformDirection(new Vector3(0f, 0f, PumpDetachLocalZOffset));
            Vector3 rayStart = releaseFloorPoint + Vector3.up * PumpDetachRayStartHeight;
            int mask = GetVanillaGroundMask();
            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, PumpDetachRayDistance, mask, QueryTriggerInteraction.Ignore))
                releaseFloorPoint = hit.point;

            return pump != null ? pump.GetGroundedPositionForFloor(releaseFloorPoint) : releaseFloorPoint;
        }

        private static int GetVanillaGroundMask()
        {
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : Physics.DefaultRaycastLayers;
            return mask != 0 ? mask : Physics.DefaultRaycastLayers;
        }

        // One-frame delay so the rig's own colliders and the level physics have
        // settled before the corner probes run.
        private IEnumerator GroundReconcileRoutine()
        {
            yield return null;
            ReconcileGroundContact();
        }

        // Server-only one-shot: probes the four base-footprint corners on the
        // rig-local Y=0 support plane and lowers the root by the smallest all-corner
        // air gap. NetworkObject.SynchronizeTransform replicates the move. The rig's
        // own colliders live on the Room layer and would self-hit, so hits inside the
        // rig hierarchy are filtered out. Always logs exactly one GroundReconcile line.
        private void ReconcileGroundContact()
        {
            float halfWidth = FuelSourceSpawner.TerrainPlatformWidth * 0.5f - GroundReconcileCornerInset;
            float halfDepth = FuelSourceSpawner.TerrainPlatformDepth * 0.5f - GroundReconcileCornerInset;
            Vector3[] cornerOffsets =
            {
                new Vector3(halfWidth, 0f, halfDepth),
                new Vector3(halfWidth, 0f, -halfDepth),
                new Vector3(-halfWidth, 0f, halfDepth),
                new Vector3(-halfWidth, 0f, -halfDepth),
            };

            int mask = GetVanillaGroundMask();
            float[] gaps = new float[cornerOffsets.Length];
            bool anyMiss = false;
            bool anyBuried = false;
            float minGap = float.MaxValue;
            float maxGap = float.MinValue;
            for (int i = 0; i < cornerOffsets.Length; i++)
            {
                Vector3 supportPoint = transform.TransformPoint(cornerOffsets[i]);
                Vector3 rayStart = supportPoint + Vector3.up * GroundReconcileRayStartHeight;
                if (!TryRaycastGroundIgnoringSelf(rayStart, mask, out RaycastHit hit))
                {
                    gaps[i] = float.NaN;
                    anyMiss = true;
                    continue;
                }

                // Positive gap = air between the support plane and the real ground.
                gaps[i] = supportPoint.y - hit.point.y;
                if (gaps[i] < -GroundReconcileBuriedEpsilon)
                    anyBuried = true;
                minGap = Mathf.Min(minGap, gaps[i]);
                maxGap = Mathf.Max(maxGap, gaps[i]);
            }

            string gapText = $"[{gaps[0]:0.000}, {gaps[1]:0.000}, {gaps[2]:0.000}, {gaps[3]:0.000}]";
            if (anyMiss)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Refinery] GroundReconcile: gaps={gapText} action=none (a corner probe found no ground within {GroundReconcileRayDistance:0.#}; never yank the rig into a void).");
                return;
            }

            if (anyBuried)
            {
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] GroundReconcile: gaps={gapText} action=none (ground above the support plane; the terrain-platform path owns buried corners).");
                return;
            }

            if (minGap <= GroundReconcileMinAirGap)
            {
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] GroundReconcile: gaps={gapText} action=none (smallest air gap {minGap:0.000} <= {GroundReconcileMinAirGap:0.##}; base already rests on ground).");
                return;
            }

            transform.position += Vector3.down * minGap;

            // The remaining corner gaps after lowering; skirt them with the runtime
            // platform so no corner is left hanging and no stale platform floats.
            float residual = maxGap - minGap;
            float platformHeight = _terrainPlatformHeight.Value;
            if (residual > GroundReconcileResidualThreshold)
            {
                platformHeight = Mathf.Max(platformHeight, residual + GroundReconcileResidualPlatformSkirt);
                ConfigureTerrainPlatform(platformHeight);
            }

            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] GroundReconcile: gaps={gapText} action=lowered offset={minGap:0.000} residual={residual:0.000} platformHeight={platformHeight:0.000}.");
        }

        private bool TryRaycastGroundIgnoringSelf(Vector3 rayStart, int mask, out RaycastHit bestHit)
        {
            bestHit = default;
            RaycastHit[] hits = Physics.RaycastAll(
                rayStart,
                Vector3.down,
                GroundReconcileRayDistance + GroundReconcileRayStartHeight,
                mask,
                QueryTriggerInteraction.Ignore);

            bool found = false;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null || collider.transform.IsChildOf(transform))
                    continue;

                if (hits[i].distance < bestDistance)
                {
                    bestDistance = hits[i].distance;
                    bestHit = hits[i];
                    found = true;
                }
            }

            return found;
        }

        [ClientRpc]
        private void ShowTipClientRpc(string title, string body, bool warning)
        {
            HUDManager.Instance?.DisplayTip(title, body, isWarning: warning);
        }

        private PlayerControllerB FindPlayerByClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.actualClientId == clientId)
                    return player;
            }

            return null;
        }

        private Transform ResolvePumpSnapPoint()
        {
            Transform direct = FindDirectChild("PumpSnapPoint");
            Transform nestedDuplicate = FindNestedChild("PumpSnapPoint", direct);
            if (direct != null)
            {
                // The authored nested PumpSnapPoint stays ACTIVE: we no longer snap
                // pumps to it, but AlignAnchorsToAuthoredSnapPoint reads its transform
                // as the baked ground/anchor reference, so it must remain in the scene.
                if (nestedDuplicate != null && !_loggedNestedPumpSnapPoint)
                {
                    _loggedNestedPumpSnapPoint = true;
                    ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Fuel] Preferring the direct runtime PumpSnapPoint; authored anchor at '{BuildTransformPath(nestedDuplicate)}' is kept active as the alignment reference.");
                }

                return direct;
            }

            return nestedDuplicate ?? transform;
        }

        // Re-derive the runtime snap point, hookup trigger, and oil-stream anchor from
        // the authored PumpSnapPoint baked into the visual (which sits at the builder's
        // authoring identity), instead of the rig-root-local constants that assume the
        // visual is at identity. Idempotent: positions are set absolutely, and the guard
        // skips repeated Awake/spawn cycles.
        private void AlignAnchorsToAuthoredSnapPoint()
        {
            if (_anchoredToAuthoredSnap)
                return;

            _refineryVisual = FindDirectChild(RefineryVisualName) ?? _refineryVisual;
            if (_refineryVisual == null)
                return;

            Transform authored = FindDescendant(_refineryVisual, AuthoredSnapPointName);
            if (authored == null)
                return;

            _authoredSnapPoint = authored;

            Vector3 snapWorld = authored.position + _refineryVisual.TransformVector(AuthoredSnapToPumpSnapVisualOffset);
            if (_snapPoint != null && _snapPoint != transform)
            {
                _snapPoint.position = snapWorld;
                _snapPoint.rotation = transform.rotation;
            }

            Transform hookup = FindDirectChild(PumpHookupTriggerName);
            if (hookup != null)
                hookup.position = snapWorld;

            _anchoredToAuthoredSnap = true;
        }

        // One-shot alignment diagnostic: dumps the world/local transforms of the whole
        // anchor chain so the in-game float/tilt regressions are diagnosable in one line.
        private void LogAlignmentDiagnostic()
        {
            if (_loggedAlignmentDiagnostic)
                return;
            _loggedAlignmentDiagnostic = true;

            Transform visual = _refineryVisual ?? FindDirectChild(RefineryVisualName);
            Transform modelRoot = visual != null ? FindDescendant(visual, "ModelRoot") : null;
            Transform authored = _authoredSnapPoint ?? (visual != null ? FindDescendant(visual, AuthoredSnapPointName) : null);
            Vector3 snapWorld = _snapPoint != null ? _snapPoint.position : transform.position;
            Vector3 streamWorld = authored != null && visual != null
                ? authored.position + visual.TransformVector(AuthoredSnapToStreamMouthVisualOffset)
                : transform.TransformPoint(OilStreamMouthLocalPosition);

            string cubeBounds = "unresolved";
            if (visual != null)
            {
                Transform cube = FindDescendant(visual, "Cube_119");
                if (cube != null && TryGetWorldBoundsY(cube, out float minY, out float maxY))
                    cubeBounds = $"minY={minY:0.000} maxY={maxY:0.000}";
            }

            ShipSystemsBootstrap.Log?.LogInfo(
                $"[LGUShipSystems.Refinery] Alignment: rootWorld={transform.position} "
                + $"visualLocal={(visual != null ? visual.localPosition.ToString() : "<none>")} "
                + $"modelRootLocal={(modelRoot != null ? modelRoot.localPosition.ToString() : "<none>")} "
                + $"authoredSnapWorld={(authored != null ? authored.position.ToString() : "<none>")} "
                + $"runtimeSnapWorld={snapWorld} oilStreamAnchorWorld={streamWorld} Cube_119Bounds=[{cubeBounds}].");
        }

        private static bool TryGetWorldBoundsY(Transform target, out float minY, out float maxY)
        {
            minY = float.MaxValue;
            maxY = float.MinValue;
            if (target == null)
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

                for (int c = 0; c < corners.Length; c++)
                {
                    float worldY = filter.transform.TransformPoint(corners[c]).y;
                    minY = Mathf.Min(minY, worldY);
                    maxY = Mathf.Max(maxY, worldY);
                    found = true;
                }
            }

            return found;
        }

        private Transform FindDescendant(Transform parent, string childName)
        {
            if (parent == null)
                return null;

            Transform[] children = parent.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && child != parent && string.Equals(child.name, childName, StringComparison.Ordinal))
                    return child;
            }

            return null;
        }

        private Transform FindDirectChild(string childName)
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child != null && string.Equals(child.name, childName, StringComparison.Ordinal))
                    return child;
            }

            return null;
        }

        private Transform FindNestedChild(string childName, Transform ignored)
        {
            Transform[] children = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null || child == transform || child == ignored || child.parent == transform)
                    continue;
                if (string.Equals(child.name, childName, StringComparison.Ordinal))
                    return child;
            }

            return null;
        }

        private Transform FindChild(string childName)
        {
            Transform[] children = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && string.Equals(child.name, childName, StringComparison.Ordinal))
                    return child;
            }

            return null;
        }

        private static string BuildTransformPath(Transform child)
        {
            if (child == null)
                return string.Empty;

            string path = child.name;
            while (child.parent != null)
            {
                child = child.parent;
                path = child.name + "/" + path;
            }

            return path;
        }

        private void OnAttachedFillChanged(float previous, float current)
        {
            UpdateText();
        }

        private void OnRemainingChanged(int previous, int current)
        {
            UpdateText();
        }

        private void OnSourceStateChanged(int previous, int current)
        {
            UpdateText();
        }

        private void OnTerrainPlatformHeightChanged(float previous, float current)
        {
            ApplyTerrainPlatformHeight(current);
        }

        private void ApplyTerrainPlatformHeight(float height)
        {
            if (_terrainPlatform == null)
                _terrainPlatform = FindChild("RefineryTerrainPlatform");
            if (_terrainPlatform == null)
                return;

            height = Mathf.Clamp(height, 0f, TerrainPlatformMaxHeight);
            bool active = height > 0.05f;
            _terrainPlatform.gameObject.SetActive(active);
            if (!active)
                return;

            Vector3 localScale = _terrainPlatform.localScale;
            localScale.y = height;
            _terrainPlatform.localScale = localScale;

            Vector3 localPosition = _terrainPlatform.localPosition;
            localPosition.y = TerrainPlatformTopLocalY - height * 0.5f;
            _terrainPlatform.localPosition = localPosition;
        }

        private void UpdateText()
        {
            if (IsServer && _attachedPump != null)
            {
                _sourceState.Value = _attachedPump.IsStalled ? 2 : 1;
                _attachedFill01.Value = _attachedPump.FillProgress01;
            }

            int sourceState = _sourceState.Value;
            int fillPercent = Mathf.RoundToInt(Mathf.Clamp01(_attachedFill01.Value) * 100f);
            if (_trigger != null)
            {
                _trigger.interactable = _remainingFills.Value > 0 || sourceState != 0;
                ApplyInteractionIcons(_trigger);
                // [LMB] is the placeholder vanilla rewrites with the bound interact key.
                _trigger.hoverTip = sourceState == 2
                    ? "Restart fuel pump : [LMB]"
                    : sourceState == 1
                        ? $"Detach fuel pump : [LMB]\n(filling {Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Amount(ShipFuelConfig.PumpFuelAmount * Mathf.Clamp01(_attachedFill01.Value))} fuel)"
                        : "Drop fuel pump here";
            }

            if (_scan != null)
            {
                _scan.headerText = "Oil Refinery";
                _scan.subText = sourceState != 0
                    ? sourceState == 2 ? "pump stalled" : $"pump filling {Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Amount(ShipFuelConfig.PumpFuelAmount * Mathf.Clamp01(_attachedFill01.Value))} fuel"
                    : $"{_remainingFills.Value}/{ShipFuelConfig.SourceFillCount} pumps remaining";
            }
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

        private void ConfigureOilRigLoopAudio()
        {
            if (_loopAudio != null)
                return;

            Transform existing = FindDirectChild("OilRigLoopAudio");
            GameObject audioObject = existing != null ? existing.gameObject : new GameObject("OilRigLoopAudio");
            audioObject.transform.SetParent(transform, false);
            audioObject.transform.localPosition = Vector3.zero;
            audioObject.transform.localRotation = Quaternion.identity;
            audioObject.transform.localScale = Vector3.one;

            _loopAudio = audioObject.GetComponent<AudioSource>();
            if (_loopAudio == null)
                _loopAudio = audioObject.AddComponent<AudioSource>();

            _loopAudio.loop = true;
            _loopAudio.playOnAwake = false;
            _loopAudio.spatialBlend = 1f;
            _loopAudio.rolloffMode = AudioRolloffMode.Linear;
            _loopAudio.minDistance = OilRigLoopMinDistance;
            _loopAudio.maxDistance = OilRigLoopMaxDistance;
            _loopAudio.volume = OilRigLoopVolume;
            _loopAudio.dopplerLevel = 0f;
            if (_oilRigLoopClip != null)
                _loopAudio.clip = _oilRigLoopClip;
        }

        private void EnsureOilRigLoopLoading()
        {
            if (_loopAudio != null && _oilRigLoopClip != null && _loopAudio.clip != _oilRigLoopClip)
                _loopAudio.clip = _oilRigLoopClip;
            if (_oilRigLoopClip != null || _oilRigLoopLoadStarted || _oilRigLoopLoadFailed || !gameObject.activeInHierarchy)
                return;

            string path = ResolveOilRigLoopPath();
            if (string.IsNullOrEmpty(path))
            {
                _oilRigLoopLoadFailed = true;
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Oil rig loop audio missing: {OilRigLoopFileName}");
                return;
            }

            _oilRigLoopLoadStarted = true;
            StartCoroutine(LoadOilRigLoop(path));
        }

        private IEnumerator LoadOilRigLoop(string path)
        {
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _oilRigLoopLoadFailed = true;
                    ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Failed loading oil rig loop audio '{path}': {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    _oilRigLoopLoadFailed = true;
                    ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Oil rig loop audio decoded null: {path}");
                    yield break;
                }

                clip.name = "LGU_OilRigLoop_Provided";
                _oilRigLoopClip = clip;
                if (_loopAudio != null)
                    _loopAudio.clip = clip;
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Refinery] Loaded oil rig loop audio from '{path}'.");
                TryStartOilRigLoopAudio();
            }
        }

        private void TryStartOilRigLoopAudio()
        {
            if (_loopAudio == null || !gameObject.activeInHierarchy)
                return;

            if (_loopAudio.clip == null && _oilRigLoopClip != null)
                _loopAudio.clip = _oilRigLoopClip;
            if (_loopAudio.clip != null && !_loopAudio.isPlaying)
                _loopAudio.Play();
        }

        private static string ResolveOilRigLoopPath()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(ShipSystemsBootstrap).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                !string.IsNullOrWhiteSpace(assemblyDir) ? Path.Combine(assemblyDir, "Audio", OilRigLoopFileName) : null,
                // #613 task 2.1: this feature ships in its own package now, so the plugin
                // directory holding the payload is named after it. Both Gale layouts are
                // listed for the same reason the deploy target lists both. The legacy
                // y4ngz-Y4NGZCompany entry below stays: it is what finds the payload a
                // pre-#613 install left behind, on a profile that has not redeployed.
                Path.Combine(Paths.PluginPath, "Y4NGZShipSystems", "Audio", OilRigLoopFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZShipSystems", "Audio", OilRigLoopFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "Audio", OilRigLoopFileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "Audio", OilRigLoopFileName),
                Path.Combine(Paths.PluginPath, "Audio", OilRigLoopFileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Audio", OilRigLoopFileName),
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}