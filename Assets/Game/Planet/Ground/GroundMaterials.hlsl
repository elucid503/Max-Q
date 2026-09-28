// The ground's materials: grass, forest floor, soil, sand, rock, scree and snow. Where each lies comes from the land
// itself: how steep it is, whether it is a crest or a hollow, how high, and world-anchored noise, inside the bounds the
// satellite's tone sets (vegetated, bare, sandy or snowbound). Near the camera they are textures at two scales, out to
// 15 km a macro scale of the same textures, and past that the satellite colour, reshaded by the same choice of
// materials. Each texture is laid stochastically, turned and shifted at random across a lattice, so none shows a repeat.
// Everything is recoloured by how the satellite pixel differs from the materials its tone implies, so the handover never
// shifts the colour the ground has from further away, while a cliff in snow still shows as rock, coloured like the bare
// land around it.
#ifndef MAXQ_GROUND_MATERIALS_INCLUDED
#define MAXQ_GROUND_MATERIALS_INCLUDED

#include "Ground.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

TEXTURE2D_ARRAY(_GroundAlbedo);
TEXTURE2D_ARRAY(_GroundNormal);
SAMPLER(sampler_GroundAlbedo);

// The arrays' own sampler filters anisotropically, which only the near repeat needs; coarser samples take plain
// trilinear filtering, far cheaper at grazing angles.
SAMPLER(sampler_trilinear_repeat);

// Repeats of each scale before its tile origin wraps; must match GroundView.TilePeriod. Anything drawn at random on a
// lattice tied to the repeats wraps with it, so every patch draws the same numbers at the same place.
#define TILE_PERIOD 64

uint3 Pcg3(uint3 v) {

    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;

    return v;

}

// Three uniform numbers, ten bits each, for a lattice point, the lattice wrapping every period points (a power of two, so
// the wrap is a mask rather than a division, which GPUs do slowly).
float3 LatticeRandom(int3 lattice, int period, uint seed) {

    uint3 wrapped = (uint3)lattice & (uint)(period - 1);
    uint h = seed * 0x9E3779B9u ^ wrapped.x * 0x8DA6B343u ^ wrapped.y * 0xD8163841u ^ wrapped.z * 0xCB1AB31Fu;

    h = (h ^ (h >> 16)) * 0x7FEB352Du;
    h = (h ^ (h >> 15)) * 0x846CA68Bu;
    h ^= h >> 16;

    return float3(h & 1023u, (h >> 10) & 1023u, (h >> 20) & 1023u) / 1023.0;

}

// Stochastic tiling (after Heitz and Neyret, and Mikkelsen's hex tiling): a lattice of STOCHASTIC_CELLS points to a
// repeat each way, split into triangles; each point turns and shifts the texture at random, and a spot blends the three
// of its triangle, weighted sharply toward the nearest, keeping the texture's contrast where they mix. Points weighing
// under STOCHASTIC_FAINT are skipped, so most spots take one sample and few take three.
#define STOCHASTIC_CELLS 1
#define STOCHASTIC_SHARPNESS 8.0
#define STOCHASTIC_FAINT 0.1

// The stochastic layout at a spot: the lattice cell and which of its triangles holds the spot, and how much each of that
// triangle's points weighs. Each point's turn and shift are drawn only as it is sampled, so none is held between samples.
struct Stochastic {

    int2 cell;
    bool upper;
    float3 weight;

};

Stochastic StochasticAt(float2 uv) {

    float2 p = uv * STOCHASTIC_CELLS;
    float2 cell = floor(p);
    float2 f = p - cell;

    Stochastic s;
    s.cell = (int2)cell;
    s.upper = f.x + f.y > 1.0;

    float3 w = s.upper ? float3(f.x + f.y - 1.0, 1.0 - f.y, 1.0 - f.x) : float3(1.0 - f.x - f.y, f.x, f.y);

    w = pow(max(w, 1e-4), STOCHASTIC_SHARPNESS);
    s.weight = w / (w.x + w.y + w.z);

    return s;

}

// How the k-th point of a layout turns and shifts the texture.
void StochasticTransform(Stochastic s, int k, int repeats, uint seed, out float2x2 turn, out float2 shift) {

    int2 corner = k == 0 ? (s.upper ? int2(1, 1) : int2(0, 0)) : k == 1 ? int2(1, 0) : int2(0, 1);
    float3 random = LatticeRandom(int3(s.cell + corner, 0), STOCHASTIC_CELLS * TILE_PERIOD * repeats, seed);
    float2 heading = normalize(float2(random.z, frac(random.x + random.y + random.z)) * 2.0 - 1.0 + 1e-4);

    turn = float2x2(heading.x, -heading.y, heading.y, heading.x);
    shift = random.xy;

}

