using System;
using System.Globalization;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    // Ship Systems is independently deployed. Its readouts must not require a
    // newer Core binary merely to format a quantity.
    internal static class FuelPresentation
    {
        internal static string Amount(float value) => float.IsNaN(value) || float.IsInfinity(value)
            ? "--" : Math.Max(0f, value).ToString("0.###", CultureInfo.InvariantCulture);
        internal static string Tank(float amount, float capacity) => Amount(amount) + " / " + Amount(capacity);
    }
}
