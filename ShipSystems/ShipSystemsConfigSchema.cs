using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace Y4NGZCompany.ShipSystems
{
    internal static class ShipSystemsConfigSchema
    {
        private static readonly Dictionary<string,string> Renames = new Dictionary<string,string>(StringComparer.Ordinal)
        {
            { "Shared\tEnabled", "General\tEnabled" },
            { "Shared\tVerbose Logging", "Advanced\tVerbose Logging" },
            { "Fuel\tFuel System Enabled", "Fuel\tEnabled" },
            { "Fuel\tTank Size", "Fuel\tTank Capacity" },
            { "Fuel\tMoon Fuel Cost By Tier", "Advanced Fuel Defaults\tMoon Prices by Risk" },
            { "Fuel\tDefault Constellation Entry Fuel Cost", "Fuel Pricing\tDefault Constellation Entry Fee" },
            { "Fuel\tConstellation Entry Fuel Cost By Name", "Advanced Fuel Defaults\tUndetected Constellation Entry Fees" },
            { "Fuel\tEmergency Moon Name Contains", "Fuel Pricing\tEmergency Moons" },
            { "Fuel\tEmergency Moon Fuel Cost", "Fuel Pricing\tEmergency Moon Price" },
            { "Fuel\tFuel Blacklisted Moons", "Fuel Pricing\tFree Travel Moons" },
            { "Fuel\tShow Fuel In Moon Catalogue", "Fuel Monitor\tShow Terminal Fuel Costs" },
            { "Fuel Monitor\tGeneralImprovementsSlot", "Fuel Monitor\tBetter Monitors Screen" },
            { "Fuel Loot\tEnable Spawn Injection", "Fuel Items\tSpawn Fuel Items" },
            { "Battery\tHits To Break", "Battery\tHits to Break" },
            { "Battery\tHits To Break With Plating", "Battery\tReinforced Hits to Break" },
            { "Battery\tHit Interval Seconds", "Battery\tMonster Hit Interval" },
            { "Battery\tDock Hold Seconds", "Battery\tApparatus Dock Time" },
            { "Battery\tTechnician Repair Hold Seconds", "Battery\tTechnician Repair Time" },
            { "Battery\tSabotage Target Chance Percent", "Battery\tSabotage Chance Percent" },
            { "Battery\tDoor Closed Target Chance Multiplier", "Advanced Battery\tClosed Door Chance Multiplier" },
            { "Battery\tDoor Closed Retarget Chance Percent", "Advanced Battery\tClosed Door Retry Chance Percent" },
            { "Battery\tSabotage Radius", "Advanced Battery\tApproach Radius" },
            { "Battery\tSabotage Interrupt Range", "Advanced Battery\tPlayer Distraction Range" },
            { "Battery\tSabotage Release Distance", "Advanced Battery\tResume Sabotage Distance" },
        };
        internal static string Rewrite(string source, bool terminalSettingsMigrated = false)
        {
            var values = new Dictionary<string,string>(StringComparer.Ordinal);
            string section = "";
            using (var reader = new StringReader(source)) for (string line; (line=reader.ReadLine())!=null;)
            {
                string trimmed=line.Trim();
                if(trimmed.Length==0 || trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;
                if(trimmed.StartsWith("[") && trimmed.EndsWith("]")){section=trimmed.Substring(1,trimmed.Length-2);continue;}
                int equals=line.IndexOf('=');
                if(equals<1 || section.Length==0) return source; // Preserve malformed files for manual repair.
                values[section+"\t"+line.Substring(0,equals).Trim()]=line.Substring(equals+1).Trim();
            }
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            bool changed=false;
            foreach(var pair in values)
            {
                string oldSection=pair.Key.Split('\t')[0];
                if(oldSection=="Power" || oldSection=="Fuel Sources" || pair.Key=="Fuel\tEnable Fuel Monitor Display" || oldSection=="78 - Ship Systems - Monitor Row Content" || (terminalSettingsMigrated && IsTerminalSection(oldSection)) || pair.Key=="Fuel Loot\tFuel Values" || pair.Key=="Fuel Loot\tScrap Values") {changed=true;continue;}
                if(Renames.ContainsKey(pair.Key)){changed=true;continue;}
                result[pair.Key]=pair.Value;
            }
            foreach(var pair in values) if(Renames.TryGetValue(pair.Key,out string target) && !result.ContainsKey(target))result[target]=pair.Value;
            ExpandItemMap(values,result,"Fuel Values","Fuel Units");
            ExpandItemMap(values,result,"Scrap Values","Sell Value");
            if(!changed)return source;
            var sections=new Dictionary<string,List<string>>(StringComparer.Ordinal);
            foreach(var pair in result){int split=pair.Key.IndexOf('\t');string name=pair.Key.Substring(0,split);if(!sections.TryGetValue(name,out var list))sections[name]=list=new List<string>();list.Add(pair.Key.Substring(split+1)+" = "+pair.Value);}
            var output=new StringBuilder("# Ship Systems player configuration. Previous file backed up before migration.\n");
            foreach(var group in sections){output.Append("\n[").Append(group.Key).Append("]\n");foreach(string line in group.Value)output.Append(line).Append('\n');}
            return output.ToString();
        }
        private static bool IsTerminalSection(string section) =>
            section.StartsWith("74 - Terminal - ",StringComparison.Ordinal) ||
            section.StartsWith("75 - Shop - ",StringComparison.Ordinal) ||
            section.StartsWith("76 - Shop - ",StringComparison.Ordinal) ||
            section.StartsWith("77 - Ship Upgrades - ",StringComparison.Ordinal) ||
            section=="78 - Ship Systems - Monitor Row";
        private static void ExpandItemMap(Dictionary<string,string> source,Dictionary<string,string> result,string oldKey,string suffix)
        {
            if(!source.TryGetValue("Fuel Loot\t"+oldKey,out string raw))return;
            foreach(string part in raw.Split(','))
            {
                string[] pair=part.Split('=');if(pair.Length!=2)continue;
                string old=pair[0].Trim();string name=old.Equals("Small",StringComparison.OrdinalIgnoreCase)?"Light Fuel":old.Equals("Medium",StringComparison.OrdinalIgnoreCase)?"Dense Fuel":old.Equals("Large",StringComparison.OrdinalIgnoreCase)?"Natural Fuel":null;
                if(name==null)continue;string key="Fuel Items\t"+name+" - "+suffix;if(!result.ContainsKey(key))result[key]=pair[1].Trim();
            }
        }
    }
}
