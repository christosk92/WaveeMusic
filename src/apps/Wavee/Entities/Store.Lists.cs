// ── Entities/Store.Lists.cs — SHELL with a CORE gate (owner L1, wave D2; plan §3.2) ──────────────────────────────────
//
// LISTS ON DISK (docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md §3.2). Until wave D2 a playlist's
// membership, the account's rootlist and the revision each was answered at never reached the file, so every launch
// re-read every playlist a surface showed, in full, and a `/diff` across launches was impossible (plan §1.2: session
// a7ce5209, ~30 full `GET /playlist/v2/playlist/{id}` three minutes after the previous launch, zero diffs). This named
// partial of Store.cs is the whole of it:
//
//     list_head(scope_id, list, revision, total, written_at, written_by)             one row per list
//     list_item(scope_id, list, position, uri, item_id, added_at, added_by,           one row per member, in order
//               chart_status, chart_pos, chart_prev, flags, kind, depth, wire_pos, folder_id, folder_name)
//
// `list` is the playlist's uri, or `rootlist:<account uri>` — ONE table for both, because a rootlist IS a playlist4 list
// (its last five columns are the marker stream's). Account isolation is the existing `scope_id`. Every string is TEXT:
// the in-memory edge holds slots and interned ids, and neither means anything after a restart.
//
// THE WRITE. `WriteBehind` — the call every wire answer's commit is followed by — snapshots, on the UI thread, every list
// the batch settled, and ONE store-thread transaction per list replaces its rows, upserts its head and rewrites the
// playlist row's count: rows + revision + total together or not at all (0.2.9 `SqliteColdStore.ReplaceMembership`: "a
// torn write can never leave a half-applied membership"). The gate is PURE (`ListWrite.MayPersist`): not Complete, a
// partial window, an optimistic row, or a revision that is not a well-formed playlist4 head ⇒ nothing is written. And
// the revision must be the one THIS batch carried for THIS list — never whatever the live row happens to hold — because
// a revision whose contents were not applied is never stored (plan §2).
//
// THE READ. `ReadList` stages what it read EXACTLY as the wire decoder stages a full read — a `PlaylistTracks` run of
// identities (a catalog uri as its packed gid, anything else as text) with the adder as a user uri and the item id as
// hex, or a `StagedRootlist` marker stream — plus the head's revision and count, and lands it through `Entities.Commit`.
// Interning, AddRef ownership, the adder's user row, the membership fold (`Playlist.Refold`) and every derived fact are
// therefore the wire's own, by construction; nothing here knows a payload's layout on the way back. The staged answer
// is COMMITTED and never written behind: it came from this file. It lands only on a list that is still Unknown — a
// live answer that beat the disk is never overwritten.
//
// THE STAGING IS SHARED (wave D3, plan §3.3). One per-row staging — `ListRow`s in, a whole-list run (or marker
// stream) + the revision + the count out — serves the disk read (its count at Thin) and every LIVE list that is not a
// wire read: the `/diff` replay (Spotify.Api.Library.cs's `ListReplay`, landed by `Spotify.Decode.PlaylistReplay`/
// `RootlistReplay`) and a dealer push applied in place, both through the public `StageList` (its count at Full). It
// touches the staging and nothing else, so the API thread may call it; a replayed list therefore commits exactly like a
// disk read, and — being a live answer with a revision beside a whole run — is written behind like a full read.
// `SnapshotList` is the other half: the settled list as text, taken on the UI thread and handed to the provider in the
// `FetchBatch` (`Fetch.FillBaselines`), because the provider may never read a live table.
//
// NOT HERE: a list completed by several pages (a page answer describes its window, and pages can straddle revisions,
// so it is never stored), the dealer.
//
// Rules: C1 (the UI thread snapshots and lands; the store thread reads, writes and never interns), C7 (a read carries its
// scope epoch, and a replaced scope's answer is dropped with its continuation), C8 (a refused read falls through to the
// network; a refused write is a cache miss), P7 (a member's instant is the wire's UNIX seconds already — stored as is).

using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Wavee;

// ── 1. CORE: the row, and the gate ───────────────────────────────────────────────────────────────────────────────────

/// <summary>One persisted list member, as TEXT: what survives a restart of a <see cref="PlaylistTrackEdge"/> or a
/// <see cref="RootlistEdge"/>. A slot, an interned id and a text-form <see cref="EntityId"/>'s payload are all
/// process-local, so no field is one. A playlist member fills the first eight fields and is an
/// <see cref="RootlistKind.Item"/> at depth 0; a rootlist row fills <paramref name="Uri"/> (empty for a folder marker),
/// <paramref name="AddedAt"/> and the last five.</summary>
/// <param name="Uri">The member's uri. EMPTY for a rootlist folder marker — and, inside the WRITE snapshot, for a GID
/// member, whose key the store thread formats (Store.cs's header: the format stays off the frame). A replay baseline
/// (<see cref="Store.SnapshotList"/>) always carries it: its reader has no table to format from.</param>
/// <param name="ItemId">The playlist4 <c>item_id</c> as lowercase hex — the reconciler's key; null when the wire gave none.</param>
/// <param name="AddedAt">UNIX seconds — the wire's own unit on both relations; 0 = none.</param>
/// <param name="AddedBy">The adder's user uri (<c>spotify:user:&lt;name&gt;</c>), or null.</param>
/// <param name="WirePos">The rootlist's wire position (<see cref="RootlistEdge.Position"/>), which a skipped
/// non-playlist item makes differ from the row's index.</param>
public readonly record struct ListRow(
    string Uri, string? ItemId, int AddedAt, string? AddedBy,
    byte ChartStatus, ushort ChartPos, ushort ChartPrev, byte Flags,
    RootlistKind Kind, byte Depth, ushort WirePos, string? FolderId, string? FolderName);

/// <summary>THE list-write gate and the list head's rules — pure, no table, no file, no clock (plan §2: "one revision
/// gate: only a well-formed revision may be stored, on every writer"). 0.2.9 persisted a rootlist push's URI BYTES as a
/// revision once, and it "would keep failing every equality gate forever"; a poisoned persisted list is forever too
/// (plan §7), which is why this refuses on any doubt and the head carries a stamp a later fix can refuse by.</summary>
public static class ListWrite
{
    /// <summary>A playlist4 revision's hash in the wire spelling: 20 bytes as 40 lowercase hex characters. Every
    /// revision in the four 2026-09 captures is a 4-byte counter + a 20-byte hash (24 bytes — the length the tune
    /// route's own revision check also demands), and both formatters (<c>Spotify.Api.FormatRevision</c>,
    /// <c>Spotify.Decode.Revision</c>) spell it <c>{counter},{hex}</c>.</summary>
    public const int HashHexChars = 40;

