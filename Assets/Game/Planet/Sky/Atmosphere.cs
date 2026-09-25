using System;

using MaxQ.Game.Map;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet.Sky;

/// <summary>A body's air: bakes the scattering tables once, keeps the shader globals (centre, sun) current, and adds the
/// sky-view and composite passes to every game and scene camera.</summary>
public sealed class Atmosphere : IDisposable {

    private static readonly int PlanetCentreId = Shader.PropertyToID("_PlanetCentre");
    private static readonly int PlanetRadiusId = Shader.PropertyToID("_PlanetRadius");
    private static readonly int SunDirectionId = Shader.PropertyToID("_SunDirection");
    private static readonly int SunIlluminanceId = Shader.PropertyToID("_SunIlluminance");
    private static readonly int TransmittanceId = Shader.PropertyToID("_TransmittanceLut");
    private static readonly int MultiScatterId = Shader.PropertyToID("_MultiScatterLut");
    private static readonly int IrradianceId = Shader.PropertyToID("_IrradianceLut");
    private static readonly int FogTopId = Shader.PropertyToID("_FogTop");
    private static readonly int FogDensityId = Shader.PropertyToID("_FogDensity");

    // Height of the air's top above the ground, metres; matches ATMOSPHERE_HEIGHT in Atmosphere.hlsl.
    private const double AirThickness = 100_000.0;

    // The low haze fills the ground around the camera, averaged over this many metres, to a little above its mean, so
    // valleys fill and ridges stand clear. It is thickest while the sun is low and burns off as it climbs, and it fades
    // from view as the camera climbs away from it, since one layer stands in for every region's.
    private const double FogRegion = 20_000.0;
    private const double FogAboveMean = 40.0;
    private const float FogExtinction = 0.25f;
    private const double FogSunLow = 0.07;
    private const double FogSunHigh = 0.42;
    private const double FogFadeStart = 5_000.0;
    private const double FogFadeEnd = 20_000.0;
    private const double FogSettleSeconds = 2.0;

    private readonly CelestialBody _body;
    private readonly RenderTexture _transmittance;
    private readonly RenderTexture _multiScatter;
    private readonly RenderTexture _irradiance;
    private readonly Material _composite;
    private readonly AtmospherePass _pass;
    private readonly SkyViewPass _skyView;
    private readonly GraphicsBuffer _exposure;
    private readonly GraphicsBuffer _histogram;

    private double _fogTop = double.NaN;

    /// <summary>Whether the composite pass draws; the capture turns it off to time the rest of the frame.</summary>
    public bool Enabled { get; set; } = true;

    public Atmosphere(CelestialBody body, Shader luts, Shader sky, ComputeShader exposure, Vector3 sunDirection, Color sunIlluminance) {

        _body = body;

        Shader.SetGlobalFloat(PlanetRadiusId, (float)(body.Radius / MapSpace.MetresPerUnit));
        Shader.SetGlobalVector(SunDirectionId, sunDirection.normalized);
        Shader.SetGlobalVector(SunIlluminanceId, (Vector4)sunIlluminance);

        _transmittance = Table("Transmittance", 256, 64);
        _multiScatter = Table("Multiple Scattering", 32, 32);
        _irradiance = Table("Skylight", 64, 16);

        Material material = new Material(luts);
        CommandBuffer commands = new CommandBuffer { name = "Atmosphere Tables" };

        Render(commands, material, _transmittance, 0, TransmittanceId);
        Render(commands, material, _multiScatter, 1, MultiScatterId);
        Render(commands, material, _irradiance, 2, IrradianceId);

        Graphics.ExecuteCommandBuffer(commands);
        commands.Release();
        UnityEngine.Object.Destroy(material);

        _exposure = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float));
        _exposure.SetData(new[] { 1.0f });
        _histogram = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 64, sizeof(uint));
        _histogram.SetData(new uint[64]);

        _composite = new Material(sky);
        _pass = new AtmospherePass(_composite, exposure, _exposure, _histogram);
        _skyView = new SkyViewPass(_composite);
        RenderPipelineManager.beginCameraRendering += Enqueue;

    }

    private void Enqueue(ScriptableRenderContext context, Camera camera) {

        if (!Enabled || (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)) {

            return;

        }

        ScriptableRenderer renderer = camera.GetUniversalAdditionalCameraData().scriptableRenderer;

        renderer.EnqueuePass(_skyView);
        renderer.EnqueuePass(_pass);

    }

    /// <summary>The exposure the eye has adapted to, read back from the GPU; for diagnostics only, as it stalls.</summary>
    public float Exposure {

        get {

            float[] value = new float[1];
            _exposure.GetData(value);

            return value[0];

        }

    }

    /// <summary>Keeps the planet's centre and the low haze current, and lets the eye adapt while <paramref name="camera"/>
    /// is inside the air.</summary>
    public void Update(double time, Vector3 camera, float deltaSeconds) {

        Vector3 centre = MapSpace.ToScene(_body.PositionAt(time));

        Shader.SetGlobalVector(PlanetCentreId, centre);
        _pass.Adapting = (camera - centre).magnitude * MapSpace.MetresPerUnit < _body.Radius + AirThickness;

        UpdateFog(time, camera, deltaSeconds);

    }

    private void UpdateFog(double time, Vector3 camera, float deltaSeconds) {

        if (_body.Terrain is not { } terrain) {

            Shader.SetGlobalFloat(FogDensityId, 0.0f);

            return;

        }

        Vector3d fromCentre = MapSpace.Origin + new Vector3d(camera.x, camera.z, camera.y) * MapSpace.MetresPerUnit - _body.PositionAt(time);
        Vector3d up = fromCentre.Normalized;
        double top = Math.Max(terrain.HeightAt(_body.ToBodyFixed(up, time), FogRegion), 0.0) + FogAboveMean;

        _fogTop = double.IsNaN(_fogTop) ? top : top + (_fogTop - top) * Math.Exp(-deltaSeconds / FogSettleSeconds);

        double sunLow = 1.0 - SmoothStep(FogSunLow, FogSunHigh, Vector3d.Dot(up, Vector3d.UnitX));
        double near = 1.0 - SmoothStep(FogFadeStart, FogFadeEnd, fromCentre.Length - _body.Radius - _fogTop);

        Shader.SetGlobalFloat(FogTopId, (float)(_fogTop / MapSpace.MetresPerUnit));
        Shader.SetGlobalFloat(FogDensityId, FogExtinction * (float)(sunLow * near));

    }

    private static double SmoothStep(double from, double to, double x) {

        double t = Math.Clamp((x - from) / (to - from), 0.0, 1.0);

        return t * t * (3.0 - 2.0 * t);

    }

    private static RenderTexture Table(string name, int width, int height) {

        RenderTexture texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) {

            name = name,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,

        };

        texture.Create();

        return texture;

    }

    // Each table reads the ones before it, so each is published as a global before the next is drawn.
    private static void Render(CommandBuffer commands, Material material, RenderTexture target, int pass, int id) {

        commands.SetRenderTarget(target);
        commands.DrawProcedural(Matrix4x4.identity, material, pass, MeshTopology.Triangles, 3);
        commands.SetGlobalTexture(id, target);

    }

    public void Dispose() {

        RenderPipelineManager.beginCameraRendering -= Enqueue;
        UnityEngine.Object.Destroy(_composite);

        _transmittance.Release();
        _multiScatter.Release();
        _irradiance.Release();
        _exposure.Release();
        _histogram.Release();

    }

}
