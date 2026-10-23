// Beacon parts on a unit quad. _Shape 0 = light pillar (fades upward, dashes travel up),
// _Shape 1 = diamond marker. Premultiplied alpha, drawn on top of everything.
Shader "AimLink/Beacon"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0.62, 0.1, 1)
        _Shape ("Shape (0 pillar, 1 diamond)", Range(0, 1)) = 0
        _Opacity ("Opacity (how much passthrough it covers)", Range(0, 1)) = 0.85
        _Alpha ("Alpha", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+40" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Beacon"
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
                float _Shape;
                half _Opacity;
                half _Alpha;
            CBUFFER_END

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // pillar: bright core, soft sides, fading towards the top
                float side = 1.0 - abs(i.uv.x - 0.5) * 2.0;
                float core = smoothstep(0.55, 1.0, side);
                float glow = side * side * 0.35;
                float rise = pow(saturate(1.0 - i.uv.y), 1.6);
                float dashes = 0.55 + 0.45 * step(0.5, frac(i.uv.y * 14.0 - _Time.y * 1.2));
                float pillar = (core + glow) * rise * dashes;

                // diamond: outline plus a solid centre
                float2 p = abs(i.uv - 0.5);
                float d = p.x + p.y;
                float aa = max(fwidth(d), 1e-4);
                float outline = 1.0 - smoothstep(0.035 - aa, 0.035 + aa, abs(d - 0.4));
                float centre = 1.0 - smoothstep(0.14 - aa, 0.14 + aa, d);
                float diamond = saturate(outline + centre);

                float m = saturate(lerp(pillar, diamond, _Shape)) * _Alpha;
                return half4(_Color.rgb * m, m * _Opacity);
            }
            ENDHLSL
        }
    }
}
