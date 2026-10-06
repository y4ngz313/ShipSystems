using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Fuel
{
    internal static class FuelItemSounds
    {
        private static Item _donor;
        internal static AudioSource EnsureSource(GameObject root)
        {
            AudioSource audio = root.GetComponent<AudioSource>();
            if (audio == null) audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            audio.minDistance = 1f;
            audio.maxDistance = 12f;
            audio.rolloffMode = AudioRolloffMode.Logarithmic;
            AudioSource donorAudio = _donor != null && _donor.spawnPrefab != null ? _donor.spawnPrefab.GetComponent<AudioSource>() : null;
            if (donorAudio != null) audio.outputAudioMixerGroup = donorAudio.outputAudioMixerGroup;
            return audio;
        }

        internal static void TryAssign(Item item)
        {
            if (item == null || item.spawnPrefab == null) return;
            // Big bolt supplies the game's dry metal handling sounds. Assign Item fields:
            // vanilla handles pickup replication, floor impact, noise and walkie relay.
            if (_donor == null)
                foreach (Item candidate in Resources.FindObjectsOfTypeAll<Item>())
                    if (candidate != null && candidate.name == "BigBolt" && candidate.grabSFX != null && candidate.dropSFX != null)
                    { _donor = candidate; break; }
            AudioSource audio = EnsureSource(item.spawnPrefab);
            if (_donor == null) return; // Retried at round registration and pickup Start.
            bool changed = item.grabSFX != _donor.grabSFX || item.dropSFX != _donor.dropSFX;
            item.grabSFX = _donor.grabSFX;
            item.dropSFX = _donor.dropSFX;
            AudioSource original = _donor.spawnPrefab != null ? _donor.spawnPrefab.GetComponent<AudioSource>() : null;
            if (original != null) audio.outputAudioMixerGroup = original.outputAudioMixerGroup;
            if (changed) ShipSystemsBootstrap.Log?.LogInfo($"[LGUShipSystems.Fuel] {item.itemName} handling audio: pickup={item.grabSFX.name}, drop={item.dropSFX.name}.");
        }
    }
}
