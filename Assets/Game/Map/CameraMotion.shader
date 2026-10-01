// Motion vectors for temporal antialiasing: each pixel carried with the body the camera is over from last frame's view to
// this one's (see CameraMotion.cs).
Shader "Hidden/MaxQ/CameraMotion" {

    SubShader {

        ZTest Always
        ZWrite Off
        Cull Off

        Pass {

            Name "Motion"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "../Planet/Sky/ViewRay.hlsl"

            // This frame's and last frame's view-projections, both unjittered.
            float4x4 _MotionViewProjection;
            float4x4 _MotionPreviousViewProjection;

            // The body's centre, how far it and the scene's origin have moved apart since last frame, and its turn since
            // then less the identity; kept apart so a point near the camera never sums the body's radius in single precision.
            float3 _MotionCentre;
            float3 _MotionShift;
            float4x4 _MotionTurn;

            // Nearer than this (scene units) is a vessel, whose object motion URP has drawn; left as it is.
            float _MotionKeep;

            float2 ScreenUv(float4 clip) {

                #if UNITY_UV_STARTS_AT_TOP
                clip.y = -clip.y;
                #endif

                return clip.xy / clip.w * 0.5 + 0.5;

            }

            float2 Frag(Varyings input) : SV_Target {

                int2 pixel = int2(input.positionCS.xy);
                ViewRay ray = ViewRayThrough((pixel + 0.5) * _SceneSize.zw, LOAD_TEXTURE2D_X(_SceneDepth, pixel).r);

                if (!ray.sky && ray.distance < _MotionKeep) {

                    discard;

                }

                // The sky stands still in the scene's axes; everything else rides with the body.
                float4 here = float4(ray.direction, 0.0);
                float4 was = here;

                if (!ray.sky) {

                    here = float4(_WorldSpaceCameraPos + ray.direction * ray.distance, 1.0);
                    was = float4(here.xyz + _MotionShift + mul((float3x3)_MotionTurn, here.xyz - _MotionCentre), 1.0);

                }

                return ScreenUv(mul(_MotionViewProjection, here)) - ScreenUv(mul(_MotionPreviousViewProjection, was));

            }

            ENDHLSL

        }

    }

}
