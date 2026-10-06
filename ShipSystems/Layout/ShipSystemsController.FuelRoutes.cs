using System;
using UnityEngine;
using Y4NGZCompany.ShipSystems.Fuel;
namespace Y4NGZCompany.ShipSystems.Layout
{
    internal sealed partial class ShipSystemsController
    {
        internal ShipFuelQuote GetFuelQuote(ShipFuelRouteRequest request, int[] constellationRouteIndexes)
        {
            if (!FuelSystemEnabled)
                return DisabledFuelQuote();

            EnsureFuelInitialized();
            FuelRouteConstellationIndexCache.Remember(request.ConstellationKey, constellationRouteIndexes);

            bool emergencyRoute = ShipFuelConfig.IsEmergencyMoon(request.MoonName);
            string currentFuelConstellationKey = ResolveCurrentFuelConstellationKey();
            bool hasRequestConstellationKey = request.ConstellationKey.Length > 0;
            bool currentConstellation = request.IsCompanyMoon ||
                (hasRequestConstellationKey
                 && currentFuelConstellationKey.Length > 0
                 && string.Equals(currentFuelConstellationKey, request.ConstellationKey, StringComparison.OrdinalIgnoreCase));

            // #679: both prices are resolved from the MOON, not just its constellation, so the
            // generated per-moon entries and the fuel blacklist reach the route that is actually
            // charged. Passing the moon name is the whole difference: without it the resolver has
            // no per-moon price or blacklist entry to consult and falls back to the constellation
            // arithmetic this line used before.
            // An ungrouped vanilla moon has no constellation boundary to charge.
            float entryCost = !hasRequestConstellationKey || currentConstellation || emergencyRoute
                ? 0f
                : ShipFuelConfig.GetConstellationEntryFuelCost(
                    request.MoonName, request.ConstellationName, request.IsCompanyMoon);
            float moonCost = ShipFuelConfig.GetMoonFuelCostForConstellation(
                request.MoonName, request.RiskTier, request.ConstellationName, request.IsCompanyMoon);
            if (request.IsCompanyMoon)
                entryCost = 0f;

            float reservedForSameRoute = string.Equals(_reservedFuelRouteKey, request.RouteKey, StringComparison.Ordinal)
                ? _reservedRouteFuel
                : 0f;
            float available = Mathf.Clamp(_fuel, 0f, FuelCapacity);
            float total = entryCost + moonCost;
            bool affordable = available + 0.001f >= total;
            bool hasFuelSource = false; // Oil rigs are disabled; do not advertise a source.

            string denial = affordable
                ? string.Empty
                : $"Insufficient ship fuel. Required {Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Amount(total)}, available {Y4NGZCompany.ShipSystems.Fuel.FuelPresentation.Amount(available)}.";

            return new ShipFuelQuote(
                fuelSystemAvailable: true,
                isAffordable: affordable,
                isCurrentConstellation: currentConstellation,
                hasFuelSource: hasFuelSource,
                fuelAvailable: available,
                fuelCapacity: FuelCapacity,
                reservedFuel: reservedForSameRoute,
                entryFuelCost: entryCost,
                moonFuelCost: moonCost,
                routeKey: request.RouteKey,
                constellationKey: request.ConstellationKey,
                constellationName: request.ConstellationName,
                denialText: denial);
        }

        private string ResolveCurrentFuelConstellationKey()
        {
            if (!string.IsNullOrWhiteSpace(_currentFuelConstellationKey))
                return _currentFuelConstellationKey;

            StartOfRound start = StartOfRound.Instance;
            if (start == null)
                return string.Empty;

            SelectableLevel[] levels = start.levels;
            int routeIndex = start.currentLevelID;
            SelectableLevel level = ResolveSelectableLevel(levels, routeIndex) ?? start.currentLevel;
            if (level == null)
                return string.Empty;

            if (ShipFuelConstellationMap.TryResolveRoute(
                    routeIndex,
                    level,
                    levels,
                    out string constellationKey,
                    out string constellationName,
                    out int[] routeIndexes))
            {
                _currentFuelConstellationKey = constellationKey ?? string.Empty;
                FuelRouteConstellationIndexCache.Remember(_currentFuelConstellationKey, routeIndexes);
                return _currentFuelConstellationKey;
            }

            _currentFuelConstellationKey = string.Empty;
            return _currentFuelConstellationKey;
        }

        private bool TryBuildServerRouteRequest(int routeIndex, out ShipFuelRouteRequest request, out int[] constellationRouteIndexes)
        {
            request = default;
            constellationRouteIndexes = Array.Empty<int>();
            StartOfRound start = StartOfRound.Instance;
            SelectableLevel[] levels = start?.levels;
            SelectableLevel level = ResolveSelectableLevel(levels, routeIndex);
            if (level == null)
                return false;

            string constellationKey;
            bool knownConstellation = ShipFuelConstellationMap.TryResolveRoute(
                routeIndex,
                level,
                levels,
                out constellationKey,
                out string constellationName,
                out constellationRouteIndexes);
            if (!knownConstellation)
                knownConstellation = FuelRouteConstellationIndexCache.TryGetConstellationKeyForRoute(routeIndex, out constellationKey);
            if (!knownConstellation || string.IsNullOrWhiteSpace(constellationKey))
                constellationKey = string.Empty; // Synthetic source groups are not priced constellations.

            if (constellationRouteIndexes == null || constellationRouteIndexes.Length == 0)
            {
                constellationRouteIndexes = knownConstellation
                    ? FuelRouteConstellationIndexCache.GetRouteIndexes(constellationKey)
                    : FuelRouteConstellationIndexCache.GetKnownRouteIndexesOrFallback(levels);
            }

            request = new ShipFuelRouteRequest(
                routeIndex,
                level.levelID,
                level.PlanetName,
                level.riskLevel,
                constellationKey,
                knownConstellation && !string.IsNullOrWhiteSpace(constellationName) ? constellationName : constellationKey,
                ShipFuelConfig.IsCompanyMoonName(level.PlanetName));
            return true;
        }

    }
}