    /// <summary>The counter is an unsigned 32-bit number: at most ten decimal digits.</summary>
    public const int MaxCounterDigits = 10;

    /// <summary>THE LIST DECODER'S GENERATION, stamped into every <c>list_head.written_by</c>. Bump it when a decoder
    /// fix means lists written before it cannot be trusted (plan §7's poison mitigation): every head stamped with an
    /// older generation then reads as a miss, and the list is read from the network and written again — no migration,
    /// no schema change, no file dropped.</summary>
    public const int Generation = 1;

    /// <summary>The <c>list</c> key prefix of an account's rootlist: <c>rootlist:spotify:user:&lt;name&gt;</c>.</summary>
    public const string RootlistPrefix = "rootlist:";

    static readonly string s_stampPrefix = "g" + Generation.ToString(CultureInfo.InvariantCulture) + " ";

    /// <summary>May this list be written to disk? All of it must hold, or nothing is written:
    /// <list type="bullet">
    /// <item>the list is <see cref="EdgeState.Complete"/> — Unknown has nothing to write and Partial is a window;</item>
    /// <item><paramref name="wholeAnswer"/>: the batch that settled it rewrote the WHOLE list. A list completed by pages
    /// was read across requests that may straddle revisions, and one revision cannot vouch for all of them;</item>
    /// <item><paramref name="rows"/> equals <paramref name="total"/> — fewer rows than the list says it has is a
    /// partial window whatever its state claims;</item>
    /// <item>no row is optimistic (<paramref name="anyPending"/>: the table's <see cref="EdgePending"/> column — the
    /// pending bits live there, never in <see cref="PlaylistTrackEdge.Flags"/>, which carries wire flags only). Only
    /// SETTLED membership is written: 0.2.9 stored optimistic rows under the old revision, safe there only because a
    /// durable outbox replayed them, and 0.3's pending state is in memory;</item>
    /// <item><paramref name="revision"/> is well-formed (<see cref="IsWellFormedRevision"/>).</item>
    /// </list></summary>
    public static bool MayPersist(EdgeState state, int rows, int total, bool wholeAnswer, bool anyPending, string? revision)
        => IsSettled(state, rows, total, anyPending) && wholeAnswer && IsWellFormedRevision(revision);

    /// <summary>Is the list a SETTLED whole — <see cref="EdgeState.Complete"/>, every row the list says it has, no
    /// optimistic row? The half of <see cref="MayPersist"/> that is about the rows alone, and the whole of what a replay
    /// baseline needs (<see cref="Store.SnapshotList"/>): ops replayed over a window, or over rows the server has not
    /// confirmed, describe a list nobody has.</summary>
    public static bool IsSettled(EdgeState state, int rows, int total, bool anyPending)
        => state == EdgeState.Complete && rows >= 0 && rows == total && !anyPending;

    /// <summary>Is this a playlist4 revision in its wire spelling? Precisely: 1 to <see cref="MaxCounterDigits"/>
    /// ASCII digits whose value fits a <c>uint</c>, ONE comma, then exactly <see cref="HashHexChars"/> LOWERCASE hex
    /// characters — nothing before, between or after. Empty, null, a uri, a truncated hash, an upper-case one and a
    /// negative counter all fail.</summary>
    public static bool IsWellFormedRevision(ReadOnlySpan<char> revision)
    {
        int comma = revision.IndexOf(',');
        if (comma < 1 || comma > MaxCounterDigits) return false;
        ulong counter = 0;
        for (int i = 0; i < comma; i++)
        {
            char c = revision[i];
            if (c is < '0' or > '9') return false;
            counter = counter * 10 + (ulong)(c - '0');
        }
        if (counter > uint.MaxValue) return false;
        ReadOnlySpan<char> hash = revision[(comma + 1)..];
        if (hash.Length is < 8 or > 56 || (hash.Length & 1) != 0) return false;
        for (int i = 0; i < hash.Length; i++)
            if (hash[i] is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }

    /// <summary>Does any row carry an optimistic bit? One vectorized scan over the table's pending column.</summary>
    public static bool AnyPending(ReadOnlySpan<byte> pending) => pending.IndexOfAnyExcept((byte)0) >= 0;

    /// <summary><c>list_head.written_by</c>: the generation, then the build that wrote it (diagnostics only — which
    /// build wrote this list is what a poisoned-list report has to be able to answer).</summary>
    public static string Stamp(string? build) => s_stampPrefix + (string.IsNullOrEmpty(build) ? "unknown" : build);

    /// <summary>May a list whose head carries this stamp be read back? Only this <see cref="Generation"/>'s.</summary>
    public static bool Trusts(string? writtenBy)
        => writtenBy is not null && writtenBy.StartsWith(s_stampPrefix, StringComparison.Ordinal);

    /// <summary>The <c>list</c> key of an account's rootlist.</summary>
    public static string RootlistKey(string userUri) => RootlistPrefix + userUri;
}

// ── 2. SHELL: the tables, the write, the read ────────────────────────────────────────────────────────────────────────

public static partial class Store
{
    /// <summary>The two list tables and their one secondary index, appended verbatim to <see cref="Ddl"/> — so they are
    /// part of the fingerprint that NAMES the file: a column added here opens a new file, never a migration.
    /// <c>ix_list_item_uri</c> answers "which lists hold this uri", the dealer's question (wave D3).</summary>
    const string ListDdl =
        "CREATE TABLE IF NOT EXISTS list_head(scope_id INT NOT NULL, list TEXT NOT NULL, revision TEXT NOT NULL, total INT NOT NULL, written_at INT NOT NULL, written_by TEXT NOT NULL, PRIMARY KEY(scope_id,list)) WITHOUT ROWID;\n" +
        "CREATE TABLE IF NOT EXISTS list_item(scope_id INT NOT NULL, list TEXT NOT NULL, position INT NOT NULL, uri TEXT NOT NULL, item_id TEXT, added_at INT, added_by TEXT, chart_status INT, chart_pos INT, chart_prev INT, flags INT, kind INT, depth INT, wire_pos INT, folder_id TEXT, folder_name TEXT, PRIMARY KEY(scope_id,list,position)) WITHOUT ROWID;\n" +
        "CREATE INDEX IF NOT EXISTS ix_list_item_uri ON list_item(scope_id, uri);\n";

