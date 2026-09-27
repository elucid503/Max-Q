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
/// Fills the initial spectra on the workers when the sea changes, blends to them over a second, and each frame evolves,
/// transforms and assembles the displacement, slopes and foam the surface samples.</summary>
internal sealed class WaveCascades : IDisposable {

    public const int Size = 256;
    public const int Cascades = 4;

    private const double Degree = Math.PI / 180.0;

    /// <summary>Tile sizes, metres.</summary>
    public static readonly double[] Sizes = { 1_597.0, 119.3, 8.93, 0.668 };

    /// <summary>Each tile's turn from the wave frame, radians.</summary>
    public static readonly double[] Angles = { 0.0, 23.4 * Degree, 47.1 * Degree, 71.3 * Degree };

    // A new spectrum takes this long to blend in, seconds.
    private const double BlendSeconds = 1.0;

    // Foam steps at a fixed rate of sim time; after a jump it is run on long enough to settle.
    public const double FoamStep = 1.0 / 30.0;
    private const int FoamStepsPerFrame = 8;
    private const int FoamPrewarmSteps = 180;

    private static readonly int InitialId = Shader.PropertyToID("_Initial");
    private static readonly int InitialNextId = Shader.PropertyToID("_InitialNext");
    private static readonly int BlendId = Shader.PropertyToID("_Blend");
    private static readonly int CascadeSizeId = Shader.PropertyToID("_CascadeSize");
    private static readonly int LoopPhaseId = Shader.PropertyToID("_LoopPhase");
    private static readonly int SpectrumAId = Shader.PropertyToID("_SpectrumA");
    private static readonly int SpectrumBId = Shader.PropertyToID("_SpectrumB");
    private static readonly int DisplacementId = Shader.PropertyToID("_Displacement");
    private static readonly int DerivativesId = Shader.PropertyToID("_Derivatives");
    private static readonly int MomentsId = Shader.PropertyToID("_Moments");
    private static readonly int AssembledDisplacementId = Shader.PropertyToID("_AssembledDisplacement");
    private static readonly int AssembledDerivativesId = Shader.PropertyToID("_AssembledDerivatives");
    private static readonly int FoamSourceId = Shader.PropertyToID("_FoamSource");
    private static readonly int FoamId = Shader.PropertyToID("_Foam");
    private static readonly int FoamThresholdId = Shader.PropertyToID("_FoamThreshold");
    private static readonly int FoamDriftNearId = Shader.PropertyToID("_FoamDriftNear");
    private static readonly int FoamDriftFarId = Shader.PropertyToID("_FoamDriftFar");
    private static readonly int FoamKeepId = Shader.PropertyToID("_FoamKeep");

    private readonly ComputeShader _shader;
    private readonly int _evolve;
    private readonly int _rows;
    private readonly int _columns;
    private readonly int _assemble;
    private readonly int _foamKernel;

    private readonly Texture2DArray[] _initial = new Texture2DArray[2];
    private readonly RenderTexture _spectrumA;
    private readonly RenderTexture _spectrumB;
    private readonly RenderTexture[] _foam = new RenderTexture[2];
    private readonly CommandBuffer _commands = new CommandBuffer { name = "Waves" };

    private NativeArray<float4> _staging;
    private NativeArray<double3> _rowSums;
    private JobHandle _job;
    private bool _filling;
    private bool _blending;
    private bool _first = true;
    private int _now;
    private double _blendStart = double.NegativeInfinity;
    private double _foamTime = double.NaN;
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

    /// <summary>Whether the waves are still blending to a new spectrum or filling one, which must finish first.</summary>
    public bool Busy => _filling || _blending;

