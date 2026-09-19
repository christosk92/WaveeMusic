// ── Wavee.Tests/SpotifyTelemetryTests.cs — the gabo batcher's decisions and the event shapes ─────────────────────
//
// Wave 2's gate for `Spotify/Spotify.Telemetry.cs`. What is worth pinning here is not the POST — that is a socket —
// but the three DECISIONS the batcher makes and the SHAPES it puts on the wire:
//
//   WHEN TO FLUSH. A hundred events, or 125 kB, or the 300 s heartbeat. The caps are advertised to the service in the
//   sdk version string, so a cap that drifts from that string is a batch the service can refuse for a reason nothing
//   in the response explains — which is exactly why the string is asserted against the numbers here.
//   WHAT TO DROP. A persistent outage must not grow a list forever (C8). The overflow is a pure function of the
//   pending count, and it drops the OLDEST, because the newest plays are the ones still worth registering.
//   WHAT A SOURCE IS. `ContextKind` turns `spotify:playlist:…` into `playlist`, which is the `source_start` every
//   royalty row carries.
//
// The named timers (P10) are asserted as constants, which is the only way a "this is event-driven, not polled" claim
// survives a refactor: a new timer would have to be given a name and a number, and this file would have to change.
//
// PODCAST PLAN §5.8 (wave P2) adds the herodotus READ: the CEL filter's exact text for a given `since`, the fold of a
// `ListCurrentStates` answer into staged episode rows, the resume-point WRITE's wire shape, the window (the whole 180 days
// on every sync — no stamp), the settle-without-an-answer rule for a session that cannot reach Spotify (plan §6.1), and
// the authority rule that keeps a Full wire answer off a Local row.
//
// THE 2026-09-19 CAPTURE of the official client (findings-podcast-wire.md §3.2, §4.1) rewrote the unit and the arms: the
// resume point is a google.protobuf.Duration (the retired "µs in field 2" corrupted the account's progress), the value
// is a oneof (2 position · 3 / 4 empty markers · 12 a context resume) where NO arm claims nothing, and a state carries
// REPEATED revisions. The facts pin the write to the official client's own bytes and fold answers framed by hand around
// bytes copied from the decoded bodies (`HerodotusWire`; every uri synthetic).

using System;
using System.Text;
using System.Threading;
using Google.Protobuf;
using Wavee;
using Xunit;
using Duration = Google.Protobuf.WellKnownTypes.Duration;
using Rs = Wavee.Protocol.Resumption;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using W = Wavee.Tests.HerodotusWire;

namespace Wavee.Tests;

public class SpotifyTelemetryBatcherTests
{
    [Fact]
    public void The_caps_are_the_ones_the_sdk_string_advertises()
    {
        Assert.Equal(100, Spotify.Telemetry.GaboMaxEvents);
        Assert.Equal(125 * 1024, Spotify.Telemetry.GaboMaxUncompressedBytes);
        Assert.Equal(300_000, Spotify.Telemetry.GaboFlushIntervalMs);
    }

    [Fact]
    public void The_two_timers_this_file_owns_are_named_and_numbered()
    {
        Assert.Equal(300_000, Spotify.Telemetry.GaboFlushIntervalMs);   // the gabo heartbeat
        Assert.Equal(2_000, Spotify.Telemetry.ResumeFlushMs);           // the resume-point tick
    }

    [Fact]
    public void A_batch_under_both_caps_waits_for_the_heartbeat()
    {
        Assert.False(Spotify.Telemetry.ShouldFlush(99, 1024));
        Assert.False(Spotify.Telemetry.ShouldFlush(0, 0));
    }

    [Fact]
    public void A_batch_flushes_at_the_event_cap()
        => Assert.True(Spotify.Telemetry.ShouldFlush(Spotify.Telemetry.GaboMaxEvents, 0));

    [Fact]
    public void A_batch_flushes_at_the_size_cap_even_with_one_event_in_it()
        => Assert.True(Spotify.Telemetry.ShouldFlush(1, Spotify.Telemetry.GaboMaxUncompressedBytes));

    /// <summary>An outage shorter than the backlog loses nothing…</summary>
    [Fact]
    public void A_backlog_inside_the_cap_drops_nothing()
    {
        Assert.Equal(0, Spotify.Telemetry.Overflow(0));
        Assert.Equal(0, Spotify.Telemetry.Overflow(Spotify.Telemetry.GaboBacklogCap));
    }

    /// <summary>…and one longer than it drops exactly the excess, counted, never silently.</summary>
    [Fact]
    public void A_backlog_past_the_cap_drops_exactly_the_excess()
        => Assert.Equal(7, Spotify.Telemetry.Overflow(Spotify.Telemetry.GaboBacklogCap + 7));
}

