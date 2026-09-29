// Procedural rock instances from Rocks, shared by every body's rock shader.
#ifndef MAXQ_ROCK_INSTANCES_INCLUDED
#define MAXQ_ROCK_INSTANCES_INCLUDED

struct RockVertex {

    float3 position;
    float3 normal;

};

// Per instance: position and size (km); orientation; shape pick, two ground values (PatchStrewJob), outcrop flag.
StructuredBuffer<RockVertex> _RockVertices;
StructuredBuffer<float4> _RockInstances;
uint _RockShapeVertices;
uint _RockInstanceOffset;

#define ROCK_SHAPES 12

// Set by URP while it renders a shadow cascade.
float3 _LightDirection;

struct RockInstance {

    float3 positionWS;
    float3 positionOS;
    float3 normalOS;
    float4 turn;
    float4 pick;
    float3 placed;
    float scale;

};

float3 Rotate(float4 q, float3 v) {

    return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);

}

RockInstance LoadRock(uint vertex, uint instance) {

    uint index = 3 * (instance + _RockInstanceOffset);
    float4 placed = _RockInstances[index];
    float4 turn = _RockInstances[index + 1];
    float4 pick = _RockInstances[index + 2];
    uint shape = min((uint)(pick.x * ROCK_SHAPES), ROCK_SHAPES - 1u);
    RockVertex v = _RockVertices[shape * _RockShapeVertices + vertex];

    RockInstance rock;
    rock.positionWS = placed.xyz + Rotate(turn, v.position * placed.w);
    rock.positionOS = v.position;
    rock.normalOS = v.normal;
    rock.turn = turn;
    rock.pick = pick;
    rock.placed = placed.xyz;

    rock.scale = placed.w * 1000.0;

    return rock;

}

// Depth bias only: normal bias at far-cascade texel sizes would turn a rock inside out.
float4 RockShadowVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID) : SV_POSITION {

    return ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(LoadRock(vertex, instance).positionWS, 0.0, _LightDirection)));

}

#endif
