// Terra's ground: satellite colour lit through the atmosphere with the per-patch detail normals, handing over to tiled
// materials near the camera. Under the water sheet it is the bed, its colour recovered from the satellite's and lit
// through the water; where the detail finds water the mesh is too coarse to hold a sheet for, it shades as that water.
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
        _WaterDetail ("Water Detail", 2D) = "gray" {}
        _ParentWaterDetail ("Parent Water Detail", 2D) = "gray" {}
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
            #include "../Water/Surface/WaterSurface.hlsl"
            #include "../Water/Shore/Shore.hlsl"

            // The water's optics where the ground lies under it.
            WaterOptics OpticsHere(GroundVaryings input, GroundDetail detail, SeaState sea, WaterDetail water) {

                return OpticsOf(WaterTypeAt(sea.latitude, input.water.y / TERRA_SCALE, detail.waterDepth / TERRA_SCALE, saturate(length(water.flow))));

            }

            // The bed under the water: the satellite's colour with the water it saw undone, lit by the sun and sky that
            // reach down through the water. The water's surface then adds what the water does on the way back up.
            float3 BedRadiance(GroundVaryings input, GroundDetail detail, Sunlight light, WaterOptics optics) {

                float depth = detail.waterDepth / TERRA_SCALE;
                float3 albedo = InvertBed(SatelliteColour(input.uv), optics, depth);
                float cosSun = saturate(dot(_SunDirection, light.up));
                float refracted = sqrt(1.0 - (1.0 - cosSun * cosSun) / (1.333 * 1.333));
                float3 sun = light.direct * light.shadow * cosSun * (1.0 - EffectiveFresnel(cosSun, 0.01)) * exp(-optics.attenuation * depth / refracted);
                float3 sky = light.sky * exp(-optics.attenuation * depth * 1.2);

                return albedo / PI * (sun * saturate(dot(detail.normalWS, light.up)) + sky * (0.5 + 0.5 * dot(detail.normalWS, light.up)));

            }

            // Water too narrow or far for the sheet, or where the coarse mesh rises through it at the coast: the same
            // surface with the waves' slopes filtered to the pixel, over the bed where light could return from it.
            float3 DistantWater(GroundVaryings input, GroundDetail detail, Sunlight light, float2 coords, float2 coordsDx, float2 coordsDy) {

                SeaState sea = SeaStateAt(input.positionWS);
                WaterDetail water = SampleWaterDetail(input.uv, input.morph);
                float depth = detail.waterDepth / TERRA_SCALE;
                float rapids = Rapids(length(water.flow), depth);
                float4 weights = RapidsWeights(CascadeWeights(sea, water.seaShelter, water.swellShelter) * WaveVariation(coords), rapids);
                WaveSurface waves = SampleWaves(coords, coordsDx, coordsDy, weights, 0.0, WhitecapCoverage(sea.wind) > 1e-5);
                WaterLook look = LookAtWater(input.positionWS, waves, light.up);
                WaterOptics optics = OpticsHere(input, detail, sea, water);
                float3 reflection = SkyReflection(reflect(-look.view, look.normal), look.up, look.variance, light);
                float3 bed = 0.0;
                float path = 1e5;

                UNITY_BRANCH
                if (detail.waterDepth < BED_REACH) {

                    bed = BedRadiance(input, detail, light, optics);
                    path = depth / max(dot(look.view, look.up), 0.05);

                }

                float3 body = WaterBody(optics, bed, path, depth, Underwater(light, look.up, look.variance));
                float3 ice = GroundRadiance(SeaIceAlbedo(SatelliteColour(input.uv)), look.up, light, Surroundings(input.uv), 1.0);
                float height = length(float2(sea.seaHeight * water.seaShelter, sea.swellHeight * water.swellShelter)) * (1.0 - sea.ice);

                return WaterColour(look, light, reflection, body, 0.0, optics, sea, max(SurfFoam(detail.waterDepth, height), RAPIDS_FOAM * rapids), float3(FoamStructure(coords).xx, 1.0), ice);

            }

            float4 Frag(GroundVaryings input) : SV_Target {

                float footprint = PixelFootprint(input.positionWS);
                float2 coords = FrameCoords(input.positionWS);
                float2 coordsDx = ddx(coords);
                float2 coordsDy = ddy(coords);
                GroundDetail detail = SampleDetail(input.uv, input.morph);
                Sunlight light = SunlightAt(input.positionWS);

                // Under the sheet, the bed; the sheet covers it where its level stands over the mesh.
                UNITY_BRANCH
                if (detail.waterDepth > 0.0 && input.water.x > 0.0) {

                    WaterOptics optics = OpticsHere(input, detail, SeaStateAt(input.positionWS), SampleWaterDetail(input.uv, input.morph));

                    return float4(BedRadiance(input, detail, light, optics), 1.0);

                }

                GroundSurface surface = GroundMaterial(input, detail, light.up, footprint);

                float3 ground = GroundRadiance(surface.albedo, surface.normalWS, light, Surroundings(input.uv), detail.occlusion);

                UNITY_BRANCH
                if (surface.snow > 0.01) {

                    float3 toCamera = normalize(_WorldSpaceCameraPos - input.positionWS);
                    float glitter = Glitter(input.positionOS * 1000.0, surface.normalWS, toCamera, footprint);

                    ground = lerp(ground, SnowRadiance(surface.albedo, surface.normalWS, toCamera, light, Surroundings(input.uv), detail.occlusion, glitter), surface.snow);

                }

                UNITY_BRANCH
                if (detail.waterDepth <= 0.0) {

                    return float4(ground, 1.0);

                }

                return float4(lerp(ground, DistantWater(input, detail, light, coords, coordsDx, coordsDy), saturate(detail.waterDepth / 0.1)), 1.0);

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
