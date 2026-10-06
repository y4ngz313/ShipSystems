using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compat;
// Y4NGZCompany.Facility.Shared lives in Y4NGZCore, not in the Contracted feature set; it is
// the shared facility layer both sides may reference.
using Y4NGZCompany.Facility.Shared;
using Y4NGZCompany.ShipSystems.Fuel;
using Y4NGZCompany.ShipSystems.MonitorRow;
using Y4NGZCompany.ShipSystems.Power;
using Y4NGZCore.Modules.Environment;
using Y4NGZCore.Modules.Net;

namespace Y4NGZCompany.ShipSystems.Layout
{
    internal sealed partial class ShipSystemsController : MonoBehaviour
    {
        /// <summary>
        /// Shipped under ContractAudio for the mainframe hack, and reused here
        /// rather than copied into the ship audio set: two of the same file
        /// would only ever go out of step with each other.
        /// </summary>
        private const string ObjectiveChimeClipFile = "Mainframe/Hack_Success.wav";

        // #666: the ten fuel/power wire names, unchanged and frozen. The eight
        // vote/upgrade/refit names moved to Y4NGZTerminalUpgrades' ShipCommandController
        // with the systems that speak them.
        private const string MsgPowerState = "LGUShipSystems.PowerState";
        private const string MsgFuelState = "LGUShipSystems.FuelState";
        private const string MsgFuelRouteDenied = "LGUShipSystems.FuelRouteDenied";
        private const string MsgFuelChuteFeedback = "LGUShipSystems.FuelChuteFeedback";
        private const string MsgFuelChuteToggleRequest = "LGUShipSystems.FuelChuteToggleRequest";
        private const string MsgFuelChuteState = "LGUShipSystems.FuelChuteState";
        private const string MsgPowerSpend = "LGUShipSystems.PowerSpend";
        private const string MsgAbilityEvent = "LGUShipSystems.AbilityEvent";
        private const string MsgBatteryRestore = "LGUShipSystems.BatteryRestore";
        private const string MsgTechnicianRepair = "LGUShipSystems.TechnicianRepair";
        private const float WorldProbeReadyIntervalSeconds = 0.4f;
        private const float WorldProbeMissingShipIntervalSeconds = 1.5f;
        private const float SlowTickIntervalSeconds = 0.1f;
        private const float SlowTickPerfReportIntervalSeconds = 30f;
        // #951 S11H U3 (d): the report is a warning, so it is emitted only when there is something
        // to warn about. Test 3 logged it every 30 s unconditionally at a measured 0.2 ms/s.
        //
        // Derivation of the two thresholds, neither of them a round number picked to be quiet:
        // 5 ms/s is 0.5 % of a one-second budget, the point at which a background pass stops being
        // free and starts being a share of the frame time worth naming; and 4 ms in one call is a
        // quarter of a 16.7 ms frame at 60 fps, which is the same figure BlacksiteLandingSearch
        // budgets a whole search slice at. Below both, the window is closed and re-armed silently:
        // nothing is accumulated across a quiet window, so a later busy window still measures only
        // itself.
        private const double SlowTickPerfReportRateMillisecondsPerSecond = 5d;
        private const double SlowTickPerfReportSingleCallMilliseconds = 4d;
        private static readonly string[] SlowTickPerfStepNames =
        {
            "EnsureNetworkHandlers",
            "TrackShipPhaseReset",
            "FuelSourceSpawner.Tick",
            "ServerMaintenance",
            "IsShipWorldReady",
            "EnsureWorldSystems"
        };

        private const int AbilityBatteryBroken = 3;
        private const int AbilityBatteryFailureSequence = 4;
        private const int AbilityBatterySabotageHit = 5;
        private const int AbilityBatteryRestored = 6;
        private const int AbilityBatteryCritical = 7;
        private const int AbilityBatteryRestoredSpare = 8;
        private const int AbilityBatteryDropRepaired = 9;
        private const int AbilityBatterySabotageHitCritical = 10;

        internal static ShipSystemsController Instance { get; private set; }

        /// <summary>
        /// The ten fuel/power <c>LGUShipSystems.*</c> named-message handlers and their #435
        /// latch. See <see cref="BuildNetworkHandlers"/> for what this replaced and why.
        ///
        /// <para>Built lazily rather than in a field initialiser: the handler list is ten
        /// instance method groups, which a field initialiser may not reference, and a
        /// <c>MonoBehaviour</c> constructor is the wrong place to do work Unity may run on the
        /// loading thread.</para>
        /// </summary>
        private NamedMessageRegistration _networkHandlers;

        private float _nextStateBroadcastAt;
        private float _nextWorldProbeAt;
        private bool _lastInShipPhase = true;
        private float _nextSlowTickAt;
        private bool _shipWorldReady;
        private readonly double[] _slowTickPerfStepTotals = new double[SlowTickPerfStepNames.Length];
        private readonly double[] _slowTickPerfStepMaxima = new double[SlowTickPerfStepNames.Length];
        private double _slowTickPerfTotalMilliseconds;
        private int _slowTickPerfCalls;
        // #951 S11H U3 (d): the slowest single pass in the window, which is the half of the
        // threshold the per-step maxima cannot answer: a pass can be cheap in every step and still
        // be expensive as a whole.
        private double _slowTickPerfMaxPassMilliseconds;
        private int _stableWorldEnsureCallsSuppressed;
        private float _slowTickPerfWindowStartedAt = -1f;
        private float _nextSlowTickPerfReportAt;

        private float _power;
        private float _fuel;
        private bool _fuelInitialized;
        private float _reservedRouteFuel;
        private ShipFuelRouteRequest _reservedFuelRoute;
        private string _reservedFuelRouteKey = string.Empty;
        private string _currentFuelConstellationKey = string.Empty;
        private bool _pendingRouteFuelAuthorization;
        private int _pendingRouteFuelRouteIndex = -1;
        private int _pendingRouteFuelPreviousLevelIndex = -1;
        private string _pendingRouteFuelRouteKey = string.Empty;
        private bool _shipPowerOnline = true;
        private bool _batteryBroken;
        private bool _restoreObjectiveActive;
        private bool _restoreUsesSpareBattery;
        private bool _spareBatterySpawnedForEmergency;
        private NetworkObject _spawnedSpareBatteryNetworkObject;
        private float _nextRestoreItemFallbackCheckAt;
        // #716 D2: the emergency restore fallback used to run a full
        // FindObjectsOfType<GrabbableObject>(true) heap walk on every 2s outage tick, for
        // the whole outage. The candidate set barely moves between ticks, so the scan is
        // snapshotted here and reused; the periodic tick refreshes at most every 10s, and
        // the one-shot entry points force a fresh scan so a just-dropped apparatus counts.
        private const float OutageItemScanLifetimeSeconds = 10f;
        private static GrabbableObject[] _outageItemScan = Array.Empty<GrabbableObject>();
        private static float _outageItemScanExpiresAt = float.NegativeInfinity;
        // #666: the takeover/presentation scopes moved to Core's ShipPresentationScopes so
        // they coordinate both split ship plugins. This side keeps only its own deferral
        // shadows below and subscribes to the scope-change events in Awake.
        private bool _primaryMapScreenPowerDeferred;
        private bool _deferredPrimaryMapScreenPowerOnline;
        // Power-visual requests that arrived while the full-wall takeover owned
        // the monitors. Vanilla SwitchScreenOn rewrites the Cube.001 material
        // slot outright, so replaying it mid-takeover snaps the large monitor
        // back to the vanilla map view; we hold the request and apply it once
        // the takeover releases the wall.
        private bool _powerVisualDeferred;
        private bool _deferredPowerVisualOnline;
        // Change-guards (issue #500). These track what was last *applied* to the
        // world / last *received* off the wire, not what was requested, so the
        // periodic 2.5 s broadcast and a late joiner's first payload still land:
        // a null cache means "unknown", which always applies once. Anything that
        // can desync the visuals from the cache (round reset, monitor takeover)
        // invalidates them instead of trusting the last value.
        private bool? _appliedPowerVisualOnline;
        private bool? _appliedEmergencyProtocolActive;
        private bool? _appliedEmergencyInteractionLockout;
        private bool? _lastReceivedPowerOnline;
        private bool? _lastReceivedBatteryBroken;
        private float _nextPowerDeltaBroadcastAt;
        private float _nextInteractionBlockTipAt;
        private float _lastBatteryFeedbackAt;
        private Coroutine _batteryFailureServerCoroutine;
        private Coroutine _batteryFailureLocalEffectsCoroutine;
        private Coroutine _criticalHitFlickerCoroutine;
#pragma warning disable CS0649, CS0169
        private AudioClip _objectiveChimeClip;
        private bool _objectiveChimeRequested;
        private AudioClip _batteryAlarmClip;
        private AudioClip _batteryThudClip;
        private AudioClip _batteryFaultClip;
        private AudioClip _batteryPowerDownClip;
        private AudioClip _batteryCriticalClip;
        private AudioClip _batteryRestoredClip;
        private AudioClip _batteryRepairClip;
        private AudioClip _batteryDockClip;
        private AudioClip _batteryWorkingLoopClip;
        private AudioClip _batteryBrokenLoopClip;
        private AudioClip _batteryHitMonsterClip;
        private bool _batteryAudioLoadStarted;
#pragma warning restore CS0649, CS0169
        private bool _loggedMonitorWallState;
        private readonly List<MeshRenderer> _shipScreenRenderers = new List<MeshRenderer>();
        private readonly List<Material[]> _shipScreenOriginalMaterials = new List<Material[]>();
        private readonly List<Material[]> _shipScreenBlackMaterials = new List<Material[]>();
        private Material _shipScreenBlackMaterial;
        private bool _shipScreenPowerOff;

        private ExternalBatteryStation _batteryStation;
        private FuelChuteController _fuelChute;
        private bool _fuelSystemEnabled = true;
        private bool _fuelSystemEnabledReplicated;
        private bool _fuelChuteOpen;
        private float _fuelChuteIdleCloseAt;

        internal float Power => _power;
        internal float Fuel
        {
            get
            {
                EnsureFuelInitialized();
                return _fuel;
            }
        }

        // Fuel is host-authoritative: a client must never render or price fuel from
        // its own config, only from the flag the host ships in MsgFuelState. The
        // local config is the fallback used before the first state arrives.
        internal bool FuelSystemEnabled =>
            IsServer() || !_fuelSystemEnabledReplicated
                ? ConfiguredFuelSystemEnabled
                : _fuelSystemEnabled;

        internal static bool ConfiguredFuelSystemEnabled =>
            ShipSystemsBootstrap.FuelSystemEnabled?.Value ?? true;

        internal static bool IsFuelSystemEnabled()
        {
            ShipSystemsController instance = Instance;
            return instance != null ? instance.FuelSystemEnabled : ConfiguredFuelSystemEnabled;
        }

        internal float PowerCapacity => GetPowerCapacity();
        private float _replicatedFuelCapacity;
        internal float FuelCapacity => !IsServer() && _fuelSystemEnabledReplicated ? Mathf.Max(1f, _replicatedFuelCapacity) : ShipFuelConfig.TankSize;
        private readonly BatteryWarningDisplay _batteryWarnings = new BatteryWarningDisplay();
        internal float ReservedRouteFuel => _reservedRouteFuel;
        internal bool FuelChuteOpen => _fuelChuteOpen;
        internal bool ShipPowerOnline => _shipPowerOnline;
        internal bool BatteryBroken => _batteryBroken;
        internal bool EmergencyProtocolActive => _batteryBroken || !_shipPowerOnline;
        internal bool RestoreObjectiveActive => _restoreObjectiveActive;
        internal bool RestoreUsesSpareBattery => _restoreUsesSpareBattery;
        internal string RestoreItemDisplayName => _restoreUsesSpareBattery ? "spare battery" : "apparatus";
        internal string RestoreRecoveryObjectiveText => _restoreUsesSpareBattery ? "Find spare battery" : "Recover the apparatus";

        internal static void EnsureInstance()
        {
            if (Instance != null) return;

            GameObject go = new GameObject("LGUShipSystems_Controller");
            DontDestroyOnLoad(go);
            go.AddComponent<ShipSystemsController>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            ResetRunState(fullPower: true);
            // Preload battery SFX now: the working loop must already be playing
            // the moment the ship lands, not 1-2 s later while clips stream in.
            EnsureBatteryAudioClips();

            // #666: the presentation scopes live in Core now. This side's stake in them is
            // the deferred power visuals — replay them the moment a takeover releases the
            // wall, exactly as the pre-split scope setters did inline.
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.ExternalTakeoverChanged +=
                OnExternalTakeoverScopeChanged;
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.PrimaryTakeoverChanged +=
                OnPrimaryTakeoverScopeChanged;
            // #666: upgrade levels live with the command plugin. A BackupCapacitor purchase
            // used to write _power directly from ApplyUpgrade; now the change signal lands
            // here and the same floor raise runs against the new capacity.
            Y4NGZCore.Modules.ShipSystems.ShipUpgradeState.LevelsChanged += OnUpgradeLevelsChanged;
        }

        internal void Shutdown()
        {
            UnregisterNetworkHandlers();
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.ExternalTakeoverChanged -=
                OnExternalTakeoverScopeChanged;
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.PrimaryTakeoverChanged -=
                OnPrimaryTakeoverScopeChanged;
            Y4NGZCore.Modules.ShipSystems.ShipUpgradeState.LevelsChanged -= OnUpgradeLevelsChanged;
            _appliedPowerVisualOnline = null;
            _appliedEmergencyProtocolActive = null;
            _lastReceivedPowerOnline = null;
            _lastReceivedBatteryBroken = null;
            StopBatteryFailureSequences();
            SetShipAlarmAnimation(active: false);
            _batteryStation?.Destroy();
            if (_fuelChute != null)
                Destroy(_fuelChute.gameObject);
            _fuelChute = null;
            // Hands the GI slot back and tears the offscreen rig down (#676). Registrations
            // outlive the session by design; only leases and hardware bindings are dropped.
            FuelMonitorDisplay.Shutdown();
            _batteryWarnings.Destroy();
            if (Instance == this) Instance = null;
        }

