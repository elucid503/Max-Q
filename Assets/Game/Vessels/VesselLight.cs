using System;

using MaxQ.Game.Map;
using MaxQ.Game.Planet;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;

using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Vessels;

/// <summary>Light on the vessel, which URP's Lit draws: the sun, dimmed through the penumbra as a body eclipses it, and the
/// sunlit body below filling the ambient probe and the reflection cubemap, so the hull's shade and metal show the planet.</summary>
public sealed class VesselLight : IDisposable {

    // Sunlit white ground shows as albedo * 2.4 (see MapView); URP's Lit has no 1/pi, so that is the light's intensity.
    private const float SunIntensity = 2.4f;

    // The sun's angular radius, radians.
    private const double SunRadius = 0.004_65;

    // Mean albedo and colour of each body as seen from orbit (Terra's from SeleneLight: oceans, land and cloud together).
    private const double TerraAlbedo = 0.3;
    private static readonly Vector3 TerraTint = new Vector3(0.88f, 0.99f, 1.2f) / 0.982f;
    private const double SeleneAlbedo = 0.12;

    private const int CubeSize = 32;
    private const int RefreshFrames = 8;

    private static readonly CubemapFace[] Faces = {

        CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY, CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ,

    };

    private readonly Sun _sun;
    private readonly CelestialBody[] _bodies;
    private readonly Vector3d _sunward;
    private readonly Cubemap _environment;
    private readonly Color[] _face = new Color[CubeSize * CubeSize];
    private int _frame;

    /// <summary>Share of the sun's disc the vessel sees, 0 to 1.</summary>
    public double Sunlit { get; private set; } = 1.0;

    public VesselLight(Sun sun, Vector3d sunward, params CelestialBody[] bodies) {

        _sun = sun;
        _sunward = sunward;
        _bodies = bodies;
        _environment = new Cubemap(CubeSize, TextureFormat.RGBAHalf, true) { name = "Vessel Environment", hideFlags = HideFlags.DontSave };

        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = _environment;
        RenderSettings.ambientMode = AmbientMode.Skybox;

    }

    /// <summary>Lights for a vessel at <paramref name="position"/> relative to the root body, over <paramref name="near"/>.</summary>
    public void Update(Vector3d position, CelestialBody near, double time) {

        double sunlit = 1.0;

        foreach (CelestialBody body in _bodies) {

            sunlit = Math.Min(sunlit, Unblocked(position, body, time));

        }

        Sunlit = sunlit;
        _sun.Intensity = SunIntensity * (float)sunlit;

        if (_frame++ % RefreshFrames == 0) {

            Surround(position - near.PositionAt(time), near, time);

        }

    }

    public void Dispose() => UnityEngine.Object.Destroy(_environment);

    // How much of the sun's disc clears a body's disc, both seen as discs from the point; linear across the penumbra.
    private double Unblocked(Vector3d position, CelestialBody body, double time) {

        Vector3d toBody = body.PositionAt(time) - position;
        double distance = toBody.Length;

        if (distance <= body.Radius) {

            return 0.0;

        }

        double bodyRadius = Math.Asin(body.Radius / distance);
        double apart = Math.Acos(Math.Clamp(Vector3d.Dot(toBody / distance, _sunward), -1.0, 1.0));

        return Math.Clamp((apart - (bodyRadius - SunRadius)) / (2.0 * SunRadius), 0.0, 1.0);

    }

    // The body as a Lambert sphere in an otherwise black sky, into the cubemap and the ambient probe, in scene axes.
    private void Surround(Vector3d fromBody, CelestialBody body, double time) {

        double radius = body.Radius;
        Vector3 tint = body.Parent == null ? TerraTint * (float)TerraAlbedo : Vector3.one * (float)SeleneAlbedo;
        SphericalHarmonicsL2 ambient = new SphericalHarmonicsL2();
        double texel = 2.0 / CubeSize;

        for (int f = 0; f < Faces.Length; f++) {

            for (int y = 0; y < CubeSize; y++) {

                for (int x = 0; x < CubeSize; x++) {

                    float u = (x + 0.5f) * (float)texel - 1.0f;
                    float v = (y + 0.5f) * (float)texel - 1.0f;
                    Vector3 scene = Direction(Faces[f], u, v);
                    float solidAngle = 4.0f / Mathf.Pow(1.0f + u * u + v * v, 1.5f) * (float)(texel * texel / 4.0);
                    Vector3d ray = new Vector3d(scene.x, scene.z, scene.y).Normalized;
                    Color radiance = Color.black;

                    // Nearest hit of the ray from the vessel with the body's sphere.
                    double b = Vector3d.Dot(fromBody, ray);
                    double c = fromBody.LengthSquared - radius * radius;
                    double disc = b * b - c;

                    if (disc >= 0.0 && -b - Math.Sqrt(disc) > 0.0) {

                        Vector3d normal = (fromBody + ray * (-b - Math.Sqrt(disc))) / radius;
                        float lit = (float)Math.Max(0.0, Vector3d.Dot(normal, _sunward));
                        Vector3 l = tint * (SunIntensity * lit);

                        radiance = new Color(l.x, l.y, l.z);
                        ambient.AddDirectionalLight(scene.normalized, radiance, solidAngle / Mathf.PI);

                    }

                    _face[y * CubeSize + x] = radiance;

                }

            }

            _environment.SetPixels(_face, Faces[f]);

        }

        _environment.Apply(true);
        RenderSettings.ambientProbe = ambient;

    }

    // Direct3D's cube layout, which Unity follows: each face's texel rows run top to bottom.
    private static Vector3 Direction(CubemapFace face, float u, float v) => face switch {

        CubemapFace.PositiveX => new Vector3(1.0f, -v, -u),
        CubemapFace.NegativeX => new Vector3(-1.0f, -v, u),
        CubemapFace.PositiveY => new Vector3(u, 1.0f, v),
        CubemapFace.NegativeY => new Vector3(u, -1.0f, -v),
        CubemapFace.PositiveZ => new Vector3(u, -v, 1.0f),
        _ => new Vector3(-u, -v, -1.0f),

    };

}
