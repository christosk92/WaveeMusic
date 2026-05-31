using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Services.Tracks;

/// <summary>Resolves a track URI to its 30-second Spotify preview MP3 URL (or null when none exists).</summary>
public interface IPreviewUrlResolver
{
    /// <summary>Preview MP3 URL for a track URI, or null when the track has no preview file.</summary>
    Task<string?> ResolveAsync(string trackUri, CancellationToken ct = default);

    /// <summary>Warm the cache for upcoming cards. Never throws per item.</summary>
    Task PrefetchAsync(IReadOnlyList<string> trackUris, CancellationToken ct = default);
}

/// <summary>Builds the canonical Spotify preview-clip URL from an <c>AudioFile.file_id</c>.</summary>
public static class PreviewUrl
{
    public static string Build(ReadOnlySpan<byte> fileId) =>
        "https://p.scdn.co/mp3-preview/" + Convert.ToHexString(fileId).ToLowerInvariant();
}
