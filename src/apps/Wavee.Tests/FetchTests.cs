// ── Wavee.Tests/FetchTests.cs — the planner: dedupe, disk before network, batching, backoff, the epoch drop ────────
//
// Wave 1's gate for Entities/Fetch.cs (plan §5's "Plan dedupe", §4.15's `Second_plan_in_same_epoch_asks_nothing`).
// These are the tests the 0.2.9 hydration path could not have: "have we already asked?" was spread across a ledger,
// a coordinator, six negative memos and a per-uri Resource, so the question had four answers and no assertion could
// name one. Here it is `wanted & ~known & ~asked` over two columns, and every fact below is a column read.
//
// The rows are REAL rows in the current scope's TrackTable, driven through the base `Table` surface (`Slot`, `Known`,
// `Applied`) — no mock, no fake table. The only stand-in is the transport, because a test that opened a socket would
// be testing the socket.
//
// SINCE 2026-09-12 (docs/plans/wavee/wavee-0.3-entity-identity-memory.md, option 2) a row is named by a packed
// 24-byte `EntityId`, and three of the facts below are about what that changed here:
//   · a batch crosses to the provider as IDENTITIES — 16 gid bytes for the six catalog kinds, which is what the wire
//     wanted all along, where 0.2.9 encoded them into a uri string (569 ns) that the next layer hashed back;
//   · the text half is resolved on the UI THREAD, once, for the rows that actually have text, because a provider
//     thread may not resolve an id it might outlive;
//   · and the whole 300-row path allocates NOTHING after warm-up — asserted, not asserted-about, because "the planner
//     allocates nothing per call" (P8) is the kind of claim that rots silently.

using System.Collections.Concurrent;
using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>A transport that records what it was handed and answers nothing until the test says so.</summary>
sealed class RecordingProvider(EntityProvider provider) : FetchProvider
{
    public override EntityProvider Provider { get; } = provider;

    /// <summary>What each batch carried, captured BY VALUE: the runner pools and recycles the batch object the
    /// moment it settles, so holding the reference would be holding a lie.</summary>
    public readonly List<(uint Ticket, int Count, uint Wanted, FetchPriority Priority, int Attempt)> Seen = new();

    public override void Start(FetchBatch batch)
        => Seen.Add((batch.Ticket, batch.Count, batch.Wanted, batch.Priority, batch.Attempt));
}

/// <summary>A transport that keeps the batch OBJECT, for the facts that read its identities. Legal only until the
/// batch settles — that is when the runner recycles it into the pool — so the tests below never settle theirs.</summary>
sealed class HoldingProvider(EntityProvider provider) : FetchProvider
{
    public override EntityProvider Provider { get; } = provider;
    public FetchBatch? Last;
    public int Batches;

    public override void Start(FetchBatch batch)
    {
        Last = batch;
        Batches++;
    }
}

/// <summary>A transport for the ALLOCATION probe: it counts, and it does nothing that could allocate. (A
/// <c>List&lt;T&gt;.Add</c> grows, and a growing list inside the measured window would be measuring the test.)</summary>
sealed class CountingProvider(EntityProvider provider) : FetchProvider
{
    public override EntityProvider Provider { get; } = provider;
    public int Batches;
    public int Rows;
    public uint LastTicket;

    public override void Start(FetchBatch batch)
    {
        Batches++;
        Rows += batch.Count;
        LastTicket = batch.Ticket;
    }
}

[Collection(EntitiesCollection.Name)]
public class FetchTests : IDisposable
{
    const uint Identity = 1u << 0;
    const uint Extras = 1u << 1;

    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-v3-fetch-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();

