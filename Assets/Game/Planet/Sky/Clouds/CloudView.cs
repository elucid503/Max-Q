using System;
using System.Threading.Tasks;

using MaxQ.Game.Map;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Sky.Clouds;

/// <summary>Terra's clouds: a June climatology over its land and sea, redrawn on the GPU as the weather drifts east and
/// churns, carved from tiling noise that drifts with it. Keeps the shader globals current; the atmosphere records the
/// passes that draw them.</summary>
public sealed class CloudView : IDisposable {

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
    private static readonly int OrbitId = Shader.PropertyToID("_CloudOrbit");

    // Match CLOUD_SHAPE_TEXELS and CLOUD_DETAIL_TEXELS in Clouds.hlsl.
    private const int ShapeTexels = 128;
    private const int DetailTexels = 64;

    // Mips of the shape ranked back onto its sharp values, in bins of its 8-bit values; the levels match
    // CLOUD_SHAPE_LEVELS in Clouds.hlsl.
    private const int RemapLevels = 6;
    private const int RemapBins = 256;

    // The weather's cells (about 8 km at the equator), and the survey's land and heights it is drawn over.
    private const int WeatherWidth = 1024;
    private const int WeatherHeight = 512;
    private const int LandWidth = 512;
    private const int LandHeight = 256;

    // The weather is redrawn in bands of a thread group's rows, every band once each WeatherInterval.
    private const int WeatherBands = WeatherHeight / 8;

    // Metres of ground height over the land map's range; matches LAND_HEIGHT_RANGE in CloudNoise.compute.
    private const double LandHeightRange = 2_000.0;

    // Tile sizes (km) of the heaps and of the detail; match CLOUD_SHAPE_SIZE and CLOUD_DETAIL_SIZE in Clouds.hlsl. Their
    // churn is passed in tiles, so each pair's second tiling can wrap with the first.
    private const double ShapeTile = 6.1;
    private const double DetailTile = 1.1;

    // Drift east (m/s at the equator), churn of heaps and edges (m/s), of the weather (tiles/s: storms change over hours,
    // fronts over days), the cyclones' lives (s), and the interval (s) the whole map is redrawn over, too short for its
    // steps to show.
    private const double DriftSpeed = 8.0;
    private const double ShapeChurn = 0.5;
    private const double DetailChurn = 2.0;
    private const double ChurnRate = 1.3e-6;
    private const double CycloneCycle = 4.0 * 86_400.0;
    private const double WeatherInterval = 1.0;

    // The shadow map spans ShadowSpan km of ground, doubling as the camera climbs, up to ShadowMaxSpan: by
    // ShadowSpanPerAltitude for each km up to ShadowLowAltitude, where the view takes in the ground all round, and by
    // ShadowSpanPerHighAltitude beyond. Between ShadowLookStart and ShadowLookEnd km up its centre moves from the ground
    // below to the ground in view, which from orbit lies hundreds of kilometres ahead, so the map spends none of its texels
    // behind the camera.
    private const int ShadowTexels = 512;
    private const double ShadowSpan = 60.0;
    private const double ShadowMaxSpan = 3_840.0;
    private const double ShadowSpanPerAltitude = 8.0;
    private const double ShadowLowAltitude = 30.0;
    private const double ShadowSpanPerHighAltitude = 1.5;
    private const double ShadowLookStart = 30.0;
    private const double ShadowLookEnd = 60.0;

    // Altitudes (km) over which the camera rises from seeing the clouds round it to seeing them from orbit, where only
    // their formations are drawn (_CloudOrbit).
    private const double OrbitStart = 10.0;
    private const double OrbitEnd = 40.0;

    // The shader's shadow and sky map passes; the sky map's width matches CLOUD_SKY_WIDTH in Atmosphere.hlsl.
    private const int ShadowPass = 2;
    private const int SkyPass = 3;
    private const int SkyWidth = 256;
    private const int SkyHeight = 96;

