using System;
using System.IO;

using MaxQ.Game.Map;
using MaxQ.Sim.Bodies;

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Sky.Clouds;

/// <summary>Terra's clouds: the weather ERA5 saw at one hour (clouds.bin), carved from tiling noise that drifts east with
/// the wind and churns as it goes. Keeps the shader globals current; the atmosphere records the passes that draw them.</summary>
public sealed class CloudView : IDisposable {

    private static readonly int WeatherId = Shader.PropertyToID("_CloudWeather");
    private static readonly int AnvilsId = Shader.PropertyToID("_CloudAnvils");
    private static readonly int ShapeId = Shader.PropertyToID("_CloudShape");
    private static readonly int DetailId = Shader.PropertyToID("_CloudDetail");
    private static readonly int BoundId = Shader.PropertyToID("_CloudShapeBound");
    private static readonly int SourceId = Shader.PropertyToID("_Source");
    private static readonly int BodyFromSceneId = Shader.PropertyToID("_CloudBodyFromScene");
    private static readonly int NoiseFromSceneId = Shader.PropertyToID("_CloudNoiseFromScene");
    private static readonly int ShapeOffsetId = Shader.PropertyToID("_CloudShapeOffset");
    private static readonly int DetailOffsetId = Shader.PropertyToID("_CloudDetailOffset");
    private static readonly int CloudsOnId = Shader.PropertyToID("_CloudsOn");
    private static readonly int SkyId = Shader.PropertyToID("_CloudSky");
    private static readonly int ShadowId = Shader.PropertyToID("_CloudShadow");
    private static readonly int ShadowOriginId = Shader.PropertyToID("_CloudShadowOrigin");
    private static readonly int ShadowRightId = Shader.PropertyToID("_CloudShadowRight");
    private static readonly int ShadowUpId = Shader.PropertyToID("_CloudShadowUp");
    private static readonly int VolumeId = Shader.PropertyToID("_Volume");
    private static readonly int SizeId = Shader.PropertyToID("_Size");

    private const int Magic = 0x4443514D;
    private const int Channels = 8;
    private const int ShapeTexels = 128;
    private const int DetailTexels = 32;

    // Tile sizes (km) of the heaps and of the detail; match CLOUD_SHAPE_SIZE and CLOUD_DETAIL_SIZE in Clouds.hlsl.
    private const double ShapeTile = 5.0;
    private const double DetailTile = 0.7;

    // The noise drifts east at DriftSpeed (m/s) on the equator, while the heaps churn at ShapeChurn and their edges at DetailChurn.
    private const double DriftSpeed = 8.0;
    private const double ShapeChurn = 0.5;
    private const double DetailChurn = 2.0;

    // The shadow map spans ShadowSpan km round the camera on the ground, doubling as it climbs, up to ShadowMaxSpan.
    internal const int ShadowTexels = 512;
    private const double ShadowSpan = 60.0;
    private const double ShadowMaxSpan = 3_840.0;
    private const double ShadowSpanPerAltitude = 8.0;

    private readonly CelestialBody _body;
    private readonly float _radius;
    private readonly Vector3 _sun;
    private readonly Vector3 _shadowRight;
    private readonly Vector3 _shadowUp;
    private readonly Texture2D _weather;
    private readonly Texture2D _anvils;
    private readonly RenderTexture _shape;
    private readonly RenderTexture _detail;
    private readonly RenderTexture _bound;
    private readonly Material _material;

    /// <summary>Whether the clouds draw and cast shadows; the capture turns them off to time them.</summary>
    public bool Enabled { get; set; } = true;

    internal CloudSkyPass SkyPass { get; }

    internal CloudTrace Trace { get; }

    /// <summary>The planet's centre in the scene this frame, and its turn about its axis (radians).</summary>
    internal Vector3 Centre { get; private set; }

    internal double Rotation { get; private set; }

