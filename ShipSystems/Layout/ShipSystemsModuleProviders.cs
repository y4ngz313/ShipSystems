// ShipSystemsModuleProviders.cs — #611 task 1.3.
//
// Ship Systems' registrations into the Y4NGZCore module contract layer.
//
// Three separate callers used to reach ShipSystemsApi through
// Type.GetType("Y4NGZCompany.ShipSystems.Layout.ShipSystemsApi, Y4NGZCompany") and a fistful of
// cached MethodInfo handles:
//
//   * the Bundy escalation, for battery-sabotage direction (BundyShipSystemsBridge);
//   * the ship-power escalation objective, for the four power-status reads
//     (ShipPowerEscalationObjectiveState);
//   * the monitor takeover, to hand the wall over (ShipSystemsTakeoverBridge).
//
// Every one of those strings names the *monolith*. After the split each resolves to null, the
// bridge caches the failure, and the feature behind it silently stops working with no log line
// and no crash — the worst possible failure mode, because nothing distinguishes it from "the
// feature is off". These providers replace all three with compile-time contracts that fail
// loudly at build time instead.

using System;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCore.Modules;
using Y4NGZCore.Modules.Quota;
using Y4NGZCore.Modules.ShipSystems;

namespace Y4NGZCompany.ShipSystems.Layout
{
    /// <summary>
    /// Registers Ship Systems' power-status, battery-sabotage and monitor-takeover-sink
    /// providers. Called once from <c>ShipSystemsBootstrap.Initialize</c>.
    /// </summary>
    internal static class ShipSystemsModuleProviders
    {
        private static PowerStatusProvider _power;
        private static BatterySabotageCoordinator _sabotage;
        private static ShipFuelRewardProvider _fuelReward;
        private static FuelRoutingService _fuelRouting;

        /// <summary>Idempotent: a second call replaces this module's own registrations.</summary>
        internal static void EnsureRegistered()
        {
            if (_power != null)
                return;

            _power = new PowerStatusProvider();
            _sabotage = new BatterySabotageCoordinator();
            _fuelReward = new ShipFuelRewardProvider();
            _fuelRouting = new FuelRoutingService();

            ModuleCapabilityRegistry.Register(ModuleCapabilities.ShipPowerStatus, _power);
            ModuleCapabilityRegistry.Register(ModuleCapabilities.BatterySabotage, _sabotage);
            // #666: the monitor-takeover sink and presentation coordinator moved to Core's
            // ShipPresentationScopes (registered from CorePlugin), and the constellation
            // unlock source plus the ship-upgrade catalogue registration moved to
            // Y4NGZTerminalUpgrades with the terminal and the upgrade enum.
            ModuleCapabilityRegistry.Register(ModuleCapabilities.ShipFuelRouting, _fuelRouting);

            // Registering the fuel provider is what re-offers every ship_fuel milestone the
            // ledger is still holding deferred: the broker raises RewardProviderAvailable off
            // this call, and the milestone owner replays what it owes. That is the whole
            // mechanism by which turning the fuel system on mid-save pays out the milestones
            // already passed.
            QuotaRewardBroker.RegisterProvider(_fuelReward);
        }

        /// <summary>
        /// Route-fuel pricing and reservation for the custom terminal (#666), which lives in
        /// Y4NGZTerminalUpgrades now and cannot reference this assembly. Every member
        /// forwards to <see cref="ShipSystemsApi"/> so the null-controller case is handled in
        /// exactly one place.
        /// </summary>
        private sealed class FuelRoutingService : IShipFuelRoutingService
        {
            public bool IsFuelSystemEnabled => ShipSystemsApi.IsFuelSystemEnabled();

            public float FuelFraction => ShipSystemsApi.GetFuelFraction();

            public float FuelCapacity => ShipSystemsApi.GetFuelCapacity();

            public ShipFuelRouteQuote GetRouteQuote(
                int routeIndex,
                int levelId,
                string moonName,
                string riskTier,
                string constellationKey,
                string constellationName,
                bool isCompanyMoon)
            {
                return Convert(ShipSystemsApi.GetFuelQuoteForRoute(
                    routeIndex, levelId, moonName, riskTier,
                    constellationKey, constellationName, isCompanyMoon));
            }

            public bool TryReserveRouteFuel(
                int routeIndex,
                int levelId,
                string moonName,
                string riskTier,
                string constellationKey,
                string constellationName,
                bool isCompanyMoon,
                out ShipFuelRouteQuote quote)
            {
                bool reserved = ShipSystemsApi.TryReserveFuelForRoute(
                    routeIndex, levelId, moonName, riskTier,
                    constellationKey, constellationName, isCompanyMoon,
                    out Fuel.ShipFuelQuote raw);
                quote = Convert(raw);
                return reserved;
            }

            public bool CommitReservedRoute(int routeIndex) =>
                ShipSystemsApi.CommitReservedFuelRoute(routeIndex);

            public void CancelReservedRoute() => ShipSystemsApi.CancelReservedFuelRoute();

            public float GetConstellationEntryCost(string constellationName) =>
                ShipSystemsApi.GetConstellationEntryFuelCost(constellationName);

            public float GetMoonCostForConstellation(string constellationName, bool isCompanyMoon) =>
                ShipSystemsApi.GetMoonFuelCostForConstellation(constellationName, isCompanyMoon);

