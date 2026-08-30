namespace Wavee.Core;

// The proto-free Recents domain. The wire read + proto decode live in Wavee.Backend.Playlists (RecentsWireMapper);
// everything here is a pure, framework-neutral projection of that mapper output. `GET /playlist/v2/list/recents/page`
// returns ~9,446 flat items collapsed into far fewer grouped cards: consecutive plays of one context fold behind a
// single group header whose `group_metadata` names its children. Rows are keyed on item_id (NOT uri — ~1,388 uris
// repeat across a real list), so the reconciler stays stable across reorders.

/// <summary>What a recents entry points at, derived from its uri scheme. <see cref="Collection"/> covers both
/// <c>spotify:collection:…</c> and the <c>spotify:user:{id}:collection</c> tail the recents surface uses; a header with
/// no uri of its own resolves its kind from the first child uri in its <c>group_metadata</c>.</summary>
public enum RecentsEntityKind { Unknown, Track, Playlist, Album, Artist, Show, Episode, Collection }

/// <summary>Why an entry is in Recents — the semantic of the <c>recent_type_*</c> format-attribute KEY (the value is
/// empty): the client played it (<see cref="Played"/>) or saved it (<see cref="Saved"/>).</summary>
public enum RecentsReason { Unknown, Played, Saved }

/// <summary>A grouped recents ROW is either a single play (renders as a track/entity row) or a collapsed group header
/// (renders as a card with a "played N" child count).</summary>
public enum RecentsRowKind { Single, Group }

/// <summary>The decoded <c>group_metadata</c> of a recents group header — a proto-free projection of the wire
/// <c>RecentsGroupMetadata</c>. <paramref name="ChildUris"/> is what an empty-uri single-context header is rendered
/// from; <paramref name="KindName"/>/<paramref name="KindCount"/> carry the header's declared Kind facet when present.</summary>
public sealed record RecentsGroupInfo(
    int ChildCount,
    IReadOnlyList<string> ChildUris,
    string? KindName = null,
    int KindCount = 0);

/// <summary>One flat recents entry as mapped off the wire (proto-free), BEFORE consecutive-run grouping. The semantic
/// payload of a recents item lives in the KEYS of its <c>format_attributes</c> (the values are empty, except
/// <c>group_metadata</c>) — this record carries those keys already interpreted, plus the decoded
/// <c>group_metadata</c> for a header. Produced by <c>RecentsWireMapper</c>; consumed by <see cref="RecentsList"/>.</summary>
/// <param name="ItemId">Hex <c>item_id</c> — the stable reconciler key (uris repeat).</param>
/// <param name="Uri">The entry's uri; may be <c>""</c> for a single-context group header.</param>
/// <param name="PlayedAtMs">The <c>timestamp</c> attribute (unix ms) — when it was played/saved.</param>
/// <param name="Reason">Parsed <c>recent_type_*</c> key.</param>
/// <param name="ContentType">Suffix of a <c>content_type_*</c> key (<c>"music"</c>, <c>"podcast"</c>, …); null when absent.</param>
/// <param name="GroupId">Parsed N of a <c>group_id_&lt;N&gt;</c> key: 0 = group header, &gt;0 = collapsed member, null = single.</param>
/// <param name="HasChildrenGroupId">The header-only <c>children_group_id</c> marker.</param>
/// <param name="Group">Decoded <c>group_metadata</c> (present on a header); null otherwise.</param>
public sealed record RecentsItem(
    string ItemId,
    string Uri,
    long PlayedAtMs,
    RecentsReason Reason,
    string? ContentType,
    int? GroupId,
    bool HasChildrenGroupId,
    RecentsGroupInfo? Group);

/// <summary>One grouped, display-ready recents row. Title/Subtitle/Image start null — they are hydrated later by the UI
/// stream (viewport-driven extended-metadata), never invented here. For a <see cref="RecentsRowKind.Group"/> row,
/// <paramref name="ContextUri"/> is what opening the card navigates to (the header uri, or null for a multi/empty-uri
/// header rendered from <paramref name="ChildUris"/>), and <paramref name="ChildCount"/> is the "played N" count.</summary>
public sealed record RecentsRow(
    RecentsRowKind Kind,
    string ItemId,
    string Uri,
    string? ContextUri,
    string? Title,
    string? Subtitle,
    Image? Image,
    int ChildCount,
    long PlayedAtMs,
    RecentsEntityKind EntityKind,
    RecentsReason Reason = RecentsReason.Unknown,
    string? ContentType = null,
    IReadOnlyList<string>? ChildUris = null);

