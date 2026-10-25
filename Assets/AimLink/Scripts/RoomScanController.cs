using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace AimLink
{
    public enum RoomDataState { Loading, Ready, Missing }

    /// <summary>
    /// Builds the scan surfaces from MRUK (global mesh, optionally planes/volumes), drives the scan wave
    /// material, pops scene labels as the wave reaches each anchor and counts mapped surfaces.
    /// </summary>
    public sealed class RoomScanController : MonoBehaviour
    {
        static readonly int ScanOriginId = Shader.PropertyToID("_AimLinkScanOrigin");
        static readonly int ScanRadiusId = Shader.PropertyToID("_ScanRadius");
        static readonly int ScanSpeedId = Shader.PropertyToID("_ScanSpeed");
        static readonly int FadeTimeId = Shader.PropertyToID("_FadeTime");
        static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        static readonly int BaseGlowId = Shader.PropertyToID("_BaseGlow");
        static readonly int EdgeGlowId = Shader.PropertyToID("_EdgeGlow");

        const float ScanDone = 100000f; // radius that puts the whole room "behind" the wave

        [Header("References")]
        [Tooltip("Material using AimLink/RoomScan. Instanced at runtime.")]
        [SerializeField] Material scanMaterial;
        [SerializeField] Transform head;
        [SerializeField] AppStateMachine stateMachine;
        [SerializeField] InputRouter input;
        [SerializeField] SfxPlayer sfx;

        [Header("Surfaces")]
        [Tooltip("Also draw scene planes/volumes when a global mesh exists (doubles up brightness on walls).")]
        [SerializeField] bool drawAnchorsWithGlobalMesh = false;
        [Tooltip("Seconds to wait for MRUK before reporting the room as missing.")]
        [SerializeField] float roomTimeoutSeconds = 8f;

        [Header("Scan wave")]
        [Tooltip("Seconds for the wave to cross the whole room. 2.8 matches scan_sweep.wav.")]
        [SerializeField] float scanDuration = 2.8f;
        [Tooltip("Seconds the grid stays lit behind the wave front.")]
        [SerializeField] float trailFadeSeconds = 1.5f;
        [Tooltip("Upper limit for the wave radius, metres.")]
        [SerializeField] float maxScanRadius = 15f;

        [Header("Geometry view (X toggles it in LIVE)")]
        [Tooltip("Whether the room geometry grid is shown when the app starts.")]
        [SerializeField] bool geometryVisibleAtStart = true;
        [Tooltip("Extra grid brightness over the whole room, on top of the reveal around the reticle. 0 = reveal only.")]
        [Range(0f, 1f)] [SerializeField] float geometryGridGlow = 0f;
        [Tooltip("Faint edge highlight over the whole room.")]
        [Range(0f, 1f)] [SerializeField] float geometryEdgeGlow = 0.05f;

        [Header("Labels")]
        [SerializeField] Color labelColor = new Color(0.55f, 0.95f, 1f, 1f);
        [Tooltip("Label text height as a visual angle, degrees.")]
        [SerializeField] float labelAngularHeight = 1.1f;
        [SerializeField] float labelCharsPerSecond = 45f;
        [SerializeField] float labelHoldSeconds = 2f;
        [SerializeField] float labelFadeSeconds = 0.5f;

        public RoomDataState RoomState { get; private set; } = RoomDataState.Loading;
        public int SurfacesMapped { get; private set; }
        public bool IsScanning { get; private set; }
        /// <summary>Seconds the wave front takes to cross the room (the trail keeps fading after that).</summary>
        public float ScanDuration => scanDuration;
        /// <summary>Colliders of the global mesh, for AimRaycaster (MRUK's own raycast ignores the global mesh).</summary>
        public IReadOnlyList<Collider> MeshColliders => _colliders;
        /// <summary>The room surfaces as drawn (global mesh, or planes and volumes), for the minimap.</summary>
        public IReadOnlyList<MeshFilter> SurfaceFilters => _filters;
        /// <summary>World height of the floor, if the room has one.</summary>
        public float? FloorHeight { get; private set; }

        /// <summary>True while the whole room's geometry grid is shown (toggled with X).</summary>
        public bool GeometryVisible { get; private set; }

        public event Action<bool> GeometryToggled;
        public event Action<int> SurfaceMapped;
        public event Action RoomStateChanged;
        public event Action SurfacesBuilt;

        struct LabelTarget { public MRUKAnchor Anchor; public string Name; }

        readonly List<Collider> _colliders = new List<Collider>();
        readonly List<Renderer> _renderers = new List<Renderer>();
        readonly List<MeshFilter> _filters = new List<MeshFilter>();
        readonly List<GameObject> _built = new List<GameObject>();
        readonly List<LabelTarget> _labels = new List<LabelTarget>();
        readonly List<SceneLabelTag> _tagPool = new List<SceneLabelTag>();
        readonly List<(float dist, int index)> _scanOrder = new List<(float, int)>();

        Material _material;
        Coroutine _scan;
        float _startTime;
        bool _visible = true;

        void Awake()
        {
            if (scanMaterial != null) _material = new Material(scanMaterial);
            GeometryVisible = geometryVisibleAtStart;
            ApplyGeometry();
            SetRadius(ScanDone);
        }

        void OnEnable()
        {
            if (stateMachine != null) stateMachine.StateChanged += OnStateChanged;
            if (input != null) input.XPressed += OnToggleGeometry;
        }

        void OnDisable()
        {
            if (stateMachine != null) stateMachine.StateChanged -= OnStateChanged;
            if (input != null) input.XPressed -= OnToggleGeometry;
        }

        void Start()
        {
            _startTime = Time.unscaledTime;
            if (MRUK.Instance != null) MRUK.Instance.RegisterSceneLoadedCallback(OnSceneLoaded);
            else SetRoomState(RoomDataState.Missing);
        }

        void Update()
        {
            if (RoomState == RoomDataState.Loading && Time.unscaledTime - _startTime > roomTimeoutSeconds)
                SetRoomState(RoomDataState.Missing);
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }

        // ---------- room ----------

        void OnSceneLoaded()
        {
            MRUKRoom room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
            if (room == null)
            {
                SetRoomState(RoomDataState.Missing);
                return;
            }
            Build(room);
            SetRoomState(RoomDataState.Ready);
            // launched straight into LIVE without a boot scan: everything counts as mapped already
            if (!IsScanning && (stateMachine == null || stateMachine.State == AppState.Live))
                SetSurfaces(_labels.Count);
        }

        void Build(MRUKRoom room)
        {
            foreach (GameObject go in _built) if (go != null) Destroy(go);
            _built.Clear();
            _renderers.Clear();
            _colliders.Clear();
            _filters.Clear();
            FloorHeight = room.FloorAnchor != null ? room.FloorAnchor.transform.position.y : (float?)null;
            _labels.Clear();

            bool hasGlobalMesh = false;
            MRUKAnchor globalAnchor = room.GlobalMeshAnchor;
            if (globalAnchor != null)
            {
                Mesh mesh = globalAnchor.GlobalMesh;
                if (mesh != null && mesh.vertexCount > 0)
                {
                    GameObject go = AddSurface(globalAnchor.transform, "ScanGlobalMesh", mesh);
                    var col = go.AddComponent<MeshCollider>();
                    col.sharedMesh = mesh;
                    _colliders.Add(col);
                    hasGlobalMesh = true;
                }
            }

            foreach (MRUKAnchor anchor in room.Anchors)
            {
                if (anchor == null || anchor == globalAnchor) continue;
                if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.GLOBAL_MESH | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE)) continue;

                _labels.Add(new LabelTarget { Anchor = anchor, Name = DisplayName(anchor.Label) });

                if (hasGlobalMesh && !drawAnchorsWithGlobalMesh) continue;
                if (anchor.VolumeBounds.HasValue)
                    AddSurface(anchor.transform, "ScanVolume", BoxMesh(anchor.VolumeBounds.Value));
                else if (anchor.PlaneRect.HasValue)
                    AddSurface(anchor.transform, "ScanPlane", QuadMesh(anchor.PlaneRect.Value));
            }
            ApplyVisibility();
            SurfacesBuilt?.Invoke();
        }

        GameObject AddSurface(Transform parent, string name, Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            _filters.Add(filter);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            _renderers.Add(r);
            _built.Add(go);
            return go;
        }

        // MRUK planes lie in the anchor's local XY plane
        static Mesh QuadMesh(Rect r)
        {
            var mesh = new Mesh { name = "ScanPlane" };
            mesh.SetVertices(new[]
            {
                new Vector3(r.xMin, r.yMin, 0f), new Vector3(r.xMax, r.yMin, 0f),
                new Vector3(r.xMin, r.yMax, 0f), new Vector3(r.xMax, r.yMax, 0f)
            });
            mesh.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh BoxMesh(Bounds b)
        {
            Vector3 n = b.min, x = b.max;
            var v = new[]
            {
                new Vector3(n.x, n.y, n.z), new Vector3(x.x, n.y, n.z), new Vector3(x.x, x.y, n.z), new Vector3(n.x, x.y, n.z),
                new Vector3(n.x, n.y, x.z), new Vector3(x.x, n.y, x.z), new Vector3(x.x, x.y, x.z), new Vector3(n.x, x.y, x.z)
            };
            var t = new[]
            {
                0, 2, 1, 0, 3, 2,   4, 5, 6, 4, 6, 7,
                0, 1, 5, 0, 5, 4,   3, 6, 2, 3, 7, 6,
                0, 4, 7, 0, 7, 3,   1, 2, 6, 1, 6, 5
            };
            var mesh = new Mesh { name = "ScanVolume" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static string DisplayName(MRUKAnchor.SceneLabels label)
        {
            if ((label & MRUKAnchor.SceneLabels.FLOOR) != 0) return "FLOOR";
            if ((label & MRUKAnchor.SceneLabels.CEILING) != 0) return "CEILING";
            if ((label & (MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INNER_WALL_FACE)) != 0) return "WALL";
            if ((label & MRUKAnchor.SceneLabels.DOOR_FRAME) != 0) return "DOOR";
            if ((label & MRUKAnchor.SceneLabels.WINDOW_FRAME) != 0) return "WINDOW";
            if ((label & MRUKAnchor.SceneLabels.WALL_ART) != 0) return "WALL ART";
            if ((label & MRUKAnchor.SceneLabels.TABLE) != 0) return "TABLE";
            if ((label & MRUKAnchor.SceneLabels.COUCH) != 0) return "COUCH";
            if ((label & MRUKAnchor.SceneLabels.BED) != 0) return "BED";
            if ((label & MRUKAnchor.SceneLabels.STORAGE) != 0) return "STORAGE";
            if ((label & MRUKAnchor.SceneLabels.SCREEN) != 0) return "SCREEN";
            if ((label & MRUKAnchor.SceneLabels.LAMP) != 0) return "LAMP";
            if ((label & MRUKAnchor.SceneLabels.PLANT) != 0) return "PLANT";
            return "OBJECT";
        }

        /// <summary>Short display name for an aim hit on this anchor (used by AimRaycaster).</summary>
        public static string DisplayNameOf(MRUKAnchor anchor)
        {
            return anchor != null ? DisplayName(anchor.Label) : "SURFACE";
        }

        void SetRoomState(RoomDataState state)
        {
            if (RoomState == state) return;
            RoomState = state;
            RoomStateChanged?.Invoke();
        }

        // ---------- scan ----------

        /// <summary>Hide the grid and zero the surface count, ready for a boot scan.</summary>
        public void ResetScan()
        {
            StopScan();
            SetRadius(-1f);
            SetSurfaces(0);
        }

        /// <summary>Runs the wave from the user's position. Yieldable from BootSequence.</summary>
        public Coroutine RunScan()
        {
            StopScan();
            _scan = StartCoroutine(ScanRoutine());
            return _scan;
        }

        void StopScan()
        {
            if (_scan != null) StopCoroutine(_scan);
            _scan = null;
            IsScanning = false;
        }

        void OnToggleGeometry()
        {
            if (stateMachine != null && stateMachine.State != AppState.Live) return;
            GeometryVisible = !GeometryVisible;
            ApplyGeometry();
            if (input != null) input.Pulse(true, 0.4f, 0.03f);
            if (sfx != null && sfx.Bank != null) sfx.Play(sfx.Bank.toggle);
            GeometryToggled?.Invoke(GeometryVisible);
        }

        // On: the translucent look (reveal around the reticle plus a faint edge glow). Off: no geometry drawn at all.
        void ApplyGeometry()
        {
            if (_material != null)
            {
                _material.SetFloat(BaseGlowId, geometryGridGlow);
                _material.SetFloat(EdgeGlowId, geometryEdgeGlow);
            }
            ApplyVisibility();
        }

        IEnumerator ScanRoutine()
        {
            IsScanning = true;
            SetSurfaces(0);
            Vector3 origin = head != null ? head.position : Vector3.zero;
            Shader.SetGlobalVector(ScanOriginId, origin);

            // order the labels by when the wave reaches them
            _scanOrder.Clear();
            float far = 2f;
            for (int i = 0; i < _labels.Count; i++)
            {
                MRUKAnchor a = _labels[i].Anchor;
                if (a == null) continue;
                float d = a.GetClosestSurfacePosition(origin, out _);
                _scanOrder.Add((d, i));
                far = Mathf.Max(far, Vector3.Distance(origin, a.GetAnchorCenter()));
            }
            _scanOrder.Sort((x, y) => x.dist.CompareTo(y.dist));

            float maxRadius = Mathf.Min(far + 0.5f, maxScanRadius);
            float speed = maxRadius / Mathf.Max(scanDuration, 0.1f);
            if (_material != null)
            {
                _material.SetFloat(ScanSpeedId, speed);
                _material.SetFloat(FadeTimeId, trailFadeSeconds);
            }

            int next = 0;
            float radius = 0f;
            float end = maxRadius + speed * trailFadeSeconds; // keep going until the trail has faded everywhere
            while (radius < end)
            {
                radius += speed * Time.deltaTime;
                SetRadius(radius);
                while (next < _scanOrder.Count && _scanOrder[next].dist <= radius)
                {
                    PopLabel(_labels[_scanOrder[next].index], origin);
                    next++;
                    SetSurfaces(next);
                }
                yield return null;
            }
            // anything beyond maxScanRadius still counts
            SetSurfaces(_labels.Count);
            SetRadius(ScanDone);
            IsScanning = false;
            _scan = null;
        }

        void PopLabel(LabelTarget target, Vector3 origin)
        {
            if (target.Anchor == null) return;
            float dist = target.Anchor.GetClosestSurfacePosition(origin, out Vector3 closest);
            // tag sits at the anchor centre, pulled 5 cm towards the user so it is not inside the surface
            Vector3 pos = target.Anchor.GetAnchorCenter();
            Vector3 toUser = origin - pos;
            if (toUser.sqrMagnitude > 0.01f) pos += toUser.normalized * 0.05f;

            string text = string.Format("{0}  {1:0.0} m", target.Name, dist);
            GetTag().Show(text, pos, head, labelColor, labelAngularHeight, labelCharsPerSecond, labelHoldSeconds, labelFadeSeconds);
            if (sfx != null && sfx.Bank != null) sfx.PlayAt(sfx.Bank.labelPop, pos, 0.8f);
        }

        SceneLabelTag GetTag()
        {
            foreach (SceneLabelTag t in _tagPool) if (!t.Active) return t;
            SceneLabelTag tag = SceneLabelTag.Create(transform);
            _tagPool.Add(tag);
            return tag;
        }

        void SetSurfaces(int count)
        {
            if (SurfacesMapped == count) return;
            SurfacesMapped = count;
            SurfaceMapped?.Invoke(count);
        }

        void SetRadius(float radius)
        {
            if (_material != null) _material.SetFloat(ScanRadiusId, radius);
        }

        // ---------- state ----------

        void OnStateChanged(AppState prev, AppState next)
        {
            switch (next)
            {
                case AppState.Live:
                    // boot skipped or finished: leave the room in its "already scanned" resting look
                    if (!IsScanning) SetRadius(ScanDone);
                    if (RoomState == RoomDataState.Ready && !IsScanning) SetSurfaces(_labels.Count);
                    _visible = true;
                    break;
                case AppState.Boot:
                    // the boot scan is always shown, even if the geometry was switched off before
                    GeometryVisible = true;
                    ResetScan();
                    _visible = true;
                    break;
                case AppState.Blackout:
                    StopScan();
                    foreach (SceneLabelTag t in _tagPool) t.Hide();
                    _visible = false;
                    break;
            }
            ApplyVisibility();
        }

        void ApplyVisibility()
        {
            foreach (Renderer r in _renderers) if (r != null) r.enabled = _visible && GeometryVisible;
            if (_material != null) _material.SetFloat(IntensityId, 1f);
        }
    }
}
