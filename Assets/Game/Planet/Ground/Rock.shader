// Rocks strewn over the ground near the camera, drawn procedurally: each instance picks one of the shapes in a shared
// vertex buffer. The ground's rock material, mapped triplanar in each rock's frame so it stays put on the stone, takes
// the colour the climate gives the ground's own rock (see Biome.hlsl); its base is dirtied with the colour of the ground
// it stands on, stone facing the sky in green country gathers moss, and the ground around it bounces its colour back.
// Each rock sits partly buried; its lower flanks see less sky.
Shader "MaxQ/Rock" {

    Properties {

        _GroundAlbedo ("Ground Albedo", 2DArray) = "" {}
        _GroundNormal ("Ground Normals", 2DArray) = "" {}

    }

    SubShader {

        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE

        #include "Sunlight.hlsl"
        #include "Biome.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

        TEXTURE2D_ARRAY(_GroundAlbedo);
        TEXTURE2D_ARRAY(_GroundNormal);
        SAMPLER(sampler_GroundAlbedo);

        struct RockVertex {

            float3 position;
            float3 normal;

        };

        // Per instance, as Rocks writes them: position (scene) and size (km); orientation; shape pick, the ground's
        // vegetation and aridity, and one for an outcrop.
        StructuredBuffer<RockVertex> _RockVertices;
        StructuredBuffer<float4> _RockInstances;
        uint _RockShapeVertices;
        uint _RockInstanceOffset;

        // Slice of the ground arrays holding rock; GroundMaterials.hlsl names the same.
        #define ROCK_SLICE 4
        #define ROCK_SHAPES 12

        // Metres of stone per texture repeat.
        #define ROCK_TILE 1.6

        // Set by URP while it renders a shadow cascade.
        float3 _LightDirection;

        struct Varyings {

            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 positionOS : TEXCOORD1;
            float3 normalOS : TEXCOORD2;
            nointerpolation float4 turn : TEXCOORD3;
            nointerpolation float4 ground : TEXCOORD4;
            nointerpolation float scale : TEXCOORD5;
            nointerpolation float4 stone : TEXCOORD6;

        };

        float3 Rotate(float4 q, float3 v) {

            return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);

        }

        Varyings Vertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID) {

            uint index = 3 * (instance + _RockInstanceOffset);
            float4 placed = _RockInstances[index];
            float4 turn = _RockInstances[index + 1];
            float4 pick = _RockInstances[index + 2];
            uint shape = min((uint)(pick.x * ROCK_SHAPES), ROCK_SHAPES - 1u);
            RockVertex v = _RockVertices[shape * _RockShapeVertices + vertex];

            Varyings output;
            output.positionWS = placed.xyz + Rotate(turn, v.position * placed.w);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.positionOS = v.position;
            output.normalOS = v.normal;
            output.turn = turn;

            // The ground's colour under the rock and its vegetation, the stone's recolouring and whether it is bedrock,
            // and metres of stone per unit of mesh.
            GroundCover cover = (GroundCover)0;
            cover.vegetation = pick.y;
            cover.arid = pick.z;

            float3 up = normalize(placed.xyz - _PlanetCentre);
            float warmth = Warmth(up, (length(placed.xyz - _PlanetCentre) - _PlanetRadius) * 1000.0);

            output.ground = float4(CoverColour(cover, warmth), pick.y);
            output.stone = float4(Tint(ROCK, cover, warmth), pick.w);
            output.scale = placed.w * 1000.0;

            return output;

        }

        float4 ShadowVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID) : SV_POSITION {

            // Depth bias only: URP's normal bias moves each vertex a shadow texel inward, and a far cascade's texels are
            // tens of metres, which would turn a rock inside out into a hill-sized shadow.
            float3 positionWS = Vertex(vertex, instance).positionWS;

            return ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, 0.0, _LightDirection)));

        }

        ENDHLSL

        Pass {

            Name "Rock"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            float4 Frag(Varyings input) : SV_Target {

                float3 n = normalize(input.normalOS);
                float3 weights = pow(abs(n), 4.0);
                weights /= weights.x + weights.y + weights.z;

                float3 p = input.positionOS * input.scale / ROCK_TILE;
                float4 albedo = 0.0;
                float3 normal = 0.0;

                // Triplanar with whiteout-blended normals, in the rock's own frame.
                float3 tx = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, p.zy, ROCK_SLICE));
                float3 ty = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, p.xz, ROCK_SLICE));
                float3 tz = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, p.xy, ROCK_SLICE));

                albedo += SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, p.zy, ROCK_SLICE) * weights.x;
                albedo += SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, p.xz, ROCK_SLICE) * weights.y;
                albedo += SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, p.xy, ROCK_SLICE) * weights.z;

                normal += float3(tx.xy + n.zy, abs(tx.z) * n.x).zyx * weights.x;
                normal += float3(ty.xy + n.xz, abs(ty.z) * n.y).xzy * weights.y;
                normal += float3(tz.xy + n.xy, abs(tz.z) * n.z) * weights.z;

                float3 ground = input.ground.rgb;
                float3 stone = albedo.rgb * input.stone.rgb;

                // Moss on the tops of stones in green country; soil splashed up the base, deepest in the texture's pits.
                // Outcrops are bedrock, bare but for their buried foot.
                float moss = input.ground.a * (1.0 - input.stone.w) * saturate((n.y - 0.35) / 0.3) * saturate(1.2 - 2.0 * albedo.a);
                float dirt = saturate((-0.1 - input.positionOS.y) / 0.2 + (0.5 - albedo.a) * 0.8);

                stone = lerp(stone, ground * float3(0.8, 0.95, 0.7), moss);
                stone = lerp(stone, ground, dirt * 0.85);

                float3 normalWS = normalize(Rotate(input.turn, normalize(normal)));
                Sunlight light = SunlightAt(input.positionWS);

                // The buried base sits at -0.3 of the mesh; below the waist the ground hides more and more of the sky.
                float occlusion = saturate(0.35 + 0.65 * (input.positionOS.y + 0.3) / 0.8);

                return float4(GroundRadiance(stone, normalWS, light, ground, occlusion), 1.0);

            }

            ENDHLSL

        }

        Pass {

            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ColorMask 0

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