        internal void Tick()
        {
            _batteryWarnings.Tick(_batteryBroken);
            if (Time.unscaledTime >= _nextSlowTickAt)
            {
                _nextSlowTickAt = Time.unscaledTime + SlowTickIntervalSeconds;
                RunSlowTick();
            }

            if (IsServer())
            {
                ServerTickFuelChute();
            }

            if (_shipWorldReady)
            {
                _batteryStation?.Tick();
                TickFuelMonitor();
            }
        }

        /// <summary>
        /// Drives the fuel readout (#676; multi-host since #678). Deliberately NOT a field on
        /// this class: the display owns its own static instance so no member here is named after
        /// the monitor content #659 deleted. Which monitor it lands on — a GeneralImprovements
        /// screen or a cloned stock one — is the display's own auto-detection, and its Tick is a
        /// no-op on a profile where neither host is available.
        /// </summary>
        private static void TickFuelMonitor()
        {
            FuelMonitorDisplay.EnsureInstance();
            FuelMonitorDisplay.Instance?.Tick();
        }

        private void OnExternalTakeoverScopeChanged(bool active, object takeoverTexture)
        {
            if (active)
            {
                ShipSystemsBootstrap.Log.LogInfo(
                    "[LGUShipSystems] External monitor takeover started; power visuals defer.");
                return;
            }

            ShipSystemsBootstrap.Log.LogInfo(
                "[LGUShipSystems] External monitor takeover ended.");
            if (_powerVisualDeferred)
            {
                bool online = _deferredPowerVisualOnline;
                _powerVisualDeferred = false;
                ShipSystemsBootstrap.Log.LogInfo(
                    $"[LGUShipSystems] Replaying deferred ship power visual state (online={online}).");
                ApplyShipPowerVisualState(online);
            }
        }

        private void OnPrimaryTakeoverScopeChanged(bool active)
        {
            if (active || !_primaryMapScreenPowerDeferred)
                return;

            bool online = _deferredPrimaryMapScreenPowerOnline;
            _primaryMapScreenPowerDeferred = false;
            ShipSystemsBootstrap.Log.LogInfo(
                $"[LGUShipSystems] Replaying deferred primary map-screen power state (online={online}).");
            ApplyPrimaryMapScreenPower(StartOfRound.Instance, online);
        }

        private float _lastKnownPowerCapacity = -1f;

        private void OnUpgradeLevelsChanged()
        {
            // Host-authoritative like every other power write. The capacity property reads
            // the new level on demand; what has to happen here is the BackupCapacitor floor
            // raise the pre-split ApplyUpgrade performed — only when capacity actually grew —
            // plus a broadcast so clients see the new capacity immediately rather than on
            // the next periodic snapshot.
            if (!IsServer())
                return;

            float previousCapacity = _lastKnownPowerCapacity;
            float capacity = GetPowerCapacity();
            _lastKnownPowerCapacity = capacity;
            if (previousCapacity > 0f && capacity > previousCapacity + 0.001f)
            {
                float oldFraction = Mathf.Clamp01(_power / previousCapacity);
                _power = Mathf.Clamp(
                    Mathf.Max(_power, capacity * Mathf.Max(0.5f, oldFraction)), 0f, capacity);
            }
            else
            {
                _power = Mathf.Clamp(_power, 0f, capacity);
            }

            MarkPowerDisplayDirty();
            BroadcastPowerState();
        }

        private void RunSlowTick()
        {
            long passStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long stepStart = passStart;
            EnsureNetworkHandlers();
            RecordSlowTickPerfStep(0, stepStart);

            stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
            TrackShipPhaseReset();
            RecordSlowTickPerfStep(1, stepStart);

            stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
            FuelSourceSpawner.Tick();
            RecordSlowTickPerfStep(2, stepStart);

            stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (IsServer())
            {
                ServerTickRestoreItemFallback();
                BatterySabotageDirector.TickServer();
                ServerTickPeriodicState();
            }
            RecordSlowTickPerfStep(3, stepStart);

            stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
            bool shipWorldReady = IsShipWorldReady();
            RecordSlowTickPerfStep(4, stepStart);
            _shipWorldReady = shipWorldReady;
            if (Time.unscaledTime >= _nextWorldProbeAt)
            {
                _nextWorldProbeAt = Time.unscaledTime + (shipWorldReady
                    ? WorldProbeReadyIntervalSeconds
                    : WorldProbeMissingShipIntervalSeconds);

                if (shipWorldReady)
                {
                    stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    EnsureWorldSystems();
                    RecordSlowTickPerfStep(5, stepStart);
                }
            }

            RecordSlowTickPerfPass(passStart);
        }

        private static double SlowTickElapsedMilliseconds(long startTicks)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
        }

        private void RecordSlowTickPerfStep(int index, long startTicks)
        {
            if (index < 0 || index >= _slowTickPerfStepTotals.Length)
                return;

            double elapsed = SlowTickElapsedMilliseconds(startTicks);
            _slowTickPerfStepTotals[index] += elapsed;
            if (elapsed > _slowTickPerfStepMaxima[index])
                _slowTickPerfStepMaxima[index] = elapsed;
        }

        private void RecordSlowTickPerfPass(long passStart)
        {
            float now = Time.unscaledTime;
            if (_slowTickPerfWindowStartedAt < 0f)
            {
                _slowTickPerfWindowStartedAt = now;
                _nextSlowTickPerfReportAt = now + SlowTickPerfReportIntervalSeconds;
            }

            double passMilliseconds = SlowTickElapsedMilliseconds(passStart);
            _slowTickPerfTotalMilliseconds += passMilliseconds;
            if (passMilliseconds > _slowTickPerfMaxPassMilliseconds)
                _slowTickPerfMaxPassMilliseconds = passMilliseconds;
            _slowTickPerfCalls++;
            if (now < _nextSlowTickPerfReportAt)
                return;

            double windowSeconds = Math.Max(0.001d, now - _slowTickPerfWindowStartedAt);
            // #951 S11H U3 (d): a quiet window closes without a line. Everything below is the same
            // report it always was, with the same keys in the same order.
            if (_slowTickPerfTotalMilliseconds / windowSeconds <= SlowTickPerfReportRateMillisecondsPerSecond
                && _slowTickPerfMaxPassMilliseconds <= SlowTickPerfReportSingleCallMilliseconds)
            {
                ResetSlowTickPerfWindow(now);
                return;
            }

            int dominantIndex = 0;
            for (int i = 1; i < _slowTickPerfStepTotals.Length; i++)
            {
                if (_slowTickPerfStepTotals[i] > _slowTickPerfStepTotals[dominantIndex])
                    dominantIndex = i;
            }

            ShipSystemsBootstrap.Log.LogWarning(
                $"[LGUShipSystems.Perf] RunSlowTick window={windowSeconds:0.0}s calls={_slowTickPerfCalls} " +
                $"total={_slowTickPerfTotalMilliseconds:0.00}ms " +
                $"rate={_slowTickPerfTotalMilliseconds / windowSeconds:0.00}ms/s " +
                $"average={(_slowTickPerfCalls > 0 ? _slowTickPerfTotalMilliseconds / _slowTickPerfCalls : 0d):0.000}ms/call " +
                $"dominant={SlowTickPerfStepNames[dominantIndex]}:" +
                $"{_slowTickPerfStepTotals[dominantIndex]:0.00}ms total/" +
                $"{_slowTickPerfStepMaxima[dominantIndex]:0.00}ms max " +
                $"stableWorldEnsureCallsSuppressed={_stableWorldEnsureCallsSuppressed}.");

            ResetSlowTickPerfWindow(now);
        }

        // #951 S11H U3 (d): one window reset, used by the report and by the silent close above, so
        // a quiet window cannot leave a counter behind for the next one to attribute to itself.
        private void ResetSlowTickPerfWindow(float now)
        {
            Array.Clear(_slowTickPerfStepTotals, 0, _slowTickPerfStepTotals.Length);
            Array.Clear(_slowTickPerfStepMaxima, 0, _slowTickPerfStepMaxima.Length);
            _slowTickPerfTotalMilliseconds = 0d;
            _slowTickPerfMaxPassMilliseconds = 0d;
            _slowTickPerfCalls = 0;
            _stableWorldEnsureCallsSuppressed = 0;
            _slowTickPerfWindowStartedAt = now;
            _nextSlowTickPerfReportAt = now + SlowTickPerfReportIntervalSeconds;
        }

        // #666: the external/primary takeover and quota-presentation scopes are Core state
        // (ShipPresentationScopes); the vote/upgrade machinery that armed requisitions from
        // quota events lives in Y4NGZTerminalUpgrades' ShipCommandController.
        private static bool ExternalMonitorTakeoverActive =>
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.ExternalTakeoverActive;

        private static bool PrimaryMonitorTakeoverActiveScope =>
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.PrimaryTakeoverActive;

        private void MarkPowerDisplayDirty()
        {
            Y4NGZCore.Modules.ShipSystems.ShipStatusSignals.PublishStatusDisplayDirty();
        }

        internal void OnProfitQuotaRolledOver()
        {
            ResetFuelForQuota();
            LogVerbose("Profit quota rolled over; ship fuel refilled.");
        }

        internal void OnShipReadyToLand()
        {
            // Starting a flight must not repair damage inflicted in orbit.
            if (!_batteryBroken) _power = GetPowerCapacity();
            ResetRouteAuthorizationStateForOrbit();
            FuelSourceSpawner.ClearRoundSource();
            _fuelChuteOpen = false;
            _fuelChuteIdleCloseAt = 0f;
            BroadcastPowerState();
            BroadcastFuelState();
        }

        internal void OnShipLeaving()
        {
            FuelSourceSpawner.ClearRoundSource();
            // The station remains part of the ship during flight.
        }

        internal void OnDisconnected()
        {
            _lastBatteryFeedbackAt = 0f;
            _fuelInitialized = false;
            // The controller is DontDestroyOnLoad, so the replicated latch would carry a
            // previous host's setting into the next session. Drop it back to the local
            // config until a new host's MsgFuelState arrives.
            _fuelSystemEnabled = true;
            _fuelSystemEnabledReplicated = false;
            _fuel = 0f;
            // Pending unlanded route fuel is session-only. Because visible fuel is
            // not debited until landing, disconnect/reload safely drops the hold.
            ClearReservedFuelRoute(refundFuel: false, broadcast: false);
            _currentFuelConstellationKey = string.Empty;
            ClearPendingRouteFuelAuthorization();
            ShipFuelSourceAssignments.Clear();
            FuelRouteConstellationIndexCache.Clear();
            FuelSourceSpawner.ClearRoundSource();
            _fuelChuteOpen = false;
            _fuelChuteIdleCloseAt = 0f;
            StopBatteryFailureSequences();
            SetShipAlarmAnimation(active: false);
            UnregisterNetworkHandlers();
            ResetRunState(fullPower: true);
            _batteryStation?.Destroy();
        }

        private void ResetRouteAuthorizationStateForOrbit()
        {
            StartOfRound start = StartOfRound.Instance;
            bool clearedStartFlags = false;
            if (start != null)
            {
                if (start.travellingToNewLevel)
                {
                    start.travellingToNewLevel = false;
                    clearedStartFlags = true;
                }

                if (start.beganLoadingNewLevel)
                {
                    start.beganLoadingNewLevel = false;
                    clearedStartFlags = true;
                }
            }

            RoundManager round = RoundManager.Instance;
            bool clearedDungeonFlag = false;
            if (round != null && round.dungeonIsGenerating)
            {
                round.dungeonIsGenerating = false;
                clearedDungeonFlag = true;
            }

            bool hadReservation = _reservedRouteFuel > 0f || !string.IsNullOrEmpty(_reservedFuelRouteKey);
            bool hadPendingAuthorization = _pendingRouteFuelAuthorization ||
                _pendingRouteFuelRouteIndex >= 0 ||
                !string.IsNullOrEmpty(_pendingRouteFuelRouteKey);

            ClearReservedFuelRoute(refundFuel: false, broadcast: false);
            ClearPendingRouteFuelAuthorization();
            ResetTerminalRouteCooldown();

            if (clearedStartFlags || clearedDungeonFlag || hadReservation || hadPendingAuthorization)
            {
                ShipSystemsBootstrap.Log.LogInfo(
                    "[LGUShipSystems.Fuel] Cleared stale route state at orbit: " +
                    $"startFlags={clearedStartFlags}, dungeon={clearedDungeonFlag}, " +
                    $"reservation={hadReservation}, pending={hadPendingAuthorization}.");
            }
        }

        internal bool ShouldBlockShipDeparture()
        {
            // #666: the requisition-vote hold moved to the command plugin, which applies its
            // own departure patch. This side holds the ship only for a dead battery.
            return _batteryBroken || !_shipPowerOnline;
        }

        internal string GetShipLockReason()
        {
            if (_batteryBroken || !_shipPowerOnline)
                return "Ship power is offline. Dock the apparatus into the exterior battery socket.";
            return "Ship systems are not cleared.";
        }

