using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    /// <summary>Small fuel canister in the vanilla terminal; keeps foreign sprite assets intact.</summary>
    internal static class FuelTerminalSprite
    {
        private static TMP_SpriteAsset _asset;
        private static TMP_Text _text;
        internal static string Icon => _asset != null ? "<sprite name=\"ship_fuel\" tint=1>" : "FUEL";

        internal static void Install(Terminal terminal)
        {
            var text = terminal?.screenText?.textComponent;
            Install(text);
            _text = text;
        }

        internal static void Install(TMP_Text text)
        {
            if (text == null) return;
            if (_asset == null) Build();
            if (_asset == null) return;
            // A fallback leaves the terminal's own/another mod's primary atlas untouched.
            if (text.spriteAsset == null) text.spriteAsset = _asset;
            else if (text.spriteAsset != _asset)
            {
                if (text.spriteAsset.fallbackSpriteAssets == null)
                    text.spriteAsset.fallbackSpriteAssets = new List<TMP_SpriteAsset>();
                if (!text.spriteAsset.fallbackSpriteAssets.Contains(_asset))
                    text.spriteAsset.fallbackSpriteAssets.Add(_asset);
            }
            text.richText = true;
        }

        internal static string AlignCatalogue(string text)
        {
            if (_text == null) return text;
            float margins = Mathf.Max(0f, _text.margin.x) + Mathf.Max(0f, _text.margin.z);
            float Measure(string value) => Mathf.Max(0f,
                _text.GetPreferredValues(value, float.PositiveInfinity, float.PositiveInfinity).x - margins);
            text = TerminalCompatibility.MoonCatalogueColumns.RenderFuel(text, Icon, Measure);
            return TerminalCompatibility.MoonCatalogueColumns.Arrange(text,
                _text.rectTransform.rect.width - _text.margin.x - _text.margin.z, Measure);
        }

        private static void Build()
        {
            Shader shader = Shader.Find("TextMeshPro/Sprite");
            if (shader == null) return;
            var texture = new Texture2D(FuelIconPixels.Size, FuelIconPixels.Size, TextureFormat.RGBA32, false)
                { name = "ShipFuelIcon", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[FuelIconPixels.Size * FuelIconPixels.Size];
            for (int y = 0; y < FuelIconPixels.Size; y++)
                for (int x = 0; x < FuelIconPixels.Size; x++)
                    pixels[(FuelIconPixels.Size - 1 - y) * FuelIconPixels.Size + x] = FuelIconPixels.IsInk(x, y) ? new Color32(255,255,255,255) : new Color32(0,0,0,0);
            texture.SetPixels32(pixels); texture.Apply(false, true);
            var material = new Material(shader) { name = "ShipFuelIconMaterial", mainTexture = texture, hideFlags = HideFlags.HideAndDontSave };
            var glyph = new TMP_SpriteGlyph(0, new GlyphMetrics(16,16,0,14,14), new GlyphRect(0,0,FuelIconPixels.Size,FuelIconPixels.Size), 1f, 0);
            _asset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            _asset.name = "ShipFuelIcons"; _asset.hideFlags = HideFlags.HideAndDontSave;
#if UNITY_EDITOR
            // The authoring editor does not publicize TMP's internal metadata.
            typeof(TMP_SpriteAsset).GetField("m_Version", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(_asset, "1.1.0");
#else
            _asset.version = "1.1.0";
#endif
            _asset.spriteInfoList = new List<TMP_Sprite>();
            _asset.spriteSheet = texture; _asset.material = material;
            _asset.spriteGlyphTable.Clear(); _asset.spriteGlyphTable.Add(glyph);
            _asset.spriteCharacterTable.Clear(); _asset.spriteCharacterTable.Add(new TMP_SpriteCharacter(0xE081, glyph) { name = "ship_fuel", scale = 1f });
            _asset.UpdateLookupTables();
        }
    }
}
