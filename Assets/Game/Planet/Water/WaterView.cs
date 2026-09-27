using System;

using MaxQ.Game.Map;
using MaxQ.Game.Planet.Water.Surface;
using MaxQ.Game.Planet.Water.Waves;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Ocean;
using MaxQ.Sim.Surface;

using Terrain = MaxQ.Sim.Surface.Terrain;

using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet.Water;

/// <summary>A body's seas, lakes and rivers: keeps the wave frame under the camera, fills the waves for the open sea
/// there from the climatology, runs them each frame, publishes what the water and ground shaders need to weight the
/// waves by the sea where they fall, and adds the water's pass to every game and scene camera.</summary>
public sealed class WaterView : IDisposable {

    /// <summary>Month of the climatology shown: June, as the satellite's land, snow and ice are.</summary>
    public const int Month = 6;

    // A camera farther than this from the frame's origin (metres) has jumped, and the waves start afresh.
    private const double JumpDistance = 50_000.0;

    // The sea at the camera is refilled once its heights, periods or wind change by this share, or it turns this far.
    private const double Change = 0.01;
    private const double Turn = Math.PI / 180.0;

    // Foam drifts at a thirtieth of the wind's speed; foam lasts about four seconds, its fresh brightness under one.
    private const double FoamDrift = 0.03;
    private const double FoamLife = 4.0;
    private const double FreshLife = 0.6;

    // Only the two longest cascades break into whitecaps; each raises half the coverage.
    private const int BreakingCascades = 2;

    // The sea's slow variation and foam's structure repeat over this many metres; must match PATTERN_PERIOD in
    // WaterSurface.hlsl.
    private const double PatternPeriod = 29_700.0;

    // Shares of each system's variance per cascade, by peak period from one second to LongestPeriod in log steps.
    private const int BandPeriods = 64;
    private const double LongestPeriod = 25.0;

    private static readonly int OriginId = Shader.PropertyToID("_WaterOrigin");
    private static readonly int EastId = Shader.PropertyToID("_WaterEast");
    private static readonly int NorthId = Shader.PropertyToID("_WaterNorth");
    private static readonly int OffsetNearId = Shader.PropertyToID("_WaterOffsetNear");
    private static readonly int OffsetFarId = Shader.PropertyToID("_WaterOffsetFar");
    private static readonly int SizesId = Shader.PropertyToID("_WaterSizes");
    private static readonly int CosId = Shader.PropertyToID("_WaterCos");
    private static readonly int SinId = Shader.PropertyToID("_WaterSin");
    private static readonly int BandEnergyId = Shader.PropertyToID("_WaterBandEnergy");
    private static readonly int WavelengthId = Shader.PropertyToID("_WaterWavelength");
    private static readonly int CoverageId = Shader.PropertyToID("_WaterCoverage");
    private static readonly int DisplacementId = Shader.PropertyToID("_WaterDisplacement");
    private static readonly int DerivativesId = Shader.PropertyToID("_WaterDerivatives");
    private static readonly int MomentsId = Shader.PropertyToID("_WaterMoments");
    private static readonly int FoamId = Shader.PropertyToID("_WaterFoam");
    private static readonly int SeaStateId = Shader.PropertyToID("_WaterSeaState");
    private static readonly int SwellId = Shader.PropertyToID("_WaterSwell");
    private static readonly int BandsId = Shader.PropertyToID("_WaterBands");
    private static readonly int TimeId = Shader.PropertyToID("_WaterTime");
    private static readonly int FoamTextureId = Shader.PropertyToID("_WaterFoamTexture");
    private static readonly int PatternId = Shader.PropertyToID("_WaterPattern");

    private readonly CelestialBody _body;
    private readonly Terrain _terrain;
    private readonly SeaState _seaState;
    private readonly WaveFrame _frame = new WaveFrame();
    private readonly WaveCascades _cascades;
    private readonly Material _copy;
    private readonly WaterPass _pass;
    private readonly Texture2D _seaMap;
    private readonly Texture2D _swellMap;
    private readonly Texture2D _bands;
    private readonly Texture2D _foam;
    private readonly float[,] _seaShares = new float[WaveCascades.Cascades, BandPeriods];
    private readonly float[,] _swellShares = new float[WaveCascades.Cascades, BandPeriods];

    private bool _placed;
    private bool _jumped;
    private bool _filled;
    private SeaConditions _filledSea;
    private double _filledSeaHeading;
    private double _filledSwellHeading;

    /// <summary>Whether the water pass draws; the capture turns it off to time the rest of the frame.</summary>
    public bool Hidden { get; set; }

