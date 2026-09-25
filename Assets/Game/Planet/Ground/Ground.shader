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

            #include "GroundMaterials.hlsl"

            float4 Frag(GroundVaryings input) : SV_Target {

                GroundDetail detail = SampleDetail(input.uv, input.morph);
                Sunlight light = SunlightAt(input.positionWS);
                GroundSurface surface = GroundMaterial(input, detail, light.up);

                float3 ground = GroundRadiance(surface.albedo, surface.normalWS, light);
                float wet = Wetness(detail);

                if (wet <= 0.0) {

                    return float4(ground, 1.0);

                }

                return float4(lerp(ground, WaterRadiance(input.positionWS, detail.waterDepth, SatelliteColour(input.uv), light), wet), 1.0);

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
