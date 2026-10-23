// Procedural reticle on a unit quad: centre dot, ring broken on the four axes, tick marks through the gaps.
// Premultiplied alpha and it writes alpha, so it covers passthrough and stays the same colour on a
// bright wall (an additive reticle washes out to white there). A thin dark halo keeps it readable.
Shader "AimLink/Reticle"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0.14, 0.1, 1)
        _RingRadius ("Ring Radius", Range(0, 0.5)) = 0.25
        _RingWidth ("Ring Width", Range(0, 0.1)) = 0.024
        _GapSize ("Ring Gap Size", Range(0, 1)) = 0.22
        _DotRadius ("Dot Radius", Range(0, 0.2)) = 0.02
        _Ticks ("Tick Strength", Range(0, 1)) = 1
        _Halo ("Dark Halo", Range(0, 1)) = 0.8
        _HaloGrow ("Halo Width", Range(0, 0.05)) = 0.014
        _Spin ("Spin (radians)", Float) = 0
        _Alpha ("Alpha", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+50" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Reticle"
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _RingRadius;
                float _RingWidth;
                float _GapSize;
                float _DotRadius;
                float _Ticks;
                float _Halo;
                float _HaloGrow;
                float _Spin;
                half _Alpha;
            CBUFFER_END

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv - 0.5;
                return o;
            }

            // Coverage of the whole shape, with every stroke grown by 'grow' (used for the halo).
            float Shape(float2 p, float grow, float aa)
            {
                float r = length(p);
                float dotMask = 1.0 - smoothstep(_DotRadius + grow - aa, _DotRadius + grow + aa, r);

                float halfW = _RingWidth * 0.5 + grow;
                float ring = 1.0 - smoothstep(halfW - aa, halfW + aa, abs(r - _RingRadius));
                // gaps on the four axes, where the ticks pass through
                float ang = atan2(p.y, p.x);
                float q = abs(frac(ang / (PI * 0.5)) - 0.5) * 2.0;   // 1 on the axes, 0 on the diagonals
                ring *= smoothstep(_GapSize - 0.04, _GapSize, 1.0 - q);

                // short ticks outside the ring on the four axes
                float2 ap = abs(p);
                float along = max(ap.x, ap.y);
                float across = min(ap.x, ap.y);
                float tickW = _RingWidth * 0.5 + grow;
                float tick = (1.0 - smoothstep(tickW - aa, tickW + aa, across))
                           * smoothstep(_RingRadius + 0.05 - grow - aa, _RingRadius + 0.05 - grow + aa, along)
                           * (1.0 - smoothstep(0.44 + grow - aa, 0.44 + grow + aa, along));

                return saturate(dotMask + ring + tick * _Ticks);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float s, c;
                sincos(_Spin, s, c);
                float2 p = float2(c * i.uv.x - s * i.uv.y, s * i.uv.x + c * i.uv.y);
                float aa = max(fwidth(length(p)), 1e-4);

                // crisp edges: a slim stroke with soft edges never reaches full coverage and reads as pink
                float m = Shape(p, 0.0, aa * 0.5);
                float halo = Shape(p, _HaloGrow, aa * 1.5) * _Halo;
                float a = saturate(max(m, halo)) * _Alpha;
                // full colour on the shape, a dark shade of the same colour in the halo around it
                float shade = max(m, halo * 0.3);
                return half4(_Color.rgb * shade * _Alpha, a);
            }
            ENDHLSL
        }
    }
}