    /// <summary>Significant height (m) of the open sea under the camera, and of the waves the cascades hold for it.</summary>
    public double SeaHeight { get; private set; }

    public double DrawnHeight => 4.0 * Math.Sqrt(math.csum(_cascades.HeightVariance));

    public WaterView(CelestialBody body, SeaState seaState, ComputeShader waves, Shader copy) {

        _body = body;
        _terrain = body.Terrain ?? throw new ArgumentException($"{body.Name} has no terrain", nameof(body));
        _seaState = seaState;
        _cascades = new WaveCascades(waves);
        _copy = new Material(copy);
        _pass = new WaterPass(_copy);

        _seaMap = new Texture2D(720, 361, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None) { name = "Sea State", filterMode = FilterMode.Bilinear };
        _swellMap = new Texture2D(720, 361, GraphicsFormat.R16G16_SFloat, TextureCreationFlags.None) { name = "Swell", filterMode = FilterMode.Bilinear };
        _bands = new Texture2D(BandPeriods, 2, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None) { name = "Wave Bands", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };

        _foam = FoamTexture.Create();

        MapSeaState();
        TabulateBands();

        Shader.SetGlobalTexture(SeaStateId, _seaMap);
        Shader.SetGlobalTexture(SwellId, _swellMap);
        Shader.SetGlobalTexture(BandsId, _bands);
        Shader.SetGlobalTexture(FoamTextureId, _foam);
        Shader.SetGlobalVector(SizesId, Vector(i => WaveCascades.Sizes[i]));
        Shader.SetGlobalVector(CosId, Vector(i => Math.Cos(WaveCascades.Angles[i])));
        Shader.SetGlobalVector(SinId, Vector(i => Math.Sin(WaveCascades.Angles[i])));
        Shader.SetGlobalTexture(DisplacementId, _cascades.Displacement);
        Shader.SetGlobalTexture(DerivativesId, _cascades.Derivatives);
        Shader.SetGlobalTexture(MomentsId, _cascades.Moments);

        RenderPipelineManager.beginCameraRendering += Enqueue;

    }

    /// <summary>Starts the waves afresh where the camera next stands; the view has jumped there.</summary>
    public void Jump() => _placed = false;

    /// <summary>Carries the wave frame with <paramref name="camera"/>, fills the waves when the sea under it has
    /// changed, and runs them to sim <paramref name="time"/> (s).</summary>
    public void Update(double time, Camera camera) {

        Vector3d bodyPosition = _body.PositionAt(time);
        Vector3 cameraScene = camera.transform.position;
        Vector3d cameraSim = MapSpace.Origin + new Vector3d(cameraScene.x, cameraScene.z, cameraScene.y) * MapSpace.MetresPerUnit;
        Vector3d direction = _body.ToBodyFixed(cameraSim - bodyPosition, time).Normalized;

        if (!_placed || (direction - _frame.Origin).Length * _terrain.Radius > JumpDistance) {

            _frame.Reset(direction);
            _placed = true;
            _jumped = true;

        } else {

            _frame.Follow(direction, _terrain.Radius);

        }

        // The open sea, as if nothing sheltered or froze it: the shaders weight the waves down where land or ice does.
        SeaConditions sea = _seaState.At(_frame.Origin, Month) with { Ice = 0.0 };
        double seaHeading = _frame.Heading(sea.SeaEast, sea.SeaNorth);
        double swellHeading = _frame.Heading(sea.SwellEast, sea.SwellNorth);

        if ((_jumped || Changed(sea, seaHeading, swellHeading)) && !_cascades.Busy) {

            _cascades.Request(Spectrum.For(sea, seaHeading, swellHeading));
            _filledSea = sea;
            _filledSeaHeading = seaHeading;
            _filledSwellHeading = swellHeading;
            _filled = true;
            SeaHeight = Spectrum.For(sea, seaHeading, swellHeading).SignificantHeight;

        }

        if (_jumped) {

            _cascades.Settle();
            _jumped = false;

        }

        double coverage = WhitecapCoverage(_filledSea.WindSpeed);
        float4 thresholds = new float4(-1e3f);

        for (int i = 0; i < BreakingCascades; i++) {

            thresholds[i] = (float)(1.0 + Math.Sqrt(_cascades.SlopeVariance[i]) * InverseNormal(0.5 * coverage));

        }

        float2 keep = new float2((float)Math.Exp(-WaveCascades.FoamStep / FoamLife), (float)Math.Exp(-WaveCascades.FoamStep / FreshLife));

        _cascades.Update(time, thresholds, _frame.Heading(_filledSea.WindEast, _filledSea.WindNorth), FoamDrift * _filledSea.WindSpeed, keep);

        Publish(time, bodyPosition, coverage);

    }

