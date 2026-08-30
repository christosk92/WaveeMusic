using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

// ── The recents-page fetch (SpotifyLive boundary, but Backend so the orchestration is unit-tested) ────────────────────
// GETs /playlist/v2/list/recents/page → a zstd SelectedListContent of ~9,446 items, mapped (RecentsWireMapper) and
// grouped (RecentsList) into a proto-free RecentsSnapshot. Writes NOTHING to the store — it returns the snapshot; the UI
// stream owns residency + viewport hydration. Models on PlaylistFetcher: same IHttpExchange/baseUrl ctor shape, the same
// FormatRevision (%2C) diff idiom, the same SpotifyZstd multi-frame-safe decode.
//
// STATELESS on purpose: it holds no revision and no rows, so the page that renders recents owns `lastRevision` +
// `lastRows` and hands them back on each revalidation. It reaches the UI as IRecentsSource through the session-scoped
// SwitchableRecentsService (Services.Recents), installed on go-live and reset on logout.
public sealed class RecentsFetcher : IRecentsSource
{
    const string PagePath = "/playlist/v2/list/recents/page";
    const string DiffPath = "/playlist/v2/list/recents/page/diff";

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;

    public RecentsFetcher(IHttpExchange http, Func<string> baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    /// <summary>The cold page load: GET <c>/playlist/v2/list/recents/page</c> → decompress → parse → map → group. A body
    /// that carried no <c>contents</c> at all is an empty page (see <see cref="SnapshotOf"/>): the caller asked for the
    /// whole list and the server described none of it.</summary>
    public async Task<RecentsSnapshot> FetchAsync(CancellationToken ct = default)
        => await FetchPageAsync(ct).ConfigureAwait(false) ?? RecentsSnapshot.Empty;

    /// <summary>Revision-gated refresh: GET <c>/playlist/v2/list/recents/page/diff?revision=…&amp;handlesContent=&amp;hint_revision=…</c>
    /// → the new snapshot, or null for "unchanged, keep what you have". Branches exactly like
    /// <see cref="PlaylistFetcher.FetchPlaylistDiffAsync"/>, because the server answers on the same four shapes:
    /// <list type="bullet">
    /// <item><c>304</c> (0-byte body) → unchanged.</item>
    /// <item><c>up_to_date</c> set → unchanged.</item>
    /// <item>a <c>diff</c>: NO ops → unchanged (this is the COMMON no-change reply — capture session 292 is an
    /// uncompressed <c>200</c> of 123 bytes carrying <c>diff{from_revision == to_revision}</c> with <c>contents</c> AND
    /// <c>up_to_date</c> both absent); WITH ops → changed, and it converges through a full page read rather than
    /// replaying playlist ops onto GROUPED rows (a recents row is a collapsed RUN of items, so an item-index op does not
    /// address it).</item>
    /// <item><c>contents</c> present → a full snapshot, subject to the revision-lies guard below.</item>
    /// <item>anything else → unchanged (a 200 with nothing actionable in it).</item>
    /// </list>
    /// The REVISION-LIES guard: a changed revision can carry byte-identical CONTENTS (the server reserialises a
    /// list-level <c>filters</c> attribute inside the hashed region), so the freshly parsed row vector is compared
    /// against <paramref name="lastRows"/> and null is returned when it did not move — callers never rebuild ~1.7k rows
    /// for a no-op. An absent/short prior revision can't be diffed, so it converges via a full <see cref="FetchAsync"/>
    /// (always makes progress).
    /// <para>Contents-hash guard (rejected): We considered hashing the raw <c>contents</c> bytes before parsing,
    /// invalidating rows only when the digest changed. This was rejected because: (a) the server reserialises metadata
    /// attributes non-deterministically within the hashed region, defeating byte-level equivalence checks; (b) hashing
    /// ~9.4k items costs the same computational order as mapping them into the fresh row vector. The post-map
    /// <c>SameItems</c> structural comparison is sufficient, simpler, and more direct.</para>
    /// <para>INVARIANT: this never returns rows derived from a body that carried no <c>contents</c>. Mapping such a body
    /// yields ZERO items, and handing that back as a "snapshot" is what replaced 1,708 resident rows with 0.</para></summary>
    public async Task<RecentsSnapshot?> FetchDiffAsync(byte[]? lastRevision, IReadOnlyList<RecentsRow> lastRows, CancellationToken ct = default)
    {
        if (lastRevision is null || lastRevision.Length < 5)   // rev = 4B counter + hash; nothing to gate on → full
            return await FetchAsync(ct).ConfigureAwait(false);

        // revision wire string "counter,hexhash" — the comma MUST be %2C-encoded or the gateway 509s; hint_revision rides
        // the same value (capture: it selects the 0-byte 304 path over a full protobuf re-send).
        var enc = Uri.EscapeDataString(PlaylistFetcher.FormatRevision(lastRevision));
        var url = _baseUrl() + DiffPath + "?revision=" + enc + "&handlesContent=&hint_revision=" + enc;
        var headers = SpotifyHeaders.RecentsList(diff: true);
        Pl.SelectedListContent? slc;
        using (var resp = await _http.SendAsync(new HttpReq("GET", url, headers, null), ct).ConfigureAwait(false))
        {
            if (resp.Status == 304) return null;                                      // Not Modified = our revision is current
            if (resp.Status != 200) throw new InvalidOperationException($"recents diff failed ({resp.Status})");
            slc = await ParseAsync(resp.Body, ct).ConfigureAwait(false);
        }
        if (slc is null) return null;                                                 // empty 200 body = nothing actionable

        if (slc.HasUpToDate && slc.UpToDate) return null;                             // the server says our revision is current

        if (slc.Diff is { } diff)
            return diff.Ops.Count == 0
                ? null                                                                // from == to with no ops: NOTHING changed
                : await FetchPageAsync(ct).ConfigureAwait(false);                     // ops exist → re-read the whole page

        // Only a body that actually CARRIED contents may become a snapshot; SnapshotOf enforces it and nulls otherwise.
        if (SnapshotOf(slc) is not { } snapshot) return null;
        return RecentsList.SameItems(lastRows, snapshot.Rows) ? null : snapshot;      // revision lied — contents unchanged
    }

    /// <summary>The page GET, shared by the cold load and the ops-carrying diff's convergence. Null when the 200 body was
    /// empty or carried no <c>contents</c> — i.e. when there is nothing legitimate to build rows from.</summary>
    async Task<RecentsSnapshot?> FetchPageAsync(CancellationToken ct)
    {
        var headers = SpotifyHeaders.RecentsList(diff: false);
        using var resp = await _http.SendAsync(new HttpReq("GET", _baseUrl() + PagePath, headers, null), ct).ConfigureAwait(false);
        if (resp.Status != 200) throw new InvalidOperationException($"recents fetch failed ({resp.Status})");
        var slc = await ParseAsync(resp.Body, ct).ConfigureAwait(false);
        return slc is null ? null : SnapshotOf(slc);
    }

    /// <summary>Map + group a parsed reply into a snapshot — or null when the reply carried no <c>contents</c>. THIS is
    /// where the B-1 invariant lives: no contents ⇒ no rows to derive ⇒ no snapshot, ever.</summary>
    static RecentsSnapshot? SnapshotOf(Pl.SelectedListContent slc)
    {
        if (slc.Contents is null) return null;
        var (items, rev) = RecentsWireMapper.Map(slc);
        return RecentsList.Snapshot(rev is null ? null : Convert.ToHexStringLower(rev), items);
    }

    // Buffer the (small enough) body so the 4-byte zstd frame magic can be sniffed — .NET's automatic zstd decode
    // truncates multi-frame bodies, so SpotifyZstd.MaybeDecompressZstd is the only safe decode. Returns null for a
    // 0-byte body (a 200 that carried nothing).
    static async Task<Pl.SelectedListContent?> ParseAsync(Stream body, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct).ConfigureAwait(false);
        if (ms.Length == 0) return null;
        return Pl.SelectedListContent.Parser.ParseFrom(SpotifyZstd.MaybeDecompressZstd(ms.ToArray()));
    }
}
