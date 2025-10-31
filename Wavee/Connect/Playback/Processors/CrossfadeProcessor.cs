using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Processors;

/// <summary>
/// Crossfade processor for smooth transitions between tracks.
/// Supports equal-power crossfading with configurable duration and curve.
/// </summary>
public sealed class CrossfadeProcessor : IAudioProcessor
{
    private AudioFormat? _format;
    private CrossfadeState _state = CrossfadeState.Normal;
    private long _crossfadeStartMs;
    private int _crossfadeDurationMs = 3000; // Default 3 seconds
    private CrossfadeCurve _curve = CrossfadeCurve.EqualPower;
    private AudioBuffer? _nextTrackBuffer;
    private readonly Queue<AudioBuffer> _nextTrackQueue = new();
    private bool _crossfadeEnabled = true;

    public string ProcessorName => "Crossfade";
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the crossfade duration in milliseconds.
    /// </summary>
    public int CrossfadeDurationMs
    {
        get => _crossfadeDurationMs;
        set => _crossfadeDurationMs = Math.Max(0, Math.Min(value, 30000)); // 0-30 seconds
    }

    /// <summary>
    /// Gets or sets the crossfade curve type.
    /// </summary>
    public CrossfadeCurve Curve
    {
        get => _curve;
        set => _curve = value;
    }

    /// <summary>
    /// Gets or sets whether crossfading is enabled.
    /// </summary>
    public bool CrossfadeEnabled
    {
        get => _crossfadeEnabled;
        set => _crossfadeEnabled = value;
    }

    /// <summary>
    /// Gets the current crossfade state.
    /// </summary>
    public CrossfadeState State => _state;

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        _format = format;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts a crossfade to the next track.
    /// Call this method when you want to begin fading to the next track.
    /// </summary>
    /// <param name="currentPositionMs">Current playback position of the outgoing track</param>
    public void StartCrossfade(long currentPositionMs)
    {
        if (!_crossfadeEnabled || _crossfadeDurationMs == 0)
        {
            _state = CrossfadeState.Normal;
            return;
        }

        _state = CrossfadeState.FadingOut;
        _crossfadeStartMs = currentPositionMs;
    }

    /// <summary>
    /// Queues audio from the next track for crossfading.
    /// Call this method to provide audio data from the incoming track during crossfade.
    /// </summary>
    public void QueueNextTrackBuffer(AudioBuffer buffer)
    {
        _nextTrackQueue.Enqueue(buffer);
    }

    /// <summary>
    /// Completes the crossfade and returns to normal playback.
    /// </summary>
    public void CompleteCrossfade()
    {
        _state = CrossfadeState.Normal;
        _nextTrackQueue.Clear();
        _nextTrackBuffer = null;
    }

    public AudioBuffer Process(AudioBuffer input)
    {
        if (_format == null)
            throw new InvalidOperationException("Processor not initialized");

        if (input.IsEmpty || _state == CrossfadeState.Normal || !_crossfadeEnabled)
            return input;

        // Calculate crossfade progress (0.0 to 1.0)
        var elapsedMs = input.PositionMs - _crossfadeStartMs;
        var progress = Math.Clamp((double)elapsedMs / _crossfadeDurationMs, 0.0, 1.0);

        if (progress >= 1.0)
        {
            // Crossfade complete
            CompleteCrossfade();
            return input;
        }

        // Get next track buffer if available
        if (_nextTrackQueue.TryDequeue(out var nextBuffer))
        {
            _nextTrackBuffer = nextBuffer;
        }

        if (_nextTrackBuffer == null || _nextTrackBuffer.IsEmpty)
        {
            // No next track data yet, just apply fade out to current track
            return ApplyFadeOut(input, progress);
        }

        // Mix current and next track with crossfade
        return MixBuffers(input, _nextTrackBuffer, progress);
    }

    public void Reset()
    {
        _state = CrossfadeState.Normal;
        _nextTrackQueue.Clear();
        _nextTrackBuffer = null;
    }

