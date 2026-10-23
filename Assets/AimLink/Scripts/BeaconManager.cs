using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace AimLink
{
    /// <summary>
    /// Left trigger drops a beacon at the aim point: a light pillar, a diamond marker with a numbered
    /// distance label and a pulsing ring on the surface. Left grip clears them. LIVE only.
    /// </summary>
    public sealed class BeaconManager : MonoBehaviour
    {
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        static readonly int ShapeId = Shader.PropertyToID("_Shape");
        static readonly int RingRadiusId = Shader.PropertyToID("_RingRadius");
        static readonly int RingWidthId = Shader.PropertyToID("_RingWidth");
        static readonly int DotRadiusId = Shader.PropertyToID("_DotRadius");
        static readonly int TicksId = Shader.PropertyToID("_Ticks");
        static readonly int GapSizeId = Shader.PropertyToID("_GapSize");
        static readonly int HaloId = Shader.PropertyToID("_Halo");

        const float UnitTextHeight = 0.1f;

        [Header("References")]
        [SerializeField] AimRaycaster aim;
        [SerializeField] Transform head;
        [SerializeField] InputRouter input;
        [SerializeField] AppStateMachine stateMachine;
        [SerializeField] SfxPlayer sfx;
        [Tooltip("Material using AimLink/Beacon. Instanced at runtime.")]
        [SerializeField] Material beaconMaterial;
        [Tooltip("Material using AimLink/Reticle, used for the ring on the surface. Instanced at runtime.")]
        [SerializeField] Material ringMaterial;

        [Header("Look")]
        [SerializeField] Color color = new Color(1f, 0.62f, 0.1f, 1f);
        [Tooltip("Oldest beacon is removed when this many are placed.")]
        [SerializeField] int maxBeacons = 6;
        [Tooltip("Height of the light pillar, metres.")]
        [SerializeField] float pillarHeight = 2.2f;
        [Tooltip("Pillar width as a visual angle, degrees (so it stays visible far away).")]
        [SerializeField] float pillarAngularWidth = 0.5f;
        [Tooltip("Diamond marker size as a visual angle, degrees.")]
        [SerializeField] float markerAngularSize = 1.5f;
        [SerializeField] float labelAngularHeight = 0.7f;
        [Tooltip("Diameter of the ring on the surface, metres.")]
        [SerializeField] float ringDiameter = 0.35f;

        public int Count => _beacons.Count;

        /// <summary>Short message for the HUD message line.</summary>
        public event System.Action<string> Notice;

        sealed class Beacon
        {
            public GameObject Root;
            public Transform Pillar, Marker, Ring;
            public TextMeshPro Label;
            public Vector3 Point, Normal;
            public int Number;
            public float Age;
        }

        readonly List<Beacon> _beacons = new List<Beacon>();
        Material _pillarMat, _markerMat, _ringMat;
        Mesh _quad, _pillarQuad;
        int _nextNumber = 1;
        bool _live = true;

        void Awake()
        {
            if (beaconMaterial != null)
            {
                _pillarMat = new Material(beaconMaterial);
                _pillarMat.SetFloat(ShapeId, 0f);
                _pillarMat.SetColor(ColorId, color);
                _markerMat = new Material(beaconMaterial);
                _markerMat.SetFloat(ShapeId, 1f);
                _markerMat.SetColor(ColorId, color);
            }
            if (ringMaterial != null)
            {
                _ringMat = new Material(ringMaterial);
                _ringMat.SetColor(ColorId, color);
                _ringMat.SetFloat(DotRadiusId, 0f);
                _ringMat.SetFloat(TicksId, 0f);
                _ringMat.SetFloat(GapSizeId, 0f);
                _ringMat.SetFloat(HaloId, 0f);
                _ringMat.SetFloat(RingRadiusId, 0.42f);
                _ringMat.SetFloat(RingWidthId, 0.03f);
            }
            _quad = BuildQuad(new Vector2(0.5f, 0.5f));
            _pillarQuad = BuildQuad(new Vector2(0.5f, 0f)); // pivot at the bottom
        }

        void OnEnable()
        {
            if (input != null)
            {
                input.LeftTriggerPressed += OnPlace;
                input.LeftGripPressed += OnClear;
            }
            if (stateMachine != null) stateMachine.StateChanged += OnStateChanged;
        }

        void OnDisable()
        {
            if (input != null)
            {
                input.LeftTriggerPressed -= OnPlace;
                input.LeftGripPressed -= OnClear;
            }
            if (stateMachine != null) stateMachine.StateChanged -= OnStateChanged;
        }

        void OnDestroy()
        {
            if (_pillarMat != null) Destroy(_pillarMat);
            if (_markerMat != null) Destroy(_markerMat);
            if (_ringMat != null) Destroy(_ringMat);
        }

        void OnStateChanged(AppState prev, AppState next)
        {
            _live = next == AppState.Live;
            foreach (Beacon b in _beacons) b.Root.SetActive(_live);
        }

        void OnPlace()
        {
            if (!_live || aim == null) return;
            AimHit hit = aim.Current;
            if (!hit.HasSurface)
            {
                // nothing to mark: a weak double buzz instead of the confirm pulse
                if (input != null) input.Pulse(true, 0.3f, 0.04f);
                Notice?.Invoke("NO SURFACE TO MARK");
                return;
            }
            while (_beacons.Count >= Mathf.Max(1, maxBeacons)) Remove(0);
            _beacons.Add(Create(hit.Point, hit.Normal));
            if (input != null) input.Pulse(true, 0.8f, 0.08f);
            if (sfx != null && sfx.Bank != null) sfx.Play(sfx.Bank.beaconPlace);
            Notice?.Invoke(string.Format("MARK {0:00} PLACED", _beacons[_beacons.Count - 1].Number));
        }

        void OnClear()
        {
            if (!_live || _beacons.Count == 0) return;
            while (_beacons.Count > 0) Remove(0);
            _nextNumber = 1;
            if (input != null) input.Pulse(true, 0.5f, 0.12f);
            if (sfx != null && sfx.Bank != null) sfx.Play(sfx.Bank.trimReset);
            Notice?.Invoke("MARKS CLEARED");
        }

        void Remove(int index)
        {
            Destroy(_beacons[index].Root);
            _beacons.RemoveAt(index);
        }

        Beacon Create(Vector3 point, Vector3 normal)
        {
            var b = new Beacon { Point = point, Normal = normal, Number = _nextNumber++ };
            b.Root = new GameObject("Beacon " + b.Number);
            b.Root.transform.SetParent(transform, false);
            b.Pillar = CreateQuad(b.Root.transform, "Pillar", _pillarQuad, _pillarMat);
            b.Marker = CreateQuad(b.Root.transform, "Marker", _quad, _markerMat);
            b.Ring = CreateQuad(b.Root.transform, "Ring", _quad, _ringMat);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(b.Root.transform, false);
            b.Label = labelGo.AddComponent<TextMeshPro>();
            AimLinkFonts.ApplyMono(b.Label);
            b.Label.fontSize = 1f;
            b.Label.alignment = TextAlignmentOptions.Bottom;
            b.Label.textWrappingMode = TextWrappingModes.NoWrap;
            b.Label.rectTransform.sizeDelta = new Vector2(4f, 0.2f);
            b.Label.rectTransform.pivot = new Vector2(0.5f, 0f);
            b.Label.color = color;
            return b;
        }

        void LateUpdate()
        {
            if (!_live || head == null) return;
            Vector3 eye = head.position;
            for (int i = 0; i < _beacons.Count; i++)
            {
                Beacon b = _beacons[i];
                b.Age += Time.deltaTime;
                float grow = 1f - Mathf.Pow(1f - Mathf.Clamp01(b.Age / 0.3f), 3f);

                Vector3 toBeacon = b.Point - eye;
                float dist = Mathf.Max(toBeacon.magnitude, 0.05f);

                // pillar: rises along world up, turns about that axis to face the viewer
                Vector3 flat = Vector3.ProjectOnPlane(toBeacon, Vector3.up);
                if (flat.sqrMagnitude < 1e-6f) flat = head.forward;
                float width = dist * Mathf.Tan(pillarAngularWidth * Mathf.Deg2Rad);
                b.Pillar.SetPositionAndRotation(b.Point, Quaternion.LookRotation(flat.normalized, Vector3.up));
                b.Pillar.localScale = new Vector3(width, pillarHeight * grow, 1f);

                // diamond floats above the point, always the same apparent size
                float markerSize = dist * Mathf.Tan(markerAngularSize * Mathf.Deg2Rad);
                Vector3 markerPos = b.Point + Vector3.up * (0.25f + markerSize * 0.5f) * grow;
                Quaternion face = Quaternion.LookRotation((markerPos - eye).normalized, Vector3.up);
                float bob = 1f + 0.06f * Mathf.Sin(Time.time * 3f + b.Number);
                b.Marker.SetPositionAndRotation(markerPos, face);
                b.Marker.localScale = Vector3.one * (markerSize * grow * bob);

                // ring on the surface, pinging outward
                float ping = Mathf.Repeat(b.Age * 0.6f, 1f);
                b.Ring.SetPositionAndRotation(b.Point + b.Normal * 0.01f, Quaternion.LookRotation(b.Normal));
                b.Ring.localScale = Vector3.one * (ringDiameter * (0.5f + ping));

                float textScale = dist * Mathf.Tan(labelAngularHeight * Mathf.Deg2Rad) / UnitTextHeight;
                b.Label.transform.SetPositionAndRotation(markerPos + Vector3.up * (markerSize * 0.7f), face);
                b.Label.transform.localScale = Vector3.one * textScale;
                b.Label.text = string.Format("MARK {0:00}  {1:0.0} m", b.Number, dist);
            }
        }

        Transform CreateQuad(Transform parent, string name, Mesh mesh, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        static Mesh BuildQuad(Vector2 pivot)
        {
            var mesh = new Mesh { name = "BeaconQuad" };
            float x0 = -pivot.x, x1 = 1f - pivot.x, y0 = -pivot.y, y1 = 1f - pivot.y;
            mesh.SetVertices(new[]
            {
                new Vector3(x0, y0, 0f), new Vector3(x1, y0, 0f), new Vector3(x0, y1, 0f), new Vector3(x1, y1, 0f)
            });
            mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) });
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
