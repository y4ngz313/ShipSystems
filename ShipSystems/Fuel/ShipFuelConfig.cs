using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class ShipFuelConfig
    {
        internal const string MoonFuelCostByTierConfigName = "Moon Prices by Risk";

        internal static float TankSize => Math.Max(1f, ShipSystemsBootstrap.FuelTankSize?.Value ?? 100f);
        internal static float DefaultEntryCost => Math.Max(0f, ShipSystemsBootstrap.DefaultConstellationEntryFuelCost?.Value ?? 14f);
        internal static float SourceChance01 => Math.Max(0f, Math.Min(1f, (ShipSystemsBootstrap.FuelSourceChancePercent?.Value ?? 50f) / 100f));
        internal static int SourceFillCount => Math.Max(1, ShipSystemsBootstrap.FuelSourceFillCount?.Value ?? 3);
        internal static float PumpFillSeconds => Math.Max(1f, ShipSystemsBootstrap.FuelPumpFillSeconds?.Value ?? 240f);
        internal static float PumpStallChance01 => Math.Max(0f, Math.Min(1f, (ShipSystemsBootstrap.FuelPumpStallChancePercent?.Value ?? 18f) / 100f));
        internal static float PumpFuelAmount => Math.Max(0f, ShipSystemsBootstrap.FuelPumpFuelAmount?.Value ?? 30f);
        internal static int PumpPrice => Math.Max(0, ShipSystemsBootstrap.FuelPumpPrice?.Value ?? 80);

        internal static float GetMoonFuelCost(string moonName, string riskTier, bool isCompanyMoon)
        {
            if (isCompanyMoon)
                return GetNamedFloat(ShipSystemsBootstrap.MoonFuelCostByTier?.Value, "Company", 0f);
            if (IsEmergencyMoon(moonName))
                return Math.Max(0f, ShipSystemsBootstrap.EmergencyMoonFuelCost?.Value ?? 0f);

            string tier = NormalizeRiskTier(riskTier);
            return GetNamedFloat(ShipSystemsBootstrap.MoonFuelCostByTier?.Value, tier, GetNamedFloat(ShipSystemsBootstrap.MoonFuelCostByTier?.Value, "Unknown", 12f));
        }

        /// <summary>
        /// Constellation-only moon price. #679 keeps this overload byte-identical in behaviour
        /// to what shipped before: a quarter of the constellation entry cost, rounded up. It is
        /// what a caller that does not know WHICH moon it is pricing must use, and it is
        /// deliberately not routed through the per-moon precedence chain, because with no moon
        /// name there is no per-moon price or blacklist entry to consult.
        /// </summary>
        internal static float GetMoonFuelCostForConstellation(string constellationName, bool isCompanyMoon)
        {
            if (isCompanyMoon)
                return 0f;

            return Economy.MoonFuelPriceResolver.DeriveMoonCostFromEntryCost(
                GetConstellationEntryFuelCost(constellationName));
        }

        /// <summary>
        /// #679: the moon-aware price. Precedence is explicit per-moon price, then the fuel
        /// blacklist, then the emergency-moon binding, then an explicit constellation price,
        /// then the risk-tier table - resolved by
        /// <see cref="Economy.MoonFuelPriceResolver"/>. Callers that hold the route's moon name
        /// should prefer this over the constellation-only overload above.
        /// </summary>
        internal static float GetMoonFuelCostForConstellation(
            string moonName,
            string riskTier,
            string constellationName,
            bool isCompanyMoon)
        {
            return Economy.MoonFuelCatalogue
                .ResolveMoonCost(moonName, riskTier, constellationName, isCompanyMoon)
                .Cost;
        }

        /// <summary>
        /// Constellation entry cost, now resolved through the generated per-constellation
        /// entries with the legacy <c>Constellation Entry Fuel Cost By Name</c> map as the
        /// fallback for constellations this profile never detected. A blacklisted moon zeroes
        /// the entry cost too, so "fuel is not a factor" covers the whole route.
        /// </summary>
        internal static float GetConstellationEntryFuelCost(string constellationName)
        {
            return GetConstellationEntryFuelCost(null, constellationName, isCompanyMoon: false);
        }

        internal static float GetConstellationEntryFuelCost(
            string moonName,
            string constellationName,
            bool isCompanyMoon)
        {
            return Economy.MoonFuelCatalogue
                .ResolveEntryCost(moonName, constellationName, isCompanyMoon)
                .Cost;
        }

        /// <summary>True when the host put this moon on the fuel blacklist (#679).</summary>
        internal static bool IsFuelBlacklistedMoon(string moonName)
        {
            return Economy.MoonFuelCatalogue.IsBlacklisted(moonName);
        }

        internal static bool IsEmergencyMoon(string moonName)
        {
            string normalizedMoon = NormalizeKey(moonName);
            if (string.IsNullOrWhiteSpace(normalizedMoon))
                return false;

            string raw = ShipSystemsBootstrap.EmergencyMoonNameContains?.Value ?? string.Empty;
            foreach (string part in raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string key = NormalizeKey(part);
                if (key.Length > 0 && normalizedMoon.Contains(key))
                    return true;
            }

            return false;
        }

        internal static bool IsCompanyMoonName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            return name.IndexOf("Gordion", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Company", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static Dictionary<string, float> ParseFloatMap(string raw)
        {
            var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw))
                return result;

            foreach (string segment in raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = segment.IndexOf('=');
                if (equals <= 0)
                    continue;
                string key = NormalizeKey(segment.Substring(0, equals));
                string valueText = segment.Substring(equals + 1).Trim();
                if (key.Length > 0 && float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                    result[key] = value;
            }

            return result;
        }

        internal static string NormalizeKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        private static float GetNamedFloat(string raw, string key, float fallback)
        {
            Dictionary<string, float> map = ParseFloatMap(raw);
            return map.TryGetValue(NormalizeKey(key), out float value) ? Math.Max(0f, value) : fallback;
        }

        private static string NormalizeRiskTier(string riskTier)
        {
            string normalized = NormalizeKey(riskTier);
            if (string.IsNullOrEmpty(normalized))
                return "Unknown";
            if (normalized.Contains("company") || normalized.Contains("safe"))
                return "Company";
            if (normalized.Contains("s"))
                return "S";
            if (normalized.Contains("a"))
                return "A";
            if (normalized.Contains("b"))
                return "B";
            if (normalized.Contains("c"))
                return "C";
            if (normalized.Contains("d"))
                return "D";
            return "Unknown";
        }
    }
}
