// What the water sees of the scene behind and around it, from the copies WaterPass takes of the opaque scene's colour
// and depth before the water is drawn: the bed through the surface, bent by it, and nearby scenery mirrored in it.
#ifndef MAXQ_WATER_SCENE_INCLUDED
#define MAXQ_WATER_SCENE_INCLUDED

TEXTURE2D(_WaterSceneColour);
TEXTURE2D_FLOAT(_WaterSceneDepth);

// The surface bends what lies under it by up to this share of the screen, where the water is deep and near.
#define REFRACTION_STRENGTH 0.08

// Screen-space reflections march this many steps, each this much longer than the last, and are left to the sky's
// reflection where the surface is rougher than ROUGHEST_MIRROR.
#define REFLECTION_STEPS 12
#define REFLECTION_GROWTH 1.8
#define ROUGHEST_MIRROR 0.01

// Before marching, the ray's path across the screen is looked along at this mip of the scene's depth, where a texel
// spans sixteen pixels: only a path that crosses something solid is marched.
#define SCENERY_MIP 4.0
#define SCENERY_PROBES 4

// Eye depth (km) of the opaque scene at uv; the sky, which leaves the reversed depth at zero, is infinitely far.
float SceneEyeDepth(float2 uv) {

    float raw = SAMPLE_TEXTURE2D_LOD(_WaterSceneDepth, sampler_linear_clamp, uv, 0.0).r;

    return raw <= 0.0 ? 1e9 : LinearEyeDepth(raw, _ZBufferParams);

}

float2 ClipToUv(float4 positionCS) {

    float2 ndc = positionCS.xy / positionCS.w;

    #if UNITY_UV_STARTS_AT_TOP
    return float2(0.5 + 0.5 * ndc.x, 0.5 - 0.5 * ndc.y);
    #else
    return 0.5 + 0.5 * ndc;
    #endif

}

struct Refraction {

    float3 colour;
    float path;

};

// The bed seen through the surface: its colour, and the metres (on Terra) the view travels through water to reach it,
// or a path too long for any light to return where the scene behind is sky.
Refraction RefractedBed(float4 positionCS, float3 positionWS, float3 normal, float3 up, float depth) {

    float2 uv = positionCS.xy / _ScaledScreenParams.xy;
    float waterEye = LinearEyeDepth(positionCS.z, _ZBufferParams);
    float sceneEye = SceneEyeDepth(uv);
    float gap = max(sceneEye - waterEye, 0.0) * 1000.0;
    float3 tilt = TransformWorldToViewDir(normal - up);
    float2 bent = uv + float2(tilt.x, -tilt.y) * REFRACTION_STRENGTH * saturate(gap / 3.0) / max(waterEye * 20.0, 1.0);
    float bentEye = SceneEyeDepth(bent);

    // What lies in front of the water is not seen through it.
    if (bentEye < waterEye) {

        bent = uv;
        bentEye = sceneEye;

    }

    Refraction refraction;
    refraction.colour = SAMPLE_TEXTURE2D_LOD(_WaterSceneColour, sampler_linear_clamp, bent, 0.0).rgb;
    refraction.path = max((bentEye - waterEye) * length(positionWS - _WorldSpaceCameraPos) / waterEye * 1000.0, depth);

    if (bentEye > 1e8) {

        refraction.colour = 0.0;
        refraction.path = 1e5;

    }

    return refraction;

}

// Scenery the surface mirrors: the reflected ray marched across the screen against the opaque scene's depth, in steps
// that lengthen with distance, the hit refined by bisection. The air between the hit and the water hazes it toward the
// sky seen that way (sky, the mirrored sky already looked up), by the air's extinction over the distance, as the
// composite hazes everything between the water and the eye. Alpha is how far the hit can be trusted: not at all on
// rough water, off the screen's edges, or where the ray finds only sky.
float4 ScreenReflection(float3 positionWS, float3 direction, float3 up, float variance, float3 sky) {

    if (variance > ROUGHEST_MIRROR || dot(direction, up) <= 0.0) {

        return 0.0;

    }

    // A point along a ray moves linearly in clip space, so each step is one multiply-add.
    float4 origin = TransformWorldToHClip(positionWS);
    float4 heading = TransformWorldToHClip(positionWS + direction) - origin;
    float near = max(length(positionWS - _WorldSpaceCameraPos) * 0.01, 5e-4);

    // The ray's path runs from here toward its vanishing point, where it would reach the sky; over open water nothing
    // on it can be hit.
    float2 from = ClipToUv(origin);
    float4 farClip = origin + heading * 1e4;
    float2 to = farClip.w > 0.0 ? ClipToUv(farClip) : from;
    bool scenery = false;

    [unroll]
    for (int p = 1; p <= SCENERY_PROBES; p++) {

        float2 at = lerp(from, to, p / (float)SCENERY_PROBES);

        scenery = scenery || SAMPLE_TEXTURE2D_LOD(_WaterSceneDepth, sampler_linear_clamp, saturate(at), SCENERY_MIP).r > 0.0;

    }

    if (!scenery) {

        return 0.0;

    }

    float t = near;
    float previous = 0.0;
    float hit = -1.0;
    float2 last = -1.0;

    [loop]
    for (int i = 0; i < REFLECTION_STEPS; i++) {

        float4 clip = origin + heading * t;
        float2 uv = ClipToUv(clip);

        // A ray heading for the horizon crawls toward its vanishing point; once a step moves it less than a pixel,
        // nothing farther along can come into view.
        if (clip.w <= 0.0 || any(uv < 0.0) || any(uv > 1.0) || all(abs(uv - last) * _ScaledScreenParams.xy < 1.0)) {

            break;

        }

        float behind = clip.w - SceneEyeDepth(uv);

        if (behind > 0.0 && behind < 2.0 * (t - previous) + near) {

            hit = t;

            break;

        }

        last = uv;
        previous = t;
        t *= REFLECTION_GROWTH;

    }

    if (hit < 0.0) {

        return 0.0;

    }

    // Bisect between the last step in front of the scene and the first behind it.
    float low = previous;

    [loop]
    for (int j = 0; j < 5; j++) {

        float middle = 0.5 * (low + hit);
        float4 probe = origin + heading * middle;

        if (probe.w > SceneEyeDepth(ClipToUv(probe))) {

            hit = middle;

        } else {

            low = middle;

        }

    }

    float2 uv = ClipToUv(origin + heading * hit);
    float2 edge = saturate(min(uv, 1.0 - uv) * 10.0);
    float3 colour = SAMPLE_TEXTURE2D_LOD(_WaterSceneColour, sampler_linear_clamp, uv, 0.0).rgb;
    float3 clear = exp(-SampleMedium(length(positionWS - _PlanetCentre) - _PlanetRadius).extinction * hit);

    return float4(lerp(sky, colour, clear), edge.x * edge.y * saturate(1.0 - variance / ROUGHEST_MIRROR));

}

#endif
