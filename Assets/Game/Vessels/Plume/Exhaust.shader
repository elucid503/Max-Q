// An engine's exhaust in vacuum as one glowing volume rather than nested shells, so it has no edges to show from any side.
// Two lobes share it, a hot core and the wide expansion, each a Gaussian about the axis: its own width at the exit,
// widening steadily downstream at its angle, its density falling as the width squared so the flow through each
// cross-section holds. The gas leaves the bell within its lip and fans out round it, so an envelope from the lip trims
// whatever either lobe would spread wider; upstream of the exit it glows only inside the bell. Each pixel sums the light along its ray, from the eye to the first opaque surface (so nothing needs the depth
// test), in steps even in the angle seen from the exit's centre, which is where the light gathers. The hull round the
// volume only finds the pixels: a frustum whose far faces draw once per pixel, inside it or out.
// Transparents draw after the air's composite, so the exposure is applied here.
Shader "MaxQ/Exhaust" {

    SubShader {

        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass {

            Name "Exhaust"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            ZTest Always
            Cull Front

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            #define STEPS 24

            // How soft the envelope's edge is at the lip (m), and how much softer it grows downstream (m per m).
            #define LIP_SOFTNESS 0.1
            #define LIP_SOFTENING 0.25

            // depth of the bell's apex upstream of the exit (m), step spacing's softening (m), length (m), exit radius (m)
            float4 _ExhaustShape;
            // the hull: its length (m), radius at the exit (m), flare (m per m); the envelope's flare round the lip (m per m)
            float4 _ExhaustHull;
            // per lobe: width at the exit (m), tangent of its angle, brightness scaled to its width, 1 / reach (m)
            float4 _CoreLobe;
            float4 _CoreStartTint;
            float4 _CoreEndTint;
            float4 _ExpansionLobe;
            float4 _ExpansionStartTint;
            float4 _ExpansionEndTint;
            // noise contrast, tiles across the expansion's width, tile length (m), speed (m/s)
            float4 _ExhaustFlow;
            float _ExhaustStrength;

            StructuredBuffer<float> _Exposure;

            struct Attributes {

                float4 positionOS : POSITION;

            };

            struct Varyings {

                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;

            };

            Varyings Vert(Attributes input) {

                // The unit cylinder (radius 1, y from 0 to 1) stretched to the frustum.
                float y = input.positionOS.y * _ExhaustHull.x;
                float3 position = float3(input.positionOS.x * (_ExhaustHull.y + y * _ExhaustHull.z), y, input.positionOS.z * (_ExhaustHull.y + y * _ExhaustHull.z));

                Varyings output;
                output.positionOS = position;
                output.positionWS = TransformObjectToWorld(position);
                output.positionCS = TransformWorldToHClip(output.positionWS);

                return output;

            }

            float Hash(float3 p) {

                p = frac(p * 0.1031);
                p += dot(p, p.zyx + 31.32);

                return frac((p.x + p.y) * p.z);

            }

            float Noise(float3 p) {

                float3 cell = floor(p);
                float3 f = p - cell;
                float3 u = f * f * (3.0 - 2.0 * f);

                float a = lerp(Hash(cell), Hash(cell + float3(1.0, 0.0, 0.0)), u.x);
                float b = lerp(Hash(cell + float3(0.0, 1.0, 0.0)), Hash(cell + float3(1.0, 1.0, 0.0)), u.x);
                float c = lerp(Hash(cell + float3(0.0, 0.0, 1.0)), Hash(cell + float3(1.0, 0.0, 1.0)), u.x);
                float d = lerp(Hash(cell + float3(0.0, 1.0, 1.0)), Hash(cell + float3(1.0, 1.0, 1.0)), u.x);

                return lerp(lerp(a, b, u.y), lerp(c, d, u.y), u.z);

            }

            // A lobe's width squared at a distance past the exit; inside the bell it holds its width at the exit.
            float Width2(float4 lobe, float past) {

                float width = lobe.x + past * lobe.y;

                return width * width;

            }

            // A lobe's light at a point off the axis: its share there, dimming and cooling with distance past the exit.
            float3 Lobe(float4 lobe, float3 startTint, float3 endTint, float across2, float past) {

                float width2 = Width2(lobe, past);
                float cooled = exp(-past * lobe.w);

                return lobe.z * exp(-0.5 * across2 / width2) / width2 * cooled * lerp(endTint, startTint, cooled);

            }

            float4 Frag(Varyings input) : SV_Target {

                float3 eye = TransformWorldToObject(_WorldSpaceCameraPos);
                float3 ray = input.positionOS - eye;
                float far = sqrt(dot(ray, ray));
                float3 view = ray / far;

                // The first opaque surface along the ray, in the same metres: eye depths scale with distance along it. Past
                // the hull only the bell's inside holds gas.
                float fragmentDepth = -TransformWorldToView(input.positionWS).z;
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(input.positionCS.xy / _ScaledScreenParams.xy), _ZBufferParams);
                float end = min(far * sceneDepth / fragmentDepth, far + _ExhaustShape.x);

                // Closest to the exit's centre at tc; the steps even in the angle the ray subtends there, softened.
                float tc = -dot(eye, view);
                float spacing = sqrt(max(dot(eye, eye) - tc * tc, 0.0) + _ExhaustShape.y * _ExhaustShape.y);
                float a0 = atan(-tc / spacing);
                float stride = (atan((end - tc) / spacing) - a0) / STEPS;

                // Interleaved gradient noise, turned each frame, staggers the steps for temporal AA to blend.
                float2 pixel = input.positionCS.xy + 5.588238 * fmod(floor(_Time.y * 60.0), 64.0);
                float jitter = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));

                float3 light = 0.0;

                for (int i = 0; i < STEPS; i++) {

                    float slope = tan(a0 + (i + jitter) * stride);
                    float3 p = eye + (tc + spacing * slope) * view;
                    float across2 = dot(p.xz, p.xz);
                    float across = sqrt(across2);
                    float past = max(p.y, 0.0);

                    // Within the length; upstream of the exit inside the bell; downstream within the fan from the lip, its
                    // edge falling away smoothly.
                    float bell = _ExhaustShape.w * (1.0 + p.y / _ExhaustShape.x);
                    float spill = max(across - _ExhaustShape.w - past * _ExhaustHull.w, 0.0) / (LIP_SOFTNESS + past * LIP_SOFTENING);
                    float inside = smoothstep(_ExhaustShape.z, 0.6 * _ExhaustShape.z, p.y)
                        * (p.y >= 0.0 ? exp(-0.5 * spill * spill) : smoothstep(bell + 0.05, bell - 0.05, across));

                    // Streaks of denser gas flowing out, sized to the expansion's width, calm at the exit.
                    float3 cell = float3(p.xz * rsqrt(Width2(_ExpansionLobe, past)) * _ExhaustFlow.y, (p.y - _ExhaustFlow.w * _Time.y) / _ExhaustFlow.z);
                    float grain = 0.67 * Noise(cell) + 0.33 * Noise(2.03 * cell + 17.0);
                    float body = max(lerp(1.0, 2.0 * grain, _ExhaustFlow.x * saturate(past / _ExhaustFlow.z)), 0.0);

                    // Each step stands for spacing * sec^2 of its angle along the ray.
                    light += (Lobe(_CoreLobe, _CoreStartTint.rgb, _CoreEndTint.rgb, across2, past)
                        + Lobe(_ExpansionLobe, _ExpansionStartTint.rgb, _ExpansionEndTint.rgb, across2, past)) * inside * body * (1.0 + slope * slope);

                }

                float3 radiance = light * stride * spacing * _ExhaustStrength;

                return float4(radiance * _Exposure[0], 0.0);

            }

            ENDHLSL

        }

    }

}
