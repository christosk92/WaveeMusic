using System.Collections.Concurrent;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Threadsafe in-memory cache backing <see cref="IMusicVideoCatalogCache"/>.
/// Each entry accumulates information as it becomes known. Updates use
/// <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey, TValue, System.Func{TKey, TValue, TValue})"/>
/// so concurrent writers from multiple GraphQL response handlers don't
/// clobber each other.
/// </summary>
internal sealed partial class MusicVideoCatalogCache : IMusicVideoCatalogCache
{
    private sealed record Entry(bool? HasVideo, string? VideoUri, string? ManifestId);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(System.StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _audioUrisByVideoUri = new(System.StringComparer.Ordinal);

    // Upper bound on cached entries. Each entry is tiny (three nullable strings +
    // a bool) but, before this cap, _entries grew once per unique track URI for
    // the whole app lifetime — a long session over a large library accumulated
    // them without limit. Keys are evicted oldest-first once the cap is exceeded.
    private const int MaxEntries = 4096;
    private readonly ConcurrentQueue<string> _insertionOrder = new();

    // Records a newly-added key and evicts the oldest entries when over budget.
    // Approximate FIFO rather than strict LRU: entries are cheap and re-derivable
    // from a GraphQL refetch, so the simpler bound is sufficient. Called only when
    // a Note* actually inserts a new key (GraphQL response path — low frequency).
    private void TrackInsertion(string audioTrackUri)
    {
        _insertionOrder.Enqueue(audioTrackUri);
        while (_entries.Count > MaxEntries && _insertionOrder.TryDequeue(out var oldest))
            ForgetVideoAssociation(oldest);
    }

    public bool? GetHasVideo(string audioTrackUri)
    {
        if (string.IsNullOrEmpty(audioTrackUri)) return null;
        return _entries.TryGetValue(audioTrackUri, out var e) ? e.HasVideo : null;
    }

    public void NoteHasVideo(string audioTrackUri, bool hasVideo)
    {
        if (string.IsNullOrEmpty(audioTrackUri)) return;
        var added = false;
        _entries.AddOrUpdate(
            audioTrackUri,
            _ => { added = true; return new Entry(hasVideo, null, null); },
            (_, prev) => prev.HasVideo == hasVideo
                ? prev
                : prev with { HasVideo = hasVideo });
        if (added) TrackInsertion(audioTrackUri);
    }

    public void NoteVideoUri(string audioTrackUri, string videoTrackUri)
    {
        if (string.IsNullOrEmpty(audioTrackUri) || string.IsNullOrEmpty(videoTrackUri)) return;
        var added = false;
        _entries.AddOrUpdate(
            audioTrackUri,
            _ => { added = true; return new Entry(true, videoTrackUri, null); },
            (_, prev) => prev.HasVideo == true
                         && string.Equals(prev.VideoUri, videoTrackUri, System.StringComparison.Ordinal)
                ? prev
                : prev with { HasVideo = true, VideoUri = videoTrackUri });
        if (added) TrackInsertion(audioTrackUri);
        if (!_audioUrisByVideoUri.TryGetValue(videoTrackUri, out var existing)
            || !string.Equals(existing, audioTrackUri, System.StringComparison.Ordinal))
        {
            _audioUrisByVideoUri[videoTrackUri] = audioTrackUri;
        }
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
        var added = false;
        _entries.AddOrUpdate(
            audioTrackUri,
            _ => { added = true; return new Entry(true, null, manifestId); },
            (_, prev) => prev.HasVideo == true
                         && string.Equals(prev.ManifestId, manifestId, System.StringComparison.Ordinal)
                ? prev
                : prev with { HasVideo = true, ManifestId = manifestId });
        if (added) TrackInsertion(audioTrackUri);
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
