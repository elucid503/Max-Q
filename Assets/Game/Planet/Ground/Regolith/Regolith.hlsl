// Selene's regolith: Hapke reflectance lit by the sun, earthshine and the sunlit ground around, with a readable fill.
#ifndef MAXQ_REGOLITH_INCLUDED
#define MAXQ_REGOLITH_INCLUDED

#include "../Sunlight.hlsl"

// Published by SeleneLight: centre (scene), radius (km), earthshine illuminance and the direction toward Terra.
float3 _SeleneCentre;
float _SeleneRadius;
float3 _Earthshine;
float3 _EarthshineDirection;

// Single-scattering albedos: mature highland, mare, and fresh soil (young ejecta and steep walls).
#define HIGHLAND_ALBEDO float3(0.31, 0.285, 0.255)
#define MARE_ALBEDO float3(0.175, 0.165, 0.152)
#define FRESH_ALBEDO float3(0.46, 0.45, 0.44)

// Opposition surge amplitude and width, and backscatter (Henyey-Greenstein asymmetry, negated).
#define SURGE_AMPLITUDE 1.0
#define SURGE_WIDTH 0.06
#define BACKSCATTER 0.25

// Share of sunlight filling shadows while the sun is up.
#define SHADOW_FILL 0.02

// Lambertian albedo returning about as much light as the Hapke reflectance, for bounce light.
#define BOUNCED_ALBEDO 0.55

float3 RegolithAlbedo(float mare, float fresh, float steep = 0.0) {

    return lerp(lerp(HIGHLAND_ALBEDO, MARE_ALBEDO, mare), FRESH_ALBEDO, 0.6 * saturate(fresh + 0.8 * steep));

}

// Chandrasekhar's H function, Hapke's approximation.
float3 HapkeH(float x, float3 gamma) {

    return (1.0 + 2.0 * x) / (1.0 + 2.0 * x * gamma);

}

// Radiance per unit illuminance (sr^-1) for albedo w, light from l, viewed along v, surface normal n.
float3 Hapke(float3 w, float3 n, float3 l, float3 v) {

    float mu0 = saturate(dot(n, l));
    float mu = saturate(dot(n, v)) + 1e-3;

    if (mu0 <= 0.0) {

        return 0.0;

    }

    float cosG = clamp(dot(l, v), -1.0, 1.0);
    float halfTan = sqrt(saturate(1.0 - cosG) / max(1.0 + cosG, 1e-4));
    float surge = SURGE_AMPLITUDE / (1.0 + halfTan / SURGE_WIDTH);
    float b2 = BACKSCATTER * BACKSCATTER;
    float phase = (1.0 - b2) / pow(max(1.0 - 2.0 * BACKSCATTER * cosG + b2, 1e-4), 1.5);
    float3 gamma = sqrt(saturate(1.0 - w));

    return w / (4.0 * PI) * mu0 / (mu0 + mu) * ((1.0 + surge) * phase + HapkeH(mu0, gamma) * HapkeH(mu, gamma) - 1.0);

}

// Whether light from l clears the sphere's horizon, which dips as ground rises (height, km), so peaks light first.
float AboveHorizon(float3 up, float3 l, float height) {

    float dip = sqrt(2.0 * max(height, 0.0) / _SeleneRadius);

    return smoothstep(-dip - 0.004, -dip + 0.004, dot(up, l));

}

// Occlusion is the share of sky a spot leaves open; shadow is the sun's visibility past the ground's shadows.
float3 RegolithRadiance(float3 w, float3 normalWS, float3 positionWS, float shadow, float occlusion) {

    float3 fromCentre = positionWS - _SeleneCentre;
    float3 up = normalize(fromCentre);
    float height = length(fromCentre) - _SeleneRadius;
    float3 toCamera = normalize(_WorldSpaceCameraPos - positionWS);
    float sunUp = AboveHorizon(up, _SunDirection, height);

    float3 direct = _SunIlluminance * Hapke(w, normalWS, _SunDirection, toCamera) * sunUp * shadow;
    float3 earthshine = _Earthshine * Hapke(w, normalWS, _EarthshineDirection, toCamera) * AboveHorizon(up, _EarthshineDirection, height);

    // Bounce from sunlit surroundings where the ground hides the sky, plus the fill.
    float surroundings = dot(w, float3(0.2126, 0.7152, 0.0722)) * BOUNCED_ALBEDO;
    float sky = (0.5 + 0.5 * dot(normalWS, up)) * occlusion;
    float3 around = _SunIlluminance * (saturate(dot(up, _SunDirection)) * surroundings * (1.0 - sky) + SHADOW_FILL * sunUp * occlusion);

    return direct + earthshine + w * BOUNCED_ALBEDO / PI * around;

}

#endif
