using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AimLink
{
    /// <summary>
    /// Drives the BOOT state: POST text, weapon link, visor opening on the sound's peak, environment scan,
    /// HUD fly-in and reticle lock. All timings are tunable for the edit.
    /// </summary>
    public sealed class BootSequence : MonoBehaviour
    {
        [Serializable]
        public sealed class Timings
        {
            [Tooltip("Seconds of black with only the blinking cursor before POST text starts.")]
            public float postStart = 0.5f;
            [Tooltip("Typewriter speed for POST lines, characters per second.")]
            public float charsPerSecond = 70f;
            [Tooltip("Pause after each POST line, seconds.")]
            public float linePause = 0.18f;
            [Tooltip("Longest wait on WEAPON LINK ... SEARCHING, seconds.")]
            public float linkTimeout = 3f;
            [Tooltip("Controller movement that counts as linked, metres.")]
            public float linkMoveDistance = 0.02f;
            [Tooltip("Controller rotation that counts as linked, degrees.")]
            public float linkRotateDegrees = 5f;
            [Tooltip("Boot time at which the visor lights up. Set to the peak of the boot sound (4.0 for the default boot sound).")]
            public float visorOpenAt = 4f;
            [Tooltip("Seconds the visor takes to open.")]
            public float visorOpenDuration = 1.2f;
            [Tooltip("Pause between the visor opening and the scan wave, seconds.")]
            public float scanDelay = 0.25f;
            public float hudFlyDuration = 0.3f;
            public float reticleSpinDuration = 0.6f;
            public float postFadeDuration = 0.6f;
            [Tooltip("Boot time at which the idle hum loop fades in (0 = never).")]
            public float humStart = 4f;
            [Range(0f, 1f)] public float humVolume = 0.35f;
        }

        [SerializeField] Timings timings = new Timings();

        [Header("References")]
        [SerializeField] AppStateMachine stateMachine;
        [SerializeField] PassthroughController passthrough;
        [SerializeField] RoomScanController room;
        [SerializeField] HudController hud;
        [SerializeField] ReticleController reticle;
        [SerializeField] MuzzleTrim muzzle;
        [SerializeField] InputRouter input;
        [SerializeField] SfxPlayer sfx;

        readonly List<string> _lines = new List<string>();
        readonly StringBuilder _sb = new StringBuilder(512);
        Coroutine _routine;
        float _bootStart;

        float BootTime => Time.time - _bootStart;
        SfxBank Bank => sfx != null ? sfx.Bank : null;

        void OnEnable()
        {
            if (stateMachine != null) stateMachine.StateChanged += OnStateChanged;
        }

        void OnDisable()
        {
            if (stateMachine != null) stateMachine.StateChanged -= OnStateChanged;
        }

        void OnStateChanged(AppState prev, AppState next)
        {
            if (_routine != null)
            {
                // boot was interrupted (hold-Y to LIVE): everything else resets itself from the state change
                StopAllCoroutines();
                _routine = null;
                if (sfx != null) sfx.StopAll();
            }
            if (next == AppState.Boot) _routine = StartCoroutine(Run());
            else if (next == AppState.Blackout && sfx != null) sfx.StopAll();
        }

        IEnumerator Run()
        {
            // other subscribers may not have handled the state change yet
            yield return null;

            Timings t = timings;
            _bootStart = Time.time;
            _lines.Clear();
            Vector3 startPos = muzzle != null ? muzzle.transform.position : Vector3.zero;
            Quaternion startRot = muzzle != null ? muzzle.transform.rotation : Quaternion.identity;

            // 1. black, blinking cursor
            Play(Bank != null ? Bank.bootPowerup : null);
            RefreshConsole();
            yield return WaitUntilBootTime(t.postStart);

            // 2. POST
            yield return TypeLine("AIMLINK OS v1.0");
            yield return TypeLine("VISOR DISPLAY ........ OK");
            yield return TypeLine("INERTIAL REFERENCE .... OK");
            yield return TypeLine("WEAPON LINK ........... SEARCHING");

            // 3. wait for the pipe to move, but never past the visor cue
            int linkLine = _lines.Count - 1;
            float deadline = Mathf.Min(Time.time + t.linkTimeout, _bootStart + t.visorOpenAt - 0.6f);
            while (Time.time < deadline && muzzle != null)
            {
                bool moved = Vector3.Distance(muzzle.transform.position, startPos) > t.linkMoveDistance
                             || Quaternion.Angle(muzzle.transform.rotation, startRot) > t.linkRotateDegrees;
                if (moved && (input == null || input.WeaponLinked)) break;
                yield return null;
            }
            _lines[linkLine] = "WEAPON LINK ........... LINKED";
            RefreshConsole();
            Play(Bank != null ? Bank.linkConfirm : null);
            Play(Bank != null ? Bank.voiceWeaponLinked : null);
            if (input != null) input.PulseBoth(0.8f, 0.12f);
            yield return new WaitForSeconds(t.linePause);

            // 4. boresight with the real saved trim
            yield return TypeLine("BORESIGHT ............. LOADED (TRIM " + (muzzle != null ? muzzle.FormatTrim() : "--") + ")");

            // 5. visor opens on the sound's peak
            yield return WaitUntilBootTime(t.visorOpenAt);
            if (t.humStart > 0f && Bank != null) sfx.StartLoop(Bank.bootHumLoop, t.humVolume, 1.5f);
            Play(Bank != null ? Bank.visorOpen : null);
            if (passthrough != null)
            {
                passthrough.BeginVisorOpen();
                for (float e = 0f; e < t.visorOpenDuration; e += Time.deltaTime)
                {
                    float k = e / t.visorOpenDuration;
                    // the view arrives in blocks; linear progress keeps the pieces coming at an even rate
                    float open = k;
                    // three short tears while it opens
                    float glitch = (k > 0.18f && k < 0.26f) || (k > 0.42f && k < 0.47f) || (k > 0.66f && k < 0.74f) ? 0.8f : 0f;
                    passthrough.SetVisor(open, glitch);
                    yield return null;
                }
                passthrough.SetLive();
            }

            // 6. environment scan
            yield return new WaitForSeconds(t.scanDelay);
            yield return TypeLine("ENVIRONMENT SCAN ...");
            int scanLine = _lines.Count - 1;
            if (room != null && room.RoomState == RoomDataState.Ready)
            {
                Play(Bank != null ? Bank.scanSweep : null);
                room.RunScan();
                float scanEnd = Time.time + room.ScanDuration;
                while (Time.time < scanEnd)
                {
                    _lines[scanLine] = "ENVIRONMENT SCAN ...   SURFACES MAPPED: " + room.SurfacesMapped;
                    RefreshConsole();
                    yield return null;
                }
                _lines[scanLine] = "ENVIRONMENT ........... MAPPED";
                Play(Bank != null ? Bank.voiceEnvironmentMapped : null);
            }
            else
            {
                _lines[scanLine] = "ENVIRONMENT ........... NO DATA";
            }
            RefreshConsole();
            yield return new WaitForSeconds(t.linePause);

            // 7. HUD comes up (it is deliberately sparse: just the compass)
            if (hud != null)
            {
                Play(Bank != null ? Bank.hudElementIn : null);
                yield return hud.FlyIn(HudElement.Compass, t.hudFlyDuration);
            }

            // 8. reticle last
            if (reticle != null) yield return reticle.SpinIn(t.reticleSpinDuration);
            Play(Bank != null ? Bank.reticleLock : null);
            Play(Bank != null ? Bank.voiceSmartLinkActive : null);
            if (input != null) input.PulseBoth(1f, 0.08f);

            // 9. POST text fades
            if (hud != null)
            {
                for (float e = 0f; e < t.postFadeDuration; e += Time.deltaTime)
                {
                    hud.SetConsoleAlpha(1f - e / t.postFadeDuration);
                    yield return null;
                }
                hud.SetConsole("", false);
                hud.SetConsoleAlpha(1f);
            }
            if (sfx != null) sfx.StopLoop(1f);

            _routine = null;
            if (stateMachine != null) stateMachine.NotifyBootFinished();
        }

        IEnumerator WaitUntilBootTime(float seconds)
        {
            while (BootTime < seconds) yield return null;
        }

        IEnumerator TypeLine(string line)
        {
            _lines.Add("");
            int index = _lines.Count - 1;
            float perChar = 1f / Mathf.Max(timings.charsPerSecond, 1f);
            float shown = 0f;
            int last = 0;
            while (last < line.Length)
            {
                shown += Time.deltaTime / perChar;
                int count = Mathf.Min(line.Length, Mathf.FloorToInt(shown) + 1);
                if (count != last)
                {
                    // tick every few characters, not every one
                    if (count / 4 != last / 4) Play(Bank != null ? Bank.postTick : null, 0.5f);
                    last = count;
                    _lines[index] = line.Substring(0, count);
                    RefreshConsole();
                }
                yield return null;
            }
            yield return new WaitForSeconds(timings.linePause);
        }

        void RefreshConsole()
        {
            if (hud == null) return;
            _sb.Length = 0;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (i > 0) _sb.Append('\n');
                _sb.Append(_lines[i]);
            }
            hud.SetConsole(_sb.ToString(), true);
        }

        void Play(AudioClip clip, float volume = 1f)
        {
            if (sfx != null) sfx.Play(clip, volume);
        }
    }
}
