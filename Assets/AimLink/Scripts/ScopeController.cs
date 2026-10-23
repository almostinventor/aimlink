using System.Collections;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.UI;

namespace AimLink
{
    /// <summary>
    /// Zoom inset on the visor: a magnified crop of the headset's forward camera, centred on the point
    /// the reticle is on. A on the right (handle) controller toggles it. LIVE only.
    /// Uses the Passthrough Camera API, which needs the headset camera permission.
    /// </summary>
    public sealed class ScopeController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Passthrough camera component (MRUK). Disabled while the scope is off, to save performance.")]
        [SerializeField] PassthroughCameraAccess cameraAccess;
        [SerializeField] AimRaycaster aim;
        [SerializeField] HudController hud;
        [SerializeField] InputRouter input;
        [SerializeField] AppStateMachine stateMachine;
        [SerializeField] SfxPlayer sfx;

        [Header("Scope")]
        [SerializeField] bool enabledAtStart = true;
        [Tooltip("Magnification of the inset relative to the camera image.")]
        [Range(1.5f, 8f)] [SerializeField] float zoom = 4f;
        [Tooltip("Centre of the inset on the visor canvas, canvas units (mm) from the centre.")]
        [SerializeField] Vector2 position = new Vector2(330f, -252f);
        [Tooltip("Size of the inset, canvas units. Keep 4:3 to match the camera.")]
        [SerializeField] Vector2 size = new Vector2(300f, 225f);

        [Header("Look")]
        [SerializeField] Color frameColor = new Color(0.45f, 0.78f, 0.9f, 0.75f);
        [SerializeField] Color crossColor = new Color(1f, 0.14f, 0.1f, 1f);

        public bool ScopeEnabled { get; private set; }

        RectTransform _root;
        RawImage _image;
        RectTransform _crossH, _crossV;
        TextMeshProUGUI _caption, _noSignal;
        bool _live = true;
        bool _permissionRequested;

        void Awake() { ScopeEnabled = enabledAtStart; }

        void OnEnable()
        {
            if (input != null) input.RightAPressed += OnToggle;
            if (stateMachine != null) stateMachine.StateChanged += OnStateChanged;
        }

        void OnDisable()
        {
            if (input != null) input.RightAPressed -= OnToggle;
            if (stateMachine != null) stateMachine.StateChanged -= OnStateChanged;
        }

        void Start()
        {
            Build();
            Apply();
            StartCoroutine(RequestCameraPermission());
        }

