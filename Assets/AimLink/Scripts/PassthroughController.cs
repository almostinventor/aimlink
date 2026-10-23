using UnityEngine;

namespace AimLink
{
    /// <summary>
    /// Shows/hides passthrough and drives the full-screen visor mask (opaque black with a circular opening).
    /// BLACKOUT = passthrough layer hidden + mask fully closed.
    /// </summary>
    public sealed class PassthroughController : MonoBehaviour
    {
        static readonly int ProgressId = Shader.PropertyToID("_Progress");
        static readonly int GlitchId = Shader.PropertyToID("_Glitch");
        static readonly int GlitchSeedId = Shader.PropertyToID("_GlitchSeed");

        [SerializeField] OVRPassthroughLayer passthroughLayer;
        [Tooltip("Material using AimLink/VisorMask.")]
        [SerializeField] Material visorMaskMaterial;
        [SerializeField] AppStateMachine stateMachine;

        Material _mask;
        MeshRenderer _maskRenderer;

        void Awake()
        {
            if (visorMaskMaterial == null) return;
            _mask = new Material(visorMaskMaterial);

            // Unit quad; the shader stretches it over the whole view. Huge bounds so it is never culled.
            var mesh = new Mesh { name = "VisorMaskQuad" };
            mesh.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);

            var go = new GameObject("VisorMask");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            _maskRenderer = go.AddComponent<MeshRenderer>();
            _maskRenderer.sharedMaterial = _mask;
            _maskRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _maskRenderer.receiveShadows = false;
            _maskRenderer.enabled = false;
        }

        void OnEnable()
        {
            if (stateMachine != null) stateMachine.StateChanged += OnStateChanged;
        }

        void OnDisable()
        {
            if (stateMachine != null) stateMachine.StateChanged -= OnStateChanged;
        }

        void OnDestroy()
        {
            if (_mask != null) Destroy(_mask);
        }

        void OnStateChanged(AppState prev, AppState next)
        {
            switch (next)
            {
                case AppState.Live: SetLive(); break;
                case AppState.Blackout:
                case AppState.Boot: SetBlack(); break;
            }
        }

        /// <summary>Passthrough fully visible, no mask.</summary>
        public void SetLive()
        {
            SetPassthroughVisible(true);
            if (_maskRenderer != null) _maskRenderer.enabled = false;
        }

        /// <summary>Opaque black, passthrough off.</summary>
        public void SetBlack()
        {
            SetPassthroughVisible(false);
            SetVisor(0f, 0f);
        }

        /// <summary>Turn the passthrough feed on behind a still-closed visor.</summary>
        public void BeginVisorOpen()
        {
            SetPassthroughVisible(true);
            SetVisor(0f, 0f);
        }

        /// <summary>open01: 0 = closed (black), 1 = fully open (the view arrives in blocks). glitch: 0..1 band tearing.</summary>
        public void SetVisor(float open01, float glitch)
        {
            if (_maskRenderer == null) return;
            _maskRenderer.enabled = open01 < 1f;
            _mask.SetFloat(ProgressId, Mathf.Clamp01(open01));
            _mask.SetFloat(GlitchId, glitch);
            if (glitch > 0f) _mask.SetFloat(GlitchSeedId, Mathf.Floor(Time.unscaledTime * 30f));
        }

        void SetPassthroughVisible(bool visible)
        {
            if (passthroughLayer == null) return;
            passthroughLayer.hidden = !visible;
            passthroughLayer.textureOpacity = 1f;
        }
    }
}