/// <summary>A grouped recents page: the opaque revision (lowercase hex of the playlist4 revision bytes, round-trippable
/// via <see cref="System.Convert.FromHexString(string)"/> for the next diff) plus the collapsed rows in wire order.</summary>
public sealed record RecentsSnapshot(string? Revision, IReadOnlyList<RecentsRow> Rows)
{
    public static readonly RecentsSnapshot Empty = new(null, System.Array.Empty<RecentsRow>());
}

/// <summary>The recents READ seam the UI holds — the page never touches the wire fetcher, the protobuf, or the session.
/// Stateless by contract: the page owns the last revision + the last rows and hands them back on every revalidation, so
/// two surfaces reading recents can never desynchronise a shared cursor. The live implementation is
/// <c>Wavee.Backend.Playlists.RecentsFetcher</c>.</summary>
public interface IRecentsSource
{
    /// <summary>The cold page load — the whole grouped list.</summary>
    Task<RecentsSnapshot> FetchAsync(CancellationToken ct = default);

    /// <summary>Revision-gated revalidation. Returns the NEW snapshot, or null for "unchanged — keep what you have".
    /// A null/short <paramref name="lastRevision"/> means "no baseline", which converges through a full read.</summary>
    Task<RecentsSnapshot?> FetchDiffAsync(byte[]? lastRevision, IReadOnlyList<RecentsRow> lastRows, CancellationToken ct = default);
}

/// <summary>A stable recents identity the UI holds for the whole session: go-live installs the live fetcher and logout
/// resets it, so a mounted page never re-resolves a service nor keeps a session-bound one alive across a login change.
/// The seam is REQUIRED, never nullable — offline it is the named <see cref="NullRecentsService"/>, not null.</summary>
public sealed class SwitchableRecentsService : IRecentsSource
{
    volatile IRecentsSource _inner = NullRecentsService.Instance;

    public void SetInner(IRecentsSource inner) => _inner = inner ?? NullRecentsService.Instance;
    public void Reset() => _inner = NullRecentsService.Instance;

    public Task<RecentsSnapshot> FetchAsync(CancellationToken ct = default) => _inner.FetchAsync(ct);

    public Task<RecentsSnapshot?> FetchDiffAsync(byte[]? lastRevision, IReadOnlyList<RecentsRow> lastRows, CancellationToken ct = default)
        => _inner.FetchDiffAsync(lastRevision, lastRows, ct);
}

/// <summary>The offline / fake-backend recents source, named with intent: there is no recents endpoint without a live
/// Spotify session, so the page shows its empty state and a revalidation is a no-change. Deliberately NOT a nullable
/// seam — a null service would make every call site re-invent this answer.</summary>
public sealed class NullRecentsService : IRecentsSource
{
    public static readonly NullRecentsService Instance = new();

    public Task<RecentsSnapshot> FetchAsync(CancellationToken ct = default) => Task.FromResult(RecentsSnapshot.Empty);

    public Task<RecentsSnapshot?> FetchDiffAsync(byte[]? lastRevision, IReadOnlyList<RecentsRow> lastRows, CancellationToken ct = default)
        => Task.FromResult<RecentsSnapshot?>(null);
}