    private AudioBuffer ApplyFadeOut(AudioBuffer input, double progress)
    {
        var outGain = CalculateFadeOutGain(progress);

        if (Math.Abs(outGain - 1.0) < 0.0001)
            return input; // No fade needed

        var inputSpan = input.Data.Span;
        var output = new byte[inputSpan.Length];
        var outputSpan = output.AsSpan();

        if (_format!.BitsPerSample == 16)
        {
            ApplyGainInt16(inputSpan, outputSpan, outGain);
        }
        else if (_format.BitsPerSample == 24)
        {
            ApplyGainInt24(inputSpan, outputSpan, outGain);
        }
        else if (_format.BitsPerSample == 32)
        {
            ApplyGainInt32(inputSpan, outputSpan, outGain);
        }

        return new AudioBuffer(output, input.PositionMs);
    }

    private AudioBuffer MixBuffers(AudioBuffer current, AudioBuffer next, double progress)
    {
        var outGain = CalculateFadeOutGain(progress);
        var inGain = CalculateFadeInGain(progress);

        var currentSpan = current.Data.Span;
        var nextSpan = next.Data.Span;

        // Use the smaller length
        var mixLength = Math.Min(currentSpan.Length, nextSpan.Length);
        var output = new byte[mixLength];
        var outputSpan = output.AsSpan();

        if (_format!.BitsPerSample == 16)
        {
            MixInt16(currentSpan, nextSpan, outputSpan, outGain, inGain, mixLength);
        }
        else if (_format.BitsPerSample == 24)
        {
            MixInt24(currentSpan, nextSpan, outputSpan, outGain, inGain, mixLength);
        }
        else if (_format.BitsPerSample == 32)
        {
            MixInt32(currentSpan, nextSpan, outputSpan, outGain, inGain, mixLength);
        }

        return new AudioBuffer(output, current.PositionMs);
    }

    private double CalculateFadeOutGain(double progress)
    {
        return _curve switch
        {
            CrossfadeCurve.Linear => 1.0 - progress,
            CrossfadeCurve.EqualPower => Math.Cos(progress * Math.PI / 2.0),
            CrossfadeCurve.Logarithmic => Math.Pow(1.0 - progress, 2.0),
            CrossfadeCurve.SCurve => 1.0 - SmoothStep(progress),
            _ => 1.0 - progress
        };
    }

    private double CalculateFadeInGain(double progress)
    {
        return _curve switch
        {
            CrossfadeCurve.Linear => progress,
            CrossfadeCurve.EqualPower => Math.Sin(progress * Math.PI / 2.0),
            CrossfadeCurve.Logarithmic => 1.0 - Math.Pow(1.0 - progress, 2.0),
            CrossfadeCurve.SCurve => SmoothStep(progress),
            _ => progress
        };
    }

    private static double SmoothStep(double x)
    {
        // Smoothstep interpolation (3x^2 - 2x^3)
        return x * x * (3.0 - 2.0 * x);
    }

