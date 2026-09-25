// Rocks strewn over the ground near the camera, drawn procedurally: each instance picks one of the shapes in a shared
// vertex buffer. The ground's rock material, mapped triplanar in each rock's frame so it stays put on the stone, takes
// on part of the colour of the satellite pixel it stands in; its base is dirtied with the ground's own colour, stone
// facing the sky in green country gathers moss, and the ground around it bounces its colour back. Each rock sits partly
// buried; its lower flanks see less sky.
Shader "MaxQ/Rock" {

    Properties {

        _GroundAlbedo ("Ground Albedo", 2DArray) = "" {}
        _GroundNormal ("Ground Normals", 2DArray) = "" {}

    }

    SubShader {

        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE

        #include "Sunlight.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

        TEXTURE2D_ARRAY(_GroundAlbedo);
        TEXTURE2D_ARRAY(_GroundNormal);
        SAMPLER(sampler_GroundAlbedo);

        // The satellite tile of the patch these rocks belong to, and where the patch lies on it.
        TEXTURE2D(_Colour);
        SAMPLER(sampler_Colour);
        float4 _ColourRect;

        struct RockVertex {

            float3 position;
            float3 normal;

        };

        // Per instance, as Rocks writes them: position (scene) and size (km); orientation; shape pick, place on the
        // patch, and one for an outcrop.
        StructuredBuffer<RockVertex> _RockVertices;
        StructuredBuffer<float4> _RockInstances;
        uint _RockShapeVertices;
        uint _RockInstanceOffset;

        // Slice of the ground arrays holding rock, and its mean albedo; GroundMaterials.hlsl names the same.
        #define ROCK_SLICE 4
        #define ROCK_AVERAGE float3(0.0729, 0.0683, 0.0576)
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

            // The ground's colour under the rock, and metres of stone per unit of mesh.
            output.ground = float4(SAMPLE_TEXTURE2D_LOD(_Colour, sampler_Colour, pick.yz * _ColourRect.xy + _ColourRect.zw, 2.0).rgb, pick.w);
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
                float3 tone = LinearToSRGB(ground);
                float green = saturate((tone.g / max(tone.r + tone.g + tone.b, 1e-4) - 0.36) / 0.08);

                // The stone takes on part of the local colour: pale in snow country, red in the desert.
                float3 stone = albedo.rgb * lerp(1.0, clamp(ground / ROCK_AVERAGE, 0.3, 3.0), 0.35);

                // Moss on the tops of stones in green country; soil splashed up the base, deepest in the texture's pits.
                // Outcrops are bedrock, bare but for their buried foot.
                float moss = green * (1.0 - input.ground.a) * saturate((n.y - 0.35) / 0.3) * saturate(1.2 - 2.0 * albedo.a);
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
