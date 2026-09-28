// Terra's clouds: the columns ERA5 saw at one hour (cover, convection, base and top, and the anvils over deep convection),
// carved from tiling noise after Schneider (Horizon Zero Dawn, 2015) and lit after Hillaire, "Physically Based Sky,
// Atmosphere and Cloud Rendering in Frostbite" (2016). Scene units are kilometres, so extinction is per kilometre.
#ifndef MAXQ_CLOUDS_INCLUDED
#define MAXQ_CLOUDS_INCLUDED

#include "../Atmosphere.hlsl"

// Low cloud per cell: cover, convection, base (over CLOUD_BASE_RANGE) and top; anvils: cover, base and top (over
// CLOUD_TOP_RANGE). Equirectangular, row 0 at the north pole and column 0 at 180 W, as the bake wrote them.
TEXTURE2D(_CloudWeather);
TEXTURE2D(_CloudAnvils);
TEXTURE3D(_CloudShape);
TEXTURE3D(_CloudDetail);

// The shape noise's bounds: each mip holds the highest value within its cell and the cells round it, so a point sample
// of the mip whose cells are at least r wide bounds the noise anywhere within r.
TEXTURE3D(_CloudShapeBound);

// Inline states, so filtering never rests on how the textures were made: the weather wraps round in longitude only.
SAMPLER(sampler_trilinear_repeatU_clampV);
SAMPLER(sampler_trilinear_repeat);
SAMPLER(sampler_point_repeat);

// Scene directions to the body-fixed frame, and to the noise's frame, which drifts east with the wind.
float4x4 _CloudBodyFromScene;
float4x4 _CloudNoiseFromScene;
float3 _CloudShapeOffset;
float3 _CloudDetailOffset;

// Byte ranges of the weather's heights (km); match CLOUD_BASE_RANGE and CLOUD_TOP_RANGE in terra_bake.py.
#define CLOUD_BASE_RANGE 6.0
#define CLOUD_TOP_RANGE 16.0
#define CLOUD_TOP 16.0

// Tile sizes (km) of the noise: cumulus heaps, storm towers and anvils, the cover's wander between weather cells, and
// the detail that erodes edges; match CloudView.
#define CLOUD_SHAPE_SIZE 5.0
#define CLOUD_SHAPE_OTHER_SIZE 7.3
#define CLOUD_STORM_SIZE 30.0
#define CLOUD_COVER_SIZE 90.0
#define CLOUD_DETAIL_SIZE 0.7

// The weather is read this many of its cells off course, by the cover noise, so its grid never shows.
#define CLOUD_WEATHER_WARP 2.0
#define CLOUD_WEATHER_CELLS float2(1440.0, 720.0)

// Extinction per kilometre of dense water cloud and of an anvil's ice.
#define CLOUD_EXTINCTION 40.0
#define ANVIL_EXTINCTION 6.0

// Steps along a view ray through clear air: at least CLOUD_MIN_STEP, growing with distance by at least CLOUD_STEP_GROWTH
// and by as much as the steps left need to reach the far end, at most a 48th of the stretch through the layer, and
// skipping up to CLOUD_MAX_SKIP toward a layer. Where cloud may lie, steps halve up to CLOUD_REFINES times toward the
// sampling step: about one optical depth, at least half a pixel's footprint and at most CLOUD_FINE_STEP in dense cloud
// or CLOUD_THIN_STEP in thin, so no step is wholly opaque.
#define CLOUD_MIN_STEP 0.04
#define CLOUD_STEP_GROWTH 0.01
#define CLOUD_MAX_SKIP 3.0
#define CLOUD_FINE_STEP 0.1
#define CLOUD_THIN_STEP 0.3
#define CLOUD_REFINES 3
#define CLOUD_LIGHT_STEP 0.08
#define CLOUD_GROUND_ALBEDO 0.25
#define CLOUD_FAR 10000.0

struct CloudPoint {

    float extinction;
    // 0 at the layer's base to 1 at its top.
    float height;
    // Kilometres up or down to the nearest layer that could hold cloud; zero inside one.
    float gap;

};

float CloudRemap(float value, float from, float to, float newFrom, float newTo) {

    return newFrom + (value - from) / (to - from) * (newTo - newFrom);

}

