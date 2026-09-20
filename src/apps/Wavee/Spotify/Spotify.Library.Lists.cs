// ── Spotify/Spotify.Library.Lists.cs ─────────────────────────────────────────────────────────────────────────────────
// the per-list half of the library host: the freshness stamps, a dealer push for ONE playlist or the rootlist, and the
// reconnect's list revalidation (wave D3)
//
// Role: CORE (ListStamps' rules, ListPushReplay, OptimisticHead) + SHELL (the host section, a named partial of
//       Spotify.Library)
// Owner: L3
// Wave: D3 (plan §3.3, last paragraph)
// Budget: 560 lines
// Spec: docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md §2, §3.3, §3.7–§3.10, §5 gate D3
//
// CORRECTNESS IS THE REVISION + `/diff` ON OPEN AND ON RECONNECT; THE DEALER IS AN OPTIMISATION (plan §2). Nothing here
// is the source of truth for a list. A push only ever does one of four things (`ListPush.Decide`, CORE, tested):
//
//     hm://playlist/v2/playlist/{id} ─ OnDealerPush (dealer thread) ─ DecodePush ─ post ─┐
//                                                                                        ▼  UI thread
//     held revision · list Complete? · EdgePending rows? · page on screen? ─ ListPush.Decide
//        ├─ Drop           the head we already hold: an echo of our own write, or the second arrival
//        ├─ ApplyInPlace   held == parent, ops, settled rows: Store.SnapshotList ─ ListPushReplay ─ Store.StageList
//        │                 ─ Commit ─ WriteBehind (rows + head + count in one transaction) ─ stamp revalidated. ZERO HTTP.
//        │                 Any misfit — a staging that refuses included — ⇒ the stale arm below, nothing changed. The
//        │                 echo of OUR OWN row edit never gets here: the edit forgot the held head
//        │                 (`Entities.ForgetListRevision`), so held ≠ parent and the echo only dirties the list.
//        ├─ MarkDirty      ListStamps.MarkDirty; NOTHING is asked (anti-herd) and the head is NEVER stored — a head-only
//        │                 push (every editorial refresh, every bulk ADD's echo) says the list moved, not what it became
//        └─ RevalidateNow  the page is on screen: dirty + one RefreshEdge (the held revision makes it the `/diff`)
//
//     hm://playlist/…/rootlist ─ the 250 ms settle ─ ListPush.DecideRootlist ─ dirty + ONE rootlist re-ask, deduped by
//                                head (the v2 and legacy topics arrive as a pair). Its live shape is unobserved (§3.10),
//                                so a rootlist push is NEVER applied in place.
//
//     reconnect (SyncNow, behind LibrarySyncRules' 30 s guard) ─ the list on screen + every DIRTY resident list are
//                                re-asked at Prefetch; every other stamp is forgotten, so its next open revalidates.
//
// Every push writes one always-on `list.push list=<playlist|rootlist> verdict=<drop|dirty|applied|revalidate> ops=<n>
// reason=<…>` line — no uris, no names. The reason is how a field report names why a push did not apply.
//
// THE STAMPS ARE MEMORY-ONLY (`ListStamps`): the first open of a list in a session always revalidates, because its
// baseline came off disk and may be days old. A scope switch or a sign-out forgets them all (`AfterPublish`).
//
// Rules: C1 (a dealer frame arrives on the socket thread and is POSTED before it reads a table or a stamp), C7 (a
// replaced scope's work is dropped — the staging carries the scope epoch), no head stored without its rows (§2), nothing
// written from an optimistic list (`EdgePending`; an optimistic row edit forgets the held head), the decisions pure and
// tested (ListPushTests, LibraryPushRulesTests, ListStampsTests, OptimisticRevisionTests).

using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

// ── 1. CORE: the freshness stamps ────────────────────────────────────────────────────────────────────────────────────