        /// <summary>#666: upgrade levels live with the command plugin; read through Core.</summary>
        private static bool HasUpgradeLevel(string upgradeKey)
        {
            return Y4NGZCore.Modules.ShipSystems.ShipUpgradeState.Has(upgradeKey);
        }

        // Compatibility gate for older consumers; shared energy costs are retired.
        internal bool TrySpendPower(float amount, string reason) => _shipPowerOnline && !_batteryBroken;

        internal bool TryReserveFuelForRoute(ShipFuelRouteRequest request, int[] constellationRouteIndexes, out ShipFuelQuote quote)
        {
            if (!FuelSystemEnabled)
            {
                quote = DisabledFuelQuote();
                return true;
            }

            EnsureFuelInitialized();
            quote = GetFuelQuote(request, constellationRouteIndexes);
            if (string.Equals(_reservedFuelRouteKey, request.RouteKey, StringComparison.Ordinal) &&
                Mathf.Abs(_reservedRouteFuel - quote.TotalFuelCost) <= 0.001f)
            {
                return quote.IsAffordable;
            }

            ClearReservedFuelRoute(refundFuel: false, broadcast: false);
            quote = GetFuelQuote(request, constellationRouteIndexes);
            if (!quote.IsAffordable)
            {
                BroadcastFuelState();
                return false;
            }

            _reservedRouteFuel = Mathf.Max(0f, quote.TotalFuelCost);
            _reservedFuelRoute = request;
            _reservedFuelRouteKey = request.RouteKey;
            MarkPowerDisplayDirty();
            BroadcastFuelState();
            LogVerbose($"Route fuel {_reservedRouteFuel:0.0} pending landing for route {request.MoonName}. Visible fuel={_fuel:0.0}.");
            return true;
        }

        internal bool CommitReservedFuelRoute(ShipFuelRouteRequest request)
        {
            EnsureFuelInitialized();
            bool matchedReservation = string.Equals(_reservedFuelRouteKey, request.RouteKey, StringComparison.Ordinal);
            if (matchedReservation)
            {
                BroadcastFuelState();
                return true;
            }

            return false;
        }

        internal void CancelReservedFuelRoute()
        {
            ClearReservedFuelRoute(refundFuel: false, broadcast: true);
        }

        internal float AddFuel(float amount, string source)
        {
            if (!FuelSystemEnabled)
                return 0f;

            EnsureFuelInitialized();
            if (amount <= 0f)
                return 0f;

            float before = _fuel;
            _fuel = Mathf.Clamp(_fuel + amount, 0f, FuelCapacity);
            float added = _fuel - before;
            if (added > 0f)
            {
                MarkPowerDisplayDirty();
                BroadcastFuelState();
                LogVerbose($"Added {added:0.0} fuel from {source}. Fuel={_fuel:0.0}/{FuelCapacity:0.0}.");
            }

            return added;
        }

        internal bool CurrentLevelHasFuelSource() => false; // Oil rigs are disabled.

        internal bool CommitReservedFuelRouteIndex(int routeIndex)
        {
            EnsureFuelInitialized();
            if (_reservedFuelRouteKey.Length == 0 || _reservedFuelRoute.RouteIndex != routeIndex)
                return false;

            return CommitReservedFuelRoute(_reservedFuelRoute);
        }

        internal bool TryAuthorizeRouteChangeFromServer(int routeIndex, out string denialText)
        {
            denialText = string.Empty;
            if (!IsServer() || !FuelSystemEnabled)
                return true;

            if (!TryBuildServerRouteRequest(routeIndex, out ShipFuelRouteRequest request, out int[] constellationRouteIndexes))
                return true;

            StartOfRound start = StartOfRound.Instance;
            if (start != null && start.currentLevelID == routeIndex)
                return true;

            if (!TryReserveFuelForRoute(request, constellationRouteIndexes, out ShipFuelQuote quote))
            {
                denialText = quote.DenialText;
                ResetTerminalRouteCooldown();
                BroadcastFuelRouteDenied(denialText);
                return false;
            }

            _pendingRouteFuelAuthorization = true;
            _pendingRouteFuelRouteIndex = routeIndex;
            _pendingRouteFuelPreviousLevelIndex = start != null ? start.currentLevelID : -1;
            _pendingRouteFuelRouteKey = request.RouteKey;
            return true;
        }

        internal void CompleteAuthorizedRouteChangeFromServer(int routeIndex, bool originalRan)
        {
            if (!IsServer() || !_pendingRouteFuelAuthorization || _pendingRouteFuelRouteIndex != routeIndex)
                return;

            bool accepted = originalRan &&
                string.Equals(_reservedFuelRouteKey, _pendingRouteFuelRouteKey, StringComparison.Ordinal) &&
                RouteRpcStartedTravel(routeIndex);
            if (accepted)
            {
                CommitReservedFuelRoute(_reservedFuelRoute);
                ShipSystemsBootstrap.Log.LogInfo($"[LGUShipSystems.Fuel] Route fuel pending landing for route {routeIndex}: charge={_reservedRouteFuel:0.0}, visible={_fuel:0.0}/{FuelCapacity:0.0}.");
            }
            else
            {
                ClearReservedFuelRoute(refundFuel: false, broadcast: true);
                ResetTerminalRouteCooldown();
                ShipSystemsBootstrap.Log.LogInfo($"[LGUShipSystems.Fuel] Dropped pending route fuel for route {routeIndex}; vanilla route change was not accepted.");
            }

            ClearPendingRouteFuelAuthorization();
        }

        private bool RouteRpcStartedTravel(int routeIndex)
        {
            StartOfRound start = StartOfRound.Instance;
            if (start == null)
                return false;

            if (start.travellingToNewLevel)
                return true;

            return start.currentLevelID == routeIndex &&
                _pendingRouteFuelPreviousLevelIndex != routeIndex;
        }

        internal void BroadcastFuelChuteFeedback(int added, int requested)
        {
            if (!IsServer())
                return;

            // Play host presentation directly; network delivery is for observers.
            _fuelChute?.PlaySwallowLocal(added, requested);
            using (FastBufferWriter writer = new FastBufferWriter(sizeof(int) * 2, Allocator.Temp))
            {
                writer.WriteValueSafe(added);
                writer.WriteValueSafe(requested);
                SendToAll(MsgFuelChuteFeedback, writer);
            }
        }

        internal void RequestFuelChuteToggle()
        {
            if (!FuelSystemEnabled)
                return;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer)
            {
                SetFuelChuteOpen(!FuelChuteOpen, broadcast: nm != null && nm.IsListening, resetIdleTimer: true);
                return;
            }