    private readonly CelestialBody _body;
    private readonly float _radius;
    private readonly Vector3 _sun;
    private readonly Vector3 _shadowRight;
    private readonly Vector3 _shadowUp;
    private readonly ComputeShader _noise;
    private readonly int _weatherKernel;
    private readonly Texture2D _land;
    private readonly RenderTexture _weather;
    private readonly RenderTexture _warp;
    private readonly RenderTexture _shape;
    private readonly RenderTexture _detail;
    private readonly RenderTexture _bound;
    private readonly Texture2D _remap;
    private readonly Material _material;

    // The sim time the weather's redraw counts from, and the bands drawn since.
    private double _forecast = double.NaN;
    private long _forecastBands;

    /// <summary>Whether the clouds draw and cast shadows; the capture turns them off to time them.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maps the clouds' shadow along the sun, which the ground, water, plants and haze read, and the clouds round
    /// the camera, which water mirrors over the clear sky's table.</summary>
    internal TablePass Maps { get; }

    internal CloudTrace Trace { get; }

    /// <summary>The planet's centre in the scene this frame, and its turn about its axis (radians).</summary>
    internal Vector3 Centre { get; private set; }

    internal double Rotation { get; private set; }

    public CloudView(CelestialBody body, Shader shader, ComputeShader noise, Vector3 sunDirection) {

        _body = body;
        _radius = (float)(body.Radius / MapSpace.MetresPerUnit);
        _sun = sunDirection.normalized;
        _shadowRight = Vector3.Cross(Mathf.Abs(_sun.y) < 0.99f ? Vector3.up : Vector3.right, _sun).normalized;
        _shadowUp = Vector3.Cross(_sun, _shadowRight);
        _noise = noise;
        _weatherKernel = noise.FindKernel("Weather");

        _shape = Generate(noise, "Shape", ShapeTexels);
        _detail = Generate(noise, "Detail", DetailTexels);
        _bound = Bound(noise, _shape);
        _remap = Remap(noise, _shape);
        _land = Land(body);
        _weather = WeatherMap("Cloud Weather");
        _warp = WeatherMap("Cloud Warp");
        noise.SetTexture(_weatherKernel, "_Land", _land);
        noise.SetTexture(_weatherKernel, "_Weather", _weather);
        noise.SetTexture(_weatherKernel, "_Warp", _warp);
        noise.SetVector("_WeatherSize", new Vector4(WeatherWidth, WeatherHeight, 1.0f / WeatherWidth, 1.0f / WeatherHeight));

        Shader.SetGlobalTexture("_CloudWeather", _weather);
        Shader.SetGlobalTexture("_CloudWarp", _warp);
        Shader.SetGlobalTexture("_CloudShape", _shape);
        Shader.SetGlobalTexture("_CloudDetail", _detail);
        Shader.SetGlobalTexture("_CloudShapeBound", _bound);
        Shader.SetGlobalTexture("_CloudShapeRemap", _remap);
        Shader.SetGlobalTexture(SkyId, Texture2D.blackTexture);
        Shader.SetGlobalTexture(ShadowId, Texture2D.whiteTexture);
        Shader.SetGlobalFloat(CloudsOnId, 0.0f);
        Shader.SetGlobalVector(ShadowOriginId, Vector4.zero);

        _material = new Material(shader);
        Trace = new CloudTrace(_material, _radius);
        Maps = new TablePass(_material,
            ("Cloud Shadow", ShadowTexels, ShadowTexels, GraphicsFormat.R16G16_SFloat, ShadowPass, ShadowId),
            ("Cloud Sky", SkyWidth, SkyHeight, GraphicsFormat.R16G16B16A16_SFloat, SkyPass, SkyId));

    }

    /// <summary>Turns the clouds with the planet, drifts and churns their weather and noise, and fits their shadow map to
    /// the ground <paramref name="camera"/> sees.</summary>
    public void Update(double time, Camera camera) {

        Centre = MapSpace.ToScene(_body.PositionAt(time));
        Rotation = Wrap(_body.RotationAt(time), 2.0 * Math.PI);

        Quaternion toBody = Quaternion.AngleAxis((float)(Rotation * 180.0 / Math.PI), Vector3.up);
        double drift = Wrap(time * DriftSpeed / _body.Radius, 2.0 * Math.PI);

        Shader.SetGlobalMatrix(BodyFromSceneId, Matrix4x4.Rotate(toBody));
        Shader.SetGlobalMatrix(NoiseFromSceneId, Matrix4x4.Rotate(Quaternion.AngleAxis((float)(drift * 180.0 / Math.PI), Vector3.up) * toBody));
        Shader.SetGlobalVector(ShapeOffsetId, Churn(time * ShapeChurn / MapSpace.MetresPerUnit / ShapeTile, 1.0, new Vector3(0.3f, 1.0f, 0.2f)));
        Shader.SetGlobalVector(DetailOffsetId, Churn(time * DetailChurn / MapSpace.MetresPerUnit / DetailTile, 1.0, new Vector3(0.5f, 1.0f, -0.3f)));
        Shader.SetGlobalFloat(CloudsOnId, Enabled ? 1.0f : 0.0f);

        Forecast(time, drift, toBody * _sun);
        FitShadow(camera);

    }