/// <summary>THE LIST FRESHNESS STAMPS (plan §2, §3.3): per list, "a push said it moved and it was not applied"
/// (<see cref="IsDirty"/>) and "when it was last revalidated IN THIS SESSION" (<see cref="RevalidatedAtMs"/>) — the two
/// inputs <see cref="ListFreshness.Decide"/> takes beside the baseline, on ONE clock (<see cref="NowMs"/>).
/// <para>MEMORY-ONLY by design: nothing here survives a restart, so the first open of a list in a session always
/// revalidates. KEYED by the list's uri in its canonical spelling (<c>EntityId.Text</c>:
/// <c>spotify:playlist:&lt;base62&gt;</c>), or <see cref="ListWrite.RootlistKey"/> (<c>rootlist:&lt;account uri&gt;</c>)
/// for the account's rootlist — the same keys <c>list_head</c> uses.</para>
/// <para>Writers: the dealer (<c>Spotify.Library</c>: <see cref="MarkDirty"/> for a push it did not apply,
/// <see cref="MarkRevalidated"/> for one it did), WHOEVER LANDS A REVALIDATION (<see cref="MarkRevalidated"/> — the
/// <c>/diff</c> answer, unchanged or replayed or read in full), the reconnect (<see cref="ForgetExcept"/>) and a scope
/// switch or sign-out (<see cref="ForgetAll"/>). Readers: the playlist page, the library pane and the queue's context,
/// through <see cref="ListFreshness.Decide"/>.</para>
/// <para>UI THREAD ONLY (C1). Not a table: a handful of entries (the lists the dealer and the pages named this
/// session), no persistence, no scope — a scope switch empties it.</para></summary>
public static class ListStamps
{
    /// <summary>One list's stamp. <see cref="RevalidatedAtMs"/> 0 = never this session (a real stamp is never 0).</summary>
    readonly record struct Stamp(bool Dirty, int RevalidatedAtMs);

    static readonly Dictionary<string, Stamp> s_stamps = new(StringComparer.Ordinal);
    static readonly long s_origin = Stopwatch.GetTimestamp();
    static uint s_version;

    /// <summary>Bumps on every change to any stamp. A surface that re-decides its freshness when a push dirties the list
    /// it shows subscribes; nothing else needs to. Written only on the UI thread, never from inside a computation that
    /// reads it.</summary>
    public static Signal<uint> Changed { get; } = new(0u);

    /// <summary>How many lists hold a stamp (diagnostics, the reconnect's log line).</summary>
    public static int Count => s_stamps.Count;

    /// <summary>THE freshness clock: milliseconds since this process first asked, as an <c>int</c> that WRAPS (every
    /// ~49.7 days) — <see cref="ListFreshness.Decide"/> subtracts with <c>unchecked</c>, so an age is right for any gap
    /// under ~24.8 days and reads "past the window" beyond. Monotonic (<see cref="Stopwatch"/>, never the wall clock), and
    /// NEVER 0, so 0 stays "never revalidated".</summary>
    public static int NowMs() => ClockOf(Stopwatch.GetElapsedTime(s_origin).Ticks / TimeSpan.TicksPerMillisecond);

    /// <summary>An elapsed millisecond count as the clock reads it: truncated to 32 bits (the wrap), 0 moved to 1. PURE.</summary>
    public static int ClockOf(long elapsedMs)
    {
        int ms = unchecked((int)elapsedMs);
        return ms == 0 ? 1 : ms;
    }

    /// <summary>Did a push say this list moved without the move being applied here?</summary>
    public static bool IsDirty(string listUri)
        => !string.IsNullOrEmpty(listUri) && s_stamps.TryGetValue(listUri, out Stamp stamp) && stamp.Dirty;

    /// <summary>A push said this list moved and it was not applied: its next open (or its revalidation now, when it is on
    /// screen) asks <c>/diff</c>. Keeps the revalidation stamp — dirty is what overrides it.</summary>
    public static void MarkDirty(string listUri)
    {
        if (string.IsNullOrEmpty(listUri)) return;
        ref Stamp stamp = ref CollectionsMarshal.GetValueRefOrAddDefault(s_stamps, listUri, out _);
        if (stamp.Dirty) return;
        stamp = stamp with { Dirty = true };
        Bump();
    }

    /// <summary>When this list was last revalidated this session, on <see cref="NowMs"/>'s clock; 0 = never.</summary>
    public static int RevalidatedAtMs(string listUri)
        => !string.IsNullOrEmpty(listUri) && s_stamps.TryGetValue(listUri, out Stamp stamp) ? stamp.RevalidatedAtMs : 0;

    /// <summary>The list is known current as of <paramref name="nowMs"/> (<see cref="NowMs"/>): a revalidation landed, or
    /// a push was replayed over exactly the revision it was computed against. Clears dirty. A 0 is stored as 1 — 0 is
    /// "never".</summary>
    public static void MarkRevalidated(string listUri, int nowMs)
    {
        if (string.IsNullOrEmpty(listUri)) return;
        s_stamps[listUri] = new Stamp(false, nowMs == 0 ? 1 : nowMs);
        Bump();
    }

    /// <summary>Forget every stamp — a scope switch, a sign-out: each one described a list of a scope that is gone.</summary>
    public static void ForgetAll()
    {
        if (s_stamps.Count == 0) return;
        s_stamps.Clear();
        Bump();
    }

    /// <summary>Every DIRTY list's key, into <paramref name="into"/> (cleared first) — the reconnect's re-ask set.</summary>
    public static void CollectDirty(List<string> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        foreach (var (key, stamp) in s_stamps)
            if (stamp.Dirty) into.Add(key);
    }

