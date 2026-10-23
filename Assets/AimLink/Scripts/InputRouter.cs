using System;
using UnityEngine;

namespace AimLink
{
    /// <summary>
    /// All controller input and haptics (Meta OVRInput lives here and nowhere else).
    /// Left controller = remote: Y, thumbstick flicks, stick click, X. Right controller = tracking reference.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class InputRouter : MonoBehaviour
    {
        [Header("Y button")]
        [Tooltip("Seconds Y must be held to raise YHeld.")]
        [SerializeField] float yHoldSeconds = 1.5f;

        [Header("Trim stick")]
        [Tooltip("Stick deflection that counts as a flick.")]
        [Range(0.1f, 0.95f)] [SerializeField] float flickDeadzone = 0.6f;
        [Tooltip("Seconds a flick must be held before it auto-repeats.")]
        [SerializeField] float repeatDelay = 0.25f;
        [Tooltip("Auto-repeat rate while held, steps per second.")]
        [SerializeField] float repeatHz = 20f;
        [Tooltip("Seconds the stick click must be held to reset trim.")]
        [SerializeField] float resetHoldSeconds = 1f;

        [Header("Feedback")]
        [SerializeField] SfxPlayer sfx;

        public event Action YPressed;
        public event Action YHeld;
        public event Action XPressed;
        /// <summary>A on the right (handle) controller: toggle the scope inset.</summary>
        public event Action RightAPressed;
        /// <summary>Left index trigger: place a beacon.</summary>
        public event Action LeftTriggerPressed;
        /// <summary>Left grip: clear all beacons.</summary>
        public event Action LeftGripPressed;
        /// <summary>One trim step. x = yaw (-1/+1), y = pitch (-1/+1).</summary>
        public event Action<Vector2Int> TrimStep;
        public event Action TrimReset;

        /// <summary>Set by AppStateMachine: trim input only does anything in LIVE.</summary>
        public bool TrimEnabled { get; set; } = true;

        public bool RightPositionTracked { get; private set; } = true;
        public bool RightOrientationTracked { get; private set; } = true;
        public bool LeftPositionTracked { get; private set; } = true;
        /// <summary>True when both controllers are position-tracked: the aim line runs between them.</summary>
        public bool WeaponLinked => RightPositionTracked && LeftPositionTracked;

        const OVRInput.Controller Left = OVRInput.Controller.LTouch;
        const OVRInput.Controller Right = OVRInput.Controller.RTouch;

        struct AxisState { public int Dir; public float NextRepeat; }
        AxisState _axisX, _axisY;

        float _yDownTime = -1f;
        bool _yHeldFired;
        float _clickDownTime = -1f;
        bool _resetFired;
        float _hapticEndLeft, _hapticEndRight;

        void Update()
        {
            RightPositionTracked = OVRInput.GetControllerPositionTracked(Right);
            RightOrientationTracked = OVRInput.GetControllerOrientationTracked(Right);
            LeftPositionTracked = OVRInput.GetControllerPositionTracked(Left);

            UpdateY();
            if (OVRInput.GetDown(OVRInput.RawButton.X)) XPressed?.Invoke();
            if (OVRInput.GetDown(OVRInput.RawButton.A)) RightAPressed?.Invoke();
            if (OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger)) LeftTriggerPressed?.Invoke();
            if (OVRInput.GetDown(OVRInput.RawButton.LHandTrigger)) LeftGripPressed?.Invoke();
            if (TrimEnabled)
            {
                UpdateTrimStick();
                UpdateTrimReset();
            }
            UpdateHaptics();
        }

        void UpdateY()
        {
            if (OVRInput.GetDown(OVRInput.RawButton.Y))
            {
                _yDownTime = Time.unscaledTime;
                _yHeldFired = false;
                // feedback first: the user may be blind (BLACKOUT) when pressing this
                Pulse(true, 0.6f, 0.06f);
                if (sfx != null && sfx.Bank != null) sfx.Play(sfx.Bank.yPress);
                YPressed?.Invoke();
            }
            if (OVRInput.Get(OVRInput.RawButton.Y))
            {
                if (!_yHeldFired && _yDownTime >= 0f && Time.unscaledTime - _yDownTime >= yHoldSeconds)
                {
                    _yHeldFired = true;
                    Pulse(true, 1f, 0.15f);
                    YHeld?.Invoke();
                }
            }
            else _yDownTime = -1f;
        }

        void UpdateTrimStick()
        {
            Vector2 stick = OVRInput.Get(OVRInput.RawAxis2D.LThumbstick);
            int stepX = StepAxis(ref _axisX, stick.x);
            int stepY = StepAxis(ref _axisY, stick.y);
            if (stepX == 0 && stepY == 0) return;
            Pulse(true, 0.25f, 0.015f);
            TrimStep?.Invoke(new Vector2Int(stepX, stepY));
        }

        // Edge-triggered flick with hold-to-repeat. Returns -1/0/+1 for this frame.
        int StepAxis(ref AxisState s, float value)
        {
            float release = flickDeadzone * 0.8f;
            int dir = s.Dir;
            if (dir == 0)
            {
                if (value > flickDeadzone) dir = 1;
                else if (value < -flickDeadzone) dir = -1;
                if (dir == 0) return 0;
                s.Dir = dir;
                s.NextRepeat = Time.unscaledTime + repeatDelay;
                return dir;
            }
            if (Mathf.Abs(value) < release || Mathf.Sign(value) != dir)
            {
                s.Dir = 0;
                return 0;
            }
            if (Time.unscaledTime >= s.NextRepeat)
            {
                s.NextRepeat = Time.unscaledTime + 1f / Mathf.Max(repeatHz, 1f);
                return dir;
            }
            return 0;
        }

        void UpdateTrimReset()
        {
            if (OVRInput.Get(OVRInput.RawButton.LThumbstick))
            {
                if (_clickDownTime < 0f)
                {
                    _clickDownTime = Time.unscaledTime;
                    _resetFired = false;
                }
                else if (!_resetFired && Time.unscaledTime - _clickDownTime >= resetHoldSeconds)
                {
                    _resetFired = true;
                    Pulse(true, 1f, 0.3f);
                    TrimReset?.Invoke();
                }
            }
            else _clickDownTime = -1f;
        }

        /// <summary>Short haptic pulse. Amplitude 0..1.</summary>
        public void Pulse(bool left, float amplitude, float seconds)
        {
            OVRInput.SetControllerVibration(1f, Mathf.Clamp01(amplitude), left ? Left : Right);
            if (left) _hapticEndLeft = Time.unscaledTime + seconds;
            else _hapticEndRight = Time.unscaledTime + seconds;
        }

        public void PulseBoth(float amplitude, float seconds)
        {
            Pulse(true, amplitude, seconds);
            Pulse(false, amplitude, seconds);
        }

        void UpdateHaptics()
        {
            if (_hapticEndLeft > 0f && Time.unscaledTime >= _hapticEndLeft)
            {
                _hapticEndLeft = 0f;
                OVRInput.SetControllerVibration(0f, 0f, Left);
            }
            if (_hapticEndRight > 0f && Time.unscaledTime >= _hapticEndRight)
            {
                _hapticEndRight = 0f;
                OVRInput.SetControllerVibration(0f, 0f, Right);
            }
        }

        void OnDisable()
        {
            OVRInput.SetControllerVibration(0f, 0f, Left);
            OVRInput.SetControllerVibration(0f, 0f, Right);
        }
    }
}
