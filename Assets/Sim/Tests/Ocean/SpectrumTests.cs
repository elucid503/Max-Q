using System;

using MaxQ.Sim.Ocean;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Ocean;

public sealed class SpectrumTests {

    private static SeaConditions Open(double wind, double seaHeight, double seaPeriod, double swellHeight = 0.0, double swellPeriod = 12.0) => new SeaConditions {

        WindEast = wind,
        WindSpeed = wind,
        SeaHeight = seaHeight,
        SeaPeriod = seaPeriod,
        SeaEast = 1.0,
        SwellHeight = swellHeight,
        SwellPeriod = swellPeriod,
        SwellEast = 1.0,

    };

    // Surface variance of the whole directional spectrum, integrated over the wavenumber plane in polar steps.
    private static double Variance(Spectrum spectrum) {

        const int rings = 1_500;
        const int spokes = 360;
        double low = Math.Log(1e-4);
        double high = Math.Log(2.0 * Math.PI / Spectrum.ShortestWavelength * 3.0);
        double step = (high - low) / rings;
        double sum = 0.0;

        for (int i = 0; i < rings; i++) {

            double k = Math.Exp(low + (i + 0.5) * step);

            for (int j = 0; j < spokes; j++) {

                double theta = (j + 0.5) * 2.0 * Math.PI / spokes;

                sum += spectrum.Density(k * Math.Cos(theta), k * Math.Sin(theta)) * k * k * step * 2.0 * Math.PI / spokes;

            }

        }

        return sum;

    }

    [Test]
    public void DeepWaterWavesRunAtTheSquareRootOfGk() {

        double k = 2.0 * Math.PI / 100.0;

        Assert.That(Spectrum.Frequency(k), Is.EqualTo(Math.Sqrt(Spectrum.Gravity * k)).Within(1e-6));

    }

    [Test]
    public void RipplesAreSlowestAtAboutSeventeenMillimetres() {

        double slowest = double.MaxValue;
        double wavelength = 0.0;

        for (double lambda = 0.005; lambda < 0.05; lambda += 0.0001) {

            double k = 2.0 * Math.PI / lambda;
            double speed = Spectrum.Frequency(k) / k;

            if (speed < slowest) {

                slowest = speed;
                wavelength = lambda;

            }

        }

        Assert.That(slowest, Is.EqualTo(0.23).Within(0.01));
        Assert.That(wavelength, Is.EqualTo(0.017).Within(0.001));

    }

    [Test]
    public void EveryFrequencyRepeatsInTheLoop() {

        foreach (double k in new[] { 0.004, 0.1, 3.0, 400.0 }) {

            double cycles = Spectrum.LoopFrequency(k) * Spectrum.LoopSeconds / (2.0 * Math.PI);

            Assert.That(cycles, Is.EqualTo(Math.Round(cycles)).Within(1e-9));
            Assert.That(cycles, Is.GreaterThanOrEqualTo(1.0));

        }

    }

    [TestCase(0.3)]
    [TestCase(1.0)]
    [TestCase(1.3)]
    [TestCase(4.0)]
    public void SpreadingIntegratesToOne(double ratio) {

        double banner = 0.0;
        double cosine = 0.0;

        for (int j = 0; j < 20_000; j++) {

            double theta = -Math.PI + (j + 0.5) * 2.0 * Math.PI / 20_000;

            banner += Spectrum.DonelanBanner(theta, ratio) * 2.0 * Math.PI / 20_000;
            cosine += Spectrum.CosinePower(theta, 75.0 * ratio) * 2.0 * Math.PI / 20_000;

        }

        Assert.That(banner, Is.EqualTo(1.0).Within(1e-3));
        Assert.That(cosine, Is.EqualTo(1.0).Within(1e-3));

    }

    [Test]
    public void TheSpectrumHoldsTheRequestedHeight() {

        Spectrum spectrum = Spectrum.For(Open(12.0, 2.5, 8.0, 1.5, 13.0), 0.0, 0.4);

        Assert.That(spectrum.SignificantHeight, Is.EqualTo(Math.Sqrt(2.5 * 2.5 + 1.5 * 1.5)).Within(0.01));
        Assert.That(4.0 * Math.Sqrt(Variance(spectrum)), Is.EqualTo(spectrum.SignificantHeight).Within(0.03 * spectrum.SignificantHeight));

    }

    [Test]
    public void ShortFetchHoldsTheSeaDown() {

        // Hasselmann's growth law: 10 m/s over 10 km raises about half a metre, whatever the open sea outside holds.
        Spectrum spectrum = Spectrum.For(Spectrum.Sheltered(Open(10.0, 3.0, 9.0), 10_000.0, 10_000.0), 0.0, 0.0);
        double expected = 0.0016 * Math.Sqrt(Spectrum.Gravity * 10_000.0 / 100.0) * 100.0 / Spectrum.Gravity;

        Assert.That(spectrum.SignificantHeight, Is.EqualTo(expected).Within(0.02 * expected));

    }

    [Test]
    public void OpenWaterTakesTheClimatologysSea() {

        Spectrum spectrum = Spectrum.For(Spectrum.Sheltered(Open(10.0, 1.8, 7.0), 1e7, 1e7), 0.0, 0.0);

        Assert.That(spectrum.SignificantHeight, Is.EqualTo(1.8).Within(0.01));

    }

    [Test]
    public void SwellReachesOnlyWaterOpenToIt() {

        SeaConditions calm = Open(0.5, 0.0, 3.0, 2.0, 14.0);

        Assert.That(Spectrum.Sheltered(calm, 1e7, 1e7).SwellHeight, Is.EqualTo(2.0));
        Assert.That(Spectrum.Sheltered(calm, 1e7, 1_000.0).SwellHeight, Is.EqualTo(0.0));

    }

    [Test]
    public void IceStillsTheSea() {

        SeaConditions frozen = Open(10.0, 2.0, 7.0, 1.0, 12.0) with { Ice = 1.0 };

        Assert.That(Spectrum.For(frozen, 0.0, 0.0).SignificantHeight, Is.EqualTo(0.0));

    }

    [Test]
    public void WavesRunWithTheirHeading() {

        Spectrum spectrum = Spectrum.For(Open(10.0, 1.8, 7.0), 0.5 * Math.PI, 0.0);
        double k = Math.Pow(2.0 * Math.PI / 7.0, 2.0) / Spectrum.Gravity;

        Assert.That(spectrum.Density(0.0, k), Is.GreaterThan(10.0 * spectrum.Density(0.0, -k)));
        Assert.That(spectrum.Density(0.0, k), Is.GreaterThan(spectrum.Density(k, 0.0)));

    }

}