public class SpotifyTelemetryShapeTests
{
    [Theory]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "playlist")]
    [InlineData("spotify:album:0apIvboeRy3QYd13K5Dfj4", "album")]
    [InlineData("spotify:artist:4gzpq5DPGxSnKTe4SA8HAU", "artist")]
    [InlineData("spotify:user:bob:collection", "user")]
    public void A_context_uri_names_its_source(string uri, string expected)
        => Assert.Equal(expected, Spotify.Telemetry.ContextKind(uri));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uri")]
    [InlineData("spotify")]
    public void Something_that_is_not_a_uri_has_no_source(string uri)
        => Assert.Equal("unknown", Spotify.Telemetry.ContextKind(uri));

    /// <summary>The client context is supplied ONCE and then stays stable for the process: a fresh installation id
    /// per read would make the account look like a new device on every event. It is settable because the login smoke
    /// and the offline fixture path both supply one, so a run can be identified without reading the machine's real
    /// SID — which is also what keeps this test off the filesystem (the DERIVED context persists an installation id,
    /// and a unit test has no business writing one).</summary>
    [Fact]
    public void The_client_context_is_supplied_once_and_then_stable()
    {
        var supplied = new Spotify.Telemetry.Context(
            new byte[16], new byte[16], new byte[16], "1.2.3", 123, "windows", "m", "d", "S-1-5-21-1-2-3-4", "10.0.1");
        Spotify.Telemetry.Client = supplied;

        Assert.Same(supplied, Spotify.Telemetry.Client);
        Assert.Same(supplied, Spotify.Telemetry.Client);
        Assert.Equal("windows", Spotify.Telemetry.Client.PlatformType);
    }
}

// ── G-077: the sequence is in-memory (Interlocked), persisted only on a flush, and a restart never replays a number
// the service has already seen ──────────────────────────────────────────────────────────────────────────────────────
//
// `Telemetry.Boot`/`Enqueue`/`Flush` are process-global (one worker thread, one `Pending` list, one `s_sequence`,
// started once — `Boot` is idempotent for the process's whole lifetime), so this class shares the `platform`
// collection with `PlatformTests` (disabled parallelization) and every fact restores `Platform.UseSettings` and
// `Spotify.Telemetry.PostGabo` in a `finally`, the same discipline `PlatformSettingsTests.WithStore` uses.
[Collection(PlatformCollection.Name)]
public class SpotifyTelemetrySequenceTests
{
    /// <summary>An <see cref="IAppSettings"/> that counts <c>Set</c> CALLS, not distinct keys — the question this
    /// file asks ("did the flush write once, not once per event?") is exactly the one <see cref="MemoryAppSettings"/>'s
    /// dictionary can't answer, because a second `Set` of the same key never grows its count.</summary>
    sealed class CountingAppSettings : IAppSettings
    {
        public int SetCalls;
        public T Get<T>(SettingKey<T> key) => key.Default;
        public void Set<T>(SettingKey<T> key, T value) => Interlocked.Increment(ref SetCalls);
    }

    [Fact]
    public void Ten_events_write_no_setting_and_the_hundredth_flushes_the_sequence_exactly_once()
    {
        var store = new CountingAppSettings();
        Platform.UseSettings(store);
        Func<byte[], Spotify.Api.Result> originalPost = Spotify.Telemetry.PostGabo;
        Spotify.Telemetry.PostGabo = static _ => new Spotify.Api.Result(200, []);
        try
        {
            for (int i = 0; i < 10; i++) Spotify.Telemetry.Enqueue("Test", []);
            Thread.Sleep(150);   // give the worker thread time to drain the 10 into `Pending` — nothing should flush
            Assert.Equal(0, store.SetCalls);

            for (int i = 10; i < Spotify.Telemetry.GaboMaxEvents; i++) Spotify.Telemetry.Enqueue("Test", []);

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref store.SetCalls) == 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);

            Assert.Equal(1, store.SetCalls);
        }
        finally
        {
            Spotify.Telemetry.PostGabo = originalPost;
            Platform.UseSettings(null);
        }
    }

    /// <summary>The worst case a crash can strand: a flush just missed (`GaboMaxEvents - 1` events minted since the
    /// last persisted flush) the instant before the process dies, so the persisted value is that far behind the
    /// highest number actually sent. The margin must still land the next boot strictly past it.</summary>
    [Fact]
    public void The_resume_margin_clears_the_worst_case_unpersisted_gap()
    {
        long lastSent = 123_456;
        long persisted = lastSent - (Spotify.Telemetry.GaboMaxEvents - 1);

        long resumed = persisted + Spotify.Telemetry.GaboSequenceResumeMargin;

        Assert.True(resumed > lastSent);
    }
}

// ── podcast plan §5.8: the progress hydrate's filter, window and fold, the resume-point write's shape, the settle ─────

public class SpotifyTelemetryProgressTests
{
    internal const string E1 = "spotify:episode:4rOoJ6Egrf8K2IrywzwOMk";
    internal const string E2 = "spotify:episode:512ojhOuo1ktJprKbVcKyQ";

    internal static EntityId Gid(string uri)
    {
        Assert.True(EntityId.TryParseGid(uri.AsSpan(), out EntityId id));
        return id;
    }

