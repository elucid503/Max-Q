using System;

using MaxQ.Sim.Ocean;

using NUnit.Framework;

namespace MaxQ.Sim.Tests.Ocean;

public sealed class SpectrumTests {

    private static SeaConditions Open(double seaHeight, double seaPeriod, double swellHeight = 0.0, double swellPeriod = 12.0) => new SeaConditions {

        SeaHeight = seaHeight,
        SeaPeriod = seaPeriod,
        SwellHeight = swellHeight,
        SwellPeriod = swellPeriod,

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

        Spectrum spectrum = Spectrum.For(Open(2.5, 8.0, 1.5, 13.0), 0.0, 0.4);

        Assert.That(spectrum.SignificantHeight, Is.EqualTo(Math.Sqrt(2.5 * 2.5 + 1.5 * 1.5)).Within(0.01));
        Assert.That(4.0 * Math.Sqrt(Variance(spectrum)), Is.EqualTo(spectrum.SignificantHeight).Within(0.03 * spectrum.SignificantHeight));

    }

    [Test]
    public void ShortFetchHoldsTheSeaDown() {

        // Hasselmann's growth law: 10 m/s over 10 km raises about half a metre.
        double height = Spectrum.WindSea(10.0, 10_000.0, out double period);
        double expected = 0.0016 * Math.Sqrt(Spectrum.Gravity * 10_000.0 / 100.0) * 100.0 / Spectrum.Gravity;

        Spectrum.WindSea(10.0, double.PositiveInfinity, out double openPeriod);

        Assert.That(height, Is.EqualTo(expected).Within(1e-9));
        Assert.That(period, Is.LessThan(0.6 * openPeriod));

    }

    [Test]
    public void TheOpenOceanRaisesPiersonAndMoskowitzsSea() {

        double height = Spectrum.WindSea(10.0, double.PositiveInfinity, out double period);

        Assert.That(height, Is.EqualTo(0.21 * 100.0 / Spectrum.Gravity).Within(0.05 * height));
        Assert.That(period, Is.EqualTo(2.0 * Math.PI * 10.0 / (0.877 * Spectrum.Gravity)).Within(0.01 * period));

    }

    [Test]
    public void WavesRunWithTheirHeading() {

        Spectrum spectrum = Spectrum.For(Open(1.8, 7.0), 0.5 * Math.PI, 0.0);
        double k = Math.Pow(2.0 * Math.PI / 7.0, 2.0) / Spectrum.Gravity;

        Assert.That(spectrum.Density(0.0, k), Is.GreaterThan(10.0 * spectrum.Density(0.0, -k)));
        Assert.That(spectrum.Density(0.0, k), Is.GreaterThan(spectrum.Density(k, 0.0)));

    }

}
