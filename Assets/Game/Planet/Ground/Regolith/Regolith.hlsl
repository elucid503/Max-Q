// Selene's regolith: Hapke reflectance lit by the sun, earthshine and the sunlit ground around, with a readable fill.
#ifndef MAXQ_REGOLITH_INCLUDED
#define MAXQ_REGOLITH_INCLUDED

#include "../Sunlight.hlsl"

// Published by SeleneLight: centre (scene), radius (km), earthshine illuminance and the direction toward Terra.
float3 _SeleneCentre;
float _SeleneRadius;
float3 _Earthshine;
float3 _EarthshineDirection;

// Single-scattering albedos: mature highland, the maria's dark titanium-rich and paler flows, and fresh soil (young
// ejecta and steep walls).
#define HIGHLAND_ALBEDO float3(0.31, 0.285, 0.255)
#define MARE_DARK_ALBEDO float3(0.138, 0.136, 0.14)
#define MARE_PALE_ALBEDO float3(0.205, 0.19, 0.168)
#define FRESH_ALBEDO float3(0.46, 0.45, 0.44)

// How far the mottling (Mottling.cs, 0 to 1 about a half) swings the highlands' albedo either way.
#define HIGHLAND_MOTTLING 0.15

// Opposition surge amplitude and width, and backscatter (Henyey-Greenstein asymmetry, negated).
#define SURGE_AMPLITUDE 1.0
#define SURGE_WIDTH 0.06
#define BACKSCATTER 0.25

// Share of sunlight filling shadows while the sun is up: the sunlit ground round a shadow lights it about this much.
#define SHADOW_FILL 0.05

// Sine of the sun's elevation by which the fill is full.
#define FILL_CLIMB 0.25

// Slopes of the relief finer than a pixel, up close (where the mesh holds nearly all of it) and from UNRESOLVED_FAR (m)
// of footprint on.
#define ROUGHNESS_NEAR 0.015
#define ROUGHNESS_FAR 0.05
#define UNRESOLVED_FAR 2000.0

// Lambertian albedo returning about as much light as the Hapke reflectance, for bounce light.
#define BOUNCED_ALBEDO 0.55

float3 RegolithAlbedo(float mare, float fresh, float steep = 0.0, float mottling = 0.5) {

    float3 highland = HIGHLAND_ALBEDO * (1.0 + 2.0 * HIGHLAND_MOTTLING * (mottling - 0.5));
    float3 maria = lerp(MARE_DARK_ALBEDO, MARE_PALE_ALBEDO, saturate(1.4 * mottling - 0.2));

    return lerp(lerp(highland, maria, mare), FRESH_ALBEDO, 0.6 * saturate(fresh + 0.8 * steep));

}

// The slopes of the relief a pixel spanning footprint metres cannot show.
float UnresolvedRoughness(float footprint) {

    return lerp(ROUGHNESS_NEAR, ROUGHNESS_FAR, saturate(footprint / UNRESOLVED_FAR));

}

// The sun's cosine on a spot whose unresolved facets tilt by about roughness: those facing the sun catch it a little past
// the mean ground's terminator, so day fades into night over a band as wide as their slopes rather than at a line.
float SoftCosine(float x, float roughness) {

    return max(x, 0.0) + roughness * log(1.0 + exp(-abs(x) / roughness));

}

// Chandrasekhar's H function, Hapke's approximation.
float3 HapkeH(float x, float3 gamma) {

    return (1.0 + 2.0 * x) / (1.0 + 2.0 * x * gamma);

}

// Radiance per unit illuminance (sr^-1) for albedo w, light from l, viewed along v, surface normal n, its unresolved
// facets tilting by about roughness.
float3 Hapke(float3 w, float3 n, float3 l, float3 v, float roughness) {

    float cosine = dot(n, l);

    if (cosine < -6.0 * roughness) {

        return 0.0;

    }

    float mu0 = SoftCosine(cosine, roughness);
    float mu = saturate(dot(n, v)) + 1e-3;

    float cosG = clamp(dot(l, v), -1.0, 1.0);
    float halfTan = sqrt(saturate(1.0 - cosG) / max(1.0 + cosG, 1e-4));
    float surge = SURGE_AMPLITUDE / (1.0 + halfTan / SURGE_WIDTH);
    float b2 = BACKSCATTER * BACKSCATTER;
    float phase = (1.0 - b2) / pow(max(1.0 - 2.0 * BACKSCATTER * cosG + b2, 1e-4), 1.5);
    float3 gamma = sqrt(saturate(1.0 - w));

    return w / (4.0 * PI) * mu0 / (mu0 + mu) * ((1.0 + surge) * phase + HapkeH(mu0, gamma) * HapkeH(mu, gamma) - 1.0);

}

// Whether light from l clears the sphere's horizon, which dips as ground rises (height, km), so peaks light first, and
// the unresolved relief's peaks (roughness) a little before the rest.
float AboveHorizon(float3 up, float3 l, float height, float roughness) {

    float dip = sqrt(2.0 * max(height, 0.0) / _SeleneRadius);

    return smoothstep(-dip - 0.004 - 2.0 * roughness, -dip + 0.004, dot(up, l));

}

// Occlusion is the share of sky a spot leaves open; shadow is the sun's visibility past the ground's shadows; roughness
// the slopes of the relief finer than the pixel (UnresolvedRoughness).
float3 RegolithRadiance(float3 w, float3 normalWS, float3 positionWS, float shadow, float occlusion, float roughness = ROUGHNESS_NEAR) {

    float3 fromCentre = positionWS - _SeleneCentre;
    float3 up = normalize(fromCentre);
    float height = length(fromCentre) - _SeleneRadius;
    float3 toCamera = normalize(_WorldSpaceCameraPos - positionWS);
    float sunUp = AboveHorizon(up, _SunDirection, height, roughness);

    float3 direct = _SunIlluminance * Hapke(w, normalWS, _SunDirection, toCamera, roughness) * sunUp * shadow;
    float3 earthshine = _Earthshine * Hapke(w, normalWS, _EarthshineDirection, toCamera, roughness) * AboveHorizon(up, _EarthshineDirection, height, roughness);

    // Bounce from sunlit surroundings where the ground hides the sky, plus the fill.
    float surroundings = dot(w, float3(0.2126, 0.7152, 0.0722)) * BOUNCED_ALBEDO;
    float sky = (0.5 + 0.5 * dot(normalWS, up)) * occlusion;
    // The surroundings brighten as the sun climbs on them, so the fill fades out toward the terminator with the day.
    float climb = saturate(SoftCosine(dot(up, _SunDirection), roughness) / FILL_CLIMB);
    float3 around = _SunIlluminance * (saturate(dot(up, _SunDirection)) * surroundings * (1.0 - sky) + SHADOW_FILL * climb * sunUp * occlusion);

    return direct + earthshine + w * BOUNCED_ALBEDO / PI * around;

}

#endif