// Albedo and height, and the tangent-space normal when normals is set, blended from the stochastic layout at uv; mean is
// the texture's own albedo and height, which the blend keeps its contrast about. A negative lod filters each sample by the
// gradients dx and dy, turned with it, as an anisotropic sampler needs; otherwise every sample takes that mip.
void StochasticSample(int slice, float2 uv, float2 dx, float2 dy, float lod, int repeats, uint seed, bool normals, float4 mean, SamplerState state,
    out float4 albedo, out float3 normal) {

    Stochastic s = StochasticAt(uv);
    float4 sum = 0.0;
    float3 bent = 0.0;

    [unroll]
    for (int k = 0; k < 3; k++) {

        [flatten]
        if (s.weight[k] > STOCHASTIC_FAINT) {

            float2x2 turn;
            float2 shift;

            StochasticTransform(s, k, repeats, seed, turn, shift);

            float2 at = mul(turn, uv) + shift;
            float4 a;
            float3 t = float3(0.0, 0.0, 1.0);

            [flatten]
            if (lod < 0.0) {

                a = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundAlbedo, state, at, slice, mul(turn, dx), mul(turn, dy));

                if (normals) {

                    t = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY_GRAD(_GroundNormal, state, at, slice, mul(turn, dx), mul(turn, dy)));

                }

            } else {

                a = SAMPLE_TEXTURE2D_ARRAY_LOD(_GroundAlbedo, state, at, slice, lod);

                if (normals) {

                    t = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY_LOD(_GroundNormal, state, at, slice, lod));

                }

            }

            sum += s.weight[k] * (a - mean);
            bent += s.weight[k] * float3(mul(t.xy, turn), t.z);

        }

    }

    float contrast = rsqrt(dot(s.weight, s.weight));

    albedo = saturate(mean + sum * contrast);
    normal = normalize(float3(bent.xy * contrast, bent.z));

}

#define GRASS 0
#define FOREST 1
#define SOIL 2
#define SAND 3
#define ROCK 4
#define SNOW 5
#define SCREE 6
#define MATERIALS 7

// Texture slice of each material, and how many of its repeats fit in one NEAR_TILE: scree is rock broken small. Repeats
// are powers of two, so the stochastic lattice wraps with a mask.
static const int Slice[MATERIALS] = { 0, 1, 2, 3, 4, 5, 4 };
static const float Repeats[MATERIALS] = { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 4.0 };

// Metres per repeat of the near, far, macro and broad samples; each is a whole multiple of the one before where they
// must line up, and GroundView keeps each one's origin.
#define NEAR_TILE 3.0
#define FAR_TILE 17.0
#define MACRO_TILE 153.0
#define BROAD_TILE 1377.0

// Parallax: metres of relief each material's full height stands for (grass and forest floor are blades and litter,
// which parallax would smear), how far from the camera it shows, and its steps.
static const float ParallaxDepth[MATERIALS] = { 0.0, 0.0, 0.05, 0.04, 0.08, 0.05, 0.1 };
#define PARALLAX_REACH 25.0
#define PARALLAX_STEPS 10

// Texels per metre of the near sample: 1024-texel materials over NEAR_TILE metres.
#define NEAR_TEXELS_PER_METRE (1024.0 / NEAR_TILE)

// Metres from the camera over which the near textures fade into the far ones (past about 40 m the far repeat's texels
// are as fine as the pixels), the far into the macro scale, and the macro into the satellite.
#define FINE_START 30.0
#define FINE_END 60.0
#define DETAIL_START 1500.0
#define DETAIL_END 4000.0
#define MACRO_START 9000.0
#define MACRO_END 15000.0

// Forest gives way to grass and soil around this altitude (metres on Terra: about 2 km on Earth).
#define TREE_LINE 420.0

struct Layer {

    float3 albedo;
    float3 normalOS;
    float height;

};

// A scale of a material: where a spot falls in its repeats, how far that moves across the pixel, and the repeats to a
// tile origin's repeat, which its stochastic lattice wraps with.
struct Tiling {

    float3 position;
    float3 dx;
    float3 dy;
    int repeats;
    uint seed;

};

float2 OnPlane(float3 v, int plane) {

    return plane == 0 ? v.zy : plane == 1 ? v.xz : v.xy;

}

