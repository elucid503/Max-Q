// Boulders strewn over the ground near the camera: the ground's own rock material, mapped triplanar in each boulder's
// frame so it stays put on the stone, lit as the ground is. Each boulder sits partly buried; its lower flanks see less
// sky, and the ground around them is taken as mid-grey for the light it bounces.
Shader "MaxQ/Rock" {

    Properties {

        _GroundAlbedo ("Ground Albedo", 2DArray) = "" {}
        _GroundNormal ("Ground Normals", 2DArray) = "" {}

    }

    SubShader {

        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE

        #include "Sunlight.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

        TEXTURE2D_ARRAY(_GroundAlbedo);
        TEXTURE2D_ARRAY(_GroundNormal);
        SAMPLER(sampler_GroundAlbedo);

        // Slice of the ground arrays holding rock; GroundMaterials.hlsl names the same slices.
        #define ROCK_SLICE 4

        // Metres of stone per texture repeat, and the albedo of the ground the boulders stand on.
        #define ROCK_TILE 1.6
        #define SURROUNDINGS 0.15

        // Set by URP while it renders a shadow cascade.
        float3 _LightDirection;

        struct Attributes {

            float3 position : POSITION;
            float3 normal : NORMAL;
            UNITY_VERTEX_INPUT_INSTANCE_ID

        };

        struct Varyings {

            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 positionOS : TEXCOORD1;
            float3 normalOS : TEXCOORD2;
            float3 normalWS : TEXCOORD3;
            float scale : TEXCOORD4;

        };

        Varyings Vertex(Attributes input) {

            UNITY_SETUP_INSTANCE_ID(input);

            Varyings output;
            output.positionWS = TransformObjectToWorld(input.position);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.positionOS = input.position;
            output.normalOS = input.normal;
            output.normalWS = TransformObjectToWorldNormal(input.normal);

            // Kilometres of scene per unit of mesh: the boulder's size, which scales the texture back to metres.
            output.scale = length(TransformObjectToWorldDir(float3(1.0, 0.0, 0.0), false)) * 1000.0;

            return output;

        }

        float4 ShadowVertex(Attributes input) : SV_POSITION {

            UNITY_SETUP_INSTANCE_ID(input);

            // Depth bias only: URP's normal bias moves each vertex a shadow texel inward, and a far cascade's texels are
            // tens of metres, which would turn a boulder inside out into a hill-sized shadow.
            float3 positionWS = TransformObjectToWorld(input.position);

            return ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, 0.0, _LightDirection)));

        }

        ENDHLSL

        Pass {

            Name "Rock"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            float4 Frag(Varyings input) : SV_Target {

                float3 n = normalize(input.normalOS);
                float3 weights = pow(abs(n), 4.0);
                weights /= weights.x + weights.y + weights.z;

                float3 p = input.positionOS * input.scale / ROCK_TILE;
                float3 albedo = 0.0;
                float3 normal = 0.0;

                // Triplanar with whiteout-blended normals, in the boulder's own frame.
                float3 tx = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, p.zy, ROCK_SLICE));
                float3 ty = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, p.xz, ROCK_SLICE));
                float3 tz = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, p.xy, ROCK_SLICE));

                albedo += SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, p.zy, ROCK_SLICE).rgb * weights.x;
                albedo += SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, p.xz, ROCK_SLICE).rgb * weights.y;
                albedo += SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, p.xy, ROCK_SLICE).rgb * weights.z;

                normal += float3(tx.xy + n.zy, abs(tx.z) * n.x).zyx * weights.x;
                normal += float3(ty.xy + n.xz, abs(ty.z) * n.y).xzy * weights.y;
                normal += float3(tz.xy + n.xy, abs(tz.z) * n.z) * weights.z;

                float3 normalWS = TransformObjectToWorldNormal(normalize(normal));
                Sunlight light = SunlightAt(input.positionWS);

                // The buried base sits at -0.3 of the mesh; below the waist the ground hides more and more of the sky.
                float occlusion = saturate(0.35 + 0.65 * (input.positionOS.y + 0.3) / 0.8);

                return float4(GroundRadiance(albedo, normalWS, light, SURROUNDINGS, occlusion), 1.0);

            }

            ENDHLSL

        }

        Pass {

            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ColorMask 0

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex ShadowVertex
            #pragma fragment Frag
            #pragma multi_compile_instancing

            void Frag() {

            }

            ENDHLSL

        }

        Pass {

            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ColorMask R

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Frag
            #pragma multi_compile_instancing

            float Frag(Varyings input) : SV_Target {

                return input.positionCS.z;

            }

            ENDHLSL

        }

    }

}
