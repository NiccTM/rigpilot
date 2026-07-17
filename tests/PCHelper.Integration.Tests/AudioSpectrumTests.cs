using PCHelper.App;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins the music-mode spectrum math on synthetic tones: a pure tone lands in
/// the expected band, silence produces no energy, normalisation keeps values
/// in [0,1], and colour scaling is monotone. No capture device is touched —
/// the WASAPI loopback layer is exercised live.
/// </summary>
public sealed class AudioSpectrumTests
{
    private const int SampleRate = 48000;
    private const int Bands = 24;

    private static float[] Tone(double frequency, int length)
    {
        float[] samples = new float[length];
        for (int index = 0; index < length; index++)
        {
            samples[index] = (float)Math.Sin(2 * Math.PI * frequency * index / SampleRate);
        }

        return samples;
    }

    [Fact]
    public void ABassToneEnergisesALowBand()
    {
        double[] bands = new double[Bands];
        AudioSpectrum.ComputeBands(Tone(80, AudioSpectrum.FftSize), SampleRate, Bands, 0, bands);

        int peakBand = Array.IndexOf(bands, bands.Max());
        Assert.True(peakBand < Bands / 3, $"80 Hz should peak in a low band, got band {peakBand}.");
        Assert.All(bands, energy => Assert.InRange(energy, 0, 1));
    }

    [Fact]
    public void ATrebleToneEnergisesAHighBand()
    {
        double[] bands = new double[Bands];
        AudioSpectrum.ComputeBands(Tone(9000, AudioSpectrum.FftSize), SampleRate, Bands, 0, bands);

        int peakBand = Array.IndexOf(bands, bands.Max());
        Assert.True(peakBand > Bands / 2, $"9 kHz should peak in a high band, got band {peakBand}.");
    }

    [Fact]
    public void SilenceProducesNoStrongEnergy()
    {
        double[] bands = new double[Bands];
        AudioSpectrum.ComputeBands(new float[AudioSpectrum.FftSize], SampleRate, Bands, 0, bands);

        Assert.All(bands, energy => Assert.InRange(energy, 0, 1));
        Assert.True(bands.Max() < 0.5, "Silence should not light bands strongly.");
    }

    [Fact]
    public void PeakNormalisationDecaysButNeverToZero()
    {
        double[] bands = new double[Bands];
        double loudPeak = AudioSpectrum.ComputeBands(Tone(1000, AudioSpectrum.FftSize), SampleRate, Bands, 0, bands);
        double quietPeak = AudioSpectrum.ComputeBands(new float[AudioSpectrum.FftSize], SampleRate, Bands, loudPeak, bands);

        Assert.True(quietPeak > 0, "Peak must stay above zero to avoid divide-by-zero.");
        Assert.True(quietPeak < loudPeak, "Peak must decay toward quieter passages.");
    }

    [Theory]
    [InlineData(0.0, 0u)]
    [InlineData(0.5, 0x000080u)] // half of pure red (R in low byte)
    [InlineData(1.0, 0x0000FFu)]
    public void ColourScalingIsMonotone(double energy, uint expected)
    {
        Assert.Equal(expected, AudioSpectrum.ScaleColour(0x0000FF, energy));
    }
}
