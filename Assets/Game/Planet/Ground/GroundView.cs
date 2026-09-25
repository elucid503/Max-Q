using System;
using System.Collections.Generic;

using MaxQ.Game.Map;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

using Terrain = MaxQ.Sim.Surface.Terrain;

using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Ground;

/// <summary>Draws a surveyed body as a cube-sphere quadtree of patches (CDLOD): each frame it picks the nodes the camera
/// needs, geomorphs between levels in the shader, and builds missing patches with Burst jobs from the sim's terrain.</summary>
public sealed class GroundView : IDisposable {

    public const int MaxDepth = 16;

    // A node splits once the camera is within Range of its children's size; 5.5 keeps each finer level
    // inside the coarser level's unmorphed band, so neighbouring levels always meet on shared edges.
    private const double Range = 5.5;

    // Farthest a shadow reaches onto the view: the tallest ground under a sun two degrees up.
    private const double ShadowReach = 60_000.0;
    private const double MorphStart = 0.85;

    private const int BuildSlots = 48;
    private const int BuildsPerFrame = 32;
    private const int Capacity = 2_400;

    // Deepest trench on Terra, below which nothing can hide the horizon.
    private const double LowestGround = -2_300.0;

    private static readonly int ColourId = Shader.PropertyToID("_Colour");
    private static readonly int ColourRectId = Shader.PropertyToID("_ColourRect");
    private static readonly int DetailId = Shader.PropertyToID("_Detail");
    private static readonly int ParentDetailId = Shader.PropertyToID("_ParentDetail");
    private static readonly int ParentRectId = Shader.PropertyToID("_ParentRect");
    private static readonly int LevelId = Shader.PropertyToID("_Level");
    private static readonly int TileOriginNearId = Shader.PropertyToID("_TileOriginNear");
    private static readonly int TileOriginFarId = Shader.PropertyToID("_TileOriginFar");
    private static readonly int WaveOriginId = Shader.PropertyToID("_WaveOrigin");

    // Repeats of the ground materials in metres; must match GroundMaterials.hlsl.
    private const double NearTile = 3.0;
    private const double FarTile = 17.0;

    // Metres over which every wave train repeats; must match WAVE_PERIOD in Ground.hlsl.
    private const double WavePeriod = 128.0;
    private static readonly int MorphId = Shader.PropertyToID("_GroundMorph");
    private static readonly int GroundCameraId = Shader.PropertyToID("_GroundCamera");

    private sealed class Node {

        public readonly int Face;
        public readonly int Depth;
        public readonly int X;
        public readonly int Y;
        public readonly Node Parent;
        public readonly Vector3d Direction;

        public Node[] Children;
        public Patch Patch;
        public bool Building;
        public double MinHeight;
        public double MaxHeight;
        public double Radius;
        public int LastUsed;

        public Node(int face, int depth, int x, int y, Node parent, double bodyRadius) {

            Face = face;
            Depth = depth;
            X = x;
            Y = y;
            Parent = parent;
            Direction = PatchJob.CentreDirection(face, depth, x, y);

            double span = 2.0 / (1L << depth);
            double a = x * span - 1.0;
            double b = y * span - 1.0;

            // Until built, a node borrows its parent's heights, padded for the finer relief it will show.
            MinHeight = parent?.MinHeight - 50.0 ?? LowestGround;
            MaxHeight = parent?.MaxHeight + 50.0 ?? 2_000.0;

            double corner = 0.0;

            foreach ((double ca, double cb) in new[] { (a, b), (a + span, b), (a, b + span), (a + span, b + span) }) {

                corner = Math.Max(corner, (CubeFace.Direction(face, ca, cb) - Direction).Length * bodyRadius);

            }

            Radius = corner + 0.5 * (MaxHeight - MinHeight);

        }

        public Vector3d Middle(double bodyRadius) => Direction * (bodyRadius + 0.5 * (MinHeight + MaxHeight));

    }

    private sealed class Patch {

        public GameObject Object;
        public Transform Transform;
        public MeshRenderer Renderer;
        public Mesh Mesh;
        public Texture2D Detail;
        public Material Ground;
        public Material Water;
        public Material[] GroundOnly;
        public Material[] Both;
        public Texture2D Colour;
        public Vector3d Centre;
        public NativeArray<byte> Horizons;
        public float4[] Rocks;

    }

    private sealed class Build {

