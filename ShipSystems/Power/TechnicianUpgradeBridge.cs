using System;
using System.Reflection;
using UnityEngine;

namespace Y4NGZCompany.ShipSystems.Power
{
    /// <summary>
    /// Soft bridge into the Y4NGZUpgrades skill tree. The Technician-class
    /// "Field Mechanic" upgrade (tier 2) authorises ship battery field repairs.
    /// Y4NGZUpgrades is optional from this assembly's perspective.
    /// </summary>
    internal static class TechnicianUpgradeBridge
    {
        private const string AccessorTypeName = "Y4NGZUpgrades.Upgrades.TurretHackerUpgrade, Y4NGZUpgrades";

        private static Type _accessorType;
        private static MethodInfo _canRepairBatteriesMethod;
        private static float _nextResolveAttemptAt;

        internal static bool CanRepairBatteries()
        {
            if (!Resolve() || _canRepairBatteriesMethod == null)
                return false;

            try
            {
                return (bool)_canRepairBatteriesMethod.Invoke(null, Array.Empty<object>());
            }
            catch
            {
                return false;
            }
        }

        private static bool Resolve()
        {
            if (_accessorType != null)
                return true;

            if (Time.realtimeSinceStartup < _nextResolveAttemptAt)
                return false;

            _nextResolveAttemptAt = Time.realtimeSinceStartup + 2f;
            _accessorType = Type.GetType(AccessorTypeName, throwOnError: false);
            if (_accessorType == null)
                return false;

            _canRepairBatteriesMethod = _accessorType.GetMethod(
                "CanRepairBatteries",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return true;
        }
    }
}
