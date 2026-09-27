// The sea's surface, shared by the water sheet and by the ground where it stands in for water too narrow or far for the
// sheet. The waves' four cascades lie on the wave frame, each weighted by the sea where it falls; their slopes are
// filtered as moments (Dupuy's LEADR, as Atlas uses it), so as the surface recedes the waves it cannot resolve become
// the spread of a statistical surface, and it hands over from wave faces to glint by mip level alone (Bruneton, Neyret
// and Holzschuch 2010). Needs Ground.hlsl (the patch) and Sunlight.hlsl (the air) included first.
#ifndef MAXQ_WATER_SURFACE_INCLUDED
#define MAXQ_WATER_SURFACE_INCLUDED

#include "WaterOptics.hlsl"

#define CASCADES 4
#define WAVE_TEXELS 256.0
#define WAVE_MIPS 8.0
#define WATER_F0 0.0204

// Vertex spacing is at most a 88th of the distance from the camera (the ground's split range over its quads). A wave
// shorter than two vertices would alias into rings around the camera, so displacement is read from the mip whose
// texels are two vertices wide, where the box filter has taken such waves out.
#define VERTEX_SPACING (1.0 / 88.0)
#define DISPLACEMENT_FILTER 2.0

// A cascade whose crests would rise less than a quarter of a pixel is not displaced at all, fading in by half a pixel:
// from there on the slopes alone carry it.
#define SUBPIXEL_FROM 0.25
#define SUBPIXEL_BY 0.5

// The cascades' strengths wander by this share over kilometres, each on its own scale, so their repeats never line up.
#define WAVE_VARIATION 0.35

// The variation and foam's structure repeat over PATTERN_PERIOD metres of sea, every scale of them a whole division of
// it, so they stay put on the sea across the frame's wrap of the distance travelled (WaterView.PatternPeriod).
#define PATTERN_PERIOD 29700.0
#define FOAM_TILES_NEAR 9600.0
#define FOAM_TILES_FAR 2170.0

// A cascade whose tile spans more than the first of these shares of a pixel hands over to its statistics alone, and
// wholly by the second: its mipmaps would otherwise repeat the tile across the view as a lattice of dots.
#define STATISTICS_FROM (1.0 / 24.0)
#define STATISTICS_BY (1.0 / 6.0)

// A river carries its short waves and foam downstream in two phases of this many seconds, half a period apart, each
// fading out as it slips too far and starting again (Vlachos's flow maps); a whole division of the waves' loop.
#define FLOW_PERIOD 2.0

// The phases start their cycles at times spread over this many metres of river; a whole division of PATTERN_PERIOD.
#define FLOW_SPREAD 7425.0

// Rapids: past this Froude number a river's surface starts to stand in steep broken waves, wholly so by the second. There
// its three shorter cascades stand at least this high (rms, m), and white water covers this share of it.
#define RAPIDS_FROM 0.6
#define RAPIDS_BY 1.0
#define RAPIDS_HEIGHT float3(0.12, 0.05, 0.008)
#define RAPIDS_FOAM 0.6

// River foam gathers into clumps repeating over this many metres; a whole division of PATTERN_PERIOD.
#define RAPIDS_PATCHES 45.0

// Ripples too fine for any cascade still leave the surface this rough, so the sun's glint stays a finite disc.
#define BASE_SLOPE_VARIANCE 2e-5

// The wave frame and cascades (WaterView): origin in the scene (km) and axes; each cascade's tile coordinates of the
// origin, size (m), turn from the frame, the camera sea's variance in its band and that band's mean wavelength (m).
float3 _WaterOrigin;
float3 _WaterEast;
float3 _WaterNorth;
float4 _WaterOffsetNear;
float4 _WaterOffsetFar;
float4 _WaterSizes;
float4 _WaterCos;
float4 _WaterSin;
float4 _WaterBandEnergy;
float4 _WaterWavelength;

// Sim time (s), wrapped to the waves' loop.
float _WaterTime;

// Where the frame's origin lies on the sea's patterns (m along the frame's axes, within PATTERN_PERIOD).
float2 _WaterPattern;

// Whitecap coverage the foam was raised for at the camera's wind (Monahan and O'Muircheartaigh).
float _WaterCoverage;

