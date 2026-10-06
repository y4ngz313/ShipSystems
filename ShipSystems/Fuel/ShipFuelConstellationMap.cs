using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class ShipFuelConstellationMap
    {
        private sealed class Entry
        {
            internal string Key;
            internal string DisplayName;
            internal List<string> MoonNames = new List<string>();
        }

        private static List<Entry> _cachedEntries;
        private static DateTime _cachedWriteTimeUtc;

        internal static bool TryResolveRoute(
            int routeIndex,
            SelectableLevel level,
            SelectableLevel[] levels,
            out string constellationKey,
            out string constellationName,
            out int[] routeIndexes)
        {
            constellationKey = string.Empty;
            constellationName = string.Empty;
            routeIndexes = Array.Empty<int>();
            if (level == null)
                return false;

            List<Entry> entries = Load();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (!MoonMatches(level, entry.MoonNames))
                    continue;

                constellationKey = entry.Key;
                constellationName = entry.DisplayName;
                routeIndexes = BuildRouteIndexes(levels, entry.MoonNames);
                if (routeIndexes.Length == 0 && routeIndex >= 0)
                    routeIndexes = new[] { routeIndex };
                FuelRouteConstellationIndexCache.Remember(constellationKey, routeIndexes);
                return true;
            }

            return false;
        }

        private static List<Entry> Load()
        {
            string path = Path.Combine(Paths.ConfigPath, "com.github.darmuh.LethalConstellations.cfg");
            DateTime writeTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_cachedEntries != null && writeTime == _cachedWriteTimeUtc)
                return _cachedEntries;

            var entries = new List<Entry>();
            try
            {
                if (File.Exists(path))
                {
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        string line = rawLine.Trim();
                        if (!line.StartsWith("ManualSetupListing", StringComparison.OrdinalIgnoreCase))
                            continue;

                        int equals = line.IndexOf('=');
                        if (equals < 0)
                            continue;

                        string value = line.Substring(equals + 1).Trim();
                        foreach (string segment in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int colon = segment.IndexOf(':');
                            if (colon < 0)
                                continue;

                            string displayName = segment.Substring(0, colon).Trim();
                            List<string> moons = segment.Substring(colon + 1)
                                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(moon => moon.Trim())
                                .Where(moon => !string.IsNullOrWhiteSpace(moon))
                                .ToList();
                            if (string.IsNullOrWhiteSpace(displayName) || moons.Count == 0)
                                continue;

                            entries.Add(new Entry
                            {
                                Key = ShipFuelConfig.NormalizeKey(displayName),
                                DisplayName = displayName,
                                MoonNames = moons,
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log?.LogWarning($"[LGUShipSystems.Fuel] Failed to read constellation map: {ex.Message}");
            }

            _cachedEntries = entries;
            _cachedWriteTimeUtc = writeTime;
            return entries;
        }

        private static int[] BuildRouteIndexes(SelectableLevel[] levels, List<string> moonNames)
        {
            if (levels == null || moonNames == null)
                return Array.Empty<int>();

            List<int> routeIndexes = new List<int>();
            for (int i = 0; i < levels.Length; i++)
            {
                SelectableLevel level = levels[i];
                if (level != null && MoonMatches(level, moonNames))
                    routeIndexes.Add(i);
            }

            return routeIndexes.Distinct().OrderBy(index => index).ToArray();
        }

        private static bool MoonMatches(SelectableLevel level, List<string> moonNames)
        {
            if (level == null || moonNames == null)
                return false;

            string planet = ShipFuelConfig.NormalizeKey(level.PlanetName);
            foreach (string rawMoon in moonNames)
            {
                string moon = ShipFuelConfig.NormalizeKey(rawMoon);
                if (moon.Length > 0 && (planet.Contains(moon) || moon.Contains(planet)))
                    return true;
            }

            return false;
        }
    }
}