// One plane of a triplanar sample, its albedo and height and its tangent-space normal each laid stochastically or plainly.
void PlaneSample(int slice, Tiling tiling, int plane, bool stochastic, bool stochasticNormal, float4 mean, SamplerState state,
    out float4 albedo, out float3 normal) {

    float2 uv = OnPlane(tiling.position, plane);
    float2 dx = OnPlane(tiling.dx, plane);
    float2 dy = OnPlane(tiling.dy, plane);

    // Laid plainly, a sample needs no gradients: its mip from the widest step across the pixel, 1024 texels a repeat.
    float lod = log2(max(max(length(dx), length(dy)) * 1024.0, 1e-6));

    UNITY_BRANCH
    if (stochastic) {

        // Blended normals only where asked; the near sample's anisotropic filter needs the gradients.
        StochasticSample(slice, uv, dx, dy, stochasticNormal ? -1.0 : lod, tiling.repeats, tiling.seed + 17u * plane, stochasticNormal, mean, state,
            albedo, normal);

        if (!stochasticNormal) {

            normal = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY_LOD(_GroundNormal, state, uv, slice, lod));

        }

        return;

    }

    albedo = SAMPLE_TEXTURE2D_ARRAY_LOD(_GroundAlbedo, state, uv, slice, lod);
    normal = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY_LOD(_GroundNormal, state, uv, slice, lod));

}

// Triplanar in the body-fixed frame, skipping planes the surface barely faces; normals by whiteout blending. Mean is the
// texture's own albedo and height, which a stochastic layout keeps its contrast about; its normals may be laid plainly.
Layer Triplanar(int slice, Tiling tiling, bool stochastic, bool stochasticNormal, float4 mean, float3 normal, float3 weights, SamplerState state) {

    Layer layer;
    layer.albedo = 0.0;
    layer.normalOS = 0.0;
    layer.height = 0.0;

    float4 albedo;
    float3 t;

    UNITY_BRANCH
    if (weights.x > 0.02) {

        PlaneSample(slice, tiling, 0, stochastic, stochasticNormal, mean, state, albedo, t);
        t = float3(t.xy + normal.zy, abs(t.z) * normal.x);
        layer.albedo += albedo.rgb * weights.x;
        layer.height += albedo.a * weights.x;
        layer.normalOS += t.zyx * weights.x;

    }

    UNITY_BRANCH
    if (weights.y > 0.02) {

        PlaneSample(slice, tiling, 1, stochastic, stochasticNormal, mean, state, albedo, t);
        t = float3(t.xy + normal.xz, abs(t.z) * normal.y);
        layer.albedo += albedo.rgb * weights.y;
        layer.height += albedo.a * weights.y;
        layer.normalOS += t.xzy * weights.y;

    }

    UNITY_BRANCH
    if (weights.z > 0.02) {

        PlaneSample(slice, tiling, 2, stochastic, stochasticNormal, mean, state, albedo, t);
        t = float3(t.xy + normal.xy, abs(t.z) * normal.z);
        layer.albedo += albedo.rgb * weights.z;
        layer.height += albedo.a * weights.z;
        layer.normalOS += t.xyz * weights.z;

    }

    float total = weights.x * (weights.x > 0.02) + weights.y * (weights.y > 0.02) + weights.z * (weights.z > 0.02);

    layer.albedo /= total;
    layer.height /= total;
    layer.normalOS = normalize(layer.normalOS);

    return layer;

}

// Each material's mean linear albedo: the mean of its texture, taken from Art/Ground; update it with the textures.
static const float3 Averages[MATERIALS] = {

    float3(0.0999, 0.1263, 0.0275), float3(0.3192, 0.2964, 0.1071), float3(0.1728, 0.1314, 0.0759),
    float3(0.5872, 0.4684, 0.2853), float3(0.0729, 0.0683, 0.0576), float3(0.6942, 0.8120, 0.9188),
    float3(0.0729, 0.0683, 0.0576),

};

float3 Average(int material) {

    return Averages[material];

}

// Where a spot, metres from the patch centre, falls in a scale's repeats and how far that moves across the pixel.
Tiling TilingOf(float3 metres, float3 dx, float3 dy, float tile, float4 origin, int repeats, uint seed) {

    Tiling tiling;
    tiling.position = (metres / tile + origin.xyz) * repeats;
    tiling.dx = dx / tile * repeats;
    tiling.dy = dy / tile * repeats;
    tiling.repeats = repeats;
    tiling.seed = seed;

    return tiling;

}

// The macro sample is filtered to texels of at least this many metres: the materials' textures magnified fifty times
// would otherwise show as smeared copies of their finest grain, where all it should carry is how colour and relief wander.
// So blurred, its repeats hardly show, and it is laid plainly.
#define MACRO_TEXEL 2.4

Tiling Blurred(Tiling tiling) {

    float spread = max(max(length(tiling.dx), length(tiling.dy)), 1e-8);
    float widen = max(MACRO_TEXEL / MACRO_TILE / spread, 1.0);

    tiling.dx *= widen;
    tiling.dy *= widen;

    return tiling;

}

// Seed of a material's scale, for its stochastic layout.
uint LayoutSeed(int material, int scale) {

    return (uint)(material * 4 + scale) * 0x2545F491u;

}