TEXTURE2D_ARRAY(_WaterDisplacement);
TEXTURE2D_ARRAY(_WaterDerivatives);
TEXTURE2D_ARRAY(_WaterMoments);
TEXTURE2D_ARRAY(_WaterFoam);

// The month's sea state (equirectangular, 0.5 degree cells): wind speed, wind-sea height and period, ice; swell height
// and period. The share of each system's variance the four cascades hold, by peak period (1 to 25 s, log steps): the
// wind's sea in the first row, swell in the second.
TEXTURE2D(_WaterSeaState);
TEXTURE2D(_WaterSwell);
TEXTURE2D(_WaterBands);

// Foam's structure (red) and smooth noise (green, blue, alpha), tiling (FoamTexture).
TEXTURE2D(_WaterFoamTexture);

SAMPLER(sampler_WaterTrilinearRepeat);
SAMPLER(sampler_WaterLinearRepeat);
SAMPLER(sampler_linear_repeatU_clampV);

// Metres along the wave frame's axes from its origin.
float2 FrameCoords(float3 positionWS) {

    float3 offset = (positionWS - _WaterOrigin) * 1000.0;

    return float2(dot(offset, _WaterEast), dot(offset, _WaterNorth));

}

float2 TileOffset(int cascade) {

    return cascade == 0 ? _WaterOffsetNear.xy : cascade == 1 ? _WaterOffsetNear.zw : cascade == 2 ? _WaterOffsetFar.xy : _WaterOffsetFar.zw;

}

// A point's coordinates in a cascade's tile, whose axes are the frame's turned by the cascade's angle.
float2 TileUv(int cascade, float2 coords) {

    float c = _WaterCos[cascade];
    float s = _WaterSin[cascade];

    return float2(c * coords.x + s * coords.y, -s * coords.x + c * coords.y) / _WaterSizes[cascade] + TileOffset(cascade);

}

float2 ToFrame(int cascade, float2 v) {

    float c = _WaterCos[cascade];
    float s = _WaterSin[cascade];

    return float2(c * v.x - s * v.y, s * v.x + c * v.y);

}

// A covariance of slopes (xx, zz, xz) turned from a cascade's tile into the frame.
float3 CovarianceToFrame(int cascade, float3 m) {

    float c = _WaterCos[cascade];
    float s = _WaterSin[cascade];

    return float3(c * c * m.x - 2.0 * c * s * m.z + s * s * m.y, s * s * m.x + 2.0 * c * s * m.z + c * c * m.y, c * s * (m.x - m.y) + (c * c - s * s) * m.z);

}

struct SeaState {

    float wind;
    float seaHeight;
    float seaPeriod;
    float ice;
    float swellHeight;
    float swellPeriod;
    float latitude;

};

// The month's sea where a point on the patch lies; the patch's transform turns the scene into the body's frame.
SeaState SeaStateAt(float3 positionWS) {

    float3 body = normalize(mul((float3x3)UNITY_MATRIX_I_M, positionWS - _PlanetCentre));
    float latitude = asin(clamp(body.y, -1.0, 1.0));
    float longitude = atan2(body.z, body.x);
    float2 uv = float2(longitude / (2.0 * PI) + 0.5, ((0.5 * PI - latitude) / PI * 360.0 + 0.5) / 361.0);
    float4 sea = SAMPLE_TEXTURE2D_LOD(_WaterSeaState, sampler_linear_repeatU_clampV, uv, 0.0);
    float2 swell = SAMPLE_TEXTURE2D_LOD(_WaterSwell, sampler_linear_repeatU_clampV, uv, 0.0).xy;

    SeaState state;
    state.wind = sea.x;
    state.seaHeight = sea.y;
    state.seaPeriod = sea.z;
    state.ice = saturate(sea.w);
    state.swellHeight = swell.x;
    state.swellPeriod = swell.y;
    state.latitude = latitude;

    return state;

}

// Shares of a system's variance the four cascades hold, for its peak period.
float4 BandShares(float period, bool swell) {

    float u = saturate(log(max(period, 1.0)) / log(25.0)) * (63.0 / 64.0) + 0.5 / 64.0;

    return SAMPLE_TEXTURE2D_LOD(_WaterBands, sampler_linear_clamp, float2(u, swell ? 0.75 : 0.25), 0.0);

}

