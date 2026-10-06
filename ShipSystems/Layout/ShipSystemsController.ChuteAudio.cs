using UnityEngine;
namespace Y4NGZCompany.ShipSystems.Layout
{
    internal sealed partial class ShipSystemsController
    {
        private AudioClip _fuelMonitorCountdownClip;
        private bool _fuelMonitorAudioRequested;
        internal AudioClip FuelMonitorCountdownClip => _fuelMonitorCountdownClip;
        internal void PrewarmFuelMonitorAudio()
        {
            if (_fuelMonitorAudioRequested) return;
            _fuelMonitorAudioRequested = true;
            StartCoroutine(LoadBatteryAudioClipFromDisk("FuelMonitorCountdown.wav", clip => _fuelMonitorCountdownClip = clip));
        }
        private AudioClip _fuelChuteRejectClip;
        private bool _fuelChuteAudioRequested;
        internal AudioClip FuelChuteRejectClip => _fuelChuteRejectClip;
        internal void PrewarmFuelChuteAudio()
        {
            if (_fuelChuteAudioRequested) return;
            _fuelChuteAudioRequested = true;
            StartCoroutine(LoadBatteryAudioClipFromDisk("FuelChuteReject.mp3", clip => _fuelChuteRejectClip = clip));
        }
    }
}
