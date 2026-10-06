using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelHotbarIcons
    {
        private static readonly Dictionary<string,Sprite> Icons=new Dictionary<string,Sprite>();
        internal static Sprite Load(string size)
        {
            if(Icons.TryGetValue(size,out var icon) && icon!=null)return icon;
            string path=Path.Combine(Path.GetDirectoryName(typeof(ShipSystemsBootstrap).Assembly.Location),"Assets","Fuel",size.ToLowerInvariant()+"-icon.png");
            try
            {
                var texture=new Texture2D(2,2,TextureFormat.RGBA32,false){name=size+"FuelHotbar",filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave};
                if(!texture.LoadImage(File.ReadAllBytes(path)))throw new InvalidDataException("Invalid fuel thumbnail");
                icon=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100);
                icon.name=size+"FuelHotbar";icon.hideFlags=HideFlags.HideAndDontSave;Icons[size]=icon;ShipSystemsBootstrap.Log?.LogInfo("[LGUShipSystems.Fuel] Loaded rendered "+size+" hotbar icon.");return icon;
            }
            catch(Exception ex){ShipSystemsBootstrap.Log?.LogWarning("[LGUShipSystems.Fuel] Hotbar thumbnail unavailable: "+ex.Message);return null;}
        }
    }
}
