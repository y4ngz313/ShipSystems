using System;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Fuel;
using Y4NGZCompany.ShipSystems.Power;

namespace Y4NGZCompany.ShipSystems.Layout
{
    public static class ShipSystemsApi
    {
        public static bool HasUpgrade(string upgradeName)
        {
            // #666: upgrade levels live with the command plugin, read through Core. The
            // string keys are unchanged — they were always the upgrade enum member names.
            return Y4NGZCore.Modules.ShipSystems.ShipUpgradeState.Has(upgradeName);
        }

        public static bool HasCctvTerminalUpgrade()
        {
            return HasUpgrade("CCTVTerminal");
        }

        public static bool HasTurretUpgrade()
        {
            return HasUpgrade("Turret");
        }

        public static bool IsShipPowerOnline()
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            return controller == null || (controller.ShipPowerOnline && !controller.BatteryBroken);
        }

        public static bool IsRestoreObjectiveActive()
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            return controller != null && controller.RestoreObjectiveActive;
        }

        public static bool IsBatteryBroken()
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            return controller != null && controller.BatteryBroken;
        }

        public static string GetRestoreItemDisplayName()
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            return controller != null ? controller.RestoreItemDisplayName : "apparatus";
        }

        public static string GetRestoreRecoveryObjectiveText()
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            return controller != null ? controller.RestoreRecoveryObjectiveText : "Recover the apparatus";
        }

        public static bool IsAnyPlayerHoldingRestoreItem()
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            return controller != null && controller.IsAnyPlayerHoldingRestoreItem();
        }

        public static float GetPowerFraction()
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            if (controller == null || controller.PowerCapacity <= 0f) return 1f;
            return Math.Max(0f, Math.Min(1f, controller.Power / controller.PowerCapacity));
        }

        public static float GetPowerCapacity()
        {
            ShipSystemsController controller = ShipSystemsController.Instance;
            return controller != null ? Math.Max(1f, controller.PowerCapacity) : 100f;
        }

        public static string GetCurrentLayoutName()
        {
            return Y4NGZCore.Modules.ShipSystems.ShipUpgradeState.CurrentLayoutName;
        }

        public static bool TrySpendTurretShotPower()
        {
            return ShipSystemsController.Instance != null &&
                   ShipSystemsController.Instance.TrySpendTurretShotPower();
        }

        public static void NoteTurretShotAccepted()
        {
            ShipSystemsController.Instance?.PredictAcceptedTurretShotPowerSpend();
        }

        /// <summary>
        /// Always false since #659: the power/fuel monitor overlay was removed
        /// with the rest of the custom monitor content (GeneralImprovements
        /// Better Monitors covers it). Kept because LethalCCTV resolves this
        /// member by reflection.
        ///
        /// <para>Deliberately still false after the #676 fuel monitor came back. This flag
        /// means "Ship Systems owns the VANILLA monitor face", and LethalCCTV acts on it:
        /// <c>OpenBodyCamsCompat.SuppressShipMonitor</c> stops unconditionally restoring the
        /// large right monitor's material when it reads true, and starts calling
        /// <c>ForceRebindPowerDisplay</c>. The #676 readout lives on a GeneralImprovements
        /// small screen and never touches the vanilla face, so returning true here would
        /// change body-cam material restoration for a claim this module is not making. Use
        /// <see cref="IsFuelMonitorDisplayEnabled"/> for the GI readout instead.</para>
        /// </summary>
        public static bool IsPowerMonitorDisplayEnabled()
        {
            return false;
        }

        public static bool IsFuelSystemEnabled()
        {
            return ShipSystemsController.IsFuelSystemEnabled();
        }

        /// <summary>
        /// True when the fuel readout is configured on and some monitor can host it (#676,
        /// widened in #678).
        ///
        /// <para>Since #678 that is either of two hosts: GeneralImprovements' Better Monitors
        /// wall, or — when no monitor-wall mod is installed at all — a spare stock ship monitor.
        /// A profile with GI installed but Better Monitors switched off still answers false,
        /// because GI is authoring the vanilla faces itself there and the readout stands
        /// down.</para>
        ///
        /// <para>Reports configuration and hardware availability, not whether the readout is
        /// painting this instant — it stands down for the duration of a takeover or
        /// announcement without that being a change of state. Nothing in the repository
        /// consumes this member today; it is a reflection seam, and it is answered honestly so
        /// a future consumer is not misled. Note that it remains independent of
        /// <see cref="IsPowerMonitorDisplayEnabled"/>, which still means "Ship Systems owns the
        /// vanilla LARGE monitor face" and is still false.</para>
        /// </summary>
        public static bool IsFuelMonitorDisplayEnabled()
        {
            try
            {
                if (ShipSystemsBootstrap.FuelMonitorEnabled != null &&
                    !ShipSystemsBootstrap.FuelMonitorEnabled.Value)
                {
                    return false;
                }

                return ShipSystemsController.IsFuelSystemEnabled() &&
                       Fuel.FuelMonitorDisplay.HostAvailable;
            }
            catch
            {
                return false;
            }
        }

        public static ShipFuelQuote GetFuelQuoteForRoute(
            int routeIndex,
            int levelId,
            string moonName,
            string riskTier,
            string constellationKey,
            string constellationName,
            bool isCompanyMoon)
        {
            ShipSystemsController controller = GetController();
            if (controller == null)
                return UnavailableFuelQuote();

            var request = new ShipFuelRouteRequest(routeIndex, levelId, moonName, riskTier, constellationKey, constellationName, isCompanyMoon);
            return controller.GetFuelQuote(request, FuelRouteConstellationIndexCache.GetRouteIndexes(request.ConstellationKey));
        }

        public static ShipFuelQuote GetRouteFuelQuote(
            int routeIndex,
            int levelId,
            string moonName,
            string riskTier,
            string constellationKey,
            string constellationName,
            bool isCompanyMoon)
        {
            return GetFuelQuoteForRoute(routeIndex, levelId, moonName, riskTier, constellationKey, constellationName, isCompanyMoon);
        }

        public static bool TryReserveFuelForRoute(
            int routeIndex,
            int levelId,
            string moonName,
            string riskTier,
            string constellationKey,
            string constellationName,
            bool isCompanyMoon,
            out ShipFuelQuote quote)
        {
            ShipSystemsController controller = GetController();
            if (controller == null)
            {
                quote = UnavailableFuelQuote();
                return true;
            }

            var request = new ShipFuelRouteRequest(routeIndex, levelId, moonName, riskTier, constellationKey, constellationName, isCompanyMoon);
            int[] constellationRouteIndexes = FuelRouteConstellationIndexCache.GetRouteIndexes(request.ConstellationKey);
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer)
            {
                quote = controller.GetFuelQuote(request, constellationRouteIndexes);
                return quote.IsAffordable;
            }

            return controller.TryReserveFuelForRoute(request, constellationRouteIndexes, out quote);
        }

        public static bool ReserveRouteFuel(
            int routeIndex,
            int levelId,
            string moonName,
            string riskTier,
            string constellationKey,
            string constellationName,
            bool isCompanyMoon,
            out ShipFuelQuote quote)
        {
            return TryReserveFuelForRoute(routeIndex, levelId, moonName, riskTier, constellationKey, constellationName, isCompanyMoon, out quote);
        }

        public static bool CommitReservedFuelRoute(int routeIndex)
        {
            ShipSystemsController controller = GetController();
            return controller != null && controller.CommitReservedFuelRouteIndex(routeIndex);
        }

        public static void CancelReservedFuelRoute()
        {
            GetController()?.CancelReservedFuelRoute();
        }

        public static float GetFuelFraction()
        {
            ShipSystemsController controller = GetController();
            if (controller == null || controller.FuelCapacity <= 0f)
                return 1f;
            return Math.Max(0f, Math.Min(1f, controller.Fuel / controller.FuelCapacity));
        }

        public static float GetFuelCapacity()
        {
            ShipSystemsController controller = GetController();
            return controller != null ? Math.Max(1f, controller.FuelCapacity) : ShipFuelConfig.TankSize;
        }

        public static float GetConstellationEntryFuelCost(string constellationName)
        {
            return ShipFuelConfig.GetConstellationEntryFuelCost(constellationName);
        }

        public static float GetMoonFuelCostForConstellation(string constellationName, bool isCompanyMoon)
        {
            return ShipFuelConfig.GetMoonFuelCostForConstellation(constellationName, isCompanyMoon);
        }

        public static int GetFuelSourceFillCount()
        {
            return ShipFuelConfig.SourceFillCount;
        }

        public static int GetFilledPumpFuelAmount()
        {
            return Mathf.RoundToInt(ShipFuelConfig.PumpFuelAmount);
        }

        /// <summary>
        /// No-op since #659: the power display was removed. Kept because the
        /// member is part of the reflected API surface.
        /// </summary>
        public static void ForceRebindPowerDisplay()
        {
        }

        public static bool TryRollBatterySabotageTarget(EnemyAI enemy)
        {
            return BatterySabotageDirector.TryRollBatteryTarget(enemy);
        }

        public static bool TryForceBatterySabotageTarget(EnemyAI enemy)
        {
            return BatterySabotageDirector.TryForceBatteryTarget(enemy);
        }

        public static bool IsBatterySabotageTargetActive(EnemyAI enemy)
        {
            return BatterySabotageDirector.IsBatteryTargetActive(enemy);
        }

        public static bool TryGetBatterySabotageDestination(EnemyAI enemy, out Vector3 destination)
        {
            return BatterySabotageDirector.TryGetBatteryDestination(enemy, out destination);
        }

        public static bool TryGetBatterySabotageInterruptTarget(EnemyAI enemy, out PlayerControllerB player)
        {
            return BatterySabotageDirector.TryGetInterruptTarget(enemy, out player);
        }

        public static bool ShouldReleaseBatterySabotageInterrupt(EnemyAI enemy, PlayerControllerB player)
        {
            return BatterySabotageDirector.ShouldReleaseInterrupt(enemy, player);
        }

        public static void ClearBatterySabotageInterrupt(EnemyAI enemy)
        {
            BatterySabotageDirector.ClearInterrupt(enemy);
        }

        // #666: the scopes below are Core state (ShipPresentationScopes) so they keep
        // coordinating whichever subset of the split ship plugins is installed. This
        // reflected surface stays here, name-stable, because LethalCCTV resolves it.
        public static void BeginExternalMonitorTakeover()
        {
            BeginExternalMonitorTakeover(null);
        }

        public static void BeginExternalMonitorTakeover(Texture takeoverTexture)
        {
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.SetExternalTakeover(true, takeoverTexture);
        }

        public static void UpdateExternalMonitorTakeoverTexture(Texture takeoverTexture)
        {
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.UpdateExternalTakeoverTexture(takeoverTexture);
        }

        public static void EndExternalMonitorTakeover()
        {
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.SetExternalTakeover(false, null);
        }

        public static bool IsExternalMonitorTakeoverActive()
        {
            return Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.ExternalTakeoverActive;
        }

        public static string DumpMonitorWallFaceMap()
        {
            string described = Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.MonitorFaceMapDescriber?.Invoke();
            return string.IsNullOrEmpty(described) ? "monitor face map is unavailable" : described;
        }

        public static bool SetMonitorWallFaceDebugPaint(bool enabled)
        {
            return Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.TrySetMonitorFaceDebugPaint(enabled);
        }

        // Large-monitors-only (Cube.001) takeover used by CCTV. Unlike the
        // full-wall variant above, this leaves the fuel/quota/deadline
        // physical overlays on the small monitors running.
        public static void BeginPrimaryMonitorTakeover()
        {
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.SetPrimaryTakeover(true);
        }

        public static bool IsPrimaryMonitorTakeoverActive()
        {
            return Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.PrimaryTakeoverActive;
        }

        /// <summary>
        /// Holds every claim-owned ship monitor in a coordinated Company
        /// broadcast for the length of a quota reward. Nothing resumes normal
        /// content until <see cref="EndQuotaPresentation"/> runs.
        /// </summary>
        public static void BeginQuotaPresentation(int quota)
        {
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.SetQuotaPresentation(true, quota);
        }

        public static void EndQuotaPresentation()
        {
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.SetQuotaPresentation(false, 0);
        }

        public static bool IsQuotaPresentationActive()
        {
            return Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.QuotaPresentationActive;
        }

        public static void EndPrimaryMonitorTakeover()
        {
            Y4NGZCore.Modules.ShipSystems.ShipPresentationScopes.SetPrimaryTakeover(false);
        }

        private static ShipSystemsController GetController()
        {
            if (ShipSystemsBootstrap.Enabled != null && !ShipSystemsBootstrap.Enabled.Value)
                return null;

            ShipSystemsController.EnsureInstance();
            return ShipSystemsController.Instance;
        }

        private static ShipFuelQuote UnavailableFuelQuote()
        {
            return new ShipFuelQuote(
                fuelSystemAvailable: false,
                isAffordable: true,
                isCurrentConstellation: true,
                hasFuelSource: false,
                fuelAvailable: ShipFuelConfig.TankSize,
                fuelCapacity: ShipFuelConfig.TankSize,
                reservedFuel: 0f,
                entryFuelCost: 0f,
                moonFuelCost: 0f,
                routeKey: string.Empty,
                constellationKey: string.Empty,
                constellationName: string.Empty,
                denialText: string.Empty);
        }
    }
}
