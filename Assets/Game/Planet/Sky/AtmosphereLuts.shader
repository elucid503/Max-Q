// Precomputed atmosphere tables, rendered once: transmittance, multiple scattering (Hillaire 2020) and skylight.
Shader "Hidden/MaxQ/AtmosphereLuts" {

    SubShader {

        ZTest Always
        ZWrite Off
        Cull Off

        HLSLINCLUDE

        #include "Atmosphere.hlsl"

        struct Varyings {

            float4 position : SV_POSITION;
            float2 uv : TEXCOORD0;

        };

        Varyings Vert(uint id : SV_VertexID) {

            Varyings output;
            output.uv = float2((id << 1) & 2, id & 2);
            output.position = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);

            #if UNITY_UV_STARTS_AT_TOP
            output.uv.y = 1.0 - output.uv.y;
            #endif

            return output;

        }

        ENDHLSL

        Pass {

            Name "Transmittance"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target {

                float r;
                float mu;
                TransmittanceRMu(input.uv, r, mu);

                float3 origin = float3(0.0, 0.0, r);
                float3 direction = float3(sqrt(1.0 - mu * mu), 0.0, mu);
                float end = RaySphere(origin, direction, TopRadius()).y;
                float3 depth = 0.0;

                const int steps = 64;

                for (int i = 0; i < steps; i++) {

                    float3 p = origin + direction * ((i + 0.5) / steps * end);
                    depth += SampleMedium(length(p) - _PlanetRadius).extinction;

                }

                return float4(exp(-depth * end / steps), 1.0);

            }

            ENDHLSL

        }

        Pass {

            Name "MultiScatter"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target {

                float muSun = TexelToUnit(input.uv.x, MULTI_SCATTER_SIZE.x) * 2.0 - 1.0;
                float r = _PlanetRadius + TexelToUnit(input.uv.y, MULTI_SCATTER_SIZE.y) * ATMOSPHERE_HEIGHT;

                float3 origin = float3(0.0, 0.0, max(r, _PlanetRadius + 0.01));
                float3 sun = float3(sqrt(saturate(1.0 - muSun * muSun)), 0.0, muSun);
                float3 second = 0.0;
                float3 transfer = 0.0;

                const int rings = 8;
                const int steps = 20;
                const float uniformPhase = 1.0 / (4.0 * PI);

                for (int a = 0; a < rings; a++) {

                    for (int b = 0; b < rings; b++) {

                        float cosTheta = 1.0 - 2.0 * (a + 0.5) / rings;
                        float phi = 2.0 * PI * (b + 0.5) / rings;
                        float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
                        float3 direction = float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);

                        float2 top = RaySphere(origin, direction, TopRadius());
                        float2 ground = RaySphere(origin, direction, _PlanetRadius);
                        float end = ground.x > 0.0 ? ground.x : top.y;
                        float dt = end / steps;
                        float3 transmittance = 1.0;

                        for (int i = 0; i < steps; i++) {

                            float3 p = origin + direction * ((i + 0.3) * dt);
                            Medium medium = SampleMedium(length(p) - _PlanetRadius);
                            float3 stepTransmittance = exp(-medium.extinction * dt);
                            float3 integral = (1.0 - stepTransmittance) / max(medium.extinction, 1e-9);

                            second += transmittance * medium.scattering * uniformPhase * SunTransmittance(p, sun) * integral;
                            transfer += transmittance * medium.scattering * integral;
                            transmittance *= stepTransmittance;

                        }

                        if (ground.x > 0.0) {

                            float3 p = origin + direction * ground.x;
                            float3 normal = normalize(p);

                            second += transmittance * SunTransmittance(p, sun) * saturate(dot(normal, sun)) * GROUND_ALBEDO / PI;

                        }

                    }

                }

                second /= rings * rings;
                transfer /= rings * rings;

                return float4(second / (1.0 - transfer), 1.0);

            }

            ENDHLSL

        }

        Pass {

            Name "Irradiance"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target {

                float muSun = TexelToUnit(input.uv.x, 64.0) * 2.0 - 1.0;
                float r = _PlanetRadius + TexelToUnit(input.uv.y, 16.0) * ATMOSPHERE_HEIGHT;

                float3 origin = float3(0.0, 0.0, max(r, _PlanetRadius + 0.01));
                float3 sun = float3(sqrt(saturate(1.0 - muSun * muSun)), 0.0, muSun);
                float3 irradiance = 0.0;

                const int rings = 8;

                // Cosine-weighted hemisphere, so each direction carries pi / count.
                for (int a = 0; a < rings; a++) {

                    for (int b = 0; b < rings; b++) {

                        float sinTheta = sqrt((a + 0.5) / rings);
                        float phi = 2.0 * PI * (b + 0.5) / rings;
                        float3 direction = float3(sinTheta * cos(phi), sinTheta * sin(phi), sqrt(1.0 - sinTheta * sinTheta));

                        irradiance += Integrate(origin, direction, 1e9, sun, 24, true).radiance;

                    }

                }

                return float4(irradiance * PI / (rings * rings), 1.0);

            }

            ENDHLSL

        }

    }

}
