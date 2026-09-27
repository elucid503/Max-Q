// Draws one patch's water depth into a shore cascade: an 8 by 8 grid of quads whose corners ShoreCascades placed in the
// cascade's square (projected on the CPU, in doubles, so the body's curve is carried), textured by the patch's detail.
Shader "Hidden/MaxQ/ShoreDepth" {

    SubShader {

        Tags { "RenderPipeline" = "UniversalPipeline" }

        ZTest Always
        ZWrite Off
        Cull Off

        Pass {

            Name "Depth"

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            #define GRID 8
            #define DETAIL_TEXELS 129.0
            #define WATER_DEPTH_RANGE 16384.0

            // Each patch's (GRID + 1)^2 corners as cascade coordinates, -1 to 1 across its square.
            StructuredBuffer<float2> _ShorePoints;
            int _ShoreBase;

            Texture2D _Detail;
            SamplerState sampler_linear_clamp;

            struct Varyings {

                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;

            };

            static const uint2 Corners[6] = { uint2(0, 0), uint2(1, 0), uint2(0, 1), uint2(1, 0), uint2(1, 1), uint2(0, 1) };

            Varyings Vert(uint id : SV_VertexID) {

                uint quad = id / 6;
                uint2 grid = uint2(quad % GRID, quad / GRID) + Corners[id % 6];
                float2 square = _ShorePoints[_ShoreBase + grid.y * (GRID + 1) + grid.x];

                // Row 0 of the cascade is its southern edge, whichever way up the target is stored.
                #if UNITY_UV_STARTS_AT_TOP
                square.y = -square.y;
                #endif

                Varyings output;
                output.positionCS = float4(square, 0.5, 1.0);
                output.uv = grid / (float)GRID;

                return output;

            }

            // The signed water depth the patch's detail holds (metres over the bed, negative on land).
            float Frag(Varyings input) : SV_Target {

                float2 texel = (input.uv * (DETAIL_TEXELS - 1.0) + 0.5) / DETAIL_TEXELS;
                float encoded = _Detail.SampleLevel(sampler_linear_clamp, texel, 0.0).b * 2.0 - 1.0;

                return sign(encoded) * encoded * encoded * WATER_DEPTH_RANGE;

            }

            ENDHLSL

        }

    }

}
