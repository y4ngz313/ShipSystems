using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    /// <summary>Monochrome tank silhouettes; tint and typography belong to the existing monitor.</summary>
    internal static class FuelMonitorTankSprite
    {
        private static TMP_SpriteAsset _asset;
        internal static string Icon(float fill)
        {
            Ensure();
            int level = Mathf.Clamp(Mathf.RoundToInt(fill*100f),0,100);
            return _asset == null ? string.Empty : "<sprite name=\"ship_tank_" + level + "\" tint=1>";
        }
        internal static string Readout(float fuel, float capacity) => "<size=230%>" + Icon(fuel / Mathf.Max(1f, capacity)) + "</size>\n<size=85%>" + Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Tank(fuel, capacity) + "</size>";
        internal static void Install(TMP_Text text)
        {
            Ensure(); if (text == null || _asset == null) return;
            if (text.spriteAsset == null) text.spriteAsset = _asset;
            else if (text.spriteAsset != _asset)
            {
                if (text.spriteAsset.fallbackSpriteAssets == null) text.spriteAsset.fallbackSpriteAssets = new List<TMP_SpriteAsset>();
                if (!text.spriteAsset.fallbackSpriteAssets.Contains(_asset)) text.spriteAsset.fallbackSpriteAssets.Add(_asset);
            }
            text.richText = true;
        }
        private static void Ensure()
        {
            if (_asset != null) return;
            var shader=Shader.Find("TextMeshPro/Sprite"); if(shader==null)return;
            const int scale=4, cell=FuelIconPixels.Size*scale, columns=11, width=cell*columns, height=cell*10;
            var pixels = new Color32[width*height];
            var glyphs=new List<TMP_SpriteGlyph>(); var characters=new List<TMP_SpriteCharacter>();
            for(int level=0;level<=100;level++)
            {
                int ox=(level%columns)*cell, oy=(level/columns)*cell;
                for(int y=0;y<cell;y++) for(int x=0;x<cell;x++)
                {
                    int px=x/scale, py=FuelIconPixels.Size-1-y/scale;
                    bool ink=FuelIconPixels.IsInk(px,py);
                    // The droplet outline stays visible; the unobstructed interior
                    // makes the current liquid level legible at monitor resolution.
                    bool filled=FuelIconPixels.IsInterior(px,py) && (y-(FuelIconPixels.Size-1-FuelIconPixels.InteriorBottom)*scale)<(FuelIconPixels.InteriorBottom-FuelIconPixels.InteriorTop+1)*scale*level/100f;
                    if(ink||filled)pixels[(oy+y)*width+ox+x]=ink
                        ? new Color32(255,255,255,255) : new Color32(190,190,190,255);
                }
                var glyph=new TMP_SpriteGlyph((uint)level,new GlyphMetrics(cell,cell,0,cell*.875f,cell),new GlyphRect(ox,oy,cell,cell),1f,0);
                glyphs.Add(glyph);characters.Add(new TMP_SpriteCharacter((uint)(0xE100+level),glyph){name="ship_tank_"+level,scale=1f});
            }
            var texture=new Texture2D(width,height,TextureFormat.RGBA32,false){name="ShipFuelTankLevels",filterMode=FilterMode.Point,hideFlags=HideFlags.HideAndDontSave};
            texture.SetPixels32(pixels);texture.Apply(false,true);
            _asset=ScriptableObject.CreateInstance<TMP_SpriteAsset>();_asset.name="ShipFuelTankLevels";_asset.hideFlags=HideFlags.HideAndDontSave;
            #if UNITY_EDITOR
            typeof(TMP_SpriteAsset).GetField("m_Version", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(_asset, "1.1.0");
#else
            _asset.version="1.1.0";
#endif
            _asset.spriteInfoList=new List<TMP_Sprite>();_asset.spriteSheet=texture;
            _asset.material=new Material(shader){mainTexture=texture,hideFlags=HideFlags.HideAndDontSave};
            _asset.spriteGlyphTable.Clear();_asset.spriteGlyphTable.AddRange(glyphs);_asset.spriteCharacterTable.Clear();_asset.spriteCharacterTable.AddRange(characters);_asset.UpdateLookupTables();
        }
    }
}
