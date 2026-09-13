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
// THE FOUR MARKS, and what each one means — they are the whole state machine, and there is no other:
//   `Asked[slot] & group`      somebody asked for this GROUP of this row in this scope (G-040). Set when a plan marks
//                              the row; it SURVIVES the answer — a group asked and not answered is the "exhausted"
//                              seal 0.2.9's ledger needed a second cache to hold, and it is what stops a TrackV4 answer
//                              (which fills Identity and not PlayCount) from being re-POSTed by the next `Ensure(Row)`.
//                              A TERMINAL transport failure un-asks the batch's groups (a 503 is not an answer); the
//                              scope being replaced drops every mark with its tables.
//   `Inflight[slot] == stamp`  a request for this row is out right now. `Table.Applied` clears it when a group lands;
//                              the store's trim reads it as "pinned". It no longer gates the dedupe — `Asked` does.
//   `FetchedAt[slot] != 0`     somebody has answered about this row before — including the disk answering "I do not
//                              have it" (Store.ReadCore stamps the whole batch). It is what makes the disk leg run
//                              ONCE per row per session instead of once per page mount.
//   `Known[slot] & group`      the group is filled. Nothing else is a hydration level, and there are no others.
//
// A BUCKET IS (provider, subject, kind, need, priority). `need` is the per-row `wanted & ~known & ~asked` — not the
// page's `wanted` — so a row whose Identity is already in flight and whose PlayCount is not asks for PlayCount alone,
// and a partial answer never re-asks what it already answered. Rows of one `Ensure` nearly always share one need.
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
    /// <summary>The extended-metadata kind this batch asks for, or 0 for "the kind's own routes" (a provider maps that
    /// through <see cref="FetchRoutes"/>). Non-zero for exactly one group today: <see cref="Fetch.AudioFilesKind"/> = 5,
    /// the FLAC ladder, whose request is a <c>spotify:audio:</c> uri in <see cref="Text"/> and whose ANSWER is keyed by
    /// that same uri — so a provider maps the answer back to the row it asked for with <see cref="IdFor"/> (plan §5.2).</summary>
    public int Extension;

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

    /// <summary>A row's NEED: the groups of <paramref name="wanted"/> it does not know and nobody has asked for yet
    /// this scope. The one expression the whole dedupe is (G-040).</summary>
    public static uint NeedOf(Table table, int slot, uint wanted) => wanted & ~table.Known[slot] & ~table.Asked[slot];

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

    /// <summary>One (provider, subject, kind, need, priority) bucket of rows waiting to go out — or, for an edge,
    /// (provider, relation, offset, priority) of PARENTS. That tuple IS the "shape" of a request: everything in a
    /// bucket can ride the same request, and nothing outside it can.</summary>
    sealed class Demand
    {
        public EntityProvider Provider;
        public EntityKind Kind;
        public FetchSubject Subject;
        public uint Wanted;
        public FetchEdge Edge;
        public int Offset;
        public FetchPriority Priority;
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
    // one word: provider | subject | kind | priority | edge in the first, need-or-offset in the second.
    static readonly Dictionary<(long, long), Demand> s_buckets = new(16);
    static readonly List<Demand> s_order = new(16);                 // stable iteration; the dictionary is the index
    static readonly Dictionary<uint, FetchBatch> s_active = new(8);
    static readonly Stack<FetchBatch> s_pool = new(8);
    static readonly FetchProvider?[] s_providers = new FetchProvider?[8];   // indexed by (byte)EntityProvider
    static uint s_ticket;
    static int s_inFlight;
    static uint s_scopeEpoch;

    static int s_planned, s_deduped, s_toDisk, s_toNetwork, s_answered, s_failed, s_retried, s_abandoned;

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
        s_inFlight = 0;
        // Boot may run before `Entities.Boot` has a scope (a test calling Reset): read defensively.
        Scope? current = Entities.Current;
        s_scopeEpoch = current is null ? 0u : current.Epoch;
    }

    /// <summary>Attach a transport. One per provider; the last one wins, so a test can replace the live session.</summary>
    public static void Register(FetchProvider provider) => s_providers[(byte)provider.Provider] = provider;

    /// <summary>Detach every transport and forget every pending row — the test seam, and what a sign-out runs.</summary>
    public static void Reset()
    {
        Boot();
        Array.Clear(s_providers);
        s_planned = s_deduped = s_toDisk = s_toNetwork = s_answered = s_failed = s_retried = s_abandoned = 0;
    }

    // ── the planner ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"These rows, these groups." The one entry point for rows (through <c>Entities.Ensure</c>), and the
    /// whole of §5.4's access path:
    /// <list type="number">
    /// <item>filter to the rows that NEED something — missing and not yet asked for (<see cref="Select"/>);</item>
    /// <item>mark those groups asked for this scope, so the other nine pages asking this drain ask for nothing (C7);</item>
    /// <item>split disk / network on "has anything ever answered about this row" and send the disk leg first;</item>
    /// <item>bucket the rest by (provider, subject, kind, need, priority) and pump.</item>
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
            if (disk > 0 && Store.Read(scope, table, span[..disk], diskWanted, priority))
            {
                s_toDisk += disk;
                Queue(scope, table, span[disk..], groupsOf[disk..], priority);   // the rest do not wait for the disk
            }
            else
            {
                Queue(scope, table, span, groupsOf, priority);                   // no store (or it refused): straight out
            }
            Pump();
        }
        finally
        {
            ArrayPool<int>.Shared.Return(scratch);
            ArrayPool<uint>.Shared.Return(needs);
        }
    }

    /// <summary>The disk answered; whatever it could not fill goes to the provider. Called by <c>Store</c> from the
    /// cold read's completion, on the UI thread, with the same slots and groups the read was asked for.
    /// <para>It re-derives <c>wanted &amp; ~known</c> rather than trusting the caller's list, so a batch the disk
    /// filled completely asks for nothing — and it does NOT subtract <c>Asked</c>, because those marks are this plan's
    /// own.</para></summary>
    public static void Continue(Scope scope, Table table, ReadOnlySpan<int> slots, uint wanted, FetchPriority priority)
    {
        if (wanted == 0 || slots.IsEmpty) return;
        if (!ReferenceEquals(scope, Entities.Current)) return;   // C7: the disk answered into a replaced set

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
                uint groups = wanted & ~table.Known[slot] & table.Asked[slot];
                if (groups == 0) continue;                  // the disk had it; `Applied` already cleared the in-flight mark
                scratch[n] = slot;
                needs[n++] = groups;
            }
            if (n == 0) return;
            Queue(scope, table, scratch.AsSpan(0, n), needs.AsSpan(0, n), priority);
            Pump();
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
    /// subject nobody owns, which is the scope's catalogue's (<see cref="ProviderFor"/>, G-041).</para></summary>
    static void Queue(Scope scope, Table table, ReadOnlySpan<int> slots, ReadOnlySpan<uint> needs, FetchPriority priority)
    {
        var tracks = table as TrackTable;
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
            }
            if (derived == 0) continue;

            if (tracks!.OriginalAudio[slot] != UInt128.Zero)
            {
                Bucket(table, subject, provider, derived, FetchEdge.None, 0, priority).Add(slot, id);
                s_toNetwork++;
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

    static Demand Bucket(Table table, FetchSubject subject, EntityProvider provider, uint wanted, FetchEdge edge, int offset,
                         FetchPriority priority)
    {
        long shape = ((long)(byte)provider << 32) | ((long)(byte)subject << 24) | ((long)(byte)table.Kind << 16)
                   | ((long)(byte)priority << 8) | (byte)edge;
        long payload = edge == FetchEdge.None ? wanted : offset;
        if (s_buckets.TryGetValue((shape, payload), out Demand? d))
        {
            Debug.Assert(ReferenceEquals(d.Table, table), "one bucket, one table: the key packs the subject and the kind, so this can only differ across a scope switch that was not dropped.");
            return d;
        }
        d = new Demand
        {
            Provider = provider, Kind = table.Kind, Subject = subject, Wanted = edge == FetchEdge.None ? wanted : 0,
            Edge = edge, Offset = offset, Priority = priority, Table = table,
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
        for (int i = 0; i < s_providers.Length; i++) s_providers[i]?.Abandon(scope.Epoch);
        // In-flight batches are NOT cancelled here: their answers are dropped by `Staging.Epoch` at the commit (C7),
        // and forgetting the tickets would leak the slots in `s_active`.
    }

    // ── the runner (SHELL) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Send what can be sent: at most <see cref="MaxInFlight"/> requests, highest priority first, at most
    /// <see cref="MaxUrisPerRequest"/> uris each. Idempotent and cheap (one pass over at most a handful of buckets),
    /// and called by every door in this file — a plan, an answer, a failure.
    /// <para>A backoff that expires with nothing else happening has no timer of its own: the HOST calls this on its
    /// frame tick, which is the only clock this layer is allowed to have (P10 — timers are named, few, and owned by
    /// the shell). Without that call a sleeping bucket waits for the next plan, which is a delay, never a loss.</para></summary>
    public static void Pump()
    {
        Sync();
        int now = Entities.Now;
        while (s_inFlight < MaxInFlight)
        {
            Demand? best = null;
            for (int i = 0; i < s_order.Count; i++)
            {
                Demand d = s_order[i];
                if (d.Count == 0 || d.ReadyAt > now) continue;
                if (s_providers[(byte)d.Provider] is null) continue;
                // Playback beats Visible beats Prefetch. Within a priority, first-come — a bucket that has been
                // waiting is a page that has been showing skeletons.
                if (best is null || d.Priority > best.Priority) best = d;
            }
            if (best is null) return;
            Send(best);
        }
    }

    static void Send(Demand d)
    {
        int take = Math.Min(MaxUrisPerRequest, d.Count);
        FetchBatch batch = s_pool.Count > 0 ? s_pool.Pop() : new FetchBatch();
        if (batch.Ids.Length < take)
        {
            batch.Ids = new EntityId[take];
            batch.Text = new string?[take];
            batch.Slots = new int[take];
            batch.Revisions = new string?[take];
        }
        Array.Copy(d.Ids, batch.Ids, take);
        Array.Copy(d.Slots, batch.Slots, take);
        // The text half is resolved HERE because Send runs on the UI thread and the provider's does not (C1): an id
        // may only be resolved while it is alive, and the batch outlives that guarantee. A gid row has no text at all,
        // which is nearly every row of a catalog batch — so this loop touches the interner for almost none of them.
        for (int i = 0; i < take; i++)
        {
            EntityId id = batch.Ids[i];
            batch.Text[i] = id.Form == EntityForm.Text ? Entities.Strings.Resolve(id.TextId) : null;
        }
        batch.Extension = 0;
        if (IsAudioFiles(d.Subject, d.Kind, d.Wanted)) FillAudioUris(batch, (TrackTable)d.Table, take);
        if (d.Subject == FetchSubject.Edge) FillRevisions(batch, d.Edge, take);
        batch.Count = take;
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
        d.Drop(take);

        s_active[batch.Ticket] = batch;
        s_inFlight++;
        FetchProvider provider = s_providers[(byte)d.Provider]!;
        try { provider.Start(batch); }
        catch (Exception)
        {
            // A transport that throws synchronously has not taken the batch: settle it here or the slots stay in
            // flight for the life of the scope.
            Failed(batch.Ticket, 0, 0);
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
    /// with no route, so a surface stops showing a skeleton for a list nobody will ever send (<c>Fetch.Edges.cs</c>).</para></summary>
    public static void Answer(uint ticket, Staging? staging)
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

        if (staging is not null)
        {
            if (staging.Epoch == 0) staging.Epoch = batch.Epoch;      // the provider may not have stamped it
            Entities.Commit(staging);                                  // drops the batch whole if the scope moved (C7)
            if (!Store.WriteBehind(staging)) Staging.Return(staging);  // write-behind takes ownership when it accepts
        }
        if (batch.Subject == FetchSubject.Edge && batch.Epoch == s_scopeEpoch) EdgesAnswered(batch);

        Recycle(batch);
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
                else Unask(batch, table);
            }
            s_abandoned++;
        }

        Recycle(batch);
        Pump();
    }

    /// <summary>Un-ask a terminally failed row batch: the in-flight mark goes, and so do the batch's groups — the marks
    /// are what suppress the next request, and a failure must not suppress it. A slot recycled since the plan (its
    /// identity moved on) is not this batch's to touch.</summary>
    static void Unask(FetchBatch batch, Table table)
    {
        uint stamp = Stamp(batch.Epoch);
        for (int i = 0; i < batch.Count; i++)
        {
            int slot = batch.Slots[i];
            if (slot <= Table.None || slot >= table.Count || table.Id[slot] != batch.Ids[i]) continue;
            if (table.Inflight[slot] == stamp) table.Inflight[slot] = 0;
            table.Asked[slot] &= ~batch.Wanted;
        }
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
    /// <see cref="Send"/>, C1).</summary>
    static void FillRevisions(FetchBatch batch, FetchEdge edge, int take)
    {
        Edges edges = Entities.Current.Edges;
        for (int i = 0; i < take; i++)
        {
            StringId revision = edge switch
            {
                FetchEdge.Rootlist => edges.RootlistRevision(batch.Slots[i]),
                FetchEdge.Recents => edges.RecentsRevision(batch.Slots[i]),
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
}
