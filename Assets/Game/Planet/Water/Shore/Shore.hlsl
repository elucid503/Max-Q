// Waves reaching a coast. The shore cascades give each point near the camera its distance to shore, the direction to
// land and the shallowest water upwave; from those, waves shoal as they slow over the shallowing bed (Green's law), break
// once their height passes 0.78 of the depth, and run up the beach, phased by their travel time over a bed sloping
// evenly to the shore so their crests close in and bunch up as they would. Needs WaterSurface.hlsl included first.
#ifndef MAXQ_SHORE_INCLUDED
#define MAXQ_SHORE_INCLUDED

#define SHORE_SLICES 4
#define WATER_GRAVITY 9.81
#define BREAKING_INDEX 0.78

// Waves reach a coast in groups of about seven; must match WaterView.GroupWaves.
#define GROUP_WAVES 7.0

// Each shore square: its centre in the scene (km), its axes, and half its width (m; zero before it is first drawn).
float4 _ShoreCentre[SHORE_SLICES];
float4 _ShoreEast[SHORE_SLICES];
float4 _ShoreNorth[SHORE_SLICES];
float4 _ShoreHalf;

// Signed distance to shore (m, negative over water), direction to land in the square's axes (its length how far the
// field agrees on it), and the shallowest water upwave (m).
TEXTURE2D_ARRAY(_ShoreField);

// The peak period of the waves reaching the coast (s).
float _WaterShorePeriod;

struct Shore {

    float distance;
    float3 toward;
    float agreement;
    float upwave;
    float weight;

};

// The shore field at a point from the finest square holding it, handing over to the next across the outer eighth of
// each; weight is how much of it any square covers.
Shore ShoreAt(float3 positionWS) {

    Shore shore = (Shore)0;
    float remaining = 1.0;

    [unroll]
    for (int i = 0; i < SHORE_SLICES; i++) {

        if (_ShoreHalf[i] <= 0.0) {

            continue;

        }

        float3 offset = (positionWS - _ShoreCentre[i].xyz) * 1000.0;
        float2 local = float2(dot(offset, _ShoreEast[i].xyz), dot(offset, _ShoreNorth[i].xyz)) / _ShoreHalf[i];
        float edge = saturate((1.0 - max(abs(local.x), abs(local.y))) * 8.0);

        if (edge <= 0.0) {

            continue;

        }

        float4 field = SAMPLE_TEXTURE2D_ARRAY_LOD(_ShoreField, sampler_linear_clamp, local * 0.5 + 0.5, i, 0.0);
        float weight = edge * remaining;

        shore.distance += field.x * weight;
        shore.toward += (_ShoreEast[i].xyz * field.y + _ShoreNorth[i].xyz * field.z) * weight;
        shore.upwave += field.w * weight;
        shore.weight += weight;
        remaining *= 1.0 - edge;

        if (remaining <= 0.0) {

            break;

        }

    }

    if (shore.weight > 0.0) {

        shore.distance /= shore.weight;
        shore.toward /= shore.weight;
        shore.upwave /= shore.weight;

    }

    shore.agreement = length(shore.toward);
    shore.toward = shore.agreement > 1e-4 ? shore.toward / shore.agreement : 0.0;

    return shore;

}

struct Breaker {

    // Rise (m) and push toward land (m) of the surface, its slope toward land, and the foam of breaking waves.
    float height;
    float push;
    float slope;
    float foam;

    // How strongly shore waves stand here, which the open sea's longest waves give way to.
    float presence;

};

// The waves breaking on the shore at a point in depth metres of water (on Terra), from a sea of significant height
// height0 metres whose waves peak at _WaterShorePeriod.
Breaker BreakerAt(float3 positionWS, Shore shore, float depth, float height0, float3 up) {

    Breaker breaker = (Breaker)0;

    if (shore.weight <= 0.0 || height0 <= 0.0 || _WaterShorePeriod <= 0.0) {

        return breaker;

    }

    float period = _WaterShorePeriod;
    float omega = 2.0 * PI / period;
    float h = max(depth, 0.05);
    float celerity = sqrt(WATER_GRAVITY * h);
    float deepGroup = WATER_GRAVITY * period / (4.0 * PI);

    // Energy flux kept as the waves slow: height grows as the root of the drop in group speed, until the bed trips them.
    float shoaled = height0 * sqrt(deepGroup / min(celerity, deepGroup));
    float breaking = shoaled / (BREAKING_INDEX * h);
    float height = min(shoaled, BREAKING_INDEX * h);

    // Waves read as a surf of their own only once the bed has shoaled them close to breaking; farther out they are
    // the open sea's, felt through its depth.
    breaker.presence = shore.weight * saturate(shore.agreement * 1.5) * smoothstep(0.35, 0.75, breaking);

    // Along the coast, waves come in groups and their phase wanders, so no stretch of shore breaks in step with the next.
    float3 along = cross(up, shore.toward);
    float2 alongFrame = float2(dot(along, _WaterEast), dot(along, _WaterNorth));
    float alongMetres = dot(FrameCoords(positionWS), alongFrame);
    float wander = 1.1 * sin(alongMetres * 0.0131 + 1.7) + 0.7 * sin(alongMetres * 0.0337 + 0.4);
    float offshore = max(-shore.distance, 0.0);
    float phase = omega * (_WaterTime - 2.0 * offshore / celerity) + wander;
    float group = 0.7 + 0.3 * sin(omega * _WaterTime / GROUP_WAVES + alongMetres * 0.004 + 0.5 * wander);
    float amplitude = 0.5 * height * breaker.presence * group;
    float k = omega / celerity;

    // A trochoid, its crest drawn up and forward as the wave nears breaking, never folding over.
    float steepness = min(0.8 * saturate(breaking), 0.9 / max(k * amplitude, 1e-4));

    breaker.height = amplitude * cos(phase);
    breaker.push = steepness * amplitude * sin(phase);
    breaker.slope = -amplitude * k * sin(phase) / max(1.0 - steepness * k * amplitude * cos(phase), 0.2);

    // Broken waves carry a bore of white water on their fronts, which thins behind each as it runs in, and the surf
    // between them stays streaked with what earlier waves left.
    float broken = smoothstep(0.8, 1.1, breaking) * breaker.presence;
    float since = frac((phase - 0.2) / (2.0 * PI));

    breaker.foam = broken * max(exp(-4.0 * since), 0.3);

    return breaker;

}

// White water in the surf beyond the shore cascades, where no single wave is drawn: the share of the surf zone broken
// waves keep white, from how far past breaking the shoaled sea would be.
float SurfFoam(float depth, float height0) {

    if (height0 <= 0.0 || _WaterShorePeriod <= 0.0) {

        return 0.0;

    }

    float deepGroup = WATER_GRAVITY * _WaterShorePeriod / (4.0 * PI);
    float shoaled = height0 * sqrt(deepGroup / min(sqrt(WATER_GRAVITY * max(depth, 0.05)), deepGroup));

    return 0.35 * smoothstep(0.85, 1.3, shoaled / (BREAKING_INDEX * max(depth, 0.05)));

}

#endif
