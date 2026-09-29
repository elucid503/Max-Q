// Earth's atmosphere round Terra, after Hillaire (2020); scene units are kilometres, so coefficients are per kilometre.
#ifndef MAXQ_ATMOSPHERE_INCLUDED
#define MAXQ_ATMOSPHERE_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

#define ATMOSPHERE_HEIGHT 100.0
#define RAYLEIGH_SCATTERING float3(5.802e-3, 13.558e-3, 33.1e-3)
#define RAYLEIGH_SCALE_HEIGHT 8.0
// Continental aerosol: about 50 km of visibility at sea level, thinning with height.
#define MIE_SCATTERING 0.058
#define MIE_EXTINCTION 0.065
#define MIE_SCALE_HEIGHT 1.2
#define MIE_G 0.8
#define OZONE_ABSORPTION float3(0.650e-3, 1.881e-3, 0.085e-3)
#define OZONE_CENTRE 25.0
#define OZONE_HALF_WIDTH 15.0
#define GROUND_ALBEDO 0.3

#define TRANSMITTANCE_SIZE float2(256.0, 64.0)
#define MULTI_SCATTER_SIZE float2(32.0, 32.0)

float3 _PlanetCentre;
float _PlanetRadius;
float3 _SunDirection;
float3 _SunIlluminance;

// Valley haze below a flat top (km above the reference radius), scattering like the aerosol without absorbing.
float _FogTop;
float _FogDensity;

TEXTURE2D(_TransmittanceLut);
TEXTURE2D(_MultiScatterLut);
TEXTURE2D(_IrradianceLut);
TEXTURE2D(_SkyViewLut);
SAMPLER(sampler_linear_clamp);

// Clouds' shadow over 1 / _CloudShadowOrigin.w km round the camera: sunlight let through, and how far sunward it starts.
TEXTURE2D(_CloudShadow);
float4 _CloudShadowOrigin;
float3 _CloudShadowRight;
float3 _CloudShadowUp;

#define CLOUD_SHADOW_SOFTNESS 0.4
#define CLOUD_SHADOW_TEXELS 512.0

// Height (km) of the clouds' shell, above which nothing shades the sun; matches WEATHER_TOP_RANGE in CloudNoise.compute.
#define CLOUD_TOP 16.0

// March steps given to the stretch of a view ray that shadows can fall on, so shafts resolve, and the finest mip of the
// clouds' shadow they read (four texels).
#define SHADOW_STEPS 20
#define CLOUD_SHADOW_SHAFT_LOD 2.0

// The clouds round the camera by azimuth from the sun and elevation: what they add to the sky, and how much they let through.
TEXTURE2D(_CloudSky);
float _CloudsOn;

#define CLOUD_SKY_WIDTH 256.0
#define CLOUD_SKY_BELOW 0.1

#define SKY_VIEW_SIZE float2(192.0, 108.0)

float TopRadius() {

    return _PlanetRadius + ATMOSPHERE_HEIGHT;

}

struct Medium {

    float3 rayleigh;
    float mie;
    float3 scattering;
    float3 extinction;

};

Medium SampleMedium(float altitude) {

    float rayleigh = exp(-altitude / RAYLEIGH_SCALE_HEIGHT);
    float mie = exp(-altitude / MIE_SCALE_HEIGHT);
    float ozone = max(0.0, 1.0 - abs(altitude - OZONE_CENTRE) / OZONE_HALF_WIDTH);

    Medium medium;
    medium.rayleigh = RAYLEIGH_SCATTERING * rayleigh;
    medium.mie = MIE_SCATTERING * mie;
    medium.scattering = medium.rayleigh + medium.mie;
    medium.extinction = medium.rayleigh + MIE_EXTINCTION * mie + OZONE_ABSORPTION * ozone;

    return medium;

}

