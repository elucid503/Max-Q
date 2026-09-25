// Trees, drawn procedurally as surfaces of revolution: two rings of trunk, then the crown. A conifer's crown is tiers of
// drooping skirts, ragged at their rims, narrowing to the tip; a broadleaf's is a lumpy dome. Each tree Vegetation keeps
// has its own size, colour, lean of lumps and turn. The near detail draws inside TREE_NEAR and the far one out to
// TREE_REACH, thinning as it goes and shrinking away short of the reach into the canopy the ground draws. Foliage is the forest texture tinted
// to the tree's colour, lit softly as a whole crown, darker underneath, with light through its edges; bark is dark.
Shader "MaxQ/Tree" {

    Properties {

        _GroundAlbedo ("Ground Albedo", 2DArray) = "" {}
        _GroundNormal ("Ground Normals", 2DArray) = "" {}

    }

    SubShader {

        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE

        #include "Sunlight.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

        TEXTURE2D_ARRAY(_GroundAlbedo);
        TEXTURE2D_ARRAY(_GroundNormal);
        SAMPLER(sampler_GroundAlbedo);

        struct Plant {

            float4 position;
            float4 colour;
            float4 shape;

        };

        StructuredBuffer<Plant> _Plants;
        float4 _PatchPosition;
        float4 _PatchRotation;
        uint _Rings;
        uint _Segments;
        // Metres from the camera within which this draw's trees stand, and whether they thin with distance.
        float2 _Band;
        uint _Thin;

        // Metres; must match Vegetation.
        #define TREE_REACH 800.0
        #define TREE_NEAR 150.0

        // Slices of the ground arrays and their mean albedo; GroundMaterials.hlsl names the same.
        #define FOREST_SLICE 1
        #define ROCK_SLICE 4
        #define FOREST_LUMA 0.29
        #define ROCK_LUMA 0.068
        #define LEAF_TILE 0.6
        #define CLUMP_TILE 3.0

        // The foot, the top of the trunk, and the top again where the foliage begins, so bark never smears onto leaves.
        #define TRUNK_RINGS 3

        // Set by URP while it renders a shadow cascade.
        float3 _LightDirection;

        struct Varyings {

            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float3 local : TEXCOORD2;
            nointerpolation float3 colour : TEXCOORD3;
            float foliage : TEXCOORD4;
            float occlusion : TEXCOORD5;

        };

        float3 Rotate(float4 q, float3 v) {

            return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);

        }

        // A point on the tree in its own frame (metres, y up): ring (0 at the foot) and angle about the trunk.
        float3 Profile(Plant plant, int ring, float angle) {

            float height = plant.shape.x;
            float crown = plant.shape.y;
            float seed = plant.shape.z * 40.0;
            bool needles = plant.colour.w > 0.5;
            float trunk = 0.05 + 0.006 * height;

            // Trees in a stand shed their lower branches.
            float base = needles ? 0.3 * height : 0.4 * height;

            if (ring < TRUNK_RINGS) {

                // The trunk tapers from a flared foot to two thirds as thick where the crown begins.
                return ring == 0 ? float3(cos(angle) * 1.3 * trunk, -0.5, sin(angle) * 1.3 * trunk) : float3(cos(angle) * 0.65 * trunk, base, sin(angle) * 0.65 * trunk);

            }

            int rings = (int)_Rings - TRUNK_RINGS;
            int c = ring - TRUNK_RINGS;

            if (needles) {

                // Tiers of about the square root of the rings each, at least two; each starts a little under the last
                // one's top.
                int each = max(2, (int)round(sqrt((float)rings)));
                int tiers = rings / each;
                int tier = min(c / each, tiers - 1);
                float f = saturate((c - tier * each) / (float)(each - 1));
                float span = (height - base) / tiers;
                float bottom = base + span * tier - 0.2 * span * (tier > 0);
                float top = base + span * (tier + 1);
                float wide = crown * pow(1.0 - (float)tier / tiers, 0.9);
                float narrow = tier == tiers - 1 ? 0.0 : 0.3 * wide;
                float rim = f < 0.01 ? 1.0 + 0.18 * sin(7.0 * angle + seed + tier * 2.3) * sin(3.0 * angle + seed * 0.7) : 1.0;
                float r = lerp(wide, narrow, sqrt(f)) * rim;

                return float3(cos(angle) * r, lerp(bottom - 0.08 * wide, top, f), sin(angle) * r);

            }

            float t = c / (float)(rings - 1);
            // A broadleaf crown is a cluster of smaller crowns: lumps at three scales, and a flatter, wider top.
            float lumps = 1.0 + 0.22 * sin(3.0 * angle + seed + 2.0 * t) * sin(5.0 * t + seed * 0.3) + 0.14 * sin(5.0 * angle + seed * 1.7) * cos(9.0 * t + seed)
                + 0.08 * sin(9.0 * angle + seed * 2.9) * sin(13.0 * t + seed * 1.3);
            float r = crown * pow(max(sin(PI * lerp(0.02, 1.0, t * t * (1.5 - 0.5 * t))), 0.0), 0.5) * lumps;

            return float3(cos(angle) * r, base + (height - base) * t, sin(angle) * r);

        }

        Varyings Vertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID) {

            Plant plant = _Plants[instance];
            float3 root = _PatchPosition.xyz + Rotate(_PatchRotation, plant.position.xyz);
            float distance = length(root - _WorldSpaceCameraPos) * 1000.0;
            // Past the near trees, a share falling with the square of distance stands: those whose random rank is under it.
            float keep = saturate(TREE_NEAR * TREE_NEAR * 2.0 / (distance * distance));
            bool here = distance >= _Band.x && distance < _Band.y && (_Thin == 0 || plant.position.w < keep);

            Varyings output = (Varyings)0;

            if (!here) {

                output.positionCS = asfloat(0x7FC00000u);

                return output;

            }

            int ring = vertex / (_Segments + 1);
            int segment = vertex % (_Segments + 1);
            float step = 2.0 * PI / _Segments;
            float angle = segment * step + plant.shape.w * 6.2832;

            float3 p = Profile(plant, ring, angle);
            float3 around = Profile(plant, ring, angle + 0.25 * step) - Profile(plant, ring, angle - 0.25 * step);
            float3 upward = Profile(plant, min(ring + 1, (int)_Rings - 1), angle) - Profile(plant, max(ring - 1, 0), angle);
            float3 normal = cross(upward, around);

            // At a crown's tip the ring has shrunk to a point and has no tangent; the tip faces up.
            float3 n = dot(normal, normal) > 1e-12 ? normalize(normal) : float3(0.0, 1.0, 0.0);

            if (dot(n, float3(p.x, 0.0, p.z)) < 0.0) {

                n = -n;

            }

            // Shrink into the canopy short of the reach.
            float scale = saturate((TREE_REACH - distance) / (0.2 * TREE_REACH));

            float3 up = normalize(root - _PlanetCentre);
            float3 side = normalize(cross(up, abs(up.y) < 0.9 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0)));
            float3 ahead = cross(side, up);
            float3x3 frame = float3x3(side, up, ahead);

            float height = plant.shape.x;
            bool leaves = ring >= TRUNK_RINGS - 1;
            float3 centre = float3(0.0, lerp(plant.colour.w > 0.5 ? 0.3 : 0.4, 1.0, 0.45) * height, 0.0);
            float3 soft = leaves ? normalize(lerp(n, normalize(p - centre), 0.5)) : n;

            output.positionWS = root + mul(p * scale, frame) / 1000.0;
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.normalWS = mul(soft, frame);
            output.local = p;
            output.colour = plant.colour.rgb;
            output.foliage = leaves ? 1.0 : 0.0;
            output.occlusion = leaves ? lerp(0.6, 1.0, saturate(0.5 + 0.5 * n.y)) * lerp(0.7, 1.0, saturate(p.y / height)) : 0.5;

            return output;

        }

        float4 ShadowVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID) : SV_POSITION {

            // Depth bias only, as for rocks: a far cascade's normal bias would shrink a tree to nothing.
            float3 positionWS = Vertex(vertex, instance).positionWS;

            return ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, 0.0, _LightDirection)));

        }

        ENDHLSL

        Pass {

            Name "Tree"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            float4 Frag(Varyings input) : SV_Target {

                float3 n = normalize(input.normalWS);
                float3 toCamera = normalize(_WorldSpaceCameraPos - input.positionWS);
                bool leaves = input.foliage > 0.5;
                int slice = leaves ? FOREST_SLICE : ROCK_SLICE;

                // Leaf texture on the plane the crown most faces there, in the tree's own frame so it stays put, at two
                // scales: leaves, and clumps of foliage a few metres across whose hollows fall into shade, which keep a
                // crown from reading as a smooth ball from afar.
                float3 a = abs(input.local);
                float3 p = input.local / LEAF_TILE;
                float3 q = input.local / CLUMP_TILE + 0.37;
                bool top = a.y > a.x && a.y > a.z;
                float2 uv = top ? p.xz : a.x > a.z ? p.zy : p.xy;
                float2 clumpUv = top ? q.xz : a.x > a.z ? q.zy : q.xy;
                float4 texel = SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, uv, slice);
                float3 bump = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, uv, slice));
                float luma = dot(texel.rgb, float3(0.2126, 0.7152, 0.0722)) / (leaves ? FOREST_LUMA : ROCK_LUMA);
                float4 clump = leaves ? SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, clumpUv, FOREST_SLICE) : float4(0.0, 0.0, 0.0, 0.5);
                float3 clumpBump = leaves ? UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, clumpUv, FOREST_SLICE)) : float3(0.0, 0.0, 1.0);

                float3 tangent = normalize(cross(n, abs(n.y) < 0.9 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0)));
                float3 bitangent = cross(n, tangent);

                n = normalize(n + (tangent * (0.3 * bump.x + 0.7 * clumpBump.x) + bitangent * (0.3 * bump.y + 0.7 * clumpBump.y)));
                n = dot(n, toCamera) < -0.2 ? -n : n;

                float3 albedo = leaves ? input.colour * lerp(1.0, luma, 0.6) * lerp(0.5, 1.25, clump.a) : float3(0.09, 0.07, 0.05) * luma;
                Sunlight light = SunlightAt(input.positionWS);
                float3 lit = GroundRadiance(albedo, n, light, albedo, input.occlusion);
                float behind = leaves ? saturate(dot(-n, _SunDirection)) : 0.0;

                return float4(lit + light.direct * light.shadow * behind * albedo * 0.3 / PI, 1.0);

            }

            ENDHLSL

        }

        Pass {

            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ColorMask 0
            Cull Off

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex ShadowVertex
            #pragma fragment Frag

            void Frag() {

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
            #pragma fragment Frag

            float Frag(Varyings input) : SV_Target {

                return input.positionCS.z;

            }

            ENDHLSL

        }

    }

}
