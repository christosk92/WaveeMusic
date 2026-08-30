using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Lyrics.Lyricify;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.Backend.Lyrics.Sources;

// The "grey" providers (docs/lyrics-aggregator-reranker-plan.md §6) — reverse-engineered CJK + Musixmatch APIs that carry
// word/syllable timing (QRC/KRC/YRC/richsync). OFF by default; the live wiring adds them only when grey is enabled. They
// use the shared HttpClient directly (POST/forms/provider headers); the decrypters + body parsers are the unit-tested
// part. Endpoints/flows per Lyricify.Lyrics.Helper. A miss/timeout/throw → null (the aggregate is never failed).

static class GreyHttp
{
    const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Wavee/1.0";

    public static async Task<string?> Get(string url, (string, string)[]? headers, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            Add(req, headers);
            using var resp = await HttpPools.Get(HttpPool.ThirdParty).SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static async Task<string?> PostJson(string url, string json, (string, string)[]? headers, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            Add(req, headers);
            using var resp = await HttpPools.Get(HttpPool.ThirdParty).SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static async Task<string?> PostForm(string url, (string, string)[] form, (string, string)[]? headers, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))) };
            Add(req, headers);
            using var resp = await HttpPools.Get(HttpPool.ThirdParty).SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    static void Add(HttpRequestMessage req, (string, string)[]? headers)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);
        if (headers is null) return;
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
    }

    public static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Kugou (KRC, word-synced). search → hash → lyric search (accesskey+id) → download (base64 KRC) → decrypt.</summary>
public sealed class KugouSource : ILyricCandidateSource
{
    public string Id => "kugou";
    public bool Enabled => true;
    public double Prior => 0.5;

    public async Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct)
    {
        foreach (var kw in LyricsQuery.Variants(req))
        {
            var cand = await TryForKeyword(kw, req, ct).ConfigureAwait(false);
            if (cand is not null) { LyricsProbe.Note(Id, $"hit on variant '{kw}'"); return cand; }
            ct.ThrowIfCancellationRequested();
        }
        return null;
    }

    async Task<LyricsCandidate?> TryForKeyword(string kw, LyricsRequest req, CancellationToken ct)
    {
        string searchUrl = $"http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={Uri.EscapeDataString(kw)}&page=1&pagesize=10&showtype=1";
        string? sj = await GreyHttp.Get(searchUrl, null, ct).ConfigureAwait(false);
        LyricsProbe.CaptureRaw(Id, LyricsProbe.Redact(searchUrl), "json", sj);
        if (sj is null) return null;

        string? hash = null; long bestDelta = long.MaxValue; int n = 0;
        try
        {
            using var d = JsonDocument.Parse(sj);
            if (!d.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array) return null;
            foreach (var it in info.EnumerateArray())
            {
                n++;
                string? h = GreyHttp.Str(it, "hash");
                long dsec = it.TryGetProperty("duration", out var dv) && dv.TryGetInt64(out var ds) ? ds : 0;
                long delta = req.DurationMs > 0 && dsec > 0 ? Math.Abs(dsec * 1000 - req.DurationMs) : 0;
                if (h is not null && delta < bestDelta) { bestDelta = delta; hash = h; }
            }
        }
        catch { return null; }
        LyricsProbe.Note(Id, $"song search '{kw}' → {n} results");
        if (hash is null) return null;

        string lyrSearch = $"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Uri.EscapeDataString(req.Title)}&duration={req.DurationMs}&hash={hash}";
        string? lj = await GreyHttp.Get(lyrSearch, null, ct).ConfigureAwait(false);
        LyricsProbe.CaptureRaw(Id, LyricsProbe.Redact(lyrSearch), "json", lj);
        if (lj is null) { LyricsProbe.Note(Id, "matched song but lyric search HTTP failed"); return null; }
        string? id = null, accesskey = null;
        try
        {
            using var d = JsonDocument.Parse(lj);
            if (d.RootElement.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array)
                foreach (var c in cands.EnumerateArray()) { id = GreyHttp.Str(c, "id"); accesskey = GreyHttp.Str(c, "accesskey"); if (id is not null && accesskey is not null) break; }
        }
        catch { return null; }
        if (id is null || accesskey is null) { LyricsProbe.Note(Id, "matched song but kugou has no KRC lyric for it (no candidate)"); return null; }

        // The download endpoint is PICKY, and gets it wrong quietly: the id parameter must be `id` (NOT `kgid`) and
        // `ver=1` is mandatory. Miss either and it answers HTTP 200 carrying {"status":400,"info":"Bad Request"} with no
        // content — which read as "this track has no KRC" and silently cost us every Kugou word-synced lyric. Verified
        // against the live endpoint: kgid+ver → 400, id without ver → 400, id+ver → 200. `client=pc` is optional.
        string dl = $"https://lyrics.kugou.com/download?ver=1&client=pc&id={id}&accesskey={accesskey}&fmt=krc&charset=utf8";
        string? dj = await GreyHttp.Get(dl, null, ct).ConfigureAwait(false);
        LyricsProbe.CaptureRaw(Id, LyricsProbe.Redact(dl), "json", dj);
        if (dj is null) return null;
        string? b64; string status = "";
        try
        {
            using var d = JsonDocument.Parse(dj);
            b64 = GreyHttp.Str(d.RootElement, "content");
            // Carry the endpoint's OWN verdict into the breadcrumb. "empty content" hid a 400 for as long as it took to
            // capture the payload and look.
            if (d.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number)
                status = $" (status {st.GetInt32()} {GreyHttp.Str(d.RootElement, "info") ?? ""})".TrimEnd();
        }
        catch { return null; }
        if (string.IsNullOrEmpty(b64)) { LyricsProbe.Note(Id, "KRC download returned empty content" + status); return null; }

        string? krc = LyricCrypto.DecryptKrc(b64!);
        if (krc is null) { LyricsProbe.Note(Id, "matched song but KRC decrypt failed"); return null; }
        // The DECRYPTED body, not the base64 envelope: this is the text the parser is handed, so it is the one that
        // answers "did the provider's timings look like this, or did we mangle them?".
        LyricsProbe.CaptureRaw(Id, "decrypted KRC (kgid " + id + ")", "krc", krc);
        var doc = LyricsWordFormats.ParseKrc(krc, req.TrackId, Id);
        if (doc.Lines.Count == 0) { LyricsProbe.Note(Id, "matched song but KRC parsed to 0 lines"); return null; }
        return new LyricsCandidate(Id, Prior, MatchBasis.MetadataSearch, doc);
    }
}

