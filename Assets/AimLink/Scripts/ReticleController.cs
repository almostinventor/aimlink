using System.Collections;
using TMPro;
using UnityEngine;

namespace AimLink
{
    /// <summary>
    /// World-space reticle at the aim hit: billboarded, with a distance and spread readout next to it,
    /// a HIP tag, and the local scan reveal position.
    ///
    /// Optional (showAimSpread, off by default): the ring radius becomes the measured aim spread, drawn at
    /// the target. Deliberate swings of the pipe count as spread too, so the ring gets very large while moving.
    /// </summary>
    public sealed class ReticleController : MonoBehaviour
    {
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        static readonly int SpinId = Shader.PropertyToID("_Spin");
        static readonly int RingRadiusId = Shader.PropertyToID("_RingRadius");
        static readonly int RingWidthId = Shader.PropertyToID("_RingWidth");
        static readonly int DotRadiusId = Shader.PropertyToID("_DotRadius");
        static readonly int HaloGrowId = Shader.PropertyToID("_HaloGrow");
        static readonly int RevealPosId = Shader.PropertyToID("_AimLinkRevealPos");

        const float UnitTextHeight = 0.1f;
        const float BaseHaloGrow = 0.014f;
        static readonly Vector3 FarAway = new Vector3(0f, -10000f, 0f);

        [Header("References")]
        [SerializeField] AimRaycaster aim;
        [SerializeField] MuzzleTrim muzzle;
        [SerializeField] InputRouter input;
        [SerializeField] Transform head;
        [SerializeField] AppStateMachine stateMachine;
        [Tooltip("Material using AimLink/Reticle. Instanced at runtime.")]
        [SerializeField] Material reticleMaterial;

        [Header("Size & placement")]
        [Tooltip("Reticle diameter as a visual angle, degrees, while the aim is steady. Same apparent size at any distance.")]
        [SerializeField] float angularSizeDegrees = 3f;
        [Tooltip("Offset from the surface along its normal, metres (avoids z-fighting).")]
        [SerializeField] float normalOffset = 0.01f;

        [Header("Smoothing (one-euro filter)")]
        [Tooltip("Lower = smoother when still, more lag.")]
        [SerializeField] float filterMinCutoff = 2.5f;
        [Tooltip("Higher = less lag when moving fast.")]
        [SerializeField] float filterBeta = 3f;
        [SerializeField] float filterDerivativeCutoff = 1f;
        [Tooltip("Jumps larger than this (metres) skip the filter, e.g. sweeping off a table edge onto the far wall.")]
        [SerializeField] float filterResetDistance = 0.6f;

        [Header("Aim spread (ring size)")]
        [Tooltip("Size the ring by the measured aim wobble and show it in cm. Off: fixed-size ring and distance-only readout.")]
        [SerializeField] bool showAimSpread = false;
        [Tooltip("Seconds over which the average aim direction is taken; wobble is measured against it.")]
        [SerializeField] float spreadMeanSeconds = 0.25f;
        [Tooltip("Seconds over which the wobble is averaged.")]
        [SerializeField] float spreadWindowSeconds = 0.5f;
        [Tooltip("Ring radius in standard deviations of the wobble. 2 covers about 95% of where the pipe pointed.")]
        [SerializeField] float spreadSigmas = 2f;
        [Tooltip("Smallest ring radius as a visual angle, degrees, so it never collapses onto the dot.")]
        [SerializeField] float minRingDegrees = 0.35f;

        [Header("States")]
        [SerializeField] Color surfaceColor = new Color(1f, 0.14f, 0.1f, 1f);
        [SerializeField] Color lostColor = new Color(0.6f, 0.6f, 0.6f, 1f);
        [Tooltip("Colour of the readout next to the reticle.")]
        [SerializeField] Color labelColor = new Color(1f, 0.86f, 0.84f, 1f);
        [Range(0f, 1f)] [SerializeField] float noSurfaceAlpha = 0.35f;
        [Tooltip("Muzzle this far below eye height (metres) shows the HIP tag.")]
        [SerializeField] float hipBelowEyes = 0.25f;

        [Header("Label")]
        [SerializeField] float labelAngularHeight = 0.75f;

        public bool IsHip { get; private set; }
        /// <summary>World position the reticle is drawn at (smoothed).</summary>
        public Vector3 Position { get; private set; }
        /// <summary>Radius of the current aim spread at the target, metres.</summary>
        public float SpreadRadius { get; private set; }