    /// <summary>One state as herodotus answers it: one revision whose value holds a position in ms as the official
    /// Duration (whole seconds, the remainder as whole-ms nanos) — or no arm at all for a null position.</summary>
    internal static Rs.CurrentStateEntry State(string entityUri, long? positionMs, long createdS = 0, long updatedS = 0,
                                               string valueUri = "")
    {
        var value = new Rs.CurrentStateValue { EntityUri = valueUri };
        if (positionMs is long ms) value.ResumePoint = new Duration { Seconds = ms / 1000, Nanos = (int)(ms % 1000) * 1_000_000 };
        var revision = new Rs.CurrentStateRevision { Value = value };
        if (createdS > 0) revision.CreateTime = new Timestamp { Seconds = createdS };
        if (updatedS > 0) revision.UpdateTime = new Timestamp { Seconds = updatedS };
        return new Rs.CurrentStateEntry { EntityUri = entityUri, Revisions = { revision } };
    }

    internal static Rs.ListCurrentStatesResponse Answer(params Rs.CurrentStateEntry[] states)
    {
        var response = new Rs.ListCurrentStatesResponse();
        response.States.AddRange(states);
        return response;
    }

    [Fact]
    public void The_filter_is_the_cel_expression_over_update_time_with_a_utc_instant_to_the_millisecond()
        => Assert.Equal(
            "cs.resume_point_revisions.exists(revision, revision.update_time > timestamp('2026-08-29T10:40:00.123Z'))",
            Spotify.Telemetry.CurrentStatesFilter(1_788_000_000_123));

    [Fact]
    public void The_hydrate_asks_one_page_of_1000_over_180_days()
    {
        Assert.Equal(1000, Spotify.Telemetry.CurrentStatesLimit);   // the official client's limit, cold and filtered alike
        Assert.Equal(180L * 24 * 60 * 60 * 1000, Spotify.Telemetry.ProgressLookbackMs);
    }

    /// <summary>EVERY sync looks back the whole window — the first after sign-in and each reconnect's alike. A "since the
    /// last landed sync" stamp assumed the cache kept every row an earlier hydrate landed, and the 30-day sweep, "Clear
    /// metadata" and a schema change (a new, empty file) each break that without telling it.</summary>
    [Theory]
    [InlineData(1_790_000_000_000L)]
    [InlineData(1_790_000_060_000L)]   // a minute later — a reconnect — asks the same whole window, not "since then"
    public void Every_hydrate_looks_back_the_whole_180_days(long nowMs)
        => Assert.Equal(nowMs - 180L * 24 * 60 * 60 * 1000, Spotify.Telemetry.HydrateSince(nowMs));

    // ── THE UNIT: a google.protobuf.Duration, out and back ──

    /// <summary>A position goes out as whole seconds plus the remainder as whole-ms nanos. The rows include the captured
    /// official values (141.122 s, 20 s, 0), the two positions the retired µs write corrupted on the account (4 699 ms,
    /// 998 943 ms), and positions past 1000 s — where that write produced nanos ≥ 1e9, an invalid Duration.</summary>
    [Theory]
    [InlineData(0L, 0L, 0)]
    [InlineData(999L, 0L, 999_000_000)]
    [InlineData(4_699L, 4L, 699_000_000)]
    [InlineData(20_000L, 20L, 0)]
    [InlineData(141_122L, 141L, 122_000_000)]
    [InlineData(998_943L, 998L, 943_000_000)]
    [InlineData(1_000_000L, 1_000L, 0)]
    [InlineData(7_200_999L, 7_200L, 999_000_000)]
    [InlineData(-5L, 0L, 0)]                          // a negative position clamps to the start
    public void A_position_goes_out_as_a_Duration_of_seconds_and_whole_millisecond_nanos(long positionMs, long seconds, int nanos)
    {
        Duration sent = Spotify.Telemetry.ResumeRevision(E1, positionMs, 1_788_000_000_000).Revision.Value.ResumePoint;
        Assert.Equal(seconds, sent.Seconds);
        Assert.Equal(nanos, sent.Nanos);
    }

    /// <summary>At ANY position the Duration is valid (nanos a whole number of ms below a second — protobuf's own
    /// <c>ToTimeSpan</c> throws on anything else), it IS the position, and the read gives the milliseconds back.</summary>
    [Fact]
    public void Nanos_never_reach_a_second_and_every_position_reads_back_exactly()
    {
        var positions = new List<long>(10_010);
        for (long ms = 0; ms <= 10_000; ms++) positions.Add(ms);
        long[] far = [999_999, 1_000_000, 1_000_001, 2_147_483, 2_147_484, 3_600_000, 86_399_999, int.MaxValue];
        positions.AddRange(far);
        foreach (long positionMs in positions)
        {
            Duration d = Spotify.Telemetry.ResumeDurationOf(positionMs);
            Assert.InRange(d.Nanos, 0, 999_000_000);
            Assert.Equal(0, d.Nanos % Spotify.Telemetry.NanosPerMs);
            Assert.Equal(TimeSpan.FromTicks(positionMs * TimeSpan.TicksPerMillisecond), d.ToTimeSpan());
            Assert.Equal(positionMs, Spotify.Telemetry.PositionMsOf(d));
        }
    }