// One material: the macro sample sets its colour and carries relief a few metres to tens of metres across; within
// DETAIL_END the far sample adds relief and grain a few centimetres up, and within FINE_END the near one adds the finest
// grain and its height, with the far one breaking up its tone. The macro and far samples blend planes over a narrower
// band than the near one: their seams are too coarse to show, and most slopes then need only one plane of each. dx and dy
// are how far metres moves across the pixel.
Layer SampleMaterial(int material, float3 metres, float3 macroMetres, float3 dx, float3 dy, float3 normalOS, float3 weights, float3 sharpWeights,
    float detail, float fine) {

    int slice = Slice[material];
    float3 average = Average(material);
    float4 mean = float4(average, 0.5);
    Tiling macroTiling = Blurred(TilingOf(macroMetres, dx, dy, MACRO_TILE, _TileOriginMacro, 1, LayoutSeed(material, 0)));
    Layer macro = Triplanar(slice, macroTiling, false, false, mean, normalOS, sharpWeights, sampler_trilinear_repeat);

    UNITY_BRANCH
    if (detail <= 0.0) {

        return macro;

    }

    // The far sample is laid plainly: under the near one it only breaks up its tone, and past it the blurred macro
    // sample's colour, wandering on its own longer repeat, hides the far one's.
    Layer far = Triplanar(slice, TilingOf(metres, dx, dy, FAR_TILE, _TileOriginFar, 1, LayoutSeed(material, 1)), false, false, mean,
        macro.normalOS, sharpWeights, sampler_trilinear_repeat);
    Layer near = far;
    float3 grain = far.albedo / max(average, 1e-3);

    UNITY_BRANCH
    if (fine > 0.0) {

        Tiling nearTiling = TilingOf(metres, dx, dy, NEAR_TILE, _TileOriginNear, (int)Repeats[material], LayoutSeed(material, 2));

        near = Triplanar(slice, nearTiling, true, true, mean, far.normalOS, weights, sampler_GroundAlbedo);
        grain = lerp(grain, near.albedo * lerp(1.0, grain, 0.5) / max(average, 1e-3), fine);

    }

    Layer layer;
    layer.albedo = macro.albedo * lerp(1.0, grain, detail);
    layer.height = lerp(macro.height, lerp(far.height, saturate(near.height * 0.7 + far.height * 0.3), fine), detail);
    layer.normalOS = normalize(lerp(macro.normalOS, lerp(far.normalOS, near.normalOS, fine), detail));

    return layer;

}

// The ground's noise (see GroundNoise.cs): one repeat of it spans TILE_PERIOD repeats of a scale.
TEXTURE2D(_GroundNoise);

#define GROUND_NOISE_TEXELS 2048.0

// Smooth world-anchored noise in [0, 1], on the plane the ground's up most faces: patches tens of metres across (octaves
// of 38 m and 19 m) and hundreds of metres across (690 m and 340 m). Blurred where a pixel spans footprint metres, so
// neither aliases far away; zero footprint keeps them sharp.
float2 GroundNoise(float3 metres, float3 upOS, float footprint) {

    float3 a = abs(upOS);
    int plane = a.x > a.y && a.x > a.z ? 0 : a.y > a.z ? 1 : 2;
    float2 macro = OnPlane(metres / MACRO_TILE + _TileOriginMacro.xyz, plane) / TILE_PERIOD;
    float2 broad = OnPlane(metres / BROAD_TILE + _TileOriginBroad.xyz, plane) / TILE_PERIOD;
    float texel = TILE_PERIOD / GROUND_NOISE_TEXELS;
    float2 noise = float2(
        SAMPLE_TEXTURE2D_LOD(_GroundNoise, sampler_trilinear_repeat, macro, max(log2(2.0 * footprint / (MACRO_TILE * texel)), 0.0)).r,
        SAMPLE_TEXTURE2D_LOD(_GroundNoise, sampler_trilinear_repeat, broad + 0.37, max(log2(2.0 * footprint / (BROAD_TILE * texel)), 0.0)).r);

    // The texture holds its octaves' sum over their total amplitude, halved about the middle.
    return saturate(0.5 + 0.9 * (noise * 2.0 - 1.0) * 1.5);

}

// Convexity is how much more sky a spot sees than a plane of its slope would: negative in hollows, near zero on crests
// and plains. The horizon occlusion measures it at every scale from the stones to the valley walls. Beach is how far a
// spot lies in the band just over the water that waves and wind keep bare.
struct Terrain {

    float upness;
    float convexity;
    float altitude;
    float2 noise;
    float beach;

};

// Metres over the water that a beach reaches: a sea's surf and storms reach far up the shore, a lake's hardly, and a
// river's banks, grassed to the water, not at all.
#define SEA_BEACH 1.0
#define LAKE_BEACH 0.3

