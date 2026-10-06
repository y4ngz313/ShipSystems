using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel.Economy
{
    /// <summary>One detected moon and the config entry that prices it.</summary>
    internal sealed class MoonFuelCatalogueEntry
    {
        internal MoonFuelCatalogueEntry(
            string displayName,
            string lookupKey,
            string configKey,
            float generatedDefault,
            ConfigEntry<float> binding)
        {
            DisplayName = displayName;
            LookupKey = lookupKey;
            ConfigKey = configKey;
            GeneratedDefault = generatedDefault;
            Binding = binding;
            Aliases = new List<string>();
        }

        internal string DisplayName { get; }
        internal string LookupKey { get; }
        internal string ConfigKey { get; }
        internal float GeneratedDefault { get; }
        internal ConfigEntry<float> Binding { get; }
        internal List<string> Aliases { get; }
        internal string ConstellationName { get; set; } = string.Empty;
        internal string RiskTier { get; set; } = string.Empty;
        internal bool IsCompanyMoon { get; set; }

        internal float Value => Binding != null ? Binding.Value : GeneratedDefault;

        internal bool IsOverridden =>
            Binding != null && MoonFuelPriceResolver.IsOverridden(Binding.Value, GeneratedDefault);
    }

    /// <summary>
    /// #679: launch-time moon detection and the generated per-moon / per-constellation fuel
    /// price surface.
    ///
    /// <para><b>When detection runs.</b> Never at plugin load - <c>StartOfRound.levels</c> does
    /// not exist yet and LethalLevelLoader has not injected modded moons. <see cref="EnsureBuilt"/>
    /// is called from the <c>StartOfRound.Start</c> and <c>Terminal.Start</c> postfixes, which is
    /// the first point where the catalogue is final, and again from the terminal text postfix.
    /// It is additive and idempotent: each call binds entries only for moons it has not seen, so
    /// a moon that a later mod adds mid-session still gets an entry, and a moon seen twice does
    /// not get a second one.</para>
    ///
    /// <para><b>Why entries survive a mod-list change.</b> Entries are keyed by the moon's own
    /// display name, sanitised for BepInEx (<see cref="MoonFuelConfigKeys"/>), not by route index
    /// or level id - both of which shuffle whenever a moon pack is added or removed. Uninstalling
    /// a moon leaves its section entry ORPHANED in the file. That is deliberate and safe: BepInEx
    /// keeps orphaned entries in memory and rewrites them on save, so re-installing the pack
    /// restores the price the host had set. This type never calls <c>ConfigFile.Clear()</c> and
    /// never removes a key, so no orphan purge can lose an edit. <c>Save()</c> is called once per
    /// build pass that added something, so a fresh profile has an editable file after one
    /// launch.</para>
    /// </summary>
    internal static class MoonFuelCatalogue
    {
        internal const string MoonPriceSection = "Fuel Moon Prices";
        internal const string ConstellationPriceSection = "Fuel Constellations";

        private static readonly object Gate = new object();

        private static readonly Dictionary<string, MoonFuelCatalogueEntry> Moons =
            new Dictionary<string, MoonFuelCatalogueEntry>(StringComparer.Ordinal);

        private static readonly Dictionary<string, MoonFuelCatalogueEntry> Constellations =
            new Dictionary<string, MoonFuelCatalogueEntry>(StringComparer.Ordinal);

        private static readonly List<MoonFuelCatalogueEntry> MoonOrder = new List<MoonFuelCatalogueEntry>();

        private static string _blacklistRaw;
        private static string[] _blacklistParsed = Array.Empty<string>();

        /// <summary>Every moon detected so far, in first-seen order.</summary>
        internal static IReadOnlyList<MoonFuelCatalogueEntry> DetectedMoons => MoonOrder;

        /// <summary>
        /// Detects moons and constellations and binds any entry that does not exist yet.
        /// Safe to call from any patch; failures are logged and swallowed, because a config
        /// surface must never be the reason a level fails to load.
        /// </summary>
        internal static void EnsureBuilt(string reason)
        {
            ConfigFile config = ShipSystemsBootstrap.ConfigFile;
            if (config == null)
                return;

            try
            {
                lock (Gate)
                {
                    bool added = ScanLevels(config);
                    if (added)
                    {
                        // Written immediately so the generated entries are editable between
                        // sessions rather than only after a clean shutdown.
                        config.Save();
                        ShipSystemsBootstrap.Log?.LogInfo(
                            $"[ShipSystems.Fuel] Fuel price catalogue built from {MoonOrder.Count} moon(s) " +
                            $"and {Constellations.Count} constellation(s) ({reason}).");
                    }
                }
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning(
                    $"[ShipSystems.Fuel] Fuel price catalogue build failed ({reason}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool ScanLevels(ConfigFile config)
        {
            StartOfRound startOfRound = StartOfRound.Instance;
            SelectableLevel[] levels = startOfRound != null ? startOfRound.levels : null;
            if (levels == null || levels.Length == 0)
                return false;

            bool added = false;
            for (int index = 0; index < levels.Length; index++)
            {
                SelectableLevel level = levels[index];
                if (level == null || string.IsNullOrWhiteSpace(level.PlanetName))
                    continue;

                string displayName = level.PlanetName.Trim();
                string lookupKey = MoonFuelConfigKeys.NormalizeLookup(displayName);
                if (lookupKey.Length == 0) continue;
                if (Moons.TryGetValue(lookupKey, out MoonFuelCatalogueEntry known))
                {
                    known.ConstellationName = ResolveConstellationName(index, level, levels);
                    if (!string.IsNullOrWhiteSpace(known.ConstellationName))
                        added |= EnsureConstellationEntry(config, known.ConstellationName);
                    continue;
                }

                bool isCompanyMoon = ShipFuelConfig.IsCompanyMoonName(displayName);
                string riskTier = level.riskLevel ?? string.Empty;
                float tierDefault = ShipFuelConfig.GetMoonFuelCost(displayName, riskTier, isCompanyMoon);

                string configKey = MoonFuelConfigKeys.Sanitize(displayName);
                ConfigEntry<float> binding = config.Bind(
                    MoonPriceSection,
                    configKey,
                    tierDefault,
                    new ConfigDescription(
                        "Fuel spent landing on " + displayName + ". Generated at launch from the risk tier " +
                        "(" + DescribeTier(riskTier) + ") using '" + ShipFuelConfig.MoonFuelCostByTierConfigName +
                        "', so leaving it alone keeps the tier pricing. Editing it makes this moon's price " +
                        "explicit, which outranks Free Travel Moons, the constellation price and the tier table. " +
                        "Entries for moons you later uninstall are kept, not deleted, so re-installing the " +
                        "pack restores this value.",
                        new AcceptableValueRange<float>(0f, 300f)));

                var entry = new MoonFuelCatalogueEntry(displayName, lookupKey, configKey, tierDefault, binding)
                {
                    RiskTier = riskTier,
                    IsCompanyMoon = isCompanyMoon,
                };
                entry.Aliases.Add(displayName);
                string stripped = MoonFuelConfigKeys.StripRoutePrefix(displayName);
                if (!string.Equals(stripped, displayName, StringComparison.Ordinal))
                    entry.Aliases.Add(stripped);

                entry.ConstellationName = ResolveConstellationName(index, level, levels);
                if (!string.IsNullOrWhiteSpace(entry.ConstellationName))
                    added |= EnsureConstellationEntry(config, entry.ConstellationName);

                Moons[lookupKey] = entry;
                MoonOrder.Add(entry);
                added = true;
            }

            return added;
        }

        private static string ResolveConstellationName(int routeIndex, SelectableLevel level, SelectableLevel[] levels)
        {
            try
            {
                if (ShipFuelConstellationMap.TryResolveRoute(
                        routeIndex, level, levels, out _, out string constellationName, out _))
                {
                    return constellationName ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning(
                    $"[ShipSystems.Fuel] Constellation lookup failed for '{level?.PlanetName}': {ex.Message}");
            }

            return string.Empty;
        }

        private static bool EnsureConstellationEntry(ConfigFile config, string constellationName)
        {
            string lookupKey = MoonFuelConfigKeys.NormalizeLookup(constellationName);
            if (lookupKey.Length == 0 || Constellations.ContainsKey(lookupKey))
                return false;

            // The generated default is the legacy comma map's value when the host already wrote
            // one there, so #679 does not silently discard a pre-existing override.
            float generatedDefault = ShipFuelConfig.DefaultEntryCost;
            Dictionary<string, float> legacy = ShipFuelConfig.ParseFloatMap(
                ShipSystemsBootstrap.ConstellationEntryFuelCostByName?.Value);
            if (legacy.TryGetValue(lookupKey, out float legacyValue))
                generatedDefault = Math.Max(0f, legacyValue);

            string configKey = MoonFuelConfigKeys.Sanitize(constellationName);
            ConfigEntry<float> binding = config.Bind(
                ConstellationPriceSection,
                configKey,
                generatedDefault,
                new ConfigDescription(
                    "Fuel spent once when crossing into the " + constellationName + " constellation. " +
                    "Generated at launch from 'Default Constellation Entry Fee' (or from an existing " +
                    "'Undetected Constellation Entry Fees' override for the same constellation). Editing " +
                    "it also sets the per-moon price for every moon in the constellation that has no " +
                    "explicit price of its own, at a quarter of this number, rounded up.",
                    new AcceptableValueRange<float>(0f, 300f)));

            Constellations[lookupKey] = new MoonFuelCatalogueEntry(
                constellationName, lookupKey, configKey, generatedDefault, binding);
            return true;
        }

        private static string DescribeTier(string riskTier)
        {
            return string.IsNullOrWhiteSpace(riskTier) ? "unrated" : riskTier.Trim();
        }

        // ---- lookups -------------------------------------------------------------------

        internal static bool TryGetMoon(string moonName, out MoonFuelCatalogueEntry entry)
        {
            entry = null;
            string lookupKey = MoonFuelConfigKeys.NormalizeLookup(moonName);
            if (lookupKey.Length == 0)
                return false;

            lock (Gate)
            {
                if (Moons.TryGetValue(lookupKey, out entry))
                    return true;

                // A caller may hold the bare name ("Experimentation") while the catalogue holds
                // the route-numbered one ("41 Experimentation"), or the reverse.
                foreach (KeyValuePair<string, MoonFuelCatalogueEntry> pair in Moons)
                {
                    if (MoonFuelConfigKeys.NameMatches(pair.Key, lookupKey))
                    {
                        entry = pair.Value;
                        return true;
                    }
                }
            }

            return false;
        }

        internal static bool TryGetMoonOverride(string moonName, out float value)
        {
            value = 0f;
            if (!TryGetMoon(moonName, out MoonFuelCatalogueEntry entry) || !entry.IsOverridden)
                return false;

            value = Math.Max(0f, entry.Value);
            return true;
        }

        internal static bool TryGetConstellationOverride(string constellationName, out float value)
        {
            value = 0f;
            string lookupKey = MoonFuelConfigKeys.NormalizeLookup(constellationName);
            if (lookupKey.Length == 0)
                return false;

            lock (Gate)
            {
                if (Constellations.TryGetValue(lookupKey, out MoonFuelCatalogueEntry entry) && entry.IsOverridden)
                {
                    value = Math.Max(0f, entry.Value);
                    return true;
                }
            }

            // The legacy comma map stays authoritative for constellations that were never
            // detected - a host running without LethalConstellations installed still has one.
            Dictionary<string, float> legacy = ShipFuelConfig.ParseFloatMap(
                ShipSystemsBootstrap.ConstellationEntryFuelCostByName?.Value);
            if (legacy.TryGetValue(lookupKey, out float legacyValue))
            {
                value = Math.Max(0f, legacyValue);
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when the host listed this moon in 'Fuel Blacklisted Moons'. The list is a single
        /// string binding rather than one entry per moon on purpose: it stays valid and editable
        /// whether or not the named moon is currently installed, which per-moon toggles would
        /// not.
        /// </summary>
        internal static bool IsBlacklisted(string moonName)
        {
            if (string.IsNullOrWhiteSpace(moonName))
                return false;

            string[] entries = GetBlacklistEntries();
            for (int i = 0; i < entries.Length; i++)
            {
                if (MoonFuelConfigKeys.NameMatches(moonName, entries[i]))
                    return true;
            }

            return false;
        }

        private static string[] GetBlacklistEntries()
        {
            string raw = ShipSystemsBootstrap.FuelBlacklistedMoons?.Value ?? string.Empty;
            if (string.Equals(raw, _blacklistRaw, StringComparison.Ordinal))
                return _blacklistParsed;

            var parsed = new List<string>();
            foreach (string segment in raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = segment.Trim();
                if (trimmed.Length > 0)
                    parsed.Add(trimmed);
            }

            _blacklistRaw = raw;
            _blacklistParsed = parsed.ToArray();
            return _blacklistParsed;
        }

        // ---- pricing -------------------------------------------------------------------

        internal static MoonFuelPriceInputs BuildInputs(
            string moonName,
            string riskTier,
            string constellationName,
            bool isCompanyMoon)
        {
            if (string.IsNullOrWhiteSpace(constellationName)
                && TryGetMoon(moonName, out MoonFuelCatalogueEntry known)
                && !string.IsNullOrWhiteSpace(known.ConstellationName))
            {
                constellationName = known.ConstellationName;
            }

            bool hasMoonOverride = TryGetMoonOverride(moonName, out float moonOverride);
            bool hasConstellationOverride = TryGetConstellationOverride(constellationName, out float constellationOverride);

            return new MoonFuelPriceInputs(
                isCompanyMoon: isCompanyMoon,
                isBlacklistedMoon: IsBlacklisted(moonName),
                isEmergencyMoon: ShipFuelConfig.IsEmergencyMoon(moonName),
                emergencyMoonCost: Math.Max(0f, ShipSystemsBootstrap.EmergencyMoonFuelCost?.Value ?? 0f),
                hasMoonOverride: hasMoonOverride,
                moonOverride: moonOverride,
                hasConstellationOverride: hasConstellationOverride,
                constellationOverride: constellationOverride,
                constellationEntryDefault: ShipFuelConfig.DefaultEntryCost,
                tierMoonCost: ShipFuelConfig.GetMoonFuelCost(moonName, riskTier, isCompanyMoon));
        }

        internal static MoonFuelPrice ResolveMoonCost(
            string moonName,
            string riskTier,
            string constellationName,
            bool isCompanyMoon)
        {
            MoonFuelPriceInputs inputs = BuildInputs(moonName, riskTier, constellationName, isCompanyMoon);
            return MoonFuelPriceResolver.ResolveMoonCost(inputs);
        }

        internal static MoonFuelPrice ResolveEntryCost(
            string moonName,
            string constellationName,
            bool isCompanyMoon)
        {
            MoonFuelPriceInputs inputs = BuildInputs(moonName, string.Empty, constellationName, isCompanyMoon);
            return MoonFuelPriceResolver.ResolveEntryCost(inputs);
        }

        /// <summary>
        /// Total route price for the terminal indicator. This is entry plus moon rather than the
        /// live quote, because the catalogue prices every moon at once and the quote is a
        /// per-route, reservation-aware object.
        /// </summary>
        internal static MoonFuelPrice ResolveCatalogueCost(MoonFuelCatalogueEntry entry)
        {
            if (entry == null)
                return new MoonFuelPrice(0f, MoonFuelPriceSource.TierDefault);

            MoonFuelPriceInputs inputs = BuildInputs(
                entry.DisplayName, entry.RiskTier, entry.ConstellationName, entry.IsCompanyMoon);
            return MoonFuelPriceResolver.ResolveMoonCost(inputs);
        }
    }
}
