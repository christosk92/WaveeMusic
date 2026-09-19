// ── Entities/Fetch.cs — CORE planner + SHELL runner (owner C, wave 1, budget 700; plan §2, §4.5) ─────────────────────
//
// THE PLANNER. Every page in the app asks for data the same way — `Entities.Ensure(rows, wanted)` — and this file is
// what that becomes: `wanted & ~known & ~asked`, deduped per GROUP for the life of the scope, disk before network, and
// batched by provider and shape (§5.4, C7, P4).
//
// It replaces, by construction, the four mechanisms 0.2.9 needed to answer "have we already asked for this?":
// `HydrationLedger`'s (locale, uri, level) seals, `ResourceCoordinator`'s job table and waiters, six per-service
// negative memos, and `MetadataService`'s per-uri `Resource`. All four existed because "what is missing" was a
// question about OBJECTS. Here it is one AND-NOT over two columns, so the answer is a bitmask and the dedupe is a bit
// — and neither needs a second store to remember it.
//
// The two halves, and the line between them:
//   CORE (pure, no I/O, no allocation after warm-up): `Plan` / `Continue` decide WHICH rows need WHAT, and mark them.
//     `Backoff` and `Retryable` are pure functions of (attempt, status, retry-after) — ported from 0.2.9's
//     `RateLimitMiddleware` (the 429 clamp) and `Mutation`/`LibrarySync` §8.3 (min(60 s, 2^attempts)).
//   SHELL: the runner keeps at most four requests in flight, hands each provider a batch of at most 300 uris (the
//     one copy of the extended-metadata ceiling 0.2.9 spread across seven services), and takes answers back on the
//     UI thread through `Answer` / `Failed`. WHICH routes a provider sends for a batch is `FetchRoutes` (the named
//     partial `Fetch.Routes.cs`), and the relation half of the door is `Fetch.Edges.cs`.
//
// THE DRAIN (wave D4, plan §3.5 — "the query layer batches", the half of P4 that never existed). A door never sends:
// `Plan`, `Continue`, `PlanEdge` and the disk leg's `AfterDisk` bucket their rows and OWE a drain, and the host calls
// `Drain()` once per UI tick — so every `Ensure` of a tick has landed in its bucket before the bucket leaves, as ONE
// request of up to 300 uris. Until 2026-09-19 `Pump()` ran inline at the end of every door, and four one-row Ensures in
// a tick were four one-uri POSTs (122 extended-metadata POSTs in 62 s, median body 107 B). Two doors still pump inline:
// a PLAYBACK ask (the now-playing row must not wait a frame; whatever of its shape the tick already bucketed rides the
// same request) and a SETTLE (`Answer`/`Failed` free an in-flight slot). The first owe of a tick wakes the host
// (`WakeForDrain`), so an ask made outside a rendered frame — a posted answer's re-plan, a session event — never waits
// for an unrelated repaint; `NextWakeAt` answers "now" while one is owed.
//
// THE ONLINE GATE (`CanSend`). A provider that may not send yet — the Spotify session before it is Online — has its
// buckets HELD by `Pump`: never dropped, never un-asked, never re-planned. They leave on the first pump after the gate
// opens (the session's Online transition pumps; so does every tick while anything is held), so the boot burst goes out
// as a few full batches instead of as unauthenticated requests to the fallback spclient that 401 and retry. The disk
// leg is NOT gated: disk before network, whatever the session is doing.
//
// Every request the runner sends writes one always-on `fetch.send` line and every settle one `fetch.answer` line — the
// `wire.call` line (Platform.Wire.cs) says WHAT went out; these say WHY, and how long the rows waited for it.
//
// THE FIVE MARKS, and what each one means — they are the whole state machine, and there is no other:
//   `Asked[slot] & group`      somebody asked for this GROUP of this row in this scope (G-040). Set when a plan marks
//                              the row; it SURVIVES the answer — a group asked and not answered is the "exhausted"
//                              seal 0.2.9's ledger needed a second cache to hold, and it is what stops a TrackV4 answer
//                              (which fills Identity and not PlayCount) from being re-POSTed by the next `Ensure(Row)`.
//                              A TERMINAL transport failure un-asks the batch's groups (a 503 is not an answer); so does
//                              an ANSWER for the groups of a route that did not answer beside one that did (`Answer`'s
//                              `unfilled`: a 401 on the top-tracks REST next to a 200 overview un-asks Chart, and Chart
//                              only); `Refresh` un-asks on demand (a Retry); the scope being replaced drops every mark
//                              with its tables. An auth refusal (401/403, `Queue.SeedRetryOn`) is un-asked AND remembered
//                              (`s_refused`), so the next Online transition re-plans it (`Resume`) without a remount.
//   `Inflight[slot] == stamp`  a request for this row is out right now. `Table.Applied` clears it when a group lands, and
//                              a batch settling (`Answer` or `Failed`) clears it for every row it carried — an answer
//                              that did not name a row is still that row's answer, and `Asked && Inflight == 0` is how a
//                              surface reads "asked, nothing coming" (Search.Page.cs, Artist.UI.Chart.cs). The store's
//                              trim reads it as "pinned". It does not gate the dedupe — `Asked` does.
//   `FetchedAt[slot] != 0`     somebody has answered about this row before — including the disk answering "I do not
//                              have it" (Store.ReadCore stamps the whole batch). It is what makes the disk leg run
//                              ONCE per row per session instead of once per page mount.
//   `Known[slot] & group`      the group is filled. Nothing else is a hydration level, and there are no others.
//   `Stale[slot] & group`      the group is filled AND belongs to an ended edition: a daylist past its rollover, a chart
//                              past its week. The columns keep their values and every surface keeps rendering them —
//                              a stale row is never a skeleton — but the planner reads `Settled = Known & ~Stale` where
//                              it used to read `Known`, so the group is re-asked like a missing one and `Table.Accepts`
//                              lets any wire authority replace it. `Entities.Invalidate` sets it (and un-asks, so the
//                              seal does not hold the old edition in place); `Table.Applied` clears it when the answer
//                              lands. It is the ROLLOVER TWIN of `Refresh`: Refresh re-asks what is NOT known, Invalidate
//                              re-asks what IS. Never persisted — a restart re-derives it from the clock.
//
// A BUCKET IS (provider, subject, kind, need). `need` is the per-row `wanted & ~settled & ~asked` — not the page's
// `wanted` — so a row whose Identity is already in flight and whose PlayCount is not asks for PlayCount alone, and a
// partial answer never re-asks what it already answered. Rows of one `Ensure` nearly always share one need. PRIORITY IS
// NOT PART OF THE KEY (wave D4): a bucket carries the HIGHEST priority of the rows that joined it since it last emptied,
// so the sidebar's Visible identity ask and `EnsureRootlistRows`' Prefetch ask of the same shape are one request, sent
// at Visible — where a priority in the key made them two.
//
// SUBJECTS (G-041). A row of the eight entity tables is addressed by its kind. The four SYNTHETIC tables — Home feeds,
// sections, search subjects, browse nodes — all answer `EntityKind.Unknown`, and their uris (`wavee:home`,
// `wavee:search:…`, `wavee:browse`) are owned by no provider, so the planner used to abandon every one of them. A row's
// SUBJECT is decided here, off the table it lives in (`FetchSubject`), and a synthetic subject nobody owns is routed to
// the SCOPE's own catalogue provider: Home in a Spotify scope is Spotify's, in `--fake` it is the seed's.
//
// IDENTITY, SINCE 2026-09-12 (docs/plans/wavee/wavee-0.3-entity-identity-memory.md, option 2). A row is named by a
// packed 24-byte `EntityId`: routing reads `Id[slot].Provider` (one field) and a bucket holds `(slot, EntityId)` pairs,
// so a gid row crosses to the provider as 16 bytes and no string (`batch.Ids[i]`, `WriteGid`); `batch.Uri(i)` is the
// door for a provider that genuinely needs text. The planner's fast path allocates nothing after warm-up: `FetchTests`
// pins that over a 300-uri batch, which is the doc's own unit of measurement.
//
// Rules: P4 (batch APIs only), P8/P9 (the planner allocates nothing per call after warm-up — no LINQ, no closures,
// no async; the runner's buffers are pooled and grow ×2), C1 (every mutation here is on the UI thread), C7 (the
// scope epoch is the drop key), C8 (bounded: at most four requests, 300 uris each).

using System.Buffers;
using System.Diagnostics;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>One batch of rows going out to one provider. Handed to a <see cref="FetchProvider"/>, which answers it
/// exactly once through <see cref="Fetch.Answer"/> or <see cref="Fetch.Failed"/> — carrying the
/// <see cref="Ticket"/> back, because by the time an answer arrives the scope may be gone (C7).
///
/// <para><b>What crosses to the provider's thread, and why it is safe.</b> <see cref="Ids"/> are values — a provider
/// reads a kind, a provider and 16 gid bytes off them with no table and no interner (C1). The rows that have uri TEXT
/// carry it in <see cref="Text"/>, resolved on the UI thread when the batch was filled, because resolving an id off
/// the UI thread is only safe while that id is alive and a batch outlives the guarantee. <see cref="Uri"/> answers
/// either kind from any thread: a gid formats (81 ns, one string), a text row hands back the string already there.</para>
///
/// <para><b>What to send.</b> <see cref="FetchRoutes.For(FetchBatch,Span{FetchRoute},out uint)"/> turns
/// (<see cref="Subject"/>, <see cref="Kind"/>, <see cref="Wanted"/>) into the extension kinds, pathfinder operations and
/// spclient routes that fill those groups; an EDGE batch (<see cref="Subject"/> == <see cref="FetchSubject.Edge"/>) is
/// <see cref="FetchRoutes.ForEdge"/> of (<see cref="Edge"/>, <see cref="Offset"/>), and its rows are the PARENTS.</para></summary>
public sealed class FetchBatch
{
    /// <summary>The answer key. Unique for the life of the process; a duplicate or late answer is dropped by it.</summary>
    public uint Ticket;
    public EntityProvider Provider;
    /// <summary>The kind of the rows' table. For a synthetic subject this is <see cref="EntityKind.Unknown"/> and
    /// <see cref="Subject"/> is what names the table; for an edge batch it is the PARENT table's kind.</summary>
    public EntityKind Kind;
    /// <summary>Which table the rows live in, beyond their kind (G-041): an entity row, one of the synthetic subjects,
    /// or the parents of an edge request.</summary>
    public FetchSubject Subject;
    /// <summary>The field groups this batch asks for, as the table's <c>&lt;Kind&gt;Fields</c> bits — the rows' shared
    /// NEED, never more. A provider that can only answer some of them answers those; the rest stay asked (sealed) for
    /// the scope. Zero for an edge batch.</summary>
    public uint Wanted;
    /// <summary>The relation an edge batch asks for (<see cref="FetchEdge.None"/> for a row batch).</summary>
    public FetchEdge Edge;
    /// <summary>The page offset an edge batch asks for (0 for a row batch, and for the first page).</summary>
    public int Offset;
    public FetchPriority Priority;
    /// <summary>The scope epoch this batch belongs to. Stamp it into the answer's <c>Staging.Epoch</c> and the commit
    /// drops the whole batch if the scope has been replaced meanwhile (C7).</summary>
    public uint Epoch;
    /// <summary>How many times this shape has been retried; 0 the first time out.</summary>
    public int Attempt;
    public int Count;
    /// <summary>The identities, <see cref="Count"/> of them. A metadata POST wants the 16 raw bytes of each
    /// (<see cref="EntityId.WriteGid"/>), which is what the wire asked for all along (doc §2).</summary>
    public EntityId[] Ids = [];
    /// <summary>The REQUEST uri, parallel to <see cref="Ids"/>; null when the row's own identity is the request, which
    /// is the ordinary case for a gid row (it has no text anywhere in the process). Filled on the UI thread by the
    /// runner — see the class summary — for the TEXT-form rows, and for the one group whose request is addressed at a
    /// DERIVED entity rather than at the row: <see cref="Extension"/> says which.</summary>
    public string?[] Text = [];
    /// <summary>The slots the rows came from, parallel to <see cref="Ids"/>. The runner's business, not the provider's.</summary>
    public int[] Slots = [];
    /// <summary>For an EDGE batch, the revision each parent's list was last answered at, parallel to <see cref="Ids"/> —
    /// resolved on the UI thread at send like <see cref="Text"/> — or null when none is held (a first read). It is what
    /// lets a provider choose the <c>/diff</c> read over the full one without touching a table: the rootlist's
    /// (<c>Edges.RootlistRevision</c>) and the recents snapshot's (<c>Edges.RecentsRevision</c>). Null for a row batch.</summary>
    public string?[] Revisions = [];
    /// <summary>For an EDGE batch of a persisted list (a playlist's membership, the rootlist): the settled list each
    /// parent's held revision describes, as text, snapshotted on the UI thread at send (<c>Fetch.FillBaselines</c> →
    /// <c>Store.SnapshotList</c>) — the baseline a <c>/diff</c>'s ops are replayed over on the provider's thread without
    /// touching a table (wave D3). Null where no revision is held or the list is not a settled whole. Parallel to
    /// <see cref="Ids"/>.</summary>
    public ListRow[]?[] Baselines = [];
    /// <summary>The extended-metadata kind this batch asks for, or 0 for "the kind's own routes" (a provider maps that
    /// through <see cref="FetchRoutes"/>). Non-zero for exactly one group today: <see cref="Fetch.AudioFilesKind"/> = 5,
    /// the FLAC ladder, whose request is a <c>spotify:audio:</c> uri in <see cref="Text"/> and whose ANSWER is keyed by
    /// that same uri — so a provider maps the answer back to the row it asked for with <see cref="IdFor"/> (plan §5.2).</summary>
    public int Extension;

