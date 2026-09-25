using System;

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet;

/// <summary>The sun as URP's main light, casting the cascaded shadows the ground and the air sample. The cascades are refit
/// to the camera's altitude every frame, so the nearest covers the ground at the camera's feet and the farthest the horizon.</summary>
public sealed class Sun {

    // Nearest cascade reach in kilometres at pad level, and the height of the tallest ground (km) that shows past the horizon.
    private const float NearestReach = 0.06f;
    private const float HighestGround = 1.8f;

    private readonly UniversalRenderPipelineAsset _pipeline;
    private readonly Light _light;

    /// <summary>Whether the sun casts shadows; the capture turns them off to time them.</summary>
    public bool Shadows {

        get => _light.shadows != LightShadows.None;
        set => _light.shadows = value ? LightShadows.Soft : LightShadows.None;

    }

    /// <summary><paramref name="direction"/> points at the sun, in scene axes.</summary>
    public Sun(Vector3 direction) {

        _light = new GameObject("Sun").AddComponent<Light>();
        _light.type = LightType.Directional;
        _light.intensity = 1.6f;
        _light.color = new Color(1.0f, 0.97f, 0.92f);
        _light.shadows = LightShadows.Soft;
        _light.shadowStrength = 1.0f;
        _light.transform.rotation = Quaternion.LookRotation(-direction);

        // A private copy, so refitting the cascades never dirties the project's pipeline asset.
        _pipeline = UnityEngine.Object.Instantiate((UniversalRenderPipelineAsset)GraphicsSettings.defaultRenderPipeline);
        QualitySettings.renderPipeline = _pipeline;

    }

    /// <summary>Fits the cascades to a camera <paramref name="altitude"/> kilometres above the ground of a body of
    /// <paramref name="radius"/> kilometres: out to the horizon of the highest ground, in geometric steps from the ground below.</summary>
    public void Fit(double altitude, double radius) {

        double height = Math.Max(altitude, 0.0);
        double horizon = Math.Sqrt(height * (2.0 * radius + height)) + Math.Sqrt(2.0 * radius * HighestGround);
        double nearest = Math.Min(Math.Max(NearestReach, 1.5 * height), 0.5 * horizon);
        double ratio = Math.Pow(horizon / nearest, 1.0 / 3.0);

        _pipeline.shadowDistance = (float)horizon;
        _pipeline.cascade4Split = new Vector3((float)(1.0 / (ratio * ratio * ratio)), (float)(1.0 / (ratio * ratio)), (float)(1.0 / ratio));

    }

}
