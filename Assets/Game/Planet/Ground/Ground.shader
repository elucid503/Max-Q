// Terra's ground: satellite colour lit through the atmosphere with the per-patch detail normals, handing over to tiled
// materials near the camera. Where the detail says the ground lies under a water level, it shades as water, which keeps
// coastlines crisp on coarse patches.
Shader "MaxQ/Ground" {

    Properties {

        _Colour ("Satellite Colour", 2D) = "black" {}
        _ColourRect ("Colour Rect", Vector) = (1, 1, 0, 0)
        _Detail ("Detail", 2D) = "gray" {}
        _ParentDetail ("Parent Detail", 2D) = "gray" {}
        _ParentRect ("Parent Rect", Vector) = (1, 1, 0, 0)
        _Level ("Level", Float) = 0
        _TileOriginNear ("Near Tile Origin", Vector) = (0, 0, 0, 0)
        _TileOriginFar ("Far Tile Origin", Vector) = (0, 0, 0, 0)
        _TileOriginMacro ("Macro Tile Origin", Vector) = (0, 0, 0, 0)
        _TileOriginBroad ("Broad Tile Origin", Vector) = (0, 0, 0, 0)
        _WaveOrigin ("Wave Origin", Vector) = (0, 0, 0, 0)
        _WaveOriginLong ("Long Wave Origin", Vector) = (0, 0, 0, 0)
        _GroundAlbedo ("Ground Albedo", 2DArray) = "" {}
        _GroundNormal ("Ground Normals", 2DArray) = "" {}

    }

    SubShader {

        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass {

            Name "Ground"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex GroundVertex
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "GroundMaterials.hlsl"

            float4 Frag(GroundVaryings input) : SV_Target {

                float footprint = PixelFootprint(input.positionWS);
                GroundDetail detail = SampleDetail(input.uv, input.morph);
                Sunlight light = SunlightAt(input.positionWS);
                GroundSurface surface = GroundMaterial(input, detail, light.up, footprint);

                float3 ground = GroundRadiance(surface.albedo, surface.normalWS, light, Surroundings(input.uv), detail.occlusion);

                UNITY_BRANCH
                if (surface.snow > 0.01) {

                    float3 toCamera = normalize(_WorldSpaceCameraPos - input.positionWS);
                    float glitter = Glitter(input.positionOS * 1000.0, surface.normalWS, toCamera, footprint);

                    ground = lerp(ground, SnowRadiance(surface.albedo, surface.normalWS, toCamera, light, Surroundings(input.uv), detail.occlusion, glitter), surface.snow);

                }
                float wet = Wetness(detail);

                if (wet <= 0.0) {

                    return float4(ground, 1.0);

                }

                return float4(lerp(ground, WaterRadiance(input.positionOS, input.positionWS, footprint, detail.waterDepth, SatelliteColour(input.uv), light), wet), 1.0);

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

            #include "Ground.hlsl"

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

            #include "Ground.hlsl"

            float Frag(GroundVaryings input) : SV_Target {

                return input.positionCS.z;

            }

            ENDHLSL

        }

    }

}
