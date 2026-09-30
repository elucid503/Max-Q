using System;
using System.Collections.Generic;

using MaxQ.Game.Map;
using MaxQ.Game.Planet.Ground.Plants;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

using Terrain = MaxQ.Sim.Surface.Terrain;

using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Ground;

/// <summary>Draws a surveyed body as a cube-sphere quadtree of patches (CDLOD): each frame it picks the nodes the camera
/// needs, geomorphs between levels in the shader, and builds missing patches with Burst jobs from the sim's terrain.
/// Beyond the depth range it draws scaled down about the eye as a stand-in, lit where it truly is.</summary>
public sealed class GroundView : IDisposable {

    // Approximate finest quad size (m); cratered ground needs its small craters in the mesh.
    private const double FinestQuad = 1.0;
    private const double FinestCrateredQuad = 0.5;

    // A node splits once the camera is within Range of its children's size; 5.5 keeps each finer level
    // inside the coarser level's unmorphed band, so neighbouring levels always meet on shared edges.
    private const double Range = 5.5;

    // Farthest a shadow reaches onto the view: the tallest ground under a sun two degrees up.
    private const double ShadowReach = 60_000.0;
    private const double MorphStart = 0.85;

    // Patch textures filter across this many texels along the view where the ground is seen edge-on.
    private const int Anisotropy = 4;

    // A patch whose water quads the view sees edge-on, thinner than this many pixels, draws its sheet at half
    // resolution: finer triangles would shade most of their pixels twice over.
    private const double WaterEdgeOn = 2.0;

    // Each build spreads across every worker, but a slot hands over at most one patch a frame however soon its build
    // finishes, so enough are in flight for the view to fill within a second or two; more would only queue ahead of the
    // engine's own jobs. The six roots build together before the first frame.
    private static readonly int BuildSlots = Math.Clamp(JobsUtility.JobWorkerCount / 2, 3, 12);
    private const int Capacity = 2_400;

    // Finished builds handed over in one frame: each uploads a mesh and two mipped textures, and a burst of them all
    // landing together (a jump, a fast pass low over the ground) would stall the frame. The rest wait a frame in their slots.
    private const int CollectsPerFrame = 3;

    private static readonly int DetailId = Shader.PropertyToID("_Detail");
    private static readonly int ParentDetailId = Shader.PropertyToID("_ParentDetail");
    private static readonly int CoverId = Shader.PropertyToID("_Cover");
    private static readonly int ParentCoverId = Shader.PropertyToID("_ParentCover");
    private static readonly int ParentRectId = Shader.PropertyToID("_ParentRect");
    private static readonly int LevelId = Shader.PropertyToID("_Level");
    private static readonly int WaterCoarseId = Shader.PropertyToID("_WaterCoarse");
    private static readonly int TileOriginNearId = Shader.PropertyToID("_TileOriginNear");
    private static readonly int TileOriginFarId = Shader.PropertyToID("_TileOriginFar");
    private static readonly int TileOriginMacroId = Shader.PropertyToID("_TileOriginMacro");
    private static readonly int TileOriginBroadId = Shader.PropertyToID("_TileOriginBroad");

    // Repeats of the ground materials in metres; must match GroundMaterials.hlsl.
    private const double NearTile = 3.0;
    private const double FarTile = 17.0;
    private const double MacroTile = 153.0;
    private const double BroadTile = 1_377.0;

    // Repeats before a tile origin wraps; must match TILE_PERIOD in GroundMaterials.hlsl.
    private const double TilePeriod = 64.0;

    private static readonly int MorphId = Shader.PropertyToID("_Morph");
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
        public double Urgency;

        // Whether this frame drew the finer level in the node's place, rather than the node itself.
        public bool Refined;

