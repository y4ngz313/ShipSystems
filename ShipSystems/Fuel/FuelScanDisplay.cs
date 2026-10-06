using System.Collections.Generic;
using Y4NGZCompany.Bootstrap;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    // The scanner animator writes colours after HUD.Update. Apply after animation,
    // and release our changes before vanilla/the UI theme use the pool again.
    [DefaultExecutionOrder(10000)]
    internal sealed class FuelScanDisplay : MonoBehaviour
    {
        private HUDManager _hud;
        private readonly Dictionary<RectTransform, Binding> _bindings = new Dictionary<RectTransform, Binding>();
        private static readonly Color Gold = FuelPalette.Gold;

        private bool _subscribed;
        private bool _loggedBinding;
        internal void Initialize(HUDManager hud)
        {
            // HUD objects may be rebuilt/cloned by UI mods; never rely on a private
            // HUD reference having survived a component clone.
            _hud = hud;
            if (!_subscribed) { Canvas.preWillRenderCanvases += BeforeCanvas; _subscribed = true; }
        }
        private void BeforeCanvas() { ApplyVisible(); }
        internal void Restore()
        {
            foreach (Binding binding in _bindings.Values) binding.Restore();
        }
        private void OnDisable()
        {
            if (_subscribed) Canvas.preWillRenderCanvases -= BeforeCanvas;
            _subscribed = false;
            Restore();
        }
        private void OnDestroy() { OnDisable(); _bindings.Clear(); }

        private void LateUpdate() { ApplyVisible(); }
        internal void ApplyVisible()
        {
            Restore();
            if (_hud == null) return;
#if UNITY_EDITOR
            // The authoring project's vanilla scripts are not assembly-publicized.
            var nodes = (Dictionary<RectTransform, ScanNodeProperties>)typeof(HUDManager).GetField("scanNodes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(_hud);
#else
            var nodes = _hud.scanNodes;
#endif
            if (nodes == null) return;
            foreach (var pair in nodes)
            {
                RectTransform root = pair.Key;
                ScanNodeProperties node = pair.Value;
                if (root == null || !root.gameObject.activeInHierarchy || node == null) continue;
                FuelLootItem fuel = node.GetComponentInParent<FuelLootItem>();
                bool chute = node.GetComponentInParent<FuelChuteController>() != null;
                if (fuel == null && !chute) continue;
                if (!_bindings.TryGetValue(root, out Binding binding))
                {
                    binding = new Binding(root);
                    _bindings.Add(root, binding);
                }
                binding.Apply(fuel != null ? (float?)fuel.FuelValue : null);
                if (!_loggedBinding)
                {
                    _loggedBinding = true;
                    ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.FuelScan] Bound live fuel marker: {node.headerText}; value={(fuel != null ? FuelPresentation.Amount(fuel.FuelValue) : "chute")}; graphics={root.GetComponentsInChildren<Graphic>(true).Length}.");
                }
            }
        }

        private sealed class Binding
        {
            private readonly Graphic[] _graphics;
            private readonly Color[] _colours;
            private readonly UnityEngine.UI.Outline[] _outlines;
            private readonly Color[] _outlineColours;
            private readonly LineRenderer[] _lines;
            private readonly Color[] _lineStarts, _lineEnds;
            private readonly TMP_Text _detail;
            private TMP_SpriteAsset _sprite, _installedSprite;
            private bool _richText, _applied, _detailApplied;
            private string _originalText, _fuelText;
            internal Binding(RectTransform root)
            {
                _graphics = root.GetComponentsInChildren<Graphic>(true);
                _colours = new Color[_graphics.Length];
                _outlines = root.GetComponentsInChildren<UnityEngine.UI.Outline>(true);
                _outlineColours = new Color[_outlines.Length];
                _lines = root.GetComponentsInChildren<LineRenderer>(true);
                _lineStarts = new Color[_lines.Length]; _lineEnds = new Color[_lines.Length];
                TMP_Text[] texts = root.GetComponentsInChildren<TMP_Text>(true);
                if (texts.Length > 1) _detail = texts[1];
            }
            private static Color GraphicTint(Graphic graphic, Color original)
            {
                // TMP glyphs sit on opaque gold plates. Sprite submeshes are also
                // foreground, including ones created lazily by TMP during a rebuild.
                if (graphic is TMP_Text || graphic is TMP_SubMeshUI)
                    return new Color(0.055f, 0.045f, 0.02f, original.a);
                return Tint(original);
            }
            private static Color Tint(Color original) => new Color(Gold.r, Gold.g, Gold.b, original.a);
            internal void Apply(float? value)
            {
                for (int i = 0; i < _graphics.Length; i++)
                    if (_graphics[i] != null) { _colours[i] = _graphics[i].color; _graphics[i].color = GraphicTint(_graphics[i], _colours[i]); }
                for (int i = 0; i < _outlines.Length; i++)
                    if (_outlines[i] != null) { _outlineColours[i] = _outlines[i].effectColor; _outlines[i].effectColor = Tint(_outlineColours[i]); }
                for (int i = 0; i < _lines.Length; i++)
                    if (_lines[i] != null)
                    {
                        _lineStarts[i] = _lines[i].startColor; _lineEnds[i] = _lines[i].endColor;
                        _lines[i].startColor = Tint(_lineStarts[i]); _lines[i].endColor = Tint(_lineEnds[i]);
                    }
                _detailApplied = _detail != null && value.HasValue;
                if (_detailApplied)
                {
                    _sprite = _detail.spriteAsset; _richText = _detail.richText; _originalText = _detail.text;
                    FuelTerminalSprite.Install(_detail);
                    _installedSprite = _detail.spriteAsset;
                    _fuelText = FuelPresentation.Amount(value.Value) + FuelTerminalSprite.Icon;
                    _detail.text = _fuelText;
                }
                _applied = true;
            }
            internal void Restore()
            {
                if (!_applied) return;
                for (int i = 0; i < _graphics.Length; i++)
                    if (_graphics[i] != null && _graphics[i].color == GraphicTint(_graphics[i], _colours[i])) _graphics[i].color = _colours[i];
                for (int i = 0; i < _outlines.Length; i++)
                    if (_outlines[i] != null && _outlines[i].effectColor == Tint(_outlineColours[i])) _outlines[i].effectColor = _outlineColours[i];
                for (int i = 0; i < _lines.Length; i++)
                    if (_lines[i] != null)
                    {
                        if (_lines[i].startColor == Tint(_lineStarts[i])) _lines[i].startColor = _lineStarts[i];
                        if (_lines[i].endColor == Tint(_lineEnds[i])) _lines[i].endColor = _lineEnds[i];
                    }
                if (_detailApplied && _detail != null)
                {
                    if (_detail.text == _fuelText) _detail.text = _originalText;
                    if (_detail.spriteAsset == _installedSprite) _detail.spriteAsset = _sprite;
                    if (_detail.richText) _detail.richText = _richText;
                }
                _applied = false;
            }
        }
    }

    [HarmonyPatch]
    internal static class FuelScanDisplayPatch
    {
        private static bool _logged;
        [HarmonyPatch(typeof(HUDManager), "UpdateScanNodes")]
        [HarmonyPrefix]
        private static void Prefix(HUDManager __instance)
        {
            FuelScanDisplay display = __instance.GetComponent<FuelScanDisplay>();
            if (display == null) display = __instance.gameObject.AddComponent<FuelScanDisplay>();
            display.Initialize(__instance);
            display.Restore();
            if (!_logged)
            {
                _logged = true;
                ShipSystemsBootstrap.Log?.LogInfo("[LGUShipSystems.FuelScan] HUD scan binding and before-canvas styling installed.");
            }
        }

        [HarmonyPatch(typeof(HUDManager), "UpdateScanNodes")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(HUDManager __instance)
        {
            __instance.GetComponent<FuelScanDisplay>()?.ApplyVisible();
        }

        // Vanilla replaces subText with Value: $0 when scrap values synchronize.
        // GI then hides the empty-value detail box before our color pass runs.
        [HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.SetScrapValue))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void ScrapValuePostfix(GrabbableObject __instance)
        {
            __instance.GetComponent<FuelLootItem>()?.RefreshScanText();
        }

        [HarmonyPatch(typeof(HUDManager), "AssignNodeToUIElement")]
        [HarmonyPrefix]
        private static void AssignPrefix(ScanNodeProperties node)
        {
            node?.GetComponentInParent<FuelLootItem>()?.RefreshScanText();
        }
    }
}