    // Each frame redraws the bands due since the last, so no frame draws the whole map; the first frame, or a leap in
    // time, redraws it all at once.
    private void Forecast(double time, double drift, Vector3 sun) {

        long due = double.IsNaN(_forecast) ? 0 : (long)Math.Floor((time - _forecast) / WeatherInterval * WeatherBands);

        if (double.IsNaN(_forecast) || due < _forecastBands || due - _forecastBands >= WeatherBands) {

            _forecast = time;
            _forecastBands = 0;
            DrawWeather(time, drift, sun, 0, WeatherBands);

            return;

        }

        int count = (int)(due - _forecastBands);

        if (count == 0) {

            return;

        }

        int first = (int)(_forecastBands % WeatherBands);
        int head = Math.Min(count, WeatherBands - first);

        DrawWeather(time, drift, sun, first, head);

        if (count > head) {

            DrawWeather(time, drift, sun, 0, count - head);

        }

        _forecastBands = due;

    }

    private void DrawWeather(double time, double drift, Vector3 sun, int firstBand, int bands) {

        _noise.SetFloat("_WeatherDrift", (float)drift);
        _noise.SetVector("_WeatherChurn", Churn(time * ChurnRate, 1.0, new Vector3(0.6f, 0.3f, 0.75f)));
        _noise.SetFloat("_WeatherPhase", (float)Wrap(time / CycloneCycle, 1.0));
        _noise.SetVector("_WeatherSun", sun);
        _noise.SetInt("_WeatherFirstRow", firstBand * (WeatherHeight / WeatherBands));
        _noise.Dispatch(_weatherKernel, WeatherWidth / 8, bands, 1);

    }

    // The map's plane faces the sun through the ground it centres on, snapped to its texels about the planet's centre so
    // the shadows hold still as the camera moves.
    private void FitShadow(Camera camera) {

        Vector3 eye = camera.transform.position - Centre;
        double altitude = Math.Max(eye.magnitude - _radius, 0.0);
        double reach = ShadowSpan + ShadowSpanPerAltitude * Math.Min(altitude, ShadowLowAltitude) + ShadowSpanPerHighAltitude * Math.Max(altitude - ShadowLowAltitude, 0.0);
        double span = ShadowSpan;

        while (span < ShadowMaxSpan && span < reach) {

            span *= 2.0;

        }

        Shader.SetGlobalFloat(OrbitId, Mathf.SmoothStep(0.0f, 1.0f, (float)((altitude - OrbitStart) / (OrbitEnd - OrbitStart))));

        // The ground in view lies between where the bottom of the view and its middle meet the planet, or the horizon.
        Transform view = camera.transform;
        Vector3 bottom = Quaternion.AngleAxis(0.5f * camera.fieldOfView, view.right) * view.forward;
        Vector3 inView = Vector3.Slerp(Seen(eye, bottom), Seen(eye, view.forward), 0.5f);
        float ahead = Mathf.SmoothStep(0.0f, 1.0f, (float)((altitude - ShadowLookStart) / (ShadowLookEnd - ShadowLookStart)));
        Vector3 ground = Vector3.Slerp(eye.normalized, inView, ahead) * _radius;

        float texel = (float)(span / ShadowTexels);
        float right = Mathf.Round(Vector3.Dot(ground, _shadowRight) / texel) * texel;
        float up = Mathf.Round(Vector3.Dot(ground, _shadowUp) / texel) * texel;
        Vector3 origin = Centre + _shadowRight * right + _shadowUp * up + _sun * Vector3.Dot(ground, _sun);

        Shader.SetGlobalVector(ShadowOriginId, new Vector4(origin.x, origin.y, origin.z, Enabled ? (float)(1.0 / span) : 0.0f));
        Shader.SetGlobalVector(ShadowRightId, _shadowRight);
        Shader.SetGlobalVector(ShadowUpId, _shadowUp);

    }

