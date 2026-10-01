using System;

using MaxQ.Game.Map;
using MaxQ.Game.Planet;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;

using UnityEngine;
using UnityEngine.Rendering;

namespace MaxQ.Game.Vessels;

/// <summary>Light on the vessel, which URP's Lit draws: the sun, dimmed through the penumbra as a body eclipses it and,
/// past Terra's limb, reddened and bent by its air; and the body below filling the ambient probe and the reflection
/// cubemap, its ground lit through the same air and its limb glowing with the sunlight the air scatters.</summary>
public sealed class VesselLight : IDisposable {

    // Sunlit white ground shows as albedo * 2.4 (see MapView); URP's Lit has no 1/pi, so that is the light's intensity.
    private const float SunIntensity = 2.4f;

    // The sun's angular radius, radians.
    private const double SunRadius = 0.004_65;

    // Mean albedo and colour of each body as seen from orbit (Terra's from SeleneLight: oceans, land and cloud together).
    private const double TerraAlbedo = 0.3;
    private static readonly Vector3 TerraTint = new Vector3(0.88f, 0.99f, 1.2f) / 0.982f;
    private const double SeleneAlbedo = 0.12;

    // Terra's air as Atmosphere.hlsl has it: kilometres, and coefficients per kilometre.
    private const double AirHeight = 100.0;
    private static readonly Vector3 RayleighScattering = new Vector3(5.802e-3f, 13.558e-3f, 33.1e-3f);
    private const double RayleighScaleHeight = 8.0;
    private const double MieScattering = 0.058;
    private const double MieExtinction = 0.065;
    private const double MieScaleHeight = 1.2;
    private const double MieG = 0.8;
    private static readonly Vector3 OzoneAbsorption = new Vector3(0.650e-3f, 1.881e-3f, 0.085e-3f);
    private const double OzoneCentre = 25.0;
    private const double OzoneHalfWidth = 15.0;

    // Air's refractivity at sea level in visible light; it thins with the air's density.
    private const double Refractivity = 2.93e-4;

    // Transmittance table: heights (spaced as squares, finer low down) by cosines of the zenith angle, and steps per entry.
    private const int TableHeights = 64;
    private const int TableAngles = 128;
    private const int TableSteps = 40;

    // The sun's disc as seen: samples across the limb and along it; and steps marching the sunlit air at the limb.
    private const int DiscRows = 64;
    private const int DiscColumns = 16;
    private const int LimbSteps = 16;

    // URP floors a light's squared distance at HALF_MIN, in scene units (km^2): within this many metres it shines flat.
    private const double FlatWithin = 7.812_5;

    /// <summary>Range, m, for a light made stronger by <see cref="Nearby"/>: a couple of its flat reaches, past which it
    /// would overlight.</summary>
    internal const double NearbyRange = 2.0 * FlatWithin;

    private const int CubeSize = 64;
    private const int RefreshFrames = 8;

    private static readonly CubemapFace[] Faces = {

        CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY, CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ,

    };

    private readonly Sun _sun;
    private readonly CelestialBody[] _bodies;
    private readonly CelestialBody _airy;
    private readonly Vector3[,] _transmittance;
    private readonly Vector3d _sunward;
    private readonly Cubemap _environment;
    private readonly Color[] _face = new Color[CubeSize * CubeSize];
    private int _frame;

    /// <summary>The sunlight reaching the vessel as a share of full sun, by luminance, 0 to 1.</summary>
    public double Sunlit { get; private set; } = 1.0;

    public VesselLight(Sun sun, Vector3d sunward, params CelestialBody[] bodies) {

        _sun = sun;
        _sunward = sunward;
        _bodies = bodies;
        _airy = Array.Find(bodies, body => body.Parent == null);
        _transmittance = Transmittance(_airy.Radius / 1_000.0);
        _environment = new Cubemap(CubeSize, TextureFormat.RGBAHalf, true) { name = "Vessel Environment", hideFlags = HideFlags.DontSave };

        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = _environment;
        RenderSettings.ambientMode = AmbientMode.Skybox;

    }

    /// <summary>Lights for a vessel at <paramref name="position"/> relative to the root body, over <paramref name="near"/>.</summary>
    public void Update(Vector3d position, CelestialBody near, double time) {

        Vector3 sunlight = Vector3.one;

        foreach (CelestialBody body in _bodies) {

            sunlight = Vector3.Scale(sunlight, body == _airy ? ThroughAir(position, body, time) : Vector3.one * (float)Unblocked(position, body, time));

        }

        float peak = Mathf.Max(sunlight.x, Mathf.Max(sunlight.y, sunlight.z));

        Sunlit = 0.2126 * sunlight.x + 0.7152 * sunlight.y + 0.0722 * sunlight.z;
        _sun.Intensity = SunIntensity * peak;

        // A light's colour is sRGB.
        _sun.Colour = peak > 0.0f ? new Color(sunlight.x / peak, sunlight.y / peak, sunlight.z / peak).gamma : Color.white;

        if (_frame++ % RefreshFrames == 0) {

            Surround(position - near.PositionAt(time), near, time);

        }

    }

