using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Processors;

/// <summary>
/// Brick-wall limiter for preventing clipping in "radio" sound processing.
/// Ensures the output never exceeds the ceiling level, allowing for higher
/// perceived loudness without distortion.
/// </summary>
public sealed class LimiterProcessor : IAudioProcessor
{
    // Limiter preset values
    private const float CeilingDb = -0.5f;       // Maximum output level (leaves headroom for inter-sample peaks)
    private const float ReleaseMs = 50f;         // Fast release for minimal pumping

    private AudioFormat? _format;
    private double[] _gainReductionDb = Array.Empty<double>();  // Per-channel gain reduction
    private double _releaseCoeff;
    private float _ceilingLinear;

    public string ProcessorName => "Limiter";
    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        _format = format;

        // Initialize per-channel gain reduction state
        _gainReductionDb = new double[format.Channels];

        // Calculate release coefficient
        _releaseCoeff = Math.Exp(-1.0 / (format.SampleRate * ReleaseMs / 1000.0));

        // Pre-calculate ceiling in linear
        _ceilingLinear = DbToLinear(CeilingDb);

        return Task.CompletedTask;
    }

    public AudioBuffer Process(AudioBuffer input)
    {
        if (_format == null)
            throw new InvalidOperationException("Processor not initialized");

        if (input.IsEmpty)
            return input;

        var inputSpan = input.Data.Span;
        var output = new byte[inputSpan.Length];
        var outputSpan = output.AsSpan();

        if (_format.BitsPerSample == 16)
        {
            ProcessInt16(inputSpan, outputSpan, _format.Channels);
        }
        else if (_format.BitsPerSample == 24)
        {
            ProcessInt24(inputSpan, outputSpan, _format.Channels);
        }
        else if (_format.BitsPerSample == 32)
        {
            ProcessInt32(inputSpan, outputSpan, _format.Channels);
        }
        else
        {
            throw new NotSupportedException($"Unsupported bit depth: {_format.BitsPerSample}");
        }

        return new AudioBuffer(output, input.PositionMs);
    }

    public void Reset()
    {
        for (var i = 0; i < _gainReductionDb.Length; i++)
        {
            _gainReductionDb[i] = 0.0;
        }
    }

    private void ProcessInt16(ReadOnlySpan<byte> input, Span<byte> output, int channels)
    {
        var frameCount = input.Length / (2 * channels);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var sampleIndex = (frame * channels + ch) * 2;
                var sample = (short)(input[sampleIndex] | (input[sampleIndex + 1] << 8));

                // Convert to normalized float (-1 to 1)
                var floatSample = sample / 32768.0;

                // Process through limiter
                floatSample = LimitSample(floatSample, ch);

                // Convert back to int16
                var processed = (int)(floatSample * 32768.0);
                processed = Math.Clamp(processed, short.MinValue, short.MaxValue);

                var result = (short)processed;
                output[sampleIndex] = (byte)result;
                output[sampleIndex + 1] = (byte)(result >> 8);
            }
        }
    }

    private void ProcessInt24(ReadOnlySpan<byte> input, Span<byte> output, int channels)
    {
        const double maxValue = 8388607.0;
        var frameCount = input.Length / (3 * channels);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var sampleIndex = (frame * channels + ch) * 3;

                var sample = input[sampleIndex] | (input[sampleIndex + 1] << 8) | (input[sampleIndex + 2] << 16);
                if ((sample & 0x800000) != 0)
                    sample |= unchecked((int)0xFF000000);

                var floatSample = sample / maxValue;
                floatSample = LimitSample(floatSample, ch);

                var processed = (int)(floatSample * maxValue);
                processed = Math.Clamp(processed, -8388608, 8388607);

                output[sampleIndex] = (byte)processed;
                output[sampleIndex + 1] = (byte)(processed >> 8);
                output[sampleIndex + 2] = (byte)(processed >> 16);
            }
        }
    }

    private void ProcessInt32(ReadOnlySpan<byte> input, Span<byte> output, int channels)
    {
        var frameCount = input.Length / (4 * channels);

        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var sampleIndex = (frame * channels + ch) * 4;
                var sample = input[sampleIndex] | (input[sampleIndex + 1] << 8) |
                            (input[sampleIndex + 2] << 16) | (input[sampleIndex + 3] << 24);

                var floatSample = sample / 2147483648.0;
                floatSample = LimitSample(floatSample, ch);

                var processed = (long)(floatSample * 2147483648.0);
                processed = Math.Clamp(processed, int.MinValue, int.MaxValue);

                var result = (int)processed;
                output[sampleIndex] = (byte)result;
                output[sampleIndex + 1] = (byte)(result >> 8);
                output[sampleIndex + 2] = (byte)(result >> 16);
                output[sampleIndex + 3] = (byte)(result >> 24);
            }
        }
    }

    /// <summary>
    /// Core limiting algorithm - instant attack, smooth release.
    /// </summary>
    private double LimitSample(double sample, int channel)
    {
        var absLevel = Math.Abs(sample);

        // Check if we need to limit
        if (absLevel > _ceilingLinear)
        {
            // Calculate required gain reduction (instant attack)
            var requiredReduction = 20.0 * Math.Log10(absLevel / _ceilingLinear);
            _gainReductionDb[channel] = Math.Max(_gainReductionDb[channel], requiredReduction);
        }
        else
        {
            // Release - gradually reduce gain reduction
            _gainReductionDb[channel] *= _releaseCoeff;

            // Snap to zero when very small
            if (_gainReductionDb[channel] < 0.01)
                _gainReductionDb[channel] = 0.0;
        }

        // Apply gain reduction
        if (_gainReductionDb[channel] > 0.0)
        {
            var gainLinear = DbToLinear((float)-_gainReductionDb[channel]);
            return sample * gainLinear;
        }

        return sample;
    }

    private static float DbToLinear(float db)
    {
        if (float.IsNegativeInfinity(db)) return 0.0f;
        return MathF.Pow(10.0f, db / 20.0f);
    }
}