// How strongly each cascade's waves stand here, against the camera's sea they were drawn for: the square root of the
// ratio of the variance each band holds. The land's shelter holds the wind's sea down to what its fetch can raise (its
// period shortening as Hasselmann's growth law has it) and keeps swell out; ice damps both.
float4 CascadeWeights(SeaState sea, float seaShelter, float swellShelter) {

    float damping = (1.0 - sea.ice) * (1.0 - sea.ice);
    float seaHeight = sea.seaHeight * seaShelter * damping;
    float seaPeriod = sea.seaPeriod * pow(max(seaShelter, 1e-3), 2.0 / 3.0);
    float swellHeight = sea.swellHeight * swellShelter * damping;
    float4 energy = (seaHeight * seaHeight * BandShares(seaPeriod, false) + swellHeight * swellHeight * BandShares(sea.swellPeriod, true)) / 16.0;

    return _WaterBandEnergy > 1e-12 ? min(sqrt(energy / max(_WaterBandEnergy, 1e-12)), 2.0) : 0.0;

}

// How much stronger or weaker each cascade's waves run here than on average: smooth noise at kilometre scales, a
// different lattice for each, so the sea's texture changes across a view as a real sea's does with gusts and currents.
// The finest cascade takes the longest's noise turned over.
float4 WaveVariation(float2 coords) {

    float3 noise = SAMPLE_TEXTURE2D_LOD(_WaterFoamTexture, sampler_WaterLinearRepeat, (coords + _WaterPattern) / PATTERN_PERIOD, 0.0).gba;

    return 1.0 + WAVE_VARIATION * (2.0 * float4(noise, 1.0 - noise.x) - 1.0);

}

// Foam's structure at a point: packed bubbles at two scales, a few metres and a dozen across.
float FoamStructure(float2 coords) {

    float2 sea = (coords + _WaterPattern) / PATTERN_PERIOD;
    float near = SAMPLE_TEXTURE2D(_WaterFoamTexture, sampler_WaterTrilinearRepeat, sea * FOAM_TILES_NEAR).r;
    float far = SAMPLE_TEXTURE2D(_WaterFoamTexture, sampler_WaterTrilinearRepeat, sea * FOAM_TILES_FAR + 0.5).r;

    return 0.55 * near + 0.45 * far;

}

// Foam of a coverage (0 to 1) as it shows over its structure: thin foam only where bubbles crowd, thick foam solid
// (Crest's feathering, a threshold eating into the structure).
float FeatherFoam(float coverage, float structure) {

    return smoothstep(1.0 - coverage, 1.0 - coverage + 0.35, structure) * saturate(coverage * 4.0);

}

// Waves feel the bottom once it is shallower than half their length. In the surf they feel Terra's own bed, whose
// slopes are the Earth's, so they break where they would there; offshore they feel the Earth's depth the water stands
// for, whose seas the climatology already knows. Depth and shore distance in metres on Terra.
float4 DepthFade(float depth, float shore) {

    float felt = lerp(depth, depth / TERRA_SCALE, smoothstep(1000.0, 3000.0, -shore));

    return saturate(2.0 * max(felt, 0.0) / _WaterWavelength);

}

// Displacement of the surface at a point (m, along the frame's east, the vertical and the frame's north), from waves
// long enough for the vertices distance metres from the camera to carry and high enough to see from there.
float3 WaveDisplacement(float2 coords, float distance, float4 strength) {

    float3 displacement = 0.0;
    float spacing = distance * VERTEX_SPACING;
    float pixels = _ScreenParams.y * 0.5 * abs(UNITY_MATRIX_P[1][1]) / max(distance, 1e-3);

    [unroll]
    for (int i = 0; i < CASCADES; i++) {

        float mip = log2(max(DISPLACEMENT_FILTER * spacing * WAVE_TEXELS / _WaterSizes[i], 1.0));
        float crest = 3.0 * sqrt(_WaterBandEnergy[i]) * strength[i] * pixels;
        float shown = saturate((crest - SUBPIXEL_FROM) / (SUBPIXEL_BY - SUBPIXEL_FROM));

        // Past the last mip a cascade's waves average away across the vertex's reach.
        UNITY_BRANCH
        if (mip < WAVE_MIPS && shown > 0.0) {

            float3 d = SAMPLE_TEXTURE2D_ARRAY_LOD(_WaterDisplacement, sampler_WaterTrilinearRepeat, TileUv(i, coords), i, mip).xyz * strength[i] * shown;

            displacement += float3(ToFrame(i, d.xz), d.y).xzy;

        }

    }

    return displacement;

}

