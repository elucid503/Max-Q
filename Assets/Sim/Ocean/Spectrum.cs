using System;

namespace MaxQ.Sim.Ocean;

/// <summary>One train of waves: a JONSWAP spectrum spread about the heading it travels.</summary>
public readonly struct WaveSystem {

    /// <summary>Phillips' constant, scaling the whole spectrum; zero for no waves.</summary>
    public double Alpha { get; init; }

    /// <summary>Frequency of the peak, rad/s.</summary>
    public double PeakFrequency { get; init; }

    /// <summary>JONSWAP's peak enhancement: 3.3 for a growing sea, higher for narrow swell.</summary>
    public double Gamma { get; init; }

    /// <summary>Direction of travel, radians anticlockwise from the first axis of the caller's frame.</summary>
    public double Heading { get; init; }

}

/// <summary>A sea as a directional wave spectrum: the sea the local wind raises (JONSWAP, spread as Donelan and
/// Banner measured) plus swell from far away (JONSWAP, narrowly spread as Mitsuyasu and Goda give it). Blittable, so the
/// game's Burst jobs fill the wave simulation from it.</summary>
public readonly struct Spectrum {

    public const double Gravity = 9.81;

    /// <summary>The wave field repeats after this many seconds: every frequency is a whole number of cycles in it.</summary>
    public const double LoopSeconds = 1_024.0;

    /// <summary>Waves shorter than this (m) are ripples surface tension damps at once.</summary>
    public const double ShortestWavelength = 0.01;

    // Surface tension over density of seawater, m^3/s^2.
    private const double Tension = 7.4e-5;

    // JONSWAP's growth with fetch (Hasselmann et al. 1973) saturates at a fully developed sea at this dimensionless
    // fetch, where the peak reaches Pierson and Moskowitz's 0.877 g/U.
    private const double DevelopedFetch = 15_785.0;

    // Swell reaches a coast in full from open water this far upwave, and not at all through gaps narrower than the
    // shorter one (real metres).
    private const double ShelteredFetch = 2_000.0;
    private const double ExposedFetch = 500_000.0;

    private const double SwellSpread = 75.0;

    public WaveSystem Sea { get; init; }

    public WaveSystem Swell { get; init; }

    /// <summary>Angular frequency of waves of wavenumber <paramref name="k"/> (rad/m) on deep water, capillarity included.</summary>
    public static double Frequency(double k) => Math.Sqrt(Gravity * k + Tension * k * k * k);

    /// <summary>The frequency nearest <see cref="Frequency"/> that repeats in <see cref="LoopSeconds"/>, never zero.</summary>
    public static double LoopFrequency(double k) {

        double quantum = 2.0 * Math.PI / LoopSeconds;

        return Math.Max(Math.Round(Frequency(k) / quantum), 1.0) * quantum;

    }

    /// <summary>The spectrum of a sea in <paramref name="conditions"/>, with each system's heading given in the caller's
    /// frame. Ice damps both systems.</summary>
    public static Spectrum For(SeaConditions conditions, double seaHeading, double swellHeading) {

        double damping = (1.0 - conditions.Ice) * (1.0 - conditions.Ice);

        return new Spectrum {

            Sea = System(conditions.SeaHeight * damping, conditions.SeaPeriod, 3.3, seaHeading),
            Swell = System(conditions.SwellHeight * damping, conditions.SwellPeriod, 6.0, swellHeading),

        };

    }

    /// <summary>The open sea's <paramref name="conditions"/> where land shelters the water: the local sea is held to
    /// what the wind can raise over <paramref name="windFetch"/> (real metres of open water upwind), and swell reaches
    /// in as far as <paramref name="swellFetch"/> (open water back toward where it comes from) exposes the water.</summary>
    public static SeaConditions Sheltered(SeaConditions conditions, double windFetch, double swellFetch) {

        double wind = Math.Max(conditions.WindSpeed, 0.5);
        double fetch = Math.Min(Gravity * Math.Max(windFetch, 1.0) / (wind * wind), DevelopedFetch);
        double limitedHeight = 0.0016 * Math.Sqrt(fetch) * wind * wind / Gravity;
        double limitedPeriod = 2.0 * Math.PI / (22.0 * Gravity / wind * Math.Pow(fetch, -1.0 / 3.0));
        double exposure = Math.Log(Math.Max(swellFetch, 1.0) / ShelteredFetch) / Math.Log(ExposedFetch / ShelteredFetch);
        bool limited = limitedHeight < conditions.SeaHeight;

        return conditions with {

            SeaHeight = limited ? limitedHeight : conditions.SeaHeight,
            SeaPeriod = limited ? limitedPeriod : conditions.SeaPeriod,
            SwellHeight = conditions.SwellHeight * Math.Min(Math.Max(exposure, 0.0), 1.0),

        };

    }

    /// <summary>A system of significant height <paramref name="height"/> (m) peaking at <paramref name="period"/> (s).</summary>
    public static WaveSystem System(double height, double period, double gamma, double heading) {

        double peak = 2.0 * Math.PI / Math.Max(period, 0.5);
        WaveSystem unit = new WaveSystem { Alpha = 1.0, PeakFrequency = peak, Gamma = gamma, Heading = heading };

        return unit with { Alpha = height > 0.0 ? height * height / (16.0 * Variance(unit)) : 0.0 };

    }

    /// <summary>Energy per unit area of wavenumber at (<paramref name="kx"/>, <paramref name="ky"/>) (rad/m, in the
    /// caller's frame): m^2 of surface variance per (rad/m)^2.</summary>
    public double Density(double kx, double ky) {

        double k = Math.Sqrt(kx * kx + ky * ky);

        if (k < 1e-6) {

            return 0.0;

        }

        double omega = Frequency(k);
        double slope = (Gravity + 3.0 * Tension * k * k) / (2.0 * omega);
        double theta = Math.Atan2(ky, kx);
        double cutoff = k * ShortestWavelength / (2.0 * Math.PI);

        double sea = Energy(Sea, omega) * DonelanBanner(theta - Sea.Heading, omega / Sea.PeakFrequency);
        double swell = Energy(Swell, omega) * CosinePower(theta - Swell.Heading, SwellExponent(omega / Swell.PeakFrequency));

        return (sea + swell) * slope / k * Math.Exp(-cutoff * cutoff);

    }

    /// <summary>Significant height of the whole sea, m.</summary>
    public double SignificantHeight => 4.0 * Math.Sqrt(Variance(Sea) + Variance(Swell));

    /// <summary>JONSWAP energy per unit angular frequency, m^2 s.</summary>
    public static double Energy(WaveSystem system, double omega) {

        if (system.Alpha <= 0.0 || omega <= 0.0) {

            return 0.0;

        }

        double r = system.PeakFrequency / omega;
        double sigma = omega <= system.PeakFrequency ? 0.07 : 0.09;
        double offset = (omega - system.PeakFrequency) / (sigma * system.PeakFrequency);
        double enhancement = Math.Pow(system.Gamma, Math.Exp(-0.5 * offset * offset));

        return system.Alpha * Gravity * Gravity / Math.Pow(omega, 5.0) * Math.Exp(-1.25 * r * r * r * r) * enhancement;

    }

    /// <summary>Donelan and Banner's spreading at <paramref name="theta"/> from the heading, for frequency
    /// <paramref name="ratio"/> times the peak's; it integrates to one over the circle.</summary>
    public static double DonelanBanner(double theta, double ratio) {

        double beta;

        if (ratio < 0.95) {

            beta = 2.61 * Math.Pow(ratio, 1.3);

        } else if (ratio < 1.6) {

            beta = 2.28 * Math.Pow(ratio, -1.3);

        } else {

            beta = Math.Pow(10.0, -0.4 + 0.8393 * Math.Exp(-0.567 * Math.Log(ratio * ratio)));

        }

        double wrapped = Wrap(theta);
        double sech = 1.0 / Math.Cosh(beta * wrapped);

        return beta / (2.0 * Math.Tanh(beta * Math.PI)) * sech * sech;

    }

    /// <summary>The cosine-power spreading cos^2s(theta/2), normalised over the circle.</summary>
    public static double CosinePower(double theta, double s) {

        double c = Math.Abs(Math.Cos(0.5 * Wrap(theta)));

        return Math.Exp(LogGamma(s + 1.0) - LogGamma(s + 0.5)) / (2.0 * Math.Sqrt(Math.PI)) * Math.Pow(c, 2.0 * s);

    }

    // Mitsuyasu's spreading exponent, peaking at the peak frequency; swell's peak value is Goda's 75.
    private static double SwellExponent(double ratio) =>
        SwellSpread * (ratio <= 1.0 ? Math.Pow(ratio, 5.0) : Math.Pow(ratio, -2.5));

    // Surface variance of a system, m^2: its energy integrated over frequency in log steps.
    private static double Variance(WaveSystem system) {

        if (system.Alpha <= 0.0) {

            return 0.0;

        }

        const int steps = 600;
        double low = Math.Log(0.3 * system.PeakFrequency);
        double high = Math.Log(40.0 * system.PeakFrequency);
        double step = (high - low) / steps;
        double sum = 0.0;

        for (int i = 0; i < steps; i++) {

            double omega = Math.Exp(low + (i + 0.5) * step);

            sum += Energy(system, omega) * omega * step;

        }

        return sum;

    }

    private static double Wrap(double theta) {

        double turns = (theta + Math.PI) / (2.0 * Math.PI);

        return (turns - Math.Floor(turns)) * 2.0 * Math.PI - Math.PI;

    }

    // Lanczos' approximation (g = 7, nine terms), good to 15 digits for positive arguments.
    private static double LogGamma(double x) {

        double sum = 0.99999999999980993 + 676.5203681218851 / x - 1259.1392167224028 / (x + 1.0) + 771.32342877765313 / (x + 2.0) -
            176.61502916214059 / (x + 3.0) + 12.507343278686905 / (x + 4.0) - 0.13857109526572012 / (x + 5.0) +
            9.9843695780195716e-6 / (x + 6.0) + 1.5056327351493116e-7 / (x + 7.0);
        double t = x + 6.5;

        return 0.5 * Math.Log(2.0 * Math.PI) + (x - 0.5) * Math.Log(t) - t + Math.Log(sum);

    }

}