    /// <summary>The list saves one <see cref="WriteBehind"/> prepared, held until its row job has been accepted.
    /// UI thread only (C1), reused (P8).</summary>
    static readonly List<Action> s_listJobs = new(4);

    static string? s_buildStamp;

    /// <summary><c>list_head.written_by</c> for this build: <see cref="ListWrite.Stamp"/> over the informational
    /// version (the version and the commit it was built from). Resolved once, on the UI thread.</summary>
    static string BuildStamp => s_buildStamp ??= ListWrite.Stamp(
        typeof(Store).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    // ── the write ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Persist ONE parent's list as it stands in the live table — its SETTLED membership and the revision it is
    /// true at, together or not at all. UI thread (C1): the rows are snapshotted here, as text, and one store-thread
    /// transaction replaces the list's rows, upserts its head and, for a playlist, rewrites the row's count.
    /// <paramref name="relation"/> is <see cref="EdgeRelation.PlaylistTracks"/> (parent = a playlist slot) or
    /// <see cref="EdgeRelation.Rootlist"/> (parent = the account's user slot).
    /// <para>The caller vouches that <paramref name="revision"/> describes the WHOLE live list. That is why
    /// <see cref="WriteBehind"/> does not come through here blind: it passes the revision the committed batch carried
    /// for that very list, and only for a whole-list answer. Returns false, writing nothing, with no store, when the
    /// gate refuses (<see cref="ListWrite.MayPersist"/>), when a member has no identity, or when the queue is full (C8:
    /// a dropped write is a cache miss).</para></summary>
    public static bool SaveList(Scope scope, EdgeRelation relation, int parent, string? revision)
    {
        ListSnapshot? list = Snapshot(scope, relation, parent, revision, wholeAnswer: true);
        return list is not null && Enqueue(list.Save);
    }

    /// <summary>UI THREAD, from <see cref="WriteBehind"/> right after <c>Entities.Commit</c> landed the same staging:
    /// prepare a save for every list the batch settled — each <see cref="Relation.PlaylistTracks"/> run and each
    /// staged rootlist — into <see cref="s_listJobs"/>. Read off the LIVE tables (the commit already landed this batch)
    /// and paired with the revision the batch carried for that list; a list the batch named without one is not written.</summary>
    static void PrepareListsTouchedBy(Staging staging)
    {
        s_listJobs.Clear();
        if (staging.Epoch != 0 && staging.Epoch != Entities.Current.Epoch) return;   // C7: the scope moved on
        Scope scope = Entities.Current;

        if (staging.EdgesOrNull is { RunCount: > 0 } edges)
        {
            ReadOnlySpan<StagedRun> runs = edges.Runs;
            for (int i = 0; i < runs.Length; i++)
            {
                ref readonly StagedRun run = ref runs[i];
                if (run.Relation is not (Relation.PlaylistTracks or Relation.ShowEpisodes)) continue;
                bool show = run.Relation == Relation.ShowEpisodes;
                int parent = SlotOf(staging, show ? scope.Shows : scope.Playlists, in run.Parent);
                if (parent == Table.None) continue;
                // Only a WHOLE-list rewrite (`Offset < 0`) is the list the revision describes; a page is a window.
                ListSnapshot? list = Snapshot(scope, show ? EdgeRelation.ShowEpisodes : EdgeRelation.PlaylistTracks, parent,
                                              show ? ShowRevisionStagedFor(staging, scope.Shows, parent) : RevisionStagedFor(staging, scope.Playlists, parent), wholeAnswer: run.Offset < 0);
                if (list is not null) s_listJobs.Add(list.Save);
            }
        }

        if (staging.StagedRootlists is { Count: > 0 } rootlists)
        {
            ReadOnlySpan<StagedRootlist> lists = rootlists.Span;
            for (int i = 0; i < lists.Length; i++)
            {
                ref readonly StagedRootlist list = ref lists[i];
                // A stream with no revision (the pathfinder's libraryV3) lands in memory and never on disk.
                if (list.Revision.IsEmpty) continue;
                int parent = SlotOf(staging, scope.Users, in list.Parent);
                if (parent == Table.None) continue;
                ListSnapshot? snapshot = Snapshot(scope, EdgeRelation.Rootlist, parent, Utf16Of(staging, list.Revision),
                                                  wholeAnswer: true);
                if (snapshot is not null) s_listJobs.Add(snapshot.Save);
            }
        }
    }

    /// <summary>UI THREAD: queue what <see cref="PrepareListsTouchedBy"/> prepared, behind the row job just accepted.</summary>
    static void EnqueuePreparedLists()
    {
        for (int i = 0; i < s_listJobs.Count; i++) Enqueue(s_listJobs[i]);
        s_listJobs.Clear();
    }

    /// <summary>The revision this batch carried for <paramref name="parent"/>: the playlist rows it staged with a
    /// revision, the last one winning exactly as it does at commit. Null when the batch carried none — a pathfinder
    /// page, an answer that named no revision — and then the list is not written: its rows were never vouched for by a
    /// revision. (A <c>/diff</c> answered with <c>contents</c> and no attributes DOES carry one since wave D3:
    /// <c>Spotify.Decode.PlaylistFormatAttributes</c> stages the revision whenever the answer carries rows.)</summary>
    static string? ShowRevisionStagedFor(Staging staging, Table shows, int parent)
    {
        if (staging.ShowsOrNull is not { Count: > 0 } staged) return null;
        string? revision = null;
        foreach (ref readonly var row in staged.Span)
            if (!row.ListRevision.IsEmpty && SlotOf(staging, shows, in row.Id) == parent)
                revision = Utf16Of(staging, row.ListRevision);
        return revision;
    }

    static string? RevisionStagedFor(Staging staging, Table playlists, int parent)
    {
        if (staging.StagedPlaylists is not { Count: > 0 } staged) return null;
        ReadOnlySpan<StagedPlaylist> rows = staged.Span;
        string? revision = null;
        for (int i = 0; i < rows.Length; i++)
            if (!rows[i].Revision.IsEmpty && SlotOf(staging, playlists, in rows[i].Id) == parent)
                revision = Utf16Of(staging, rows[i].Revision);
        return revision;
    }

    /// <summary>A staged identity's slot in <paramref name="table"/> WITHOUT allocating one: a batch that was committed
    /// has every row it names, and one that was not must not grow a live table from the write path.</summary>
    static int SlotOf(Staging staging, Table table, in StagedId id)
    {
        if (id.IsEmpty) return Table.None;
        int slot;
        bool found = id.Packed.IsEmpty ? table.TryGetSlot(staging.Utf8(id.Text), out slot) : table.TryGetSlot(id.Packed, out slot);
        return found ? slot : Table.None;
    }

    static string? Utf16Of(Staging staging, TextRef text) => text.IsEmpty ? null : Encoding.UTF8.GetString(staging.Utf8(text));

    static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>UI THREAD. The gate, then the WRITE snapshot: null when there is no store or the list may not be written
    /// (<see cref="ListWrite.MayPersist"/> for this revision and this window).</summary>
    static ListSnapshot? Snapshot(Scope scope, EdgeRelation relation, int parent, string? revision, bool wholeAnswer)
    {
        if (!s_open) return null;
        ListRow[]? rows;
        EntityId[] ids;
        string key;
        if (relation is EdgeRelation.PlaylistTracks or EdgeRelation.ShowEpisodes)
            rows = SnapshotPlaylist(scope, parent, baseline: false, revision, wholeAnswer, out ids, out key, relation == EdgeRelation.ShowEpisodes);
        else if (relation == EdgeRelation.Rootlist)
            rows = SnapshotRootlist(scope, parent, baseline: false, revision, wholeAnswer, out ids, out key);
        else return null;
        return rows is null ? null
             : new ListSnapshot(key, revision!, rows, ids, relation == EdgeRelation.PlaylistTracks ? key : null,
                                Entities.Now, BuildStamp);
    }

    /// <summary>UI THREAD (C1). ONE parent's settled list as TEXT — the BASELINE a <c>/diff</c>'s ops are replayed over
    /// on an API thread, which may never read a live table (<c>Fetch.FillBaselines</c> puts it in the batch; plan §3.3).
    /// <paramref name="relation"/> is <see cref="EdgeRelation.PlaylistTracks"/> (parent = a playlist slot) or
    /// <see cref="EdgeRelation.Rootlist"/> (parent = the account's user slot).
    /// <para>The rows the write snapshot takes, with ONE difference: a GID member's uri is formatted here (the reader has
    /// no table to format from — one ~80 ns format per catalog row, once per revalidation). Null — no baseline, so a
    /// diff with ops is read in full — for a list that is not a settled whole (<see cref="ListWrite.IsSettled"/>:
    /// Unknown, a Partial window, an optimistic row), for a member with no identity, and for any other relation. The store
    /// need not be open: a replay is a network optimisation, not a disk one.</para></summary>
    public static ListRow[]? SnapshotList(Scope scope, EdgeRelation relation, int parent)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (relation is EdgeRelation.PlaylistTracks or EdgeRelation.ShowEpisodes)
            return SnapshotPlaylist(scope, parent, baseline: true, null, false, out _, out _, relation == EdgeRelation.ShowEpisodes);
        if (relation == EdgeRelation.Rootlist)
            return SnapshotRootlist(scope, parent, baseline: true, null, false, out _, out _);
        return null;
    }