    public void Dispose() => UnityEngine.Object.Destroy(_environment);

    /// <summary>How much stronger to make a light whose surfaces lie about <paramref name="distance"/> metres off, so its
    /// light lands right on them although URP lights everything within 7.8 m as if from 7.8 m.</summary>
    internal static float Nearby(double distance) => (float)Math.Max(1.0, FlatWithin * FlatWithin / (distance * distance));

    // How much of the sun's disc clears an airless body's disc, both seen as discs from the point; linear across the penumbra.
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

    // Sunlight past a body with air, summed over the sun's disc as it appears. Each ray grazes the limb at some height; the
    // air there lets it through along the whole path and bends it toward the body, so the sun sinks into the limb
    // flattened and reddening, and lingers a little after the ground has hidden it.
    private Vector3 ThroughAir(Vector3d position, CelestialBody body, double time) {

        Vector3d toBody = body.PositionAt(time) - position;
        double distance = toBody.Length;
        double radius = body.Radius;

        if (distance <= radius) {

            return Vector3.zero;

        }

        // Angles from the body's centre as seen: the sun's, the air's edge, the ground's, and the most a ray bends.
        double away = Math.Acos(Math.Clamp(Vector3d.Dot(toBody / distance, _sunward), -1.0, 1.0));
        double airEdge = Math.Asin(Math.Min((radius + AirHeight * 1_000.0) / distance, 1.0));
        double ground = Math.Asin(radius / distance);
        double bend = Bending(0.0, radius);

        if (away - SunRadius >= airEdge) {

            return Vector3.one;

        }

        if (away + SunRadius < ground - bend) {

            return Vector3.zero;

        }

        // Rows run outward from the body across the disc and as far as the most bent ray seen; a ray seen at an angle
        // left the sun that much less its bending.
        double rowStep = (2.0 * SunRadius + bend) / DiscRows;
        double columnStep = 2.0 * SunRadius / DiscColumns;
        Vector3 sum = Vector3.zero;
        int whole = 0;

        for (int row = 0; row < DiscRows; row++) {

            double seen = away - SunRadius + (row + 0.5) * rowStep;
            double height = distance * Math.Sin(seen) - radius;
            double left = height < 0.0 ? double.NaN : seen - Bending(height, radius);
            Vector3 through = height >= AirHeight * 1_000.0 || seen >= 0.5 * Math.PI ? Vector3.one : Squared(Transmitted(height / 1_000.0, 0.0));

            for (int column = 0; column < DiscColumns; column++) {

                double across = -SunRadius + (column + 0.5) * columnStep;

                whole += (seen - away) * (seen - away) + across * across <= SunRadius * SunRadius ? 1 : 0;

                if (height >= 0.0 && (left - away) * (left - away) + across * across <= SunRadius * SunRadius) {

                    sum += through;

                }

            }

        }

        return sum / whole;

    }

    // A ray grazing the limb at a height (m) bends by the air's refractivity there times sqrt(2 pi R / H), the share of its
    // path that runs through air of that density.
    private static double Bending(double height, double radius) =>
        Refractivity * Math.Sqrt(2.0 * Math.PI * radius / (RayleighScaleHeight * 1_000.0)) * Math.Exp(-height / (RayleighScaleHeight * 1_000.0));

