using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Processors;

/// <summary>
/// Audio normalization processor supporting ReplayGain and LUFS standards.
/// Automatically adjusts volume based on track or album gain metadata.
/// </summary>
public sealed class NormalizationProcessor : IAudioProcessor
{
    private AudioFormat? _format;
    private float _currentGainLinear = 1.0f;
    private NormalizationMode _mode = NormalizationMode.Track;
    private float _preAmpDb = 0.0f;
    private bool _preventClipping = true;

    public string ProcessorName => "Normalization";
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the normalization mode (Track or Album).
    /// </summary>
    public NormalizationMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    /// <summary>
    /// Gets or sets the pre-amplification in dB (applied before gain).
    /// Typical range: -12dB to +12dB
    /// </summary>
    public float PreAmpDb
    {
        get => _preAmpDb;
        set => _preAmpDb = Math.Clamp(value, -24.0f, 24.0f);
    }

    /// <summary>
    /// Gets or sets whether to prevent clipping by applying peak limiting.
    /// </summary>
    public bool PreventClipping
    {
        get => _preventClipping;
        set => _preventClipping = value;
    }

    /// <summary>
    /// Gets the current gain being applied (in linear scale).
    /// </summary>
    public float CurrentGain => _currentGainLinear;

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        _format = format;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the gain for the current track based on metadata.
    /// </summary>
    public void SetTrackGain(TrackMetadata metadata)
    {
        double? gainDb = _mode switch
        {
            NormalizationMode.Track => metadata.ReplayGainTrackGain,
            NormalizationMode.Album => metadata.ReplayGainAlbumGain ?? metadata.ReplayGainTrackGain,
            _ => null
        };

        if (gainDb.HasValue)
        {
            var totalGainDb = (float)gainDb.Value + _preAmpDb;
            _currentGainLinear = DbToLinear(totalGainDb);
        }
        else
        {
            // No gain metadata - apply only pre-amp
            _currentGainLinear = DbToLinear(_preAmpDb);
        }
    }

    public AudioBuffer Process(AudioBuffer input)
    {
        if (_format == null)
            throw new InvalidOperationException("Processor not initialized");

        if (input.IsEmpty || Math.Abs(_currentGainLinear - 1.0f) < 0.0001f)
            return input; // No change needed

        var inputSpan = input.Data.Span;
        var output = new byte[inputSpan.Length];
        var outputSpan = output.AsSpan();

        if (_format.BitsPerSample == 16)
        {
            ProcessInt16(inputSpan, outputSpan, _currentGainLinear, _preventClipping);
        }
        else if (_format.BitsPerSample == 24)
        {
            ProcessInt24(inputSpan, outputSpan, _currentGainLinear, _preventClipping);
        }
        else if (_format.BitsPerSample == 32)
        {
            ProcessInt32(inputSpan, outputSpan, _currentGainLinear, _preventClipping);
        }
        else
        {
            throw new NotSupportedException($"Unsupported bit depth: {_format.BitsPerSample}");
        }

        return new AudioBuffer(output, input.PositionMs);
    }

    public void Reset()
    {
        _currentGainLinear = 1.0f;
    }

    private static void ProcessInt16(ReadOnlySpan<byte> input, Span<byte> output, float gain, bool preventClipping)
    {
        var sampleCount = input.Length / 2;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 2;
            var sample = (short)(input[sampleIndex] | (input[sampleIndex + 1] << 8));
            var processed = sample * gain;

            // Apply soft clipping if enabled
            if (preventClipping && Math.Abs(processed) > short.MaxValue)
            {
                processed = SoftClip(processed, short.MaxValue);
            }
            else
            {
                processed = Math.Clamp(processed, short.MinValue, short.MaxValue);
            }

            var result = (short)processed;
            output[sampleIndex] = (byte)result;
            output[sampleIndex + 1] = (byte)(result >> 8);
        }
    }

    private static void ProcessInt24(ReadOnlySpan<byte> input, Span<byte> output, float gain, bool preventClipping)
    {
        const int maxValue = 8388607;
        const int minValue = -8388608;

        var sampleCount = input.Length / 3;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 3;

            // Read 24-bit sample
            var sample = input[sampleIndex] | (input[sampleIndex + 1] << 8) | (input[sampleIndex + 2] << 16);
            if ((sample & 0x800000) != 0)
                sample |= unchecked((int)0xFF000000);

            var processed = sample * gain;

            if (preventClipping && Math.Abs(processed) > maxValue)
            {
                processed = SoftClip(processed, maxValue);
            }
            else
            {
                processed = Math.Clamp(processed, minValue, maxValue);
            }

            var result = (int)processed;
            output[sampleIndex] = (byte)result;
            output[sampleIndex + 1] = (byte)(result >> 8);
            output[sampleIndex + 2] = (byte)(result >> 16);
        }
    }

    private static void ProcessInt32(ReadOnlySpan<byte> input, Span<byte> output, float gain, bool preventClipping)
    {
        var sampleCount = input.Length / 4;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 4;
            var sample = input[sampleIndex] | (input[sampleIndex + 1] << 8) |
                        (input[sampleIndex + 2] << 16) | (input[sampleIndex + 3] << 24);

            var processed = (double)sample * gain;

            if (preventClipping && Math.Abs(processed) > int.MaxValue)
            {
                processed = SoftClip((float)processed, int.MaxValue);
            }
            else
            {
                processed = Math.Clamp(processed, int.MinValue, int.MaxValue);
            }

            var result = (int)processed;
            output[sampleIndex] = (byte)result;
            output[sampleIndex + 1] = (byte)(result >> 8);
            output[sampleIndex + 2] = (byte)(result >> 16);
            output[sampleIndex + 3] = (byte)(result >> 24);
        }
    }

    /// <summary>
    /// Soft clipping using tanh curve to prevent harsh distortion.
    /// </summary>
    private static float SoftClip(float sample, float threshold)
    {
        var normalized = sample / threshold;
        var clipped = MathF.Tanh(normalized * 0.8f); // 0.8 factor reduces aggressiveness
        return clipped * threshold;
    }

    private static float DbToLinear(float db)
    {
        if (float.IsNegativeInfinity(db)) return 0.0f;
        return MathF.Pow(10.0f, db / 20.0f);
    }
}

/// <summary>
/// Normalization mode for ReplayGain.
/// </summary>
public enum NormalizationMode
{
    /// <summary>
    /// Use track-specific gain (each track normalized independently).
    /// </summary>
    Track,

    /// <summary>
    /// Use album gain (preserves relative volume between tracks in an album).
    /// Falls back to track gain if album gain is not available.
    /// </summary>
    Album
}