float2 CloudWeatherUv(float3 p) {

    float3 body = mul((float3x3)_CloudBodyFromScene, p);

    return float2(atan2(body.z, body.x) * (0.5 / PI) + 0.5, acos(clamp(body.y / length(body), -1.0, 1.0)) / PI);

}

float LayerGap(float altitude, float bottom, float top) {

    return max(max(bottom - altitude, altitude - top), 0.0);

}

float ShapeNoise(float3 uvw, float lod) {

    return SAMPLE_TEXTURE3D_LOD(_CloudShape, sampler_trilinear_repeat, uvw, lod).r;

}

float DetailNoise(float3 uvw, float lod) {

    return SAMPLE_TEXTURE3D_LOD(_CloudDetail, sampler_trilinear_repeat, uvw, lod).r;

}

// The highest the shape noise of a tile (km) reaches within radius (km) of uvw.
float ShapeBound(float3 uvw, float radius, float tile) {

    return SAMPLE_TEXTURE3D_LOD(_CloudShapeBound, sampler_point_repeat, uvw, clamp(ceil(log2(max(radius * 128.0 / tile, 1.0))), 0.0, 7.0)).r;

}

// The weather over a point, as its layers stand there.
struct CloudColumn {

    float altitude;
    float3 q;
    float cover;
    float convection;
    float bottom;
    float top;
    float anvil;
    float anvilBottom;
    float anvilTop;
    // Share of the second tiling in the shape noise.
    float mix;

};

CloudColumn ColumnAt(float3 p) {

    CloudColumn column;
    column.altitude = length(p) - _PlanetRadius;
    column.q = mul((float3x3)_CloudNoiseFromScene, p);

    float wander = ShapeNoise(column.q / CLOUD_COVER_SIZE, 0.0);
    float drift = ShapeNoise(column.q / CLOUD_COVER_SIZE + 0.5, 0.0);
    float2 uv = CloudWeatherUv(p) + (float2(wander, drift) - 0.5) * (2.0 * CLOUD_WEATHER_WARP / CLOUD_WEATHER_CELLS);
    float4 low = SAMPLE_TEXTURE2D_LOD(_CloudWeather, sampler_trilinear_repeatU_clampV, uv, 0.0);

    // Anvils read a coarser mip, so they spread past the cells that fed them.
    float4 anvils = SAMPLE_TEXTURE2D_LOD(_CloudAnvils, sampler_trilinear_repeatU_clampV, uv, 1.0);

    // The weather's cells are kilometres across: cover wanders within them, but full and empty cells stay so.
    column.cover = saturate(low.r + (wander - 0.5) * saturate(4.0 * low.r * (1.0 - low.r)));
    column.convection = low.g;
    column.bottom = low.b * CLOUD_BASE_RANGE;
    column.top = max(low.a * CLOUD_TOP_RANGE, column.bottom + 0.1);
    column.anvil = anvils.r;
    column.anvilBottom = anvils.g * CLOUD_TOP_RANGE;
    column.anvilTop = max(anvils.b * CLOUD_TOP_RANGE, column.anvilBottom + 0.3);
    column.mix = smoothstep(0.3, 0.7, drift);

    return column;

}

// The same weather at another point nearby.
CloudColumn Moved(CloudColumn column, float3 p) {

    column.altitude = length(p) - _PlanetRadius;
    column.q = mul((float3x3)_CloudNoiseFromScene, p);

    return column;

}

// Deep columns are storm towers.
float Deep(CloudColumn column) {

    return saturate((column.top - column.bottom - 3.0) / 5.0);

}