/// <summary>NetEase Cloud Music. Public web API (no EAPI): search → song id → lyric (request yrc). Prefers YRC (word) when
/// present, else the line LRC.</summary>
public sealed class NeteaseSource : ILyricCandidateSource
{
    static readonly (string, string)[] Headers = { ("Referer", "https://music.163.com"), ("Cookie", "appver=8.9.70; os=pc") };

    public string Id => "netease";
    public bool Enabled => true;
    public double Prior => 0.5;

    public async Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct)
    {
        foreach (var kw in LyricsQuery.Variants(req))
        {
            var cand = await TryForKeyword(kw, req, ct).ConfigureAwait(false);
            if (cand is not null) { LyricsProbe.Note(Id, $"hit on variant '{kw}'"); return cand; }
            ct.ThrowIfCancellationRequested();
        }
        return null;
    }

    async Task<LyricsCandidate?> TryForKeyword(string kw, LyricsRequest req, CancellationToken ct)
    {
        string searchUrl = $"https://music.163.com/api/search/get/web?type=1&offset=0&limit=10&s={Uri.EscapeDataString(kw)}";
        string? sj = await GreyHttp.Get(searchUrl, Headers, ct).ConfigureAwait(false);
        LyricsProbe.CaptureRaw(Id, LyricsProbe.Redact(searchUrl), "json", sj);
        if (sj is null) return null;

        string? id = null; long bestDelta = long.MaxValue; int n = 0;
        try
        {
            using var d = JsonDocument.Parse(sj);
            if (!d.RootElement.TryGetProperty("result", out var r) || !r.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return null;
            foreach (var s in songs.EnumerateArray())
            {
                n++;
                string? sid = s.TryGetProperty("id", out var iv) ? (iv.ValueKind == JsonValueKind.Number ? iv.GetInt64().ToString() : iv.GetString()) : null;
                long dur = s.TryGetProperty("duration", out var dv) && dv.TryGetInt64(out var dd) ? dd : 0;
                long delta = req.DurationMs > 0 && dur > 0 ? Math.Abs(dur - req.DurationMs) : 0;
                if (sid is not null && delta < bestDelta) { bestDelta = delta; id = sid; }
            }
        }
        catch { return null; }
        LyricsProbe.Note(Id, $"song search '{kw}' → {n} results");
        if (id is null) return null;

        string lyrUrl = $"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=1&yv=1";
        string? lj = await GreyHttp.Get(lyrUrl, Headers, ct).ConfigureAwait(false);
        LyricsProbe.CaptureRaw(Id, LyricsProbe.Redact(lyrUrl), "json", lj);
        if (lj is null) return null;
        try
        {
            using var d = JsonDocument.Parse(lj);
            var root = d.RootElement;
            string? yrc = root.TryGetProperty("yrc", out var y) ? GreyHttp.Str(y, "lyric") : null;
            if (!string.IsNullOrWhiteSpace(yrc))
            {
                var doc = LyricsWordFormats.ParseYrc(yrc!, req.TrackId, Id);
                if (doc.Lines.Count > 0) return new LyricsCandidate(Id, Prior, MatchBasis.MetadataSearch, doc);
            }
            string? lrc = root.TryGetProperty("lrc", out var l) ? GreyHttp.Str(l, "lyric") : null;
            if (!string.IsNullOrWhiteSpace(lrc))
            {
                var doc = LyricsText.ParseLrc(lrc!, req.TrackId, Id);
                if (doc.Lines.Count > 0) return new LyricsCandidate(Id, Prior, MatchBasis.MetadataSearch, doc);
            }
        }
        catch { return null; }
        LyricsProbe.Note(Id, "matched song but it carried no yrc/lrc lyric");
        return null;
    }
}

