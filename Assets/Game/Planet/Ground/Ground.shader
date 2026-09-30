// Terra's ground: its materials, placed by the climate and the lie of the land, lit through the atmosphere with the
// per-patch detail normals. Under the water sheet it is the bed, lit through the water; where the detail finds water the
// mesh is too coarse to hold a sheet for, it shades as that water.
Shader "MaxQ/Ground" {

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

            #define TREE_SHADOWS 150.0

            // The water's optics where the ground lies under it.
            WaterOptics OpticsHere(GroundVaryings input, GroundDetail detail) {

                return OpticsOf(WaterTypeAt(input.water.y / TERRA_SCALE, detail.waterDepth / TERRA_SCALE));

            }

            // The bed of an albedo under the water, lit by the sun and sky that reach down through the water. The water's
            // surface then adds what the water does on the way back up.
            float3 BedRadiance(GroundDetail detail, Sunlight light, WaterOptics optics, float3 albedo) {

                float depth = detail.waterDepth / TERRA_SCALE;
                float cosSun = saturate(dot(_SunDirection, light.up));
                float refracted = sqrt(1.0 - (1.0 - cosSun * cosSun) / (1.333 * 1.333));
                float3 sun = light.direct * light.shadow * cosSun * (1.0 - EffectiveFresnel(cosSun, 0.01)) * exp(-optics.attenuation * depth / refracted);
                float3 sky = light.sky * exp(-optics.attenuation * depth * 1.2);

                float3 lit = albedo / PI * (sun * saturate(dot(detail.normalWS, light.up)) + sky * (0.5 + 0.5 * dot(detail.normalWS, light.up)));

                // Toward BED_REACH, past which the mesh leaves the bed out and the water has none, the bed gives way to
                // what bottomless water returns from this depth, so the water looks the same where the bed ends.
                float3 bottomless = optics.reflectance * Underwater(light, light.up, 0.01) / PI * exp(-optics.attenuation * depth);

                return lerp(lit, bottomless, smoothstep(0.5 * BED_REACH, 0.9 * BED_REACH, detail.waterDepth));

            }

            // Water too small or far for the sheet, or where the coarse mesh rises through it at the coast: the same
            // surface with the waves' slopes filtered to the pixel, over the bed where light could return from it.
            float3 DistantWater(GroundVaryings input, GroundDetail detail, Sunlight light, GroundSurface surface, float2 coords, float2 coordsDx, float2 coordsDy) {

                SeaState sea = SeaStateAt(input.positionWS, input.water.y);
                float4 weights = CascadeWeights(sea) * WaveVariation(coords, max(length(coordsDx), length(coordsDy)));
                WaveSurface waves = SampleWaves(coords, coordsDx, coordsDy, weights, WhitecapCoverage(sea.wind) > 1e-5);
                WaterLook look = LookAtWater(input.positionWS, waves, light.up);
                WaterOptics optics = OpticsHere(input, detail);
                float3 reflection = SkyReflection(reflect(-look.view, look.normal), look.up, look.variance, light);
                float depth = detail.waterDepth / TERRA_SCALE;
                float3 bed = 0.0;
                float path = 1e5;

                UNITY_BRANCH
                if (detail.waterDepth < BED_REACH) {

                    bed = BedRadiance(detail, light, optics, surface.albedo);
                    path = depth / max(dot(look.view, look.up), 0.05);

                }

                float3 body = WaterBody(optics, bed, path, depth, Underwater(light, look.up, look.variance));
                float3 ice = GroundRadiance(SEA_ICE_ALBEDO, look.up, light, surface.surroundings, 1.0);
                float structure = 0.0;

                UNITY_BRANCH
                if (waves.foam * WhitecapCoverage(sea.wind) / max(_WaterCoverage, 1e-4) > 0.01) {

                    structure = FoamStructure(coords, coordsDx, coordsDy);

                }

                return WaterColour(look, light, reflection, body, 0.0, optics, sea, structure, ice, 0.0);

            }

            float4 Frag(GroundVaryings input) : SV_Target {

                float footprint = PixelFootprint(input.positionWS);
                float3 metresDx = ddx(input.positionOS) * 1000.0;
                float3 metresDy = ddy(input.positionOS) * 1000.0;
                float2 coords = FrameCoords(input.positionWS);
                float2 coordsDx = ddx(coords);
                float2 coordsDy = ddy(coords);
                float2 uvDx = ddx(input.uv);
                float2 uvDy = ddy(input.uv);
                GroundDetail detail = SampleDetail(input.uv, input.morph, uvDx, uvDy);
                Sunlight light = SunlightAt(input.positionWS, footprint / 1000.0);

                // The coast gives way to the water over at least a pixel, so it never steps along the pixels.
                float coast = max(fwidth(detail.waterDepth), 0.1);

                GroundSurface surface = GroundMaterial(input, detail, light.up, footprint, metresDx, metresDy, uvDx, uvDy);

                // Under the sheet, the bed; the sheet covers it where its level stands over the mesh. A stand-in draws no sheet.
                UNITY_BRANCH
                if (detail.waterDepth > 0.0 && input.water.x > 0.0 && GROUND_STAND_IN >= 1.0) {

                    return float4(BedRadiance(detail, light, OpticsHere(input, detail), surface.albedo), 1.0);

                }

                // Trees cast their own shadows only out to TREE_SHADOWS (Vegetation.TreeNear).
                UnderCanopy(light, surface.canopy, surface.leaves, smoothstep(0.8 * TREE_SHADOWS, 1.15 * TREE_SHADOWS, length(input.positionWS - _WorldSpaceCameraPos) * 1000.0));

                float3 ground = GroundRadiance(surface.albedo, surface.normalWS, light, surface.surroundings, detail.occlusion);

                UNITY_BRANCH
                if (surface.snow > 0.01) {

                    float3 toCamera = normalize(_WorldSpaceCameraPos - input.positionWS);
                    float glitter = Glitter(input.positionOS * 1000.0, surface.normalWS, toCamera, footprint);

                    ground = lerp(ground, SnowRadiance(surface.albedo, surface.normalWS, toCamera, light, surface.surroundings, detail.occlusion, glitter), surface.snow);

                }

                UNITY_BRANCH
                if (detail.waterDepth <= 0.0) {

                    return float4(ground, 1.0);

                }

                return float4(lerp(ground, DistantWater(input, detail, light, surface, coords, coordsDx, coordsDy), saturate(detail.waterDepth / coast)), 1.0);

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
