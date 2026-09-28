// Light in water, shared by the water surface and the seabed under it. Depths and paths here are real-world metres:
// Terra's water is a fifth as deep as the Earth's it stands for, and absorbs as the Earth's does, so shelves and lagoons
// read as the real places do.
#ifndef MAXQ_WATER_OPTICS_INCLUDED
#define MAXQ_WATER_OPTICS_INCLUDED

#define WATER_TYPES 8
#define TERRA_SCALE 0.2

// Water deeper than this (m, on Terra) hides its bed: the ground under it is not drawn (see PatchJob).
#define BED_REACH 20.0

// Jerlov's water types from the clearest ocean to the murkiest coast (I, II, III, 1C, 3C, 5C, 7C, 9C): diffuse
// attenuation per metre at the red, green and blue channels (620, 540, 460 nm), after Jerlov (1976).
static const float3 JerlovAttenuation[WATER_TYPES] = {

    float3(0.26, 0.052, 0.019), float3(0.29, 0.072, 0.043), float3(0.33, 0.108, 0.083), float3(0.37, 0.14, 0.18),
    float3(0.41, 0.19, 0.33), float3(0.47, 0.27, 0.52), float3(0.58, 0.42, 0.86), float3(0.75, 0.66, 1.5),

};

// Backscattering by particles at 540 nm, per metre, falling as one over wavelength; pure water backscatters as Morel
// (1974) measured, falling with the 4.32nd power.
static const float JerlovParticles[WATER_TYPES] = { 0.0002, 0.0008, 0.002, 0.004, 0.009, 0.016, 0.03, 0.05 };

#define WATER_BACKSCATTER float3(0.0004, 0.0008, 0.0016)
#define PARTICLE_SPECTRUM float3(540.0 / 620.0, 1.0, 540.0 / 460.0)

struct WaterOptics {

    float3 attenuation;
    float3 reflectance;

};

// Optics of a water type (0 to WATER_TYPES - 1, blended between types). Deep water's irradiance reflectance is
// Gordon's 0.33 bb / (a + bb), with absorption taken as three quarters of the diffuse attenuation.
WaterOptics OpticsOf(float type) {

    float t = clamp(type, 0.0, WATER_TYPES - 1.001);
    int i = (int)t;
    float f = t - i;

    WaterOptics optics;
    optics.attenuation = lerp(JerlovAttenuation[i], JerlovAttenuation[i + 1], f);

    float3 backscatter = WATER_BACKSCATTER + lerp(JerlovParticles[i], JerlovParticles[i + 1], f) * PARTICLE_SPECTRUM;

    optics.reflectance = 0.33 * backscatter / (0.75 * optics.attenuation);

    return optics;

}

// Which water a place holds: the open ocean clear, clouding toward the coast as the shore nears and the water shallows,
// so shelf seas, bays and lakes are greener. Distances and depths in real metres.
float WaterTypeAt(float shoreDistance, float depth) {

    float coast = 1.0 - smoothstep(2e3, 80e3, -shoreDistance);
    float shallow = 1.0 - smoothstep(5.0, 60.0, depth);
    float near = max(1.0 - smoothstep(0.0, 8e3, -shoreDistance), shallow * coast);

    return lerp(0.5, lerp(1.5, 4.0, near), max(coast, shallow * 0.5));

}

#endif