    public FetchTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);                                   // memory-only unless a test asks for a file
        Store.Post = static a => a();
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    static Scope Boot()
    {
        Entities.Boot(CatalogScope.Fake());
        return Entities.Current;
    }

    /// <summary>TEXT-form rows: a 32-hex-character id is not a gid, so these keep a uri string and a dictionary
    /// entry. Most of the facts in this file do not care which form they run over — deliberately.</summary>
    static int[] Rows(Table table, int count, string prefix = "spotify:track:")
    {
        var slots = new int[count];
        for (int i = 0; i < count; i++) slots[i] = table.Slot((prefix + Guid.NewGuid().ToString("n")).AsSpan());
        return slots;
    }

    /// <summary>GID-form rows: <c>spotify:track:</c> + 22 base62 characters, which is what a real catalog batch is
    /// made of and the only form with no uri string anywhere in the process.</summary>
    static int[] GidRows(Table table, int count, int seed)
    {
        var slots = new int[count];
        Span<char> uri = stackalloc char["spotify:track:".Length + Base62.GidChars];
        "spotify:track:".AsSpan().CopyTo(uri);
        for (int i = 0; i < count; i++)
        {
            ulong n = (ulong)(seed + i);
            Base62.Encode(new UInt128(n * 0x9E37_79B9_7F4A_7C15UL + 11, n * 0xC2B2_AE3D_27D4_EB4FUL + 3),
                          uri["spotify:track:".Length..]);
            slots[i] = table.Slot(uri);
        }
        return slots;
    }

    void DrainPosts()
    {
        while (_posted.TryDequeue(out Action? a)) a();
    }

    // ── the dedupe (C7) ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Second_plan_in_the_same_epoch_asks_nothing()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] slots = Rows(t, 5);

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);

        // Ten pages asking for the same rows in one drain produce ONE request. That sentence is C7, and this is it.
        Assert.Single(provider.Seen);
        Assert.Equal(5, provider.Seen[0].Count);
        Assert.Equal(5, Fetch.Deduped);
    }

    [Fact]
    public void A_row_that_already_knows_the_group_is_never_planned()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] slots = Rows(t, 3);
        t.Known[slots[0]] |= Identity;
        t.Known[slots[1]] |= Identity;

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);

        Assert.Single(provider.Seen);
        Assert.Equal(1, provider.Seen[0].Count);
    }

    [Fact]
    public void Asking_for_a_group_a_row_does_not_have_still_plans_it()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] slots = Rows(t, 1);
        t.Known[slots[0]] |= Identity;

        Fetch.Plan(scope, t, slots, Identity | Extras, FetchPriority.Visible);

        Assert.Single(provider.Seen);                       // Extras is missing, so the row goes out
    }

    [Fact]
    public void Select_is_a_pure_decision_and_marks_nothing()
    {
        Scope scope = Boot();
        Table t = scope.Tracks;
        int[] slots = Rows(t, 4);
        t.Known[slots[2]] |= Identity;
        Span<int> dst = stackalloc int[8];

        int n = Fetch.Select(t, slots, Identity, dst);

        Assert.Equal(3, n);
        for (int i = 0; i < slots.Length; i++)
        {
            Assert.Equal(0u, t.Inflight[slots[i]]);                                          // nothing was marked
            Assert.Equal(0u, t.Asked[slots[i]]);
        }
    }

    [Fact]
    public void The_dedupe_stamp_is_never_zero_so_the_first_scope_dedupes_too()
    {
        // `Inflight == 0` means "nobody is asking" and the first scope's epoch IS 0. Without the +1 every row would
        // re-request on every plan, forever, silently.
        Assert.NotEqual(0u, Fetch.Stamp(0));
        Assert.NotEqual(Fetch.Stamp(0), Fetch.Stamp(1));
    }

    // ── disk before network ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_row_nobody_has_answered_for_goes_to_the_disk_first_and_to_the_provider_after()
    {
        Store.Use(_dbPath);
        Store.Post = a => _posted.Enqueue(a);
        Store.Register(new FetchProbeShape());
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] slots = Rows(t, 3);

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);

        // Leg one: the disk, and NOTHING on the wire yet.
        Assert.Equal(3, Fetch.ToDisk);
        Assert.Equal(0, Fetch.ToNetwork);
        Assert.Empty(provider.Seen);

        Store.Flush();
        DrainPosts();

        // Leg two: the disk had nothing, so the same rows continue to the provider — in the same plan, without a
        // second Ensure and without clearing the in-flight marks in between.
        Assert.Equal(3, Fetch.ToNetwork);
        Assert.Single(provider.Seen);
        Assert.Equal(3, provider.Seen[0].Count);
    }

    [Fact]
    public void A_row_the_disk_has_already_been_asked_about_goes_straight_to_the_provider()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] slots = Rows(t, 2);
        // What Store's cold read stamps on every slot in a batch, found or not: "somebody has answered about this
        // row" — the negative memo that keeps the disk leg to once per row per session.
        Entities.Now = 500;
        t.FetchedAt[slots[0]] = 500;
        t.FetchedAt[slots[1]] = 500;

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);

        Assert.Equal(0, Fetch.ToDisk);
        Assert.Equal(2, Fetch.ToNetwork);
        Assert.Single(provider.Seen);
    }

    [Fact]
    public void With_no_store_everything_goes_straight_to_the_provider()
    {
        Scope scope = Boot();                               // Store.Use(null) in the ctor: a memory-only graph
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;

        Fetch.Plan(scope, t, Rows(t, 4), Identity, FetchPriority.Visible);

        Assert.Equal(0, Fetch.ToDisk);
        Assert.Equal(4, Fetch.ToNetwork);
    }

    // ── batching (P4) ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_request_carries_at_most_three_hundred_uris()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;

        Fetch.Plan(scope, t, Rows(t, 700), Identity, FetchPriority.Visible);

        Assert.Equal(3, provider.Seen.Count);
        Assert.Equal(300, provider.Seen[0].Count);
        Assert.Equal(300, provider.Seen[1].Count);
        Assert.Equal(100, provider.Seen[2].Count);
    }

    [Fact]
    public void At_most_four_requests_are_in_flight_and_the_rest_wait()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;

        Fetch.Plan(scope, t, Rows(t, 2_000), Identity, FetchPriority.Visible);

        Assert.Equal(4, provider.Seen.Count);
        Assert.Equal(4, Fetch.InFlight);
        Assert.Equal(800, Fetch.Pending);                   // 2,000 − 4 × 300
    }

    [Fact]
    public void Providers_never_share_a_request()
    {
        // A local file, a module playable and a Spotify track live in the same TrackTable and cannot ride the same
        // POST. 0.2.9 answered this with per-service routing; here it is the uri's own provider, once.
        Scope scope = Boot();
        var spotify = new RecordingProvider(EntityProvider.Spotify);
        var local = new RecordingProvider(EntityProvider.Local);
        Fetch.Register(spotify);
        Fetch.Register(local);
        Table t = scope.Tracks;
        var slots = new List<int>();
        slots.AddRange(Rows(t, 2));
        slots.AddRange(Rows(t, 3, "wavee:local:file:"));

        Fetch.Plan(scope, t, slots.ToArray(), Identity, FetchPriority.Visible);

        Assert.Single(spotify.Seen);
        Assert.Equal(2, spotify.Seen[0].Count);
        Assert.Single(local.Seen);
        Assert.Equal(3, local.Seen[0].Count);
    }

    [Fact]
    public void Playback_outranks_visible_which_outranks_prefetch()
    {
        Scope scope = Boot();
        Table t = scope.Tracks;
        // Queue three shapes with no transport attached, so nothing can leave…
        Fetch.Plan(scope, t, Rows(t, 1), Identity, FetchPriority.Prefetch);
        Fetch.Plan(scope, t, Rows(t, 1), Identity, FetchPriority.Visible);
        Fetch.Plan(scope, t, Rows(t, 1), Identity, FetchPriority.Playback);
        Assert.Equal(3, Fetch.Pending);

        // …then attach one and pump once: the batch that goes out is the one the user can hear.
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Fetch.Pump();

        Assert.Equal(FetchPriority.Playback, provider.Seen[0].Priority);
        Assert.Equal(FetchPriority.Visible, provider.Seen[1].Priority);
        Assert.Equal(FetchPriority.Prefetch, provider.Seen[2].Priority);
    }

    [Fact]
    public void A_uri_nobody_owns_is_not_sent_anywhere()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int slot = t.Slot("https://open.spotify.com/track/x".AsSpan());

        Fetch.Plan(scope, t, new[] { slot }, Identity, FetchPriority.Visible);

        Assert.Empty(provider.Seen);
        Assert.Equal(0, Fetch.Pending);
    }

    // ── the one DERIVED request: kind 5 on `spotify:audio:` (FLAC plan §5.1, §5.2) ──────────────────────────────────
    //
    // `TrackFields.Files` — the format ladder, and therefore "is this track available lossless" — is the only group
    // that is NOT asked on the row's own identity: TRACK_V4 carries Ogg and AAC and never a FLAC row, so the question
    // goes to `spotify:audio:<base62(original_audio.uuid)>` with extension kind 5. Everything else about it is the
    // ordinary path, and these facts are what says so: one shape key, one in-flight stamp, one 300-uri ceiling.

    /// <summary>A deterministic, non-zero audio key. Any 128-bit value is one; zero means "the row has none".</summary>
    static UInt128 AudioKey(int seed) => new((ulong)seed + 0xA11D10, 0x5EED_0000_0000_0001UL + (ulong)seed);

    [Fact]
    public void A_row_that_wants_the_ladder_is_asked_on_its_audio_entity_with_kind_five()
    {
        Scope scope = Boot();
        var provider = new HoldingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 1, 4242);
        UInt128 audio = AudioKey(1);
        t.OriginalAudio[slots[0]] = audio;

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Files, FetchPriority.Visible);

        FetchBatch batch = provider.Last!;
        Assert.Equal(1, provider.Batches);
        Assert.Equal(Fetch.AudioFilesKind, batch.Extension);

        Span<char> expected = stackalloc char[Fetch.AudioUriChars];
        Assert.Equal(Fetch.AudioUriChars, Fetch.WriteAudioUri(audio, expected));
        string audioUri = new(expected);
        Assert.StartsWith("spotify:audio:", audioUri, StringComparison.Ordinal);

        // The REQUEST is the derived entity…
        Assert.Equal(audioUri, batch.Uri(0));
        // …and the row it will answer for is still the TRACK, which is the only thing that can map the answer back.
        Assert.Equal(t.Id[slots[0]], batch.Ids[0]);
        Assert.Equal(t.Id[slots[0]], batch.IdFor(Encoding.UTF8.GetBytes(audioUri)));
    }

    [Fact]
    public void A_second_plan_for_the_ladder_in_the_same_epoch_asks_nothing()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 3, 5150);
        for (int i = 0; i < slots.Length; i++) t.OriginalAudio[slots[i]] = AudioKey(i + 2);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Files, FetchPriority.Visible);
        Fetch.Plan(scope, t, slots, (uint)TrackFields.Files, FetchPriority.Visible);

        // The derived request is deduped by the same in-flight stamp as every other group (C7): two drawers opening
        // the same row in one drain is one POST, not two.
        Assert.Single(provider.Seen);
        Assert.Equal(3, provider.Seen[0].Count);
        Assert.Equal(3, Fetch.Deduped);
    }

    [Fact]
    public void The_ladder_never_rides_another_groups_post()
    {
        // A different uri AND a different extension kind, so it cannot share a request with the row's own groups. The
        // shape key already separates it — `wanted` is part of it — and the planner peels the bit off rather than
        // letting one bucket carry two request shapes.
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 2, 6006);
        for (int i = 0; i < slots.Length; i++) t.OriginalAudio[slots[i]] = AudioKey(i + 20);

        Fetch.Plan(scope, t, slots, Identity | (uint)TrackFields.Files, FetchPriority.Visible);

        Assert.Equal(2, provider.Seen.Count);
        Assert.Contains(provider.Seen, x => x.Wanted == Identity);
        Assert.Contains(provider.Seen, x => x.Wanted == (uint)TrackFields.Files);
        Assert.All(provider.Seen, x => Assert.Equal(2, x.Count));
    }

    [Fact]
    public void A_row_with_no_audio_key_is_not_asked_and_is_not_sealed_either()
    {
        // "No key yet" is never "no ladder": the uuid rides the track's own payload, so a row that has only ever been a
        // search hit has none. The planner un-asks it — the mark is what suppresses the next request — and the seal for
        // a track that genuinely has no audio entity belongs to the commit, where the answer that said so is in hand.
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 1, 7007);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Files, FetchPriority.Visible);

        Assert.Empty(provider.Seen);
        Assert.False(t.Knows(slots[0], (uint)TrackFields.Files));
        Assert.Equal(0u, t.Inflight[slots[0]]);

        // …and the moment the key lands, the very same Ensure goes out.
        t.OriginalAudio[slots[0]] = AudioKey(99);
        Fetch.Plan(scope, t, slots, (uint)TrackFields.Files, FetchPriority.Visible);

        Assert.Single(provider.Seen);
        Assert.Equal(1, provider.Seen[0].Count);
    }

    // ── what crosses to the provider (doc §2, §3.1) ─────────────────────────────────────────────────────────────────

    /// <summary>The wire's native identity for the six catalog kinds IS the 16-byte gid: every 0.2.9 metadata decode
    /// base62-ENCODED it into a uri string (569 ns measured) that the next layer hashed back into a slot. A batch now
    /// carries the identity itself, <c>Text</c> stays null because a gid row has no uri string anywhere in the
    /// process, and the same 16 bytes find the same row through the 3 ns door.</summary>
    [Fact]
    public void A_gid_row_crosses_to_the_provider_as_sixteen_bytes_and_no_string()
    {
        Scope scope = Boot();
        var provider = new HoldingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] slots = GidRows(t, 1, 42);
        EntityId id = t.Id[slots[0]];
        Assert.Equal(EntityForm.Gid, id.Form);

        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);

        FetchBatch batch = provider.Last!;
        Assert.NotNull(batch);
        Assert.Equal(1, provider.Batches);
        Assert.Equal(id, batch.Ids[0]);
        Assert.Null(batch.Text[0]);                                   // nothing to resolve: there is no string
        Span<byte> gid = stackalloc byte[Base62.GidBytes];
        Assert.Equal(Base62.GidBytes, batch.Gid(0, gid));
        Assert.Equal(slots[0], t.Slot(EntityKind.Track, gid));        // the gid door lands on the row we planned
        Assert.Equal(id.Text, batch.Uri(0));                          // …and text is still there for whoever needs it
    }

    /// <summary>The other half of the threading rule (C1). A provider thread may NOT resolve a <c>StringId</c>: a
    /// released id's slot is cleared 16 ticks later and a batch can outlive that. So the runner resolves on the UI
    /// thread, once, and only for the rows that genuinely have text.</summary>
    [Fact]
    public void A_text_form_row_carries_the_string_the_ui_thread_resolved()
    {
        Scope scope = Boot();
        var provider = new HoldingProvider(EntityProvider.Local);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int slot = t.Slot("wavee:local:file:carried-20260912".AsSpan());

        Fetch.Plan(scope, t, new[] { slot }, Identity, FetchPriority.Visible);

        FetchBatch batch = provider.Last!;
        Assert.NotNull(batch);
        Assert.Equal(EntityForm.Text, batch.Ids[0].Form);
        Assert.Equal("wavee:local:file:carried-20260912", batch.Text[0]);
        Assert.Equal("wavee:local:file:carried-20260912", batch.Uri(0));
        Assert.Equal(0, batch.Gid(0, stackalloc byte[Base62.GidBytes]));   // a text row has no gid, and says so
    }

    /// <summary>Routing is a FIELD READ now. <c>Queue</c> used to resolve the uri and walk <c>ProviderOf</c> over the
    /// text per row per plan (45-100 ns, doc §1.3 item 3) to decide which transport owns it; the parse established
    /// that once, when the row was allocated, and the answer rides in the id.</summary>
    [Fact]
    public void The_provider_a_row_is_routed_to_comes_off_its_packed_id()
    {
        Scope scope = Boot();
        Table t = scope.Tracks;
        int spotify = GidRows(t, 1, 77)[0];
        int local = t.Slot("wavee:local:file:routed-20260912".AsSpan());
        int module = t.Slot("wavee:module:demo:cm91dGVk".AsSpan());

        Assert.Equal(EntityProvider.Spotify, t.Id[spotify].Provider);
        Assert.Equal(EntityProvider.Local, t.Id[local].Provider);
        Assert.Equal(EntityProvider.Module, t.Id[module].Provider);

        var one = new RecordingProvider(EntityProvider.Spotify);
        var two = new RecordingProvider(EntityProvider.Local);
        var three = new RecordingProvider(EntityProvider.Module);
        Fetch.Register(one);
        Fetch.Register(two);
        Fetch.Register(three);

        Fetch.Plan(scope, t, new[] { spotify, local, module }, Identity, FetchPriority.Visible);

        Assert.Equal(1, Assert.Single(one.Seen).Count);
        Assert.Equal(1, Assert.Single(two.Seen).Count);
        Assert.Equal(1, Assert.Single(three.Seen).Count);
    }

    // ── what a plan costs (P8/P14) ──────────────────────────────────────────────────────────────────────────────────
    //
    // Three facts, because "planning 300 rows allocates nothing" is really two claims with two owners, and until
    // 2026-09-12 the first of them was measured through the second and read 80 B:
    //   · THE PLANNER allocates nothing per call. Its window is `Select`'s AND-NOT, the in-flight marks, the
    //     disk/network partition, the bucket, the batch and the send — and it is 0 B, not 0-ish.
    //   · THE DISK LEG is the store's line item, not the planner's, and it is one closure per CALL and nothing per
    //     row. `Store.Read` hands `Enqueue` a lambda over ten locals; Roslyn creates that closure's display class at
    //     the TOP of the method that declares it, which is why the number is the same for one row and for three
    //     hundred — and why, until the guard was split into its own method, even "there is no database" paid the
    //     80 bytes. That refusal is the third fact.
    // The 80 B this file used to report was ALL of it that display class, on the refusal path, with the store closed.
    // Nothing in the planner ever allocated; no bound needed widening.

    /// <summary>THE PLANNER, over the doc's own unit of measurement: 300 uris is one extended-metadata POST. The
    /// whole path — <c>Select</c>'s AND-NOT, the in-flight marks, the bucket, the batch, the send — runs over pooled
    /// buffers and packed ids, so a page mounting 300 rows must add NOTHING to the frame's allocation budget.
    ///
    /// <para>The rows are stamped <c>FetchedAt</c> first, and that is what makes this a measurement OF THE PLANNER:
    /// a row the disk has already answered about skips the disk leg entirely (<c>disk &gt; 0 &amp;&amp;</c>
    /// short-circuits), so no part of <c>Store</c> is inside the window and the number cannot move because a store
    /// was attached. It is also the steady state — the disk probe runs once per row per session, and every frame
    /// after the first is this shape.</para>
    ///
    /// <para>The warm-up pass is not a fudge: it is what "after warm-up" means, and every allocation it makes (the
    /// bucket's arrays doubling to 512, the batch object, the ArrayPool buckets) is a once-per-process cost.</para></summary>
    [Fact]
    public void Planning_a_three_hundred_row_batch_allocates_nothing_after_warm_up()
    {
        Scope scope = Boot();
        var provider = new CountingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] warm = GidRows(t, Fetch.MaxUrisPerRequest, 1);
        int[] measured = GidRows(t, Fetch.MaxUrisPerRequest, 100_001);
        Entities.Now = 500;
        for (int i = 0; i < warm.Length; i++) t.FetchedAt[warm[i]] = 500;
        for (int i = 0; i < measured.Length; i++) t.FetchedAt[measured[i]] = 500;

        Fetch.Plan(scope, t, warm, Identity, FetchPriority.Visible);
        Fetch.Failed(provider.LastTicket, 404, 0);                    // terminal: settles the batch back into the pool
        Assert.Equal(1, provider.Batches);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Fetch.Plan(scope, t, measured, Identity, FetchPriority.Visible);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2, provider.Batches);
        Assert.Equal(2 * Fetch.MaxUrisPerRequest, provider.Rows);
        Assert.Equal(0, Fetch.ToDisk);                                // the disk leg is deliberately not in the window
        Assert.Equal(0L, allocated);
    }

    /// <summary>THE REFUSAL. <c>--fake</c>, every test in this assembly, and the app's whole first second run with no
    /// database, and the planner still offers every unanswered row to the disk first. That offer must cost nothing:
    /// <c>Store.Read</c>'s own summary calls it "a no-op that returns false", and until 2026-09-12 it was a no-op
    /// that allocated 80 bytes per plan, because the closure it hands the queue is built above its <c>s_open</c>
    /// guard. The guard is its own method now, and this is the assertion that keeps it one.</summary>
    [Fact]
    public void With_no_store_the_disk_leg_refuses_without_allocating()
    {
        Scope scope = Boot();                                         // Store.Use(null) in the ctor: no database
        var provider = new CountingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] warm = GidRows(t, Fetch.MaxUrisPerRequest, 1);
        int[] measured = GidRows(t, Fetch.MaxUrisPerRequest, 100_001);

        Fetch.Plan(scope, t, warm, Identity, FetchPriority.Visible);  // FetchedAt == 0: the disk IS offered them
        Fetch.Failed(provider.LastTicket, 404, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Fetch.Plan(scope, t, measured, Identity, FetchPriority.Visible);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(Store.IsOpen);
        Assert.Equal(0, Fetch.ToDisk);                                // offered, refused…
        Assert.Equal(2 * Fetch.MaxUrisPerRequest, Fetch.ToNetwork);   // …and straight out instead
        Assert.Equal(0L, allocated);
    }

    /// <summary>THE DISK LEG, priced. With a real database behind it, one plan costs ONE CLOSURE — the display class
    /// <c>Store.ReadJob</c> hoists to the top of itself, plus the <c>Action</c> the store queue takes: 144 bytes on
    /// 2026-09-12 — and NOT ONE BYTE PER ROW. That is the fact worth pinning, and the reason it holds is the packed
    /// identity: the snapshot is three pooled arrays of <c>(slot, EntityId, string?)</c>, the keys are formatted on
    /// the store thread, and a gid row has no uri text to resolve — so a 300-row page mount and a single-row
    /// <c>Ensure</c> cost exactly the same. The equality is the assertion; the ceiling only names the number.</summary>
    [Fact]
    public void The_disk_leg_costs_one_closure_per_call_and_nothing_per_row()
    {
        Store.Use(_dbPath);
        Store.Post = a => _posted.Enqueue(a);
        Store.Register(new FetchProbeShape());
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        Assert.True(Store.IsOpen);

        // Warm BOTH ArrayPool bucket sizes the snapshot rents — 512 for a full request, 16 for a single row — and
        // every once-per-process object behind them. The local `Plan` below also settles each round completely (the
        // rented arrays come back in the read's own completion, on this thread), so no Rent inside a measured window
        // can be the thing being measured and no leftover bucket can turn `Pump` into a send.
        Plan(GidRows(t, Fetch.MaxUrisPerRequest, 1));
        Plan(GidRows(t, 1, 2_001));

        long many = Plan(GidRows(t, Fetch.MaxUrisPerRequest, 100_001));
        long one = Plan(GidRows(t, 1, 200_001));

        Assert.Equal(one, many);
        Assert.True(many is > 0 and <= 192,
            $"the disk leg costs {many} B per plan; it was 144 B on 2026-09-12 — one display class (80 B) and one " +
            $"delegate (64 B), allocated once per CALL. Outside 1..192 the closure has changed shape: re-derive the " +
            $"number and say what it is now, do not widen the bound.");

        long Plan(int[] rows)
        {
            // Nothing may be pending: a leftover bucket would make `Pump` send inside the window, and a send with an
            // empty batch pool allocates a `FetchBatch`.
            Assert.Equal(0, Fetch.Pending);
            Assert.Equal(0, Fetch.InFlight);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Fetch.Plan(scope, t, rows, Identity, FetchPriority.Visible);
            long cost = GC.GetAllocatedBytesForCurrentThread() - before;

            Store.Flush();
            DrainPosts();                                             // the disk answers; the rest continues out
            while (provider.Seen.Count > 0)
            {
                uint ticket = provider.Seen[0].Ticket;
                provider.Seen.RemoveAt(0);
                Fetch.Failed(ticket, 404, 0);                         // terminal: batches back into the pool
            }
            return cost;
        }
    }

    // ── answers and failures ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_terminal_failure_un_asks_the_rows_so_the_next_page_retries()
    {
        // "A transport error is not an answer" (0.2.9's ledger sealed nothing on failure). A 404 is terminal: the
        // batch is abandoned, but the MARK must go, or those rows are dead for the life of the scope.
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] slots = Rows(t, 2);
        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        uint ticket = provider.Seen[0].Ticket;

        Fetch.Failed(ticket, 404, 0);

        Assert.Equal(0u, t.Inflight[slots[0]]);
        Assert.Equal(0, Fetch.InFlight);
        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Assert.Equal(2, provider.Seen.Count);               // asked again, because nothing said not to
    }

    [Fact]
    public void A_retryable_failure_keeps_the_marks_and_waits_out_the_backoff()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] slots = Rows(t, 2);
        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        uint ticket = provider.Seen[0].Ticket;

        Fetch.Failed(ticket, 503, 0);

        Assert.Equal(1, Fetch.Retried);
        Assert.Equal(2, Fetch.Pending);                     // re-queued, not lost
        Assert.Equal(Fetch.Stamp(scope.Epoch), t.Inflight[slots[0]]);   // still ours: a second plan must not duplicate
        Assert.Single(provider.Seen);                       // and it does NOT go out again in the same second
        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);
        Assert.Single(provider.Seen);

        Entities.Now = 60;                                  // the backoff expires; the host's frame tick pumps
        Fetch.Pump();
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(1, provider.Seen[1].Attempt);
    }

    [Fact]
    public void A_batch_gives_up_after_four_attempts()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] slots = Rows(t, 1);
        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);

        for (int i = 0; i < Fetch.MaxAttempts; i++)
        {
            Assert.Equal(i + 1, provider.Seen.Count);
            Fetch.Failed(provider.Seen[i].Ticket, 500, 0);
            Entities.Now += 120;
            Fetch.Pump();
        }

        Assert.Equal(Fetch.MaxAttempts, provider.Seen.Count);
        Assert.Equal(0, Fetch.Pending);
        Assert.Equal(0u, t.Inflight[slots[0]]);             // given up ⇒ un-asked, not sealed
    }

    [Fact]
    public void An_answer_for_a_ticket_nobody_is_holding_is_ignored()
    {
        Boot();
        Fetch.Answer(9_999, null);
        Fetch.Failed(9_999, 500, 0);
        Assert.Equal(0, Fetch.InFlight);
    }

    [Fact]
    public void An_answer_that_filled_the_group_lets_the_row_be_asked_for_the_next_one()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] slots = Rows(t, 1);
        Fetch.Plan(scope, t, slots, Identity, FetchPriority.Visible);

        // What a commit does to a row it filled: mark the group known and clear the in-flight mark (Table.Applied).
        Column<byte> auth = default;
        auth.EnsureCapacity(t.Count);
        t.Applied(slots[0], Identity, Authority.Full, ref auth);
        Fetch.Answer(provider.Seen[0].Ticket, null);

        Assert.Equal(0u, t.Inflight[slots[0]]);
        Fetch.Plan(scope, t, slots, Identity | Extras, FetchPriority.Visible);
        Assert.Equal(2, provider.Seen.Count);
    }

    // ── the per-group seal (G-040) ──────────────────────────────────────────────────────────────────────────────────
    //
    // THE REQUEST LOOP this closes: a TrackV4 answer fills Identity and clears the in-flight mark, and PlayCount — which
    // TrackV4 never carries — was still `wanted & ~known`, so the next `Ensure(Row)` (the sidebar's per-rebuild Fill) re-
    // POSTed the same V4, forever. The planner now remembers which GROUPS it asked (`Table.Asked`) for the scope.

    /// <summary>Commit what a TrackV4 answer commits for one row: Identity and Availability, at full authority.
    /// <paramref name="unfilled"/> is what the provider reports for a route that did not answer beside this one.</summary>
    static void AnswerIdentity(uint ticket, EntityId id, uint unfilled = 0)
    {
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(id, Authority.Full, (uint)(TrackFields.Identity | TrackFields.Availability));
        row.Title = s.Text("an answered title");
        Fetch.Answer(ticket, s, unfilled);
    }

    [Fact]
    public void A_partial_answer_does_not_re_ask()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 1, 404_040);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Row, FetchPriority.Visible);
        Assert.Single(provider.Seen);
        Assert.Equal((uint)TrackFields.Row, provider.Seen[0].Wanted);

        AnswerIdentity(provider.Seen[0].Ticket, t.Id[slots[0]]);
        Assert.True(t.Knows(slots[0], (uint)TrackFields.Identity));
        Assert.False(t.Knows(slots[0], (uint)TrackFields.PlayCount));   // the answer did not carry it…

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Row, FetchPriority.Visible);
        Fetch.Plan(scope, t, slots, (uint)TrackFields.Row, FetchPriority.Visible);
        Assert.Single(provider.Seen);                                     // …and nobody asks for it again this scope
        Assert.Equal((uint)TrackFields.PlayCount, t.Asked[slots[0]] & (uint)TrackFields.PlayCount);
    }

    [Fact]
    public void A_group_nobody_asked_for_yet_still_goes_out_alone()
    {
        // The seal is per GROUP, not per row: a row whose Identity is answered (or in flight) and whose Audio has never
        // been asked asks for Audio — and for Audio only, so the Identity POST is not repeated beside it.
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 3, 505_050);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Identity, FetchPriority.Visible);   // still in flight
        Fetch.Plan(scope, t, slots, (uint)(TrackFields.Identity | TrackFields.Audio), FetchPriority.Visible);

        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal((uint)TrackFields.Identity, provider.Seen[0].Wanted);
        Assert.Equal((uint)TrackFields.Audio, provider.Seen[1].Wanted);
        Assert.Equal(3, provider.Seen[1].Count);
    }

    [Fact]
    public void A_terminal_failure_un_asks_only_the_groups_that_failed()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 1, 606_060);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Identity, FetchPriority.Visible);
        Fetch.Plan(scope, t, slots, (uint)TrackFields.PlayCount, FetchPriority.Visible);
        Fetch.Failed(provider.Seen[1].Ticket, 404, 0);

        Assert.Equal((uint)TrackFields.Identity, t.Asked[slots[0]]);     // the Identity ask is still out and still ours
        Fetch.Plan(scope, t, slots, (uint)(TrackFields.Identity | TrackFields.PlayCount), FetchPriority.Visible);
        Assert.Equal(3, provider.Seen.Count);
        Assert.Equal((uint)TrackFields.PlayCount, provider.Seen[2].Wanted);
    }

    [Fact]
    public void A_scope_switch_forgets_every_seal_with_its_tables()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int[] before = GidRows(scope.Tracks, 1, 707_070);
        Fetch.Plan(scope, scope.Tracks, before, (uint)TrackFields.Row, FetchPriority.Visible);

        Entities.Switch(CatalogScope.Fake(locale: "nl-NL", market: "NL"));
        Scope next = Entities.Current;
        int[] after = GidRows(next.Tracks, 1, 707_070);                   // the same identity, a new row
        Assert.Equal(0u, next.Tracks.Asked[after[0]]);
        Fetch.Plan(next, next.Tracks, after, (uint)TrackFields.Row, FetchPriority.Visible);

        Assert.Equal(2, provider.Seen.Count);
    }

    // ── the seal's one exception: a route that did not answer beside one that did (2026-09-16 chart defect) ────────
    //
    // A batch whose routes split — one 200, one 401 — used to be delivered as a plain answer, and the answer's seal
    // kept the refused route's group ASKED for the life of the scope: an artist's Chart bit, and with it the chart's
    // shimmer, forever. The provider now names the groups of the routes that failed (`unfilled`) and the answer
    // un-asks those and only those.

    [Fact]
    public void An_answer_whose_route_failed_un_asks_that_group_only()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 1, 808_080);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Row, FetchPriority.Visible);
        AnswerIdentity(provider.Seen[0].Ticket, t.Id[slots[0]], unfilled: (uint)TrackFields.PlayCount);

        Assert.True(t.Knows(slots[0], (uint)TrackFields.Identity));
        Assert.Equal(0u, t.Asked[slots[0]] & (uint)TrackFields.PlayCount);          // the refused route's group is free again
        Assert.Equal((uint)TrackFields.Identity, t.Asked[slots[0]] & (uint)TrackFields.Identity);   // the answered one stays asked
        Assert.Equal(0u, t.Inflight[slots[0]]);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Row, FetchPriority.Visible);
        Assert.Equal(2, provider.Seen.Count);                                           // the next mount really retries…
        Assert.Equal((uint)TrackFields.PlayCount, provider.Seen[1].Wanted);              // …for that group alone
    }

    [Fact]
    public void A_group_no_route_serves_stays_sealed_by_the_answer()
    {
        // `unfilled` is the groups of the routes that FAILED — a group nothing was ever sent for (the Api's
        // `sealedGroups`) is not in it, and the answer seals it as before; and a group the answer DID fill is never
        // un-asked, whatever the provider says about the route that also claimed it.
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 1, 909_090);
        uint wanted = (uint)(TrackFields.Row | TrackFields.Audio);

        Fetch.Plan(scope, t, slots, wanted, FetchPriority.Visible);
        AnswerIdentity(provider.Seen[0].Ticket, t.Id[slots[0]], unfilled: (uint)(TrackFields.PlayCount | TrackFields.Identity));

        Assert.Equal((uint)TrackFields.Audio, t.Asked[slots[0]] & (uint)TrackFields.Audio);       // nobody served it: sealed
        Assert.Equal((uint)TrackFields.Identity, t.Asked[slots[0]] & (uint)TrackFields.Identity); // filled: stays asked
        Fetch.Plan(scope, t, slots, wanted, FetchPriority.Visible);
        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal((uint)TrackFields.PlayCount, provider.Seen[1].Wanted);                       // only the refused group
    }

    [Fact]
    public void An_answer_clears_inflight_for_rows_it_did_not_name()
    {
        // `Table.Applied` clears the in-flight mark for a row a group LANDED on; a row the answer skipped kept it for
        // the life of the scope, so `Asked && Inflight == 0` — the surfaces' "asked, nothing coming" — never read true.
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 2, 111_222);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Identity, FetchPriority.Visible);
        Assert.Equal(Fetch.Stamp(scope.Epoch), t.Inflight[slots[1]]);
        AnswerIdentity(provider.Seen[0].Ticket, t.Id[slots[0]]);                       // names the first row only

        Assert.Equal(0u, t.Inflight[slots[0]]);
        Assert.Equal(0u, t.Inflight[slots[1]]);                                         // settled, not stranded
        Assert.Equal((uint)TrackFields.Identity, t.Asked[slots[1]]);                    // and still sealed: no re-ask
        Fetch.Plan(scope, t, slots, (uint)TrackFields.Identity, FetchPriority.Visible);
        Assert.Single(provider.Seen);
    }

    // ── an auth refusal is re-planned when the session resumes ─────────────────────────────────────────────────────
    //
    // A 401/403 is terminal to the planner and un-asks the batch — right for a 400, but a refusal a boot-time or
    // reconnecting session WILL answer once it authorises leaves every mounted page holding a skeleton nothing re-asks.
    // `Failed` remembers what it un-asked and `Resume` (the session's Online transition) plans it again.

    [Fact]
    public void An_auth_refusal_is_re_planned_when_the_session_resumes()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 2, 333_444);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Identity, FetchPriority.Prefetch);
        Fetch.Failed(provider.Seen[0].Ticket, 401, 0);

        Assert.Equal(0u, t.Asked[slots[0]]);                                            // un-asked, as any terminal failure
        Assert.Equal(0u, t.Inflight[slots[1]]);
        Assert.Single(provider.Seen);                                                   // nothing goes out while refused
        Assert.Equal(2, Fetch.Refused);

        Fetch.Resume();

        Assert.Equal(2, provider.Seen.Count);                                           // planned again, as ONE request…
        Assert.Equal(2, provider.Seen[1].Count);
        Assert.Equal((uint)TrackFields.Identity, provider.Seen[1].Wanted);
        Assert.Equal(FetchPriority.Prefetch, provider.Seen[1].Priority);                // …with the urgency it was asked at
        Assert.Equal((uint)TrackFields.Identity, t.Asked[slots[1]]);
        Assert.Equal(0, Fetch.Refused);
        Fetch.Resume();
        Assert.Equal(2, provider.Seen.Count);                                           // once: the list is spent
    }

    [Fact]
    public void A_refusal_that_is_not_an_auth_refusal_is_not_resumed()
    {
        // 404 is an answer the planner never sees here; 400/410 are terminal and un-asked, but the session coming
        // online changes nothing about them — the next mount's Ensure is their retry.
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 1, 555_666);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Identity, FetchPriority.Visible);
        Fetch.Failed(provider.Seen[0].Ticket, 400, 0);
        Assert.Equal(0, Fetch.Refused);
        Fetch.Resume();
        Assert.Single(provider.Seen);
    }

    [Fact]
    public void A_refusal_from_a_replaced_scope_is_not_resumed()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int[] slots = GidRows(scope.Tracks, 1, 777_888);

        Fetch.Plan(scope, scope.Tracks, slots, (uint)TrackFields.Identity, FetchPriority.Visible);
        Fetch.Failed(provider.Seen[0].Ticket, 403, 0);
        Assert.Equal(1, Fetch.Refused);

        Entities.Switch(CatalogScope.Fake(locale: "nl-NL", market: "NL"));            // the slot indexes a table nobody holds
        Fetch.Resume();

        Assert.Single(provider.Seen);
        Assert.Equal(0, Fetch.Pending);
        Assert.Equal(0, Fetch.Refused);
    }

    [Fact]
    public void A_refusal_whose_slot_was_recycled_is_not_resumed()
    {
        // The identity guard `SettleTicket` applies: a slot that moved on to another entity is not the refusal's to ask.
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 1, 999_000);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Identity, FetchPriority.Visible);
        Fetch.Failed(provider.Seen[0].Ticket, 401, 0);
        t.Id[slots[0]] = default;                                                       // recycled: another row lives here now

        Fetch.Resume();
        Assert.Single(provider.Seen);
    }

    // ── Refresh: ask again what the scope sealed (the Retry vacancy's door) ─────────────────────────────────────────

    [Fact]
    public void Refresh_re_asks_a_group_the_scope_has_already_sealed()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] slots = GidRows(t, 1, 121_212);

        Fetch.Plan(scope, t, slots, (uint)TrackFields.Row, FetchPriority.Visible);
        AnswerIdentity(provider.Seen[0].Ticket, t.Id[slots[0]]);                        // PlayCount: asked, never filled
        Fetch.Plan(scope, t, slots, (uint)TrackFields.Row, FetchPriority.Visible);
        Assert.Single(provider.Seen);                                                   // sealed for the scope…

        Entities.Refresh(t, slots, (uint)TrackFields.PlayCount);                        // …until somebody means it

        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal((uint)TrackFields.PlayCount, provider.Seen[1].Wanted);
        Assert.Equal(Fetch.Stamp(scope.Epoch), t.Inflight[slots[0]]);

        Entities.Refresh(t, slots, (uint)TrackFields.Identity);                         // known: nothing to fetch again
        Assert.Equal(2, provider.Seen.Count);
    }

    // ── subjects: the synthetic tables' routing (G-041) ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_home_feed_nobody_owns_is_routed_to_the_scopes_own_catalogue()
    {
        // `wavee:home` names no provider, and the planner used to abandon it — so the Home page skeletoned forever. It
        // is the SCOPE's catalogue's: the seed's in a fake scope, Spotify's in a live one.
        Scope scope = Boot();                                                 // CatalogScope.Fake(): provider "fake"
        var provider = new HoldingProvider(EntityProvider.Fake);
        Fetch.Register(provider);
        int home = scope.Homes.Slot(Home.FeedUri.AsSpan());
        Assert.Equal(EntityProvider.None, scope.Homes.Id[home].Provider);

        Fetch.Plan(scope, scope.Homes, new[] { home }, (uint)HomeFields.All, FetchPriority.Visible);

        FetchBatch batch = provider.Last!;
        Assert.Equal(1, provider.Batches);
        Assert.Equal(FetchSubject.Home, batch.Subject);
        Assert.Equal(EntityKind.Unknown, batch.Kind);
        Assert.Equal((uint)HomeFields.All, batch.Wanted);
        Assert.Equal(Home.FeedUri, batch.Uri(0));
        Assert.Equal(EntityProvider.Spotify, Fetch.CatalogProvider(new CatalogScope("spotify", "a", "en", "US", 0, true)));
    }

    [Fact]
    public void Four_synthetic_tables_that_share_a_kind_never_share_a_request()
    {
        // All four answer EntityKind.Unknown, and the bucket used to be keyed by kind: a Home row and a search row
        // asking bit 0 landed in ONE bucket over two different tables.
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Fake);
        Fetch.Register(provider);
        int home = scope.Homes.Slot(Home.FeedUri.AsSpan());
        int search = Entities.Search("daft punk".AsSpan()).Slot;

        Fetch.Plan(scope, scope.Homes, new[] { home }, 1u, FetchPriority.Visible);
        Fetch.Plan(scope, scope.Searches, new[] { search }, 1u, FetchPriority.Visible);

        Assert.Equal(2, provider.Seen.Count);
        Assert.All(provider.Seen, x => Assert.Equal(1, x.Count));
    }

    [Fact]
    public void A_subject_is_read_off_the_table_and_a_section_off_its_family()
    {
        Scope scope = Boot();
        int home = scope.Homes.Slot(Home.FeedUri.AsSpan());
        int homeBand = Entities.Section("spotify:section:home-band".AsSpan()).Slot;
        int browseBand = Entities.BrowseSection("spotify:section:browse-band".AsSpan()).Slot;
        int directory = Entities.BrowseDirectory().Slot;
        int page = Entities.BrowseNode("spotify:genre:0JQ5DAqbMKFSi39LMRT0Cy".AsSpan()).Slot;
        int track = GidRows(scope.Tracks, 1, 909)[0];

        Assert.Equal(FetchSubject.Home, Fetch.SubjectOf(scope, scope.Homes, home));
        Assert.Equal(FetchSubject.HomeSection, Fetch.SubjectOf(scope, scope.Sections, homeBand));
        Assert.Equal(FetchSubject.BrowseSection, Fetch.SubjectOf(scope, scope.Sections, browseBand));
        Assert.Equal(FetchSubject.BrowseDirectory, Fetch.SubjectOf(scope, scope.Browses, directory));
        Assert.Equal(FetchSubject.BrowsePage, Fetch.SubjectOf(scope, scope.Browses, page));
        Assert.Equal(FetchSubject.Entity, Fetch.SubjectOf(scope, scope.Tracks, track));
    }

    [Fact]
    public void Stamping_a_browse_band_never_overwrites_a_form_an_answer_wrote()
    {
        Scope scope = Boot();
        int slot = scope.Sections.Slot("spotify:section:answered".AsSpan());
        scope.Sections.Form[slot] = (byte)SectionKind.BrowseCategoryGrid;

        Assert.Equal(slot, Entities.BrowseSection("spotify:section:answered".AsSpan()).Slot);
        Assert.Equal((byte)SectionKind.BrowseCategoryGrid, scope.Sections.Form[slot]);
    }

    // ── the scope epoch (C7) ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_scope_switch_drops_every_pending_row()
    {
        // The slots in a bucket index the OLD table set. Sending them after a switch would fetch a random album's
        // uri into a track's slot — the one class of bug a slot-based model can produce that an object model cannot.
        Scope scope = Boot();
        Table t = scope.Tracks;
        Fetch.Plan(scope, t, Rows(t, 10), Identity, FetchPriority.Visible);
        Assert.Equal(10, Fetch.Pending);

        Entities.Switch(CatalogScope.Fake(locale: "de-DE", market: "DE"));
        Fetch.Register(new RecordingProvider(EntityProvider.Spotify));
        Fetch.Pump();

        Assert.Equal(0, Fetch.Pending);
    }

    [Fact]
    public void A_failure_for_a_replaced_scope_is_not_retried()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        Fetch.Plan(scope, t, Rows(t, 3), Identity, FetchPriority.Visible);
        uint ticket = provider.Seen[0].Ticket;

        Entities.Switch(CatalogScope.Fake(locale: "fr-FR", market: "FR"));
        Fetch.Failed(ticket, 503, 0);

        Assert.Equal(0, Fetch.Retried);
        Assert.Equal(0, Fetch.Pending);
    }

    [Fact]
    public void Continue_for_a_replaced_scope_does_nothing()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Table t = scope.Tracks;
        int[] slots = Rows(t, 2);

        Entities.Switch(CatalogScope.Fake(locale: "es-ES", market: "ES"));
        Fetch.Continue(scope, t, slots, Identity, FetchPriority.Visible);

        Assert.Empty(provider.Seen);
    }

    // ── the backoff (pure) ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 500, 0, 1)]        // 2^0
    [InlineData(1, 500, 0, 2)]
    [InlineData(3, 0, 0, 8)]          // a transport error with no status backs off the same way
    [InlineData(9, 500, 0, 60)]       // min(60, 2^n)
    [InlineData(0, 429, 7, 7)]        // Retry-After wins for a rate limit…
    [InlineData(0, 429, 86_400, 30)]  // …CLAMPED to 30 s: the header is unauthenticated
    [InlineData(0, 429, 0, 1)]        // a 429 with no header waits a second, as RateLimitMiddleware did
    public void Backoff_is_the_ported_curve(int attempt, int status, int retryAfter, int expected)
        => Assert.Equal(expected, Fetch.Backoff(attempt, status, retryAfter));

    [Theory]
    [InlineData(0, true)]             // transport error
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(404, false)]          // the server answered; asking again does not change it
    [InlineData(400, false)]
    [InlineData(200, false)]
    public void Retryable_is_transport_rate_limit_or_server_fault(int status, bool expected)
        => Assert.Equal(expected, Fetch.Retryable(status));
}

/// <summary>The minimum a kind has to teach the store to make the disk leg real. Its columns are not Track's — this
/// file tests the PLANNER, and the planner never looks inside a row.</summary>
sealed class FetchProbeShape : KindShape
{
    static readonly StoreColumn[] s_cols = [new("title", StoreType.Text, StoreColumnFlags.Title)];

    public override EntityKind Kind => EntityKind.Track;
    public override string Table => "track";
    public override ReadOnlySpan<StoreColumn> Columns => s_cols;
    public override void Save(Staging s, RowWriter w) { }
    public override void Load(RowReader r, Staging into) { }
}
