using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Processors;

/// <summary>
/// Applies volume adjustment to audio samples.
/// Supports both linear and logarithmic (dB) volume control.
/// </summary>
public sealed class VolumeProcessor : IAudioProcessor
{
    private float _volumeLinear = 1.0f;
    private AudioFormat? _format;

    public string ProcessorName => "Volume";
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the volume level (0.0 = silence, 1.0 = 100%, 2.0 = +6dB, etc.)
    /// </summary>
    public float Volume
    {
        get => _volumeLinear;
        set => _volumeLinear = Math.Max(0.0f, value);
    }

    /// <summary>
    /// Gets or sets the volume in decibels (-inf = silence, 0.0 = 100%, +6.0 = 2x, etc.)
    /// </summary>
    public float VolumeDb
    {
        get => LinearToDb(_volumeLinear);
        set => _volumeLinear = DbToLinear(value);
    }

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        _format = format;
        return Task.CompletedTask;
    }

    public AudioBuffer Process(AudioBuffer input)
    {
        if (_format == null)
            throw new InvalidOperationException("Processor not initialized");

        if (input.IsEmpty || Math.Abs(_volumeLinear - 1.0f) < 0.0001f)
            return input; // No change needed

        var inputSpan = input.Data.Span;
        var output = new byte[inputSpan.Length];
        var outputSpan = output.AsSpan();

        if (_format.BitsPerSample == 16)
        {
            ProcessInt16(inputSpan, outputSpan, _volumeLinear);
        }
        else if (_format.BitsPerSample == 24)
        {
            ProcessInt24(inputSpan, outputSpan, _volumeLinear);
        }
        else if (_format.BitsPerSample == 32)
        {
            ProcessInt32(inputSpan, outputSpan, _volumeLinear);
        }
        else
        {
            throw new NotSupportedException($"Unsupported bit depth: {_format.BitsPerSample}");
        }

        return new AudioBuffer(output, input.PositionMs);
    }

    public void Reset()
    {
        // Volume processor has no state to reset
    }

    private static void ProcessInt16(ReadOnlySpan<byte> input, Span<byte> output, float volume)
    {
        var sampleCount = input.Length / 2;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 2;
            var sample = (short)(input[sampleIndex] | (input[sampleIndex + 1] << 8));
            var processed = (int)(sample * volume);

            // Clamp to prevent overflow
            processed = Math.Clamp(processed, short.MinValue, short.MaxValue);

            var result = (short)processed;
            output[sampleIndex] = (byte)result;
            output[sampleIndex + 1] = (byte)(result >> 8);
        }
    }

    private static void ProcessInt24(ReadOnlySpan<byte> input, Span<byte> output, float volume)
    {
        var sampleCount = input.Length / 3;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 3;

            // Read 24-bit sample (little endian, sign-extended to 32-bit)
            var sample = input[sampleIndex] | (input[sampleIndex + 1] << 8) | (input[sampleIndex + 2] << 16);
            if ((sample & 0x800000) != 0) // Sign extend
                sample |= unchecked((int)0xFF000000);

            var processed = (int)(sample * volume);

            // Clamp to 24-bit range
            processed = Math.Clamp(processed, -8388608, 8388607);

            output[sampleIndex] = (byte)processed;
            output[sampleIndex + 1] = (byte)(processed >> 8);
            output[sampleIndex + 2] = (byte)(processed >> 16);
        }
    }

    private static void ProcessInt32(ReadOnlySpan<byte> input, Span<byte> output, float volume)
    {
        var sampleCount = input.Length / 4;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 4;
            var sample = input[sampleIndex] | (input[sampleIndex + 1] << 8) |
                        (input[sampleIndex + 2] << 16) | (input[sampleIndex + 3] << 24);

            // Use 64-bit to prevent overflow during multiplication
            var processed = (long)(sample * volume);
            processed = Math.Clamp(processed, int.MinValue, int.MaxValue);

            var result = (int)processed;
            output[sampleIndex] = (byte)result;
            output[sampleIndex + 1] = (byte)(result >> 8);
            output[sampleIndex + 2] = (byte)(result >> 16);
            output[sampleIndex + 3] = (byte)(result >> 24);
        }
    }

    private static float DbToLinear(float db)
    {
        if (float.IsNegativeInfinity(db)) return 0.0f;
        return MathF.Pow(10.0f, db / 20.0f);
    }

    private static float LinearToDb(float linear)
    {
        if (linear <= 0.0f) return float.NegativeInfinity;
        return 20.0f * MathF.Log10(linear);
    }
}
