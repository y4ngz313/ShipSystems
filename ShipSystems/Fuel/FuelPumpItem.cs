using System.Collections;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal enum FuelPumpState
    {
        Empty = 0,
        Filling = 1,
        Stalled = 2,
        Filled = 3,
    }

    internal sealed class FuelPumpItem : NetworkBehaviour
    {
        private const float DefaultGroundLockOffset = 0.39f;
        private const float LooseFallSafetyDropDistance = 10f;
        private const float LooseFallSafetySaturatedSeconds = 2f;
        private const float LooseFallSafetyRayStartHeight = 2f;
        private const float LooseFallSafetyRayDistance = 80f;

        private readonly NetworkVariable<int> _state = new NetworkVariable<int>((int)FuelPumpState.Empty);
        private readonly NetworkVariable<float> _fillProgress01 = new NetworkVariable<float>(0f);

        private static bool _loggedFirstNetworkSpawn;
        private static bool _startedTemporaryFallTrack;

        private ScanNodeProperties _scan;
        private GrabbableObject _grabbable;
        private FuelSourceController _source;
        private bool _mustBeHeldBeforeReattach;
        private float _stallRollAccumulator;
        private float _groundLockOffset = float.NaN;
        private float _fallSafetyNoFloorSince = -1f;

        internal FuelPumpState State => (FuelPumpState)_state.Value;
        internal bool IsFilled => State == FuelPumpState.Filled;
        internal bool IsStalled => State == FuelPumpState.Stalled;
        internal bool IsEmpty => State == FuelPumpState.Empty;
        internal float FuelValue => ShipFuelConfig.PumpFuelAmount;
        internal float FillProgress01 => _fillProgress01.Value;
        internal bool CanAttachToRefinery => IsEmpty
            && !_mustBeHeldBeforeReattach
            && _grabbable != null
            && !_grabbable.isHeld
            && !_grabbable.isPocketed
            && !_grabbable.heldByPlayerOnServer
            && _grabbable.playerHeldBy == null
            && _grabbable.hasHitGround;

        private void Awake()
        {
            _grabbable = GetComponent<GrabbableObject>();
            _scan = GetComponentInChildren<ScanNodeProperties>(true);
        }

        private void Update()
        {
            if (!IsServer)
                return;

            if (_mustBeHeldBeforeReattach && IsHeldByServerPlayer())
                _mustBeHeldBeforeReattach = false;

            TickLooseFallSafetyNet();
        }

        public override void OnNetworkSpawn()
        {
            _state.OnValueChanged += OnStateChanged;
            _fillProgress01.OnValueChanged += OnProgressChanged;
            UpdateScan();

            // Late joiners never saw the one-shot lock ClientRpc; rebuild the
            // locked pose from the replicated state.
            if (!IsServer && (State == FuelPumpState.Filling || State == FuelPumpState.Stalled))
                ApplyRefineryLockLocal(transform.position, transform.rotation, locked: true);

            if (!_loggedFirstNetworkSpawn)
            {
                _loggedFirstNetworkSpawn = true;
                FuelRuntimeUtility.LogPrefabDiagnostics("spawned Fuel Pump clone", gameObject, _grabbable?.itemProperties);
            }

            if (!_startedTemporaryFallTrack)
            {
                _startedTemporaryFallTrack = true;
                StartCoroutine(TemporaryFallTrackRoutine());
            }
        }

        public override void OnNetworkDespawn()
        {
            _state.OnValueChanged -= OnStateChanged;
            _fillProgress01.OnValueChanged -= OnProgressChanged;
        }

        internal bool TryBeginFilling(FuelSourceController source, Transform snapPoint = null)
        {
            if (!IsServer || source == null || State != FuelPumpState.Empty)
                return false;

            _source = source;
            _stallRollAccumulator = 0f;
            if (snapPoint != null)
                ApplyRefineryLock(GetGroundedLockPosition(snapPoint), snapPoint.rotation, locked: true);
            _state.Value = (int)FuelPumpState.Filling;
            UpdateScan();
            return true;
        }

        internal void ServerTickFill(float deltaTime, Transform snapPoint = null)
        {
            if (!IsServer || State != FuelPumpState.Filling)
                return;

            if (snapPoint != null)
                MaintainLockedPose(snapPoint);

            if (_source != null && Vector3.Distance(transform.position, _source.transform.position) > 8f)
            {
                _state.Value = (int)FuelPumpState.Stalled;
                UpdateScan();
                return;
            }

            _stallRollAccumulator += Mathf.Max(0f, deltaTime);
            if (_stallRollAccumulator >= 1f)
            {
                float seconds = _stallRollAccumulator;
                _stallRollAccumulator = 0f;
                float stallChance = ShipFuelConfig.PumpStallChance01 * (seconds / 60f);
                if (stallChance > 0f && Random.value < stallChance)
                {
                    _state.Value = (int)FuelPumpState.Stalled;
                    UpdateScan();
                    return;
                }
            }

            float fillSeconds = ShipFuelConfig.PumpFillSeconds;
            _fillProgress01.Value = Mathf.Clamp01(_fillProgress01.Value + deltaTime / fillSeconds);
            if (_fillProgress01.Value >= 1f)
            {
                _state.Value = (int)FuelPumpState.Filled;
                _source?.NotifyPumpFilled(this);
                _source = null;
                ApplyRefineryLock(transform.position, transform.rotation, locked: false);
                UpdateScan();
            }
        }

        internal void RestartStalled()
        {
            if (!IsServer || State != FuelPumpState.Stalled)
                return;

            _stallRollAccumulator = 0f;
            _state.Value = (int)FuelPumpState.Filling;
            UpdateScan();
        }

        internal void DetachFromRefinery(Vector3 position, Quaternion rotation)
        {
            if (!IsServer)
                return;

            _source = null;
            _stallRollAccumulator = 0f;
            _mustBeHeldBeforeReattach = true;
            _state.Value = (int)FuelPumpState.Empty;
            ApplyRefineryLock(position, rotation, locked: false);
            UpdateScan();
        }

        internal Vector3 GetGroundedPositionForFloor(Vector3 floorPoint)
        {
            return floorPoint + Vector3.up * GetGroundLockOffset();
        }

        internal void MaintainLockedPose(Transform snapPoint)
        {
            if (!IsServer || snapPoint == null)
                return;

            MaintainLockedPose(GetGroundedLockPosition(snapPoint), snapPoint.rotation);
        }

        [ServerRpc(RequireOwnership = false)]
        internal void RestartStalledServerRpc()
        {
            RestartStalled();
        }

        private void OnStateChanged(int previous, int current)
        {
            UpdateScan();
        }

        private void OnProgressChanged(float previous, float current)
        {
            UpdateScan();
        }

        private void UpdateScan()
        {
            int fillPercent = Mathf.RoundToInt(Mathf.Clamp01(_fillProgress01.Value) * 100f);
            if (_scan != null)
            {
                _scan.headerText = "FUEL PUMP";
                switch (State)
                {
                    case FuelPumpState.Filling:
                        _scan.subText = $"filling {Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Amount(FuelValue * Mathf.Clamp01(FillProgress01))} fuel";
                        break;
                    case FuelPumpState.Stalled:
                        _scan.subText = "stalled, restart at refinery";
                        break;
                    case FuelPumpState.Filled:
                        _scan.subText = $"+{Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Amount(FuelValue)} fuel";
                        break;
                    default:
                        _scan.subText = fillPercent > 0
                            ? $"paused at {Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Amount(FuelValue * Mathf.Clamp01(FillProgress01))} fuel - drop at oil refinery"
                            : "empty - drop at oil refinery";
                        break;
                }
            }

            if (_grabbable != null)
            {
                if (IsFilled)
                    _grabbable.customGrabTooltip = $"Grab filled fuel pump (+{Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Amount(FuelValue)} fuel)";
                else if (fillPercent > 0)
                    _grabbable.customGrabTooltip = $"Grab fuel pump ({Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Amount(FuelValue * Mathf.Clamp01(FillProgress01))} fuel full)";
                else
                    _grabbable.customGrabTooltip = "Grab fuel pump (empty)";
            }
        }

        // The snap point sits on the ground; the pump pivot is at its center,
        // so lift by the capsule's lower extent so the base rests on the ground.
        private Vector3 GetGroundedLockPosition(Transform snapPoint)
        {
            return snapPoint.position + snapPoint.up * GetGroundLockOffset();
        }

        private float GetGroundLockOffset()
        {
            if (!float.IsNaN(_groundLockOffset))
                return _groundLockOffset;

            _groundLockOffset = DefaultGroundLockOffset;
            CapsuleCollider capsule = GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                float halfVerticalExtent = capsule.direction == 1
                    ? capsule.height * 0.5f
                    : capsule.radius;
                float bottom = capsule.center.y - halfVerticalExtent;
                _groundLockOffset = Mathf.Max(0f, -bottom);
            }

            return _groundLockOffset;
        }

        private void MaintainLockedPose(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            if (_grabbable == null)
                return;

            _grabbable.hasHitGround = true;
            _grabbable.reachedFloorTarget = true;
            _grabbable.fallTime = 1f;
            _grabbable.targetFloorPosition = transform.parent != null
                ? transform.parent.InverseTransformPoint(position)
                : position;
            _grabbable.startFallingPosition = _grabbable.targetFloorPosition;
            StopBodyMotion(_grabbable.propBody);
        }

        private void ApplyRefineryLock(Vector3 position, Quaternion rotation, bool locked)
        {
            ApplyRefineryLockLocal(position, rotation, locked);
            ApplyRefineryLockClientRpc(position, rotation, locked);
        }

        [ClientRpc]
        private void ApplyRefineryLockClientRpc(Vector3 position, Quaternion rotation, bool locked)
        {
            ApplyRefineryLockLocal(position, rotation, locked);
        }

        private void ApplyRefineryLockLocal(Vector3 position, Quaternion rotation, bool locked)
        {
            transform.SetPositionAndRotation(position, rotation);
            if (_grabbable == null)
                return;

            _grabbable.grabbable = !locked;
            _grabbable.grabbableToEnemies = !locked;
            _grabbable.isHeld = false;
            _grabbable.isPocketed = false;
            _grabbable.heldByPlayerOnServer = false;
            _grabbable.playerHeldBy = null;
            _grabbable.parentObject = null;
            _grabbable.hasHitGround = true;
            _grabbable.reachedFloorTarget = true;
            _grabbable.fallTime = 1f;
            _grabbable.targetFloorPosition = transform.parent != null
                ? transform.parent.InverseTransformPoint(position)
                : position;
            _grabbable.startFallingPosition = _grabbable.targetFloorPosition;

            if (locked)
                _grabbable.EnablePhysics(enable: false);
            else
                _grabbable.EnablePhysics(enable: true);

            Rigidbody body = _grabbable.propBody != null ? _grabbable.propBody : GetComponent<Rigidbody>();
            StopBodyMotion(body);
            if (body != null)
            {
                body.isKinematic = true;
                body.useGravity = false;
            }
        }

        private bool IsHeldByServerPlayer()
        {
            return _grabbable != null
                   && (_grabbable.isHeld || _grabbable.heldByPlayerOnServer || _grabbable.playerHeldBy != null);
        }

        private void TickLooseFallSafetyNet()
        {
            if (!IsLooseFallSafetyCandidate())
            {
                _fallSafetyNoFloorSince = -1f;
                return;
            }

            Vector3 dropStart = GetStartFallingWorldPosition();
            bool fellFarBelowStart = dropStart.y - transform.position.y > LooseFallSafetyDropDistance;
            bool saturatedWithoutFloor = _grabbable.fallTime >= 1f && !_grabbable.reachedFloorTarget;
            if (saturatedWithoutFloor)
            {
                if (_fallSafetyNoFloorSince < 0f)
                    _fallSafetyNoFloorSince = Time.time;
            }
            else
            {
                _fallSafetyNoFloorSince = -1f;
            }

            bool timedOutWithoutFloor = _fallSafetyNoFloorSince >= 0f && Time.time - _fallSafetyNoFloorSince >= LooseFallSafetySaturatedSeconds;
            if (!fellFarBelowStart && !timedOutWithoutFloor)
                return;

            RegroundLoosePump(dropStart, fellFarBelowStart ? "fell below drop start" : "fall saturated without floor");
        }

        private bool IsLooseFallSafetyCandidate()
        {
            return _grabbable != null
                   && State != FuelPumpState.Filling
                   && State != FuelPumpState.Stalled
                   && !_grabbable.isHeld
                   && !_grabbable.isPocketed
                   && !_grabbable.heldByPlayerOnServer
                   && _grabbable.playerHeldBy == null
                   && !_grabbable.hasHitGround;
        }

        private Vector3 GetStartFallingWorldPosition()
        {
            if (_grabbable == null)
                return transform.position;

            return transform.parent != null
                ? transform.parent.TransformPoint(_grabbable.startFallingPosition)
                : _grabbable.startFallingPosition;
        }

        private void RegroundLoosePump(Vector3 dropStart, string reason)
        {
            Vector3 rayStart = dropStart + Vector3.up * LooseFallSafetyRayStartHeight;
            int mask = GetVanillaGroundMask();
            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, LooseFallSafetyRayDistance, mask, QueryTriggerInteraction.Ignore))
            {
                Vector3 groundedPosition = GetGroundedPositionForFloor(hit.point);
                ApplyRefineryLock(groundedPosition, transform.rotation, locked: false);
                _fallSafetyNoFloorSince = -1f;
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel][FallTrack] Re-grounded loose fuel pump after {reason}; hit={hit.point.ToString("F2")}, root={groundedPosition.ToString("F2")}.");
                return;
            }

            Vector3 fallbackPosition = ResolveLastResortRecoveryPosition();
            ApplyRefineryLock(fallbackPosition, transform.rotation, locked: false);
            _fallSafetyNoFloorSince = -1f;
            ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel][FallTrack] Fuel pump had no floor hit after {reason}; moved to recovery position {fallbackPosition.ToString("F2")}.");
        }

        private Vector3 ResolveLastResortRecoveryPosition()
        {
            if (_source != null)
                return GetGroundedPositionForFloor(_source.transform.position);

            StartOfRound start = StartOfRound.Instance;
            if (start?.elevatorTransform != null)
            {
                Vector3 shipFloor = start.elevatorTransform.position + start.elevatorTransform.forward * 2f;
                return GetGroundedPositionForFloor(shipFloor);
            }

            return transform.position + Vector3.up * Mathf.Max(1f, GetGroundLockOffset());
        }

        private static int GetVanillaGroundMask()
        {
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : Physics.DefaultRaycastLayers;
            return mask != 0 ? mask : Physics.DefaultRaycastLayers;
        }

        // Temporary diagnostic: verifies vanilla-style scripted fall state after clone spawn.
        private IEnumerator TemporaryFallTrackRoutine()
        {
            for (float elapsed = 0f; elapsed <= 16f; elapsed += 2f)
            {
                LogTemporaryFallTrack(elapsed);
                if (elapsed >= 16f)
                    yield break;

                yield return new WaitForSeconds(2f);
            }
        }

        private void LogTemporaryFallTrack(float elapsed)
        {
            try
            {
                Rigidbody body = _grabbable?.propBody != null ? _grabbable.propBody : GetComponent<Rigidbody>();
                Renderer renderer = GetComponentInChildren<Renderer>(true);
                string parentName = transform.parent != null ? transform.parent.name : "<none>";
                string bodyState = body != null
                    ? $"isKinematic={body.isKinematic}, useGravity={body.useGravity}, velocity={body.velocity.ToString("F2")}"
                    : "<null>";
                string grabbableState = _grabbable != null
                    ? $"hasHitGround={_grabbable.hasHitGround}, reachedFloorTarget={_grabbable.reachedFloorTarget}, fallTime={_grabbable.fallTime:0.00}, startFallingPosition={_grabbable.startFallingPosition.ToString("F2")}, targetFloorPosition={_grabbable.targetFloorPosition.ToString("F2")}"
                    : "<null>";

                // Per-item placement telemetry every 2s for 16s: diagnostic detail, not
                // something a player's console should carry at Info (#716 F5).
                ShipSystemsBootstrap.Log?.LogDebug($"[LGUShipSystems.Fuel][FallTrack] FuelPump t={elapsed:0}s root={transform.position.ToString("F2")}, parent={parentName}, scan={FormatWorldPosition(_scan != null ? _scan.transform : null)}, renderer={FormatWorldPosition(renderer != null ? renderer.transform : null)}, rigidbody={bodyState}, grabbable={grabbableState}");
            }
            catch (System.Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel][FallTrack] FuelPump diagnostic failed: {ex.Message}");
            }
        }

        private static string FormatWorldPosition(Transform target)
        {
            return target != null ? target.position.ToString("F2") : "<null>";
        }

        private static void StopBodyMotion(Rigidbody body)
        {
            if (body == null)
                return;

            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }
}