// The same in the scene: the frame's axes laid on the ground at the point, the vertical its own.
float3 WaveDisplacementWS(float3 positionWS, float4 strength) {

    float3 up = normalize(positionWS - _PlanetCentre);
    float3 east = normalize(_WaterEast - up * dot(_WaterEast, up));
    float3 north = cross(up, east);
    float3 d = WaveDisplacement(FrameCoords(positionWS), length(positionWS - _WorldSpaceCameraPos) * 1000.0, strength);

    return (east * d.x + up * d.y + north * d.z) / 1000.0;

}

struct WaveSurface {

    float2 slope;
    float3 covariance;
    float foam;
    float fresh;

};

// A cascade's mean slope and slope moments over a pixel's footprint (dx, dy in tile units), weighted, handing over to
// its whole band's statistics as the tile shrinks toward the pixel.
void CascadeSlopes(int i, float2 uv, float2 dx, float2 dy, float statistics, float weight, inout float2 mean, inout float3 moments) {

    UNITY_BRANCH
    if (statistics < 1.0) {

        mean += SAMPLE_TEXTURE2D_ARRAY_GRAD(_WaterDerivatives, sampler_WaterTrilinearRepeat, uv, i, dx, dy).xy * (1.0 - statistics) * weight;
        moments += SAMPLE_TEXTURE2D_ARRAY_GRAD(_WaterMoments, sampler_WaterTrilinearRepeat, uv, i, dx, dy).xyz * (1.0 - statistics) * weight;

    }

    UNITY_BRANCH
    if (statistics > 0.0) {

        moments += SAMPLE_TEXTURE2D_ARRAY_LOD(_WaterMoments, sampler_WaterTrilinearRepeat, uv, i, WAVE_MIPS).xyz * statistics * weight;

    }

}

struct FlowPhases {

    float2 shift0;
    float2 shift1;
    float weight0;

};

// The two phases a river's current (m/s along the frame's axes) carries its short waves and foam in: how far each has
// slipped downstream (m) and how much the first shows. Each place starts its cycle at its own time, so the river never
// pulses as one; the start drifts slowly enough over the river that it barely warps what the current carries.
FlowPhases FlowPhasesAt(float2 coords, float2 flow) {

    float offset = SAMPLE_TEXTURE2D_LOD(_WaterFoamTexture, sampler_WaterLinearRepeat, (coords + _WaterPattern) / FLOW_SPREAD, 0.0).g;
    float phase0 = frac(_WaterTime / FLOW_PERIOD + offset);
    float phase1 = frac(phase0 + 0.5);

    FlowPhases phases;
    phases.shift0 = flow * (phase0 - 0.5) * FLOW_PERIOD;
    phases.shift1 = flow * (phase1 - 0.5) * FLOW_PERIOD;
    phases.weight0 = 1.0 - abs(2.0 * phase0 - 1.0);

    return phases;

}

