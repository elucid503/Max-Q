using System;
using System.Collections.Generic;

using MaxQ.Game.Map;
using MaxQ.Game.Planet.Ground;
using MaxQ.Game.Planet.Water.Waves;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Surface;

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Water.Shore;

/// <summary>The shore around the camera at four scales: squares of 512 texels, from half a metre to 32 m each, holding the
/// water depth drawn from the finest ground built there and the field the waves need near a coast (distance and
/// direction to shore, and the shallowest water upwave). Each square stays where it was drawn, square to the wave frame
/// then, until the camera has moved an eighth of its width or new ground has streamed in; one is redrawn a frame.</summary>
internal sealed class ShoreCascades : IDisposable {

    public const int Size = 512;
    public const int Slices = 4;

    // Corners per patch edge drawn into a cascade; must match GRID in ShoreDepth.shader.
    private const int Grid = 8;

    /// <summary>Metres a texel spans in each cascade.</summary>
    public static readonly double[] Texels = { 0.5, 2.0, 8.0, 32.0 };

    private static readonly int PointsId = Shader.PropertyToID("_ShorePoints");
    private static readonly int BaseId = Shader.PropertyToID("_ShoreBase");
    private static readonly int DetailId = Shader.PropertyToID("_Detail");
    private static readonly int DepthId = Shader.PropertyToID("_ShoreDepth");
    private static readonly int FieldId = Shader.PropertyToID("_ShoreField");
    private static readonly int SeedsInId = Shader.PropertyToID("_SeedsIn");
    private static readonly int SeedsOutId = Shader.PropertyToID("_SeedsOut");
    private static readonly int SliceId = Shader.PropertyToID("_Slice");
    private static readonly int StepId = Shader.PropertyToID("_Step");
    private static readonly int TexelId = Shader.PropertyToID("_Texel");
    private static readonly int UpwaveId = Shader.PropertyToID("_Upwave");
    private static readonly int UpwaveReachId = Shader.PropertyToID("_UpwaveReach");
    private static readonly int CentreId = Shader.PropertyToID("_ShoreCentre");
    private static readonly int EastId = Shader.PropertyToID("_ShoreEast");
    private static readonly int NorthId = Shader.PropertyToID("_ShoreNorth");
    private static readonly int HalfId = Shader.PropertyToID("_ShoreHalf");

    private sealed class Square {

        public Vector3d Centre;
        public Vector3d East;
        public Vector3d North;
        public bool Drawn;
        public int Revision = -1;

    }

    private readonly ComputeShader _shader;
    private readonly Material _capture;
    private readonly int _seed;
    private readonly int _flood;
    private readonly int _resolve;
    private readonly RenderTexture _depth;
    private readonly RenderTexture[] _seeds = new RenderTexture[2];
    private readonly Square[] _squares = new Square[Slices];
    private readonly List<GroundView.BuiltPatch> _patches = new List<GroundView.BuiltPatch>();
    private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
    private readonly CommandBuffer _commands = new CommandBuffer { name = "Shore" };
    private readonly Vector4[] _centres = new Vector4[Slices];
    private readonly Vector4[] _easts = new Vector4[Slices];
    private readonly Vector4[] _norths = new Vector4[Slices];

    private GraphicsBuffer _points;
    private Vector2[] _pointData = Array.Empty<Vector2>();
    private int _next;

    public RenderTexture Field { get; }

    public ShoreCascades(ComputeShader shader, Shader capture) {

        _shader = shader;
        _capture = new Material(capture);
        _seed = shader.FindKernel("Seed");
        _flood = shader.FindKernel("Flood");
        _resolve = shader.FindKernel("Resolve");

        _depth = CascadeArray("Shore Depth", GraphicsFormat.R16_SFloat, Slices, false);
        Field = CascadeArray("Shore Field", GraphicsFormat.R16G16B16A16_SFloat, Slices, true);

        for (int i = 0; i < 2; i++) {

            _seeds[i] = new RenderTexture(new RenderTextureDescriptor(Size, Size, GraphicsFormat.R32G32_SFloat, GraphicsFormat.None, 1) { enableRandomWrite = true }) { name = "Shore Seeds" };
            _seeds[i].Create();

        }

        for (int i = 0; i < Slices; i++) {

            _squares[i] = new Square();

        }

    }

    /// <summary>Redraws the square most out of date, if any is, around the wave frame's origin. Waves come from
    /// <paramref name="upwave"/> (radians anticlockwise from the frame's east), and the water within
    /// <paramref name="upwaveReach"/> metres that way shelters what lies behind it.</summary>
    public void Update(GroundView ground, WaveFrame frame, double radius, double upwave, double upwaveReach) {

        for (int k = 0; k < Slices; k++) {

            int slice = (_next + k) % Slices;
            Square square = _squares[slice];
            double half = 0.5 * Size * Texels[slice];
            bool moved = !square.Drawn || (frame.Origin - square.Centre).Length * radius > 0.25 * half;

            if (moved || square.Revision != ground.Revision) {

                Draw(slice, ground, frame, radius, upwave, upwaveReach);
                _next = (slice + 1) % Slices;

                return;

            }

        }

    }