    /// <summary>The read never throws and never goes negative on what an older writer left: negative parts read 0,
    /// nanos past a second read 999 ms, and seconds clamp to the int range.</summary>
    [Theory]
    [InlineData(-3L, 0, 0L)]
    [InlineData(0L, -5, 0L)]
    [InlineData(2L, 1_500_000_000, 2_999L)]
    [InlineData(long.MaxValue, 0, int.MaxValue * 1000L)]
    public void The_read_saturates_on_an_invalid_Duration(long seconds, int nanos, long expectedMs)
        => Assert.Equal(expectedMs, Spotify.Telemetry.PositionMsOf(new Duration { Seconds = seconds, Nanos = nanos }));

    // ── THE WRITE, pinned to bytes ──

    /// <summary>A position write is the OFFICIAL client's bytes: vc2 #308 left an episode at 820.912 s (its uri swapped for
    /// <see cref="E1"/>, the same length) — <c>{2 uri, 4 {2 {2 {1 820, 2 912000000}}, 3 {1 1789818513}}}</c>, the value's
    /// entity uri left out as that client leaves it. Byte for byte, so the unit can never drift again.</summary>
    [Fact]
    public void A_position_write_is_the_official_clients_bytes()
    {
        byte[] expected = [.. W.Hex("1226"), .. W.Ascii(E1), .. W.Hex("2215120b120908b406108088f0b2031a060891edb9d506")];
        Assert.Equal(expected, Spotify.Telemetry.ResumeRevision(E1, 820_912, 1_789_818_513_000).ToByteArray());
    }

    /// <summary>"Unplayed" (and a start at 0) is a PRESENT, empty Duration — <c>{2: {}}</c>, the official NOT_STARTED
    /// (vc1 #47's 5AUB) — never an arm-less value.</summary>
    [Fact]
    public void A_zero_write_is_a_present_empty_Duration()
    {
        byte[] expected = [.. W.Hex("1226"), .. W.Ascii(E1), .. W.Hex("220c120212001a060891edb9d506")];
        Assert.Equal(expected, Spotify.Telemetry.ResumeRevision(E1, 0, 1_789_818_513_000).ToByteArray());
    }

    /// <summary>The write and the read agree end to end, through the wire bytes: what this device sends is what a
    /// hydrate on another one folds — a position keeps its milliseconds past 1000 s, and 0 stays unplayed.</summary>
    [Theory]
    [InlineData(754_000L)]
    [InlineData(0L)]
    [InlineData(998_943L)]
    [InlineData(3_600_999L)]
    public void What_the_write_sends_the_fold_reads_back(long positionMs)
    {
        byte[] wire = Spotify.Telemetry.ResumeRevision(E1, positionMs, 1_788_000_000_000).ToByteArray();
        Rs.CreateResumePointRevisionRequest sent = Rs.CreateResumePointRevisionRequest.Parser.ParseFrom(wire);
        Staging s = Staging.Rent();
        try
        {
            Spotify.Telemetry.FoldCurrentStates(Answer(new Rs.CurrentStateEntry { EntityUri = E1, Revisions = { sent.Revision } }), s);
            Assert.Equal((int)positionMs, s.Episodes[0].ProgressMs);
            Assert.Equal(1_788_000_000, s.Episodes[0].PlayedAt);
        }
        finally { Staging.Return(s); }
    }

    // ── THE READ, over answers framed around the captured bytes ──

    /// <summary>Captured positions fold to their milliseconds at Full, for Progress only (vc1 #47's cold answer): the four
    /// official values pathfinder confirmed to the millisecond, the NOT_STARTED <c>{}</c>, a nanos-only one — and the two
    /// the retired µs write left on the server, which read as what the server HOLDS (every client reads them so), not
    /// repaired: the next write replaces them.</summary>
    [Theory]
    [InlineData("1208088d011080a5963a", 141_122)]   // 3OaT {141, 122000000} — pathfinder IN_PROGRESS 141122
    [InlineData("12020814", 20_000)]                // 2oO4 {20} — pathfinder 20000
    [InlineData("120308bc0b", 1_468_000)]           // 5AFg {1468} — pathfinder 1468000
    [InlineData("1200", 0)]                         // 5AUB {} — pathfinder NOT_STARTED 0
    [InlineData("120510c08abf52", 173)]             // 0J2S {nanos 173000000}
    [InlineData("120510f8e69e02", 4)]               // 6lQJ: Wavee's 4 699 ms × 1000 — the server holds 4.7 ms
    [InlineData("12061098d2aadc03", 998)]           // 5f8Y: Wavee's 998 943 ms × 1000 — the server holds 0.999 s
    public void A_captured_position_folds_to_its_milliseconds_at_Full_for_Progress_only(string armHex, int expectedMs)
    {
        Rs.ListCurrentStatesResponse answer = W.Parse(W.Answer(
            W.State(E1, W.Revision(W.Value(E1, armHex), createdS: 1_783_547_220, updatedS: 1_783_547_225))));
        Staging s = Staging.Rent();
        try
        {
            Assert.Equal(1, Spotify.Telemetry.FoldCurrentStates(answer, s));

            ref StagedEpisode row = ref s.Episodes[0];
            Assert.Equal(Gid(E1), row.Id.Packed);
            Assert.Equal(expectedMs, row.ProgressMs);
            Assert.Equal(Authority.Full, row.Authority);
            Assert.Equal((uint)EpisodeFields.Progress, row.Known);
            Assert.Equal(1_783_547_220, row.PlayedAt);
        }
        finally { Staging.Return(s); }
    }

