// ── Wavee.Tests/LyricsGreyCnTests.cs — the Chinese grey sources (kugou KRC, qq QRC) end to end ────────────────────
//
// search → id → lyric through a fake `IHttpPost` keyed by URL prefix (GET and POST, every call recorded): the happy
// path's candidate and its match fields, the pick skipping a live cut and a minute-off duration, the ladder stopping on
// a Perfect hit, remembered misses (no request on the second call), transport errors not remembered, three failures
// opening the breaker, kugou's {"status":400}, qq's three-block XML (orig chosen over the longer roma), qq's plain-LRC
// fallback, and cancellation propagating without counting as a failure.
//
// Pure: no network; EVERY test first repoints the guard registry at an in-memory ledger so the real
// %LOCALAPPDATA%\Wavee\lyrics\source-misses.json is never read or written and no miss or breaker leaks between tests.
// The collection is non-parallel because that registry is process-wide. All lyric text is SYNTHETIC; the encrypted
// payloads are built with the test-only encrypt helpers.

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LyricsGreyCnCollection
{
    public const string Name = "LyricsGreyCn";
}

[Collection(LyricsGreyCnCollection.Name)]
public class LyricsGreyCnTests
{
    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static long Clock() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>FIRST in every test: a fresh registry over an in-memory ledger.</summary>
    static void Fresh() => Lyrics.SourceGuard.ResetRegistry(new Lyrics.MissLedger(null, Clock));

    static Lyrics.Request Req(string trackId) => new(trackId, "spotify:track:" + trackId, "Song", ["Artist"], "Album", 200_000);

    sealed record Call(string Method, string Url, string? Body, IReadOnlyDictionary<string, string>? Headers);

    /// <summary>Answers by URL prefix (first registered match; <see cref="On"/> with a known prefix replaces its route);
    /// no route → 404. Records every call, GET and POST.</summary>
    sealed class FakeHttp : Lyrics.IHttpPost
    {
        readonly List<(string Prefix, Func<string, string?, Lyrics.HttpResult> Answer)> _routes = [];
        public readonly List<Call> Calls = [];

        public FakeHttp On(string prefix, Func<string, string?, Lyrics.HttpResult> answer)
        {
            _routes.RemoveAll(r => r.Prefix == prefix);
            _routes.Add((prefix, answer));
            return this;
        }

        public FakeHttp On(string prefix, string body) => On(prefix, (_, _) => new Lyrics.HttpResult(200, body));

        public FakeHttp Status(string prefix, int status) => On(prefix, (_, _) => new Lyrics.HttpResult(status, null));

        public FakeHttp Cancel(string prefix) => On(prefix, (_, _) => throw new OperationCanceledException("per-source timeout"));

        public int Count(string prefix)
        {
            lock (Calls) return Calls.Count(c => c.Url.StartsWith(prefix, StringComparison.Ordinal));
        }

        public IReadOnlyList<Call> To(string prefix)
        {
            lock (Calls) return Calls.Where(c => c.Url.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }

        public async Task<string?> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            var r = await GetAsync(url, headers, ct);
            return r.IsSuccess ? r.Body : null;
        }

        public Task<Lyrics.HttpResult> GetAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
            => Task.FromResult(Answer("GET", url, null, headers, ct));

        public async Task<Lyrics.HttpResult> PostAsync(string url, HttpContent body, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            string text = await body.ReadAsStringAsync(ct);
            return Answer("POST", url, text, headers, ct);
        }

