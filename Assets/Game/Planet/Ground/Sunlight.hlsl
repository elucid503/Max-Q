// Sunlight and skylight on the ground and anything standing on it, through the air and past the ground's shadows.
#ifndef MAXQ_SUNLIGHT_INCLUDED
#define MAXQ_SUNLIGHT_INCLUDED

#include "../Sky/Atmosphere.hlsl"

struct Sunlight {

    float3 direct;
    float shadow;
    float3 sky;
    float3 up;

};

// Sun and skylight arriving at a point on the ground, both already dimmed and coloured by the air above it; direct is
// before the ground's own shadows, which shadow carries.
Sunlight SunlightAt(float3 positionWS) {

    float3 fromCentre = positionWS - _PlanetCentre;
    float r = max(length(fromCentre), _PlanetRadius + 1e-3);

    Sunlight light;
    light.up = fromCentre / length(fromCentre);
    light.direct = _SunIlluminance * SunTransmittance(light.up * r, _SunDirection) * FogSunTransmittance(fromCentre, _SunDirection);
    light.shadow = SunShadow(positionWS);
    light.sky = _SunIlluminance * SkyIrradiance(r, dot(light.up, _SunDirection));

    return light;

}

// Sun, the sky the surrounding ground leaves open (occlusion), and light bounced off that ground (of albedo
// surroundings) from wherever it hides the sky.
float3 GroundRadiance(float3 albedo, float3 normalWS, Sunlight light, float3 surroundings, float occlusion) {

    float direct = saturate(dot(normalWS, _SunDirection)) * light.shadow;
    float sky = (0.5 + 0.5 * dot(normalWS, light.up)) * occlusion;
    float3 lit = light.direct * saturate(dot(light.up, _SunDirection)) + light.sky;

    return albedo / PI * (light.direct * direct + light.sky * sky + surroundings * lit * (1.0 - sky));

}

// Snow: the ground's diffuse light, plus light that wanders through the pack past the terminator and loses its red on
// the way, a broad sheen where grains mirror the sun toward the eye at a low angle, and glitter, the share of the sun's
// direct light (zero to a few times over) that single grain facets throw at the eye.
float3 SnowRadiance(float3 albedo, float3 normalWS, float3 toCamera, Sunlight light, float3 surroundings, float occlusion, float glitter) {

    float3 base = GroundRadiance(albedo, normalWS, light, surroundings, occlusion);
    float nl = dot(normalWS, _SunDirection);
    float nv = saturate(dot(normalWS, toCamera)) + 1e-3;
    float3 sun = light.direct * light.shadow;

    float wrap = saturate((nl + 0.35) / 1.35) - saturate(nl);
    float3 subsurface = sun * wrap * albedo * float3(0.25, 0.5, 0.75) / PI;

    float roughness = 0.45;
    float a2 = roughness * roughness * roughness * roughness;
    float3 h = normalize(toCamera + _SunDirection);
    float nh = saturate(dot(normalWS, h));
    float d = nh * nh * (a2 - 1.0) + 1.0;
    float k = roughness * roughness * 0.5;
    float visibility = 0.25 / ((nv * (1.0 - k) + k) * (saturate(nl) * (1.0 - k) + k));
    float fresnel = 0.02 + 0.98 * pow(1.0 - saturate(dot(h, toCamera)), 5.0);
    float3 sheen = sun * saturate(nl) * a2 / (PI * d * d) * visibility * fresnel;

    return base + subsurface + sheen + sun * glitter;

}

#endif
