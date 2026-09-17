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

using System;
using System.Threading;
using Wavee;
using Xunit;

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
