using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    /// <summary>
    /// The fuel readout on a stock ship monitor, for profiles with no monitor-wall mod at all
    /// (#678).
    ///
    /// <para><b>How it is a near-identical match rather than an imitation.</b> It does not draw a
    /// vanilla-looking screen; it <em>clones the vanilla screen</em>. Both objects are
    /// <c>Object.Instantiate</c> copies of <c>StartOfRound.profitQuotaMonitorBGImage</c> and
    /// <c>StartOfRound.profitQuotaMonitorText</c>, dropped back onto their own parents at a
    /// sibling monitor's pose. Font, font size, colour, material, backing plate, canvas and
    /// scale are the quota monitor's, because they are the same components — the only things
    /// this class sets are the transform, the object name and the string.</para>
    ///
    /// <para><b>Why this cannot reproduce the #659 flood.</b> The flood was a material problem:
    /// cloned <c>Material</c> instances, <c>sharedMaterial</c> assignment, and an unconditional
    /// forced-white <c>_EmissiveColor</c> pushed through a MaterialPropertyBlock onto renderers
    /// whose shader had no emissive map to sample. Nothing here is a material. There is no
    /// <c>Material</c>, no <c>MaterialPropertyBlock</c>, no <c>Renderer</c>, no shader keyword
    /// and no <c>RenderTexture</c> in this file — only two UI graphics on the ship's existing
    /// world-space monitor canvas, which is the same mechanism the game itself uses for the
    /// quota screen. It is also unreachable whenever a monitor-wall mod is loaded: see
    /// <see cref="FuelMonitorHost.ForeignMonitorWallPresent"/>.</para>
    ///
    /// <para><b>And it is not per-frame work.</b> The clones are built once per ship and then
    /// left alone; the string is assigned only when the whole-percent reading changes.</para>
    /// </summary>
    internal sealed class VanillaFuelMonitorPanel
    {
        /// <summary>
        /// Pose of the top-left small screen, relative to the quota monitor's own backing plate.
        ///
        /// <para>These are GeneralImprovements' own vanilla-overlay numbers for monitor slot 1
        /// (<c>MonitorsHelper.CreateOldStyleMonitors</c>), used here because they are the
        /// best-tested description in the ecosystem of where a stock ship's spare monitor faces
        /// actually are — measured against the shipped mesh rather than guessed. Slot 1 is
        /// chosen because the stock game leaves it blank: slots 5 and 6 are the quota and
        /// deadline screens, and the right-hand pair carries the internal camera.</para>
        /// </summary>
        private static Vector3 SlotOffset => new Vector3(ShipSystemsBootstrap.VanillaFuelMonitorSlot?.Value == 2 ? 470f : 0f, 465f, -22f);
        private static readonly Vector3 SlotRotation = new Vector3(-18f, 0f, 0f);

        /// <summary>The text sits a hair in front of its plate, exactly as GI places its own.</summary>
        private static readonly Vector3 TextOffset = new Vector3(0f, 0f, -1f);
        private static readonly Vector3 TextRotation = new Vector3(1f, 0f, 0f);

        private Image _plate;
        private TextMeshProUGUI _text;
        private string _lastText = string.Empty;
        private bool _loggedBuild;
        private bool _loggedMissingHost;

        internal Vector3 SoundPosition => _plate != null ? _plate.transform.position : Vector3.zero;
        internal bool IsBuilt => _plate != null && _text != null;

        /// <summary>
        /// Builds the panel if it is not up, then writes <paramref name="body"/> onto it. Safe to
        /// call every tick: everything past the first call is two reference checks and a string
        /// comparison.
        /// </summary>
        internal void Show(string body)
        {
            if (!EnsureBuilt()) return;
            FuelMonitorTankSprite.Install(_text);

            // Follow whatever the game is doing with the real quota screen, so the fuel panel
            // goes dark with the rest of the wall instead of floating on an unpowered ship.
            TextMeshProUGUI source = SourceText;
            bool visible = source == null || source.enabled;
            if (_text.enabled != visible) _text.enabled = visible;
            if (_plate.enabled != visible) _plate.enabled = visible;

            if (string.Equals(_lastText, body, StringComparison.Ordinal)) return;
            _lastText = body;
            _text.text = body;
        }

        /// <summary>
        /// Hides the panel without tearing it down — what a ceremony or a displaced lease wants,
        /// because the pose and the clones are still correct when the surface comes back.
        /// </summary>
        internal void Hide()
        {
            if (_text != null) _text.enabled = false;
            if (_plate != null) _plate.enabled = false;
        }

        internal void Teardown()
        {
            if (_text != null) UnityEngine.Object.Destroy(_text.gameObject);
            if (_plate != null) UnityEngine.Object.Destroy(_plate.gameObject);
            _text = null;
            _plate = null;
            _lastText = string.Empty;
        }

        private static Image SourcePlate =>
            StartOfRound.Instance != null ? StartOfRound.Instance.profitQuotaMonitorBGImage : null;

        private static TextMeshProUGUI SourceText =>
            StartOfRound.Instance != null ? StartOfRound.Instance.profitQuotaMonitorText : null;

        private bool EnsureBuilt()
        {
            if (IsBuilt) return true;

            // A half-surviving panel means the ship was rebuilt under us; drop the remnant so the
            // rebuild below is not competing with a stale clone.
            if (_plate != null || _text != null) Teardown();

            Image plateSource = SourcePlate;
            TextMeshProUGUI textSource = SourceText;
            if (plateSource == null || textSource == null ||
                plateSource.transform.parent == null || textSource.transform.parent == null)
            {
                if (!_loggedMissingHost)
                {
                    _loggedMissingHost = true;
                    ShipSystemsBootstrap.Log?.LogInfo(
                        "[LGUShipSystems.FuelMonitor] The vanilla quota monitor is not built yet; " +
                        "the fuel panel will appear once it is.");
                }
                return false;
            }

            Vector3 plateOrigin = plateSource.transform.localPosition;
            Vector3 plateAngles = plateSource.transform.localEulerAngles;

            _plate = UnityEngine.Object.Instantiate(plateSource, plateSource.transform.parent);
            _plate.name = "Y4NGZ_ShipFuelMonitorBG";
            _plate.enabled = true;
            _plate.transform.localPosition = plateOrigin + SlotOffset;
            _plate.transform.localEulerAngles = plateAngles + SlotRotation;

            _text = UnityEngine.Object.Instantiate(textSource, textSource.transform.parent);
            _text.name = "Y4NGZ_ShipFuelMonitorText";
            _text.enabled = true;
            _text.transform.localPosition = plateOrigin + SlotOffset + TextOffset;
            _text.transform.localEulerAngles = plateAngles + SlotRotation + TextRotation;
            _text.text = string.Empty;
            _lastText = string.Empty;

            // A cloned quota screen inherits the quota screen's scan node, which would put a
            // second "Quota" scannable on the wall reading the fuel level.
            foreach (ScanNodeProperties node in _text.GetComponentsInChildren<ScanNodeProperties>(true))
                UnityEngine.Object.Destroy(node.gameObject);
            foreach (ScanNodeProperties node in _plate.GetComponentsInChildren<ScanNodeProperties>(true))
                UnityEngine.Object.Destroy(node.gameObject);

            if (!_loggedBuild)
            {
                _loggedBuild = true;
                ShipSystemsBootstrap.Log?.LogInfo(
                    "[LGUShipSystems.FuelMonitor] No monitor-wall mod is loaded; the fuel readout is " +
                    "on the stock ship monitor above the profit quota screen, cloned from it.");
            }
            return true;
        }
    }
}
