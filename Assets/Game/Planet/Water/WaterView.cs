using System;

using MaxQ.Game.Map;
using MaxQ.Game.Planet.Water.Surface;
using MaxQ.Game.Planet.Water.Waves;
using MaxQ.Sim.Bodies;
using MaxQ.Sim.Numerics;
using MaxQ.Sim.Ocean;

using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MaxQ.Game.Planet.Water;

/// <summary>A body's seas and lakes: keeps the wave frame under the camera, fills the waves for the open sea there from
/// the climatology, runs them each frame, publishes what the water and ground shaders need to weight the waves by the
/// sea where they fall, and adds the water's pass to every game and scene camera.</summary>
public sealed class WaterView : IDisposable {

    /// <summary>Month of the climatology shown: June, as the ground's climate is.</summary>
    public const int Month = 6;

    // A camera farther than this from the frame's origin (metres) has jumped, and the waves start afresh.
    private const double JumpDistance = 50_000.0;

    // The sea at the camera is refilled once its heights, periods or wind change by this share, or it turns this far.
    private const double Change = 0.01;
    private const double Turn = Math.PI / 180.0;

    // Foam drifts at a thirtieth of the wind's speed.
    private const double FoamDrift = 0.03;

    // Only the two longest cascades break into whitecaps; each raises half the coverage.
    private const int BreakingCascades = 2;

    // The sea's slow variation and foam's structure repeat over this many metres; must match PATTERN_PERIOD in
    // WaterSurface.hlsl.
    private const double PatternPeriod = 29_700.0;

    // The sea state map's texels: one a degree, rows from pole to pole; must match SEA_STATE_ROWS in WaterSurface.hlsl.
    private const int SeaStateColumns = 360;
    private const int SeaStateRows = 181;

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
    private static readonly int BandEdgesId = Shader.PropertyToID("_WaterBandEdges");
    private static readonly int FoamTextureId = Shader.PropertyToID("_WaterFoamTexture");
    private static readonly int PatternId = Shader.PropertyToID("_WaterPattern");

    private readonly CelestialBody _body;
    private readonly SeaState _seaState;
    private readonly WaveFrame _frame = new WaveFrame();
    private readonly WaveCascades _cascades;
    private readonly Material _copy;
    private readonly WaterPass _pass;
    private readonly Texture2D _seaMap;
    private readonly Texture2D _foam;

    // The cascades' tile coordinates of the frame's origin, two to a vector.
    private readonly Vector4[] _offsets = new Vector4[2];

    private bool _placed;
    private bool _jumped;
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
        _seaState = seaState;
        _cascades = new WaveCascades(waves);
        _copy = new Material(copy);
        _pass = new WaterPass(_copy);

        _seaMap = MapSeaState(seaState);
        _foam = FoamTexture.Create();

        Shader.SetGlobalTexture(SeaStateId, _seaMap);
        Shader.SetGlobalVector(BandEdgesId, Vector(i => i < WaveCascades.Cascades - 1 ? BandEdge(i) : 0.0));
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

        if (!_placed || (direction - _frame.Origin).Length * _body.Radius > JumpDistance) {

            _frame.Reset(direction);
            _placed = true;
            _jumped = true;

        } else {

            _frame.Follow(direction, _body.Radius);

        }

        // The open sea, as if nothing sheltered or froze it: the shaders weight the waves down where land or ice does.
        SeaConditions sea = _seaState.At(_frame.Origin, Month);
        double seaHeading = _frame.Heading(sea.WindEast, sea.WindNorth);
        double swellHeading = _frame.Heading(sea.SwellEast, sea.SwellNorth);

        if ((_jumped || Changed(sea, seaHeading, swellHeading)) && !_cascades.Busy) {

            Spectrum spectrum = Spectrum.For(sea, seaHeading, swellHeading);

            _cascades.Request(spectrum);
            _filledSea = sea;
            _filledSeaHeading = seaHeading;
            _filledSwellHeading = swellHeading;
            SeaHeight = spectrum.SignificantHeight;

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

        _cascades.Update(time, thresholds, _filledSeaHeading, FoamDrift * _filledSea.WindSpeed);

        Publish(time, bodyPosition, coverage);

    }

    private bool Changed(SeaConditions sea, double seaHeading, double swellHeading) {

        static bool Moved(double a, double b) => Math.Abs(a - b) > Change * Math.Max(Math.Abs(b), 0.1);
        static bool Turned(double a, double b) => Math.Abs(Math.IEEERemainder(a - b, 2.0 * Math.PI)) > Turn;

        return Moved(sea.WindSpeed, _filledSea.WindSpeed) || Moved(sea.SwellHeight, _filledSea.SwellHeight) || Moved(sea.SwellPeriod, _filledSea.SwellPeriod) ||
            Turned(seaHeading, _filledSeaHeading) || Turned(swellHeading, _filledSwellHeading);

    }

