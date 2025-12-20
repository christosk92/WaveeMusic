namespace Wavee.Connect.Events;

/// <summary>
/// Metrics about the audio content being played.
/// Captured during track loading for event reporting.
/// </summary>
public sealed record ContentMetrics
{
    /// <summary>
    /// File ID in hex format (lowercase).
    /// </summary>
    public string? FileId { get; init; }

    /// <summary>
    /// Whether the audio key was retrieved from cache.
    /// </summary>
    public bool PreloadedAudioKey { get; init; }

    /// <summary>
    /// Time in milliseconds to fetch the audio key.
    /// -1 if the key was preloaded from cache.
    /// </summary>
    public int AudioKeyTime { get; init; }

    /// <summary>
    /// Creates ContentMetrics with validation.
    /// </summary>
    public static ContentMetrics Create(string? fileId, bool wasCached, int audioKeyTimeMs)
    {
        // If cached, audio key time must be -1
        if (wasCached && audioKeyTimeMs != -1)
            audioKeyTimeMs = -1;

        return new ContentMetrics
        {
            FileId = fileId?.ToLowerInvariant(),
            PreloadedAudioKey = wasCached,
            AudioKeyTime = wasCached ? -1 : audioKeyTimeMs
        };
    }
}