    /// <summary>The two empty markers (PROVISIONAL readings, findings §4.1): marker 3 — the official desktop's write at a
    /// fresh play's start (vc2 #359), 1Fwl in the cold answer — is STARTED at 0, never completed; marker 4 — 4FQr's older
    /// revision, stamped exactly its duration after a plausible start — is COMPLETED (the duration is not resident on the
    /// api thread, so the fold stores <see cref="int.MaxValue"/>, which the completion rule reads as finished).</summary>
    [Theory]
    [InlineData("1a00", 0, false)]
    [InlineData("2200", int.MaxValue, true)]
    public void A_marker_folds_to_started_at_zero_or_to_completed(string armHex, int expectedMs, bool completed)
    {
        Rs.ListCurrentStatesResponse answer = W.Parse(W.Answer(
            W.State(E1, W.Revision(W.Value(E1, armHex), createdS: 1_777_654_235, updatedS: 1_777_654_235))));
        Staging s = Staging.Rent();
        try
        {
            Assert.Equal(1, Spotify.Telemetry.FoldCurrentStates(answer, s));
            Assert.Equal(expectedMs, s.Episodes[0].ProgressMs);
            Assert.Equal(completed, Episode.Rules.Completed(s.Episodes[0].ProgressMs, 1_800_000));
            Assert.Equal(completed, Episode.Rules.Played(Episode.Rules.Pct(s.Episodes[0].ProgressMs, 1_800_000)));
        }
        finally { Staging.Return(s); }
    }

    /// <summary>NO arm is not "completed" (the retired reading: 0 of 31 captured episode states lack one) and a context
    /// arm is an album/playlist resume, never an episode position — both claim NOTHING, so the row keeps what it knew.
    /// The playlist state itself (vc1 #47's context shape, a synthetic track uri and row uid) is skipped by its uri.</summary>
    [Fact]
    public void No_arm_and_a_context_arm_stage_nothing()
    {
        const string Playlist = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M";
        Rs.ListCurrentStatesResponse answer = W.Parse(W.Answer(
            W.State(E1, W.Revision(W.Value(E1, ""), createdS: 1_788_000_000, updatedS: 1_788_000_000)),
            W.State(E2, W.Revision(W.Value(E2, W.ContextHex), createdS: 1_788_000_000, updatedS: 1_788_000_000)),
            W.State(Playlist, W.Revision(W.Value(Playlist, W.ContextHex), createdS: 1_788_000_000, updatedS: 1_788_000_000))));
        Staging s = Staging.Rent();
        try
        {
            Assert.Equal(Episode.Rules.ResumeArm.None, Spotify.Telemetry.ArmOf(answer.States[0].Revisions[0].Value));
            Assert.Equal(Episode.Rules.ResumeArm.Context, Spotify.Telemetry.ArmOf(answer.States[1].Revisions[0].Value));
            Assert.Equal(0, Spotify.Telemetry.FoldCurrentStates(answer, s));
            Assert.Equal(0, s.Episodes.Count);
        }
        finally { Staging.Return(s); }
    }

    /// <summary>A state carries every revision the server kept: the captured 4FQr held a 131 s position (newest, listed
    /// first) over an older marker 4. The fold reads the NEWEST, in either wire order — declared singular, the parser
    /// MERGED the two and the older marker won, reading a re-listened episode as finished at the older instant.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_newest_of_two_revisions_is_the_state_in_either_wire_order(bool olderFirst)
    {
        byte[] newer = W.Revision(W.Value(E1, "1203088301"), createdS: 1_778_186_788, updatedS: 1_778_186_791);
        byte[] older = W.Revision(W.Value(E1, "2200"), createdS: 1_777_654_235, updatedS: 1_777_654_235);
        Rs.ListCurrentStatesResponse answer = W.Parse(W.Answer(olderFirst ? W.State(E1, older, newer) : W.State(E1, newer, older)));
        Staging s = Staging.Rent();
        try
        {
            Assert.Equal(2, answer.States[0].Revisions.Count);
            Assert.Equal(1, Spotify.Telemetry.FoldCurrentStates(answer, s));
            Assert.Equal(131_000, s.Episodes[0].ProgressMs);
            Assert.Equal(1_778_186_788, s.Episodes[0].PlayedAt);
        }
        finally { Staging.Return(s); }
    }

    [Fact]
    public void A_newer_marker_4_over_an_older_position_reads_completed()
    {
        byte[] position = W.Revision(W.Value(E1, "1203088301"), createdS: 1_777_654_235, updatedS: 1_777_654_235);
        byte[] marker = W.Revision(W.Value(E1, "2200"), createdS: 1_778_186_788, updatedS: 1_778_186_791);
        Staging s = Staging.Rent();
        try
        {
            Spotify.Telemetry.FoldCurrentStates(W.Parse(W.Answer(W.State(E1, position, marker))), s);
            Assert.Equal(int.MaxValue, s.Episodes[0].ProgressMs);
            Assert.Equal(1_778_186_788, s.Episodes[0].PlayedAt);
        }
        finally { Staging.Return(s); }
    }