    /// <summary>Forget every stamp whose key is not in <paramref name="keep"/> — the reconnect: what the socket missed
    /// while it was down is unknown, so every list not being re-asked right now revalidates on its next open. Returns how
    /// many were forgotten.</summary>
    public static int ForgetExcept(IReadOnlySet<string> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        int forgot = 0;
        foreach (string key in s_stamps.Keys)                          // Remove during enumeration is legal (.NET Core 3+)
        {
            if (keep.Contains(key)) continue;
            s_stamps.Remove(key);
            forgot++;
        }
        if (forgot > 0) Bump();
        return forgot;
    }

    static void Bump() => Changed.Value = ++s_version;
}

// ── 2. CORE: a push's ops over the held rows ─────────────────────────────────────────────────────────────────────────

/// <summary>A DEALER PUSH REPLAYED OVER A PLAYLIST'S SETTLED ROWS as the list tables hold them (<see cref="ListRow"/>,
/// <c>Store.SnapshotList</c>) — the in-place half of <see cref="ListPush.Verdict.ApplyInPlace"/>. The replay is
/// <see cref="PlaylistOps.TryApply{TRow,TAccess}"/>'s, unchanged (any misfit refuses and the rows are untouched); this
/// maps the push's wire rows onto the persisted row EXACTLY as a full read stages them (<see cref="RowOf"/>), and names
/// why a push was not applied (<see cref="WhyStale"/>) for the <c>list.push</c> line. PURE: no table, no clock, no
/// interner.</summary>
public static class ListPushReplay
{
    /// <summary>A wire item as a persisted playlist member — the full read's own reading
    /// (<c>Spotify.Decode.PlaylistRevision</c>): the uri as the wire spelled it; the item id as the lowercase hex both
    /// decoders produce; the timestamp through <c>Spotify.Decode.Instant</c> (the wire's milliseconds or seconds → UNIX
    /// seconds); the adder's bare USERNAME as <c>spotify:user:&lt;name&gt;</c> (<see cref="AdderUri"/>); the chart
    /// triple; no wire flags, depth 0, an item. An UPDATE_ITEM's values (an empty uri) map the same way.</summary>
    public static ListRow RowOf(in PlaylistOps.WireItem item)
        => new(item.Uri, item.ItemId is { Length: > 0 and <= 128 } id ? id : null, Spotify.Decode.Instant(item.Timestamp),
               AdderUri(item.AddedBy), item.ChartStatus, item.ChartPos, item.ChartPrev, 0, RootlistKind.Item, 0, 0, null, null);

    /// <summary>An <c>added_by</c> as the list tables hold it — <c>Spotify.Decode.UserUri</c>'s rule: a value that is
    /// already a <c>spotify:</c> uri is kept; an empty one, or one over 200 UTF-8 bytes, is none; anything else is a
    /// username and becomes <c>spotify:user:&lt;name&gt;</c>.</summary>
    public static string? AdderUri(string? addedBy)
    {
        if (addedBy is null) return null;
        if (addedBy.StartsWith("spotify:", StringComparison.Ordinal)) return addedBy;
        if (addedBy.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(addedBy) > 200) return null;
        return "spotify:user:" + addedBy;
    }

    /// <summary>Replay <paramref name="batch"/> over a copy of <paramref name="baseline"/>. True: <paramref name="rows"/>
    /// is the list at the push's new revision, <paramref name="attrs"/> the header change it carried and
    /// <paramref name="tally"/> what it added and removed. False: <paramref name="misfit"/> names the op and why, and
    /// <paramref name="rows"/> is the untouched copy.</summary>
    public static bool TryReplay(ReadOnlySpan<ListRow> baseline, PlaylistOps.Batch batch, out List<ListRow> rows,
        out PlaylistOps.ListAttributeChange attrs, out PlaylistOps.Tally tally, out PlaylistOps.Misfit misfit)
    {
        ArgumentNullException.ThrowIfNull(batch);
        rows = new List<ListRow>(baseline.Length + batch.Items.Length);
        rows.AddRange(baseline);
        var items = new ListRow[batch.Items.Length];
        for (int i = 0; i < items.Length; i++) items[i] = RowOf(in batch.Items[i]);
        return PlaylistOps.TryApply<ListRow, RowAccess>(rows, batch.Ops, items, batch.Lists, default,
                                                          out attrs, out tally, out misfit);
    }

    /// <summary>Does the push's own uri (<c>PlaylistModificationInfo</c> field 1) name the playlist its topic named? A
    /// push that carries none is the topic's (the topic is what routed it). Compared by the trailing id, so the
    /// user-namespaced spelling <c>spotify:user:&lt;u&gt;:playlist:&lt;id&gt;</c> is the same list.</summary>
    public static bool NamesList(string? pushUri, ReadOnlySpan<char> playlistId)
        => pushUri is null || EntityUri.IdOf(pushUri.AsSpan()).SequenceEqual(playlistId);

