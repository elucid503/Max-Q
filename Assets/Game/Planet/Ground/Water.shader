// The flat water sheet of seas and lakes, laid on the same patches as the ground and morphing with them. Where the
// detail says the ground is dry, it shades as land, mirroring the ground shader, so coarse coastlines agree.
Shader "MaxQ/Water" {

    Properties {

        _Colour ("Satellite Colour", 2D) = "black" {}
        _ColourRect ("Colour Rect", Vector) = (1, 1, 0, 0)
        _Detail ("Detail", 2D) = "gray" {}
        _ParentDetail ("Parent Detail", 2D) = "gray" {}
        _ParentRect ("Parent Rect", Vector) = (1, 1, 0, 0)
        _Level ("Level", Float) = 0
        _TileOriginNear ("Near Tile Origin", Vector) = (0, 0, 0, 0)
        _TileOriginFar ("Far Tile Origin", Vector) = (0, 0, 0, 0)

    }

    SubShader {

        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+1" "RenderPipeline" = "UniversalPipeline" }

        Pass {

            Name "Water"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex GroundVertex
            #pragma fragment Frag

            #include "Ground.hlsl"

            float4 Frag(GroundVaryings input) : SV_Target {

                GroundDetail detail = SampleDetail(input.uv, input.morph);
                Sunlight light = SunlightAt(input.positionWS);
                float3 water = WaterRadiance(input.positionWS, detail.waterDepth, SatelliteColour(input.uv), light);
                float wet = Wetness(detail);

                // On a coarse patch the sheet can lie over ground the detail says is dry; shade that as land.
                if (wet >= 1.0) {

                    return float4(water, 1.0);

                }

                return float4(lerp(GroundRadiance(SatelliteColour(input.uv), detail.normalWS, light), water, wet), 1.0);

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