// The surface's mean slope (along the frame's axes) and the spread of slopes about it over the pixel, as the mipmaps
// of each cascade filter them, and the foam on it. The pixel's footprint is the frame coordinates' screen derivatives,
// taken before any branch, where they are still defined. Trilinear filtering blurs the slopes more than the pixel needs
// along its narrow axis, and the moments carry what it blurs away as spread, so the glint keeps its energy. A cascade
// whose tile shrinks toward the pixel hands over to its last mip, one texel holding its whole band's statistics,
// rather than repeat the tile. A river's current (m/s along the frame's axes) carries the two short cascades with it;
// blending its two phases blurs their slopes, and the moments keep the blur as spread. Foam is looked up only where
// the wind is strong enough to raise it.
WaveSurface SampleWaves(float2 coords, float2 coordsDx, float2 coordsDy, float4 weights, float2 flow, bool foamy) {

    WaveSurface surface;
    surface.slope = 0.0;
    surface.covariance = float3(BASE_SLOPE_VARIANCE, BASE_SLOPE_VARIANCE, 0.0);
    surface.foam = 0.0;
    surface.fresh = 0.0;

    bool flowing = dot(flow, flow) > 1e-4;
    FlowPhases phases = (FlowPhases)0;

    UNITY_BRANCH
    if (flowing) {

        phases = FlowPhasesAt(coords, flow);

    }

    [unroll]
    for (int i = 0; i < CASCADES; i++) {

        float2 uv = TileUv(i, coords);
        float2 dx = TileUv(i, coords + coordsDx) - uv;
        float2 dy = TileUv(i, coords + coordsDy) - uv;
        float statistics = smoothstep(STATISTICS_FROM, STATISTICS_BY, max(length(dx), length(dy)));
        float2 mean = 0.0;
        float3 moments = 0.0;

        UNITY_BRANCH
        if (i >= 2 && flowing) {

            CascadeSlopes(i, TileUv(i, coords - phases.shift0), dx, dy, statistics, phases.weight0, mean, moments);
            CascadeSlopes(i, TileUv(i, coords - phases.shift1), dx, dy, statistics, 1.0 - phases.weight0, mean, moments);

        } else {

            CascadeSlopes(i, uv, dx, dy, statistics, 1.0, mean, moments);

        }

        float3 spread = float3(max(moments.x - mean.x * mean.x, 0.0), max(moments.y - mean.y * mean.y, 0.0), moments.z - mean.x * mean.y);

        surface.slope += ToFrame(i, mean) * weights[i];
        surface.covariance += CovarianceToFrame(i, spread) * weights[i] * weights[i];

        UNITY_BRANCH
        if (i < 2 && foamy) {

            float2 foam = SAMPLE_TEXTURE2D_ARRAY_GRAD(_WaterFoam, sampler_WaterTrilinearRepeat, uv, i, dx, dy).xy;

            surface.foam = max(surface.foam, foam.x * weights[i]);
            surface.fresh = max(surface.fresh, foam.y * weights[i]);

        }

    }

    return surface;

}

// How much of a river is in rapids: where its current (m/s) outruns the waves its depth (real metres) lets travel.
float Rapids(float speed, float depth) {

    return smoothstep(RAPIDS_FROM, RAPIDS_BY, speed / sqrt(9.81 * max(depth, 0.1)));

}

// Cascade weights where rapids stand: the three shorter bands at least the rapids' height.
float4 RapidsWeights(float4 weights, float rapids) {

    float3 standing = min(RAPIDS_HEIGHT / sqrt(max(_WaterBandEnergy.yzw, 1e-10)), 16.0) * rapids;

    return float4(weights.x, max(weights.yzw, standing));

}

// White water in a river: patches of it a few metres across and clumps of them some tens, as the current breaks it up.
float RiverFoamStructure(float2 coords) {

    float2 sea = (coords + _WaterPattern) / RAPIDS_PATCHES;
    float clumps = SAMPLE_TEXTURE2D(_WaterFoamTexture, sampler_WaterTrilinearRepeat, sea).g;
    float patches = SAMPLE_TEXTURE2D(_WaterFoamTexture, sampler_WaterTrilinearRepeat, sea * 4.0 + 0.5).g;

    return 0.6 * clumps + 0.4 * patches;

}

// Foam's structure at a point, as the two phases a river's current (m/s along the frame's axes) carries it in and how
// much the first shows; still water holds one phase.
float3 FoamStructures(float2 coords, float2 flow) {

    UNITY_BRANCH
    if (dot(flow, flow) <= 1e-4) {

        return float3(FoamStructure(coords).xx, 1.0);

    }

    FlowPhases phases = FlowPhasesAt(coords, flow);

    return float3(RiverFoamStructure(coords - phases.shift0), RiverFoamStructure(coords - phases.shift1), phases.weight0);

}

// A current along the ground's east and north (m/s) as a velocity along the wave frame's axes; the patch's transform
// turns the body's frame, its axis up, into the scene.
float2 FlowToFrame(float3 positionWS, float2 flow) {

    float3 up = normalize(mul((float3x3)UNITY_MATRIX_I_M, positionWS - _PlanetCentre));
    float3 east = normalize(cross(up, float3(0.0, 1.0, 0.0)));
    float3 north = cross(east, up);
    float3 velocity = TransformObjectToWorldDir(east * flow.x + north * flow.y, false);

    return float2(dot(velocity, _WaterEast), dot(velocity, _WaterNorth));

}

// Variance of slope along a direction's heading across the frame.
float DirectionalVariance(float3 direction, float3 east, float3 north, float3 covariance) {

    float2 heading = float2(dot(direction, east), dot(direction, north));
    float length2 = dot(heading, heading);

    if (length2 < 1e-8) {

        return 0.5 * (covariance.x + covariance.y);

    }

    heading /= sqrt(length2);

    return covariance.x * heading.x * heading.x + 2.0 * covariance.z * heading.x * heading.y + covariance.y * heading.y * heading.y;

}