    // Where a ray from eye (relative to the centre) meets the ground, as a direction from the centre; a ray that misses
    // gives the horizon under its heading.
    private Vector3 Seen(Vector3 eye, Vector3 direction) {

        float distance = eye.magnitude;
        Vector3 up = eye / distance;
        float b = Vector3.Dot(eye, direction);
        float discriminant = b * b - (distance * distance - _radius * _radius);

        if (discriminant >= 0.0f && -b - Mathf.Sqrt(discriminant) > 0.0f) {

            return (eye + direction * (-b - Mathf.Sqrt(discriminant))).normalized;

        }

        Vector3 level = direction - up * Vector3.Dot(direction, up);
        float horizon = Mathf.Acos(Mathf.Min(_radius / distance, 1.0f));

        return level.sqrMagnitude > 1e-8f ? up * Mathf.Cos(horizon) + level.normalized * Mathf.Sin(horizon) : up;

    }

    // An offset that has travelled distance along direction, wrapped to the tile.
    private static Vector4 Churn(double distance, double tile, Vector3 direction) {

        return new Vector4((float)Wrap(distance * direction.x, tile), (float)Wrap(distance * direction.y, tile), (float)Wrap(distance * direction.z, tile), 0.0f);

    }

    private static double Wrap(double value, double period) => value - Math.Floor(value / period) * period;

    // Land share and ground height round the body, row 0 at the north pole and column 0 at 180 W, as the weather is laid out.
    private static Texture2D Land(CelestialBody body) {

        byte[] texels = new byte[LandWidth * LandHeight * 2];

        if (body.Terrain is { } terrain) {

            double footprint = 2.0 * Math.PI * body.Radius / LandWidth;

            Parallel.For(0, LandHeight, row => {

                double latitude = Math.PI * (0.5 - (row + 0.5) / LandHeight);

                for (int column = 0; column < LandWidth; column++) {

                    double longitude = Math.PI * (2.0 * (column + 0.5) / LandWidth - 1.0);
                    Vector3d direction = new Vector3d(Math.Cos(latitude) * Math.Cos(longitude), Math.Cos(latitude) * Math.Sin(longitude), Math.Sin(latitude));
                    int texel = 2 * (row * LandWidth + column);

                    texels[texel] = double.IsNaN(terrain.WaterLevelAt(direction, footprint)) ? (byte)255 : (byte)0;
                    texels[texel + 1] = (byte)Math.Clamp(terrain.HeightAt(direction, footprint) / LandHeightRange * 255.0, 0.0, 255.0);

                }

            });

        }

        Texture2D land = new Texture2D(LandWidth, LandHeight, TextureFormat.RG16, false, true) {

            name = "Cloud Land",
            wrapModeU = TextureWrapMode.Repeat,
            wrapModeV = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,

        };

        land.SetPixelData(texels, 0);
        land.Apply(false, true);

        return land;

    }

    private static RenderTexture WeatherMap(string name) {

        RenderTexture map = new RenderTexture(WeatherWidth, WeatherHeight, 0, GraphicsFormat.R8G8B8A8_UNorm) {

            name = name,
            enableRandomWrite = true,
            wrapModeU = TextureWrapMode.Repeat,
            wrapModeV = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,

        };

        map.Create();

        return map;

    }

