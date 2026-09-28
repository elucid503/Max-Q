// Renders the clouds. Each frame marches one ray per 4x4 block of pixels, with its samples staggered anew; the ray goes
// through one of the block's four half-resolution pixels, a different one each frame, and the resolve blends it into a
// half-resolution history it reprojects from the last frame, so every pixel of the history is fresh every four frames.
// Before the opaque scene, their shadow is mapped along the sun and the sky they fill is mapped around the camera for
// water to mirror.
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

        ENDHLSL

        Pass {

            Name "Trace"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            struct Output {

                float4 light : SV_Target0;
                float2 depth : SV_Target1;

            };

            Output Frag(Varyings input) {

                int2 texel = int2(input.positionCS.xy);
                int2 pixel = min(texel * 4 + int2(_CloudJitter.xy), int2(_SceneSize.xy) - 1);
                ViewRay ray = ViewRayThrough((pixel + 0.5) * _SceneSize.zw, LOAD_TEXTURE2D_X(_SceneDepth, pixel).r);
                float jitter = CloudHash(uint2(texel), uint(_CloudJitter.z));
                CloudTrace clouds = TraceClouds(_WorldSpaceCameraPos - _PlanetCentre, ray.direction, ray.sky ? 1e9 : ray.distance, jitter, _CloudPixelAngle, 96, 4, true);

                Output output;
                output.light = float4(clouds.radiance, clouds.transmittance);
                output.depth = float2(clouds.depth, ray.distance);

                return output;

            }

            ENDHLSL

        }

        Pass {

            Name "Resolve"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            // Share of each fresh sample in the history.
            #define CLOUD_BLEND 0.15

            struct Output {

                float4 light : SV_Target0;
                float2 depth : SV_Target1;

            };

            Output Frag(Varyings input) {

                int2 pixel = int2(input.positionCS.xy);
                int2 traced = int2(_CloudJitter.xy) >> 1;
                int2 last = int2(_CloudTraceSize.xy) - 1;
                int2 block = min(pixel >> 1, last);
                bool fresh = all((pixel & 1) == traced);

                // The fresh samples round this pixel bound its history; on the three frames in four it gets none of its
                // own, their bilinear blend stands in for one.
                float4 lowest = 1e9;
                float4 highest = -1e9;

                for (int y = -1; y <= 1; y++) {

                    for (int x = -1; x <= 1; x++) {

                        float4 neighbour = LOAD_TEXTURE2D(_CloudTrace, clamp(block + int2(x, y), 0, last));

                        lowest = min(lowest, neighbour);
                        highest = max(highest, neighbour);

                    }

                }

                float4 current = 0.0;
                float2 depth = 0.0;

                if (fresh) {

                    current = LOAD_TEXTURE2D(_CloudTrace, block);
                    depth = LOAD_TEXTURE2D(_CloudTraceDepth, block).rg;

                } else {

                    float2 at = (pixel - traced) * 0.5;
                    int2 base = int2(floor(at));
                    float2 f = at - base;

                    for (int i = 0; i < 4; i++) {

                        int2 offset = int2(i & 1, i >> 1);
                        int2 tap = clamp(base + offset, 0, last);
                        float bilinear = (offset.x ? f.x : 1.0 - f.x) * (offset.y ? f.y : 1.0 - f.y);

                        current += LOAD_TEXTURE2D(_CloudTrace, tap) * bilinear;
                        depth += LOAD_TEXTURE2D(_CloudTraceDepth, tap).rg * bilinear;

                    }

                }

                // Where the clouds seen through this pixel stood last frame, as the planet turned and the camera moved.
                float2 uv = (pixel * 2.0 + 1.0) * _SceneSize.zw;
                float3 direction = normalize(ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP) - _WorldSpaceCameraPos);
                float3 position = _WorldSpaceCameraPos + direction * depth.x;
                float4 clip = mul(_CloudReprojection, float4(position, 1.0));
                float2 previous = ComputeNormalizedDeviceCoordinatesWithZ(position, _CloudReprojection).xy;
                float2 historyUv = previous * _SceneSize.xy / (2.0 * _CloudSize.xy);
                float4 history = SAMPLE_TEXTURE2D_LOD(_CloudHistory, sampler_LinearClamp, historyUv, 0);
                float2 historyDepth = SAMPLE_TEXTURE2D_LOD(_CloudHistoryDepth, sampler_LinearClamp, historyUv, 0).rg;

                // The history's distance was measured from last frame's camera: carried to this one's, it keeps up with
                // a moving camera instead of trailing it, which would split the air short of the clouds.
                historyDepth.x += depth.x - length(position - _CloudPreviousCamera);

                // History is dropped off screen, and where the ground behind the pixel has changed, as at a ridge line.
                bool valid = _CloudHistoryValid > 0.0 && clip.w > 0.0 && all(previous >= 0.0) && all(previous <= 1.0) &&
                    abs(historyDepth.y - depth.y) < 0.1 * depth.y + 0.05;
                float4 spread = 0.25 * (highest - lowest);
                float blend = valid ? (fresh ? CLOUD_BLEND : 0.0) : 1.0;

                history = clamp(history, lowest - spread, highest + spread);

                Output output;
                output.light = lerp(history, current, blend);
                output.depth = float2(lerp(historyDepth.x, depth.x, blend), depth.y);

                return output;

            }

            ENDHLSL

        }

        Pass {

            Name "Shadow"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            // Each texel's sun line, from where it meets the ground (the map's plane stands in for it); the shade's start
            // is measured along the sun from the plane.
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

            // The clouds around the camera, seen through the air in front of them: what they add to the sky behind
            // (which the air past them lights), and how much of that sky they let through.
            float4 Frag(Varyings input) : SV_Target {

                float3 origin = _WorldSpaceCameraPos - _PlanetCentre;
                float3 up = normalize(origin);
                float3 flatSun = _SunDirection - up * dot(_SunDirection, up);
                float3 anyFlat = normalize(cross(up, abs(up.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0)));
                float3 forward = length(flatSun) > 1e-4 ? normalize(flatSun) : anyFlat;
                float3 side = cross(up, forward);
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
