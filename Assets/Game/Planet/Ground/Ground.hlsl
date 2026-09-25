// Shared by the ground and water shaders: CDLOD geomorphing, the per-patch detail texture, and sunlight through the air.
#ifndef MAXQ_GROUND_INCLUDED
#define MAXQ_GROUND_INCLUDED

#include "../Sky/Atmosphere.hlsl"

CBUFFER_START(UnityPerMaterial)
float4 _ColourRect;
float4 _ParentRect;
float4 _TileOriginNear;
float4 _TileOriginFar;
float _Level;
CBUFFER_END

TEXTURE2D(_Colour);
SAMPLER(sampler_Colour);
TEXTURE2D(_Detail);
TEXTURE2D(_ParentDetail);

// Per level: morph start (km) and one over the morph band's width.
float4 _GroundMorph[17];

#define DETAIL_TEXELS 65.0
#define WATER_DEPTH_RANGE 16384.0
#define VERTICAL_SCALE 0.2

struct GroundAttributes {

    float3 position : POSITION;
    float2 uv : TEXCOORD0;
    float3 morph : TEXCOORD1;

};

struct GroundVaryings {

    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float morph : TEXCOORD2;
    float3 positionOS : TEXCOORD3;

};

// Vertices slide onto the parent level's surface as they near the edge of their level's range.
GroundVaryings GroundVertex(GroundAttributes input) {

    float3 world = TransformObjectToWorld(input.position);
    float4 range = _GroundMorph[(int)_Level];
    float morph = saturate((distance(world, _WorldSpaceCameraPos) - range.x) * range.y);
    float3 position = input.position + input.morph * morph;

    GroundVaryings output;
    output.positionWS = TransformObjectToWorld(position);
    output.positionCS = TransformWorldToHClip(output.positionWS);
    output.uv = input.uv;
    output.morph = morph;
    output.positionOS = position;

    return output;

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

struct GroundDetail {

    float3 normalOS;
    float3 normalWS;
    float waterDepth;

};

// This patch's detail blended toward its parent's by the same factor that morphs the geometry.
GroundDetail SampleDetail(float2 uv, float morph) {

    float4 own = SAMPLE_TEXTURE2D(_Detail, sampler_linear_clamp, DetailUv(uv));
    float4 parent = SAMPLE_TEXTURE2D(_ParentDetail, sampler_linear_clamp, DetailUv(uv * _ParentRect.xy + _ParentRect.zw));

    GroundDetail detail;
    detail.normalOS = normalize(lerp(DecodeOctahedral(own.rg), DecodeOctahedral(parent.rg), morph));
    detail.normalWS = TransformObjectToWorldNormal(detail.normalOS);
    float encoded = lerp(own.b, parent.b, morph) * 2.0 - 1.0;
    detail.waterDepth = sign(encoded) * encoded * encoded * WATER_DEPTH_RANGE;

    return detail;

}

float3 SatelliteColour(float2 uv) {

    return SAMPLE_TEXTURE2D(_Colour, sampler_Colour, uv * _ColourRect.xy + _ColourRect.zw).rgb;

}

// The satellite pixel's colour without its finest texels, which would otherwise pick materials pixel by pixel.
float3 SatelliteTone(float2 uv) {

    return SAMPLE_TEXTURE2D_LOD(_Colour, sampler_Colour, uv * _ColourRect.xy + _ColourRect.zw, 2.0).rgb;

}

struct Sunlight {

    float3 direct;
    float3 sky;
    float3 up;

};

// Sun and skylight arriving at a point on the ground, both already dimmed and coloured by the air above it.
Sunlight SunlightAt(float3 positionWS) {

    float3 fromCentre = positionWS - _PlanetCentre;
    float r = max(length(fromCentre), _PlanetRadius + 1e-3);

    Sunlight light;
    light.up = fromCentre / length(fromCentre);
    light.direct = _SunIlluminance * SunTransmittance(light.up * r, _SunDirection);
    light.sky = _SunIlluminance * SkyIrradiance(r, dot(light.up, _SunDirection));

    return light;

}

float3 GroundRadiance(float3 albedo, float3 normalWS, Sunlight light) {

    float direct = saturate(dot(normalWS, _SunDirection));
    float sky = 0.5 + 0.5 * dot(normalWS, light.up);

    return albedo / PI * (light.direct * direct + light.sky * sky);

}

// Plain water: a flat, glinting surface over a column that swallows red first. The bed shows through the shallows in
// the satellite's own colour, which already records what they look like from above. Depth is on Terra's scale; the
// water's colour answers to the real depth it stands for, so shelves and lagoons read as they do on Earth.
float3 WaterRadiance(float3 positionWS, float depth, float3 bed, Sunlight light) {

    float3 n = light.up;
    float3 v = normalize(_WorldSpaceCameraPos - positionWS);
    float3 h = normalize(v + _SunDirection);

    float nv = saturate(dot(n, v)) + 1e-3;
    float nl = saturate(dot(n, _SunDirection));
    float nh = saturate(dot(n, h));
    float fresnel = 0.02 + 0.98 * pow(1.0 - nv, 5.0);

    float3 column = exp(-2.0 * max(depth, 0.0) / VERTICAL_SCALE * float3(0.35, 0.07, 0.04));
    float3 body = float3(0.003, 0.014, 0.032) * (1.0 - column) + bed * column;
    float3 diffuse = body / PI * (light.direct * nl + light.sky) * (1.0 - fresnel);

    const float roughness = 0.35;
    float a2 = roughness * roughness * roughness * roughness;
    float d = nh * nh * (a2 - 1.0) + 1.0;
    float ggx = a2 / (PI * d * d);
    float k = roughness * roughness * 0.5;
    float visibility = 0.25 / ((nv * (1.0 - k) + k) * (nl * (1.0 - k) + k));
    float3 glint = light.direct * nl * ggx * visibility * (0.02 + 0.98 * pow(1.0 - saturate(dot(h, v)), 5.0));

    // The sky the surface mirrors: from the sky-view table near the camera, else the mean skylight.
    float3 reflected = reflect(-v, n);
    float3 sky = CameraInsideAir() ? SkyRadiance(reflected) : light.sky / PI;

    return diffuse + glint + fresnel * sky;

}

// How much a point is water rather than land, by the detail's water depth; ground and sheet share it so they agree.
float Wetness(GroundDetail detail) {

    return smoothstep(-0.05, 0.05, detail.waterDepth);

}

#endif
