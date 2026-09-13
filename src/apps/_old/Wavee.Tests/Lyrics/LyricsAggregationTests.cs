using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Lyrics;
using Wavee.Backend.Lyrics.Sources;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Lyrics;

// Aggregator + source tests with fakes (no network): fan-out, reranking, graceful degradation, caching, and the
// LRCLIB/AMLL JSON-and-TTML parse paths through a fake ILyricHttp.
public class LyricsAggregationTests
{
    sealed class FakeSource : ILyricCandidateSource
    {
        readonly LyricsCandidate? _result;
        readonly int _delayMs;
        readonly bool _throw;
        public int Calls;
        public FakeSource(string id, LyricsCandidate? result, double prior = 0.5, int delayMs = 0, bool doThrow = false)
        { Id = id; _result = result; Prior = prior; _delayMs = delayMs; _throw = doThrow; }
        public string Id { get; }
        public bool Enabled => true;
        public double Prior { get; }
        public async Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (_delayMs > 0) await Task.Delay(_delayMs, ct);
            if (_throw) throw new InvalidOperationException("boom");
            return _result;
        }
    }

    sealed class FakeHttp : ILyricHttp
    {
        readonly List<(string Key, string? Body)> _map;
        public int Calls;
        public FakeHttp(params (string, string?)[] map) => _map = map.ToList();
        public Task<string?> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            foreach (var (k, v) in _map) if (url.Contains(k, StringComparison.Ordinal)) return Task.FromResult(v);
            return Task.FromResult<string?>(null);
        }
    }

    static LyricsRequest Req() => new("t1", "spotify:track:t1", "Test Song", new[] { "Artist" }, "Album", 200000);
    static Func<string, CancellationToken, Task<LyricsRequest?>> Resolver(LyricsRequest? r)
        => (_, _) => Task.FromResult(r);

    static LyricsDocument LineDoc(string p, params (long, string)[] ls)
        => new("t1", true, ls.Select(l => new LyricLine(l.Item1, l.Item2, Array.Empty<LyricSyllable>())).ToList(), LyricsSyncKind.Line, p);
    static LyricsDocument WordDoc(string p, params (long, string)[] ls)
        => new("t1", true, ls.Select(l => new LyricLine(l.Item1, l.Item2, new[] { new LyricSyllable(l.Item1, l.Item1 + 300, l.Item2) }, l.Item1 + 600, IsWordByWord: true)).ToList(), LyricsSyncKind.Syllable, p);

    static readonly (long, string)[] Song = { (1000, "line one here"), (5000, "line two there"), (9000, "line three everywhere") };

    [Fact]
    public async Task Aggregator_FansOut_RerankerPicksWordSynced()
    {
        var sources = new ILyricCandidateSource[]
        {
            new FakeSource("spotify", new LyricsCandidate("spotify", 0.55, MatchBasis.Identity, LineDoc("spotify", Song))),
            new FakeSource("lrclib", new LyricsCandidate("lrclib", 0.45, MatchBasis.MetadataSearch, LineDoc("lrclib", Song))),
            new FakeSource("amll", new LyricsCandidate("amll", 0.9, MatchBasis.Identity, WordDoc("amll", Song))),
        };
        var agg = new AggregatingLyricsProvider(sources, Resolver(Req()));
        var doc = await agg.GetLyricsAsync("t1");

        Assert.NotNull(doc);
        Assert.Equal("amll", doc!.Provider);
        Assert.Equal(LyricsSyncKind.Syllable, doc.Sync);
    }

    [Fact]
    public async Task Aggregator_GoldArrivesFirst_WaitsForReference_ThenCorrectsOffset()
    {
        var shifted = Song.Select(l => (l.Item1 + 500, l.Item2)).ToArray();
        var sources = new ILyricCandidateSource[]
        {
            new FakeSource("musixmatch", new LyricsCandidate("musixmatch", 0.9, MatchBasis.Isrc, WordDoc("musixmatch", shifted))),
            new FakeSource("spotify", new LyricsCandidate("spotify", 0.55, MatchBasis.Identity, LineDoc("spotify", Song)), delayMs: 60),
        };
        var agg = new AggregatingLyricsProvider(sources, Resolver(Req()));

        var doc = await agg.GetLyricsAsync("t1");

        Assert.NotNull(doc);
        Assert.Equal("musixmatch", doc!.Provider);
        Assert.Equal(-500, doc.OffsetMsApplied);
        Assert.Equal(1000, doc.Lines[0].StartMs);
        Assert.Equal(1000, doc.Lines[0].Syllables[0].StartMs);
        Assert.Equal(1300, doc.Lines[0].Syllables[0].EndMs);
    }

    [Fact]
    public async Task Aggregator_SourceThrowsOrTimesOut_DoesNotFailAggregate()
    {
        var sources = new ILyricCandidateSource[]
        {
            new FakeSource("amll", null, doThrow: true),                          // throws
            new FakeSource("spotify", null, delayMs: 600),                        // times out (per-source 150ms)
            new FakeSource("lrclib", new LyricsCandidate("lrclib", 0.45, MatchBasis.MetadataSearch, LineDoc("lrclib", Song))),
        };
        var agg = new AggregatingLyricsProvider(sources, Resolver(Req()), new LyricsOptions(PerSourceTimeoutMs: 150));
        var doc = await agg.GetLyricsAsync("t1");

        Assert.NotNull(doc);
        Assert.Equal("lrclib", doc!.Provider);   // the surviving source still wins
    }

    [Fact]
    public async Task Aggregator_NoResolution_ReturnsNull()
    {
        var agg = new AggregatingLyricsProvider(new ILyricCandidateSource[] { new FakeSource("amll", null) }, Resolver(null));
        Assert.Null(await agg.GetLyricsAsync("t1"));
    }

    [Fact]
    public async Task FetchOne_DecoyShape_IsMissedBeforeEverBecomingACandidate()
    {
        // The FetchOne-level gate (not the reranker's stage-one floor): a document that runs to 824000ms on a 200000ms
        // track, every line exactly 4000ms apart, must never even become a LyricsCandidate — the real bug had
        // MatchBasis.Isrc make it "verified by construction" and win outright regardless of score.
        var decoyLines = Enumerable.Range(0, 206).Select(i => ((long)(12000 + i * 4000), $"nonsense line {i}")).ToArray();
        var decoy = LineDoc("musixmatch", decoyLines);
        var src = new FakeSource("musixmatch", new LyricsCandidate("musixmatch", 0.7, MatchBasis.Isrc, decoy));
        var agg = new AggregatingLyricsProvider(new ILyricCandidateSource[] { src }, Resolver(Req()));

        var doc = await agg.GetLyricsAsync("t1");

        Assert.Null(doc);   // the only source's only candidate was a decoy → 0 candidates → no winner
    }

    [Fact]
    public async Task Aggregator_CachesWinner_SecondCallDoesNotRefetch()
    {
        var src = new FakeSource("lrclib", new LyricsCandidate("lrclib", 0.45, MatchBasis.MetadataSearch, LineDoc("lrclib", Song)));
        var agg = new AggregatingLyricsProvider(new ILyricCandidateSource[] { src }, Resolver(Req()));

        var a = await agg.GetLyricsAsync("t1");
        var b = await agg.GetLyricsAsync("t1");

        Assert.NotNull(a);
        Assert.Same(a, b);          // cached instance
        Assert.Equal(1, src.Calls); // not refetched
    }

    [Fact]
    public async Task LrcLibSource_ParsesSyncedLyrics_FromFakeHttp()
    {
        const string json = "{\"instrumental\":false,\"syncedLyrics\":\"[00:01.00]Hello\\n[00:05.00]World\",\"plainLyrics\":\"Hello\\nWorld\"}";
        var src = new LrcLibSource(new FakeHttp(("lrclib.net/api/get", json)));
        var cand = await src.FetchAsync(Req(), default);

        Assert.NotNull(cand);
        Assert.Equal("lrclib", cand!.ProviderId);
        Assert.Equal(LyricsSyncKind.Line, cand.Sync);
        Assert.Equal(2, cand.Document.Lines.Count);
        Assert.Equal(1000, cand.Document.Lines[0].StartMs);
    }

    [Fact]
    public async Task LrcLibSource_Instrumental_ReturnsNull()
    {
        const string json = "{\"instrumental\":true}";
        var src = new LrcLibSource(new FakeHttp(("lrclib.net/api/get", json)));
        Assert.Null(await src.FetchAsync(Req(), default));
    }

    [Fact]
    public async Task LrcLibSource_FallsBackToFeatStrippedSearch()
    {
        const string valid = "[{\"instrumental\":false,\"duration\":200.0," +
            "\"syncedLyrics\":\"[00:01.00]Hi\\n[00:05.00]There\",\"plainLyrics\":\"Hi\\nThere\"}]";
        // /api/get misses; the raw "Song (feat. X)" /api/search returns [] (no match); the feat-stripped "Song" search
        // hits → the variant ladder must fall back to it. (Keys are matched by substring against the request URL.)
        var http = new FakeHttp(
            ("/api/get", null),                              // exact lookup misses
            ("feat", "[]"),                                  // full-title search (URL carries the encoded "feat") → empty
            ("track_name=Song&artist", valid));              // feat-stripped "Song"/"Artist" variant → hit
        var req = new LyricsRequest("t1", "spotify:track:t1", "Song (feat. X)", new[] { "Artist" }, "Album", 200000);

        var cand = await new LrcLibSource(http).FetchAsync(req, default);

        Assert.NotNull(cand);
        Assert.Equal("lrclib", cand!.ProviderId);
        Assert.Equal(2, cand.Document.Lines.Count);
    }

    // ── background pass replaces an unverified first winner (the ISRC decoy escape hatch) ──────────────────────────────
    // A first pass with no reference yet (the reference source is still in flight when the grace window elapses) picks
    // an ISRC-trusted candidate purely on trust — the "no-reference" reranker path, exactly like the real Musixmatch
    // decoy bug. The background pass then gathers the reference and must replace that incumbent once it turns out to
    // have ZERO text agreement with it, even though nothing out-tiers it (both documents are Line sync, so plain
    // IsRicher never fires).

    [Fact]
    public async Task BackgroundPass_ReplacesAnUnverifiedFirstWinner_WithAVerifiedLineDoc()
    {
        var decoy = LineDoc("musixmatch", (12000, "wob gopini den"), (16000, "tefe woxica fero"), (20000, "mubli tark yolen"));
        var opt = new LyricsOptions(FirstHitGraceMs: 20, PerSourceTimeoutMs: 3000, TotalTimeoutMs: 3000);
        var sources = new ILyricCandidateSource[]
        {
            new FakeSource("musixmatch", new LyricsCandidate("musixmatch", 0.7, MatchBasis.Isrc, decoy), delayMs: 0),
            new FakeSource("spotify", new LyricsCandidate("spotify", 0.55, MatchBasis.Identity, LineDoc("spotify", Song)), delayMs: 120),
            new FakeSource("lrclib", new LyricsCandidate("lrclib", 0.45, MatchBasis.MetadataSearch, LineDoc("lrclib", Song)), delayMs: 120),
        };
        var agg = new AggregatingLyricsProvider(sources, Resolver(Req()), opt);

        var upgrades = new List<LyricsDocument>();
        agg.LyricsUpgraded.Subscribe(Observers.From<LyricsDocument>(d => { lock (upgrades) upgrades.Add(d); }));

        var first = await agg.GetLyricsAsync("t1");
        Assert.Equal("musixmatch", first!.Provider);   // the decoy wins the first pass — no reference to check it against yet

        LyricsDocument? upgraded = null;
        for (int i = 0; i < 300 && upgraded is null; i++)
        {
            await Task.Delay(10);
            lock (upgrades) if (upgrades.Count > 0) upgraded = upgrades[0];
        }
        Assert.NotNull(upgraded);
        Assert.NotEqual("musixmatch", upgraded!.Provider);   // replaced, even though it never got RICHER (both Line sync)
        Assert.Equal(LyricsSyncKind.Line, upgraded.Sync);

        var again = await agg.GetLyricsAsync("t1");
        Assert.Same(upgraded, again);   // the promotion landed in the memory cache too
    }

    // ── Musixmatch status_code (captcha/quota hides behind HTTP 200) ────────────────────────────────────────────────────

    sealed class MusixmatchFakeHttp : ILyricHttp
    {
        readonly List<(string Key, string? Body)> _map;
        readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        public MusixmatchFakeHttp(params (string, string?)[] map) => _map = map.ToList();

        public Task<string?> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            foreach (var (k, v) in _map)
                if (url.Contains(k, StringComparison.Ordinal))
                {
                    lock (_calls) { _calls.TryGetValue(k, out int n); _calls[k] = n + 1; }
                    return Task.FromResult(v);
                }
            return Task.FromResult<string?>(null);
        }

        public int CallsFor(string key) { lock (_calls) { return _calls.TryGetValue(key, out int n) ? n : 0; } }
    }

    [Fact]
    public async Task MusixmatchSource_StatusCode401_MissesAndDropsTheTokenForTheNextCall()
    {
        // message.header.status_code — Musixmatch answers HTTP 200 even for a captcha (401) block; the body underneath
        // is the decoy. A valid token.get response, but every macro.subtitles.get answers the captcha shape.
        const string tokenOk = "{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"user_token\":\"TOK123\"}}}";
        const string captcha = "{\"message\":{\"header\":{\"status_code\":401},\"body\":{}}}";
        var http = new MusixmatchFakeHttp(("token.get", tokenOk), ("macro.subtitles.get", captcha));
        var src = new MusixmatchSource(http);
        var req = new LyricsRequest("t1", "spotify:track:t1", "Sorry Seems To Be The Hardest Word",
            new[] { "Elton John" }, "Album", 210066, Isrc: "GBUM71505078");

        var first = await src.FetchAsync(req, default);
        Assert.Null(first);
        Assert.Equal(1, http.CallsFor("token.get"));

        var second = await src.FetchAsync(req, default);
        Assert.Null(second);
        Assert.Equal(2, http.CallsFor("token.get"));   // the dropped token forced exactly one re-fetch on the next call
    }

    [Fact]
    public void Musixmatch_BuildSubtitlesUrl_IsrcVsQuery()
    {
        var byIsrc = MusixmatchSource.BuildSubtitlesUrl("TOK", "USRC17607839", null, null, 200, "abc12345");
        Assert.Contains("track_isrc=USRC17607839", byIsrc);
        Assert.DoesNotContain("q_track=", byIsrc);
        Assert.Contains("q_duration=200", byIsrc);

        var byQuery = MusixmatchSource.BuildSubtitlesUrl("TOK", null, "Mood", "24kGoldn", 200, "abc12345");
        Assert.Contains("q_track=Mood", byQuery);
        Assert.Contains("q_artist=24kGoldn", byQuery);
        Assert.DoesNotContain("track_isrc=", byQuery);
    }

    [Fact]
    public async Task AmllSource_ParsesTtml_FromFakeHttp()
    {
        const string ttml =
            "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div>" +
            "<p begin=\"0:01.000\" end=\"0:03.000\"><span begin=\"0:01.000\" end=\"0:02.000\">Hello</span> <span begin=\"0:02.000\" end=\"0:03.000\">world</span></p>" +
            "</div></body></tt>";
        var src = new AmllTtmlDbSource(new FakeHttp(("spotify-lyrics/t1.ttml", ttml)));
        var cand = await src.FetchAsync(Req(), default);

        Assert.NotNull(cand);
        Assert.Equal("amll", cand!.ProviderId);
        Assert.Equal(LyricsSyncKind.Syllable, cand.Sync);
        Assert.True(cand.Document.Lines[0].IsWordByWord);
    }

    [Fact]
    public async Task AmllSource_NegativeCachesMissForSession()
    {
        var http = new FakeHttp(("spotify-lyrics/t1.ttml", null));
        var src = new AmllTtmlDbSource(http);

        Assert.Null(await src.FetchAsync(Req(), default));
        Assert.Null(await src.FetchAsync(Req(), default));

        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task SpotifyNativeSource_ParsesColorLyricsJson()
    {
        const string json = "{\"lyrics\":{\"syncType\":\"LINE_SYNCED\",\"lines\":[" +
            "{\"startTimeMs\":\"1000\",\"words\":\"first\"},{\"startTimeMs\":\"5000\",\"words\":\"second\"}]}}";
        var src = new SpotifyNativeLyricsSource((_, _) => Task.FromResult<string?>(json), () => "https://spclient");
        var cand = await src.FetchAsync(Req(), default);

        Assert.NotNull(cand);
        Assert.Equal("spotify", cand!.ProviderId);
        Assert.Equal(LyricsSyncKind.Line, cand.Sync);
        Assert.Equal(1000, cand.Document.Lines[0].StartMs);
        Assert.Equal("first", cand.Document.Lines[0].Text);
    }

    [Fact]
    public async Task SpotifyNativeSource_SkippedWhenHasSpotifyLyricsFalse()
    {
        var src = new SpotifyNativeLyricsSource((_, _) => Task.FromResult<string?>("{}"), () => "https://spclient");
        var req = Req() with { HasSpotifyLyrics = false };
        Assert.Null(await src.FetchAsync(req, default));
    }
}