        public Node Node;
        public JobHandle Handle;
        public Mesh.MeshDataArray Data;
        public NativeArray<ushort> Detail;
        public NativeArray<double> Info;
        public NativeArray<byte> Horizons;
        public NativeArray<float4> Rocks;

    }

    private readonly CelestialBody _body;
    private readonly Terrain _terrain;
    private readonly ColourTiles _tiles;
    private readonly Material _groundTemplate;
    private readonly Material _waterTemplate;
    private readonly Transform _root;

    private readonly Node[] _roots = new Node[6];
    private readonly Build[] _builds = new Build[BuildSlots];
    private readonly Stack<Patch> _sparePatches = new Stack<Patch>();
    private readonly List<Patch> _patches = new List<Patch>();
    private readonly NativeArray<byte> _noHorizons = new NativeArray<byte>(0, Allocator.Persistent);
    private readonly List<Node> _shown = new List<Node>();
    private readonly List<Node> _selected = new List<Node>();
    private readonly List<Node> _requests = new List<Node>();
    private readonly List<Node> _built = new List<Node>();
    private readonly List<Node> _strewn = new List<Node>();
    private readonly Plane[] _planes = new Plane[6];
    private readonly Rocks _rocks;

    private Vector3d _camera;
    private Vector3 _sunward;
    private int _frame;
    private int _patchCount;

    public CelestialBody Body => _body;

    /// <summary>Keeps selecting and streaming but draws nothing; the capture uses it to time the ground.</summary>
    public bool Hidden { get; set; }

    /// <summary>Patches waiting to be built plus colour tiles still streaming; zero once the view has settled.</summary>
    public int Pending => _requests.Count + InFlight + _tiles.Pending;

    public int PatchCount => _patchCount;

    public int ShownCount => _shown.Count;

    /// <summary>Metres from the camera down to the ground beneath it, as of the last draw.</summary>
    public double CameraAltitude { get; private set; }

    private int InFlight {

        get {

            int count = 0;

            foreach (Build build in _builds) {

                count += build.Node != null ? 1 : 0;

            }

            return count;

        }

    }

    public GroundView(CelestialBody body, ColourTiles tiles, Material ground, Material water, Material rock) {

        _body = body;
        _rocks = new Rocks(rock);
        _terrain = body.Terrain ?? throw new ArgumentException($"{body.Name} has no terrain", nameof(body));
        _tiles = tiles;
        _groundTemplate = ground;
        _waterTemplate = water;
        _root = new GameObject(body.Name).transform;

        for (int i = 0; i < BuildSlots; i++) {

            _builds[i] = new Build {

                Detail = new NativeArray<ushort>(PatchJob.Texels * PatchJob.Texels * 4, Allocator.Persistent),
                Info = new NativeArray<double>(PatchJob.InfoLength, Allocator.Persistent),
                Horizons = new NativeArray<byte>(PatchJob.HorizonLength, Allocator.Persistent),
                Rocks = new NativeArray<float4>(PatchJob.RockLength, Allocator.Persistent),

            };

        }

        Vector4[] morph = new Vector4[MaxDepth + 1];

        for (int depth = 0; depth <= MaxDepth; depth++) {

            double end = RangeOf(depth) / MapSpace.MetresPerUnit;
            double start = end * MorphStart;

            morph[depth] = new Vector4((float)start, (float)(1.0 / (end - start)), 0.0f, 0.0f);

        }

        Shader.SetGlobalVectorArray(MorphId, morph);

        for (int face = 0; face < 6; face++) {

            _roots[face] = new Node(face, 0, 0, 0, null, _terrain.Radius);
            Schedule(_roots[face], _builds[face]);

        }

        _tiles.LoadPinned();
        JobHandle.ScheduleBatchedJobs();
        Collect(wait: true);

    }

    private double RangeOf(int depth) => Range * _terrain.Radius * 0.5 * Math.PI / (1L << depth);