// Nearest and farthest hits of a ray from origin (relative to the centre) with a sphere; both negative on a miss.
float2 RaySphere(float3 origin, float3 direction, float radius) {

    float b = dot(origin, direction);
    float c = dot(origin, origin) - radius * radius;
    float discriminant = b * b - c;

    if (discriminant < 0.0) {

        return float2(-1.0, -1.0);

    }

    float s = sqrt(discriminant);

    return float2(-b - s, -b + s);

}

float RayleighPhase(float cosTheta) {

    return 3.0 / (16.0 * PI) * (1.0 + cosTheta * cosTheta);

}

// Cornette-Shanks.
float MiePhase(float cosTheta) {

    float g2 = MIE_G * MIE_G;

    return 3.0 / (8.0 * PI) * (1.0 - g2) * (1.0 + cosTheta * cosTheta) / ((2.0 + g2) * pow(max(1.0 + g2 - 2.0 * MIE_G * cosTheta, 1e-4), 1.5));

}

float UnitToTexel(float x, float size) {

    return 0.5 / size + x * (1.0 - 1.0 / size);

}

float TexelToUnit(float u, float size) {

    return (u - 0.5 / size) / (1.0 - 1.0 / size);

}

// Bruneton's (r, mu) parameterisation of transmittance to the top of the atmosphere.
float2 TransmittanceUv(float r, float mu) {

    float top = TopRadius();
    float h = sqrt(top * top - _PlanetRadius * _PlanetRadius);
    float rho = sqrt(max(r * r - _PlanetRadius * _PlanetRadius, 0.0));
    float d = max(-r * mu + sqrt(max(r * r * (mu * mu - 1.0) + top * top, 0.0)), 0.0);
    float dMin = top - r;
    float dMax = rho + h;

    return float2(UnitToTexel((d - dMin) / (dMax - dMin), TRANSMITTANCE_SIZE.x), UnitToTexel(rho / h, TRANSMITTANCE_SIZE.y));

}

void TransmittanceRMu(float2 uv, out float r, out float mu) {

    float top = TopRadius();
    float h = sqrt(top * top - _PlanetRadius * _PlanetRadius);
    float rho = h * TexelToUnit(uv.y, TRANSMITTANCE_SIZE.y);

    r = sqrt(rho * rho + _PlanetRadius * _PlanetRadius);

    float dMin = top - r;
    float dMax = rho + h;
    float d = dMin + TexelToUnit(uv.x, TRANSMITTANCE_SIZE.x) * (dMax - dMin);

    mu = d == 0.0 ? 1.0 : clamp((h * h - rho * rho - d * d) / (2.0 * r * d), -1.0, 1.0);

}

float3 Transmittance(float r, float mu) {

    return SAMPLE_TEXTURE2D_LOD(_TransmittanceLut, sampler_linear_clamp, TransmittanceUv(r, mu), 0).rgb;

}

// Sunlight reaching a point (relative to the centre): zero in the planet's shadow.
float3 SunTransmittance(float3 position, float3 sun) {

    if (RaySphere(position, sun, _PlanetRadius).x > 0.0) {

        return 0.0;

    }

    float r = length(position);

    return Transmittance(r, dot(position, sun) / r);

}

// How much of the sun gets down through the low haze to a point (relative to the centre).
float FogSunTransmittance(float3 position, float3 sun) {

    float r = _PlanetRadius + _FogTop;

    if (_FogDensity <= 0.0 || dot(position, position) >= r * r) {

        return 1.0;

    }

    return exp(-_FogDensity * RaySphere(position, sun, r).y);

}

// The integral of saturate(u) from zero.
float RampIntegral(float u) {

    float ramp = saturate(u);

    return 0.5 * ramp * ramp + max(u - 1.0, 0.0);

}

