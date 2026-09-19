// ── Wavee.Tests/FetchDrainTests.cs — the planner batches, and waits for the session (wave D4, plan §3.5) ─────────────
//
// THE TWO DEFECTS THIS FILE GATES (plan §1.4-§1.5, read off the always-on wire log on 2026-09-18):
//   · 122 extended-metadata POSTs in 62 s with a median body of 107 bytes — ONE uri each — because `Pump()` ran inline at
//     the end of every `Ensure`, so the only coalescing was accidental back-pressure at four requests in flight. P4 said
//     "the query layer batches"; this is the half of it that never existed.
//   · 401s at boot, four to six of them, retried: the planner sent before the session had a token, against the fallback
//     spclient host.
// The official client makes 171 POSTs in 38 s (reorders.saz, plan §3.7), so the yardstick is "fewer than that, and
// full", never zero.
//
// Every fact below drives the REAL planner over real rows in a real scope with a recording transport, and stands in for
// the host's per-tick call with `Fetch.Drain()` exactly where the tick would come. Nothing reads production source.
//   · N asks in one tick leave as ONE request per bucket — on the drain, and not a moment before;
//   · a PLAYBACK ask leaves at once, and takes the tick's rows of its shape with it;
//   · urgency is not shape: a Visible and a Prefetch ask of one shape are one request at Visible, and an emptied bucket
//     starts again from its next ask's own urgency;
//   · a row and its relation asked in one tick share the wire call whichever came first (row buckets leave first);
//   · the 300-uri ceiling still splits a big tick;
//   · a provider `CanSend` refuses is HELD — rows, marks and in-flight stamps kept, nothing sent, nothing un-asked — and
//     leaves as ONE batch on the first pump after the gate opens: the Online transition's, or the next tick's; the disk
//     leg is not gated; a scope switch drops held rows with everything else (C7);
//   · the host is woken ONCE per owed drain, and `NextWakeAt` answers "now" while one is owed;
//   · every send and every settle writes its always-on line.

