using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Processors;

/// <summary>
/// Multi-band parametric equalizer using biquad IIR filters.
/// Supports standard EQ types: peaking, low shelf, high shelf, lowpass, highpass.
/// </summary>
public sealed class EqualizerProcessor : IAudioProcessor
{
    private readonly List<EqualizerBand> _bands = new();
    private AudioFormat? _format;
    private BiquadFilter[][]? _filters; // [band][channel]

    public string ProcessorName => "Equalizer";
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets the list of EQ bands.
    /// </summary>
    public IReadOnlyList<EqualizerBand> Bands => _bands.AsReadOnly();

    /// <summary>
    /// Adds a parametric EQ band.
    /// </summary>
    public void AddBand(EqualizerBand band)
    {
        _bands.Add(band);
        if (_format != null)
        {
            RebuildFilters();
        }
    }

    /// <summary>
    /// Removes an EQ band.
    /// </summary>
    public bool RemoveBand(EqualizerBand band)
    {
        var removed = _bands.Remove(band);
        if (removed && _format != null)
        {
            RebuildFilters();
        }
        return removed;
    }

    /// <summary>
    /// Removes all EQ bands.
    /// </summary>
    public void ClearBands()
    {
        _bands.Clear();
        _filters = null;
    }

    /// <summary>
    /// Creates a standard 10-band graphic EQ with frequencies: 31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz.
    /// </summary>
    public void CreateGraphicEq10Band()
    {
        ClearBands();
        var frequencies = new[] { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        foreach (var freq in frequencies)
        {
            AddBand(new EqualizerBand(freq, 0.0, 1.0, BandType.Peaking));
        }
    }

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        _format = format;
        RebuildFilters();
        return Task.CompletedTask;
    }

    public AudioBuffer Process(AudioBuffer input)
    {
        if (_format == null)
            throw new InvalidOperationException("Processor not initialized");

        if (input.IsEmpty || _filters == null || _filters.Length == 0)
            return input;

        var inputSpan = input.Data.Span;
        var output = new byte[inputSpan.Length];
        input.Data.CopyTo(output); // Start with input data

        var outputSpan = output.AsSpan();

        if (_format.BitsPerSample == 16)
        {
            ProcessInt16(outputSpan, _format.Channels);
        }
        else if (_format.BitsPerSample == 24)
        {
            ProcessInt24(outputSpan, _format.Channels);
        }
        else if (_format.BitsPerSample == 32)
        {
            ProcessInt32(outputSpan, _format.Channels);
        }
        else
        {
            throw new NotSupportedException($"Unsupported bit depth: {_format.BitsPerSample}");
        }

        return new AudioBuffer(output, input.PositionMs);
    }

    public void Reset()
    {
        if (_filters != null)
        {
            foreach (var bandFilters in _filters)
            {
                foreach (var filter in bandFilters)
                {
                    filter.Reset();
                }
            }
        }
    }

    private void RebuildFilters()
    {
        if (_format == null || _bands.Count == 0)
        {
            _filters = null;
            return;
        }

        _filters = new BiquadFilter[_bands.Count][];
        for (var i = 0; i < _bands.Count; i++)
        {
            _filters[i] = new BiquadFilter[_format.Channels];
            for (var ch = 0; ch < _format.Channels; ch++)
            {
                _filters[i][ch] = BiquadFilter.Create(_bands[i], _format.SampleRate);
            }
        }
    }

    private void ProcessInt16(Span<byte> data, int channels)
    {
        if (_filters == null) return;

        var frameCount = data.Length / (2 * channels);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var sampleIndex = (frame * channels + ch) * 2;
                var sample = (short)(data[sampleIndex] | (data[sampleIndex + 1] << 8));
                var floatSample = sample / 32768.0;

                // Apply all bands
                foreach (var bandFilters in _filters)
                {
                    floatSample = bandFilters[ch].Process(floatSample);
                }

                var processed = (int)(floatSample * 32768.0);
                processed = Math.Clamp(processed, short.MinValue, short.MaxValue);

                var result = (short)processed;
                data[sampleIndex] = (byte)result;
                data[sampleIndex + 1] = (byte)(result >> 8);
            }
        }
    }

    private void ProcessInt24(Span<byte> data, int channels)
    {
        if (_filters == null) return;

        const double maxValue = 8388607.0;
        var frameCount = data.Length / (3 * channels);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var sampleIndex = (frame * channels + ch) * 3;

                var sample = data[sampleIndex] | (data[sampleIndex + 1] << 8) | (data[sampleIndex + 2] << 16);
                if ((sample & 0x800000) != 0)
                    sample |= unchecked((int)0xFF000000);

                var floatSample = sample / maxValue;

                foreach (var bandFilters in _filters)
                {
                    floatSample = bandFilters[ch].Process(floatSample);
                }

                var processed = (int)(floatSample * maxValue);
                processed = Math.Clamp(processed, -8388608, 8388607);

                data[sampleIndex] = (byte)processed;
                data[sampleIndex + 1] = (byte)(processed >> 8);
                data[sampleIndex + 2] = (byte)(processed >> 16);
            }
        }
    }

    private void ProcessInt32(Span<byte> data, int channels)
    {
        if (_filters == null) return;

        var frameCount = data.Length / (4 * channels);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var sampleIndex = (frame * channels + ch) * 4;
                var sample = data[sampleIndex] | (data[sampleIndex + 1] << 8) |
                            (data[sampleIndex + 2] << 16) | (data[sampleIndex + 3] << 24);

                var floatSample = sample / 2147483648.0;

                foreach (var bandFilters in _filters)
                {
                    floatSample = bandFilters[ch].Process(floatSample);
                }

                var processed = (long)(floatSample * 2147483648.0);
                processed = Math.Clamp(processed, int.MinValue, int.MaxValue);

                var result = (int)processed;
                data[sampleIndex] = (byte)result;
                data[sampleIndex + 1] = (byte)(result >> 8);
                data[sampleIndex + 2] = (byte)(result >> 16);
                data[sampleIndex + 3] = (byte)(result >> 24);
            }
        }
    }
}

