// The water sheet of seas and lakes, laid on the same patches as the ground and morphing with them, displaced by the
// waves and shaded as a statistical surface. Drawn by WaterPass after the opaque scene and before the air, into the
// camera's colour and depth, over copies of both taken first: the bed shows through by refraction, nearby scenery in
// screen-space reflections, and the air's haze then lies over the water as over the ground.
Shader "MaxQ/Water" {

    Properties {

        _Detail ("Detail", 2D) = "gray" {}
        _ParentDetail ("Parent Detail", 2D) = "gray" {}
        _ParentRect ("Parent Rect", Vector) = (1, 1, 0, 0)
        _Level ("Level", Float) = 0
        _Morph ("Morph", Vector) = (0, 0, 0, 0)
        _WaterCoarse ("Coarse", Float) = 0
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

            // Under this depth (m) the water thins to a film the bed shows straight through.
            #define WATER_FILM 0.03

            // Lakes stand at least this far (m on Terra) over or under the sea, and no swell reaches them.
            #define LAKE_LEVEL 2.0

            struct WaterVaryings {

                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float2 coords : TEXCOORD2;
                float4 weights : TEXCOORD3;

                // Wind (m/s), ice, crest height (m) and the morph.
                float4 state : TEXCOORD4;

                // Sunlight and skylight arriving through the air, and the water's optics (attenuation and deep
                // reflectance), which change slowly enough across a triangle to be found at its corners.
                float3 sun : TEXCOORD5;
                float3 sky : TEXCOORD6;
                float3 attenuation : TEXCOORD7;
                float3 reflectance : TEXCOORD8;

            };

            struct WavePoint {

                float3 displacement;
                float4 weights;
                float depth;
                float height;
                SeaState sea;

            };

            // How strongly each cascade stands at a point depth metres deep: as the local sea weights it, as deep as the
            // bottom lets it, never so high that a crest climbs over the shore.
            float4 Strength(float4 weights, float depth, float shoreDistance, float height) {

                return weights * DepthFade(depth, shoreDistance) * saturate(depth / max(height, 0.05));

            }

            // The surface at a point of the sheet: the open sea's cascades as strongly as the local sea stands against
            // the camera's, as deep as the bottom lets them. Displacement in the scene (km).
            WavePoint Surface(float3 positionWS, float2 uv, float morph, float shoreDistance) {

                WavePoint wave;
                wave.depth = SampleDetail(uv, morph).waterDepth;
                wave.sea = SeaStateAt(positionWS, shoreDistance);

                // The sheet stands at its water's level, which tells a lake from the sea.
                wave.sea.swellHeight *= abs(length(positionWS - _PlanetCentre) - _PlanetRadius) * 1000.0 < LAKE_LEVEL ? 1.0 : 0.0;
                wave.height = length(float2(wave.sea.seaHeight, wave.sea.swellHeight)) * (1.0 - wave.sea.ice);
                wave.weights = CascadeWeights(wave.sea) * WaveVariation(FrameCoords(positionWS));
                wave.displacement = WaveDisplacementWS(positionWS, Strength(wave.weights, wave.depth, shoreDistance, wave.height));

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
                float3 localWS = PlacedWS(input.position);
                float morph = MorphAt(localWS);
                float3 up = normalize(localWS - _PlanetCentre);

                // The half-resolution sheet is the parent's triangles, so the odd vertices its shores and skirts still
                // use lie on their edges, waves and all; left to their own waves they open pinholes to the sky.
                float shape = max(morph, _WaterCoarse);

                // A skirt hangs below its edge vertex and moves with it.
                float3 hang = up * input.parent.w;
                WavePoint wave = Surface(localWS + hang, uv, morph, input.water.y);
                float3 displacement = wave.displacement;

                // An odd vertex's waves morph as its position does, onto the midpoint of its parent's edge, where the
                // coarser patch alongside puts them; until it starts to morph, its own waves are those. The sea there
                // is taken as the vertex's own, which changes over kilometres; the depth is read again only where the
                // bottom might hold the waves down.
                UNITY_BRANCH
                if (shape > 0.0 && any(input.morph != 0.0)) {

                    float3 endA = PlacedWS(input.position + input.parent.xyz) + hang;
                    float3 endB = PlacedWS(input.position + 2.0 * input.morph - input.parent.xyz) + hang;
                    float4 strengthA = wave.weights;
                    float4 strengthB = wave.weights;

                    UNITY_BRANCH
                    if (wave.depth < 2.0 * max(0.5 * _WaterWavelength.x, wave.height)) {

                        float2 step = ParentStep(uv);

                        strengthA = Strength(wave.weights, SampleDetail(uv + step, morph).waterDepth, input.water.y, wave.height);
                        strengthB = Strength(wave.weights, SampleDetail(uv - step, morph).waterDepth, input.water.y, wave.height);

                    }

                    displacement = lerp(displacement, 0.5 * (WaveDisplacementWS(endA, strengthA) + WaveDisplacementWS(endB, strengthB)), shape);

                }

                float3 restWS = PlacedWS(input.position + input.morph * shape);

                WaterVaryings output;
                output.positionWS = restWS + displacement;
                output.positionCS = PlacedToHClip(output.positionWS);
                output.uv = uv;
                output.coords = FrameCoords(restWS);
                output.weights = wave.weights;
                output.state = float4(wave.sea.wind, wave.sea.ice, dot(displacement, up) * 1000.0, morph);

                float3 fromCentre = output.positionWS - _PlanetCentre;
                float r = max(length(fromCentre), _PlanetRadius + 1e-3);
                float3 surfaceUp = fromCentre / length(fromCentre);
                WaterOptics optics = OpticsOf(WaterTypeAt(input.water.y / TERRA_SCALE, wave.depth / TERRA_SCALE));

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

                float2 coordsDx = ddx(input.coords);
                float2 coordsDy = ddy(input.coords);
                GroundDetail detail = SampleDetail(input.uv, input.state.w);

                SeaState sea = (SeaState)0;
                sea.wind = input.state.x;
                sea.ice = input.state.y;

                WaveSurface waves = SampleWaves(input.coords, coordsDx, coordsDy, input.weights, WhitecapCoverage(sea.wind) > 1e-5);

                // Where the detail finds the ground above the water, the ground shows: coasts as crisp as the detail.
                clip(detail.waterDepth);

                Sunlight light;
                light.up = normalize(input.positionWS - _PlanetCentre);
                light.direct = input.sun;
                light.sky = input.sky;
                light.shadow = SunShadow(input.positionWS);

                WaterLook look = LookAtWater(input.positionWS, waves, light.up);
                float3 reflected = reflect(-look.view, look.normal);
                float3 sky = SkyReflection(reflected, look.up, look.variance, light);
                float4 scenery = ScreenReflection(input.positionWS, reflected, look.up, look.variance, sky);
                float3 reflection = lerp(sky, scenery.rgb, scenery.a);

                WaterOptics optics;
                optics.attenuation = input.attenuation;
                optics.reflectance = input.reflectance;

                Refraction bed = (Refraction)0;
                bed.path = 1e5;

                UNITY_BRANCH
                if (detail.waterDepth < BED_REACH) {

                    bed = RefractedBed(input.positionCS, input.positionWS, look.normal, look.up, detail.waterDepth);

                }

                float3 body = WaterBody(optics, bed.colour, bed.path / TERRA_SCALE, detail.waterDepth / TERRA_SCALE, Underwater(light, look.up, look.variance));

                // Crests thin toward the sun glow with the light passing through them.
                float crest = saturate(input.state.z / max(0.25 * _WaterWavelength.x, 0.1));
                float structure = 0.0;

                UNITY_BRANCH
                if (waves.foam * WhitecapCoverage(sea.wind) / max(_WaterCoverage, 1e-4) > 0.01) {

                    structure = FoamStructure(input.coords, coordsDx, coordsDy);

                }

                float3 ice = 0.0;

                UNITY_BRANCH
                if (sea.ice > 0.0) {

                    ice = GroundRadiance(SEA_ICE_ALBEDO, look.up, light, Surroundings(input.uv, input.positionWS), 1.0);

                }

                float4 film = float4(bed.colour, 1.0 - saturate(detail.waterDepth / WATER_FILM));

                return float4(WaterColour(look, light, reflection, body, crest, optics, sea, structure, ice, film), 1.0);

            }

            ENDHLSL

        }

    }

}