using System.Collections.Concurrent;
using System.Globalization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class FetchDrainTests : IDisposable
{
    const uint Identity = (uint)TrackFields.Identity;
    const uint PlayCount = (uint)TrackFields.PlayCount;

    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-v3-drain-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();
    readonly Action? _hostWake = Fetch.WakeForDrain;

    public FetchDrainTests()
    {
        Fetch.Reset();
        Fetch.WakeForDrain = null;         // the host's wiring; no host here — the facts below are the tick
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();                     // drops the send gate too
        Fetch.WakeForDrain = _hostWake;
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

    /// <summary>One GID-form catalog row of <paramref name="token"/>'s kind — <c>spotify:&lt;token&gt;:</c> + 22 base62
    /// characters, the shape a real catalog row is made of, deterministic per <paramref name="seed"/>.</summary>
    static int GidRow(Table table, string token, int seed)
    {
        string prefix = "spotify:" + token + ":";
        Span<char> uri = stackalloc char[prefix.Length + Base62.GidChars];
        prefix.AsSpan().CopyTo(uri);
        ulong n = (ulong)seed;
        Base62.Encode(new UInt128(n * 0x9E37_79B9_7F4A_7C15UL + 11, n * 0xC2B2_AE3D_27D4_EB4FUL + 3), uri[prefix.Length..]);
        return table.Slot(uri);
    }

    static int[] GidRows(Table table, int count, int seed)
    {
        var slots = new int[count];
        for (int i = 0; i < count; i++) slots[i] = GidRow(table, "track", seed + i);
        return slots;
    }

    void DrainPosts()
    {
        while (_posted.TryDequeue(out Action? a)) a();
    }

    /// <summary>The newest always-on line with this event id whose <paramref name="field"/> is <paramref name="value"/>.
    /// The ring is process-wide, but a ticket is unique for the life of the process, so the match is this fact's own
    /// line (and the only collections that clear or filter the ring run alone or serially with this one).</summary>
    static WaveeLogEntry LastLine(string eventId, string field, string value)
    {
        WaveeLogEntry[] ring = Log.Snapshot();
        for (int i = ring.Length - 1; i >= 0; i--)
            if (ring[i].EventId == eventId && FieldOf(ring[i], field) == value) return ring[i];
        Assert.Fail($"no {eventId} line with {field}={value} in the log ring");
        return default;
    }

    static string? FieldOf(in WaveeLogEntry entry, string name)
    {
        if (entry.Fields is not { } fields) return null;
        foreach (WaveeLogField f in fields)
            if (f.Name == name) return f.Value;
        return null;
    }

    // ── one request per bucket per tick ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Many_asks_in_one_tick_leave_as_one_request_per_bucket()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] rows = GidRows(t, 12, 10_000);
        int[] counted = GidRows(t, 3, 20_000);

        // Twelve one-row asks — the per-row callers the wire log caught (the pin band, the rail, a restored queue's
        // episodes) — and three of a second shape, all inside one tick.
        for (int i = 0; i < rows.Length; i++) Entities.Ensure(t, rows.AsSpan(i, 1), Identity, FetchPriority.Visible);
        for (int i = 0; i < counted.Length; i++) Entities.Ensure(t, counted.AsSpan(i, 1), PlayCount, FetchPriority.Visible);

        Assert.Empty(provider.Seen);                                      // nothing leaves before the tick…
        Assert.Equal(15, Fetch.Pending);

        Fetch.Drain();                                                    // …and the tick sends one request per shape

        Assert.Equal(2, provider.Seen.Count);
        Assert.Contains(provider.Seen, x => x.Wanted == Identity && x.Count == 12);
        Assert.Contains(provider.Seen, x => x.Wanted == PlayCount && x.Count == 3);
        Assert.Equal(0, Fetch.Pending);

        Fetch.Drain();                                                    // a tick with nothing owed sends nothing
        Assert.Equal(2, provider.Seen.Count);
    }

    [Fact]
    public void A_playback_ask_leaves_at_once_and_takes_the_ticks_rows_of_its_shape_with_it()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Fetch.Drain();                                                    // the registration's own owe, settled
        TrackTable t = scope.Tracks;
        int[] visible = GidRows(t, 3, 30_000);
        int[] nowPlaying = GidRows(t, 1, 31_000);

        Entities.Ensure(t, visible, Identity, FetchPriority.Visible);
        Assert.Empty(provider.Seen);

        // The now-playing row must not wait a frame (plan §3.5) — no drain between the ask and the assertion.
        Entities.Ensure(t, nowPlaying, Identity, FetchPriority.Playback);

        var batch = Assert.Single(provider.Seen);
        Assert.Equal(4, batch.Count);                                     // ONE request: the tick's Visible rows ride along
        Assert.Equal(FetchPriority.Playback, batch.Priority);
        Assert.Equal(0, Fetch.Pending);
    }

    [Fact]
    public void A_visible_and_a_prefetch_ask_of_one_shape_are_one_request_at_visible()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] rows = GidRows(t, 8, 40_000);

        // `EnsureRootlistRows`' Prefetch sweep and the sidebar's Visible ask for the rows on screen, overlapping as they
        // do. The overlap is deduped by the ask marks; the rest used to be a SECOND request, because urgency was part of
        // the bucket's key.
        Entities.Ensure(t, rows.AsSpan(0, 6), Identity, FetchPriority.Prefetch);
        Entities.Ensure(t, rows.AsSpan(3, 5), Identity, FetchPriority.Visible);
        Fetch.Drain();

        var batch = Assert.Single(provider.Seen);
        Assert.Equal(8, batch.Count);
        Assert.Equal(FetchPriority.Visible, batch.Priority);              // the MAX of what joined it
        Assert.Equal(3, Fetch.Deduped);                                   // rows 3..5 were asked once

        // The bucket emptied with that send: the next ask of the shape is a new demand at its OWN urgency, not at the
        // urgency the previous one happened to reach.
        Entities.Ensure(t, GidRows(t, 2, 41_000), Identity, FetchPriority.Prefetch);
        Fetch.Drain();

        Assert.Equal(2, provider.Seen.Count);
        Assert.Equal(FetchPriority.Prefetch, provider.Seen[1].Priority);
        Assert.Equal(2, provider.Seen[1].Count);
    }

    [Fact]
    public void A_relation_asked_before_its_row_in_one_tick_still_shares_the_rows_request()
    {
        // A page that asks its tracks before its header. With a pump per ask the EDGE left first, and the row's
        // Metadata(AlbumV4) — the very same wire call — went out beside it. The tick sends ROW buckets before EDGE
        // buckets of one priority, so the row's batch is in the route index when the edge is looked at and the edge
        // rides the row's answer (FetchDedupTests pins the re-plan when that answer does not carry it).
        Scope scope = Boot();
        var provider = new DedupProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int album = GidRow(scope.Albums, "album", 77);

        Entities.EnsureEdge(FetchEdge.AlbumTracks, album);
        Entities.Ensure(scope.Albums, new[] { album }, (uint)AlbumFields.Detail, FetchPriority.Visible);
        Fetch.Drain();

        var only = Assert.Single(provider.Seen);
        Assert.Equal(FetchSubject.Entity, only.Subject);
        Assert.True(scope.Edges.AlbumTracks.WasAsked(album, 0));          // asked, and absorbed — not lost
    }

    [Fact]
    public void A_big_tick_still_splits_at_three_hundred_uris()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] rows = GidRows(t, 700, 50_000);

        for (int i = 0; i < 7; i++) Entities.Ensure(t, rows.AsSpan(i * 100, 100), Identity, FetchPriority.Visible);
        Fetch.Drain();

        Assert.Equal(3, provider.Seen.Count);                             // seven asks, one bucket, the ceiling's split
        Assert.Equal(Fetch.MaxUrisPerRequest, provider.Seen[0].Count);
        Assert.Equal(Fetch.MaxUrisPerRequest, provider.Seen[1].Count);
        Assert.Equal(100, provider.Seen[2].Count);
    }

    // ── the online gate ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_provider_that_may_not_send_is_held_and_then_released_as_one_batch()
    {
        Scope scope = Boot();
        var spotify = new RecordingProvider(EntityProvider.Spotify);
        var local = new RecordingProvider(EntityProvider.Local);
        Fetch.Register(spotify);
        Fetch.Register(local);
        bool online = false;
        Fetch.CanSend = p => p != EntityProvider.Spotify || online;      // Spotify.Library.Install's gate, over a flag
        TrackTable t = scope.Tracks;
        int[] rows = GidRows(t, 5, 60_000);
        int file = t.Slot("wavee:local:file:held-20260919".AsSpan());

        for (int i = 0; i < rows.Length; i++) Entities.Ensure(t, rows.AsSpan(i, 1), Identity, FetchPriority.Visible);
        Entities.Ensure(t, GidRows(t, 1, 61_000), Identity, FetchPriority.Playback);   // held too: the gate outranks urgency
        Entities.Ensure(t, [file], Identity, FetchPriority.Visible);
        Fetch.Drain();
        Fetch.Drain();                                                    // tick after tick, while the session is not Online

        Assert.Empty(spotify.Seen);                                       // nothing unauthenticated leaves
        Assert.Equal(1, Assert.Single(local.Seen).Count);                 // the gate is per provider: a local file is not held
        Assert.Equal(6, Fetch.Pending);                                   // HELD, not dropped…
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.Equal(Identity, t.Asked[rows[i]] & Identity);          // …not un-asked…
            Assert.Equal(Fetch.Stamp(scope.Epoch), t.Inflight[rows[i]]);  // …and still "coming", never "failed"
            Assert.Equal(0u, t.Failed[rows[i]]);
        }
        Assert.Equal(0, Fetch.Refused);                                   // not a refusal either: nothing to resume
        Entities.Ensure(t, rows, Identity, FetchPriority.Visible);        // a remount while held asks nothing twice
        Assert.Equal(6, Fetch.Pending);

        online = true;
        Fetch.Pump();                                                     // the session's Online transition

        var batch = Assert.Single(spotify.Seen);                          // ONE batch, not six
        Assert.Equal(6, batch.Count);
        Assert.Equal(FetchPriority.Playback, batch.Priority);
        Assert.Equal(0, Fetch.Pending);
    }

    [Fact]
    public void A_held_bucket_leaves_on_the_first_tick_after_the_gate_opens()
    {
        // The Online transition pumps (Spotify.Library). If one is ever missed — a reconnect inside the sync's 30 s rate
        // limit does not run the sync — the tick looks again on its own, because the last pump HELD something.
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        bool online = false;
        Fetch.CanSend = p => p != EntityProvider.Spotify || online;
        TrackTable t = scope.Tracks;

        Entities.Ensure(t, GidRows(t, 4, 65_000), Identity, FetchPriority.Visible);
        Fetch.Drain();
        Assert.Empty(provider.Seen);
        Assert.Equal(int.MaxValue, Fetch.NextWakeAt());                   // held is not a deadline: no busy idle wake

        online = true;
        Fetch.Drain();                                                    // no pump from anyone: just the next tick

        Assert.Equal(4, Assert.Single(provider.Seen).Count);
        Fetch.Drain();
        Assert.Single(provider.Seen);
    }

    [Fact]
    public void The_disk_leg_is_not_gated_by_the_session()
    {
        Store.Use(_dbPath);
        Store.Post = a => _posted.Enqueue(a);
        Store.Register(new FetchProbeShape());
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        bool online = false;
        Fetch.CanSend = p => p != EntityProvider.Spotify || online;
        TrackTable t = scope.Tracks;
        int[] rows = GidRows(t, 3, 70_000);

        Entities.Ensure(t, rows, Identity, FetchPriority.Visible);
        Assert.Equal(3, Fetch.ToDisk);                                    // offered to the disk while offline — disk first…
        Store.Flush();
        DrainPosts();                                                     // …which answers (nothing there) and continues
        Fetch.Drain();

        Assert.Empty(provider.Seen);                                      // the network leg waits for the session
        Assert.Equal(3, Fetch.Pending);

        online = true;
        Fetch.Pump();
        Assert.Equal(3, Assert.Single(provider.Seen).Count);
    }

    [Fact]
    public void A_scope_switch_drops_the_held_rows_with_everything_else()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        bool online = false;
        Fetch.CanSend = p => p != EntityProvider.Spotify || online;
        Entities.Ensure(scope.Tracks, GidRows(scope.Tracks, 4, 80_000), Identity, FetchPriority.Visible);
        Fetch.Drain();
        Assert.Equal(4, Fetch.Pending);

        Entities.Switch(CatalogScope.Fake(locale: "sv-SE", market: "SE"));   // the held slots index the OLD table set (C7)
        online = true;
        Fetch.Pump();

        Assert.Empty(provider.Seen);
        Assert.Equal(0, Fetch.Pending);
    }

    // ── the host's wake ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_host_is_woken_once_per_owed_drain_and_its_idle_deadline_is_now()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Fetch.Drain();                                                    // the registration's own owe, settled
        int wakes = 0;
        Fetch.WakeForDrain = () => wakes++;
        TrackTable t = scope.Tracks;
        Entities.Now = 1_000;
        Assert.Equal(int.MaxValue, Fetch.NextWakeAt());                   // nothing waits

        int[] rows = GidRows(t, 3, 90_000);
        for (int i = 0; i < rows.Length; i++) Entities.Ensure(t, rows.AsSpan(i, 1), Identity, FetchPriority.Visible);

        Assert.Equal(1, wakes);                                           // the first owe of the tick wakes the host, once
        Assert.Equal(1_000, Fetch.NextWakeAt());                          // …and the idle wake's deadline is "now"

        Fetch.Drain();
        Assert.Equal(3, Assert.Single(provider.Seen).Count);
        Assert.Equal(int.MaxValue, Fetch.NextWakeAt());

        Entities.Ensure(t, GidRows(t, 1, 91_000), Identity, FetchPriority.Visible);
        Assert.Equal(2, wakes);                                           // the next tick's first owe wakes it again
        Entities.Ensure(t, GidRows(t, 1, 92_000), Identity, FetchPriority.Playback);
        Assert.Equal(2, wakes);                                           // a Playback ask pumps itself: no wake needed…
        Assert.Equal(2, provider.Seen.Count);                             // …and took the owed row with it
        Assert.Equal(2, provider.Seen[1].Count);
        Fetch.Drain();
        Assert.Equal(2, provider.Seen.Count);
    }

    // ── the always-on lines ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_send_and_every_settle_writes_its_always_on_line()
    {
        Scope scope = Boot();
        var provider = new RecordingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        TrackTable t = scope.Tracks;
        int[] rows = GidRows(t, 4, 95_000);

        for (int i = 0; i < rows.Length; i++) Entities.Ensure(t, rows.AsSpan(i, 1), Identity, FetchPriority.Prefetch);
        Fetch.Drain();
        uint ticket = Assert.Single(provider.Seen).Ticket;
        string id = ticket.ToString(CultureInfo.InvariantCulture);

        WaveeLogEntry send = LastLine("fetch.send", "ticket", id);
        Assert.Equal("fetch", send.Category);
        Assert.Equal("Spotify", FieldOf(send, "provider"));
        Assert.Equal("Entity", FieldOf(send, "subject"));
        Assert.Equal("Track", FieldOf(send, "kind"));
        Assert.Equal("None", FieldOf(send, "edge"));
        Assert.Equal("4", FieldOf(send, "rows"));                         // the tick's four asks, one request
        Assert.Equal("0x3f", FieldOf(send, "need"));                      // TrackFields.Identity
        Assert.Equal("Prefetch", FieldOf(send, "prio"));
        Assert.NotNull(FieldOf(send, "waitedMs"));

        Fetch.Failed(ticket, 503, 0);

        WaveeLogEntry answer = LastLine("fetch.answer", "ticket", id);
        Assert.Equal("503", FieldOf(answer, "status"));
        Assert.Equal("4", FieldOf(answer, "rows"));
        Assert.Equal("true", FieldOf(answer, "retry"));
        Assert.NotNull(FieldOf(answer, "ms"));
    }
}
