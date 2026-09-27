// Shared by the ground and water shaders: CDLOD geomorphing, the per-patch detail textures, and sunlight through the air.
#ifndef MAXQ_GROUND_INCLUDED
#define MAXQ_GROUND_INCLUDED

#include "Sunlight.hlsl"

CBUFFER_START(UnityPerMaterial)
float4 _ColourRect;
float4 _ParentRect;
float4 _TileOriginNear;
float4 _TileOriginFar;
float4 _TileOriginMacro;
float4 _TileOriginBroad;
float _Level;
CBUFFER_END

TEXTURE2D(_Colour);
SAMPLER(sampler_Colour);
TEXTURE2D(_Detail);
TEXTURE2D(_ParentDetail);
TEXTURE2D(_WaterDetail);
TEXTURE2D(_ParentWaterDetail);

// Per level: morph start (km) and one over the morph band's width, measured from the camera the levels were chosen for.
float4 _GroundMorph[17];
float3 _GroundCamera;

// Set by URP while it renders a shadow cascade.
float3 _LightDirection;

#define DETAIL_TEXELS 129.0
#define WATER_TEXELS 65.0
#define FLOW_RANGE 8.0
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
GroundDetail SampleDetail(float2 uv, float morph) {

    float4 own = SAMPLE_TEXTURE2D_LOD(_Detail, sampler_linear_clamp, DetailUv(uv), 0.0);
    float4 parent = morph > 0.0 ? SAMPLE_TEXTURE2D_LOD(_ParentDetail, sampler_linear_clamp, DetailUv(uv * _ParentRect.xy + _ParentRect.zw), 0.0) : own;

    GroundDetail detail;
    detail.normalOS = normalize(lerp(DecodeOctahedral(own.rg), DecodeOctahedral(parent.rg), morph));
    detail.normalWS = TransformObjectToWorldNormal(detail.normalOS);
    detail.waterDepth = DecodeWaterDepth(lerp(own.b, parent.b, morph));
    detail.occlusion = lerp(own.a, parent.a, morph);

    return detail;

}

float2 WaterUv(float2 uv) {

    return (uv * (WATER_TEXELS - 1.0) + 0.5) / WATER_TEXELS;

}

struct WaterDetail {

    float2 flow;
    float seaShelter;
    float swellShelter;

};

// The river's current (m/s, the ground's east and north) and the land's shelter, blended toward the parent's like the
// detail.
WaterDetail SampleWaterDetail(float2 uv, float morph) {

    float4 own = SAMPLE_TEXTURE2D_LOD(_WaterDetail, sampler_linear_clamp, WaterUv(uv), 0.0);
    float4 parent = morph > 0.0 ? SAMPLE_TEXTURE2D_LOD(_ParentWaterDetail, sampler_linear_clamp, WaterUv(uv * _ParentRect.xy + _ParentRect.zw), 0.0) : own;
    float4 blended = lerp(own, parent, morph);

    WaterDetail detail;
    detail.flow = (blended.xy * 2.0 - 1.0) * FLOW_RANGE;
    detail.seaShelter = blended.z;
    detail.swellShelter = blended.w;

    return detail;

}

// Metres of ground a pixel spans; taken before any branch, where screen-space derivatives are still defined.
float PixelFootprint(float3 positionWS) {

    return max(length(ddx(positionWS)), length(ddy(positionWS))) * 1000.0;

}

float3 SatelliteColour(float2 uv) {

    return SAMPLE_TEXTURE2D(_Colour, sampler_Colour, uv * _ColourRect.xy + _ColourRect.zw).rgb;

}

// The satellite pixel's colour without its finest texels, which would otherwise pick materials pixel by pixel.
float3 SatelliteTone(float2 uv) {

    return SAMPLE_TEXTURE2D_LOD(_Colour, sampler_Colour, uv * _ColourRect.xy + _ColourRect.zw, 2.0).rgb;

}

// The ground around a point, as the light it bounces sees it.
float3 Surroundings(float2 uv) {

    return SAMPLE_TEXTURE2D_LOD(_Colour, sampler_Colour, uv * _ColourRect.xy + _ColourRect.zw, 5.0).rgb;

}

#endif
