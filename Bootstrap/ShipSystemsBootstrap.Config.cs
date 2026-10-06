using BepInEx.Configuration;
namespace Y4NGZCompany.Bootstrap
{
    internal static partial class ShipSystemsBootstrap
    {
        internal static ConfigEntry<float> LightFuelUnits, DenseFuelUnits, NaturalFuelUnits;
        internal static ConfigEntry<int> LightFuelSale, DenseFuelSale, NaturalFuelSale;
        internal static ConfigEntry<int> LightFuelWeight, DenseFuelWeight, NaturalFuelWeight;
        internal static ConfigEntry<string> AdditionalBatteryAttackers;
        internal static ConfigEntry<float> FuelMonitorSoundVolume;
        internal static ConfigEntry<int> VanillaFuelMonitorSlot;
        private static void BindConfig(ConfigFile config)
        {
            ConfigFile = config;
            Enabled = config.Bind("General", "Enabled", true, "Enable ship fuel and the breakable ship battery. Host controls gameplay; monitor settings are local.");
            VerboseLogging = config.Bind("Advanced", "Verbose Logging", false, "Extra diagnostic logs. Leave off unless troubleshooting.");
            FuelSystemEnabled = config.Bind("Fuel", "Enabled", true, "Host setting. Enable fuel costs, fuel pickups and the chute. Disabling this leaves battery gameplay active.");
            FuelTankSize = Float(config,"Fuel","Tank Capacity",100,25,300,"Maximum fuel units the ship can hold. Landing deducts the quoted route cost once.");
            MoonFuelCostByTier = config.Bind("Advanced Fuel Defaults","Moon Prices by Risk","Company=0,Emergency=0,D=6,C=10,B=18,A=26,S=34,Unknown=12","Starting prices for newly detected moons. For a specific moon, edit Fuel Moon Prices. Existing player overrides are preserved.");
            DefaultConstellationEntryFuelCost = Float(config,"Fuel Pricing","Default Constellation Entry Fee",14,0,100,"Extra fuel for crossing into a real constellation. No entry fee is charged for ordinary ungrouped moons. Detected groups appear in Fuel Constellations.");
            ConstellationEntryFuelCostByName = config.Bind("Advanced Fuel Defaults","Undetected Constellation Entry Fees","","Optional Name=Fee pairs for groups not yet detected. Detected Fuel Constellations entries take priority.");
            EmergencyMoonNameContains = config.Bind("Fuel Pricing","Emergency Moons","Experimentation,Assurance,Vow","Comma-separated moon names eligible for the emergency landing price. Partial names match. These defaults provide free escape routes.");
            EmergencyMoonFuelCost = Float(config,"Fuel Pricing","Emergency Moon Price",0,0,20,"Landing fuel price for Emergency Moons. This applies on every visit, not only when the tank is empty. Explicit moon overrides take priority.");
            FuelBlacklistedMoons = config.Bind("Fuel Pricing","Free Travel Moons","","Comma-separated names that should cost no fuel, including constellation entry. Partial names match. An explicitly edited moon price takes priority over this list.");
            ShowFuelInMoonCatalogue = config.Bind("Fuel Monitor","Show Terminal Fuel Costs",true,"Show the gold fuel icon and route cost in the terminal moons list and planet information.");
            FuelMonitorEnabled = config.Bind("Fuel Monitor","Enabled",true,"Show a live ship fuel monitor. Automatically uses GeneralImprovements with Better Monitors enabled; uses a spare stock screen when no wall mod is installed. GI with UseBetterMonitors=false and unknown replacement walls are not overwritten.");
            FuelMonitorGeneralImprovementsSlot = config.Bind("Fuel Monitor","Better Monitors Screen","Screen9","GeneralImprovements screen name, Screen1 through Screen12 (or a number). Default Screen9. Set that screen to None in GeneralImprovements to dedicate it to fuel. For the unmodified wall, use Vanilla Screen instead.");
            VanillaFuelMonitorSlot = Int(config,"Fuel Monitor","Vanilla Screen",1,1,2,"Stock wall only: 1 is the upper-left small screen; 2 is the next small screen to its right. Quota, deadline and camera screens are left alone. Restart after changing this setting.");
            FuelMonitorSoundVolume = Float(config,"Fuel Monitor","Countdown Sound Volume",.35f,0,1,"Local volume of the fuel-countdown bleeps from the monitor when the landing doors open. Zero mutes it.");
            EnableFuelLootSpawnInjection = config.Bind("Fuel Items","Spawn Fuel Items",true,"Host setting. Add fuel items to each moon's scrap pool. Weights below are relative to all other scrap, not percentage chances or guaranteed counts. Restart the game after changing item settings.");
            LightFuelUnits=Float(config,"Fuel Items","Light Fuel - Fuel Units",4,0,300,"Fuel units added by one Light Fuel can.");
            DenseFuelUnits=Float(config,"Fuel Items","Dense Fuel - Fuel Units",7,0,300,"Fuel units added by one Dense Fuel can.");
            NaturalFuelUnits=Float(config,"Fuel Items","Natural Fuel - Fuel Units",12,0,300,"Fuel units added by one Natural Fuel lump.");
            LightFuelSale=Int(config,"Fuel Items","Light Fuel - Sell Value",18,0,1000,"Base scrap sale value, before the Company's buying rate.");
            DenseFuelSale=Int(config,"Fuel Items","Dense Fuel - Sell Value",32,0,1000,"Base scrap sale value, before the Company's buying rate.");
            NaturalFuelSale=Int(config,"Fuel Items","Natural Fuel - Sell Value",55,0,1000,"Base scrap sale value, before the Company's buying rate.");
            LightFuelWeight=Int(config,"Fuel Items","Light Fuel - Spawn Weight",12,0,1000,"Relative scrap spawn weight. Zero prevents natural spawning of Light Fuel.");
            DenseFuelWeight=Int(config,"Fuel Items","Dense Fuel - Spawn Weight",7,0,1000,"Relative scrap spawn weight. Zero prevents natural spawning of Dense Fuel.");
            NaturalFuelWeight=Int(config,"Fuel Items","Natural Fuel - Spawn Weight",3,0,1000,"Relative scrap spawn weight. Zero prevents natural spawning of Natural Fuel.");
            BatteryHitsToBreak=Int(config,"Battery","Hits to Break",6,1,30,"Battery durability in normal damage units. Player hits and supported monster attacks use the same durability.");
            BatteryHitsToBreakWithPlating=Int(config,"Battery","Reinforced Hits to Break",9,1,45,"Durability after Battery Reinforcement is installed, when an upgrade provider is present.");
            BatteryHitIntervalSeconds=Float(config,"Battery","Monster Hit Interval",1.6f,.8f,6,"Seconds between monster damage pulses while in contact. More monsters do not multiply the hit rate.");
            BatteryRestoreHoldSeconds=Float(config,"Battery","Apparatus Dock Time",2,.5f,12,"Seconds to hold interact when restoring power with an apparatus or spare battery.");
            TechnicianRepairHoldSeconds=Float(config,"Battery","Technician Repair Time",6,1,30,"Repair hold time for the Field Mechanic profession, when that profession is installed.");
            BatterySabotageTargetChancePercent=Float(config,"Battery","Sabotage Chance Percent",25,0,100,"Initial chance that a Masked or Bundy chooses the battery. Zero disables random selection; Bundy's scripted closed-ship siege can still apply.");
            AdditionalBatteryAttackers=config.Bind("Battery","Additional Nearby Attackers","","Comma-separated exact enemy names (or AI class names) allowed to damage the battery when within reach. Example: Nutcracker. Empty allows only Masked/Bundy. This does not change another monster's navigation or supply new animations.");
            BatteryDoorClosedTargetChanceMultiplier=Float(config,"Advanced Battery","Closed Door Chance Multiplier",2,1,6,"Multiply the initial sabotage chance while a living player shelters behind the closed ship door.");
            BatteryDoorClosedRetargetChancePercent=Float(config,"Advanced Battery","Closed Door Retry Chance Percent",.35f,0,10,"Chance every 1.5 seconds to reconsider a failed roll while a player shelters behind the closed door. 0.35 means 0.35 percent, not 35 percent.");
            BatterySabotageRadius=Float(config,"Advanced Battery","Approach Radius",4.2f,1,12,"Distance used to identify a nearby saboteur. Physical contact and obstruction checks still apply before damage.");
            BatterySabotageInterruptRange=Float(config,"Advanced Battery","Player Distraction Range",5,1,30,"Visible players outside the ship distract an approaching saboteur within this distance in metres. Bundy is capped at 5 metres.");
            BatterySabotageReleaseDistance=Float(config,"Advanced Battery","Resume Sabotage Distance",15,2,60,"Resume the battery approach when the distracting player is this far away or becomes untargetable.");
        }
        private static ConfigEntry<float> Float(ConfigFile c,string section,string key,float value,float min,float max,string help) => c.Bind(section,key,value,new ConfigDescription(help,new AcceptableValueRange<float>(min,max)));
        private static ConfigEntry<int> Int(ConfigFile c,string section,string key,int value,int min,int max,string help) => c.Bind(section,key,value,new ConfigDescription(help,new AcceptableValueRange<int>(min,max)));
    }
}