/// <summary>
/// Represents a single EQ band with frequency, gain, and Q factor.
/// </summary>
public sealed class EqualizerBand
{
    public EqualizerBand(double frequencyHz, double gainDb, double q, BandType type)
    {
        FrequencyHz = frequencyHz;
        GainDb = gainDb;
        Q = q;
        Type = type;
    }

    /// <summary>
    /// Center frequency in Hz.
    /// </summary>
    public double FrequencyHz { get; set; }

    /// <summary>
    /// Gain in dB (for peaking, low shelf, high shelf).
    /// </summary>
    public double GainDb { get; set; }

    /// <summary>
    /// Q factor (bandwidth). Higher Q = narrower band.
    /// Typical range: 0.5 to 10.0
    /// </summary>
    public double Q { get; set; }

    /// <summary>
    /// Type of filter.
    /// </summary>
    public BandType Type { get; set; }
}

/// <summary>
/// Type of biquad filter.
/// </summary>
public enum BandType
{
    Peaking,      // Parametric bell curve
    LowShelf,     // Shelf below frequency
    HighShelf,    // Shelf above frequency
    LowPass,      // Cut frequencies above
    HighPass      // Cut frequencies below
}

/// <summary>
/// Biquad IIR filter implementation.
/// </summary>
internal sealed class BiquadFilter
{
    private double _a0, _a1, _a2, _b1, _b2;
    private double _z1, _z2; // State variables

    public static BiquadFilter Create(EqualizerBand band, int sampleRate)
    {
        var filter = new BiquadFilter();
        filter.SetCoefficients(band, sampleRate);
        return filter;
    }

    public void SetCoefficients(EqualizerBand band, int sampleRate)
    {
        var omega = 2.0 * Math.PI * band.FrequencyHz / sampleRate;
        var sinOmega = Math.Sin(omega);
        var cosOmega = Math.Cos(omega);
        var alpha = sinOmega / (2.0 * band.Q);
        var a = Math.Pow(10.0, band.GainDb / 40.0); // For peaking/shelf

        double a0, a1, a2, b0, b1, b2;

        switch (band.Type)
        {
            case BandType.Peaking:
                b0 = 1.0 + alpha * a;
                b1 = -2.0 * cosOmega;
                b2 = 1.0 - alpha * a;
                a0 = 1.0 + alpha / a;
                a1 = -2.0 * cosOmega;
                a2 = 1.0 - alpha / a;
                break;

            case BandType.LowShelf:
                {
                    var sqrtA = Math.Sqrt(a);
                    b0 = a * ((a + 1.0) - (a - 1.0) * cosOmega + 2.0 * sqrtA * alpha);
                    b1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cosOmega);
                    b2 = a * ((a + 1.0) - (a - 1.0) * cosOmega - 2.0 * sqrtA * alpha);
                    a0 = (a + 1.0) + (a - 1.0) * cosOmega + 2.0 * sqrtA * alpha;
                    a1 = -2.0 * ((a - 1.0) + (a + 1.0) * cosOmega);
                    a2 = (a + 1.0) + (a - 1.0) * cosOmega - 2.0 * sqrtA * alpha;
                }
                break;

            case BandType.HighShelf:
                {
                    var sqrtA = Math.Sqrt(a);
                    b0 = a * ((a + 1.0) + (a - 1.0) * cosOmega + 2.0 * sqrtA * alpha);
                    b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cosOmega);
                    b2 = a * ((a + 1.0) + (a - 1.0) * cosOmega - 2.0 * sqrtA * alpha);
                    a0 = (a + 1.0) - (a - 1.0) * cosOmega + 2.0 * sqrtA * alpha;
                    a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cosOmega);
                    a2 = (a + 1.0) - (a - 1.0) * cosOmega - 2.0 * sqrtA * alpha;
                }
                break;

            case BandType.LowPass:
                b0 = (1.0 - cosOmega) / 2.0;
                b1 = 1.0 - cosOmega;
                b2 = (1.0 - cosOmega) / 2.0;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosOmega;
                a2 = 1.0 - alpha;
                break;

            case BandType.HighPass:
                b0 = (1.0 + cosOmega) / 2.0;
                b1 = -(1.0 + cosOmega);
                b2 = (1.0 + cosOmega) / 2.0;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosOmega;
                a2 = 1.0 - alpha;
                break;

            default:
                throw new NotSupportedException($"Unsupported band type: {band.Type}");
        }

        // Normalize coefficients
        _a0 = b0 / a0;
        _a1 = b1 / a0;
        _a2 = b2 / a0;
        _b1 = a1 / a0;
        _b2 = a2 / a0;
    }

    public double Process(double input)
    {
        var output = _a0 * input + _z1;
        _z1 = _a1 * input - _b1 * output + _z2;
        _z2 = _a2 * input - _b2 * output;
        return output;
    }

    public void Reset()
    {
        _z1 = 0.0;
        _z2 = 0.0;
    }
}
