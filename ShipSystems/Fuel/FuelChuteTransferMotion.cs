using UnityEngine;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelChuteTransferMotion
    {
        internal const float SinkSeconds = .24f;
        internal const float HoldSeconds = .7f;
        internal const float EjectAt = SinkSeconds + HoldSeconds;
        internal const float RiseSeconds = .18f;
        internal const float FlightSeconds = .55f;
        internal const float RejectSeconds = EjectAt + RiseSeconds + FlightSeconds;
        internal static readonly Vector3 Sink = new Vector3(0f, -.68f, 0f);
        internal static Vector3 Position(Vector3 start, Vector3 end, bool rejected, float elapsed)
        {
            if (!rejected) return Vector3.Lerp(start, end, Mathf.SmoothStep(0f, 1f, elapsed / SinkSeconds));
            if (elapsed < SinkSeconds) return Vector3.Lerp(start, Sink, Mathf.SmoothStep(0f, 1f, elapsed / SinkSeconds));
            if (elapsed < EjectAt) return Sink;
            Vector3 launch = new Vector3(0f, .8f, 0f);
            if (elapsed < EjectAt + RiseSeconds)
                return Vector3.Lerp(Sink, launch, Mathf.Clamp01((elapsed - EjectAt) / RiseSeconds));
            float t = Mathf.Clamp01((elapsed - EjectAt - RiseSeconds) / FlightSeconds);
            return Vector3.Lerp(launch, end, t) + Vector3.up * (Mathf.Sin(t * Mathf.PI) * .45f);
        }
    }
}