    /// <summary>What the proto does not declare survives a parse and a re-encode byte for byte — an undeclared field on
    /// a value and on a state — and so does the context arm, which is kept as raw bytes.</summary>
    [Fact]
    public void Unknown_fields_and_the_context_arm_round_trip_byte_for_byte()
    {
        const string Playlist = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M";
        byte[] episode = [.. W.State(E1, W.Revision(W.Value(E1, "1208088d011080a5963a" + "4803"),   // + field 9 = 3
                                                    createdS: 1_783_547_220, updatedS: 1_783_547_225)),
                          .. W.Hex("3801")];                                                       // + field 7 = 1
        byte[] wire = W.Answer(episode,
            W.State(Playlist, W.Revision(W.Value(Playlist, W.ContextHex), createdS: 1_788_000_000, updatedS: 1_788_000_000)));

        Assert.Equal(wire, W.Parse(wire).ToByteArray());
    }

    [Fact]
    public void PlayedAt_is_the_revisions_create_time_and_its_update_time_only_without_one()
    {
        Staging s = Staging.Rent();
        try
        {
            Spotify.Telemetry.FoldCurrentStates(Answer(
                State(E1, 1_000, createdS: 1_788_000_000, updatedS: 1_788_000_042),
                State(E2, 1_000, updatedS: 1_788_000_042)), s);

            Assert.Equal(1_788_000_000, s.Episodes[0].PlayedAt);
            Assert.Equal(1_788_000_042, s.Episodes[1].PlayedAt);
        }
        finally { Staging.Return(s); }
    }

    [Fact]
    public void Only_episode_states_with_a_value_are_staged_and_the_values_uri_stands_in_for_a_missing_one()
    {
        var history = new Rs.CurrentStateEntry
        {
            EntityUri = "spotify:list:play-history:v1",
            Revisions = { new Rs.CurrentStateRevision { Value = new Rs.CurrentStateValue { ItemUri = E1 } } },
        };
        var valueless = new Rs.CurrentStateEntry { EntityUri = E1, Revisions = { new Rs.CurrentStateRevision() } };
        var revisionless = new Rs.CurrentStateEntry { EntityUri = E1 };
        Staging s = Staging.Rent();
        try
        {
            int staged = Spotify.Telemetry.FoldCurrentStates(Answer(
                history, valueless, revisionless,
                State("spotify:track:4rOoJ6Egrf8K2IrywzwOMk", 5_000),
                State("spotify:episode:not-a-gid", 5_000),
                State("", 5_000, valueUri: E2)), s);

            Assert.Equal(1, staged);
            Assert.Equal(Gid(E2), s.Episodes[0].Id.Packed);
            Assert.Equal(5_000, s.Episodes[0].ProgressMs);
        }
        finally { Staging.Return(s); }
    }

    // ── settled without an answer (plan §6.1): a session that cannot reach Spotify never syncs ──

    /// <summary>Exactly the two phases that are the session's own verdict: Reconnecting (the offline launch's AP ladder,
    /// or a dealer drop) and Failed (terminal for the credential). Offline is the BOOT state before the posted login —
    /// settling on it would read "progress unavailable" for the first second of every launch — and every rung of the
    /// login ladder is still on its way to Online.</summary>
    [Theory]
    [InlineData(Spotify.SessionPhase.Offline, false)]
    [InlineData(Spotify.SessionPhase.Resolving, false)]
    [InlineData(Spotify.SessionPhase.Connecting, false)]
    [InlineData(Spotify.SessionPhase.Handshaking, false)]
    [InlineData(Spotify.SessionPhase.Authenticating, false)]
    [InlineData(Spotify.SessionPhase.Minting, false)]
    [InlineData(Spotify.SessionPhase.Online, false)]
    [InlineData(Spotify.SessionPhase.Reconnecting, true)]
    [InlineData(Spotify.SessionPhase.Failed, true)]
    public void Only_a_reconnecting_or_failed_session_cannot_reach_spotify(Spotify.SessionPhase phase, bool expected)
        => Assert.Equal(expected, Spotify.Telemetry.ProgressUnreachable(phase));

    /// <summary>The settle rule: FAILED only when the phase is unreachable AND no hydrate for the scope is on the wire
    /// (its own landing settles it) AND none has landed (a dealer drop never raises "unavailable" over true rows) AND it
    /// has not already been said (the ladder re-enters Reconnecting on every backoff retry).</summary>
    [Theory]
    [InlineData(Spotify.SessionPhase.Reconnecting, false, false, false, true)]    // the offline launch
    [InlineData(Spotify.SessionPhase.Failed, false, false, false, true)]          // no credential, rejected, not Premium
    [InlineData(Spotify.SessionPhase.Reconnecting, true, false, false, false)]    // an answer is on its way
    [InlineData(Spotify.SessionPhase.Reconnecting, false, true, false, false)]    // landed rows stand
    [InlineData(Spotify.SessionPhase.Reconnecting, false, false, true, false)]    // said once, not per retry
    [InlineData(Spotify.SessionPhase.Offline, false, false, false, false)]        // boot, before the login is posted
    [InlineData(Spotify.SessionPhase.Resolving, false, false, false, false)]      // the ladder is still climbing
    [InlineData(Spotify.SessionPhase.Online, false, false, false, false)]         // the sync asks instead
    public void An_unreachable_session_settles_the_hydrate_failed_only_when_nothing_else_will(
        Spotify.SessionPhase phase, bool onTheWire, bool landed, bool alreadyFailed, bool expected)
        => Assert.Equal(expected, Spotify.Telemetry.SettlesUnreachable(phase, onTheWire, landed, alreadyFailed));
}

