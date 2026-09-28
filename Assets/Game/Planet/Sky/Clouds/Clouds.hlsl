// Clouds carved from tiling noise where the weather puts them (Schneider 2015), lit after Hillaire (2016); units are km.
#ifndef MAXQ_CLOUDS_INCLUDED
#define MAXQ_CLOUDS_INCLUDED

#include "../Atmosphere.hlsl"

// CloudNoise.compute's weather: cover, base, top and anvil cover; row 0 at the north pole, column 0 at 180 W.
TEXTURE2D(_CloudWeather);
TEXTURE3D(_CloudShape);
TEXTURE3D(_CloudDetail);

// Each mip holds the shape's highest value over its cell and their neighbours, so it bounds the noise within a cell's width.
TEXTURE3D(_CloudShapeBound);

SAMPLER(sampler_linear_repeatU_clampV);
SAMPLER(sampler_trilinear_repeat);
SAMPLER(sampler_point_repeat);

// Scene directions to the body-fixed frame, and to the noise's frame, which drifts east with the weather.
float4x4 _CloudBodyFromScene;
float4x4 _CloudNoiseFromScene;
float3 _CloudShapeOffset;
float3 _CloudDetailOffset;

// Height ranges (km) of the weather; match WEATHER_BASE_RANGE and WEATHER_TOP_RANGE in CloudNoise.compute.
#define CLOUD_BASE_RANGE 8.0
#define CLOUD_TOP 16.0

// Noise tiles (km): heaps, a second heap tiling, towers and anvils, the mask between the heap tilings, edge detail.
#define CLOUD_SHAPE_SIZE 5.0
#define CLOUD_SHAPE_OTHER_SIZE 7.3
#define CLOUD_STORM_SIZE 30.0
#define CLOUD_MIX_SIZE 90.0
#define CLOUD_DETAIL_SIZE 0.7

// The noise shifts sideways as it climbs, so towers lean and the tiles never stack into terraces.
#define CLOUD_LEAN float3(0.23, 0.0, 0.14)

#define CLOUD_EXTINCTION 40.0
#define ANVIL_EXTINCTION 6.0

// View-ray steps (km): clear air grows them with distance; where cloud may lie they halve toward about one optical depth.
#define CLOUD_MIN_STEP 0.04
#define CLOUD_STEP_GROWTH 0.01
#define CLOUD_MAX_SKIP 3.0
#define CLOUD_FINE_STEP 0.1
#define CLOUD_THIN_STEP 0.3
#define CLOUD_REFINES 3
#define CLOUD_LIGHT_STEP 0.08
#define CLOUD_GROUND_ALBEDO 0.25
#define CLOUD_FAR 10000.0

// Height (km) where towers stop and spread into anvils; matches Tropopause in CloudNoise.compute.
float Tropopause(float sinLatitude) {

    return lerp(15.0, 11.0, smoothstep(0.35, 0.7, abs(sinLatitude)));

}

float CloudRemap(float value, float from, float to, float newFrom, float newTo) {

    return newFrom + (value - from) / (to - from) * (newTo - newFrom);

}

float LayerGap(float altitude, float bottom, float top) {

    return max(max(bottom - altitude, altitude - top), 0.0);

}

// The mip that blurs a noise of texels across its tile (km) to a footprint (km).
float NoiseLod(float footprint, float tile, float texels) {

    return log2(max(footprint * texels / tile, 1.0));

}

float ShapeNoise(float3 uvw, float lod) {

    return SAMPLE_TEXTURE3D_LOD(_CloudShape, sampler_trilinear_repeat, uvw, lod).r;

}

// The highest the shape noise of a tile (km) reaches within radius (km) of uvw.
float ShapeBound(float3 uvw, float radius, float tile) {

    return SAMPLE_TEXTURE3D_LOD(_CloudShapeBound, sampler_point_repeat, uvw, clamp(ceil(NoiseLod(radius, tile, 128.0)), 0.0, 7.0)).r;

}

