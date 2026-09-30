// The air and clouds over the opaque scene: marched at half resolution, split at the clouds so far ones fade into the air
// before them, then upsampled by depth so silhouettes stay sharp; sky pixels get the sun's disk.
Shader "Hidden/MaxQ/AtmosphereSky" {

    SubShader {

        ZTest Always
        ZWrite Off
        Cull Off

        HLSLINCLUDE

        #include "Atmosphere.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        #include "ViewRay.hlsl"
        #include "HorizonGlow.hlsl"

        TEXTURE2D_X(_AtmosphereInscatter);
        TEXTURE2D_X(_AtmosphereTransmittance);
        float4 _AtmosphereSize;
        StructuredBuffer<float> _Exposure;

        // The clouds' light and transmittance, and their distance and the ground's; _CloudSize is zero without clouds.
        TEXTURE2D(_CloudLight);
        TEXTURE2D(_CloudDepth);
        float4 _CloudSize;

        // This frame's step through the golden-ratio sequence, which walks each ray's jitter so the antialiasing
        // averages it rather than printing it as a fixed pattern.
        float _AtmosphereJitter;

        ENDHLSL

        Pass {

            Name "March"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE

            struct Output {

                float4 inscatter : SV_Target0;
                float4 transmittance : SV_Target1;

            };

            Output Frag(Varyings input) {

                // Exactly the block's top-left pixel: a texel centre between pixels rounds differently by row and draws a lattice.
                int2 pixel = int2(input.positionCS.xy) * 2;
                ViewRay ray = ViewRayThrough((pixel + 0.5) * _SceneSize.zw, LOAD_TEXTURE2D_X(_SceneDepth, pixel).r);

                // Interleaved gradient noise staggers neighbouring rays' cascade samples, so terrain's shafts blend instead of
                // banding. The shadowed stretch has SHADOW_STEPS of its own, so the rest of the ray needs fewer.
                float jitter = frac(52.9829189 * frac(dot(input.positionCS.xy, float2(0.06711056, 0.00583715))) + _AtmosphereJitter);
                float4 clouds = float4(0.0, 0.0, 0.0, 1.0);
                float cloudDepth = 1e9;

                // The clouds' history is at this pass's resolution, traced through the same half-resolution pixels.
                if (_CloudSize.x > 0.0) {

                    int2 at = min(int2(input.positionCS.xy), int2(_CloudSize.xy) - 1);

                    clouds = LOAD_TEXTURE2D(_CloudLight, at);
                    cloudDepth = LOAD_TEXTURE2D(_CloudDepth, at).r;

                }

                Scattering scattering = Integrate(_WorldSpaceCameraPos - _PlanetCentre, ray.direction, ray.reach, _SunDirection, 16, true, jitter, true, true,
                    cloudDepth);
                float3 front = scattering.frontRadiance * _SunIlluminance;

                // Air before the clouds, the clouds through it, and the air behind them through both.
                Output output;
                output.inscatter = float4(front + scattering.frontTransmittance * clouds.rgb + clouds.a * (scattering.radiance * _SunIlluminance - front), ray.distance);
                output.transmittance = float4(scattering.transmittance * clouds.a, 1.0);

                return output;

            }

            ENDHLSL

        }

        Pass {

            Name "Sky View"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target {

                float3 fromCentre = _WorldSpaceCameraPos - _PlanetCentre;
                float viewHeight = max(length(fromCentre), _PlanetRadius + 1e-3);
                float3 up = fromCentre / length(fromCentre);
                float3 forward;
                float3 side;
                float cosZenith;
                float cosLight;

                SkyFrame(up, forward, side);
                SkyViewDirection(float2(TexelToUnit(input.texcoord.x, SKY_VIEW_SIZE.x), TexelToUnit(input.texcoord.y, SKY_VIEW_SIZE.y)), viewHeight, cosZenith, cosLight);

                float sinZenith = sqrt(saturate(1.0 - cosZenith * cosZenith));
                float sinLight = sqrt(saturate(1.0 - cosLight * cosLight));
                float3 direction = up * cosZenith + (forward * cosLight + side * sinLight) * sinZenith;

                return float4(Integrate(up * viewHeight, direction, 1e9, _SunDirection, 32, true).radiance * _SunIlluminance, 1.0);

            }

            ENDHLSL

        }

        Pass {

            Name "Composite"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            // The sun seen from Terra spans the same half degree it does from Earth.
            #define SUN_COS_RADIUS 0.99998931

            // The eye adapts locally to a body standing in the sky as it looks at it: however far the exposure climbs for
            // the night ground round the camera, the body and its air keep at most this many stops over daylight's, so a
            // sunlit planet in the lunar night shows its seas, land and clouds rather than a white disk.
            #define STAND_IN_STOPS 0.5

            // The sine of the angle the air's planet spans below which, seen from afar, its pixels march at full resolution.
            #define SMALL_PLANET 0.15

            // A far planet, standing in or not, is too few pixels across for the half-resolution march to draw its limb or
            // its air's halo, so its pixels march their own air at full resolution through the clouds' history, read
            // bilinearly. However far the camera, the planet covers little of the view, so this costs little.
            bool OnSmallPlanet(float3 direction) {

                float3 origin = _WorldSpaceCameraPos - _PlanetCentre;
                float2 air = RaySphere(origin, direction, TopRadius() * 1.02);

                return TopRadius() < SMALL_PLANET * length(origin) && air.y > max(air.x, 0.0);

            }

            void MarchStandIn(float2 uv, float2 positionCS, ViewRay ray, out float3 inscatter, out float3 transmittance) {

                float4 clouds = float4(0.0, 0.0, 0.0, 1.0);
                float cloudDepth = 1e9;

                if (_CloudSize.x > 0.0) {

                    // History texel i holds the ray through pixel 2i.
                    float2 at = ((uv * _SceneSize.xy - 0.5) * 0.5 + 0.5) * _CloudSize.zw;

                    clouds = SAMPLE_TEXTURE2D_LOD(_CloudLight, sampler_LinearClamp, at, 0);
                    cloudDepth = SAMPLE_TEXTURE2D_LOD(_CloudDepth, sampler_LinearClamp, at, 0).r;

                }

                float jitter = frac(52.9829189 * frac(dot(positionCS, float2(0.06711056, 0.00583715))) + _AtmosphereJitter);
                Scattering scattering = Integrate(_WorldSpaceCameraPos - _PlanetCentre, ray.direction, ray.reach, _SunDirection, 16, true, jitter, true, true,
                    cloudDepth);
                float3 front = scattering.frontRadiance * _SunIlluminance;

                inscatter = front + scattering.frontTransmittance * clouds.rgb + clouds.a * (scattering.radiance * _SunIlluminance - front);
                transmittance = scattering.transmittance * clouds.a;

            }

            float4 Frag(Varyings input) : SV_Target {

                float2 uv = input.texcoord;
                float3 scene = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, uv, 0).rgb;
                ViewRay ray = ViewRayAt(uv);
                float standIn = max(StandInCover(ray.direction, _TerraStandInSphere), StandInCover(ray.direction, _SeleneStandInSphere));
                float3 inscatter = 0.0;
                float3 transmittance = 0.0;

                UNITY_BRANCH
                if (standIn > 0.0 || OnSmallPlanet(ray.direction)) {

                    MarchStandIn(uv, input.positionCS.xy, ray, inscatter, transmittance);

                } else {

                    // The surface's own slope in distance: the gentler side, as a silhouette's steep side belongs behind it.
                    float2 pixel = _SceneSize.zw;
                    float slopeX = min(abs(ViewRayAt(uv + float2(pixel.x, 0.0)).distance - ray.distance), abs(ViewRayAt(uv - float2(pixel.x, 0.0)).distance - ray.distance));
                    float slopeY = min(abs(ViewRayAt(uv + float2(0.0, pixel.y)).distance - ray.distance), abs(ViewRayAt(uv - float2(0.0, pixel.y)).distance - ray.distance));
                    float tolerance = 1e-3 * ray.distance + 3.0 * max(slopeX, slopeY);

                    // Bilinear over the nearest samples (sample i marched through pixel 2i), weighed down where their
                    // distance strays beyond the slope, so air from behind a ridge never bleeds onto it.
                    float2 texel = (uv * _SceneSize.xy - 0.5) * 0.5;
                    int2 base = int2(floor(texel));
                    float2 f = texel - base;
                    float total = 0.0;

                    for (int i = 0; i < 4; i++) {

                        int2 offset = int2(i & 1, i >> 1);
                        int2 at = clamp(base + offset, 0, int2(_AtmosphereSize.xy) - 1);
                        float4 marched = LOAD_TEXTURE2D_X(_AtmosphereInscatter, at);
                        float bilinear = (offset.x ? f.x : 1.0 - f.x) * (offset.y ? f.y : 1.0 - f.y);
                        float weight = (bilinear + 1e-4) / (1.0 + abs(marched.a - ray.distance) / max(tolerance, 1e-6));

                        inscatter += marched.rgb * weight;
                        transmittance += LOAD_TEXTURE2D_X(_AtmosphereTransmittance, at).rgb * weight;
                        total += weight;

                    }

                    inscatter /= total;
                    transmittance /= total;

                }

                float exposure = lerp(_Exposure[0], min(_Exposure[0], exp2(STAND_IN_STOPS)), standIn);

                if (!ray.sky) {

                    return float4((scene * transmittance + inscatter) * exposure, 1.0);

                }

                // Stars fade as the sky brightens; Selene's horizon glow adds where there is no air.
                float glare = dot(inscatter, float3(0.2126, 0.7152, 0.0722));
                float3 background = scene * exp(-glare * 40.0) + HorizonGlow(ray.direction);
                float3 origin = _WorldSpaceCameraPos - _PlanetCentre;
                float cosSun = dot(ray.direction, _SunDirection);

                if (cosSun > SUN_COS_RADIUS && RaySphere(origin, ray.direction, _PlanetRadius).x < 0.0) {

                    float edge = saturate((1.0 - cosSun) / (1.0 - SUN_COS_RADIUS));
                    float limb = 1.0 - 0.6 * (1.0 - sqrt(1.0 - edge));
                    float solidAngle = 2.0 * PI * (1.0 - SUN_COS_RADIUS);

                    background += _SunIlluminance / solidAngle * limb;

                }

                return float4((background * transmittance + inscatter) * exposure, 1.0);

            }

            ENDHLSL

        }

    }

}
