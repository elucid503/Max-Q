// Selene's horizon glow: levitated dust lit just after sunset, a thin band over the sunset point (Surveyor).
#ifndef MAXQ_HORIZON_GLOW_INCLUDED
#define MAXQ_HORIZON_GLOW_INCLUDED

// From SeleneLight; the glow is zero away from Selene's ground.
float3 _SeleneCentre;
float _SeleneRadius;
float _HorizonGlow;

// Band height and azimuth spread, and the sun depressions it rises and fades over (radians).
#define GLOW_HEIGHT 0.012
#define GLOW_SPREAD 0.2
#define GLOW_RISE 0.005
#define GLOW_SET_START 0.03
#define GLOW_SET_END 0.08
#define GLOW_COLOUR float3(1.0, 0.9, 0.78)

float3 HorizonGlow(float3 direction) {

    if (_HorizonGlow <= 0.0) {

        return 0.0;

    }

    float3 fromCentre = _WorldSpaceCameraPos - _SeleneCentre;
    float3 up = normalize(fromCentre);
    float dip = acos(saturate(_SeleneRadius / length(fromCentre)));
    float elevation = asin(clamp(dot(direction, up), -1.0, 1.0)) + dip;
    float depression = -asin(clamp(dot(_SunDirection, up), -1.0, 1.0)) - dip;
    float3 flatView = direction - up * dot(direction, up);
    float3 flatSun = _SunDirection - up * dot(_SunDirection, up);
    float azimuth = acos(clamp(dot(normalize(flatView + 1e-6), normalize(flatSun + 1e-6)), -1.0, 1.0));

    float setting = smoothstep(0.0, GLOW_RISE, depression) * (1.0 - smoothstep(GLOW_SET_START, GLOW_SET_END, depression));
    float band = exp(-max(elevation, 0.0) / GLOW_HEIGHT) * exp(-0.5 * azimuth * azimuth / (GLOW_SPREAD * GLOW_SPREAD));

    return _SunIlluminance / PI * _HorizonGlow * setting * band * GLOW_COLOUR;

}

#endif