// Two tilings mixed by a mask are rescaled to keep their spread.
float Stretch(float value, float mix) {

    return saturate(0.5 + (value - 0.5) * rsqrt(mix * mix + (1.0 - mix) * (1.0 - mix)));

}

struct CloudPoint {

    float extinction;
    // 0 at the layer's base to 1 at its top.
    float height;
    // Kilometres to the nearest layer that could hold cloud; zero inside one.
    float gap;

};

// The weather over a point, as its layers stand there.
struct CloudColumn {

    float altitude;
    float3 q;
    float cover;
    float bottom;
    float top;
    float anvil;
    float anvilBottom;
    float anvilTop;
    float deep;
    float stratiform;
    float mix;

};

// The same weather at another point nearby.
CloudColumn Moved(CloudColumn column, float3 p) {

    column.altitude = length(p) - _PlanetRadius;
    column.q = mul((float3x3)_CloudNoiseFromScene, p) + CLOUD_LEAN * column.altitude;

    return column;

}

CloudColumn ColumnAt(float3 p) {

    float3 body = mul((float3x3)_CloudBodyFromScene, p);
    float sinLatitude = body.y / length(body);
    float2 uv = float2(atan2(body.z, body.x) * (0.5 / PI) + 0.5, acos(clamp(sinLatitude, -1.0, 1.0)) / PI);
    float4 weather = SAMPLE_TEXTURE2D_LOD(_CloudWeather, sampler_linear_repeatU_clampV, uv, 0.0);

    CloudColumn column;
    column.altitude = length(p) - _PlanetRadius;
    column.q = mul((float3x3)_CloudNoiseFromScene, p) + CLOUD_LEAN * column.altitude;
    column.cover = weather.r;
    column.bottom = weather.g * CLOUD_BASE_RANGE;
    column.top = max(weather.b * CLOUD_TOP, column.bottom + 0.1);
    column.anvil = weather.a;
    column.anvilTop = Tropopause(sinLatitude);
    column.anvilBottom = column.anvilTop - 0.5 - 2.5 * weather.a;

    // Deep columns are storm towers; shallow ones under full cover spread into decks.
    column.deep = saturate((column.top - column.bottom - 3.0) / 5.0);
    column.stratiform = (1.0 - column.deep) * smoothstep(0.55, 0.85, column.cover);
    column.mix = smoothstep(0.3, 0.7, ShapeNoise(column.q / CLOUD_MIX_SIZE, 0.0));

    return column;

}

