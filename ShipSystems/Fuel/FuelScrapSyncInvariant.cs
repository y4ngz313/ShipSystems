using System;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelScrapSyncInvariant
    {
        internal static T[] EnsureArray<T>(T[] values)
        {
            return values ?? Array.Empty<T>();
        }
    }
}