    /// <summary>The <see cref="Stopwatch"/> timestamp the runner handed this batch to its provider at — what the
    /// always-on <c>fetch.answer</c> line's <c>ms=</c> is measured from. The runner's bookkeeping, not the provider's.</summary>
    internal long SentAt;

    /// <summary>The batch as identities — the span a provider builds its request from.</summary>
    public ReadOnlySpan<EntityId> Wire => Ids.AsSpan(0, Count);

    /// <summary>Row <paramref name="i"/>'s uri as text, from ANY thread: the string the UI thread resolved for a
    /// text-form row, or a fresh one formatted from the gid. One allocation for a gid row, so a transport that speaks
    /// protobuf should read <see cref="Ids"/> instead and never call this.</summary>
    public string Uri(int i)
    {
        if ((uint)i >= (uint)Count) return "";
        if (Text[i] is { } text) return text;
        EntityId id = Ids[i];
        if (id.Form != EntityForm.Gid) return "";
        Span<char> buf = stackalloc char[EntityId.MaxGidTextChars];
        return new string(buf[..id.Format(buf)]);
    }

    /// <summary>Row <paramref name="i"/>'s 16 raw big-endian gid bytes, or 0 written for a row that has no gid. The
    /// allocation-free door, and the one Wave 2's extended-metadata request uses.</summary>
    public int Gid(int i, Span<byte> dst16) => (uint)i < (uint)Count ? Ids[i].WriteGid(dst16) : 0;

    /// <summary>The identity of the row whose REQUEST uri was <paramref name="utf8"/> — how an answer keyed by a
    /// derived entity finds the row that asked for it. The kind-5 envelope's <c>entity_uri</c> is
    /// <c>spotify:audio:&lt;uuid&gt;</c>, which is not a row in any table and cannot be parsed back into one; the batch
    /// that asked is the only thing that knows which track it stood for (plan §5.2: "the caller passes the track's
    /// StagedId it asked for").
    ///
    /// <para>Linear over <see cref="Count"/> — at most 300, once per answered entity, on the provider's own thread, and
    /// allocation-free. <c>default</c> when nothing in this batch asked for that uri, which a caller must treat as
    /// "drop this entity" rather than as a row.</para></summary>
    public EntityId IdFor(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return default;
        for (int i = 0; i < Count; i++)
        {
            if (Text[i] is not { Length: > 0 } text || text.Length != utf8.Length) continue;
            int j = 0;
            // ASCII only, deliberately: a derived catalog uri is `spotify:audio:` + 22 base62 characters, and letting a
            // non-ascii char compare by its low byte could match the WRONG row's uri (a local file's path can hold one).
            while (j < utf8.Length && text[j] < 0x80 && (byte)text[j] == utf8[j]) j++;
            if (j == utf8.Length) return Ids[i];
        }
        return default;
    }
}

/// <summary>A transport that can answer for one provider's uris. Wave 2's Spotify session is one; the local-files
/// opener and each playback module are others (<see cref="EntityProvider"/> is the routing key).
///
/// <para><see cref="Start"/> is called ON THE UI THREAD and must not block: hand the batch to your own thread/socket
/// and return. Answer through <see cref="Fetch.Answer"/> (posted back to the UI thread) or
/// <see cref="Fetch.Failed"/> — exactly once per batch, or that batch's slots stay in flight until the scope
/// changes.</para></summary>
public abstract class FetchProvider
{
    public abstract EntityProvider Provider { get; }

    /// <summary>Take a batch. Never blocks; never touches a table.</summary>
    public abstract void Start(FetchBatch batch);

    /// <summary>The scope moved on (D9): anything still in flight for an older epoch is now pointless. Optional —
    /// a provider that ignores it merely wastes one round trip, because the commit drops the answer anyway.</summary>
    public virtual void Abandon(uint epoch) { }
}

/// <summary>The planner and the runner. Called by <c>Entities.Ensure</c> through the <c>PlanFetch</c> hook (rows) and by
/// <c>Entities.EnsureEdge</c> (relations, <c>Fetch.Edges.cs</c>); there is no single-uri entry point to reach for (P4).</summary>
public static partial class Fetch
{
    /// <summary>Bumps once per batch that SETTLED — answered or failed. A commit publishes the tables it wrote, but a
    /// batch that fails, or that answers without naming a row, only clears that row's <c>Inflight</c>/<c>Asked</c>
    /// marks and publishes nothing; a surface whose verdict reads those marks (the track table's reveal gate,
    /// <c>TableRules.RowUnsettled</c>) re-reads here so "asked, nothing coming" reaches it. UI THREAD.</summary>
    public static readonly Signal<uint> Settled = new(0);

    /// <summary>THE per-request entity ceiling — one extended-metadata POST. 0.2.9 carried seven copies of this 300
    /// (adornments, play counts, video detect, expansion, the closure, the paged hydrate, show episodes); here a page
    /// demands its whole model and the QUERY layer batches, so the number lives once, in the layer that batches.</summary>
    public const int MaxUrisPerRequest = 300;

    /// <summary>Concurrent requests. Four is 0.2.9's shape and the ceiling a desktop client should put on a shared
    /// spclient: more parallelism buys nothing once the server rate-limits, and a 429 storm costs a whole minute.</summary>
    public const int MaxInFlight = 4;

    /// <summary>Attempts per batch before it is abandoned (0.2.9 <c>RateLimitMiddleware</c>: four passes).</summary>
    public const int MaxAttempts = 4;

    // ── the one DERIVED request (FLAC plan §5.1, §5.2) ──────────────────────────────────────────────────────────────
    //
    // Every other group is asked ON THE ROW: the batch's identities go out and the answers come back keyed by the same
    // uris. `TrackFields.Files` — the format ladder, and therefore "is this track available lossless" — is asked on the
    // `spotify:audio:<base62(original_audio.uuid)>` entity instead, because TRACK_V4's own `file[]` carries Ogg and AAC
    // and never a FLAC row (librespot #1578/#1583; observed in a live capture). That makes it a different uri AND a
    // different extension kind from everything else a track wants, so it gets its OWN bucket and can never ride
    // another group's POST — the planner's shape key already separates it, since the need is part of that key.

    /// <summary>The extended-metadata kind for <see cref="TrackFields.Files"/>: <c>AUDIO_FILES = 5</c>
    /// (<c>extension_kind.proto</c>). Named here because the planner is what decides a batch asks for it.</summary>
    public const int AudioFilesKind = 5;

    /// <summary>How long <see cref="WriteAudioUri"/> writes: <c>"spotify:audio:"</c> + 22 base62 characters.</summary>
    public const int AudioUriChars = 14 + Base62.GidChars;

    /// <summary>The derived request uri for a track's audio entity: <c>spotify:audio:&lt;base62(uuid)&gt;</c>, written
    /// into <paramref name="dst"/>. Returns 0 for a zero key — the row named no original audio, so there is no entity
    /// to ask and no ladder to be had. Pure and allocation-free; the same encode
    /// <c>Spotify.Audio.LosslessMetadata</c> does per open.</summary>
    public static int WriteAudioUri(UInt128 audio, Span<char> dst)
    {
        const string prefix = "spotify:audio:";
        if (audio == UInt128.Zero || dst.Length < AudioUriChars) return 0;
        prefix.CopyTo(dst);
        Base62.Encode(audio, dst[prefix.Length..]);
        return AudioUriChars;
    }

    /// <summary>Does this bucket ask the derived audio entity? True for the <see cref="TrackFields.Files"/> bucket and
    /// nothing else — and it is an equality, not a mask test: a bucket that mixed Files with another group would be a
    /// bucket the planner failed to split (see <see cref="Queue"/>).</summary>
    static bool IsAudioFiles(FetchSubject subject, EntityKind kind, uint wanted)
        => subject == FetchSubject.Entity && kind == EntityKind.Track && wanted == (uint)TrackFields.Files;

    // ── the pure half (CORE) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The in-flight stamp for a scope. <c>Inflight = 0</c> means "nobody is asking", so the stamp must never
    /// be 0 — and the FIRST scope's epoch IS 0. One addition, and the in-flight mark works on the first launch as well as
    /// after a locale switch.</summary>
    public static uint Stamp(uint epoch) => epoch + 1;

    /// <summary>Is this status worth trying again? A transport error (0, no status), a rate limit, or a server fault.
    /// A 4xx is the server telling us the answer, and repeating the question does not change it.</summary>
    public static bool Retryable(int status) => status == 0 || status == 429 || status >= 500;

