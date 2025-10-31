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

    /// <summary>
    /// Adds a processor to the chain.
    /// </summary>
    public void AddProcessor(IAudioProcessor processor)
    {
        _processors.Add(processor);
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
    /// Processes an audio buffer through all enabled processors.
    /// </summary>
    public AudioBuffer Process(AudioBuffer input)
    {
        var current = input;

        foreach (var processor in _processors)
        {
            if (processor.IsEnabled)
            {
                current = processor.Process(current);
            }
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
