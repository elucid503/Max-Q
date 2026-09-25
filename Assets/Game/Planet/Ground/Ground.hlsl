// Shared by the ground and water shaders: CDLOD geomorphing, the per-patch detail texture, and sunlight through the air.
#ifndef MAXQ_GROUND_INCLUDED
#define MAXQ_GROUND_INCLUDED

#include "Sunlight.hlsl"

CBUFFER_START(UnityPerMaterial)
float4 _ColourRect;
float4 _ParentRect;
float4 _TileOriginNear;
float4 _TileOriginFar;
float4 _WaveOrigin;
float _Level;
CBUFFER_END

TEXTURE2D(_Colour);
SAMPLER(sampler_Colour);
TEXTURE2D(_Detail);
TEXTURE2D(_ParentDetail);

// Per level: morph start (km) and one over the morph band's width, measured from the camera the levels were chosen for.
float4 _GroundMorph[17];
float3 _GroundCamera;

// Set by URP while it renders a shadow cascade.
float3 _LightDirection;

#define DETAIL_TEXELS 65.0
#define WATER_DEPTH_RANGE 16384.0
#define VERTICAL_SCALE 0.2
#define SKIRT_FLAG 2.0

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

    float4 range = _GroundMorph[(int)_Level];
    float morph = saturate((distance(TransformObjectToWorld(input.position), _GroundCamera) - range.x) * range.y);
    float3 position = input.position + input.morph * morph;

    GroundVaryings output;
    output.positionWS = TransformObjectToWorld(position);
    output.positionCS = TransformWorldToHClip(output.positionWS);
    output.uv = float2(input.uv.x > 1.5 ? input.uv.x - SKIRT_FLAG : input.uv.x, input.uv.y);
    output.morph = morph;
    output.positionOS = position;

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

struct GroundDetail {

    float3 normalOS;
    float3 normalWS;
    float waterDepth;
    float occlusion;

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
    detail.occlusion = lerp(own.a, parent.a, morph);

    return detail;

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

// Waves on a flat sea: a dozen trains from 64 m down to 1.4 m, spread about one wind direction, each a whole number of
// cycles per WAVE_PERIOD along every object axis, so a patch's origin within the period keeps them exact anywhere on
// the planet. Waves shorter than two pixels cannot be drawn; their slopes become roughness instead, so the sun breaks
// into glitter on the waves nearby and spreads to Cox and Munk's glint (a 5 m/s wind) far away.
#define WAVE_PERIOD 128.0
#define WAVE_SLOPE 0.08
#define WAVE_ROUGHNESS 0.02
#define WAVES 12

static const float3 WaveCycles[WAVES] = {

    float3(2, 0, 0), float3(2, 2, 0), float3(1, 0, 4), float3(5, 1, 2), float3(8, 1, 0), float3(3, 11, 2),
    float3(13, 6, 7), float3(21, 8, -1), float3(10, 22, 21), float3(35, 5, 29), float3(19, 61, -4), float3(22, 68, 57),

};

struct WaterSurface {