    /// <summary>The <c>reason=</c> of a decodable push <see cref="ListPush.Decide"/> did not let apply — the first rule it
    /// failed, in the rule's own order: ops this build does not replay (<c>refused:&lt;why&gt;</c>), a head-only push
    /// (no ops, or no parent — every editorial refresh, every bulk ADD's echo), a list not held whole, optimistic rows on
    /// it, or a held revision that is not the push's parent (a push was missed in between: <c>gap</c>).</summary>
    public static string WhyStale(in PlaylistOps.PushAnswer push, bool resident, bool pendingLocal)
    {
        if (!push.Ok) return "undecodable";
        if (push.Why != PlaylistOps.Refusal.None) return "refused:" + push.Why;
        if (push.Batch.Ops.Length == 0 || string.IsNullOrEmpty(push.ParentRevision)) return "head-only";
        if (!resident) return "not-resident";
        if (pendingLocal) return "pending";
        return "gap";
    }

    /// <summary>The <c>verdict=</c> word: <c>drop</c>, <c>dirty</c>, <c>applied</c> (written only for a replay that
    /// landed — one that did not is logged under the arm it fell back to) or <c>revalidate</c>.</summary>
    public static string VerdictName(ListPush.Verdict verdict) => verdict switch
    {
        ListPush.Verdict.Drop => "drop",
        ListPush.Verdict.MarkDirty => "dirty",
        ListPush.Verdict.ApplyInPlace => "applied",
        _ => "revalidate",
    };

    /// <summary>How the replayer reads and patches a persisted member. Identity is the replayer's one rule (item_id
    /// when both rows carry one, else uri). A playlist member holds <c>added_by</c> and the timestamp and nothing else
    /// an UPDATE_ITEM may name: <c>public</c> (a rootlist entry's bit) refuses, and the push falls back to a
    /// revalidation.</summary>
    readonly struct RowAccess : PlaylistOps.IRowAccess<ListRow>
    {
        public bool SameUri(in ListRow a, in ListRow b) => string.Equals(a.Uri, b.Uri, StringComparison.Ordinal);
        public bool HasItemId(in ListRow row) => !string.IsNullOrEmpty(row.ItemId);
        public bool SameItemId(in ListRow a, in ListRow b) => string.Equals(a.ItemId, b.ItemId, StringComparison.Ordinal);

        public bool Holds(in ListRow row, in ListRow values, PlaylistOps.ItemAttrs set, PlaylistOps.ItemAttrs unset)
        {
            if (((set | unset) & PlaylistOps.ItemAttrs.Public) != 0) return false;
            if ((set & PlaylistOps.ItemAttrs.AddedBy) != 0 && !string.Equals(row.AddedBy, values.AddedBy, StringComparison.Ordinal)) return false;
            if ((set & PlaylistOps.ItemAttrs.Timestamp) != 0 && row.AddedAt != values.AddedAt) return false;
            if ((unset & PlaylistOps.ItemAttrs.AddedBy) != 0 && row.AddedBy is not null) return false;
            if ((unset & PlaylistOps.ItemAttrs.Timestamp) != 0 && row.AddedAt != 0) return false;
            return true;
        }

        public bool TryPatch(ref ListRow row, in ListRow values, PlaylistOps.ItemAttrs set, PlaylistOps.ItemAttrs unset)
        {
            if (((set | unset) & PlaylistOps.ItemAttrs.Public) != 0) return false;
            string? addedBy = row.AddedBy;
            int addedAt = row.AddedAt;
            if ((set & PlaylistOps.ItemAttrs.AddedBy) != 0) addedBy = values.AddedBy;
            if ((set & PlaylistOps.ItemAttrs.Timestamp) != 0) addedAt = values.AddedAt;
            if ((unset & PlaylistOps.ItemAttrs.AddedBy) != 0) addedBy = null;
            if ((unset & PlaylistOps.ItemAttrs.Timestamp) != 0) addedAt = 0;
            row = row with { AddedBy = addedBy, AddedAt = addedAt };
            return true;
        }
    }
}

// ── 3. CORE: the held head under an optimistic rootlist write ────────────────────────────────────────────────────────

