// Shared by the ground and water shaders: CDLOD geomorphing, the per-patch detail and cover textures, and sunlight
// through the air.
#ifndef MAXQ_GROUND_INCLUDED
#define MAXQ_GROUND_INCLUDED

#include "Sunlight.hlsl"
#include "Biome.hlsl"

CBUFFER_START(UnityPerMaterial)
float4 _ParentRect;
float4 _TileOriginNear;
float4 _TileOriginFar;
float4 _TileOriginMacro;
float4 _TileOriginBroad;
float _Level;

// One while the patch draws its water sheet at half resolution (GroundView.ShapeWater).
float _WaterCoarse;
CBUFFER_END

TEXTURE2D(_Detail);
TEXTURE2D(_ParentDetail);
TEXTURE2D(_Cover);
TEXTURE2D(_ParentCover);

// The four patch textures filter alike, trilinear and anisotropic over their mips, so they share the detail's sampler.
SAMPLER(sampler_Detail);

// Per level: morph start (km) and one over the morph band's width, measured from the camera the levels were chosen for.
float4 _GroundMorph[17];
float3 _GroundCamera;

// Set by URP while it renders a shadow cascade.
float3 _LightDirection;

#define DETAIL_TEXELS 129.0
#define NO_LEVEL -1e5
#define WATER_DEPTH_RANGE 16384.0
#define VERTICAL_SCALE 0.2
#define SKIRT_FLAG 2.0

// Parent: for the water sheet, the offset (km) to one end of the parent edge a vertex morphs onto, and a skirt's drop.
// Water: how far the sheet's level stands over the vertex (m, under NO_LEVEL for none), and the distance to shore (m,
// negative offshore).
struct GroundAttributes {

    float3 position : POSITION;
    float2 uv : TEXCOORD0;
    float3 morph : TEXCOORD1;
    float4 parent : TEXCOORD2;
    float2 water : TEXCOORD3;

};

struct GroundVaryings {

    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float morph : TEXCOORD2;
    float3 positionOS : TEXCOORD3;
    float2 water : TEXCOORD4;

};

// How far a point at positionWS has slid toward the parent level, as it nears the edge of this level's range.
float MorphAt(float3 positionWS) {

    float4 range = _GroundMorph[(int)_Level];

    return saturate((distance(positionWS, _GroundCamera) - range.x) * range.y);

}

// Vertices slide onto the parent level's surface as they near the edge of their level's range.
GroundVaryings GroundVertex(GroundAttributes input) {

    float morph = MorphAt(TransformObjectToWorld(input.position));
    float3 position = input.position + input.morph * morph;

    GroundVaryings output;
    output.positionWS = TransformObjectToWorld(position);
    output.positionCS = TransformWorldToHClip(output.positionWS);
    output.uv = float2(input.uv.x > 1.5 ? input.uv.x - SKIRT_FLAG : input.uv.x, input.uv.y);
    output.morph = morph;
    output.positionOS = position;
    output.water = input.water;

    return output;

}

// The same morphed surface seen from the sun, biased along the local vertical so the ground never shadows itself. Skirts
// hang below every patch edge to hide cracks from the camera; where a neighbour is culled, one would stand in the sun and
// cast a wall of shadow, so their vertices go to NaN, which drops their triangles.
float4 GroundShadowVertex(GroundAttributes input) : SV_POSITION {

    if (input.uv.x > 1.5) {

        return asfloat(0x7FC00000u);

    }

    float3 positionWS = GroundVertex(input).positionWS;
    float3 up = normalize(positionWS - _PlanetCentre);

    return ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, up, _LightDirection)));

}

float2 DetailUv(float2 uv) {

    return (uv * (DETAIL_TEXELS - 1.0) + 0.5) / DETAIL_TEXELS;

}

float2 ParentUv(float2 uv) {

    return DetailUv(uv * _ParentRect.xy + _ParentRect.zw);

}

// A patch texture and its parent's at uv, the parent's only where the morph reaches toward it, filtered to the pixel by
// uv's screen-space gradients.
void SamplePatch(TEXTURE2D_PARAM(own, ownSampler), TEXTURE2D_PARAM(parent, parentSampler), float2 uv, float morph, float2 uvDx, float2 uvDy,
    out float4 ownTexel, out float4 parentTexel) {

    float scale = (DETAIL_TEXELS - 1.0) / DETAIL_TEXELS;
    float2 parentScale = _ParentRect.xy * scale;

    ownTexel = SAMPLE_TEXTURE2D_GRAD(own, ownSampler, DetailUv(uv), uvDx * scale, uvDy * scale);
    parentTexel = morph > 0.0 ? SAMPLE_TEXTURE2D_GRAD(parent, parentSampler, ParentUv(uv), uvDx * parentScale, uvDy * parentScale) : ownTexel;

}