    /// <summary>A playlist's membership as text. The adder is resolved to its user URI and the item id to its hex HERE,
    /// where resolving is legal (C1); a text-form member's uri too (the string exists). For the WRITE a GID member
    /// carries its packed id in <paramref name="ids"/> and an empty uri — the store thread formats its key
    /// (<see cref="KeyOf"/>), as every other write does; for a <paramref name="baseline"/> the uri is formatted here. A
    /// member with no identity is a hole, and a list with a hole is neither written nor replayed over. The gate is
    /// <see cref="ListWrite.MayPersist"/> for a write and <see cref="ListWrite.IsSettled"/> for a baseline.</summary>
    static ListRow[]? SnapshotPlaylist(Scope scope, int parent, bool baseline, string? revision, bool wholeAnswer,
                                       out EntityId[] ids, out string key, bool show = false)
    {
        ids = [];
        key = "";
        Table playlists = show ? scope.Shows : scope.Playlists;
        if (parent <= Table.None || parent >= playlists.Count) return null;
        EdgeTable<PlaylistTrackEdge> edges = show ? scope.Edges.ShowEpisodes : scope.Edges.PlaylistTracks;
        ReadOnlySpan<int> targets = edges.Targets(parent);
        ReadOnlySpan<PlaylistTrackEdge> payload = edges.Payload(parent);
        bool pending = ListWrite.AnyPending(edges.Pending(parent));
        if (baseline ? !ListWrite.IsSettled(edges.State(parent), targets.Length, edges.Total(parent), pending)
                     : !ListWrite.MayPersist(edges.State(parent), targets.Length, edges.Total(parent), wholeAnswer, pending, revision))
            return null;
        key = KeyText(playlists.Id[parent]);
        if (key.Length == 0) return null;

        Table tracks = show ? scope.Episodes : scope.Tracks;
        UserTable users = scope.Users;
        int n = targets.Length;
        ListRow[] rows = n == 0 ? Array.Empty<ListRow>() : new ListRow[n];
        EntityId[] packed = n == 0 || baseline ? Array.Empty<EntityId>() : new EntityId[n];
        for (int i = 0; i < n; i++)
        {
            int target = targets[i];
            if (target <= Table.None || target >= tracks.Count) return null;
            EntityId id = tracks.Id[target];
            if (id.Form == EntityForm.None) return null;
            string uri = id.Form == EntityForm.Text ? Entities.Strings.Resolve(id.TextId) : baseline ? KeyOf(in id, null) : "";
            if (baseline && uri.Length == 0) return null;
            if (!baseline) packed[i] = id;
            PlaylistTrackEdge edge = payload[i];
            int by = edge.AddedBy;
            string? addedBy = by > Table.None && by < users.Count ? NullIfEmpty(KeyText(users.Id[by])) : null;
            rows[i] = new ListRow(uri, edge.ItemId.IsEmpty ? null : Entities.Strings.Resolve(edge.ItemId), edge.AddedAt,
                                  addedBy, edge.ChartStatus, edge.ChartPos, edge.ChartPrev, edge.Flags, RootlistKind.Item, 0, 0,
                                  null, null);
        }
        ids = packed;
        return rows;
    }