    public WaveCascades(ComputeShader shader) {

        _shader = shader;
        _evolve = shader.FindKernel("Evolve");
        _rows = shader.FindKernel("TransformRows");
        _columns = shader.FindKernel("TransformColumns");
        _assemble = shader.FindKernel("Assemble");
        _foamKernel = shader.FindKernel("Foam");

        for (int i = 0; i < 2; i++) {

            _initial[i] = new Texture2DArray(Size, Size, Cascades, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.None) {

                name = "Wave Spectrum",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,

            };

            _foam[i] = CascadeTexture("Wave Foam", GraphicsFormat.R16G16_SFloat, true);

        }

        _spectrumA = CascadeTexture("Wave Transform A", GraphicsFormat.R32G32B32A32_SFloat, false);
        _spectrumB = CascadeTexture("Wave Transform B", GraphicsFormat.R32G32B32A32_SFloat, false);
        Displacement = CascadeTexture("Wave Displacement", GraphicsFormat.R16G16B16A16_SFloat, true);
        Derivatives = CascadeTexture("Wave Slopes", GraphicsFormat.R16G16B16A16_SFloat, true);
        Moments = CascadeTexture("Wave Slope Moments", GraphicsFormat.R16G16B16A16_SFloat, true);

        _staging = new NativeArray<float4>(Size * Size * Cascades, Allocator.Persistent);
        _rowSums = new NativeArray<double3>(Size * Cascades, Allocator.Persistent);

    }

    /// <summary>Wavenumbers (rad/m) a cascade holds: from half the Nyquist of the one before to half its own.</summary>
    public static double BandLow(int cascade) => cascade == 0 ? 0.0 : BandHigh(cascade - 1);

    public static double BandHigh(int cascade) => cascade == Cascades - 1 ? double.PositiveInfinity : 0.5 * Math.PI * Size / Sizes[cascade];

    /// <summary>Starts filling the waves for <paramref name="spectrum"/>; ignored while <see cref="Busy"/>.</summary>
    public void Request(Spectrum spectrum) {

        if (Busy) {

            return;

        }

        _job = new SpectrumJob {

            Spectrum = spectrum,
            Sizes = new double4(Sizes[0], Sizes[1], Sizes[2], Sizes[3]),
            Angles = new double4(Angles[0], Angles[1], Angles[2], Angles[3]),
            Low = new double4(BandLow(0), BandLow(1), BandLow(2), BandLow(3)),
            High = new double4(BandHigh(0), BandHigh(1), BandHigh(2), BandHigh(3)),
            Initial = _staging,
            Rows = _rowSums,

        }.Schedule(Size * Cascades, 8);

        _filling = true;

    }

    /// <summary>Waits for a spectrum being filled; used when the view jumps and must not show the old sea.</summary>
    public void Settle() {

        if (_filling) {

            _job.Complete();
            Upload(double.NaN);

        }

        _blending = false;
        _blendStart = double.NegativeInfinity;

    }