/// <summary>MAY A ROOTLIST WRITE'S REPLY HEAD BE HELD OVER THE TREE IT LANDED? (plan §2: "never persist a revision whose
/// ops/contents you did not apply".) A rootlist gesture lands its locally applied tree at once and FORGETS the held head
/// (<c>Spotify.Library.LandRootlistTree</c> → <c>Entities.ForgetListRevision</c>): the tree is no longer the list that
/// head describes. The <c>/changes</c> reply names the head the server moved to, and holding it is honest only when that
/// head is provably OUR tree — all three of:
/// <list type="bullet">
/// <item>the tree was computed over a held head (<see cref="MayAdopt"/>'s <c>baseRevision</c>) — a write that had to
/// read a fresh head first (none held, or another write's tree still unconfirmed) computed its ops over a tree that head
/// may not describe;</item>
/// <item>the reply is that base's DIRECT successor (<see cref="IsDirectSuccessor"/>): every <c>/changes</c> in the
/// captures moved the counter by exactly one (reorders.saz: 28→29 … 35→36 over eight writes; the a164 folder-create
/// golden: 71→72), so any other step means the server rebased our ops over a change this client never saw;</item>
/// <item><c>untouched</c>: nothing replaced the rows since the landing (the rootlist edge's version is the one it left)
/// and nothing re-armed a head.</item>
/// </list>
/// Otherwise the head stays forgotten and the next rootlist ask — this write's own dealer echo, a reconnect, the next
/// launch's disk pair — is a full read or a <c>/diff</c> from a pair that is true. Counters are compared ONLY here, between
/// our own base and our own reply; a push's counter is never trusted (<see cref="ListPush.Decide"/>). PURE.</summary>
public static class OptimisticHead
{
    /// <summary>Is <paramref name="head"/> the revision one write after <paramref name="baseRevision"/> — both
    /// well-formed (<see cref="ListWrite.IsWellFormedRevision"/>) and the counter exactly one higher?</summary>
    public static bool IsDirectSuccessor(string? baseRevision, string? head)
        => CounterOf(baseRevision, out ulong from) && CounterOf(head, out ulong to) && to == from + 1;

    /// <summary>THE RULE (the type's summary): held only over the tree it came from, one write on, untouched since.</summary>
    public static bool MayAdopt(string? baseRevision, string? head, bool untouched)
        => untouched && IsDirectSuccessor(baseRevision, head);

    /// <summary>A well-formed revision's counter (the digits before the comma, at most a <c>uint</c>).</summary>
    static bool CounterOf(string? revision, out ulong counter)
    {
        counter = 0;
        if (revision is null || !ListWrite.IsWellFormedRevision(revision)) return false;
        int comma = revision.IndexOf(',');
        for (int i = 0; i < comma; i++) counter = counter * 10 + (ulong)(revision[i] - '0');
        return true;
    }
}

// ── 4. SHELL: the host's list section ────────────────────────────────────────────────────────────────────────────────

public static partial class Spotify
{
    public static partial class Library
    {
        // ══ 10. list pushes, list stamps, the reconnect (wave D3) ═══════════════════════════════════════════════════

        const string PlaylistUriPrefix = "spotify:playlist:";

        /// <summary>The newest rootlist push the dealer delivered, for the settle's flush: its head (null when its body
        /// did not decode — the flush then re-asks without a head to dedupe by) and its op count (the log line's).</summary>
        sealed class RootlistPushed(string? newRevision, int ops)
        {
            public readonly string? NewRevision = newRevision;
            public readonly int Ops = ops;
        }

        static RootlistPushed? s_rootlistPush;

        /// <summary>The head the last rootlist re-ask was made FOR: a repeat of that push (the second topic of the pair
        /// arriving after the settle, a redelivery) before the answer lands is dropped by revision, never asked twice.
        /// Forgotten with the scope (<see cref="AfterPublish"/>).</summary>
        static string? s_rootlistAskedFor;

        /// <summary>The scope the last library sync ran in — a SECOND sync of the same scope is a reconnect
        /// (<see cref="SyncNow"/>).</summary>
        static Scope? s_syncedScope;

        static readonly List<string> s_dirtyLists = new();
        static readonly HashSet<string> s_keptLists = new(StringComparer.Ordinal);

        /// <summary>DEALER THREAD. A push body → its decoded answer; a body the decoder throws on is an undecodable push
        /// (it marks the list dirty), never a dead receive loop.</summary>
        static PlaylistOps.PushAnswer DecodePushBody(ReadOnlySpan<byte> payload)
        {
            try { return PlaylistOps.DecodePush(payload); }
            catch (Exception ex)
            {
                Log.Warn("library", "a list push body could not be decoded: " + ex.GetType().Name);
                return new PlaylistOps.PushAnswer(false, null, null, null, PlaylistOps.Batch.Empty, PlaylistOps.Refusal.Malformed);
            }
        }

        /// <summary>DEALER THREAD. Hand one decoded playlist push to the UI thread (C1). Its own method so the closure is
        /// built only for a playlist push — Roslyn builds a display class at the top of the method that declares it, and
        /// <see cref="OnDealerPush"/> runs for every library push.</summary>
        static readonly ListPushRefreshGuard s_pushReadGuard = new();

