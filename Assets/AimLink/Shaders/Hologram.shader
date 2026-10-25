// Minimap hologram. Surface mode shades a scaled copy of the room mesh: walls bright, floors dim, with
// a bright seam where the walls are cut off above eye height so you can see into the room.
// Solid mode draws markers and lines in one flat colour. Everything outside the map sphere is clipped.
Shader "AimLink/Hologram"
{
    Properties
    {
        _Color ("Color", Color) = (0.3, 0.9, 1, 1)
        _Solid ("Solid (markers)", Range(0, 1)) = 0
        _UseCut ("Cut Above Eye Height", Range(0, 1)) = 1
        _Opacity ("Opacity", Range(0, 1)) = 0.55
        _CutPlane ("Cut Plane (world, xyz normal, w offset)", Vector) = (0, 1, 0, -10000)
        _MapUp ("Map Up (world)", Vector) = (0, 1, 0, 0)
        _MapCenter ("Map Centre (world)", Vector) = (0, 0, 0, 0)
        _MapRadius ("Map Radius (m)", Float) = 1000
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+60" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Hologram"
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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 posWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Solid;
                float _UseCut;
                half _Opacity;
                float4 _CutPlane;
                float4 _MapUp;
                float4 _MapCenter;
                float _MapRadius;
            CBUFFER_END

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.posWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.posWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 p = i.posWS;

                // only the part of the room around you, and nothing above the cut
                clip(_MapRadius - distance(p, _MapCenter.xyz));
                float above = dot(_CutPlane.xyz, p) + _CutPlane.w;   // > 0 above the cut
                if (_UseCut > 0.5) clip(-above);

                if (_Solid > 0.5)
                    return half4(_Color.rgb * _Opacity, _Opacity);

                // face normal from derivatives: walls (normal across the map) bright, floors (normal = map up) dim
                float3 n = normalize(cross(ddy(p), ddx(p)));
                float facing = abs(dot(n, normalize(_MapUp.xyz)));
                float shade = lerp(0.7, 0.2, facing);

                // bright seam along the cut so the wall tops read as a clean outline
                float seam = 1.0 - saturate(-above / max(fwidth(above) * 2.0, 1e-5));
                float m = saturate(shade + seam * _UseCut);
                float a = m * _Opacity;
                return half4(_Color.rgb * a, a);
            }
            ENDHLSL
        }
    }
}
