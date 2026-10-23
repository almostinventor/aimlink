using System;
using UnityEngine;

namespace AimLink
{
    /// <summary>
    /// Derives the muzzle pose from the two controllers taped to the pipe: the aim direction is the
    /// straight line from the handle controller to the barrel controller. A static mount offset and
    /// the yaw/pitch trim are applied on top. The aim ray is transform.position + transform.forward.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public sealed class MuzzleTrim : MonoBehaviour
    {
        const string YawKey = "AimLink.TrimYaw";
        const string PitchKey = "AimLink.TrimPitch";

        [Header("Controllers on the pipe")]
        [Tooltip("Controller anchor at the handle (rear) end.")]
        [SerializeField] Transform handleController;
        [Tooltip("Controller anchor at the barrel (front) end.")]
        [SerializeField] Transform barrelController;
        [Tooltip("Tick if the controllers are mounted the other way round.")]
        [SerializeField] bool swapControllers = false;

        [Header("Mount offset (controller line -> pipe axis)")]
        [Tooltip("Offset from the front controller to the pipe axis / front end, in the pipe's frame (x right, y up, z forward), metres.")]
        [SerializeField] Vector3 mountLocalPosition = Vector3.zero;
        [Tooltip("Static rotation from the controller line to the pipe axis, degrees.")]
        [SerializeField] Vector3 mountLocalEuler = Vector3.zero;

        [Header("Trim")]
        [Tooltip("Degrees per stick flick.")]
        [SerializeField] float stepDegrees = 0.2f;
        [SerializeField] InputRouter input;
        [SerializeField] SfxPlayer sfx;

        public float YawDegrees { get; private set; }
        public float PitchDegrees { get; private set; }
        public Ray AimRay => new Ray(transform.position, transform.forward);

        /// <summary>Distance between the two controllers, metres. The longer, the steadier the aim line.</summary>
        public float Baseline { get; private set; }

        /// <summary>Angular speed of the muzzle in degrees per second (for the reticle's "moving fast" state).</summary>
        public float AngularSpeed { get; private set; }

        public event Action TrimChanged;

        Quaternion _prevRotation;

        void Awake()
        {
            YawDegrees = PlayerPrefs.GetFloat(YawKey, 0f);
            PitchDegrees = PlayerPrefs.GetFloat(PitchKey, 0f);
            UpdatePose();
            _prevRotation = transform.rotation;
        }

        void OnEnable()
        {
            if (input == null) return;
            input.TrimStep += OnTrimStep;
            input.TrimReset += OnTrimReset;
        }

        void OnDisable()
        {
            if (input == null) return;
            input.TrimStep -= OnTrimStep;
            input.TrimReset -= OnTrimReset;
        }

        // Runs before AimRaycaster (execution order), after OVRCameraRig has moved the anchors in Update.
        void LateUpdate()
        {
            UpdatePose();
            if (Time.deltaTime > 0f)
            {
                float speed = Quaternion.Angle(_prevRotation, transform.rotation) / Time.deltaTime;
                AngularSpeed = Mathf.Lerp(AngularSpeed, speed, 0.3f);
            }
            _prevRotation = transform.rotation;
        }

        void UpdatePose()
        {
            Transform handle = swapControllers ? barrelController : handleController;
            Transform barrel = swapControllers ? handleController : barrelController;
            if (handle == null || barrel == null) return;

            Vector3 line = barrel.position - handle.position;
            Baseline = line.magnitude;
            // controllers on top of each other (or untracked at the origin): keep the last good pose
            if (Baseline < 0.05f) return;
            Vector3 forward = line / Baseline;

            // Roll reference from the handle controller, so the mount offset and trim axes stay fixed
            // to the pipe when it is canted. Fall back if that axis happens to lie along the line.
            Vector3 up = handle.up;
            if (Mathf.Abs(Vector3.Dot(up, forward)) > 0.95f) up = handle.forward;
            Quaternion mount = Quaternion.LookRotation(forward, up);

            // stick up = aim up: positive pitch rotates the muzzle upwards (negative X rotation in Unity)
            Quaternion rotation = mount * Quaternion.Euler(mountLocalEuler) * Quaternion.Euler(-PitchDegrees, YawDegrees, 0f);
            transform.SetPositionAndRotation(barrel.position + mount * mountLocalPosition, rotation);
        }

        void OnTrimStep(Vector2Int dir)
        {
            YawDegrees += dir.x * stepDegrees;
            PitchDegrees += dir.y * stepDegrees;
            Commit();
            if (sfx != null) sfx.Play(sfx.Bank != null ? sfx.Bank.trimTick : null);
        }

        void OnTrimReset()
        {
            YawDegrees = 0f;
            PitchDegrees = 0f;
            Commit();
            if (sfx != null) sfx.Play(sfx.Bank != null ? sfx.Bank.trimReset : null);
        }

        void Commit()
        {
            UpdatePose();
            PlayerPrefs.SetFloat(YawKey, YawDegrees);
            PlayerPrefs.SetFloat(PitchKey, PitchDegrees);
            PlayerPrefs.Save();
            TrimChanged?.Invoke();
        }

        public string FormatTrim()
        {
            return string.Format("Y {0:+0.00;-0.00}°  P {1:+0.00;-0.00}°", YawDegrees, PitchDegrees);
        }
    }
}
