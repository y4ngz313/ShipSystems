using System;
using System.Collections.Generic;
using System.Linq;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class ShipFuelSourceAssignments
    {
        private static int _cachedSeed = int.MinValue;
        private static readonly Dictionary<string, int> SourceRouteByConstellation = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        internal static bool HasSource(int routeIndex, string constellationKey, IReadOnlyList<int> constellationRouteIndexes, int generationSeed)
        {
            Refresh(generationSeed, constellationKey, constellationRouteIndexes, routeIndex);
            string key = ShipFuelConfig.NormalizeKey(constellationKey);
            return SourceRouteByConstellation.TryGetValue(key, out int selected) && selected == routeIndex;
        }

        internal static void Clear()
        {
            _cachedSeed = int.MinValue;
            SourceRouteByConstellation.Clear();
        }

        private static void Refresh(int generationSeed, string constellationKey, IReadOnlyList<int> routeIndexes, int routeIndex)
        {
            if (_cachedSeed != generationSeed)
            {
                _cachedSeed = generationSeed;
                SourceRouteByConstellation.Clear();
            }

            string key = ShipFuelConfig.NormalizeKey(constellationKey);
            if (string.IsNullOrWhiteSpace(key) || SourceRouteByConstellation.ContainsKey(key))
                return;

            int[] candidates = routeIndexes?.Where(index => index >= 0).Distinct().OrderBy(index => index).ToArray() ?? Array.Empty<int>();
            if (candidates.Length == 0 && routeIndex >= 0)
                candidates = new[] { routeIndex };
            if (candidates.Length == 0)
                return;

            int seed = generationSeed ^ StableHash(key) ^ (candidates.Length * 397);
            var random = new Random(seed);
            if (random.NextDouble() > ShipFuelConfig.SourceChance01)
                return;

            SourceRouteByConstellation[key] = candidates[random.Next(candidates.Length)];
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < value.Length; i++)
                    hash = hash * 31 + value[i];
                return hash;
            }
        }
    }

    internal static class FuelRouteConstellationIndexCache
    {
        private static readonly Dictionary<string, int[]> RouteIndexesByConstellation = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, string> ConstellationKeyByRouteIndex = new Dictionary<int, string>();

        internal static void Remember(string constellationKey, int[] routeIndexes)
        {
            string key = ShipFuelConfig.NormalizeKey(constellationKey);
            if (string.IsNullOrWhiteSpace(key) || routeIndexes == null || routeIndexes.Length == 0)
                return;

            RouteIndexesByConstellation[key] = routeIndexes.Where(index => index >= 0).Distinct().OrderBy(index => index).ToArray();
            int[] normalizedRouteIndexes = RouteIndexesByConstellation[key];
            for (int i = 0; i < normalizedRouteIndexes.Length; i++)
                ConstellationKeyByRouteIndex[normalizedRouteIndexes[i]] = key;
        }

        internal static void SetRouteIndexes(string constellationKey, int[] routeIndexes)
        {
            Remember(constellationKey, routeIndexes);
        }

        internal static int[] GetRouteIndexes(string constellationKey)
        {
            string key = ShipFuelConfig.NormalizeKey(constellationKey);
            return key.Length > 0 && RouteIndexesByConstellation.TryGetValue(key, out int[] routeIndexes)
                ? routeIndexes
                : Array.Empty<int>();
        }

        internal static bool TryGetConstellationKeyForRoute(int routeIndex, out string constellationKey)
        {
            return ConstellationKeyByRouteIndex.TryGetValue(routeIndex, out constellationKey);
        }

        internal static int[] GetKnownRouteIndexesOrFallback(SelectableLevel[] levels)
        {
            int[] known = ConstellationKeyByRouteIndex.Keys.OrderBy(index => index).ToArray();
            if (known.Length > 0)
                return known;

            if (levels == null)
                return Array.Empty<int>();

            return levels
                .Select((level, index) => level != null ? index : -1)
                .Where(index => index >= 0)
                .ToArray();
        }

        internal static void Clear()
        {
            RouteIndexesByConstellation.Clear();
            ConstellationKeyByRouteIndex.Clear();
        }
    }
}
