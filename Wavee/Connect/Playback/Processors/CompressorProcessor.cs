using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Processors;

/// <summary>
/// Dynamic range compressor for "radio" sound processing.
/// Reduces the dynamic range by attenuating loud parts, making the audio sound
/// more consistent and punchy - similar to FM radio broadcast processing.
/// </summary>
public sealed class CompressorProcessor : IAudioProcessor
{
    // Radio-style compression preset values
    private const float ThresholdDb = -18f;      // Level where compression starts
    private const float Ratio = 4f;              // 4:1 compression ratio
    private const float AttackMs = 10f;          // Fast attack for punch
    private const float ReleaseMs = 100f;        // Medium release to avoid pumping
    private const float MakeupGainDb = 6f;       // Compensate for gain reduction

    private AudioFormat? _format;
    private double[] _envelopeDb = Array.Empty<double>();  // Per-channel envelope follower
    private double _attackCoeff;
    private double _releaseCoeff;
    private float _makeupGainLinear;

    public string ProcessorName => "Compressor";
    public bool IsEnabled { get; set; } = true;

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        _format = format;

        // Initialize per-channel envelope state
        _envelopeDb = new double[format.Channels];
        for (var i = 0; i < format.Channels; i++)
        {
            _envelopeDb[i] = -96.0; // Start at silence
        }

        // Calculate attack/release coefficients
        // These determine how fast the compressor responds to level changes
        _attackCoeff = Math.Exp(-1.0 / (format.SampleRate * AttackMs / 1000.0));
        _releaseCoeff = Math.Exp(-1.0 / (format.SampleRate * ReleaseMs / 1000.0));

        // Pre-calculate makeup gain
        _makeupGainLinear = DbToLinear(MakeupGainDb);

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
        for (var i = 0; i < _envelopeDb.Length; i++)
        {
            _envelopeDb[i] = -96.0;
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

                // Process through compressor
                floatSample = CompressSample(floatSample, ch);

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
                floatSample = CompressSample(floatSample, ch);

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
                floatSample = CompressSample(floatSample, ch);

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
    /// Core compression algorithm with envelope following.
    /// </summary>
    private double CompressSample(double sample, int channel)
    {
        // Calculate input level in dB
        var inputLevel = Math.Abs(sample);
        var inputDb = inputLevel > 1e-10 ? 20.0 * Math.Log10(inputLevel) : -96.0;

        // Envelope follower with attack/release
        // Attack when level is rising, release when falling
        if (inputDb > _envelopeDb[channel])
        {
            // Attack - fast response to transients
            _envelopeDb[channel] = _attackCoeff * _envelopeDb[channel] + (1.0 - _attackCoeff) * inputDb;
        }
        else
        {
            // Release - slower return to baseline
            _envelopeDb[channel] = _releaseCoeff * _envelopeDb[channel] + (1.0 - _releaseCoeff) * inputDb;
        }

        // Calculate gain reduction
        var gainReductionDb = 0.0;
        if (_envelopeDb[channel] > ThresholdDb)
        {
            // Above threshold - apply compression
            var excess = _envelopeDb[channel] - ThresholdDb;
            gainReductionDb = excess * (1.0 - 1.0 / Ratio);
        }

        // Apply gain reduction and makeup gain
        var outputGainLinear = DbToLinear((float)-gainReductionDb) * _makeupGainLinear;
        return sample * outputGainLinear;
    }

    private static float DbToLinear(float db)
    {
        if (float.IsNegativeInfinity(db)) return 0.0f;
        return MathF.Pow(10.0f, db / 20.0f);
    }
}