// The shadow map through a cubic B-spline over its 4x4 texels in four bilinear taps: bilinear alone draws a sharp shade's
// outline as straight-sided polygons once a texel spans many pixels, as it does on the ground.
float2 CloudShadeSmooth(float2 uv, float lod) {

    float size = CLOUD_SHADOW_TEXELS * exp2(-lod);
    float2 position = uv * size - 0.5;
    float2 centre = floor(position);
    float2 f = position - centre;
    float2 g = 1.0 - f;
    float2 w0 = g * g * g / 6.0;
    float2 w1 = (4.0 - 6.0 * f * f + 3.0 * f * f * f) / 6.0;
    float2 w3 = f * f * f / 6.0;
    float2 low = w0 + w1;
    float2 high = 1.0 - low;
    float2 at0 = (centre - 0.5 + w1 / low) / size;
    float2 at1 = (centre + 1.5 + w3 / high) / size;

    return low.y * (low.x * SAMPLE_TEXTURE2D_LOD(_CloudShadow, sampler_linear_clamp, at0, lod).rg +
            high.x * SAMPLE_TEXTURE2D_LOD(_CloudShadow, sampler_linear_clamp, float2(at1.x, at0.y), lod).rg) +
        high.y * (low.x * SAMPLE_TEXTURE2D_LOD(_CloudShadow, sampler_linear_clamp, float2(at0.x, at1.y), lod).rg +
            high.x * SAMPLE_TEXTURE2D_LOD(_CloudShadow, sampler_linear_clamp, at1, lod).rg);

}

// Sunlight the clouds let through to a point, all of it sunward of their shade and off the map; lod blurs the map, and
// smooth filters it for surfaces seen up close. The shade's sunward edge is averaged over span (km before and after the
// point along the sun), as the map's blur never can: along the sun the map holds still, so a point sample would print its
// jitter. The shade fades out smoothly toward the map's edge, so no line marks where it ends.
float CloudShadow(float3 positionWS, float lod = 0.0, float2 span = float2(0.0, 0.0), bool smooth = false) {

    if (_CloudShadowOrigin.w <= 0.0) {

        return 1.0;

    }

    float3 offset = positionWS - _CloudShadowOrigin.xyz;
    float2 uv = float2(dot(offset, _CloudShadowRight), dot(offset, _CloudShadowUp)) * _CloudShadowOrigin.w + 0.5;
    float2 edge = smoothstep(0.0, 0.125, 0.5 - abs(uv - 0.5));
    float2 shade = smooth ? CloudShadeSmooth(uv, lod) : SAMPLE_TEXTURE2D_LOD(_CloudShadow, sampler_linear_clamp, uv, lod).rg;
    float2 u = (dot(offset, _SunDirection) + span - shade.g) / CLOUD_SHADOW_SOFTNESS + 0.5;
    float width = u.y - u.x;
    float sunward = abs(width) > 1e-3 ? (RampIntegral(u.y) - RampIntegral(u.x)) / width : saturate(0.5 * (u.x + u.y));

    return lerp(1.0, lerp(shade.r, 1.0, sunward), edge.x * edge.y);

}

// Sun visibility past the cascades, where they reach.
float CascadeShadow(float3 positionWS) {

    #if defined(_MAIN_LIGHT_SHADOWS_CASCADE)
    return lerp(MainLightRealtimeShadow(TransformWorldToShadowCoord(positionWS)), 1.0, GetMainLightShadowFade(positionWS));
    #else
    return 1.0;
    #endif

}

// Sun visibility past the cascades and the clouds.
float SunShadow(float3 positionWS) {

    return CascadeShadow(positionWS) * CloudShadow(positionWS, 0.0, float2(0.0, 0.0), true);

}

float CloudShadowLod(float stretch) {

    return log2(max(stretch * _CloudShadowOrigin.w * CLOUD_SHADOW_TEXELS, 1.0));

}