    public CloudView(CelestialBody body, string data, Shader shader, ComputeShader noise, Vector3 sunDirection) {

        _body = body;
        _radius = (float)(body.Radius / MapSpace.MetresPerUnit);
        _sun = sunDirection.normalized;
        _shadowRight = Vector3.Cross(Mathf.Abs(_sun.y) < 0.99f ? Vector3.up : Vector3.right, _sun).normalized;
        _shadowUp = Vector3.Cross(_sun, _shadowRight);

        (_weather, _anvils) = Load(Path.Combine(data, "clouds.bin"));
        _shape = Volume("Cloud Shape", ShapeTexels);
        _detail = Volume("Cloud Detail", DetailTexels);
        Generate(noise, "Shape", _shape, ShapeTexels);
        Generate(noise, "Detail", _detail, DetailTexels);
        _bound = Volume("Cloud Shape Bound", ShapeTexels);
        Bound(noise, _shape, _bound);

        Shader.SetGlobalTexture(WeatherId, _weather);
        Shader.SetGlobalTexture(AnvilsId, _anvils);
        Shader.SetGlobalTexture(ShapeId, _shape);
        Shader.SetGlobalTexture(DetailId, _detail);
        Shader.SetGlobalTexture(BoundId, _bound);
        Shader.SetGlobalTexture(SkyId, Texture2D.blackTexture);
        Shader.SetGlobalTexture(ShadowId, Texture2D.whiteTexture);
        Shader.SetGlobalFloat(CloudsOnId, 0.0f);
        Shader.SetGlobalVector(ShadowOriginId, Vector4.zero);

        _material = new Material(shader);
        SkyPass = new CloudSkyPass(_material);
        Trace = new CloudTrace(_material, _radius);

    }

    /// <summary>Turns the clouds with the planet, drifts and churns their noise, and fits their shadow map round <paramref name="camera"/>.</summary>
    public void Update(double time, Vector3 camera) {

        Centre = MapSpace.ToScene(_body.PositionAt(time));
        Rotation = Wrap(_body.RotationAt(time), 2.0 * Math.PI);

        Quaternion toBody = Quaternion.AngleAxis((float)(Rotation * 180.0 / Math.PI), Vector3.up);
        double drift = Wrap(time * DriftSpeed / _body.Radius, 2.0 * Math.PI);

        Shader.SetGlobalMatrix(BodyFromSceneId, Matrix4x4.Rotate(toBody));
        Shader.SetGlobalMatrix(NoiseFromSceneId, Matrix4x4.Rotate(Quaternion.AngleAxis((float)(drift * 180.0 / Math.PI), Vector3.up) * toBody));
        Shader.SetGlobalVector(ShapeOffsetId, Churn(time, ShapeChurn, ShapeTile, new Vector3(0.3f, 1.0f, 0.2f)));
        Shader.SetGlobalVector(DetailOffsetId, Churn(time, DetailChurn, DetailTile, new Vector3(0.5f, 1.0f, -0.3f)));
        Shader.SetGlobalFloat(CloudsOnId, Enabled ? 1.0f : 0.0f);

        FitShadow(camera);

    }

    // The map's plane faces the sun through the ground below the camera, snapped to its texels about the planet's centre so
    // the shadows hold still as the camera moves.
    private void FitShadow(Vector3 camera) {

        Vector3 ground = (camera - Centre).normalized * _radius;
        double altitude = Math.Max((camera - Centre).magnitude - _radius, 0.0);
        double span = ShadowSpan;

        while (span < ShadowMaxSpan && span < ShadowSpan + ShadowSpanPerAltitude * altitude) {

            span *= 2.0;

        }

        float texel = (float)(span / ShadowTexels);
        float right = Mathf.Round(Vector3.Dot(ground, _shadowRight) / texel) * texel;
        float up = Mathf.Round(Vector3.Dot(ground, _shadowUp) / texel) * texel;
        Vector3 origin = Centre + _shadowRight * right + _shadowUp * up + _sun * Vector3.Dot(ground, _sun);

        Shader.SetGlobalVector(ShadowOriginId, new Vector4(origin.x, origin.y, origin.z, Enabled ? (float)(1.0 / span) : 0.0f));
        Shader.SetGlobalVector(ShadowRightId, _shadowRight);
        Shader.SetGlobalVector(ShadowUpId, _shadowUp);

    }

