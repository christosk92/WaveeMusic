using System.Collections.Concurrent;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Hydration;

// ── THE session negative memo (design §2.4) ──────────────────────────────────────────────────────────────────────────
// "This entity has no such extension" is the single most repeated answer the trait wire gives, and before this it was
// remembered four times over — adornments, play counts, video detect and publishing each carried their own dictionary,
// so a track with no video still cost a request from the play-count pass and a track with no tempo still cost one from
// the video pass. ONE memo, keyed by (uri, kind), shared by the pipeline AND the display-only ExtensionReader, means a
// negative is paid for exactly once per session no matter who asks next.
//
// Session-scoped ON PURPOSE: the durable half of the answer already exists (ExtensionEtagCache persists a Missing row
// with a 24h TTL and no ETag). This tier only has to stop the SAME session re-walking the same page.

/// <summary>A bounded set of (uri, kind) pairs the wire has already answered "no" for. Concurrent because the pipeline
/// projects on the hydration pool while the reader answers drawer opens from the UI thread.</summary>
public sealed class NegativeMemo
{
    /// <summary>The ceiling. A 10k playlist of tracks with no video is 10k entries — ~1 MB of keys — so the memo has to
    /// stop somewhere. Past the cap it STOPS ADDING rather than evicting: an eviction policy here would be a cache
    /// (and a wrong one, since every entry is equally valid forever), whereas refusing to grow simply degrades to "the
    /// extension cache's own negative TTL answers it", which is the correct fallback and costs no request either.</summary>
    public const int Cap = 65_536;

    readonly ConcurrentDictionary<(string Uri, Xm.ExtensionKind Kind), byte> _known = new();

    /// <summary>How many negatives are held (diagnostics + the boundedness test).</summary>
    public int Count => _known.Count;

    public bool Contains(string uri, Xm.ExtensionKind kind)
        => !string.IsNullOrEmpty(uri) && _known.ContainsKey((uri, kind));

    public void Add(string uri, Xm.ExtensionKind kind)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (_known.Count >= Cap) return;
        _known.TryAdd((uri, kind), 0);
    }
}