/// <summary>The authority ladder for progress (plan §6.2: Seed &lt; Thin &lt; Full(wire) &lt; Local(player)), through the
/// REAL commit: a wire answer staged by the fold never rewinds a position this device wrote; and the landing rule's one
/// refinement — only a strictly newer revision is promoted over a local write.</summary>
[Collection(EntitiesCollection.Name)]
public class SpotifyTelemetryProgressAuthorityTests
{
    /// <summary>Even a NEWER Full row bounces off a Local one at the commit — which is exactly why the hydrate's landing
    /// has its own Promote step for a strictly newer revision (the theory below), and why nothing else can rewind it.</summary>
    [Fact]
    public void A_Full_wire_position_never_overwrites_a_Local_one_at_the_commit()
    {
        TestScope.Fresh();
        EntityId id = SpotifyTelemetryProgressTests.Gid(SpotifyTelemetryProgressTests.E1);
        Staging local = Staging.Rent();
        Entities.StageLocalProgress(local, id, 600_000, 1_788_000_100_000);
        TestScope.CommitAndPublish(local);

        Staging wire = Staging.Rent();
        Spotify.Telemetry.FoldCurrentStates(SpotifyTelemetryProgressTests.Answer(
            SpotifyTelemetryProgressTests.State(SpotifyTelemetryProgressTests.E1, 100_000, createdS: 1_788_000_200)), wire);
        TestScope.CommitAndPublish(wire);

        Episode e = Entities.Episode(id);
        Assert.Equal(600_000, e.ProgressMs);
        Assert.Equal(1_788_000_100, e.PlayedAt);
    }

    [Fact]
    public void A_Full_wire_position_fills_a_row_with_no_progress_and_a_later_one_replaces_it()
    {
        TestScope.Fresh();
        EntityId id = SpotifyTelemetryProgressTests.Gid(SpotifyTelemetryProgressTests.E1);
        foreach ((long ms, long created) in new[] { (100_000L, 1_788_000_000L), (200_000L, 1_788_000_300L) })
        {
            Staging wire = Staging.Rent();
            Spotify.Telemetry.FoldCurrentStates(SpotifyTelemetryProgressTests.Answer(
                SpotifyTelemetryProgressTests.State(SpotifyTelemetryProgressTests.E1, ms, createdS: created)), wire);
            TestScope.CommitAndPublish(wire);
        }

        Episode e = Entities.Episode(id);
        Assert.True(e.Knows(EpisodeFields.Progress));
        Assert.Equal(200_000, e.ProgressMs);
        Assert.Equal(1_788_000_300, e.PlayedAt);
    }

    /// <summary>The landing, over real rows: a resident Local position is replaced by a STRICTLY newer revision (the
    /// account played on elsewhere), kept against an equal or older one (its group withdrawn from the batch), and a row
    /// that is not resident lands as staged.</summary>
    [Theory]
    [InlineData(1_788_000_200L, 1, 0, 100_000)]       // newer: promoted, the other device's position lands
    [InlineData(1_788_000_100L, 0, 1, 600_000)]       // this device's own revision read back: kept
    [InlineData(1_788_000_050L, 0, 1, 600_000)]       // a stale server position: kept
    public void The_landing_promotes_only_a_strictly_newer_revision_over_a_local_write(long createdS, int promotedExpected,
                                                                                     int keptExpected, int progressExpected)
    {
        TestScope.Fresh();
        EntityId id = SpotifyTelemetryProgressTests.Gid(SpotifyTelemetryProgressTests.E1);
        Staging local = Staging.Rent();
        Entities.StageLocalProgress(local, id, 600_000, 1_788_000_100_000);
        TestScope.CommitAndPublish(local);

        Staging wire = Staging.Rent();
        Spotify.Telemetry.FoldCurrentStates(SpotifyTelemetryProgressTests.Answer(
            SpotifyTelemetryProgressTests.State(SpotifyTelemetryProgressTests.E1, 100_000, createdS: createdS),
            SpotifyTelemetryProgressTests.State(SpotifyTelemetryProgressTests.E2, 42_000, createdS: createdS)), wire);
        int promoted = Spotify.Telemetry.ReconcileProgress(wire, Entities.Current.Episodes, out int kept);
        TestScope.CommitAndPublish(wire);

        Assert.Equal(promotedExpected, promoted);
        Assert.Equal(keptExpected, kept);
        Assert.Equal(progressExpected, Entities.Episode(id).ProgressMs);
        Assert.Equal(42_000, Entities.Episode(SpotifyTelemetryProgressTests.Gid(SpotifyTelemetryProgressTests.E2)).ProgressMs);
    }

