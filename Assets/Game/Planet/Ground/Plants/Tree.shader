// Trees, drawn procedurally from cards cut out of the foliage atlas: a trunk that turns about its axis to face the viewer,
// and a crown. A conifer's crown is whorls of branch sprays reaching out from the trunk, drooping low down and shortening
// up to a spire; a broadleaf's is clumps of leaves spread over a lumpy dome. Branch sprays roll about their own length to
// face halfway between the sky and the viewer and clumps face the viewer, so no card is ever seen edge-on; the shadow
// pass turns them to the sun instead. Each tree a patch strews has its own size, climate and turn; a far detail takes
// every third card of the same layout, grown to fill in. Further out a tree is a single card cut from its painted
// picture, and further still each grove card stands for the trees of its cell; Vegetation sets the distances. Foliage
// takes the colour its climate gives it (see Biome.hlsl) shaded by the atlas, with normals rounded over the whole crown
// and bent by each leaf, or painted into the picture, darkening into the crown, and light through the leaves from
// behind; bark is dark.
Shader "MaxQ/Tree" {

    Properties {

        _Foliage ("Foliage", 2DArray) = "" {}
        _GroundAlbedo ("Ground Albedo", 2DArray) = "" {}

    }

    SubShader {

        Tags { "RenderType" = "Opaque" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE

        #include "../Sunlight.hlsl"
        #include "../Biome.hlsl"

        TEXTURE2D_ARRAY(_Foliage);
        SAMPLER(sampler_Foliage);
        TEXTURE2D_ARRAY(_GroundAlbedo);
        SAMPLER(sampler_GroundAlbedo);

        // As PatchStrewJob writes them: position (km from the patch origin) and a random number; aridity, warmth, another
        // random number and one for needles; height and crown width (m), and under a grove the ground's normal.
        struct Plant {

            float4 position;
            float4 climate;
            float4 shape;

        };

        StructuredBuffer<Plant> _Plants;
        float4 _PatchPosition;
        float4 _PatchRotation;
        // Cards per tree in this draw's detail, the trunk's included; one draws each tree or grove as its picture.
        uint _Cards;
        // Metres from the camera within which this draw's trees stand, each tree's own distance jittered so the details
        // meet raggedly; metres over which they fade in and out, tree by tree, as the next level takes over.
        float2 _Band;
        float4 _Fade;
        // Metres across a grove's cell, or zero for trees.
        float _Grove;

        // Foliage cards in the full layout; a detail with fewer takes every so many of them. Must match Vegetation.
        #define FOLIAGE 72u

        // Slices of the foliage atlas (FoliageAtlas names the same; each picture's conifer follows its broadleaf) and of
        // the ground arrays, with the rock's mean albedo.
        #define BROADLEAF_SLICE 0.0
        #define CONIFER_SLICE 1.0
        #define STAND_SLICE 2.0
        #define GROVE_SLICE 4.0
        #define ROCK_SLICE 4
        #define ROCK_LUMA 0.068

        // FoliageAtlas bakes its coverage for this cutoff.
        #define CUTOFF 0.5

        // How far each leaf's own normal bends the crown's; the share of sunlight that passes through a leaf.
        #define LEAF_BUMP 0.6
        #define TRANSMISSION 0.35

        // Metres toward the sun the foliage looks for its shadow: the far cards that cast it stand a little proud of the
        // near ones drawn, and would otherwise speckle the sunlit side of their own crown.
        #define CROWN_CLEARANCE 1.5

        // A picture leans back this share of the way toward a viewer above it, and sinks this many metres into the ground.
        // A grove card is so much wider than its cell, to overlap its neighbours, and so much taller than its trees' mean.
        #define LEAN 0.5
        #define STAND_SINK 0.5
        #define GROVE_SPREAD 1.5
        #define GROVE_HEIGHT 1.1

        // Set by URP while it renders a shadow cascade.
        float3 _LightDirection;

        struct Varyings {

            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float3 tangentWS : TEXCOORD2;
            float3 bitangentWS : TEXCOORD3;
            // The card's texture coordinates and atlas slice; bark has a negative slice, and its v runs in metres.
            float3 uv : TEXCOORD4;
            nointerpolation float3 colour : TEXCOORD5;
            float occlusion : TEXCOORD6;

        };

        float3 Rotate(float4 q, float3 v) {

            return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);

        }

        // Independent random numbers in [0, 1) for each card of a tree.
        float Random(Plant plant, uint card, uint salt) {

            uint h = asuint(plant.position.w) ^ card * 0x9E3779B9u ^ salt * 0x85EBCA6Bu;

            h = (h ^ (h >> 16)) * 0x7FEB352Du;
            h = (h ^ (h >> 15)) * 0x846CA68Bu;

            return (h ^ (h >> 16)) / 4294967296.0;

        }

        // A card of the tree in its own frame (metres, y up), at uv: the point, its normal for lighting, and the card's
        // axes across (u) and along (v).
        struct Card {

            float3 position;
            float3 normal;
            float3 across;
            float3 along;
            float3 uv;
            float occlusion;

        };

        // Toward whoever sees the card, in the tree's frame: the sun for the shadow pass, else the camera.
        float3 Toward(float3 place, float3 root, float3x3 frame, bool shadow) {

            float3 toward = shadow ? _LightDirection : _WorldSpaceCameraPos - (root + mul(place, frame) / 1000.0);

            return normalize(mul(frame, toward));

        }

        // The trunk turns about its axis to face the viewer; the fragment rounds it. It runs up into the crown, darkening
        // as the foliage closes over it.
        Card Trunk(Plant plant, float2 uv, float3 root, float3x3 frame, bool shadow) {

            float height = plant.shape.x;
            bool needles = plant.climate.w > 0.5;
            float top = (needles ? 0.8 : 0.62) * height;
            float radius = (0.05 + 0.006 * height) * lerp(1.2, 0.45, uv.y);
            float3 toward = Toward(float3(0.0, 0.5 * top, 0.0), root, frame, shadow);
            float3 flat = float3(toward.x, 0.0, toward.z);

            flat = dot(flat, flat) > 1e-6 ? normalize(flat) : float3(0.0, 0.0, 1.0);

            Card card;
            card.across = float3(flat.z, 0.0, -flat.x);
            card.along = float3(0.0, 1.0, 0.0);
            card.position = float3(0.0, lerp(-0.5, top, uv.y), 0.0) + card.across * (2.0 * uv.x - 1.0) * radius;
            card.normal = flat;
            card.uv = float3(uv.x, uv.y * (top + 0.5) / 1.5, -1.0);
            card.occlusion = lerp(0.55, 0.1, uv.y);

            return card;

        }

        // Branch sprays in whorls from the base of the crown up, each whorl's turned by the golden angle from the last;
        // low branches reach furthest and droop, high ones shorten and rise into the spire. Lighting sees a cone.
        Card Branch(Plant plant, uint index, float fill, float2 uv, float3 root, float3x3 frame, bool shadow) {

            const uint perWhorl = 6u;
            const uint whorls = FOLIAGE / perWhorl;

            float height = plant.shape.x;
            float crown = plant.shape.y;
            uint whorl = index / perWhorl;
            uint branch = index % perWhorl;
            uint salt = index + 1u;

            float base = height * lerp(0.15, 0.35, Random(plant, 0u, 1u));
            float f = (whorl + 0.2 + 0.6 * Random(plant, salt, 2u)) / whorls;
            float reach = crown * 1.15 * pow(saturate(1.0 - f), 0.8) * lerp(0.8, 1.15, Random(plant, salt, 3u)) + 0.5;
            float azimuth = (branch + 0.6 * Random(plant, salt, 4u)) * 2.0 * PI / perWhorl + whorl * 2.39996 + Random(plant, 0u, 9u) * 2.0 * PI;
            float pitch = -0.45 + 0.6 * f + smoothstep(0.82, 1.0, f) + 0.25 * (Random(plant, salt, 5u) - 0.5);
            float3 direction = float3(cos(pitch) * cos(azimuth), sin(pitch), cos(pitch) * sin(azimuth));
            float3 start = float3(0.0, lerp(base, 0.97 * height, f), 0.0);

            // Roll the spray about its length to face halfway between the sky and the viewer.
            float3 skyward = normalize(float3(0.0, 1.0, 0.0) - direction * direction.y);
            float3 toward = Toward(start + 0.5 * reach * direction, root, frame, shadow);
            float3 facing = toward - direction * dot(toward, direction);

            facing = dot(facing, facing) > 1e-6 ? normalize(facing) : skyward;
            skyward = dot(skyward, facing) < 0.0 ? -skyward : skyward;
            facing = normalize(skyward + facing);

            Card card;
            card.along = direction;
            card.across = normalize(cross(direction, facing));
            card.position = start + direction * reach * uv.y + card.across * reach * 0.8 * pow(fill, 1.5) * (uv.x - 0.5);

            float3 radial = float3(card.position.x, 0.0, card.position.z);
            radial = dot(radial, radial) > 1e-4 ? normalize(radial) : normalize(float3(direction.x, 0.0, direction.z) + 1e-4);

            card.normal = normalize(lerp(normalize(radial * (height - base) + float3(0.0, crown, 0.0)), facing, 0.25));
            card.uv = float3(uv, CONIFER_SLICE);
            card.occlusion = lerp(0.3, 1.0, pow(saturate(f), 0.6)) * lerp(0.55, 1.0, uv.y);

            return card;

        }

        // Leaf clumps spread over a dome by the golden angle, top first and fewer underneath, each set in or out a little
        // and the dome swelling into a few lobes. Lighting sees the dome, darker toward its middle and underside.
        Card Clump(Plant plant, uint index, float fill, float2 uv, float3 root, float3x3 frame, bool shadow) {

            float height = plant.shape.x;
            float crown = plant.shape.y;
            uint salt = index + 1u;
            float seed = Random(plant, 0u, 10u) * 40.0;

            float base = height * lerp(0.3, 0.45, Random(plant, 0u, 1u));
            float3 centre = float3(0.0, lerp(base, height, 0.52), 0.0);
            float3 radii = float3(crown, 0.5 * (height - base), crown);

            float y = lerp(1.0, -0.55, (index + 0.5) / FOLIAGE);
            float azimuth = index * 2.39996 + Random(plant, 0u, 9u) * 2.0 * PI + 0.4 * (Random(plant, salt, 2u) - 0.5);
            float3 direction = float3(sqrt(1.0 - y * y) * cos(azimuth), y, sqrt(1.0 - y * y) * sin(azimuth));
            float lobes = 1.0 + 0.22 * sin(3.0 * azimuth + seed) * cos(2.5 * y + seed * 0.7) + 0.1 * sin(5.0 * azimuth + seed * 1.3);
            float3 place = centre + direction * radii * lerp(0.55, 0.95, Random(plant, salt, 3u)) * lobes;
            float size = crown * lerp(0.5, 0.75, Random(plant, salt, 4u)) * fill;

            float3 toward = Toward(place, root, frame, shadow);
            float3 right = cross(float3(0.0, 1.0, 0.0), toward);
            right = dot(right, right) > 1e-6 ? normalize(right) : float3(1.0, 0.0, 0.0);
            float3 upward = cross(toward, right);
            float roll = Random(plant, salt, 5u) * 2.0 * PI;

            Card card;
            card.across = right * cos(roll) + upward * sin(roll);
            card.along = upward * cos(roll) - right * sin(roll);
            card.position = place + (card.across * (uv.x - 0.5) + card.along * (uv.y - 0.5)) * size;

            float3 inside = (card.position - centre) / radii;

            card.normal = normalize(inside / radii);
            card.uv = float3(uv, BROADLEAF_SLICE);
            card.occlusion = lerp(0.3, 1.0, saturate(dot(inside, inside))) * lerp(0.75, 1.0, 0.5 + 0.5 * card.normal.y);

            return card;

        }

        // An octahedral unit vector, as PatchJob folds it.
        float3 Unfold(float2 e) {

            float3 n = float3(e.x, 1.0 - abs(e.x) - abs(e.y), e.y);

            if (n.y < 0.0) {

                n.xz = (1.0 - abs(n.zx)) * (n.xz >= 0.0 ? 1.0 : -1.0);

            }

            return normalize(n);

        }

        // A tree as one card cut from its painted picture, or a grove card standing for the trees of its cell. The card
        // turns to the viewer about the vertical and leans back part way when seen from above; a grove's foot follows the
        // ground's slope across it, so its trees neither float nor sink. Lighting takes the normals painted in, about the
        // upright card.
        Card Stand(Plant plant, float2 uv, float3 root, float3x3 frame) {

            bool needles = plant.climate.w > 0.5;
            bool grove = _Grove > 0.0;
            float height = plant.shape.x * (grove ? GROVE_HEIGHT : 1.0);
            float width = grove ? GROVE_SPREAD * _Grove : 2.2 * plant.shape.y;
            float3 toward = Toward(float3(0.0, 0.5 * height, 0.0), root, frame, false);
            float3 flat = float3(toward.x, 0.0, toward.z);

            flat = dot(flat, flat) > 1e-6 ? normalize(flat) : float3(0.0, 0.0, 1.0);

            float3 across = float3(flat.z, 0.0, -flat.x);
            float3 lean = normalize(lerp(float3(0.0, 1.0, 0.0), cross(toward, across), LEAN));
            float3 ground = mul(frame, Rotate(_PatchRotation, Unfold(plant.shape.zw)));
            float slope = grove ? clamp(-dot(ground, across) / max(ground.y, 0.2), -1.5, 1.5) : 0.0;
            float x = (uv.x - 0.5) * width;

            Card card;
            card.across = across;
            card.along = float3(0.0, 1.0, 0.0);
            card.position = across * x + lean * (uv.y * height) + float3(0.0, x * slope - STAND_SINK, 0.0);
            card.normal = flat;
            card.uv = float3(uv, (grove ? GROVE_SLICE : STAND_SLICE) + (needles ? 1.0 : 0.0));
            card.occlusion = 1.0;

            return card;

        }

        Varyings TreeVertex(uint vertex, uint instance, bool shadow) {

            Plant plant = _Plants[instance];
            float3 root = _PatchPosition.xyz + Rotate(_PatchRotation, plant.position.xyz);
            float distance = length(root - _WorldSpaceCameraPos) * 1000.0;
            float jittered = distance * lerp(0.85, 1.15, Random(plant, 0u, 7u));
            float fade = min(smoothstep(_Fade.x, _Fade.y, distance), 1.0 - smoothstep(_Fade.z, _Fade.w, distance));
            bool here = jittered >= _Band.x && jittered < _Band.y && Random(plant, 0u, 8u) < fade;

            Varyings output = (Varyings)0;

            if (!here) {

                output.positionCS = asfloat(0x7FC00000u);

                return output;

            }

            float3 up = normalize(root - _PlanetCentre);
            float3 side = normalize(cross(up, abs(up.y) < 0.9 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0)));
            float3 ahead = cross(side, up);
            float3x3 frame = float3x3(side, up, ahead);

            uint card = vertex / 4u;
            float2 uv = float2(vertex & 1u, (vertex >> 1) & 1u);

            if (_Cards == 1u) {

                Card stand = Stand(plant, uv, root, frame);

                output.positionWS = root + mul(stand.position, frame) / 1000.0;
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = mul(stand.normal, frame);
                output.tangentWS = mul(stand.across, frame);
                output.bitangentWS = mul(stand.along, frame);
                output.uv = stand.uv;
                output.colour = FoliageColour(plant.climate.x, plant.climate.y, plant.climate.w > 0.5, float2(Random(plant, 0u, 12u), plant.climate.z));
                output.occlusion = stand.occlusion;

                return output;

            }

            uint stride = FOLIAGE / (_Cards - 1u);
            uint index = (card - 1u) * stride;
            // Fewer cards grow to cover as much: clumps both ways, branch sprays across only, as they cannot outreach the crown.
            float fill = sqrt((float)stride);
            bool needles = plant.climate.w > 0.5;

            Card c = (Card)0;

            if (card == 0u) {

                c = Trunk(plant, uv, root, frame, shadow);

            } else if (needles) {

                c = Branch(plant, index, fill, uv, root, frame, shadow);

            } else {

                c = Clump(plant, index, fill, uv, root, frame, shadow);

            }

            // The crown sways in the wind about its foot, each tree to its own time.
            float height = plant.shape.x;
            float gust = sin(_Time.y * 0.9 + Random(plant, 0u, 10u) * 40.0) + 0.5 * sin(_Time.y * 1.7 + Random(plant, 0u, 11u) * 30.0);
            float bend = saturate(c.position.y / height);

            c.position.xz += float2(0.8, 0.6) * gust * 0.006 * height * bend * bend;

            output.positionWS = root + mul(c.position, frame) / 1000.0;
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.normalWS = mul(c.normal, frame);
            output.tangentWS = mul(c.across, frame);
            output.bitangentWS = mul(c.along, frame);
            output.uv = c.uv;
            output.colour = FoliageColour(plant.climate.x, plant.climate.y, plant.climate.w > 0.5, float2(Random(plant, 0u, 12u), plant.climate.z));
            output.occlusion = c.occlusion;

            return output;

        }

        Varyings Vertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID) {

            return TreeVertex(vertex, instance, false);

        }

        // Leaves are cut out of their cards; bark is solid.
        void Cut(float3 uv) {

            if (uv.z >= 0.0) {

                clip(SAMPLE_TEXTURE2D_ARRAY(_Foliage, sampler_Foliage, uv.xy, uv.z).a - CUTOFF);

            }

        }

        struct ShadowVaryings {

            float4 positionCS : SV_POSITION;
            float3 uv : TEXCOORD0;

        };

        ShadowVaryings ShadowVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID) {

            Varyings tree = TreeVertex(vertex, instance, true);

            // Depth bias only, as for rocks: a far cascade's normal bias would shrink a tree to nothing.
            ShadowVaryings output;
            output.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(tree.positionWS, 0.0, _LightDirection)));
            output.uv = tree.uv;

            return output;

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

                Sunlight light = SunlightAt(input.positionWS);
                float3 toCamera = normalize(_WorldSpaceCameraPos - input.positionWS);

                UNITY_BRANCH
                if (input.uv.z < 0.0) {

                    // Bark: the trunk rounded across its card, streaked by the rock texture drawn out along it.
                    float across = 2.0 * input.uv.x - 1.0;
                    float3 n = normalize(normalize(input.tangentWS) * across + normalize(input.normalWS) * sqrt(saturate(1.0 - across * across)));
                    float4 rock = SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, float2(0.3 * input.uv.x, 0.25 * input.uv.y), ROCK_SLICE);
                    float3 bark = float3(0.085, 0.07, 0.055) * lerp(1.0, dot(rock.rgb, float3(0.2126, 0.7152, 0.0722)) / ROCK_LUMA, 0.5);

                    light.shadow *= saturate(2.0 * input.occlusion);

                    return float4(GroundRadiance(bark, n, light, bark, input.occlusion), 1.0);

                }

                float4 texel = SAMPLE_TEXTURE2D_ARRAY(_Foliage, sampler_Foliage, input.uv.xy, input.uv.z);

                clip(texel.a - CUTOFF);

                // A picture carries its whole normal; a card's leaves bend the crown's.
                float2 bump = 2.0 * texel.gb - 1.0;
                float3 tangent = normalize(input.tangentWS);
                float3 bitangent = normalize(input.bitangentWS);
                float3 n = input.uv.z >= STAND_SLICE
                    ? normalize(tangent * bump.x + bitangent * bump.y + normalize(input.normalWS) * sqrt(saturate(1.0 - dot(bump, bump))))
                    : normalize(normalize(input.normalWS) + (tangent * bump.x + bitangent * bump.y) * LEAF_BUMP);
                float3 albedo = input.colour * 2.0 * texel.r;
                float occlusion = input.occlusion;

                // Inside the crown the sun gets through only in flecks.
                light.shadow = SunShadow(input.positionWS + _SunDirection * (CROWN_CLEARANCE / 1000.0)) * saturate(1.3 * occlusion);

                float3 lit = GroundRadiance(albedo, n, light, albedo, occlusion);

                // Light through the leaves: the sun behind them as the camera sees them, most where they face away from it,
                // and the sky above through the undersides, dimmed by the leaves over them.
                float through = 0.5 * pow(saturate(dot(-toCamera, _SunDirection)), 3.0) + 0.5 * saturate(dot(-n, _SunDirection));
                float3 sky = light.sky * saturate(-dot(n, light.up)) * occlusion;

                return float4(lit + (light.direct * light.shadow * through + sky) * albedo * TRANSMISSION / PI, 1.0);

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

            void Frag(ShadowVaryings input) {

                Cut(input.uv);

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

                Cut(input.uv);

                return input.positionCS.z;

            }

            ENDHLSL

        }

    }

}
