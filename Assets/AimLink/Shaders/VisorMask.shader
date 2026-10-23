// Full-screen opaque black that breaks up into blocks as the visor comes online (classic sci-fi visor boot):
// the view appears in pieces, mostly from the centre outwards, with blocks popping in and out a few
// times before they settle and a faint glow on blocks that have just appeared.
// Writes alpha = 1 where black, which is what hides the passthrough underlay.
// Blocks are laid out in view-angle space per eye, so both eyes agree and it is stereo-correct.
Shader "AimLink/VisorMask"
{
    Properties
    {
        _Progress ("Progress (0 black, 1 fully open)", Range(0, 1)) = 0
        _TileSize ("Block Size (tan of angle)", Float) = 0.05
        _GroupCells ("Blocks per Group", Float) = 3
        _MaxRadius ("Reveal Radius (tan of angle)", Float) = 1.6
        _RadialBias ("Centre-Out Bias", Range(0, 1)) = 0.65
        _FlickerBand ("Flicker Band", Range(0, 0.3)) = 0.12
        _Glitch ("Glitch Amount", Range(0, 1)) = 0
        _GlitchSeed ("Glitch Seed", Float) = 0
        [HDR] _RimColor ("Glow Color", Color) = (0.2, 0.9, 1, 1)
        _Rim ("Glow Strength", Range(0, 2)) = 0.6
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-1" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "VisorMask"
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
                float2 ndc : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float _Progress;
                float _TileSize;
                float _GroupCells;
                float _MaxRadius;
                float _RadialBias;
                float _FlickerBand;
                float _Glitch;
                float _GlitchSeed;
                half4 _RimColor;
                half _Rim;
            CBUFFER_END

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                // unit quad (-0.5..0.5) stretched over the whole clip rect, ignoring all transforms
                o.ndc = v.positionOS.xy * 2.0;
                o.positionCS = float4(o.ndc, UNITY_NEAR_CLIP_VALUE, 1.0);
                return o;
            }

            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 v = mul(unity_CameraInvProjection, float4(i.ndc, 1.0, 1.0));
                float2 t = v.xy / max(abs(v.z), 1e-5);     // tan of the angle from the view axis

                // horizontal bands that tear sideways while glitching
                float band = floor(t.y * 14.0);
                t.x += (Hash(float2(band, _GlitchSeed)) - 0.5) * _Glitch * 0.3;

                // blocks, grouped so neighbouring blocks tend to come in together
                float2 cell = floor(t / _TileSize);
                float2 group = floor(cell / _GroupCells);
                float2 groupCentre = (group + 0.5) * _GroupCells * _TileSize;
                float radial = saturate(length(groupCentre) / _MaxRadius);
                float order = lerp(Hash(group + 17.0), radial, _RadialBias);
                float threshold = order * 0.88 + Hash(cell) * 0.08;

                // just before a block settles it pops in and out a few times
                float p = _Progress;
                float flicker = Hash(cell + floor(_Time.y * 24.0 + Hash(cell) * 7.0) * 0.37);
                float shown = p >= threshold ? 1.0 : 0.0;
                if (p < threshold && p > threshold - _FlickerBand) shown = flicker > 0.6 ? 1.0 : 0.0;

                // a block that has just appeared glows faintly, then clears
                float age = saturate((p - threshold) / 0.05);
                float glow = step(threshold, p) * (1.0 - age) * _Rim;   // settled blocks only, not ones still flickering
                // thin seams between blocks while they are still dark, so the grid reads as pixels
                float2 inCell = frac(t / _TileSize);
                float seam = step(inCell.x, 0.06) + step(inCell.y, 0.06);
                float seamGlow = (1.0 - shown) * saturate(seam) * step(0.001, p) * 0.05;

                float black = 1.0 - shown;
                half3 col = _RimColor.rgb * (glow * 0.2 + seamGlow);
                return half4(col, black);
            }
            ENDHLSL
        }
    }
}
