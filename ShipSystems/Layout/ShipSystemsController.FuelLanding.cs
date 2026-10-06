using System;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Fuel;

namespace Y4NGZCompany.ShipSystems.Layout
{
    internal sealed partial class ShipSystemsController
    {
        internal void OnShipLanded()
        {
            // Called when the landing doors start opening. The later vanilla landed
            // event is only an idempotent fallback; shipHasLanded is still false here.
            if (!IsServer())
                return;

            ApplyReservedRouteFuelChargeOnLanding();
        }

        private bool ApplyReservedRouteFuelChargeOnLanding()
        {
            if (!FuelSystemEnabled)
                return false;

            EnsureFuelInitialized();
            if (string.IsNullOrEmpty(_reservedFuelRouteKey))
                return false;

            StartOfRound start = StartOfRound.Instance;
            int landedRouteIndex = start != null ? start.currentLevelID : -1;
            if (landedRouteIndex >= 0 &&
                _reservedFuelRoute.RouteIndex >= 0 &&
                landedRouteIndex != _reservedFuelRoute.RouteIndex)
            {
                ShipSystemsBootstrap.Log.LogWarning(
                    $"[LGUShipSystems.Fuel] Dropped pending route fuel for route {_reservedFuelRoute.RouteIndex}; landed route is {landedRouteIndex}.");
                ClearReservedFuelRoute(refundFuel: false, broadcast: true);
                return false;
            }

            ShipFuelRouteRequest route = _reservedFuelRoute;
            float charge = Mathf.Max(0f, _reservedRouteFuel);
            _fuel = Mathf.Clamp(_fuel - charge, 0f, FuelCapacity);
            _currentFuelConstellationKey = route.ConstellationKey;
            _reservedRouteFuel = 0f;
            _reservedFuelRoute = default;
            _reservedFuelRouteKey = string.Empty;
            MarkPowerDisplayDirty();
            BroadcastFuelState();
            ShipSystemsBootstrap.Log.LogInfo(
                $"[LGUShipSystems.Fuel] Charged {charge:0.0} fuel on landing at {route.MoonName}. Fuel={_fuel:0.0}/{FuelCapacity:0.0}.");
            return true;
        }

    }
}
