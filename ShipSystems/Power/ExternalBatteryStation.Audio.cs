using UnityEngine;
namespace Y4NGZCompany.ShipSystems.Power
{
    internal sealed partial class ExternalBatteryStation
    {
        private const float WorkingLoopBaseVolume = 0.08f;
        private const float BrokenLoopVolume = 0.08f;

        // Quiet spatial loops: hum on the working station, crackle on the broken
        // drop. Hit feedback ducks the working loop via DuckWorkingLoop.
        private void TickBatteryLoops()
        {
            if (_root == null)
                return;

            if (!_batteryAudioRequested)
            {
                _batteryAudioRequested = true;
                _controller.RequestBatteryAudioClips();
            }

            bool working = !_controller.BatteryBroken && _controller.ShipPowerOnline;
            TickWorkingArcs(working);

            StartOfRound ship = StartOfRound.Instance;
            bool landed = ship != null && ship.shipHasLanded && !ship.inShipPhase;
            if (!landed)
            {
                if (_workingLoopSource != null) _workingLoopSource.Stop();
                if (_brokenLoopSource != null) _brokenLoopSource.Stop();
                return;
            }
            AudioClip workingClip = _controller.BatteryWorkingLoopClip;
            if (workingClip != null)
            {
                if (_workingLoopSource == null)
                    _workingLoopSource = CreateLoopSource(_root.transform, "BatteryWorkingLoopAudio", workingClip, WorkingLoopBaseVolume);

                if (working && !_workingLoopSource.isPlaying)
                    _workingLoopSource.Play();
                else if (!working && _workingLoopSource.isPlaying)
                    _workingLoopSource.Stop();

                if (_workingLoopSource.isPlaying)
                {
                    bool ducked = Time.unscaledTime < _workingLoopDuckUntil;
                    float target = ducked ? WorkingLoopBaseVolume * 0.3f : WorkingLoopBaseVolume;
                    _workingLoopSource.volume = ducked
                        ? target
                        : Mathf.MoveTowards(_workingLoopSource.volume, target, Time.unscaledDeltaTime * 0.5f);
                }
            }

            AudioClip brokenClip = _controller.BatteryBrokenLoopClip;
            if (_brokenBatteryDrop != null && brokenClip != null && _brokenLoopSource == null)
            {
                _brokenLoopSource = CreateLoopSource(_brokenBatteryDrop.transform, "BrokenBatteryLoopAudio", brokenClip, BrokenLoopVolume);
            }
            if (_brokenLoopSource != null)
            {
                bool broken = _controller.BatteryBroken && _brokenBatteryDrop != null;
                if (broken && !_brokenLoopSource.isPlaying) _brokenLoopSource.Play();
                else if (!broken && _brokenLoopSource.isPlaying) _brokenLoopSource.Stop();
            }
        }

        internal void DuckWorkingLoop(float seconds)
        {
            _workingLoopDuckUntil = Time.unscaledTime + Mathf.Max(0.2f, seconds);
        }

        private static AudioSource CreateLoopSource(Transform parent, string name, AudioClip clip, float volume)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            AudioSource source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 1.2f;
            source.maxDistance = 10f;
            source.volume = volume;
            return source;
        }

    }
}
