// One layer of an exhaust plume, after KSP's Waterfall: an open tube flared in the vertex stage by a linear spread and a
// bounded one, lit additively in the fragment stage. Light fades along the tube as a power of what is left of it, so the
// gas thins into space with no end to see; the fresnel weighs the faces toward the eye (or, inverted, the axis), the tint
// runs from the exit's to the tail's, and periodic noise streams downstream for the flow. Most of a jet's light is light
// it scatters, so it dims with the sun and the sunlit body round it; only its glow shows in the dark. Each renderer carries its
// layer's values (see PlumeLayer in Catalogue.cs); transparents draw after the air's composite, so the exposure is applied
// here.
Shader "MaxQ/Plume" {

    SubShader {

        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass {

            Name "Plume"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // Where a layer meets the hull it fades into it over this depth (scene units, km) rather than cutting a line; short
            // enough that the gas inside a bell still fills it.
            #define SOFT_DEPTH 0.0004

            // The sun's light at full strength, as VesselLight sets it.
            #define FULL_SUN 2.4

            // radius (m), length (m), spread (m per m), bounded (m)
            float4 _PlumeShape;
            // where the layer starts downstream of the exit (m; negative inside the nozzle)
            float _PlumeOffset;
            // falloff power, fade-in share, tint falloff, brightness
            float4 _PlumeLight;
            // fresnel, inverted fresnel, noise contrast, seed
            float4 _PlumeFresnel;
            // tiles round (whole), tiles along, speed (tiles a second)
            float4 _PlumeFlow;
            float4 _PlumeStartTint;
            float4 _PlumeEndTint;
            // share of the brightness the gas gives off itself
            float _PlumeGlow;
            float _PlumeStrength;

            StructuredBuffer<float> _Exposure;

            struct Attributes {

                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;

            };

            struct Varyings {

                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 flowWS : TEXCOORD2;
                float2 uv : TEXCOORD3;

            };

            Varyings Vert(Attributes input) {

                float along = input.uv.y;
                float2 around = normalize(input.positionOS.xz);
                float span = _PlumeShape.y;
                float filled = exp(-3.0 * along);
                float radius = _PlumeShape.x + _PlumeShape.z * along * span + _PlumeShape.w * (1.0 - filled);
                float slope = _PlumeShape.z + _PlumeShape.w * 3.0 * filled / max(span, 1e-4);

                float3 position = float3(around.x * radius, _PlumeOffset + along * span, around.y * radius);
                float3 normal = normalize(float3(around.x, -slope, around.y));
                float3 flow = normalize(float3(around.x * slope, 1.0, around.y * slope));

                Varyings output;
                output.positionWS = TransformObjectToWorld(position);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(normal);
                output.flowWS = TransformObjectToWorldDir(flow);
                output.uv = float2(input.uv.x / TWO_PI, along);

                return output;

            }

            float Hash(float2 p) {

                float3 q = frac(float3(p.xyx) * 0.1031);

                q += dot(q, q.yzx + 33.33);

                return frac((q.x + q.y) * q.z);

            }

            // Value noise that repeats every period cells round the tube, so it closes on itself without a seam.
            float Noise(float2 p, float period) {

                float2 cell = floor(p);
                float2 f = p - cell;
                float2 u = f * f * (3.0 - 2.0 * f);
                float x0 = cell.x - period * floor(cell.x / period);
                float x1 = x0 + 1.0 >= period ? 0.0 : x0 + 1.0;

                float a = Hash(float2(x0, cell.y));
                float b = Hash(float2(x1, cell.y));
                float c = Hash(float2(x0, cell.y + 1.0));
                float d = Hash(float2(x1, cell.y + 1.0));

                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);

            }

            float Turbulence(float2 p, float period) {

                float sum = 0.0;
                float amplitude = 0.5;

                for (int i = 0; i < 3; i++) {

                    sum += amplitude * Noise(p, period);
                    p *= 2.0;
                    period *= 2.0;
                    amplitude *= 0.5;

                }

                return sum / 0.875;

            }

            float4 Frag(Varyings input) : SV_Target {

                float along = input.uv.y;
                float3 normal = normalize(input.normalWS);
                float3 flow = normalize(input.flowWS);
                float3 view = normalize(_WorldSpaceCameraPos - input.positionWS);

                // The eye's direction across the flow, so a plume seen end-on is not all rim.
                float3 across = view - dot(view, flow) * flow;
                float facing = abs(dot(normal, across * rsqrt(max(dot(across, across), 1e-8))));
                float rim = smoothstep(0.0, 1.0, facing);

                // What is left of the layer, as a power, fading in from the exit over its share.
                float fade = pow(saturate(1.0 - along), _PlumeLight.x) * (_PlumeLight.y > 0.0 ? smoothstep(0.0, _PlumeLight.y, along) : 1.0);
                float3 tint = lerp(_PlumeEndTint.rgb, _PlumeStartTint.rgb, pow(saturate(fade * (0.5 + 0.5 * rim)), _PlumeLight.z));

                // Noise streams downstream; the contrast lifts it away from mid-grey, and it calms toward the exit.
                float2 flowing = float2(input.uv.x * _PlumeFlow.x, along * _PlumeFlow.y - _PlumeFlow.z * _Time.y + _PlumeFresnel.w);
                float grain = max(lerp(0.5, Turbulence(flowing, _PlumeFlow.x), _PlumeFresnel.z), 0.0);
                float body = lerp(grain, 1.0, fade);

                // Where the noise is thin the edges sharpen, so the breakup reads as gas, not a stencil.
                float edge = 1.0 - body + 0.5 * _PlumeFresnel.z;
                float glow = pow(max(rim, 1e-3), max(edge * _PlumeFresnel.x, 0.0)) * pow(clamp(1.0 - rim, 1e-3, 1.0), clamp(edge * _PlumeFresnel.y, 1e-3, 10.0));

                // Walls closing on the camera fade out before the near plane can slice them, over as far again as it lies.
                float eye = -TransformWorldToView(input.positionWS).z;
                float near = _ProjectionParams.y;
                float scene = LinearEyeDepth(SampleSceneDepth(input.positionCS.xy / _ScaledScreenParams.xy), _ZBufferParams);
                float soft = smoothstep(near, 2.0 * near, eye) * saturate((scene - eye) / SOFT_DEPTH);

                // Gas scatters light from every side alike: the sun's, and the mean radiance round it (the ambient probe's
                // constant term) from all 4 pi, as a share of full sun.
                float3 surround = float3(unity_SHAr.w, unity_SHAg.w, unity_SHAb.w) + float3(unity_SHBr.z, unity_SHBg.z, unity_SHBb.z) / 3.0;
                float3 lit = lerp((_MainLightColor.rgb + 4.0 * surround) / FULL_SUN, 1.0, _PlumeGlow);

                float3 radiance = tint * lit * glow * fade * body * soft * _PlumeLight.w * _PlumeStrength;

                return float4(radiance * _Exposure[0], 0.0);

            }

            ENDHLSL

        }

    }

}
