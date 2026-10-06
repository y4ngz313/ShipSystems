using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Layout;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal sealed partial class FuelChuteController : MonoBehaviour
    {
        internal const float IdleCloseSeconds = 10f;

        private const float DoorSlideSeconds = 1.35f;
        private const float IrisSlideDistance = 1.62f;
        private const float FuelItemPollSeconds = 0.30f;
        private const float DoorCoverageScale = 1.12f;
        private const float FuelItemSinkSeconds = 0.24f;
        internal const float FlameAnchorLocalY = -.9f;
        internal const float HissLoopVolume = 0.55f;
        // Unique name: the blend model has its own "FlameAnchor" that must
        // never be adopted (it lives in rotated/scaled model space).
        private const string FlameAnchorName = "FuelChute_FlameAnchor";
        private const string HissAudioObjectName = "FuelChuteHissAudio";
        internal static readonly Vector3 DepositTriggerCenter = new Vector3(0f, 0.30f, 0f);
        internal static readonly Vector3 DepositTriggerSize = new Vector3(1.35f, 1.2f, 1.35f);
        private static readonly Vector3 FuelItemSinkLocalPosition = new Vector3(0f, -0.68f, 0f);

        private ShipSystemsController _owner;
        private AudioSource _audio;
        private AudioSource _hissAudio;
        private InteractTrigger _trigger;
        private ScanNodeProperties _scan;
        private GameObject _pitVoidSurface;
        private Transform _flameAnchor;
        private GameObject _flameRoot;
        private VanillaChuteFlame _flameEffect;
        private float _nextFlameResolve;
        private Transform _flameRiseTarget;
        private Vector3 _flameRootRestLocalPos;
        private Transform _leftDoor;
        private Transform _rightDoor;
        private Transform _leftSeam;
        private Transform _rightSeam;
        private Transform _irisLeft;
        private Transform _irisRight;
        private Vector3 _leftDoorClosed;
        private Vector3 _rightDoorClosed;
        private Vector3 _leftSeamClosed;
        private Vector3 _rightSeamClosed;
        private Vector3 _irisLeftClosed;
        private Vector3 _irisRightClosed;
        private Renderer[] _leftDoorRenderers = Array.Empty<Renderer>();
        private Renderer[] _rightDoorRenderers = Array.Empty<Renderer>();
        private Renderer[] _leftSeamRenderers = Array.Empty<Renderer>();
        private Renderer[] _rightSeamRenderers = Array.Empty<Renderer>();
        private Transform _leftDoorScaleTarget;
        private Transform _rightDoorScaleTarget;
        private Transform _leftSeamScaleTarget;
        private Transform _rightSeamScaleTarget;
        private Vector3 _leftDoorBaseScale;
        private Vector3 _rightDoorBaseScale;
        private Vector3 _leftSeamBaseScale;
        private Vector3 _rightSeamBaseScale;
        private Coroutine _doorRoutine;
        private Coroutine _flameRoutine;
        private bool _referencesResolved;
        private bool _flameDiagnosticLogged;
        private bool _doorToggleDiagnosticLogged;
        private bool _isOpen;
        private float _fuelLevel01;
        private float _doorOpenAmount;
        private float _fuelItemPollTimer;
        private float _nextConsumeDiagnosticTime;
        private AudioClip _openCloseClip;
        private AudioClip _hissClip;
        private int _consumeClipIndex;
        private readonly HashSet<int> _consumedFuelInstanceIds = new HashSet<int>();

        private void Awake()
        {
            _audio = GetComponent<AudioSource>();
            ResolveVisualReferences();
            ConfigureInteractTrigger();
            SetOpenLocal(false, immediate: true);
            SetFuelLevel01(0f);
        }

        private void Start()
        {
            ResolveVisualReferences();
            ConfigureInteractTrigger();
            ApplyInteractText();
            SetFuelLevel01(_fuelLevel01);
        }

        private void Update()
        {
            if (!_isOpen)
            {
                _fuelItemPollTimer = 0f;
                return;
            }

            if (IsServerRuntime())
            {
                _fuelItemPollTimer -= Time.deltaTime;
                if (_fuelItemPollTimer <= 0f)
                {
                    _fuelItemPollTimer = FuelItemPollSeconds;
                    FlushOverlappingFuelItems();
                }
            }

            if (_flameRoot == null && Time.unscaledTime >= _nextFlameResolve)
            {
                _nextFlameResolve = Time.unscaledTime + 2f;
                _referencesResolved = false;
                ResolveVisualReferences();
                ApplyPitVisibility();
            }

            if (_flameRoot != null && _flameRoutine == null)
                ApplyFlameVisual(0f, 0f);
        }

        internal void Configure(ShipSystemsController owner, bool isOpen, float fuelLevel01)
        {
            _owner = owner;
            _owner?.PrewarmFuelChuteAudio();
            ResolveVisualReferences();
            ConfigureInteractTrigger();
            SetFuelLevel01(fuelLevel01);
            SetOpenLocal(isOpen, immediate: true);
        }

        private void FlushOverlappingFuelItems()
        {
            if (!_isOpen || !IsServerRuntime())
                return;

            Vector3 halfExtents = Vector3.Scale(DepositTriggerSize, transform.lossyScale) * 0.5f;
            Collider[] overlaps = Physics.OverlapBox(transform.TransformPoint(DepositTriggerCenter), halfExtents, transform.rotation, ~0, QueryTriggerInteraction.Collide);
            ClearRejectedItems();
            for (int i = 0; i < overlaps.Length; i++)
            {
                TryConsumeFuelItem(overlaps[i]);
            }
        }

        private void TryConsumeFuelItem(Collider other)
        {
            if (!_isOpen || _doorOpenAmount < .8f || !IsServerRuntime() || other == null || _activeDeposit != null) return;
            var item = other.GetComponentInParent<GrabbableObject>();
            if (!CanDeposit(item) || !IsAtMouth(item)) return;
            if (_rejectedItems.ContainsKey(item.GetInstanceID())) return;
            if (!ShipSystemsController.IsFuelSystemEnabled()) return;
            FuelChuteDeposit decision = FuelChuteIntake.Classify(item, _owner != null,
                _owner != null ? _owner.Fuel : 0f, _owner != null ? _owner.FuelCapacity : 0f, out float value);
            if (decision == FuelChuteDeposit.Wait)
            {
                LogFuelConsumeRejected(item, _owner == null ? "chute not ready" : "invalid fuel state");
                ApplyInteractText();
                return;
            }
            bool reject = decision == FuelChuteDeposit.RejectItem;
            _activeDeposit = item;
            _owner?.NotifyFuelChuteFuelDeposited();
            StartCoroutine(TransferItemRoutine(item, reject, value));
        }

        private void LogFuelConsumeRejected(GrabbableObject grabbable, string reason)
        {
            if (Time.realtimeSinceStartup < _nextConsumeDiagnosticTime)
                return;

            _nextConsumeDiagnosticTime = Time.realtimeSinceStartup + 1f;
            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Fuel][ChuteConsume] waiting item={GetGrabbableDisplayName(grabbable)}, reason={reason}");
        }

        private static string GetGrabbableDisplayName(GrabbableObject grabbable)
        {
            if (grabbable == null)
                return "<null>";

            return grabbable.itemProperties?.itemName ?? grabbable.name ?? "<unnamed>";
        }

        internal void SetOpenLocal(bool open, bool immediate)
        {
            _isOpen = open;
            ResolveVisualReferences();
            ApplyInteractText();

            if (_doorRoutine != null)
            {
                StopCoroutine(_doorRoutine);
                _doorRoutine = null;
            }

            bool routineStarted = false;
            if (immediate || !gameObject.activeInHierarchy)
            {
                ApplyDoorPositions(open ? 1f : 0f);
                ApplyPitVisibility();
                if (open)
                    FlushOverlappingFuelItems();
                LogDoorToggleDiagnosticOnce(open, immediate, routineStarted);
                return;
            }

            if (open)
            {
                ApplyPitVisibility();
                FlushOverlappingFuelItems();
            }

            _doorRoutine = StartCoroutine(AnimateDoorsRoutine(open));
            routineStarted = _doorRoutine != null;
            if (routineStarted)
                PlayOpenCloseOneShot();
            LogDoorToggleDiagnosticOnce(open, immediate, routineStarted);
        }

        private void LogDoorToggleDiagnosticOnce(bool open, bool immediate, bool routineStarted)
        {
            if (_doorToggleDiagnosticLogged || immediate)
                return;

            _doorToggleDiagnosticLogged = true;
            try
            {
                ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.FuelChute] Door toggle: open={open}, immediate={immediate}, activeInHierarchy={gameObject.activeInHierarchy}, leftDoor={GetTransformPath(_leftDoor)}, rightDoor={GetTransformPath(_rightDoor)}, closedPos={_leftDoorClosed.ToString("F3")}/{_rightDoorClosed.ToString("F3")}, irisFound={_irisLeft != null}/{_irisRight != null}, irisClosedPos={_irisLeftClosed.ToString("F3")}/{_irisRightClosed.ToString("F3")}, IrisSlideDistance={IrisSlideDistance.ToString("0.000")}, routineStarted={routineStarted}");
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.FuelChute] Door toggle diagnostic failed: {ex.Message}");
            }
        }

        internal void SetFuelLevel01(float fuelLevel01)
        {
            _fuelLevel01 = Mathf.Clamp01(fuelLevel01);
        }

        internal void PlaySwallowLocal(int added, int requested)
        {
            SetOpenLocal(true, immediate: false);
            PulseFuelVisual(added, requested);
            PlayConsumeOneShot();
            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.FuelChute] Acceptance started: added={added}, requested={requested}, nativeReady={_flameEffect?.IsInitialized == true}, active={gameObject.activeInHierarchy}.");
        }

        private IEnumerator AnimateDoorsRoutine(bool open)
        {
            float elapsed = 0f;
            float startOpen01 = _doorOpenAmount;
            float targetOpen01 = open ? 1f : 0f;

            while (elapsed < DoorSlideSeconds)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / DoorSlideSeconds));
                ApplyDoorPositions(Mathf.Lerp(startOpen01, targetOpen01, t));
                yield return null;
            }

            ApplyDoorPositions(targetOpen01);
            _doorRoutine = null;
            if (!open)
                ApplyPitVisibility();
        }

        private void ApplyDoorPositions(float open01)
        {
            _doorOpenAmount = Mathf.Clamp01(open01);
            EnsureLidSupport();
            _lidSupport.enabled = _doorOpenAmount < .8f;
            float eased = Mathf.SmoothStep(0f, 1f, _doorOpenAmount);
            if (_hissAudio == null)
                ResolveHissAudioSource();
            if (_hissAudio != null)
                _hissAudio.volume = HissLoopVolume * eased;
            if (_irisLeft != null) _irisLeft.localPosition = _irisLeftClosed + Vector3.left * (IrisSlideDistance * eased);
            if (_irisRight != null) _irisRight.localPosition = _irisRightClosed + Vector3.right * (IrisSlideDistance * eased);
            if (_leftDoor != null) _leftDoor.localPosition = _leftDoorClosed;
            if (_rightDoor != null) _rightDoor.localPosition = _rightDoorClosed;
            if (_leftSeam != null) _leftSeam.localPosition = _leftSeamClosed;
            if (_rightSeam != null) _rightSeam.localPosition = _rightSeamClosed;
        }

        private void ConfigureInteractTrigger()
        {
            if (_trigger == null)
                _trigger = GetComponentInChildren<InteractTrigger>(true);
            if (_trigger == null)
                return;

            FuelChuteInteractionGeometry.Configure(transform, _trigger);
            _trigger.interactable = true;
            _trigger.oneHandedItemAllowed = true;
            _trigger.twoHandedItemAllowed = true;
            _trigger.holdInteraction = false;
            _trigger.timeToHold = 0.1f;
            _trigger.timeToHoldSpeedMultiplier = 1f;
            _trigger.interactCooldown = true;
            _trigger.cooldownTime = 0.18f;
            _trigger.disableTriggerMesh = true;
            EnsureInteractEvents(_trigger);
            _trigger.onInteract.RemoveListener(OnInteract);
            _trigger.onInteract.AddListener(OnInteract);
            ApplyInteractionIcons(_trigger);
            ApplyInteractText();
        }

        public void OnInteract(PlayerControllerB player)
        {
            // A chute left behind by a save made with fuel enabled stays inert.
            if (!ShipSystemsController.IsFuelSystemEnabled())
            {
                ApplyInteractText();
                return;
            }

            (_owner ?? ShipSystemsController.Instance)?.RequestFuelChuteToggle();
            ApplyInteractText();
        }

        private void ApplyInteractText()
        {
            bool fuelSystemEnabled = ShipSystemsController.IsFuelSystemEnabled();
            if (_trigger != null)
            {
                _trigger.interactable = fuelSystemEnabled;
                // Vanilla substitutes the player's bound interact key for [LMB] when it
                // renders a hover/cursor tip; a literal [E] would lie on a rebound key.
                _trigger.hoverTip = _isOpen ? "Close fuel chute : [LMB]" : "Open fuel chute : [LMB]";
                if (_owner != null && _owner.Fuel >= _owner.FuelCapacity - .001f)
                    _trigger.hoverTip += "\nTank Full";
                _trigger.disabledHoverTip = "[ Fuel chute unavailable ]";
            }

            if (_scan != null)
            {
                _scan.headerText = "FUEL CHUTE";
                // Scan-node subtext gets no key substitution at all, so it stays key-neutral.
                _scan.subText = _owner != null && _owner.Fuel >= _owner.FuelCapacity - .001f ? "Tank Full" : (_isOpen ? "drop fuel into pit" : "interact to open");
            }
        }

        private void ResolveVisualReferences()
        {
            if (_hissAudio == null)
                ResolveHissAudioSource();

            if (_referencesResolved && _pitVoidSurface != null && _flameRoot != null && _leftDoor != null && _rightDoor != null && _irisLeft != null && _irisRight != null)
                return;

            _scan = GetComponentInChildren<ScanNodeProperties>(true);
            Transform pitSurface = FindChild("FuelChute_PitVoidSurface");
            _pitVoidSurface = pitSurface != null ? pitSurface.gameObject : null;
            _irisLeft = pitSurface != null ? FindChild(pitSurface, "FuelChute_IrisLeft") : FindChild("FuelChute_IrisLeft");
            _irisRight = pitSurface != null ? FindChild(pitSurface, "FuelChute_IrisRight") : FindChild("FuelChute_IrisRight");
            _flameAnchor = pitSurface != null ? FindChild(pitSurface, FlameAnchorName) : FindChild(FlameAnchorName);
            EnsureFlameVisual();
            CaptureFlameReferences();
            _leftDoor = FindChild("LeftDoor");
            _rightDoor = FindChild("RightDoor");
            _leftSeam = FindChild("LeftDoorSeamEdge_MovesWithDoor");
            _rightSeam = FindChild("RightDoorSeamEdge_MovesWithDoor");

            CaptureDoorPositions();
            CapturePhysicalDoorRenderers();
            _referencesResolved = true;
        }

        private void CaptureDoorPositions()
        {
            if (_leftDoor != null)
                _leftDoorClosed = _leftDoor.localPosition;

            if (_rightDoor != null)
                _rightDoorClosed = _rightDoor.localPosition;

            if (_leftSeam != null)
                _leftSeamClosed = _leftSeam.localPosition;

            if (_rightSeam != null)
                _rightSeamClosed = _rightSeam.localPosition;

            if (_irisLeft != null)
                _irisLeftClosed = _irisLeft.localPosition;

            if (_irisRight != null)
                _irisRightClosed = _irisRight.localPosition;

            ApplyDoorCoverageScale();
        }

        private void CapturePhysicalDoorRenderers()
        {
            _leftDoorRenderers = GetRenderers(_leftDoor);
            _rightDoorRenderers = GetRenderers(_rightDoor);
            _leftSeamRenderers = GetRenderers(_leftSeam);
            _rightSeamRenderers = GetRenderers(_rightSeam);
        }

        private static Renderer[] GetRenderers(Transform root)
        {
            return root != null ? root.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
        }

        private void ApplyDoorCoverageScale()
        {
            ApplyDoorCoverageScale(_leftDoor, ref _leftDoorScaleTarget, ref _leftDoorBaseScale);
            ApplyDoorCoverageScale(_rightDoor, ref _rightDoorScaleTarget, ref _rightDoorBaseScale);
            ApplyDoorCoverageScale(_leftSeam, ref _leftSeamScaleTarget, ref _leftSeamBaseScale);
            ApplyDoorCoverageScale(_rightSeam, ref _rightSeamScaleTarget, ref _rightSeamBaseScale);
        }

        private static void ApplyDoorCoverageScale(Transform target, ref Transform scaleTarget, ref Vector3 baseScale)
        {
            if (target == null)
            {
                scaleTarget = null;
                baseScale = Vector3.one;
                return;
            }

            if (scaleTarget != target)
            {
                scaleTarget = target;
                baseScale = target.localScale;
            }

            target.localScale = new Vector3(baseScale.x * DoorCoverageScale, baseScale.y * DoorCoverageScale, baseScale.z);
        }

        private void ApplyPitVisibility()
        {
            bool pitVisible = _isOpen || (_doorRoutine != null && !_isOpen);
            if (_pitVoidSurface != null && _pitVoidSurface.activeSelf != pitVisible)
                _pitVoidSurface.SetActive(pitVisible);
            if (_flameRoot != null && _flameRoot.activeSelf != pitVisible)
                _flameRoot.SetActive(pitVisible);
            SetPhysicalDoorRenderersVisible(!pitVisible);
            DriveHissLoop(pitVisible);
            if (pitVisible)
                LogFlameDiagnosticOnce();
        }

        private void ResolveHissAudioSource()
        {
            Transform hissTransform = FindChild(HissAudioObjectName);
            if (hissTransform != null)
                _hissAudio = hissTransform.GetComponent<AudioSource>();
            if (_hissAudio != null)
                return;

            AudioSource[] audioSources = GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < audioSources.Length; i++)
            {
                AudioSource candidate = audioSources[i];
                if (candidate != null && candidate.gameObject != null && string.Equals(candidate.gameObject.name, HissAudioObjectName, StringComparison.OrdinalIgnoreCase))
                {
                    _hissAudio = candidate;
                    return;
                }
            }
        }

        private void DriveHissLoop(bool pitVisible)
        {
            if (_hissAudio == null)
                ResolveHissAudioSource();
            if (_hissAudio == null)
                return;

            if (pitVisible)
            {
                if (_hissClip == null)
                    _hissClip = FuelChuteAssetLoader.TryGetHissLoopClip();
                if (_hissClip == null)
                    return;
                if (_hissAudio.clip != _hissClip)
                    _hissAudio.clip = _hissClip;
                _hissAudio.volume = HissLoopVolume * Mathf.SmoothStep(0f, 1f, _doorOpenAmount);
                if (!_hissAudio.isPlaying)
                    _hissAudio.Play();
            }
            else if (_hissAudio.isPlaying)
            {
                _hissAudio.Stop();
            }
        }

        private AudioSource ResolvePrimaryAudioSource()
        {
            if (_audio == null)
                _audio = GetComponent<AudioSource>();
            return _audio;
        }

        private void PlayOpenCloseOneShot()
        {
            AudioSource audio = ResolvePrimaryAudioSource();
            if (audio == null)
                return;

            if (_openCloseClip == null)
                _openCloseClip = FuelChuteAssetLoader.TryGetOpenCloseClip();
            if (_openCloseClip != null)
                audio.PlayOneShot(_openCloseClip);
        }

        private void PlayConsumeOneShot()
        {
            AudioSource audio = ResolvePrimaryAudioSource();
            if (audio == null)
                return;

            AudioClip clip = FuelChuteAssetLoader.TryGetConsumeClip(_consumeClipIndex++);
            if (clip != null)
                audio.PlayOneShot(clip);
        }

        private void SetPhysicalDoorRenderersVisible(bool visible)
        {
            SetRenderersVisible(_leftDoorRenderers, visible);
            SetRenderersVisible(_rightDoorRenderers, visible);
            SetRenderersVisible(_leftSeamRenderers, visible);
            SetRenderersVisible(_rightSeamRenderers, visible);
        }

        private static void SetRenderersVisible(Renderer[] renderers, bool visible)
        {
            if (renderers == null)
                return;

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = visible;
            }
        }

        private void PulseFuelVisual(int added, int requested)
        {
            ResolveVisualReferences();
            EnsureFlameVisual();
            CaptureFlameReferences();
            if (_flameRoot == null)
                return;

            _flameEffect?.Restart();

            if (_flameRoutine != null)
                StopCoroutine(_flameRoutine);
            _flameRoutine = StartCoroutine(FlameFlareRoutine(added, requested));
        }

        private IEnumerator FlameFlareRoutine(int added, int requested)
        {
            float peak = FuelAcceptanceEnvelope.PeakScale(Mathf.Max(added, requested));
            for (float elapsed = 0f; elapsed < FuelAcceptanceEnvelope.Duration; elapsed += Time.deltaTime)
            {
                float strength = FuelAcceptanceEnvelope.Strength(elapsed);
                ApplyFlameVisual(peak * strength, .35f * strength);
                yield return null;
            }
            ApplyFlameVisual(0f, 0f);
            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.FuelChute] Acceptance finished: nativeFrames={_flameEffect?.FrameChanges ?? 0}, nativeDraws={_flameEffect?.DrawCalls ?? 0}, returnedToIdle=true.");
            _flameRoutine = null;
        }

        private void EnsureFlameVisual()
        {
            if (_flameAnchor == null)
            {
                Transform anchorParent = _pitVoidSurface != null ? _pitVoidSurface.transform : transform;
                GameObject anchor = new GameObject(FlameAnchorName);
                anchor.transform.SetParent(anchorParent, false);
                anchor.transform.localPosition = new Vector3(0f, FlameAnchorLocalY, 0f);
                anchor.transform.localRotation = Quaternion.identity;
                anchor.transform.localScale = Vector3.one;
                _flameAnchor = anchor.transform;
            }

            // Retire the authored quads even when the bundle is present. That was the
            // active path in Gale 3 and must not short-circuit vanilla-source discovery.
            Transform authored = FindChild(_flameAnchor, "FuelChute_FlameRoot");
            if (authored != null) { authored.gameObject.SetActive(false); Destroy(authored.gameObject); }
            Transform existing = FindChild(_flameAnchor, "FuelChute_VanillaThruster");
            if (existing != null)
            {
                if (existing.GetComponent<VanillaChuteFlame>()?.IsInitialized == true)
                { _flameRoot = existing.gameObject; return; }
                // Furniture cloning does not copy the native animation driver ownership.
                existing.name = "FuelChute_RetiredThruster";
                existing.gameObject.SetActive(false); Destroy(existing.gameObject);
            }
            _flameAnchor.localPosition = new Vector3(0f, FlameAnchorLocalY, 0f);
            _flameRoot = FuelChuteAssetLoader.TryInstantiateVanillaFlame(_flameAnchor);
            if (_flameRoot != null)
            {
                _flameRoot.SetActive(_isOpen);
                CaptureFlameRestPoseIfNeeded();
            }
        }

        private void CaptureFlameReferences()
        {
            _flameEffect = _flameRoot != null ? _flameRoot.GetComponent<VanillaChuteFlame>() : null;
            CaptureFlameRestPoseIfNeeded();
        }

        private void CaptureFlameRestPoseIfNeeded()
        {
            Transform currentRoot = _flameRoot != null ? _flameRoot.transform : null;
            if (_flameRiseTarget == currentRoot)
                return;

            _flameRiseTarget = currentRoot;
            _flameRootRestLocalPos = currentRoot != null ? currentRoot.localPosition : Vector3.zero;
        }

        private void ApplyFlameRise(float riseLocalY)
        {
            if (_flameRoot == null)
                return;

            CaptureFlameRestPoseIfNeeded();
            if (_flameRiseTarget != null)
                _flameRiseTarget.localPosition = _flameRootRestLocalPos + Vector3.up * riseLocalY;
        }

        private void ApplyFlameVisual(float strength, float riseLocalY)
        {
            if (_flameRoot == null) return;
            ApplyFlameRise(riseLocalY);
            _flameEffect?.SetEnvelope(strength);
        }

        private void LogFlameDiagnosticOnce()
        {
            if (_flameDiagnosticLogged) return;
            _flameDiagnosticLogged = true;
            ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.FuelChute] Native flame ready={_flameEffect?.IsInitialized == true}, active={_flameRoot != null && _flameRoot.activeInHierarchy}, path=native-material-custom-pass.");
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            string path = transform.name;
            for (Transform current = transform.parent; current != null; current = current.parent)
                path = current.name + "/" + path;
            return path;
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (root == null)
                return null;

            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            }

            return null;
        }

        private Transform FindChild(string name)
        {
            Transform[] children = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            }

            return null;
        }

        private static void EnsureInteractEvents(InteractTrigger trigger)
        {
            if (trigger.onInteract == null) trigger.onInteract = new InteractEvent();
            if (trigger.onInteractEarly == null) trigger.onInteractEarly = new InteractEvent();
            if (trigger.onInteractEarlyOtherClients == null) trigger.onInteractEarlyOtherClients = new InteractEvent();
            if (trigger.onStopInteract == null) trigger.onStopInteract = new InteractEvent();
            if (trigger.onCancelAnimation == null) trigger.onCancelAnimation = new InteractEvent();
            if (trigger.holdingInteractEvent == null) trigger.holdingInteractEvent = new InteractEventFloat();
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

        private static bool IsServerRuntime()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager == null || manager.IsServer;
        }
    }

    internal static class FuelChuteControllerFactory
    {
        private const float AuthoredVisualScale = 0.86f;
        private static readonly string[] AuthoringOnlyNameFragments = new string[]
        {
            "PreviewShipFloorPanel",
            "PreviewFloor",
            "PreviewOnly",
            "ShipFloorPanel",
            "ShipFloor",
            "FloorPanel",
            "SlidingPocketCover",
            "PocketCover",
            "PitVoidShadowShaft"
        };
        internal static GameObject Create()
        {
            FuelChuteFurnitureRegistry.EnsureRegistered();
            GameObject root = CreateRuntimeObject("LGUShipSystems_FuelChute");
            FuelChuteFurnitureRegistry.ConfigureSpawnedChute(root, root.GetComponent<FuelChuteController>());
            return root;
        }

        internal static GameObject CreateRuntimeObject(string rootName)
        {
            GameObject root = new GameObject(string.IsNullOrWhiteSpace(rootName) ? "LGUShipSystems_FuelChute" : rootName);
            root.layer = FuelRuntimeUtility.PropsLayer;

            GameObject visual = FuelChuteAssetLoader.TryInstantiateChuteVisual(root.transform);
            bool authoredVisual = visual != null;
            if (visual == null)
                visual = BuildFallbackVisual(root.transform);
            visual.name = "FuelChuteVisual";
            if (authoredVisual)
            {
                HideAuthoringOnlyVisuals(visual.transform);
                ApplyAuthoredVisualCorrection(visual);
            }

            ConfigureInteractTrigger(root.transform);
            ConfigureAudio(root);
            EnsurePitVoidSurface(root.transform);
            BuildExteriorChamber(root.transform);
            FuelRuntimeUtility.AddScanNode(root.transform, "ScanNode", "FUEL CHUTE", "interact to open", Vector3.up * 0.45f, new Vector3(1.5f, 0.8f, 1.5f));

            FuelChuteController controller = root.AddComponent<FuelChuteController>();
            controller.Configure(ShipSystemsController.Instance, isOpen: false, fuelLevel01: 0f);
            return root;
        }

        private static void BuildExteriorChamber(Transform parent)
        {
            // Same transform as the furniture, so saved/moved chute poses cannot leave
            // an unrelated casing behind. The receiving chamber is enclosed underneath.
            var shell = new GameObject("FuelChute_ExteriorChamber");
            shell.transform.SetParent(parent, false);
            Material steel = CreateMaterial("FuelChute_ExteriorSteel", new Color(.11f,.12f,.12f,1), .65f,.24f);
            Material band = CreateMaterial("FuelChute_ExteriorBand", new Color(.18f,.19f,.18f,1), .7f,.3f);
            const int sides = 32;
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            float[] heights = { -.08f, -.28f, -1.35f, -1.82f };
            float[] radii = { .82f, .82f, .62f, .49f };
            for (int row = 0; row < heights.Length; row++)
                for (int i = 0; i <= sides; i++)
                {
                    float angle = i * Mathf.PI * 2f / sides;
                    vertices.Add(new Vector3(Mathf.Cos(angle)*radii[row],heights[row],Mathf.Sin(angle)*radii[row]));
                }
            for (int row = 0; row < heights.Length - 1; row++)
                for (int i = 0; i < sides; i++)
                {
                    int a=row*(sides+1)+i, b=a+sides+1;
                    triangles.Add(a); triangles.Add(a+1); triangles.Add(b);
                    triangles.Add(a+1); triangles.Add(b+1); triangles.Add(b);
                }
            var mesh = new Mesh { name = "FuelChute_ExteriorHousing" };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles,0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            CreateRuntimeMeshObject("Housing",shell.transform,Vector3.zero,Quaternion.identity,Vector3.one,mesh,steel);
            CreatePrimitive(PrimitiveType.Cylinder,"ClosedBottom",shell.transform,new Vector3(0,-1.83f,0),new Vector3(1.00f,.035f,1.00f),band);
            CreateFlatAnnulus("MountFlange",shell.transform,.91f,.75f,sides,new Vector3(0,-.08f,0),band);
        }

        private static void ApplyAuthoredVisualCorrection(GameObject visual)
        {
            if (visual == null)
                return;

            Transform model = FindChildStatic(visual.transform, "FuelChuteModel") ?? visual.transform;
            model.localPosition += Vector3.down * 0.025f;
            model.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            model.localScale = Vector3.one * AuthoredVisualScale;
            NormalizeAuthoredChuteMaterials(model);
        }

        private static void NormalizeAuthoredChuteMaterials(Transform root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] materials = renderers[i].materials;
                for (int j = 0; j < materials.Length; j++)
                {
                    Material material = materials[j];
                    if (material == null)
                        continue;

                    Color color = material.name.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0
                        ? new Color(0.075f, 0.082f, 0.08f, 1f)
                        : new Color(0.12f, 0.125f, 0.115f, 1f);

                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                    if (material.HasProperty("_Color")) material.SetColor("_Color", color);
                    if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.18f);
                    if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.18f);
                    if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.18f);
                    if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", Color.black);
                    if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", Color.black);
                    material.DisableKeyword("_EMISSION");
                }
            }
        }
        private static void HideAuthoringOnlyVisuals(Transform root)
        {
            if (root == null)
                return;

            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child == null || child == root || !IsAuthoringOnlyVisualName(child.name))
                    continue;

                child.gameObject.SetActive(false);
            }
        }

        private static bool IsAuthoringOnlyVisualName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName))
                return false;

            for (int i = 0; i < AuthoringOnlyNameFragments.Length; i++)
            {
                if (objectName.IndexOf(AuthoringOnlyNameFragments[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }
        private static void ConfigureInteractTrigger(Transform parent)
        {
            GameObject triggerObject = new GameObject("FuelChuteInteractTrigger");
            triggerObject.transform.SetParent(parent, false);
            triggerObject.transform.localPosition = new Vector3(0f, 0.22f, 0f);
            triggerObject.layer = FuelRuntimeUtility.InteractLayer;
            FuelRuntimeUtility.SetTagSafe(triggerObject, "InteractTrigger");

            BoxCollider collider = triggerObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.center = new Vector3(0f, 0.15f, 0f);
            collider.size = new Vector3(2.0f, 1.0f, 2.0f);

            triggerObject.AddComponent<InteractTrigger>();
        }

        private static void ConfigureAudio(GameObject root)
        {
            AudioSource audio = root.AddComponent<AudioSource>();
            audio.spatialBlend = 1f;
            audio.playOnAwake = false;
            audio.minDistance = 1.5f;
            audio.maxDistance = 12f;

            GameObject hissObject = new GameObject("FuelChuteHissAudio");
            hissObject.transform.SetParent(root.transform, false);
            hissObject.transform.localPosition = Vector3.zero;
            hissObject.transform.localRotation = Quaternion.identity;
            hissObject.transform.localScale = Vector3.one;

            AudioSource hissAudio = hissObject.AddComponent<AudioSource>();
            hissAudio.loop = true;
            hissAudio.playOnAwake = false;
            hissAudio.spatialBlend = 1f;
            hissAudio.minDistance = 1.2f;
            hissAudio.maxDistance = 9f;
            hissAudio.volume = FuelChuteController.HissLoopVolume;
        }

        private static void EnsurePitVoidSurface(Transform parent)
        {
            if (FindChildStatic(parent, "FuelChute_PitVoidSurface") != null)
                return;

            GameObject root = CreatePitShaftVisual(parent);
            root.SetActive(false);
        }

        private static Transform FindChildStatic(Transform root, string name)
        {
            if (root == null)
                return null;

            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            }

            return null;
        }

        private static GameObject CreatePitShaftVisual(Transform parent)
        {
            GameObject root = new GameObject("FuelChute_PitVoidSurface");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(0f, 0.0295f, 0f);
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            Material maskMat = CreateMaterial("FuelChute_Runtime_PitOpeningMask", new Color(0.045f, 0.042f, 0.040f, 1f), 0f, 0.08f);
            Material wallMat = CreateVertexColorMaterial("FuelChute_Runtime_PitShaftGradient", 2450);
            Material bottomMat = CreateMaterial("FuelChute_Runtime_PitBottomWarm", new Color(0.018f, 0.012f, 0.006f, 1f), 0f, 0.08f, emission: true);
            Material irisMat = CreateVertexColorMaterial("FuelChute_Runtime_StencilIrisDoor", 2472, new Color(0.085f, 0.09f, 0.088f, 1f));

            CreateFlatAnnulus("FuelChute_PitOpeningMask", root.transform, 0.76f, 0.73f, 40, new Vector3(0f, 0.007f, 0f), maskMat);
            CreateGradientOpenCylinderWall("FuelChute_PitShaftWall", root.transform, 0.75f, 0.45f, 1.85f, 40, new Vector3(0f, -0.875f, 0f), wallMat);
            CreatePrimitive(PrimitiveType.Cylinder, "FuelChute_PitBottom", root.transform, new Vector3(0f, -1.80f, 0f), new Vector3(0.92f, 0.006f, 0.92f), bottomMat);

            GameObject irisRoot = new GameObject("FuelChute_IrisDoors");
            irisRoot.transform.SetParent(root.transform, false);
            irisRoot.transform.localPosition = new Vector3(0f, 0.020f, 0f);
            irisRoot.transform.localRotation = Quaternion.identity;
            irisRoot.transform.localScale = Vector3.one;
            CreateRuntimeMeshObject("FuelChute_IrisLeft", irisRoot.transform, Vector3.zero, Quaternion.identity, Vector3.one, CreateRuntimeHalfDiscMesh("FuelChute_RuntimeIrisLeftMesh", 0.78f, 32, Color.white, new Color(0.75f, 0.75f, 0.75f, 1f), positiveX: false), irisMat);
            CreateRuntimeMeshObject("FuelChute_IrisRight", irisRoot.transform, Vector3.zero, Quaternion.identity, Vector3.one, CreateRuntimeHalfDiscMesh("FuelChute_RuntimeIrisRightMesh", 0.78f, 32, Color.white, new Color(0.75f, 0.75f, 0.75f, 1f), positiveX: true), irisMat);
            return root;
        }

        private static GameObject CreateFlatAnnulus(string name, Transform parent, float outerRadius, float innerRadius, int segments, Vector3 localPosition, Material material)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            Mesh mesh = new Mesh { name = name + "Mesh" };
            Vector3[] vertices = new Vector3[(segments + 1) * 2];
            int[] triangles = new int[segments * 6];
            for (int i = 0; i <= segments; i++)
            {
                float angle = (Mathf.PI * 2f * i) / segments;
                float x = Mathf.Cos(angle);
                float z = Mathf.Sin(angle);
                vertices[i * 2] = new Vector3(x * outerRadius, 0f, z * outerRadius);
                vertices[i * 2 + 1] = new Vector3(x * innerRadius, 0f, z * innerRadius);
            }

            for (int i = 0; i < segments; i++)
            {
                int vertex = i * 2;
                int tri = i * 6;
                triangles[tri] = vertex;
                triangles[tri + 1] = vertex + 1;
                triangles[tri + 2] = vertex + 2;
                triangles[tri + 3] = vertex + 2;
                triangles[tri + 4] = vertex + 1;
                triangles[tri + 5] = vertex + 3;
            }

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            MeshFilter filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return go;
        }
        private static GameObject CreateGradientOpenCylinderWall(string name, Transform parent, float topRadius, float bottomRadius, float height, int segments, Vector3 localPosition, Material material)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            const int RingCount = 3;
            Mesh mesh = new Mesh { name = name + "Mesh" };
            Vector3[] vertices = new Vector3[(segments + 1) * RingCount];
            Color[] colors = new Color[(segments + 1) * RingCount];
            int[] triangles = new int[segments * (RingCount - 1) * 6];
            float halfHeight = height * 0.5f;
            Color topColor = new Color(0.13f, 0.12f, 0.11f, 1f);
            Color quarterColor = new Color(0.030f, 0.027f, 0.025f, 1f);
            Color bottomColor = new Color(0.004f, 0.003f, 0.003f, 1f);

            for (int ring = 0; ring < RingCount; ring++)
            {
                float fraction = ring == 0 ? 0f : ring == 1 ? 0.25f : 1f;
                float y = halfHeight - height * fraction;
                float radius = Mathf.Lerp(topRadius, bottomRadius, fraction);
                Color color = ring == 0 ? topColor : ring == 1 ? quarterColor : bottomColor;
                int offset = ring * (segments + 1);
                for (int i = 0; i <= segments; i++)
                {
                    float angle = (Mathf.PI * 2f * i) / segments;
                    vertices[offset + i] = new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);
                    colors[offset + i] = color;
                }
            }

            for (int ring = 0; ring < RingCount - 1; ring++)
            {
                int ringOffset = ring * (segments + 1);
                int nextRingOffset = (ring + 1) * (segments + 1);
                for (int i = 0; i < segments; i++)
                {
                    int tri = (ring * segments + i) * 6;
                    triangles[tri] = ringOffset + i;
                    triangles[tri + 1] = ringOffset + i + 1;
                    triangles[tri + 2] = nextRingOffset + i;
                    triangles[tri + 3] = ringOffset + i + 1;
                    triangles[tri + 4] = nextRingOffset + i + 1;
                    triangles[tri + 5] = nextRingOffset + i;
                }
            }

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.colors = colors;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            MeshFilter filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return go;
        }

        private static GameObject BuildFallbackVisual(Transform parent)
        {
            GameObject visual = new GameObject("FuelChute_Sliding_FallbackVisual");
            visual.transform.SetParent(parent, false);

            Material rimMat = CreateMaterial("FuelChute_Fallback_RimMetal", new Color(0.28f, 0.30f, 0.29f, 1f), 0.45f, 0.28f);
            Material doorMat = CreateMaterial("FuelChute_Fallback_DarkDoorMetal", new Color(0.18f, 0.20f, 0.20f, 1f), 0.35f, 0.22f);
            Material pitMat = CreateMaterial("FuelChute_Fallback_PitBlack", new Color(0.005f, 0.004f, 0.003f, 1f), 0f, 0.05f);

            GameObject pit = CreatePrimitive(PrimitiveType.Cylinder, "PitVoidShadowShaft_NoCollider", visual.transform, new Vector3(0f, -0.42f, 0f), new Vector3(1.42f, 0.82f, 1.42f), pitMat);
            GameObject rim = CreatePrimitive(PrimitiveType.Cylinder, "Rim", visual.transform, new Vector3(0f, 0.025f, 0f), new Vector3(1.95f, 0.08f, 1.95f), rimMat);
            GameObject leftDoor = CreatePrimitive(PrimitiveType.Cube, "LeftDoor", visual.transform, new Vector3(-0.36f, -0.01f, 0f), new Vector3(0.72f, 0.04f, 1.42f), doorMat);
            GameObject rightDoor = CreatePrimitive(PrimitiveType.Cube, "RightDoor", visual.transform, new Vector3(0.36f, -0.01f, 0f), new Vector3(0.72f, 0.04f, 1.42f), doorMat);
            CreatePrimitive(PrimitiveType.Cube, "LeftDoorSeamEdge_MovesWithDoor", leftDoor.transform, new Vector3(0.5f, 0.52f, 0f), new Vector3(0.012f, 0.03f, 1.38f), pitMat);
            CreatePrimitive(PrimitiveType.Cube, "RightDoorSeamEdge_MovesWithDoor", rightDoor.transform, new Vector3(-0.5f, 0.52f, 0f), new Vector3(0.012f, 0.03f, 1.38f), pitMat);

            return visual;
        }

        private static GameObject CreateRuntimeMeshObject(string name, Transform parent, Vector3 localPosition, Quaternion localRotation, Vector3 localScale, Mesh mesh, Material material)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = localRotation;
            go.transform.localScale = localScale;
            MeshFilter filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        private static Mesh CreateRuntimeHalfDiscMesh(string name, float radius, int segments, Color centerColor, Color edgeColor, bool positiveX)
        {
            Mesh mesh = new Mesh { name = name };
            Vector3[] vertices = new Vector3[segments + 2];
            int[] triangles = new int[segments * 3];
            Color[] colors = new Color[segments + 2];
            vertices[0] = Vector3.zero;
            colors[0] = centerColor;
            float startAngle = positiveX ? -Mathf.PI * 0.5f : Mathf.PI * 0.5f;
            float endAngle = positiveX ? Mathf.PI * 0.5f : Mathf.PI * 1.5f;
            for (int i = 0; i <= segments; i++)
            {
                float angle = Mathf.Lerp(startAngle, endAngle, i / (float)segments);
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                colors[i + 1] = edgeColor;
            }

            for (int i = 0; i < segments; i++)
            {
                int tri = i * 3;
                triangles[tri] = 0;
                triangles[tri + 1] = i + 2;
                triangles[tri + 2] = i + 1;
            }

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.colors = colors;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static GameObject CreatePrimitive(PrimitiveType type, string name, Transform parent, Vector3 localPosition, Vector3 localScale, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = localScale;
            RemoveCollider(go);
            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = material;
            return go;
        }

        private static Material CreateVertexColorMaterial(string name, int renderQueue)
        {
            return CreateVertexColorMaterial(name, renderQueue, Color.white);
        }

        private static Material CreateVertexColorMaterial(string name, int renderQueue, Color baseColor)
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Diffuse");
            Material material = new Material(shader) { name = name };
            material.renderQueue = renderQueue;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
            if (material.HasProperty("_Color")) material.SetColor("_Color", baseColor);
            return material;
        }

        private static Material CreateMaterial(string name, Color color, float metallic = 0f, float smoothness = 0.2f, bool emission = false)
        {
            Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Diffuse");
            Material material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            if (emission)
            {
                material.EnableKeyword("_EMISSION");
                if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", color * 2.8f);
                if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", color * 2.8f);
            }
            return material;
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (root == null)
                return null;

            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < children.Length; i++)
            {
                Transform child = children[i];
                if (child != null && string.Equals(child.name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            }

            return null;
        }

        private static void RemoveCollider(GameObject go)
        {
            Collider collider = go != null ? go.GetComponent<Collider>() : null;
            if (collider != null)
                UnityEngine.Object.Destroy(collider);
        }
    }
}
