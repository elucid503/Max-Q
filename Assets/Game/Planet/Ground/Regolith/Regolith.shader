// Selene's ground: cover-driven albedo (mare, fresh, steep) with Hapke lighting; the regolith texture adds detail up close.
Shader "MaxQ/Regolith" {

    Properties {

        _Detail ("Detail", 2D) = "gray" {}
        _ParentDetail ("Parent Detail", 2D) = "gray" {}
        _Cover ("Cover", 2D) = "black" {}
        _ParentCover ("Parent Cover", 2D) = "black" {}
        _ParentRect ("Parent Rect", Vector) = (1, 1, 0, 0)
        _Level ("Level", Float) = 0
        _Morph ("Morph", Vector) = (0, 0, 0, 0)
        _TileOriginNear ("Near Tile Origin", Vector) = (0, 0, 0, 0)
        _TileOriginFar ("Far Tile Origin", Vector) = (0, 0, 0, 0)
        _TileOriginMacro ("Macro Tile Origin", Vector) = (0, 0, 0, 0)
        _TileOriginBroad ("Broad Tile Origin", Vector) = (0, 0, 0, 0)
        _GroundAlbedo ("Ground Albedo", 2DArray) = "" {}
        _GroundNormal ("Ground Normals", 2DArray) = "" {}

    }

    SubShader {

        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE

        #define GROUND_STAND_IN _SeleneStandIn
        #define GROUND_CENTRE _SeleneCentre

        #include "Regolith.hlsl"
        #include "../GroundMaterials.hlsl"

        ENDHLSL

        Pass {

            Name "Regolith"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex GroundVertex
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            // Texture array slice, its mean albedo and height range (tools/regolith_texture.py), and its layout seed.
            #define REGOLITH_SLICE 6
            #define REGOLITH_MEAN 0.3
            #define REGOLITH_RELIEF 0.06
            #define REGOLITH 7

            float4 Frag(GroundVaryings input) : SV_Target {

                float footprint = PixelFootprint(input.positionWS);
                float3 metres = input.positionOS * 1000.0;
                float3 metresDx = ddx(input.positionOS) * 1000.0;
                float3 metresDy = ddy(input.positionOS) * 1000.0;
                float2 uvDx = ddx(input.uv);
                float2 uvDy = ddy(input.uv);
                GroundDetail detail = SampleDetail(input.uv, input.morph, uvDx, uvDy);

                float4 own;
                float4 parent;

                SamplePatch(TEXTURE2D_ARGS(_Cover, sampler_Detail), TEXTURE2D_ARGS(_ParentCover, sampler_Detail), input.uv, input.morph, uvDx, uvDy, own, parent);

                float4 cover = lerp(own, parent, input.morph);
                float distance = length(input.positionWS - _WorldSpaceCameraPos) * 1000.0;
                float3 up = normalize(input.positionWS - _SeleneCentre);
                float2 tone = GroundNoise(metres, TransformWorldToObjectDir(up), footprint);
                float3 albedo = RegolithAlbedo(cover.r, cover.g, cover.b) * (0.88 + 0.24 * tone.x) * (0.94 + 0.12 * tone.y);
                float3 normalWS = detail.normalWS;
                float macro = 1.0 - smoothstep(MACRO_START, MACRO_END, distance);

                UNITY_BRANCH
                if (macro > 0.0) {

                    float3 planes = pow(abs(detail.normalOS), 4.0);
                    float3 sharpPlanes = planes * planes;

                    planes /= planes.x + planes.y + planes.z;
                    sharpPlanes /= sharpPlanes.x + sharpPlanes.y + sharpPlanes.z;

                    float detailed = 1.0 - smoothstep(DETAIL_START, DETAIL_END, distance);
                    float fine = 1.0 - smoothstep(FINE_START, FINE_END, distance);
                    float3 toCameraOS = TransformWorldToObjectDir(normalize(_WorldSpaceCameraPos - input.positionWS));
                    float3 shifted = metres + ParallaxSlice(REGOLITH_SLICE, 1, REGOLITH_RELIEF, REGOLITH, metres, detail.normalOS, planes, toCameraOS, distance,
                        footprint);
                    Layer layer = SampleSlice(REGOLITH_SLICE, REGOLITH_MEAN, 1, REGOLITH, shifted, metres, metresDx, metresDy, detail.normalOS, planes, sharpPlanes,
                        detailed, fine);

                    albedo *= lerp(1.0, dot(layer.albedo, 1.0 / 3.0) / REGOLITH_MEAN, macro);
                    normalWS = normalize(lerp(detail.normalWS, TransformObjectToWorldNormal(layer.normalOS), macro));

                }

                float shadow = CascadeShadow(input.positionWS);

                return float4(RegolithRadiance(min(albedo, 0.9), normalWS, input.positionWS, shadow, detail.occlusion), 1.0);

            }

            ENDHLSL

        }

        Pass {

            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ColorMask 0

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex GroundShadowVertex
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
            #pragma vertex GroundVertex
            #pragma fragment Frag

            float Frag(GroundVaryings input) : SV_Target {

                return input.positionCS.z;

            }

            ENDHLSL

        }

    }

}
