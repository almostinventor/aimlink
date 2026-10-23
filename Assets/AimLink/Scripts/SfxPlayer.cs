using System.Collections;
using UnityEngine;

namespace AimLink
{
    /// <summary>Plays SfxBank clips. 2D one-shots, one looping bed, and spatialized one-shots at a world position.</summary>
    public sealed class SfxPlayer : MonoBehaviour
    {
        [SerializeField] SfxBank bank;
        [Range(0f, 1f)] [SerializeField] float masterVolume = 1f;

        AudioSource _oneShot;
        AudioSource _loop;
        Coroutine _loopFade;

        public SfxBank Bank => bank;

        void Awake()
        {
            _oneShot = gameObject.AddComponent<AudioSource>();
            _oneShot.playOnAwake = false;
            _oneShot.spatialBlend = 0f;

            _loop = gameObject.AddComponent<AudioSource>();
            _loop.playOnAwake = false;
            _loop.spatialBlend = 0f;
            _loop.loop = true;
            _loop.volume = 0f;
        }

        public void Play(AudioClip clip, float volume = 1f)
        {
            if (clip == null) return;
            _oneShot.PlayOneShot(clip, volume * masterVolume);
        }

        public void PlayAt(AudioClip clip, Vector3 position, float volume = 1f)
        {
            if (clip == null) return;
            AudioSource.PlayClipAtPoint(clip, position, volume * masterVolume);
        }

        public void StartLoop(AudioClip clip, float volume, float fadeSeconds)
        {
            if (clip == null) return;
            if (_loop.clip != clip || !_loop.isPlaying)
            {
                _loop.clip = clip;
                _loop.volume = 0f;
                _loop.Play();
            }
            FadeLoop(volume * masterVolume, fadeSeconds, false);
        }

        public void StopLoop(float fadeSeconds) { FadeLoop(0f, fadeSeconds, true); }

        /// <summary>Hard stop of everything (BLACKOUT).</summary>
        public void StopAll()
        {
            if (_loopFade != null) StopCoroutine(_loopFade);
            _loopFade = null;
            _loop.Stop();
            _loop.volume = 0f;
            _oneShot.Stop();
        }

        void FadeLoop(float target, float seconds, bool stopAtEnd)
        {
            if (_loopFade != null) StopCoroutine(_loopFade);
            _loopFade = StartCoroutine(FadeLoopRoutine(target, seconds, stopAtEnd));
        }

        IEnumerator FadeLoopRoutine(float target, float seconds, bool stopAtEnd)
        {
            float from = _loop.volume;
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                _loop.volume = Mathf.Lerp(from, target, t / seconds);
                yield return null;
            }
            _loop.volume = target;
            if (stopAtEnd) _loop.Stop();
            _loopFade = null;
        }
    }
}