    /// <summary>The account's rootlist marker stream as text: an item's playlist uri (or, for the write, its packed id),
    /// every marker's kind, depth, wire position, folder id and name. A marker has no uri — its identity is the folder id
    /// (and, for a start marker, its name). Same two gates as <see cref="SnapshotPlaylist"/>.</summary>
    static ListRow[]? SnapshotRootlist(Scope scope, int parent, bool baseline, string? revision, bool wholeAnswer,
                                       out EntityId[] ids, out string key, bool show = false)
    {
        ids = [];
        key = "";
        UserTable users = scope.Users;
        if (parent <= Table.None || parent >= users.Count) return null;
        EdgeTable<RootlistEdge> edges = scope.Edges.Rootlist;
        ReadOnlySpan<int> targets = edges.Targets(parent);
        ReadOnlySpan<RootlistEdge> payload = edges.Payload(parent);
        bool pending = ListWrite.AnyPending(edges.Pending(parent));
        if (baseline ? !ListWrite.IsSettled(edges.State(parent), targets.Length, edges.Total(parent), pending)
                     : !ListWrite.MayPersist(edges.State(parent), targets.Length, edges.Total(parent), wholeAnswer, pending, revision))
            return null;
        string user = KeyText(users.Id[parent]);
        if (user.Length == 0) return null;
        key = ListWrite.RootlistKey(user);

        Table playlists = show ? scope.Shows : scope.Playlists;
        int n = targets.Length;
        ListRow[] rows = n == 0 ? Array.Empty<ListRow>() : new ListRow[n];
        EntityId[] packed = n == 0 || baseline ? Array.Empty<EntityId>() : new EntityId[n];
        for (int i = 0; i < n; i++)
        {
            RootlistEdge edge = payload[i];
            var kind = (RootlistKind)edge.Kind;
            string uri = "";
            if (kind == RootlistKind.Item)
            {
                int target = targets[i];
                if (target <= Table.None || target >= playlists.Count) return null;
                EntityId id = playlists.Id[target];
                if (id.Form == EntityForm.None) return null;
                if (id.Form == EntityForm.Text) uri = Entities.Strings.Resolve(id.TextId);
                else if (baseline) uri = KeyOf(in id, null);
                if (baseline && uri.Length == 0) return null;
                if (!baseline) packed[i] = id;
            }
            rows[i] = new ListRow(uri, null, edge.AddedAt, null, 0, 0, 0, 0, kind, edge.Depth, edge.Position,
                                  edge.FolderId.IsEmpty ? null : Entities.Strings.Resolve(edge.FolderId),
                                  edge.FolderName.IsEmpty ? null : Entities.Strings.Resolve(edge.FolderName));
        }
        ids = packed;
        return rows;
    }

    /// <summary>One list's snapshot: taken on the UI thread, written on the store thread by <see cref="Save"/>. A class —
    /// one allocation per list saved — rather than a closure over seven locals, because the job IS this object's method.</summary>
    sealed class ListSnapshot(string key, string revision, ListRow[] rows, EntityId[] ids, string? playlistUri, int now,
                              string stamp)
    {
        public readonly string Key = key;
        public readonly string Revision = revision;
        public readonly ListRow[] Rows = rows;
        /// <summary>Parallel to <see cref="Rows"/>: a GID member's packed identity, whose uri the store thread formats;
        /// default for a member whose uri is already text and for a folder marker.</summary>
        public readonly EntityId[] Ids = ids;
        /// <summary>The playlist whose row count this list rewrites (its own key); null for the rootlist.</summary>
        public readonly string? PlaylistUri = playlistUri;
        /// <summary>App seconds when it was taken (P7): the head's <c>written_at</c> and the row's LRU touch.</summary>
        public readonly int Now = now;
        public readonly string Stamp = stamp;

        public void Save() => SaveListCore(this);
    }

    /// <summary>STORE THREAD. ONE transaction: the list's rows are deleted and written again in order, its head is
    /// upserted (revision, total, when, by whom) and — for a playlist whose kind shape is registered — the row's count
    /// and count-known bit are rewritten, so the header a cold start reads agrees with the list it restores. A failure
    /// anywhere rolls ALL of it back: the previous head and the previous rows stand. (v1 rewrites the rows; a positional
    /// <c>SaveListDelta(ops)</c> is the plan's recorded follow-up once the replayer exists.)</summary>
    static void SaveListCore(ListSnapshot list)
    {
        Db? db = s_db;
        if (db is null) return;
        try
        {
            lock (db.WriteLock)
            {
                using SqliteTransaction tx = db.Write.BeginTransaction();
                using (var delete = db.Write.CreateCommand())
                {
                    delete.Transaction = tx;
                    delete.CommandText = "DELETE FROM list_item WHERE scope_id=$s AND list=$l;";
                    delete.Parameters.Add(new SqliteParameter("$s", s_scopeId));
                    delete.Parameters.Add(new SqliteParameter("$l", list.Key));
                    delete.ExecuteNonQuery();
                }
                if (list.Rows.Length > 0) InsertItems(db, tx, list);
                using (var head = db.Write.CreateCommand())
                {
                    head.Transaction = tx;
                    head.CommandText = "INSERT OR REPLACE INTO list_head(scope_id,list,revision,total,written_at,written_by) VALUES($s,$l,$r,$n,$w,$b);";
                    head.Parameters.Add(new SqliteParameter("$s", s_scopeId));
                    head.Parameters.Add(new SqliteParameter("$l", list.Key));
                    head.Parameters.Add(new SqliteParameter("$r", list.Revision));
                    head.Parameters.Add(new SqliteParameter("$n", (long)list.Rows.Length));
                    head.Parameters.Add(new SqliteParameter("$w", ToUnix(list.Now)));
                    head.Parameters.Add(new SqliteParameter("$b", list.Stamp));
                    head.ExecuteNonQuery();
                }
                if (list.PlaylistUri is { } uri && s_shapes[(byte)EntityKind.Playlist] is { Shape: PlaylistShape shape })
                {
                    using var count = db.Write.CreateCommand();
                    count.Transaction = tx;
                    count.CommandText = $"UPDATE {shape.Table} SET {PlaylistShape.CountColumn}=$n, known=known|$k, touched=max(touched,$t) WHERE scope_id=$s AND uri=$u;";
                    count.Parameters.Add(new SqliteParameter("$n", (long)list.Rows.Length));
                    count.Parameters.Add(new SqliteParameter("$k", (long)(uint)PlaylistFields.TrackCount));
                    count.Parameters.Add(new SqliteParameter("$t", ToUnix(list.Now)));
                    count.Parameters.Add(new SqliteParameter("$s", s_scopeId));
                    count.Parameters.Add(new SqliteParameter("$u", uri));
                    count.ExecuteNonQuery();
                }
                tx.Commit();
            }
            s_writes++;
            s_writeRows += list.Rows.Length;
        }
        catch (Exception ex) { s_faults++; Fault("list.write", ex); }
    }

