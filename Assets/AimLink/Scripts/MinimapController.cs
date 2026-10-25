using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace AimLink
{
    /// <summary>
    /// A small 3D hologram of the scanned room in the lower left of the visor. Heading-up and centred on
    /// you, so the room turns as you turn. Shows the aim line, the point it hits and the beacons. Walls are
    /// cut off just above eye height so you can see into the room. B on the right controller toggles it.
    /// </summary>
    [DefaultExecutionOrder(100)] // after HudController has placed the visor for this frame
    public sealed class MinimapController : MonoBehaviour
    {
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int SolidId = Shader.PropertyToID("_Solid");
        static readonly int UseCutId = Shader.PropertyToID("_UseCut");
        static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        static readonly int CutPlaneId = Shader.PropertyToID("_CutPlane");
        static readonly int MapUpId = Shader.PropertyToID("_MapUp");
        static readonly int MapCenterId = Shader.PropertyToID("_MapCenter");
        static readonly int MapRadiusId = Shader.PropertyToID("_MapRadius");

        const float UnitTextHeight = 0.1f;

        [Header("References")]
        [SerializeField] HudController hud;
        [SerializeField] Transform head;
        [SerializeField] AimRaycaster aim;
        [SerializeField] MuzzleTrim muzzle;
        [SerializeField] BeaconManager beacons;
        [SerializeField] RoomScanController room;
        [SerializeField] InputRouter input;
        [SerializeField] AppStateMachine stateMachine;
        [SerializeField] SfxPlayer sfx;
        [Tooltip("Material using AimLink/Hologram. Instanced at runtime.")]
        [SerializeField] Material hologramMaterial;
        [Tooltip("Material using AimLink/Reticle, used for the ring around the map. Instanced at runtime.")]
        [SerializeField] Material ringMaterial;

        [Header("Placement")]
        [Tooltip("Centre of the map on the visor canvas, canvas units (mm) from the centre.")]
        [SerializeField] Vector2 canvasPosition = new Vector2(-380f, -240f);
        [Tooltip("Radius of the map on the visor, metres.")]
        [SerializeField] float mapRadius = 0.085f;
        [Tooltip("How far around you the map reaches, metres of real room.")]
        [SerializeField] float roomRadius = 4f;
        [Tooltip("How far the map is tilted back towards you, degrees (0 = edge-on, 90 = straight from above).")]
        [SerializeField] float tiltDegrees = 55f;
        [Tooltip("Walls are cut off this far above eye height, metres.")]
        [SerializeField] float cutAboveEyes = 0.05f;
        [SerializeField] bool enabledAtStart = true;

        [Header("Colours")]
        [SerializeField] Color roomColor = new Color(0.3f, 0.9f, 1f, 1f);
        [SerializeField] Color userColor = new Color(0.86f, 0.95f, 1f, 1f);
        [SerializeField] Color aimColor = new Color(1f, 0.14f, 0.1f, 1f);
        [SerializeField] Color beaconColor = new Color(1f, 0.62f, 0.1f, 1f);

        public bool MapEnabled { get; private set; }

        Transform _root, _world, _user, _hit;
        LineRenderer _aimLine;
        TextMeshPro _north;
        Material _roomMat, _userMat, _aimMat, _beaconMat, _ringMat;
        Mesh _octahedron, _chevron, _quad;
        readonly List<Material> _holoMats = new List<Material>();
        readonly List<GameObject> _surfaces = new List<GameObject>();
        readonly List<Transform> _beaconDots = new List<Transform>();
        readonly List<Vector3> _beaconPoints = new List<Vector3>();
        bool _live = true;
        bool _initialised;

        void Awake()
        {
            MapEnabled = enabledAtStart;
            Init();
        }

        void OnEnable()
        {
            if (input != null) input.RightBPressed += OnToggle;
            if (stateMachine != null) stateMachine.StateChanged += OnStateChanged;
            if (room != null) room.SurfacesBuilt += OnSurfacesBuilt;
        }

        void OnDisable()
        {
            if (input != null) input.RightBPressed -= OnToggle;
            if (stateMachine != null) stateMachine.StateChanged -= OnStateChanged;
            if (room != null) room.SurfacesBuilt -= OnSurfacesBuilt;
        }

        void Start()
        {
            if (room != null && room.SurfaceFilters.Count > 0) OnSurfacesBuilt();
        }

        void OnDestroy()
        {
            foreach (Material m in _holoMats) if (m != null) Destroy(m);
            if (_ringMat != null) Destroy(_ringMat);
        }

        void OnStateChanged(AppState prev, AppState next) { _live = next == AppState.Live; }

        void OnToggle()
        {
            if (!_live) return;
            MapEnabled = !MapEnabled;
            if (hud != null) hud.Notify(MapEnabled ? "MAP ON" : "MAP OFF");
            if (input != null) input.Pulse(false, 0.4f, 0.03f);
            if (sfx != null && sfx.Bank != null) sfx.Play(sfx.Bank.toggle);
        }

        void OnSurfacesBuilt()
        {
            var meshes = new List<Mesh>();
            var matrices = new List<Matrix4x4>();
            foreach (MeshFilter f in room.SurfaceFilters)
            {
                if (f == null || f.sharedMesh == null) continue;
                meshes.Add(f.sharedMesh);
                matrices.Add(f.transform.localToWorldMatrix);
            }
            ShowSurfaces(meshes, matrices);
        }

        /// <summary>Replace the room shown on the map. Matrices are the surfaces' real-world transforms.</summary>
        public void ShowSurfaces(IList<Mesh> meshes, IList<Matrix4x4> worldMatrices)
        {
            Init();
            foreach (GameObject go in _surfaces) if (go != null) Destroy(go);
            _surfaces.Clear();
            for (int i = 0; i < meshes.Count; i++)
            {
                var go = new GameObject("MapSurface");
                go.transform.SetParent(_world, false);
                Matrix4x4 m = worldMatrices[i];
                go.transform.localPosition = m.GetPosition();
                go.transform.localRotation = m.rotation;
                go.transform.localScale = m.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = meshes[i];
                AddRenderer(go, _roomMat);
                _surfaces.Add(go);
            }
        }

        // ---------- per frame ----------

        void LateUpdate()
        {
            bool show = MapEnabled && _live && hud != null && head != null;
            if (_root.gameObject.activeSelf != show) _root.gameObject.SetActive(show);
            if (!show) return;

            // pinned to the visor, tilted back so the room is seen from above at an angle
            Transform visor = hud.transform;
            _root.SetPositionAndRotation(visor.TransformPoint(new Vector3(canvasPosition.x, canvasPosition.y, 0f)),
                visor.rotation * Quaternion.Euler(-tiltDegrees, 0f, 0f));

            // heading-up: the room turns around you, floor on the map plane
            Vector3 fwd = head.forward;
            float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            Quaternion turn = Quaternion.Euler(0f, -yaw, 0f);
            float floorY = room != null && room.FloorHeight.HasValue ? room.FloorHeight.Value : head.position.y - 1.6f;
            Vector3 me = new Vector3(head.position.x, floorY, head.position.z);
            float scale = mapRadius / Mathf.Max(roomRadius, 0.5f);
            _world.localRotation = turn;
            _world.localScale = Vector3.one * scale;
            _world.localPosition = turn * (-me * scale);

            // clipping: a sphere around the map, and a cut just above eye height
            Vector3 up = _root.up;
            Vector3 cutPoint = _world.TransformPoint(new Vector3(head.position.x, head.position.y + cutAboveEyes, head.position.z));
            var cutPlane = new Vector4(up.x, up.y, up.z, -Vector3.Dot(up, cutPoint));
            foreach (Material m in _holoMats)
            {
                m.SetVector(CutPlaneId, cutPlane);
                m.SetVector(MapUpId, up);
                m.SetVector(MapCenterId, _root.position);
                m.SetFloat(MapRadiusId, mapRadius * 1.15f);
            }

            UpdateAim();
            UpdateBeacons();

            // north marker on the ring, turning with the room
            _north.transform.SetPositionAndRotation(_root.TransformPoint(turn * new Vector3(0f, 0f, mapRadius * 1.2f)), visor.rotation);
            _north.transform.localScale = Vector3.one * (0.007f / UnitTextHeight);
        }

        void UpdateAim()
        {
            bool on = aim != null && muzzle != null;
            _aimLine.enabled = on;
            if (!on)
            {
                _hit.gameObject.SetActive(false);
                return;
            }
            AimHit hit = aim.Current;
            Vector3 start = _world.TransformPoint(muzzle.transform.position);
            Vector3 end = _world.TransformPoint(hit.Point);
            _aimLine.SetPosition(0, start);
            _aimLine.SetPosition(1, end);
            _hit.gameObject.SetActive(hit.HasSurface);
            _hit.SetPositionAndRotation(end, _root.rotation);
        }

        void UpdateBeacons()
        {
            if (beacons != null) beacons.GetPoints(_beaconPoints);
            else _beaconPoints.Clear();
            while (_beaconDots.Count < _beaconPoints.Count)
                _beaconDots.Add(CreateMarker("MapBeacon", _octahedron, _beaconMat, 0.0055f));
            for (int i = 0; i < _beaconDots.Count; i++)
            {
                bool on = i < _beaconPoints.Count;
                _beaconDots[i].gameObject.SetActive(on);
                if (on) _beaconDots[i].SetPositionAndRotation(_world.TransformPoint(_beaconPoints[i]), _root.rotation);
            }
        }

        // ---------- construction ----------

        void Init()
        {
            if (_initialised) return;
            _initialised = true;

            _octahedron = BuildOctahedron();
            _chevron = BuildChevron();
            _quad = BuildQuad();

            _roomMat = HoloMaterial(roomColor, solid: false, useCut: true, opacity: 0.5f);
            _userMat = HoloMaterial(userColor, solid: true, useCut: false, opacity: 0.95f);
            _aimMat = HoloMaterial(aimColor, solid: true, useCut: false, opacity: 0.95f);
            _beaconMat = HoloMaterial(beaconColor, solid: true, useCut: false, opacity: 0.95f);

            _root = new GameObject("Minimap").transform;
            _root.SetParent(transform, false);
            _world = new GameObject("MapWorld").transform;
            _world.SetParent(_root, false);

            // you: a flat chevron at the centre, always pointing up the map (heading-up)
            _user = CreateMarker("MapUser", _chevron, _userMat, 0.012f);
            _user.SetParent(_root, false);
            _user.localPosition = new Vector3(0f, 0.001f, 0f);
            _user.localRotation = Quaternion.identity;

            _hit = CreateMarker("MapHit", _octahedron, _aimMat, 0.006f);

            var lineGo = new GameObject("MapAimLine");
            lineGo.transform.SetParent(_root, false);
            _aimLine = lineGo.AddComponent<LineRenderer>();
            _aimLine.useWorldSpace = true;
            _aimLine.positionCount = 2;
            _aimLine.widthMultiplier = 0.0012f;
            _aimLine.sharedMaterial = _aimMat;
            _aimLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _aimLine.receiveShadows = false;

            // thin ring around the map, lying in the map plane
            if (ringMaterial != null)
            {
                _ringMat = new Material(ringMaterial);
                _ringMat.SetColor(ColorId, roomColor);
                _ringMat.SetFloat("_DotRadius", 0f);
                _ringMat.SetFloat("_Ticks", 0f);
                _ringMat.SetFloat("_GapSize", 0f);
                _ringMat.SetFloat("_Halo", 0f);
                _ringMat.SetFloat("_RingRadius", 0.46f);
                _ringMat.SetFloat("_RingWidth", 0.012f);
                _ringMat.SetFloat("_Alpha", 0.6f);
                var ringGo = new GameObject("MapRing");
                ringGo.transform.SetParent(_root, false);
                ringGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                ringGo.transform.localScale = Vector3.one * (mapRadius * 2.2f);
                ringGo.AddComponent<MeshFilter>().sharedMesh = _quad;
                AddRenderer(ringGo, _ringMat);
            }

            var northGo = new GameObject("MapNorth");
            northGo.transform.SetParent(_root, false);
            _north = northGo.AddComponent<TextMeshPro>();
            AimLinkFonts.ApplyMono(_north);
            _north.fontSize = 1f;
            _north.alignment = TextAlignmentOptions.Center;
            _north.textWrappingMode = TextWrappingModes.NoWrap;
            _north.rectTransform.sizeDelta = new Vector2(1f, 0.2f);
            _north.color = roomColor;
            _north.text = "N";
        }

        Material HoloMaterial(Color color, bool solid, bool useCut, float opacity)
        {
            if (hologramMaterial == null) return null;
            var m = new Material(hologramMaterial);
            m.SetColor(ColorId, color);
            m.SetFloat(SolidId, solid ? 1f : 0f);
            m.SetFloat(UseCutId, useCut ? 1f : 0f);
            m.SetFloat(OpacityId, opacity);
            _holoMats.Add(m);
            return m;
        }

        Transform CreateMarker(string name, Mesh mesh, Material material, float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root, false);
            go.transform.localScale = Vector3.one * size;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            AddRenderer(go, material);
            return go.transform;
        }

        static void AddRenderer(GameObject go, Material material)
        {
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        static Mesh BuildOctahedron()
        {
            var mesh = new Mesh { name = "MapOctahedron" };
            mesh.SetVertices(new[]
            {
                new Vector3(0f, 0.5f, 0f), new Vector3(0f, -0.5f, 0f),
                new Vector3(0.5f, 0f, 0f), new Vector3(-0.5f, 0f, 0f),
                new Vector3(0f, 0f, 0.5f), new Vector3(0f, 0f, -0.5f)
            });
            mesh.SetTriangles(new[]
            {
                0, 4, 2, 0, 2, 5, 0, 5, 3, 0, 3, 4,
                1, 2, 4, 1, 5, 2, 1, 3, 5, 1, 4, 3
            }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // flat arrowhead in the XZ plane pointing along +Z
        static Mesh BuildChevron()
        {
            var mesh = new Mesh { name = "MapChevron" };
            mesh.SetVertices(new[]
            {
                new Vector3(0f, 0f, 0.5f), new Vector3(-0.35f, 0f, -0.4f),
                new Vector3(0f, 0f, -0.15f), new Vector3(0.35f, 0f, -0.4f)
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "MapQuad" };
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
    }
}
