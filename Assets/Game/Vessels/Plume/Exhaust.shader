// An engine's exhaust, or a whole cluster's, as one glowing volume rather than nested shells, so it has no edges to show
// from any side and a cluster costs one march. It stacks layers after Waterfall's EFFECTs, each set from the catalogue for
// the air round the exit: gas glowing about an axis, its width growing along its length (offset, linear, quadratic and
// bounded terms, so it can neck past the exit and flare again), fading along its length to a power, fading in and out
// over shares of it and tinted along the fade. Each engine draws its own copy of a layer about its own exit, except a
// merged layer, drawn once about the cluster's axis: as engines' layers fade out downstream a merged one fades in, and the
// plumes join into one. Upstream of an exit an engine's layers glow only inside its bell.
// Across its axis a layer is a Gaussian sharpened to a flatter top and crisper edge, less a narrower one as it is hollow,
// so its light gathers in the shear layer round its edge; it dims as it widens, as the same flow spreads wider. One noise
// field streams downstream through the whole volume in streaks along the flow, each octave convecting at its own pace so it
// churns; as in Waterfall it varies each layer's light and ripples its edge, more the further the layer has faded, and
// none at the exit, where the flow leaves laminar. Held in by the air, the gas brightens at evenly spaced shock diamonds.
// Each pixel sums the light along its ray, from the eye to the first opaque surface (so nothing needs the depth test), in
// steps even in the angle seen from the exit plane's centre, where the light gathers. The hull round the volume only finds
// the pixels: a frustum whose far faces draw once per pixel, inside it or out.
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

            // Most layers and engines one volume holds, and the vectors per layer; match ExhaustRenderer.
            #define MOST_LAYERS 4
            #define LAYER_VECTORS 6
            #define MOST_NOZZLES 9

            // depth of the bell's apex upstream of the exit (m), step spacing's softening (m), unused, exit radius (m)
            float4 _ExhaustShape;
            // the hull: its length (m), radius at the exit (m), flare (m per m)
            float4 _ExhaustHull;
            // per layer: radius (m), linear (m per m), square (m per m^2), bounded (m) growth; length (m), falloff, fade in
            // and fade out shares; brightness, tint falloff, sharpness, hollowness; noise, ragged, diamond contrast, unused;
            // start tint; end tint. Engines' layers come first, then merged ones.
            float4 _ExhaustLayers[MOST_LAYERS * LAYER_VECTORS];
            // how many layers each engine draws, and how many there are in all
            float4 _ExhaustLayerCounts;
            // streaks across a width, streak length (m), speed (m/s), eddy length per metre of width
            float4 _ExhaustFlow;
            // laminar length (m), shock diamond spacing (m), 1 / the length they fade over (m)
            float4 _ExhaustTurbulence;
            // each engine's exit, off the cluster's axis (m, in xz), and how many there are
            float4 _ExhaustNozzles[MOST_NOZZLES];
            float _ExhaustNozzleCount;
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

            // A layer's profile across its axis: a Gaussian sharpened to a flatter top and crisper edge, less a narrower one
            // as it is hollow.
            float Profile(float across2, float width2, float hollow, float sharp) {

                float q = across2 / width2;

                return max(exp(-0.5 * pow(q, sharp)) - hollow * exp(-0.5 * pow(2.78 * q, sharp)), 0.0);

            }

            float4 Frag(Varyings input) : SV_Target {

                float3 eye = TransformWorldToObject(_WorldSpaceCameraPos);
                float3 ray = input.positionOS - eye;
                float far = sqrt(dot(ray, ray));
                float3 view = ray / far;

                // The first opaque surface along the ray, in the same metres: eye depths scale with distance along it. Past
                // the hull only the bells' insides hold gas.
                float fragmentDepth = -TransformWorldToView(input.positionWS).z;
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(input.positionCS.xy / _ScaledScreenParams.xy), _ZBufferParams);
                float end = min(far * sceneDepth / fragmentDepth, far + _ExhaustShape.x);

                // Closest to the exit plane's centre at tc; the steps even in the angle the ray subtends there, softened.
                float tc = -dot(eye, view);
                float spacing = sqrt(max(dot(eye, eye) - tc * tc, 0.0) + _ExhaustShape.y * _ExhaustShape.y);
                float a0 = atan(-tc / spacing);
                float stride = (atan((end - tc) / spacing) - a0) / STEPS;

                // Interleaved gradient noise, turned each frame, staggers the steps for temporal AA to blend.
                float2 pixel = input.positionCS.xy + 5.588238 * fmod(floor(_Time.y * 60.0), 64.0);
                float jitter = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));

                int own = (int)_ExhaustLayerCounts.x;
                int layers = (int)_ExhaustLayerCounts.y;
                int nozzles = (int)_ExhaustNozzleCount;

                float3 light = 0.0;

                for (int i = 0; i < STEPS; i++) {

                    float slope = tan(a0 + (i + jitter) * stride);
                    float3 p = eye + (tc + spacing * slope) * view;
                    float past = max(p.y, 0.0);

                    // The noise, in streaks along the flow sized to the first layer's width there, laminar at the exit.
                    float4 first = _ExhaustLayers[0];
                    float width = max(first.x + past * (first.y + past * first.z), 0.25 * first.x);
                    float eddy = _ExhaustFlow.y + _ExhaustFlow.w * width;
                    float2 streak = p.xz / width * _ExhaustFlow.x;
                    float along = p.y / eddy;
                    float flow = _ExhaustFlow.z * _Time.y / eddy;
                    float grain = 0.5 * Noise(float3(streak, along - flow))
                        + 0.3 * Noise(float3(2.03 * streak + 17.0, 2.03 * (along - 1.31 * flow)))
                        + 0.2 * Noise(float3(4.07 * streak + 31.0, 4.07 * (along - 1.73 * flow)));
                    float churn = 4.0 * (grain - 0.5) * saturate(past / _ExhaustTurbulence.x);

                    // Bright discs where the flow shocks back to the air's pressure, the first a spacing downstream.
                    float wave = 0.5 + 0.5 * cos(6.2831853 * past / _ExhaustTurbulence.y);
                    float shocks = exp(-past * _ExhaustTurbulence.z) * smoothstep(0.0, 0.6 * _ExhaustTurbulence.y, past) * (3.0 * pow(wave, 8.0) - 0.4);

                    // Upstream of the exit the bell narrows to its apex.
                    float bell = _ExhaustShape.w * (1.0 + p.y / _ExhaustShape.x);
                    float3 glow = 0.0;

                    for (int l = 0; l < layers; l++) {

                        float4 growth = _ExhaustLayers[l * LAYER_VECTORS];
                        float4 fades = _ExhaustLayers[l * LAYER_VECTORS + 1];
                        float4 looks = _ExhaustLayers[l * LAYER_VECTORS + 2];
                        float4 rough = _ExhaustLayers[l * LAYER_VECTORS + 3];
                        float share = past / fades.x;

                        if (share >= 1.0 || (l >= own && p.y < 0.0)) {

                            continue;

                        }

                        float w = max(growth.x + past * (growth.y + past * growth.z) + growth.w * (1.0 - exp(-3.0 * share)), 0.25 * growth.x);
                        float fade = pow(max(1.0 - share, 0.0), fades.y);
                        float shown = fade * (fades.z > 0.0 ? smoothstep(0.0, fades.z, share) : 1.0)
                            * (fades.w > 0.0 ? 1.0 - smoothstep(1.0 - fades.w, 1.0, share) : 1.0);

                        // Texture strongest where the layer has faded, none at the exit.
                        float speckle = rough.x * (1.0 - fade) * churn;
                        float ripple = max(1.0 + rough.y * (1.0 - fade) * churn, 0.3);
                        float width2 = w * w * ripple * ripple;
                        float sum = 0.0;

                        if (l < own) {

                            for (int n = 0; n < nozzles; n++) {

                                float2 off = p.xz - _ExhaustNozzles[n].xy;
                                float across2 = dot(off, off);
                                float within = p.y >= 0.0 ? 1.0 : smoothstep(bell + 0.05, bell - 0.05, sqrt(across2));

                                sum += Profile(across2, width2, looks.w, looks.z) * within;

                            }

                        } else {

                            sum = Profile(dot(p.xz, p.xz), width2, looks.w, looks.z);

                        }

                        float3 tint = lerp(_ExhaustLayers[l * LAYER_VECTORS + 5].rgb, _ExhaustLayers[l * LAYER_VECTORS + 4].rgb, pow(max(shown, 0.0), looks.y));

                        // As bright seen across it at the exit, dimming as the same flow spreads wider.
                        glow += looks.x * growth.x / (w * w * 2.5066283) * shown * sum * max(1.0 + speckle, 0.0) * (1.0 + rough.z * shocks) * tint;

                    }

                    // Each step stands for spacing * sec^2 of its angle along the ray.
                    light += glow * (1.0 + slope * slope);

                }

                float3 radiance = light * stride * spacing * _ExhaustStrength;

                return float4(radiance * _Exposure[0], 0.0);

            }

            ENDHLSL

        }

    }

}
