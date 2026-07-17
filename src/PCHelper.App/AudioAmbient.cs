using NAudio.Dsp;
using NAudio.Wave;

namespace PCHelper.App;

/// <summary>
/// Pure spectrum math for the music-reactive ambience: a windowed FFT over the
/// most recent mono samples, aggregated into log-spaced band energies in
/// [0, 1]. Deterministic over its inputs so tests can drive it with synthetic
/// tones — no capture device is involved at this layer.
/// </summary>
public static class AudioSpectrum
{
    public const int FftSize = 2048; // power of two; ~43 ms at 48 kHz

    /// <summary>
    /// Computes <paramref name="bandCount"/> log-spaced band energies from the
    /// last <see cref="FftSize"/> mono samples. Energies are normalised by a
    /// caller-maintained running peak (<paramref name="peak"/>), which decays
    /// slowly so quiet passages stay dim instead of being auto-gained to full
    /// brightness. Returns the updated peak.
    /// </summary>
    public static double ComputeBands(ReadOnlySpan<float> monoSamples, int sampleRate, int bandCount, double peak, double[] bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentOutOfRangeException.ThrowIfLessThan(bandCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bands.Length, bandCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(monoSamples.Length, FftSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);

        Complex[] buffer = new Complex[FftSize];
        ReadOnlySpan<float> window = monoSamples[^FftSize..];
        for (int index = 0; index < FftSize; index++)
        {
            buffer[index].X = (float)(window[index] * FastFourierTransform.HannWindow(index, FftSize));
            buffer[index].Y = 0;
        }

        FastFourierTransform.FFT(true, (int)Math.Log2(FftSize), buffer);

        // Log-spaced bands from 40 Hz to 16 kHz (clamped to Nyquist).
        double lowHz = 40;
        double highHz = Math.Min(16000, sampleRate / 2.0);
        double frame = 0;
        for (int band = 0; band < bandCount; band++)
        {
            double fromHz = lowHz * Math.Pow(highHz / lowHz, (double)band / bandCount);
            double toHz = lowHz * Math.Pow(highHz / lowHz, (double)(band + 1) / bandCount);
            int fromBin = Math.Max(1, (int)(fromHz * FftSize / sampleRate));
            int toBin = Math.Max(fromBin + 1, (int)(toHz * FftSize / sampleRate));
            double magnitude = 0;
            for (int bin = fromBin; bin < toBin && bin < FftSize / 2; bin++)
            {
                magnitude = Math.Max(magnitude, Math.Sqrt((buffer[bin].X * buffer[bin].X) + (buffer[bin].Y * buffer[bin].Y)));
            }

            bands[band] = magnitude;
            frame = Math.Max(frame, magnitude);
        }

        // Slow-decay peak normalisation: react instantly to louder music,
        // decay ~4%/tick toward quieter passages, keep a floor against
        // divide-by-near-zero flicker in silence.
        double updatedPeak = Math.Max(frame, Math.Max(peak * 0.96, 0.001));
        for (int band = 0; band < bandCount; band++)
        {
            bands[band] = Math.Clamp(bands[band] / updatedPeak, 0, 1);
        }

        return updatedPeak;
    }

    /// <summary>Scales a packed OpenRGB colour (R | G&lt;&lt;8 | B&lt;&lt;16) by a [0,1] energy.</summary>
    public static uint ScaleColour(uint colour, double energy)
    {
        double clamped = Math.Clamp(energy, 0, 1);
        uint r = (uint)Math.Round((colour & 0xFF) * clamped);
        uint g = (uint)Math.Round((colour >> 8 & 0xFF) * clamped);
        uint b = (uint)Math.Round((colour >> 16 & 0xFF) * clamped);
        return r | (g << 8) | (b << 16);
    }
}

/// <summary>
/// WASAPI loopback capture of whatever the default output device is playing,
/// reduced to a rolling mono ring buffer. Privacy boundary: this is the
/// system's own output audio (never a microphone), it stays in process memory,
/// and nothing is recorded to disk or transmitted — the only product is LED
/// colour values. Runs only between explicit start/stop actions.
/// </summary>
public sealed class AudioAmbientSource : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly float[] _ring = new float[AudioSpectrum.FftSize * 4];
    private readonly object _gate = new();
    private int _writePosition;
    private long _totalWritten;

    public AudioAmbientSource()
    {
        _capture = new WasapiLoopbackCapture();
        _capture.DataAvailable += OnDataAvailable;
    }

    public int SampleRate => _capture.WaveFormat.SampleRate;

    public void Start() => _capture.StartRecording();

    /// <summary>
    /// Copies the most recent <see cref="AudioSpectrum.FftSize"/> mono samples
    /// into <paramref name="destination"/>. False until enough audio has been
    /// captured (e.g. nothing is playing yet).
    /// </summary>
    public bool TryReadLatest(float[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            if (_totalWritten < AudioSpectrum.FftSize || destination.Length < AudioSpectrum.FftSize)
            {
                return false;
            }

            int start = (_writePosition - AudioSpectrum.FftSize + _ring.Length) % _ring.Length;
            for (int index = 0; index < AudioSpectrum.FftSize; index++)
            {
                destination[index] = _ring[(start + index) % _ring.Length];
            }

            return true;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        // Loopback delivers IEEE-float frames in the device mix format;
        // average the channels into mono.
        int channels = _capture.WaveFormat.Channels;
        int frameBytes = 4 * channels;
        lock (_gate)
        {
            for (int offset = 0; offset + frameBytes <= args.BytesRecorded; offset += frameBytes)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += BitConverter.ToSingle(args.Buffer, offset + (channel * 4));
                }

                _ring[_writePosition] = sum / channels;
                _writePosition = (_writePosition + 1) % _ring.Length;
                _totalWritten++;
            }
        }
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        try
        {
            _capture.StopRecording();
        }
        catch (InvalidOperationException)
        {
            // Not recording; nothing to stop.
        }

        _capture.Dispose();
    }
}
