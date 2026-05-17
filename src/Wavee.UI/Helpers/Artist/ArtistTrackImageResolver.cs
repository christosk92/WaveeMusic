using System.Collections.Generic;

namespace Wavee.UI.Helpers.Artist;

/// <summary>
/// Looks up a track's image URL in a dictionary that may be keyed by the bare
/// id ("4tZwfgr...") OR by the full URI ("spotify:track:4tZwfgr..."). Tries
/// both forms so the upstream cache shape doesn't constrain the call site.
/// </summary>
internal static class ArtistTrackImageResolver
{
    private const string TrackPrefix = "spotify:track:";

    public static bool TryResolve(
        IReadOnlyDictionary<string, string?> imagesByKey,
        string trackUri,
        out string? imageUrl)
    {
        if (imagesByKey.TryGetValue(trackUri, out imageUrl))
            return true;

        var bareId = trackUri.StartsWith(TrackPrefix, System.StringComparison.Ordinal)
            ? trackUri[TrackPrefix.Length..]
            : trackUri;

        if (imagesByKey.TryGetValue(bareId, out imageUrl))
            return true;

        return imagesByKey.TryGetValue(TrackPrefix + bareId, out imageUrl);
    }
}