// Cloud at the column's point, with the noise blurred to a footprint (km), and the edges eroded by detail (0 to 1),
// which fades out as the footprint outgrows the detail.
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

    float stormLod = log2(max(footprint * (128.0 / CLOUD_STORM_SIZE), 1.0));

    if (towerGap <= 0.0) {

        float h = (column.altitude - column.bottom) / (column.top - column.bottom);

        // Shallow columns without convection spread into decks of stratocumulus. Profiles peak at one height rather
        // than holding a plateau, so heaps round off instead of standing as slabs: cumulus on a flat base, decks
        // swelling in the middle.
        float deep = Deep(column);
        float stratiform = (1.0 - column.convection) * (1.0 - deep);
        float cumulus = saturate(h / 0.06) * sqrt(saturate(1.0 - h));
        float deck = sqrt(saturate(4.0 * h * (1.0 - h)));

        // Two tilings at unrelated scales, mixed by a slow mask and rescaled to keep their spread, so no grid repeats.
        float mix = column.mix;
        float first = ShapeNoise((q + _CloudShapeOffset) / CLOUD_SHAPE_SIZE, log2(max(footprint * (128.0 / CLOUD_SHAPE_SIZE), 1.0)));
        float second = ShapeNoise(q / CLOUD_SHAPE_OTHER_SIZE + 0.21, log2(max(footprint * (128.0 / CLOUD_SHAPE_OTHER_SIZE), 1.0)));
        float shape = saturate(0.5 + (lerp(first, second, mix) - 0.5) * rsqrt(mix * mix + (1.0 - mix) * (1.0 - mix)));

        // Towers take the storm's broad noise, keeping some heaps, so they stay lumpy up close.
        if (deep > 0.0) {

            shape = lerp(shape, ShapeNoise(q / CLOUD_STORM_SIZE, stormLod), 0.6 * deep);

        }

        float density = saturate(CloudRemap(shape * lerp(cumulus, deck, stratiform), 1.0 - column.cover, 1.0, 0.0, 1.0));

        // Wisps along the base, billows above, fading out as the footprint outgrows them.
        float erosion = 0.4 * detail * saturate((0.5 * CLOUD_DETAIL_SIZE - footprint) / (0.4 * CLOUD_DETAIL_SIZE));

        if (density > 0.0 && erosion > 0.0) {

            float fine = DetailNoise((q + _CloudDetailOffset) / CLOUD_DETAIL_SIZE, log2(max(footprint * (32.0 / CLOUD_DETAIL_SIZE), 1.0)));

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

// Cloud at p (relative to the centre).
CloudPoint CloudAt(float3 p, float footprint, float detail) {

    return CloudIn(ColumnAt(p), footprint, detail);

}

// Whether cloud may lie within radius (km) of the column's point, and the kilometres up or down from it to the nearest
// layer: CloudAt's test with the layers widened and the noise replaced by its bounds, so it is never false where CloudAt
// finds cloud. The weather may have been read up to spread (km) away: layers are widened by that too, and cover is
// allowed to rise by a margin over it.
bool CloudPossible(CloudColumn column, float radius, float spread, out float gap) {

    float reach = radius + spread + 0.1;
    float margin = 0.05 + 0.05 * (radius + spread);
    float towerGap = column.cover > 0.004 ? LayerGap(column.altitude, column.bottom, column.top) : CLOUD_TOP;
    float anvilGap = column.anvil > 0.004 ? LayerGap(column.altitude, column.anvilBottom, column.anvilTop) : CLOUD_TOP;

    gap = min(towerGap, anvilGap);

    if (gap > reach) {

        return false;

    }

    float3 q = column.q;

    if (towerGap <= reach) {

        float mix = column.mix;
        float highest = max(ShapeBound((q + _CloudShapeOffset) / CLOUD_SHAPE_SIZE, radius, CLOUD_SHAPE_SIZE), ShapeBound(q / CLOUD_SHAPE_OTHER_SIZE + 0.21, radius, CLOUD_SHAPE_OTHER_SIZE));
        float shape = saturate(0.5 + (highest - 0.5) * rsqrt(mix * mix + (1.0 - mix) * (1.0 - mix)));

        if (Deep(column) > 0.0) {

            shape = max(shape, ShapeBound(q / CLOUD_STORM_SIZE, radius, CLOUD_STORM_SIZE));

        }

        if (shape > 1.0 - saturate(column.cover + margin)) {

            return true;

        }

    }

    return anvilGap <= reach && ShapeBound(q / CLOUD_STORM_SIZE + 0.37, radius, CLOUD_STORM_SIZE) > 1.0 - saturate(column.anvil + margin);

}

// Optical depth toward the sun from p, over steps that triple from CLOUD_LIGHT_STEP, each sampled within its middle half
// by jitter (0 to 1), so the self-shadow never switches at the same distances and prints contours on the cloud. The weather
// changes over kilometres, so the column read for p serves the whole look.
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

// Water droplets throw most light forward, with a weaker lobe back toward the sun.
float CloudPhase(float cosTheta, float scale) {

    return 0.7 * HenyeyGreenstein(0.8 * scale, cosTheta) + 0.3 * HenyeyGreenstein(-0.3 * scale, cosTheta);

}

// Radiance a cloud scatters toward the eye per unit of its extinction: sunlight through the cloud toward the sun, with
// Wrenninge's octaves standing in for light scattered many times over (each weaker, reaching deeper and spread wider;
// water cloud's albedo is near one, so the light that wanders keeps most of its energy), and the sky above and the sunlit
// ground below, which the cloud's upper parts see more of.
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

            octaves += energy * CloudPhase(cosTheta, spread) * exp(-depth * reach);
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

// A uniform number from a texel and a frame (PCG, Jarzynski and Olano 2020): white noise, so what the history leaves of
// it is noise too, not a pattern.
float CloudHash(uint2 texel, uint frame) {

    uint3 v = uint3(texel, frame) * 1664525u + 1013904223u;

    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;

    return (v.x & 0xFFFFFFu) / 16777216.0;

}

// The clouds along a ray from origin (relative to the centre) out to tMax. Each step starts as long as the budget needs
// to reach the far end, and halves while the noise's bounds say cloud may lie along it; a step proven clear is crossed
// whole, so no cloud is ever stepped over, and one that is not is sampled jitter (0 to 1) of its way in, which rays and
// frames vary. The footprint of a sample is its distance times pixelAngle; with detail, edges are eroded while the
// footprint is finer than the detail.
CloudTrace TraceClouds(float3 origin, float3 direction, float tMax, float jitter, float pixelAngle, int steps, int lightSteps, bool detail) {

    CloudTrace result;
    result.radiance = 0.0;
    result.transmittance = 1.0;
    result.depth = min(tMax, CLOUD_FAR);

    float2 shell = RaySphere(origin, direction, _PlanetRadius + CLOUD_TOP);

    if (shell.y <= 0.0) {

        return result;

    }

    float start = max(shell.x, 0.0);
    float end = min(shell.y, tMax);
    float ground = RaySphere(origin, direction, _PlanetRadius).x;

    if (ground > 0.0) {

        end = min(end, ground);

    }

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
        float gap = 0.0;
        bool clear = false;

        // The weather is read once at the full step's middle; the halved steps within it test against it.
        float first = stride;
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

            // From the step's middle, the layer lies at least gap up or down, so the ray may run on until it climbs that far.
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

// Nearest and farthest points of a line through origin along direction on a sphere, either side of origin.
bool LineSphere(float3 origin, float3 direction, float radius, out float2 hits) {

    float b = dot(origin, direction);
    float discriminant = b * b - dot(origin, origin) + radius * radius;

    hits = 0.0;

    if (discriminant < 0.0) {

        return false;

    }

    hits = float2(-b - sqrt(discriminant), -b + sqrt(discriminant));

    return true;

}

// The light the clouds let through along the sun line through origin (relative to the centre), and how far toward the
// sun from origin the shade starts, as the mean of where the light was lost; lines through clear air give the layer's
// middle, so filtering between texels never drags a neighbour's shade far off. The march climbs from where the line
// leaves the ground, skipping to the layers and stepping finely through them, each sample jitter (0 to 1) of its step in,
// so neighbouring lines never sample the same slices.
float2 CloudShade(float3 origin, float footprint, float jitter, int steps) {

    float2 shell;
    float2 ground;

    if (!LineSphere(origin, _SunDirection, _PlanetRadius + CLOUD_TOP, shell)) {

        return float2(1.0, 0.0);

    }

    float start = LineSphere(origin, _SunDirection, _PlanetRadius, ground) ? max(shell.x, ground.y) : shell.x;
    float end = shell.y;
    float t = start;
    float transmittance = 1.0;
    float weighted = 0.0;
    float weight = 0.0;

    for (int i = 0; i < steps && t < end; i++) {

        float stride = clamp(max((t - start) * CLOUD_STEP_GROWTH, 0.25 * (end - t) / (steps - i)), 0.05, 0.4);
        float at = t + stride * jitter;
        float3 p = origin + _SunDirection * at;
        CloudPoint cloud = CloudAt(p, footprint, 0.0);

        if (cloud.extinction <= 0.0) {

            float climb = max(dot(p, _SunDirection) / length(p), 0.05);

            t += max(stride, min(cloud.gap / climb, 2.0 * CLOUD_MAX_SKIP));

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
