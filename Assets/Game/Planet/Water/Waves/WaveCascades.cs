using System;

using MaxQ.Sim.Ocean;

using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MaxQ.Game.Planet.Water.Waves;

/// <summary>The sea's waves on the GPU: four FFT cascades, each a tile of the sea holding one band of wavelengths, their
/// sizes about 13.4 apart and none a multiple of another, turned against each other so their grids never line up.
/// Fills the initial spectra on the workers when the sea changes (the same waves re-weighted, so a small change never
/// shows) on a single worker, so a refill never holds up the engine's own jobs, and each frame evolves, transforms and
/// assembles the displacement, slopes and foam the surface samples.</summary>
internal sealed class WaveCascades : IDisposable {

    public const int Size = 256;
    public const int Cascades = 4;

    private const double Degree = Math.PI / 180.0;

    /// <summary>Tile sizes, metres.</summary>
    public static readonly double[] Sizes = { 1_597.0, 119.3, 8.93, 0.668 };

    /// <summary>Each tile's turn from the wave frame, radians.</summary>
    public static readonly double[] Angles = { 0.0, 23.4 * Degree, 47.1 * Degree, 71.3 * Degree };

    // Foam lasts about four seconds, its fresh brightness under one.
    private const double FoamLife = 4.0;
    private const double FreshLife = 0.6;

    // Foam moves on by at most this much sim time a frame; a fresh start runs it over the seconds before in steps.
    private const double LongestStep = 0.25;
    private const int PrewarmSteps = 24;
    private const double PrewarmStep = 1.0 / 6.0;

    private static readonly int InitialId = Shader.PropertyToID("_Initial");
    private static readonly int CascadeSizeId = Shader.PropertyToID("_CascadeSize");
    private static readonly int LoopPhaseId = Shader.PropertyToID("_LoopPhase");
    private static readonly int SpectrumAId = Shader.PropertyToID("_SpectrumA");
    private static readonly int SpectrumBId = Shader.PropertyToID("_SpectrumB");
    private static readonly int DisplacementId = Shader.PropertyToID("_Displacement");
    private static readonly int DerivativesId = Shader.PropertyToID("_Derivatives");
    private static readonly int MomentsId = Shader.PropertyToID("_Moments");
    private static readonly int FoamSourceId = Shader.PropertyToID("_FoamSource");
    private static readonly int FoamId = Shader.PropertyToID("_Foam");
    private static readonly int CascadeAngleId = Shader.PropertyToID("_CascadeAngle");
    private static readonly int FoamThresholdId = Shader.PropertyToID("_FoamThreshold");
    private static readonly int FoamStepId = Shader.PropertyToID("_FoamStep");

    private readonly ComputeShader _shader;
    private readonly int _evolve;
    private readonly int _rows;
    private readonly int _columns;
    private readonly int _assemble;

    private readonly GraphicsBuffer _initial;
    private readonly RenderTexture _spectrumA;
    private readonly RenderTexture _spectrumB;
    private readonly RenderTexture[] _foam = new RenderTexture[2];
    private readonly CommandBuffer _commands = new CommandBuffer { name = "Waves" };

    private NativeArray<float4> _staging;
    private NativeArray<double3> _rowSums;
    private JobHandle _job;
    private double _time = double.NaN;
    private int _foamSource;

    /// <summary>Displacement, slopes and slope moments of the cascades, one slice each, mipmapped.</summary>
    public RenderTexture Displacement { get; }

    public RenderTexture Derivatives { get; }

    public RenderTexture Moments { get; }

    public RenderTexture Foam => _foam[_foamSource];

    /// <summary>Height and slope variance each cascade holds in the spectrum being drawn.</summary>
    public double4 HeightVariance { get; private set; }

    public double4 SlopeVariance { get; private set; }

    /// <summary>Mean wavelength (m) of the waves each cascade holds, weighted by their variance.</summary>
    public double4 MeanWavelength { get; private set; }

    /// <summary>Whether a spectrum is still being filled, which must finish first.</summary>
    public bool Busy { get; private set; }

