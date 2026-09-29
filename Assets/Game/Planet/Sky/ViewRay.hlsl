// View rays through the full-resolution depth buffer, for the passes that march the air and the clouds after the opaque scene.
#ifndef MAXQ_VIEW_RAY_INCLUDED
#define MAXQ_VIEW_RAY_INCLUDED

TEXTURE2D_X(_SceneDepth);
float4 _SceneSize;

// Stand-in distance for sky pixels, and the cap on every other: large, but safe in half precision.
#define SKY_DISTANCE 60000.0

// Depth past which pixels belong to a stand-in body (GroundView); rays reaching it run on to the body's sphere.
float _StandInDepth;

// Reach: how far the air and clouds may march, unbounded for sky and stand-ins.
struct ViewRay {

    float3 direction;
    float distance;
    float reach;
    bool sky;

};

// Directions from the near plane and distances from linear depth: unprojecting far depths loses all precision.
ViewRay ViewRayThrough(float2 uv, float depth) {

    #if UNITY_REVERSED_Z
    bool sky = depth <= 0.0;
    #else
    bool sky = depth >= 1.0;
    #endif

    float3 near = ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP);

    ViewRay ray;
    ray.direction = normalize(near - _WorldSpaceCameraPos);
    ray.distance = sky ? SKY_DISTANCE : min(LinearEyeDepth(depth, _ZBufferParams) / dot(ray.direction, -UNITY_MATRIX_V[2].xyz), SKY_DISTANCE);
    ray.reach = sky || ray.distance >= _StandInDepth ? 1e9 : ray.distance;
    ray.sky = sky;

    return ray;

}

ViewRay ViewRayAt(float2 uv) {

    return ViewRayThrough(uv, SAMPLE_TEXTURE2D_X_LOD(_SceneDepth, sampler_PointClamp, uv, 0).r);

}

#endif
