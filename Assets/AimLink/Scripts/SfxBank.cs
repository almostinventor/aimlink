using UnityEngine;

namespace AimLink
{
    /// <summary>All sound hooks. Any clip may be left empty; playback silently skips nulls.</summary>
    [CreateAssetMenu(menuName = "AimLink/Sfx Bank", fileName = "SfxBank")]
    public sealed class SfxBank : ScriptableObject
    {
        [Header("Boot")]
        public AudioClip bootPowerup;
        public AudioClip bootHumLoop;
        public AudioClip postTick;
        public AudioClip linkConfirm;
        public AudioClip visorOpen;
        public AudioClip scanSweep;
        public AudioClip labelPop;
        public AudioClip hudElementIn;
        public AudioClip reticleLock;

        [Header("Input")]
        public AudioClip trimTick;
        public AudioClip trimReset;
        public AudioClip yPress;
        public AudioClip beaconPlace;
        public AudioClip toggle;

        [Header("Voice (optional)")]
        public AudioClip voiceWeaponLinked;
        public AudioClip voiceEnvironmentMapped;
        public AudioClip voiceSmartLinkActive;
    }
}
