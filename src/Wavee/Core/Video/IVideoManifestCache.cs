using System;

namespace Wavee.Core.Video;

/// <summary>
/// Caches parsed Spotify music-video manifests by manifest id.
/// Prefetch parses the JSON once during the previous track; the play path
/// reads the parsed model from here instead of re-fetching + re-parsing.
///
/// Entries are short-lived (license / segment URLs can rotate) — TTL is
/// measured in minutes, not hours.
/// </summary>
public interface IVideoManifestCache
{
    bool TryGet(string manifestId, out CachedVideoManifest entry);

    void Store(string manifestId, string rawJson, SpotifyWebEmeVideoManifest parsed);

    void Invalidate(string manifestId);
}

public sealed record CachedVideoManifest(
    string ManifestId,
    string RawJson,
    SpotifyWebEmeVideoManifest Parsed,
    DateTimeOffset StoredAt);