        public Node(int face, int depth, int x, int y, Node parent, double bodyRadius, double lowest, double highest) {

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
            MinHeight = parent?.MinHeight - 50.0 ?? lowest;
            MaxHeight = parent?.MaxHeight + 50.0 ?? highest;

            double corner = 0.0;

            for (int c = 0; c < 4; c++) {

                corner = Math.Max(corner, (CubeFace.Direction(face, a + span * (c & 1), b + span * (c >> 1)) - Direction).Length * bodyRadius);

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
        public Texture2D Cover;
        public Material Ground;
        public Material Water;
        public Material[] GroundOnly;
        public Material[] Both;
        public Vector3d Centre;
        public NativeArray<byte> Horizons;
        public float4[] Rocks;
        public Vegetation.Plot Plot;
        public SubMeshDescriptor FineWater;
        public SubMeshDescriptor CoarseWater;
        public bool Coarse;
        public bool HasWater;
        public bool SheetShown;

    }

    private sealed class Build {

        public Node Node;
        public JobHandle Handle;
        public Mesh.MeshDataArray Data;
        public NativeArray<ushort> Detail;
        public NativeArray<byte> Cover;
        public NativeArray<double> Info;
        public NativeArray<byte> Horizons;
        public NativeArray<float4> Rocks;
        public NativeArray<float4> Plants;
        public PatchSamples Samples;

    }

    private readonly CelestialBody _body;
    private readonly Terrain _terrain;
    private readonly Material _groundTemplate;
    private readonly Material _waterTemplate;
    private readonly Transform _root;

    private readonly Node[] _roots = new Node[6];
    private readonly Build[] _builds = new Build[Math.Max(BuildSlots, 6)];
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
    private readonly Vegetation _vegetation;
    private readonly Comparison<Node> _byPriority;
    private readonly Vector4[] _morphs;
    private readonly int _standInId;
    private readonly int _standInSphereId;

    private Vector3d _camera;
    private Vector3 _sunward;
    private int _frame;
    private int _patchCount;
    private int _collectFrom;

    public CelestialBody Body => _body;

    /// <summary>The finest level of the quadtree, where quads are about FinestQuad across.</summary>
    public int MaxDepth { get; }

    /// <summary>How far the last draw scaled the body down about the eye to stand within reach; one when drawn true.</summary>
    public double Scale { get; private set; } = 1.0;


    /// <summary>Keeps selecting and streaming but draws nothing; the capture uses it to time the ground.</summary>
    public bool Hidden { get; set; }

    /// <summary>Draws the ground but none of the rocks and plants strewn on it; the capture uses it to time them.</summary>
    public bool StrewHidden { get; set; }

    /// <summary>Patches waiting to be built; zero once the view has settled.</summary>
    public int Pending => _requests.Count + InFlight;

    public int PatchCount => _patchCount;

    public int ShownCount => _shown.Count;

    /// <summary>Metres from the camera down to the ground or water beneath it, as of the last draw.</summary>
    public double CameraAltitude { get; private set; }

    private double _pixelsPerRadian = 1.0;

    private int InFlight {

        get {

            int count = 0;

            foreach (Build build in _builds) {

                count += build.Node != null ? 1 : 0;

            }

            return count;

        }

    }

    /// <summary>Water and vegetation may be null; shaders read the stand-in scale as _&lt;body&gt;StandIn.</summary>
    public GroundView(CelestialBody body, Material ground, Material water, Material rock, Vegetation vegetation) {

        _body = body;
        _rocks = new Rocks(rock);
        _vegetation = vegetation;
        _terrain = body.Terrain ?? throw new ArgumentException($"{body.Name} has no terrain", nameof(body));
        _groundTemplate = ground;
        _waterTemplate = water;
        _root = new GameObject(body.Name).transform;
        _standInId = Shader.PropertyToID($"_{body.Name}StandIn");
        _standInSphereId = Shader.PropertyToID($"_{body.Name}StandInSphere");
        MaxDepth = MaxDepthFor(_terrain);
        Shader.SetGlobalFloat(_standInId, 1.0f);
        Shader.SetGlobalVector(_standInSphereId, Vector4.zero);

        for (int i = 0; i < _builds.Length; i++) {

            _builds[i] = new Build {

                Detail = new NativeArray<ushort>(PatchJob.MipStart(PatchJob.Mips) * 4, Allocator.Persistent),
                Cover = new NativeArray<byte>(PatchJob.MipStart(PatchJob.Mips) * 4, Allocator.Persistent),
                Info = new NativeArray<double>(PatchJob.InfoLength, Allocator.Persistent),
                Horizons = new NativeArray<byte>(PatchJob.HorizonLength, Allocator.Persistent),
                Rocks = new NativeArray<float4>(3 * PatchJob.MaxRocks, Allocator.Persistent),
                Plants = new NativeArray<float4>(PatchStrewJob.PlantLength, Allocator.Persistent),
                Samples = new PatchSamples(),

            };

        }

        // Most urgent first, coarser first among equals, as each unlocks its children.
        _byPriority = (a, b) => a.Urgency != b.Urgency ? a.Urgency.CompareTo(b.Urgency) : a.Depth.CompareTo(b.Depth);

        _morphs = new Vector4[MaxDepth + 1];

        for (int depth = 0; depth <= MaxDepth; depth++) {

            double end = RangeOf(depth) / MapSpace.MetresPerUnit;
            double start = end * MorphStart;

            _morphs[depth] = new Vector4((float)start, (float)(1.0 / (end - start)), 0.0f, 0.0f);

        }

        // A root has no coarser level; morphing would only halve its detail where the whole body is seen from afar.
        _morphs[0] = new Vector4(float.MaxValue, 0.0f, 0.0f, 0.0f);

        for (int face = 0; face < 6; face++) {

            _roots[face] = new Node(face, 0, 0, 0, null, _terrain.Radius, _terrain.Lowest, _terrain.Highest);
            Schedule(_roots[face], _builds[face]);

        }

        JobHandle.ScheduleBatchedJobs();
        Collect(wait: true);

    }

    /// <summary>The finest level of a body's ground.</summary>
    public static int MaxDepthFor(Terrain terrain) =>
        (int)Math.Round(Math.Log(PatchJob.Footprint(terrain.Radius, 0) / (terrain.IsCratered ? FinestCrateredQuad : FinestQuad), 2.0));

    private double RangeOf(int depth) => Range * _terrain.Radius * 0.5 * Math.PI / (1L << depth);

    /// <summary>The body stands in when its centre is farther than <paramref name="reach"/> (scene units); shaders read the
    /// sphere it stands in within, air and highest ground included, as _&lt;body&gt;StandInSphere (zero when drawn true).</summary>
    public void Draw(double time, Camera camera, Vector3 sunward, double reach, double airThickness) {

        _frame++;

        Vector3d bodyPosition = _body.PositionAt(time);
        Vector3 cameraScene = camera.transform.position;
        Vector3d cameraSim = MapSpace.Origin + new Vector3d(cameraScene.x, cameraScene.z, cameraScene.y) * MapSpace.MetresPerUnit;
        double centreDistance = (cameraSim - bodyPosition).Length / MapSpace.MetresPerUnit;

        // The air and clouds tell a stand-in's pixels by their rays meeting its sphere, never by the scene's depth: scaled
        // down so far, its ground and air lie within a sliver of the depth range, which the depth cannot resolve.
        Scale = Math.Min(1.0, reach / centreDistance);
        Shader.SetGlobalFloat(_standInId, (float)Scale);

        Vector3 sphere = StandIn(MapSpace.ToScene(bodyPosition));
        float sphereRadius = (float)(Scale * (_terrain.Radius + Math.Max(_terrain.Highest, 0.0) + airThickness) / MapSpace.MetresPerUnit);

        Shader.SetGlobalVector(_standInSphereId, Scale < 1.0 ? new Vector4(sphere.x, sphere.y, sphere.z, sphereRadius) : Vector4.zero);

        _camera = _body.ToBodyFixed(cameraSim - bodyPosition, time);
        _sunward = sunward * (float)(ShadowReach / MapSpace.MetresPerUnit);
        _pixelsPerRadian = camera.pixelHeight / (2.0 * Math.Tan(camera.fieldOfView * Math.PI / 360.0));
        GeometryUtility.CalculateFrustumPlanes(camera, _planes);
        Shader.SetGlobalVector(GroundCameraId, cameraScene);

        double cameraDistance = _camera.Length;
        double footprint = Math.Max(cameraDistance - _terrain.Radius, 1.0);
        double ground = _terrain.HeightAt(_camera / cameraDistance, footprint);
        double level = _terrain.WaterLevelAt(_camera / cameraDistance, footprint);

        CameraAltitude = cameraDistance - _terrain.Radius - (double.IsNaN(level) ? ground : Math.Max(ground, level));

        Collect(wait: false);

        _selected.Clear();
        _requests.Clear();
        _strewn.Clear();

        foreach (Node root in _roots) {

            Select(root, time, bodyPosition);

        }

        Show(time, bodyPosition);
        Strew(time, bodyPosition, cameraScene);
        ScheduleRequests(time, bodyPosition);

        if (_patchCount > Capacity) {

            Evict();

        }

    }

    private void Select(Node node, double time, Vector3d bodyPosition) {

        node.LastUsed = _frame;
        node.Refined = false;

        if (!Visible(node, time, bodyPosition)) {

            return;

        }

        if (node.Patch != null && (node.Patch.Rocks != null || node.Patch.Plot?.Count > 0)) {

            _strewn.Add(node);

        }

        // Shadow casters off screen split like the rest: a coarse caster stands clear of the fine ground and shadows it.
        if (node.Depth < MaxDepth && Distance(node) < RangeOf(node.Depth + 1)) {

            node.Children ??= new[] {

                new Node(node.Face, node.Depth + 1, 2 * node.X, 2 * node.Y, node, _terrain.Radius, _terrain.Lowest, _terrain.Highest),
                new Node(node.Face, node.Depth + 1, 2 * node.X + 1, 2 * node.Y, node, _terrain.Radius, _terrain.Lowest, _terrain.Highest),
                new Node(node.Face, node.Depth + 1, 2 * node.X, 2 * node.Y + 1, node, _terrain.Radius, _terrain.Lowest, _terrain.Highest),
                new Node(node.Face, node.Depth + 1, 2 * node.X + 1, 2 * node.Y + 1, node, _terrain.Radius, _terrain.Lowest, _terrain.Highest),

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

                node.Refined = true;

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

        double occluder = _terrain.Radius + _terrain.Lowest;
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

        Vector3 centre = StandIn(MapSpace.ToScene(bodyPosition + _body.FromBodyFixed(node.Middle(_terrain.Radius), time)));
        float radius = (float)(Scale * node.Radius / MapSpace.MetresPerUnit);

        foreach (Plane plane in _planes) {

            if (plane.GetDistanceToPoint(centre) < -radius && plane.GetDistanceToPoint(centre - _sunward) < -radius) {

                return false;

            }

        }

        return true;

    }

    private bool InFrustum(Node node, double time, Vector3d bodyPosition) {

        Vector3 centre = StandIn(MapSpace.ToScene(bodyPosition + _body.FromBodyFixed(node.Middle(_terrain.Radius), time)));
        float radius = (float)(Scale * node.Radius / MapSpace.MetresPerUnit);

        foreach (Plane plane in _planes) {

            if (plane.GetDistanceToPoint(centre) < -radius) {

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
        Vector3 scale = Vector3.one * (float)Scale;

        // A stand-in's shadow would fall at its scaled place, so it casts none.
        ShadowCastingMode shadows = Scale < 1.0 ? ShadowCastingMode.Off : ShadowCastingMode.On;

        foreach (Node node in _selected) {

            Patch patch = node.Patch;

            patch.Renderer.enabled = !Hidden;
            patch.Renderer.shadowCastingMode = shadows;

            // A stand-in's seas are shaded by its ground, filtered to the pixel; a sheet so far off would only fight the
            // bed for depth.
            bool sheet = patch.HasWater && Scale >= 1.0;

            if (sheet != patch.SheetShown) {

                patch.Renderer.sharedMaterials = sheet ? patch.Both : patch.GroundOnly;
                patch.SheetShown = sheet;

            }

            ShapeWater(node);
            patch.Transform.SetPositionAndRotation(StandIn(MapSpace.ToScene(bodyPosition + _body.FromBodyFixed(patch.Centre, time))), rotation);
            patch.Transform.localScale = scale;

        }

        _shown.Clear();
        _shown.AddRange(_selected);

    }

    // Draws a patch's sheet at half resolution where the view sees its quads edge-on.
    private void ShapeWater(Node node) {

        Patch patch = node.Patch;

        if (patch.CoarseWater.indexCount == 0) {

            return;

        }

        double distance = Math.Max(Distance(node), 1.0);
        double quad = RangeOf(node.Depth) / Range / PatchJob.Quads;
        bool coarse = quad / distance * _pixelsPerRadian * Math.Min(Math.Max(CameraAltitude, 0.0) / distance, 1.0) < WaterEdgeOn;

        if (coarse != patch.Coarse) {

            patch.Mesh.SetSubMesh(1, coarse ? patch.CoarseWater : patch.FineWater, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            patch.Water.SetFloat(WaterCoarseId, coarse ? 1.0f : 0.0f);
            patch.Coarse = coarse;

        }

    }

    // A scene position scaled toward the eye as the body stands in; the eye is the scene's origin.
    private Vector3 StandIn(Vector3 scene) => scene * (float)Scale;

    // Rocks and plants of the strewing levels' visible patches, whether those patches are drawn or their finer children are.
    // Nothing strewn is ever seen on a stand-in.
    private void Strew(double time, Vector3d bodyPosition, Vector3 camera) {

        if (Hidden || StrewHidden || Scale < 1.0) {

            return;

        }

        Quaternion rotation = Quaternion.AngleAxis(-(float)(_body.RotationAt(time) * 180.0 / Math.PI), Vector3.up);

        foreach (Node node in _strewn) {

            Patch patch = node.Patch;
            Vector3 position = MapSpace.ToScene(bodyPosition + _body.FromBodyFixed(patch.Centre, time));

            if (patch.Rocks != null) {

                _rocks.Add(patch.Rocks, position, rotation, camera);

            }

            if (patch.Plot?.Count > 0) {

                Vector3 middle = MapSpace.ToScene(bodyPosition + _body.FromBodyFixed(node.Middle(_terrain.Radius), time));

                _vegetation?.Draw(patch.Plot, position, rotation, middle, (float)(node.Radius / MapSpace.MetresPerUnit), camera, node.Refined);

            }

        }

        _rocks.Draw();

    }

    // A node is as urgent as the ground drawn in its place is coarse: its distance over the range its level serves, zero
    // wherever the camera stands inside it, so the view refines outward from the camera rather than level by level across
    // the whole view. Nodes whose parent the frustum takes in only for its shadow wait on those the camera sees.
    private void ScheduleRequests(double time, Vector3d bodyPosition) {

        foreach (Node node in _requests) {

            node.Urgency = Math.Max(Distance(node), 0.0) / RangeOf(node.Depth) + (InFrustum(node.Parent, time, bodyPosition) ? 0.0 : 1.0);

        }

        _requests.Sort(_byPriority);

        foreach (Node node in _requests) {

            Build slot = FreeSlot(BuildSlots);

            if (slot == null) {

                break;

            }

            Schedule(node, slot);

        }

        JobHandle.ScheduleBatchedJobs();

    }

    // A slot among the first count with no build in it.
    private Build FreeSlot(int count) {

        for (int i = 0; i < count; i++) {

            if (_builds[i].Node == null) {

                return _builds[i];

            }

        }

        return null;

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
            MaxDepth = MaxDepth,
            X = node.X,
            Y = node.Y,
            Mesh = slot.Data[0],
            Detail = slot.Detail,
            Cover = slot.Cover,
            Info = slot.Info,
            Horizons = slot.Horizons,
            Rocks = slot.Rocks,
            Plants = slot.Plants,
            ParentHorizons = node.Parent?.Patch.Horizons ?? _noHorizons,

        }.ScheduleStages(slot.Samples);

    }

    private void Collect(bool wait) {

        int collected = 0;
        int start = _collectFrom;

        // Round the slots from where the last frame stopped, so none waits behind the others.
        for (int i = 0; i < _builds.Length; i++) {

            if (!wait && collected >= CollectsPerFrame) {

                return;

            }

            Build slot = _builds[(start + i) % _builds.Length];

            if (slot.Node == null || (!wait && !slot.Handle.IsCompleted)) {

                continue;

            }

            collected++;
            _collectFrom = (start + i + 1) % _builds.Length;

            slot.Handle.Complete();

            Node node = slot.Node;
            Patch patch = _sparePatches.Count > 0 ? _sparePatches.Pop() : CreatePatch();

            Mesh.ApplyAndDisposeWritableMeshData(slot.Data, patch.Mesh, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            patch.Mesh.bounds = patch.Mesh.GetSubMesh(0).bounds;

            patch.Horizons.CopyFrom(slot.Horizons);

            int rocks = (int)slot.Info[PatchJob.InfoRocks];
            patch.Rocks = rocks > 0 ? slot.Rocks.GetSubArray(0, 3 * rocks).ToArray() : null;

            int plants = (int)slot.Info[PatchJob.InfoPlants];

            if (plants > 0 && _vegetation != null) {

                patch.Plot ??= new Vegetation.Plot();
                _vegetation.Load(patch.Plot, slot.Plants, plants, node.Depth, MaxDepth);

            } else if (patch.Plot != null) {

                patch.Plot.Count = 0;

            }

            for (int mip = 0; mip < PatchJob.Mips; mip++) {

                patch.Detail.SetPixelData(slot.Detail, mip, 4 * PatchJob.MipStart(mip));
                patch.Cover.SetPixelData(slot.Cover, mip, 4 * PatchJob.MipStart(mip));

            }

            patch.Detail.Apply(false, false);
            patch.Cover.Apply(false, false);

            node.MinHeight = slot.Info[PatchJob.InfoMinHeight];
            node.MaxHeight = slot.Info[PatchJob.InfoMaxHeight];
            node.Radius = slot.Info[PatchJob.InfoRadius];
            node.Building = false;
            node.Patch = patch;

            patch.Centre = node.Direction * _terrain.Radius;
            patch.Object.name = $"{node.Face}/{node.Depth}/{node.X},{node.Y}";
            patch.HasWater = slot.Info[PatchJob.InfoWaterIndices] > 0;
            patch.SheetShown = patch.HasWater;
            patch.Renderer.sharedMaterials = patch.HasWater ? patch.Both : patch.GroundOnly;
            patch.FineWater = patch.Mesh.GetSubMesh(1);
            patch.CoarseWater = patch.FineWater;
            patch.CoarseWater.indexStart = patch.FineWater.indexStart + patch.FineWater.indexCount;
            patch.CoarseWater.indexCount = (int)slot.Info[PatchJob.InfoCoarseWaterIndices];
            patch.Coarse = false;
            patch.Water?.SetFloat(WaterCoarseId, 0.0f);

            Patch parent = node.Parent?.Patch ?? patch;
            Vector4 parentRect = node.Parent == null ? new Vector4(1.0f, 1.0f, 0.0f, 0.0f) : new Vector4(0.5f, 0.5f, 0.5f * (node.X & 1), 0.5f * (node.Y & 1));

            Vector4 near = TileOrigin(patch.Centre, NearTile);
            Vector4 far = TileOrigin(patch.Centre, FarTile);
            Vector4 macro = TileOrigin(patch.Centre, MacroTile);
            Vector4 broad = TileOrigin(patch.Centre, BroadTile);

            foreach (Material material in patch.Both) {

                material.SetVector(TileOriginNearId, near);
                material.SetVector(TileOriginFarId, far);
                material.SetVector(TileOriginMacroId, macro);
                material.SetVector(TileOriginBroadId, broad);
                material.SetTexture(DetailId, patch.Detail);
                material.SetTexture(ParentDetailId, parent.Detail);
                material.SetTexture(CoverId, patch.Cover);
                material.SetTexture(ParentCoverId, parent.Cover);
                material.SetVector(ParentRectId, parentRect);
                material.SetFloat(LevelId, node.Depth);
                material.SetVector(MorphId, _morphs[node.Depth]);

            }

            slot.Node = null;
            _patchCount++;

        }

    }

    // Where the patch's centre falls within TilePeriod repeats of a material, in object axes; the shader adds the small
    // local offset, so textures and the random layouts tied to their repeats stay put and seamless in single precision
    // anywhere on the planet.
    private static Vector4 TileOrigin(Vector3d centre, double tile) {

        static float Wrapped(double v, double tile) => (float)(v / tile - TilePeriod * Math.Floor(v / (TilePeriod * tile)));

        return new Vector4(Wrapped(centre.X, tile), Wrapped(centre.Z, tile), Wrapped(centre.Y, tile), 0.0f);

    }

    private Patch CreatePatch() {

        GameObject go = new GameObject("Patch");
        go.transform.SetParent(_root, false);

        Patch patch = new Patch {

            Object = go,
            Transform = go.transform,
            Mesh = new Mesh { name = "Ground Patch" },
            Detail = new Texture2D(PatchJob.Texels, PatchJob.Texels, GraphicsFormat.R16G16B16A16_UNorm, PatchJob.Mips, TextureCreationFlags.MipChain) {

                name = "Ground Detail",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = Anisotropy,

            },
            Cover = new Texture2D(PatchJob.Texels, PatchJob.Texels, GraphicsFormat.R8G8B8A8_UNorm, PatchJob.Mips, TextureCreationFlags.MipChain) {

                name = "Ground Cover",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = Anisotropy,

            },
            Ground = new Material(_groundTemplate),
            Water = _waterTemplate == null ? null : new Material(_waterTemplate),
            Horizons = new NativeArray<byte>(PatchJob.HorizonLength, Allocator.Persistent),

        };

        _patches.Add(patch);

        patch.GroundOnly = new[] { patch.Ground };
        patch.Both = patch.Water == null ? patch.GroundOnly : new[] { patch.Ground, patch.Water };

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
            slot.Cover.Dispose();
            slot.Info.Dispose();
            slot.Horizons.Dispose();
            slot.Rocks.Dispose();
            slot.Plants.Dispose();
            slot.Samples.Dispose();

        }

        foreach (Patch patch in _patches) {

            patch.Horizons.Dispose();
            patch.Plot?.Dispose();

        }

        _noHorizons.Dispose();
        _rocks.Dispose();
        _vegetation?.Dispose();

    }

}
