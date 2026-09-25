// Constant-pixel-width, anti-aliased trajectory lines. Each segment is a quad expanded in screen space;
// NORMAL carries the neighbouring point, TEXCOORD0 = (side, direction sign).
Shader "MaxQ/MapLine" {

    Properties {

        _Width ("Width (px)", Float) = 2.0

    }

    SubShader {

        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass {

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float _Width;
            CBUFFER_END

            struct Attributes {

                float3 position : POSITION;
                float3 neighbour : NORMAL;
                float2 side : TEXCOORD0;
                float4 color : COLOR;

            };

            struct Varyings {

                float4 position : SV_POSITION;
                float4 color : COLOR;
                float edge : TEXCOORD0;

            };

            Varyings Vert(Attributes input) {

                float4 clip = TransformWorldToHClip(input.position);
                float4 other = TransformWorldToHClip(input.neighbour);

                // Pull a neighbour behind the camera back to the near side so the direction stays sane.
                const float nearW = 1e-4;

                if (other.w < nearW) {

                    other = lerp(clip, other, (clip.w - nearW) / max(clip.w - other.w, 1e-6));

                }

                float2 halfScreen = _ScreenParams.xy * 0.5;
                float2 here = clip.xy / max(clip.w, nearW) * halfScreen;
                float2 there = other.xy / max(other.w, nearW) * halfScreen;

                float2 along = there - here;
                float2 direction = dot(along, along) > 1e-8 ? normalize(along) * input.side.y : float2(1.0, 0.0);
                float2 normal = float2(-direction.y, direction.x);

                // One extra pixel each side is the anti-aliasing ramp.
                float halfWidth = _Width * 0.5 + 1.0;

                clip.xy += normal * input.side.x * halfWidth / halfScreen * clip.w;

                Varyings output;
                output.position = clip;
                output.color = input.color;
                output.edge = input.side.x * halfWidth;

                return output;

            }

            float4 Frag(Varyings input) : SV_Target {

                float coverage = saturate(_Width * 0.5 + 0.5 - abs(input.edge));

                return float4(input.color.rgb, input.color.a * coverage);

            }

            ENDHLSL

        }

    }

}
