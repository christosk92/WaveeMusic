// ── Wavee.Tests/LyricsGreyInfraTests.cs — the grey sources' shared infrastructure ─────────────────────────────────
//
// `Lyrics.SourceGuard` (negative cache TTLs, circuit breaker, trips, forget), its persisted `Lyrics.MissLedger`
// (round trip, TTL prune on load, cap, corrupt file) and `Lyrics.GreyLadder` (pooling across query variants, early
// stop, ≤ 2 results ≥ Medium, best first). Pure: the clock is injected, the ledger points at a fresh temp folder the
// test deletes, and the ladder searches through an in-memory fake — no network, never the real profile.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class LyricsGreyInfraTests
{
    const long Hour = 3_600_000L;
    const long Minute = 60_000L;

    sealed class Clock
    {
        public long Now = 1_700_000_000_000L;
        public long Read() => Now;
    }

    sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "wavee-grey-" + Guid.NewGuid().ToString("N"));
        public string Ledger => Path.Combine(Root, Lyrics.MissLedger.FileName);
        public void Dispose() { try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { } }
    }

    // A ledger that writes ONLY on Flush — deterministic, no background write racing the temp folder's deletion.
    static Lyrics.MissLedger Ledger(string? path, Clock clock) => new(path, clock.Read, debounceMs: -1);

    // ── SourceGuard: the negative cache ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ttls_are_6h_for_not_found_and_24h_for_no_lyric()
    {
        Assert.Equal(6 * Hour, Lyrics.SourceGuard.Ttl(Lyrics.SourceGuard.MissKind.SongNotFound));
        Assert.Equal(24 * Hour, Lyrics.SourceGuard.Ttl(Lyrics.SourceGuard.MissKind.NoLyricForSong));
    }

    [Fact]
    public void A_song_not_found_miss_lives_six_hours()
    {
        var clock = new Clock();
        var guard = new Lyrics.SourceGuard("kugou", clock.Read);

        guard.Miss("t1", Lyrics.SourceGuard.MissKind.SongNotFound);
        Assert.True(guard.TryGetMiss("t1", out var kind, out long age));
        Assert.Equal(Lyrics.SourceGuard.MissKind.SongNotFound, kind);
        Assert.Equal(0, age);

        clock.Now += 6 * Hour - 1;
        Assert.True(guard.TryGetMiss("t1", out _, out age));
        Assert.Equal(6 * Hour - 1, age);

        clock.Now += 1;
        Assert.False(guard.TryGetMiss("t1", out _, out _));
    }

    [Fact]
    public void A_no_lyric_miss_lives_a_day()
    {
        var clock = new Clock();
        var guard = new Lyrics.SourceGuard("qq", clock.Read);

        guard.Miss("t1", Lyrics.SourceGuard.MissKind.NoLyricForSong);
        clock.Now += 7 * Hour;   // past the not-found TTL
        Assert.True(guard.TryGetMiss("t1", out var kind, out _));
        Assert.Equal(Lyrics.SourceGuard.MissKind.NoLyricForSong, kind);

        clock.Now += 17 * Hour;  // exactly 24 h
        Assert.False(guard.TryGetMiss("t1", out _, out _));
    }

    [Fact]
    public void Forget_drops_one_tracks_miss_and_only_that_one()
    {
        var clock = new Clock();
        var guard = new Lyrics.SourceGuard("netease", clock.Read);
        guard.Miss("t1", Lyrics.SourceGuard.MissKind.NoLyricForSong);
        guard.Miss("t2", Lyrics.SourceGuard.MissKind.NoLyricForSong);

        guard.Forget("t1");

        Assert.False(guard.TryGetMiss("t1", out _, out _));
        Assert.True(guard.TryGetMiss("t2", out _, out _));
    }

    [Fact]
    public void Misses_are_per_source_even_on_a_shared_ledger()
    {
        var clock = new Clock();
        var ledger = Ledger(null, clock);
        var kugou = new Lyrics.SourceGuard("kugou", clock.Read, ledger);
        var qq = new Lyrics.SourceGuard("qq", clock.Read, ledger);

        kugou.Miss("t1", Lyrics.SourceGuard.MissKind.SongNotFound);

        Assert.True(kugou.TryGetMiss("t1", out _, out _));
        Assert.False(qq.TryGetMiss("t1", out _, out _));
    }

    // ── SourceGuard: the breaker ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Three_consecutive_failures_open_the_breaker_for_ten_minutes()
    {
        var clock = new Clock();
        var guard = new Lyrics.SourceGuard("musixmatch", clock.Read);

        guard.Failure("503");
        guard.Failure("503");
        Assert.False(guard.IsOpen(out _, out _));

        guard.Failure("503");
        Assert.True(guard.IsOpen(out string reason, out long until));
        Assert.Equal(clock.Now + 10 * Minute, until);
        Assert.Contains("503", reason);

        clock.Now += 10 * Minute - 1;
        Assert.True(guard.IsOpen(out _, out _));
        clock.Now += 1;
        Assert.False(guard.IsOpen(out reason, out until));
        Assert.Equal("", reason);
        Assert.Equal(0, until);
    }

    [Fact]
    public void A_success_resets_the_failure_run()
    {
        var clock = new Clock();
        var guard = new Lyrics.SourceGuard("kugou", clock.Read);

        guard.Failure("timeout");
        guard.Failure("timeout");
        guard.Success();
        guard.Failure("timeout");
        guard.Failure("timeout");

        Assert.False(guard.IsOpen(out _, out _));
    }

    [Fact]
    public void Trip_opens_for_its_own_window_and_a_shorter_trip_never_shortens_it()
    {
        var clock = new Clock();
        var guard = new Lyrics.SourceGuard("musixmatch", clock.Read);

        guard.Trip("captcha", 2 * Hour);
        Assert.True(guard.IsOpen(out string reason, out long until));
        Assert.Equal("captcha", reason);
        Assert.Equal(clock.Now + 2 * Hour, until);

        guard.Trip("risk control", 30 * Minute);
        Assert.True(guard.IsOpen(out reason, out until));
        Assert.Equal("captcha", reason);
        Assert.Equal(clock.Now + 2 * Hour, until);

        clock.Now += 2 * Hour;
        Assert.False(guard.IsOpen(out _, out _));
    }

    [Fact]
    public void Trip_does_not_touch_the_negative_cache()
    {
        var clock = new Clock();
        var guard = new Lyrics.SourceGuard("qq", clock.Read);
        guard.Trip("quota", 6 * Hour);
        Assert.False(guard.TryGetMiss("t1", out _, out _));
    }

    // ── MissLedger: persistence ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Misses_survive_a_restart()
    {
        using var dir = new TempDir();
        var clock = new Clock();
        var before = Ledger(dir.Ledger, clock);
        var guard = new Lyrics.SourceGuard("kugou", clock.Read, before);
        guard.Miss("t1", Lyrics.SourceGuard.MissKind.NoLyricForSong);
        guard.Miss("t2", Lyrics.SourceGuard.MissKind.SongNotFound);
        before.Flush();
        Assert.True(File.Exists(dir.Ledger));

        clock.Now += Hour;
        var after = Ledger(dir.Ledger, clock);
        var restarted = new Lyrics.SourceGuard("kugou", clock.Read, after);

        Assert.True(restarted.TryGetMiss("t1", out var k1, out long age1));
        Assert.Equal(Lyrics.SourceGuard.MissKind.NoLyricForSong, k1);
        Assert.Equal(Hour, age1);
        Assert.True(restarted.TryGetMiss("t2", out var k2, out _));
        Assert.Equal(Lyrics.SourceGuard.MissKind.SongNotFound, k2);
        Assert.False(new Lyrics.SourceGuard("qq", clock.Read, after).TryGetMiss("t1", out _, out _));
    }

    [Fact]
    public void Expired_entries_are_pruned_on_load()
    {
        using var dir = new TempDir();
        var clock = new Clock();
        var before = Ledger(dir.Ledger, clock);
        var guard = new Lyrics.SourceGuard("netease", clock.Read, before);
        guard.Miss("keeps", Lyrics.SourceGuard.MissKind.NoLyricForSong);
        guard.Miss("expires", Lyrics.SourceGuard.MissKind.SongNotFound);
        before.Flush();

        clock.Now += 7 * Hour;
        var after = Ledger(dir.Ledger, clock);

        Assert.Equal(1, after.Count);
        Assert.True(after.TryGet("netease", "keeps", out _, out _));
        Assert.False(after.TryGet("netease", "expires", out _, out _));
    }

    [Fact]
    public void Forget_is_persisted()
    {
        using var dir = new TempDir();
        var clock = new Clock();
        var before = Ledger(dir.Ledger, clock);
        var guard = new Lyrics.SourceGuard("qq", clock.Read, before);
        guard.Miss("t1", Lyrics.SourceGuard.MissKind.NoLyricForSong);
        before.Flush();
        guard.Forget("t1");
        before.Flush();

        var after = Ledger(dir.Ledger, clock);
        Assert.Equal(0, after.Count);
    }

    [Fact]
    public void The_ledger_is_capped_and_keeps_the_newest()
    {
        var clock = new Clock();
        var ledger = Ledger(null, clock);
        for (int i = 0; i <= Lyrics.MissLedger.Cap; i++)
        {
            clock.Now++;
            ledger.Put("kugou", "t" + i, Lyrics.SourceGuard.MissKind.NoLyricForSong);
        }

        Assert.True(ledger.Count <= Lyrics.MissLedger.Cap);
        Assert.True(ledger.TryGet("kugou", "t" + Lyrics.MissLedger.Cap, out _, out _));   // the newest survives
        Assert.False(ledger.TryGet("kugou", "t0", out _, out _));                         // the oldest went first
    }

    [Fact]
    public void A_corrupt_ledger_file_is_an_empty_ledger()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Root);
        File.WriteAllText(dir.Ledger, "{ this is not json");
        var clock = new Clock();
        var ledger = Ledger(dir.Ledger, clock);

        Assert.Equal(0, ledger.Count);
        ledger.Put("kugou", "t1", Lyrics.SourceGuard.MissKind.SongNotFound);
        ledger.Flush();

        Assert.True(Ledger(dir.Ledger, clock).TryGet("kugou", "t1", out _, out _));
    }

    // ── GreyLadder ──────────────────────────────────────────────────────────────────────────────────────────────────

    // "Song" by "Artist": Query.Variants yields two keywords ("song artist", then "song").
    static Lyrics.Request Req() => new("t1", "spotify:track:t1", "Song", ["Artist"], "Album", 200_000);

    static Lyrics.Hit Exact(string id) => new(id, "Song", ["Artist"], "Album", 200_000, 1);

    // A minute off: over the 5 s duration hard gate, whatever else matches.
    static Lyrics.Hit Gated(string id) => new(id, "Song", ["Artist"], "Album", 260_000, 1);

    sealed class FakeSearch(Func<int, string, IReadOnlyList<Lyrics.Hit>?> answer)
    {
        public readonly List<string> Keywords = [];

        public Task<IReadOnlyList<Lyrics.Hit>?> Search(string keyword, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Keywords.Add(keyword);
            return Task.FromResult(answer(Keywords.Count - 1, keyword));
        }
    }

    static void AssertBestFirstAndAtLeastMedium(IReadOnlyList<(Lyrics.Hit Hit, Lyrics.MatchScore Score)> result)
    {
        Assert.True(result.Count <= Lyrics.GreyLadder.MaxResults);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < result.Count; i++)
        {
            Assert.True(result[i].Score.Band >= Lyrics.MatchBand.Medium);
            Assert.True(ids.Add(result[i].Hit.Id));
            if (i > 0) Assert.True(result[i - 1].Score.Band >= result[i].Score.Band);
        }
    }

    [Fact]
    public async Task A_perfect_hit_stops_the_ladder_at_the_first_variant()
    {
        Assert.True(Lyrics.Query.Variants(Req()).Count >= 2);
        var fake = new FakeSearch((_, _) => new[] { Exact("a") });

        var result = await Lyrics.GreyLadder.SearchAsync("kugou", Req(), fake.Search, CancellationToken.None);

        Assert.Single(fake.Keywords);
        var only = Assert.Single(result);
        Assert.Equal("a", only.Hit.Id);
        Assert.True(only.Score.Band >= Lyrics.MatchBand.VeryHigh);
    }

    [Fact]
    public async Task Hits_are_pooled_across_variants_and_deduplicated()
    {
        // Variant 0 finds only a wrong cut; variant 1 finds the wrong cut again plus the real song.
        var fake = new FakeSearch((i, _) => i == 0 ? new[] { Gated("far") } : new[] { Gated("far"), Exact("real"), Exact("real") });

        var result = await Lyrics.GreyLadder.SearchAsync("qq", Req(), fake.Search, CancellationToken.None);

        Assert.Equal(2, fake.Keywords.Count);
        var only = Assert.Single(result);
        Assert.Equal("real", only.Hit.Id);
        AssertBestFirstAndAtLeastMedium(result);
    }

    [Fact]
    public async Task At_most_two_hits_come_back_best_first_with_a_deterministic_tiebreak()
    {
        var fake = new FakeSearch((_, _) => new[] { Exact("d"), Exact("c"), Exact("b"), Exact("a") });

        var result = await Lyrics.GreyLadder.SearchAsync("netease", Req(), fake.Search, CancellationToken.None);

        Assert.Equal(2, result.Count);
        AssertBestFirstAndAtLeastMedium(result);
        Assert.Equal("a", result[0].Hit.Id);   // equal scores → ordinal id order, never arrival order
        Assert.Equal("b", result[1].Hit.Id);
    }

    [Fact]
    public async Task The_better_match_comes_first_whatever_the_arrival_order()
    {
        var weaker = new Lyrics.Hit("weaker", "Song", ["Artist"], "Album", 202_500, 1000);
        var fake = new FakeSearch((_, _) => new[] { weaker, Exact("exact") });

        var result = await Lyrics.GreyLadder.SearchAsync("kugou", Req(), fake.Search, CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.Equal("exact", result[0].Hit.Id);
        AssertBestFirstAndAtLeastMedium(result);
    }

    [Fact]
    public async Task Only_gated_hits_walk_every_variant_and_return_nothing()
    {
        var probe = new Lyrics.Probe();
        Lyrics.Probe.Current.Value = probe;
        var fake = new FakeSearch((i, _) => new[] { Gated("far" + i) });

        var result = await Lyrics.GreyLadder.SearchAsync("kugou", Req(), fake.Search, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(Lyrics.Query.Variants(Req()).Count, fake.Keywords.Count);
        Assert.Contains("rejected", probe.NotesFor("kugou"));
    }

    [Fact]
    public async Task A_failing_search_is_a_miss_not_a_throw()
    {
        var result = await Lyrics.GreyLadder.SearchAsync("qq", Req(),
            (_, _) => throw new InvalidOperationException("boom"), CancellationToken.None);
        Assert.Empty(result);

        var nulls = await Lyrics.GreyLadder.SearchAsync("qq", Req(),
            (_, _) => Task.FromResult<IReadOnlyList<Lyrics.Hit>?>(null), CancellationToken.None);
        Assert.Empty(nulls);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var fake = new FakeSearch((_, _) => new[] { Exact("a") });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Lyrics.GreyLadder.SearchAsync("musixmatch", Req(), fake.Search, cts.Token));
    }
}
