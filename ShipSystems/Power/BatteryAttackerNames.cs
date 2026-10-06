using System;
namespace Y4NGZCompany.ShipSystems.Power
{
    internal static class BatteryAttackerNames
    {
        private static string _raw;
        private static string[] _names = Array.Empty<string>();
        internal static bool Matches(string configured, string enemyName, string className)
        {
            if(configured != _raw){_raw=configured;_names=(configured??"").Split(new[]{','},StringSplitOptions.RemoveEmptyEntries);for(int i=0;i<_names.Length;i++)_names[i]=_names[i].Trim();}
            foreach(string name in _names) if(name.Length>0 && (string.Equals(name,enemyName,StringComparison.OrdinalIgnoreCase)||string.Equals(name,className,StringComparison.OrdinalIgnoreCase)))return true;
            return false;
        }
    }
}
