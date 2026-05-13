using System.Collections.Concurrent;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Threadsafe in-memory cache backing <see cref="IMusicVideoCatalogCache"/>.
/// Each entry accumulates information as it becomes known. Updates use
/// <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey, TValue, System.Func{TKey, TValue, TValue})"/>
/// so concurrent writers from multiple GraphQL response handlers don't
/// clobber each other.
/// </summary>
internal sealed class MusicVideoCatalogCache : IMusicVideoCatalogCache
{
    private sealed record Entry(bool? HasVideo, string? VideoUri, string? ManifestId);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(System.StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _audioUrisByVideoUri = new(System.StringComparer.Ordinal);

    public bool? GetHasVideo(string audioTrackUri)
    {
        if (string.IsNullOrEmpty(audioTrackUri)) return null;
        return _entries.TryGetValue(audioTrackUri, out var e) ? e.HasVideo : null;
    }

    public void NoteHasVideo(string audioTrackUri, bool hasVideo)
    {
        if (string.IsNullOrEmpty(audioTrackUri)) return;
        _entries.AddOrUpdate(
            audioTrackUri,
            _ => new Entry(hasVideo, null, null),
            (_, prev) => prev with { HasVideo = hasVideo });
    }

    public void NoteVideoUri(string audioTrackUri, string videoTrackUri)
    {
        if (string.IsNullOrEmpty(audioTrackUri) || string.IsNullOrEmpty(videoTrackUri)) return;
        _entries.AddOrUpdate(
            audioTrackUri,
            _ => new Entry(true, videoTrackUri, null),
            (_, prev) => prev with { HasVideo = true, VideoUri = videoTrackUri });
        _audioUrisByVideoUri[videoTrackUri] = audioTrackUri;
    }

    public bool TryGetVideoUri(string audioTrackUri, out string videoTrackUri)
    {
        if (!string.IsNullOrEmpty(audioTrackUri)
            && _entries.TryGetValue(audioTrackUri, out var e)
            && !string.IsNullOrEmpty(e.VideoUri))
        {
            videoTrackUri = e.VideoUri;
            return true;
        }
        videoTrackUri = string.Empty;
        return false;
    }

    public bool TryGetAudioUri(string videoTrackUri, out string audioTrackUri)
    {
        if (!string.IsNullOrEmpty(videoTrackUri)
            && _audioUrisByVideoUri.TryGetValue(videoTrackUri, out audioTrackUri)
            && !string.IsNullOrEmpty(audioTrackUri))
        {
            return true;
        }

        audioTrackUri = string.Empty;
        return false;
    }

    public void ForgetVideoAssociation(string audioTrackUri)
    {
        if (string.IsNullOrEmpty(audioTrackUri)) return;
        if (_entries.TryRemove(audioTrackUri, out var removed)
            && !string.IsNullOrEmpty(removed.VideoUri))
        {
            _audioUrisByVideoUri.TryRemove(removed.VideoUri, out _);
        }
    }

    public void NoteManifestId(string audioTrackUri, string manifestId)
    {
        if (string.IsNullOrEmpty(audioTrackUri) || string.IsNullOrEmpty(manifestId)) return;
        _entries.AddOrUpdate(
            audioTrackUri,
            _ => new Entry(true, null, manifestId),
            (_, prev) => prev with { HasVideo = true, ManifestId = manifestId });
    }

    public bool TryGetManifestId(string audioTrackUri, out string manifestId)
    {
        if (!string.IsNullOrEmpty(audioTrackUri)
            && _entries.TryGetValue(audioTrackUri, out var e)
            && !string.IsNullOrEmpty(e.ManifestId))
        {
            manifestId = e.ManifestId;
            return true;
        }
        manifestId = string.Empty;
        return false;
    }
}