            using (FastBufferWriter writer = new FastBufferWriter(1, Allocator.Temp))
            {
                SendToServer(MsgFuelChuteToggleRequest, writer);
            }
        }

        internal void NotifyFuelChuteFuelDeposited()
        {
            if (!IsServer() || !_fuelChuteOpen)
                return;

            _fuelChuteIdleCloseAt = Time.realtimeSinceStartup + FuelChuteController.IdleCloseSeconds;
            BroadcastFuelChuteState();
        }

        private void ServerTickFuelChute()
        {
            // Still ticked while fuel is disabled: a hatch restored open from an older
            // save has to be closed rather than left swallowing items for no fuel.
            if (_fuelChuteOpen && !FuelSystemEnabled)
            {
                SetFuelChuteOpen(false, broadcast: true, resetIdleTimer: false);
                return;
            }

            if (!_fuelChuteOpen || _fuelChuteIdleCloseAt <= 0f)
                return;

            if (Time.realtimeSinceStartup >= _fuelChuteIdleCloseAt)
                SetFuelChuteOpen(false, broadcast: true, resetIdleTimer: false);
        }

        private void SetFuelChuteOpen(bool open, bool broadcast, bool resetIdleTimer)
        {
            // A chute left over from a save made with fuel enabled must go inert, not
            // stay a hatch that swallows items for zero fuel.
            if (open && !FuelSystemEnabled)
                open = false;

            if (open)
                _fuelChuteIdleCloseAt = Time.realtimeSinceStartup + FuelChuteController.IdleCloseSeconds;
            else
                _fuelChuteIdleCloseAt = 0f;

            bool changed = _fuelChuteOpen != open;
            _fuelChuteOpen = open;
            _fuelChute?.SetOpenLocal(open, immediate: false);

            if (broadcast && (changed || resetIdleTimer))
                BroadcastFuelChuteState();
        }

        private void BroadcastFuelChuteState()
        {
            using (FastBufferWriter writer = new FastBufferWriter(1, Allocator.Temp))
            {
                writer.WriteValueSafe(_fuelChuteOpen);
                SendToAll(MsgFuelChuteState, writer);
            }
        }
        private ShipFuelQuote DisabledFuelQuote()
        {
            return new ShipFuelQuote(
                fuelSystemAvailable: false,
                isAffordable: true,
                isCurrentConstellation: true,
                hasFuelSource: false,
                fuelAvailable: FuelCapacity,
                fuelCapacity: FuelCapacity,
                reservedFuel: 0f,
                entryFuelCost: 0f,
                moonFuelCost: 0f,
                routeKey: string.Empty,
                constellationKey: string.Empty,
                constellationName: string.Empty,
                denialText: string.Empty);
        }

        internal ShipFuelQuote GetServerRouteQuote(int routeIndex)
        {
            if (!FuelSystemEnabled)
                return DisabledFuelQuote();

            return TryBuildServerRouteRequest(routeIndex, out ShipFuelRouteRequest request, out int[] constellationRouteIndexes)
                ? GetFuelQuote(request, constellationRouteIndexes)
                : new ShipFuelQuote(
                    fuelSystemAvailable: false,
                    isAffordable: true,
                    isCurrentConstellation: true,
                    hasFuelSource: false,
                    fuelAvailable: FuelCapacity,
                    fuelCapacity: FuelCapacity,
                    reservedFuel: 0f,
                    entryFuelCost: 0f,
                    moonFuelCost: 0f,
                    routeKey: string.Empty,
                    constellationKey: string.Empty,
                    constellationName: string.Empty,
                    denialText: string.Empty);
        }

        private static SelectableLevel ResolveSelectableLevel(SelectableLevel[] levels, int routeIndex)
        {
            if (levels == null)
                return null;
            if (routeIndex >= 0 && routeIndex < levels.Length)
                return levels[routeIndex];
            for (int i = 0; i < levels.Length; i++)
            {
                SelectableLevel level = levels[i];
                if (level != null && level.levelID == routeIndex)
                    return level;
            }

            return null;
        }

        private void EnsureFuelInitialized()
        {
            if (_fuelInitialized)
                return;

            _fuelInitialized = true;
            _fuel = FuelCapacity;
            _reservedRouteFuel = 0f;
            _reservedFuelRoute = default;
            _reservedFuelRouteKey = string.Empty;
            _currentFuelConstellationKey = string.Empty;
            MarkPowerDisplayDirty();
        }

        private void ResetFuelForQuota()
        {
            _fuelInitialized = true;
            _fuel = FuelCapacity;
            ClearReservedFuelRoute(refundFuel: false, broadcast: false);
            _currentFuelConstellationKey = string.Empty;
            ShipFuelSourceAssignments.Clear();
            FuelRouteConstellationIndexCache.Clear();
            MarkPowerDisplayDirty();
            BroadcastFuelState();
            LogVerbose("Quota completed; ship fuel refilled.");
        }

        private void ClearReservedFuelRoute(bool refundFuel, bool broadcast)
        {
            // Reservations are bookkeeping holds only. Fuel is charged by
            // ApplyReservedRouteFuelChargeOnLanding, so cancellation never refunds.
            _reservedRouteFuel = 0f;
            _reservedFuelRoute = default;
            _reservedFuelRouteKey = string.Empty;

            if (broadcast)
            {
                MarkPowerDisplayDirty();
                BroadcastFuelState();
            }
        }

        private void ClearPendingRouteFuelAuthorization()
        {
            _pendingRouteFuelAuthorization = false;
            _pendingRouteFuelRouteIndex = -1;
            _pendingRouteFuelPreviousLevelIndex = -1;
            _pendingRouteFuelRouteKey = string.Empty;
        }

        private static void ResetTerminalRouteCooldown()
        {
            Terminal terminal = FindObjectOfType<Terminal>();
            if (terminal != null)
                terminal.useCreditsCooldown = false;
        }

        private int GetFuelGenerationSeed()
        {
            unchecked
            {
                int seed = 7103;
                if (TimeOfDay.Instance != null)
                    seed = (seed * 397) ^ TimeOfDay.Instance.timesFulfilledQuota;
                return seed;
            }
        }

        internal bool TrySpendTurretShotPower() => _shipPowerOnline && !_batteryBroken;
        internal void PredictAcceptedTurretShotPowerSpend() { }

        internal bool IsSabotageEligible()
        {
            return true;
        }

        internal bool IsBatterySabotageAvailable()
        {
            return IsLandedOnMoon() && IsSabotageEligible() && !_batteryBroken && _shipPowerOnline;
        }

        internal bool TryGetBatterySocketPosition(out Vector3 position)
        {
            position = Vector3.zero;
            return _batteryStation != null && _batteryStation.TryGetSocketWorldPosition(out position);
        }

        internal bool TryGetBatterySabotagePosition(out Vector3 position)
        {
            position = Vector3.zero;
            return _batteryStation != null && _batteryStation.TryGetSabotageWorldPosition(out position);
        }

        internal void StartBatteryDebugPlacement(string layoutName)
        {
            EnsureBatteryStationForDebug()?.StartDebugPlacement(layoutName);
        }

        internal void ShowBatteryDebugPreview()
        {
            EnsureBatteryStationForDebug()?.ShowDebugPreview();
        }

        internal void ClearBatteryDebugPreview()
        {
            EnsureBatteryStationForDebug()?.ClearDebugPreview();
        }

        internal string GetBatteryDebugStatus()
        {
            ExternalBatteryStation station = EnsureBatteryStationForDebug();
            return station != null ? station.GetDebugStatus() : "battery station unavailable";
        }

        internal void RotateBatteryDebugPlacement(float xDegrees, float yDegrees, float zDegrees)
        {
            EnsureBatteryStationForDebug()?.RotateDebugPlacement(xDegrees, yDegrees, zDegrees);
        }

        internal void SetBatteryDebugPlacementScale(float uniformScale)
        {
            EnsureBatteryStationForDebug()?.SetDebugPlacementScale(uniformScale);
        }

        private ExternalBatteryStation EnsureBatteryStationForDebug()
        {
            if (_batteryStation == null)
                _batteryStation = new ExternalBatteryStation(this);

            _batteryStation.Ensure();
            return _batteryStation;
        }

        internal void PlayBatterySabotageFeedback(Vector3 sourcePosition, int hitCount, bool critical = false)
        {
            if (IsServer())
                BroadcastAbilityEvent(critical ? AbilityBatterySabotageHitCritical : AbilityBatterySabotageHit);

            PlayBatterySabotageFeedbackLocal(sourcePosition, hitCount, critical);
        }

        private void PlayBatterySabotageFeedbackLocal(Vector3 sourcePosition, int hitCount, bool critical = false)
        {
            if (_batteryBroken || !_shipPowerOnline)
                return;
            if (Time.unscaledTime - _lastBatteryFeedbackAt < 0.2f)
                return;

            _lastBatteryFeedbackAt = Time.unscaledTime;
            EnsureBatteryAudioClips();
            int clampedHits = Mathf.Clamp(hitCount, 1, 6);
            float thudVolume = Mathf.Clamp(0.65f + clampedHits * 0.08f, 0.7f, 1f);
            PlayShipAudibleOneShot(_batteryThudClip, thudVolume);
            PlayWorldOneShot(_batteryThudClip, sourcePosition, thudVolume, 45f);
            // Layer the user's monster-impact clip on top of the electric hit.
            PlayShipAudibleOneShot(_batteryHitMonsterClip, thudVolume * 0.9f);
            PlayWorldOneShot(_batteryHitMonsterClip, sourcePosition, thudVolume, 45f);
            _batteryStation?.DuckWorkingLoop(1f);
            _batteryStation?.PlayLocalSaboteurSwing(sourcePosition);

            _batteryWarnings.Pulse(critical);

            // No klaxon / ship-alarm animation per hit: AlarmRinging is the
            // vanilla ship-takeover alarm and reads completely wrong here
            // (round-4 feedback). The break sequence still rings it briefly.
        }

        internal void BreakBattery(string reason)
        {
            if (_batteryBroken) return;

            _batteryBroken = true;
            _restoreObjectiveActive = true;
            MarkPowerDisplayDirty();
            EnsureRestoreItemAvailabilityForEmergency();
            _batteryStation?.ShowBrokenState();
            BroadcastPowerState();
            BroadcastAbilityEvent(AbilityBatteryFailureSequence);
            StartBatteryFailureLocalEffects();

            if (_batteryFailureServerCoroutine != null)
                StopCoroutine(_batteryFailureServerCoroutine);
            _batteryFailureServerCoroutine = StartCoroutine(CompleteBatteryBreakAfterSequence(reason));

            ShipSystemsBootstrap.Log.LogInfo($"[LGUShipSystems] Battery broken: {reason}");
            BatterySabotageDirector.ClearAllBatteryTargets();
            BatterySabotageDirector.CleanupDeadEnemies();
        }

        private IEnumerator CompleteBatteryBreakAfterSequence(string reason)
        {
            yield return new WaitForSecondsRealtime(1.45f);

            SetPowerOffline(reason);
            ForceHangarDoorOpenServer();
            ApplyEmergencyProtocol(active: true);
            BroadcastPowerState();
            BroadcastAbilityEvent(AbilityBatteryBroken);
            ForceHangarDoorOpenLocal();
            _batteryFailureServerCoroutine = null;
        }

        private static void ForceHangarDoorOpenServer()
        {
            try
            {
                StartOfRound sor = StartOfRound.Instance;
                if (sor != null && sor.hangarDoorsClosed)
                    sor.SetShipDoorsClosed(closed: false);
            }
            catch (Exception ex)
            {
                LogVerbose($"Forced hangar door state change failed: {ex.Message}");
            }
        }

        private static void ForceHangarDoorOpenLocal()
        {
            try
            {
                // Bypass HangarShipDoor.PlayDoorAnimation because emergency
                // protocol disables the door buttons before this runs.
                HangarShipDoor hangarDoor = FindObjectOfType<HangarShipDoor>();
                if (hangarDoor != null && hangarDoor.shipDoorsAnimator != null)
                    hangarDoor.shipDoorsAnimator.SetBool("Closed", false);
            }
            catch (Exception ex)
            {
                LogVerbose($"Forced hangar door animation failed: {ex.Message}");
            }
        }

        private void EnsureRestoreItemAvailabilityForEmergency()
        {
            if (!IsServer())
                return;

            // Entering the outage: scan the live world, not a snapshot from before it.
            if (HasAvailableApparatusInWorld(forceRefresh: true))
            {
                _restoreUsesSpareBattery = false;
                return;
            }

            _restoreUsesSpareBattery = true;
            TrySpawnSpareBatteryFallback(forceRefresh: true);
        }

        private bool TrySpawnSpareBatteryFallback(bool forceRefresh)
        {
            if (!IsServer())
                return false;

            if (TryFindExistingSpareBattery(out NetworkObject existing, forceRefresh))
            {
                _spawnedSpareBatteryNetworkObject = existing;
                _spareBatterySpawnedForEmergency = true;
                return true;
            }

            if (!TryGetSpareBatterySpawnPoint(out Vector3 position, out Quaternion rotation))
            {
                LogVerbose("Spare battery fallback spawn delayed; no interior node was available yet.");
                return false;
            }

            bool spawned = SpareBatteryItemRegistry.TrySpawn(
                position,
                rotation,
                StartOfRound.Instance != null ? StartOfRound.Instance.propsContainer : null,
                out NetworkObject spawnedObject);
            if (!spawned)
                return false;

            _spawnedSpareBatteryNetworkObject = spawnedObject;
            _spareBatterySpawnedForEmergency = true;
            if (_batteryBroken)
            {
                BroadcastPowerState();
            }
            return true;
        }

        internal void EnsureInteriorSpareBatteryForRound()
        {
            if (!IsServer() || !IsLandedOnMoon() || IsCompanyMoon())
                return;

            if (HasAvailableApparatusInWorld(forceRefresh: true))
                return;

            if (TryFindExistingSpareBattery(out NetworkObject existing, forceRefresh: false))
            {
                _spawnedSpareBatteryNetworkObject = existing;
                return;
            }

            if (TrySpawnSpareBatteryFallback(forceRefresh: false))
                ShipSystemsBootstrap.Log.LogInfo("[LGUShipSystems] No apparatus on this interior; guaranteed spare battery spawned inside.");
        }

        private bool TryGetSpareBatterySpawnPoint(out Vector3 position, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            position = Vector3.zero;
            var ship = StartOfRound.Instance;
            // Orbit and the Company have no facility apparatus. Keep failures repairable.
            if (ship != null && ship.elevatorTransform != null && (!IsLandedOnMoon() || IsCompanyMoon()))
            {
                Vector3 point = ship.middleOfShipNode != null ? ship.middleOfShipNode.position : ship.elevatorTransform.TransformPoint(new Vector3(0f,1f,-3f));
                position = point + Vector3.up*.25f;
                if (Physics.Raycast(position,Vector3.down,out RaycastHit floor,3f,268437760,QueryTriggerInteraction.Ignore))
                    position=floor.point+Vector3.up*.35f;
                rotation=ship.elevatorTransform.rotation;
                return true;
            }

            GameObject[] nodes = RoundManager.Instance != null ? RoundManager.Instance.insideAINodes : null;
            if (nodes == null || nodes.Length == 0)
                nodes = GameObject.FindGameObjectsWithTag("AINode");
            if (nodes == null || nodes.Length == 0)
                return false;

            Vector3 shipPosition = StartOfRound.Instance != null && StartOfRound.Instance.elevatorTransform != null
                ? StartOfRound.Instance.elevatorTransform.position
                : Vector3.zero;

            GameObject best = null;
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < nodes.Length; i++)
            {
                GameObject node = nodes[i];
                if (node == null || !node.activeInHierarchy)
                    continue;

                float distance = Vector3.Distance(node.transform.position, shipPosition);
                float score = distance + Mathf.Abs(Mathf.Sin((i + 1) * 12.9898f)) * 6f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = node;
                }
            }

            if (best == null)
                return false;

            Vector3 raw = best.transform.position + Vector3.up * 0.35f;
            if (RoundManager.Instance != null)
            {
                Vector3 sampled = RoundManager.Instance.GetNavMeshPosition(raw, default(UnityEngine.AI.NavMeshHit), 4f);
                if (RoundManager.Instance.GotNavMeshPositionResult)
                    raw = sampled + Vector3.up * 0.35f;
            }

            position = raw;
            rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            return true;
        }

        /// <summary>
        /// Snapshot of every <see cref="GrabbableObject"/> in the world, shared by the two
        /// emergency-restore scans. <paramref name="forceRefresh"/> is for the one-shot entry
        /// points that must see the live world; the 2s outage tick reuses the snapshot until
        /// <see cref="OutageItemScanLifetimeSeconds"/> has passed (#716 D2).
        /// </summary>
        private static GrabbableObject[] GetRestoreItemCandidates(bool forceRefresh)
        {
            if (!forceRefresh && Time.realtimeSinceStartup < _outageItemScanExpiresAt)
                return _outageItemScan;

            _outageItemScan = FindObjectsOfType<GrabbableObject>(includeInactive: true) ?? Array.Empty<GrabbableObject>();
            _outageItemScanExpiresAt = Time.realtimeSinceStartup + OutageItemScanLifetimeSeconds;
            return _outageItemScan;
        }

        private static void InvalidateRestoreItemCandidates()
        {
            _outageItemScan = Array.Empty<GrabbableObject>();
            _outageItemScanExpiresAt = float.NegativeInfinity;
        }

        private static bool HasAvailableApparatusInWorld(bool forceRefresh)
        {
            GrabbableObject[] objects = GetRestoreItemCandidates(forceRefresh);
            if (objects == null)
                return false;

            for (int i = 0; i < objects.Length; i++)
            {
                GrabbableObject item = objects[i];
                if (item == null || item.gameObject == SpareBatteryItemRegistry.Prefab)
                    continue;
                if (!item.gameObject.scene.IsValid() || !item.gameObject.scene.isLoaded)
                    continue;
                if (IsApparatusObject(item))
                    return true;
            }

            return false;
        }

        private static bool TryFindExistingSpareBattery(out NetworkObject networkObject, bool forceRefresh)
        {
            networkObject = null;
            GrabbableObject[] objects = GetRestoreItemCandidates(forceRefresh);
            if (objects == null)
                return false;

            for (int i = 0; i < objects.Length; i++)
            {
                GrabbableObject item = objects[i];
                if (item == null || item.gameObject == SpareBatteryItemRegistry.Prefab)
                    continue;
                if (!item.gameObject.scene.IsValid() || !item.gameObject.scene.isLoaded)
                    continue;
                if (!SpareBatteryItemRegistry.IsSpareBatteryObject(item))
                    continue;

                networkObject = item.NetworkObject != null
                    ? item.NetworkObject
                    : item.GetComponent<NetworkObject>();
                return networkObject != null;
            }

            return false;
        }

        private static bool IsSpawnedObjectAlive(NetworkObject networkObject)
        {
            return networkObject != null && networkObject.IsSpawned && networkObject.gameObject != null;
        }

        internal void RequestBatteryRestoreFromLocalPlayer()
        {
            if (!_batteryBroken && _shipPowerOnline) return;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (player == null) return;

            if (!TryGetHeldRestoreItem(player, out GrabbableObject restoreItem)) return;

            bool usedApparatus = IsApparatusObject(restoreItem);
            if (IsServer())
            {
                ConsumeRestoreItem(player, restoreItem);
                RestoreBatteryPower(usedApparatus, player);
                return;
            }

            SendBatteryRestoreRequest(usedApparatus);
        }

        internal void RequestTechnicianBatteryRepairFromLocalPlayer()
        {
            if (!_batteryBroken) return;

            if (IsServer())
            {
                ServerPerformTechnicianRepair(GameNetworkManager.Instance?.localPlayerController);
                return;
            }

            using (FastBufferWriter writer = new FastBufferWriter(1, Allocator.Temp))
            {
                SendToServer(MsgTechnicianRepair, writer);
            }
        }

        private void ServerPerformTechnicianRepair(PlayerControllerB repairedBy = null)
        {
            if (!IsServer() || !_batteryBroken)
                return;

            Vector3 position;
            if (_batteryStation == null || !_batteryStation.TryGetBrokenDropPosition(out position))
            {
                if (!TryGetBatterySocketPosition(out position))
                    return;
            }

            bool spawned = SpareBatteryItemRegistry.TrySpawn(
                position + Vector3.up * 0.15f,
                Quaternion.identity,
                StartOfRound.Instance != null ? StartOfRound.Instance.propsContainer : null,
                out NetworkObject spawnedObject);
            if (!spawned)
            {
                ShipSystemsBootstrap.Log.LogWarning("[LGUShipSystems] Technician repair could not spawn the repaired battery item.");
                return;
            }

            _spawnedSpareBatteryNetworkObject = spawnedObject;
            _spareBatterySpawnedForEmergency = true;
            _restoreUsesSpareBattery = true;
            BroadcastPowerState();
            BroadcastAbilityEvent(AbilityBatteryDropRepaired);
            OnBatteryDropRepairedLocal();
            ShipSystemsBootstrap.Log.LogInfo("[LGUShipSystems] Technician repaired the broken battery; repaired cell spawned as an item.");

            // Bundy noise bus (3b follow-up). The technician held still through the whole repair
            // and is about to bend down for the cell, so the clank pins them tightly.
            ReportWorldNoise(EnvironmentalNoiseKinds.BatteryRepair, position, repairedBy);
        }

        private void OnBatteryDropRepairedLocal()
        {
            EnsureBatteryAudioClips();
            Vector3 position;
            if (_batteryStation != null && _batteryStation.TryGetBrokenDropPosition(out position))
                PlayWorldOneShot(_batteryRepairClip, position, 0.85f, 30f);
            _batteryStation?.OnDropRepairedLocal();
        }

        internal void NotifyBatteryCriticalFromServer()
        {
            if (!IsServer()) return;

            BroadcastAbilityEvent(AbilityBatteryCritical);
            PlayBatteryCriticalLocal();
        }

        private void PlayBatteryCriticalLocal()
        {
            // A late critical event must never fire once the local failure
            // sequence has started — client _batteryBroken can lag the events.
            if (_batteryBroken || !_shipPowerOnline || _batteryFailureLocalEffectsCoroutine != null)
                return;

            EnsureBatteryAudioClips();
            PlayShipAudibleOneShot(_batteryCriticalClip, 0.9f);
            _batteryWarnings.Pulse(true);
        }

        private void RestoreBatteryPower(bool usedApparatus, PlayerControllerB restoredBy = null)
        {
            _batteryBroken = false;
            _restoreObjectiveActive = false;
            _restoreUsesSpareBattery = false;
            _spareBatterySpawnedForEmergency = false;
            _spawnedSpareBatteryNetworkObject = null;
            _nextRestoreItemFallbackCheckAt = 0f;
            _shipPowerOnline = true;
            _power = Mathf.Max(_power, GetPowerCapacity() * 0.55f);
            MarkPowerDisplayDirty();
            _lastBatteryFeedbackAt = 0f;
            StopBatteryFailureSequences();
            SetShipAlarmAnimation(active: false);
            ApplyEmergencyProtocol(active: false);
            OnBatteryRestoredLocal(usedApparatus);
            BroadcastPowerState();
            BroadcastAbilityEvent(usedApparatus ? AbilityBatteryRestored : AbilityBatteryRestoredSpare);
            ShipSystemsBootstrap.Log.LogInfo($"[LGUShipSystems] Ship power restored by {(usedApparatus ? "apparatus" : "spare battery")}.");

            // Bundy noise bus (3b follow-up). The restore is the counterplay moment of his battery
            // siege -- the whole ship slams back to life, and the docker was just at the socket.
            // Only runs on the server, which is the only client whose bus acts anyway.
            if (TryGetBatterySocketPosition(out Vector3 restoreNoisePosition))
                ReportWorldNoise(EnvironmentalNoiseKinds.BatteryRestore, restoreNoisePosition, restoredBy);
            else if (restoredBy != null)
                ReportWorldNoise(EnvironmentalNoiseKinds.BatteryRestore, restoredBy.transform.position, restoredBy);
        }

        /// <summary>
        /// Publishes one world-noise report onto the Y4NGZCore bus (#611 task 1.3).
        ///
        /// <para>This used to call <c>BundyWorldNoisePatchSupport.Report</c> with a
        /// <c>BundyWorldNoise</c> enum value, which is a compile-time dependency on the
        /// escalation assembly <em>and</em> an ordinal-matched enum crossing a DLL boundary.
        /// Now the kind is a stable string and this side neither knows nor cares whether
        /// anything is listening — <see cref="EnvironmentalNoise.Report"/> is a documented
        /// no-op with no listener registered, so the call is unconditional and its return value
        /// is deliberately ignored.</para>
        ///
        /// <para>The client ID is <c>actualClientId</c>, not <c>playerClientId</c>: the two are
        /// different numbers that coincide only in single player, and the contract requires the
        /// transport ID so a listener can search for the right crew member.</para>
        /// </summary>
        private static void ReportWorldNoise(string noiseKind, Vector3 position, PlayerControllerB causedBy)
        {
            EnvironmentalNoise.Report(
                noiseKind,
                position.x,
                position.y,
                position.z,
                causedBy != null
                    ? causedBy.actualClientId
                    : EnvironmentalNoiseEvent.UnknownPlayerClientId);
        }

        private void OnBatteryRestoredLocal(bool usedApparatus)
        {
            EnsureBatteryAudioClips();
            if (usedApparatus)
                _batteryStation?.ShowDockedRestoreVisual();
            if (TryGetBatterySocketPosition(out Vector3 socket))
                PlayWorldOneShot(_batteryDockClip, socket, 0.9f, 30f);
            // No PowerRestored.mp3 was provided: fall back to the replacement
            // clip so the power-on moment is audible everywhere in the ship.
            PlayShipAudibleOneShot(_batteryRestoredClip != null ? _batteryRestoredClip : _batteryDockClip, 0.85f);
        }

        internal static bool TryGetHeldApparatus(PlayerControllerB player, out GrabbableObject apparatus)
        {
            return TryGetHeldItemMatching(player, IsApparatusObject, out apparatus);
        }

        internal static bool TryGetHeldSpareBattery(PlayerControllerB player, out GrabbableObject spareBattery)
        {
            return TryGetHeldItemMatching(player, SpareBatteryItemRegistry.IsSpareBatteryObject, out spareBattery);
        }

        internal bool TryGetHeldRestoreItem(PlayerControllerB player, out GrabbableObject restoreItem)
        {
            if (_restoreUsesSpareBattery)
                return TryGetHeldSpareBattery(player, out restoreItem);

            if (TryGetHeldApparatus(player, out restoreItem))
                return true;

            return TryGetHeldSpareBattery(player, out restoreItem);
        }

        internal bool IsAnyPlayerHoldingRestoreItem()
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return false;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || !player.isPlayerControlled || player.isPlayerDead)
                    continue;
                if (TryGetHeldRestoreItem(player, out _))
                    return true;
            }

            return false;
        }

        private static bool TryGetHeldItemMatching(
            PlayerControllerB player,
            Func<GrabbableObject, bool> predicate,
            out GrabbableObject item)
        {
            item = null;
            if (player == null || predicate == null)
                return false;

            GrabbableObject held = player.currentlyHeldObjectServer;
            if (predicate(held))
            {
                item = held;
                return true;
            }

            GrabbableObject[] slots = player.ItemSlots;
            if (slots == null || slots.Length == 0)
                return false;

            int selectedSlot = Mathf.Clamp(player.currentItemSlot, 0, slots.Length - 1);
            if (predicate(slots[selectedSlot]))
            {
                item = slots[selectedSlot];
                return true;
            }

            for (int i = 0; i < slots.Length; i++)
            {
                if (predicate(slots[i]))
                {
                    item = slots[i];
                    return true;
                }
            }

            return false;
        }

        private static bool IsApparatusObject(GrabbableObject item)
        {
            if (item == null)
                return false;

            if (item is LungProp)
                return true;

            string itemName = item.itemProperties != null ? item.itemProperties.itemName : item.name;
            if (string.IsNullOrWhiteSpace(itemName))
                return false;

            return itemName.IndexOf("apparatus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   itemName.IndexOf("lung", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ConsumeRestoreItem(PlayerControllerB player, GrabbableObject apparatus)
        {
            if (player == null || apparatus == null)
                return;

            try
            {
                if (player.currentlyHeldObjectServer == apparatus)
                {
                    player.DespawnHeldObject();
                    return;
                }

                apparatus.DestroyObjectInHand(player);
                NetworkObject networkObject = apparatus.NetworkObject != null
                    ? apparatus.NetworkObject
                    : apparatus.GetComponent<NetworkObject>();
                if (networkObject != null && networkObject.IsSpawned)
                    networkObject.Despawn(true);
                else if (apparatus.gameObject != null)
                    Destroy(apparatus.gameObject);
            }
            catch (Exception ex)
            {
                LogVerbose($"Could not consume apparatus: {ex.Message}");
            }
        }

        private static PlayerControllerB FindPlayerByClientId(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && (player.actualClientId == clientId || player.playerClientId == clientId))
                    return player;
            }

            return null;
        }

        private void ServerTickRestoreItemFallback()
        {
            if (!_restoreObjectiveActive || !_restoreUsesSpareBattery)
                return;
            if (_spareBatterySpawnedForEmergency && IsSpawnedObjectAlive(_spawnedSpareBatteryNetworkObject))
                return;
            if (Time.realtimeSinceStartup < _nextRestoreItemFallbackCheckAt)
                return;

            _nextRestoreItemFallbackCheckAt = Time.realtimeSinceStartup + 2f;
            // The 2s cadence stays, but it now rides the cached candidate list: the
            // FindObjectsOfType heap walk behind it happens at most every 10s (#716 D2).
            TrySpawnSpareBatteryFallback(forceRefresh: false);
        }

        private void ServerTickPeriodicState()
        {
            if (Time.realtimeSinceStartup < _nextStateBroadcastAt) return;
            _nextStateBroadcastAt = Time.realtimeSinceStartup + 2.5f;
            BroadcastPowerState();
            BroadcastFuelState();
        }

        private void ResetRunState(bool fullPower)
        {
            _batteryWarnings.Stop();
            _lastPlayerBatteryHit.Clear();
            // A new round rebuilds the ship world, so nothing we applied last
            // round is still on screen: forget the change-guard caches first so
            // the ApplyEmergencyProtocol(false) below always re-applies once.
            _appliedPowerVisualOnline = null;
            _appliedEmergencyProtocolActive = null;
            _lastReceivedPowerOnline = null;
            _lastReceivedBatteryBroken = null;
            _nextPowerDeltaBroadcastAt = 0f;
            _lastKnownPowerCapacity = GetPowerCapacity();
            _batteryBroken = false;
            _shipPowerOnline = true;
            _restoreObjectiveActive = false;
            _restoreUsesSpareBattery = false;
            _spareBatterySpawnedForEmergency = false;
            _spawnedSpareBatteryNetworkObject = null;
            _nextRestoreItemFallbackCheckAt = 0f;
            // The rebuilt world invalidates last round's item snapshot outright (#716 D2).
            InvalidateRestoreItemCandidates();
            _nextInteractionBlockTipAt = 0f;
            _lastBatteryFeedbackAt = 0f;
            StopBatteryFailureSequences();
            SetShipAlarmAnimation(active: false);
            if (fullPower || _power <= 0f)
                _power = GetPowerCapacity();
            MarkPowerDisplayDirty();
            ApplyEmergencyProtocol(active: false);
            BatterySabotageDirector.ClearAllBatteryTargets();
            _batteryStation?.ResetStation();
        }



        private void EnsureWorldSystems()
        {
            if (!IsShipWorldReady())
                return;


            if (_batteryStation == null)
                _batteryStation = new ExternalBatteryStation(this);
            _batteryStation.Ensure();

            EnsureFuelChute();

            // CCTV Terminal now unlocks access to the vanilla monitor station instead
            // of spawning a separate furniture monitor.
        }

        private void EnsureFuelChute()
        {
            if (!FuelSystemEnabled)
                return;
            if (_fuelChute != null || StartOfRound.Instance?.elevatorTransform == null)
                return;

            float fuelLevel01 = Fuel / Mathf.Max(1f, FuelCapacity);
            FuelChuteFurnitureRegistry.EnsureRegistered();
            if (FuelChuteFurnitureRegistry.TryEnsureSpawned(this, _fuelChuteOpen, fuelLevel01, out FuelChuteController furnitureChute))
            {
                _fuelChute = furnitureChute;
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.IsListening && !manager.IsServer)
                return;

            if (manager != null && manager.IsListening && manager.IsServer && manager.ConnectedClientsList != null && manager.ConnectedClientsList.Count > 1)
                ShipSystemsBootstrap.Log.LogError("[LGUShipSystems.Fuel] Fuel chute furniture spawn failed; using local factory fallback, but connected clients will not see the fuel chute. Probable vanilla signature drift in StartOfRound.SpawnUnlockable.");

            GameObject chute = FuelChuteControllerFactory.Create();
            chute.transform.SetParent(StartOfRound.Instance.elevatorTransform, false);
            chute.transform.localPosition = new Vector3(-2.65f, 0.02f, -4.1f);
            chute.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            _fuelChute = chute.GetComponent<FuelChuteController>();
            FuelChuteFurnitureRegistry.ConfigureSpawnedChute(chute, _fuelChute);
            _fuelChute?.Configure(this, _fuelChuteOpen, fuelLevel01);
        }

        private static bool IsShipWorldReady()
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || sor.elevatorTransform == null)
                return false;

            GameObject elevator = sor.elevatorTransform.gameObject;
            return elevator != null
                && elevator.scene.IsValid()
                && elevator.scene.isLoaded
                && elevator.activeInHierarchy;
        }

        private void SetPowerOffline(string reason)
        {
            _shipPowerOnline = false;
            _power = 0f;
            MarkPowerDisplayDirty();
            ApplyShipPowerVisualState(powerOnline: false);
        }

        internal bool TrySpendTerminalBootPower() => !IsLandedOnMoon() || !EmergencyProtocolActive;
        internal void OnShipDoorsToggled(bool closed, bool previousClosed) { }

        internal bool ShouldBlockShipInteraction(Transform target)
        {
            if (!EmergencyProtocolActive) return false;
            // Allow emergency landing/routing to reach an exterior repair socket.
            if (!IsLandedOnMoon() && target != null &&
                (target.GetComponentInParent<StartMatchLever>() != null || target.GetComponentInParent<Terminal>() != null))
                return false;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController;
            if (player == null || player.isPlayerDead || !player.isInHangarShipRoom)
                return false;

            if (target == null)
                return true;

            StartOfRound sor = StartOfRound.Instance;
            if (sor == null)
                return true;

            if (sor.shipInnerRoomBounds != null && sor.shipInnerRoomBounds.bounds.Contains(target.position))
                return true;

            if (sor.shipBounds != null && sor.shipBounds.bounds.Contains(target.position))
                return true;

            return sor.elevatorTransform != null && target.IsChildOf(sor.elevatorTransform);
        }

        internal void NotifyBlockedShipInteraction()
        {
            if (Time.unscaledTime < _nextInteractionBlockTipAt) return;
            _nextInteractionBlockTipAt = Time.unscaledTime + 4f;
        }

        // Task E: while the battery is critical every landed hit blips the ship
        // lights + monitors off for a beat on every client.
        private void StartCriticalHitFlicker()
        {
            if (_criticalHitFlickerCoroutine != null || _batteryFailureLocalEffectsCoroutine != null)
                return;

            _criticalHitFlickerCoroutine = StartCoroutine(RunCriticalHitFlicker());
        }

        private IEnumerator RunCriticalHitFlicker()
        {
            SetShipPowerVisualStateLocal(powerOnline: false);
            yield return new WaitForSecondsRealtime(0.22f);
            // Never flicker back on if power actually failed in the meantime.
            if (!_batteryBroken && _shipPowerOnline && _batteryFailureLocalEffectsCoroutine == null)
                SetShipPowerVisualStateLocal(powerOnline: true);
            _criticalHitFlickerCoroutine = null;
        }

        private void StartBatteryFailureLocalEffects()
        {
            if (_batteryFailureLocalEffectsCoroutine != null)
                return;

            _batteryFailureLocalEffectsCoroutine = StartCoroutine(RunBatteryFailureLocalEffects());
        }

        private void StopBatteryFailureSequences()
        {
            if (_batteryFailureServerCoroutine != null)
            {
                StopCoroutine(_batteryFailureServerCoroutine);
                _batteryFailureServerCoroutine = null;
            }

            if (_batteryFailureLocalEffectsCoroutine != null)
            {
                StopCoroutine(_batteryFailureLocalEffectsCoroutine);
                _batteryFailureLocalEffectsCoroutine = null;
            }
        }

        private IEnumerator RunBatteryFailureLocalEffects()
        {
            EnsureBatteryAudioClips();
            _batteryStation?.ShowBrokenState();
            // Physical monitors carry hit warnings; Contracted owns the failure objective.
            _batteryWarnings.Pulse(true);
            SetShipAlarmAnimation(active: true);
            PlayShipAudibleOneShot(_batteryAlarmClip, 0.9f);
            PlayShipAudibleOneShot(_batteryPowerDownClip != null ? _batteryPowerDownClip : _batteryFaultClip, 0.85f);
            TriggerBatteryFailureLightEffects();

            bool lightsOn = false;
            float[] flickerCadence = { 0.08f, 0.10f, 0.07f, 0.16f, 0.09f, 0.13f, 0.18f, 0.12f };
            for (int i = 0; i < flickerCadence.Length; i++)
            {
                SetShipPowerVisualStateLocal(lightsOn);
                lightsOn = !lightsOn;
                yield return new WaitForSecondsRealtime(flickerCadence[i]);
            }

            SetShipPowerVisualStateLocal(powerOnline: false);
            SetShipAlarmAnimation(active: false);
            _batteryFailureLocalEffectsCoroutine = null;
        }

        private void TriggerBatteryFailureLightEffects()
        {
            try
            {
                StartOfRound.Instance?.PowerSurgeShip();
            }
            catch (Exception ex)
            {
                LogVerbose($"Ship power surge effect skipped: {ex.Message}");
            }

            try
            {
                RoundManager round = RoundManager.Instance;
                if (round == null) return;

                if (round.allPoweredLightsAnimators == null || round.allPoweredLightsAnimators.Count == 0)
                    round.RefreshLightsList();

                round.FlickerLights(flickerFlashlights: true);
            }
            catch (Exception ex)
            {
                LogVerbose($"Powered light flicker skipped: {ex.Message}");
            }
        }

        private void SetShipPowerVisualStateLocal(bool powerOnline)
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null) return;

            if (DeferPowerVisualDuringTakeover(powerOnline)) return;

            try
            {
                ApplyPrimaryMapScreenPower(sor, powerOnline);
                sor.shipRoomLights?.SetShipLightsOnLocalClientOnly(setLightsOn: powerOnline);
                Y4NGZCore.Modules.ShipSystems.ShipStatusSignals.PublishPowerVisual(powerOnline);
                SetVanillaShipScreensPoweredLocal(powerOnline);
            }
            catch (Exception ex)
            {
                LogVerbose($"Local ship power visual sync failed: {ex.Message}");
            }
        }

        private static void SetShipAlarmAnimation(bool active)
        {
            try
            {
                NetworkObject alarmObject = StartOfRound.Instance?.shipAnimatorObject;
                Animator animator = alarmObject != null ? alarmObject.GetComponent<Animator>() : null;
                animator?.SetBool("AlarmRinging", active);
            }
            catch (Exception ex)
            {
                LogVerbose($"Ship alarm animation sync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts the objective chime loading before anything can need it. The
        /// clip comes off disk asynchronously, and the first completion of a run
        /// is the one moment where arriving late would be silent.
        /// </summary>
        internal void PrewarmObjectiveChime()
        {
            if (_objectiveChimeRequested)
                return;

            _objectiveChimeRequested = true;
            FacilityAudioClipLoader.Request(
                this,
                ObjectiveChimeClipFile,
                clip => _objectiveChimeClip = clip);
        }

        /// <summary>
        /// The objective-completion chime, ship speakers only. It shipped with a
        /// 2D UI copy so the crew member inside the facility would hear their
        /// own completion, and playtesting overturned that: a chime that follows
        /// players everywhere reads as an annoying UI beep, not a ship
        /// broadcast. Whoever is in the facility learns it from the row instead.
        /// </summary>
        internal void PlayObjectiveChime()
        {
            PrewarmObjectiveChime();
            PlayShipAudibleOneShot(_objectiveChimeClip, 0.85f, includeUiLayer: false);
        }

        private void EnsureBatteryAudioClips()
        {
            if (_batteryAlarmClip == null)
                _batteryAlarmClip = FindLoadedAlarmClip() ?? CreateKlaxonClip();
            if (_batteryThudClip == null)
                _batteryThudClip = CreateImpactThudClip();
            if (_batteryFaultClip == null)
                _batteryFaultClip = CreateElectricalFaultClip();

            if (!_batteryAudioLoadStarted)
            {
                _batteryAudioLoadStarted = true;
                StartCoroutine(LoadBatteryAudioClipFromDisk("BatteryHit.mp3", clip => _batteryThudClip = clip));
                StartCoroutine(LoadBatteryAudioClipFromDisk("ShipPowerDown.mp3", clip => _batteryPowerDownClip = clip));
                StartCoroutine(LoadBatteryAudioClipFromDisk("BatteryCritical.mp3", clip => _batteryCriticalClip = clip));
                StartCoroutine(LoadBatteryAudioClipFromDisk("PowerRestored.mp3", clip => _batteryRestoredClip = clip));
                StartCoroutine(LoadBatteryAudioClipFromDisk("BatteryRepair.mp3", clip => _batteryRepairClip = clip));
                StartCoroutine(LoadBatteryAudioClipFromDisk("BatteryDock.mp3", clip => _batteryDockClip = clip));
                StartCoroutine(LoadBatteryAudioClipFromDisk("WorkingBatteryLoop.mp3", clip => _batteryWorkingLoopClip = clip));
                StartCoroutine(LoadBatteryAudioClipFromDisk("BrokenBatteryLoop.mp3", clip => _batteryBrokenLoopClip = clip));
                StartCoroutine(LoadBatteryAudioClipFromDisk("BatteryHitMonster.mp3", clip => _batteryHitMonsterClip = clip));
            }
        }

        internal AudioClip BatteryWorkingLoopClip => _batteryWorkingLoopClip;
        internal AudioClip BatteryBrokenLoopClip => _batteryBrokenLoopClip;
        internal void RequestBatteryAudioClips() => EnsureBatteryAudioClips();

        private IEnumerator LoadBatteryAudioClipFromDisk(string fileName, Action<AudioClip> assign)
        {
            string path = ResolveBatteryAudioPath(fileName);
            if (string.IsNullOrEmpty(path))
            {
                ShipSystemsBootstrap.Log.LogInfo($"[LGUShipSystems] Battery audio missing: {fileName}; keeping procedural fallback.");
                yield break;
            }

            using (UnityEngine.Networking.UnityWebRequest request =
                UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? AudioType.WAV : AudioType.MPEG))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    ShipSystemsBootstrap.Log.LogInfo($"[LGUShipSystems] Failed loading battery audio '{path}': {request.error}");
                    yield break;
                }

                AudioClip clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                    yield break;

                clip.name = "LGUShipSystems_" + fileName;
                assign(clip);
            }
        }

        private static string ResolveBatteryAudioPath(string fileName)
        {
            string assemblyDir = System.IO.Path.GetDirectoryName(typeof(ShipSystemsBootstrap).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                !string.IsNullOrWhiteSpace(assemblyDir) ? System.IO.Path.Combine(assemblyDir, "Audio", fileName) : null,
                // #613 task 2.1: this feature ships in its own package now, so the plugin
                // directory holding the payload is named after it. Both Gale layouts are
                // listed for the same reason the deploy target lists both. The legacy
                // y4ngz-Y4NGZCompany entry below stays: it is what finds the payload a
                // pre-#613 install left behind, on a profile that has not redeployed.
                System.IO.Path.Combine(BepInEx.Paths.PluginPath, "Y4NGZShipSystems", "Audio", fileName),
                System.IO.Path.Combine(BepInEx.Paths.PluginPath, "y4ngz-Y4NGZShipSystems", "Audio", fileName),
                System.IO.Path.Combine(BepInEx.Paths.PluginPath, "y4ngz-Y4NGZCompany", "Audio", fileName),
                System.IO.Path.Combine(BepInEx.Paths.PluginPath, "Audio", fileName),
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (!string.IsNullOrWhiteSpace(candidate) && System.IO.File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static AudioClip FindLoadedAlarmClip()
        {
            try
            {
                string[] names =
                {
                    "ShipAlarmHornConstant",
                    "FireAlarm",
                    "DeadlineAlarm",
                    "0DaysLeftAlert",
                    "ShipAlarm",
                    "RedAlarmSFX",
                    "ShipAlarmSFX"
                };

                AudioClip[] clips = Resources.FindObjectsOfTypeAll<AudioClip>();
                for (int n = 0; n < names.Length; n++)
                {
                    for (int i = 0; i < clips.Length; i++)
                    {
                        AudioClip clip = clips[i];
                        if (clip != null &&
                            clip.length >= 1.0f &&
                            string.Equals(clip.name, names[n], StringComparison.OrdinalIgnoreCase))
                        {
                            return clip;
                        }
                    }
                }
            }
            catch
            {
                // Runtime-generated fallback below is always available.
            }

            return null;
        }

        private static void PlayShipAudibleOneShot(
            AudioClip clip,
            float volume,
            bool includeUiLayer = true)
        {
            if (clip == null) return;

            StartOfRound sor = StartOfRound.Instance;
            AudioSource source = null;
            if (sor != null)
                source = sor.shipDoorAudioSource != null ? sor.shipDoorAudioSource : sor.speakerAudioSource;

            if (source != null)
                source.PlayOneShot(clip, Mathf.Clamp01(volume));

            // The UI layer exists for the battery emergencies, which have to
            // reach a crew member deep in the facility. Routine sounds skip it:
            // hearing the ship from everywhere reads as a bug, not a broadcast.
            if (!includeUiLayer)
                return;

            AudioSource ui = HUDManager.Instance != null ? HUDManager.Instance.UIAudio : null;
            if (ui != null && ui != source)
                ui.PlayOneShot(clip, Mathf.Clamp01(volume * 0.45f));
        }

        private void PlayWorldOneShot(AudioClip clip, Vector3 position, float volume, float maxDistance)
        {
            if (clip == null) return;

            GameObject go = new GameObject("LGUShipSystems_BatteryImpactAudio");
            go.transform.position = position;
            AudioSource source = go.AddComponent<AudioSource>();
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 4f;
            source.maxDistance = Mathf.Max(12f, maxDistance);
            source.volume = Mathf.Clamp01(volume);
            source.PlayOneShot(clip);
            Destroy(go, clip.length + 0.25f);
        }



        // Held while the full-wall takeover owns the monitors. Deliberately NOT
        // gated on _primaryMonitorTakeoverActive — CCTV scopes itself to the two
        // large lower monitors and expects these power visuals to keep running.
        private bool DeferPowerVisualDuringTakeover(bool powerOnline)
        {
            if (!ExternalMonitorTakeoverActive) return false;

            _powerVisualDeferred = true;
            _deferredPowerVisualOnline = powerOnline;
            // The takeover repaints the monitor faces itself, so whatever we
            // last applied is no longer what is on screen. Forget it, or the
            // replay once the takeover ends would be swallowed by the guard.
            _appliedPowerVisualOnline = null;
            LogVerbose($"Ship power visual deferred during external monitor takeover (online={powerOnline}).");
            return true;
        }

        private void ApplyPrimaryMapScreenPower(StartOfRound sor, bool powerOnline)
        {
            if (PrimaryMonitorTakeoverActiveScope)
            {
                _primaryMapScreenPowerDeferred = true;
                _deferredPrimaryMapScreenPowerOnline = powerOnline;
                LogVerbose($"Primary map-screen power deferred during monitor takeover (online={powerOnline}).");
                return;
            }

            sor?.mapScreen?.SwitchScreenOn(on: powerOnline);
        }

        private void ApplyShipPowerVisualState(bool powerOnline)
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null) return;

            // Defer check first: it invalidates the applied cache, so a takeover
            // that matches the currently applied state still replays afterwards.
            if (DeferPowerVisualDuringTakeover(powerOnline)) return;

            // Change-guard (issue #500). Everything below is idempotent world
            // state, and SetShipLightsServerRpc is a network call that vanilla
            // also persists — re-issuing it on every drain broadcast was the
            // RPC storm. A null cache (fresh round, post-takeover) applies once.
            if (_appliedPowerVisualOnline == powerOnline) return;
            _appliedPowerVisualOnline = powerOnline;

            try
            {
                ApplyPrimaryMapScreenPower(sor, powerOnline);
                if (sor.IsServer)
                    sor.shipRoomLights?.SetShipLightsServerRpc(setLightsOn: powerOnline);
                else
                    sor.shipRoomLights?.SetShipLightsOnLocalClientOnly(setLightsOn: powerOnline);
                Y4NGZCore.Modules.ShipSystems.ShipStatusSignals.PublishPowerVisual(powerOnline);
                SetVanillaShipScreensPoweredLocal(powerOnline);
                if (!powerOnline)
                    LogMonitorWallStateOnce();
            }
            catch (Exception ex)
            {
                LogVerbose($"Ship power visual sync failed: {ex.Message}");
            }
        }

        // Round-4 census identified the screens that stay lit after power-off:
        // vanilla 'Cube' slot ShipScreen1Mat and 'SingleScreen' ShipScreen2Mat
        // (both emissive). Swap those slots to a black material while power is
        // off — swap references only, never mutate the vanilla materials.
        private void SetVanillaShipScreensPoweredLocal(bool powerOnline)
        {
            bool off = !powerOnline;
            if (_shipScreenPowerOff == off)
                return;
            _shipScreenPowerOff = off;

            try
            {
                if (off)
                    BuildShipScreenPowerCacheIfNeeded();

                for (int i = 0; i < _shipScreenRenderers.Count; i++)
                {
                    MeshRenderer renderer = _shipScreenRenderers[i];
                    if (renderer == null)
                        continue;
                    renderer.sharedMaterials = off ? _shipScreenBlackMaterials[i] : _shipScreenOriginalMaterials[i];
                }
            }
            catch (Exception ex)
            {
                LogVerbose($"Vanilla ship screen power swap failed: {ex.Message}");
            }
        }

        private void BuildShipScreenPowerCacheIfNeeded()
        {
            bool valid = _shipScreenRenderers.Count > 0;
            for (int i = 0; valid && i < _shipScreenRenderers.Count; i++)
            {
                if (_shipScreenRenderers[i] == null)
                    valid = false;
            }

            if (valid)
                return;

            _shipScreenRenderers.Clear();
            _shipScreenOriginalMaterials.Clear();
            _shipScreenBlackMaterials.Clear();

            // #599: this cache blacks out screens by swapping whole
            // sharedMaterials arrays on MonitorWall/Cube and SingleScreen. Under
            // GeneralImprovements' UseBetterMonitors both of those renderers are
            // disabled, so the swap would black out nothing; retargeting it onto
            // GI's screens is worse still, because GI adopts a foreign
            // sharedMaterial as an "OverwrittenMaterial" and re-applies it on
            // every power toggle and group rebuild — the blackout would outlive
            // the power cut. GI runs its own monitor power handling; leave it to it.
            if (GeneralImprovementsMonitors.BetterMonitorsActive)
            {
                GeneralImprovementsMonitors.LogStandDown(
                    ShipSystemsBootstrap.Log,
                    "the ship-screen power blackout",
                    "GI disabled the vanilla screen meshes and owns monitor power for its own; " +
                    "a sharedMaterial swap onto GI screens would be adopted permanently");
                return;
            }

            if (_shipScreenBlackMaterial == null)
            {
                Shader shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Standard");
                _shipScreenBlackMaterial = new Material(shader) { name = "LGUShipSystems_ScreenOffBlack" };
                _shipScreenBlackMaterial.color = Color.black;
                if (_shipScreenBlackMaterial.HasProperty("_UnlitColor"))
                    _shipScreenBlackMaterial.SetColor("_UnlitColor", Color.black);
                if (_shipScreenBlackMaterial.HasProperty("_BaseColor"))
                    _shipScreenBlackMaterial.SetColor("_BaseColor", Color.black);
            }

            GameObject wall = GameObject.Find("Environment/HangarShip/ShipModels2b/MonitorWall");
            if (wall == null)
                return;

            MeshRenderer[] renderers = wall.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                string rendererName = renderer.gameObject.name;
                if (rendererName != "Cube" && rendererName != "SingleScreen")
                    continue;

                Material[] original = renderer.sharedMaterials;
                Material[] black = (Material[])original.Clone();
                bool any = false;
                for (int m = 0; m < black.Length; m++)
                {
                    string materialName = black[m] != null ? black[m].name ?? string.Empty : string.Empty;
                    if (materialName.StartsWith("ShipScreen", StringComparison.OrdinalIgnoreCase))
                    {
                        black[m] = _shipScreenBlackMaterial;
                        any = true;
                    }
                }

                if (!any)
                    continue;

                _shipScreenRenderers.Add(renderer);
                _shipScreenOriginalMaterials.Add(original);
                _shipScreenBlackMaterials.Add(black);
            }
        }

        // One-shot power-off census of the monitor wall so the next Player.log
        // identifies which screen (the beige neighbour) stays lit in the dark.
        private void LogMonitorWallStateOnce()
        {
            if (_loggedMonitorWallState)
                return;
            _loggedMonitorWallState = true;

            try
            {
                GameObject wall = GameObject.Find("Environment/HangarShip/ShipModels2b/MonitorWall");
                if (wall == null)
                {
                    ShipSystemsBootstrap.Log.LogInfo("[LGUShipSystems.PowerOff] MonitorWall not found for census.");
                    return;
                }

                MeshRenderer[] renderers = wall.GetComponentsInChildren<MeshRenderer>(includeInactive: false);
                for (int i = 0; i < renderers.Length; i++)
                {
                    MeshRenderer r = renderers[i];
                    if (r == null || !r.enabled) continue;
                    Material[] mats = r.sharedMaterials;
                    string matNames = string.Empty;
                    for (int m = 0; m < mats.Length; m++)
                    {
                        Material mat = mats[m];
                        if (mat == null) continue;
                        bool emissive = mat.HasProperty("_EmissiveColor") && mat.GetColor("_EmissiveColor").maxColorComponent > 0.01f;
                        matNames += $"{mat.name}(emissive={emissive}); ";
                    }
                    ShipSystemsBootstrap.Log.LogInfo($"[LGUShipSystems.PowerOff] MonitorWall renderer '{r.gameObject.name}': {matNames}");
                }
            }
            catch (Exception ex)
            {
                LogVerbose($"Monitor wall census failed: {ex.Message}");
            }
        }

        private float GetPowerCapacity()
        {
            int capacitorLevel = Y4NGZCore.Modules.ShipSystems.ShipUpgradeState.GetLevel("BackupCapacitor");
            return Mathf.Max(10f, 100f + capacitorLevel * 25f);
        }

        /// <summary>
        /// Builds this controller's named-message latch. Ten legacy handlers since #666 plus three versioned damage/transfer handlers — the
        /// fuel/power half of the original eighteen — every wire name unchanged and frozen
        /// (<c>LGUShipSystems.*</c> — an older peer reads them).
        ///
        /// <para><b>#613 task 2.2 — this replaces a latch on the wrong object.</b> The previous
        /// implementation kept a "registered" bool plus a <see cref="NetworkManager"/> reference
        /// and returned early when the live <c>NetworkManager</c> was the one it had already
        /// registered against. That is exactly the #435 bug: NGO builds a <em>fresh</em>
        /// <see cref="CustomMessagingManager"/> on every <c>StartHost</c>/<c>StartClient</c> while
        /// <c>NetworkManager.Singleton</c> lives for the whole process, so on the <em>second</em>
        /// lobby of a session the guard said "already registered", nothing re-attached, and all
        /// eighteen of these messages silently stopped arriving — power state, fuel state, the
        /// requisition vote, upgrade purchases and refit transitions, with no error anywhere. The
        /// hand-written unregister half had the same fault from the other end: it read the
        /// messaging manager off that remembered <see cref="NetworkManager"/>, which by then was
        /// the new one, so it would have unregistered the handlers it had just attached.</para>
        ///
        /// <para><see cref="NamedMessageRegistration"/> is the single copy of that latch and keys
        /// on the messaging manager, which is where handlers actually live.</para>
        /// </summary>
        private NamedMessageRegistration BuildNetworkHandlers()
        {
            var registration = new NamedMessageRegistration(
                "ship systems",
                new NamedMessageHandler(MsgPowerState, OnReceivePowerState),
                new NamedMessageHandler(MsgFuelState, OnReceiveFuelState),
                new NamedMessageHandler(MsgFuelRouteDenied, OnReceiveFuelRouteDenied),
                new NamedMessageHandler(MsgFuelChuteFeedback, OnReceiveFuelChuteFeedback),
                new NamedMessageHandler(MsgFuelChuteToggleRequest, OnReceiveFuelChuteToggleRequest),
                new NamedMessageHandler(MsgFuelChuteState, OnReceiveFuelChuteState),
                new NamedMessageHandler(MsgPowerSpend, OnReceivePowerSpend),
                new NamedMessageHandler(MsgAbilityEvent, OnReceiveAbilityEvent),
                new NamedMessageHandler(MsgBatteryRestore, OnReceiveBatteryRestore),
                new NamedMessageHandler(MsgTechnicianRepair, OnReceiveTechnicianRepair),
                new NamedMessageHandler(MsgChuteTransfer, OnReceiveChuteTransfer),
                new NamedMessageHandler(MsgBatteryDamage, OnReceiveBatteryDamage),
                new NamedMessageHandler(MsgBatteryDamageFeedback, OnReceiveBatteryDamageFeedback));

            registration.Registered += _ => LogVerbose("Network handlers registered.");
            registration.Unregistering += _ => ResetReplicatedStateShadows();
            return registration;
        }

        /// <summary>
        /// Safe to call every frame; the latch decides whether anything happens.
        /// </summary>
        private NamedMessageRegistration NetworkHandlers =>
            _networkHandlers ?? (_networkHandlers = BuildNetworkHandlers());

        private void EnsureNetworkHandlers()
        {
            NetworkHandlers.EnsureRegistered();
        }

        private void UnregisterNetworkHandlers()
        {
            NetworkHandlers.Detach();
        }

        /// <summary>
        /// Drops the client-side de-duplication shadows of host-replicated state when a session
        /// ends (#608's reconnect discipline).
        ///
        /// <para>Each of these fields exists to suppress a redundant visual re-apply: "the last
        /// power state I was told was online, so don't rebuild the lights". Carried across a
        /// disconnect they suppress the <em>first</em> message of the next lobby whenever the new
        /// host's value happens to equal the old one's, and the ship then sits on last session's
        /// visuals until something changes. They are pure shadows of replicated state, so
        /// forgetting them is always safe: the worst case is one redundant re-apply.</para>
        /// </summary>
        private void ResetReplicatedStateShadows()
        {
            _appliedPowerVisualOnline = null;
            _appliedEmergencyProtocolActive = null;
            _lastReceivedPowerOnline = null;
            _lastReceivedBatteryBroken = null;
        }

        private void BroadcastPowerState()
        {
            MarkPowerDisplayDirty();
            if (!IsServer()) return;
            int payloadSize = sizeof(float) * 2 + sizeof(bool) * 4;
            using (FastBufferWriter writer = new FastBufferWriter(payloadSize, Allocator.Temp))
            {
                writer.WriteValueSafe(_power);
                writer.WriteValueSafe(GetPowerCapacity());
                writer.WriteValueSafe(_shipPowerOnline);
                writer.WriteValueSafe(_batteryBroken);
                writer.WriteValueSafe(_restoreObjectiveActive);
                writer.WriteValueSafe(_restoreUsesSpareBattery);
                SendToAll(MsgPowerState, writer);
            }
        }

        private void BroadcastFuelState()
        {
            EnsureFuelInitialized();
            MarkPowerDisplayDirty();
            _fuelChute?.SetFuelLevel01(_fuel / Mathf.Max(1f, FuelCapacity));
            if (!IsServer()) return;
            int payloadSize = sizeof(float) * 3 + sizeof(int) + sizeof(bool) * 2 + 96;
            using (FastBufferWriter writer = new FastBufferWriter(payloadSize, Allocator.Temp))
            {
                FixedString64Bytes constellationKey = _currentFuelConstellationKey ?? string.Empty;
                writer.WriteValueSafe(ConfiguredFuelSystemEnabled);
                writer.WriteValueSafe(_fuel);
                writer.WriteValueSafe(FuelCapacity);
                writer.WriteValueSafe(_reservedRouteFuel);
                writer.WriteValueSafe(_reservedFuelRoute.RouteIndex);
                writer.WriteValueSafe(!string.IsNullOrWhiteSpace(_currentFuelConstellationKey));
                writer.WriteValueSafe(constellationKey);
                SendToAll(MsgFuelState, writer);
            }
        }

        private void BroadcastFuelRouteDenied(string denialText)
        {
            if (!IsServer())
                return;

            FixedString128Bytes message = string.IsNullOrWhiteSpace(denialText)
                ? "Insufficient ship fuel for that route."
                : denialText;
            using (FastBufferWriter writer = new FastBufferWriter(160, Allocator.Temp))
            {
                writer.WriteValueSafe(message);
                SendToAll(MsgFuelRouteDenied, writer);
            }
        }

        private void BroadcastAbilityEvent(int ability)
        {
            if (!IsServer()) return;
            using (FastBufferWriter writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(ability);
                SendToAll(MsgAbilityEvent, writer);
            }
        }

        private void RequestPowerSpend(float amount, string reason)
        {
            using (FastBufferWriter writer = new FastBufferWriter(sizeof(float), Allocator.Temp))
            {
                writer.WriteValueSafe(amount);
                SendToServer(MsgPowerSpend, writer);
            }
        }

        private void SendBatteryRestoreRequest(bool usedApparatus)
        {
            using (FastBufferWriter writer = new FastBufferWriter(sizeof(bool), Allocator.Temp))
            {
                writer.WriteValueSafe(usedApparatus);
                SendToServer(MsgBatteryRestore, writer);
            }
        }

        private void SendToAll(string messageName, FastBufferWriter writer)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer) return;
            nm.CustomMessagingManager.SendNamedMessageToAll(messageName, writer);
        }

        private void SendToServer(string messageName, FastBufferWriter writer)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null || !nm.IsClient) return;
            nm.CustomMessagingManager.SendNamedMessage(messageName, NetworkManager.ServerClientId, writer);
        }

        private void SendToClient(string messageName, ulong targetClientId, FastBufferWriter writer)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer) return;
            nm.CustomMessagingManager.SendNamedMessage(messageName, targetClientId, writer);
        }

        private void OnReceivePowerState(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerMessage(senderClientId)) return;

            try
            {
                float power;
                float capacity;
                bool online;
                bool broken;
                bool objective;
                bool spareBattery;

                reader.ReadValueSafe(out power);
                reader.ReadValueSafe(out capacity);
                reader.ReadValueSafe(out online);
                reader.ReadValueSafe(out broken);
                reader.ReadValueSafe(out objective);
                reader.ReadValueSafe(out spareBattery);

                _power = Mathf.Clamp(power, 0f, Mathf.Max(1f, capacity));
                _shipPowerOnline = online;
                _batteryBroken = broken;
                _restoreObjectiveActive = objective;
                _restoreUsesSpareBattery = spareBattery;
                MarkPowerDisplayDirty();
                // Only the binary flags drive the emergency protocol; the power
                // float alone must not re-run it. Nulls on first receipt mean a
                // late joiner's opening payload always applies full state.
                if (_lastReceivedPowerOnline != online || _lastReceivedBatteryBroken != broken)
                {
                    _lastReceivedPowerOnline = online;
                    _lastReceivedBatteryBroken = broken;
                    ApplyEmergencyProtocol(active: !online || broken);
                }
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log.LogWarning($"[LGUShipSystems] malformed power state: {ex.Message}");
            }
        }

        private void OnReceiveFuelChuteToggleRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServer())
                return;

            SetFuelChuteOpen(!FuelChuteOpen, broadcast: true, resetIdleTimer: true);
        }

        private void OnReceiveFuelChuteState(ulong senderClientId, FastBufferReader reader)
        {
            if (IsServer() || !IsServerMessage(senderClientId)) return;
            try
            {
                bool open;
                reader.ReadValueSafe(out open);
                _fuelChuteOpen = open;
                _fuelChute?.SetOpenLocal(open, immediate: false);
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log.LogWarning($"[LGUShipSystems] malformed chute state: {ex.Message}");
            }
        }
        private void OnReceiveFuelRouteDenied(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerMessage(senderClientId)) return;

            try
            {
                FixedString128Bytes message;
                reader.ReadValueSafe(out message);
                HUDManager.Instance?.DisplayTip("SHIP FUEL LOW", message.ToString(), isWarning: true);

                Terminal terminal = FindObjectOfType<Terminal>();
                if (terminal != null)
                    terminal.useCreditsCooldown = false;
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log.LogWarning($"[LGUShipSystems] malformed fuel denial: {ex.Message}");
            }
        }

        private void OnReceiveFuelChuteFeedback(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerMessage(senderClientId) || IsServer()) return;

            try
            {
                int added;
                int requested;
                reader.ReadValueSafe(out added);
                reader.ReadValueSafe(out requested);
                _fuelChute?.PlaySwallowLocal(added, requested);
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log.LogWarning($"[LGUShipSystems] malformed chute feedback: {ex.Message}");
            }
        }

        private void OnReceivePowerSpend(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServer()) return;
            float amount;
            reader.ReadValueSafe(out amount);
            TrySpendPower(Mathf.Max(0f, amount), "remote client request");
        }

        private void OnReceiveAbilityEvent(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerMessage(senderClientId)) return;

            int ability;
            reader.ReadValueSafe(out ability);
            switch (ability)
            {
                case AbilityBatteryFailureSequence:
                    _batteryStation?.ShowBrokenState();
                    StartBatteryFailureLocalEffects();
                    break;
                case AbilityBatteryBroken:
                    _batteryStation?.ShowBrokenState();
                    ForceHangarDoorOpenLocal();
                    break;
                case AbilityBatteryCritical:
                    PlayBatteryCriticalLocal();
                    break;
                case AbilityBatteryRestoredSpare:
                    OnBatteryRestoredLocal(usedApparatus: false);
                    SetShipAlarmAnimation(active: false);
                    break;
                case AbilityBatteryDropRepaired:
                    OnBatteryDropRepairedLocal();
                    break;
                case AbilityBatterySabotageHit:
                    Vector3 sourcePosition;
                    if (!TryGetBatterySocketPosition(out sourcePosition))
                    {
                        StartOfRound sor = StartOfRound.Instance;
                        sourcePosition = sor != null && sor.elevatorTransform != null
                            ? sor.elevatorTransform.position
                            : Vector3.zero;
                    }
                    PlayBatterySabotageFeedbackLocal(sourcePosition, 1);
                    break;
                case AbilityBatteryRestored:
                    OnBatteryRestoredLocal(usedApparatus: true);
                    SetShipAlarmAnimation(active: false);
                    break;
                case AbilityBatterySabotageHitCritical:
                    Vector3 criticalSourcePosition;
                    if (!TryGetBatterySocketPosition(out criticalSourcePosition))
                    {
                        StartOfRound criticalSor = StartOfRound.Instance;
                        criticalSourcePosition = criticalSor != null && criticalSor.elevatorTransform != null
                            ? criticalSor.elevatorTransform.position
                            : Vector3.zero;
                    }
                    PlayBatterySabotageFeedbackLocal(criticalSourcePosition, 1, critical: true);
                    break;
            }
        }

        private void OnReceiveBatteryRestore(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServer()) return;

            bool usedApparatus = false;
            try
            {
                reader.ReadValueSafe(out usedApparatus);
            }
            catch
            {
                // Older payloads carried no item flag; treat as apparatus dock.
                usedApparatus = true;
            }

            PlayerControllerB player = FindPlayerByClientId(senderClientId);
            if (TryGetHeldRestoreItem(player, out GrabbableObject restoreItem))
            {
                usedApparatus = IsApparatusObject(restoreItem);
                ConsumeRestoreItem(player, restoreItem);
            }
            else
            {
                LogVerbose($"Battery restore request from {senderClientId} had no server-visible restore item; restoring anyway to avoid a stale held-item desync.");
            }

            RestoreBatteryPower(usedApparatus, player);
        }

        private void OnReceiveTechnicianRepair(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServer()) return;
            ServerPerformTechnicianRepair(FindPlayerByClientId(senderClientId));
        }

        private static AudioClip CreateToneClip(string name, float duration, float startHz, float endHz, float volume)
        {
            int sampleRate = 44100;
            int samples = Mathf.Max(1, Mathf.RoundToInt(duration * sampleRate));
            float[] data = new float[samples];
            float phase = 0f;
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)samples;
                float hz = Mathf.Lerp(startHz, endHz, t);
                phase += hz / sampleRate;
                float envelope = Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI);
                data[i] = Mathf.Sin(phase * Mathf.PI * 2f) * envelope * volume;
            }
            AudioClip clip = AudioClip.Create(name, samples, 1, sampleRate, stream: false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip CreateKlaxonClip()
        {
            int sampleRate = 44100;
            float cycleSeconds = 0.48f;
            int cycleSamples = Mathf.Max(1, Mathf.RoundToInt(sampleRate * cycleSeconds));
            int totalSamples = cycleSamples * 4;
            float[] data = new float[totalSamples];
            float phase1 = 0f;
            float phase2 = 0f;

            for (int i = 0; i < totalSamples; i++)
            {
                float t = (i % cycleSamples) / (float)cycleSamples;
                float hz = t < 0.5f
                    ? Mathf.Lerp(420f, 760f, t * 2f)
                    : Mathf.Lerp(760f, 420f, (t - 0.5f) * 2f);

                phase1 += hz / sampleRate;
                phase2 += (hz * 2f) / sampleRate;
                float square = Mathf.Sin(phase1 * Mathf.PI * 2f) > 0f ? 1f : -1f;
                float harmonic = Mathf.Sin(phase2 * Mathf.PI * 2f);
                float attack = Mathf.Clamp01(i / (sampleRate * 0.015f));
                data[i] = (square * 0.62f + harmonic * 0.22f) * attack * 0.78f;
            }

            AudioClip clip = AudioClip.Create("LGUShipSystems_BatteryAlarm", totalSamples, 1, sampleRate, stream: false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip CreateImpactThudClip()
        {
            int sampleRate = 44100;
            float duration = 0.34f;
            int samples = Mathf.Max(1, Mathf.RoundToInt(duration * sampleRate));
            float[] data = new float[samples];
            float phase = 0f;
            float filteredNoise = 0f;

            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)samples;
                float hz = Mathf.Lerp(78f, 38f, t);
                phase += hz / sampleRate;
                float envelope = Mathf.Exp(-t * 10.5f);
                filteredNoise += 0.12f * ((UnityEngine.Random.value * 2f - 1f) - filteredNoise);
                float body = Mathf.Sin(phase * Mathf.PI * 2f) * 0.95f;
                data[i] = (body + filteredNoise * 0.35f) * envelope * 0.9f;
            }

            AudioClip clip = AudioClip.Create("LGUShipSystems_BatteryImpact", samples, 1, sampleRate, stream: false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip CreateElectricalFaultClip()
        {
            int sampleRate = 44100;
            float duration = 1.35f;
            int samples = Mathf.Max(1, Mathf.RoundToInt(duration * sampleRate));
            float[] data = new float[samples];
            float lowPass = 0f;
            float thumpPhase = 0f;

            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)samples;
                float white = UnityEngine.Random.value * 2f - 1f;
                lowPass += 0.06f * (white - lowPass);
                thumpPhase += 45f / sampleRate;
                float thump = Mathf.Sin(thumpPhase * Mathf.PI * 2f) * Mathf.Exp(-t * 7.5f) * 0.62f;
                float crackle = lowPass * (1.2f + Mathf.Sin(t * Mathf.PI * 36f) * 0.2f);
                float envelope = Mathf.Clamp01(1f - t * t);
                data[i] = (crackle * 0.45f + thump) * envelope * 0.68f;
            }

            AudioClip clip = AudioClip.Create("LGUShipSystems_BatteryFailure", samples, 1, sampleRate, stream: false);
            clip.SetData(data, 0);
            return clip;
        }

        private static bool IsServer()
        {
            NetworkManager nm = NetworkManager.Singleton;
            return nm != null && nm.IsServer;
        }

        /// <summary>
        /// True when this machine owns ship-layout state: the host in a live session, or
        /// any offline/test-harness process where no netcode session is running at all.
        /// Clients must never read, write or act on their own saved layout state; the
        /// host replicates the authoritative layout through the periodic snapshot.
        /// </summary>
        internal static bool IsServerRuntime()
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening)
                return true;
            return nm.IsServer;
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 17;
                value = value ?? string.Empty;
                for (int i = 0; i < value.Length; i++)
                    hash = hash * 31 + value[i];
                return hash;
            }
        }

        private static bool IsServerMessage(ulong senderClientId)
        {
            return senderClientId == NetworkManager.ServerClientId;
        }

        private static bool IsLandedOnMoon()
        {
            StartOfRound sor = StartOfRound.Instance;
            return sor != null && sor.shipHasLanded && !sor.inShipPhase;
        }

        private static bool IsCompanyMoon()
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null || sor.currentLevel == null) return false;
            if (sor.currentLevel.levelID == 3) return true;

            string name = sor.currentLevel.PlanetName ?? string.Empty;
            return name.IndexOf("company", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsInOrbit()
        {
            StartOfRound sor = StartOfRound.Instance;
            return sor != null && sor.inShipPhase;
        }

        private static void LogVerbose(string message)
        {
            if (ShipSystemsBootstrap.VerboseLogging != null && ShipSystemsBootstrap.VerboseLogging.Value)
                ShipSystemsBootstrap.Log.LogInfo("[LGUShipSystems] " + message);
        }
    }
}