// Smith's masking of a Gaussian slope distribution, in Walter et al.'s rational fit.
float SmithLambda(float cosine, float variance) {

    float c = clamp(cosine, 1e-4, 0.9999);
    float a = c / sqrt(2.0 * variance * (1.0 - c * c));

    return a < 1.6 ? (1.0 - 1.259 * a + 0.396 * a * a) / (3.535 * a + 2.181 * a * a) : 0.0;

}

// Fresnel averaged over the facets a view sees (Bruneton et al. 2010).
float EffectiveFresnel(float cosView, float variance) {

    float sigma = sqrt(variance);

    return WATER_F0 + (1.0 - WATER_F0) * pow(1.0 - saturate(cosView), 5.0) * exp(-2.69 * sigma) / (1.0 + 22.7 * pow(sigma, 1.5));

}

// Sunlight the waves' facets mirror toward the eye, per unit of the sun's irradiance: the share of facets turned to
// the half vector, their Fresnel, masked and shadowed by the waves in front.
float SunGlint(float3 view, float3 sun, float3 up, float3 east, float3 north, WaveSurface surface) {

    float lz = dot(sun, up);
    float vz = dot(view, up);

    if (lz <= 0.0 || vz <= 0.0) {

        return 0.0;

    }

    float3 h = normalize(view + sun);
    float hz = max(dot(h, up), 1e-4);
    float2 zeta = float2(-dot(h, east), -dot(h, north)) / hz - surface.slope;
    float3 c = surface.covariance;
    float determinant = max(c.x * c.y - c.z * c.z, 1e-12);
    float q = (zeta.x * zeta.x * c.y - 2.0 * zeta.x * zeta.y * c.z + zeta.y * zeta.y * c.x) / determinant;
    float density = exp(-0.5 * q) / (2.0 * PI * sqrt(determinant));
    float fresnel = WATER_F0 + (1.0 - WATER_F0) * pow(1.0 - saturate(dot(view, h)), 5.0);
    float masking = 1.0 + SmithLambda(vz, DirectionalVariance(view, east, north, c)) + SmithLambda(lz, DirectionalVariance(sun, east, north, c));

    return density * fresnel / (4.0 * hz * hz * hz * hz * vz * masking);

}

// The sky the surface mirrors, blurred by the spread of its facets. A reflection that would dip below the horizon
// strikes the next wave instead, which mirrors the sky just above it.
float3 SkyReflection(float3 reflected, float3 up, float variance, Sunlight light) {

    reflected = normalize(reflected + up * max(0.01 - dot(reflected, up), 0.0));

    if (!CameraInsideAir()) {

        return light.sky / PI;

    }

    float3 fromCentre = _WorldSpaceCameraPos - _PlanetCentre;
    float viewHeight = max(length(fromCentre), _PlanetRadius + 1e-3);
    float3 cameraUp = fromCentre / length(fromCentre);
    float3 flatView = reflected - cameraUp * dot(reflected, cameraUp);
    float3 flatSun = _SunDirection - cameraUp * dot(_SunDirection, cameraUp);
    float cosLight = dot(flatView, flatSun) / max(length(flatView) * length(flatSun), 1e-5);

    // The mirrored lobe spans about twice the facets' spread of slope, against the table's 192 texels round the sky.
    float lod = log2(max(4.0 * sqrt(variance) * SKY_VIEW_SIZE.x / (2.0 * PI), 1.0));

    return SAMPLE_TEXTURE2D_LOD(_SkyViewLut, sampler_linear_clamp, SkyViewUv(viewHeight, dot(reflected, cameraUp), cosLight), lod).rgb;

}

// Light the water sends back up from below its surface (radiance, just above it), where the bed shows through a view
// path of pathMetres and the light came down depthMetres (both real-world) to reach it.
float3 WaterBody(WaterOptics optics, float3 bed, float pathMetres, float depthMetres, float3 down) {

    float3 up = exp(-optics.attenuation * pathMetres);
    float3 trip = up * exp(-optics.attenuation * depthMetres);

    // Light crossing the surface leaves a little over half its radiance behind (Mobley's t / n^2).
    return 0.55 * (bed * up + optics.reflectance * down / PI * (1.0 - trip));

}

