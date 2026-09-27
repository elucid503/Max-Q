// Copies the opaque scene's depth into a colour target the water can sample while it draws into the depth itself.
Shader "Hidden/MaxQ/WaterCopy" {

    SubShader {

        Tags { "RenderPipeline" = "UniversalPipeline" }

        ZTest Always
        ZWrite Off
        Cull Off

        Pass {

            Name "Depth"

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float Frag(Varyings input) : SV_Target {

                return LOAD_TEXTURE2D_X_LOD(_BlitTexture, uint2(input.positionCS.xy), 0).r;

            }

            ENDHLSL

        }

    }

}