    private static Vector4 Churn(double time, double speed, double tile, Vector3 direction) {

        double distance = time * speed / MapSpace.MetresPerUnit;

        return new Vector4((float)Wrap(distance * direction.x, tile), (float)Wrap(distance * direction.y, tile), (float)Wrap(distance * direction.z, tile), 0.0f);

    }

    private static double Wrap(double value, double period) => value - Math.Floor(value / period) * period;

    private static (Texture2D Weather, Texture2D Anvils) Load(string path) {

        if (!File.Exists(path)) {

            throw new InvalidOperationException("Terra's weather is not baked; run tools/terra.sh");

        }

        using BinaryReader reader = new BinaryReader(File.OpenRead(path));

        int magic = reader.ReadInt32();
        int rows = reader.ReadInt32();
        int columns = reader.ReadInt32();
        int channels = reader.ReadInt32();

        if (magic != Magic || channels != Channels) {

            throw new InvalidDataException($"{path} is not a cloud bake; rebake it with tools/terra.sh");

        }

        byte[] cells = reader.ReadBytes(rows * columns * Channels);
        byte[] low = new byte[rows * columns * 4];
        byte[] anvils = new byte[rows * columns * 4];

        for (int i = 0; i < rows * columns; i++) {

            Buffer.BlockCopy(cells, i * Channels, low, i * 4, 4);
            Buffer.BlockCopy(cells, i * Channels + 4, anvils, i * 4, 4);

        }

        return (Weather("Cloud Weather", columns, rows, low), Weather("Cloud Anvils", columns, rows, anvils));

    }

    // Row 0 is the north edge and sits at v = 0, as the shaders read it.
    private static Texture2D Weather(string name, int width, int height, byte[] texels) {

        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, true, true) {

            name = name,
            wrapModeU = TextureWrapMode.Repeat,
            wrapModeV = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,

        };

        texture.SetPixelData(texels, 0);
        texture.Apply(true, true);

        return texture;

    }

    private static RenderTexture Volume(string name, int size, bool mips = true) {

        RenderTexture volume = new RenderTexture(size, size, 0, GraphicsFormat.R8_UNorm) {

            name = name,
            dimension = TextureDimension.Tex3D,
            volumeDepth = size,
            enableRandomWrite = true,
            useMipMap = mips,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,

        };

        volume.Create();

        return volume;

    }

    private static void Generate(ComputeShader noise, string kernelName, RenderTexture volume, int size) {

        int kernel = noise.FindKernel(kernelName);

        noise.SetTexture(kernel, VolumeId, volume);
        noise.SetInt(SizeId, size);
        noise.Dispatch(kernel, size / 4, size / 4, size / 4);
        volume.GenerateMips();

    }

    // The shape's bounds, mip by mip: each level's cell maxima from the level below, spread over their neighbours.
    private static void Bound(ComputeShader noise, RenderTexture shape, RenderTexture bound) {

        int highest = noise.FindKernel("Highest");
        int spread = noise.FindKernel("Spread");
        RenderTexture level = shape;

        for (int mip = 0, size = ShapeTexels; size >= 1; mip++, size /= 2) {

            int groups = Mathf.Max(1, size / 4);

            if (mip > 0) {

                RenderTexture next = Volume("Cloud Shape Highest", size, false);

                noise.SetTexture(highest, SourceId, level);
                noise.SetTexture(highest, VolumeId, next);
                noise.SetInt(SizeId, size);
                noise.Dispatch(highest, groups, groups, groups);
                Discard(level, shape);
                level = next;

            }

            noise.SetTexture(spread, SourceId, level);
            noise.SetTexture(spread, VolumeId, bound, mip);
            noise.SetInt(SizeId, size);
            noise.Dispatch(spread, groups, groups, groups);

        }

        Discard(level, shape);

    }

    private static void Discard(RenderTexture level, RenderTexture shape) {

        if (level != shape) {

            level.Release();
            UnityEngine.Object.Destroy(level);

        }

    }

    public void Dispose() {

        Trace.Dispose();
        UnityEngine.Object.Destroy(_material);
        UnityEngine.Object.Destroy(_weather);
        UnityEngine.Object.Destroy(_anvils);

        _shape.Release();
        _detail.Release();
        _bound.Release();

    }

}
