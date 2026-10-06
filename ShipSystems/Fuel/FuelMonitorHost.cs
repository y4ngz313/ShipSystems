using System;
using System.Reflection;
using BepInEx.Bootstrap;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    /// <summary>
    /// Everything the fuel monitor needs to know about <em>who owns the ship's monitor wall</em>
    /// and <em>what that owner's screens look like</em> (#678).
    ///
    /// <para><b>Why this is here and not in Y4NGZCore.</b> <c>GeneralImprovementsMonitors</c> is
    /// the shared bridge for <i>painting</i> GI surfaces — renderers, material indices, property
    /// blocks. It deliberately knows nothing about GI's text canvases, its palette, or which
    /// mods replaced the wall, because nothing else in the repository needs those. This file is
    /// the fuel monitor's own, additive view of the same API and stays local to the feature.</para>
    ///
    /// <para><b>No material writes anywhere in this file.</b> The #659 white-screen flood came
    /// from cloned materials and a forced <c>_EmissiveColor</c>. The two things here are
    /// reflection reads and one call into GI's <em>public</em> <c>MonitorInfo.QueueRender</c>,
    /// which asks GI to re-render its own text canvas with its own camera onto its own
    /// material. Nothing in this assembly touches a renderer, a material or a shader keyword
    /// through it.</para>
    /// </summary>
    internal static class FuelMonitorHost
    {
        internal const string GeneralImprovementsGuid = "ShaosilGaming.GeneralImprovements";

        private const string MonitorsApiTypeName =
            "GeneralImprovements.API.MonitorsAPI, GeneralImprovements";
        private const string GiPluginTypeName =
            "GeneralImprovements.Plugin, GeneralImprovements";

        /// <summary>GI's own defaults, used when its config members cannot be read.</summary>
        private static readonly Color FallbackTextColor = HexColor(0x00, 0xFF, 0x2C);
        private static readonly Color FallbackBackgroundColor = HexColor(0x16, 0x09, 0x59);

        private static bool _resolved;
        private static MethodInfo _getMonitorAtIndex;
        private static MethodInfo _queueRender;
        private static MemberInfo _meshRendererMember;
        private static MemberInfo _textCanvasMember;
        private static MemberInfo _assignmentMember;
        private static MemberInfo _screenMaterialIndexMember;

        private static bool _foreignWallProbed;
        private static bool _foreignWallPresent;

        private static bool _colorsProbed;
        private static MemberInfo _textColorMember;
        private static MemberInfo _backgroundColorMember;
        private static MemberInfo _moreTextColorsMember;
        private static MemberInfo _centerAlignMember;

        // ---- who owns the wall ---------------------------------------------------------

        /// <summary>
        /// True when a mod that replaces or re-skins the ship monitor wall is loaded, whether or
        /// not its replacement meshes are currently up.
        ///
        /// <para>This is the gate on the vanilla path, and it is deliberately stricter than
        /// <c>GeneralImprovementsMonitors.BetterMonitorsActive</c>. That property answers "are
        /// GI's meshes on screen this frame", which is false during a group rebuild and false
        /// when GI is installed with <c>UseBetterMonitors=false</c> — and in the second case GI
        /// is nevertheless authoring the vanilla monitor faces itself, including the very
        /// profit-quota overlay the vanilla path clones. Cloning on top of that would produce
        /// two panels in one place. Chainloader membership is the only signal that answers
        /// "is anybody else responsible for these faces", so that is what gates the clone.</para>
        /// </summary>
        internal static bool ForeignMonitorWallPresent
        {
            get
            {
                if (_foreignWallProbed) return _foreignWallPresent;
                _foreignWallProbed = true;
                try
                {
                    if (Chainloader.PluginInfos == null) return _foreignWallPresent = false;
                    if (Chainloader.PluginInfos.ContainsKey(GeneralImprovementsGuid))
                        return _foreignWallPresent = true;

                    // A standalone Better Monitors-style mod under any GUID. Cheap, computed
                    // once, and it fails safe: an unrecognised wall mod costs the fuel monitor
                    // its vanilla panel, never a duplicated or fought-over one.
                    foreach (string guid in Chainloader.PluginInfos.Keys)
                    {
                        if (guid != null &&
                            guid.IndexOf("bettermonitor", StringComparison.OrdinalIgnoreCase) >= 0)
                            return _foreignWallPresent = true;
                    }
                }
                catch
                {
                    // A Chainloader we cannot read is not evidence of a wall mod.
                }
                return _foreignWallPresent;
            }
        }

        // ---- GI text-canvas slots -------------------------------------------------------

        /// <summary>
        /// One GI monitor slot, as much of it as the fuel monitor cares about. Never cached
        /// across ticks: GI destroys and rebuilds MonitorGroup on every
        /// <c>StartOfRound.Start</c> and on client join.
        /// </summary>
        internal readonly struct Slot
        {
            internal Slot(
                object monitorInfo,
                MeshRenderer renderer,
                int materialIndex,
                TextMeshProUGUI text,
                bool unassigned)
            {
                MonitorInfo = monitorInfo;
                Renderer = renderer;
                MaterialIndex = materialIndex;
                Text = text;
                Unassigned = unassigned;
            }

            internal object MonitorInfo { get; }
            internal MeshRenderer Renderer { get; }

            /// <summary>
            /// GI's <c>ScreenMaterialIndex</c>. Always 0 on today's screens; read anyway so a
            /// future multi-slot screen does not get slot 0 painted.
            /// </summary>
            internal int MaterialIndex { get; }

            internal TextMeshProUGUI Text { get; }

            /// <summary>True when GI has no monitor of its own assigned to this screen.</summary>
            internal bool Unassigned { get; }

            /// <summary>
            /// True when this slot can carry Y4NGZ text without fighting GI for it: GI exposes a
            /// text canvas AND has nothing of its own scheduled to overwrite it.
            /// </summary>
            internal bool CanHostText => MonitorInfo != null && Text != null && Unassigned;
        }

        /// <summary>
        /// Resolves the GI slot whose screen mesh is named <paramref name="screenName"/>. Name
        /// matching is on the renderer's own GameObject name, which is what
        /// <c>GeneralImprovementsMonitors.TryResolveScreenByName</c> matches on too, so a config
        /// value that resolves for the property-block path resolves here as well.
        /// </summary>
        internal static bool TryResolveSlot(string screenName, out Slot slot)
        {
            slot = default;
            string wanted = NormalizeScreenName(screenName);
            if (wanted.Length == 0) return false;

            Resolve();
            if (_getMonitorAtIndex == null || _meshRendererMember == null) return false;

            try
            {
                for (int i = 0; i < 64; i++)
                {
                    object info = _getMonitorAtIndex.Invoke(null, new object[] { i });
                    if (info == null) break;

                    var renderer = ReadMember(_meshRendererMember, info) as MeshRenderer;
                    if (renderer == null) continue;
                    if (!string.Equals(renderer.gameObject.name, wanted, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var text = ReadMember(_textCanvasMember, info) as TextMeshProUGUI;
                    object assignment = ReadMember(_assignmentMember, info);
                    // eMonitorNames.None is 0 and is the only value that means "GI paints
                    // nothing here". Compared as an integer so no GI enum type is referenced.
                    bool unassigned = assignment == null ||
                                      (assignment is Enum && Convert.ToInt32(assignment) == 0);

                    int materialIndex = 0;
                    if (ReadMember(_screenMaterialIndexMember, info) is int declared && declared >= 0)
                        materialIndex = declared;
                    Material[] materials = renderer.sharedMaterials;
                    if (materials == null || materials.Length == 0) return false;
                    if (materialIndex >= materials.Length) materialIndex = 0;

                    slot = new Slot(info, renderer, materialIndex, text, unassigned);
                    return true;
                }
            }
            catch
            {
                slot = default;
            }
            return slot.Renderer != null;
        }

        /// <summary>Accepts "Screen9", "screen 9" and a bare "9" alike — same rule as Core.</summary>
        private static string NormalizeScreenName(string screenName)
        {
            if (string.IsNullOrWhiteSpace(screenName)) return string.Empty;
            string compact = screenName.Trim().Replace(" ", string.Empty).Replace("_", string.Empty);
            if (compact.Length == 0) return string.Empty;

            for (int i = 0; i < compact.Length; i++)
            {
                if (compact[i] < '0' || compact[i] > '9') return compact;
            }
            return "Screen" + compact;
        }

        /// <summary>
        /// Hands <paramref name="text"/> to GI and asks it to re-render the slot.
        ///
        /// <para>This is the whole reason the GI path looks like GI: the glyphs are drawn by
        /// GI's own <c>TextMeshProUGUI</c>, in GI's font, at GI's size, in GI's text colour, over
        /// GI's background, by GI's per-monitor camera, onto GI's material. There is nothing
        /// left for a Y4NGZ style to get wrong.</para>
        ///
        /// <para><c>QueueRender</c> logs and refuses when the text is unchanged, so callers must
        /// only call it on an actual change.</para>
        /// </summary>
        internal static bool TrySetText(in Slot slot, string text)
        {
            FuelMonitorTankSprite.Install(slot.Text);
            Resolve();
            if (_queueRender == null || slot.MonitorInfo == null || slot.Text == null) return false;

            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    // QueueRender treats an empty argument as "refresh what is already there",
                    // so the clear has to be written onto the canvas first.
                    slot.Text.text = string.Empty;
                    _queueRender.Invoke(slot.MonitorInfo, new object[] { string.Empty });
                    return true;
                }

                if (string.Equals(slot.Text.text, text, StringComparison.Ordinal)) return true;
                object result = _queueRender.Invoke(slot.MonitorInfo, new object[] { text });
                return !(result is bool ok) || ok;
            }
            catch
            {
                return false;
            }
        }

        // ---- GI palette, for the fallback rig -------------------------------------------

        /// <summary>
        /// The look of a GI text monitor, for the RenderTexture fallback that runs when GI is
        /// present but its text-canvas API is not (an older or newer GI than the one this was
        /// written against). Read from the live scene wherever possible, so a player who has
        /// recoloured their monitors gets a fuel screen that still matches them.
        /// </summary>
        internal readonly struct Style
        {
            internal Style(TMP_FontAsset font, TMP_SpriteAsset sprites, Color text, Color background, bool centered)
            {
                Font = font;
                Sprites = sprites;
                TextColor = text;
                BackgroundColor = background;
                Centered = centered;
            }

            internal TMP_FontAsset Font { get; }
            internal TMP_SpriteAsset Sprites { get; }
            internal Color TextColor { get; }
            internal Color BackgroundColor { get; }
            internal bool Centered { get; }
        }

        /// <summary>
        /// Resolves the monitor look. The font is always the game's own monitor font — GI copies
        /// it off <c>StartOfRound.profitQuotaMonitorText</c> for every screen it makes, so taking
        /// it from the same place is exact rather than approximate, and it is equally correct on
        /// the vanilla path where GI is not installed at all.
        /// </summary>
        internal static Style ResolveStyle(MeshRenderer nearScreen)
        {
            TMP_FontAsset font = null;
            TMP_SpriteAsset sprites = null;
            Color text = FallbackTextColor;
            Color background = FallbackBackgroundColor;
            bool centered = true;

            try
            {
                TextMeshProUGUI vanilla = StartOfRound.Instance != null
                    ? StartOfRound.Instance.profitQuotaMonitorText
                    : null;
                if (vanilla != null)
                {
                    font = vanilla.font;
                    sprites = vanilla.spriteAsset;
                }
            }
            catch
            {
                font = null;
            }

            ProbeColors();
            if (TryReadStaticColor(_textColorMember, out Color giText)) text = giText;
            if (TryReadStaticColor(_backgroundColorMember, out Color giBackground)) background = giBackground;
            if (TryReadStaticBool(_centerAlignMember, out bool giCentered)) centered = giCentered;

            // The live background beats the config value: it is what is actually on the wall.
            Image plate = FindMonitorGroupBackground(nearScreen);
            if (plate != null) background = plate.color;

            return new Style(font, sprites, text, background, centered);
        }

        /// <summary>
        /// GI colours its own values (green/yellow/orange/red) with inline rich-text tags when
        /// <c>UseMoreMonitorTextColors</c> is on, and leaves them plain when it is off. The fuel
        /// readout follows the same switch so it is never the one loud screen on a wall the
        /// player asked to keep monochrome.
        /// </summary>
        internal static bool UseMoreMonitorTextColors
        {
            get
            {
                ProbeColors();
                return TryReadStaticBool(_moreTextColorsMember, out bool enabled) ? enabled : true;
            }
        }

        /// <summary>
        /// GI exposes its booleans as <c>ConfigEntry&lt;bool&gt;</c>, not as bare fields, so the
        /// read has to step through <c>ConfigEntry.Value</c>. Both shapes are accepted rather
        /// than pinning one, for the same reason the MonitorInfo members are.
        /// </summary>
        private static bool TryReadStaticBool(MemberInfo member, out bool value)
        {
            value = false;
            if (member == null) return false;
            try
            {
                object read = ReadStaticMember(member);
                if (read is bool direct)
                {
                    value = direct;
                    return true;
                }
                if (read == null) return false;

                PropertyInfo entryValue = read.GetType().GetProperty(
                    "Value", BindingFlags.Public | BindingFlags.Instance);
                if (entryValue != null && entryValue.GetValue(read) is bool boxed)
                {
                    value = boxed;
                    return true;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private static Image FindMonitorGroupBackground(MeshRenderer nearScreen)
        {
            if (nearScreen == null) return null;
            try
            {
                for (Transform current = nearScreen.transform; current != null; current = current.parent)
                {
                    if (current.name == null ||
                        !current.name.StartsWith("MonitorGroup", StringComparison.Ordinal))
                        continue;
                    Transform plate = current.Find("Canvas/Background");
                    return plate != null ? plate.GetComponent<Image>() : null;
                }
            }
            catch
            {
                // A layout we do not recognise simply leaves the configured colour in place.
            }
            return null;
        }

        // ---- reflection ------------------------------------------------------------------

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                Type api = Type.GetType(MonitorsApiTypeName, throwOnError: false);
                if (api == null) return;

                _getMonitorAtIndex = api.GetMethod(
                    "GetMonitorAtIndex",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(int) },
                    modifiers: null);
                if (_getMonitorAtIndex == null) return;

                Type info = _getMonitorAtIndex.ReturnType;
                _meshRendererMember = FindMember(info, "MeshRenderer");
                _textCanvasMember = FindMember(info, "TextCanvas");
                _assignmentMember = FindMember(info, "Assignment");
                _screenMaterialIndexMember = FindMember(info, "ScreenMaterialIndex");
                _queueRender = info.GetMethod(
                    "QueueRender",
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null,
                    types: new[] { typeof(string) },
                    modifiers: null);
            }
            catch
            {
                _getMonitorAtIndex = null;
                _queueRender = null;
                _meshRendererMember = null;
                _textCanvasMember = null;
                _assignmentMember = null;
                _screenMaterialIndexMember = null;
            }
        }

        private static void ProbeColors()
        {
            if (_colorsProbed) return;
            _colorsProbed = true;
            try
            {
                Type plugin = Type.GetType(GiPluginTypeName, throwOnError: false);
                if (plugin == null) return;
                _textColorMember = FindStaticMember(plugin, "MonitorTextColorVal");
                _backgroundColorMember = FindStaticMember(plugin, "MonitorBackgroundColorVal");
                _moreTextColorsMember = FindStaticMember(plugin, "UseMoreMonitorTextColors");
                _centerAlignMember = FindStaticMember(plugin, "CenterAlignMonitorText");
            }
            catch
            {
                _textColorMember = null;
                _backgroundColorMember = null;
                _moreTextColorsMember = null;
                _centerAlignMember = null;
            }
        }

        private static bool TryReadStaticColor(MemberInfo member, out Color color)
        {
            color = default;
            if (member == null) return false;
            try
            {
                object value = ReadStaticMember(member);
                if (!(value is Color read)) return false;
                // GI parses its hex config before the wall exists; a black-and-transparent
                // value means "not initialised yet", not "the player wants an invisible screen".
                if (read.a <= 0f) return false;
                color = new Color(read.r, read.g, read.b, 1f);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            MemberInfo member = type.GetProperty(name, flags);
            return member ?? (MemberInfo)type.GetField(name, flags);
        }

        private static MemberInfo FindStaticMember(Type type, string name)
        {
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            MemberInfo member = type.GetProperty(name, flags);
            return member ?? (MemberInfo)type.GetField(name, flags);
        }

        private static object ReadMember(MemberInfo member, object instance)
        {
            switch (member)
            {
                case PropertyInfo property: return property.GetValue(instance);
                case FieldInfo field: return field.GetValue(instance);
                default: return null;
            }
        }

        private static object ReadStaticMember(MemberInfo member)
        {
            switch (member)
            {
                case PropertyInfo property: return property.GetValue(null);
                case FieldInfo field: return field.GetValue(null);
                default: return null;
            }
        }

        private static Color HexColor(int r, int g, int b) =>
            new Color(r / 255f, g / 255f, b / 255f, 1f);
    }
}
