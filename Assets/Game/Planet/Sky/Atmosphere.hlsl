// Earth's atmosphere wrapped around Terra, after Hillaire, "A Scalable and Production Ready Sky and Atmosphere
// Rendering Technique" (2020). Scene units are kilometres, so the coefficients are per kilometre.
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

// Low haze that pools in the valleys around the camera, below a flat top _FogTop (km above the reference radius), with
// extinction _FogDensity per kilometre; its droplets scatter like the aerosol and absorb nothing.
float _FogTop;
float _FogDensity;

TEXTURE2D(_TransmittanceLut);
TEXTURE2D(_MultiScatterLut);
TEXTURE2D(_IrradianceLut);
TEXTURE2D(_SkyViewLut);
SAMPLER(sampler_linear_clamp);

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

// Sun visibility past the ground's cascaded shadows; one wherever the cascades do not reach.
float SunShadow(float3 positionWS) {

    #if defined(_MAIN_LIGHT_SHADOWS_CASCADE)
    float4 coord = TransformWorldToShadowCoord(positionWS);

    return lerp(MainLightRealtimeShadow(coord), 1.0, GetMainLightShadowFade(positionWS));
    #else
    return 1.0;
    #endif

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

struct Scattering {

    float3 radiance;
    float3 transmittance;

};

// Light scattered toward the origin along a ray, per unit of sun illuminance, and the ray's transmittance. Steps
// crowd toward the origin when it is inside the air, where the air is densest, and sample the air at their middles. With
// shadows, the ground's cascaded shadows carve the sunlight, so mountains cast shafts into the haze; those are looked up
// at jitter (0 to 1) of each step, which neighbouring rays vary so shafts blend instead of banding.
// With fog, each step also takes the stretch of it that lies inside the low haze, which is uniform, so a thin layer
// counts in full however long the step.
Scattering Integrate(float3 origin, float3 direction, float tMax, float3 sun, int steps, bool multiScatter, float jitter = 0.3, bool shadows = false, bool fog = false) {

    Scattering result;
    result.radiance = 0.0;
    result.transmittance = 1.0;

    float2 top = RaySphere(origin, direction, TopRadius());

    if (top.y <= 0.0) {

        return result;

    }

    float2 ground = RaySphere(origin, direction, _PlanetRadius);
    float start = max(top.x, 0.0);
    float end = min(top.y, tMax);

    if (ground.x > 0.0) {

        end = min(end, ground.x);

    }

    if (end <= start) {

        return result;

    }

    float cosTheta = dot(direction, sun);
    float rayleighPhase = RayleighPhase(cosTheta);
    float miePhase = MiePhase(cosTheta);
    bool inside = top.x <= 0.0;
    float previous = start;
    float2 haze = fog && _FogDensity > 0.0 ? RaySphere(origin, direction, _PlanetRadius + _FogTop) : -1.0;

    for (int i = 0; i < steps; i++) {

        float f = (i + 1.0) / steps;
        float t = start + (end - start) * (inside ? f * f : f);
        float dt = t - previous;
        float3 p = origin + direction * (previous + 0.5 * dt);
        float r = length(p);
        float muSun = dot(p, sun) / r;

        Medium medium = SampleMedium(r - _PlanetRadius);
        float3 sunlight = SunTransmittance(p, sun) * FogSunTransmittance(p, sun);

        if (shadows) {

            sunlight *= SunShadow(origin + direction * (previous + jitter * dt) + _PlanetCentre);

        }

        float3 ambient = multiScatter ? MultiScatter(r, muSun) : 0.0;
        float3 scattered = ((medium.rayleigh * rayleighPhase + medium.mie * miePhase) * sunlight + medium.scattering * ambient) * dt;
        float3 depth = medium.extinction * dt;
        float inHaze = max(min(t, haze.y) - max(previous, haze.x), 0.0);

        if (inHaze > 0.0) {

            float3 q = origin + direction * (max(previous, haze.x) + 0.5 * inHaze);
            float3 hazeLight = SunTransmittance(q, sun) * FogSunTransmittance(q, sun);

            if (shadows) {

                hazeLight *= SunShadow(q + _PlanetCentre);

            }

            scattered += _FogDensity * inHaze * (miePhase * hazeLight + ambient);
            depth += _FogDensity * inHaze;

        }

        float3 stepTransmittance = exp(-depth);

        result.radiance += result.transmittance * scattered * (1.0 - stepTransmittance) / max(depth, 1e-9);
        result.transmittance *= stepTransmittance;
        previous = t;

    }

    return result;

}

// Hillaire's sky-view parameterisation around the camera: latitude crowds toward the horizon, where the sky changes
// fastest, and azimuth is measured from the sun, crowding toward it.
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

// Sky radiance seen from the camera in a direction, from the per-frame sky-view table; only valid inside the air.
float3 SkyRadiance(float3 direction) {

    float3 fromCentre = _WorldSpaceCameraPos - _PlanetCentre;
    float viewHeight = max(length(fromCentre), _PlanetRadius + 1e-3);
    float3 up = fromCentre / length(fromCentre);
    float3 flatView = direction - up * dot(direction, up);
    float3 flatSun = _SunDirection - up * dot(_SunDirection, up);
    float cosLight = dot(flatView, flatSun) / max(length(flatView) * length(flatSun), 1e-5);

    return SAMPLE_TEXTURE2D_LOD(_SkyViewLut, sampler_linear_clamp, SkyViewUv(viewHeight, dot(direction, up), cosLight), 0).rgb;

}

bool CameraInsideAir() {

    return length(_WorldSpaceCameraPos - _PlanetCentre) < TopRadius();

}

#endif
