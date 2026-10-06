using System;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.ShipSystems.Layout;
using Y4NGZCompany.ShipSystems.Rendering;
using Y4NGZCore.Modules.Monitor;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    /// <summary>
    /// The ship fuel readout, rendered so that it looks like it belongs to whatever monitor
    /// wall the player actually has (#676, restyled in #678).
    ///
    /// <para><b>Three hosts, auto-detected, no config for the look.</b></para>
    /// <list type="number">
    /// <item><description><b>GeneralImprovements Better Monitors, slot free.</b> The readout is
    /// handed to GI as text and GI renders it — its font, its size, its green, its navy plate,
    /// its camera, its material. The fuel screen is not styled <em>like</em> a GI monitor; it
    /// <em>is</em> one, in the same "LABEL:\nVALUE" shape GI uses for DOOR POWER and
    /// SHIP SCRAP.</description></item>
    /// <item><description><b>GI Better Monitors, but no usable text canvas</b> — an older or
    /// newer GI without the API, or a slot the player pointed at one of GI's own monitors. The
    /// offscreen rig paints a RenderTexture through the property-block lease instead, dressed in
    /// GI's own palette and the game's monitor font so the mismatch is as small as the mechanism
    /// allows.</description></item>
    /// <item><description><b>No monitor-wall mod at all.</b>
    /// <see cref="VanillaFuelMonitorPanel"/> clones the stock profit-quota screen onto a spare
    /// vanilla monitor face. See that class for why it cannot bring back #659.</description></item>
    /// </list>
    ///
    /// <para><b>The #659 boundary, restated.</b> The white-screen flood came from the deleted
    /// ShipPowerDisplay writing cloned materials directly — <c>new Material(source)</c>,
    /// <c>sharedMaterial =</c>, and an unconditional forced-white <c>_EmissiveColor</c> plus
    /// <c>EnableKeyword</c>. None of the three paths above does any of that. Under GI the only
    /// thing this assembly ever hands the hardware is a <see cref="RenderTexture"/> passed to
    /// <see cref="GeneralImprovementsMonitorPropertyBlock.Apply"/>, which gates the emissive push
    /// behind the material's own <c>_EMISSIVE_COLOR_MAP</c> keyword — and on the text path, not
    /// even that. The vanilla path is UI graphics on the ship's own canvas and is reachable only
    /// when <see cref="FuelMonitorHost.ForeignMonitorWallPresent"/> is false, so it can never run
    /// against a GI-managed renderer.</para>
    /// </summary>
    internal sealed partial class FuelMonitorDisplay
    {
        internal const string OwnerId = "Y4NGZShipSystems:FuelMonitor";

        private const int RenderWidth = 320;
        private const int RenderHeight = 256;
        private const float RebindSeconds = 0.10f;
        private readonly FuelReadoutAnimation _readout = new FuelReadoutAnimation();

        /// <summary>
        /// Ceremonies at or above this priority paint the WHOLE wall, the configured slot
        /// included, so the fuel monitor hands its slot back for the duration rather than
        /// fighting them at the property-block layer. The full-screen takeover, the round-end
        /// performance report and any safety message all sit at or above it.
        ///
        /// <para><b>#682: the quota announcement is deliberately NOT in this set any more.</b>
        /// It was, from back when the reward presentation repainted every surface. Since #680
        /// (commit 500811b) it binds only the three large lower monitors and the map screen —
        /// <c>GeneralImprovementsMonitors.EnumerateAnnouncementSurfaces</c> — so a fuel readout
        /// on one of the small upper slots is not a surface it wants. Standing down for it
        /// anyway cleared GI's text canvas and left an empty navy screen beside a presentation
        /// that had no use for it. The slot-level overlap that <em>can</em> still happen is
        /// answered precisely by <see cref="YieldsToAnnouncementOverlay"/> instead of by a
        /// wall-wide priority threshold.</para>
        /// </summary>
        private const MonitorPriority StandDownAtOrAbove = MonitorPriority.QuotaTakeover;

        private static FuelMonitorDisplay _instance;
        private static bool _surfaceRegistered;

        private readonly int _ownerToken = GeneralImprovementsMonitorPropertyBlock.NextOwnerToken();
        private readonly VanillaFuelMonitorPanel _vanillaPanel = new VanillaFuelMonitorPanel();

        private GameObject _rigRoot;
        private Camera _rigCamera;
        private Canvas _canvas;
        private RenderTexture _renderTexture;
        private TextMeshProUGUI _rigText;

        private GeneralImprovementsMonitorPropertyBlock _slotBinding;
        private MeshRenderer _boundRenderer;
        private int _boundMaterialIndex;
        private bool _slotHostsText;

        private MonitorLease _lease;
        private float _nextRebindAt;
        private int _lastPercent = -1;
        private string _body;
        private bool _standingDown;
        private string _standDownReason = string.Empty;
        private bool _loggedMissingSlot;
        private bool _loggedRibbonCollision;
        private bool _loggedOccupiedSlot;
        private string _resolvedSlotName = string.Empty;

        internal static FuelMonitorDisplay Instance => _instance;

        internal static void EnsureInstance()
        {
            if (_instance != null) return;
            _instance = new FuelMonitorDisplay();
        }

        internal static void Shutdown()
        {
            _instance?.Teardown();
            _instance = null;
        }

        private static bool FeatureEnabled =>
            ShipSystemsBootstrap.FuelMonitorEnabled == null ||
            ShipSystemsBootstrap.FuelMonitorEnabled.Value;

        private static string ConfiguredSlot =>
            ShipSystemsBootstrap.FuelMonitorGeneralImprovementsSlot != null
                ? ShipSystemsBootstrap.FuelMonitorGeneralImprovementsSlot.Value
                : "Screen9";

        /// <summary>
        /// True when a fuel readout can be hosted somewhere on this profile — GI's wall is up, or
        /// no wall mod is installed and the stock faces are ours to clone. Reports availability,
        /// not whether the readout is painting this instant.
        /// </summary>
        internal static bool HostAvailable =>
            GeneralImprovementsMonitors.BetterMonitorsActive ||
            !FuelMonitorHost.ForeignMonitorWallPresent;

        /// <summary>
        /// Drive from the ship-systems tick. Everything is re-evaluated every call: GI
        /// rebuilds its MonitorGroup on <c>StartOfRound.Start</c> and on client join, so no
        /// renderer, material index or lease is assumed to survive between ticks.
        /// </summary>
        internal void Tick()
        {
            try
            {
                if (!FeatureEnabled || !ShipSystemsController.IsFuelSystemEnabled())
                {
                    ReleaseEverything();
                    return;
                }

                ShipSystemsController.Instance?.PrewarmFuelMonitorAudio();
                if (_countdownSource != null) _countdownSource.transform.position = SoundPosition;

                if (GeneralImprovementsMonitors.BetterMonitorsActive)
                {
                    // GI owns the wall. Nothing vanilla may exist alongside it.
                    _vanillaPanel.Teardown();
                    TickGeneralImprovements();
                    return;
                }

                if (FuelMonitorHost.ForeignMonitorWallPresent)
                {
                    // A wall mod is installed but its replacement meshes are not up — GI with
                    // UseBetterMonitors off, or a group mid-rebuild. Either way the stock faces
                    // are not ours, and this is exactly the state #659 left behind.
                    ReleaseEverything();
                    return;
                }

                TickVanilla();
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning(
                    $"[LGUShipSystems.FuelMonitor] Tick failed; standing down. {ex.GetType().Name}: {ex.Message}");
                ReleaseEverything();
            }
        }

        // ---- host: GeneralImprovements ---------------------------------------------------

        private void TickGeneralImprovements()
        {
            EnsureSurfaceRegistered();
            EnsureLease();

            if (ShouldStandDown())
            {
                if (!_standingDown)
                {
                    _standingDown = true;
                    GeneralImprovementsMonitors.LogStandDown(
                        ShipSystemsBootstrap.Log,
                        "FuelMonitor",
                        _standDownReason.Length > 0
                            ? _standDownReason
                            : "a wall-wide ceremony owns the monitors");
                }
                ReleaseGeneralImprovementsSlot();
                return;
            }

            _standingDown = false;
            MaintainSlotBinding();
            RefreshContent();
        }

        // ---- host: vanilla ---------------------------------------------------------------

        private void TickVanilla()
        {
            // Nothing GI-shaped may be held while the stock wall is the host.
            ReleaseGeneralImprovementsSlot();
            DestroyRig();

            EnsureSurfaceRegistered();
            EnsureLease();

            if (ShouldStandDown())
            {
                _standingDown = true;
                _vanillaPanel.Hide();
                return;
            }

            _standingDown = false;
            if (!TryReadFuel(out float fill01)) return;

            int percent = Mathf.RoundToInt(fill01 * 100f);
            string nextBody = BuildBody(percent);
            if (nextBody != _body || _lastPercent < 0)
            {
                _lastPercent = percent;
                _body = BuildBody(percent);
            }

            // Unconditional, and cheap by construction: Show is two reference checks, two bool
            // compares and one string compare. It has to run every tick because it is also what
            // brings the panel back after a stand-down, which can end on an unchanged reading.
            if (_body != null) _vanillaPanel.Show(_body);
        }

        /// <summary>
        /// True while something that genuinely covers this slot owns a monitor surface. The
        /// arbiter's surfaces are independent, so a takeover holding <c>monitor.wall</c> does
        /// NOT displace a <c>monitor.power</c> lease on its own — these are the explicit checks
        /// that make the fuel monitor yield to it anyway.
        ///
        /// <para>Each clause names a reason the readout would be invisible or would fight
        /// somebody for pixels. A presentation that leaves this slot alone — the quota
        /// announcement on the large monitors, since #680 — matches none of them and the
        /// readout stays up, which is #682.</para>
        /// </summary>
        private bool ShouldStandDown()
        {
            // A takeover-strength ceremony: it owns the ship's whole presentation, this slot
            // included, whichever surface it took the lease on.
            if (MonitorArbiter.IsAnyHeldAtOrAbove(StandDownAtOrAbove))
            {
                _standDownReason = "a wall-wide ceremony owns the monitors";
                return true;
            }

            // monitor.wall IS "every screen as one surface", so any owner of it paints here,
            // at whatever priority it happened to ask for.
            if (MonitorArbiter.IsSurfaceRegistered(MonitorSurfaces.MonitorWall) &&
                MonitorArbiter.TryGetOwner(MonitorSurfaces.MonitorWall, out _))
            {
                _standDownReason = "the whole monitor wall is leased";
                return true;
            }

            // Somebody stronger took monitor.power itself.
            if (_lease != null && _lease.IsActive && !_lease.IsWinner)
            {
                _standDownReason = "a stronger claimant holds the power-monitor surface";
                return true;
            }

            if (YieldsToAnnouncementOverlay())
            {
                _standDownReason = "the configured slot is one of the announcement screens";
                return true;
            }

            if (CollidesWithRibbonRow())
            {
                _standDownReason = "the configured slot belongs to the monitor row ribbon";
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when a live announcement is painting the very renderer this readout is bound to.
        ///
        /// <para>The announcement claims <c>monitor.announcement</c>, a different surface from
        /// <c>monitor.power</c>, so the arbiter never displaces the fuel lease for it — and it
        /// should not, because the two normally describe different screens. The one case where
        /// they do not is a player who pointed <c>FuelMonitorGeneralImprovementsSlot</c> at one
        /// of the large announcement screens. Then the announcement's property-block lease and
        /// this one's are on the same renderer, both re-applying on a 0.10s cadence, and the
        /// weaker claimant has to step aside. Same shape as
        /// <see cref="CollidesWithRibbonRow"/>: ask the arbiter who owns the surface, then ask
        /// the GI bridge whether this renderer is in that surface's set. Neither question names
        /// the feature on the other side of the DLL split.</para>
        ///
        /// <para>Restricted to the property-block path on purpose. On GI's own text canvas
        /// there is nothing to fight over: GI keeps the text, the announcement overlays the
        /// face through a renderer-local block, and the readout is simply underneath it and
        /// intact when the block is released. Clearing the canvas there would blank the screen
        /// for the announcement's duration <em>and</em> for however long it takes the reading to
        /// change afterwards — the #682 failure, reintroduced one layer down.</para>
        /// </summary>
        private bool YieldsToAnnouncementOverlay()
        {
            if (_boundRenderer == null || _slotHostsText) return false;
            if (!MonitorArbiter.IsSurfaceRegistered(MonitorSurfaces.AnnouncementScreens)) return false;
            if (!MonitorArbiter.TryGetOwner(MonitorSurfaces.AnnouncementScreens, out _)) return false;

            // mirror: true is the superset — the mirrored set is the primary set plus the
            // remaining large screens — so this yields for either shape of the presentation
            // without having to know which one is running.
            var screens = GeneralImprovementsMonitors.EnumerateAnnouncementSurfaces(mirror: true);
            for (int i = 0; i < screens.Count; i++)
            {
                if (screens[i].renderer == _boundRenderer) return true;
            }
            return false;
        }

        /// <summary>
        /// The monitor row leases the UPPER small-screen ribbon. A configured slot inside that
        /// ribbon would be painted by both features every tick, so the fuel monitor yields and
        /// says so once — a config problem, not a runtime one.
        /// </summary>
        private bool CollidesWithRibbonRow()
        {
            if (!MonitorArbiter.IsSurfaceRegistered(MonitorSurfaces.MonitorRow)) return false;
            if (!MonitorArbiter.TryGetOwner(MonitorSurfaces.MonitorRow, out _)) return false;
            if (_boundRenderer == null) return false;

            var ribbon = GeneralImprovementsMonitors.EnumerateRibbonRow();
            for (int i = 0; i < ribbon.Count; i++)
            {
                if (ribbon[i].Renderer != _boundRenderer) continue;
                if (!_loggedRibbonCollision)
                {
                    _loggedRibbonCollision = true;
                    ShipSystemsBootstrap.Log?.LogWarning(
                        $"[LGUShipSystems.FuelMonitor] Configured slot '{_resolvedSlotName}' is part of the " +
                        "monitor row ribbon, which already owns it; the fuel monitor is standing down. " +
                        "Pick a bottom-row slot (Screen7..Screen12) instead.");
                }
                return true;
            }
            return false;
        }

        private static void EnsureSurfaceRegistered()
        {
            if (_surfaceRegistered) return;
            _surfaceRegistered = MonitorArbiter.RegisterSurface(
                MonitorSurfaces.PowerMonitor, new SurfaceBinding());
        }

        private void EnsureLease()
        {
            if (_lease != null && _lease.IsActive) return;

            _lease = MonitorArbiter.Acquire(
                new MonitorLeaseRequest(
                    OwnerId,
                    MonitorSurfaces.PowerMonitor,
                    MonitorPriority.Ambient,
                    reason: "ship fuel readout",
                    displaced: OnDisplaced),
                out MonitorLeaseOutcome outcome);

            if (outcome != MonitorLeaseOutcome.Granted && outcome != MonitorLeaseOutcome.Deferred)
                _lease = null;
        }

        /// <summary>
        /// Raised by the arbiter from inside the call that displaced this lease, before the new
        /// winner snapshots anything. This class paints the hardware itself, so it MUST hand
        /// the slot back here rather than on its next tick — otherwise the incoming winner
        /// records the fuel readout as the slot's original content. It must not throw and must
        /// not release its own lease: the lease is what gets the slot back afterwards.
        /// </summary>
        private void OnDisplaced()
        {
            try
            {
                _standingDown = true;
                ReleaseGeneralImprovementsSlot();
                _vanillaPanel.Hide();
            }
            catch
            {
                // A displaced callback must never throw.
            }
        }

        // ---- GI single-slot binding ------------------------------------------------------

        /// <summary>
        /// Re-resolves the configured slot on a 0.10s cadence and rebinds when GI has swapped
        /// renderer identities, choosing between the two GI hosts as it goes: GI's own text
        /// canvas when the slot is free, the property-block lease otherwise.
        /// </summary>
        private void MaintainSlotBinding()
        {
            float now = Time.unscaledTime;
            bool due = now >= _nextRebindAt;
            bool bindingLive = _slotHostsText
                ? _boundRenderer != null
                : _slotBinding != null && _slotBinding.IsAlive;
            if (!due && bindingLive) return;
            if (due) _nextRebindAt = now + RebindSeconds;

            string slotName = ConfiguredSlot;
            if (!FuelMonitorHost.TryResolveSlot(slotName, out FuelMonitorHost.Slot slot) ||
                slot.Renderer == null)
            {
                if (!_loggedMissingSlot)
                {
                    _loggedMissingSlot = true;
                    ShipSystemsBootstrap.Log?.LogWarning(
                        $"[LGUShipSystems.FuelMonitor] GeneralImprovements has no monitor named '{slotName}'. " +
                        $"Available slots: {string.Join(", ", GeneralImprovementsMonitors.EnumerateScreenNames().ToArray())}. " +
                        "The fuel monitor is standing down until the slot is corrected.");
                }
                ReleaseGeneralImprovementsSlot();
                return;
            }

            _loggedMissingSlot = false;
            _resolvedSlotName = slotName;

            bool wantsText = slot.CanHostText;
            if (!wantsText && slot.Text != null && !slot.Unassigned && !_loggedOccupiedSlot)
            {
                _loggedOccupiedSlot = true;
                ShipSystemsBootstrap.Log?.LogWarning(
                    $"[LGUShipSystems.FuelMonitor] GeneralImprovements has its own monitor assigned to " +
                    $"'{slotName}', so the fuel readout cannot use GI's text canvas there and falls back to " +
                    "painting the screen itself. Point FuelMonitorGeneralImprovementsSlot at a screen set to " +
                    "None in GeneralImprovements' ShipMonitor config for a seamless match.");
            }

            bool sameSurface = _boundRenderer == slot.Renderer &&
                               _boundMaterialIndex == slot.MaterialIndex &&
                               _slotHostsText == wantsText;
            if (sameSurface && (wantsText || (_slotBinding != null && _slotBinding.IsAlive)))
            {
                if (!wantsText) _slotBinding.Apply(_renderTexture, Vector2.one, Vector2.zero);
                return;
            }

            ReleaseGeneralImprovementsSlot();
            _boundRenderer = slot.Renderer;
            _boundMaterialIndex = slot.MaterialIndex;
            _slotHostsText = wantsText;
            _lastPercent = -1;

            if (!wantsText)
            {
                EnsureRig();
                _slotBinding = new GeneralImprovementsMonitorPropertyBlock(
                    slot.Renderer, slot.MaterialIndex, _ownerToken);
                _slotBinding.Apply(_renderTexture, Vector2.one, Vector2.zero);
            }

            ShipSystemsBootstrap.Log?.LogInfo(
                $"[LGUShipSystems.FuelMonitor] Bound the fuel readout to GeneralImprovements slot " +
                $"'{_resolvedSlotName}' via " +
                (wantsText
                    ? "GeneralImprovements' own text canvas."
                    : "the property-block lease."));
        }

        private void ReleaseGeneralImprovementsSlot()
        {
            // Called unconditionally from the vanilla tick, so it has to be free when there is
            // nothing bound — otherwise it would reset _lastPercent every frame and defeat the
            // content throttle on the path that never binds a GI slot at all.
            if (_boundRenderer == null && _slotBinding == null) return;

            if (_slotHostsText && _boundRenderer != null)
            {
                if (FuelMonitorHost.TryResolveSlot(_resolvedSlotName, out FuelMonitorHost.Slot slot) &&
                    slot.Renderer == _boundRenderer)
                {
                    FuelMonitorHost.TrySetText(slot, string.Empty);
                }
            }

            _slotBinding?.Release();
            _slotBinding = null;
            _boundRenderer = null;
            _boundMaterialIndex = 0;
            _slotHostsText = false;
            _lastPercent = -1;
        }

        private void ReleaseEverything()
        {
            _readout.Reset();
            if (_countdownSource != null) { UnityEngine.Object.Destroy(_countdownSource.gameObject); _countdownSource = null; }
            ReleaseGeneralImprovementsSlot();
            _vanillaPanel.Teardown();
            _lastPercent = -1;
            _body = null;
            if (_lease != null)
            {
                MonitorArbiter.Release(_lease);
                _lease = null;
            }
            _standingDown = false;
        }

        internal void Teardown()
        {
            ReleaseEverything();
            DestroyRig();
        }

        // ---- content ---------------------------------------------------------------------

        private bool TryReadFuel(out float fill01)
        {
            fill01 = 0f;
            ShipSystemsController controller = ShipSystemsController.Instance;
            if (controller == null) return false;
            float capacity = Mathf.Max(1f, controller.FuelCapacity);
            float before = _readout.Value;
            float shown = _readout.Sample(_holdLandingReadout && _readout.IsInitialized ? before : controller.Fuel, capacity, Time.unscaledTime);
            if (!_holdLandingReadout && Time.unscaledTime <= _landingSoundUntil && shown < before)
            {
                _landingSoundUntil = -1f;
                PlayCountdownSound(controller.FuelMonitorCountdownClip);
            }
            fill01 = Mathf.Clamp01(shown / capacity);
            return true;
        }

        /// <summary>
        /// GI's own monitor grammar: an all-caps label, a colon, a newline, then the value. It is
        /// what DOOR POWER, SHIP SCRAP, CREDITS and TOTAL DEATHS all read like, so a screen
        /// written this way is indistinguishable from one of GI's.
        /// </summary>
        private string BuildBody(int percent)
        {
            var controller = ShipSystemsController.Instance;
            if (controller == null) return "SHIP FUEL:\n--";
            return "<align=center><size=85%>SHIP FUEL:</size>\n" + Colorize(FuelMonitorTankSprite.Readout(_readout.Value, controller.FuelCapacity), percent) + "</align>";
        }

        /// <summary>
        /// The same four-step green/yellow/orange/red ladder GI paints its own deadline and
        /// player-count values with, and suppressed by the same switch — a player who turned
        /// GI's extra monitor colours off does not want one screen still shouting.
        /// </summary>
        private static string Colorize(string value, int percent)
        {
            if (!FuelMonitorHost.UseMoreMonitorTextColors) return value;
            string hex =
                percent > 25 ? "00ff00" :
                percent > 10 ? "ffff00" :
                percent > 0 ? "ff8800" :
                "ff0000";
            return "<color=#" + hex + ">" + value + "</color>";
        }

        private void RefreshContent()
        {
            if (_boundRenderer == null) return;
            if (!TryReadFuel(out float fill01)) return;

            int percent = Mathf.RoundToInt(fill01 * 100f);
            string nextBody = BuildBody(percent);
            if (nextBody == _body && _lastPercent >= 0) return;

            string body = BuildBody(percent);

            if (_slotHostsText)
            {
                if (!FuelMonitorHost.TryResolveSlot(_resolvedSlotName, out FuelMonitorHost.Slot slot) ||
                    slot.Renderer != _boundRenderer ||
                    !FuelMonitorHost.TrySetText(slot, body))
                {
                    // GI swapped the wall out from under the resolve; the next MaintainSlotBinding
                    // rebinds. Do not record the value as shown.
                    return;
                }
                _body = body;
                _lastPercent = percent;
                return;
            }

            if (_slotBinding == null) return;
            EnsureRig();
            if (_renderTexture == null || _rigText == null) return;
            _lastPercent = percent;
            FuelMonitorTankSprite.Install(_rigText);
            _rigText.text = body;
            RenderNow();
            _body = body;
        }

        /// <summary>
        /// Manual render only. The camera is <c>enabled = false</c> and the canvas is enabled
        /// solely for the duration of this call, so the rig costs nothing on frames where the
        /// readout has not changed.
        /// </summary>
        private void RenderNow()
        {
            if (_rigCamera == null || _canvas == null) return;
            _canvas.enabled = true;
            try
            {
                Canvas.ForceUpdateCanvases();
                _rigText?.ForceMeshUpdate(true, true);
                _rigCamera.Render();
            }
            finally
            {
                _canvas.enabled = false;
            }
        }

        // ---- offscreen rig (GI fallback only) --------------------------------------------

        private void EnsureRig()
        {
            if (_rigRoot != null &&
                _rigCamera != null &&
                _canvas != null &&
                _renderTexture != null &&
                _renderTexture.IsCreated())
            {
                return;
            }

            DestroyRig();

            FuelMonitorHost.Style style = FuelMonitorHost.ResolveStyle(_boundRenderer);

            _rigRoot = new GameObject("Y4NGZ_FuelMonitorRig");
            UnityEngine.Object.DontDestroyOnLoad(_rigRoot);
            // Reuses the PowerFuel parking slot, which has been reserved and unused since the
            // #659 removal — no new slot, so no Core change.
            _rigRoot.transform.position = new Vector3(
                OffscreenRigParking.SlotX(OffscreenRigSlot.PowerFuel),
                OffscreenRigParking.ParkingHeight,
                0f);

            _renderTexture = new RenderTexture(RenderWidth, RenderHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "Y4NGZ_FuelMonitorRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                antiAliasing = 1
            };
            if (!_renderTexture.Create())
            {
                DestroyRig();
                throw new InvalidOperationException("Fuel monitor RenderTexture.Create() failed.");
            }

            var cameraGo = new GameObject("FuelMonitorCamera");
            cameraGo.transform.SetParent(_rigRoot.transform, false);
            cameraGo.transform.localPosition = new Vector3(0f, 0f, -10f);
            _rigCamera = cameraGo.AddComponent<Camera>();
            _rigCamera.clearFlags = CameraClearFlags.SolidColor;
            _rigCamera.backgroundColor = style.BackgroundColor;
            _rigCamera.orthographic = true;
            _rigCamera.orthographicSize = RenderHeight * 0.5f;
            _rigCamera.aspect = RenderWidth / (float)RenderHeight;
            _rigCamera.nearClipPlane = 0.1f;
            _rigCamera.farClipPlane = 30f;
            _rigCamera.cullingMask = 1 << 5;
            _rigCamera.targetTexture = _renderTexture;
            _rigCamera.enabled = false;

            // Under HDRP a manually driven camera without HDAdditionalCameraData cannot
            // resolve into its target texture and stomps the backbuffer with its clear
            // colour instead. Same treatment the quota announcement rig uses.
            HDAdditionalCameraData hdrp = cameraGo.AddComponent<HDAdditionalCameraData>();
            hdrp.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            hdrp.backgroundColorHDR = _rigCamera.backgroundColor;
            hdrp.volumeLayerMask = 0;
            Y4NGZCompany.ShipSystems.Rendering.CameraRenderProfile.ApplyProfile(
                _rigCamera, Y4NGZCompany.ShipSystems.Rendering.CameraRenderRole.Unlit);

            var canvasGo = new GameObject("FuelMonitorCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(_rigRoot.transform, false);
            canvasGo.layer = 5;
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.worldCamera = _rigCamera;
            _canvas.enabled = false;
            RectTransform canvasRt = canvasGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(RenderWidth, RenderHeight);

            Image background = canvasGo.AddComponent<Image>();
            background.color = style.BackgroundColor;
            background.raycastTarget = false;

            // One plain text block, exactly like a GI text monitor: no header rule, no gauge,
            // no status strip. Auto-sizing stands in for GI's hand-tuned per-screen font size,
            // which is not readable from outside its prefab.
            var textGo = new GameObject("Readout", typeof(RectTransform));
            textGo.transform.SetParent(canvasRt, false);
            textGo.layer = 5;
            _rigText = textGo.AddComponent<TextMeshProUGUI>();
            if (style.Font != null) _rigText.font = style.Font;
            if (style.Sprites != null) _rigText.spriteAsset = style.Sprites;
            _rigText.color = style.TextColor;
            _rigText.alignment = style.Centered
                ? TextAlignmentOptions.Center
                : TextAlignmentOptions.TopLeft;
            _rigText.raycastTarget = false;
            _rigText.enableWordWrapping = true;
            _rigText.richText = true;
            _rigText.enableAutoSizing = true;
            _rigText.fontSizeMin = 18f;
            _rigText.fontSizeMax = 72f;
            _rigText.margin = new Vector4(16f, 12f, 16f, 12f);
            _rigText.text = string.Empty;

            RectTransform textRt = _rigText.rectTransform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            _lastPercent = -1;
        }


        private void DestroyRig()
        {
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                UnityEngine.Object.Destroy(_renderTexture);
                _renderTexture = null;
            }
            if (_rigRoot != null)
            {
                UnityEngine.Object.Destroy(_rigRoot);
                _rigRoot = null;
            }
            _rigCamera = null;
            _canvas = null;
            _rigText = null;
        }

        /// <summary>
        /// The arbiter-facing registration for <c>monitor.power</c>. Capture, restore and apply
        /// are deliberately inert. Under GI the save/restore contract belongs to the
        /// property-block lease, which snapshots and restores per renderer and refuses to
        /// clobber a newer owner; on the text and vanilla paths there is no hardware state to
        /// snapshot at all, only Y4NGZ-owned objects that hide themselves. Doing it here as well
        /// would be the duelling-snapshot bug the arbiter exists to prevent. Same shape as
        /// <c>MonitorRowSurface.SurfaceBinding</c> in its GI mode.
        /// </summary>
        private sealed class SurfaceBinding : IMonitorSurfaceBinding
        {
            public bool IsAvailable => HostAvailable;

            public MonitorSurfaceInfo Info => GeneralImprovementsMonitors.BetterMonitorsActive
                ? new MonitorSurfaceInfo(
                    MonitorSurfaces.PowerMonitor,
                    MonitorSurfaceBacking.GeneralImprovements,
                    detail: "ship fuel readout on a GeneralImprovements monitor")
                : new MonitorSurfaceInfo(
                    MonitorSurfaces.PowerMonitor,
                    MonitorSurfaceBacking.Vanilla,
                    detail: "ship fuel readout on a stock ship monitor");

            public object CaptureSnapshot() => null;

            public void RestoreSnapshot(object snapshot) { }

            public void Apply(MonitorLease lease) { }
        }
    }
}