    private static RenderTexture Volume(string name, int size, bool mips) {

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

    private static RenderTexture Generate(ComputeShader noise, string kernelName, int size) {

        RenderTexture volume = Volume($"Cloud {kernelName}", size, true);
        int kernel = noise.FindKernel(kernelName);

        noise.SetTexture(kernel, "_Volume", volume);
        noise.SetInt("_Size", size);
        noise.Dispatch(kernel, size / 4, size / 4, size / 4);
        volume.GenerateMips();

        return volume;

    }

    // The shape's bounds, mip by mip, each level read back from its own copy while the next is drawn.
    private static RenderTexture Bound(ComputeShader noise, RenderTexture shape) {

        RenderTexture bound = Volume("Cloud Shape Bound", ShapeTexels, true);
        RenderTexture source = shape;
        int kernel = noise.FindKernel("Bound");

        for (int mip = 0, size = ShapeTexels; size >= 1; mip++, size /= 2) {

            RenderTexture level = Volume("Cloud Shape Bound Level", size, false);
            int groups = Mathf.Max(1, size / 4);

            noise.SetTexture(kernel, "_Source", source);
            noise.SetTexture(kernel, "_Volume", level);
            noise.SetTexture(kernel, "_Bound", bound, mip);
            noise.SetInt("_Size", size);
            noise.SetInt("_Scale", mip == 0 ? 1 : 2);
            noise.Dispatch(kernel, groups, groups, groups);
            Discard(source, shape);
            source = level;

        }

        Discard(source, shape);

        return bound;

    }

    // Each mip's values mapped to the sharp shape's value that holds the same share of the volume below it, so the cover's
    // threshold carves the same share of cloud from a blurred mip as from the sharp one.
    private static Texture2D Remap(ComputeShader noise, RenderTexture shape) {

        int kernel = noise.FindKernel("Histogram");
        uint[] counts = new uint[RemapLevels * RemapBins];
        using ComputeBuffer histogram = new ComputeBuffer(counts.Length, sizeof(uint));

        histogram.SetData(counts);
        noise.SetBuffer(kernel, "_Histogram", histogram);
        noise.SetTexture(kernel, "_Source", shape);

        for (int mip = 0; mip < RemapLevels; mip++) {

            int groups = Mathf.Max(1, (ShapeTexels >> mip) / 4);

            noise.SetInt("_Size", ShapeTexels >> mip);
            noise.SetInt("_Mip", mip);
            noise.Dispatch(kernel, groups, groups, groups);

        }

        histogram.GetData(counts);

        float[] table = new float[RemapLevels * RemapBins];
        double[] sharp = Cumulative(counts, 0);

        for (int mip = 0; mip < RemapLevels; mip++) {

            double[] blurred = Cumulative(counts, mip);

            for (int bin = 0; bin < RemapBins; bin++) {

                table[mip * RemapBins + bin] = (float)ValueAtShare(sharp, 0.5 * (blurred[bin] + blurred[bin + 1]));

            }

        }

        Texture2D remap = new Texture2D(RemapBins, RemapLevels, TextureFormat.RFloat, false, true) {

            name = "Cloud Shape Remap",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,

        };

        remap.SetPixelData(table, 0);
        remap.Apply(false, true);

        return remap;

    }

    // Share of a mip's values below each bin, and below all of them at the end.
    private static double[] Cumulative(uint[] counts, int mip) {

        double[] below = new double[RemapBins + 1];

        for (int bin = 0; bin < RemapBins; bin++) {

            below[bin + 1] = below[bin] + counts[mip * RemapBins + bin];

        }

        for (int bin = 0; bin <= RemapBins; bin++) {

            below[bin] /= Math.Max(below[RemapBins], 1.0);

        }

        return below;

    }

    // The value with share of the volume below it, spread evenly across its bin.
    private static double ValueAtShare(double[] below, double share) {

        for (int bin = 0; bin < RemapBins; bin++) {

            if (share < below[bin + 1]) {

                double within = (share - below[bin]) / (below[bin + 1] - below[bin]);

                return Math.Clamp((bin - 0.5 + within) / (RemapBins - 1), 0.0, 1.0);

            }

        }

        return 1.0;

    }

    private static void Discard(RenderTexture level, RenderTexture shape) {

        if (level != shape) {

            level.Release();
            UnityEngine.Object.Destroy(level);

        }

    }

    public void Dispose() {

        UnityEngine.Object.Destroy(_material);
        UnityEngine.Object.Destroy(_land);
        UnityEngine.Object.Destroy(_remap);

        _weather.Release();
        _warp.Release();
        _shape.Release();
        _detail.Release();
        _bound.Release();

    }

}