// The finest level of the same, for the vertex stage and single taps.
void SamplePatch(TEXTURE2D_PARAM(own, ownSampler), TEXTURE2D_PARAM(parent, parentSampler), float2 uv, float morph, out float4 ownTexel, out float4 parentTexel) {

    ownTexel = SAMPLE_TEXTURE2D_LOD(own, ownSampler, DetailUv(uv), 0.0);
    parentTexel = morph > 0.0 ? SAMPLE_TEXTURE2D_LOD(parent, parentSampler, ParentUv(uv), 0.0) : ownTexel;

}

float3 DecodeOctahedral(float2 encoded) {

    float2 e = encoded * 2.0 - 1.0;
    float3 n = float3(e.x, 1.0 - abs(e.x) - abs(e.y), e.y);

    if (n.y < 0.0) {

        n.xz = (1.0 - abs(n.zx)) * (n.xz >= 0.0 ? 1.0 : -1.0);

    }

    return normalize(n);

}

// Water depth (m over the bed, negative on land) from the detail's blue channel, stored as its signed square root.
float DecodeWaterDepth(float channel) {

    float encoded = channel * 2.0 - 1.0;

    return sign(encoded) * encoded * encoded * WATER_DEPTH_RANGE;

}

struct GroundDetail {

    float3 normalOS;
    float3 normalWS;
    float waterDepth;
    float occlusion;

};

// This patch's detail blended toward its parent's by the same factor that morphs the geometry.
GroundDetail DecodeDetail(float4 own, float4 parent, float morph) {

    GroundDetail detail;
    detail.normalOS = normalize(lerp(DecodeOctahedral(own.rg), DecodeOctahedral(parent.rg), morph));
    detail.normalWS = TransformObjectToWorldNormal(detail.normalOS);
    detail.waterDepth = DecodeWaterDepth(lerp(own.b, parent.b, morph));
    detail.occlusion = lerp(own.a, parent.a, morph);

    return detail;

}

GroundDetail SampleDetail(float2 uv, float morph, float2 uvDx, float2 uvDy) {

    float4 own;
    float4 parent;

    SamplePatch(TEXTURE2D_ARGS(_Detail, sampler_Detail), TEXTURE2D_ARGS(_ParentDetail, sampler_Detail), uv, morph, uvDx, uvDy, own, parent);

    return DecodeDetail(own, parent, morph);

}

GroundDetail SampleDetail(float2 uv, float morph) {

    float4 own;
    float4 parent;

    SamplePatch(TEXTURE2D_ARGS(_Detail, sampler_Detail), TEXTURE2D_ARGS(_ParentDetail, sampler_Detail), uv, morph, own, parent);

    return DecodeDetail(own, parent, morph);

}

// What covers the ground (see Cover.cs), blended toward the parent's like the detail.
GroundCover DecodeCover(float4 own, float4 parent, float morph) {

    float4 blended = lerp(own, parent, morph);

    GroundCover cover;
    cover.vegetation = blended.r;
    cover.forest = blended.g;
    cover.arid = blended.b;
    cover.snow = blended.a;

    return cover;

}

GroundCover SampleCover(float2 uv, float morph, float2 uvDx, float2 uvDy) {

    float4 own;
    float4 parent;

    SamplePatch(TEXTURE2D_ARGS(_Cover, sampler_Detail), TEXTURE2D_ARGS(_ParentCover, sampler_Detail), uv, morph, uvDx, uvDy, own, parent);

    return DecodeCover(own, parent, morph);

}

GroundCover SampleCover(float2 uv, float morph) {

    float4 own;
    float4 parent;

    SamplePatch(TEXTURE2D_ARGS(_Cover, sampler_Detail), TEXTURE2D_ARGS(_ParentCover, sampler_Detail), uv, morph, own, parent);

    return DecodeCover(own, parent, morph);

}

// Metres of ground a pixel spans; taken before any branch, where screen-space derivatives are still defined.
float PixelFootprint(float3 positionWS) {

    return max(length(ddx(positionWS)), length(ddy(positionWS))) * 1000.0;

}

// The ground around a point on the patch, as the light it bounces sees it.
float3 Surroundings(float2 uv, float3 positionWS) {

    float3 fromCentre = positionWS - _PlanetCentre;
    float altitude = (length(fromCentre) - _PlanetRadius) * 1000.0;

    return CoverColour(SampleCover(uv, 0.0), Warmth(normalize(fromCentre), altitude));

}

#endif
