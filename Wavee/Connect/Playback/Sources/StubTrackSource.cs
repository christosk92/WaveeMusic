using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// Stub track source for testing (generates dummy tracks).
/// </summary>
public sealed class StubTrackSource : ITrackSource
{
    public string SourceName => "Stub";

    public bool CanHandle(string uri)
    {
        return uri.StartsWith("stub:");
    }

    public Task<ITrackStream> LoadAsync(string uri, CancellationToken cancellationToken = default)
    {
        var metadata = new TrackMetadata
        {
            Uri = uri,
            Title = "Stub Track",
            Artist = "Stub Artist",
            DurationMs = 10000 // 10 seconds
        };

        // Return empty stream (decoder will generate silence)
        var stream = new MemoryStream();
        return Task.FromResult<ITrackStream>(new StubTrackStream(stream, metadata));
    }
}

internal sealed class StubTrackStream : ITrackStream
{
    public StubTrackStream(Stream audioStream, TrackMetadata metadata)
    {
        AudioStream = audioStream;
        Metadata = metadata;
    }

    public Stream AudioStream { get; }
    public TrackMetadata Metadata { get; }
    public AudioFormat? KnownFormat => null;
    public bool CanSeek => AudioStream.CanSeek;

    public ValueTask DisposeAsync()
    {
        return AudioStream.DisposeAsync();
    }
}
