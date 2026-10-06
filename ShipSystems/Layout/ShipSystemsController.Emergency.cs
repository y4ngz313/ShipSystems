using System;
using UnityEngine;
namespace Y4NGZCompany.ShipSystems.Layout
{
    internal sealed partial class ShipSystemsController
    {
        private void TrackShipPhaseReset()
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null) return;

            bool inShipPhase = sor.inShipPhase;
            if (inShipPhase && !_lastInShipPhase && !_batteryBroken && IsServer())
            {
                _power = GetPowerCapacity();
                BroadcastPowerState();
            }
            _lastInShipPhase = inShipPhase;
            // Once the failure sequence has finished, refresh phase-dependent
            // controls without resetting durability or shortening the sequence.
            if (_appliedEmergencyProtocolActive == true && !_shipPowerOnline)
                ApplyEmergencyProtocol(active: true);
        }

        private void ApplyEmergencyProtocol(bool active)
        {
            StartOfRound sor = StartOfRound.Instance;
            if (sor == null) return;

            // Guard on the applied value: the TV lookup below is a
            // FindObjectOfType and the lever/door writes are pure state, so an
            // unchanged request has nothing to do. The visual call keeps its own
            // guard, so an invalidated visual cache still re-applies through it.
            bool lockout = active && IsLandedOnMoon();
            if (_appliedEmergencyProtocolActive == active && _appliedEmergencyInteractionLockout == lockout)
            {
                ApplyShipPowerVisualState(powerOnline: !active);
                return;
            }

            _appliedEmergencyProtocolActive = active;
            _appliedEmergencyInteractionLockout = lockout;
            ApplyShipPowerVisualState(powerOnline: !active);

            if (active && IsServer())
            {
                try
                {
                    TVScript tv = FindObjectOfType<TVScript>();
                    if (tv != null && tv.tvOn)
                        tv.TurnOffTVServerRpc();
                }
                catch (Exception ex)
                {
                    LogVerbose($"Ship TV power-down skipped: {ex.Message}");
                }
            }

            try
            {
                StartMatchLever lever = FindObjectOfType<StartMatchLever>();
                if (lever != null && lever.triggerScript != null)
                {
                    lever.triggerScript.interactable = !lockout;
                    if (active)
                        lever.triggerScript.disabledHoverTip = "Emergency protocol active";
                }

                HangarShipDoor hangarDoor = FindObjectOfType<HangarShipDoor>();
                if (hangarDoor != null)
                {
                    hangarDoor.SetDoorButtonsEnabled(doorButtonsEnabled: !active);
                }
            }
            catch (Exception ex)
            {
                LogVerbose($"Emergency protocol sync failed: {ex.Message}");
            }
        }
    }
}
