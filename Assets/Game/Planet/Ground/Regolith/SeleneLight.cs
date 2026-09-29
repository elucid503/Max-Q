using System;

using MaxQ.Game.Map;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;

using UnityEngine;

namespace MaxQ.Game.Planet.Ground.Regolith;

/// <summary>Publishes Selene's lighting globals: its centre, earthshine, and the horizon glow near its ground.</summary>
public sealed class SeleneLight {

    private static readonly int CentreId = Shader.PropertyToID("_SeleneCentre");
    private static readonly int RadiusId = Shader.PropertyToID("_SeleneRadius");
    private static readonly int EarthshineId = Shader.PropertyToID("_Earthshine");
    private static readonly int EarthshineDirectionId = Shader.PropertyToID("_EarthshineDirection");
    private static readonly int HorizonGlowId = Shader.PropertyToID("_HorizonGlow");

    // Terra's geometric albedo and light colour (unit luminance).
    private const double TerraAlbedo = 0.3;
    private static readonly Vector3 TerraTint = new Vector3(0.88f, 0.99f, 1.2f) / 0.982f;

    // Glow radiance as a share of sunlit white ground's, and the camera altitude (m) it fades out over.
    private const float HorizonGlow = 1.5e-6f;
    private const double GlowFade = 20_000.0;

    private readonly CelestialBody _selene;
    private readonly CelestialBody _terra;
    private readonly Vector3 _sunIlluminance;

    public SeleneLight(CelestialBody selene, CelestialBody terra, Color sunIlluminance) {

        _selene = selene;
        _terra = terra;
        _sunIlluminance = new Vector3(sunIlluminance.r, sunIlluminance.g, sunIlluminance.b);

        Shader.SetGlobalFloat(RadiusId, (float)(selene.Radius / MapSpace.MetresPerUnit));

    }

    /// <summary><paramref name="altitude"/> is the camera's height (m) over Selene, infinite elsewhere.</summary>
    public void Update(double time, Vector3d sunward, double altitude) {

        Vector3d selene = _selene.PositionAt(time);
        Vector3d terra = _terra.PositionAt(time);
        Vector3d toTerra = terra - selene;
        double distance = toTerra.Length;

        // Terra as a Lambert sphere at its phase angle.
        double phase = Math.Acos(Math.Clamp(Vector3d.Dot(sunward, -toTerra / distance), -1.0, 1.0));
        double lambert = (Math.Sin(phase) + (Math.PI - phase) * Math.Cos(phase)) / Math.PI;
        double share = TerraAlbedo * lambert * Math.Pow(_terra.Radius / distance, 2.0);
        Vector3 earthshine = Vector3.Scale(_sunIlluminance, TerraTint) * (float)share;

        Shader.SetGlobalVector(CentreId, MapSpace.ToScene(selene));
        Shader.SetGlobalVector(EarthshineId, earthshine);
        Shader.SetGlobalVector(EarthshineDirectionId, MapSpace.Direction(toTerra / distance));
        Shader.SetGlobalFloat(HorizonGlowId, HorizonGlow * (float)Math.Clamp(1.0 - altitude / GlowFade, 0.0, 1.0));

    }

}