    /// <summary>STORE THREAD, inside <see cref="SaveListCore"/>'s transaction: one prepared insert, rebound per row.
    /// <c>position</c> is the row's index, so the primary key is dense and ordered whatever the rows say.</summary>
    static void InsertItems(Db db, SqliteTransaction tx, ListSnapshot list)
    {
        using var insert = db.Write.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO list_item(scope_id,list,position,uri,item_id,added_at,added_by,chart_status,chart_pos,chart_prev,flags,kind,depth,wire_pos,folder_id,folder_name) " +
                             "VALUES($s,$l,$p,$u,$i,$a,$b,$cs,$cp,$cv,$f,$k,$d,$w,$fi,$fn);";
        insert.Parameters.Add(new SqliteParameter("$s", s_scopeId));
        insert.Parameters.Add(new SqliteParameter("$l", list.Key));
        SqliteParameter position = Param(insert, "$p"), uri = Param(insert, "$u"), itemId = Param(insert, "$i"),
                        addedAt = Param(insert, "$a"), addedBy = Param(insert, "$b"), chartStatus = Param(insert, "$cs"),
                        chartPos = Param(insert, "$cp"), chartPrev = Param(insert, "$cv"), flags = Param(insert, "$f"),
                        kind = Param(insert, "$k"), depth = Param(insert, "$d"), wirePos = Param(insert, "$w"),
                        folderId = Param(insert, "$fi"), folderName = Param(insert, "$fn");
        ListRow[] rows = list.Rows;
        for (int i = 0; i < rows.Length; i++)
        {
            ref readonly ListRow row = ref rows[i];
            position.Value = (long)i;
            uri.Value = row.Uri.Length > 0 ? row.Uri : KeyOf(list.Ids[i], null);
            itemId.Value = DbText(row.ItemId);
            addedAt.Value = (long)row.AddedAt;
            addedBy.Value = DbText(row.AddedBy);
            chartStatus.Value = (long)row.ChartStatus;
            chartPos.Value = (long)row.ChartPos;
            chartPrev.Value = (long)row.ChartPrev;
            flags.Value = (long)row.Flags;
            kind.Value = (long)(byte)row.Kind;
            depth.Value = (long)row.Depth;
            wirePos.Value = (long)row.WirePos;
            folderId.Value = DbText(row.FolderId);
            folderName.Value = DbText(row.FolderName);
            insert.ExecuteNonQuery();
        }
    }

    static SqliteParameter Param(SqliteCommand command, string name)
    {
        var parameter = new SqliteParameter(name, DBNull.Value);
        command.Parameters.Add(parameter);
        return parameter;
    }

    static object DbText(string? text) => text is null ? DBNull.Value : text;

    // ── the read ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Read one parent's persisted list back and land it — the disk leg the edge door never had
    /// (<c>Fetch.PlanEdge</c>) and the rootlist's warm (<see cref="Warm"/>). <paramref name="relation"/> is
    /// <see cref="EdgeRelation.PlaylistTracks"/> (parent = the playlist's identity) or
    /// <see cref="EdgeRelation.Rootlist"/> (parent = the account's).
    /// <para>The store thread reads the head and the rows in order and stages them as the wire decoder stages a full
    /// read; the UI thread then commits that staging — the rows <see cref="EdgeState.Complete"/> with their total, the
    /// playlist's revision and count, or the rootlist's markers and revision — IF the list is still Unknown (a live
    /// answer is never overwritten by yesterday's copy), and then runs <paramref name="then"/>.</para>
    /// <para>Returns false when there is no store, the relation is not a persisted list, or the queue refused (C8): the
    /// caller then goes straight to the network, as a refused <see cref="Read"/> does. True means
    /// <paramref name="then"/> WILL run on the UI thread — true when a list landed, false for a miss (nothing stored, a
    /// head this build does not trust, a list a live answer already filled) — unless the scope was replaced meanwhile
    /// (C7), in which case nobody continues anything.</para></summary>
    public static bool ReadList(Scope scope, EdgeRelation relation, EntityId parent, Action<bool>? then = null)
    {
        if (!s_open || parent.IsEmpty) return false;
        if (relation is not (EdgeRelation.PlaylistTracks or EdgeRelation.Rootlist or EdgeRelation.ShowEpisodes)) return false;
        string parentText = KeyText(parent);                 // UI thread: a text-form id resolves here, and only here
        return parentText.Length > 0 && ReadListJob(scope, relation, parentText, parent, then);
    }

    // The closure lives in its own method for the reason `Store.Read`'s summary gives: Roslyn builds the display class at
    // the top of the method that declares it, so a guard in the same method would not keep a refusal free.
    static bool ReadListJob(Scope scope, EdgeRelation relation, string parentText, EntityId parent, Action<bool>? then)
    {
        uint epoch = scope.Epoch;
        return Enqueue(() => ReadListCore(scope, relation, parentText, parent, epoch, then));
    }

    /// <summary>STORE THREAD. Stage the list, then post the landing. A miss with nobody to tell (the warm) posts nothing.</summary>
    static void ReadListCore(Scope scope, EdgeRelation relation, string parentText, EntityId parent, uint epoch,
                             Action<bool>? then)
    {
        Db? db = s_db;
        Staging? found = null;
        if (db is not null)
        {
            Staging staging = Staging.Rent();
            try
            {
                if (StageDiskList(db, relation, parentText, staging, epoch, out int rows))
                {
                    found = staging;
                    s_readRows += rows;
                }
            }
            catch (Exception ex) { s_faults++; Fault("list.read", ex); }
            if (found is null) Staging.Return(staging);
        }
        s_reads++;
        if (found is null && then is null) return;
        Post(() => LandList(scope, relation, parent, epoch, found, then));
    }