    float3 normalWS;
    float roughness;

};

// Metres of ground a pixel spans; taken before any branch, where screen-space derivatives are still defined.
float PixelFootprint(float3 positionWS) {

    return max(length(ddx(positionWS)), length(ddy(positionWS))) * 1000.0;

}

WaterSurface Waves(float3 positionOS, float footprint, float3 up) {

    float3 cycles = positionOS * (1000.0 / WAVE_PERIOD) + _WaveOrigin.xyz;
    float3 slope = 0.0;
    float variance = 0.0;

    // Trains this few would cross in a regular lattice. The swell bends every crest and gathers the short waves into
    // groups; both are built from whole cycles too, so they repeat with the period.
    float swell = sin(2.0 * PI * dot(WaveCycles[0] + WaveCycles[2], cycles) - 0.4 * _Time.y) +
        sin(2.0 * PI * dot(WaveCycles[1] - WaveCycles[3], cycles) + 1.7 - 0.3 * _Time.y);
    float groups = sin(2.0 * PI * dot(WaveCycles[4] - WaveCycles[1], cycles) - 0.9 * _Time.y);

    for (int i = 0; i < WAVES; i++) {

        float wavelength = WAVE_PERIOD / length(WaveCycles[i]);
        float resolved = saturate(wavelength / (2.0 * footprint) - 1.0);

        variance += (1.0 - resolved * resolved) * 0.5 * WAVE_SLOPE * WAVE_SLOPE;

        UNITY_BRANCH
        if (resolved > 0.0) {

            // Deep water: waves of wavenumber k run at sqrt(g k).
            float speed = sqrt(9.81 * 2.0 * PI / wavelength);
            float phase = 2.0 * PI * dot(WaveCycles[i], cycles) - speed * _Time.y + 1.5 * swell + i * 2.4;
            float height = i < 4 ? 1.0 : 1.0 + 0.5 * groups * (i & 1 ? 1.0 : -1.0);

            slope += normalize(WaveCycles[i]) * (resolved * height * WAVE_SLOPE * cos(phase));

        }

    }

    float3 gradient = TransformObjectToWorldDir(slope, false);

    WaterSurface surface;
    surface.normalWS = normalize(up - (gradient - up * dot(gradient, up)));
    surface.roughness = sqrt(WAVE_ROUGHNESS * WAVE_ROUGHNESS + 2.0 * variance);

    return surface;

}

// Plain water: a wavy, glinting surface over a column that swallows red first. The bed shows through the shallows in
// the satellite's own colour, which already records what they look like from above. Depth is on Terra's scale; the
// water's colour answers to the real depth it stands for, so shelves and lagoons read as they do on Earth.
float3 WaterRadiance(float3 positionOS, float3 positionWS, float footprint, float depth, float3 bed, Sunlight light) {

    WaterSurface surface = Waves(positionOS, footprint, light.up);
    float3 v = normalize(_WorldSpaceCameraPos - positionWS);

    // At grazing angles the eye sees only the wave faces turned toward it; a face turned away is tipped back to edge-on.
    float3 n = surface.normalWS;
    n = normalize(n - v * min(dot(n, v) - 0.02, 0.0));
    float3 h = normalize(v + _SunDirection);

    float nv = saturate(dot(n, v)) + 1e-3;
    float nl = saturate(dot(n, _SunDirection));
    float nh = saturate(dot(n, h));
    float fresnel = 0.02 + 0.98 * pow(1.0 - nv, 5.0);

    float3 column = exp(-2.0 * max(depth, 0.0) / VERTICAL_SCALE * float3(0.35, 0.07, 0.04));
    float3 body = float3(0.003, 0.014, 0.032) * (1.0 - column) + bed * column;
    float3 diffuse = body / PI * (light.direct * nl * light.shadow + light.sky) * (1.0 - fresnel);

    float a2 = surface.roughness * surface.roughness;
    float d = nh * nh * (a2 - 1.0) + 1.0;
    float ggx = a2 / (PI * d * d);
    float k = surface.roughness * 0.5;
    float visibility = 0.25 / ((nv * (1.0 - k) + k) * (nl * (1.0 - k) + k));
    float3 glint = light.direct * light.shadow * nl * ggx * visibility * (0.02 + 0.98 * pow(1.0 - saturate(dot(h, v)), 5.0));

    // The sky the surface mirrors: from the sky-view table near the camera, else the mean skylight.
    // A reflection that would dip below the horizon strikes the next wave instead, which mirrors the sky just above it.
    float3 reflected = reflect(-v, n);
    reflected = normalize(reflected + light.up * max(0.01 - dot(reflected, light.up), 0.0));
    float3 sky = CameraInsideAir() ? SkyRadiance(reflected) : light.sky / PI;

    return diffuse + glint + fresnel * sky;

}

// How much a point is water rather than land, by the detail's water depth; ground and sheet share it so they agree.
float Wetness(GroundDetail detail) {

    return smoothstep(-0.05, 0.05, detail.waterDepth);

}

#endif