// Cloud at the column's point, the noise blurred to a footprint (km) and edges eroded by detail (0 to 1).
CloudPoint CloudIn(CloudColumn column, float footprint, float detail) {

    CloudPoint cloud;
    cloud.extinction = 0.0;
    cloud.height = 0.0;

    float3 q = column.q;
    float towerGap = column.cover > 0.004 ? LayerGap(column.altitude, column.bottom, column.top) : CLOUD_TOP;
    float anvilGap = column.anvil > 0.004 ? LayerGap(column.altitude, column.anvilBottom, column.anvilTop) : CLOUD_TOP;

    cloud.gap = min(towerGap, anvilGap);

    if (cloud.gap > 0.0) {

        return cloud;

    }

    float stormLod = NoiseLod(footprint, CLOUD_STORM_SIZE, 128.0);

    if (towerGap <= 0.0) {

        // Profiles peak rather than plateau, so heaps round off: cumulus on a flat base, decks swelling in the middle.
        float h = (column.altitude - column.bottom) / (column.top - column.bottom);
        float cumulus = saturate(h / 0.06) * sqrt(saturate(1.0 - h));
        float deck = sqrt(saturate(4.0 * h * (1.0 - h)));
        float first = ShapeNoise((q + _CloudShapeOffset) / CLOUD_SHAPE_SIZE, NoiseLod(footprint, CLOUD_SHAPE_SIZE, 128.0));
        float second = ShapeNoise(q / CLOUD_SHAPE_OTHER_SIZE + 0.21, NoiseLod(footprint, CLOUD_SHAPE_OTHER_SIZE, 128.0));
        float shape = Stretch(lerp(first, second, column.mix), column.mix);

        if (column.deep > 0.0) {

            shape = lerp(shape, ShapeNoise(q / CLOUD_STORM_SIZE, stormLod), 0.6 * column.deep);

        }

        float density = saturate(CloudRemap(shape * lerp(cumulus, deck, column.stratiform), 1.0 - column.cover, 1.0, 0.0, 1.0));

        // Wisps along the base, billows above, fading out as the footprint outgrows them.
        float erosion = 0.4 * detail * saturate((0.5 * CLOUD_DETAIL_SIZE - footprint) / (0.4 * CLOUD_DETAIL_SIZE));

        if (density > 0.0 && erosion > 0.0) {

            float fine = SAMPLE_TEXTURE3D_LOD(_CloudDetail, sampler_trilinear_repeat, (q + _CloudDetailOffset) / CLOUD_DETAIL_SIZE,
                NoiseLod(footprint, CLOUD_DETAIL_SIZE, 32.0)).r;

            density = saturate(CloudRemap(density, erosion * lerp(fine, 1.0 - fine, saturate(4.0 * h)), 1.0, 0.0, 1.0));

        }

        cloud.extinction = density * CLOUD_EXTINCTION;
        cloud.height = h;

    }

    if (anvilGap <= 0.0) {

        float h = (column.altitude - column.anvilBottom) / (column.anvilTop - column.anvilBottom);
        float shape = ShapeNoise(q / CLOUD_STORM_SIZE + 0.37, stormLod);
        float density = saturate(CloudRemap(shape * saturate(h / 0.35) * saturate((1.0 - h) / 0.15), 1.0 - column.anvil, 1.0, 0.0, 1.0));

        if (density * ANVIL_EXTINCTION > cloud.extinction) {

            cloud.extinction = density * ANVIL_EXTINCTION;
            cloud.height = 1.0;

        }

    }

    return cloud;

}

// CloudIn's test with layers widened and noise replaced by its bounds, so it never misses cloud within radius (km); the
// weather may have been read up to spread (km) away. Also gives the kilometres to the nearest layer.
bool CloudPossible(CloudColumn column, float radius, float spread, out float gap) {

    float reach = radius + spread + 0.1;
    float margin = 0.05 + 0.05 * (radius + spread);
    float towerGap = column.cover > 0.004 ? LayerGap(column.altitude, column.bottom, column.top) : CLOUD_TOP;
    float anvilGap = column.anvil > 0.004 ? LayerGap(column.altitude, column.anvilBottom, column.anvilTop) : CLOUD_TOP;
    float3 q = column.q;

    // The lean moves the noise up to 0.27 km per km climbed, so its bounds must reach that much farther.
    float wide = 1.3 * radius;

    gap = min(towerGap, anvilGap);

    if (towerGap <= reach) {

        float highest = max(ShapeBound((q + _CloudShapeOffset) / CLOUD_SHAPE_SIZE, wide, CLOUD_SHAPE_SIZE),
            ShapeBound(q / CLOUD_SHAPE_OTHER_SIZE + 0.21, wide, CLOUD_SHAPE_OTHER_SIZE));
        float shape = Stretch(highest, column.mix);

        if (column.deep > 0.0) {

            shape = max(shape, ShapeBound(q / CLOUD_STORM_SIZE, wide, CLOUD_STORM_SIZE));

        }

        if (shape > 1.0 - saturate(column.cover + margin)) {

            return true;

        }

    }

    return anvilGap <= reach && ShapeBound(q / CLOUD_STORM_SIZE + 0.37, wide, CLOUD_STORM_SIZE) > 1.0 - saturate(column.anvil + margin);

}

// Optical depth toward the sun over tripling steps, each sampled at jitter within its middle half so no contours print.
float CloudSunDepth(CloudColumn column, float3 p, float footprint, float detail, int steps, float jitter) {

    float depth = 0.0;
    float t = 0.0;
    float stride = CLOUD_LIGHT_STEP;

    for (int i = 0; i < steps; i++) {

        depth += CloudIn(Moved(column, p + _SunDirection * (t + (0.25 + 0.5 * jitter) * stride)), max(footprint, 0.25 * stride), i < 2 ? detail : 0.0).extinction * stride;
        t += stride;
        stride *= 3.0;

    }

    return depth;

}

