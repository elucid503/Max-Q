// The water sheet of seas, lakes and rivers, laid on the same patches as the ground and morphing with them, displaced by
// the waves and shaded as a statistical surface. Drawn by WaterPass after the opaque scene and before the air, into
// the camera's colour and depth, over copies of both taken first: the bed shows through by refraction, nearby scenery
// in screen-space reflections, and the air's haze then lies over the water as over the ground.
Shader "MaxQ/Water" {

    Properties {

        _Colour ("Satellite Colour", 2D) = "black" {}
        _ColourRect ("Colour Rect", Vector) = (1, 1, 0, 0)
        _Detail ("Detail", 2D) = "gray" {}
        _ParentDetail ("Parent Detail", 2D) = "gray" {}
        _WaterDetail ("Water Detail", 2D) = "gray" {}
        _ParentWaterDetail ("Parent Water Detail", 2D) = "gray" {}
        _ParentRect ("Parent Rect", Vector) = (1, 1, 0, 0)
        _Level ("Level", Float) = 0
        _TileOriginNear ("Near Tile Origin", Vector) = (0, 0, 0, 0)
        _TileOriginFar ("Far Tile Origin", Vector) = (0, 0, 0, 0)
        _TileOriginMacro ("Macro Tile Origin", Vector) = (0, 0, 0, 0)
        _TileOriginBroad ("Broad Tile Origin", Vector) = (0, 0, 0, 0)

    }

    SubShader {

        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+1" "RenderPipeline" = "UniversalPipeline" }

        Pass {

            Name "Water"
            Tags { "LightMode" = "MaxQWater" }

            ZWrite On
            ZTest LEqual
            Blend Off

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex WaterVertex
            #pragma fragment WaterFragment

            // Terrain's shadows on water only dim its glint and body, which a single filtered tap of each cascade serves.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE

            #include "../../Ground/Ground.hlsl"
            #include "WaterSurface.hlsl"
            #include "WaterScene.hlsl"
            #include "../Shore/Shore.hlsl"

            struct WaterVaryings {

                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float2 coords : TEXCOORD2;
                float4 weights : TEXCOORD3;

                // The sea where the vertex lies: wind (m/s), ice, latitude (radians) and significant height (m).
                float4 sea : TEXCOORD4;

                // Shore distance (m), crest height (m), how much of the water is in rapids, and the morph.
                float4 water : TEXCOORD5;

                // Sunlight and skylight arriving through the air, and the water's optics (attenuation and deep
                // reflectance), which change slowly enough across a triangle to be found at its corners.
                float3 sun : TEXCOORD6;
                float3 sky : TEXCOORD7;
                float3 attenuation : TEXCOORD8;
                float3 reflectance : TEXCOORD9;

                // A river's current, m/s along the wave frame's axes.
                float2 flow : TEXCOORD10;

            };

            // The sea's local significant height (m): the wind's sea and swell, as far as the land lets them in and
            // the ice lets them stand.
            float LocalHeight(SeaState sea, WaterDetail water) {

                return length(float2(sea.seaHeight * water.seaShelter, sea.swellHeight * water.swellShelter)) * (1.0 - sea.ice);

            }

            struct WavePoint {

                float3 displacement;
                float3 breaker;
                float4 weights;
                float depth;
                float height;
                float rapids;
                SeaState sea;
                WaterDetail water;

            };

            // How strongly each cascade stands at a point depth metres deep, felt as felt metres: as the local sea
            // weights it, as deep as the bottom lets it, never so high that a crest climbs over the shore.
            float4 Strength(float4 weights, float depth, float felt, float shoreDistance, float height) {

                return weights * DepthFade(felt, shoreDistance) * saturate(depth / max(height, 0.05));

            }

            // The surface at a point of the sheet: the open sea's cascades as strongly as the local sea stands against
            // the camera's, or rapids raise them, as deep as the bottom lets them (the shallowest water upwave, so the
            // lee of a reef is calm); and the waves breaking on the shore, which the longest cascade gives way to.
            // Displacement in the scene (km), the breakers' apart.
            WavePoint Surface(float3 positionWS, float2 uv, float morph, float shoreDistance) {

                GroundDetail detail = SampleDetail(uv, morph);

                WavePoint wave;
                wave.depth = detail.waterDepth;
                wave.water = SampleWaterDetail(uv, morph);
                wave.sea = SeaStateAt(positionWS);

                // The shore shelters and breaks only waves that feel the bottom, in water under half their length.
                Shore shore = (Shore)0;

                UNITY_BRANCH
                if (detail.waterDepth < 0.5 * _WaterWavelength.x) {

                    shore = ShoreAt(positionWS);

                }

                float3 up = normalize(positionWS - _PlanetCentre);
                float felt = lerp(detail.waterDepth, min(detail.waterDepth, shore.upwave), shore.weight);

                wave.height = LocalHeight(wave.sea, wave.water);

                Breaker breaker = BreakerAt(positionWS, shore, detail.waterDepth, wave.height, up);

                wave.weights = CascadeWeights(wave.sea, wave.water.seaShelter, wave.water.swellShelter) * WaveVariation(FrameCoords(positionWS));
                wave.rapids = Rapids(length(wave.water.flow), detail.waterDepth / TERRA_SCALE);
                wave.weights = RapidsWeights(wave.weights, wave.rapids);
                wave.weights.x *= 1.0 - 0.8 * breaker.presence;
                wave.displacement = WaveDisplacementWS(positionWS, Strength(wave.weights, detail.waterDepth, felt, shoreDistance, wave.height));
                wave.breaker = (up * breaker.height + shore.toward * breaker.push) / 1000.0;

                return wave;

            }

            // The end of the parent's edge an odd vertex morphs onto, in patch texture coordinates (see PatchJob).
            float2 ParentStep(float2 uv) {

                int i = (int)round(uv.x * 32.0);
                int j = (int)round(uv.y * 32.0);
                bool oddI = (i & 1) == 1;
                bool oddJ = (j & 1) == 1;

                return (oddI && oddJ ? float2(1.0, -1.0) : oddI ? float2(-1.0, 0.0) : float2(0.0, -1.0)) / 32.0;

            }

            WaterVaryings WaterVertex(GroundAttributes input) {

                bool skirt = input.uv.x > 1.5;
                float2 uv = float2(skirt ? input.uv.x - SKIRT_FLAG : input.uv.x, input.uv.y);
                float3 localWS = TransformObjectToWorld(input.position);
                float morph = MorphAt(localWS);
                float3 up = normalize(localWS - _PlanetCentre);

                // A skirt hangs below its edge vertex and moves with it.
                float3 hang = up * input.parent.w;
                WavePoint wave = Surface(localWS + hang, uv, morph, input.water.y);
                float3 displacement = wave.displacement;

                // An odd vertex's waves morph as its position does, onto the midpoint of its parent's edge, where the
                // coarser patch alongside puts them; until it starts to morph, its own waves are those. The sea there
                // is taken as the vertex's own, which changes over kilometres; the depth is read again only where the
                // bottom might hold the waves down.
                UNITY_BRANCH
                if (morph > 0.0 && any(input.morph != 0.0)) {

                    float3 endA = TransformObjectToWorld(input.position + input.parent.xyz) + hang;
                    float3 endB = TransformObjectToWorld(input.position + 2.0 * input.morph - input.parent.xyz) + hang;
                    float4 strengthA = wave.weights;
                    float4 strengthB = wave.weights;

                    UNITY_BRANCH
                    if (wave.depth < 2.0 * max(0.5 * _WaterWavelength.x, wave.height)) {

                        float2 step = ParentStep(uv);
                        float depthA = SampleDetail(uv + step, morph).waterDepth;
                        float depthB = SampleDetail(uv - step, morph).waterDepth;

                        strengthA = Strength(wave.weights, depthA, depthA, input.water.y, wave.height);
                        strengthB = Strength(wave.weights, depthB, depthB, input.water.y, wave.height);

                    }

                    displacement = lerp(displacement, 0.5 * (WaveDisplacementWS(endA, strengthA) + WaveDisplacementWS(endB, strengthB)), morph);

                }

                displacement += wave.breaker;

                float3 restWS = TransformObjectToWorld(input.position + input.morph * morph);

                WaterVaryings output;
                output.positionWS = restWS + displacement;
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = uv;
                output.coords = FrameCoords(restWS);
                output.weights = wave.weights;
                output.sea = float4(wave.sea.wind, wave.sea.ice, wave.sea.latitude, wave.height);
                output.water = float4(input.water.y, dot(displacement, up) * 1000.0, wave.rapids, morph);
                output.flow = FlowToFrame(restWS, wave.water.flow);

                float3 fromCentre = output.positionWS - _PlanetCentre;
                float r = max(length(fromCentre), _PlanetRadius + 1e-3);
                float3 surfaceUp = fromCentre / length(fromCentre);
                WaterOptics optics = OpticsOf(WaterTypeAt(wave.sea.latitude, input.water.y / TERRA_SCALE, wave.depth / TERRA_SCALE, saturate(length(wave.water.flow))));

                output.sun = _SunIlluminance * SunTransmittance(surfaceUp * r, _SunDirection) * FogSunTransmittance(fromCentre, _SunDirection);
                output.sky = _SunIlluminance * SkyIrradiance(r, dot(surfaceUp, _SunDirection));
                output.attenuation = optics.attenuation;
                output.reflectance = optics.reflectance;

                return output;

            }

            // Depth is tested and written before shading, even where the coast discards a pixel: a hidden wave face
            // then costs nothing, and a discarded pixel keeps the ground's colour under a surface within centimetres of it.
            [earlydepthstencil]
            float4 WaterFragment(WaterVaryings input) : SV_Target {

                float morph = input.water.w;
                GroundDetail detail = SampleDetail(input.uv, morph);
                WaveSurface waves = SampleWaves(input.coords, ddx(input.coords), ddy(input.coords), input.weights, input.flow, WhitecapCoverage(input.sea.x) > 1e-5);

                // Where the detail finds the ground above the water, the ground shows: coasts as crisp as the detail.
                clip(detail.waterDepth);


                Sunlight light;
                light.up = normalize(input.positionWS - _PlanetCentre);
                light.direct = input.sun;
                light.sky = input.sky;
                light.shadow = SunShadow(input.positionWS);

                SeaState sea = (SeaState)0;
                sea.wind = input.sea.x;
                sea.ice = input.sea.y;
                sea.latitude = input.sea.z;

                float height = input.sea.w;
                Shore shore = (Shore)0;
                Breaker breaker = (Breaker)0;

                // Only water shallow enough for its waves to break can hold breakers.
                UNITY_BRANCH
                if (detail.waterDepth < 2.0 * height / BREAKING_INDEX + 1.0) {

                    shore = ShoreAt(input.positionWS);
                    breaker = BreakerAt(input.positionWS, shore, detail.waterDepth, height, light.up);

                    // The breakers tilt the surface toward and away from the land.
                    waves.slope -= float2(dot(shore.toward, _WaterEast), dot(shore.toward, _WaterNorth)) * breaker.slope;

                }

                WaterLook look = LookAtWater(input.positionWS, waves, light.up);
                float3 reflected = reflect(-look.view, look.normal);
                float3 sky = SkyReflection(reflected, look.up, look.variance, light);
                float4 scenery = ScreenReflection(input.positionWS, reflected, look.up, look.variance, sky);
                float3 reflection = lerp(sky, scenery.rgb, scenery.a);

                float depth = detail.waterDepth / TERRA_SCALE;
                WaterOptics optics;
                optics.attenuation = input.attenuation;
                optics.reflectance = input.reflectance;

                Refraction bed = (Refraction)0;
                bed.path = 1e5;

                UNITY_BRANCH
                if (detail.waterDepth < BED_REACH) {

                    bed = RefractedBed(input.positionCS, input.positionWS, look.normal, look.up, detail.waterDepth);

                }

                float3 body = WaterBody(optics, bed.colour, bed.path / TERRA_SCALE, depth, Underwater(light, look.up, look.variance));

                // Crests thin toward the sun glow with the light passing through them.
                float crest = saturate(input.water.y / max(0.25 * _WaterWavelength.x, 0.1));
                float surf = max(lerp(SurfFoam(detail.waterDepth, height), breaker.foam, shore.weight), RAPIDS_FOAM * input.water.z);
                float3 structure = 0.0;

                UNITY_BRANCH
                if (max(waves.foam * WhitecapCoverage(sea.wind) / max(_WaterCoverage, 1e-4), surf) > 0.01) {

                    structure = FoamStructures(input.coords, input.flow);

                }

                float3 ice = 0.0;

                UNITY_BRANCH
                if (sea.ice > 0.0) {

                    ice = GroundRadiance(SeaIceAlbedo(SatelliteColour(input.uv)), look.up, light, Surroundings(input.uv), 1.0);

                }

                return float4(WaterColour(look, light, reflection, body, crest, optics, sea, surf, structure, ice), 1.0);

            }

            ENDHLSL

        }

    }

}
