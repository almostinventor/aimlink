// Additive scan visual for the room mesh: triplanar world grid, expanding wave, fading trail,
// fresnel edge glow and a local reveal around the reticle. Alpha is never written, so passthrough
// stays visible underneath.
Shader "AimLink/RoomScan"
{
    Properties
    {
        [HDR] _GridColor ("Grid Color", Color) = (0.15, 0.85, 1, 1)
        [HDR] _WaveColor ("Wave Color", Color) = (0.8, 1, 1, 1)
        _GridSpacing ("Grid Spacing (m)", Float) = 0.1
        _LineWidth ("Line Width (px)", Float) = 1.2
        _ScanRadius ("Scan Radius (m)", Float) = -1
        _ScanWidth ("Scan Width (m)", Float) = 0.35
        _ScanSpeed ("Scan Speed (m/s)", Float) = 3
        _FadeTime ("Trail Fade Time (s)", Float) = 1.5
        _TrailIntensity ("Trail Intensity", Range(0, 2)) = 0.6
        _BaseGlow ("Residual Grid After Fade", Range(0, 1)) = 0
        _EdgeGlow ("Residual Edge After Fade", Range(0, 1)) = 0.05
        _FresnelPower ("Fresnel Power", Float) = 3
        _RevealRadius ("Reveal Radius (m)", Float) = 0.4
        _RevealIntensity ("Reveal Intensity", Range(0, 2)) = 0.5
        _Intensity ("Master Intensity", Range(0, 2)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Scan"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One, Zero One
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _GridColor;
                half4 _WaveColor;
                float _GridSpacing;
                float _LineWidth;
                float _ScanRadius;
                float _ScanWidth;
                float _ScanSpeed;
                float _FadeTime;
                half _TrailIntensity;
                half _BaseGlow;
                half _EdgeGlow;
                float _FresnelPower;
                float _RevealRadius;
                half _RevealIntensity;
                half _Intensity;
            CBUFFER_END

            // set globally by RoomScanController / ReticleController
            float3 _AimLinkScanOrigin;
            float3 _AimLinkRevealPos;

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            float GridLine(float2 uv)
            {
                float2 c = uv / _GridSpacing;
                float2 g = abs(frac(c - 0.5) - 0.5) / max(fwidth(c), 1e-5);
                return 1.0 - saturate(min(g.x, g.y) / _LineWidth);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 p = i.positionWS;

                // face normal from derivatives: the scene mesh has no reliable vertex normals
                float3 n = normalize(cross(ddy(p), ddx(p)));
                float3 w = pow(abs(n), 4.0);
                w /= (w.x + w.y + w.z);
                float grid = GridLine(p.zy) * w.x + GridLine(p.xz) * w.y + GridLine(p.xy) * w.z;

                float3 viewDir = normalize(GetWorldSpaceViewDir(p));
                float fresnel = pow(1.0 - abs(dot(n, viewDir)), _FresnelPower);

                float d = distance(p, _AimLinkScanOrigin);
                float behind = _ScanRadius - d;                       // > 0 once the wave has passed
                float passed = step(0.0, behind);
                float front = saturate(1.0 - abs(behind) / _ScanWidth);
                front *= front;
                float trail = passed * saturate(1.0 - behind / max(_ScanSpeed * _FadeTime, 1e-3));

                float reveal = saturate(1.0 - distance(p, _AimLinkRevealPos) / _RevealRadius);
                reveal = reveal * reveal * _RevealIntensity;

                half3 col = _GridColor.rgb * grid * (trail * _TrailIntensity + passed * _BaseGlow + reveal);
                col += _GridColor.rgb * fresnel * (trail * 0.35 + passed * _EdgeGlow);
                col += _WaveColor.rgb * front * (0.25 + grid + fresnel * 0.5);
                return half4(col * _Intensity, 0);
            }
            ENDHLSL
        }
    }
}