float HenyeyGreenstein(float g, float cosTheta) {

    float g2 = g * g;

    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4), 1.5));

}

// Radiance per unit extinction: sunlight with Wrenninge's multiple-scattering octaves, plus the sky above and ground below.
float3 CloudLight(CloudColumn column, float3 p, float cosTheta, CloudPoint cloud, float footprint, float detail, int lightSteps, float jitter) {

    float r = length(p);
    float muSun = dot(p, _SunDirection) / r;
    float3 sun = _SunIlluminance * SunTransmittance(p, _SunDirection);
    float3 light = 0.0;

    if (any(sun > 0.0)) {

        float depth = CloudSunDepth(column, p, footprint, detail, lightSteps, jitter);
        float energy = 1.0;
        float reach = 1.0;
        float spread = 1.0;
        float octaves = 0.0;

        for (int i = 0; i < 4; i++) {

            float phase = 0.7 * HenyeyGreenstein(0.8 * spread, cosTheta) + 0.3 * HenyeyGreenstein(-0.3 * spread, cosTheta);

            octaves += energy * phase * exp(-depth * reach);
            energy *= 0.55;
            reach *= 0.4;
            spread *= 0.55;

        }

        light = sun * octaves;

    }

    float3 sky = _SunIlluminance * SkyIrradiance(r, muSun);
    float3 ground = sun * saturate(muSun) * CLOUD_GROUND_ALBEDO;

    return light + (sky * lerp(0.4, 1.0, cloud.height) + 0.5 * ground * (1.0 - cloud.height)) / (2.0 * PI);

}

struct CloudTrace {

    float3 radiance;
    float transmittance;
    // Mean distance of the light the clouds send back, or of the layer when the ray meets none.
    float depth;

};

// White noise from a texel and a frame (PCG), so what the history leaves of it is no pattern.
float CloudHash(uint2 texel, uint frame) {

    uint3 v = uint3(texel, frame) * 1664525u + 1013904223u;

    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;

    return (v.x & 0xFFFFFFu) / 16777216.0;

}