        Lyrics.HttpResult Answer(string method, string url, string? body, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Calls) Calls.Add(new Call(method, url, body, headers));
            foreach (var (prefix, answer) in _routes)
                if (url.StartsWith(prefix, StringComparison.Ordinal)) return answer(url, body);
            return new Lyrics.HttpResult(404, null);
        }
    }

    // ── kugou fixtures ──────────────────────────────────────────────────────────────────────────────────────────────

    const string KugouSearchPrefix = "http://mobilecdn.kugou.com/api/v3/search/song";
    const string KugouLyricPrefix = "https://lyrics.kugou.com/search";
    const string KugouDownloadPrefix = "https://lyrics.kugou.com/download";

    const string KrcText =
        "[id:$00000000]\n[ar:Artist]\n[ti:Song]\n"
        + "[1000,1500]<0,500,0>Synthetic <500,1000,0>alpha\n"
        + "[3000,1500]<0,700,0>Synthetic <700,800,0>beta\n";

    static string KugouSearch(params (string Hash, string Title, int Seconds)[] songs)
    {
        var sb = new StringBuilder("{\"status\":1,\"errcode\":0,\"data\":{\"timestamp\":1,\"total\":")
            .Append(songs.Length).Append(",\"info\":[");
        for (int i = 0; i < songs.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"hash\":\"").Append(songs[i].Hash)
              .Append("\",\"songname\":\"").Append(songs[i].Title)
              .Append("\",\"singername\":\"Artist\",\"album_name\":\"Album\",\"filename\":\"Artist - ").Append(songs[i].Title)
              .Append("\",\"duration\":").Append(songs[i].Seconds).Append('}');
        }
        return sb.Append("]}}").ToString();
    }

    // A numeric lyric id on purpose: the endpoint sends ids as numbers on some answers.
    const string KugouLyricFound = "{\"status\":200,\"info\":\"OK\",\"candidates\":[{\"id\":987654,\"accesskey\":\"AK1\",\"duration\":200000}]}";
    const string KugouLyricNone = "{\"status\":200,\"info\":\"OK\",\"candidates\":[]}";
    const string KugouStatus400 = "{\"status\":400,\"info\":\"Bad Request\"}";

    static string KugouDownload(string krc)
        => "{\"status\":200,\"info\":\"OK\",\"charset\":\"utf8\",\"fmt\":\"krc\",\"content\":\""
           + Convert.ToBase64String(Lyrics.Crypto.EncryptKrcForTests(krc)) + "\"}";

    static FakeHttp KugouHappy(string searchBody)
        => new FakeHttp()
            .On(KugouSearchPrefix, searchBody)
            .On(KugouLyricPrefix, KugouLyricFound)
            .On(KugouDownloadPrefix, KugouDownload(KrcText));

    // ── qq fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────

    const string QqSearchPrefix = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    const string QqLyricPrefix = "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg";

    const string QrcText =
        "[ti:Song]\n[ar:Artist]\n"
        + "[1000,1500]Synthetic (1000,500)alpha(1500,1000)\n"
        + "[3000,1500]Synthetic (3000,700)beta(3700,800)\n";

    const string QrcTranslation = "[1000,1500]Translated (1000,500)gamma(1500,1000)\n";

    // Pseudo-random syllables so the block stays the LONGEST after zlib (a repetitive one would compress below orig).
    static string QrcRomanization()
    {
        var rnd = new Random(7);
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            long t = 1000 + i * 2000L;
            sb.Append('[').Append(t).Append(",1500]");
            for (int w = 0; w < 3; w++)
            {
                for (int k = 0; k < 5; k++) sb.Append((char)('a' + rnd.Next(26)));
                sb.Append(" (").Append(t + w * 500).Append(",500)");
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static string QqSearch(params (long Id, string Title, int Seconds)[] songs)
    {
        var sb = new StringBuilder("{\"code\":0,\"ts\":1,\"req_1\":{\"code\":0,\"data\":{\"body\":{\"song\":{\"list\":[");
        for (int i = 0; i < songs.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(songs[i].Id)
              .Append(",\"mid\":\"m").Append(songs[i].Id)
              .Append("\",\"name\":\"").Append(songs[i].Title)
              .Append("\",\"title\":\"").Append(songs[i].Title)
              .Append("\",\"singer\":[{\"id\":1,\"mid\":\"s1\",\"name\":\"Artist\",\"title\":\"Artist\"}]")
              .Append(",\"album\":{\"id\":1,\"mid\":\"a1\",\"name\":\"Album\",\"title\":\"Album\"}")
              .Append(",\"interval\":").Append(songs[i].Seconds).Append('}');
        }
        return sb.Append("]}}}}}").ToString();
    }

    static string QqXml(string? orig, string? ts = null, string? roma = null)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!--\n<QrcInfos>\n<lyric>\n"
           + "<content>" + orig + "</content>\n"
           + "<contentts>" + ts + "</contentts>\n"
           + "<contentroma>" + roma + "</contentroma>\n"
           + "</lyric>\n</QrcInfos>\n-->";

    static FakeHttp QqHappy(string searchBody)
        => new FakeHttp()
            .On(QqSearchPrefix, searchBody)
            .On(QqLyricPrefix, QqXml(Lyrics.Crypto.EncryptQrcForTests(QrcText)));

    // ── kugou ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kugou_search_id_lyric_returns_a_word_synced_candidate_with_its_match()
    {
        Fresh();
        var http = KugouHappy(KugouSearch(("H1", "Song", 200)));
        var kugou = new Lyrics.Sources.Kugou(http);

        var c = await kugou.FetchAsync(Req("cn-k-happy"), CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal("kugou", c.ProviderId);
        Assert.Equal(0.50, c.Prior);
        Assert.Equal(Lyrics.MatchBasis.MetadataSearch, c.Basis);
        Assert.Equal(Lyrics.SyncKind.Syllable, c.Sync);
        Assert.Equal(2, c.LineCount);
        Assert.Equal("Synthetic alpha", c.Document.Lines[0].Text);
        Assert.Equal(1000, c.Document.Lines[0].StartMs);
        Assert.True(c.Band >= Lyrics.MatchBand.VeryHigh);
        Assert.True(c.Confidence > 0.9);
        Assert.False(string.IsNullOrEmpty(c.MatchNote));
        Assert.Contains("H1", c.MatchNote);

        var lyric = Assert.Single(http.To(KugouLyricPrefix));
        Assert.Contains("hash=H1", lyric.Url);
        Assert.Contains("ver=1", lyric.Url);
        var download = Assert.Single(http.To(KugouDownloadPrefix));
        Assert.Contains("ver=1", download.Url);
        Assert.Contains("&id=987654", download.Url);
        Assert.Contains("accesskey=AK1", download.Url);
        Assert.DoesNotContain("kgid", download.Url);
        Assert.Equal(3, http.Calls.Count);
    }

    [Fact]
    public async Task Kugou_pick_skips_a_live_cut_and_a_minute_off_duration()
    {
        Fresh();
        var http = KugouHappy(KugouSearch(("LIVE", "Song (Live)", 200), ("FAR", "Song", 260), ("REAL", "Song", 200)));
        var kugou = new Lyrics.Sources.Kugou(http);

        var c = await kugou.FetchAsync(Req("cn-k-pick"), CancellationToken.None);

        Assert.NotNull(c);
        var lyric = Assert.Single(http.To(KugouLyricPrefix));
        Assert.Contains("hash=REAL", lyric.Url);
        Assert.Contains("REAL", c.MatchNote);
    }

    [Fact]
    public async Task Kugou_ladder_stops_on_a_perfect_hit()
    {
        Fresh();
        var req = Req("cn-k-ladder");
        Assert.True(Lyrics.Query.Variants(req).Count >= 2);

        var perfect = KugouHappy(KugouSearch(("P", "Song", 200)));
        var p = await new Lyrics.Sources.Kugou(perfect).FetchAsync(req, CancellationToken.None);

        Fresh();
        // Two seconds off at a one-second grain: fetchable (≥ Medium) but not good enough to stop the ladder.
        var weaker = KugouHappy(KugouSearch(("M", "Song", 202)));
        var m = await new Lyrics.Sources.Kugou(weaker).FetchAsync(req, CancellationToken.None);

        Assert.NotNull(p);
        Assert.NotNull(m);
        Assert.Equal(Lyrics.MatchBand.Perfect, p.Band);
        Assert.True(m.Band >= Lyrics.MatchBand.Medium && m.Band < Lyrics.MatchBand.VeryHigh);
        Assert.Single(perfect.To(KugouSearchPrefix));
        Assert.Equal(Lyrics.Query.Variants(req).Count, weaker.Count(KugouSearchPrefix));
        Assert.True(perfect.Calls.Count < weaker.Calls.Count);
    }

    [Fact]
    public async Task Kugou_no_lyric_for_the_song_is_remembered_and_the_second_call_makes_no_request()
    {
        Fresh();
        var http = new FakeHttp()
            .On(KugouSearchPrefix, KugouSearch(("H1", "Song", 200)))
            .On(KugouLyricPrefix, KugouLyricNone);
        var kugou = new Lyrics.Sources.Kugou(http);
        var req = Req("cn-k-nolyric");

        Assert.Null(await kugou.FetchAsync(req, CancellationToken.None));
        int calls = http.Calls.Count;
        Assert.True(calls >= 2);

        var probe = new Lyrics.Probe();
        Lyrics.Probe.Current.Value = probe;
        Assert.Null(await kugou.FetchAsync(req, CancellationToken.None));
        Assert.Equal(calls, http.Calls.Count);
        Assert.Contains("cached miss NoLyricForSong", probe.NotesFor("kugou"));

        // The inspector's re-fetch forgets the miss: the next call asks again.
        kugou.Forget(req.TrackId);
        Assert.Null(await kugou.FetchAsync(req, CancellationToken.None));
        Assert.True(http.Calls.Count > calls);
    }

    [Fact]
    public async Task Kugou_status_400_download_is_a_no_lyric_miss_with_the_status_in_the_note()
    {
        Fresh();
        var http = new FakeHttp()
            .On(KugouSearchPrefix, KugouSearch(("H1", "Song", 200)))
            .On(KugouLyricPrefix, KugouLyricFound)
            .On(KugouDownloadPrefix, KugouStatus400);
        var kugou = new Lyrics.Sources.Kugou(http);
        var req = Req("cn-k-400");
        var probe = new Lyrics.Probe();
        Lyrics.Probe.Current.Value = probe;

        Assert.Null(await kugou.FetchAsync(req, CancellationToken.None));
        string notes = probe.NotesFor("kugou");
        Assert.Contains("400", notes);
        Assert.Contains("Bad Request", notes);

        int calls = http.Calls.Count;
        Assert.Null(await kugou.FetchAsync(req, CancellationToken.None));
        Assert.Equal(calls, http.Calls.Count);
    }

    [Fact]
    public async Task Kugou_transport_error_is_not_remembered()
    {
        Fresh();
        var http = new FakeHttp().Status(KugouSearchPrefix, 503);
        var kugou = new Lyrics.Sources.Kugou(http);
        var req = Req("cn-k-transport");

        Assert.Null(await kugou.FetchAsync(req, CancellationToken.None));
        Assert.Single(http.Calls);   // the failed host is not asked again within the same fetch

        http.On(KugouSearchPrefix, KugouSearch(("H1", "Song", 200)))
            .On(KugouLyricPrefix, KugouLyricFound)
            .On(KugouDownloadPrefix, KugouDownload(KrcText));
        Assert.NotNull(await kugou.FetchAsync(req, CancellationToken.None));
    }

    [Fact]
    public async Task Kugou_three_transport_failures_open_the_breaker()
    {
        Fresh();
        var http = new FakeHttp().Status(KugouSearchPrefix, 503);
        var kugou = new Lyrics.Sources.Kugou(http);

        for (int i = 0; i < Lyrics.SourceGuard.FailuresToOpen; i++)
            Assert.Null(await kugou.FetchAsync(Req("cn-k-breaker-" + i), CancellationToken.None));
        int calls = http.Calls.Count;

        http.On(KugouSearchPrefix, KugouSearch(("H1", "Song", 200)))
            .On(KugouLyricPrefix, KugouLyricFound)
            .On(KugouDownloadPrefix, KugouDownload(KrcText));
        var probe = new Lyrics.Probe();
        Lyrics.Probe.Current.Value = probe;

        Assert.Null(await kugou.FetchAsync(Req("cn-k-breaker-next"), CancellationToken.None));
        Assert.Equal(calls, http.Calls.Count);
        Assert.Contains("breaker open", probe.NotesFor("kugou"));
    }

    [Fact]
    public async Task Kugou_cancellation_propagates_and_is_not_a_failure()
    {
        Fresh();
        var http = new FakeHttp().Cancel(KugouSearchPrefix);
        var kugou = new Lyrics.Sources.Kugou(http);
        var req = Req("cn-k-cancel");

        for (int i = 0; i < Lyrics.SourceGuard.FailuresToOpen + 1; i++)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kugou.FetchAsync(req, CancellationToken.None));

        // Had any of those counted, the breaker would be open and this would make no request.
        http.On(KugouSearchPrefix, KugouSearch(("H1", "Song", 200)))
            .On(KugouLyricPrefix, KugouLyricFound)
            .On(KugouDownloadPrefix, KugouDownload(KrcText));
        Assert.NotNull(await kugou.FetchAsync(req, CancellationToken.None));
    }

    // ── qq ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Qq_search_id_lyric_returns_a_word_synced_candidate_with_its_match()
    {
        Fresh();
        var http = QqHappy(QqSearch((1001, "Song", 200)));
        var qq = new Lyrics.Sources.Qq(http);

        var c = await qq.FetchAsync(Req("cn-q-happy"), CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal("qq", c.ProviderId);
        Assert.Equal(0.55, c.Prior);
        Assert.Equal(Lyrics.MatchBasis.MetadataSearch, c.Basis);
        Assert.Equal(Lyrics.SyncKind.Syllable, c.Sync);
        Assert.Equal(2, c.LineCount);
        Assert.Equal("Synthetic alpha", c.Document.Lines[0].Text);
        Assert.True(c.Band >= Lyrics.MatchBand.VeryHigh);
        Assert.True(c.Confidence > 0.9);
        Assert.Contains("1001", c.MatchNote);

        var search = Assert.Single(http.To(QqSearchPrefix));
        Assert.Equal("POST", search.Method);
        Assert.NotNull(search.Body);
        Assert.Contains("\"comm\"", search.Body);
        Assert.Contains("DoSearchForQQMusicDesktop", search.Body);
        Assert.Contains("\"query\":\"Song Artist\"", search.Body);
        Assert.Equal("https://y.qq.com", search.Headers?["Referer"]);

        var lyric = Assert.Single(http.To(QqLyricPrefix));
        Assert.Equal("POST", lyric.Method);
        Assert.Contains("musicid=1001", lyric.Body);
        Assert.Contains("lrctype=4", lyric.Body);
        Assert.Equal("https://y.qq.com", lyric.Headers?["Referer"]);
    }

    [Fact]
    public void Qq_search_body_is_valid_json_and_escapes_the_keyword()
    {
        Fresh();
        byte[] body = Lyrics.Sources.Qq.SearchBody("Say \"hi\" 愛");
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("19", root.GetProperty("comm").GetProperty("ct").GetString());
        Assert.Equal("Say \"hi\" 愛", root.GetProperty("req_1").GetProperty("param").GetProperty("query").GetString());
    }

    [Fact]
    public async Task Qq_pick_skips_a_live_cut_and_a_minute_off_duration()
    {
        Fresh();
        var http = QqHappy(QqSearch((1, "Song (Live)", 200), (2, "Song", 260), (3, "Song", 200)));
        var qq = new Lyrics.Sources.Qq(http);

        var c = await qq.FetchAsync(Req("cn-q-pick"), CancellationToken.None);

        Assert.NotNull(c);
        var lyric = Assert.Single(http.To(QqLyricPrefix));
        Assert.Contains("musicid=3", lyric.Body);
    }

    [Fact]
    public async Task Qq_ladder_stops_on_a_perfect_hit()
    {
        Fresh();
        var req = Req("cn-q-ladder");

        var perfect = QqHappy(QqSearch((1, "Song", 200)));
        var p = await new Lyrics.Sources.Qq(perfect).FetchAsync(req, CancellationToken.None);

        Fresh();
        var weaker = QqHappy(QqSearch((2, "Song", 202)));
        var m = await new Lyrics.Sources.Qq(weaker).FetchAsync(req, CancellationToken.None);

        Assert.NotNull(p);
        Assert.NotNull(m);
        Assert.Single(perfect.To(QqSearchPrefix));
        Assert.Equal(Lyrics.Query.Variants(req).Count, weaker.Count(QqSearchPrefix));
        Assert.True(perfect.Calls.Count < weaker.Calls.Count);
    }

    [Fact]
    public async Task Qq_no_lyric_for_the_song_is_remembered_and_the_second_call_makes_no_request()
    {
        Fresh();
        var http = new FakeHttp()
            .On(QqSearchPrefix, QqSearch((1001, "Song", 200)))
            .On(QqLyricPrefix, QqXml(null));
        var qq = new Lyrics.Sources.Qq(http);
        var req = Req("cn-q-nolyric");

        Assert.Null(await qq.FetchAsync(req, CancellationToken.None));
        int calls = http.Calls.Count;
        Assert.True(calls >= 2);

        Assert.Null(await qq.FetchAsync(req, CancellationToken.None));
        Assert.Equal(calls, http.Calls.Count);
    }

    [Fact]
    public async Task Qq_song_not_found_is_remembered()
    {
        Fresh();
        var http = new FakeHttp().On(QqSearchPrefix, QqSearch());
        var qq = new Lyrics.Sources.Qq(http);
        var req = Req("cn-q-notfound");

        Assert.Null(await qq.FetchAsync(req, CancellationToken.None));
        int calls = http.Calls.Count;
        Assert.Empty(http.To(QqLyricPrefix));

        Assert.Null(await qq.FetchAsync(req, CancellationToken.None));
        Assert.Equal(calls, http.Calls.Count);
    }

    [Fact]
    public async Task Qq_code_2001_is_a_failure_and_never_a_remembered_miss()
    {
        Fresh();
        var http = new FakeHttp().On(QqSearchPrefix, "{\"code\":0,\"req_1\":{\"code\":2001}}");
        var qq = new Lyrics.Sources.Qq(http);
        var req = Req("cn-q-2001");

        Assert.Null(await qq.FetchAsync(req, CancellationToken.None));
        Assert.Single(http.Calls);

        http.On(QqSearchPrefix, QqSearch((1001, "Song", 200)))
            .On(QqLyricPrefix, QqXml(Lyrics.Crypto.EncryptQrcForTests(QrcText)));
        Assert.NotNull(await qq.FetchAsync(req, CancellationToken.None));
    }

    [Fact]
    public async Task Qq_three_transport_failures_open_the_breaker()
    {
        Fresh();
        var http = new FakeHttp().Status(QqSearchPrefix, 502);
        var qq = new Lyrics.Sources.Qq(http);

        Assert.Null(await qq.FetchAsync(Req("cn-q-breaker-0"), CancellationToken.None));
        Assert.Single(http.Calls);   // not remembered, and not asked twice within one fetch
        for (int i = 1; i < Lyrics.SourceGuard.FailuresToOpen; i++)
            Assert.Null(await qq.FetchAsync(Req("cn-q-breaker-" + i), CancellationToken.None));
        int calls = http.Calls.Count;

        http.On(QqSearchPrefix, QqSearch((1001, "Song", 200)))
            .On(QqLyricPrefix, QqXml(Lyrics.Crypto.EncryptQrcForTests(QrcText)));
        Assert.Null(await qq.FetchAsync(Req("cn-q-breaker-next"), CancellationToken.None));
        Assert.Equal(calls, http.Calls.Count);
    }

    [Fact]
    public async Task Qq_three_block_xml_decrypts_orig_even_when_roma_is_longest()
    {
        Fresh();
        string origHex = Lyrics.Crypto.EncryptQrcForTests(QrcText);
        string tsHex = Lyrics.Crypto.EncryptQrcForTests(QrcTranslation);
        string romaHex = Lyrics.Crypto.EncryptQrcForTests(QrcRomanization());
        Assert.True(romaHex.Length > origHex.Length);

        var http = new FakeHttp()
            .On(QqSearchPrefix, QqSearch((1001, "Song", 200)))
            .On(QqLyricPrefix, QqXml(origHex, tsHex, romaHex));
        var qq = new Lyrics.Sources.Qq(http);
        var probe = new Lyrics.Probe();
        Lyrics.Probe.Current.Value = probe;

        var c = await qq.FetchAsync(Req("cn-q-3blocks"), CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal(2, c.LineCount);
        Assert.Equal("Synthetic alpha", c.Document.Lines[0].Text);
        Assert.Equal("Synthetic beta", c.Document.Lines[1].Text);
        Assert.Contains("→ orig", probe.NotesFor("qq"));
    }

    [Fact]
    public async Task Qq_orig_that_does_not_decrypt_but_is_plain_lrc_becomes_a_line_candidate()
    {
        Fresh();
        string xml = QqXml("<![CDATA[[ti:Song]\n[00:01.00]Plain line one\n[00:04.00]Plain line two\n]]>");
        var http = new FakeHttp()
            .On(QqSearchPrefix, QqSearch((1001, "Song", 200)))
            .On(QqLyricPrefix, xml);
        var qq = new Lyrics.Sources.Qq(http);

        var c = await qq.FetchAsync(Req("cn-q-lrc"), CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal(Lyrics.SyncKind.Line, c.Sync);
        Assert.Equal(2, c.LineCount);
        Assert.Equal("Plain line one", c.Document.Lines[0].Text);
        Assert.Equal(1000, c.Document.Lines[0].StartMs);
    }

    [Fact]
    public async Task Qq_cancellation_propagates_and_is_not_a_failure()
    {
        Fresh();
        var http = new FakeHttp().Cancel(QqSearchPrefix);
        var qq = new Lyrics.Sources.Qq(http);
        var req = Req("cn-q-cancel");

        for (int i = 0; i < Lyrics.SourceGuard.FailuresToOpen + 1; i++)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => qq.FetchAsync(req, CancellationToken.None));

        http.On(QqSearchPrefix, QqSearch((1001, "Song", 200)))
            .On(QqLyricPrefix, QqXml(Lyrics.Crypto.EncryptQrcForTests(QrcText)));
        Assert.NotNull(await qq.FetchAsync(req, CancellationToken.None));
    }
}
