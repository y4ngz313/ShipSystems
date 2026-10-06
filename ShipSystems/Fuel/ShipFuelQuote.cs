namespace Y4NGZCompany.ShipSystems.Fuel
{
    public readonly struct ShipFuelQuote
    {
        public readonly bool FuelSystemAvailable;
        public readonly bool IsAffordable;
        public readonly bool IsCurrentConstellation;
        public readonly bool HasFuelSource;
        public readonly float FuelAvailable;
        public readonly float FuelCapacity;
        public readonly float ReservedFuel;
        public readonly float EntryFuelCost;
        public readonly float MoonFuelCost;
        public readonly float TotalFuelCost;
        public readonly float FuelAfterRoute;
        public readonly string RouteKey;
        public readonly string ConstellationKey;
        public readonly string ConstellationName;
        public readonly string DenialText;

        internal ShipFuelQuote(
            bool fuelSystemAvailable,
            bool isAffordable,
            bool isCurrentConstellation,
            bool hasFuelSource,
            float fuelAvailable,
            float fuelCapacity,
            float reservedFuel,
            float entryFuelCost,
            float moonFuelCost,
            string routeKey,
            string constellationKey,
            string constellationName,
            string denialText)
        {
            FuelSystemAvailable = fuelSystemAvailable;
            IsAffordable = isAffordable;
            IsCurrentConstellation = isCurrentConstellation;
            HasFuelSource = hasFuelSource;
            FuelAvailable = fuelAvailable;
            FuelCapacity = fuelCapacity;
            ReservedFuel = reservedFuel;
            EntryFuelCost = entryFuelCost;
            MoonFuelCost = moonFuelCost;
            TotalFuelCost = entryFuelCost + moonFuelCost;
            FuelAfterRoute = fuelAvailable - TotalFuelCost;
            RouteKey = routeKey ?? string.Empty;
            ConstellationKey = constellationKey ?? string.Empty;
            ConstellationName = constellationName ?? string.Empty;
            DenialText = denialText ?? string.Empty;
        }
    }
}
