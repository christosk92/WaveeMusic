using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── Connect context resolution (the proto-free contract) ──────────────────────────────────────────────────────────────
// A Connect "play" command carries an OPAQUE context uri (spotify:album:…, :playlist:…, the artist :list:…,
// :user:…:collection, :station:…) + a skip_to target — NEVER a track list. Turning that uri into ordered tracks is one
// unified server call (GET /context-resolve/v1/{uri}); the live impl lives in SpotifyLive (JSON + HTTP), so this Backend
// contract stays proto-free + unit-testable. (Contrast WaveeMusic, which hand-rolls a 3-layer context cache + a
// retry/cooldown dict inside one 700-line ContextResolver; here the SWR + in-flight dedup is the shared Resource engine
// and metadata hydration is the hydration facade (IEntityHydrator) — no bespoke caches.)

/// <summary>One track in a resolved/queued context: the domain <see cref="Track"/> plus its provider-assigned context
/// <c>uid</c> (skip_to-by-uid, the PutState player_state track.uid, outbound page uids). Uid is "" for synthetic /
/// user-queued items. A struct wrapping the already-heap-allocated Track — no extra per-track allocation.</summary>
public readonly record struct QueuedTrack(
    Track Track,
    string Uid,
    string Provider = "context",
    IReadOnlyDictionary<string, string>? Metadata = null,
    QueueRowKind RowKind = QueueRowKind.Playable)
{
    public string Uri => Track.Uri;
}

/// <summary>What an inbound play command asks us to play, parsed proto-free from the command envelope. <see cref="Url"/>
/// carries a collection's sort/filter query (collections are URI-only on the wire); <see cref="EmbeddedPages"/> is
/// non-null only when the command embedded a custom-ordered page (a sorted playlist) — then we play it verbatim instead
/// of resolving.</summary>
public readonly record struct ContextSpec(
    string Uri, string? Url,
    IReadOnlyList<QueuedRef>? EmbeddedPages,
    string? SkipToTrackUri, string? SkipToTrackUid, int? SkipToIndex,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>A UI/local play intent: just a context uri + an optional start index (no remote skip_to/pages).</summary>
    public static ContextSpec ForUri(string uri, int startIndex = 0) => new(uri, null, null, null, null, startIndex);
}

