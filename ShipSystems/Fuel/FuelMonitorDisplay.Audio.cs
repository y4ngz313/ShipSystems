using UnityEngine;
using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal sealed partial class FuelMonitorDisplay
    {
        private bool _holdLandingReadout;
        private float _landingSoundUntil = -1f;
        private AudioSource _countdownSource;
        internal static void BeginLanding()
        {
            EnsureInstance();
            _instance._holdLandingReadout = true;
            _instance._landingSoundUntil = -1f;
        }
        internal static void LandingDoorsOpened()
        {
            if (_instance == null || !_instance._holdLandingReadout) return;
            _instance._holdLandingReadout = false;
            // Allow for a remote peer's authoritative fuel snapshot arriving shortly after its doors open.
            _instance._landingSoundUntil = Time.unscaledTime + 5f;
        }
        private Vector3 SoundPosition => _boundRenderer != null ? _boundRenderer.bounds.center : _vanillaPanel.SoundPosition;
        private void PlayCountdownSound(AudioClip clip)
        {
            float volume = ShipSystemsBootstrap.FuelMonitorSoundVolume?.Value ?? .35f;
            if (clip == null || volume <= 0f || (_boundRenderer == null && !_vanillaPanel.IsBuilt)) return;
            if (_countdownSource == null)
            {
                var go = new GameObject("ShipFuelMonitorAudio");
                _countdownSource = go.AddComponent<AudioSource>();
                _countdownSource.playOnAwake = false;
                _countdownSource.spatialBlend = 1f;
                _countdownSource.rolloffMode = AudioRolloffMode.Linear;
                _countdownSource.minDistance = 1f;
                _countdownSource.maxDistance = 8f;
            }
            _countdownSource.transform.position = SoundPosition;
            _countdownSource.PlayOneShot(clip, volume);
        }
    }
}