        static void PostPlaylistPush(string id, PlaylistOps.PushAnswer answer) => Post(() => PlaylistPushed(id, answer));

        /// <summary>DEALER THREAD. A rootlist push leaves its head for the settle's flush (<see cref="FlushRootlistPush"/>);
        /// the newest one wins — the pair's two bodies name the same head.</summary>
        static void NoteRootlistPush(ReadOnlySpan<byte> payload)
        {
            PlaylistOps.PushAnswer answer = DecodePushBody(payload);
            Volatile.Write(ref s_rootlistPush, answer.Ok ? new RootlistPushed(answer.NewRevision, answer.Batch.Ops.Length)
                                                         : new RootlistPushed(null, 0));
        }

        /// <summary>UI THREAD, posted by <see cref="OnDealerPush"/> in ARRIVAL ORDER (a burst of edits is a chain of pushes,
        /// each one's parent the previous one's head). Gathers what <see cref="ListPush.Decide"/> takes off the live
        /// tables and does what it says; see the file header. A push the decoder could not read, or whose own uri names
        /// another list, is stale — it can never apply.</summary>
        static void PlaylistPushed(string id, PlaylistOps.PushAnswer push)
        {
            var scope = Entities.Current;
            if (!IsAccountScope(scope)) return;                               // the dealer is the signed-in account's
            string uri = PlaylistUriPrefix + id;
            var edges = scope.Edges.PlaylistTracks;
            int slot = scope.Playlists.TryGetSlot(uri.AsSpan(), out int found) ? found : Table.None;
            bool held = slot > Table.None;
            StringId head = held ? scope.Playlists.Revision[slot] : default;
            string? stored = head.IsEmpty ? null : Entities.Strings.Resolve(head);
            bool resident = held && edges.State(slot) == EdgeState.Complete;
            bool pending = held && ListWrite.AnyPending(edges.Pending(slot));
            bool open = held && OpenPlaylist(scope) == slot;

            ListPush.Verdict verdict;
            string reason;
            string? newRevision = push.Ok ? push.NewRevision : null;
            if (newRevision is null || !ListPushReplay.NamesList(push.Uri, id))
            {
                verdict = open ? ListPush.Verdict.RevalidateNow : ListPush.Verdict.MarkDirty;
                reason = newRevision is null ? "undecodable" : "other-list";
            }
            else
            {
                verdict = ListPush.Decide(stored, push.ParentRevision, newRevision, push.HasReplayableOps, resident, pending, open);
                reason = verdict switch
                {
                    ListPush.Verdict.Drop => "echo",
                    ListPush.Verdict.ApplyInPlace => "parent",
                    _ => ListPushReplay.WhyStale(in push, resident, pending),
                };
            }

            if (verdict == ListPush.Verdict.ApplyInPlace && !ApplyInPlace(scope, slot, uri, in push, out reason))
                verdict = open ? ListPush.Verdict.RevalidateNow : ListPush.Verdict.MarkDirty;   // nothing changed

            switch (verdict)
            {
                case ListPush.Verdict.MarkDirty:
                    ListStamps.MarkDirty(uri);                                // the head is NEVER stored: rows it did not bring
                    break;
                case ListPush.Verdict.RevalidateNow:
                    if (!s_pushReadGuard.Accept(uri, newRevision, ListStamps.NowMs()))
                    { LogPush("playlist", ListPush.Verdict.Drop, push.Batch.Ops.Length, "repeat-refresh"); return; }
                    Api.NoteListPush(Api.ListKind.Playlist, id);
                    ListStamps.MarkDirty(uri);
                    // A list still Unknown has its own ask out (the page's, disk first): it reads the newest head anyway,
                    // and a refresh would skip that disk leg and ask twice.
                    if (edges.State(slot) != EdgeState.Unknown)
                        Entities.RefreshEdge(FetchEdge.PlaylistTracks, slot, FetchPriority.Visible);
                    break;
            }
            LogPush("playlist", verdict, push.Batch.Ops.Length, reason);
        }