    private bool Changed(SeaConditions sea, double seaHeading, double swellHeading) {

        if (!_filled) {

            return true;

        }

        static bool Moved(double a, double b) => Math.Abs(a - b) > Change * Math.Max(Math.Abs(b), 0.1);
        static bool Turned(double a, double b) => Math.Abs(Math.IEEERemainder(a - b, 2.0 * Math.PI)) > Turn;

        return Moved(sea.WindSpeed, _filledSea.WindSpeed) || Moved(sea.SeaHeight, _filledSea.SeaHeight) || Moved(sea.SeaPeriod, _filledSea.SeaPeriod) ||
            Moved(sea.SwellHeight, _filledSea.SwellHeight) || Moved(sea.SwellPeriod, _filledSea.SwellPeriod) ||
            Turned(seaHeading, _filledSeaHeading) || Turned(swellHeading, _filledSwellHeading);

    }

    private void Publish(double time, Vector3d bodyPosition, double coverage) {

        Vector4 near = Vector4.zero;
        Vector4 far = Vector4.zero;

        for (int i = 0; i < WaveCascades.Cascades; i++) {

            _frame.TileOffset(WaveCascades.Sizes[i], WaveCascades.Angles[i], out float x, out float y);

            if (i < 2) {

                near[2 * i] = x;
                near[2 * i + 1] = y;

            } else {

                far[2 * i - 4] = x;
                far[2 * i - 3] = y;

            }

        }

        Shader.SetGlobalVector(OriginId, MapSpace.ToScene(bodyPosition + _body.FromBodyFixed(_frame.Origin * _terrain.Radius, time)));
        Shader.SetGlobalVector(EastId, MapSpace.Direction(_body.FromBodyFixed(_frame.East, time)));
        Shader.SetGlobalVector(NorthId, MapSpace.Direction(_body.FromBodyFixed(_frame.North, time)));
        Shader.SetGlobalVector(OffsetNearId, near);
        Shader.SetGlobalVector(OffsetFarId, far);

        _frame.PatternOffset(PatternPeriod, out float patternX, out float patternY);
        Shader.SetGlobalVector(PatternId, new Vector4(patternX, patternY, 0.0f, 0.0f));
        Shader.SetGlobalVector(BandEnergyId, Vector(BandEnergy));
        Shader.SetGlobalVector(WavelengthId, Vector(i => _cascades.MeanWavelength[i]));
        Shader.SetGlobalFloat(CoverageId, (float)coverage);
        Shader.SetGlobalTexture(FoamId, _cascades.Foam);

        Shader.SetGlobalFloat(TimeId, (float)(time - Math.Floor(time / Spectrum.LoopSeconds) * Spectrum.LoopSeconds));

    }

    // The variance a cascade holds for the open sea at the camera, from the same shares the shaders weight by, so the
    // weights come to one there.
    private double BandEnergy(int cascade) {

        double sea = _filledSea.SeaHeight * _filledSea.SeaHeight * Share(_seaShares, cascade, _filledSea.SeaPeriod);
        double swell = _filledSea.SwellHeight * _filledSea.SwellHeight * Share(_swellShares, cascade, _filledSea.SwellPeriod);

        return (sea + swell) / 16.0;

    }

    // Linear between tabulated periods, as the shaders' bilinear lookup reads the same table.
    private static double Share(float[,] shares, int cascade, double period) {

        double x = Math.Clamp(Math.Log(Math.Max(period, 1.0)) / Math.Log(LongestPeriod), 0.0, 1.0) * (BandPeriods - 1);
        int i = Math.Min((int)x, BandPeriods - 2);

        return shares[cascade, i] + (shares[cascade, i + 1] - shares[cascade, i]) * (x - i);

    }

    // The month's sea state on a 0.5 degree grid for the shaders: each texel's centre at its cell's longitude and at
    // the grid's rows of latitude, pole to pole.
    private void MapSeaState() {

        Color[] sea = new Color[720 * 361];
        Color[] swell = new Color[720 * 361];

        for (int row = 0; row < 361; row++) {

            double latitude = (90.0 - 0.5 * row) * Math.PI / 180.0;

            for (int column = 0; column < 720; column++) {

                double longitude = (-180.0 + 0.5 * (column + 0.5)) * Math.PI / 180.0;
                Vector3d direction = new Vector3d(Math.Cos(latitude) * Math.Cos(longitude), Math.Cos(latitude) * Math.Sin(longitude), Math.Sin(latitude));
                SeaConditions conditions = _seaState.At(direction, Month);
                int texel = row * 720 + column;

                sea[texel] = new Color((float)conditions.WindSpeed, (float)conditions.SeaHeight, (float)conditions.SeaPeriod, (float)conditions.Ice);
                swell[texel] = new Color((float)conditions.SwellHeight, (float)conditions.SwellPeriod, 0.0f, 0.0f);

            }

        }

        _seaMap.SetPixels(sea);
        _seaMap.Apply(false, true);
        _swellMap.SetPixels(swell);
        _swellMap.Apply(false, true);

    }

