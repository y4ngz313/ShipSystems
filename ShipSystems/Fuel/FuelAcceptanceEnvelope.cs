using System;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    // Timing follows the approved drop -> ignition -> broad flare -> dark concept.
    internal static class FuelAcceptanceEnvelope
    {
        internal const float Duration = 1.45f;
        internal static float Strength(float seconds)
        {
            if (seconds <= .06f || seconds >= Duration || float.IsNaN(seconds)) return 0f;
            if (seconds < .30f) return Smooth((seconds - .06f) / .24f);
            if (seconds < .46f) return 1f;
            return 1f - Smooth((seconds - .46f) / (Duration - .46f));
        }
        internal static float PeakScale(float fuel)
        {
            if (float.IsNaN(fuel) || float.IsInfinity(fuel)) fuel = 4f;
            return .88f + .20f * Math.Max(0f, Math.Min(1f, (fuel - 4f) / 8f));
        }
        private static float Smooth(float t) => t * t * (3f - 2f * t);
    }
}
