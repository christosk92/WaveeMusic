// ── Wavee.Tests/LyricsHostTests.cs — the pure halves of the lyrics host ───────────────────────────────────────────
//
// Ported suites: `Lyrics/LyricsCoreTests` (the reranker half), `Lyrics/LyricsDiskCacheTests` (the cache-key and
// envelope half), `Lyrics/LyricsAggregationTests` (the clean-at-one-chokepoint and decoy-gate half, over FAKE sources),
// `Lyrics/MusixmatchRichsyncFixtureTests` (the end-to-end rank on four real payloads).
//
// No network, ever: every source here is an in-memory fake, and the disk cache is pointed at a fresh temp directory
// that the test deletes — the real profile is never touched (the cache takes its directory as a constructor argument
// precisely so a test cannot reach `%LOCALAPPDATA%`).

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class LyricsRerankerTests
{
    static Lyrics.Doc LineDoc(string provider, params (long Ms, string Text)[] lines)
    {
        var l = new List<Lyrics.Line>(lines.Length);
        foreach (var (ms, text) in lines) l.Add(new Lyrics.Line(ms, text, []));
        return new Lyrics.Doc("t1", true, l, Lyrics.SyncKind.Line, provider);
    }

    static Lyrics.Doc WordDoc(string provider, long spanMs, params (long Ms, string Text)[] lines)
    {
        var l = new List<Lyrics.Line>(lines.Length);
        foreach (var (ms, text) in lines)
            l.Add(new Lyrics.Line(ms, text, [new Lyrics.Syllable(ms, ms + spanMs, text)], ms + spanMs, null, null, true));
        return new Lyrics.Doc("t1", true, l, Lyrics.SyncKind.Syllable, provider);
    }

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
    ];

    static Lyrics.Decision For(Lyrics.Ranked r, string id)
    {
        foreach (var d in r.All) if (d.ProviderId == id) return d;
        throw new InvalidOperationException("no decision for " + id);
    }

    [Fact]
    public void No_candidates_is_no_winner()
    {
        var ranked = Lyrics.Reranker.Rank([], LineDoc("spotify", RealSong));
        Assert.Null(ranked.Winner);
        Assert.Null(ranked.Best);
        Assert.Empty(ranked.All);
    }

    [Fact]
    public void A_wrong_songs_karaoke_loses_to_the_correct_line_lyric()
    {
        var reference = LineDoc("spotify", RealSong);
        var wrong = new Lyrics.Candidate("netease", 1.0, Lyrics.MatchBasis.MetadataSearch, WordDoc("netease", 400, WrongSong));
        var correct = new Lyrics.Candidate("lrclib", 0.4, Lyrics.MatchBasis.MetadataSearch, LineDoc("lrclib", RealSong));

        var ranked = Lyrics.Reranker.Rank([wrong, correct], reference);

        Assert.Equal("lrclib", ranked.Best!.ProviderId);
        Assert.Contains("sync-gate:demoted", For(ranked, "netease").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_identity_matched_word_sync_is_exempt_from_the_sync_gate()
    {
        var reference = LineDoc("spotify", RealSong);
        var amll = new Lyrics.Candidate("amll", 0.9, Lyrics.MatchBasis.Identity, WordDoc("amll", 2500,
            (1000, "totally different transcription aaa"), (5000, "totally different transcription bbb"),
            (9000, "totally different transcription ccc"), (13000, "totally different transcription ddd")));
        var d = For(Lyrics.Reranker.Rank([amll], reference), "amll");
        Assert.DoesNotContain("sync-gate:demoted", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_globally_offset_lrc_is_pulled_back_onto_the_reference()
    {
        var reference = LineDoc("spotify", RealSong);
        var shifted = new (long, string)[RealSong.Length];
        for (int i = 0; i < RealSong.Length; i++) shifted[i] = (RealSong[i].Item1 + 700, RealSong[i].Item2);
        var cand = new Lyrics.Candidate("lrclib", 0.4, Lyrics.MatchBasis.MetadataSearch, LineDoc("lrclib", shifted));

        var ranked = Lyrics.Reranker.Rank([cand], reference);

        Assert.Equal(-700, ranked.Best!.AppliedOffsetMs);
        Assert.Equal(1000, ranked.Winner!.Lines[0].StartMs);
        Assert.Equal(-700, ranked.Winner.OffsetMsApplied);
    }

    [Fact]
    public void An_impossibly_fast_word_sync_is_gated_AND_repaired_to_line_sync()
    {
        var reference = LineDoc("spotify", RealSong);
        // ISRC-matched, so the ORIGINAL sync gate exempts it as "the exact recording" — the word-timing gate must not.
        var burst = new Lyrics.Candidate("musixmatch", 0.9, Lyrics.MatchBasis.Isrc, WordDoc("musixmatch", 150, RealSong));

        var ranked = Lyrics.Reranker.Rank([burst], reference);

        Assert.Contains("word-timing-gate", ranked.Best!.Reason, StringComparison.Ordinal);
        Assert.Equal(Lyrics.SyncKind.Line, ranked.Winner!.Sync);
        for (int i = 0; i < RealSong.Length; i++)
        {
            Assert.Empty(ranked.Winner.Lines[i].Syllables);
            Assert.Equal(RealSong[i].Item1, ranked.Winner.Lines[i].StartMs);   // the part the payload got RIGHT
        }
    }

    [Fact]
    public void A_plausible_word_sync_is_untouched()
    {
        var reference = LineDoc("spotify", RealSong);
        var real = new Lyrics.Candidate("amll", 0.9, Lyrics.MatchBasis.Identity, WordDoc("amll", 2500, RealSong));
        var ranked = Lyrics.Reranker.Rank([real], reference);
        Assert.DoesNotContain("word-timing-gate", ranked.Best!.Reason, StringComparison.Ordinal);
        Assert.Equal(Lyrics.SyncKind.Syllable, ranked.Winner!.Sync);
    }

    [Fact]
    public void A_verified_word_sync_beats_the_reference_line_lyric()
    {
        var reference = LineDoc("spotify", RealSong);
        var spotify = new Lyrics.Candidate("spotify", 0.5, Lyrics.MatchBasis.Identity, reference);
        var amll = new Lyrics.Candidate("amll", 1.0, Lyrics.MatchBasis.Identity, WordDoc("amll", 2500, RealSong));
        var ranked = Lyrics.Reranker.Rank([spotify, amll], reference);
        Assert.Equal("amll", ranked.Best!.ProviderId);
        Assert.Equal(Lyrics.SyncKind.Syllable, ranked.Winner!.Sync);
    }

    [Fact]
    public void An_isrc_decoy_with_zero_text_agreement_is_never_verified()
    {
        var reference = LineDoc("spotify", RealSong);
        var decoy = new Lyrics.Candidate("musixmatch", 0.9, Lyrics.MatchBasis.Isrc, WordDoc("musixmatch", 2500,
            (1000, "zzz qqq xxx"), (5000, "yyy www vvv"), (9000, "uuu ttt sss"), (13000, "rrr ppp ooo"),
            (17000, "nnn mmm lll"), (21000, "kkk jjj hhh")));
        var truth = new Lyrics.Candidate("spotify", 0.55, Lyrics.MatchBasis.Identity, reference);

        var ranked = Lyrics.Reranker.Rank([decoy, truth], reference);

        Assert.False(For(ranked, "musixmatch").Verified);
        Assert.Equal("spotify", ranked.Best!.ProviderId);
    }

    [Fact]
    public void Real_karaoke_beats_the_correct_paragraph_on_the_captured_payloads()
    {
        // Cleaned exactly as the aggregator's chokepoint delivers them — INCLUDING the reference, which is itself a
        // candidate. With the raw documents the tier bar refuses the KRC on coverage alone (0.565).
        var reference = Lyrics.Clean.Apply(LyricsFixture.Reference(), LyricsFixture.Title, LyricsFixture.Artist);
        Lyrics.Candidate[] candidates =
        [
            new("musixmatch", 0.7, Lyrics.MatchBasis.Isrc,
                Lyrics.Clean.Apply(LyricsFixture.Subtitle(), LyricsFixture.Title, LyricsFixture.Artist)),
            new("kugou", 0.5, Lyrics.MatchBasis.MetadataSearch,
                Lyrics.Clean.Apply(LyricsFixture.Kugou(), LyricsFixture.Title, LyricsFixture.Artist)),
        ];

        var ranked = Lyrics.Reranker.Rank(candidates, reference);

        Assert.Equal("kugou", ranked.Best!.ProviderId);
        Assert.Equal(Lyrics.SyncKind.Syllable, ranked.Winner!.Sync);
    }

    [Fact]
    public void The_broken_richsync_never_reaches_the_view_as_karaoke()
    {
        var reference = Lyrics.Clean.Apply(LyricsFixture.Reference(), LyricsFixture.Title, LyricsFixture.Artist);
        var rich = new Lyrics.Candidate("musixmatch", 0.7, Lyrics.MatchBasis.Isrc,
            Lyrics.Clean.Apply(LyricsFixture.Richsync(), LyricsFixture.Title, LyricsFixture.Artist));

        var ranked = Lyrics.Reranker.Rank([rich], reference);

        Assert.Contains("word-timing-gate", ranked.Best!.Reason, StringComparison.Ordinal);
        Assert.Equal(Lyrics.SyncKind.Line, ranked.Winner!.Sync);
    }

    [Fact]
    public void Without_a_reference_trust_alone_verifies()
    {
        var amll = new Lyrics.Candidate("amll", 0.9, Lyrics.MatchBasis.Identity, WordDoc("amll", 2500, RealSong));
        var ranked = Lyrics.Reranker.Rank([amll], null);
        Assert.True(ranked.Best!.Verified);
        Assert.Equal("no-reference", ranked.Best.Reason);
    }
}

public sealed class LyricsDiskCacheTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-lyrics-tests-" + Guid.NewGuid().ToString("N"));
    long _now = 1_000_000;

    Lyrics.DiskCache New(TimeSpan? negativeTtl = null) => new(_dir, () => _now, negativeTtl: negativeTtl);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    static Lyrics.Doc Doc(string id) => new(id, true,
        [new Lyrics.Line(1000, "one", [new Lyrics.Syllable(1000, 1500, "one")], 1500, "uno", null, true)],
        Lyrics.SyncKind.Syllable, "amll", -120);

    [Fact]
    public void The_file_name_is_a_hash_so_case_different_ids_never_alias()
    {
        var c = New();
        string a = c.PathFor("4JEylZNW8SbO4zUyfVrpb7"), b = c.PathFor("4jeylznw8sbo4zuyfvrpb7");
        Assert.NotEqual(a, b, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(64 + 5, Path.GetFileName(a).Length);   // sha-256 hex + ".json"
        Assert.EndsWith(".json", a, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saved_document_round_trips_whole()
    {
        var c = New();
        await c.SaveAsync("track", Doc("track"));
        var e = await c.TryLoadAsync("track");
        Assert.Equal(Lyrics.CacheOutcome.Hit, e.Outcome);
        Assert.Equal(_now, e.SavedAtUnixMs);
        var d = e.Document!;
        Assert.Equal(Lyrics.SyncKind.Syllable, d.Sync);
        Assert.Equal("amll", d.Provider);
        Assert.Equal(-120, d.OffsetMsApplied);
        Assert.Equal("uno", d.Lines[0].Translation);
        Assert.True(d.Lines[0].IsWordByWord);
        Assert.Equal(1500, d.Lines[0].Syllables[0].EndMs);
    }

    [Fact]
    public async Task A_negative_marker_answers_known_missing_inside_its_ttl_and_expires_after()
    {
        var c = New(TimeSpan.FromMilliseconds(1000));
        await c.SaveAsync("gone", null);
        Assert.Equal(Lyrics.CacheOutcome.KnownMissing, (await c.TryLoadAsync("gone")).Outcome);
        _now += 1000;
        Assert.Equal(Lyrics.CacheOutcome.Miss, (await c.TryLoadAsync("gone")).Outcome);
        Assert.False(File.Exists(c.PathFor("gone")));   // an expired marker is discarded on the way out
    }

    [Fact]
    public async Task A_corrupt_file_is_a_miss_that_deletes_itself_never_a_throw()
    {
        var c = New();
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(c.PathFor("bad"), "{ not json");
        Assert.Equal(Lyrics.CacheOutcome.Miss, (await c.TryLoadAsync("bad")).Outcome);
        Assert.False(File.Exists(c.PathFor("bad")));
    }

    [Fact]
    public async Task An_entry_written_for_another_track_is_discarded()
    {
        var c = New();
        await c.SaveAsync("a", Doc("a"));
        Directory.CreateDirectory(_dir);
        File.Copy(c.PathFor("a"), c.PathFor("b"), overwrite: true);   // right envelope, WRONG id inside
        Assert.Equal(Lyrics.CacheOutcome.Miss, (await c.TryLoadAsync("b")).Outcome);
    }

    [Fact]
    public async Task A_document_with_no_lines_is_treated_as_corrupt()
    {
        var c = New();
        await c.SaveAsync("empty", new Lyrics.Doc("empty", false, [], Lyrics.SyncKind.Unsynced, "x"));
        Assert.Equal(Lyrics.CacheOutcome.Miss, (await c.TryLoadAsync("empty")).Outcome);
    }

    [Fact]
    public async Task Clear_only_ever_touches_files_of_its_own_name_shape()
    {
        var c = New();
        await c.SaveAsync("t", Doc("t"));
        string foreign = Path.Combine(_dir, "keep-me.json");
        await File.WriteAllTextAsync(foreign, "{}");
        c.Clear();
        Assert.False(File.Exists(c.PathFor("t")));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public async Task Forget_drops_one_track_and_only_that_track()
    {
        var c = New();
        await c.SaveAsync("one", Doc("one"));
        await c.SaveAsync("two", Doc("two"));
        c.Forget("one");
        Assert.Equal(Lyrics.CacheOutcome.Miss, (await c.TryLoadAsync("one")).Outcome);
        Assert.Equal(Lyrics.CacheOutcome.Hit, (await c.TryLoadAsync("two")).Outcome);
    }
}

public class LyricsProbeTests
{
    [Theory]
    [InlineData("https://x/api?usertoken=abc&q=song", "https://x/api?usertoken=***redacted***&q=song")]
    [InlineData("https://x/api?q=song&AccessKey=k", "https://x/api?q=song&AccessKey=***redacted***")]
    [InlineData("https://x/plain", "https://x/plain")]
    public void Every_credential_bearing_parameter_is_redacted_and_the_search_terms_survive(string url, string expected)
        => Assert.Equal(expected, Lyrics.Probe.Redact(url));

    [Fact]
    public void Capture_is_a_no_op_with_no_probe_active()
    {
        Lyrics.Probe.Current.Value = null;
        Lyrics.Probe.CaptureRaw("s", "l", "json", "body");   // must not throw
        Lyrics.Probe.Note("s", "note");
    }

    [Fact]
    public void The_per_source_and_per_payload_caps_hold()
    {
        var p = new Lyrics.Probe();
        Lyrics.Probe.Current.Value = p;
        try
        {
            for (int i = 0; i < Lyrics.Probe.MaxPayloadsPerSource + 3; i++)
                Lyrics.Probe.CaptureRaw("src", "u" + i, "json", "x");
            Lyrics.Probe.CaptureRaw("big", "u", "ttml", new string('a', Lyrics.Probe.MaxPayloadChars + 10));
            var raw = p.RawPayloads();
            int fromSrc = 0;
            Lyrics.RawPayload? big = null;
            foreach (var r in raw) { if (r.SourceId == "src") fromSrc++; if (r.SourceId == "big") big = r; }
            Assert.Equal(Lyrics.Probe.MaxPayloadsPerSource, fromSrc);
            Assert.NotNull(big);
            Assert.True(big!.Truncated);
            Assert.Equal(Lyrics.Probe.MaxPayloadChars, big.Text.Length);
        }
        finally { Lyrics.Probe.Current.Value = null; }
    }
}

[Collection(LyricsDiagCollection.Name)]
public class LyricsDiagStoreTests
{
    static Lyrics.SearchReport Report(string id)
        => new(id, "", "", "", 0, null, 0, "s", []);

    [Fact]
    public void The_per_track_store_is_fifo_bounded()
    {
        string prefix = "diag-fifo-" + Guid.NewGuid().ToString("N") + "-";
        for (int i = 0; i < Lyrics.Diag.TrackCap + 5; i++) Lyrics.Diag.Publish(Report(prefix + i));
        Assert.Null(Lyrics.Diag.ForTrack(prefix + "0"));
        Assert.NotNull(Lyrics.Diag.ForTrack(prefix + (Lyrics.Diag.TrackCap + 4)));
        Assert.True(Lyrics.Diag.Recent().Count <= Lyrics.Diag.Cap);
    }

    [Fact]
    public void Only_the_last_few_tracks_keep_their_heavy_inspection()
    {
        string prefix = "diag-insp-" + Guid.NewGuid().ToString("N") + "-";
        for (int i = 0; i < Lyrics.Diag.InspectionCap + 2; i++)
            Lyrics.Diag.PublishInspection(new Lyrics.Inspection(prefix + i, 0, "n", [], [], null));
        Assert.Null(Lyrics.Diag.InspectionFor(prefix + "0"));
        Assert.NotNull(Lyrics.Diag.InspectionFor(prefix + (Lyrics.Diag.InspectionCap + 1)));
    }

    [Fact]
    public void A_republish_replaces_rather_than_merges()
    {
        string id = "diag-replace-" + Guid.NewGuid().ToString("N");
        Lyrics.Diag.PublishInspection(new Lyrics.Inspection(id, 0, "first", [], [], null));
        Lyrics.Diag.PublishInspection(new Lyrics.Inspection(id, 1, "second", [], [], null));
        Assert.Equal("second", Lyrics.Diag.InspectionFor(id)!.Note);
    }

    [Theory]
    [InlineData(0L, "0:00")]
    [InlineData(61_000L, "1:01")]
    [InlineData(836_000L, "13:56")]
    [InlineData(-5L, "0:00")]
    public void Timestamps_format_as_minutes_and_seconds(long ms, string expected)
        => Assert.Equal(expected, Lyrics.Diag.Ts(ms));
}

[Collection(LyricsDiagCollection.Name)]
public class LyricsAggregatorTests
{
    sealed class FakeSource(string id, Lyrics.MatchBasis basis, Func<Lyrics.Request, Lyrics.Doc?> answer) : Lyrics.ISource
    {
        public int Calls;
        public string Id => id;
        public bool Enabled => true;
        public double Prior => 0.5;

        public Task<Lyrics.Candidate?> FetchAsync(Lyrics.Request req, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var doc = answer(req);
            return Task.FromResult(doc is null ? null : new Lyrics.Candidate(id, Prior, basis, doc));
        }
    }

    static readonly Lyrics.Options Fast = new(PerSourceTimeoutMs: 5000, TotalTimeoutMs: 5000, FirstHitGraceMs: 0);

    static Func<string, CancellationToken, Task<Lyrics.Request?>> Resolver(Action? onCall = null)
        => (id, _) =>
        {
            onCall?.Invoke();
            return Task.FromResult<Lyrics.Request?>(new Lyrics.Request(id, "spotify:track:" + id, "Song", ["Artist"], "Album", 200_000));
        };

    static Lyrics.Doc Lines(string id, params (long Ms, string Text)[] lines)
    {
        var l = new List<Lyrics.Line>(lines.Length);
        foreach (var (ms, text) in lines) l.Add(new Lyrics.Line(ms, text, []));
        return new Lyrics.Doc(id, true, l, Lyrics.SyncKind.Line, "fake");
    }

    [Fact]
    public async Task Concurrent_callers_share_ONE_fetch()
    {
        int resolves = 0;
        var src = new FakeSource("spotify", Lyrics.MatchBasis.Identity,
            r => Lines(r.TrackId, (1000, "one line"), (5000, "two line"), (9000, "three line")));
        var agg = new Lyrics.Aggregator([src], Resolver(() => Interlocked.Increment(ref resolves)), Fast);
        string id = "share-" + Guid.NewGuid().ToString("N");

        var a = agg.GetAsync(id);
        var b = agg.GetAsync(id);
        var (da, db) = (await a, await b);

        Assert.NotNull(da);
        Assert.Same(da, db);
        Assert.Equal(1, resolves);
        Assert.Equal(1, src.Calls);
        Assert.Same(da, agg.Peek(id));
    }

    [Fact]
    public async Task A_document_that_cleans_to_nothing_is_a_miss_not_a_winner()
    {
        var junk = new FakeSource("lrclib", Lyrics.MatchBasis.MetadataSearch,
            r => Lines(r.TrackId, (1000, "♪"), (5000, "..."), (9000, "—")));
        var agg = new Lyrics.Aggregator([junk], Resolver(), Fast);
        string id = "junk-" + Guid.NewGuid().ToString("N");

        Assert.Null(await agg.GetAsync(id));
        var report = Lyrics.Diag.ForTrack(id)!;
        Assert.Equal(Lyrics.Outcome.Miss, report.Sources[0].Outcome);
    }

    [Fact]
    public async Task A_decoy_never_even_becomes_a_candidate()
    {
        var decoyLines = new (long, string)[24];
        for (int i = 0; i < decoyLines.Length; i++) decoyLines[i] = (i * 40_000L, "decoy line number " + i);
        var decoy = new FakeSource("musixmatch", Lyrics.MatchBasis.Isrc, r => Lines(r.TrackId, decoyLines));
        var real = new FakeSource("lrclib", Lyrics.MatchBasis.MetadataSearch,
            r => Lines(r.TrackId, (1000, "a real first line"), (4200, "a real second line"), (9900, "a real third")));
        var agg = new Lyrics.Aggregator([decoy, real], Resolver(), Fast);
        string id = "decoy-" + Guid.NewGuid().ToString("N");

        var winner = await agg.GetAsync(id);

        Assert.NotNull(winner);
        Assert.Equal("a real first line", winner!.Lines[0].Text);
        var report = Lyrics.Diag.ForTrack(id)!;
        foreach (var t in report.Sources)
            if (t.SourceId == "musixmatch")
            {
                Assert.Equal(Lyrics.Outcome.Miss, t.Outcome);
                Assert.Contains("decoy", t.Detail, StringComparison.Ordinal);
            }
    }

    [Fact]
    public async Task An_unresolvable_track_publishes_a_report_and_answers_null()
    {
        var src = new FakeSource("spotify", Lyrics.MatchBasis.Identity, r => Lines(r.TrackId, (0, "x")));
        var agg = new Lyrics.Aggregator([src], (_, _) => Task.FromResult<Lyrics.Request?>(null), Fast);
        string id = "unresolved-" + Guid.NewGuid().ToString("N");

        Assert.Null(await agg.GetAsync(id));
        Assert.Equal(0, src.Calls);
        Assert.Contains("could not resolve", Lyrics.Diag.ForTrack(id)!.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disk_hit_short_circuits_resolve_and_the_fan_out_entirely()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-lyrics-agg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var disk = new Lyrics.DiskCache(dir);
            string id = "disk-" + Guid.NewGuid().ToString("N");
            var word = new Lyrics.Doc(id, true,
            [
                new Lyrics.Line(1000, "one two", [new Lyrics.Syllable(1000, 2500, "one two")], 2500, null, null, true),
            ], Lyrics.SyncKind.Syllable, "amll");
            await disk.SaveAsync(id, word);

            int resolves = 0;
            var src = new FakeSource("spotify", Lyrics.MatchBasis.Identity, r => Lines(r.TrackId, (0, "x")));
            var agg = new Lyrics.Aggregator([src], Resolver(() => Interlocked.Increment(ref resolves)), Fast, diskCache: disk);

            var hit = await agg.GetAsync(id);

            Assert.NotNull(hit);
            Assert.Equal(Lyrics.SyncKind.Syllable, hit!.Sync);
            Assert.Equal(0, resolves);   // richness 3 ⇒ no background upgrade either
            Assert.Equal(0, src.Calls);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}

/// <summary>`Lyrics.Diag` is a process-wide, deliberately BOUNDED store (3 inspections, 256 tracks). Two test classes
/// that publish into it in parallel could evict each other's entries between a publish and its assert, so they share
/// one collection and run serially.</summary>
[CollectionDefinition(Name)]
public sealed class LyricsDiagCollection
{
    public const string Name = "lyrics-diag-store";
}