    /// <summary>UI THREAD. C7 first; then the list lands through <c>Entities.Commit</c> only while it is still Unknown;
    /// then the continuation — in a <c>finally</c>, like <see cref="Read"/>'s, so a landing that throws still hands the
    /// parent to the network rather than leaving it asked forever.</summary>
    static void LandList(Scope scope, EdgeRelation relation, EntityId parent, uint epoch, Staging? staging, Action<bool>? then)
    {
        bool live = false, landed = false;
        try
        {
            if (scope.Epoch != epoch || !ReferenceEquals(scope, Entities.Current)) return;   // C7
            live = true;
            if (staging is not null && StillUnknown(scope, relation, parent))
            {
                Entities.Commit(staging);
                landed = true;
            }
        }
        finally
        {
            if (staging is not null) Staging.Return(staging);
            if (live) then?.Invoke(landed);
        }
    }

    static bool StillUnknown(Scope scope, EdgeRelation relation, EntityId parent)
    {
        if (relation == EdgeRelation.Rootlist)
            return scope.Users.TryGetSlot(parent, out int me) && scope.Edges.Rootlist.State(me) == EdgeState.Unknown;
        if (relation == EdgeRelation.ShowEpisodes)
            return scope.Shows.TryGetSlot(parent, out int show) && scope.Edges.ShowEpisodes.State(show) == EdgeState.Unknown;
        return scope.Playlists.TryGetSlot(parent, out int slot) && scope.Edges.PlaylistTracks.State(slot) == EdgeState.Unknown;
    }

    /// <summary>STORE THREAD. The head, then the rows in order, staged into <paramref name="s"/> through the one list
    /// staging (<see cref="StageList(Staging,EdgeRelation,string,ReadOnlySpan{ListRow},string)"/>'s per-row halves) —
    /// exactly as the wire decoder stages a full read
    /// (<c>Spotify.Decode.PlaylistRevision</c> + <c>PlaylistFormatAttributes</c>, or <c>Spotify.Decode.Rootlist</c>),
    /// nothing interned. The header is staged at <see cref="Authority.Thin"/>: yesterday's count fills a hole and never
    /// overrules one a live answer settled. False — a miss — for no head, a head this build does not trust
    /// (<see cref="ListWrite.Trusts"/>, a malformed revision), a member with no uri, or a row count that disagrees with the
    /// head: the transaction makes the last impossible, and a list that is not exactly what was written is not a list to
    /// paint.</summary>
    static bool StageDiskList(Db db, EdgeRelation relation, string parentText, Staging s, uint epoch, out int rows)
    {
        rows = 0;
        string key = relation == EdgeRelation.Rootlist ? ListWrite.RootlistKey(parentText) : parentText;
        string revision;
        int total;
        using (var head = db.Read.CreateCommand())
        {
            head.CommandText = "SELECT revision,total,written_by FROM list_head WHERE scope_id=$s AND list=$l;";
            head.Parameters.Add(new SqliteParameter("$s", s_scopeId));
            head.Parameters.Add(new SqliteParameter("$l", key));
            using SqliteDataReader r = head.ExecuteReader();
            if (!r.Read()) return false;
            revision = ColumnText(r, 0) ?? "";
            total = r.IsDBNull(1) ? -1 : (int)r.GetInt64(1);
            if (total < 0 || !ListWrite.IsWellFormedRevision(revision) || !ListWrite.Trusts(ColumnText(r, 2))) return false;
        }

        s.Epoch = epoch;
        s.Authority = Authority.Thin;
        ListMark mark = MarkOf(s);
        StagedId parent = Stage(s, parentText);
        using var items = db.Read.CreateCommand();
        items.CommandText = "SELECT uri,item_id,added_at,added_by,chart_status,chart_pos,chart_prev,flags,kind,depth,wire_pos,folder_id,folder_name " +
                            "FROM list_item WHERE scope_id=$s AND list=$l ORDER BY position;";
        items.Parameters.Add(new SqliteParameter("$s", s_scopeId));
        items.Parameters.Add(new SqliteParameter("$l", key));
        using SqliteDataReader read = items.ExecuteReader();
        while (read.Read())
        {
            ListRow row = new(ColumnText(read, 0) ?? "", ColumnText(read, 1), (int)ColumnInt(read, 2), ColumnText(read, 3),
                              (byte)ColumnInt(read, 4), (ushort)ColumnInt(read, 5), (ushort)ColumnInt(read, 6),
                              (byte)ColumnInt(read, 7), (RootlistKind)(byte)ColumnInt(read, 8), (byte)ColumnInt(read, 9),
                              (ushort)ColumnInt(read, 10), ColumnText(read, 11), ColumnText(read, 12));
            if (!StageRow(s, relation, in row)) return false;
            rows++;
        }
        if (rows != total) return false;
        CloseList(s, relation, in parent, in mark, rows, revision, Authority.Thin);
        return true;
    }