        Material _mat;
        Transform _billboard;
        MeshRenderer _renderer;
        TextMeshPro _label, _hipTag;
        OneEuroFilter3 _filter;
        bool _visible;
        bool _hasPrev;
        Vector3 _prevTarget;
        float _baseRingWidth = 0.024f, _baseDotRadius = 0.02f, _baseRingRadius = 0.25f;
        Vector3 _meanDir;
        bool _hasMean;
        float _variance;
        // intro animation (BootSequence.SpinIn)
        float _introScale = 1f, _introSpin, _introAlpha = 1f;

        void Awake()
        {
            _filter = new OneEuroFilter3(filterMinCutoff, filterBeta, filterDerivativeCutoff);
            if (reticleMaterial != null)
            {
                _mat = new Material(reticleMaterial);
                _baseRingWidth = reticleMaterial.GetFloat(RingWidthId);
                _baseDotRadius = reticleMaterial.GetFloat(DotRadiusId);
                _baseRingRadius = reticleMaterial.GetFloat(RingRadiusId);
            }
            _billboard = CreateQuad("ReticleBillboard", BuildQuad(), _mat, out _renderer);
            _label = CreateText("ReticleLabel", TextAlignmentOptions.Top);
            _hipTag = CreateText("ReticleHipTag", TextAlignmentOptions.Left);
            _hipTag.text = "HIP";
            SetVisible(false);
        }

        void OnEnable()
        {
            if (stateMachine != null) stateMachine.StateChanged += OnStateChanged;
        }

        void OnDisable()
        {
            if (stateMachine != null) stateMachine.StateChanged -= OnStateChanged;
            Shader.SetGlobalVector(RevealPosId, FarAway);
        }

        void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
        }

        void OnStateChanged(AppState prev, AppState next)
        {
            StopAllCoroutines();
            _introScale = 1f;
            _introSpin = 0f;
            _introAlpha = 1f;
            SetVisible(next == AppState.Live);
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (!visible)
            {
                _renderer.enabled = false;
                _label.enabled = false;
                _hipTag.enabled = false;
                _hasPrev = false;
                _hasMean = false;
                Shader.SetGlobalVector(RevealPosId, FarAway);
            }
        }

