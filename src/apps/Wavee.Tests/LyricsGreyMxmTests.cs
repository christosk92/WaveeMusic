// ── Wavee.Tests/LyricsGreyMxmTests.cs — the NetEase and Musixmatch grey sources ──────────────────────────────────────
//
// `Lyrics.Sources.Netease` (search → id → yrc/lrc, risk-control breaker, remembered misses) and
// `Lyrics.Sources.Musixmatch` (the pure URL builder, token TTL, the 401 renew/captcha and 402 gates, instrumental,
// richsync → subtitle fallback, identity vs search basis). Pure: every request goes to an in-memory fake keyed by URL
// prefix, the guard gets an in-memory ledger and a fake clock, and every lyric text is synthetic (the one exception is
// the existing decoy fixture, which is itself generated nonsense).

using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class LyricsGreyMxmTests
{
    const long Hour = 3_600_000L;

    const string NeteaseSearch = "https://music.163.com/api/search/get?";
    const string NeteaseLyric = "https://music.163.com/api/song/lyric";
    const string MxmToken = "https://apic-desktop.musixmatch.com/ws/1.1/token.get";
    const string MxmMacro = "https://apic-desktop.musixmatch.com/ws/1.1/macro.subtitles.get";

    sealed class Clock
    {
        public long Now = 1_700_000_000_000L;
        public long Read() => Now;
    }

    /// <summary>An in-memory <see cref="Lyrics.IHttpPost"/>: the first route whose prefix the url starts with answers;
    /// anything else is a 404 without a body. Records every url.</summary>
    sealed class FakeHttp : Lyrics.IHttpPost
    {
        readonly (string Prefix, Func<string, Lyrics.HttpResult> Answer)[] _routes;
        readonly List<string> _calls = [];

        public FakeHttp(params (string Prefix, Func<string, Lyrics.HttpResult> Answer)[] routes) => _routes = routes;

        public Func<string, Exception>? Throw { get; init; }

        public IReadOnlyList<string> Calls { get { lock (_calls) return _calls.ToArray(); } }

        public int Count(string prefix)
        {
            lock (_calls) return _calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
        }

        public Task<Lyrics.HttpResult> GetAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            if (Throw is not null) throw Throw(url);
            ct.ThrowIfCancellationRequested();
            lock (_calls) _calls.Add(url);
            foreach (var (prefix, answer) in _routes)
                if (url.StartsWith(prefix, StringComparison.Ordinal)) return Task.FromResult(answer(url));
            return Task.FromResult(new Lyrics.HttpResult(404, null));
        }

        public async Task<string?> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            var r = await GetAsync(url, headers, ct);
            return r.IsSuccess ? r.Body : null;
        }

        public Task<Lyrics.HttpResult> PostAsync(string url, HttpContent body, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            body.Dispose();
            return GetAsync(url, headers, ct);
        }
    }

    static Lyrics.HttpResult Ok(string body) => new(200, body);

    static Lyrics.Request Req(string? isrc = null, string trackId = "trk1")
        => new(trackId, "spotify:track:" + trackId, "Paper Lanterns", ["Ada Vell"], "Night Ferries", 200_000, isrc);

    /// <summary>A fresh registry (never the real profile) plus the guard the source under test uses.</summary>
    static Lyrics.SourceGuard Guard(string id, Clock clock)
    {
        var ledger = new Lyrics.MissLedger(null, clock.Read);
        Lyrics.SourceGuard.ResetRegistry(ledger);
        return new Lyrics.SourceGuard(id, clock.Read, ledger);
    }

    static string Q(string s) => "\"" + JsonEncodedText.Encode(s).ToString() + "\"";

    // ── NetEase fixtures (synthetic text) ───────────────────────────────────────────────────────────────────────────

    const string NeteaseSearchHit = """
        {"result":{"songs":[{"id":1901,"name":"Paper Lanterns","artists":[{"id":7,"name":"Ada Vell"}],"album":{"id":3,"name":"Night Ferries"},"duration":200000}],"songCount":1},"code":200}
        """;

    const string NeteaseSearchEmpty = """
        {"result":{"songCount":0},"code":200}
        """;

    const string NeteaseLyricYrc = """
        {"code":200,"lrc":{"version":1,"lyric":"[00:10.00]Paper lanterns drift\n[00:14.00]Over quiet water\n[00:18.00]Carry me home\n"},"yrc":{"version":1,"lyric":"[10000,3000](10000,1000,0)Paper (11000,1000,0)lanterns (12000,1000,0)drift\n[14000,3000](14000,1000,0)Over (15000,1000,0)quiet (16000,1000,0)water\n[18000,3000](18000,1000,0)Carry (19000,1000,0)me (20000,1000,0)home\n"},"tlyric":{"version":0,"lyric":""},"romalrc":{"version":0,"lyric":""}}
        """;

    const string NeteaseLyricLrcOnly = """
        {"code":200,"lrc":{"version":1,"lyric":"[00:10.00]Paper lanterns drift\n[00:14.00]Over quiet water\n[00:18.00]Carry me home\n"},"tlyric":{"version":1,"lyric":"[00:10.00]Translated one\n"}}
        """;

    const string NeteaseNoLyric = """
        {"code":200,"nolyric":true,"sgc":false}
        """;

    const string NeteaseRiskControl = """
        {"code":-460,"message":"Cheating"}
        """;

    // ── NetEase ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Netease_prefers_yrc_word_timing()
    {
        var clock = new Clock();
        var guard = Guard("netease", clock);
        var http = new FakeHttp((NeteaseSearch, _ => Ok(NeteaseSearchHit)), (NeteaseLyric, _ => Ok(NeteaseLyricYrc)));
        var src = new Lyrics.Sources.Netease(http, guard);

        var c = await src.FetchAsync(Req(), CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal("netease", c!.ProviderId);
        Assert.Equal(Lyrics.SyncKind.Syllable, c.Sync);
        Assert.Equal(3, c.LineCount);
        Assert.Equal(Lyrics.MatchBasis.MetadataSearch, c.Basis);
        Assert.True(c.Band >= Lyrics.MatchBand.High, $"band {c.Band}");
        Assert.True(c.Confidence > 0);
        Assert.False(string.IsNullOrEmpty(c.MatchNote));
        Assert.Equal(1, http.Count(NeteaseSearch));   // Perfect hit on the first keyword stops the ladder
        Assert.Equal(1, http.Count(NeteaseLyric));
        Assert.Contains(http.Calls, u => u.StartsWith(NeteaseLyric + "?id=1901&", StringComparison.Ordinal) && u.Contains("yv=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Netease_falls_back_to_lrc_without_yrc()
    {
        var clock = new Clock();
        var guard = Guard("netease", clock);
        var http = new FakeHttp((NeteaseSearch, _ => Ok(NeteaseSearchHit)), (NeteaseLyric, _ => Ok(NeteaseLyricLrcOnly)));
        var src = new Lyrics.Sources.Netease(http, guard);

        var c = await src.FetchAsync(Req(), CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal(Lyrics.SyncKind.Line, c!.Sync);
        Assert.Equal(3, c.LineCount);
        Assert.Equal("Paper lanterns drift", c.Document.Lines[0].Text);
    }

    [Fact]
    public async Task Netease_risk_control_trips_the_breaker_and_sends_nothing_more()
    {
        var clock = new Clock();
        var guard = Guard("netease", clock);
        var http = new FakeHttp((NeteaseSearch, _ => Ok(NeteaseRiskControl)), (NeteaseLyric, _ => Ok(NeteaseLyricYrc)));
        var src = new Lyrics.Sources.Netease(http, guard);

        Assert.Null(await src.FetchAsync(Req(), CancellationToken.None));
        Assert.Single(http.Calls);   // the ladder's next keyword never goes out
        Assert.True(guard.IsOpen(out string reason, out long until));
        Assert.Contains("-460", reason, StringComparison.Ordinal);
        Assert.Equal(clock.Now + Lyrics.Sources.Netease.RiskControlTripMs, until);

        Assert.Null(await src.FetchAsync(Req(trackId: "trk2"), CancellationToken.None));
        Assert.Single(http.Calls);

        clock.Now += Lyrics.Sources.Netease.RiskControlTripMs;
        Assert.False(guard.IsOpen(out _, out _));
    }

    [Fact]
    public async Task Netease_song_not_found_is_remembered()
    {
        var clock = new Clock();
        var guard = Guard("netease", clock);
        var http = new FakeHttp((NeteaseSearch, _ => Ok(NeteaseSearchEmpty)));
        var src = new Lyrics.Sources.Netease(http, guard);

        Assert.Null(await src.FetchAsync(Req(), CancellationToken.None));
        int first = http.Calls.Count;
        Assert.True(first >= 1);
        Assert.True(guard.TryGetMiss("trk1", out var kind, out _));
        Assert.Equal(Lyrics.SourceGuard.MissKind.SongNotFound, kind);
        Assert.False(guard.IsOpen(out _, out _));

        Assert.Null(await src.FetchAsync(Req(), CancellationToken.None));
        Assert.Equal(first, http.Calls.Count);   // answered from the remembered miss

        src.Forget("trk1");
        Assert.False(guard.TryGetMiss("trk1", out _, out _));
    }

    [Fact]
    public async Task Netease_nolyric_is_a_no_lyric_miss()
    {
        var clock = new Clock();
        var guard = Guard("netease", clock);
        var http = new FakeHttp((NeteaseSearch, _ => Ok(NeteaseSearchHit)), (NeteaseLyric, _ => Ok(NeteaseNoLyric)));
        var src = new Lyrics.Sources.Netease(http, guard);

        Assert.Null(await src.FetchAsync(Req(), CancellationToken.None));
        Assert.True(guard.TryGetMiss("trk1", out var kind, out _));
        Assert.Equal(Lyrics.SourceGuard.MissKind.NoLyricForSong, kind);
    }

    [Fact]
    public async Task Netease_transport_error_is_one_failure_and_not_cached()
    {
        var clock = new Clock();
        var guard = Guard("netease", clock);
        var http = new FakeHttp((NeteaseSearch, _ => new Lyrics.HttpResult(0, null)));
        var src = new Lyrics.Sources.Netease(http, guard);

        Assert.Null(await src.FetchAsync(Req(), CancellationToken.None));
        Assert.Single(http.Calls);
        Assert.False(guard.TryGetMiss("trk1", out _, out _));
        Assert.False(guard.IsOpen(out _, out _));
    }

    [Fact]
    public async Task Netease_cancellation_propagates_and_is_never_a_failure()
    {
        var clock = new Clock();
        var guard = Guard("netease", clock);
        var http = new FakeHttp((NeteaseSearch, _ => Ok(NeteaseSearchHit))) { Throw = _ => new OperationCanceledException() };
        var src = new Lyrics.Sources.Netease(http, guard);

        for (int i = 0; i < Lyrics.SourceGuard.FailuresToOpen + 1; i++)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => src.FetchAsync(Req(), CancellationToken.None));

        Assert.False(guard.IsOpen(out _, out _));
        Assert.False(guard.TryGetMiss("trk1", out _, out _));
    }

    // ── Musixmatch fixtures (synthetic text) ────────────────────────────────────────────────────────────────────────

    static string Token(string token) => """{"message":{"header":{"status_code":200},"body":{"user_token":%T%}}}""".Replace("%T%", Q(token));

    static string Status(int code, string hint)
        => """{"message":{"header":{"status_code":%C%,"hint":%H%},"body":""}}"""
            .Replace("%C%", code.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace("%H%", Q(hint));

    const string Missing = """{"message":{"header":{"status_code":404},"body":[]}}""";

    const string Lines3 = "[00:10.00] Paper lanterns drift\n[00:14.00] Over quiet water\n[00:18.00] Carry me home\n";

    const string Richsync3 = """
        [{"ts":10.0,"te":13.0,"l":[{"c":"Paper","o":0.0},{"c":" ","o":0.9},{"c":"lanterns","o":1.0},{"c":" ","o":1.9},{"c":"drift","o":2.0}],"x":"Paper lanterns drift"},
         {"ts":14.0,"te":17.0,"l":[{"c":"Over","o":0.0},{"c":" ","o":0.9},{"c":"quiet","o":1.0},{"c":" ","o":1.9},{"c":"water","o":2.0}],"x":"Over quiet water"},
         {"ts":18.0,"te":21.0,"l":[{"c":"Carry","o":0.0},{"c":" ","o":0.9},{"c":"me","o":1.0},{"c":" ","o":1.9},{"c":"home","o":2.0}],"x":"Carry me home"}]
        """;

    /// <summary>A <c>macro.subtitles.get</c> answer. A null <paramref name="richsync"/> / <paramref name="subtitle"/>
    /// is that call's 404; <paramref name="matcherStatus"/> 404 is "no match".</summary>
    static string Macro(string? richsync = null, string? subtitle = null, string title = "Paper Lanterns",
        string artist = "Ada Vell", long lengthSec = 200, int hasRichsync = 1, int instrumental = 0, int confidence = 1000,
        int matcherStatus = 200)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string track = matcherStatus != 200 ? "[]"
            : """{"track":{"track_id":501,"commontrack_id":9001,"track_name":%TI%,"artist_name":%AR%,"album_name":"Night Ferries","track_length":%LEN%,"has_richsync":%HR%,"instrumental":%IN%,"restricted":0,"has_subtitles":1}}"""
                .Replace("%TI%", Q(title)).Replace("%AR%", Q(artist)).Replace("%LEN%", lengthSec.ToString(inv))
                .Replace("%HR%", hasRichsync.ToString(inv)).Replace("%IN%", instrumental.ToString(inv));
        string matcher = """{"message":{"header":{"status_code":%S%,"confidence":%CF%,"mode":"search"},"body":%B%}}"""
            .Replace("%S%", matcherStatus.ToString(inv)).Replace("%CF%", confidence.ToString(inv)).Replace("%B%", track);
        string sub = subtitle is null ? Missing
            : """{"message":{"header":{"status_code":200},"body":{"subtitle_list":[{"subtitle":{"subtitle_id":1,"subtitle_body":%B%,"subtitle_language":"en"}}]}}}"""
                .Replace("%B%", Q(subtitle));
        string rich = richsync is null ? Missing
            : """{"message":{"header":{"status_code":200},"body":{"richsync":{"richsync_id":1,"richsync_body":%B%}}}}"""
                .Replace("%B%", Q(richsync));
        return """{"message":{"header":{"status_code":200,"execute_time":0.01},"body":{"macro_calls":{"matcher.track.get":%M%,"track.subtitles.get":%SU%,"track.richsync.get":%R%}}}}"""
            .Replace("%M%", matcher).Replace("%SU%", sub).Replace("%R%", rich);
    }

    static FakeHttp MxmHttp(Func<string, Lyrics.HttpResult> macro, string token = "tok-1")
        => new((MxmToken, _ => Ok(Token(token))), (MxmMacro, macro));

    // ── Musixmatch: pure helpers ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mxm_subtitles_url_identity_form_carries_isrc_and_spotify_id()
    {
        string url = Lyrics.Sources.Musixmatch.BuildSubtitlesUrl("tok", "QZABC2600001", "spotify:track:abc", "ignored", "ignored", 200, "r1");

        Assert.StartsWith(MxmMacro + "?", url, StringComparison.Ordinal);
        Assert.Contains("&track_isrc=QZABC2600001", url, StringComparison.Ordinal);
        Assert.Contains("&track_spotify_id=spotify%3Atrack%3Aabc", url, StringComparison.Ordinal);
        Assert.DoesNotContain("q_track=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("q_artist=", url, StringComparison.Ordinal);
        Assert.Contains("&q_duration=200", url, StringComparison.Ordinal);
        Assert.Contains("&usertoken=tok", url, StringComparison.Ordinal);
        Assert.Contains("app_id=web-desktop-app-v1.0", url, StringComparison.Ordinal);
        Assert.Contains("optional_calls=track.richsync", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Mxm_subtitles_url_query_form_has_no_identity_keys()
    {
        string url = Lyrics.Sources.Musixmatch.BuildSubtitlesUrl("tok", null, null, "Paper Lanterns", "Ada Vell", 200, "r1");

        Assert.Contains("&q_track=Paper%20Lanterns&q_artist=Ada%20Vell", url, StringComparison.Ordinal);
        Assert.DoesNotContain("track_isrc", url, StringComparison.Ordinal);
        Assert.DoesNotContain("track_spotify_id", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1000, Lyrics.MatchBand.Perfect)]
    [InlineData(950, Lyrics.MatchBand.VeryHigh)]
    [InlineData(900, Lyrics.MatchBand.High)]
    [InlineData(750, Lyrics.MatchBand.PrettyHigh)]
    [InlineData(600, Lyrics.MatchBand.Medium)]
    [InlineData(599, Lyrics.MatchBand.Low)]
    public void Mxm_matcher_confidence_maps_onto_bands(int confidence, Lyrics.MatchBand band)
        => Assert.Equal(band, Lyrics.Sources.Musixmatch.BandOfConfidence(confidence));

    // ── Musixmatch: token ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mxm_token_is_reused_within_its_ttl()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(_ => Ok(Macro(richsync: Richsync3)));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        Assert.NotNull(await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None));
        Assert.NotNull(await src.FetchAsync(Req("QZABC2600001", "trk2"), CancellationToken.None));
        Assert.Equal(1, http.Count(MxmToken));

        clock.Now += Lyrics.Sources.Musixmatch.MxmProfile.Desktop.TokenTtlMs;
        Assert.NotNull(await src.FetchAsync(Req("QZABC2600001", "trk3"), CancellationToken.None));
        Assert.Equal(2, http.Count(MxmToken));
    }

    [Fact]
    public async Task Mxm_upgrade_only_token_is_refused()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(_ => Ok(Macro(richsync: Richsync3)), token: "UpgradeOnlyUpgradeOnlyUpgradeOnlyUpgradeOnly");
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        Assert.Null(await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None));
        Assert.Equal(0, http.Count(MxmMacro));
        Assert.False(guard.TryGetMiss("trk1", out _, out _));
    }

    // ── Musixmatch: status gates ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mxm_401_renew_drops_the_token_and_retries_once()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        int tokens = 0, macros = 0;
        var http = new FakeHttp(
            (MxmToken, _ => Ok(Token("tok-" + (++tokens).ToString(System.Globalization.CultureInfo.InvariantCulture)))),
            (MxmMacro, _ => ++macros == 1 ? new Lyrics.HttpResult(401, Status(401, "renew")) : Ok(Macro(richsync: Richsync3))));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        var c = await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal(2, http.Count(MxmToken));
        Assert.Equal(2, http.Count(MxmMacro));
        Assert.Contains("usertoken=tok-2", http.Calls.Last(u => u.StartsWith(MxmMacro, StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.False(guard.IsOpen(out _, out _));
    }

    [Fact]
    public async Task Mxm_401_renew_retries_exactly_once()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        int tokens = 0;
        var http = new FakeHttp(
            (MxmToken, _ => Ok(Token("tok-" + (++tokens).ToString(System.Globalization.CultureInfo.InvariantCulture)))),
            (MxmMacro, _ => Ok(Status(401, "renew"))));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        Assert.Null(await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None));
        Assert.Equal(2, http.Count(MxmMacro));
        Assert.Equal(2, http.Count(MxmToken));
        Assert.False(guard.IsOpen(out _, out _));
        Assert.False(guard.TryGetMiss("trk1", out _, out _));
    }

    [Fact]
    public async Task Mxm_401_captcha_opens_the_breaker()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(_ => Ok(Status(401, "captcha")));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        Assert.Null(await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None));
        Assert.True(guard.IsOpen(out string reason, out long until));
        Assert.Contains("captcha", reason, StringComparison.Ordinal);
        Assert.Equal(clock.Now + Lyrics.Sources.Musixmatch.CaptchaTripMs, until);
        Assert.Equal(1, http.Count(MxmMacro));

        int before = http.Calls.Count;
        Assert.Null(await src.FetchAsync(Req("QZABC2600001", "trk2"), CancellationToken.None));
        Assert.Equal(before, http.Calls.Count);   // no HTTP while the breaker is open
    }

    [Fact]
    public async Task Mxm_402_opens_the_breaker_for_the_quota()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(_ => Ok(Status(402, "")));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        Assert.Null(await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None));
        Assert.True(guard.IsOpen(out string reason, out long until));
        Assert.Contains("quota", reason, StringComparison.Ordinal);
        Assert.Equal(clock.Now + Lyrics.Sources.Musixmatch.QuotaTripMs, until);
    }

    // ── Musixmatch: bodies ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mxm_instrumental_is_a_remembered_no_lyric()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(_ => Ok(Macro(hasRichsync: 0, instrumental: 1)));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        Assert.Null(await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None));
        Assert.True(guard.TryGetMiss("trk1", out var kind, out _));
        Assert.Equal(Lyrics.SourceGuard.MissKind.NoLyricForSong, kind);
    }

    [Fact]
    public async Task Mxm_richsync_gives_a_trusted_word_synced_candidate()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(_ => Ok(Macro(richsync: Richsync3, subtitle: Lines3)));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        var c = await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal("musixmatch", c!.ProviderId);
        Assert.Equal(Lyrics.SyncKind.Syllable, c.Sync);
        Assert.Equal(3, c.LineCount);
        Assert.Equal(Lyrics.MatchBasis.Isrc, c.Basis);
        Assert.Equal(Lyrics.MatchBand.Perfect, c.Band);
        Assert.Equal(1.0, c.Confidence);
        Assert.Equal(0.70, c.Prior);
        var macro = http.Calls.Single(u => u.StartsWith(MxmMacro, StringComparison.Ordinal));
        Assert.Contains("track_isrc=QZABC2600001", macro, StringComparison.Ordinal);
        Assert.Contains("track_spotify_id=spotify%3Atrack%3Atrk1", macro, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mxm_missing_richsync_falls_back_to_the_lrc_subtitle()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(_ => Ok(Macro(richsync: null, subtitle: Lines3, hasRichsync: 1)));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        var c = await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal(Lyrics.SyncKind.Line, c!.Sync);
        Assert.Equal(3, c.LineCount);
        Assert.Equal(Lyrics.MatchBasis.Isrc, c.Basis);
        Assert.Equal(1, http.Count(MxmMacro));   // same response, no extra round trip
    }

    [Fact]
    public async Task Mxm_empty_richsync_body_falls_back_to_the_lrc_subtitle()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(_ => Ok(Macro(richsync: "[]", subtitle: Lines3)));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        var c = await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal(Lyrics.SyncKind.Line, c!.Sync);
    }

    [Fact]
    public async Task Mxm_decoy_subtitle_is_returned_for_the_reranker_to_judge()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        string decoy = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lyrics", "musixmatch-decoy-sorry-seems.lrc"));
        var http = MxmHttp(_ => Ok(Macro(subtitle: decoy, hasRichsync: 0)));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        var c = await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None);

        // The SOURCE does not judge decoys — the aggregator's decoy gates and the reranker do.
        Assert.NotNull(c);
        Assert.Equal(Lyrics.SyncKind.Line, c!.Sync);
        Assert.True(c.LineCount > 100);
        Assert.Equal(Lyrics.MatchBasis.Isrc, c.Basis);
        Assert.Equal(Lyrics.MatchBand.Perfect, c.Band);
    }

    // ── Musixmatch: basis ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mxm_identity_miss_falls_back_to_search_with_the_matcher_band()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(url => url.Contains("track_spotify_id=", StringComparison.Ordinal)
            ? Ok(Macro(matcherStatus: 404))
            : Ok(Macro(richsync: Richsync3, confidence: 950)));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        var c = await src.FetchAsync(Req(), CancellationToken.None);   // no ISRC: identity by Spotify id only

        Assert.NotNull(c);
        Assert.Equal(Lyrics.MatchBasis.MetadataSearch, c!.Basis);
        Assert.Equal(Lyrics.MatchBand.VeryHigh, c.Band);
        Assert.Equal(0.95, c.Confidence, 3);
        Assert.Equal(2, http.Count(MxmMacro));
        Assert.Contains("q_track=Paper%20Lanterns", http.Calls.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mxm_identity_match_on_a_different_version_is_not_trusted()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(url => url.Contains("track_isrc=", StringComparison.Ordinal)
            ? Ok(Macro(richsync: Richsync3, title: "Paper Lanterns (Live)"))
            : Ok(Macro(matcherStatus: 404)));
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        Assert.Null(await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None));
        Assert.True(guard.TryGetMiss("trk1", out var kind, out _));
        Assert.Equal(Lyrics.SourceGuard.MissKind.SongNotFound, kind);
    }

    [Fact]
    public async Task Mxm_capture_redacts_the_usertoken()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(_ => Ok(Macro(richsync: Richsync3)), token: "secret-token-123");
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);
        var probe = new Lyrics.Probe();
        Lyrics.Probe.Current.Value = probe;
        try
        {
            Assert.NotNull(await src.FetchAsync(Req("QZABC2600001"), CancellationToken.None));
        }
        finally { Lyrics.Probe.Current.Value = null; }

        var raw = Assert.Single(probe.RawPayloads());   // only the final payload
        Assert.Equal("musixmatch", raw.SourceId);
        Assert.DoesNotContain("secret-token-123", raw.Label, StringComparison.Ordinal);
        Assert.Contains("usertoken=", raw.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mxm_cancellation_propagates_and_is_never_a_failure()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = new FakeHttp((MxmToken, _ => Ok(Token("tok-1")))) { Throw = _ => new OperationCanceledException() };
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        for (int i = 0; i < Lyrics.SourceGuard.FailuresToOpen + 1; i++)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => src.FetchAsync(Req("QZABC2600001"), CancellationToken.None));

        Assert.False(guard.IsOpen(out _, out _));
        Assert.False(guard.TryGetMiss("trk1", out _, out _));
    }

    [Fact]
    public async Task Mxm_cancellation_mid_macro_propagates()
    {
        var clock = new Clock();
        var guard = Guard("musixmatch", clock);
        var http = MxmHttp(_ => throw new OperationCanceledException());
        var src = new Lyrics.Sources.Musixmatch(http, guard, clock.Read);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => src.FetchAsync(Req("QZABC2600001"), CancellationToken.None));
        Assert.False(guard.IsOpen(out _, out _));
        Assert.Equal(1, http.Count(MxmToken));
    }
}