/// <summary>A bare (uri, uid) pair as it appears in a command's embedded context page — pre-hydration.</summary>
public readonly record struct QueuedRef(
    string Uri,
    string Uid,
    string Provider = "context",
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>One entry in an outbound set_queue snapshot. <c>IsQueued</c> ⇒ provider:"queue" + metadata{is_queued:"true"}
/// (a user-queued row); otherwise provider:"context" + metadata{} (a context-continuation row).
/// <para><see cref="Track"/> is the richer domain track an INBOUND set_queue row can be parsed into (title, artists,
/// album, duration, image, explicit — every live capture carries these inline on the row, not just uri/uid), so a row
/// the session has never held before doesn't have to fall back to a bare uri placeholder. Null for an outbound row
/// (we always have the real Track already) and for an inbound row too thin to build one from.</para></summary>
public readonly record struct QueueWireEntry(
    string Uri,
    string Uid,
    bool IsQueued,
    IReadOnlyDictionary<string, string>? Metadata = null,
    Track? Track = null);

public readonly record struct ContextPage(IReadOnlyList<QueuedTrack> Tracks, string? NextPageUrl)
{
    public static readonly ContextPage Empty = new(Array.Empty<QueuedTrack>(), null);
}

/// <summary>The resolved context: ordered, metadata-hydrated tracks, where to start, and the paging/sort metadata the
/// publisher needs. Lean by design — only what the controller + PutState consume.</summary>
public readonly record struct ResolvedContext(
    IReadOnlyList<QueuedTrack> Tracks, int StartIndex,
    string? SortingCriteria, string? NextPageUrl, bool IsInfinite,
    IReadOnlyDictionary<string, string>? Metadata = null, string? ContextUri = null)
{
    public static readonly ResolvedContext Empty = new(Array.Empty<QueuedTrack>(), 0, null, null, false, null, null);
    public int Count => Tracks.Count;
}

/// <summary>Resolves an opaque context uri to ordered, metadata-hydrated tracks. ONE unified path for every context type
/// (the server decides order + sorting); pagination is lazy via <see cref="LoadMoreAsync"/>. The live impl is
/// SpotifyLive/LiveContextResolver; tests inject a fake.</summary>
public interface IContextResolver
{
    Task<ResolvedContext> ResolveAsync(ContextSpec spec, CancellationToken ct = default);
    Task<ContextPage> LoadMoreAsync(string nextPageUrl, CancellationToken ct = default);
    Task<ResolvedContext> ResolveAutoplayAsync(string contextUri, IReadOnlyList<string> recentTrackUris, CancellationToken ct = default);
    Task<ResolvedContext> ResolveAutopodcastAsync(string contextUri, IReadOnlyList<string> recentEpisodeUris, CancellationToken ct = default);
    /// <summary>Resolve a radio seed (a <c>spotify:track:</c> / <c>spotify:artist:</c> uri) to a concrete radio PLAYLIST
    /// uri via <c>inspiredby-mix/v2/seed_to_playlist</c>. Returns <c>spotify:playlist:{id}</c> or <c>null</c> (no radio /
    /// offline). The resolved playlist is then a NORMAL playlist context (no <c>:station:</c> special-casing at the start
    /// boundary — its natural end still triggers the existing autoplay tail).</summary>
    Task<string?> ResolveRadioSeedAsync(string seedUri, CancellationToken ct = default);
    /// <summary>Hydrate loose track refs (add_to_queue / set_queue items) into queued tracks with display + duration metadata.</summary>
    Task<IReadOnlyList<QueuedTrack>> HydrateAsync(IReadOnlyList<QueuedRef> refs, CancellationToken ct = default);
}

/// <summary>Pure helpers shared by every IContextResolver impl (proto-free, alloc-free, unit-testable).</summary>
public static class ContextResolve
{
    /// <summary>Identity-strict start index (F2, §7.3): uid → uri → NOT a blind index. No skip target specified ⇒ 0 (start
    /// at the top). A target that ISN'T in the list ⇒ -1 (an identity miss — the caller pages deeper / patches the clicked
    /// track in; it must NEVER play an unrelated index across two divergent orderings, which was the IU→FANCY bug).</summary>
    public static int FindStartIndex(IReadOnlyList<QueuedTrack> tracks, string? trackUri, string? trackUid)
    {
        if (!string.IsNullOrEmpty(trackUid))
            for (int i = 0; i < tracks.Count; i++) if (tracks[i].Uid == trackUid) return i;
        if (!string.IsNullOrEmpty(trackUri))
            for (int i = 0; i < tracks.Count; i++) if (tracks[i].Track.Uri == trackUri) return i;
        return -1;   // identity miss — the blind index fallback is intentionally gone (F2)
    }

    /// <summary>Resolve the play start index from a <see cref="ContextSpec"/>: uid → uri → (index-only skip_to) → 0.
    /// When uid/uri ARE specified but miss, returns -1 so the caller can page/patch — it never plays a blind index across
    /// divergent orderings (F2). Inbound wire <c>track_index</c>-only skip_to and local <see cref="ContextSpec.ForUri"/>
    /// start indices ride <see cref="ContextSpec.SkipToIndex"/>.</summary>
    public static int ResolveStartIndex(IReadOnlyList<QueuedTrack> tracks, in ContextSpec spec)
    {
        if (!string.IsNullOrEmpty(spec.SkipToTrackUid) || !string.IsNullOrEmpty(spec.SkipToTrackUri))
            return FindStartIndex(tracks, spec.SkipToTrackUri, spec.SkipToTrackUid);
        if (spec.SkipToIndex is int idx && idx >= 0 && idx < tracks.Count) return idx;
        return 0;
    }

    /// <summary>The shared restore/identity-miss ladder (playback-restore fix §6) used by transfer, the recovery heal and
    /// the local-snapshot restore: <b>uid → URI → saved index (in range AND a playable row — never a blind index across a
    /// regenerated ordering, F2) → context head</b> (first playable), the head rung only when the caller opted in
    /// (<c>always_play_something</c>, or launch recovery's "play something paused"). Returns -1 when every rung misses —
    /// the caller then patches the saved current in OUTSIDE the spine (with a cursor hint so Next() advances to the
    /// successor of where it sat, not context[0]).</summary>
    public static int ResolveRestoreIndex(
        IReadOnlyList<QueuedTrack> tracks, string? trackUri, string? trackUid, int savedIndex, bool allowContextHead)
    {
        int i = FindStartIndex(tracks, trackUri, trackUid);
        if (i >= 0) return i;
        if (savedIndex >= 0 && savedIndex < tracks.Count && tracks[savedIndex].RowKind == QueueRowKind.Playable)
            return savedIndex;
        if (allowContextHead)
            for (int k = 0; k < tracks.Count; k++)
                if (tracks[k].RowKind == QueueRowKind.Playable) return k;
        return -1;
    }

    /// <summary>May Spotify's own services (context-resolve, autoplay/radio, the queue heal) be asked about this uri?
    /// <para>Two namespaces answer NO. A non-<c>spotify:</c> uri (a playback module's <c>wavee:module:…</c>, a local
    /// file, a session playlist) is not in Spotify's catalog at all — asking is a guaranteed 400 and a user-visible
    /// error toast behind it. And <c>spotify:local:*</c> — Spotify's OWN self-describing local-file namespace, which is
    /// also what <c>ConnectUriMask</c> publishes non-publishable rows as — carries its metadata IN the uri precisely
    /// because there is nothing on the server to resolve. Every caller that would reach the network tests this FIRST,
    /// so a non-Spotify session simply has no continuation instead of a failed round trip.</para></summary>
    public static bool IsSpotifyContext(string? uri) =>
        uri is { Length: > 0 }
        && uri.StartsWith("spotify:", StringComparison.Ordinal)
        && !uri.StartsWith(MediaSources.ConnectUriMask.Prefix, StringComparison.Ordinal);

    /// <summary>True for algorithmic/endless contexts (station/radio/autoplay) — their end triggers autoplay, not Ended.</summary>
    public static bool IsInfinite(string uri) =>
        uri.Contains(":station:", StringComparison.Ordinal)
        || uri.Contains(":radio:", StringComparison.Ordinal)
        || uri.Contains(":autoplay", StringComparison.Ordinal);

    /// <summary>Collection contexts (Liked Songs) are URI-only on the wire — sort/filter rides on context.url.</summary>
    public static bool IsCollection(string uri) => uri.Contains(":collection", StringComparison.OrdinalIgnoreCase);

    /// <summary>A uri-only placeholder Track (duration 0) for a hydration miss / no-store fallback — preserves the uri so
    /// the queue/state and skip_to-by-uri stay valid even when metadata isn't available.</summary>
    public static Track Synthetic(string uri)
    {
        return new Track(EntityUri.IdOf(uri), uri, uri, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);
    }
}

/// <summary>The offline / no-store fallback — every resolve yields nothing (the controller logs "0 tracks" and bails).
/// Wired when the real backend store isn't available (the in-memory fake backend / pre-login).</summary>
public sealed class EmptyContextResolver : IContextResolver
{
    public static readonly EmptyContextResolver Instance = new();
    public Task<ResolvedContext> ResolveAsync(ContextSpec spec, CancellationToken ct = default) => Task.FromResult(ResolvedContext.Empty);
    public Task<ContextPage> LoadMoreAsync(string nextPageUrl, CancellationToken ct = default) => Task.FromResult(ContextPage.Empty);
    public Task<ResolvedContext> ResolveAutoplayAsync(string contextUri, IReadOnlyList<string> recentTrackUris, CancellationToken ct = default)
        => Task.FromResult(ResolvedContext.Empty);
    public Task<ResolvedContext> ResolveAutopodcastAsync(string contextUri, IReadOnlyList<string> recentEpisodeUris, CancellationToken ct = default)
        => Task.FromResult(ResolvedContext.Empty);
    public Task<string?> ResolveRadioSeedAsync(string seedUri, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<QueuedTrack>> HydrateAsync(IReadOnlyList<QueuedRef> refs, CancellationToken ct = default)
    {
        var arr = new QueuedTrack[refs.Count];
        for (int i = 0; i < refs.Count; i++)
        {
            string provider = string.IsNullOrEmpty(refs[i].Provider) ? "context" : refs[i].Provider;
            arr[i] = new QueuedTrack(ContextResolve.Synthetic(refs[i].Uri), refs[i].Uid, provider, refs[i].Metadata);
        }
        return Task.FromResult<IReadOnlyList<QueuedTrack>>(arr);
    }
}
