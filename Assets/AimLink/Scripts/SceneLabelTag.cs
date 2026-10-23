using TMPro;
using UnityEngine;

namespace AimLink
{
    /// <summary>World-space tag ("WALL  3.4 m") that types in, holds, fades and disables itself. Pooled by RoomScanController.</summary>
    public sealed class SceneLabelTag : MonoBehaviour
    {
        // TMP glyph height in metres at fontSize 1 and scale 1 (approximately)
        const float UnitTextHeight = 0.1f;

        TextMeshPro _text;
        Transform _head;
        float _age, _typeSeconds, _holdSeconds, _fadeSeconds, _angularHeightDeg;
        int _length;
        Color _color;

        public bool Active => gameObject.activeSelf;

        public static SceneLabelTag Create(Transform parent)
        {
            var go = new GameObject("SceneLabelTag");
            go.transform.SetParent(parent, false);
            var tag = go.AddComponent<SceneLabelTag>();
            tag._text = go.AddComponent<TextMeshPro>();
            AimLinkFonts.ApplyMono(tag._text);
            tag._text.fontSize = 1f;
            tag._text.alignment = TextAlignmentOptions.Center;
            tag._text.textWrappingMode = TextWrappingModes.NoWrap;
            tag._text.rectTransform.sizeDelta = new Vector2(4f, 0.3f);
            go.SetActive(false);
            return tag;
        }

        public void Show(string text, Vector3 position, Transform head, Color color,
            float angularHeightDeg, float charsPerSecond, float holdSeconds, float fadeSeconds)
        {
            _head = head;
            _color = color;
            _angularHeightDeg = angularHeightDeg;
            _length = text.Length;
            _typeSeconds = _length / Mathf.Max(charsPerSecond, 1f);
            _holdSeconds = holdSeconds;
            _fadeSeconds = fadeSeconds;
            _age = 0f;
            transform.position = position;
            _text.text = text;
            _text.maxVisibleCharacters = 0;
            _text.color = color;
            gameObject.SetActive(true);
            Face();
        }

        public void Hide() { gameObject.SetActive(false); }

        void LateUpdate()
        {
            _age += Time.deltaTime;
            _text.maxVisibleCharacters = _age >= _typeSeconds ? _length : Mathf.FloorToInt(_length * _age / _typeSeconds);

            float fadeStart = _typeSeconds + _holdSeconds;
            if (_age > fadeStart)
            {
                float a = 1f - (_age - fadeStart) / Mathf.Max(_fadeSeconds, 0.01f);
                if (a <= 0f)
                {
                    Hide();
                    return;
                }
                Color c = _color;
                c.a *= a;
                _text.color = c;
            }
            Face();
        }

        void Face()
        {
            if (_head == null) return;
            Vector3 toTag = transform.position - _head.position;
            float dist = toTag.magnitude;
            if (dist < 0.01f) return;
            transform.rotation = Quaternion.LookRotation(toTag, Vector3.up);
            float height = dist * Mathf.Tan(_angularHeightDeg * Mathf.Deg2Rad);
            transform.localScale = Vector3.one * (height / UnitTextHeight);
        }
    }
}
