using System.Collections;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal sealed class FuelLootItem : MonoBehaviour
    {
        [SerializeField] private float _fuelValue;
        [SerializeField] private int _scrapValue;
        [SerializeField] private string _label;

        private static bool _loggedFirstRuntimeClone;
        private static bool _startedTemporaryFallTrack;

        private GrabbableObject _grabbable;
        private ScanNodeProperties _scan;

        internal float FuelValue => _fuelValue;

        private void Awake()
        {
            RefreshRuntimeReferences();
        }

        private void Start()
        {
            if (_grabbable == null || _grabbable.itemProperties?.spawnPrefab == gameObject)
                return;

            FuelItemSounds.TryAssign(_grabbable.itemProperties);
            FuelItemSounds.EnsureSource(gameObject);

            if (!_loggedFirstRuntimeClone)
            {
                _loggedFirstRuntimeClone = true;
                FuelRuntimeUtility.LogPrefabDiagnostics("spawned fuel loot clone", gameObject, _grabbable.itemProperties);
            }

            if (!_startedTemporaryFallTrack)
            {
                _startedTemporaryFallTrack = true;
                StartCoroutine(TemporaryFallTrackRoutine());
            }
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
                ShipSystemsBootstrap.Log?.LogDebug($"[LGUShipSystems.Fuel][FallTrack] FuelLoot t={elapsed:0}s root={transform.position.ToString("F2")}, parent={parentName}, scan={FormatWorldPosition(_scan != null ? _scan.transform : null)}, renderer={FormatWorldPosition(renderer != null ? renderer.transform : null)}, rigidbody={bodyState}, grabbable={grabbableState}");
            }
            catch (System.Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel][FallTrack] FuelLoot diagnostic failed: {ex.Message}");
            }
        }

        private static string FormatWorldPosition(Transform target)
        {
            return target != null ? target.position.ToString("F2") : "<null>";
        }

        internal void Initialize(float fuelValue, int scrapValue, string label)
        {
            _fuelValue = Mathf.Max(0f, fuelValue);
            _scrapValue = Mathf.Max(0, scrapValue);
            _label = label ?? string.Empty;
            RefreshRuntimeReferences();
        }

        private void RefreshRuntimeReferences()
        {
            _grabbable = GetComponent<GrabbableObject>();
            _scan = GetComponentInChildren<ScanNodeProperties>(true);
            if (_scan != null && _scan.transform != transform)
            {
                // Vanilla scans use RaycastHit.transform, which resolves to the
                // attached rigidbody, not necessarily the collider's transform.
                // Give this trigger its own kinematic body so the hit resolves to
                // ScanNode, rather than the fuel root (which has no scan component).
                Rigidbody scanBody = _scan.GetComponent<Rigidbody>();
                if (scanBody == null) scanBody = _scan.gameObject.AddComponent<Rigidbody>();
                scanBody.isKinematic = true;
                scanBody.useGravity = false;
            }


            if (_grabbable != null)
                _grabbable.customGrabTooltip = "Grab fuel source";

            RefreshScanText();
        }

        internal void RefreshScanText()
        {
            if (_scan == null) _scan = GetComponentInChildren<ScanNodeProperties>(true);
            if (_scan != null)
            {
                _scan.headerText = string.IsNullOrWhiteSpace(_label) ? "Fuel source" : _label;
                _scan.subText = FuelPresentation.Amount(_fuelValue) + " fuel";
                // Keep the actual synchronized sale value independent of fuel units.
                _scan.scrapValue = _grabbable != null ? _grabbable.scrapValue : _scrapValue;
            }
        }
    }
}