    /// <summary>How long to wait before attempt <paramref name="attempt"/> + 1, in seconds. Ported verbatim in
    /// behaviour from 0.2.9: <c>Retry-After</c> wins for a 429 but is CLAMPED to 30 s (the header is unauthenticated
    /// — a hostile or buggy server saying 86400 must not make the client sleep for a day), otherwise
    /// <c>min(60, 2^attempt)</c> (<c>Mutation.cs</c> §8.3 and <c>LibrarySync</c>'s drain).</summary>
    public static int Backoff(int attempt, int status, int retryAfterSeconds)
    {
        if (status == 429) return retryAfterSeconds > 0 ? Math.Clamp(retryAfterSeconds, 1, 30) : 1;
        int shift = attempt < 0 ? 0 : attempt > 6 ? 6 : attempt;
        return Math.Min(60, 1 << shift);
    }

    /// <summary>A row's NEED: the groups of <paramref name="wanted"/> it does not have SETTLED (known and not stale) and
    /// nobody has asked for yet this scope. The one expression the whole dedupe is (G-040).</summary>
    public static uint NeedOf(Table table, int slot, uint wanted) => wanted & ~table.Settled(slot) & ~table.Asked[slot];

    /// <summary>THE filter, pure over columns and allocation-free: which of <paramref name="slots"/> still NEED any bit
    /// of <paramref name="wanted"/> (<see cref="NeedOf"/>). Writes them into <paramref name="dst"/> and returns how many.
    /// <para>It does NOT mark anything — <see cref="Plan"/> does that after it has somewhere to send them. Keeping
    /// the decision separate from the side effect is what lets a test assert the decision (§4.15).</para></summary>
    public static int Select(Table table, ReadOnlySpan<int> slots, uint wanted, Span<int> dst)
    {
        if (wanted == 0) return 0;
        int n = 0;
        for (int i = 0; i < slots.Length && n < dst.Length; i++)
        {
            int slot = slots[i];
            if (slot <= Table.None || slot >= table.Count) continue;   // slot 0 is "none": there is nothing to fetch
            if (NeedOf(table, slot, wanted) == 0) continue;            // known, or already asked — one AND-NOT
            dst[n++] = slot;
        }
        return n;
    }

    // ── state (SHELL) ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One (provider, subject, kind, need) bucket of rows waiting to go out — or, for an edge, (provider,
    /// relation, offset) of PARENTS. That tuple IS the "shape" of a request: everything in a bucket can ride the same
    /// request, and nothing outside it can. Urgency is not shape: <see cref="Priority"/> is the highest of the asks that
    /// joined the bucket since it last emptied (<see cref="Bucket"/>).</summary>
    sealed class Demand
    {
        public EntityProvider Provider;
        public EntityKind Kind;
        public FetchSubject Subject;
        public uint Wanted;
        public FetchEdge Edge;
        public int Offset;
        /// <summary>The MAX priority of the rows waiting here — a Visible row joining a Prefetch bucket lifts the whole
        /// request, and the next ask into an emptied bucket starts it again from its own.</summary>
        public FetchPriority Priority;
        /// <summary>The <see cref="Stopwatch"/> timestamp the oldest row still waiting here joined at (the bucket went
        /// from empty to not): the always-on <c>fetch.send</c> line's <c>waitedMs=</c>.</summary>
        public long Since;
        public Table Table = null!;
        public int Count;
        public int[] Slots = new int[64];
        /// <summary>The identity each slot carried when it was PLANNED. Snapshotted rather than re-read from the
        /// column at send time on purpose: a slot that is freed and recycled between the plan and the send must send
        /// the entity that was asked for, not whatever moved into its slot.</summary>
        public EntityId[] Ids = new EntityId[64];
        /// <summary>App seconds before which this bucket must not be sent (the backoff, and only that).</summary>
        public int ReadyAt;
        /// <summary>How many attempts the last failure had burned; carried onto the next batch out of this bucket.</summary>
        public int Attempt;

        public void Add(int slot, in EntityId id)
        {
            if (Count == Slots.Length)
            {
                Array.Resize(ref Slots, Slots.Length * 2);
                Array.Resize(ref Ids, Ids.Length * 2);
            }
            Slots[Count] = slot;
            Ids[Count++] = id;
        }

        /// <summary>Drop the first <paramref name="n"/> rows (they just went out) and keep the rest for the next pump.</summary>
        public void Drop(int n)
        {
            if (n >= Count) { Count = 0; return; }
            Array.Copy(Slots, n, Slots, 0, Count - n);
            Array.Copy(Ids, n, Ids, 0, Count - n);
            Count -= n;
        }
    }

    // Keyed by the whole shape. A (long, long) pair because the edge half of the key does not fit beside the row half in
    // one word: provider | subject | kind | edge in the first, need-or-offset in the second. No priority (see the header).
    static readonly Dictionary<(long, long), Demand> s_buckets = new(16);
    static readonly List<Demand> s_order = new(16);                 // stable iteration; the dictionary is the index
    static readonly Dictionary<uint, FetchBatch> s_active = new(8);
    static readonly Stack<FetchBatch> s_pool = new(8);
    static readonly FetchProvider?[] s_providers = new FetchProvider?[8];   // indexed by (byte)EntityProvider
    static uint s_ticket;
    static int s_inFlight;
    static uint s_scopeEpoch;

    // ── the drain and the gate (wave D4; see the header) ────────────────────────────────────────────────────────────
    //
    // `s_drainOwed`: a row joined a bucket since the last `Pump` — the tick's `Drain` owes a pump. `s_held`: the last
    // pump passed over a bucket `CanSend` refused — the tick's `Drain` pumps again, so the held rows leave on the first
    // tick after the gate opens even if nothing else asks. Both are cleared at the top of every `Pump`, which sends
    // everything that can go, so "owed" never outlives the pump that satisfied it.
    static bool s_drainOwed;
    static bool s_held;

    /// <summary>May this provider send right now? A <c>false</c> HOLDS its buckets — never drops them, never un-asks a
    /// row, never re-plans — so the boot burst leaves as a few full batches when the session comes Online instead of as
    /// unauthenticated requests that 401 (plan §1.5: <c>AccessToken()</c> is null before the welcome, so the header was
    /// simply omitted, against the fallback spclient). Consulted by <see cref="Pump"/> per bucket; the disk leg is never
    /// gated. Null (the default, and every test that does not set it) lets everything send.
    /// <para><c>Spotify.Library.Install</c> sets <c>CanSend = p =&gt; p != EntityProvider.Spotify || Spotify.Current.IsOnline</c>,
    /// and the session's Online transition calls <see cref="Pump"/> so the held buckets leave at once; the per-tick
    /// <see cref="Drain"/> re-tries a held bucket every tick besides, so a missed transition is a delay of one frame,
    /// never a stranded ask. <see cref="Reset"/> clears it (the test seam).</para></summary>
    public static Func<EntityProvider, bool>? CanSend { get; set; }

    /// <summary>The host's wake for an owed drain: called ONCE when a drain becomes owed (the first ask of a tick), never
    /// again until a pump has satisfied it. The GUI host posts its drain through the UI poster here, which wakes an idle
    /// loop (the post runs before the engine's idle gate), so an ask made outside a rendered frame — a posted answer's
    /// re-plan, a session event, a minimized window — leaves within one loop pass instead of waiting for an unrelated
    /// repaint. Null in the headless host (its 100 ms tick pumps) and in tests (they call <see cref="Drain"/>). UI THREAD.</summary>
    public static Action? WakeForDrain { get; set; }

    // ── the in-flight ROUTE index (request de-dupe across a row bucket and an edge bucket, see file header) ───────────
    //
    // A ROW ask and an EDGE ask are always two different buckets — different shape keys, no way to merge them without
    // changing the bucket contract — but the TRANSPORT `FetchRoutes` sends them on can be the very same wire call
    // (an album's row asks Metadata(AlbumV4) and `AlbumTracks` at offset 0 asks the very same route). The planner
    // cannot see that at PLAN time (routes are a SEND-time question, decided from what the batch actually carries), so
    // the dedupe lives here, in the runner, keyed by what the wire would carry rather than by what asked for it.
    //
    //   SEND (a ROW batch): index[(routeKey, parentId)] = ticket, for every route `FetchRoutes.For` selects — one row
    //     batch can justify several routes at once (an artist's `All` ask is both ArtistOverview and the top-tracks
    //     REST), and any of them might be what an edge would have asked for.
    //   SEND (an EDGE batch): before the wire batch is built, drop any parent whose (the edge's OWN route, parent) is
    //     already in the index — the request is already out, on a batch this runner is holding. The dropped parent is
    //     NOT lost: it is recorded against the blocking ticket (`s_pendingEdges`) and the bucket forgets it (`Drop`).
    //   SETTLE (the blocking ticket, answer OR failure): the index entries this ticket owns are removed (the wire slot
    //     is free again) and every parent recorded against it is re-examined — if the row's answer did not happen to
    //     stage the relation (`EdgeTableBase.State` is still `Unknown`), the parent goes back into its edge bucket and
    //     the next `Pump` really sends it. A row that FAILS clears the same way, retryable or not: the blocked ask must
    //     never wait on a backoff that was never its own.
    //
    // Two dictionaries, both written only from `Send`/`Answer`/`Failed` (never from `Plan`/`Queue`, the CORE hot path)
    // and both cleared on a scope switch and a boot — the same "written once per Send, cleared on Recycle" shape the
    // rest of the runner's bookkeeping already has.
    static readonly Dictionary<(RouteKey, EntityId), uint> s_routeIndex = new(64);
    static readonly Dictionary<uint, List<PendingEdge>> s_pendingEdges = new(8);

    /// <summary>The cheap half of a route: which transport, and which of ITS routes — an extension kind, a pathfinder
    /// op or a spclient route, by <see cref="RouteTransport"/>. Two value ints, so the index's key costs nothing to
    /// hash or compare, and it needs no provider field: <see cref="EntityId"/> (the other half of the index key)
    /// already carries the provider, and two different providers never share one entity id.</summary>
    readonly record struct RouteKey(RouteTransport Transport, int Id)
    {
        public static RouteKey Of(in FetchRoute route) => new(route.Transport, route.Transport switch
        {
            RouteTransport.Metadata => route.Extension,
            RouteTransport.Pathfinder => (int)route.Op,
            RouteTransport.Spclient => (int)route.Rest,
            _ => 0,
        });
    }

    /// <summary>One edge ask a row's in-flight transport pre-empted: everything <see cref="SettleTicket"/> needs to put
    /// it back in its bucket, if the row's own answer did not happen to stage it. <see cref="Epoch"/> is the scope this
    /// was recorded under (C7) — a switch drops the whole index, but a settle that races a switch must not resurrect a
    /// parent that indexes the OLD table set.</summary>
    readonly record struct PendingEdge(Table Table, EntityProvider Provider, FetchEdge Edge, int Offset,
                                        FetchPriority Priority, int Slot, EntityId Id, uint Epoch);

    // ── the refused asks (re-plan on reconnect) ─────────────────────────────────────────────────────────────────────
    //
    // A 401/403 is terminal to the planner (`Retryable` says a 4xx is the server's answer), so the batch is un-asked and
    // abandoned — correct for a 400, wrong for a refusal that a boot-time or reconnecting session WILL answer once it
    // authorises (`Queue.SeedRetryOn` is the same verdict for the seed's context resolve). Nothing re-asks on its own: a
    // reconnect switches no scope, publishes no table, and every page that had already mounted keeps its skeleton. So
    // the terminal arm of `Failed` records what it un-asked here, and the session's Online transition (`Spotify.Library`'s
    // `SyncNow`) calls `Resume`, which re-plans whatever still names the same row in the same scope. Bounded: the oldest
    // entry goes when the list is full — the next mount's `Ensure` still retries an un-asked row, so a dropped entry is
    // a delay, never a loss.
    static readonly List<RefusedAsk> s_refused = new(16);
    const int MaxRefused = 1024;

