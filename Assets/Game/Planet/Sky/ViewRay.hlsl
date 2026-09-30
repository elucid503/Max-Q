// View rays through the full-resolution depth buffer, for the passes that march the air and the clouds after the opaque scene.
#ifndef MAXQ_VIEW_RAY_INCLUDED
#define MAXQ_VIEW_RAY_INCLUDED

TEXTURE2D_X(_SceneDepth);
float4 _SceneSize;

// Stand-in distance for sky pixels, and the cap on every other: large, but safe in half precision. Scenery past it, as
// the whole planet seen from far off, is marched on to the planet itself like the sky.
#define SKY_DISTANCE 60000.0

// The spheres the bodies stand in within when drawn scaled down (GroundView), air included: scene centre and radius, zero
// when drawn true. Rays meeting one run on to the body itself.
float4 _TerraStandInSphere;
float4 _SeleneStandInSphere;

// How near the camera a pixel's scenery may lie, as a share of the way to a stand-in's sphere, and still be the stand-in:
// anything of the camera's own body stands far nearer, and the scene's depth need be no better than this.
#define STAND_IN_SHARE 0.5

// Scene distance along direction to where a stand-in's sphere begins, or -1 where the ray misses it or none stands in.
float StandInEntry(float3 direction, float4 sphere) {

    float3 toCentre = sphere.xyz - _WorldSpaceCameraPos;
    float along = dot(toCentre, direction);
    float3 miss = cross(toCentre, direction);
    float across = dot(miss, miss);

    if (sphere.w <= 0.0 || along <= 0.0 || across >= sphere.w * sphere.w) {

        return -1.0;

    }

    return max(along - sqrt(sphere.w * sphere.w - across), 0.0);

}

// How far direction looks into a stand-in's sphere, 1 within it, falling to 0 a quarter of its radius outside.
float StandInCover(float3 direction, float4 sphere) {

    float3 toCentre = sphere.xyz - _WorldSpaceCameraPos;

    if (sphere.w <= 0.0 || dot(toCentre, direction) <= 0.0) {

        return 0.0;

    }

    return 1.0 - smoothstep(1.0, 1.25, length(cross(toCentre, direction)) / sphere.w);

}

bool OnStandIn(float3 direction, float distance) {

    float terra = StandInEntry(direction, _TerraStandInSphere);
    float selene = StandInEntry(direction, _SeleneStandInSphere);

    return (terra >= 0.0 && distance > STAND_IN_SHARE * terra) || (selene >= 0.0 && distance > STAND_IN_SHARE * selene);

}

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
    ray.reach = sky || ray.distance >= SKY_DISTANCE || OnStandIn(ray.direction, ray.distance) ? 1e9 : ray.distance;
    ray.sky = sky;

    return ray;

}

ViewRay ViewRayAt(float2 uv) {

    return ViewRayThrough(uv, SAMPLE_TEXTURE2D_X_LOD(_SceneDepth, sampler_PointClamp, uv, 0).r);

}

#endif