// The clouds' shadow over a stretch of a view ray, seen from its middle at angle sine and cosine from the sun: the map
// blurred to the stretch's width across the sun, and no finer than its jittered, uneroded texels can be trusted, with
// its sunward edge averaged along it. A box over the stretch rather than a jittered point, so no pattern prints.
float CloudShadowOver(float3 middleWS, float stretch, float sinTheta, float cosTheta) {

    return CloudShadow(middleWS, max(CloudShadowLod(stretch * sinTheta), CLOUD_SHADOW_SHAFT_LOD), float2(-0.5, 0.5) * stretch * cosTheta);

}

float2 AltitudeSunUv(float r, float muSun, float2 size) {

    return float2(UnitToTexel(muSun * 0.5 + 0.5, size.x), UnitToTexel(saturate((r - _PlanetRadius) / ATMOSPHERE_HEIGHT), size.y));

}

float3 MultiScatter(float r, float muSun) {

    return SAMPLE_TEXTURE2D_LOD(_MultiScatterLut, sampler_linear_clamp, AltitudeSunUv(r, muSun, MULTI_SCATTER_SIZE), 0).rgb;

}

// Skylight on a level surface, per unit of sun illuminance.
float3 SkyIrradiance(float r, float muSun) {

    return SAMPLE_TEXTURE2D_LOD(_IrradianceLut, sampler_linear_clamp, AltitudeSunUv(r, muSun, float2(64.0, 16.0)), 0).rgb;

}

// front is the same up to the split distance along the ray, where the clouds stand.
struct Scattering {

    float3 radiance;
    float3 transmittance;
    float3 frontRadiance;
    float3 frontTransmittance;

};

// The stretch of a ray from origin (relative to the centre) that shadows can fall on: below the clouds' tops and, with
// clouds, within their shadow map, which runs on along the sun. Empty when the second is not past the first.
float2 ShadowedStretch(float3 origin, float3 direction) {

    float2 stretch = RaySphere(origin, direction, _PlanetRadius + CLOUD_TOP);

    if (_CloudShadowOrigin.w <= 0.0) {

        return stretch;

    }

    float3 offset = origin + _PlanetCentre - _CloudShadowOrigin.xyz;
    float2 across = float2(dot(offset, _CloudShadowRight), dot(offset, _CloudShadowUp));
    float2 along = float2(dot(direction, _CloudShadowRight), dot(direction, _CloudShadowUp));
    float2 rate = 1.0 / (abs(along) > 1e-6 ? along : 1e-6);
    float2 first = (-0.5 / _CloudShadowOrigin.w - across) * rate;
    float2 second = (0.5 / _CloudShadowOrigin.w - across) * rate;
    float2 enter = min(first, second);
    float2 leave = max(first, second);

    return float2(max(stretch.x, max(enter.x, enter.y)), min(stretch.y, min(leave.x, leave.y)));

}

// Where step f (0 to 1) of a stretch ends, crowding toward the camera when the stretch starts there.
float StepEnd(float from, float to, float f, bool crowd) {

    return from + (to - from) * (crowd ? f * f : f);

}