    /// <summary>One row (or one edge parent) an auth refusal un-asked, and everything <see cref="Resume"/> needs to plan
    /// it again: the groups for a row (<see cref="Edge"/> is <see cref="FetchEdge.None"/>), the relation and page for a
    /// parent. <see cref="Epoch"/> is the scope it was recorded under (C7).</summary>
    readonly record struct RefusedAsk(Table Table, int Slot, EntityId Id, uint Groups, FetchEdge Edge, int Offset,
                                       FetchPriority Priority, uint Epoch);

    static int s_planned, s_deduped, s_toDisk, s_toNetwork, s_answered, s_failed, s_retried, s_abandoned, s_resumed;

    /// <summary>What the planner has done this session. Always on (CLAUDE.md: no env-var switches) and read by the
    /// diagnostics page; <see cref="Deduped"/> against <see cref="Planned"/> is the number that says whether the
    /// dedupe is working — ten pages asking for the same 300 rows must show one request, not ten.</summary>
    public static int Planned => s_planned;
    public static int Deduped => s_deduped;
    public static int ToDisk => s_toDisk;
    public static int ToNetwork => s_toNetwork;
    public static int Answered => s_answered;
    public static int FailedCount => s_failed;
    public static int Retried => s_retried;
    public static int Abandoned => s_abandoned;
    /// <summary>Rows and parents an auth refusal un-asked that <see cref="Resume"/> has planned again.</summary>
    public static int Resumed => s_resumed;
    /// <summary>Rows and parents an auth refusal un-asked that are waiting for the next Online transition.</summary>
    public static int Refused => s_refused.Count;
    /// <summary>Requests out right now (≤ <see cref="MaxInFlight"/>) and rows still waiting for one.</summary>
    public static int InFlight => s_inFlight;
    public static int Pending
    {
        get
        {
            int n = 0;
            for (int i = 0; i < s_order.Count; i++) n += s_order[i].Count;
            return n;
        }
    }

    // ── boot ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Called by <c>Entities.Boot</c> through the <c>FetchBoot</c> hook. Registered providers survive a boot
    /// (they are transports, not data); everything that refers to a table set does not.</summary>
    public static void Boot()
    {
        s_buckets.Clear();
        s_order.Clear();
        s_active.Clear();
        s_routeIndex.Clear();
        s_pendingEdges.Clear();
        s_refused.Clear();
        s_inFlight = 0;
        s_drainOwed = s_held = false;          // nothing is bucketed, so nothing is owed or held
        // Boot may run before `Entities.Boot` has a scope (a test calling Reset): read defensively.
        Scope? current = Entities.Current;
        s_scopeEpoch = current is null ? 0u : current.Epoch;
    }

    /// <summary>Attach a transport. One per provider; the last one wins, so a test can replace the live session. Rows
    /// bucketed for a provider nobody had registered yet were waiting for exactly this: it owes a drain, so they leave on
    /// the next tick.</summary>
    public static void Register(FetchProvider provider)
    {
        s_providers[(byte)provider.Provider] = provider;
        Owe();
    }

    /// <summary>Detach every transport, drop the send gate and forget every pending row — the test seam, and what a
    /// sign-out runs. The host's <see cref="WakeForDrain"/> survives: it is the host's wiring, not a transport.</summary>
    public static void Reset()
    {
        Boot();
        Array.Clear(s_providers);
        CanSend = null;
        s_planned = s_deduped = s_toDisk = s_toNetwork = s_answered = s_failed = s_retried = s_abandoned = s_resumed = 0;
    }

    // ── the planner ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"These rows, these groups." The one entry point for rows (through <c>Entities.Ensure</c>), and the
    /// whole of §5.4's access path:
    /// <list type="number">
    /// <item>filter to the rows that NEED something — missing and not yet asked for (<see cref="Select"/>);</item>
    /// <item>mark those groups asked for this scope, so the other nine pages asking this drain ask for nothing (C7);</item>
    /// <item>split disk / network on "has anything ever answered about this row" and send the disk leg first;</item>
    /// <item>bucket the rest by (provider, subject, kind, need) and owe the tick's <see cref="Drain"/> — or, for a
    /// <see cref="FetchPriority.Playback"/> ask, pump now (<see cref="SendOrOwe"/>).</item>
    /// </list>
    /// The disk leg answers through <see cref="Continue"/>, which is step 4 for whatever sqlite could not fill.</summary>
    public static void Plan(Scope scope, Table table, ReadOnlySpan<int> slots, uint wanted, FetchPriority priority)
    {
        if (wanted == 0 || slots.IsEmpty) return;
        DropStaleScope(scope);        // the plan's own scope is the authority here, not Entities.Current

        uint stamp = Stamp(scope.Epoch);
        int[] scratch = ArrayPool<int>.Shared.Rent(slots.Length);
        uint[] needs = ArrayPool<uint>.Shared.Rent(slots.Length);
        try
        {
            int need = Select(table, slots, wanted, scratch);
            s_planned += slots.Length;
            s_deduped += slots.Length - need;
            if (need == 0) return;

            // Mark first, send second. A page that mounts twice in one drain (a remount, a scroll that re-binds) must
            // produce ONE request, and the mark is what makes the second pass find nothing. The need is taken BEFORE
            // the mark (the mark is what would zero it) and travels with the row.
            int now = Entities.Now;
            for (int i = 0; i < need; i++)
            {
                int slot = scratch[i];
                uint groups = NeedOf(table, slot, wanted);
                needs[i] = groups;
                table.Asked[slot] |= groups;
                // A group being asked again is, by definition, no longer failed: the mark exists to tell a surface
                // "nothing is coming", and something is. (`Ensure` reaches here; so does a Retry through `Refresh`.)
                table.Failed[slot] &= ~groups;
                table.Inflight[slot] = stamp;
                table.Touched[slot] = now;
            }

            // DISK BEFORE NETWORK. A row nothing has ever answered about (`FetchedAt == 0`) might be on disk from a
            // previous launch, and sqlite costs one batched query against a round trip. A row the disk has already
            // been asked about — found or not — goes straight out: asking it again is a query that cannot answer.
            int disk = 0;
            uint diskWanted = 0;
            for (int i = 0; i < need; i++)
            {
                if (table.FetchedAt[scratch[i]] != 0) continue;
                (scratch[disk], scratch[i]) = (scratch[i], scratch[disk]);
                (needs[disk], needs[i]) = (needs[i], needs[disk]);
                diskWanted |= needs[disk];
                disk++;
            }

            var span = scratch.AsSpan(0, need);
            var groupsOf = needs.AsSpan(0, need);
            int queued;
            if (disk > 0 && Store.Read(scope, table, span[..disk], diskWanted, priority))
            {
                s_toDisk += disk;
                queued = Queue(scope, table, span[disk..], groupsOf[disk..], priority);   // the rest do not wait for the disk
            }
            else
            {
                queued = Queue(scope, table, span, groupsOf, priority);                   // no store (or it refused): straight out
            }
            if (queued > 0) SendOrOwe(priority);     // a plan the disk took whole owes nothing here: `Continue` owes
        }
        finally
        {
            ArrayPool<int>.Shared.Return(scratch);
            ArrayPool<uint>.Shared.Return(needs);
        }
    }

