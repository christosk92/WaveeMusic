using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Sources;

/// <summary>
/// Registry for track sources with URI routing.
/// Routes track URIs to the appropriate source implementation.
/// </summary>
public sealed class TrackSourceRegistry
{
    private readonly List<ITrackSource> _sources = new();

    /// <summary>
    /// Registers a track source.
    /// </summary>
    public void Register(ITrackSource source)
    {
        _sources.Add(source);
    }

    /// <summary>
    /// Finds a source that can handle the given URI.
    /// </summary>
    public ITrackSource? FindSource(string uri)
    {
        return _sources.FirstOrDefault(s => s.CanHandle(uri));
    }

    /// <summary>
    /// Loads a track using the appropriate source.
    /// </summary>
    public async Task<ITrackStream> LoadAsync(string uri, CancellationToken cancellationToken = default)
    {
        var source = FindSource(uri);
        if (source == null)
            throw new NotSupportedException($"No track source registered for URI: {uri}");

        return await source.LoadAsync(uri, cancellationToken);
    }
}