// The clouds along a ray from origin (relative to the centre) to tMax. Steps halve while the bounds say cloud may lie
// along them and cross whole when proven clear, so no cloud is stepped over; samples fall at jitter (0 to 1) of a step.
CloudTrace TraceClouds(float3 origin, float3 direction, float tMax, float jitter, float pixelAngle, int steps, int lightSteps, bool detail) {

    CloudTrace result;
    result.radiance = 0.0;
    result.transmittance = 1.0;
    result.depth = min(tMax, CLOUD_FAR);

    float2 shell = RaySphere(origin, direction, _PlanetRadius + CLOUD_TOP);
    float ground = RaySphere(origin, direction, _PlanetRadius).x;
    float start = max(shell.x, 0.0);
    float end = min(shell.y, ground > 0.0 ? min(tMax, ground) : tMax);

    if (end <= start) {

        return result;

    }

    float cosTheta = dot(direction, _SunDirection);
    float longest = max((end - start) / 48.0, CLOUD_MIN_STEP);
    float erode = detail ? 1.0 : 0.0;
    float t = start;
    float sampling = CLOUD_FINE_STEP;
    float weighted = 0.0;
    float weight = 0.0;

    for (int i = 0; i < steps && t < end; i++) {

        float near = max(t, CLOUD_MIN_STEP);
        float growth = max(CLOUD_STEP_GROWTH, log(end / near) / (steps - i));
        float stride = clamp(near * growth, CLOUD_MIN_STEP, longest);
        float fine = clamp(sampling, max(0.5 * near * pixelAngle, CLOUD_MIN_STEP), stride);
        float first = stride;
        float gap = 0.0;
        bool clear = false;

        // The weather is read once at the full step's middle; the halved steps test against it.
        CloudColumn column = ColumnAt(origin + direction * (t + 0.5 * stride));

        for (int k = 0; k <= CLOUD_REFINES; k++) {

            column = Moved(column, origin + direction * (t + 0.5 * stride));

            if (!CloudPossible(column, 0.5 * stride, 0.5 * (first - stride), gap)) {

                clear = true;

                break;

            }

            if (stride <= fine || k == CLOUD_REFINES) {

                break;

            }

            stride = max(0.5 * stride, fine);

        }

        if (clear) {

            // The layer lies at least gap up or down, so the ray may run on until it has climbed that far.
            float3 middle = origin + direction * (t + 0.5 * stride);
            float climb = abs(dot(direction, middle)) / length(middle);

            t += max(stride, 0.5 * stride + min(max(gap - 0.5 * stride - 0.1, 0.0) / max(climb, 0.1), CLOUD_MAX_SKIP));
            sampling = CLOUD_FINE_STEP;

            continue;

        }

        float at = min(t + stride * jitter, end);
        float3 p = origin + direction * at;
        float footprint = at * pixelAngle;

        column = Moved(column, p);

        CloudPoint cloud = CloudIn(column, footprint, erode);

        t += stride;

        if (cloud.extinction <= 0.0) {

            continue;

        }

        // Behind cloud that already hides most of what follows, a short look toward the sun does.
        float3 light = CloudLight(column, p, cosTheta, cloud, footprint, erode, result.transmittance > 0.3 ? lightSteps : 2, jitter);
        float stepTransmittance = exp(-cloud.extinction * stride);
        float absorbed = result.transmittance * (1.0 - stepTransmittance);

        result.radiance += absorbed * light;
        result.transmittance *= stepTransmittance;
        weighted += absorbed * at;
        weight += absorbed;
        sampling = clamp(1.0 / cloud.extinction, CLOUD_MIN_STEP, lerp(CLOUD_THIN_STEP, CLOUD_FINE_STEP, saturate(cloud.extinction / 10.0)));

        if (result.transmittance < 0.02) {

            break;

        }

    }

    result.depth = weight > 0.0 ? weighted / weight : min(0.5 * (start + end), CLOUD_FAR);

    return result;

}

// Sunlight the clouds let through along the sun line through origin, climbing from where it leaves the ground, and how far
// sunward the shade starts (clear lines give the layer's middle, so filtering never drags a neighbour's shade far off).
float2 CloudShade(float3 origin, float footprint, float jitter, int steps) {

    // RaySphere gives both hits either side of origin, and equal ones on a miss.
    float2 shell = RaySphere(origin, _SunDirection, _PlanetRadius + CLOUD_TOP);
    float2 ground = RaySphere(origin, _SunDirection, _PlanetRadius);

    if (shell.x >= shell.y) {

        return float2(1.0, 0.0);

    }

    float start = ground.x < ground.y ? max(shell.x, ground.y) : shell.x;
    float end = shell.y;
    float t = start;
    float transmittance = 1.0;
    float weighted = 0.0;
    float weight = 0.0;

    for (int i = 0; i < steps && t < end; i++) {

        float stride = clamp(max((t - start) * CLOUD_STEP_GROWTH, 0.25 * (end - t) / (steps - i)), 0.05, 0.4);
        float at = t + stride * jitter;
        float3 p = origin + _SunDirection * at;
        CloudPoint cloud = CloudIn(ColumnAt(p), footprint, 0.0);

        if (cloud.extinction <= 0.0) {

            t += max(stride, min(cloud.gap / max(dot(p, _SunDirection) / length(p), 0.05), 2.0 * CLOUD_MAX_SKIP));

            continue;

        }

        float passed = exp(-cloud.extinction * stride);
        float absorbed = transmittance * (1.0 - passed);

        weighted += absorbed * at;
        weight += absorbed;
        transmittance *= passed;
        t += stride;

        if (transmittance < 0.01) {

            break;

        }

    }

    return float2(transmittance, weight > 1e-4 ? weighted / weight : 0.5 * (start + end));

}

#endif