// Light scattered toward origin per unit of sun illuminance, and transmittance, over steps crowding toward an origin
// inside the air. Shadows cast shafts: the stretch they can fall on gets SHADOW_STEPS of its own and the rest of the ray
// shares steps; the cascades are looked up at jitter of each step, the clouds' shadow over the whole of it. Fog adds each
// step's exact stretch of haze.
Scattering Integrate(float3 origin, float3 direction, float tMax, float3 sun, int steps, bool multiScatter, float jitter = 0.3, bool shadows = false, bool fog = false,
    float split = 1e9) {

    Scattering result;
    result.radiance = 0.0;
    result.transmittance = 1.0;
    result.frontRadiance = 0.0;
    result.frontTransmittance = 1.0;

    float2 top = RaySphere(origin, direction, TopRadius());
    float ground = RaySphere(origin, direction, _PlanetRadius).x;
    float start = max(top.x, 0.0);
    float end = min(top.y, ground > 0.0 ? min(tMax, ground) : tMax);

    if (end <= start) {

        return result;

    }

    float cosTheta = dot(direction, sun);
    float sinTheta = sqrt(saturate(1.0 - cosTheta * cosTheta));
    float rayleighPhase = RayleighPhase(cosTheta);
    float miePhase = MiePhase(cosTheta);
    bool inside = top.x <= 0.0;
    float previous = start;
    float2 haze = fog && _FogDensity > 0.0 ? RaySphere(origin, direction, _PlanetRadius + _FogTop) : -1.0;

    // The ray splits into the air before the shadowed stretch, the stretch, and the air after it.
    float shadedFrom = start;
    float shadedTo = start;

    if (shadows) {

        float2 stretch = ShadowedStretch(origin, direction);

        if (stretch.y > stretch.x) {

            shadedFrom = clamp(stretch.x, start, end);
            shadedTo = clamp(stretch.y, shadedFrom, end);

        }

    }

    float before = shadedFrom - start;
    float after = end - shadedTo;
    int shadedSteps = shadedTo > shadedFrom ? SHADOW_STEPS : 0;
    int beforeSteps = before > 0.0 ? clamp((int)round(steps * before / max(before + after, 1e-6)), 1, after > 0.0 ? steps - 1 : steps) : 0;
    int afterSteps = after > 0.0 ? steps - beforeSteps : 0;
    int total = beforeSteps + shadedSteps + afterSteps;

    for (int i = 0; i < total; i++) {

        bool shaded = i >= beforeSteps && i < beforeSteps + shadedSteps;
        float t;

        if (i < beforeSteps) {

            t = StepEnd(start, shadedFrom, (i + 1.0) / beforeSteps, inside);

        } else if (shaded) {

            t = StepEnd(shadedFrom, shadedTo, (i - beforeSteps + 1.0) / shadedSteps, inside && beforeSteps == 0);

        } else {

            t = StepEnd(shadedTo, end, (i - beforeSteps - shadedSteps + 1.0) / afterSteps, inside && shadedTo == start);

        }

        float dt = t - previous;
        float3 p = origin + direction * (previous + 0.5 * dt);
        float r = length(p);
        float muSun = dot(p, sun) / r;

        Medium medium = SampleMedium(r - _PlanetRadius);
        float3 sunlight = SunTransmittance(p, sun) * FogSunTransmittance(p, sun);

        if (shaded) {

            sunlight *= CascadeShadow(origin + direction * (previous + jitter * dt) + _PlanetCentre) * CloudShadowOver(p + _PlanetCentre, dt, sinTheta, cosTheta);

        }

        float3 ambient = multiScatter ? MultiScatter(r, muSun) : 0.0;
        float3 scattered = ((medium.rayleigh * rayleighPhase + medium.mie * miePhase) * sunlight + medium.scattering * ambient) * dt;
        float3 depth = medium.extinction * dt;
        float inHaze = max(min(t, haze.y) - max(previous, haze.x), 0.0);

        if (inHaze > 0.0) {

            float hazeFrom = max(previous, haze.x);
            float3 q = origin + direction * (hazeFrom + 0.5 * inHaze);
            float3 hazeLight = SunTransmittance(q, sun) * FogSunTransmittance(q, sun);

            // The haze's shafts are looked up as the air's, so the two blend rather than band.
            if (shaded) {

                hazeLight *= CascadeShadow(origin + direction * (hazeFrom + jitter * inHaze) + _PlanetCentre) * CloudShadowOver(q + _PlanetCentre, inHaze, sinTheta, cosTheta);

            }

            scattered += _FogDensity * inHaze * (miePhase * hazeLight + ambient);
            depth += _FogDensity * inHaze;

        }

        float3 stepTransmittance = exp(-depth);

        if (split > previous && split <= t) {

            float3 partial = exp(-depth * (split - previous) / dt);

            result.frontRadiance = result.radiance + result.transmittance * scattered * (1.0 - partial) / max(depth, 1e-9);
            result.frontTransmittance = result.transmittance * partial;

        }

        result.radiance += result.transmittance * scattered * (1.0 - stepTransmittance) / max(depth, 1e-9);
        result.transmittance *= stepTransmittance;
        previous = t;

    }

    if (split > end) {

        result.frontRadiance = result.radiance;
        result.frontTransmittance = result.transmittance;

    }

    return result;

}

