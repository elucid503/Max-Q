// What covers the ground and what colour it is, from the climate. Patches bake their cover (Cover.cs): how much of the
// ground grows plants, how much of that is forest, how arid it is and how much lies under snow. Here the lie of the land
// places rock, scree and beaches within that, and the climate colours each material: grass lush in the wet, straw in
// the dry and olive on the tundra, canopy deep green in the tropics and dark in the taiga, soil red where the tropics
// weather it, sand and rock the tan and rust of the deserts. Shared by the ground, its rocks and its plants.
#ifndef MAXQ_BIOME_INCLUDED
#define MAXQ_BIOME_INCLUDED

#define GRASS 0
#define FOREST 1
#define SOIL 2
#define SAND 3
#define ROCK 4
#define SNOW 5
#define SCREE 6
#define MATERIALS 7

// Each material's texture's mean linear albedo, taken from Art/Ground; update it with the textures.
static const float3 Averages[MATERIALS] = {

    float3(0.0999, 0.1263, 0.0275), float3(0.3192, 0.2964, 0.1071), float3(0.1728, 0.1314, 0.0759),
    float3(0.5872, 0.4684, 0.2853), float3(0.0729, 0.0683, 0.0576), float3(0.6942, 0.8120, 0.9188),
    float3(0.0729, 0.0683, 0.0576),

};

// Linear albedo of each material as a climate colours it, seen from far enough that its texture has averaged out.
#define LUSH_GRASS float3(0.06, 0.105, 0.03)
#define DRY_GRASS float3(0.22, 0.18, 0.09)
#define TUNDRA_GRASS float3(0.09, 0.09, 0.055)
#define TROPICAL_CANOPY float3(0.025, 0.055, 0.018)
#define BOREAL_CANOPY float3(0.018, 0.035, 0.022)
#define DRY_CANOPY float3(0.05, 0.06, 0.03)
#define LATERITE float3(0.26, 0.12, 0.06)
#define COLD_SOIL float3(0.13, 0.11, 0.085)
#define DESERT_SAND float3(0.48, 0.34, 0.19)
#define GREY_ROCK float3(0.13, 0.125, 0.115)
#define DESERT_ROCK float3(0.24, 0.15, 0.09)

// Metres over the water a beach reaches: a sea's surf and storms reach far up the shore, a lake's hardly. And metres
// inland: a sea's beaches run wide where sand is plentiful, in the deserts and the tropics, and narrow to a strand of
// shingle elsewhere; a lake's narrower still, save in the deserts, where sand and salt ring them. So a plain lying a hand
// over the water stays what its cover makes it. Must match PatchStrewJob.
#define SEA_BEACH 1.0
#define LAKE_BEACH 0.3
#define SEA_BEACH_NARROW 12.0
#define SEA_BEACH_WIDE 60.0
#define LAKE_BEACH_NARROW 4.0
#define LAKE_BEACH_WIDE 40.0

struct GroundCover {

    float vegetation;
    float forest;
    float arid;
    float snow;

};

// How the land lies at a spot: how far its ground faces up, how much more sky it sees than a plane of its slope would
// (negative in hollows, near zero on crests and plains), world-anchored noise in [0, 1] that borders follow, how far it
// lies in the band over the water that waves and wind keep bare, and how much of it lies under the water.
struct Lie {

    float upness;
    float convexity;
    float2 noise;
    float beach;
    float bed;

};

// How warm a place's year runs, 0 at -5 C to 1 at 25 C, from the direction up there and its altitude in metres on Terra;
// must match Cover.cs.
float Warmth(float3 up, float altitude) {

    float annual = -25.0 + 52.0 * sqrt(saturate(1.0 - up.y * up.y)) - 0.0065 * max(altitude, 0.0) / 0.2;

    return saturate((annual + 5.0) / 30.0);

}

