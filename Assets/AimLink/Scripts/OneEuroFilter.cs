using UnityEngine;

namespace AimLink
{
    /// <summary>One-euro filter (Casiez et al.): heavy smoothing when slow, little lag when fast.</summary>
    public sealed class OneEuroFilter
    {
        public float MinCutoff;
        public float Beta;
        public float DerivativeCutoff;

        bool _hasPrev;
        float _prev;
        float _prevDeriv;

        public OneEuroFilter(float minCutoff = 1f, float beta = 0.5f, float derivativeCutoff = 1f)
        {
            MinCutoff = minCutoff;
            Beta = beta;
            DerivativeCutoff = derivativeCutoff;
        }

        public void Reset() { _hasPrev = false; }

        public float Filter(float value, float dt)
        {
            if (!_hasPrev || dt <= 0f)
            {
                _hasPrev = true;
                _prev = value;
                _prevDeriv = 0f;
                return value;
            }
            float deriv = (value - _prev) / dt;
            _prevDeriv = Mathf.Lerp(_prevDeriv, deriv, Alpha(DerivativeCutoff, dt));
            float cutoff = MinCutoff + Beta * Mathf.Abs(_prevDeriv);
            _prev = Mathf.Lerp(_prev, value, Alpha(cutoff, dt));
            return _prev;
        }

        static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * cutoff);
            return 1f / (1f + tau / dt);
        }
    }

    public sealed class OneEuroFilter3
    {
        readonly OneEuroFilter _x, _y, _z;

        public OneEuroFilter3(float minCutoff = 1f, float beta = 0.5f, float derivativeCutoff = 1f)
        {
            _x = new OneEuroFilter(minCutoff, beta, derivativeCutoff);
            _y = new OneEuroFilter(minCutoff, beta, derivativeCutoff);
            _z = new OneEuroFilter(minCutoff, beta, derivativeCutoff);
        }

        public void SetParams(float minCutoff, float beta, float derivativeCutoff)
        {
            _x.MinCutoff = _y.MinCutoff = _z.MinCutoff = minCutoff;
            _x.Beta = _y.Beta = _z.Beta = beta;
            _x.DerivativeCutoff = _y.DerivativeCutoff = _z.DerivativeCutoff = derivativeCutoff;
        }

        public void Reset() { _x.Reset(); _y.Reset(); _z.Reset(); }

        public Vector3 Filter(Vector3 v, float dt)
        {
            return new Vector3(_x.Filter(v.x, dt), _y.Filter(v.y, dt), _z.Filter(v.z, dt));
        }
    }
}
