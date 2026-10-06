using System;
using UnityEngine;
using Unity.Collections;
using Unity.Netcode;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Fuel;

namespace Y4NGZCompany.ShipSystems.Layout
{
    internal sealed partial class ShipSystemsController
    {
        private void OnReceiveFuelState(ulong senderClientId, FastBufferReader reader)
        {
            // A host already owns this state. Replaying its free-route snapshot
            // cleared the zero-cost reservation before route acknowledgement.
            if (IsServer() || !IsServerMessage(senderClientId)) return;

            try
            {
                bool fuelSystemEnabled;
                float fuel;
                float capacity;
                float reserved;
                int reservedRouteIndex;
                bool hasConstellation;
                FixedString64Bytes constellationKey;

                reader.ReadValueSafe(out fuelSystemEnabled);
                reader.ReadValueSafe(out fuel);
                reader.ReadValueSafe(out capacity);
                reader.ReadValueSafe(out reserved);
                reader.ReadValueSafe(out reservedRouteIndex);
                reader.ReadValueSafe(out hasConstellation);
                reader.ReadValueSafe(out constellationKey);

                _fuelSystemEnabled = fuelSystemEnabled;
                _fuelSystemEnabledReplicated = true;
                _fuelInitialized = true;
                _replicatedFuelCapacity = Mathf.Max(1f, capacity);
                _fuel = Mathf.Clamp(fuel, 0f, _replicatedFuelCapacity);
                _reservedRouteFuel = Mathf.Max(0f, reserved);
                if (_reservedRouteFuel <= 0f || reservedRouteIndex < 0)
                    _reservedFuelRouteKey = string.Empty;
                _currentFuelConstellationKey = hasConstellation ? ShipFuelConfig.NormalizeKey(constellationKey.ToString()) : string.Empty;
                MarkPowerDisplayDirty();
            }
            catch (Exception ex)
            {
                ShipSystemsBootstrap.Log.LogWarning($"[LGUShipSystems] malformed fuel state: {ex.Message}");
            }
        }

    }
}