/// <summary>QQ Music (QRC, word-synced). search (musicu.fcg) → numeric songid → lyric_download.fcg (XML w/ hex QRC) → decrypt.</summary>
public sealed class QqMusicSource : ILyricCandidateSource
{
    static readonly (string, string)[] Headers = { ("Referer", "https://y.qq.com") };

    public string Id => "qq";
    public bool Enabled => true;
    public double Prior => 0.55;

    public async Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct)
    {
        foreach (var kw in LyricsQuery.Variants(req))
        {
            var cand = await TryForKeyword(kw, req, ct).ConfigureAwait(false);
            if (cand is not null) { LyricsProbe.Note(Id, $"hit on variant '{kw}'"); return cand; }
            ct.ThrowIfCancellationRequested();
        }
        return null;
    }

    async Task<LyricsCandidate?> TryForKeyword(string kw, LyricsRequest req, CancellationToken ct)
    {
        string body = $"{{\"req_1\":{{\"method\":\"DoSearchForQQMusicDesktop\",\"module\":\"music.search.SearchCgiService\",\"param\":{{\"query\":{JsonString(kw)},\"page_num\":1,\"num_per_page\":10,\"search_type\":0}}}}}}";
        string? sj = await GreyHttp.PostJson("https://u.y.qq.com/cgi-bin/musicu.fcg", body, Headers, ct).ConfigureAwait(false);
        LyricsProbe.CaptureRaw(Id, "POST musicu.fcg (DoSearchForQQMusicDesktop)", "json", sj);
        if (sj is null) return null;

        string? songid = null; long bestDelta = long.MaxValue; int n = 0;
        try
        {
            using var d = JsonDocument.Parse(sj);
            if (!d.RootElement.TryGetProperty("req_1", out var r1) || !r1.TryGetProperty("data", out var data)
                || !data.TryGetProperty("body", out var b) || !b.TryGetProperty("song", out var song)
                || !song.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            foreach (var it in list.EnumerateArray())
            {
                n++;
                string? sid = it.TryGetProperty("songid", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt64().ToString() : null;
                long sec = it.TryGetProperty("interval", out var dv) && dv.TryGetInt64(out var dd) ? dd : 0;
                long delta = req.DurationMs > 0 && sec > 0 ? Math.Abs(sec * 1000 - req.DurationMs) : 0;
                if (sid is not null && delta < bestDelta) { bestDelta = delta; songid = sid; }
            }
        }
        catch { return null; }
        LyricsProbe.Note(Id, $"song search '{kw}' → {n} results");
        if (songid is null) return null;

        var form = new (string, string)[] { ("version", "15"), ("miniversion", "82"), ("lrctype", "4"), ("musicid", songid) };
        string? xml = await GreyHttp.PostForm("https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg", form, Headers, ct).ConfigureAwait(false);
        if (xml is null) { LyricsProbe.Note(Id, "matched song but lyric download HTTP failed"); return null; }
        string? hex = LongestHexRun(xml);
        if (hex is null) { LyricsProbe.Note(Id, "matched song but qq has no QRC lyric for it (empty response)"); return null; }
        string? qrc = LyricCrypto.DecryptQrc(hex);
        if (qrc is null) { LyricsProbe.Note(Id, "matched song but QRC decrypt failed"); return null; }
        // The decrypted QRC, not the hex blob the XML carries — see the KRC capture above for why.
        LyricsProbe.CaptureRaw(Id, "decrypted QRC (musicid " + songid + ")", "qrc", qrc);
        var doc = LyricsWordFormats.ParseQrc(qrc, req.TrackId, Id);
        if (doc.Lines.Count == 0) { LyricsProbe.Note(Id, "matched song but QRC parsed to 0 lines"); return null; }
        return new LyricsCandidate(Id, Prior, MatchBasis.MetadataSearch, doc);
    }

    static string JsonString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // The QRC lyric is the longest contiguous hex run in the XML response (the <content>/LyricContent payload).
    static string? LongestHexRun(string xml)
    {
        int bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
        for (int i = 0; i <= xml.Length; i++)
        {
            bool hex = i < xml.Length && Uri.IsHexDigit(xml[i]);
            if (hex) { if (curStart < 0) curStart = i; curLen++; }
            else { if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; } curStart = -1; curLen = 0; }
        }
        return bestLen >= 16 && bestLen % 2 == 0 ? xml.Substring(bestStart, bestLen) : null;
    }
}

