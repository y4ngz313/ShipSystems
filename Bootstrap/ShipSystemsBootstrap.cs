using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.ShipSystems.Fuel;
using Y4NGZCompany.ShipSystems.Layout;
using Y4NGZCompany.ShipSystems.Power;

namespace Y4NGZCompany.Bootstrap
{
    internal static partial class ShipSystemsBootstrap
    {
        // #612 task 1.4: Ship Systems' own Harmony owner id, not the shared com.y4ngz.company.
        // While the build is monolithic Plugin.Awake still constructs the instance and passes
        // it in; the fallback below is what this id is for after the split.
        internal const string PluginGuid = Y4NGZCore.Lifecycle.ModuleHarmonyIds.ShipSystems;
        internal const string PluginName = "ShipSystems";
        internal const string PluginVersion = "0.1.9";

        internal static ManualLogSource Log { get; private set; }

        /// <summary>
        /// #679: the module's own config file, kept so the launch-time moon detection can bind
        /// generated per-moon and per-constellation price entries once the game's level list is
        /// final. Nothing else may bind through it after <see cref="BindConfig"/> returns.
        /// </summary>
        internal static ConfigFile ConfigFile { get; private set; }

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<bool> FuelSystemEnabled;
        internal static ConfigEntry<float> FuelTankSize;
        internal static ConfigEntry<string> MoonFuelCostByTier;
        internal static ConfigEntry<float> DefaultConstellationEntryFuelCost;
        internal static ConfigEntry<string> ConstellationEntryFuelCostByName;
        internal static ConfigEntry<string> EmergencyMoonNameContains;
        internal static ConfigEntry<float> EmergencyMoonFuelCost;
        internal static ConfigEntry<string> FuelBlacklistedMoons;
        internal static ConfigEntry<bool> ShowFuelInMoonCatalogue;
        internal static ConfigEntry<float> FuelSourceChancePercent;
        internal static ConfigEntry<int> FuelSourceFillCount;
        internal static ConfigEntry<float> FuelPumpFillSeconds;
        internal static ConfigEntry<float> FuelPumpStallChancePercent;
        internal static ConfigEntry<float> FuelPumpFuelAmount;
        internal static ConfigEntry<int> FuelPumpPrice;
        internal static ConfigEntry<bool> EnableFuelLootSpawnInjection;
        internal static ConfigEntry<bool> FuelMonitorEnabled;
        internal static ConfigEntry<string> FuelMonitorGeneralImprovementsSlot;
        internal static ConfigEntry<int> BatteryHitsToBreak;
        internal static ConfigEntry<int> BatteryHitsToBreakWithPlating;
        internal static ConfigEntry<float> BatteryHitIntervalSeconds;
        internal static ConfigEntry<float> BatterySabotageRadius;
        internal static ConfigEntry<float> BatteryRestoreHoldSeconds;
        internal static ConfigEntry<float> TechnicianRepairHoldSeconds;
        internal static ConfigEntry<float> BatteryDoorClosedTargetChanceMultiplier;
        internal static ConfigEntry<float> BatteryDoorClosedRetargetChancePercent;
        internal static ConfigEntry<float> BatterySabotageTargetChancePercent;
        internal static ConfigEntry<float> BatterySabotageInterruptRange;
        internal static ConfigEntry<float> BatterySabotageReleaseDistance;
        private static Harmony _harmony;

        internal static void Initialize(ConfigFile Config, ManualLogSource Logger, Harmony harmony)
        {
            Log = Logger;

            // #612 task 1.4: registered before BindConfig and before the enable check, because
            // the pre-split host called Tick() every frame regardless of either. Tick() already
            // self-guards on Enabled (see below), so registering unconditionally is what keeps
            // the behaviour identical rather than merely close.
            Y4NGZCore.Lifecycle.ModuleTickRegistry.RegisterTick(
                Y4NGZCore.Lifecycle.ModuleHarmonyIds.ShipSystems,
                Y4NGZCore.Lifecycle.ModuleTickOrder.ShipSystems,
                "ShipSystemsBootstrap",
                Tick);

            BindConfig(Config);

            if (!Enabled.Value)
            {
                Log.LogInfo($"{PluginName} disabled by config.");
                return;
            }

            _harmony = harmony ?? new Harmony(PluginGuid);
            EnsureFuelRuntimePrefabsRegistered();

            _harmony.PatchAll(typeof(Y4NGZCompany.ShipSystems.Layout.Patches));
            MoonFuelOptionalTerminalCompatibility.Initialize(_harmony);
            _harmony.PatchAll(typeof(BatterySabotagePatches));
            _harmony.PatchAll(typeof(BatteryDamagePatches));
            _harmony.PatchAll(typeof(Y4NGZCompany.ShipSystems.Fuel.FuelChuteGrabPatch));
            _harmony.PatchAll(typeof(Y4NGZCompany.ShipSystems.Fuel.FuelScanDisplayPatch));

            ShipSystemsController.EnsureInstance();

            // #666: the fuel flag for the Core presentation coordinator. The scope holder
            // lives in Core now; this probe is the one feature answer the fuel owner supplies.
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.FuelSystemEnabledProbe =
                ShipSystemsController.IsFuelSystemEnabled;

            // #611 task 1.3: everything another feature asks of Ship Systems — power status,
            // battery-sabotage direction, the monitor-wall handoff, the ship-fuel reward — is
            // published through Y4NGZCore from here on, so no caller resolves a type by name.
            // Registered behind the enable flag on purpose: a disabled Ship Systems must read
            // as absent to every consumer, and the contracts' absence defaults are exactly the
            // behaviour a disabled module should produce.
            Y4NGZCompany.ShipSystems.Layout.ShipSystemsModuleProviders.EnsureRegistered();

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        internal static void Shutdown()
        {
            ShipSystemsController.Instance?.Shutdown();
        }

        internal static void Tick()
        {
            if (Enabled != null && !Enabled.Value)
                return;

            ShipSystemsController.EnsureInstance();
            ShipSystemsController.Instance?.Tick();
        }

        internal static void EnsureFuelRuntimePrefabsRegistered()
        {
            SpareBatteryItemRegistry.EnsureRegistered();
            FuelPumpRegistry.EnsureRegistered();
            FuelLootRegistry.EnsureRegistered();
            FuelSourceSpawner.EnsureRegistered();
            FuelChuteFurnitureRegistry.EnsureRegistered();
        }

    }
}