// How far a spot lies in the beach band, above water metres over the nearest sheet (negative under it), at altitude, by
// water whose current runs at flow m/s.
float BeachAt(float aboveWater, float altitude, float flow) {

    float rise = lerp(SEA_BEACH, LAKE_BEACH, saturate(altitude / 5.0)) * saturate(1.0 - flow / 0.2);

    return saturate(1.0 - aboveWater / max(rise, 1e-3)) * (rise > 1e-3);

}

// How much each material suits a spot. The satellite's tone (sRGB-encoded, as the thresholds were read off the imagery)
// says what the land is; the terrain says where on it each material lies: rock on steep ground and bare crests, scree on
// the steep hollows below it, snow where it can hold, forest below the tree line.
void MaterialWeights(float3 tone, Terrain terrain, out float weights[MATERIALS]) {

    float sum = max(tone.r + tone.g + tone.b, 1e-4);
    float luma = dot(tone, float3(0.2126, 0.7152, 0.0722));
    float chroma = (max(tone.r, max(tone.g, tone.b)) - min(tone.r, min(tone.g, tone.b))) / max(max(tone.r, max(tone.g, tone.b)), 1e-4);
    float2 n = terrain.noise - 0.5;

    // Noise moves each threshold, so borders between materials follow the land instead of the satellite's pixels.
    float vegetation = saturate((tone.g / sum - 0.36 + 0.02 * n.y) / 0.08);
    // At the satellite's half-kilometre pixels forest and meadow differ only in how dark they are (forest about 0.14 in
    // luminance, meadow and crops 0.16 to 0.25), so darkness sets the odds of forest and broad noise lays it out as
    // woods and clearings.
    float canopy = saturate((0.19 - luma) / 0.05 + 1.2 * n.y + 0.4 * n.x);
    float bright = saturate((luma - 0.18) / 0.15 + 0.3 * n.x);
    float snow = saturate((luma - 0.40) / 0.15 + 0.4 * n.y) * saturate(1.0 - chroma * 3.0);
    float bare = saturate(1.0 - chroma * 4.0) * (1.0 - vegetation);

    // Grass and scrub hold to about 45 degrees, bare ground weathers to rock from about 35.
    float steep = saturate((lerp(0.8, 0.7, vegetation) + 0.1 * n.x - terrain.upness) / 0.1);
    float cliff = saturate((0.56 + 0.08 * n.x - terrain.upness) / 0.1);
    float crest = saturate(terrain.convexity * 30.0 + n.x);
    float hollow = saturate(-terrain.convexity * 15.0 - 0.3 + n.x);
    float scree = saturate((terrain.upness - 0.62) / 0.08) * saturate((0.93 - terrain.upness) / 0.08) * hollow;
    float alpine = smoothstep(TREE_LINE - 40.0, TREE_LINE + 40.0, terrain.altitude + 80.0 * n.y);

    canopy *= 1.0 - alpine;
    snow *= saturate((terrain.upness - 0.6) / 0.12) * (1.0 - 0.5 * crest);

    weights[GRASS] = vegetation * (1.0 - canopy) * (1.0 - 0.4 * crest);
    weights[FOREST] = vegetation * canopy;
    weights[SOIL] = (1.0 - vegetation) * (1.0 - bright) * 0.8 + vegetation * 0.35 * crest * (1.0 - canopy);
    weights[SAND] = (1.0 - vegetation) * bright * saturate((tone.r / sum - 0.36) / 0.06);
    weights[ROCK] = max(max(steep, cliff), bare * max(0.6 * crest, 0.35));
    weights[SNOW] = snow * 2.0;
    weights[SCREE] = scree * (1.0 - 0.7 * vegetation) * (1.0 - snow) * 1.4;

    for (int i = 0; i < MATERIALS; i++) {

        if (i != ROCK && i != SCREE) {

            weights[i] *= 1.0 - steep;

        }

    }

    weights[SCREE] *= 1.0 - cliff;

    // The satellite's half-kilometre pixels never see a beach: flat ground in the band is sand, save under snow.
    float beach = terrain.beach * saturate((terrain.upness - 0.9) / 0.05) * (1.0 - snow);

    for (int j = 0; j < MATERIALS; j++) {

        weights[j] *= 1.0 - beach;

    }

    weights[SAND] += 1.5 * beach;

}

// Rock takes the colour of the bare land around it, as cliffs weather to the colour of their ground: red in sandstone
// country, pale on limestone, dark where the satellite sees dark ground, with the hue muted and the brightness kept to
// what rock has. Under vegetation or snow the satellite shows no rock, which is then a weathered grey. Tone is the
// satellite's, sRGB-encoded, as MaterialWeights reads it.
#define ROCK_GREY float3(0.15, 0.14, 0.125)