    /// <summary><paramref name="sunward"/> is the scene direction toward the sun, used to keep shadow casters that are off screen.</summary>
    public void Draw(double time, Camera camera, Vector3 sunward) {

        _frame++;

        Vector3d bodyPosition = _body.PositionAt(time);
        Vector3 cameraScene = camera.transform.position;
        Vector3d cameraSim = MapSpace.Origin + new Vector3d(cameraScene.x, cameraScene.z, cameraScene.y) * MapSpace.MetresPerUnit;

        _camera = _body.ToBodyFixed(cameraSim - bodyPosition, time);
        _sunward = sunward * (float)(ShadowReach / MapSpace.MetresPerUnit);
        GeometryUtility.CalculateFrustumPlanes(camera, _planes);
        Shader.SetGlobalVector(GroundCameraId, cameraScene);

        double cameraDistance = _camera.Length;
        CameraAltitude = cameraDistance - _terrain.Radius - _terrain.HeightAt(_camera / cameraDistance, Math.Max(cameraDistance - _terrain.Radius, 1.0));

        Collect(wait: false);

        _selected.Clear();
        _requests.Clear();
        _strewn.Clear();

        foreach (Node root in _roots) {

            Select(root, time, bodyPosition);

        }

        Show(time, bodyPosition);
        Strew(time, bodyPosition, cameraScene);
        ScheduleRequests();
        _tiles.Update();

        if (_patchCount > Capacity) {

            Evict();

        }

    }

    private void Select(Node node, double time, Vector3d bodyPosition) {

        node.LastUsed = _frame;

        if (!Visible(node, time, bodyPosition)) {

            return;

        }

        if (node.Depth == PatchJob.RockDepth && node.Patch?.Rocks != null) {

            _strewn.Add(node);

        }

        // Shadow casters off screen split like the rest: a coarse caster stands clear of the fine ground and shadows it.
        if (node.Depth < MaxDepth && Distance(node) < RangeOf(node.Depth + 1)) {

            node.Children ??= new[] {

                new Node(node.Face, node.Depth + 1, 2 * node.X, 2 * node.Y, node, _terrain.Radius),
                new Node(node.Face, node.Depth + 1, 2 * node.X + 1, 2 * node.Y, node, _terrain.Radius),
                new Node(node.Face, node.Depth + 1, 2 * node.X, 2 * node.Y + 1, node, _terrain.Radius),
                new Node(node.Face, node.Depth + 1, 2 * node.X + 1, 2 * node.Y + 1, node, _terrain.Radius),

            };

            bool ready = true;

            foreach (Node child in node.Children) {

                child.LastUsed = _frame;

                if (child.Patch == null) {

                    ready = false;

                    if (!child.Building) {

                        _requests.Add(child);

                    }

                }

            }

            if (ready) {

                foreach (Node child in node.Children) {

                    Select(child, time, bodyPosition);

                }

                return;

            }

        }

        _selected.Add(node);

    }

    private double Distance(Node node) => (_camera - node.Middle(_terrain.Radius)).Length - node.Radius;

    // Behind the horizon of the lowest possible ground, or outside the view frustum even with its shadow swept along.
    private bool Visible(Node node, double time, Vector3d bodyPosition) {

        double occluder = _terrain.Radius + LowestGround;
        double cameraDistance = _camera.Length;

        if (cameraDistance > occluder) {

            double top = _terrain.Radius + node.MaxHeight;
            double cameraHorizon = Math.Acos(occluder / cameraDistance);
            double nodeHorizon = top > occluder ? Math.Acos(occluder / top) : 0.0;
            double angle = Math.Acos(Math.Clamp(Vector3d.Dot(_camera / cameraDistance, node.Direction), -1.0, 1.0));

            if (angle - node.Radius / _terrain.Radius > cameraHorizon + nodeHorizon) {

                return false;

            }

        }

        Vector3 centre = MapSpace.ToScene(bodyPosition + _body.FromBodyFixed(node.Middle(_terrain.Radius), time));
        float radius = (float)(node.Radius / MapSpace.MetresPerUnit);

        foreach (Plane plane in _planes) {

            if (plane.GetDistanceToPoint(centre) < -radius && plane.GetDistanceToPoint(centre - _sunward) < -radius) {

                return false;

            }

        }

        return true;

    }

