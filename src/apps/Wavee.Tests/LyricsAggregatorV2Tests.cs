// ── Wavee.Tests/LyricsAggregatorV2Tests.cs — the aggregator half of reranker v2 (W2-F) ────────────────────────────
//
// lyrics-grey-sources-implementation.md "Reranker v2 design" §5 and §8 plus corrections 4, 10, 15 and 17, driven
// end to end through `Lyrics.Aggregator` over in-memory FAKE sources: the first-pass lock-in (a pending reference keeps
// the fan-out alive), the provisional disk write and its replay upgrade, the consensus reference when Spotify has no
// lyrics, the gold guard against an ISRC decoy, lrclib's metadata pick, and the re-fetch reaching every source's own
// per-track memory. No network, ever; the disk cache lives in a temp directory the test deletes.

using System.Diagnostics;

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(LyricsDiagCollection.Name)]
public class LyricsAggregatorV2Tests
{
    /// <summary>A source whose answer (and its timing) the test controls. Counts calls and <see cref="Forget"/>s.</summary>
    sealed class Src(string id, Lyrics.MatchBasis basis, Func<Lyrics.Request, CancellationToken, Task<Lyrics.Doc?>> answer,
        double prior = 0.5) : Lyrics.ISource
    {
        public int Calls;
        public int Forgets;
        public string Id => id;
        public bool Enabled => true;
        public double Prior => prior;

        public async Task<Lyrics.Candidate?> FetchAsync(Lyrics.Request req, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var doc = await answer(req, ct);
            return doc is null ? null : new Lyrics.Candidate(id, prior, basis, doc);
        }

        public void Forget(string trackId) => Interlocked.Increment(ref Forgets);
    }

    sealed class Flag
    {
        volatile bool _set;
        public bool IsSet => _set;
        public void Set() => _set = true;
    }

    static Func<Lyrics.Request, CancellationToken, Task<Lyrics.Doc?>> Now(Func<string, Lyrics.Doc?> doc)
        => (r, _) => Task.FromResult(doc(r.TrackId));

    static Func<string, CancellationToken, Task<Lyrics.Request?>> Resolver(Action? onCall = null, string? isrc = null)
        => (id, _) =>
        {
            onCall?.Invoke();
            return Task.FromResult<Lyrics.Request?>(
                new Lyrics.Request(id, "spotify:track:" + id, "Song", ["Artist"], "Album", 200_000, isrc));
        };

    static readonly (long, string)[] RealSong =
    [
        (1000, "hello darkness my old friend"),
        (5000, "ive come to talk with you again"),
        (9000, "because a vision softly creeping"),
        (13000, "left its seeds while i was sleeping"),
        (17000, "and the vision that was planted"),
        (21000, "in my brain still remains"),
    ];

    static readonly (long, string)[] WrongSong =
    [
        (1000, "never gonna give you up"),
        (4000, "never gonna let you down"),
        (7000, "never gonna run around"),
        (10000, "and desert you tonight"),
    ];

    static Lyrics.Doc LineDoc(string trackId, string provider, (long Ms, string Text)[] lines, long shiftMs = 0)
    {
        var l = new List<Lyrics.Line>(lines.Length);
        foreach (var (ms, text) in lines) l.Add(new Lyrics.Line(ms + shiftMs, text, []));
        return new Lyrics.Doc(trackId, true, l, Lyrics.SyncKind.Line, provider);
    }

    static Lyrics.Doc WordDoc(string trackId, string provider, (long Ms, string Text)[] lines, long spanMs = 2500)
    {
        var l = new List<Lyrics.Line>(lines.Length);
        foreach (var (ms, text) in lines)
            l.Add(new Lyrics.Line(ms, text, [new Lyrics.Syllable(ms, ms + spanMs, text)], ms + spanMs, null, null, true));
        return new Lyrics.Doc(trackId, true, l, Lyrics.SyncKind.Syllable, provider);
    }

    static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");

    static string TempDir() => Path.Combine(Path.GetTempPath(), "wavee-lyrics-v2-" + Guid.NewGuid().ToString("N"));

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    static Lyrics.SourceTrace Trace(Lyrics.SearchReport report, string sourceId)
    {
        foreach (var t in report.Sources) if (t.SourceId == sourceId) return t;
        throw new InvalidOperationException("no trace for " + sourceId);
    }

