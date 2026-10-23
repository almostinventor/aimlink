using System;
using System.Collections;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AimLink
{
    public enum HudElement { Compass }

    /// <summary>
    /// The visor: a world-space canvas that follows the head with a little lag. Built entirely in code.
    /// Canvas units are millimetres (root scale 0.001).
    ///
    /// Deliberately sparse, like an instrument rather than a game HUD: a compass ribbon with heading, and a
    /// message line that is empty while everything is fine. Persistent alerts (tracking lost, no room
    /// data) stay up while the condition lasts; notices (trim, toggles, beacons) show briefly and fade.
    /// Range lives next to the reticle, not here.
    /// </summary>
    public sealed class HudController : MonoBehaviour
    {
        const float CanvasWidth = 1100f, CanvasHeight = 760f;
        const float HalfH = CanvasHeight * 0.5f;

        [Header("References")]
        [SerializeField] Transform head;
        [SerializeField] AppStateMachine stateMachine;
        [SerializeField] InputRouter input;
        [SerializeField] MuzzleTrim muzzle;
        [SerializeField] RoomScanController room;
        [SerializeField] BeaconManager beacons;

        [Header("Visor follow")]
        [Tooltip("Distance of the visor plane from the eyes, metres.")]
        [SerializeField] float distance = 1.2f;
        [Tooltip("Higher = stiffer follow. Lower = more inertia.")]
        [SerializeField] float followSharpness = 9f;
        [Tooltip("Overall size multiplier.")]
        [SerializeField] float scale = 1f;

        [Header("Palette")]
        [SerializeField] Color primary = new Color(0.86f, 0.95f, 1f, 1f);
        [SerializeField] Color secondary = new Color(0.45f, 0.78f, 0.9f, 0.75f);
        [SerializeField] Color warning = new Color(1f, 0.62f, 0.1f, 1f);

        [Header("Compass")]
        [Tooltip("Width of the compass ribbon, canvas units (mm).")]
        [SerializeField] float compassWidth = 460f;
        [Tooltip("Degrees of heading the ribbon covers.")]
        [SerializeField] float compassSpanDegrees = 90f;

        [Header("Messages")]
        [Tooltip("Seconds a notice (trim, toggles, beacons) stays fully visible before fading.")]
        [SerializeField] float noticeSeconds = 1.8f;
        [SerializeField] float noticeFadeSeconds = 0.5f;

        Canvas _canvas;
        Quaternion _smoothedRotation;
        bool _snap = true;

        const int TickCount = 9;
        CanvasGroup _headingGroup;
        RectTransform[] _ticks;
        Image[] _tickImages;
        TextMeshProUGUI[] _tickLabels;
        TextMeshProUGUI _heading, _alerts, _notice, _console;
        CanvasGroup _consoleGroup;

        string _noticeText = "";
        float _noticeUntil = -1f;
        string _consoleText = "";
        bool _consoleCursor;
        readonly StringBuilder _sb = new StringBuilder(128);

        void Awake() { BuildCanvas(); }

        void OnEnable()
        {
            if (stateMachine != null) stateMachine.StateChanged += OnStateChanged;
            if (muzzle != null) muzzle.TrimChanged += OnTrimChanged;
            if (room != null) room.GeometryToggled += OnGeometryToggled;
            if (beacons != null) beacons.Notice += Notify;
        }

        void OnDisable()
        {
            if (stateMachine != null) stateMachine.StateChanged -= OnStateChanged;
            if (muzzle != null) muzzle.TrimChanged -= OnTrimChanged;
            if (room != null) room.GeometryToggled -= OnGeometryToggled;
            if (beacons != null) beacons.Notice -= Notify;
        }

        void OnStateChanged(AppState prev, AppState next)
        {
            StopAllCoroutines();
            _snap = true;
            _noticeUntil = -1f;
            switch (next)
            {
                case AppState.Live:
                    _canvas.enabled = true;
                    SetElementsVisible(true);
                    SetConsole("", false);
                    break;
                case AppState.Boot:
                    _canvas.enabled = true;
                    SetElementsVisible(false);
                    SetConsole("", true);
                    SetConsoleAlpha(1f);
                    break;
                case AppState.Blackout:
                    _canvas.enabled = false;
                    break;
            }
        }

        void OnTrimChanged()
        {
            if (muzzle != null) Notify("TRIM  " + muzzle.FormatTrim());
        }

        void OnGeometryToggled(bool visible) { Notify(visible ? "GEOMETRY ON" : "GEOMETRY OFF"); }

        // ---------- API ----------

        /// <summary>Brief message on the message line; replaces any current notice.</summary>
        public void Notify(string text)
        {
            _noticeText = text ?? "";
            _noticeUntil = Time.unscaledTime + noticeSeconds;
        }

        public void SetElementsVisible(bool visible) { _headingGroup.alpha = visible ? 1f : 0f; }

        public IEnumerator FlyIn(HudElement element, float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                _headingGroup.alpha = t / seconds;
                yield return null;
            }
            _headingGroup.alpha = 1f;
        }

        public void SetConsole(string text, bool cursor)
        {
            _consoleText = text ?? "";
            _consoleCursor = cursor;
        }

        public void SetConsoleAlpha(float alpha) { _consoleGroup.alpha = alpha; }

        // ---------- per-frame ----------

        void LateUpdate()
        {
            if (head == null || !_canvas.enabled) return;
            Follow();
            UpdateCompass();
            UpdateMessages();
            UpdateConsole();
        }

        void Follow()
        {
            if (_snap)
            {
                _smoothedRotation = head.rotation;
                _snap = false;
            }
            else
            {
                float k = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
                _smoothedRotation = Quaternion.Slerp(_smoothedRotation, head.rotation, k);
            }
            // position follows the head exactly; only the direction lags, so the visor swings rather than swims
            transform.SetPositionAndRotation(head.position + _smoothedRotation * Vector3.forward * distance, _smoothedRotation);
            transform.localScale = Vector3.one * (0.001f * scale);
        }

        void UpdateCompass()
        {
            Vector3 fwd = head.forward;
            float heading = Mathf.Repeat(Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg, 360f);
            _heading.text = (Mathf.RoundToInt(heading) % 360).ToString("000");

            // ribbon: a tick every 15 deg, labels every 30 deg (letters on the cardinals)
            float pxPerDegree = compassWidth / compassSpanDegrees;
            float half = compassSpanDegrees * 0.5f;
            float first = Mathf.Ceil((heading - half) / 15f) * 15f;
            for (int i = 0; i < TickCount; i++)
            {
                float deg = first + i * 15f;
                float offset = deg - heading;
                bool on = offset <= half;
                _tickImages[i].enabled = on;
                _tickLabels[i].enabled = on;
                if (!on) continue;

                int d = Mathf.RoundToInt(Mathf.Repeat(deg, 360f)) % 360;
                bool major = d % 30 == 0;
                bool cardinal = d % 90 == 0;
                float fade = Mathf.Clamp01((half - Mathf.Abs(offset)) / 10f);
                _ticks[i].anchoredPosition = new Vector2(offset * pxPerDegree, 0f);
                _ticks[i].sizeDelta = new Vector2(cardinal ? 2f : 1.5f, major ? 14f : 7f);
                Color c = cardinal ? primary : secondary;
                c.a *= fade;
                _tickImages[i].color = c;
                _tickLabels[i].color = c;
                _tickLabels[i].text = major ? CompassLabel(d) : "";
            }
        }

        static string CompassLabel(int degrees)
        {
            switch (degrees)
            {
                case 0: return "N";
                case 90: return "E";
                case 180: return "S";
                case 270: return "W";
                default: return degrees.ToString("000");
            }
        }

        void UpdateMessages()
        {
            // persistent alerts: only while something actually needs attention
            _sb.Length = 0;
            if (input != null && !input.WeaponLinked) _sb.Append("TRACKING LOST — KEEP BOTH CONTROLLERS IN VIEW\n");
            if (room != null && room.RoomState == RoomDataState.Missing) _sb.Append("NO ROOM DATA — RUN SPACE SETUP\n");
            _alerts.SetText(_sb);

            // transient notice
            float left = _noticeUntil - Time.unscaledTime;
            if (left <= -noticeFadeSeconds || _noticeText.Length == 0)
            {
                _notice.enabled = false;
                return;
            }
            _notice.enabled = true;
            _notice.text = _noticeText;
            Color c = primary;
            c.a = left >= 0f ? 1f : 1f + left / noticeFadeSeconds;
            _notice.color = c;
        }

        void UpdateConsole()
        {
            if (_consoleText.Length == 0 && !_consoleCursor)
            {
                if (!string.IsNullOrEmpty(_console.text)) _console.text = "";
                return;
            }
            bool cursorOn = _consoleCursor && Mathf.Repeat(Time.unscaledTime, 0.6f) < 0.3f;
            _console.text = _consoleText + (cursorOn ? "_" : "");
        }

        // ---------- construction ----------

        void BuildCanvas()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            var root = (RectTransform)transform;
            root.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);
            transform.localScale = Vector3.one * (0.001f * scale);

            // compass ribbon, top centre, with the heading in a small bracketed box under it
            var compassGo = new GameObject("Compass", typeof(RectTransform));
            var crt = (RectTransform)compassGo.transform;
            crt.SetParent(root, false);
            crt.anchoredPosition = new Vector2(0f, HalfH - 60f);
            crt.sizeDelta = new Vector2(compassWidth, 90f);
            _headingGroup = compassGo.AddComponent<CanvasGroup>();

            _ticks = new RectTransform[TickCount];
            _tickImages = new Image[TickCount];
            _tickLabels = new TextMeshProUGUI[TickCount];
            for (int i = 0; i < TickCount; i++)
            {
                _tickImages[i] = MakeImage(crt, "Tick", Vector2.zero, new Vector2(2f, 7f), secondary);
                _ticks[i] = _tickImages[i].rectTransform;
                _ticks[i].pivot = new Vector2(0.5f, 1f);
                _tickLabels[i] = MakeMono(_ticks[i], "Label", new Vector2(0f, 20f), new Vector2(80f, 22f), 16f, TextAlignmentOptions.Center);
            }
            MakeImage(crt, "Baseline", new Vector2(0f, 1f), new Vector2(compassWidth, 1f), secondary);
            MakeImage(crt, "Caret", new Vector2(0f, -19f), new Vector2(2f, 8f), primary);
            _heading = MakeMono(crt, "Heading", new Vector2(0f, -40f), new Vector2(90f, 28f), 22f, TextAlignmentOptions.Center);
            MakeImage(crt, "BoxL", new Vector2(-38f, -40f), new Vector2(1.5f, 24f), secondary);
            MakeImage(crt, "BoxR", new Vector2(38f, -40f), new Vector2(1.5f, 24f), secondary);
            MakeImage(crt, "BoxLT", new Vector2(-34f, -28f), new Vector2(8f, 1.5f), secondary);
            MakeImage(crt, "BoxLB", new Vector2(-34f, -52f), new Vector2(8f, 1.5f), secondary);
            MakeImage(crt, "BoxRT", new Vector2(34f, -28f), new Vector2(8f, 1.5f), secondary);
            MakeImage(crt, "BoxRB", new Vector2(34f, -52f), new Vector2(8f, 1.5f), secondary);

            // message line, bottom centre: alerts above the transient notice
            _alerts = MakeMono(root, "Alerts", new Vector2(0f, -HalfH + 120f), new Vector2(900f, 60f), 19f, TextAlignmentOptions.Bottom);
            _alerts.color = warning;
            _alerts.lineSpacing = 10f;
            _notice = MakeMono(root, "Notice", new Vector2(0f, -HalfH + 72f), new Vector2(900f, 26f), 19f, TextAlignmentOptions.Center);
            _notice.enabled = false;

            // boot console
            _console = MakeMono(root, "Console", new Vector2(-170f, 40f), new Vector2(700f, 320f), 18f, TextAlignmentOptions.TopLeft);
            _console.lineSpacing = 12f;
            _consoleGroup = _console.gameObject.AddComponent<CanvasGroup>();
        }

        TextMeshProUGUI MakeMono(Transform parent, string name, Vector2 position, Vector2 size, float fontSize, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = size;
            rt.anchoredPosition = position;
            var text = go.AddComponent<TextMeshProUGUI>();
            AimLinkFonts.ApplyMono(text);
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = primary;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            return text;
        }

        static Image MakeImage(Transform parent, string name, Vector2 position, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = size;
            rt.anchoredPosition = position;
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }
    }
}