            public int SourceFillCount => ShipSystemsApi.GetFuelSourceFillCount();

            public int FilledPumpFuelAmount => ShipSystemsApi.GetFilledPumpFuelAmount();

            private static ShipFuelRouteQuote Convert(Fuel.ShipFuelQuote quote)
            {
                return new ShipFuelRouteQuote(
                    quote.FuelSystemAvailable,
                    quote.IsAffordable,
                    quote.IsCurrentConstellation,
                    quote.HasFuelSource,
                    quote.FuelAvailable,
                    quote.FuelCapacity,
                    quote.ReservedFuel,
                    quote.EntryFuelCost,
                    quote.MoonFuelCost,
                    quote.RouteKey,
                    quote.ConstellationKey,
                    quote.ConstellationName,
                    quote.DenialText);
            }
        }

        /// <summary>
        /// Pays the <see cref="QuotaRewardKinds.ShipFuel"/> milestone reward.
        ///
        /// <para><b>The three return values are the whole point of this class.</b> The pre-#608
        /// code recorded a fuel milestone as paid whenever the fuel system happened to be on,
        /// and <c>AddFuel</c> returns what it actually added — which is zero on a full tank, and
        /// the rollover refill always runs first. Every fuel milestone in every save was
        /// therefore written as paid without a drop of fuel changing hands.</para>
        ///
        /// <list type="bullet">
        /// <item><description><b>Deferred</b> — the fuel system is off or the controller has not
        /// spawned. Nobody could pay; the milestone stays owed and is re-offered when the
        /// provider or the system comes back.</description></item>
        /// <item><description><b>NoHeadroom</b> — we are the right owner and the tank is full.
        /// Also still owed, and the distinction exists so the log can say which it
        /// was.</description></item>
        /// <item><description><b>Granted</b> — fuel actually landed. The only outcome that
        /// consumes the ledger action.</description></item>
        /// </list>
        /// </summary>
        private sealed class ShipFuelRewardProvider : IQuotaRewardProvider
        {
            public string RewardKind => QuotaRewardKinds.ShipFuel;

            public QuotaRewardResult TryGrant(QuotaRewardGrant grant)
            {
                if (!ShipSystemsController.IsFuelSystemEnabled() ||
                    ShipSystemsController.Instance == null)
                {
                    return QuotaRewardResult.Deferred;
                }

                float added = ShipSystemsController.Instance.AddFuel(
                    (float)grant.Amount, $"quota {grant.Quota} milestone");
                return added > 0f ? QuotaRewardResult.Granted : QuotaRewardResult.NoHeadroom;
            }
        }

        private sealed class PowerStatusProvider : IShipPowerStatusProvider
        {
            public bool IsRestoreObjectiveActive => ShipSystemsApi.IsRestoreObjectiveActive();
            public bool IsBatteryBroken => ShipSystemsApi.IsBatteryBroken();
            public string RestoreRecoveryObjectiveText =>
                ShipSystemsApi.GetRestoreRecoveryObjectiveText() ?? string.Empty;
            public bool IsAnyPlayerHoldingRestoreItem =>
                ShipSystemsApi.IsAnyPlayerHoldingRestoreItem();
        }

        /// <summary>
        /// The engine-typed half of <see cref="IBatterySabotageCoordinator"/>.
        ///
        /// <para>The contract takes <see cref="object"/> because the whole
        /// <c>Y4NGZCore.Modules</c> layer is plain BCL so the off-engine harnesses can execute
        /// it. Every member therefore begins with an <c>as</c> cast, and an argument that is
        /// not the expected engine type is answered exactly as an unknown enemy is: no target,
        /// no destination, release the interrupt. That is the contract's stated rule, and it is
        /// also the only answer that cannot strand an AI.</para>
        /// </summary>
        private sealed class BatterySabotageCoordinator : IBatterySabotageCoordinator
        {
            public bool TryForceTarget(object enemy) =>
                enemy is EnemyAI ai && ShipSystemsApi.TryForceBatterySabotageTarget(ai);

            public bool IsTargetActive(object enemy) =>
                enemy is EnemyAI ai && ShipSystemsApi.IsBatterySabotageTargetActive(ai);

            public bool TryGetDestination(object enemy, out float x, out float y, out float z)
            {
                x = 0f;
                y = 0f;
                z = 0f;
                if (!(enemy is EnemyAI ai) ||
                    !ShipSystemsApi.TryGetBatterySabotageDestination(ai, out Vector3 destination))
                {
                    return false;
                }

                x = destination.x;
                y = destination.y;
                z = destination.z;
                return true;
            }

            public object TryGetInterruptTarget(object enemy)
            {
                if (!(enemy is EnemyAI ai))
                    return null;

                return ShipSystemsApi.TryGetBatterySabotageInterruptTarget(
                    ai, out PlayerControllerB player)
                    ? player
                    : null;
            }

            public bool ShouldReleaseInterrupt(object enemy, object player)
            {
                // True is the safe answer, and the one an unrecognised argument gets.
                if (!(enemy is EnemyAI ai) || !(player is PlayerControllerB controller))
                    return true;

                return ShipSystemsApi.ShouldReleaseBatterySabotageInterrupt(ai, controller);
            }

            public void ClearInterrupt(object enemy)
            {
                if (enemy is EnemyAI ai)
                    ShipSystemsApi.ClearBatterySabotageInterrupt(ai);
            }
        }

    }
}
