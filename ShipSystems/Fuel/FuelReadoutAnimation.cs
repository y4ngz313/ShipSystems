using System;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    // Presentation only: the host debits real fuel once at landing. Each visible
    // step is one unit, even at low frame rates; fractional endpoints stay exact.
    internal sealed class FuelReadoutAnimation
    {
        internal const float StepSeconds = 1f / 30f;
        internal float Value { get; private set; }
        private float _target, _nextStepAt;
        private bool _initialized;
        internal bool IsInitialized => _initialized;
        internal float Sample(float fuel, float capacity, float now)
        {
            if (float.IsNaN(fuel) || float.IsInfinity(fuel) || float.IsNaN(capacity)
                || float.IsInfinity(capacity) || capacity <= 0f) return Value;
            float target = Math.Max(0f, Math.Min(fuel, capacity));
            if (!_initialized) { Value = _target = target; _initialized = true; return Value; }
            Value = Math.Min(Value, capacity);
            if (target != _target) { _target = target; _nextStepAt = now; }
            if (Value != _target && now + .00001f >= _nextStepAt)
            {
                Value += Math.Sign(_target - Value) * Math.Min(1f, Math.Abs(_target - Value));
                _nextStepAt = now + StepSeconds;
            }
            return Value;
        }
        internal void Reset() { _initialized = false; }
    }
}
