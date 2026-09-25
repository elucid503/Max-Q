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

    private readonly CelestialBody _body;
    private readonly RenderTexture _transmittance;
    private readonly RenderTexture _multiScatter;
    private readonly RenderTexture _irradiance;
    private readonly Material _composite;
    private readonly AtmospherePass _pass;
    private readonly SkyViewPass _skyView;

    /// <summary>Whether the composite pass draws; the capture turns it off to time the rest of the frame.</summary>
    public bool Enabled { get; set; } = true;

    public Atmosphere(CelestialBody body, Shader luts, Shader sky, Vector3 sunDirection, Color sunIlluminance) {

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

        _composite = new Material(sky);
        _pass = new AtmospherePass(_composite);
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

    public void Update(double time) => Shader.SetGlobalVector(PlanetCentreId, MapSpace.ToScene(_body.PositionAt(time)));

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

    }

}