    // The body as a Lambert sphere lit through its air, ringed by the sunlit air at its limb, in an otherwise black sky;
    // into the cubemap and the ambient probe, in scene axes.
    private void Surround(Vector3d fromBody, CelestialBody body, double time) {

        bool airy = body == _airy;
        Vector3 tint = airy ? TerraTint * (float)TerraAlbedo : Vector3.one * (float)SeleneAlbedo;
        Vector3d from = fromBody / 1_000.0;
        double radius = body.Radius / 1_000.0;
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
                    Vector3 l = Vector3.zero;

                    // Nearest hit of the ray from the vessel with the body's ground, then with its air's top.
                    double b = Vector3d.Dot(from, ray);
                    double groundDisc = b * b - from.LengthSquared + radius * radius;
                    double top = radius + AirHeight;
                    double topDisc = b * b - from.LengthSquared + top * top;

                    if (groundDisc >= 0.0 && -b - Math.Sqrt(groundDisc) > 0.0) {

                        Vector3d normal = (from + ray * (-b - Math.Sqrt(groundDisc))) / radius;
                        double mu = Vector3d.Dot(normal, _sunward);
                        Vector3 lit = mu <= 0.0 ? Vector3.zero : (airy ? Transmitted(0.0, mu) : Vector3.one) * (float)mu;

                        l = Vector3.Scale(tint, lit) * SunIntensity;

                    } else if (airy && topDisc > 0.0 && -b + Math.Sqrt(topDisc) > 0.0) {

                        l = Limb(from, ray, Math.Max(-b - Math.Sqrt(topDisc), 0.0), -b + Math.Sqrt(topDisc), radius);

                    }

                    Color radiance = new Color(l.x, l.y, l.z);

                    if (l != Vector3.zero) {

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

    // Sunlight the air scatters toward the vessel along a ray between two distances (km) that misses the ground: single
    // scattering, each step lit through the air toward the sun and dimmed on its way out.
    private Vector3 Limb(Vector3d from, Vector3d ray, double near, double far, double radius) {

        double cos = Vector3d.Dot(ray, _sunward);
        double rayleighPhase = 3.0 / (16.0 * Math.PI) * (1.0 + cos * cos);
        double g2 = MieG * MieG;
        double miePhase = 3.0 / (8.0 * Math.PI) * (1.0 - g2) * (1.0 + cos * cos) / ((2.0 + g2) * Math.Pow(Math.Max(1.0 + g2 - 2.0 * MieG * cos, 1e-4), 1.5));
        double step = (far - near) / LimbSteps;
        Vector3 depth = Vector3.zero;
        Vector3 light = Vector3.zero;

        for (int i = 0; i < LimbSteps; i++) {

            Vector3d point = from + ray * (near + (i + 0.5) * step);
            double r = point.Length;
            double altitude = r - radius;
            Vector3 extinction = Extinction(altitude) * (float)step;
            Vector3 scattering = RayleighScattering * (float)(Math.Exp(-altitude / RayleighScaleHeight) * rayleighPhase)
                + Vector3.one * (float)(MieScattering * Math.Exp(-altitude / MieScaleHeight) * miePhase);

            light += Vector3.Scale(Vector3.Scale(Exp(-(depth + 0.5f * extinction)), scattering), Transmitted(altitude, Vector3d.Dot(point / r, _sunward))) * (float)step;
            depth += extinction;

        }

        // The sun's illuminance above the air is pi times the light's intensity, as URP's Lit has no 1/pi.
        return light * (SunIntensity * Mathf.PI);

    }

    // Sunlight let through the air from a height (km) along a zenith angle's cosine to the air's top; none where the ground
    // stands in the way.
    private static Vector3[,] Transmittance(double radius) {

        Vector3[,] table = new Vector3[TableHeights, TableAngles];
        double top = radius + AirHeight;

        for (int i = 0; i < TableHeights; i++) {

            double h = (double)i / (TableHeights - 1);
            double r = radius + AirHeight * h * h;

            for (int j = 0; j < TableAngles; j++) {

                double mu = -1.0 + 2.0 * j / (TableAngles - 1);

                if (mu < 0.0 && r * r * (mu * mu - 1.0) + radius * radius >= 0.0) {

                    continue;

                }

                double length = -r * mu + Math.Sqrt(Math.Max(r * r * (mu * mu - 1.0) + top * top, 0.0));
                double step = length / TableSteps;
                Vector3 depth = Vector3.zero;

                for (int k = 0; k < TableSteps; k++) {

                    double t = (k + 0.5) * step;

                    depth += Extinction(Math.Sqrt(r * r + t * t + 2.0 * r * t * mu) - radius) * (float)step;

                }

                table[i, j] = Exp(-depth);

            }

        }

        return table;

    }

    private Vector3 Transmitted(double height, double mu) {

        double x = Math.Sqrt(Math.Clamp(height / AirHeight, 0.0, 1.0)) * (TableHeights - 1);
        double y = 0.5 * (Math.Clamp(mu, -1.0, 1.0) + 1.0) * (TableAngles - 1);
        int i = Math.Min((int)x, TableHeights - 2);
        int j = Math.Min((int)y, TableAngles - 2);
        float fx = (float)(x - i);
        float fy = (float)(y - j);

        return Vector3.Lerp(Vector3.Lerp(_transmittance[i, j], _transmittance[i + 1, j], fx), Vector3.Lerp(_transmittance[i, j + 1], _transmittance[i + 1, j + 1], fx), fy);

    }

    private static Vector3 Extinction(double altitude) {

        float rayleigh = (float)Math.Exp(-altitude / RayleighScaleHeight);
        float mie = (float)(MieExtinction * Math.Exp(-altitude / MieScaleHeight));
        float ozone = (float)Math.Max(0.0, 1.0 - Math.Abs(altitude - OzoneCentre) / OzoneHalfWidth);

        return RayleighScattering * rayleigh + new Vector3(mie, mie, mie) + OzoneAbsorption * ozone;

    }

    private static Vector3 Exp(Vector3 v) => new Vector3(Mathf.Exp(v.x), Mathf.Exp(v.y), Mathf.Exp(v.z));

    private static Vector3 Squared(Vector3 v) => Vector3.Scale(v, v);

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