    public WaveCascades(ComputeShader shader) {

        _shader = shader;
        _evolve = shader.FindKernel("Evolve");
        _rows = shader.FindKernel("TransformRows");
        _columns = shader.FindKernel("TransformColumns");
        _assemble = shader.FindKernel("Assemble");

        _initial = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Size * Size * Cascades, 4 * sizeof(float)) { name = "Wave Spectrum" };

        _spectrumA = CascadeTexture("Wave Transform A", GraphicsFormat.R32G32B32A32_SFloat, false);
        _spectrumB = CascadeTexture("Wave Transform B", GraphicsFormat.R32G32B32A32_SFloat, false);
        Displacement = CascadeTexture("Wave Displacement", GraphicsFormat.R16G16B16A16_SFloat, true);
        Derivatives = CascadeTexture("Wave Slopes", GraphicsFormat.R16G16_SFloat, true);
        Moments = CascadeTexture("Wave Slope Moments", GraphicsFormat.R16G16B16A16_SFloat, true);
        _foam[0] = CascadeTexture("Wave Foam", GraphicsFormat.R16G16_SFloat, true);
        _foam[1] = CascadeTexture("Wave Foam", GraphicsFormat.R16G16_SFloat, true);

        _staging = new NativeArray<float4>(Size * Size * Cascades, Allocator.Persistent);
        _rowSums = new NativeArray<double3>(Size * Cascades, Allocator.Persistent);

        shader.SetVector(CascadeSizeId, new Vector4((float)Sizes[0], (float)Sizes[1], (float)Sizes[2], (float)Sizes[3]));
        shader.SetVector(CascadeAngleId, new Vector4((float)Angles[0], (float)Angles[1], (float)Angles[2], (float)Angles[3]));
        shader.SetBuffer(_evolve, InitialId, _initial);
        shader.SetTexture(_assemble, DisplacementId, Displacement);
        shader.SetTexture(_assemble, DerivativesId, Derivatives);
        shader.SetTexture(_assemble, MomentsId, Moments);

