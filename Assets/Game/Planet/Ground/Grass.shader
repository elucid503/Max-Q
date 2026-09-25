// Grass tufts near the camera, drawn procedurally: every tuft Vegetation keeps is a dozen curved blades, each five
// vertices, leaning and swaying about the local vertical. Tufts thin out with distance, the survivors spreading wider
// to keep the cover, and sink into the ground texture short of GRASS_REACH. Blades are lit as the ground is, darkening
// toward their roots, with sunlight glowing through them from behind.
Shader "MaxQ/Grass" {

    SubShader {

        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE

        #include "Sunlight.hlsl"

        struct Plant {

            float4 position;
            float4 colour;
            float4 shape;

        };

        StructuredBuffer<Plant> _Plants;
        float4 _PatchPosition;
        float4 _PatchRotation;

        // Metres: the reach, within which every tuft stands; tuft radius, blade length and width at full height.
        #define GRASS_REACH 50.0
        #define GRASS_DENSE 12.0
        #define TUFT_RADIUS 0.3
        #define BLADE_LENGTH 0.35
        #define BLADE_WIDTH 0.014
        #define BLADES 16

        struct Varyings {

            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float3 colour : TEXCOORD2;
            float along : TEXCOORD3;

        };

        float3 Rotate(float4 q, float3 v) {

            return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);

        }

        float Hash(uint a, uint b) {

            uint h = a * 0x9E3779B9u ^ b * 0x85EBCA6Bu;
            h = (h ^ (h >> 16)) * 0x7FEB352Du;
            h = (h ^ (h >> 15)) * 0x846CA68Bu;

            return (h ^ (h >> 16)) / 4294967295.0;

        }

        Varyings Vertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID) {

            Plant plant = _Plants[instance];
            float3 root = _PatchPosition.xyz + Rotate(_PatchRotation, plant.position.xyz);
            float distance = length(root - _WorldSpaceCameraPos) * 1000.0;

            // Keep a share of tufts falling with the square of distance past GRASS_DENSE: the ones whose random
            // rank is under it. Kept tufts grow wider to cover for the rest.
            float keep = saturate(GRASS_DENSE * GRASS_DENSE / max(distance * distance, 1e-3));
            float spread = rsqrt(keep);
            float grow = 1.0 - smoothstep(0.7 * GRASS_REACH, GRASS_REACH, distance);

            Varyings output;

            if (plant.position.w > keep || grow <= 0.0) {

                output = (Varyings)0;
                output.positionCS = asfloat(0x7FC00000u);

                return output;

            }

            uint blade = vertex / 5;
            uint k = vertex % 5;
            uint seed = asuint(plant.position.w);
            float3 up = normalize(root - _PlanetCentre);
            float3 side = normalize(cross(up, abs(up.y) < 0.9 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0)));
            float3 ahead = cross(side, up);

            float angle = Hash(seed, blade) * 2.0 * PI;
            float radius = sqrt(Hash(seed, blade + 31u)) * TUFT_RADIUS * spread;
            float3 facing = side * cos(angle) + ahead * sin(angle);
            float3 across = cross(up, facing);
            float3 offset = (side * cos(angle * 1.7 + 1.0) + ahead * sin(angle * 1.7 + 1.0)) * radius;
            float tall = BLADE_LENGTH * plant.colour.w * (0.55 + 0.45 * Hash(seed, blade + 67u)) * grow;
            float width = BLADE_WIDTH * sqrt(spread) * (0.7 + 0.6 * Hash(seed, blade + 97u));

            // Blades lean out from the tuft and bow under their own weight; the wind rocks them in gusts.
            float gust = sin(_Time.y * 1.3 + dot(root, float3(410.0, 370.0, 290.0))) * sin(_Time.y * 0.37 + root.x * 90.0);
            float bend = (0.25 + 0.35 * Hash(seed, blade + 131u)) * tall + 0.08 * gust * tall;
            float t = k < 2 ? 0.0 : k < 4 ? 0.55 : 1.0;
            float lateral = k == 4 ? 0.0 : ((k & 1) ? 1.0 : -1.0) * width * (1.0 - 0.6 * t);

            float3 metres = offset + up * tall * (t - 0.35 * t * t * bend / max(tall, 1e-3)) + facing * bend * t * t + across * lateral;
            float3 tangent = up * tall + facing * 2.0 * bend * t;

            output.positionWS = root + metres / 1000.0;
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.normalWS = normalize(lerp(normalize(cross(across, tangent)), up, 0.7));
            output.colour = plant.colour.rgb * lerp(0.75, 1.2, Hash(seed, blade + 173u)) * lerp(float3(1.0, 1.0, 1.0), float3(1.25, 1.1, 0.6), t * t * plant.shape.x);
            output.along = t;

            return output;

        }

        ENDHLSL

        Pass {

            Name "Grass"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            float4 Frag(Varyings input) : SV_Target {

                float3 toCamera = normalize(_WorldSpaceCameraPos - input.positionWS);
                float3 n = normalize(input.normalWS);
                n = dot(n, toCamera) < 0.0 ? -n : n;

                Sunlight light = SunlightAt(input.positionWS);

                // The tuft hides the sky from the roots; the sun shows through a blade lit from behind.
                float occlusion = lerp(0.55, 1.0, input.along);
                float3 lit = GroundRadiance(input.colour, n, light, input.colour, occlusion);
                float behind = saturate(dot(-n, _SunDirection));
                float3 through = light.direct * light.shadow * behind * input.colour * 0.8 / PI * input.along;

                return float4(lit + through, 1.0);

            }

            ENDHLSL

        }

        Pass {

            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ColorMask R
            Cull Off

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment DepthFrag

            float DepthFrag(Varyings input) : SV_Target {

                return input.positionCS.z;

            }

            ENDHLSL

        }

    }

}