// Hillaire's sky-view parameterisation: latitude crowds toward the horizon, azimuth from the sun toward it.
float SkyViewZenithHorizon(float viewHeight, out float beta) {

    float horizon = sqrt(max(viewHeight * viewHeight - _PlanetRadius * _PlanetRadius, 0.0));

    beta = acos(saturate(horizon / viewHeight));

    return PI - beta;

}

void SkyViewDirection(float2 uv, float viewHeight, out float cosZenith, out float cosLight) {

    float beta;
    float zenithHorizon = SkyViewZenithHorizon(viewHeight, beta);
    float zenith;

    if (uv.y < 0.5) {

        float coord = 1.0 - 2.0 * uv.y;
        zenith = zenithHorizon * (1.0 - coord * coord);

    } else {

        float coord = uv.y * 2.0 - 1.0;
        zenith = zenithHorizon + beta * coord * coord;

    }

    cosZenith = cos(zenith);
    cosLight = -(uv.x * uv.x * 2.0 - 1.0);

}

float2 SkyViewUv(float viewHeight, float cosZenith, float cosLight) {

    float beta;
    float zenithHorizon = SkyViewZenithHorizon(viewHeight, beta);
    float zenith = acos(clamp(cosZenith, -1.0, 1.0));
    float v;

    if (zenith < zenithHorizon) {

        float coord = sqrt(saturate(1.0 - zenith / zenithHorizon));
        v = 0.5 * (1.0 - coord);

    } else {

        float coord = sqrt(saturate((zenith - zenithHorizon) / beta));
        v = 0.5 * (1.0 + coord);

    }

    float u = sqrt(saturate(0.5 - 0.5 * cosLight));

    return float2(UnitToTexel(u, SKY_VIEW_SIZE.x), UnitToTexel(v, SKY_VIEW_SIZE.y));

}

// The sky tables' frame round the camera: forward toward the sun's azimuth (any level direction with the sun overhead).
void SkyFrame(float3 up, out float3 forward, out float3 side) {

    float3 flatSun = _SunDirection - up * dot(_SunDirection, up);
    float3 anyFlat = normalize(cross(up, abs(up.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0)));

    forward = length(flatSun) > 1e-4 ? normalize(flatSun) : anyFlat;
    side = cross(up, forward);

}

// The clear-sky table's radiance in a direction, with the clouds blurred to match a table read lod mips down.
float3 CloudySky(float3 direction, float3 sky, float lod) {

    if (_CloudsOn <= 0.0) {

        return sky;

    }

    float3 up = normalize(_WorldSpaceCameraPos - _PlanetCentre);
    float3 forward;
    float3 side;

    SkyFrame(up, forward, side);

    float sinElevation = dot(direction, up);
    float2 uv = float2(atan2(dot(direction, side), dot(direction, forward)) / (2.0 * PI) + 0.5, sqrt(saturate((sinElevation + CLOUD_SKY_BELOW) / (1.0 + CLOUD_SKY_BELOW))));
    float4 clouds = SAMPLE_TEXTURE2D_LOD(_CloudSky, sampler_linear_clamp, uv, lod + log2(CLOUD_SKY_WIDTH / SKY_VIEW_SIZE.x));

    return clouds.rgb + clouds.a * sky;

}

bool CameraInsideAir() {

    return length(_WorldSpaceCameraPos - _PlanetCentre) < TopRadius();

}

#endif
