using System.Buffers;
using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Processors;

/// <summary>
/// Chainable audio processing pipeline.
/// Applies multiple processors in sequence.
/// </summary>
public sealed class AudioProcessingChain
{
    private readonly List<IAudioProcessor> _processors = new();
    private AudioFormat? _format;
    private CrossfadeProcessor? _crossfadeProcessor;

    /// <summary>
    /// Adds a processor to the chain.
    /// </summary>
    public void AddProcessor(IAudioProcessor processor)
    {
        _processors.Add(processor);
        if (processor is CrossfadeProcessor cf)
            _crossfadeProcessor = cf;
    }

    /// <summary>
    /// Removes a processor from the chain.
    /// </summary>
    public bool RemoveProcessor(IAudioProcessor processor)
    {
        return _processors.Remove(processor);
    }

    /// <summary>
    /// Initializes all processors with the audio format.
    /// </summary>
    public async Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        _format = format;
        foreach (var processor in _processors)
        {
            await processor.InitializeAsync(format, cancellationToken);
        }
    }

    /// <summary>
    /// Processes an audio buffer through all enabled processors using a zero-copy pipeline.
    /// Rents a single buffer from ArrayPool, copies the decoder output once, then each
    /// processor transforms the data in-place on the same Span — no intermediate allocations.
    /// </summary>
    /// <remarks>
    /// The returned AudioBuffer is backed by a pooled array. The caller MUST call
    /// <see cref="AudioBuffer.Return"/> after the data has been consumed (e.g. after WriteAsync
    /// copies it into the audio sink).
    /// </remarks>
    public AudioBuffer Process(AudioBuffer input)
    {
        if (input.IsEmpty)
            return input;

        // Check if any processor is enabled
        var anyEnabled = false;
        foreach (var processor in _processors)
        {
            if (processor.IsEnabled)
            {
                anyEnabled = true;
                break;
            }
        }

        if (!anyEnabled)
            return input;

        // Check if CrossfadeProcessor is actively crossfading — it needs the full
        // AudioBuffer (with PositionMs) and may mix with a next-track buffer,
        // so it can't use the in-place path. Fall back to per-processor Process().
        var crossfade = _crossfadeProcessor;
        if (crossfade is { IsEnabled: true, State: not CrossfadeState.Normal })
            return ProcessFallback(input);

        // === Zero-copy pipeline ===
        // Rent one buffer, copy decoder output, run all processors in-place.
        var dataLength = input.Data.Length;
        var pipelineBuffer = ArrayPool<byte>.Shared.Rent(dataLength);
        input.Data.Span.CopyTo(pipelineBuffer.AsSpan(0, dataLength));

        var span = pipelineBuffer.AsSpan(0, dataLength);
        foreach (var processor in _processors)
        {
            if (processor.IsEnabled)
                processor.ProcessInPlace(span);
        }

        return new AudioBuffer(pipelineBuffer, dataLength, input.PositionMs);
    }

    /// <summary>
    /// Fallback path using per-processor Process() when in-place is not possible
    /// (e.g. during active crossfade). This is the old allocation-heavy path.
    /// </summary>
    private AudioBuffer ProcessFallback(AudioBuffer input)
    {
        var current = input;
        foreach (var processor in _processors)
        {
            if (processor.IsEnabled)
                current = processor.Process(current);
        }
        return current;
    }

    /// <summary>
    /// Resets all processors.
    /// </summary>
    public void Reset()
    {
        foreach (var processor in _processors)
        {
            processor.Reset();
        }
    }

    /// <summary>
    /// Gets all registered processors.
    /// </summary>
    public IReadOnlyList<IAudioProcessor> Processors => _processors.AsReadOnly();
}