    /// <summary>Publishes where each square lies in the scene at sim <paramref name="time"/> (s).</summary>
    public void Publish(CelestialBody body, double time) {

        Vector3d bodyPosition = body.PositionAt(time);
        Vector4 half = Vector4.zero;

        for (int i = 0; i < Slices; i++) {

            Square square = _squares[i];

            _centres[i] = MapSpace.ToScene(bodyPosition + body.FromBodyFixed(square.Centre * body.Radius, time));
            _easts[i] = MapSpace.Direction(body.FromBodyFixed(square.East, time));
            _norths[i] = MapSpace.Direction(body.FromBodyFixed(square.North, time));
            half[i] = square.Drawn ? (float)(0.5 * Size * Texels[i]) : 0.0f;

        }

        Shader.SetGlobalVectorArray(CentreId, _centres);
        Shader.SetGlobalVectorArray(EastId, _easts);
        Shader.SetGlobalVectorArray(NorthId, _norths);
        Shader.SetGlobalVector(HalfId, half);
        Shader.SetGlobalTexture(FieldId, Field);

    }

    private void Draw(int slice, GroundView ground, WaveFrame frame, double radius, double upwave, double upwaveReach) {

        Square square = _squares[slice];
        double half = 0.5 * Size * Texels[slice];

        square.Centre = frame.Origin;
        square.East = frame.East;
        square.North = frame.North;
        square.Revision = ground.Revision;
        square.Drawn = true;

        ground.Cover(square.Centre, 1.5 * half, _patches);
        Place(square, radius, half);

        CommandBuffer cmd = _commands;
        cmd.Clear();
        cmd.SetRenderTarget(_depth, 0, CubemapFace.Unknown, slice);
        cmd.ClearRenderTarget(false, true, new Color(1e4f, 0.0f, 0.0f, 0.0f));

        for (int i = 0; i < _patches.Count; i++) {

            _properties.SetTexture(DetailId, _patches[i].Detail);
            _properties.SetBuffer(PointsId, _points);
            _properties.SetInt(BaseId, i * (Grid + 1) * (Grid + 1));
            cmd.DrawProcedural(Matrix4x4.identity, _capture, 0, MeshTopology.Triangles, Grid * Grid * 6, 1, _properties);

        }

        int groups = Size / 8;

        cmd.SetComputeIntParam(_shader, SliceId, slice);
        cmd.SetComputeFloatParam(_shader, TexelId, (float)Texels[slice]);
        cmd.SetComputeVectorParam(_shader, UpwaveId, new Vector4((float)Math.Cos(upwave), (float)Math.Sin(upwave), 0.0f, 0.0f));
        cmd.SetComputeFloatParam(_shader, UpwaveReachId, (float)upwaveReach);
        cmd.SetComputeTextureParam(_shader, _seed, DepthId, _depth);
        cmd.SetComputeTextureParam(_shader, _seed, SeedsOutId, _seeds[0]);
        cmd.DispatchCompute(_shader, _seed, groups, groups, 1);

        int source = 0;

        for (int step = Size / 2; step >= 1; step /= 2) {

            cmd.SetComputeIntParam(_shader, StepId, step);
            cmd.SetComputeTextureParam(_shader, _flood, SeedsInId, _seeds[source]);
            cmd.SetComputeTextureParam(_shader, _flood, SeedsOutId, _seeds[1 - source]);
            cmd.DispatchCompute(_shader, _flood, groups, groups, 1);
            source = 1 - source;

        }

        cmd.SetComputeTextureParam(_shader, _resolve, DepthId, _depth);
        cmd.SetComputeTextureParam(_shader, _resolve, SeedsInId, _seeds[source]);
        cmd.SetComputeTextureParam(_shader, _resolve, FieldId, Field);
        cmd.DispatchCompute(_shader, _resolve, groups, groups, 1);
        cmd.GenerateMips(Field);

        Graphics.ExecuteCommandBuffer(cmd);

    }

    // Each patch's grid of corners in the square's coordinates, -1 to 1 across it, measured in doubles along the ground.
    private void Place(Square square, double radius, double half) {

        int count = _patches.Count * (Grid + 1) * (Grid + 1);

        if (_pointData.Length < count) {

            _pointData = new Vector2[Math.Max(count, 2 * _pointData.Length)];
            _points?.Release();
            _points = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _pointData.Length, 2 * sizeof(float));

        }

        int n = 0;

        foreach (GroundView.BuiltPatch patch in _patches) {

            double span = 2.0 / (1L << patch.Depth);
            double a0 = patch.X * span - 1.0;
            double b0 = patch.Y * span - 1.0;

            for (int j = 0; j <= Grid; j++) {

                for (int i = 0; i <= Grid; i++) {

                    Vector3d offset = (CubeFace.Direction(patch.Face, a0 + i * span / Grid, b0 + j * span / Grid) - square.Centre) * radius;

                    _pointData[n++] = new Vector2((float)(Vector3d.Dot(offset, square.East) / half), (float)(Vector3d.Dot(offset, square.North) / half));

                }

            }

        }

        _points.SetData(_pointData, 0, 0, count);

    }

    private static RenderTexture CascadeArray(string name, GraphicsFormat format, int depth, bool mips) {

        RenderTexture texture = new RenderTexture(new RenderTextureDescriptor(Size, Size, format, GraphicsFormat.None, mips ? 10 : 1) {

            dimension = TextureDimension.Tex2DArray,
            volumeDepth = depth,
            enableRandomWrite = true,
            useMipMap = mips,
            autoGenerateMips = false,

        }) {

            name = name,
            filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,

        };

        texture.Create();

        return texture;

    }

    public void Dispose() {

        _commands.Release();
        _points?.Release();
        _depth.Release();
        Field.Release();
        _seeds[0].Release();
        _seeds[1].Release();
        UnityEngine.Object.Destroy(_capture);

    }

}