    /// <summary>The disk answered; whatever it could not fill goes to the provider — bucketed for the tick's drain like
    /// any plan, so a burst of cold reads completing in one loop pass leaves as one request per shape. Called by
    /// <c>Store</c> from the cold read's completion, on the UI thread, with the same slots and groups the read was asked for.
    /// <para>It re-derives <c>wanted &amp; ~known</c> rather than trusting the caller's list, so a batch the disk
    /// filled completely asks for nothing — and it does NOT subtract <c>Asked</c>, because those marks are this plan's
    /// own.</para></summary>
    public static void Continue(Scope scope, Table table, ReadOnlySpan<int> slots, uint wanted, FetchPriority priority)
    {
        if (wanted == 0 || slots.IsEmpty) return;
        if (!ReferenceEquals(scope, Entities.Current)) return;   // C7: the disk answered into a replaced set

        uint stamp = Stamp(scope.Epoch);
        int[] scratch = ArrayPool<int>.Shared.Rent(slots.Length);
        uint[] needs = ArrayPool<uint>.Shared.Rent(slots.Length);
        try
        {
            int n = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                int slot = slots[i];
                if (slot <= Table.None || slot >= table.Count) continue;
                // Only the groups this plan asked (and the disk did not fill): a group another plan asked is its own.
                uint groups = wanted & ~table.Settled(slot) & table.Asked[slot];
                if (groups == 0) continue;                  // the disk had it; `Applied` already cleared the in-flight mark
                scratch[n] = slot;
                needs[n++] = groups;
                // The disk's partial hit (`Applied`) cleared the in-flight mark while the network leg is only now going
                // out: re-stamp it, or a surface reading `Asked && Inflight == 0` as "nothing coming" (Search.Page.cs,
                // the artist chart) would call a warm page's live round trip a failure.
                table.Inflight[slot] = stamp;
            }
            if (n == 0) return;
            if (Queue(scope, table, scratch.AsSpan(0, n), needs.AsSpan(0, n), priority) > 0) SendOrOwe(priority);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(scratch);
            ArrayPool<uint>.Shared.Return(needs);
        }
    }

    /// <summary>Bucket rows by the shape of the request they belong to — a local file, a module playable and a
    /// Spotify track all live in the same <c>TrackTable</c> and cannot ride the same POST, and two rows of one page
    /// that need different groups cannot either.
    /// <para>The provider is a FIELD on the row's identity (<see cref="EntityId.Provider"/>), except for a synthetic
    /// subject nobody owns, which is the scope's catalogue's (<see cref="ProviderFor"/>, G-041).</para>
    /// <para>Returns how many bucket entries it added — 0 when every row was unowned or premature, which is what lets a
    /// door skip owing a drain that would send nothing.</para></summary>
    static int Queue(Scope scope, Table table, ReadOnlySpan<int> slots, ReadOnlySpan<uint> needs, FetchPriority priority)
    {
        var tracks = table as TrackTable;
        int queued = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            int slot = slots[i];
            uint need = needs[i];
            if (need == 0) continue;
            EntityId id = table.Id[slot];
            FetchSubject subject = SubjectOf(scope, table, slot);
            EntityProvider provider = ProviderFor(scope, subject, id);
            if (provider == EntityProvider.None)
            {
                // Nobody owns this uri, so nobody can answer for it. Leave the asked bits set: it is the cheapest
                // possible "do not ask again", and re-deriving the same verdict every drain is not free.
                s_abandoned++;
                continue;
            }

            // THE ONE SPLIT (see "the one DERIVED request" above): the Files group is a different uri and a different
            // extension kind, so it is peeled off into its own bucket and the rest ride the shape they need. A page
            // that asks for `Row | Files` in one `Ensure` therefore produces two requests, which is what it is.
            uint derived = tracks is null ? 0u : need & (uint)TrackFields.Files;
            uint plain = need & ~derived;
            if (plain != 0)
            {
                Bucket(table, subject, provider, plain, FetchEdge.None, 0, priority).Add(slot, id);
                s_toNetwork++;
                queued++;
            }
            if (derived == 0) continue;

            if (tracks!.OriginalAudio[slot] != UInt128.Zero)
            {
                Bucket(table, subject, provider, derived, FetchEdge.None, 0, priority).Add(slot, id);
                s_toNetwork++;
                queued++;
            }
            else
            {
                // NO KEY YET, which is never the same as "no ladder". The uuid rides the track's own payload, so a row
                // that has only ever been a search hit or a playlist item has none — and a TrackV4 that genuinely names
                // none seals the group at COMMIT (`CommitTracks`, §5.2), where the answer that said so is in hand. Here
                // the question is merely premature: un-ask it, so the next `Ensure` after the identity lands really
                // asks. (When the plain half went out, that batch's own `Applied` clears the in-flight mark.)
                table.Asked[slot] &= ~(uint)TrackFields.Files;
                if (plain == 0) table.Inflight[slot] = 0;
                s_abandoned++;
            }
        }
        return queued;
    }

    /// <summary>Which table a row lives in, beyond its kind (G-041). The four synthetic tables are told apart by
    /// REFERENCE — they all answer <see cref="EntityKind.Unknown"/> — and two of them split once more, on a column:
    /// a section is a Home band or a Browse band (<see cref="SectionTable.Form"/>; an unformed section is a Home band,
    /// and <c>Entities.BrowseSection</c> stamps the family before the drill page asks), and a browse node is the
    /// directory or a page.</summary>
    public static FetchSubject SubjectOf(Scope scope, Table table, int slot)
    {
        if (table.Kind != EntityKind.Unknown) return FetchSubject.Entity;
        if (ReferenceEquals(table, scope.Homes)) return FetchSubject.Home;
        if (ReferenceEquals(table, scope.Sections))
            return (SectionKind)scope.Sections.Form[slot] >= SectionKind.BrowseShelf ? FetchSubject.BrowseSection : FetchSubject.HomeSection;
        if (ReferenceEquals(table, scope.Searches)) return FetchSubject.Search;
        if (ReferenceEquals(table, scope.Browses))
            return scope.Browses.TryGetSlot(Browse.DirectoryUri.AsSpan(), out int directory) && directory == slot
                ? FetchSubject.BrowseDirectory : FetchSubject.BrowsePage;
        return FetchSubject.Entity;
    }

    /// <summary>Who answers for a row: its own provider, or — for a synthetic subject whose uri no provider owns
    /// (<c>wavee:home</c>, <c>wavee:search:…</c>, <c>wavee:browse</c>) — the SCOPE's catalogue (G-041). An entity row
    /// nobody owns stays unowned: a guess would address a transport that 404s on it.</summary>
    public static EntityProvider ProviderFor(Scope scope, FetchSubject subject, in EntityId id)
    {
        if (id.Provider != EntityProvider.None || subject is FetchSubject.Entity or FetchSubject.Edge) return id.Provider;
        return CatalogProvider(scope.Key);
    }

    /// <summary>The provider a scope's catalogue is: <c>"spotify"</c> is Spotify, <c>"fake"</c> is the seed, anything
    /// else is nobody (Platform.cs mints the live key, <see cref="CatalogScope.Fake"/> the offline one).</summary>
    public static EntityProvider CatalogProvider(in CatalogScope key)
        => key.Provider switch
        {
            "spotify" => EntityProvider.Spotify,
            "fake" => EntityProvider.Fake,
            _ => EntityProvider.None,
        };

    /// <summary>The bucket for this shape, which every caller adds a row to next. The key is the SHAPE alone — provider,
    /// subject, kind, relation and need-or-offset — and the ask's urgency is folded in: an EMPTY bucket is a new demand
    /// (its priority, attempt count and wait clock start from this ask), a waiting one takes the MAX priority, so a
    /// Prefetch ask and a Visible ask of one shape in one tick are one request at Visible.</summary>
    static Demand Bucket(Table table, FetchSubject subject, EntityProvider provider, uint wanted, FetchEdge edge, int offset,
                         FetchPriority priority)
    {
        long shape = ((long)(byte)provider << 32) | ((long)(byte)subject << 24) | ((long)(byte)table.Kind << 16) | (byte)edge;
        long payload = edge == FetchEdge.None ? wanted : offset;
        if (s_buckets.TryGetValue((shape, payload), out Demand? d))
        {
            Debug.Assert(ReferenceEquals(d.Table, table), "one bucket, one table: the key packs the subject and the kind, so this can only differ across a scope switch that was not dropped.");
            if (d.Count == 0)
            {
                // Emptied since it last sent: whatever urgency and retry count the previous demand carried were ITS own.
                // (A retry sets its attempt after this, in `Failed`; an empty bucket's backoff has always already expired,
                // because only a send empties one and a send waits for it.)
                d.Priority = priority;
                d.Attempt = 0;
                d.Since = Stopwatch.GetTimestamp();
            }
            else if (priority > d.Priority)
            {
                d.Priority = priority;
            }
            return d;
        }
        d = new Demand
        {
            Provider = provider, Kind = table.Kind, Subject = subject, Wanted = edge == FetchEdge.None ? wanted : 0,
            Edge = edge, Offset = offset, Priority = priority, Table = table, Since = Stopwatch.GetTimestamp(),
        };
        s_buckets[(shape, payload)] = d;
        s_order.Add(d);
        return d;
    }

    /// <summary>Notice a scope switch. Every public door calls this FIRST, so no code path can act on a bucket, a
    /// stamp or a ticket that belongs to a table set nobody holds any more (C7).</summary>
    static void Sync()
    {
        Scope? current = Entities.Current;
        if (current is not null) DropStaleScope(current);
    }

    /// <summary>A scope switch orphans every bucket: the slots in them index the OLD table set (D9/C7). Dropping them
    /// is one pass, and it happens the first time anything plans or pumps after the switch — there is no separate
    /// notification to wire up, and nothing can send a stale slot in between because both doors run this first.</summary>
    static void DropStaleScope(Scope scope)
    {
        if (scope.Epoch == s_scopeEpoch) return;
        s_scopeEpoch = scope.Epoch;
        s_buckets.Clear();
        s_order.Clear();
        s_routeIndex.Clear();
        s_pendingEdges.Clear();
        s_refused.Clear();            // every entry indexes the OLD table set; the new scope has no marks to resume
        s_held = false;               // a HELD bucket is dropped with the rest: its slots index the old set too (C7)
        for (int i = 0; i < s_providers.Length; i++) s_providers[i]?.Abandon(scope.Epoch);
        // In-flight batches are NOT cancelled here: their answers are dropped by `Staging.Epoch` at the commit (C7),
        // and forgetting the tickets would leak the slots in `s_active`.
    }

    // ── the runner (SHELL) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE TICK'S DRAIN. Called ONCE per UI tick by the host (its frame tick, beside <see cref="NextWakeAt"/>'s
    /// caller, and the posted wake <see cref="WakeForDrain"/> asks for). Every <c>Ensure</c> of the tick has landed in
    /// its bucket by now, so each bucket leaves as ONE request of up to <see cref="MaxUrisPerRequest"/> uris — where
    /// <see cref="Pump"/> at the end of every door made four one-row Ensures four one-uri POSTs. Pumps when a drain is
    /// owed or a bucket is being held (<see cref="CanSend"/>), and is two bool compares otherwise: nothing allocates on
    /// a tick with nothing to send. Adds at most one tick of latency to a non-Playback ask (8 ms at 120 Hz) and takes
    /// away the queueing behind one-uri requests that <see cref="MaxInFlight"/> caused. UI THREAD.</summary>
    public static void Drain()
    {
        if (!s_drainOwed && !s_held) return;
        Pump();
    }

    /// <summary>A row just joined a bucket: the tick's drain owes a pump. The FIRST owe since the last pump wakes the host
    /// (<see cref="WakeForDrain"/>); every later one in the same tick is one bool compare.</summary>
    static void Owe()
    {
        if (s_drainOwed) return;
        s_drainOwed = true;
        WakeForDrain?.Invoke();
    }

    /// <summary>The end of every door that bucketed rows. THE NOW-PLAYING ROW DOES NOT WAIT A FRAME: a
    /// <see cref="FetchPriority.Playback"/> ask pumps inline — and whatever of its shape this tick already bucketed rides
    /// the same request, at Playback. Everything else owes the tick's <see cref="Drain"/>, where the whole tick's asks
    /// leave together.</summary>
    static void SendOrOwe(FetchPriority priority)
    {
        if (priority == FetchPriority.Playback) Pump();
        else Owe();
    }

    /// <summary>The earliest app second at which something becomes sendable: <see cref="Entities.Now"/> while a drain is
    /// owed (an idle window must still drain within one tick), otherwise the earliest backed-off bucket's deadline, or
    /// <see cref="int.MaxValue"/> when nothing waits. The host arms its one idle wake timer to it (decision D23), so an
    /// expired backoff re-sends while the window is idle, minimized or hidden. A HELD bucket is deliberately not a
    /// deadline: it waits for the session, not the clock, and a wake per tick while offline would be a busy loop. UI THREAD.</summary>
    public static int NextWakeAt()
    {
        int now = Entities.Now, next = int.MaxValue;
        if (s_drainOwed) return now;
        for (int i = 0; i < s_order.Count; i++)
        {
            Demand d = s_order[i];
            if (d.Count > 0 && d.ReadyAt > now && d.ReadyAt < next) next = d.ReadyAt;
        }
        return next;
    }

    /// <summary>Send what can be sent: at most <see cref="MaxInFlight"/> requests, highest priority first, at most
    /// <see cref="MaxUrisPerRequest"/> uris each. Idempotent and cheap (one pass over at most a handful of buckets). Called
    /// by the tick's <see cref="Drain"/>, by a Playback plan, by every settle (<see cref="Answer"/>/<see cref="Failed"/>
    /// free an in-flight slot), by the host's idle wake, and by the session's Online transition (the held buckets leave).
    /// Satisfies any owed drain: whatever it could not send waits on something that pumps again (an answer, the backoff
    /// timer, the gate).
    /// <para>ORDER: Playback beats Visible beats Prefetch. Within a priority a ROW bucket goes before an EDGE bucket — the
    /// row batch is what indexes the routes an edge ask dedupes against (<see cref="DropBlocked"/>), so an album's row
    /// and its tracks edge asked in one tick stay one request whichever was asked first — and then first-come: a bucket
    /// that has been waiting is a page that has been showing skeletons.</para>
    /// <para>THE GATE: a bucket whose provider <see cref="CanSend"/> refuses is passed over and stays exactly as it is —
    /// rows, marks, attempt — and the pump remembers that it held something, so the next tick's drain looks again.</para></summary>
    public static void Pump()
    {
        s_drainOwed = false;
        s_held = false;
        Sync();
        int now = Entities.Now;
        Func<EntityProvider, bool>? canSend = CanSend;
        while (s_inFlight < MaxInFlight)
        {
            Demand? best = null;
            for (int i = 0; i < s_order.Count; i++)
            {
                Demand d = s_order[i];
                if (d.Count == 0 || d.ReadyAt > now) continue;
                if (s_providers[(byte)d.Provider] is null) continue;
                if (canSend is not null && !canSend(d.Provider)) { s_held = true; continue; }
                if (best is null || Outranks(d, best)) best = d;
            }
            if (best is null) return;
            Send(best);
        }
    }

    /// <summary>Does <paramref name="d"/> go before <paramref name="best"/>? Priority first; within one, a row bucket
    /// before an edge bucket (see <see cref="Pump"/>); otherwise the one already chosen — the earlier in
    /// <see cref="s_order"/> — keeps its place.</summary>
    static bool Outranks(Demand d, Demand best)
        => d.Priority != best.Priority
            ? d.Priority > best.Priority
            : best.Subject == FetchSubject.Edge && d.Subject != FetchSubject.Edge;

    static void Send(Demand d)
    {
        int take = Math.Min(MaxUrisPerRequest, d.Count);

        // THE EDGE HALF OF THE DEDUPE: a parent whose route is already out on an in-flight ROW batch is dropped from
        // THIS send — recorded against that batch's ticket (`RecordPending`) rather than lost — so the wire never
        // carries the same request twice. Nothing to do for a row bucket: it is what the index is built FROM.
        int send = d.Subject == FetchSubject.Edge && s_routeIndex.Count > 0 ? DropBlocked(d, take) : take;
        if (send == 0)
        {
            d.Drop(take);                 // every parent in this window is somebody else's in-flight request right now
            return;
        }

        FetchBatch batch = s_pool.Count > 0 ? s_pool.Pop() : new FetchBatch();
        if (batch.Ids.Length < send)
        {
            batch.Ids = new EntityId[send];
            batch.Text = new string?[send];
            batch.Slots = new int[send];
            batch.Revisions = new string?[send];
            batch.Baselines = new ListRow[send][];
        }
        Array.Copy(d.Ids, batch.Ids, send);
        Array.Copy(d.Slots, batch.Slots, send);
        // The text half is resolved HERE because Send runs on the UI thread and the provider's does not (C1): an id
        // may only be resolved while it is alive, and the batch outlives that guarantee. A gid row has no text at all,
        // which is nearly every row of a catalog batch — so this loop touches the interner for almost none of them.
        for (int i = 0; i < send; i++)
        {
            EntityId id = batch.Ids[i];
            batch.Text[i] = id.Form == EntityForm.Text ? Entities.Strings.Resolve(id.TextId) : null;
        }
        batch.Extension = 0;
        if (IsAudioFiles(d.Subject, d.Kind, d.Wanted)) FillAudioUris(batch, (TrackTable)d.Table, send);
        if (d.Subject == FetchSubject.Edge) FillRevisions(batch, d.Edge, send);
        if (d.Subject == FetchSubject.Edge) FillBaselines(batch, d.Edge, send);   // reads Revisions: after it (wave D3)
        batch.Count = send;
        batch.Provider = d.Provider;
        batch.Kind = d.Kind;
        batch.Subject = d.Subject;
        batch.Wanted = d.Wanted;
        batch.Edge = d.Edge;
        batch.Offset = d.Offset;
        batch.Priority = d.Priority;
        batch.Epoch = s_scopeEpoch;
        batch.Attempt = d.Attempt;
        batch.Ticket = ++s_ticket;
        long waitedMs = (long)Stopwatch.GetElapsedTime(d.Since).TotalMilliseconds;
        d.Drop(take);                      // the whole window: `send` went out, the rest is recorded in `s_pendingEdges`

        s_active[batch.Ticket] = batch;
        s_inFlight++;
        IndexRoute(batch);
        batch.SentAt = Stopwatch.GetTimestamp();
        LogSend(batch, waitedMs);          // before Start: a transport that throws settles inline, and send comes first
        FetchProvider provider = s_providers[(byte)d.Provider]!;
        try { provider.Start(batch); }
        catch (Exception)
        {
            // A transport that throws synchronously has not taken the batch: settle it here or the slots stay in
            // flight for the life of the scope.
            Failed(batch.Ticket, 0, 0);
        }
    }

    // ── the two always-on lines (wave D4) ───────────────────────────────────────────────────────────────────────────
    //
    // `fetch.send ticket= provider= subject= kind= edge= offset= rows= need= prio= attempt= waitedMs=` per request the
    // runner hands a provider, and `fetch.answer ticket= status= ms= rows= unfilled=` per settle (`retry=` on a failure).
    // The `wire.call` line says WHAT went out; these say WHY — which shape, how urgent, how long its oldest row sat in the
    // bucket — and the ticket joins the two. One entry per REQUEST, never per row, and built only when Info passes, so a
    // filtered log costs one compare. The D4 gate reads them: rows per send ≫ 1, nothing Spotify before `logged in`.

    static void LogSend(FetchBatch batch, long waitedMs)
    {
        if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
        Log.Event(WaveeLogLevel.Info, "fetch", "fetch.send", "", null, -1, null,
            WaveeLogField.Of("ticket", (long)batch.Ticket),
            WaveeLogField.Of("provider", batch.Provider.ToString()),
            WaveeLogField.Of("subject", batch.Subject.ToString()),
            WaveeLogField.Of("kind", batch.Kind.ToString()),
            WaveeLogField.Of("edge", batch.Edge.ToString()),
            WaveeLogField.Of("offset", batch.Offset),
            WaveeLogField.Of("rows", batch.Count),
            WaveeLogField.Of("need", Groups(batch.Wanted)),
            WaveeLogField.Of("prio", batch.Priority.ToString()),
            WaveeLogField.Of("attempt", batch.Attempt),
            WaveeLogField.Of("waitedMs", waitedMs));
    }

    /// <summary>The <c>fetch.answer</c> line. <paramref name="status"/> is <c>"ok"</c> for an answer and the HTTP status
    /// (0 = a transport error) for a failure, which also says whether it goes round again (<paramref name="retry"/>).
    /// Written before the batch is recycled — it reads the batch's count and send stamp.</summary>
    static void LogAnswer(FetchBatch batch, string status, uint unfilled, bool? retry)
    {
        if (!Log.IsEnabled(WaveeLogLevel.Info)) return;
        long ms = (long)Stopwatch.GetElapsedTime(batch.SentAt).TotalMilliseconds;
        if (retry is { } again)
            Log.Event(WaveeLogLevel.Info, "fetch", "fetch.answer", "", null, -1, null,
                WaveeLogField.Of("ticket", (long)batch.Ticket), WaveeLogField.Of("status", status),
                WaveeLogField.Of("ms", ms), WaveeLogField.Of("rows", batch.Count),
                WaveeLogField.Of("unfilled", Groups(unfilled)), WaveeLogField.Of("retry", again));
        else
            Log.Event(WaveeLogLevel.Info, "fetch", "fetch.answer", "", null, -1, null,
                WaveeLogField.Of("ticket", (long)batch.Ticket), WaveeLogField.Of("status", status),
                WaveeLogField.Of("ms", ms), WaveeLogField.Of("rows", batch.Count),
                WaveeLogField.Of("unfilled", Groups(unfilled)));
    }

    /// <summary>A group mask as the log writes it: <c>0x</c> + lowercase hex, so a reader never mistakes the bits for a
    /// count (<c>need=0x100</c> is PlayCount, not a hundred of anything).</summary>
    static string Groups(uint mask)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{mask:x}");

    /// <summary>Partition an EDGE demand's next-to-send window: a parent whose (the edge's route, parent) pair is
    /// already in <see cref="s_routeIndex"/> — an in-flight ROW batch is already asking this exact transport for it —
    /// is moved to the END of <paramref name="take"/> and recorded (<see cref="RecordPending"/>) instead of sent.
    /// Returns how many of the window's front entries are still sendable; the caller sends those and drops the whole
    /// window regardless (the tail is not lost — it is in <see cref="s_pendingEdges"/>).
    /// <para>A relation this build has no route for never reaches <see cref="Send"/> (<see cref="PlanEdge"/> already
    /// refused it), so <see cref="FetchRoutes.ForEdge"/> here always answers a real transport.</para></summary>
    static int DropBlocked(Demand d, int take)
    {
        FetchRoute route = FetchRoutes.ForEdge(d.Edge, d.Offset);
        if (route.Transport == RouteTransport.None) return take;
        RouteKey key = RouteKey.Of(route);
        int n = take;
        int i = 0;
        while (i < n)
        {
            if (s_routeIndex.TryGetValue((key, d.Ids[i]), out uint ticket) && s_active.ContainsKey(ticket))
            {
                RecordPending(ticket, d, i);
                n--;
                (d.Slots[i], d.Slots[n]) = (d.Slots[n], d.Slots[i]);
                (d.Ids[i], d.Ids[n]) = (d.Ids[n], d.Ids[i]);
                continue;                  // re-check whatever just moved into position i
            }
            i++;
        }
        return n;
    }

    /// <summary>Remember a parent this send dropped, against the ROW ticket that is blocking it — everything
    /// <see cref="SettleTicket"/> needs to put it back in its bucket. A small, rare allocation (one dropped ask per
    /// blocked parent, not one per <see cref="Send"/>): the collision this whole index exists for is a handful of page
    /// opens, never the 300-row hot path.</summary>
    static void RecordPending(uint ticket, Demand d, int i)
    {
        if (!s_pendingEdges.TryGetValue(ticket, out List<PendingEdge>? list))
            s_pendingEdges[ticket] = list = new List<PendingEdge>(4);
        list.Add(new PendingEdge(d.Table, d.Provider, d.Edge, d.Offset, d.Priority, d.Slots[i], d.Ids[i], s_scopeEpoch));
    }

    /// <summary>Index a just-sent ROW batch's transports (<see cref="FetchRoutes.For"/>) so an edge bucket about to
    /// send can find them (<see cref="DropBlocked"/>). A no-op for an edge batch — it never blocks another edge, only a
    /// row batch is ever the BLOCKER — and for a batch whose need nothing routes.</summary>
    static void IndexRoute(FetchBatch batch)
    {
        if (batch.Subject == FetchSubject.Edge) return;
        Span<FetchRoute> routes = stackalloc FetchRoute[FetchRoutes.MaxRoutes];
        int count = FetchRoutes.For(batch, routes, out _);
        for (int r = 0; r < count; r++)
        {
            RouteKey key = RouteKey.Of(routes[r]);
            for (int i = 0; i < batch.Count; i++) s_routeIndex[(key, batch.Ids[i])] = batch.Ticket;
        }
    }

    /// <summary>The other half of <see cref="IndexRoute"/>: free the wire slots a settling ROW batch held, so a parent
    /// blocked on it is no longer blocked. Removes only entries THIS ticket owns — a slot another, newer batch has
    /// since claimed for the same (route, parent) is never this one's to clear.</summary>
    static void ClearRouteIndex(FetchBatch batch)
    {
        if (batch.Subject == FetchSubject.Edge || s_routeIndex.Count == 0) return;
        Span<FetchRoute> routes = stackalloc FetchRoute[FetchRoutes.MaxRoutes];
        int count = FetchRoutes.For(batch, routes, out _);
        for (int r = 0; r < count; r++)
        {
            RouteKey key = RouteKey.Of(routes[r]);
            for (int i = 0; i < batch.Count; i++)
            {
                var k = (key, batch.Ids[i]);
                if (s_routeIndex.TryGetValue(k, out uint t) && t == batch.Ticket) s_routeIndex.Remove(k);
            }
        }
    }

    /// <summary>THE RE-PLAN PATH: a ROW ticket just settled (answered or failed, either way — see the file header), so
    /// every edge ask it had pre-empted is re-examined. One that the row's own answer happened to stage
    /// (<see cref="EdgeTableBase.State"/> is no longer <see cref="EdgeState.Unknown"/>) needed nothing more and stays
    /// dropped for good; one that is STILL Unknown goes back into its bucket, so the next <see cref="Pump"/> — the one
    /// at the end of <see cref="Answer"/>/<see cref="Failed"/> — really sends it. A parent recycled since (its identity
    /// moved on) or a pending entry from a scope this settle outlived (C7) is silently forgotten: nobody is showing a
    /// skeleton for either any more.</summary>
    static void SettleTicket(uint ticket)
    {
        if (!s_pendingEdges.Remove(ticket, out List<PendingEdge>? list)) return;
        for (int i = 0; i < list.Count; i++)
        {
            PendingEdge p = list[i];
            if (p.Epoch != s_scopeEpoch) continue;
            if (p.Slot <= Table.None || p.Slot >= p.Table.Count || p.Table.Id[p.Slot] != p.Id) continue;
            EdgeTableBase? edges = EdgeTableOf(Entities.Current, p.Edge);
            if (edges is null || edges.State(p.Slot) != EdgeState.Unknown) continue;
            Bucket(p.Table, FetchSubject.Edge, p.Provider, 0, p.Edge, p.Offset, p.Priority).Add(p.Slot, p.Id);
            s_toNetwork++;
        }
    }

    /// <summary>Point the whole batch at the DERIVED audio entity: each row's request uri becomes
    /// <c>spotify:audio:&lt;base62(OriginalAudio)&gt;</c> and the batch carries the kind that goes with it. UI thread,
    /// inside <see cref="Send"/>, for the same reason the text half is resolved there (C1) — the key is a column read.
    ///
    /// <para>The planned identity is re-checked against the row's CURRENT one before the key is read: a slot freed and
    /// recycled between the plan and the send would otherwise contribute another entity's audio uri to this batch. A
    /// row that fails the check (or has lost its key) gets an EMPTY uri, which every transport already drops — one
    /// missing entity in a POST, never a request for the wrong track.</para></summary>
    static void FillAudioUris(FetchBatch batch, TrackTable table, int take)
    {
        batch.Extension = AudioFilesKind;
        Span<char> uri = stackalloc char[AudioUriChars];
        for (int i = 0; i < take; i++)
        {
            int slot = batch.Slots[i];
            UInt128 audio = (uint)slot < (uint)table.Count && table.Id[slot] == batch.Ids[i]
                          ? table.OriginalAudio[slot] : UInt128.Zero;
            batch.Text[i] = WriteAudioUri(audio, uri) == 0 ? "" : new string(uri);
        }
    }

    /// <summary>A provider answered. UI THREAD ONLY (C1) — a provider on its own thread posts this.
    ///
    /// <para><paramref name="staging"/> may be null (the provider had nothing for these uris), and it is HANDED OVER
    /// either way: the commit copies out of it, the store writes it behind, and one of the two returns it to the
    /// pool. Groups the answer did not fill stay ASKED, and that is deliberate — it is the "we asked and this is the
    /// answer for now" seal that stops a thin track re-resolving on every cluster update (0.2.9's exhausted ledger rung,
    /// expressed as the bit that is already there). An EDGE batch that landed nothing records the parents as answered
    /// with no route, so a surface stops showing a skeleton for a list nobody will ever send (<c>Fetch.Edges.cs</c>).</para>
    ///
    /// <para><paramref name="unfilled"/> is the ONE exception to the seal: the groups of a route that did NOT answer
    /// beside one that did (<c>Spotify.Api.FetchOutcome.Unfilled</c> — a 401 on the top-tracks REST next to a 200
    /// overview). Those are un-asked for every row of the batch that does not know them, so the next mount or a Retry
    /// really asks again; a group no route serves is never in it and stays sealed. The batch's in-flight mark is cleared
    /// for every row it carried either way: an answer that did not name a row is still the answer that row was waiting
    /// on, and <c>Asked &amp;&amp; Inflight == 0</c> is how a surface tells "asked, nothing coming" from "loading".</para></summary>
    public static void Answer(uint ticket, Staging? staging, uint unfilled = 0)
    {
        Sync();
        if (!s_active.Remove(ticket, out FetchBatch? batch))
        {
            // A duplicate or a very late answer. Nothing owns those slots any more; returning the staging is all
            // that is left to do.
            if (staging is not null) Staging.Return(staging);
            return;
        }
        s_inFlight--;
        s_answered++;
        LogAnswer(batch, "ok", unfilled & batch.Wanted, retry: null);

        if (staging is not null)
        {
            if (staging.Epoch == 0) staging.Epoch = batch.Epoch;      // the provider may not have stamped it
            Entities.Commit(staging);                                  // drops the batch whole if the scope moved (C7)
            if (!Store.WriteBehind(staging)) Staging.Return(staging);  // write-behind takes ownership when it accepts
        }
        if (batch.Epoch == s_scopeEpoch)
        {
            if (batch.Subject == FetchSubject.Edge) EdgesAnswered(batch);
            else if (TableOf(Entities.Current, batch) is { } table) Unask(batch, table, unfilled & batch.Wanted);
        }

        // Free this batch's wire slots and re-plan whatever they were blocking (see the file header) — AFTER the
        // commit above, so a row answer that staged the relation as a side effect is not re-asked for it.
        ClearRouteIndex(batch);
        SettleTicket(ticket);

        Recycle(batch);
        Settled.Value = Settled.Peek() + 1;
        Pump();
    }

    /// <summary>A provider could not answer. UI THREAD ONLY.
    ///
    /// <para>A transport failure SEALS NOTHING (0.2.9 <c>HydrationLedger</c>: "a transport error is not an answer"):
    /// a terminal failure un-asks the batch's groups so the next page mount really retries. A retryable one keeps the
    /// marks and re-queues the batch behind <see cref="Backoff"/> — keeping the marks is what stops a second plan from
    /// queueing the same rows a second time while the first is sleeping.</para></summary>
    /// <param name="status">The HTTP status, or 0 for a transport error with none.</param>
    /// <param name="retryAfterSeconds">The server's <c>Retry-After</c>, if it sent one. Clamped by the backoff.</param>
    public static void Failed(uint ticket, int status, int retryAfterSeconds)
    {
        Sync();                       // a failure for a scope that has been replaced must not re-queue into the new one
        if (!s_active.Remove(ticket, out FetchBatch? batch)) return;
        s_inFlight--;
        s_failed++;

        bool live = batch.Epoch == s_scopeEpoch;
        Table? table = live ? TableOf(Entities.Current, batch) : null;
        bool retry = table is not null && Retryable(status) && batch.Attempt + 1 < MaxAttempts;
        if (Log.IsEnabled(WaveeLogLevel.Info))
            LogAnswer(batch, status.ToString(System.Globalization.CultureInfo.InvariantCulture), batch.Wanted, retry);
        if (retry)
        {
            Demand d = Bucket(table!, batch.Subject, batch.Provider, batch.Wanted, batch.Edge, batch.Offset, batch.Priority);
            for (int i = 0; i < batch.Count; i++) d.Add(batch.Slots[i], batch.Ids[i]);
            d.Attempt = batch.Attempt + 1;
            d.ReadyAt = Entities.Now + Backoff(batch.Attempt, status, retryAfterSeconds);
            s_retried++;
        }
        else
        {
            if (table is not null)
            {
                if (batch.Subject == FetchSubject.Edge) EdgesFailed(batch, table, status);
                else { Unask(batch, table, batch.Wanted); MarkFailed(batch, table); }
                // An authentication refusal is the one terminal failure the session itself will answer: remember what
                // was un-asked so the next Online transition re-plans it (`Resume`) — the page has already mounted
                // and nothing else will ask again.
                if (global::Wavee.Queue.SeedRetryOn(status) == global::Wavee.Queue.SeedRetryDecision.RetryOnline)
                    Refuse(batch, table);
            }
            s_abandoned++;
        }

        // The blocked ask must never wait on a backoff that was never its own (retryable or not): the wire slot this
        // ticket held is free the moment it settles, so whatever it pre-empted gets its turn now.
        ClearRouteIndex(batch);
        SettleTicket(ticket);

        Recycle(batch);
        Settled.Value = Settled.Peek() + 1;
        Pump();
    }

    /// <summary>Settle a row batch's marks: the in-flight mark goes for every row it carried, and so do the
    /// <paramref name="groups"/> a row does not know — the whole batch's groups for a terminal failure (the marks are
    /// what suppress the next request, and a failure must not suppress it), the groups of the routes that did not
    /// answer for an answer (<see cref="Answer"/>'s <c>unfilled</c>), nothing for a clean one. A group the row DOES know
    /// stays asked whatever the caller says: a route that failed for a group another route filled changes nothing. A
    /// slot recycled since the plan (its identity moved on) is not this batch's to touch.</summary>
    static void Unask(FetchBatch batch, Table table, uint groups)
    {
        uint stamp = Stamp(batch.Epoch);
        for (int i = 0; i < batch.Count; i++)
        {
            int slot = batch.Slots[i];
            if (slot <= Table.None || slot >= table.Count || table.Id[slot] != batch.Ids[i]) continue;
            if (table.Inflight[slot] == stamp) table.Inflight[slot] = 0;
            uint unask = groups & ~table.Settled(slot);
            if (unask != 0) table.Asked[slot] &= ~unask;
        }
    }

    /// <summary>Record the terminal failure ON THE ROWS (<c>Table.Failed</c>), so a surface can tell "still coming" from
    /// "the ask failed" instead of shimmering forever — the row twin of <c>EdgeTable</c>'s failure. Only the terminal arm
    /// of <see cref="Failed"/> calls it: a retryable 429/503 re-queues the same rows and is a wait, not a failure. A group
    /// the row already KNOWS is not marked (a route that failed for a group another route filled changes nothing), and a
    /// slot recycled since the plan is not this batch's to touch. The table is marked dirty, so its <c>Changed</c> signal
    /// bumps at the next publication and the panes' readiness memos re-run.</summary>
    static void MarkFailed(FetchBatch batch, Table table)
    {
        bool any = false;
        for (int i = 0; i < batch.Count; i++)
        {
            int slot = batch.Slots[i];
            if (slot <= Table.None || slot >= table.Count || table.Id[slot] != batch.Ids[i]) continue;
            uint groups = batch.Wanted & ~table.Settled(slot);
            if (groups == 0) continue;
            table.Failed[slot] |= groups;
            any = true;
        }
        if (any) table.MarkDirty();
    }

    /// <summary>Remember a terminally refused batch for <see cref="Resume"/>: one entry per row (or parent) that still
    /// names the identity the batch asked for. Bounded by <see cref="MaxRefused"/>, oldest out.</summary>
    static void Refuse(FetchBatch batch, Table table)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            int slot = batch.Slots[i];
            if (slot <= Table.None || slot >= table.Count || table.Id[slot] != batch.Ids[i]) continue;
            if (s_refused.Count >= MaxRefused) s_refused.RemoveAt(0);
            s_refused.Add(new RefusedAsk(table, slot, batch.Ids[i], batch.Wanted, batch.Edge, batch.Offset, batch.Priority, batch.Epoch));
        }
    }

    /// <summary>The session is Online again: plan every ask an auth refusal un-asked, then forget them. UI THREAD, from
    /// <c>Spotify.Library.SyncNow</c>'s Online path. An entry from a replaced scope, or whose slot has since been
    /// recycled to another identity, is dropped — the same guards <see cref="SettleTicket"/> applies to a pending edge.
    /// Entries recorded from one batch are contiguous and share a shape, so they go back through <see cref="Plan"/> /
    /// <see cref="PlanEdge"/> a run at a time — one bucket, one request, not one request per row. Like every plan they
    /// owe the tick's drain rather than sending here; the caller's <see cref="Pump"/> (the Online transition's) sends
    /// them together with whatever the gate was holding.</summary>
    public static void Resume()
    {
        Sync();
        if (s_refused.Count == 0) return;
        Scope scope = Entities.Current;
        int count = s_refused.Count;
        RefusedAsk[] pending = ArrayPool<RefusedAsk>.Shared.Rent(count);
        int[] slots = ArrayPool<int>.Shared.Rent(count);
        s_refused.CopyTo(pending);
        s_refused.Clear();                 // a plan below that fails synchronously may record again; it must not loop here
        try
        {
            int i = 0;
            while (i < count)
            {
                RefusedAsk head = pending[i];
                int n = 0;
                int j = i;
                for (; j < count; j++)
                {
                    RefusedAsk p = pending[j];
                    if (!ReferenceEquals(p.Table, head.Table) || p.Groups != head.Groups || p.Edge != head.Edge
                        || p.Offset != head.Offset || p.Priority != head.Priority || p.Epoch != head.Epoch) break;
                    if (p.Epoch != s_scopeEpoch) continue;
                    if (p.Slot <= Table.None || p.Slot >= p.Table.Count || p.Table.Id[p.Slot] != p.Id) continue;
                    slots[n++] = p.Slot;
                }
                i = j;
                if (n == 0) continue;
                s_resumed += n;
                if (head.Edge != FetchEdge.None) PlanEdge(scope, head.Edge, slots.AsSpan(0, n), head.Offset, head.Priority);
                else Plan(scope, head.Table, slots.AsSpan(0, n), head.Groups, head.Priority);
            }
        }
        finally
        {
            ArrayPool<RefusedAsk>.Shared.Return(pending, clearArray: true);   // holds table references
            ArrayPool<int>.Shared.Return(slots);
        }
    }

    /// <summary>Ask these groups AGAIN, whatever the scope has sealed — the row twin of <see cref="PlanEdge"/>'s
    /// <c>refresh</c>, and what a Retry vacancy wants: the <c>Asked</c> bits go, then <see cref="Plan"/> asks for
    /// whatever is still not settled. A group the row knows is not re-fetched (the rows there keep rendering);
    /// a group whose request is out right now is asked a second time, which is what "refresh" means. Its rollover
    /// twin is <see cref="Invalidate"/>, which re-asks what IS known.</summary>
    public static void Refresh(Scope scope, Table table, ReadOnlySpan<int> slots, uint groups, FetchPriority priority)
    {
        if (groups == 0 || slots.IsEmpty) return;
        DropStaleScope(scope);
        for (int i = 0; i < slots.Length; i++)
        {
            int slot = slots[i];
            if (slot <= Table.None || slot >= table.Count) continue;
            table.Asked[slot] &= ~groups;
        }
        Plan(scope, table, slots, groups, priority);
    }

    /// <summary>Mark these KNOWN groups as belonging to an ended edition and ask for them again (the fifth mark): the
    /// rows keep rendering their values while the new edition is on the wire. The rollover twin of
    /// <see cref="Refresh"/>: Refresh re-asks what is NOT known, this re-asks what IS. Per slot: <c>Stale |= groups &amp;
    /// Known</c>, the <c>Asked</c> bits go (the seal must not hold the old edition in place), the version bumps so a
    /// bound row re-reads; then <see cref="Plan"/>, which finds the groups unsettled. Without a route or a provider the
    /// rows simply stay stale-and-rendering, which is the point. UI thread (C1).</summary>
    public static void Invalidate(Scope scope, Table table, ReadOnlySpan<int> slots, uint groups, FetchPriority priority)
    {
        if (groups == 0 || slots.IsEmpty) return;
        DropStaleScope(scope);
        bool any = false;
        for (int i = 0; i < slots.Length; i++)
        {
            int slot = slots[i];
            if (slot <= Table.None || slot >= table.Count) continue;
            table.Stale[slot] |= groups & table.Known[slot];
            table.Asked[slot] &= ~groups;
            table.Version[slot]++;
            any = true;
        }
        if (!any) return;
        table.MarkDirty();
        Plan(scope, table, slots, groups, priority);
    }

    /// <summary>The table a batch's rows index in the CURRENT scope — by subject, since four tables share a kind; for an
    /// edge batch, the relation's PARENT table.</summary>
    static Table? TableOf(Scope scope, FetchBatch batch) => batch.Subject switch
    {
        FetchSubject.Entity => Entities.TableFor(batch.Kind),
        FetchSubject.Home => scope.Homes,
        FetchSubject.HomeSection or FetchSubject.BrowseSection => scope.Sections,
        FetchSubject.Search => scope.Searches,
        FetchSubject.BrowseDirectory or FetchSubject.BrowsePage => scope.Browses,
        FetchSubject.Edge => ParentTableOf(scope, batch.Edge),
        _ => null,
    };

    /// <summary>The held revision of each parent's list, for the relations that have one (UI thread, inside
    /// <see cref="Send"/>, C1). A playlist's and the rootlist's survive a restart WITH their lists (Store.Lists.cs):
    /// the edge door's disk leg restores a playlist Complete with its revision before its network ask, and the warm
    /// restores the rootlist's, so the first ask of a launch is the <c>/diff</c>. A playlist's is sent only while its
    /// membership is Complete — a revision vouches for the rows it came with, never for a window. The rootlist's needs no
    /// such guard: it is only ever set beside a Complete rewrite (<c>Entities.CommitRootlist</c>, wire or disk) or by a
    /// confirmed write to a list that is already there.</summary>
    static void FillRevisions(FetchBatch batch, FetchEdge edge, int take)
    {
        Edges edges = Entities.Current.Edges;
        for (int i = 0; i < take; i++)
        {
            if (edge == FetchEdge.AlbumSimilar)
            {
                // Seeded by a TRACK while the relation hangs off the ALBUM; the provider cannot read tables (WP-5.M).
                batch.Revisions[i] = global::Wavee.Album.SimilarSeedUri(batch.Slots[i]);
                continue;
            }
            // The five persisted library relations carry their collection-v2 SYNC TOKEN rather than a revision from a
            // table: the token is durable across launches (a `meta` row, deliberately not a column — a column is DDL,
            // the DDL's fingerprint NAMES the file, and a new one moves every install onto a fresh, empty
            // `library.<fingerprint>.db` once: `Store.FileName`), so it comes out of the store's warmed in-memory map.
            // `MetaGet` is a plain dictionary lookup, never sqlite, so it is safe on this thread. A null answer is the
            // default-safe branch: the provider then walks the set in full.
            if (FetchRoutes.LibrarySetOf(edge, out LibraryEdgeKind librarySet))
            {
                batch.Revisions[i] = Store.MetaGet(Spotify.Api.CollectionMetaKey(Entities.Current.Key.Account, librarySet));
                continue;
            }
            StringId revision = edge switch
            {
                FetchEdge.Rootlist => edges.RootlistRevision(batch.Slots[i]),
                FetchEdge.ShowEpisodes when edges.ShowEpisodes.State(batch.Slots[i]) == EdgeState.Complete => Entities.Current.Shows.ListRevision[batch.Slots[i]],
                FetchEdge.Recents => edges.RecentsRevision(batch.Slots[i]),
                FetchEdge.PlaylistTracks when edges.PlaylistTracks.State(batch.Slots[i]) == EdgeState.Complete
                    => new global::Wavee.Playlist(batch.Slots[i]).RevisionId,
                _ => StringId.Empty,
            };
            batch.Revisions[i] = revision.IsEmpty ? null : Entities.Strings.Resolve(revision);
        }
    }

    static void Recycle(FetchBatch batch)
    {
        batch.Count = 0;
        batch.Extension = 0;
        batch.Edge = FetchEdge.None;
        batch.Offset = 0;
        Array.Clear(batch.Text);                 // do not pin interned strings in a pooled buffer (the ids are values)
        Array.Clear(batch.Revisions);
        Array.Clear(batch.Baselines);             // a pooled batch pins no list
        if (s_pool.Count < 8) s_pool.Push(batch);
    }
}

