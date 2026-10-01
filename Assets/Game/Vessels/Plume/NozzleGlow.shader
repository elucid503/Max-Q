// A radiatively cooled nozzle extension glowing as a grey body: its triangles drawn again over the lit surface, adding
// the emission, each pixel at its hottest temperature scaled by the vertex's share of it, looked up in the blackbody
// table (see NozzleGlow.cs). Both faces glow; transparents draw after the air's composite, so the exposure is applied
// here.
Shader "MaxQ/NozzleGlow" {

    SubShader {

        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass {

            Name "NozzleGlow"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // grey-body radiance by temperature, from the table's lowest temperature to its highest
            TEXTURE2D(_Blackbody);
            SAMPLER(sampler_Blackbody);

            // hottest temperature (K), emissivity, the table's lowest and highest temperatures (K)
            float4 _NozzleGlow;

            StructuredBuffer<float> _Exposure;

            struct Attributes {

                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;

            };

            struct Varyings {

                float4 positionCS : SV_POSITION;
                float share : TEXCOORD0;

            };

            Varyings Vert(Attributes input) {

                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.share = input.uv.x;

                return output;

            }

            float4 Frag(Varyings input) : SV_Target {

                float temperature = _NozzleGlow.x * input.share;
                float u = saturate((temperature - _NozzleGlow.z) / (_NozzleGlow.w - _NozzleGlow.z));
                float3 radiance = SAMPLE_TEXTURE2D_LOD(_Blackbody, sampler_Blackbody, float2(u, 0.5), 0).rgb * _NozzleGlow.y;

                return float4(radiance * _Exposure[0], 0.0);

            }

            ENDHLSL

        }

    }

}