    private void Show(double time, Vector3d bodyPosition) {

        foreach (Node node in _shown) {

            if (node.Patch != null) {

                node.Patch.Renderer.enabled = false;

            }

        }

        Quaternion rotation = Quaternion.AngleAxis(-(float)(_body.RotationAt(time) * 180.0 / Math.PI), Vector3.up);

        foreach (Node node in _selected) {

            Patch patch = node.Patch;

            patch.Renderer.enabled = !Hidden;
            patch.Transform.SetPositionAndRotation(MapSpace.ToScene(bodyPosition + _body.FromBodyFixed(patch.Centre, time)), rotation);

            Texture2D colour = _tiles.Resolve(node.Face, node.Depth, node.X, node.Y, out int level);

            if (colour != null && colour != patch.Colour) {

                int shift = node.Depth - level;
                double scale = 1.0 / (1L << shift);
                double u = (node.X & ((1 << shift) - 1)) * scale;
                double v = (node.Y & ((1 << shift) - 1)) * scale;
                Vector4 rect = new Vector4(

                    (float)(ColourTiles.Core * scale / ColourTiles.Texels),
                    (float)(ColourTiles.Core * scale / ColourTiles.Texels),
                    (float)((ColourTiles.Border + ColourTiles.Core * u) / ColourTiles.Texels),
                    (float)((ColourTiles.Border + ColourTiles.Core * v) / ColourTiles.Texels)

                );

                patch.Colour = colour;
                patch.Ground.SetTexture(ColourId, colour);
                patch.Ground.SetVector(ColourRectId, rect);
                patch.Water.SetTexture(ColourId, colour);
                patch.Water.SetVector(ColourRectId, rect);

            }

        }

        _shown.Clear();
        _shown.AddRange(_selected);

    }

    // Boulders of the strewing level's visible patches, whether those patches are drawn or their finer children are.
    private void Strew(double time, Vector3d bodyPosition, Vector3 camera) {

        if (Hidden) {

            return;

        }

        Quaternion rotation = Quaternion.AngleAxis(-(float)(_body.RotationAt(time) * 180.0 / Math.PI), Vector3.up);

        foreach (Node node in _strewn) {

            Patch patch = node.Patch;
            Vector3 position = MapSpace.ToScene(bodyPosition + _body.FromBodyFixed(patch.Centre, time));

            _rocks.Add(patch.Rocks, patch.Rocks.Length / 2, position, rotation, camera);

        }

        _rocks.Draw();

    }

    // Coarse levels first, then nearest: the view fills in top-down and never waits on a far patch.
    private void ScheduleRequests() {

        _requests.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : Distance(a).CompareTo(Distance(b)));

        int scheduled = 0;

        foreach (Node node in _requests) {

            if (scheduled == BuildsPerFrame) {

                break;

            }

            Build slot = Array.Find(_builds, b => b.Node == null);

            if (slot == null) {

                break;

            }

            Schedule(node, slot);
            scheduled++;

        }