// ── the Entities hooks ───────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The two seams <c>Entities.cs</c> declares for the planner. Partial methods, so a build without this file
/// has an in-memory graph that never fetches — which is exactly what the seed (<c>--fake</c>) and the unit tests
/// want, and why neither needs a stub transport.</summary>
public static partial class Entities
{
    static partial void FetchBoot() => Fetch.Boot();

    static partial void PlanFetch(Table table, ReadOnlySpan<int> slots, uint wanted, FetchPriority priority)
        => Fetch.Plan(Current, table, slots, wanted, priority);

    /// <summary>Ask these groups AGAIN, whatever was asked before — the row twin of <see cref="RefreshEdge"/>, and what a
    /// Retry vacancy calls: a group the scope sealed (asked, never filled) is un-asked and planned; a group the rows
    /// already know is left alone. The typed <c>Ensure</c> sugar has no refresh twin on purpose — a refresh is a
    /// deliberate, rare act (a Retry, a reconnect), never a page mount's.</summary>
    public static void Refresh(Table table, ReadOnlySpan<int> slots, uint wanted, FetchPriority priority = FetchPriority.Visible)
        => Fetch.Refresh(Current, table, slots, wanted, priority);

    /// <summary>These rows' KNOWN groups belong to an ended edition: mark them stale and ask again, while the rows keep
    /// rendering what they have (<see cref="Fetch.Invalidate"/>). The rollover twin of <see cref="Refresh"/> — Refresh
    /// re-asks what is not known, this re-asks what is. Called from a wall-clock fact (a daylist's rollover, a chart's
    /// week), never from a page mount.</summary>
    public static void Invalidate(Table table, ReadOnlySpan<int> slots, uint groups, FetchPriority priority = FetchPriority.Visible)
        => Fetch.Invalidate(Current, table, slots, groups, priority);

    /// <inheritdoc cref="Invalidate(Table,ReadOnlySpan{int},uint,FetchPriority)"/>
    public static void Invalidate(Playlist row, PlaylistFields groups, FetchPriority priority = FetchPriority.Visible)
    {
        int slot = row.Slot;
        Fetch.Invalidate(Current, Current.Playlists, new ReadOnlySpan<int>(in slot), (uint)groups, priority);
    }
}
