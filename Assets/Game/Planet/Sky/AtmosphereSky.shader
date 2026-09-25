// Composites the atmosphere over the opaque scene. The air varies slowly, so each view ray is marched at half resolution
// to the depth buffer (or out of the air); the full-resolution pass then upsamples by depth, so silhouettes stay sharp,
// dims what lies behind, adds the scattered light, and gives sky pixels the sun's disk.
Shader "Hidden/MaxQ/AtmosphereSky" {

    SubShader {

        ZTest Always
        ZWrite Off
        Cull Off

        HLSLINCLUDE

        #include "Atmosphere.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D_X(_SceneDepth);
        TEXTURE2D_X(_AtmosphereInscatter);
        TEXTURE2D_X(_AtmosphereTransmittance);
        float4 _AtmosphereSize;
        StructuredBuffer<float> _Exposure;

        // Stand-in distance for sky pixels, and the cap on every other: large, but safe in half precision.
        #define SKY_DISTANCE 60000.0

        struct ViewRay {

            float3 direction;
            float distance;
            bool sky;

        };

        // Directions come from the near plane and distances from linear depth: with a near plane of centimetres and a
        // far plane past Selene, unprojecting the far depth loses all precision.
        ViewRay ViewRayAt(float2 uv) {

            float depth = SAMPLE_TEXTURE2D_X_LOD(_SceneDepth, sampler_PointClamp, uv, 0).r;

            #if UNITY_REVERSED_Z
            bool sky = depth <= 0.0;
            #else
            bool sky = depth >= 1.0;
            #endif

            float3 near = ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP);

            ViewRay ray;
            ray.direction = normalize(near - _WorldSpaceCameraPos);
            ray.distance = sky ? SKY_DISTANCE : min(LinearEyeDepth(depth, _ZBufferParams) / dot(ray.direction, -UNITY_MATRIX_V[2].xyz), SKY_DISTANCE);
            ray.sky = sky;

            return ray;

        }

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

                ViewRay ray = ViewRayAt(input.texcoord);

                // Interleaved gradient noise staggers neighbouring rays' samples, so shafts in the haze blend instead of banding.
                float jitter = frac(52.9829189 * frac(dot(input.positionCS.xy, float2(0.06711056, 0.00583715))));
                Scattering scattering = Integrate(_WorldSpaceCameraPos - _PlanetCentre, ray.direction, ray.sky ? 1e9 : ray.distance, _SunDirection, 24, true, jitter, true, true);

                Output output;
                output.inscatter = float4(scattering.radiance * _SunIlluminance, ray.distance);
                output.transmittance = float4(scattering.transmittance, 1.0);

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
                float3 flatSun = _SunDirection - up * dot(_SunDirection, up);
                float3 anyFlat = normalize(cross(up, abs(up.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0)));
                float3 forward = length(flatSun) > 1e-4 ? normalize(flatSun) : anyFlat;
                float3 side = cross(up, forward);

                float cosZenith;
                float cosLight;
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

            float4 Frag(Varyings input) : SV_Target {

                float2 uv = input.texcoord;
                float3 scene = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, uv, 0).rgb;
                ViewRay ray = ViewRayAt(uv);

                // How fast distance changes across the pixels of this surface: the gentler side along each axis, as
                // the steep side of a silhouette belongs to whatever lies behind it.
                float2 pixel = 1.0 / _ScreenParams.xy;
                float slopeX = min(abs(ViewRayAt(uv + float2(pixel.x, 0.0)).distance - ray.distance), abs(ViewRayAt(uv - float2(pixel.x, 0.0)).distance - ray.distance));
                float slopeY = min(abs(ViewRayAt(uv + float2(0.0, pixel.y)).distance - ray.distance), abs(ViewRayAt(uv - float2(0.0, pixel.y)).distance - ray.distance));
                float tolerance = 1e-3 * ray.distance + 3.0 * max(slopeX, slopeY);

                // Bilinear over the four nearest half-resolution samples, each weighed down by how far its distance
                // strays from this pixel's beyond what the surface's own slope explains, so air from behind a ridge
                // never bleeds onto it.
                float2 texel = uv * _AtmosphereSize.xy - 0.5;
                int2 base = int2(floor(texel));
                float2 f = texel - base;
                float3 inscatter = 0.0;
                float3 transmittance = 0.0;
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

                float exposure = _Exposure[0];

                if (!ray.sky) {

                    return float4((scene * transmittance + inscatter) * exposure, 1.0);

                }

                // Stars fade as the sky brightens; the eye never sees them against a daylit sky.
                float glare = dot(inscatter, float3(0.2126, 0.7152, 0.0722));
                float3 background = scene * exp(-glare * 40.0);
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
