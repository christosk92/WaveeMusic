using Microsoft.Extensions.Logging;
using Wavee.AudioHost.Audio.Abstractions;

namespace Wavee.AudioHost.Audio.Sinks;

/// <summary>
/// Stub audio sink that discards audio (for testing without audio output).
/// </summary>
public sealed class StubAudioSink : IAudioSink
{
    private readonly ILogger? _logger;
    private AudioFormat? _format;
    private long _positionMs;
    private bool _isPlaying;

    public string SinkName => "Stub";

    /// <summary>
    /// Creates a new StubAudioSink.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    public StubAudioSink(ILogger? logger = null)
    {
        _logger = logger;
    }

    public Task InitializeAsync(AudioFormat format, int bufferSizeMs = 100, CancellationToken cancellationToken = default)
    {
        _format = format;
        _isPlaying = false;
        _positionMs = 0;
        return Task.CompletedTask;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        if (_format == null)
            throw new InvalidOperationException("Sink not initialized");

        // Simulate playback by advancing position
        _positionMs += _format.BytesToMilliseconds(audioData.Length);

        // Optional: Add small delay to simulate real-time playback
        // await Task.Delay((int)_format.BytesToMilliseconds(audioData.Length), cancellationToken);

        return Task.CompletedTask;
    }

    public Task<AudioSinkStatus> GetStatusAsync()
    {
        return Task.FromResult(new AudioSinkStatus(_positionMs, 0, _isPlaying));
    }

    public Task PauseAsync()
    {
        _isPlaying = false;
        return Task.CompletedTask;
    }

    public Task<bool> ResumeAsync()
    {
        _isPlaying = true;
        return Task.FromResult(true);
    }

    public Task FlushAsync()
    {
        _positionMs = 0;
        return Task.CompletedTask;
    }

    public long PlaybackPositionMs => _positionMs;

    public void SetBasePosition(long positionMs)
    {
        _positionMs = positionMs;
    }

    public Task DrainAsync(CancellationToken cancellationToken = default)
    {
        // Stub: nothing to drain
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