    [Theory]
    [InlineData(false, Authority.Local, 100, 50, EpisodeProgress.HydrateLanding.Land)]      // no progress yet: fill the hole
    [InlineData(true, Authority.Full, 100, 50, EpisodeProgress.HydrateLanding.Land)]        // a wire rung: the later answer
    [InlineData(true, Authority.Seed, 100, 50, EpisodeProgress.HydrateLanding.Land)]
    [InlineData(true, Authority.Local, 100, 101, EpisodeProgress.HydrateLanding.Promote)]   // played on elsewhere since
    [InlineData(true, Authority.Local, 100, 100, EpisodeProgress.HydrateLanding.Skip)]      // this device's own revision
    [InlineData(true, Authority.Local, 100, 99, EpisodeProgress.HydrateLanding.Skip)]       // a stale server position
    public void Only_a_strictly_newer_revision_lands_over_a_local_write(bool knows, Authority resident, int residentPlayedAt,
                                                                       int incomingPlayedAt, EpisodeProgress.HydrateLanding expected)
        => Assert.Equal(expected, EpisodeProgress.Landing(knows, resident, residentPlayedAt, incomingPlayedAt));
}

/// <summary>Herodotus answers framed by hand around bytes copied from the decoded bodies of the 2026-09-19 capture of the
/// official client (C:\WAVEE\wavee-captures\fresh-client-2026-09-19, vc1 #47 / vc2 #46, #308, #359) — every uri, uuid and
/// row uid synthetic — so the parser meets the wire's OWN shape, a repeated revision field included, rather than
/// whatever the generated encoder would have written.</summary>
internal static class HerodotusWire
{
    internal static byte[] Hex(string hex) => Convert.FromHexString(hex);
    internal static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>vc1 #47's playlist context arm (field 12): <c>{1 item uri, 2 {1 item uri, 2 Duration {35, 103000000}},
    /// 3 = 1, 4 row uid}</c>, with a synthetic track uri and uid of the captured lengths (36, 16).</summary>
    internal static string ContextHex => Convert.ToHexString(ContextArm());

    static byte[] ContextArm()
        => [.. Hex("626b0a24"), .. Ascii("spotify:track:4rOoJ6Egrf8K2IrywzwOMk"),
            .. Hex("122f0a24"), .. Ascii("spotify:track:4rOoJ6Egrf8K2IrywzwOMk"),
            .. Hex("1207082310c0cf8e3118012210"), .. Ascii("0123456789abcdef")];

    /// <summary>A length-delimited field: its tag, a varint length, the parts concatenated.</summary>
    internal static byte[] Len(int field, params byte[][] parts)
    {
        var body = new List<byte>();
        foreach (byte[] part in parts) body.AddRange(part);
        var o = new List<byte>(body.Count + 4) { (byte)(field << 3 | 2) };
        Varint(o, (ulong)body.Count);
        o.AddRange(body);
        return o.ToArray();
    }

    static void Varint(List<byte> o, ulong n)
    {
        for (; n >= 0x80; n >>= 7) o.Add((byte)((n & 0x7F) | 0x80));
        o.Add((byte)n);
    }

    /// <summary>A Timestamp of whole seconds: <c>{1: seconds}</c>.</summary>
    internal static byte[] Seconds(long seconds)
    {
        var o = new List<byte> { 0x08 };
        Varint(o, (ulong)seconds);
        return o.ToArray();
    }

    /// <summary>A <c>CurrentStateValue</c>: its entity uri (field 1, as the server answers it), then the arm's captured
    /// bytes verbatim (empty = no arm).</summary>
    internal static byte[] Value(string uri, string armHex) => [.. Len(1, Ascii(uri)), .. Hex(armHex)];

    /// <summary>A <c>CurrentStateRevision</c>: <c>{1 uuid, 2 value, 3 create_time, 4 update_time}</c>.</summary>
    internal static byte[] Revision(byte[] value, long createdS, long updatedS)
        => [.. Len(1, Ascii("00000000-0000-4000-8000-000000000000")), .. Len(2, value),
            .. Len(3, Seconds(createdS)), .. Len(4, Seconds(updatedS))];

    /// <summary>A <c>CurrentStateEntry</c>: <c>{1 uri, 2 revision, 2 revision, …}</c> — field 2 as often as it is given.</summary>
    internal static byte[] State(string uri, params byte[][] revisions)
    {
        var o = new List<byte>(Len(1, Ascii(uri)));
        foreach (byte[] revision in revisions) o.AddRange(Len(2, revision));
        return o.ToArray();
    }

    /// <summary>A <c>ListCurrentStatesResponse</c>: each state as field 1.</summary>
    internal static byte[] Answer(params byte[][] states)
    {
        var o = new List<byte>();
        foreach (byte[] state in states) o.AddRange(Len(1, state));
        return o.ToArray();
    }

    internal static Rs.ListCurrentStatesResponse Parse(byte[] wire) => Rs.ListCurrentStatesResponse.Parser.ParseFrom(wire);
}