    /// <summary>Runs the waves to sim <paramref name="time"/> (s). <paramref name="thresholds"/> are the Jacobians
    /// under which each cascade breaks; <paramref name="drift"/> the heading foam drifts along in the frame (radians)
    /// and <paramref name="driftSpeed"/> its speed (m/s). A jump in time settles the foam afresh.</summary>
    public void Update(double time, float4 thresholds, double drift, double driftSpeed, float2 foamKeep) {

        if (_filling && _job.IsCompleted) {

            _job.Complete();
            Upload(time);

        }

        float blend = 0.0f;

        if (_blending) {

            double t = (time - _blendStart) / BlendSeconds;

            if (t >= 1.0 || t < 0.0) {

                _now = 1 - _now;
                _blending = false;

            } else {

                blend = (float)t;

            }

        }

        CommandBuffer cmd = _commands;
        cmd.Clear();

        cmd.SetComputeVectorParam(_shader, CascadeSizeId, new Vector4((float)Sizes[0], (float)Sizes[1], (float)Sizes[2], (float)Sizes[3]));
        cmd.SetComputeFloatParam(_shader, LoopPhaseId, (float)(Fraction(time / Spectrum.LoopSeconds)));
        cmd.SetComputeFloatParam(_shader, BlendId, blend);
        cmd.SetComputeTextureParam(_shader, _evolve, InitialId, _initial[_now]);
        cmd.SetComputeTextureParam(_shader, _evolve, InitialNextId, _initial[_blending ? 1 - _now : _now]);
        cmd.SetComputeTextureParam(_shader, _evolve, SpectrumAId, _spectrumA);
        cmd.SetComputeTextureParam(_shader, _evolve, SpectrumBId, _spectrumB);
        cmd.DispatchCompute(_shader, _evolve, Size / 8, Size / 8, Cascades);

        foreach (int kernel in new[] { _rows, _columns }) {

            cmd.SetComputeTextureParam(_shader, kernel, SpectrumAId, _spectrumA);
            cmd.SetComputeTextureParam(_shader, kernel, SpectrumBId, _spectrumB);
            cmd.DispatchCompute(_shader, kernel, Size, Cascades, 1);

        }

        cmd.SetComputeTextureParam(_shader, _assemble, SpectrumAId, _spectrumA);
        cmd.SetComputeTextureParam(_shader, _assemble, SpectrumBId, _spectrumB);
        cmd.SetComputeTextureParam(_shader, _assemble, DisplacementId, Displacement);
        cmd.SetComputeTextureParam(_shader, _assemble, DerivativesId, Derivatives);
        cmd.SetComputeTextureParam(_shader, _assemble, MomentsId, Moments);
        cmd.DispatchCompute(_shader, _assemble, Size / 8, Size / 8, Cascades);

        cmd.GenerateMips(Displacement);
        cmd.GenerateMips(Derivatives);
        cmd.GenerateMips(Moments);

        int steps = FoamSteps(time);
        Vector4 near = Vector4.zero;
        Vector4 far = Vector4.zero;

        for (int i = 0; i < Cascades; i++) {

            double heading = drift - Angles[i];
            double texels = driftSpeed * FoamStep * Size / Sizes[i];
            Vector2 d = new Vector2((float)(Math.Cos(heading) * texels), (float)(Math.Sin(heading) * texels));

            if (i < 2) {

                near[2 * i] = d.x;
                near[2 * i + 1] = d.y;

            } else {

                far[2 * i - 4] = d.x;
                far[2 * i - 3] = d.y;

            }

        }

        cmd.SetComputeVectorParam(_shader, FoamThresholdId, thresholds);
        cmd.SetComputeVectorParam(_shader, FoamDriftNearId, near);
        cmd.SetComputeVectorParam(_shader, FoamDriftFarId, far);
        cmd.SetComputeVectorParam(_shader, FoamKeepId, new Vector4(foamKeep.x, foamKeep.y, 0.0f, 0.0f));
        cmd.SetComputeTextureParam(_shader, _foamKernel, AssembledDisplacementId, Displacement);
        cmd.SetComputeTextureParam(_shader, _foamKernel, AssembledDerivativesId, Derivatives);

        for (int step = 0; step < steps; step++) {

            cmd.SetComputeTextureParam(_shader, _foamKernel, FoamSourceId, _foam[_foamSource]);
            cmd.SetComputeTextureParam(_shader, _foamKernel, FoamId, _foam[1 - _foamSource]);
            cmd.DispatchCompute(_shader, _foamKernel, Size / 8, Size / 8, Cascades);
            _foamSource = 1 - _foamSource;

        }

        // Seen from afar a tile shrinks under a pixel, where only its mipmaps give the foam's true share.
        if (steps > 0) {

            cmd.GenerateMips(_foam[_foamSource]);

        }

        Graphics.ExecuteCommandBuffer(cmd);

    }

    // Foam steps due since the last frame; a jump in time, or the first frame, runs it long enough to settle.
    private int FoamSteps(double time) {

        if (double.IsNaN(_foamTime) || time < _foamTime || time - _foamTime > FoamStepsPerFrame * FoamStep * 4.0) {

            _foamTime = time;

            return FoamPrewarmSteps;

        }

        int steps = (int)Math.Floor((time - _foamTime) / FoamStep);

        _foamTime += steps * FoamStep;

        return Math.Min(steps, FoamStepsPerFrame);

    }

    // Sends the filled spectrum to the GPU: the first straight in, later ones blended in from the one shown.
    private void Upload(double time) {

        _filling = false;

        int target = _first || double.IsNaN(time) ? _now : 1 - _now;

        for (int cascade = 0; cascade < Cascades; cascade++) {

            _initial[target].SetPixelData(_staging, 0, cascade, cascade * Size * Size);

        }

        _initial[target].Apply(false, false);

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

        if (target != _now) {

            _blending = true;
            _blendStart = time;

        }

        _first = false;

    }

    private static double Fraction(double x) => x - Math.Floor(x);

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

        foreach (Texture2DArray texture in _initial) {

            UnityEngine.Object.Destroy(texture);

        }

        foreach (RenderTexture texture in new[] { _spectrumA, _spectrumB, Displacement, Derivatives, Moments, _foam[0], _foam[1] }) {

            texture.Release();

        }

    }

}