        // Asked after the scene permission dialog is out of the way: Android drops a request made while another is showing.
        IEnumerator RequestCameraPermission()
        {
            float deadline = Time.unscaledTime + 20f;
            while (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission) && Time.unscaledTime < deadline)
                yield return null;
            yield return new WaitForSecondsRealtime(1f);
            if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission) && !_permissionRequested)
            {
                _permissionRequested = true;
                Permission.RequestUserPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission);
            }
        }

        void OnStateChanged(AppState prev, AppState next)
        {
            _live = next == AppState.Live;
            Apply();
        }

        void OnToggle()
        {
            if (!_live) return;
            ScopeEnabled = !ScopeEnabled;
            Apply();
            if (hud != null) hud.Notify(ScopeEnabled ? "SCOPE ON" : "SCOPE OFF");
            if (input != null) input.Pulse(false, 0.4f, 0.03f);
            if (sfx != null && sfx.Bank != null) sfx.Play(sfx.Bank.toggle);
        }

        void Apply()
        {
            bool on = ScopeEnabled && _live;
            if (_root != null) _root.gameObject.SetActive(on);
            // the camera stream only runs while the scope is on
            if (cameraAccess != null && cameraAccess.enabled != ScopeEnabled) cameraAccess.enabled = ScopeEnabled;
        }

        void LateUpdate()
        {
            if (_root == null || !_root.gameObject.activeSelf) return;

            bool playing = cameraAccess != null && cameraAccess.enabled && cameraAccess.IsPlaying;
            Texture tex = playing ? cameraAccess.GetTexture() : null;
            bool hasImage = tex != null && aim != null;
            Vector2 uv = Vector2.zero;
            if (hasImage)
            {
                uv = cameraAccess.WorldToViewportPoint(aim.Current.Point);
                // behind the camera or outside its view: nothing to show
                hasImage = uv.x > 0f && uv.x < 1f && uv.y > 0f && uv.y < 1f
                           && Vector3.Dot(aim.Current.Point - cameraAccess.GetCameraPose().position, cameraAccess.GetCameraPose().forward) > 0f;
            }

            _image.enabled = hasImage;
            _crossH.gameObject.SetActive(hasImage);
            _crossV.gameObject.SetActive(hasImage);
            _noSignal.enabled = !hasImage;
            if (!hasImage)
            {
                _noSignal.text = playing ? "OUT OF VIEW" : "NO SIGNAL";
                return;
            }

            // crop a 1/zoom window centred on the aim point, kept inside the image
            float w = 1f / zoom;
            float x = Mathf.Clamp(uv.x - w * 0.5f, 0f, 1f - w);
            float y = Mathf.Clamp(uv.y - w * 0.5f, 0f, 1f - w);
            _image.texture = tex;
            _image.uvRect = new Rect(x, y, w, w);

            // the cross marks the aim point; it only leaves the centre when the crop hits the image edge
            Vector2 local = new Vector2((uv.x - x) / w - 0.5f, (uv.y - y) / w - 0.5f);
            Vector2 crossPos = new Vector2(local.x * size.x, local.y * size.y);
            _crossH.anchoredPosition = crossPos;
            _crossV.anchoredPosition = crossPos;
        }

        void Build()
        {
            if (hud == null) return;
            var go = new GameObject("Scope", typeof(RectTransform));
            _root = (RectTransform)go.transform;
            _root.SetParent(hud.transform, false);
            _root.sizeDelta = size;
            _root.anchoredPosition = position;

            // dark backing so the frame reads before the camera delivers its first image
            MakeImage(_root, "Backing", Vector2.zero, size, new Color(0f, 0f, 0f, 0.55f));

            var imageGo = new GameObject("Image", typeof(RectTransform));
            var irt = (RectTransform)imageGo.transform;
            irt.SetParent(_root, false);
            irt.sizeDelta = size;
            _image = imageGo.AddComponent<RawImage>();
            _image.raycastTarget = false;

            _crossH = MakeImage(_root, "CrossH", Vector2.zero, new Vector2(22f, 1.5f), crossColor).rectTransform;
            _crossV = MakeImage(_root, "CrossV", Vector2.zero, new Vector2(1.5f, 22f), crossColor).rectTransform;

            // frame: thin outline with heavier corners
            float hw = size.x * 0.5f, hh = size.y * 0.5f;
            MakeImage(_root, "Top", new Vector2(0f, hh), new Vector2(size.x, 1.5f), frameColor);
            MakeImage(_root, "Bottom", new Vector2(0f, -hh), new Vector2(size.x, 1.5f), frameColor);
            MakeImage(_root, "Left", new Vector2(-hw, 0f), new Vector2(1.5f, size.y), frameColor);
            MakeImage(_root, "Right", new Vector2(hw, 0f), new Vector2(1.5f, size.y), frameColor);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    MakeImage(_root, "Corner", new Vector2(sx * hw, sy * hh), new Vector2(7f, 7f), frameColor);

            _caption = MakeText(_root, "Caption", new Vector2(0f, hh + 14f), new Vector2(size.x, 18f), 14f, TextAlignmentOptions.Left, false);
            _caption.color = frameColor;
            _caption.text = "SCOPE  " + zoom.ToString("0.0") + "X";

            _noSignal = MakeText(_root, "NoSignal", Vector2.zero, new Vector2(size.x, 24f), 16f, TextAlignmentOptions.Center, true);
            _noSignal.color = frameColor;
        }

        static Image MakeImage(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        static TextMeshProUGUI MakeText(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, TextAlignmentOptions alignment, bool mono)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            var text = go.AddComponent<TextMeshProUGUI>();
            if (mono) AimLinkFonts.ApplyMono(text);
            else AimLinkFonts.ApplyLabel(text);
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            return text;
        }
    }
}
