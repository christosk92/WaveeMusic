namespace Wavee.Connect.Playback.Abstractions;

/// <summary>
/// Interface for loading tracks from various sources (Spotify, local files, HTTP, etc.).
/// </summary>
public interface ITrackSource
{
    /// <summary>
    /// Gets the name of this track source (e.g., "Spotify", "LocalFile", "Http").
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Determines if this source can handle the given URI.
    /// </summary>
    /// <param name="uri">Track URI (spotify:track:xxx, file:///path, http://...).</param>
    /// <returns>True if this source can load the URI.</returns>
    bool CanHandle(string uri);

    /// <summary>
    /// Loads a track stream from the given URI.
    /// </summary>
    /// <param name="uri">Track URI to load.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Track stream with audio data and metadata.</returns>
    Task<ITrackStream> LoadAsync(string uri, CancellationToken cancellationToken = default);
}
