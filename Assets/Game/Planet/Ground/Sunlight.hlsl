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
    light.direct = _SunIlluminance * SunTransmittance(light.up * r, _SunDirection);
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

#endif
