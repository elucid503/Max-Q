// The clouds: a ray per 4x4 block each frame, resolved into a reprojected half-resolution history; and the shadow and sky
// maps drawn before the opaque scene.
Shader "Hidden/MaxQ/Clouds" {

    SubShader {

        ZTest Always
        ZWrite Off
        Cull Off

        HLSLINCLUDE

        #include "Clouds.hlsl"
        #include "../ViewRay.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D(_CloudTrace);
        TEXTURE2D(_CloudTraceDepth);
        TEXTURE2D(_CloudHistory);
        TEXTURE2D(_CloudHistoryDepth);
        float4x4 _CloudReprojection;
        float3 _CloudPreviousCamera;

        // The pixel of each 4x4 block traced this frame, and the frame's number.
        float4 _CloudJitter;
        float4 _CloudSize;
        float4 _CloudTraceSize;
        float _CloudPixelAngle;
        float _CloudHistoryValid;

        struct Output {

            float4 light : SV_Target0;
            float2 depth : SV_Target1;

        };

        ENDHLSL

        Pass {

            Name "Trace"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            // Clouds seen from far off (a pixel spanning this many km where the ray meets their shell) are far finer than
            // the block, so one ray is a coin toss between cloud and none: the block is traced through CLOUD_SUPERSAMPLE of
            // its pixels, spread across it, and the ray stands for their mean. Only a far planet's few blocks pay for it.
            #define CLOUD_SUPERSAMPLE_FOOTPRINT 2.0
            #define CLOUD_SUPERSAMPLE 4

            Output Frag(Varyings input) {

                int2 texel = int2(input.positionCS.xy);
                int2 last = int2(_SceneSize.xy) - 1;
                int2 pixel = min(texel * 4 + int2(_CloudJitter.xy), last);
                float3 origin = _WorldSpaceCameraPos - _PlanetCentre;
                ViewRay ray = ViewRayThrough((pixel + 0.5) * _SceneSize.zw, LOAD_TEXTURE2D_X(_SceneDepth, pixel).r);
                float jitter = CloudJitter(uint2(texel), uint(_CloudJitter.z));
                float shell = max(RaySphere(origin, ray.direction, _PlanetRadius + CLOUD_TOP).x, 0.0);
                int rays = shell * _CloudPixelAngle > CLOUD_SUPERSAMPLE_FOOTPRINT ? CLOUD_SUPERSAMPLE : 1;
                float3 radiance = 0.0;
                float transmittance = 0.0;
                float depth = 0.0;

                for (int i = 0; i < rays; i++) {

                    // The other pixels of the block's 2x2 lattice this frame; the jitter walks the lattice over the rounds.
                    int2 at = i == 0 ? pixel : min(texel * 4 + ((int2(_CloudJitter.xy) + 2 * int2(i & 1, i >> 1)) & 3), last);
                    ViewRay through = ray;

                    if (i > 0) {

                        through = ViewRayThrough((at + 0.5) * _SceneSize.zw, LOAD_TEXTURE2D_X(_SceneDepth, at).r);

                    }

                    CloudTrace clouds = TraceClouds(origin, through.direction, through.reach, i == 0 ? jitter : CloudJitter(uint2(texel) * 2u + uint2(i & 1, i >> 1),
                        uint(_CloudJitter.z)), _CloudPixelAngle, 96, 4, true);

                    radiance += clouds.radiance;
                    transmittance += clouds.transmittance;
                    depth += clouds.depth;

                }

                Output output;
                output.light = float4(radiance, transmittance) / rays;
                output.depth = float2(depth / rays, ray.distance);

                return output;

            }

            ENDHLSL

        }

        Pass {

            Name "Resolve"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            // Share of each fresh sample in the history, and of the fresh samples round a pixel not traced this frame: were
            // only the traced quarter updated, noisy light would print as a lattice of every other pixel.
            #define CLOUD_BLEND 0.15
            #define CLOUD_SPREAD 0.03

            // Clouds seen from orbit are far finer than a pixel, so each ray sees cloud or none; the history averages them
            // over this many times as many frames once a pixel spans CLOUD_FAR_FOOTPRINT (km), and flickers far less.
            #define CLOUD_FAR_HOLD 3.0
            #define CLOUD_NEAR_FOOTPRINT 2.0
            #define CLOUD_FAR_FOOTPRINT 30.0

            // Half-resolution pixels the view slides in a frame before its history is clamped, and fully; likewise the share
            // by which the history's cloud distance strays from this frame's.
            #define CLOUD_SLIDE_START 0.25
            #define CLOUD_SLIDE_END 2.0
            #define CLOUD_STRAY_START 0.15
            #define CLOUD_STRAY_END 0.4

            // The history through a Catmull-Rom filter over its 4x4 texels in five bilinear taps (Jimenez 2016): bilinear
            // alone softens it a little more each frame it is carried, and the clouds blur as the camera moves.
            float4 HistoryLight(float2 uv) {

                float2 position = uv * _CloudSize.xy;
                float2 centre = floor(position - 0.5) + 0.5;
                float2 f = position - centre;
                float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
                float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
                float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
                float2 w3 = f * f * (-0.5 + 0.5 * f);
                float2 w12 = w1 + w2;
                float2 at0 = (centre - 1.0) * _CloudSize.zw;
                float2 at3 = (centre + 2.0) * _CloudSize.zw;
                float2 at12 = (centre + w2 / w12) * _CloudSize.zw;
                float4 sum = SAMPLE_TEXTURE2D_LOD(_CloudHistory, sampler_LinearClamp, float2(at12.x, at0.y), 0) * (w12.x * w0.y) +
                    SAMPLE_TEXTURE2D_LOD(_CloudHistory, sampler_LinearClamp, float2(at0.x, at12.y), 0) * (w0.x * w12.y) +
                    SAMPLE_TEXTURE2D_LOD(_CloudHistory, sampler_LinearClamp, at12, 0) * (w12.x * w12.y) +
                    SAMPLE_TEXTURE2D_LOD(_CloudHistory, sampler_LinearClamp, float2(at3.x, at12.y), 0) * (w3.x * w12.y) +
                    SAMPLE_TEXTURE2D_LOD(_CloudHistory, sampler_LinearClamp, float2(at12.x, at3.y), 0) * (w12.x * w3.y);
                float weight = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;

                // The filter's negative lobes overshoot, and carried frame after frame without the neighbourhood clamp they
                // would sharpen noise into a lattice; held within the four nearest texels, it can never exceed what it reads.
                int2 nearest = int2(centre - 0.5);
                int2 last = int2(_CloudSize.xy) - 1;
                float4 lowest = 1e9;
                float4 highest = -1e9;

                for (int i = 0; i < 4; i++) {

                    float4 texel = LOAD_TEXTURE2D(_CloudHistory, clamp(nearest + int2(i & 1, i >> 1), 0, last));

                    lowest = min(lowest, texel);
                    highest = max(highest, texel);

                }

                return clamp(sum / weight, lowest, highest);

            }

            // Trace samples see the same clouds as a pixel where their ground lies within this share of its own (and this
            // margin, km); across a silhouette they would carry the sky's clouds onto the ground before it, and back. A
            // sample far off still counts a little, so a pixel no sample matches keeps a value.
            #define CLOUD_MATCH 0.2
            #define CLOUD_MATCH_MARGIN 0.05
            #define CLOUD_FALLBACK 0.01

            float Match(float ground, float own) {

                float off = abs(ground - own) / (CLOUD_MATCH * own + CLOUD_MATCH_MARGIN);

                return 1.0 / (1.0 + off * off);

            }

            Output Frag(Varyings input) {

                int2 pixel = int2(input.positionCS.xy);
                int2 traced = int2(_CloudJitter.xy) >> 1;
                int2 last = int2(_CloudTraceSize.xy) - 1;
                bool fresh = all((pixel & 1) == traced);

                // The texel's light is what the air's march reads for the ray through its top-left pixel, so the samples
                // are matched against that ray's ground. It is carried across frames from its own centre, where last
                // frame's texel is read: from any other point the history would creep a little every frame.
                int2 full = min(pixel * 2, int2(_SceneSize.xy) - 1);
                ViewRay ray = ViewRayThrough((full + 0.5) * _SceneSize.zw, LOAD_TEXTURE2D_X(_SceneDepth, full).r);
                float2 uv = (pixel * 2.0 + 1.0) * _SceneSize.zw;
                float3 direction = normalize(ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP) - _WorldSpaceCameraPos);

                // Trace sample i went through pixel 4i + jitter: bilinear weights at this pixel, times how well each
                // sample's ground matches. The matching ones bound the history.
                float2 at = (full - _CloudJitter.xy) * 0.25;
                int2 base = int2(floor(at));
                float4 current = 0.0;
                float cloudDepth = 0.0;
                float total = 0.0;
                float confidence = 0.0;
                float4 lowest = 1e9;
                float4 highest = -1e9;
                float4 anyLowest = 1e9;
                float4 anyHighest = -1e9;

                for (int i = 0; i < 16; i++) {

                    int2 offset = int2(i & 3, i >> 2) - 1;
                    int2 tap = clamp(base + offset, 0, last);
                    float4 light = LOAD_TEXTURE2D(_CloudTrace, tap);
                    float2 depth = LOAD_TEXTURE2D(_CloudTraceDepth, tap).rg;
                    float2 tent = saturate(1.0 - abs(at - (base + offset)));
                    float bilinear = tent.x * tent.y;
                    float match = Match(depth.y, ray.distance);
                    float weight = (bilinear + CLOUD_FALLBACK) * match;

                    current += light * weight;
                    cloudDepth += depth.x * weight;
                    total += weight;
                    confidence += bilinear * match;
                    anyLowest = min(anyLowest, light);
                    anyHighest = max(anyHighest, light);

                    if (match > 0.5) {

                        lowest = min(lowest, light);
                        highest = max(highest, light);

                    }

                }

                current /= total;
                cloudDepth /= total;

                if (highest.a < lowest.a) {

                    lowest = anyLowest;
                    highest = anyHighest;

                }

                // A ray that misses the clouds' shell sees none, whatever its neighbours or history say, so nothing smears
                // off the limb into space as the planet shrinks away.
                float2 shell = RaySphere(_WorldSpaceCameraPos - _PlanetCentre, direction, _PlanetRadius + CLOUD_TOP);
                bool through = shell.y > max(shell.x, 0.0);

                current = through ? current : float4(0.0, 0.0, 0.0, 1.0);

                // Where the clouds seen through this pixel stood last frame, as the planet turned and the camera moved.
                float3 position = _WorldSpaceCameraPos + direction * cloudDepth;
                float4 clip = mul(_CloudReprojection, float4(position, 1.0));
                float2 previous = ComputeNormalizedDeviceCoordinatesWithZ(position, _CloudReprojection).xy;
                float2 historyUv = previous * _SceneSize.xy / (2.0 * _CloudSize.xy);
                float4 history = HistoryLight(historyUv);
                float2 historyDepth = SAMPLE_TEXTURE2D_LOD(_CloudHistoryDepth, sampler_LinearClamp, historyUv, 0).rg;

                // Carried to this frame's camera, the history's distance keeps up instead of splitting the air short.
                historyDepth.x += cloudDepth - length(position - _CloudPreviousCamera);

                // History is dropped off screen, and where the ground behind the pixel has changed, as at a ridge line. A
                // pixel no sample matched this frame keeps its history.
                bool valid = through && _CloudHistoryValid > 0.0 && clip.w > 0.0 && all(previous >= 0.0) && all(previous <= 1.0) &&
                    abs(historyDepth.y - ray.distance) < 0.1 * ray.distance + 0.05;
                float4 spread = 0.25 * (highest - lowest);
                float hold = lerp(1.0, CLOUD_FAR_HOLD, smoothstep(CLOUD_NEAR_FOOTPRINT, CLOUD_FAR_FOOTPRINT, cloudDepth * _CloudPixelAngle));
                float blend = valid ? (fresh ? CLOUD_BLEND : CLOUD_SPREAD) * saturate(confidence) / hold : 1.0;

                // The box comes from one ray per 4x4 block, which misses clouds smaller than that on most frames; clamped
                // every frame they would blink out. History is clamped only where it may be stale: as far as the view has
                // slid (half-resolution pixels this frame), or where the clouds it holds stand at another distance than
                // this frame's, as when the camera pulls away. It is kept whole while the view holds nearly still.
                float slide = length((previous - uv) * _SceneSize.xy) * 0.5;
                float stray = abs(historyDepth.x - cloudDepth) / max(cloudDepth, 1.0);
                float stale = max(smoothstep(CLOUD_SLIDE_START, CLOUD_SLIDE_END, slide), smoothstep(CLOUD_STRAY_START, CLOUD_STRAY_END, stray));

                history = lerp(history, clamp(history, lowest - spread, highest + spread), stale);

                Output output;
                output.light = lerp(history, current, blend);
                output.depth = float2(lerp(historyDepth.x, cloudDepth, blend), ray.distance);

                return output;

            }

            ENDHLSL

        }

        Pass {

            Name "Shadow"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            // Each texel's sun line through the map's plane, which stands in for the ground.
            float4 Frag(Varyings input) : SV_Target {

                float2 plane = (input.texcoord - 0.5) / _CloudShadowOrigin.w;
                float3 origin = _CloudShadowOrigin.xyz + _CloudShadowRight * plane.x + _CloudShadowUp * plane.y - _PlanetCentre;
                float footprint = 1.0 / (_CloudShadowOrigin.w * CLOUD_SHADOW_TEXELS);
                float jitter = CloudHash(uint2(input.positionCS.xy), 0u);

                return float4(CloudShade(origin, footprint, jitter, 64), 0.0, 0.0);

            }

            ENDHLSL

        }

        Pass {

            Name "Sky"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            // The clouds round the camera through the air before them: what they add to the sky, and how much they let through.
            float4 Frag(Varyings input) : SV_Target {

                float3 origin = _WorldSpaceCameraPos - _PlanetCentre;
                float3 up = normalize(origin);
                float3 forward;
                float3 side;

                SkyFrame(up, forward, side);

                // The map keeps CLOUD_SKY_BELOW of the sine of elevation below the horizon, as CloudySky reads it.
                float azimuth = (input.texcoord.x - 0.5) * 2.0 * PI;
                float sinElevation = input.texcoord.y * input.texcoord.y * (1.0 + CLOUD_SKY_BELOW) - CLOUD_SKY_BELOW;
                float cosElevation = sqrt(saturate(1.0 - sinElevation * sinElevation));
                float3 direction = up * sinElevation + (forward * cos(azimuth) + side * sin(azimuth)) * cosElevation;
                CloudTrace clouds = TraceClouds(origin, direction, 1e9, 0.5, 2.0 * PI / CLOUD_SKY_WIDTH, 40, 4, false);

                if (clouds.transmittance > 0.999) {

                    return float4(0.0, 0.0, 0.0, 1.0);

                }

                Scattering air = Integrate(origin, direction, clouds.depth, _SunDirection, 8, true);

                return float4(air.radiance * _SunIlluminance * (1.0 - clouds.transmittance) + air.transmittance * clouds.radiance, clouds.transmittance);

            }

            ENDHLSL

        }

    }

}