        JobHandle.ScheduleBatchedJobs();

    }

    private void Schedule(Node node, Build slot) {

        slot.Node = node;
        slot.Data = Mesh.AllocateWritableMeshData(1);
        PatchJob.Prepare(slot.Data[0]);
        node.Building = true;

        slot.Handle = new PatchJob {

            Terrain = _terrain,
            Face = node.Face,
            Depth = node.Depth,
            X = node.X,
            Y = node.Y,
            Mesh = slot.Data[0],
            Detail = slot.Detail,
            Info = slot.Info,
            Horizons = slot.Horizons,
            Rocks = slot.Rocks,
            ParentHorizons = node.Parent?.Patch.Horizons ?? _noHorizons,

        }.Schedule();

    }

    private void Collect(bool wait) {

        foreach (Build slot in _builds) {

            if (slot.Node == null || (!wait && !slot.Handle.IsCompleted)) {

                continue;

            }

            slot.Handle.Complete();

            Node node = slot.Node;
            Patch patch = _sparePatches.Count > 0 ? _sparePatches.Pop() : CreatePatch();

            Mesh.ApplyAndDisposeWritableMeshData(slot.Data, patch.Mesh, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            patch.Mesh.bounds = patch.Mesh.GetSubMesh(0).bounds;

            patch.Horizons.CopyFrom(slot.Horizons);

            int rocks = (int)slot.Info[PatchJob.InfoRocks];
            patch.Rocks = rocks > 0 ? slot.Rocks.GetSubArray(0, 2 * rocks).ToArray() : null;

            patch.Detail.SetPixelData(slot.Detail, 0);
            patch.Detail.Apply(false, false);

            node.MinHeight = slot.Info[PatchJob.InfoMinHeight];
            node.MaxHeight = slot.Info[PatchJob.InfoMaxHeight];
            node.Radius = slot.Info[PatchJob.InfoRadius];
            node.Building = false;
            node.Patch = patch;

            patch.Centre = node.Direction * _terrain.Radius;
            patch.Object.name = $"{node.Face}/{node.Depth}/{node.X},{node.Y}";
            patch.Renderer.sharedMaterials = slot.Info[PatchJob.InfoWaterIndices] > 0 ? patch.Both : patch.GroundOnly;
            patch.Colour = null;

            Patch parent = node.Parent?.Patch ?? patch;
            Vector4 parentRect = node.Parent == null ? new Vector4(1.0f, 1.0f, 0.0f, 0.0f) : new Vector4(0.5f, 0.5f, 0.5f * (node.X & 1), 0.5f * (node.Y & 1));

            Vector4 near = TileOrigin(patch.Centre, NearTile);
            Vector4 far = TileOrigin(patch.Centre, FarTile);
            Vector4 waves = TileOrigin(patch.Centre, WavePeriod);

            foreach (Material material in patch.Both) {

                material.SetVector(TileOriginNearId, near);
                material.SetVector(TileOriginFarId, far);
                material.SetVector(WaveOriginId, waves);
                material.SetTexture(DetailId, patch.Detail);
                material.SetTexture(ParentDetailId, parent.Detail);
                material.SetVector(ParentRectId, parentRect);
                material.SetFloat(LevelId, node.Depth);

            }

            slot.Node = null;
            _patchCount++;

        }

    }

    // Where the patch's centre falls within a material repeat, in object axes; the shader adds the small local offset,
    // so textures stay put and seamless in single precision anywhere on the planet.
    private static Vector4 TileOrigin(Vector3d centre, double tile) {

        static float Fraction(double v, double tile) => (float)(v / tile - Math.Floor(v / tile));

        return new Vector4(Fraction(centre.X, tile), Fraction(centre.Z, tile), Fraction(centre.Y, tile), 0.0f);

    }

    private Patch CreatePatch() {

        GameObject go = new GameObject("Patch");
        go.transform.SetParent(_root, false);

        Patch patch = new Patch {

            Object = go,
            Transform = go.transform,
            Mesh = new Mesh { name = "Ground Patch" },
            Detail = new Texture2D(PatchJob.Texels, PatchJob.Texels, GraphicsFormat.R16G16B16A16_UNorm, TextureCreationFlags.None) {

                name = "Ground Detail",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,

            },
            Ground = new Material(_groundTemplate),
            Water = new Material(_waterTemplate),
            Horizons = new NativeArray<byte>(PatchJob.HorizonLength, Allocator.Persistent),

        };

        _patches.Add(patch);

        patch.GroundOnly = new[] { patch.Ground };
        patch.Both = new[] { patch.Ground, patch.Water };

        go.AddComponent<MeshFilter>().sharedMesh = patch.Mesh;
        patch.Renderer = go.AddComponent<MeshRenderer>();
        patch.Renderer.shadowCastingMode = ShadowCastingMode.On;
        patch.Renderer.receiveShadows = false;
        patch.Renderer.lightProbeUsage = LightProbeUsage.Off;
        patch.Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        patch.Renderer.enabled = false;

        return patch;

    }

    // Oldest first, whole subtrees at a time, so a patch never outlives the parent whose detail it blends toward.
    private void Evict() {

        _built.Clear();

        foreach (Node root in _roots) {

            Gather(root);

        }

        _built.Sort((a, b) => a.LastUsed != b.LastUsed ? a.LastUsed.CompareTo(b.LastUsed) : b.Depth.CompareTo(a.Depth));

        foreach (Node node in _built) {

            if (_patchCount <= Capacity * 9 / 10 || node.LastUsed >= _frame) {

                break;

            }

            Release(node);

        }

    }

    private void Gather(Node node) {

        if (node.Children == null) {

            return;

        }

        foreach (Node child in node.Children) {

            if (child.Patch != null && child.Depth > 0) {

                _built.Add(child);

            }

            Gather(child);

        }

    }

    private void Release(Node node) {

        if (node.Children != null) {

            foreach (Node child in node.Children) {

                if (child.Building) {

                    return;

                }

            }

            foreach (Node child in node.Children) {

                Release(child);

            }

            node.Children = null;

        }

        if (node.Patch == null) {

            return;

        }

        node.Patch.Renderer.enabled = false;
        _sparePatches.Push(node.Patch);
        node.Patch = null;
        _patchCount--;

    }

    public void Dispose() {

        foreach (Build slot in _builds) {

            if (slot.Node != null) {

                slot.Handle.Complete();
                slot.Data.Dispose();

            }

            slot.Detail.Dispose();
            slot.Info.Dispose();
            slot.Horizons.Dispose();
            slot.Rocks.Dispose();

        }

        foreach (Patch patch in _patches) {

            patch.Horizons.Dispose();

        }

        _noHorizons.Dispose();

        _tiles.Dispose();

    }

}
