using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core.Audio;

namespace Wavee.UI.WinUI.Data.Contexts.Helpers;

/// <summary>
/// Shared URI normalization helpers used by both <see cref="LibraryDataService"/>
/// (its remaining read paths) and <see cref="PlaylistMutationService"/>. Extracted
/// from <c>LibraryDataService</c> in Phase 2 so both halves of the carve-out can
/// keep their existing input-validation behaviour without duplicating string code.
/// </summary>
internal static class PlaylistUriHelpers
{
    /// <summary>
    /// Strips a known <paramref name="prefix"/> off a Spotify URI, returning the
    /// bare id. Trims whitespace. Returns the empty string for null/blank input.
    /// </summary>
    public static string ExtractBareId(string? uri, string prefix)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return string.Empty;

        uri = uri.Trim();
        while (uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            uri = uri[prefix.Length..];
        return uri;
    }

    /// <summary>
    /// Returns the canonical <c>spotify:playlist:{id}</c> form. Accepts both
    /// bare ids and full URIs.
    /// </summary>
    public static string NormalizePlaylistUri(string playlistId)
    {
        var value = playlistId.Trim();
        const string prefix = "spotify:playlist:";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value
            : prefix + value;
    }

    /// <summary>
    /// Returns the canonical <c>spotify:track:{id}</c> form. Inputs already in
    /// any <c>spotify:…</c> form pass through unchanged.
    /// </summary>
    public static string NormalizeTrackUri(string idOrUri)
    {
        var value = idOrUri.Trim();
        return value.StartsWith("spotify:", StringComparison.Ordinal)
            ? value
            : $"spotify:track:{value}";
    }

    /// <summary>
    /// Reduces a heterogeneous list of track ids / URIs to a deduplicated set
    /// of bare base-62 ids. Used by the playlist-extender request to suppress
    /// already-shown rows from the recommendation pool.
    /// </summary>
    public static IReadOnlyList<string> NormalizeTrackSkipIds(IReadOnlyList<string>? idsOrUris)
    {
        if (idsOrUris is null || idsOrUris.Count == 0)
            return Array.Empty<string>();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in idsOrUris)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var id = ExtractBareId(raw, "spotify:track:");
            if (IsSpotifyBase62Id(id))
                ids.Add(id);
        }

        return ids.Count == 0 ? Array.Empty<string>() : ids.ToList();
    }

    /// <summary>
    /// True when <paramref name="value"/> is a valid Spotify base-62 id (22 chars,
    /// alphanumeric only).
    /// </summary>
    public static bool IsSpotifyBase62Id(string value)
    {
        if (value.Length != SpotifyId.Base62Length)
            return false;

        foreach (var c in value)
        {
            var valid =
                c is >= '0' and <= '9' ||
                c is >= 'A' and <= 'Z' ||
                c is >= 'a' and <= 'z';
            if (!valid)
                return false;
        }

        return true;
    }
}
