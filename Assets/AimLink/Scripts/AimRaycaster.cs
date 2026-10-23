using Meta.XR;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace AimLink
{
    public enum AimSource { None, Scene, GlobalMesh, Depth }

    public struct AimHit
    {
        /// <summary>False when nothing was hit; Point is then a fixed distance along the ray.</summary>
        public bool HasSurface;
        public Vector3 Point;
        public Vector3 Normal;
        /// <summary>Distance from the muzzle along the aim ray, metres.</summary>
        public float Distance;
        public string Label;
        public AimSource Source;
    }

    /// <summary>
    /// Casts the muzzle ray against the real room every frame:
    /// 1. MRUK scene anchors (planes/volumes) and the global mesh, nearest wins
    /// 2. environment depth raycast
    /// 3. nothing: a point at a fixed distance, HasSurface = false
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class AimRaycaster : MonoBehaviour
    {
        [SerializeField] MuzzleTrim muzzle;
        [SerializeField] RoomScanController room;
        [Tooltip("Optional. Depth-based fallback when no scene hit is found.")]
        [SerializeField] EnvironmentRaycastManager depthRaycaster;
        [SerializeField] bool useDepthFallback = true;

        [Tooltip("Longest distance to look for a surface, metres.")]
        [SerializeField] float maxDistance = 30f;
        [Tooltip("Where the reticle goes when nothing is hit, metres.")]
        [SerializeField] float noSurfaceDistance = 10f;
        [Tooltip("If the global mesh and a scene plane are within this distance, prefer the plane (cleaner normal).")]
        [SerializeField] float preferPlaneWithin = 0.06f;

        public AimHit Current { get; private set; }

        // INVISIBLE_WALL_FACE is a virtual room boundary, not a real surface
        static readonly LabelFilter SceneFilter = new LabelFilter(
            ~(MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE | MRUKAnchor.SceneLabels.GLOBAL_MESH));

        MRUKRoom _room;
        float _nextRoomRefresh;

        void LateUpdate()
        {
            if (muzzle == null) return;
            Ray ray = muzzle.AimRay;
            AimHit hit;
            if (!RaycastScene(ray, out hit) && !RaycastDepth(ray, out hit))
            {
                hit = new AimHit
                {
                    HasSurface = false,
                    Point = ray.GetPoint(noSurfaceDistance),
                    Normal = -ray.direction,
                    Distance = noSurfaceDistance,
                    Label = "NO SURFACE",
                    Source = AimSource.None
                };
            }
            Current = hit;
        }

        bool RaycastScene(Ray ray, out AimHit result)
        {
            result = default;
            bool any = false;
            float best = maxDistance;

            MRUKRoom mrukRoom = GetRoom();
            if (mrukRoom != null && mrukRoom.Raycast(ray, maxDistance, SceneFilter, out RaycastHit planeHit, out MRUKAnchor anchor))
            {
                best = planeHit.distance;
                result = new AimHit
                {
                    HasSurface = true,
                    Point = planeHit.point,
                    Normal = planeHit.normal,
                    Distance = planeHit.distance,
                    Label = RoomScanController.DisplayNameOf(anchor),
                    Source = AimSource.Scene
                };
                any = true;
            }

            if (room != null)
            {
                // the mesh must be clearly in front of the plane to win, otherwise its noise makes the reticle jitter
                float limit = any ? best - preferPlaneWithin : maxDistance;
                var colliders = room.MeshColliders;
                for (int i = 0; i < colliders.Count; i++)
                {
                    Collider c = colliders[i];
                    if (c == null || limit <= 0f) continue;
                    if (c.Raycast(ray, out RaycastHit meshHit, limit))
                    {
                        limit = meshHit.distance;
                        Vector3 n = meshHit.normal;
                        if (Vector3.Dot(n, ray.direction) > 0f) n = -n;
                        result = new AimHit
                        {
                            HasSurface = true,
                            Point = meshHit.point,
                            Normal = n,
                            Distance = meshHit.distance,
                            Label = "SURFACE",
                            Source = AimSource.GlobalMesh
                        };
                        any = true;
                    }
                }
            }
            return any;
        }

        bool RaycastDepth(Ray ray, out AimHit result)
        {
            result = default;
            if (!useDepthFallback || depthRaycaster == null || !depthRaycaster.isActiveAndEnabled) return false;
            if (!EnvironmentRaycastManager.IsSupported) return false;
            if (!depthRaycaster.Raycast(ray, out EnvironmentRaycastHit hit, maxDistance)) return false;
            result = new AimHit
            {
                HasSurface = true,
                Point = hit.point,
                Normal = hit.normalConfidence > 0.3f ? hit.normal : -ray.direction,
                Distance = Vector3.Distance(ray.origin, hit.point),
                Label = "SURFACE",
                Source = AimSource.Depth
            };
            return true;
        }

        // GetCurrentRoom is a native call; refresh it once a second instead of every frame
        MRUKRoom GetRoom()
        {
            if (Time.unscaledTime >= _nextRoomRefresh || _room == null)
            {
                _nextRoomRefresh = Time.unscaledTime + 1f;
                _room = MRUK.Instance != null && MRUK.Instance.IsInitialized ? MRUK.Instance.GetCurrentRoom() : null;
            }
            return _room;
        }
    }
}