float3 RockColour(float3 tone) {

    float sum = max(tone.r + tone.g + tone.b, 1e-4);
    float vegetation = saturate((tone.g / sum - 0.36) / 0.08);
    float snow = saturate((dot(tone, float3(0.2126, 0.7152, 0.0722)) - 0.40) / 0.15);
    float3 land = SRGBToLinear(tone);
    float brightness = max(Luminance(land), 1e-3);
    float3 hue = lerp(1.0, land / brightness, 0.7);

    return lerp(ROCK_GREY, hue * clamp(0.9 * brightness, 0.08, 0.35), (1.0 - vegetation) * (1.0 - snow));

}

// Mean colour of a material, rock and scree taking the site's.
float3 MaterialColour(int material, float3 rock) {

    return material == ROCK || material == SCREE ? rock : Average(material);

}

// How far a material's textures are recoloured from their own mean: rock and scree toward the site's rock.
float3 Tint(int material, float3 rock) {

    return material == ROCK || material == SCREE ? rock / Average(ROCK) : 1.0;

}

// The mean colour of materials in these proportions.
float3 Palette(float weights[MATERIALS], float3 rock) {

    float3 colour = 0.0;
    float total = 0.0;

    for (int i = 0; i < MATERIALS; i++) {

        colour += MaterialColour(i, rock) * weights[i];
        total += weights[i];

    }

    return colour / max(total, 1e-4);

}

// Snow grains big enough to mirror the sun: one cell in eight, each a 1024th of the far repeat, holds a facet tipped
// at random up to 20 degrees off the surface, which flashes when it turns the sun to the eye. Once cells shrink below a
// pixel they blend into the sheen instead.
#define GLINT_CELLS 1024.0
#define GLINT_CELL (FAR_TILE / GLINT_CELLS)

float Glitter(float3 metres, float3 normalWS, float3 toCamera, float footprint) {

    float fade = saturate(2.0 - footprint / GLINT_CELL);

    UNITY_BRANCH
    if (fade <= 0.0) {

        return 0.0;

    }

    uint3 cell = (uint3)(int3)floor((metres / FAR_TILE + _TileOriginFar.xyz) * GLINT_CELLS) & 1023u;
    float3 random = Pcg3(cell) / 4294967295.0;

    if (random.x > 0.125) {

        return 0.0;

    }

    float3 tilt = Pcg3(cell ^ 0x9E3779B9u) / 4294967295.0 * 2.0 - 1.0;
    float3 facet = normalize(normalWS + 0.35 * tilt);
    float align = dot(facet, normalize(toCamera + _SunDirection));
    float flash = saturate((align - 0.9994) / 0.0004);

    return flash * flash * 6.0 * fade;

}

struct GroundSurface {

    float3 albedo;
    float3 normalWS;
    float snow;

};

// How far to shift the material samples so the dominant material's height map stands proud of the surface: the view ray
// is marched down through the relief on the plane the surface most faces until it passes under the height map, as laid
// by the point of the near sample's stochastic layout that weighs most here.
float3 Parallax(int material, float3 metres, float3 normalOS, float3 planes, float3 toCameraOS, float distance, float footprint) {

    float reach = saturate(1.0 - distance / PARALLAX_REACH);

    UNITY_BRANCH
    if (reach <= 0.0 || ParallaxDepth[material] <= 0.0) {

        return 0.0;

    }

    int slice = Slice[material];
    int plane = planes.x > planes.y && planes.x > planes.z ? 0 : planes.y > planes.z ? 1 : 2;
    float lod = max(log2(footprint * NEAR_TEXELS_PER_METRE * Repeats[material]), 0.0);
    float depth = ParallaxDepth[material] * reach;
    float3 step = -toCameraOS / max(dot(toCameraOS, normalOS), 0.15) * (depth / PARALLAX_STEPS);
    float3 offset = 0.0;
    float previousGap = -1.0;

    Tiling tiling = TilingOf(metres, 0.0, 0.0, NEAR_TILE, _TileOriginNear, (int)Repeats[material], LayoutSeed(material, 2));
    float2 uv = OnPlane(tiling.position, plane);
    Stochastic layout = StochasticAt(uv);
    float3 w = layout.weight;
    float2x2 turn;
    float2 shift;

    StochasticTransform(layout, w.x >= w.y && w.x >= w.z ? 0 : w.y >= w.z ? 1 : 2, tiling.repeats, tiling.seed + 17u * plane, turn, shift);

    for (int i = 0; i < PARALLAX_STEPS; i++) {

        float2 at = mul(turn, uv + OnPlane(offset, plane) / NEAR_TILE * Repeats[material]) + shift;
        float surface = (1.0 - SAMPLE_TEXTURE2D_ARRAY_LOD(_GroundAlbedo, sampler_GroundAlbedo, at, slice, lod).a) * depth;
        float gap = surface - i * depth / PARALLAX_STEPS;

        if (gap <= 0.0) {

            // Between the last step above the relief and this one below it, where the ray crossed.
            return offset - step * saturate(-gap / max(previousGap - gap, 1e-5));

        }

        previousGap = gap;
        offset += step;

    }

    return offset;

}