    // The share of a system's variance in each cascade's band of wavenumbers, for each tabulated peak period, the four
    // cascades in a texel's channels: the wind's sea (peak enhancement 3.3) in the first row, swell (6) in the second.
    private void TabulateBands() {

        Color[] texels = new Color[BandPeriods * 2];

        for (int j = 0; j < BandPeriods; j++) {

            double period = Math.Pow(LongestPeriod, j / (double)(BandPeriods - 1));

            for (int cascade = 0; cascade < WaveCascades.Cascades; cascade++) {

                _seaShares[cascade, j] = (float)BandShare(Spectrum.System(4.0, period, 3.3, 0.0), cascade);
                _swellShares[cascade, j] = (float)BandShare(Spectrum.System(4.0, period, 6.0, 0.0), cascade);

            }

            texels[j] = new Color(_seaShares[0, j], _seaShares[1, j], _seaShares[2, j], _seaShares[3, j]);
            texels[BandPeriods + j] = new Color(_swellShares[0, j], _swellShares[1, j], _swellShares[2, j], _swellShares[3, j]);

        }

        _bands.SetPixels(texels);
        _bands.Apply(false, true);

    }

    private static double BandShare(WaveSystem system, int cascade) {

        const int steps = 4_000;
        double low = Math.Log(0.2 * system.PeakFrequency);
        double high = Math.Log(80.0 * system.PeakFrequency);
        double step = (high - low) / steps;
        double from = Spectrum.Frequency(WaveCascades.BandLow(cascade));
        double to = double.IsPositiveInfinity(WaveCascades.BandHigh(cascade)) ? double.MaxValue : Spectrum.Frequency(WaveCascades.BandHigh(cascade));
        double band = 0.0;
        double total = 0.0;

        for (int i = 0; i < steps; i++) {

            double omega = Math.Exp(low + (i + 0.5) * step);
            double energy = Spectrum.Energy(system, omega) * omega * step;

            total += energy;
            band += omega >= from && omega < to ? energy : 0.0;

        }

        return total > 0.0 ? band / total : 0.0;

    }

    // Monahan and O'Muircheartaigh's whitecap coverage for a wind of speed metres per second; matches WaterSurface.hlsl.
    private static double WhitecapCoverage(double wind) => Math.Min(3.84e-6 * Math.Pow(Math.Max(wind, 0.0), 3.41), 0.3);

    // The standard normal's quantile (Acklam's rational approximation, good to about 1e-9).
    private static double InverseNormal(double p) {

        p = Math.Clamp(p, 1e-12, 1.0 - 1e-12);

        double[] a = { -39.69683028665376, 220.9460984245205, -275.9285104469687, 138.357751867269, -30.66479806614716, 2.506628277459239 };
        double[] b = { -54.47609879822406, 161.5858368580409, -155.6989798598866, 66.80131188771972, -13.28068155288572 };
        double[] c = { -0.007784894002430293, -0.3223964580411365, -2.400758277161838, -2.549732539343734, 4.374664141464968, 2.938163982698783 };
        double[] d = { 0.007784695709041462, 0.3224671290700398, 2.445134137142996, 3.754408661907416 };

        if (p < 0.02425) {

            double q = Math.Sqrt(-2.0 * Math.Log(p));

            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);

        }

        if (p > 1.0 - 0.02425) {

            return -InverseNormal(1.0 - p);

        }

        double r = p - 0.5;
        double s = r * r;

        return (((((a[0] * s + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r / (((((b[0] * s + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1.0);

    }

    private static Vector4 Vector(Func<int, double> value) => new Vector4((float)value(0), (float)value(1), (float)value(2), (float)value(3));

    private void Enqueue(ScriptableRenderContext context, Camera camera) {

        if (Hidden || (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)) {

            return;

        }

        camera.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(_pass);

    }

    public void Dispose() {

        RenderPipelineManager.beginCameraRendering -= Enqueue;
        _cascades.Dispose();
        UnityEngine.Object.Destroy(_copy);
        UnityEngine.Object.Destroy(_seaMap);
        UnityEngine.Object.Destroy(_swellMap);
        UnityEngine.Object.Destroy(_bands);
        UnityEngine.Object.Destroy(_foam);

    }

}