    static Lyrics.SourceTrace WinnerTrace(Lyrics.SearchReport report)
    {
        foreach (var t in report.Sources) if (t.Winner) return t;
        throw new InvalidOperationException("no winner trace in: " + report.Summary);
    }

    // ── §8: provisional disk write + the replay upgrade ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unverified_winner_is_persisted_provisional_and_a_replay_re_searches_it()
    {
        string dir = TempDir();
        try
        {
            var disk = new Lyrics.DiskCache(dir);
            string id = NewId("prov");
            // A lone search-matched karaoke: syllable-rich, but with no reference and no agreeing peer it is never
            // verified — exactly the document that must not lock in forever.
            var kugou = new Src("kugou", Lyrics.MatchBasis.MetadataSearch, Now(t => WordDoc(t, "kugou", RealSong)));
            var agg = new Lyrics.Aggregator([kugou], Resolver(), new Lyrics.Options(5000, 5000, 0), diskCache: disk);

            var first = await agg.GetAsync(id);
            Assert.NotNull(first);
            Assert.Equal(Lyrics.SyncKind.Syllable, first!.Sync);

            // The winner save is fire-and-forget: poll the file until it lands.
            Lyrics.CacheEntry entry = default;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                entry = await disk.TryLoadAsync(id);
                if (entry.Outcome == Lyrics.CacheOutcome.Hit) break;
                await Task.Delay(20);
            }
            Assert.Equal(Lyrics.CacheOutcome.Hit, entry.Outcome);
            Assert.True(entry.Provisional);

            // Replay in a fresh process (fresh memory): the disk answers at once, and — although the document is at the
            // top richness tier — the provisional mark sends it through the background re-search.
            int resolves = 0;
            var kugou2 = new Src("kugou", Lyrics.MatchBasis.MetadataSearch, Now(t => WordDoc(t, "kugou", RealSong)));
            var replay = new Lyrics.Aggregator([kugou2], Resolver(() => Interlocked.Increment(ref resolves)),
                new Lyrics.Options(5000, 5000, 0), diskCache: disk);

            var hit = await replay.GetAsync(id);
            Assert.NotNull(hit);
            Assert.Equal(Lyrics.SyncKind.Syllable, hit!.Sync);
            Assert.True(await Eventually(() => Volatile.Read(ref kugou2.Calls) > 0));
            Assert.Equal(1, Volatile.Read(ref resolves));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task The_provisional_mark_round_trips_and_an_old_envelope_reads_as_verified()
    {
        string dir = TempDir();
        try
        {
            var disk = new Lyrics.DiskCache(dir);
            var doc = LineDoc("a", "lrclib", RealSong);
            await disk.SaveAsync("a", doc, provisional: true);
            await disk.SaveAsync("b", doc with { TrackId = "b" });

            Assert.True((await disk.TryLoadAsync("a")).Provisional);
            var plain = await disk.TryLoadAsync("b");
            Assert.Equal(Lyrics.CacheOutcome.Hit, plain.Outcome);
            Assert.False(plain.Provisional);
            // Written only when true: an entry without the key is what every older build wrote.
            Assert.DoesNotContain("\"prov\"", await File.ReadAllTextAsync(disk.PathFor("b")), StringComparison.Ordinal);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void A_track_wide_miss_is_trusted_for_a_week()
        => Assert.Equal(TimeSpan.FromDays(7), Lyrics.DiskCache.DefaultNegativeTtl);

    // ── §8: a pending reference keeps the fan-out alive ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_pending_reference_is_not_cancelled_when_the_first_pass_returns()
    {
        string id = NewId("refpending");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new Flag();
        // A verified, identity-matched karaoke lands first; the reference is still on its way when the grace window
        // (0 ms) closes. The old lock-in (richness 3 ⇒ done) cancelled it.
        var amll = new Src("amll", Lyrics.MatchBasis.Identity, Now(t => WordDoc(t, "amll", RealSong)));
        var spotify = new Src("spotify", Lyrics.MatchBasis.Identity, async (r, ct) =>
        {
            using var reg = ct.Register(cancelled.Set);
            await release.Task;
            return LineDoc(r.TrackId, "spotify", RealSong);
        });
        var agg = new Lyrics.Aggregator([amll, spotify], Resolver(), new Lyrics.Options(5000, 5000, 0));

        var first = await agg.GetAsync(id);

        Assert.NotNull(first);
        Assert.False(cancelled.IsSet);   // decided synchronously before GetAsync returned: the reference was kept

        release.SetResult();
        Assert.True(await Eventually(() =>
            Lyrics.Diag.ForTrack(id)?.Summary.Contains("background complete", StringComparison.Ordinal) == true));
        var report = Lyrics.Diag.ForTrack(id)!;
        Assert.Equal(Lyrics.Outcome.Hit, Trace(report, "spotify").Outcome);
        Assert.Contains("ref=spotify", report.Summary, StringComparison.Ordinal);
        Assert.False(cancelled.IsSet);
    }

    // ── §5 + correction 15: the consensus reference ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_spotify_three_agreeing_sources_form_the_reference_and_beat_a_wrong_songs_karaoke()
    {
        string id = NewId("consensus");
        var spotify = new Src("spotify", Lyrics.MatchBasis.Identity, Now(_ => null));   // Spotify has no lyrics
        var qq = new Src("qq", Lyrics.MatchBasis.MetadataSearch, Now(t => LineDoc(t, "qq", RealSong, 100)));
        var kugou = new Src("kugou", Lyrics.MatchBasis.MetadataSearch, Now(t => LineDoc(t, "kugou", RealSong)));
        var netease = new Src("netease", Lyrics.MatchBasis.MetadataSearch, Now(t => LineDoc(t, "netease", RealSong, -100)));
        // Word-synced, a high prior, internally coherent — and the wrong song.
        var wrong = new Src("musixmatch", Lyrics.MatchBasis.MetadataSearch, Now(t => WordDoc(t, "musixmatch", WrongSong)), prior: 0.9);
        var agg = new Lyrics.Aggregator([wrong, spotify, qq, kugou, netease], Resolver(), new Lyrics.Options(5000, 5000, 2000));

        var winner = await agg.GetAsync(id);

        Assert.NotNull(winner);
        Assert.Equal(Lyrics.SyncKind.Line, winner!.Sync);
        var report = Lyrics.Diag.ForTrack(id)!;
        Assert.Contains("ref=consensus(", report.Summary, StringComparison.Ordinal);
        Assert.NotEqual("musixmatch", WinnerTrace(report).SourceId);
        // The peers were aligned against the consensus reference (not ranked blind), and the reference itself was
        // measured against a peer — never against itself.
        Assert.Contains("ref-align", Trace(report, "qq").RerankReason, StringComparison.Ordinal);
        Assert.Contains("consensus-ref vs", Trace(report, "kugou").RerankReason, StringComparison.Ordinal);
        Assert.True(Trace(report, "kugou").Support >= 1);
        Assert.Equal(0, Trace(report, "musixmatch").Support);
    }

    [Fact]
    public async Task A_lone_candidate_is_never_its_own_reference()
    {
        string id = NewId("lone");
        var only = new Src("qq", Lyrics.MatchBasis.MetadataSearch, Now(t => LineDoc(t, "qq", RealSong)));
        var agg = new Lyrics.Aggregator([only], Resolver(), new Lyrics.Options(5000, 5000, 0));

        Assert.NotNull(await agg.GetAsync(id));
        Assert.Contains("ref=none", Lyrics.Diag.ForTrack(id)!.Summary, StringComparison.Ordinal);
    }

    // ── correction 17: gold needs the guard, not just the basis ─────────────────────────────────────────────────────

    [Fact]
    public async Task An_isrc_matched_decoy_never_short_circuits_the_fan_out()
    {
        string id = NewId("gold");
        var slowDone = new Flag();
        // ISRC-matched, word-synced, plausibly timed — and nothing like what Spotify says the song is.
        var decoy = new Src("musixmatch", Lyrics.MatchBasis.Isrc, Now(t => WordDoc(t, "musixmatch",
        [
            (1000, "zzz qqq xxx"), (5000, "yyy www vvv"), (9000, "uuu ttt sss"),
            (13000, "rrr ppp ooo"), (17000, "nnn mmm lll"), (21000, "kkk jjj hhh"),
        ])), prior: 0.7);
        var spotify = new Src("spotify", Lyrics.MatchBasis.Identity, Now(t => LineDoc(t, "spotify", RealSong)));
        var slow = new Src("lrclib", Lyrics.MatchBasis.MetadataSearch, async (r, ct) =>
        {
            await Task.Delay(300, ct);
            slowDone.Set();
            return LineDoc(r.TrackId, "lrclib", RealSong);
        });
        var agg = new Lyrics.Aggregator([decoy, spotify, slow], Resolver(isrc: "USXX12345678"),
            new Lyrics.Options(5000, 5000, 3000));

        var winner = await agg.GetAsync(id);

        // Basis-only gold would have broken out the moment the reference arrived, before the slow source answered.
        Assert.True(slowDone.IsSet);
        Assert.NotNull(winner);
        Assert.NotEqual("musixmatch", WinnerTrace(Lyrics.Diag.ForTrack(id)!).SourceId);
    }

    // ── lrclib: the stage-0 metadata pick ───────────────────────────────────────────────────────────────────────────

    sealed class FakeHttp(Func<string, string?> answer) : Lyrics.IHttp
    {
        public Task<string?> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
            => Task.FromResult(answer(url));
    }

    const string LiveCut = """
        {"id":11,"trackName":"Song (Live)","artistName":"Artist","albumName":"Live at the Hall","duration":200.0,
         "instrumental":false,"plainLyrics":"live first line","syncedLyrics":"[00:01.00]live first line\n[00:05.00]live second line"}
        """;

    const string Studio = """
        {"id":12,"trackName":"Song","artistName":"Artist","albumName":"Album","duration":201.0,
         "instrumental":false,"plainLyrics":"studio first line","syncedLyrics":"[00:01.00]studio first line\n[00:05.00]studio second line"}
        """;

    static Lyrics.Request LrcRequest() => new("t", "spotify:track:t", "Song", ["Artist"], "Album", 200_000);

    [Fact]
    public async Task Lrclib_picks_by_metadata_match_and_skips_a_live_cut_with_the_closer_duration()
    {
        // The live cut is the CLOSER duration (0 ms vs +1000 ms): the old closest-duration rule took it.
        var http = new FakeHttp(url => url.Contains("/api/search", StringComparison.Ordinal) ? "[" + LiveCut + "," + Studio + "]" : null);

        var c = await new Lyrics.Sources.LrcLib(http).FetchAsync(LrcRequest(), CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal("studio first line", c!.Document.Lines[0].Text);
        Assert.Equal(Lyrics.MatchBasis.MetadataSearch, c.Basis);
        Assert.True(c.Band >= Lyrics.MatchBand.Medium);
        Assert.True(c.Confidence is > 0d and < 1d);
        Assert.False(string.IsNullOrEmpty(c.MatchNote));
    }

    [Fact]
    public async Task Lrclib_answers_nothing_when_only_a_gated_version_exists()
    {
        var http = new FakeHttp(url => url.Contains("/api/search", StringComparison.Ordinal) ? "[" + LiveCut + "]" : null);

        Assert.Null(await new Lyrics.Sources.LrcLib(http).FetchAsync(LrcRequest(), CancellationToken.None));
    }

    // ── correction 10: the re-fetch reaches every source's own memory ───────────────────────────────────────────────

    [Fact]
    public async Task Refetch_makes_every_source_forget_the_track()
    {
        string id = NewId("refetch");
        var a = new Src("spotify", Lyrics.MatchBasis.Identity, Now(t => LineDoc(t, "spotify", RealSong)));
        var b = new Src("qq", Lyrics.MatchBasis.MetadataSearch, Now(_ => null));
        var agg = new Lyrics.Aggregator([a, b], Resolver(), new Lyrics.Options(5000, 5000, 0));

        await agg.GetAsync(id);
        Assert.Equal(0, a.Forgets);
        await agg.RefetchAsync(id);

        Assert.Equal(1, a.Forgets);
        Assert.Equal(1, b.Forgets);
        Assert.Equal(2, Volatile.Read(ref a.Calls));   // and the fan-out really ran again
    }
}