        /// <summary>THE IN-PLACE APPLY: the live settled list (<c>Store.SnapshotList</c>), the push's ops over it
        /// (<see cref="ListPushReplay.TryReplay"/>), and the result landed by <see cref="LandPushReplay"/> — rows, revision and
        /// total in one transaction (Store.Lists.cs). Zero HTTP. The added rows are the list's only unknown rows, so the
        /// page that shows the list demands exactly them (the planner dedupes the rest); a list nobody shows asks nothing
        /// until it is opened. Refuses — having changed NOTHING — with the reason for the log: the snapshot would not
        /// come (a hole, no store-shaped identity), a misfit, a header change (a rename rides a <c>/diff</c>, whose replay
        /// lands the header in the same commit), a total that does not reconcile, a malformed head, a staging that refused
        /// (<c>stage</c>). Never reached by the echo of OUR OWN row edit: the edit forgot the held head
        /// (<c>Entities.ForgetListRevision</c>), so the push's parent is not what is held and the verdict is dirty.</summary>
        static bool ApplyInPlace(Scope scope, int slot, string uri, in PlaylistOps.PushAnswer push, out string reason)
        {
            ListRow[]? baseline = Store.SnapshotList(scope, EdgeRelation.PlaylistTracks, slot);
            if (baseline is null || !SpellUris(scope, slot, baseline)) { reason = "snapshot"; return false; }
            if (!ListPushReplay.TryReplay(baseline, push.Batch, out List<ListRow> rows, out var attrs, out var tally, out var misfit))
            {
                reason = "misfit:" + misfit.Why;
                return false;
            }
            if (!attrs.IsEmpty) { reason = "header"; return false; }
            if (!tally.Reconciles(scope.Edges.PlaylistTracks.Total(slot), rows.Count)) { reason = "total"; return false; }
            string? revision = push.NewRevision;
            if (revision is null || !ListWrite.IsWellFormedRevision(revision)) { reason = "revision"; return false; }
            if (!LandPushReplay(scope, uri, CollectionsMarshal.AsSpan(rows), revision)) { reason = "stage"; return false; }
            reason = "replayed";
            return true;
        }

        /// <summary>THE IN-PLACE LANDING: a push's replayed <paramref name="rows"/> — the whole list, true at
        /// <paramref name="revision"/> — staged as ONE whole-list run + the head + the count through the replay arm's own
        /// staging (<c>Store.StageList</c>), committed, written behind, and only THEN the list stamped revalidated. A staging
        /// that REFUSES (a member with no identity, a malformed head) changes nothing: nothing is committed, nothing written
        /// and nothing stamped — a stamp would call current a list that never landed, and its next open would paint it
        /// without asking — and the caller falls back to dirty (or to a revalidation when the list is on screen). True when
        /// it landed. UI thread. Public for the facts (this assembly has no <c>InternalsVisibleTo</c>).</summary>
        public static bool LandPushReplay(Scope scope, string uri, ReadOnlySpan<ListRow> rows, string revision)
        {
            ArgumentNullException.ThrowIfNull(scope);
            var staging = Staging.Rent();
            staging.Epoch = scope.Epoch;                                      // C7: a replaced scope drops it whole
            if (!Store.StageList(staging, EdgeRelation.PlaylistTracks, uri, rows, revision))
            {
                Staging.Return(staging);
                return false;
            }
            Entities.Commit(staging);
            Entities.Publish();
            if (!Store.WriteBehind(staging)) Staging.Return(staging);         // write-behind owns it when it accepts
            ListStamps.MarkRevalidated(uri, ListStamps.NowMs());              // exactly the head the server holds
            return true;
        }

        /// <summary>A member whose uri the snapshot left EMPTY (a gid-form id, whose key the store thread formats when it
        /// writes) is spelled here from the live list — the replay compares uris when a row has no item id, and the
        /// staging lands text identities. False when a member still has none.</summary>
        static bool SpellUris(Scope scope, int slot, ListRow[] baseline)
        {
            ReadOnlySpan<int> targets = scope.Edges.PlaylistTracks.Targets(slot);
            var tracks = scope.Tracks;
            for (int i = 0; i < baseline.Length; i++)
            {
                if (baseline[i].Uri.Length > 0) continue;
                if (targets.Length != baseline.Length || targets[i] <= Table.None || targets[i] >= tracks.Count) return false;
                string text = tracks.Id[targets[i]].Text;
                if (text.Length == 0) return false;
                baseline[i] = baseline[i] with { Uri = text };
            }
            return true;
        }