// How sandy a shore's climate makes it, 0 to 1: the deserts' and the tropics' coral and quartz sands.
float Sandiness(float arid, float warmth) {

    return saturate(max(arid, (warmth - 0.6) / 0.3));

}

// Metres inland a beach reaches on a shore at altitude metres (lakes stand above the sea), under a climate; matches
// Cover.BeachWidth.
float BeachWidth(float altitude, float arid, float warmth) {

    return lerp(lerp(SEA_BEACH_NARROW, SEA_BEACH_WIDE, Sandiness(arid, warmth)), lerp(LAKE_BEACH_NARROW, LAKE_BEACH_WIDE, arid), saturate(altitude / 5.0));

}

// The integral of a ramp falling from one at the waterline to nothing reach metres inland, from the waterline to x.
float BeachArea(float x, float reach) {

    float t = clamp(x, 0.0, reach);

    return t - 0.5 * t * t / reach;

}

// How much of a pixel the beach band covers: the pixel lies inland metres from the waterline (negative offshore), spans
// across metres of the shore and stands aboveWater metres over the water at altitude. The band fades from the waterline
// to width metres inland, or to its rise up a steeper shore, so a pixel far wider than it shows only the share it covers.
float BeachAt(float inland, float across, float aboveWater, float altitude, float width) {

    float rise = lerp(SEA_BEACH, LAKE_BEACH, saturate(altitude / 5.0));
    float reach = max(aboveWater > 0.0 && inland > 0.0 ? min(width, rise * inland / aboveWater) : width, 1e-3);
    float side = 0.5 * max(across, 1e-4);

    return saturate((BeachArea(inland + side, reach) - BeachArea(inland - side, reach)) / (2.0 * side));

}

// How much each material suits a spot: the cover says what grows there and the lie where rock breaks through it, scree
// gathers in steep hollows, snow holds, beaches run and the bed lies under the water, sand where the climate makes
// shores sandy and silt elsewhere.
void MaterialWeights(GroundCover cover, Lie lie, float warmth, out float weights[MATERIALS]) {

    float2 n = lie.noise - 0.5;
    float vegetation = cover.vegetation;

    // Grass and scrub hold to about 45 degrees, bare ground weathers to rock from about 35.
    float steep = saturate((lerp(0.8, 0.7, vegetation) + 0.1 * n.x - lie.upness) / 0.1);
    float cliff = saturate((0.56 + 0.08 * n.x - lie.upness) / 0.1);
    float crest = saturate(lie.convexity * 30.0 + n.x);
    float hollow = saturate(-lie.convexity * 15.0 - 0.3 + n.x);
    float scree = saturate((lie.upness - 0.62) / 0.08) * saturate((0.93 - lie.upness) / 0.08) * hollow;
    float snow = cover.snow * saturate((lie.upness - 0.6) / 0.12) * (1.0 - 0.5 * crest);

    // Snow buries whatever the ground would show, save the steepest rock.
    float open = 1.0 - snow;

    weights[GRASS] = vegetation * (1.0 - cover.forest) * (1.0 - 0.4 * crest) * open;
    weights[FOREST] = vegetation * cover.forest * open;
    weights[SOIL] = ((1.0 - vegetation) * (1.0 - cover.arid) * 0.8 + vegetation * 0.35 * crest * (1.0 - cover.forest)) * open;
    weights[SAND] = (1.0 - vegetation) * cover.arid * (1.0 - 0.6 * crest) * open;
    weights[ROCK] = max(max(steep, cliff), (1.0 - vegetation) * max(0.6 * crest, 0.3 + 0.2 * n.y) * open);
    weights[SNOW] = snow * 2.0;
    weights[SCREE] = scree * (1.0 - 0.7 * vegetation) * open * 1.4;

    for (int i = 0; i < MATERIALS; i++) {

        if (i != ROCK && i != SCREE) {

            weights[i] *= 1.0 - steep;

        }

    }

    weights[SCREE] *= 1.0 - cliff;

    // Flat ground in the band over the water is sand, save under snow; flat ground under it is the bed.
    float flat = saturate((lie.upness - 0.9) / 0.05);
    float beach = lie.beach * flat * (1.0 - snow);
    float bed = lie.bed * flat;
    float sandy = Sandiness(cover.arid, warmth);

    for (int j = 0; j < MATERIALS; j++) {

        weights[j] *= (1.0 - beach) * (1.0 - bed);

    }

    weights[SAND] += 1.5 * (beach + bed * sandy);
    weights[SOIL] += 1.5 * bed * (1.0 - sandy);
    weights[SNOW] *= 1.0 - lie.bed;

}