    static string? ColumnText(SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    static long ColumnInt(SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? 0L : r.GetInt64(ordinal);

    // ── the list staging, shared (the disk read above, and the /diff replay — wave D3) ──────────────────────────────

    /// <summary>THE LIST STAGING, for a LIVE list. <paramref name="rows"/> — a whole list, in order, true at
    /// <paramref name="revision"/> — into <paramref name="s"/> exactly as the wire decoders stage a full read: for
    /// <see cref="EdgeRelation.PlaylistTracks"/> ONE whole-list <see cref="Relation.PlaylistTracks"/> run (Complete, its
    /// total = its length) — a catalog uri as its packed gid, anything else as text, the item id as hex, the adder as a
    /// user uri — plus a header row carrying <paramref name="revision"/> and the count
    /// (<see cref="PlaylistFields.TrackCount"/> at <see cref="Authority.Full"/>: a live answer, so it MOVES a count an
    /// earlier full read settled, where the disk read's Thin one only fills a hole); for <see cref="EdgeRelation.Rootlist"/>
    /// ONE <see cref="StagedRootlist"/> marker stream (kind, depth, wire position, folder id and name exactly as the rows
    /// carry them — the caller recomputes a replayed stream's) with its revision. <paramref name="parentUri"/> is the
    /// playlist's uri, or the account's user uri.
    /// <para>ANY THREAD: it writes <paramref name="s"/> and nothing else — no interner, no live table, no store state —
    /// which is why a <c>/diff</c> replay on the provider's thread (<c>Spotify.Decode.PlaylistReplay</c>/
    /// <c>RootlistReplay</c>) and a dealer push applied in place both land through it and commit exactly like a disk
    /// read; and, their staging carrying the revision beside a whole run, <see cref="WriteBehind"/> persists them like a
    /// full read (<see cref="PrepareListsTouchedBy"/> → <see cref="ListWrite.MayPersist"/>).</para>
    /// <para>False, with the staging left exactly as it was (other parents' answers may share it), for a relation that is
    /// not a list, an empty parent, a revision that is not well-formed (<see cref="ListWrite.IsWellFormedRevision"/> —
    /// the one revision gate, on every writer), or a member with no identity.</para></summary>
    public static bool StageList(Staging s, EdgeRelation relation, string parentUri, ReadOnlySpan<ListRow> rows,
                                 string revision)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (relation is not (EdgeRelation.PlaylistTracks or EdgeRelation.Rootlist or EdgeRelation.ShowEpisodes) || string.IsNullOrEmpty(parentUri)
            || !ListWrite.IsWellFormedRevision(revision)) return false;
        ListMark mark = MarkOf(s);
        StagedId parent = Stage(s, parentUri);
        for (int i = 0; i < rows.Length; i++)
        {
            if (StageRow(s, relation, in rows[i])) continue;
            Rewind(s, in mark);
            return false;
        }
        CloseList(s, relation, in parent, in mark, rows.Length, revision, Authority.Full);
        return true;
    }

    /// <summary>Where a list's staging began: the edge array, the marker rows and the text arena, so a list that fails
    /// halfway leaves nothing behind in a staging other lists share.</summary>
    readonly record struct ListMark(int Edges, int Markers, int Text);

    static ListMark MarkOf(Staging s) => new(s.Edges.Count, s.RootlistRows.Count, s.TextMark);

    static void Rewind(Staging s, in ListMark mark)
    {
        s.Edges.Rewind(mark.Edges);
        s.RootlistRows.Rewind(mark.Markers);
        s.RewindText(mark.Text);
    }

    /// <summary>One row of either relation. A playlist member fills the union a <see cref="Relation.PlaylistTracks"/>
    /// run reads (Edges.Staging.cs): Target, Text = the item id, At, Aux = the adder, B1 = chart status, U0/U1 = chart
    /// position/previous, B0 = the wire flags. A rootlist row is a <see cref="StagedRootlistRow"/>; only an item names a
    /// target (a marker's identity is its folder id). False for a member or an item with no uri.</summary>
    static bool StageRow(Staging s, EdgeRelation relation, in ListRow row)
    {
        if (relation == EdgeRelation.Rootlist)
        {
            ref StagedRootlistRow marker = ref s.RootlistRows.Add();
            marker.Kind = row.Kind;
            marker.Depth = row.Depth;
            marker.Position = row.WirePos;
            marker.AddedAt = row.AddedAt;
            marker.FolderId = Arena(s, row.FolderId);
            marker.Name = Arena(s, row.FolderName);
            if (row.Kind != RootlistKind.Item) return true;
            marker.Target = Stage(s, row.Uri);
            return !marker.Target.IsEmpty;
        }
        ref StagedEdge edge = ref s.Edges.Add();
        edge.Target = Stage(s, row.Uri);
        if (edge.Target.IsEmpty) return false;
        edge.Text = Arena(s, row.ItemId);
        edge.At = row.AddedAt;
        edge.Aux = Stage(s, row.AddedBy);
        edge.B1 = row.ChartStatus;
        edge.U0 = row.ChartPos;
        edge.U1 = row.ChartPrev;
        edge.B0 = row.Flags;
        return true;
    }

    /// <summary>Close the list staged since <paramref name="mark"/>: the whole-list run and its header (the revision the
    /// rows are true at, and the count — which the list's total IS; no Identity: a list knows none), or the rootlist's
    /// stream and its revision.</summary>
    static void CloseList(Staging s, EdgeRelation relation, in StagedId parent, in ListMark mark, int count, string revision,
                          Authority authority)
    {
        if (relation == EdgeRelation.Rootlist)
        {
            ref StagedRootlist list = ref s.Rootlists.Add();
            list.Parent = parent;
            list.Revision = Arena(s, revision);
            list.Start = mark.Markers;
            list.Length = count;
            return;
        }
        if (relation == EdgeRelation.ShowEpisodes)
        {
            s.Edges.Run(Relation.ShowEpisodes, in parent, mark.Edges, count, EdgeState.Complete, count);
            ref var show = ref s.Shows.RowFor(parent, authority, 0);
            show.ListRevision = Arena(s, revision);
            show.EpisodesAsked = count;
            return;
        }
        s.Edges.Run(Relation.PlaylistTracks, in parent, mark.Edges, count, EdgeState.Complete, count);
        ref StagedPlaylist header = ref s.Playlists.RowFor(parent, authority, (uint)PlaylistFields.TrackCount);
        header.TrackCount = count;
        header.Revision = Arena(s, revision);
    }

    /// <summary>A uri → its staged identity, the wire decoder's own rule (<c>Spotify.Decode.Identity</c>): a catalog uri
    /// parses to the packed gid with no text anywhere (<see cref="EntityId.TryParseGid(ReadOnlySpan{char},out EntityId)"/>
    /// is pure and thread-safe), anything else keeps its bytes in the arena for the commit to resolve.</summary>
    static StagedId Stage(Staging s, string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return default;
        return EntityId.TryParseGid(uri.AsSpan(), out EntityId id) ? new StagedId(id) : new StagedId(Arena(s, uri));
    }

    /// <summary>ANY THREAD (the store thread's read, the provider's replay): UTF-16 into the staging arena as UTF-8, never
    /// through the interner — the copy <see cref="RowReader"/> makes for a column. Short text goes through the stack, long
    /// text through the pool.</summary>
    static TextRef Arena(Staging s, string? text)
    {
        if (string.IsNullOrEmpty(text)) return default;
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (max <= 512)
        {
            Span<byte> buffer = stackalloc byte[512];
            return s.AddText(buffer[..Encoding.UTF8.GetBytes(text, buffer)]);
        }
        byte[] scratch = ArrayPool<byte>.Shared.Rent(max);
        try { return s.AddText(scratch.AsSpan(0, Encoding.UTF8.GetBytes(text, scratch))); }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }
}