// The materials that suit a spot, the colour its rock takes, and the recolouring toward its satellite pixel.
struct GroundSite {

    float weights[MATERIALS];
    float3 rock;
    float3 transfer;

};

// Share of the ground a material has at a site.
float Share(GroundSite site, int material) {

    float total = 0.0;

    for (int i = 0; i < MATERIALS; i++) {

        total += site.weights[i];

    }

    return site.weights[material] / max(total, 1e-4);

}

// A spot at metres (object space) on ground that faces upness of the way up, sees occlusion of the sky, and stands at
// altitude metres, aboveWater over the nearest sheet whose current runs at flow m/s, under a satellite pixel of colour
// satellite and tone tone, seen by pixels footprint metres across; upOS is the way up in object space.
GroundSite Site(float3 satellite, float3 tone, float3 metres, float3 upOS, float upness, float occlusion, float altitude, float aboveWater,
    float flow, float footprint) {

    Terrain terrain;
    terrain.upness = upness;
    terrain.convexity = occlusion - (1.0 - 0.5 * (1.0 - upness * upness));
    terrain.altitude = altitude;
    terrain.noise = GroundNoise(metres, upOS, footprint);
    terrain.beach = BeachAt(aboveWater, altitude, flow);

    GroundSite site;
    MaterialWeights(tone, terrain, site.weights);
    site.rock = RockColour(tone);

    // What the satellite pixel implies from its tone alone, on level, unremarkable ground: the materials' departure
    // from it becomes the recolouring.
    Terrain level;
    level.upness = 1.0;
    level.convexity = 0.0;
    level.altitude = 0.0;
    level.noise = 0.5;
    level.beach = 0.0;

    float implied[MATERIALS];
    MaterialWeights(tone, level, implied);

    site.transfer = lerp(1.0, clamp(satellite / max(Palette(implied, site.rock), 1e-3), 0.25, 4.0), 0.8);

    // A beach keeps its sand's own colour: the satellite pixel it falls in is the land or the water beside it.
    site.transfer = lerp(site.transfer, 1.0, 0.8 * Share(site, SAND) * terrain.beach);

    return site;

}

// Past the trees drawn near the camera, forest is a canopy: domed crowns on a jittered grid, CROWNS to a macro repeat
// (8.5 m apart), sunlit on top and dark in the gaps between them, in the satellite's own colour, which is what a forest
// looks like from above. Where crowns shrink below a few pixels only their mean shade is left.
#define CANOPY_START 250.0
#define CANOPY_FULL 600.0
#define CROWNS 18.0

void Canopy(inout GroundSurface surface, GroundSite site, float3 satellite, float3 metres, float3 upOS, float distance, float footprint) {

    float cover = Share(site, FOREST) * smoothstep(CANOPY_START, CANOPY_FULL, distance);

    UNITY_BRANCH
    if (cover <= 0.01) {

        return;

    }

    float3 q = (metres / MACRO_TILE + _TileOriginMacro.xyz) * CROWNS;
    float3 a = abs(upOS);
    int plane = a.x > a.y && a.x > a.z ? 0 : a.y > a.z ? 1 : 2;
    float2 c = plane == 0 ? q.zy : plane == 1 ? q.xz : q.xy;
    float2 cell = floor(c);
    float crown = -1.0;
    float2 slope = 0.0;
    float shade = 1.0;

    for (int j = -1; j <= 1; j++) {

        for (int i = -1; i <= 1; i++) {

            int2 index = (int2)(cell + float2(i, j));
            uint2 wrapped = (uint2)((index % (int)CROWNS + (int)CROWNS) % (int)CROWNS);
            float3 random = Pcg3(uint3(wrapped, 0x2545F491u)) / 4294967295.0;
            float2 offset = c - (cell + float2(i, j) + 0.5 + 0.7 * (random.xy - 0.5));
            float radius = 0.45 + 0.2 * random.z;
            float h = 1.0 - dot(offset, offset) / (radius * radius);

            if (h > crown) {

                crown = h;
                slope = -2.0 * offset / (radius * radius);
                shade = 0.85 + 0.3 * random.x;

            }

        }

    }

    // The crowns' relief, turned from the plane's axes into the world, fading as they shrink below a few pixels.
    float resolved = saturate(8.5 / (4.0 * footprint) - 0.5);
    float3 alongA = plane == 0 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
    float3 alongB = plane == 2 ? float3(0.0, 1.0, 0.0) : plane == 0 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0);
    float3 tilt = TransformObjectToWorldDir(alongA * slope.x + alongB * slope.y, false) * 0.6 * saturate(crown * 4.0);
    float3 crownNormal = normalize(surface.normalWS - tilt);

    float lit = lerp(0.9, lerp(0.45, 1.15, saturate(crown * 1.5)) * shade, resolved);

    surface.albedo = lerp(surface.albedo, satellite * lit, cover);
    surface.normalWS = normalize(lerp(surface.normalWS, crownNormal, cover * resolved));

}