/// <summary>The pure flat-items → grouped-rows transform (and the item-vector equality the diff's revision-lies guard
/// leans on). No I/O, no proto, no framework — headless-tested against crafted items.</summary>
public static class RecentsList
{
    /// <summary>Collapse flat recents items (mapper output) into display rows, preserving wire order: an item that heads
    /// a group (<c>group_id_0</c> / <c>children_group_id</c>) becomes ONE <see cref="RecentsRowKind.Group"/> row carrying
    /// its <c>group_metadata</c> child count/uris; the members that follow it (<c>group_id_&lt;N&gt;</c>, N&gt;0) are
    /// absorbed into that card; every ungrouped item becomes a <see cref="RecentsRowKind.Single"/> row. Every row is keyed
    /// on the heading/entry item_id.</summary>
    public static IReadOnlyList<RecentsRow> Group(IReadOnlyList<RecentsItem> items)
    {
        var rows = new List<RecentsRow>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool isHeader = item.HasChildrenGroupId || item.GroupId == 0;
            if (isHeader)
            {
                rows.Add(HeaderRow(item));
                continue;
            }
            if (item.GroupId is int n && n > 0) continue;   // collapsed member — absorbed into the preceding header card
            rows.Add(SingleRow(item));
        }
        return rows;
    }

    /// <summary>Build a full snapshot (revision + grouped rows) from the mapper output.</summary>
    public static RecentsSnapshot Snapshot(string? revision, IReadOnlyList<RecentsItem> items)
        => new(revision, Group(items));

    /// <summary>The revision-lies guard's comparison: are two grouped row vectors the SAME list? A changed revision can
    /// carry byte-identical contents, so callers skip a rebuild when this holds.
    /// <para>Comparing only the item_id sequence is NOT enough: a group's identity is its heading item_id, and that stays
    /// put while the group GROWS — keep playing the same playlist and the run behind one stable header goes from "played
    /// 7" to "played 8" with the id vector untouched. Re-playing an entry likewise moves only its timestamp. So the
    /// per-row comparison is (item_id, child_count, played_at); everything else on a row is either derived from those or
    /// hydrated by the UI afterwards (title/subtitle/image), and must not make a no-op look like a change.</para>
    /// <para>Allocation-free: index reads only, no enumerator, no projection.</para></summary>
    public static bool SameItems(IReadOnlyList<RecentsRow> a, IReadOnlyList<RecentsRow> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            RecentsRow x = a[i], y = b[i];
            if (x.ChildCount != y.ChildCount || x.PlayedAtMs != y.PlayedAtMs) return false;   // cheap fields first
            if (!string.Equals(x.ItemId, y.ItemId, System.StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>Derive the entity kind from a spotify uri scheme (empty → <see cref="RecentsEntityKind.Unknown"/>). The
    /// <c>…:collection</c> tail (e.g. <c>spotify:user:{id}:collection</c>) resolves to <see cref="RecentsEntityKind.Collection"/>.</summary>
    public static RecentsEntityKind EntityKindOf(string uri)
        // The scheme walk is THE parser now (hydration-facade-design.md §1.1) — it already folds BOTH collection shapes
        // (`spotify:collection:*` and `spotify:user:{id}:collection`), which is what the hand-rolled `EndsWith(":collection")`
        // pre-check existed for. Kinds Recents has no row for (User, Prerelease, Concert) read as Unknown, exactly as before.
        => EntityUri.KindOf(uri) switch
        {
            EntityKind.Track => RecentsEntityKind.Track,
            EntityKind.Playlist => RecentsEntityKind.Playlist,
            EntityKind.Album => RecentsEntityKind.Album,
            EntityKind.Artist => RecentsEntityKind.Artist,
            EntityKind.Show => RecentsEntityKind.Show,
            EntityKind.Episode => RecentsEntityKind.Episode,
            EntityKind.Collection => RecentsEntityKind.Collection,
            _ => RecentsEntityKind.Unknown,
        };

    static RecentsRow HeaderRow(RecentsItem item)
    {
        var childUris = item.Group?.ChildUris;
        string? contextUri = item.Uri.Length > 0 ? item.Uri : null;
        var kind = item.Uri.Length > 0
            ? EntityKindOf(item.Uri)
            : childUris is { Count: > 0 } ? EntityKindOf(childUris[0]) : RecentsEntityKind.Unknown;
        int childCount = item.Group is { } g ? (g.ChildCount > 0 ? g.ChildCount : g.ChildUris.Count) : 0;
        return new RecentsRow(RecentsRowKind.Group, item.ItemId, item.Uri, contextUri,
            Title: null, Subtitle: null, Image: null,
            ChildCount: childCount, PlayedAtMs: item.PlayedAtMs, EntityKind: kind,
            Reason: item.Reason, ContentType: item.ContentType, ChildUris: childUris);
    }

    static RecentsRow SingleRow(RecentsItem item)
        => new(RecentsRowKind.Single, item.ItemId, item.Uri, ContextUri: null,
            Title: null, Subtitle: null, Image: null,
            ChildCount: 0, PlayedAtMs: item.PlayedAtMs, EntityKind: EntityKindOf(item.Uri),
            Reason: item.Reason, ContentType: item.ContentType);
}