        foreach (int kernel in new[] { _evolve, _rows, _columns, _assemble }) {

            shader.SetTexture(kernel, SpectrumAId, _spectrumA);
            shader.SetTexture(kernel, SpectrumBId, _spectrumB);

        }

    }

    /// <summary>Wavenumbers (rad/m) a cascade holds: from half the Nyquist of the one before to half its own.</summary>
    public static double BandLow(int cascade) => cascade == 0 ? 0.0 : BandHigh(cascade - 1);

    public static double BandHigh(int cascade) => cascade == Cascades - 1 ? double.PositiveInfinity : 0.5 * Math.PI * Size / Sizes[cascade];

    /// <summary>Starts filling the waves for <paramref name="spectrum"/>; ignored while <see cref="Busy"/>. An
    /// <paramref name="urgent"/> fill, for a view that has jumped, spreads across every worker instead.</summary>
    public void Request(Spectrum spectrum, bool urgent) {

        if (Busy) {

            return;

        }

        SpectrumJob job = new SpectrumJob {

            Spectrum = spectrum,
            Sizes = new double4(Sizes[0], Sizes[1], Sizes[2], Sizes[3]),
            Angles = new double4(Angles[0], Angles[1], Angles[2], Angles[3]),
            Low = new double4(BandLow(0), BandLow(1), BandLow(2), BandLow(3)),
            High = new double4(BandHigh(0), BandHigh(1), BandHigh(2), BandHigh(3)),
            Initial = _staging,
            Rows = _rowSums,

        };

        _job = urgent ? job.ScheduleParallel(Size * Cascades, 8, default) : job.Schedule(Size * Cascades, default);

        Busy = true;

    }

    /// <summary>Finishes a spectrum being filled and starts the foam afresh; the view has jumped to a new sea.</summary>
    public void Settle() {

        if (Busy) {

            Upload();

        }

        _time = double.NaN;

    }

    /// <summary>Runs the waves to sim <paramref name="time"/> (s). <paramref name="thresholds"/> are the Jacobians
    /// under which each cascade breaks; <paramref name="drift"/> the heading foam drifts along in the frame (radians)
    /// and <paramref name="driftSpeed"/> its speed (m/s).</summary>
    public void Update(double time, float4 thresholds, double drift, double driftSpeed) {

        if (Busy && _job.IsCompleted) {

            Upload();

        }

        CommandBuffer cmd = _commands;
        cmd.Clear();

        cmd.SetComputeVectorParam(_shader, FoamThresholdId, thresholds);

        if (double.IsNaN(_time) || time < _time) {

            for (int i = PrewarmSteps; i > 0; i--) {

                Step(cmd, time - i * PrewarmStep, PrewarmStep, drift, driftSpeed);

            }

            _time = time - PrewarmStep;

        }

        Step(cmd, time, Math.Min(time - _time, LongestStep), drift, driftSpeed);
        _time = time;

        cmd.GenerateMips(Displacement);
        cmd.GenerateMips(Derivatives);
        cmd.GenerateMips(Moments);

        // Seen from afar a tile shrinks under a pixel, where only its mipmaps give the foam's true share.
        cmd.GenerateMips(_foam[_foamSource]);

        Graphics.ExecuteCommandBuffer(cmd);

    }

    // Evolves the waves to time and moves the foam on by seconds: carried downwind, fading, raised where they break.
    private void Step(CommandBuffer cmd, double time, double seconds, double drift, double driftSpeed) {

        Vector4 foam = new Vector4((float)drift, (float)(driftSpeed * seconds), (float)Math.Exp(-seconds / FoamLife), (float)Math.Exp(-seconds / FreshLife));

        cmd.SetComputeFloatParam(_shader, LoopPhaseId, (float)(time / Spectrum.LoopSeconds - Math.Floor(time / Spectrum.LoopSeconds)));
        cmd.SetComputeVectorParam(_shader, FoamStepId, foam);
        cmd.SetComputeTextureParam(_shader, _assemble, FoamSourceId, _foam[_foamSource]);
        cmd.SetComputeTextureParam(_shader, _assemble, FoamId, _foam[1 - _foamSource]);

        cmd.DispatchCompute(_shader, _evolve, Size / 8, Size / 8, Cascades);
        cmd.DispatchCompute(_shader, _rows, Size, Cascades, 1);
        cmd.DispatchCompute(_shader, _columns, Size, Cascades, 1);
        cmd.DispatchCompute(_shader, _assemble, Size / 8, Size / 8, Cascades);

        _foamSource = 1 - _foamSource;

    }

    // Sends the filled spectrum to the GPU and sums what each cascade holds.
    private void Upload() {

        _job.Complete();
        Busy = false;

        _initial.SetData(_staging);

        double4 heights = 0.0;
        double4 slopes = 0.0;
        double4 lengths = 0.0;

        for (int row = 0; row < _rowSums.Length; row++) {

            heights[row / Size] += _rowSums[row].x;
            slopes[row / Size] += _rowSums[row].y;
            lengths[row / Size] += _rowSums[row].z;

        }

        HeightVariance = heights;
        SlopeVariance = slopes;
        MeanWavelength = math.select(lengths / math.max(heights, 1e-30), new double4(Sizes[0], Sizes[1], Sizes[2], Sizes[3]) / 8.0, heights <= 1e-30);

    }

    private static RenderTexture CascadeTexture(string name, GraphicsFormat format, bool mips) {

        RenderTexture texture = new RenderTexture(new RenderTextureDescriptor(Size, Size, format, GraphicsFormat.None, mips ? 9 : 1) {

            dimension = TextureDimension.Tex2DArray,
            volumeDepth = Cascades,
            enableRandomWrite = true,
            useMipMap = mips,
            autoGenerateMips = false,

        }) {

            name = name,
            filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Repeat,
            anisoLevel = mips ? 8 : 1,

        };

        texture.Create();

        return texture;

    }

    public void Dispose() {

        _job.Complete();
        _staging.Dispose();
        _rowSums.Dispose();
        _commands.Release();
        _initial.Release();

        foreach (RenderTexture texture in new[] { _spectrumA, _spectrumB, Displacement, Derivatives, Moments, _foam[0], _foam[1] }) {

            texture.Release();

        }

    }

}
