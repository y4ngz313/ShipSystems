namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal readonly struct ShipFuelRouteRequest
    {
        internal readonly int RouteIndex;
        internal readonly int LevelId;
        internal readonly string MoonName;
        internal readonly string RiskTier;
        internal readonly string ConstellationKey;
        internal readonly string ConstellationName;
        internal readonly bool IsCompanyMoon;

        internal ShipFuelRouteRequest(
            int routeIndex,
            int levelId,
            string moonName,
            string riskTier,
            string constellationKey,
            string constellationName,
            bool isCompanyMoon)
        {
            RouteIndex = routeIndex;
            LevelId = levelId;
            MoonName = moonName ?? string.Empty;
            RiskTier = riskTier ?? string.Empty;
            ConstellationKey = ShipFuelConfig.NormalizeKey(constellationKey);
            ConstellationName = string.IsNullOrWhiteSpace(constellationName) ? ConstellationKey : constellationName.Trim();
            IsCompanyMoon = isCompanyMoon;
        }

        internal string RouteKey => $"{RouteIndex}:{LevelId}:{ShipFuelConfig.NormalizeKey(MoonName)}";
    }
}