    private static void ApplyGainInt16(ReadOnlySpan<byte> input, Span<byte> output, double gain)
    {
        var sampleCount = input.Length / 2;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 2;
            var sample = (short)(input[sampleIndex] | (input[sampleIndex + 1] << 8));
            var processed = (int)(sample * gain);
            processed = Math.Clamp(processed, short.MinValue, short.MaxValue);

            var result = (short)processed;
            output[sampleIndex] = (byte)result;
            output[sampleIndex + 1] = (byte)(result >> 8);
        }
    }

    private static void ApplyGainInt24(ReadOnlySpan<byte> input, Span<byte> output, double gain)
    {
        var sampleCount = input.Length / 3;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 3;
            var sample = input[sampleIndex] | (input[sampleIndex + 1] << 8) | (input[sampleIndex + 2] << 16);
            if ((sample & 0x800000) != 0)
                sample |= unchecked((int)0xFF000000);

            var processed = (int)(sample * gain);
            processed = Math.Clamp(processed, -8388608, 8388607);

            output[sampleIndex] = (byte)processed;
            output[sampleIndex + 1] = (byte)(processed >> 8);
            output[sampleIndex + 2] = (byte)(processed >> 16);
        }
    }

    private static void ApplyGainInt32(ReadOnlySpan<byte> input, Span<byte> output, double gain)
    {
        var sampleCount = input.Length / 4;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 4;
            var sample = input[sampleIndex] | (input[sampleIndex + 1] << 8) |
                        (input[sampleIndex + 2] << 16) | (input[sampleIndex + 3] << 24);

            var processed = (long)(sample * gain);
            processed = Math.Clamp(processed, int.MinValue, int.MaxValue);

            var result = (int)processed;
            output[sampleIndex] = (byte)result;
            output[sampleIndex + 1] = (byte)(result >> 8);
            output[sampleIndex + 2] = (byte)(result >> 16);
            output[sampleIndex + 3] = (byte)(result >> 24);
        }
    }

    private static void MixInt16(ReadOnlySpan<byte> current, ReadOnlySpan<byte> next, Span<byte> output,
        double outGain, double inGain, int length)
    {
        var sampleCount = length / 2;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 2;
            var currentSample = (short)(current[sampleIndex] | (current[sampleIndex + 1] << 8));
            var nextSample = (short)(next[sampleIndex] | (next[sampleIndex + 1] << 8));

            var mixed = (int)(currentSample * outGain + nextSample * inGain);
            mixed = Math.Clamp(mixed, short.MinValue, short.MaxValue);

            var result = (short)mixed;
            output[sampleIndex] = (byte)result;
            output[sampleIndex + 1] = (byte)(result >> 8);
        }
    }

    private static void MixInt24(ReadOnlySpan<byte> current, ReadOnlySpan<byte> next, Span<byte> output,
        double outGain, double inGain, int length)
    {
        var sampleCount = length / 3;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 3;

            var currentSample = current[sampleIndex] | (current[sampleIndex + 1] << 8) | (current[sampleIndex + 2] << 16);
            if ((currentSample & 0x800000) != 0)
                currentSample |= unchecked((int)0xFF000000);

            var nextSample = next[sampleIndex] | (next[sampleIndex + 1] << 8) | (next[sampleIndex + 2] << 16);
            if ((nextSample & 0x800000) != 0)
                nextSample |= unchecked((int)0xFF000000);

            var mixed = (int)(currentSample * outGain + nextSample * inGain);
            mixed = Math.Clamp(mixed, -8388608, 8388607);

            output[sampleIndex] = (byte)mixed;
            output[sampleIndex + 1] = (byte)(mixed >> 8);
            output[sampleIndex + 2] = (byte)(mixed >> 16);
        }
    }

    private static void MixInt32(ReadOnlySpan<byte> current, ReadOnlySpan<byte> next, Span<byte> output,
        double outGain, double inGain, int length)
    {
        var sampleCount = length / 4;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleIndex = i * 4;

            var currentSample = current[sampleIndex] | (current[sampleIndex + 1] << 8) |
                               (current[sampleIndex + 2] << 16) | (current[sampleIndex + 3] << 24);
            var nextSample = next[sampleIndex] | (next[sampleIndex + 1] << 8) |
                            (next[sampleIndex + 2] << 16) | (next[sampleIndex + 3] << 24);

            var mixed = (long)(currentSample * outGain + nextSample * inGain);
            mixed = Math.Clamp(mixed, int.MinValue, int.MaxValue);

            var result = (int)mixed;
            output[sampleIndex] = (byte)result;
            output[sampleIndex + 1] = (byte)(result >> 8);
            output[sampleIndex + 2] = (byte)(result >> 16);
            output[sampleIndex + 3] = (byte)(result >> 24);
        }
    }
}

/// <summary>
/// Crossfade state.
/// </summary>
public enum CrossfadeState
{
    /// <summary>
    /// Normal playback, no crossfade active.
    /// </summary>
    Normal,

    /// <summary>
    /// Currently fading out the current track.
    /// </summary>
    FadingOut,

    /// <summary>
    /// Currently fading in the next track.
    /// </summary>
    FadingIn
}

/// <summary>
/// Crossfade curve types.
/// </summary>
public enum CrossfadeCurve
{
    /// <summary>
    /// Linear crossfade (simple but can cause volume dip in the middle).
    /// </summary>
    Linear,

    /// <summary>
    /// Equal-power crossfade (constant perceived loudness, best for most music).
    /// Uses sine/cosine curves.
    /// </summary>
    EqualPower,

    /// <summary>
    /// Logarithmic crossfade (slower start, faster end).
    /// </summary>
    Logarithmic,

    /// <summary>
    /// S-curve crossfade (smooth acceleration/deceleration).
    /// </summary>
    SCurve
}
