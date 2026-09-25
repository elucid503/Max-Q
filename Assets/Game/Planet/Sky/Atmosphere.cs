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

    // Height of the air's top above the ground, metres; matches ATMOSPHERE_HEIGHT in Atmosphere.hlsl.
    private const double AirThickness = 100_000.0;

    private readonly CelestialBody _body;
    private readonly RenderTexture _transmittance;
    private readonly RenderTexture _multiScatter;
    private readonly RenderTexture _irradiance;
    private readonly Material _composite;
    private readonly AtmospherePass _pass;
    private readonly SkyViewPass _skyView;
    private readonly GraphicsBuffer _exposure;
    private readonly GraphicsBuffer _histogram;

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

    /// <summary>Keeps the planet's centre current, and lets the eye adapt while <paramref name="camera"/> is inside the air.</summary>
    public void Update(double time, Vector3 camera) {

        Vector3 centre = MapSpace.ToScene(_body.PositionAt(time));

        Shader.SetGlobalVector(PlanetCentreId, centre);
        _pass.Adapting = (camera - centre).magnitude * MapSpace.MetresPerUnit < _body.Radius + AirThickness;

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