    private void Publish(double time, Vector3d bodyPosition, double coverage) {

        for (int i = 0; i < WaveCascades.Cascades; i++) {

            _frame.TileOffset(WaveCascades.Sizes[i], WaveCascades.Angles[i], out float x, out float y);
            _offsets[i / 2][i % 2 * 2] = x;
            _offsets[i / 2][i % 2 * 2 + 1] = y;

        }

        Shader.SetGlobalVector(OriginId, MapSpace.ToScene(bodyPosition + _body.FromBodyFixed(_frame.Origin * _body.Radius, time)));
        Shader.SetGlobalVector(EastId, MapSpace.Direction(_body.FromBodyFixed(_frame.East, time)));
        Shader.SetGlobalVector(NorthId, MapSpace.Direction(_body.FromBodyFixed(_frame.North, time)));
        Shader.SetGlobalVector(OffsetNearId, _offsets[0]);
        Shader.SetGlobalVector(OffsetFarId, _offsets[1]);

        _frame.PatternOffset(PatternPeriod, out float patternX, out float patternY);
        Shader.SetGlobalVector(PatternId, new Vector4(patternX, patternY, 0.0f, 0.0f));
        Shader.SetGlobalVector(BandEnergyId, Vector(BandEnergy));
        Shader.SetGlobalVector(WavelengthId, Vector(i => _cascades.MeanWavelength[i]));
        Shader.SetGlobalFloat(CoverageId, (float)coverage);
        Shader.SetGlobalTexture(FoamId, _cascades.Foam);

    }

    // The variance a cascade holds for the open sea at the camera, as the shaders reckon a sea's, so the weights come to
    // one there.
    private double BandEnergy(int cascade) {

        double sea = _filledSea.SeaHeight * _filledSea.SeaHeight * BandShare(cascade, _filledSea.SeaPeriod);
        double swell = _filledSea.SwellHeight * _filledSea.SwellHeight * BandShare(cascade, _filledSea.SwellPeriod);

        return (sea + swell) / 16.0;

    }

    // Share of a system's variance a cascade holds, for its peak period: in Pierson and Moskowitz's spectrum the share
    // below a frequency has a closed form. WaterSurface.hlsl's BandShares matches.
    private static double BandShare(int cascade, double period) => Below(cascade, period) - Below(cascade - 1, period);

    private static double Below(int cascade, double period) => cascade < 0 ? 0.0 : cascade == WaveCascades.Cascades - 1 ? 1.0 :
        Math.Exp(-1.25 * Math.Pow(2.0 * Math.PI / Math.Max(period, 0.5) / BandEdge(cascade), 4.0));

    // The frequency (rad/s) of the shortest waves a cascade holds.
    private static double BandEdge(int cascade) => Spectrum.Frequency(WaveCascades.BandHigh(cascade));

    // The month's open sea for the shaders: wind speed, swell height and period, and ice. Each texel's centre lies at its
    // column's longitude and on its row of latitude, pole to pole.
    private static Texture2D MapSeaState(SeaState seaState) {

        Color[] texels = new Color[SeaStateColumns * SeaStateRows];

        for (int row = 0; row < SeaStateRows; row++) {

            double latitude = Math.PI * (0.5 - row / (double)(SeaStateRows - 1));

            for (int column = 0; column < SeaStateColumns; column++) {

                double longitude = 2.0 * Math.PI * ((column + 0.5) / SeaStateColumns - 0.5);
                Vector3d direction = new Vector3d(Math.Cos(latitude) * Math.Cos(longitude), Math.Cos(latitude) * Math.Sin(longitude), Math.Sin(latitude));
                SeaConditions sea = seaState.At(direction, Month);

                texels[row * SeaStateColumns + column] = new Color((float)sea.WindSpeed, (float)sea.SwellHeight, (float)sea.SwellPeriod, (float)sea.Ice);

            }

        }

        Texture2D map = new Texture2D(SeaStateColumns, SeaStateRows, GraphicsFormat.R16G16B16A16_SFloat, TextureCreationFlags.None) {

            name = "Sea State",
            filterMode = FilterMode.Bilinear,

        };

        map.SetPixels(texels);
        map.Apply(false, true);

        return map;

    }

    // Monahan and O'Muircheartaigh's whitecap coverage for a wind of speed metres per second; matches WaterSurface.hlsl.
    private static double WhitecapCoverage(double wind) => Math.Min(3.84e-6 * Math.Pow(Math.Max(wind, 0.0), 3.41), 0.3);

    // The standard normal's quantile, in Tukey's lambda approximation (within a few hundredths down to p = 0.001).
    private static double InverseNormal(double p) {

        p = Math.Clamp(p, 1e-12, 1.0 - 1e-12);

        return 4.91 * (Math.Pow(p, 0.14) - Math.Pow(1.0 - p, 0.14));

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
        UnityEngine.Object.Destroy(_foam);

    }

}