// A material's colour under a climate; forest's is its canopy, seen from above.
float3 MaterialColour(int material, GroundCover cover, float warmth) {

    if (material == GRASS) {

        return lerp(lerp(TUNDRA_GRASS, LUSH_GRASS, warmth), DRY_GRASS, cover.arid);

    }

    if (material == FOREST) {

        return lerp(lerp(BOREAL_CANOPY, TROPICAL_CANOPY, warmth), DRY_CANOPY, cover.arid);

    }

    if (material == SOIL) {

        return lerp(COLD_SOIL, LATERITE, warmth * warmth * (1.0 - cover.arid));

    }

    if (material == SAND) {

        return DESERT_SAND;

    }

    if (material == SNOW) {

        return Averages[SNOW];

    }

    return lerp(GREY_ROCK, DESERT_ROCK, cover.arid * warmth);

}

// How far the climate recolours a material's texture from its own mean. The forest floor under the trees keeps its own.
float3 Tint(int material, GroundCover cover, float warmth) {

    return material == FOREST ? 1.0 : MaterialColour(material, cover, warmth) / Averages[material];

}

// The mean colour of materials in these proportions.
float3 Palette(float weights[MATERIALS], GroundCover cover, float warmth) {

    float3 colour = 0.0;
    float total = 0.0;

    for (int i = 0; i < MATERIALS; i++) {

        colour += MaterialColour(i, cover, warmth) * weights[i];
        total += weights[i];

    }

    return colour / max(total, 1e-4);

}

// The colour of level, unremarkable ground under a cover, as the light it bounces sees it.
float3 CoverColour(GroundCover cover, float warmth) {

    Lie level;
    level.upness = 1.0;
    level.convexity = 0.0;
    level.noise = 0.5;
    level.beach = 0.0;
    level.bed = 0.0;

    float weights[MATERIALS];
    MaterialWeights(cover, level, warmth, weights);

    return Palette(weights, cover, warmth);

}

// Living grass blades, brighter than the mat of thatch and soil the ground averages to.
float3 BladeColour(float arid, float warmth) {

    GroundCover cover = (GroundCover)0;
    cover.arid = arid;

    return 1.3 * MaterialColour(GRASS, cover, warmth);

}

// A tree's foliage, a sunlit crown brighter than the canopy with its gaps and shade: broadleaves by a third and
// yellower, conifers a little darker and bluer. Two random numbers in [0, 1) stray each tree from its neighbours in
// brightness and hue, as a real stand does.
float3 FoliageColour(float arid, float warmth, bool needles, float2 random) {

    GroundCover cover = (GroundCover)0;
    cover.arid = arid;

    float3 canopy = MaterialColour(FOREST, cover, warmth);
    float luma = max(dot(canopy, float3(0.2126, 0.7152, 0.0722)), 1e-4);
    float3 hue = lerp(canopy / luma, needles ? float3(0.62, 1.1, 0.62) : float3(0.78, 1.15, 0.42), 0.55);
    float3 swing = float3(1.0, 0.0, -1.0) * 0.15 * (random.y - 0.5);

    return luma * (needles ? 0.9 : 1.3) * lerp(0.7, 1.3, random.x) * hue * (1.0 + swing);

}

#endif