        /// <summary>UI THREAD, from the settle's flush: the rootlist pushed. Its live shape is unobserved (§3.10), so it is
        /// never applied in place: the head already held drops it, a head the last re-ask was already made for drops it
        /// (the pair), anything else marks it dirty and asks the rootlist edge ONCE (the held revision makes that the
        /// <c>/rootlist/diff</c>). The sidebar paints the rootlist on every page, so it is always "open".</summary>
        static void FlushRootlistPush(Scope scope, int me)
        {
            RootlistPushed? pushed = Interlocked.Exchange(ref s_rootlistPush, null);
            string? newRevision = pushed?.NewRevision;
            StringId head = scope.Edges.RootlistRevision(me);
            string? stored = head.IsEmpty ? null : Entities.Strings.Resolve(head);
            var verdict = ListPush.DecideRootlist(stored, newRevision, open: true);
            string reason = verdict == ListPush.Verdict.Drop ? "echo" : newRevision is null ? "no-revision" : "never-in-place";
            if (verdict != ListPush.Verdict.Drop && newRevision is not null
                && string.Equals(newRevision, s_rootlistAskedFor, StringComparison.Ordinal))
            {
                verdict = ListPush.Verdict.Drop;
                reason = "asked";
            }
            if (verdict != ListPush.Verdict.Drop)
            {
                Api.NoteListPush(Api.ListKind.Rootlist, scope.Users.Id[me].Text.Replace("spotify:user:", "", StringComparison.Ordinal));
                ListStamps.MarkDirty(RootlistKey(scope, me));
                s_rootlistAskedFor = newRevision;
                Entities.RefreshEdge(FetchEdge.Rootlist, me);
            }
            LogPush("rootlist", verdict, pushed?.Ops ?? 0, reason);
        }

        /// <summary>RECONNECT (plan §2: "correctness = revision + /diff on open and on reconnect"). Called by
        /// <see cref="SyncNow"/> when it runs AGAIN in a scope it already synced — a new session epoch past
        /// <see cref="LibrarySyncRules"/>' 30 s guard. Whatever changed while the socket was down reached no push, so:
        /// the playlist on screen and every DIRTY list that is held whole are re-asked at Prefetch (the held revision
        /// makes each a <c>/diff</c>, almost always a 304), and every OTHER stamp is forgotten — its next open
        /// revalidates. <see cref="ListStamps.ForgetAll"/> would be wrong: the list on screen would keep painting what
        /// it had. A list not held whole is left to its own page's ask; the rootlist's stamp is kept (the sync that
        /// called this re-asks the rootlist itself).</summary>
        static void RevalidateListsAfterReconnect(Scope scope)
        {
            var edges = scope.Edges.PlaylistTracks;
            var playlists = scope.Playlists;
            s_keptLists.Clear();
            int asked = 0;
            int open = OpenPlaylist(scope);
            if (open > Table.None && edges.State(open) == EdgeState.Complete)
            {
                s_keptLists.Add(playlists.Id[open].Text);
                Entities.RefreshEdge(FetchEdge.PlaylistTracks, open, FetchPriority.Prefetch);
                asked++;
            }
            ListStamps.CollectDirty(s_dirtyLists);
            foreach (string key in s_dirtyLists)
            {
                if (key.StartsWith(ListWrite.RootlistPrefix, StringComparison.Ordinal)) { s_keptLists.Add(key); continue; }
                if (!playlists.TryGetSlot(key.AsSpan(), out int slot) || slot == open || edges.State(slot) != EdgeState.Complete) continue;
                s_keptLists.Add(key);
                Entities.RefreshEdge(FetchEdge.PlaylistTracks, slot, FetchPriority.Prefetch);
                asked++;
            }
            asked += RevalidateShowsAfterReconnect(scope);
            int forgot = ListStamps.ForgetExcept(s_keptLists);
            s_dirtyLists.Clear();
            s_keptLists.Clear();
            Log.Event(WaveeLogLevel.Info, "library", "list.reconnect", "", null, -1, null,
                WaveeLogField.Of("revalidate", asked), WaveeLogField.Of("forgot", forgot));
        }

        /// <summary>The playlist whose page is the current destination (<see cref="Shell.Current"/> — the route just
        /// committed, whose page is mounted or mounting), or <see cref="Table.None"/>. A parked keep-alive page and another
        /// workspace tab are not on screen.</summary>
        static int OpenPlaylist(Scope scope)
        {
            Shell.Route route = Shell.Current.Peek();
            if (route.Kind != Shell.RouteKind.Playlist || !route.Subject.IsValid) return Table.None;
            return scope.Playlists.TryGetSlot(route.Subject.Id, out int slot) ? slot : Table.None;
        }

        /// <summary>The rootlist's stamp key (<see cref="ListWrite.RootlistKey"/>): the account's user uri.</summary>
        static string RootlistKey(Scope scope, int me) => ListWrite.RootlistKey(scope.Users.Id[me].Text);

        /// <summary>The one always-on line per push. No uri, no name: the list's KIND, what was done, how many ops the push
        /// carried, and why.</summary>
        static void LogPush(string list, ListPush.Verdict verdict, int ops, string reason)
            => Log.Event(WaveeLogLevel.Info, "library", "list.push", "", null, -1, null,
                WaveeLogField.Of("list", list), WaveeLogField.Of("verdict", ListPushReplay.VerdictName(verdict)),
                WaveeLogField.Of("ops", ops), WaveeLogField.Of("reason", reason));
    }
}