/// <summary>Musixmatch richsync (word/char-level). token.get (cached) → macro.subtitles.get (richsync). Token can hit a
/// captcha (401) — best-effort, returns null on any failure.</summary>
public sealed class MusixmatchSource : ILyricCandidateSource
{
    const string AppId = "web-desktop-app-v1.0";
    static readonly (string, string)[] Headers = { ("Cookie", "x-mxm-token-guid=") };
    static string? _token;
    static readonly SemaphoreSlim _tokGate = new(1, 1);

    public string Id => "musixmatch";
    public bool Enabled => true;
    public double Prior => 0.7;

    public async Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct)
    {
        string? token = await GetTokenAsync(ct).ConfigureAwait(false);
        if (token is null) { LyricsProbe.Note(Id, "no usertoken — token.get blocked (captcha/401)"); return null; }

        long durSec = req.DurationMs / 1000;

        // ISRC fast-path: an EXACT-recording lookup (no fuzzy title/artist guess). The reranker trusts MatchBasis.Isrc and
        // never demotes it. Inert until the track's ISRC is wired through (LiveSessionHost.Resolve).
        if (!string.IsNullOrEmpty(req.Isrc))
        {
            var byIsrc = await TryUrl(BuildSubtitlesUrl(token, req.Isrc, null, null, durSec, Rnd()), MatchBasis.Isrc, req, ct).ConfigureAwait(false);
            if (byIsrc is not null) { LyricsProbe.Note(Id, $"ISRC fast-path hit ({req.Isrc})"); return byIsrc; }
            LyricsProbe.Note(Id, $"ISRC '{req.Isrc}' miss → q_track/q_artist");
        }

        // Fuzzy fallback: q_track/q_artist over the first two variants (full + feat-stripped). A title-only q_track is
        // skipped — too prone to wrong-language covers; the ISRC path covers the precise case.
        var variants = LyricsQuery.TitleArtistVariants(req);
        for (int i = 0; i < variants.Count && i < 2; i++)
        {
            var (title, artist) = variants[i];
            if (artist.Length == 0) continue;
            var cand = await TryUrl(BuildSubtitlesUrl(token, null, title, artist, durSec, Rnd()), MatchBasis.MetadataSearch, req, ct).ConfigureAwait(false);
            if (cand is not null) { LyricsProbe.Note(Id, $"hit on q_track '{title}'"); return cand; }
            ct.ThrowIfCancellationRequested();
        }
        LyricsProbe.Note(Id, $"no richsync for '{req.Title} / {req.PrimaryArtist}' ({durSec}s)");
        return null;
    }

    static string Rnd() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>The macro.subtitles.get URL. When <paramref name="isrc"/> is set it keys on <c>track_isrc</c> (exact
    /// recording); otherwise on <c>q_track</c>/<c>q_artist</c>. Pure → unit-testable without network.</summary>
    public static string BuildSubtitlesUrl(string token, string? isrc, string? qTrack, string? qArtist, long durSec, string rnd)
    {
        string q = !string.IsNullOrEmpty(isrc)
            ? $"&track_isrc={Uri.EscapeDataString(isrc!)}"
            : $"&q_track={Uri.EscapeDataString(qTrack ?? "")}&q_artist={Uri.EscapeDataString(qArtist ?? "")}";
        return "https://apic-desktop.musixmatch.com/ws/1.1/macro.subtitles.get"
            + "?namespace=lyrics_richsynched&optional_calls=track.richsync&subtitle_format=lrc"
            + q + $"&q_duration={durSec}&usertoken={token}&format=json&app_id={AppId}&t={rnd}";
    }

    async Task<LyricsCandidate?> TryUrl(string url, MatchBasis basis, LyricsRequest req, CancellationToken ct)
    {
        string? json = await GreyHttp.Get(url, Headers, ct).ConfigureAwait(false);
        LyricsProbe.CaptureRaw(Id, LyricsProbe.Redact(url), "json", json);
        if (json is null) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            // message.body.macro_calls["track.richsync.get"].message.body.richsync.richsync_body
            if (TryGetRichsyncBody(d.RootElement, out var body))
            {
                var doc = LyricsWordFormats.ParseRichsync(body, req.TrackId, Id);
                // A richsync body whose word timing is not physically singable is WORSE than the LRC sitting in the very
                // same response: word-sync is the top rerank tier, so it would out-rank a clean line-synced candidate on
                // timing it does not actually have. Prefer the subtitle below when that happens — same request, no extra
                // round-trip, and the line starts (the part such a body gets right) survive either way.
                if (doc.Lines.Count > 0)
                {
                    if (!LyricsTiming.HasImplausibleWordTiming(doc, out int impossible, out int judged))
                        return new LyricsCandidate(Id, Prior, basis, doc);
                    LyricsProbe.Note(Id, $"richsync rejected — {impossible}/{judged} lines would have to be sung faster "
                        + $"than {LyricsTiming.ImpossibleWordsPerSecond:0} words/second; falling back to the LRC subtitle");
                }
            }

            // The same macro response normally also carries track.subtitles.get as LRC. Keep it as a line-synced fallback
            // when richsync is missing, malformed or (above) not physically singable, instead of turning a valid
            // Musixmatch hit into a miss.
            if (TryGetSubtitleBody(d.RootElement, out var lrc))
            {
                var doc = LyricsText.ParseLrc(lrc, req.TrackId, Id);
                if (doc.Lines.Count > 0) return new LyricsCandidate(Id, Prior, basis, doc);
            }

            return null;
        }
        catch { return null; }
    }

    static bool TryGetRichsyncBody(JsonElement root, out string body)
    {
        body = "";
        if (!TryGetMacroCalls(root, out var mc) || !mc.TryGetProperty("track.richsync.get", out var rg)) return false;
        if (!rg.TryGetProperty("message", out var m) || !m.TryGetProperty("body", out var b)) return false;
        if (!b.TryGetProperty("richsync", out var richsync)) return false;
        body = GreyHttp.Str(richsync, "richsync_body") ?? "";
        return !string.IsNullOrWhiteSpace(body);
    }

    static bool TryGetSubtitleBody(JsonElement root, out string body)
    {
        body = "";
        if (!TryGetMacroCalls(root, out var mc) || !mc.TryGetProperty("track.subtitles.get", out var sg)) return false;
        if (!sg.TryGetProperty("message", out var m) || !m.TryGetProperty("body", out var b)) return false;
        if (!b.TryGetProperty("subtitle_list", out var list) || list.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("subtitle", out var sub)) continue;
            body = GreyHttp.Str(sub, "subtitle_body") ?? "";
            if (!string.IsNullOrWhiteSpace(body)) return true;
        }
        return false;
    }

    static bool TryGetMacroCalls(JsonElement root, out JsonElement macroCalls)
    {
        macroCalls = default;
        if (!root.TryGetProperty("message", out var m) || !m.TryGetProperty("body", out var b)) return false;
        return b.TryGetProperty("macro_calls", out macroCalls);
    }

    static async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null) return _token;
        await _tokGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null) return _token;
            string rnd = Guid.NewGuid().ToString("N")[..8];
            string url = $"https://apic-desktop.musixmatch.com/ws/1.1/token.get?app_id={AppId}&t={rnd}";
            string? json = await GreyHttp.Get(url, Headers, ct).ConfigureAwait(false);
            if (json is null) return null;
            using var d = JsonDocument.Parse(json);
            if (d.RootElement.TryGetProperty("message", out var m) && m.TryGetProperty("body", out var b))
            {
                string? t = GreyHttp.Str(b, "user_token");
                if (!string.IsNullOrWhiteSpace(t) && t != "UpgradeOnlyUpgradeOnlyUpgradeOnlyUpgradeOnly") _token = t;
            }
            return _token;
        }
        catch { return null; }
        finally { _tokGate.Release(); }
    }
}
