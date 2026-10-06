using UnityEngine;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal enum FuelChuteDeposit { Wait, AcceptFuel, RejectItem }
    internal static class FuelChuteIntake
    {
        internal static FuelChuteDeposit Classify(GrabbableObject item, bool ready, float fuel, float capacity, out float value)
        {
            value = 0f;
            if (item == null || !ready) return FuelChuteDeposit.Wait;
            FuelLootItem loot = item.GetComponent<FuelLootItem>();
            if (loot != null) value = loot.FuelValue;
            else
            {
                FuelPumpItem pump = item.GetComponent<FuelPumpItem>();
                if (pump != null && pump.IsFilled) value = pump.FuelValue;
            }
            if (float.IsNaN(value) || float.IsInfinity(value)) return FuelChuteDeposit.Wait;
            if (value <= 0f) return FuelChuteDeposit.RejectItem;
            if (float.IsNaN(fuel) || float.IsNaN(capacity) || float.IsInfinity(fuel) || float.IsInfinity(capacity)
                || capacity <= 0f) return FuelChuteDeposit.Wait;
            if (fuel >= capacity - .001f) return FuelChuteDeposit.RejectItem;
            return FuelChuteDeposit.AcceptFuel;
        }
    }
}