        /// <summary>Boot step 8: spins in from large to normal size at the current hit point.</summary>
        public IEnumerator SpinIn(float seconds, float startScale = 6f, float turns = 1.5f)
        {
            _introScale = startScale;
            _introSpin = turns * Mathf.PI * 2f;
            _introAlpha = 0f;
            SetVisible(true);
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                float k = t / seconds;
                float ease = 1f - Mathf.Pow(1f - k, 3f);
                _introScale = Mathf.Lerp(startScale, 1f, ease);
                _introSpin = (1f - ease) * turns * Mathf.PI * 2f;
                _introAlpha = Mathf.Clamp01(k * 3f);
                yield return null;
            }
            _introScale = 1f;
            _introSpin = 0f;
            _introAlpha = 1f;
        }

        void LateUpdate()
        {
            if (!_visible || aim == null || head == null) return;
            AimHit hit = aim.Current;
            float dt = Time.deltaTime;

            Vector3 target = hit.Point + hit.Normal * (hit.HasSurface ? normalOffset : 0f);
            if (!_hasPrev || Vector3.Distance(target, _prevTarget) > filterResetDistance) _filter.Reset();
            _filter.SetParams(filterMinCutoff, filterBeta, filterDerivativeCutoff);
            Vector3 pos = _filter.Filter(target, dt);
            _prevTarget = target;
            _hasPrev = true;
            Position = pos;

            Vector3 toReticle = pos - head.position;
            float dist = Mathf.Max(toReticle.magnitude, 0.05f);
            Quaternion face = Quaternion.LookRotation(toReticle / dist, head.up);

            // ---- aim spread: wobble of the pipe around its own running average ----
            UpdateSpread(dt);
            SpreadRadius = spreadSigmas * Mathf.Sqrt(_variance) * Mathf.Max(hit.Distance, 0.1f);
            float ringDeg = showAimSpread
                ? Mathf.Max(minRingDegrees, Mathf.Atan2(SpreadRadius, dist) * Mathf.Rad2Deg)
                : _baseRingRadius * angularSizeDegrees;
            // the quad grows when the ring outgrows it; strokes are rescaled so they keep their visual width
            float quadDeg = Mathf.Max(angularSizeDegrees, ringDeg / 0.32f);
            float strokeScale = angularSizeDegrees / quadDeg;
            float size = 2f * dist * Mathf.Tan(quadDeg * 0.5f * Mathf.Deg2Rad);
            float steadySize = 2f * dist * Mathf.Tan(angularSizeDegrees * 0.5f * Mathf.Deg2Rad);

            // ---- state ----
            bool linked = input == null || input.WeaponLinked;
            Color color = linked ? surfaceColor : lostColor;
            float alpha = hit.HasSurface ? 1f : noSurfaceAlpha;
            if (!linked) alpha *= Mathf.PerlinNoise(Time.time * 18f, 0f) > 0.45f ? 0.7f : 0.15f;
            alpha *= _introAlpha;

            // ---- shape ----
            _renderer.enabled = true;
            _billboard.SetPositionAndRotation(pos, face);
            _billboard.localScale = Vector3.one * (size * _introScale);
            if (_mat != null)
            {
                _mat.SetColor(ColorId, color);
                _mat.SetFloat(AlphaId, alpha);
                _mat.SetFloat(SpinId, _introSpin);
                _mat.SetFloat(RingRadiusId, ringDeg / quadDeg);
                _mat.SetFloat(RingWidthId, _baseRingWidth * strokeScale);
                _mat.SetFloat(DotRadiusId, _baseDotRadius * strokeScale);
                _mat.SetFloat(HaloGrowId, BaseHaloGrow * strokeScale);
            }

            // ---- readout: distance, and the spread at the target ----
            float textScale = dist * Mathf.Tan(labelAngularHeight * Mathf.Deg2Rad) / UnitTextHeight;
            Vector3 up = face * Vector3.up, right = face * Vector3.right;
            Color textColor = linked ? labelColor : lostColor;
            textColor.a = alpha;
            float ringWorld = dist * Mathf.Tan(ringDeg * Mathf.Deg2Rad);
            float textOffset = Mathf.Max(steadySize * 0.5f, ringWorld) + steadySize * 0.12f;

            bool intro = _introScale > 1.01f;
            _label.enabled = !intro;
            _label.transform.SetPositionAndRotation(pos - up * textOffset, face);
            _label.transform.localScale = Vector3.one * textScale;
            _label.color = textColor;
            _label.text = !hit.HasSurface ? "NO SURFACE"
                : showAimSpread ? FormatReadout(hit.Distance, SpreadRadius)
                : hit.Distance.ToString("0.00") + " m";

            IsHip = muzzle != null && muzzle.transform.position.y < head.position.y - hipBelowEyes;
            _hipTag.enabled = IsHip && !intro;
            _hipTag.transform.SetPositionAndRotation(pos + right * textOffset + up * (steadySize * 0.3f), face);
            _hipTag.transform.localScale = Vector3.one * textScale;
            _hipTag.color = textColor;

            Shader.SetGlobalVector(RevealPosId, hit.HasSurface ? hit.Point : FarAway);
        }

        void UpdateSpread(float dt)
        {
            if (muzzle == null || dt <= 0f) return;
            Vector3 dir = muzzle.transform.forward;
            if (!_hasMean)
            {
                _meanDir = dir;
                _variance = 0f;
                _hasMean = true;
                return;
            }
            _meanDir = Vector3.Slerp(_meanDir, dir, 1f - Mathf.Exp(-dt / spreadMeanSeconds)).normalized;
            float deviation = Vector3.Angle(dir, _meanDir) * Mathf.Deg2Rad;
            _variance = Mathf.Lerp(_variance, deviation * deviation, 1f - Mathf.Exp(-dt / spreadWindowSeconds));
        }

        static string FormatReadout(float distance, float spreadRadius)
        {
            float cm = spreadRadius * 100f;
            string spread = cm < 10f ? cm.ToString("0.0") : cm.ToString("0");
            return string.Format("{0:0.00} m\n<size=75%><alpha=#99>±{1} cm", distance, spread);
        }

        static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "ReticleQuad" };
            mesh.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
            });
            mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) });
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        Transform CreateQuad(string name, Mesh mesh, Material material, out MeshRenderer renderer)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go.transform;
        }

        TextMeshPro CreateText(string name, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var text = go.AddComponent<TextMeshPro>();
            AimLinkFonts.ApplyMono(text);
            text.fontSize = 1f;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.rectTransform.sizeDelta = new Vector2(3f, 0.2f);
            // pivot on the edge the text hangs from, so the offset is measured to the text itself
            text.rectTransform.pivot = alignment == TextAlignmentOptions.Left ? new Vector2(0f, 0.5f) : new Vector2(0.5f, 1f);
            return text;
        }
    }
}
