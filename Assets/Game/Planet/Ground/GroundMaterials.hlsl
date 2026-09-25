// Close to the camera, the satellite colour hands over to tiled ground materials: grass, forest floor, soil, sand,
// rock and snow, chosen from the satellite's tone and the slope, and recoloured toward the satellite so the handover
// never shifts the colour the ground had from further away.
#ifndef MAXQ_GROUND_MATERIALS_INCLUDED
#define MAXQ_GROUND_MATERIALS_INCLUDED

#include "Ground.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

TEXTURE2D_ARRAY(_GroundAlbedo);
TEXTURE2D_ARRAY(_GroundNormal);
SAMPLER(sampler_GroundAlbedo);

#define GRASS 0
#define FOREST 1
#define SOIL 2
#define SAND 3
#define ROCK 4
#define SNOW 5
#define MATERIALS 6

// Metres per repeat of the near and far samples; the far one breaks up the near one's repetition.
#define NEAR_TILE 3.0
#define FAR_TILE 17.0

// Metres from the camera over which the materials fade out, leaving the satellite colour alone.
#define DETAIL_START 1500.0
#define DETAIL_END 4000.0

struct Layer {

    float3 albedo;
    float3 normalOS;
    float height;

};

// Triplanar in the body-fixed frame, skipping planes the surface barely faces; normals by whiteout blending.
Layer Triplanar(int slice, float3 position, float3 normal, float3 weights) {

    Layer layer;
    layer.albedo = 0.0;
    layer.normalOS = 0.0;
    layer.height = 0.0;

    UNITY_BRANCH
    if (weights.x > 0.02) {

        float4 albedo = SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, position.zy, slice);
        float3 t = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, position.zy, slice));

        t = float3(t.xy + normal.zy, abs(t.z) * normal.x);
        layer.albedo += albedo.rgb * weights.x;
        layer.height += albedo.a * weights.x;
        layer.normalOS += t.zyx * weights.x;

    }

    UNITY_BRANCH
    if (weights.y > 0.02) {

        float4 albedo = SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, position.xz, slice);
        float3 t = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, position.xz, slice));

        t = float3(t.xy + normal.xz, abs(t.z) * normal.y);
        layer.albedo += albedo.rgb * weights.y;
        layer.height += albedo.a * weights.y;
        layer.normalOS += t.xzy * weights.y;

    }

    UNITY_BRANCH
    if (weights.z > 0.02) {

        float4 albedo = SAMPLE_TEXTURE2D_ARRAY(_GroundAlbedo, sampler_GroundAlbedo, position.xy, slice);
        float3 t = UnpackNormal(SAMPLE_TEXTURE2D_ARRAY(_GroundNormal, sampler_GroundAlbedo, position.xy, slice));

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

// One material at both scales: the near sample carries the grain, the far one modulates it.
Layer SampleMaterial(int slice, float3 positionOS, float3 normalOS, float3 weights) {

    float3 metres = positionOS * 1000.0;
    Layer near = Triplanar(slice, metres / NEAR_TILE + _TileOriginNear.xyz, normalOS, weights);
    Layer far = Triplanar(slice, metres / FAR_TILE + _TileOriginFar.xyz, normalOS, weights);
    float3 average = SAMPLE_TEXTURE2D_ARRAY_LOD(_GroundAlbedo, sampler_GroundAlbedo, float2(0.5, 0.5), slice, 12.0).rgb;

    Layer layer;
    layer.albedo = near.albedo * lerp(1.0, far.albedo / max(average, 1e-3), 0.5);
    layer.height = saturate(near.height * 0.7 + far.height * 0.3);
    layer.normalOS = normalize(near.normalOS + (far.normalOS - normalOS) * 0.5);

    return layer;

}

// How much each material suits a spot, from the satellite's tone (linear) and how steep the ground is.
void MaterialWeights(float3 tone, float upness, out float weights[MATERIALS]) {

    float sum = max(tone.r + tone.g + tone.b, 1e-4);
    float luma = dot(tone, float3(0.2126, 0.7152, 0.0722));
    float chroma = (max(tone.r, max(tone.g, tone.b)) - min(tone.r, min(tone.g, tone.b))) / max(max(tone.r, max(tone.g, tone.b)), 1e-4);

    float vegetation = saturate((tone.g / sum - 0.36) / 0.08);
    float canopy = saturate((0.07 - luma) / 0.04);
    float bright = saturate((luma - 0.18) / 0.15);
    float snow = saturate((luma - 0.40) / 0.15) * saturate(1.0 - chroma * 3.0);
    float steep = saturate((0.82 - upness) / 0.12);
    float bare = saturate(1.0 - chroma * 4.0) * (1.0 - vegetation);

    snow *= saturate((upness - 0.55) / 0.15);

    weights[GRASS] = vegetation * (1.0 - canopy);
    weights[FOREST] = vegetation * canopy;
    weights[SOIL] = (1.0 - vegetation) * (1.0 - bright) * 0.8;
    weights[SAND] = (1.0 - vegetation) * bright * saturate((tone.r / sum - 0.36) / 0.06);
    weights[ROCK] = max(steep, bare * 0.6);
    weights[SNOW] = snow * 2.0;

    for (int i = 0; i < MATERIALS; i++) {

        if (i != ROCK) {

            weights[i] *= 1.0 - steep;

        }

    }

}

struct GroundSurface {

    float3 albedo;
    float3 normalWS;

};

GroundSurface GroundMaterial(GroundVaryings input, GroundDetail detail, float3 up) {

    GroundSurface surface;
    surface.albedo = SatelliteColour(input.uv);
    surface.normalWS = detail.normalWS;

    float fade = 1.0 - smoothstep(DETAIL_START, DETAIL_END, distance(input.positionWS, _WorldSpaceCameraPos) * 1000.0);

    UNITY_BRANCH
    if (fade <= 0.0) {

        return surface;

    }

    float weights[MATERIALS];
    MaterialWeights(SatelliteTone(input.uv), dot(detail.normalWS, up), weights);

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
    planes /= planes.x + planes.y + planes.z;

    Layer a = SampleMaterial(first, input.positionOS, detail.normalOS, planes);
    Layer b = SampleMaterial(second, input.positionOS, detail.normalOS, planes);

    float wa = weights[first] / max(weights[first] + weights[second], 1e-4);
    float ha = a.height + wa;
    float hb = b.height + 1.0 - wa;
    float top = max(ha, hb) - 0.2;
    float ba = max(ha - top, 0.0);
    float bb = max(hb - top, 0.0);
    float blend = ba / max(ba + bb, 1e-4);

    float3 albedo = lerp(b.albedo, a.albedo, blend);
    float3 average = lerp(
        SAMPLE_TEXTURE2D_ARRAY_LOD(_GroundAlbedo, sampler_GroundAlbedo, float2(0.5, 0.5), second, 12.0).rgb,
        SAMPLE_TEXTURE2D_ARRAY_LOD(_GroundAlbedo, sampler_GroundAlbedo, float2(0.5, 0.5), first, 12.0).rgb, blend);
    float3 transfer = clamp(surface.albedo / max(average, 1e-3), 0.25, 4.0);

    surface.albedo = lerp(surface.albedo, albedo * lerp(1.0, transfer, 0.8), fade);
    surface.normalWS = normalize(lerp(detail.normalWS, TransformObjectToWorldNormal(normalize(lerp(b.normalOS, a.normalOS, blend))), fade));

    return surface;

}

#endif