// Sunlight and skylight just below the surface, after the share the surface mirrors away.
float3 Underwater(Sunlight light, float3 up, float variance) {

    float cosSun = saturate(dot(_SunDirection, up));

    return light.direct * light.shadow * cosSun * (1.0 - EffectiveFresnel(cosSun, variance)) + light.sky * (1.0 - 0.066);

}

// Sea ice's albedo: the satellite's where it saw the ice, otherwise snow-covered pack ice's (Perovich); Blue Marble fills
// the polar night with open ocean.
#define SEA_ICE_ALBEDO float3(0.72, 0.76, 0.8)

float3 SeaIceAlbedo(float3 satellite) {

    return lerp(SEA_ICE_ALBEDO, satellite, saturate(dot(satellite, float3(0.2126, 0.7152, 0.0722)) / 0.3));

}

// Whitecap coverage for a wind of speed metres per second (Monahan and O'Muircheartaigh 1980).
float WhitecapCoverage(float wind) {

    return min(3.84e-6 * pow(max(wind, 0.0), 3.41), 0.3);

}

// Foam lit by the sun and sky: fresh foam bright (half the light back), fading to a fifth as its bubbles burst.
float3 FoamRadiance(float fresh, float3 normal, Sunlight light) {

    float albedo = lerp(0.18, 0.5, fresh);

    return albedo / PI * (light.direct * light.shadow * saturate(dot(normal, _SunDirection)) + light.sky);

}

// The surface as a view meets it: the frame's axes on the ground, the mean normal (a face turned away tipped to
// edge-on, since at grazing angles the eye sees only faces turned toward it), and the spread of slopes along the view.
struct WaterLook {

    float3 up;
    float3 east;
    float3 north;
    float3 view;
    float3 normal;
    float variance;
    float fresnel;
    WaveSurface waves;

};

WaterLook LookAtWater(float3 positionWS, WaveSurface waves, float3 up) {

    WaterLook look;
    look.up = up;
    look.east = normalize(_WaterEast - up * dot(_WaterEast, up));
    look.north = cross(up, look.east);
    look.view = normalize(_WorldSpaceCameraPos - positionWS);
    look.waves = waves;

    float3 normal = normalize(up - look.east * waves.slope.x - look.north * waves.slope.y);

    look.normal = normalize(normal - look.view * min(dot(normal, look.view) - 0.02, 0.0));
    look.variance = DirectionalVariance(look.view, look.east, look.north, waves.covariance);
    look.fresnel = EffectiveFresnel(dot(look.view, look.normal), look.variance);

    return look;

}

// The water's radiance toward the eye: the mirrored sky or scenery, the light from its body and crests, the sun's
// glint, whitecaps where the sea breaks (as much of it as Monahan's law gives the local wind), white water where a river
// runs in rapids (surf), and ice lying on it. Where the water thins to nothing at the
// waterline, the bed seen straight through it (film, its share in alpha) takes over, so the edge has no seam. Foam's
// structure comes in two phases (FoamStructures), each feathered before they blend, so moving foam keeps its bubbles crisp.
float3 WaterColour(WaterLook look, Sunlight light, float3 reflection, float3 body, float crest, WaterOptics optics, SeaState sea, float surf, float3 structure, float3 ice, float4 film) {

    float3 glow = optics.reflectance * light.direct * light.shadow * crest * pow(saturate(dot(-look.view, _SunDirection) + 0.2), 4.0);
    float3 glint = SunGlint(look.view, _SunDirection, look.up, look.east, look.north, look.waves) * light.direct * light.shadow;
    float coverage = max(saturate(look.waves.foam * WhitecapCoverage(sea.wind) / max(_WaterCoverage, 1e-4)), saturate(surf));
    float whitecaps = lerp(FeatherFoam(coverage, structure.y), FeatherFoam(coverage, structure.x), structure.z);
    float3 colour = lerp(look.fresnel * reflection + (1.0 - look.fresnel) * (body + glow) + glint, film.rgb, film.a);

    // White water in the surf and in rapids is foam still breaking.
    colour = lerp(colour, FoamRadiance(max(look.waves.fresh, saturate(2.0 * surf)), look.normal, light), whitecaps);

    return lerp(colour, ice, sea.ice);

}

#endif
