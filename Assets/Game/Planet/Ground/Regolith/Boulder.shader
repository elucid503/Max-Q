// Selene's boulders and pebbles: fresh stone, dusted and buried in regolith, lit as the ground is.
Shader "MaxQ/Boulder" {

    Properties {

        _GroundAlbedo ("Ground Albedo", 2DArray) = "" {}
        _GroundNormal ("Ground Normals", 2DArray) = "" {}

    }

    SubShader {

        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE

        #include "Regolith.hlsl"
        #include "../RockInstances.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

        TEXTURE2D_ARRAY(_GroundAlbedo);
        TEXTURE2D_ARRAY(_GroundNormal);
        SAMPLER(sampler_GroundAlbedo);

        // Rock slice of the ground arrays and its mean albedo.
        #define ROCK_SLICE 4
        #define ROCK_MEAN 0.069

        // Metres of stone per texture repeat.
        #define ROCK_TILE 1.6

        // Brightness of bare stone and bedrock blocks over fresh regolith.
        #define STONE_GAIN 1.35
        #define BLOCK_GAIN 1.15

        struct Varyings {

            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 positionOS : TEXCOORD1;
            float3 normalOS : TEXCOORD2;
            nointerpolation float4 turn : TEXCOORD3;
            nointerpolation float3 regolith : TEXCOORD4;
            nointerpolation float3 stone : TEXCOORD5;
            nointerpolation float scale : TEXCOORD6;

        };

        Varyings Vertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID) {

            RockInstance rock = LoadRock(vertex, instance);

            Varyings output;
            output.positionWS = rock.positionWS;
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.positionOS = rock.positionOS;
            output.normalOS = rock.normalOS;
            output.turn = rock.turn;

            output.regolith = RegolithAlbedo(rock.pick.y, rock.pick.z);
            output.stone = RegolithAlbedo(rock.pick.y, 1.0) * lerp(STONE_GAIN, BLOCK_GAIN, rock.pick.w);
            output.scale = rock.scale;

            return output;

        }

        ENDHLSL

        Pass {

            Name "Boulder"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            float4 Frag(Varyings input) : SV_Target {

                float3 n = normalize(input.normalOS);
                float3 weights = pow(abs(n), 4.0);
                weights /= weights.x + weights.y + weights.z;

                float3 p = input.positionOS * input.scale / ROCK_TILE;
                float4 grain = 0.0;
                float3 normal = 0.0;

                // Triplanar with whiteout-blended normals, in the rock's own frame.
                float3 tx = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, p.zy, ROCK_SLICE));
                float3 ty = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, p.xz, ROCK_SLICE));
                float3 tz = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, p.xy, ROCK_SLICE));

                grain += SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, p.zy, ROCK_SLICE) * weights.x;
                grain += SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, p.xz, ROCK_SLICE) * weights.y;
                grain += SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, p.xy, ROCK_SLICE) * weights.z;

                normal += float3(tx.xy + n.zy, abs(tx.z) * n.x).zyx * weights.x;
                normal += float3(ty.xy + n.xz, abs(ty.z) * n.y).xzy * weights.y;
                normal += float3(tz.xy + n.xy, abs(tz.z) * n.z) * weights.z;

                float3 stone = input.stone * dot(grain.rgb, float3(0.2126, 0.7152, 0.0722)) / ROCK_MEAN;

                // Dust on upward faces and pits; the foot is buried.
                float3 normalWS = normalize(Rotate(input.turn, normalize(normal)));
                float3 up = normalize(input.positionWS - _SeleneCentre);
                float dust = saturate((dot(normalWS, up) - 0.55) / 0.3) * saturate(1.3 - 2.0 * grain.a);
                float foot = saturate((-0.12 - input.positionOS.y) / 0.18 + (0.5 - grain.a) * 0.6);
                float3 albedo = min(lerp(stone, input.regolith, saturate(0.7 * dust + foot)), 0.9);

                // The buried base sits at -0.3 of the mesh.
                float occlusion = saturate(0.35 + 0.65 * (input.positionOS.y + 0.3) / 0.8);

                return float4(RegolithRadiance(albedo, normalWS, input.positionWS, CascadeShadow(input.positionWS), occlusion), 1.0);

            }

            ENDHLSL

        }

        Pass {

            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ColorMask 0

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex RockShadowVertex
            #pragma fragment Frag

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

            float Frag(Varyings input) : SV_Target {

                return input.positionCS.z;

            }

            ENDHLSL

        }

    }

}