// The ground's surface at a pixel; dx and dy are how far its object-space metres move across the pixel, taken where
// screen-space derivatives are defined.
GroundSurface GroundMaterial(GroundVaryings input, GroundDetail detail, float3 up, float footprint, float3 dx, float3 dy) {

    float3 satellite = SatelliteColour(input.uv);
    float3 metres = input.positionOS * 1000.0;
    float distance = length(input.positionWS - _WorldSpaceCameraPos) * 1000.0;
    float altitude = (length(input.positionWS - _PlanetCentre) - _PlanetRadius) * 1000.0;

    GroundSite site = Site(satellite, LinearToSRGB(SatelliteTone(input.uv)), metres, TransformWorldToObjectDir(up), dot(detail.normalWS, up),
        detail.occlusion, altitude,
        -detail.waterDepth, length(SampleWaterDetail(input.uv, input.morph).flow), footprint);
    float3 transfer = site.transfer;
    float weights[MATERIALS];

    for (int m = 0; m < MATERIALS; m++) {

        weights[m] = site.weights[m];

    }

    GroundSurface surface;
    surface.albedo = Palette(weights, site.rock) * transfer;
    surface.normalWS = detail.normalWS;
    surface.snow = Share(site, SNOW);

    float macro = 1.0 - smoothstep(MACRO_START, MACRO_END, distance);

    UNITY_BRANCH
    if (macro <= 0.0) {

        Canopy(surface, site, satellite, metres, TransformWorldToObjectDir(up), distance, footprint);

        return surface;

    }

    // The two best suited materials, blended by their height maps so they interlock instead of cross-fading.
    int first = 0;
    int second = 1;

    for (int i = 1; i < MATERIALS; i++) {

        if (weights[i] > weights[first]) {

            second = first;
            first = i;

        } else if (weights[i] > weights[second]) {

            second = i;

        }

    }

    float3 planes = pow(abs(detail.normalOS), 4.0);
    float3 sharpPlanes = planes * planes;

    planes /= planes.x + planes.y + planes.z;
    sharpPlanes /= sharpPlanes.x + sharpPlanes.y + sharpPlanes.z;

    float detailed = 1.0 - smoothstep(DETAIL_START, DETAIL_END, distance);
    float fine = 1.0 - smoothstep(FINE_START, FINE_END, distance);
    float3 toCameraOS = TransformWorldToObjectDir(normalize(_WorldSpaceCameraPos - input.positionWS));
    float3 shifted = metres + Parallax(first, metres, detail.normalOS, planes, toCameraOS, distance, footprint);

    float wa = weights[first] / max(weights[first] + weights[second], 1e-4);
    Layer a = SampleMaterial(first, shifted, metres, dx, dy, detail.normalOS, planes, sharpPlanes, detailed, fine);
    Layer b = a;

    // Where one material has most of the ground, the other barely shows through the interlock; not worth sampling.
    UNITY_BRANCH
    if (wa < 0.8) {

        b = SampleMaterial(second, shifted, metres, dx, dy, detail.normalOS, planes, sharpPlanes, detailed, fine);

    }

    float ha = a.height + wa;
    float hb = b.height + 1.0 - wa;
    float top = max(ha, hb) - 0.2;
    float ba = max(ha - top, 0.0);
    float bb = max(hb - top, 0.0);
    float blend = ba / max(ba + bb, 1e-4);

    float3 albedo = lerp(b.albedo * Tint(second, site.rock), a.albedo * Tint(first, site.rock), blend) * transfer;
    float3 normalOS = normalize(lerp(b.normalOS, a.normalOS, blend));
    float snow = (first == SNOW ? blend : 0.0) + (second == SNOW ? 1.0 - blend : 0.0);

    surface.albedo = lerp(surface.albedo, albedo, macro);
    surface.normalWS = normalize(lerp(detail.normalWS, TransformObjectToWorldNormal(normalOS), macro));
    surface.snow = lerp(surface.snow, snow, macro);

    Canopy(surface, site, satellite, metres, TransformWorldToObjectDir(up), distance, footprint);

    return surface;

}

#endif
