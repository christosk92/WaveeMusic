// ── Shell/Lyrics.Grey.Kugou.cs ─────────────────────────────────────────────────────────────────────────────────────
// the Kugou grey source (KRC, word-synced) + what the two Chinese sources (kugou, qq) share
//
// Role: SHELL
// Spec: docs/plans/wavee/lyrics-grey-sources-implementation.md (W2-E1, frozen contracts)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE FLOW (0.2.9's KugouSource, over the shared guard and ladder):
//
//   guard      breaker open / remembered miss → a note, null, no request
//   search     GET http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword=…   (plain http, whole seconds)
//              → `Hit`s → `GreyLadder.SearchAsync` (variants pooled, early stop on VeryHigh, ≤ 2 hits ≥ Medium)
//   lyric      GET https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword=…&duration=<ms>&hash=<hash>
//              → the first candidate's `id` + `accesskey`
//   download   GET https://lyrics.kugou.com/download?ver=1&client=pc&id=…&accesskey=…&fmt=krc&charset=utf8
//              The endpoint is PICKY and wrong quietly: the parameter is `id` (never `kgid`) and `ver=1` is mandatory —
//              miss either and it answers 200 with {"status":400,"info":"Bad Request"} and no content.
//              → base64 → `Crypto.DecryptKrc` → `Probe.CaptureRaw` (the decrypted KRC, the only capture) →
//              `WordFormats.ParseKrc` → a word-synced `Candidate` carrying the hit's band / confidence / breadcrumb.
//
// VERDICTS. Every service answer → `SourceGuard.Success`; a transport failure (no answer, 5xx, 429, unparseable body) →
// ONE `SourceGuard.Failure` for the fetch, and the fetch stops asking (the host is sick — the rest of the ladder would
// only add failures); nothing found at ≥ Medium → a `SongNotFound` miss; song found but no lyric body for any fetched hit
// → a `NoLyricForSong` miss. A cancellation (the per-source timeout, the gold cancel) is rethrown and never judged.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee;

public static partial class Lyrics
{
    public static partial class Sources
    {
        /// <summary>Kugou (KRC, word-synced): song search → hash → lyric search (id + accesskey) → download (base64 KRC)
        /// → decrypt → parse. Matched by metadata, so the candidate carries its <see cref="MatchBand"/> and the
        /// reranker weighs it by <see cref="Candidate.Confidence"/>.</summary>
        public sealed class Kugou : ISource
        {
            const string SongSearchUrl = "http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword=";
            const string LyricSearchUrl = "https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword=";
            const string DownloadUrl = "https://lyrics.kugou.com/download?ver=1&client=pc&id=";

            readonly IHttpPost _http;
            readonly SourceGuard _guard;

            public Kugou(IHttpPost http)
            {
                _http = http ?? throw new ArgumentNullException(nameof(http));
                _guard = SourceGuard.For("kugou");
            }

            public string Id => "kugou";
            public bool Enabled => true;
            public double Prior => 0.50;

            public void Forget(string trackId) => _guard.Forget(trackId);

            public async Task<Candidate?> FetchAsync(Request req, CancellationToken ct)
            {
                if (!CnGrey.Preflight(_guard, Id, req)) return null;
                var st = new CnGrey.FetchState();
                try
                {
                    var hits = await GreyLadder.SearchAsync(Id, req, (kw, c) => SearchAsync(kw, st, c), ct).ConfigureAwait(false);

                    foreach (var (hit, score) in hits)
                    {
                        var doc = await LyricAsync(req, hit, st, ct).ConfigureAwait(false);
                        if (doc is null)
                        {
                            if (st.Failure is not null) break;
                            continue;
                        }
                        CnGrey.Settle(_guard, st);
                        return CnGrey.Found(Id, Prior, req, hit, score, doc);
                    }

                    CnGrey.Settle(_guard, st);
                    CnGrey.Missed(_guard, Id, req, st, songFound: hits.Count > 0);
                    return null;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    // A parser or mapping bug costs this source its answer, never the fan-out.
                    Probe.Note(Id, "failed: " + e.GetType().Name);
                    CnGrey.Decision(Id, "error=" + e.GetType().Name);
                    return null;
                }
            }

            // ── song search → hits ──────────────────────────────────────────────────────────────────────────────────

            async Task<IReadOnlyList<Hit>?> SearchAsync(string keyword, CnGrey.FetchState st, CancellationToken ct)
            {
                string url = SongSearchUrl + Uri.EscapeDataString(keyword) + "&page=1&pagesize=10&showtype=1";
                string? body = await CnGrey.SendAsync(Id, st, "song search", () => _http.GetAsync(url, null, ct)).ConfigureAwait(false);
                if (body is null) return null;

                var parsed = CnGrey.TryParse(body, KugouJson.Default.KugouSongSearch);
                if (parsed is null) { st.Fail("song search: unparseable answer"); return null; }

                KugouSongData? data = parsed.Data.ValueKind == JsonValueKind.Object
                    ? CnGrey.TryParse(parsed.Data, KugouJson.Default.KugouSongData)
                    : null;
                if (data?.Info is not { } info)
                {
                    // An API-level refusal (status 0 / an errcode), not a "no such song" — nothing is cached for it.
                    Probe.Note(Id, $"song search '{keyword}': no result list (status {parsed.Status}, errcode {parsed.ErrCode})");
                    st.Incomplete = true;
                    return null;
                }
                st.Answered = true;

                var hits = new List<Hit>(info.Length);
                foreach (var s in info)
                {
                    if (s is null || string.IsNullOrWhiteSpace(s.Hash)) continue;
                    string title = StripEm(s.SongName);
                    string singer = StripEm(s.SingerName);
                    if (title.Length == 0 && s.FileName is { Length: > 0 } file)
                    {
                        // "Artist - Title" when the split fields are missing.
                        string f = StripEm(file);
                        int dash = f.IndexOf(" - ", StringComparison.Ordinal);
                        title = dash >= 0 ? f[(dash + 3)..].Trim() : f;
                        if (singer.Length == 0 && dash > 0) singer = f[..dash].Trim();
                    }
                    long durMs = s.Duration > 0 ? (long)Math.Round(s.Duration * 1000d) : 0;
                    string[] artists = singer.Length > 0 ? [singer] : [];
                    hits.Add(new Hit(s.Hash!, title, artists, NullIfBlank(StripEm(s.AlbumName)), durMs, 1000));
                }
                return hits;
            }

            // ── one hit → its KRC document ──────────────────────────────────────────────────────────────────────────

            async Task<Doc?> LyricAsync(Request req, Hit hit, CnGrey.FetchState st, CancellationToken ct)
            {
                long durMs = req.DurationMs > 0 ? req.DurationMs : hit.DurationMs;
                string search = LyricSearchUrl + Uri.EscapeDataString(req.Title ?? "")
                    + "&duration=" + durMs.ToString(CultureInfo.InvariantCulture)
                    + "&hash=" + Uri.EscapeDataString(hit.Id);
                string? lj = await CnGrey.SendAsync(Id, st, "lyric search", () => _http.GetAsync(search, null, ct)).ConfigureAwait(false);
                if (lj is null) return null;
                var ls = CnGrey.TryParse(lj, KugouJson.Default.KugouLyricSearch);
                if (ls is null) { st.Fail("lyric search: unparseable answer"); return null; }
                st.Answered = true;

                string? lyricId = null, accessKey = null;
                if (ls.Candidates is { } cands)
                    foreach (var c in cands)
                        if (c is { Id.Length: > 0, AccessKey.Length: > 0 }) { lyricId = c.Id; accessKey = c.AccessKey; break; }
                if (lyricId is null || accessKey is null)
                {
                    Probe.Note(Id, $"{Short(hit.Id)}: song found, no KRC candidate (status {ls.Status} {ls.Info})".TrimEnd());
                    return null;
                }

                // `id`, never `kgid`; `ver=1` mandatory (header comment).
                string dl = DownloadUrl + Uri.EscapeDataString(lyricId) + "&accesskey=" + Uri.EscapeDataString(accessKey)
                    + "&fmt=krc&charset=utf8";
                string? dj = await CnGrey.SendAsync(Id, st, "KRC download", () => _http.GetAsync(dl, null, ct)).ConfigureAwait(false);
                if (dj is null) return null;
                var download = CnGrey.TryParse(dj, KugouJson.Default.KugouDownload);
                if (download is null) { st.Fail("KRC download: unparseable answer"); return null; }
                st.Answered = true;

                if (string.IsNullOrEmpty(download.Content))
                {
                    // The endpoint's OWN verdict goes into the note: "empty content" alone hid a 400 for a whole release.
                    Probe.Note(Id, $"{Short(hit.Id)}: KRC download empty (status {download.Status} {download.Info})".TrimEnd());
                    return null;
                }

                byte[] bytes;
                try { bytes = Convert.FromBase64String(download.Content); }
                catch (FormatException)
                {
                    Probe.Note(Id, $"{Short(hit.Id)}: KRC download content is not base64");
                    return null;
                }
                string? krc = Crypto.DecryptKrc(bytes);
                if (krc is null)
                {
                    Probe.Note(Id, $"{Short(hit.Id)}: KRC decrypt failed ({bytes.Length} bytes)");
                    return null;
                }
                // The DECRYPTED body — the text the parser is handed — and the only payload this source captures.
                Probe.CaptureRaw(Id, "decrypted KRC (id " + lyricId + ")", "krc", krc);

                var doc = CnGrey.DropBadLines(WordFormats.ParseKrc(krc, req.TrackId, Id));
                if (doc.Lines.Count == 0)
                {
                    Probe.Note(Id, $"{Short(hit.Id)}: KRC parsed to 0 lines");
                    return null;
                }
                return doc;
            }

            static string StripEm(string? s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                return s.Replace("<em>", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("</em>", "", StringComparison.OrdinalIgnoreCase)
                        .Trim();
            }

            static string? NullIfBlank(string s) => s.Length == 0 ? null : s;

            static string Short(string id) => id.Length > 12 ? id[..12] : id;
        }

        /// <summary>What the two Chinese grey sources (kugou, qq) share: the guard preflight, the per-fetch transport
        /// bookkeeping, the verdict lines and the document clean-up.</summary>
        internal static class CnGrey
        {
            const long HourMs = 3_600_000L;

            /// <summary>One fetch's transport state. <see cref="Failure"/> set = the host failed this fetch: every later
            /// request of the same fetch is skipped and the guard gets ONE failure.</summary>
            internal sealed class FetchState
            {
                /// <summary>At least one request got a real, parseable answer.</summary>
                public bool Answered;

                /// <summary>A request answered with something that is neither an answer nor a transport failure (a 4xx,
                /// an API-level refusal) — a miss seen through it is not trusted enough to remember.</summary>
                public bool Incomplete;

                public string? Failure;

                public void Fail(string reason) => Failure ??= reason;
            }

            /// <summary>The guard's two short-circuits. False = answer null now (a note and a log line already written).</summary>
            public static bool Preflight(SourceGuard guard, string id, Request req)
            {
                if (guard.IsOpen(out string reason, out long untilMs))
                {
                    string until = DateTimeOffset.FromUnixTimeMilliseconds(untilMs).ToLocalTime()
                        .ToString("HH:mm", CultureInfo.InvariantCulture);
                    Probe.Note(id, $"skipped: breaker open until {until} ({reason})");
                    Decision(id, $"skipped=breaker until={until} reason={reason}");
                    return false;
                }
                if (!string.IsNullOrEmpty(req.TrackId) && guard.TryGetMiss(req.TrackId, out var kind, out long ageMs))
                {
                    Probe.Note(id, $"cached miss {kind} {Age(ageMs)} ago");
                    Decision(id, $"skipped=cached-miss kind={kind} age={Age(ageMs)}");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(req.Title))
                {
                    Probe.Note(id, "no title to search with");
                    return false;
                }
                return true;
            }

            /// <summary>No answer, a server error or throttling — the host, not the song.</summary>
            public static bool IsTransportFailure(in HttpResult r) => r.Status == 0 || r.Status >= 500 || r.Status == 429;

            /// <summary>One request under the fetch's transport bookkeeping: skipped once the fetch has failed; a
            /// transport failure marks the fetch; any other non-2xx is noted (and marks the fetch incomplete). The body
            /// of a 2xx answer ("" for an empty one), or null. Cancellation propagates.</summary>
            public static async Task<string?> SendAsync(string id, FetchState st, string what, Func<Task<HttpResult>> send)
            {
                if (st.Failure is not null) return null;
                HttpResult r = await send().ConfigureAwait(false);
                if (IsTransportFailure(r))
                {
                    st.Fail(r.Status == 0 ? what + ": no answer" : what + ": HTTP " + r.Status.ToString(CultureInfo.InvariantCulture));
                    return null;
                }
                if (!r.IsSuccess)
                {
                    Probe.Note(id, what + ": HTTP " + r.Status.ToString(CultureInfo.InvariantCulture));
                    st.Incomplete = true;
                    return null;
                }
                return r.Body ?? "";
            }

            /// <summary>Tell the guard how the fetch went: one failure, or a success (an answered fetch resets the
            /// consecutive-failure count).</summary>
            public static void Settle(SourceGuard guard, FetchState st)
            {
                if (st.Failure is not null) guard.Failure(st.Failure);
                else if (st.Answered) guard.Success();
            }

            /// <summary>The end of a fetch that produced no candidate: remember the miss when it is a real one.</summary>
            public static void Missed(SourceGuard guard, string id, Request req, FetchState st, bool songFound)
            {
                if (st.Failure is not null)
                {
                    Decision(id, "failure=" + st.Failure);
                    return;
                }
                if (!st.Answered || st.Incomplete)
                {
                    Probe.Note(id, "no usable answer — miss not remembered");
                    Decision(id, "miss=unanswered");
                    return;
                }
                var kind = songFound ? SourceGuard.MissKind.NoLyricForSong : SourceGuard.MissKind.SongNotFound;
                guard.Miss(req.TrackId, kind);
                Probe.Note(id, songFound ? "song found, no lyric body — remembered" : "song not found — remembered");
                Decision(id, "miss=" + kind);
            }

            /// <summary>The candidate for a found document, with its stage-0 match and the decision line.</summary>
            public static Candidate Found(string id, double prior, Request req, in Hit hit, in MatchScore score, Doc doc)
            {
                string note = MetadataMatch.Describe(in hit, in score);
                Probe.Note(id, $"lyric from {note} ({doc.Lines.Count} lines, {doc.Sync})");
                Decision(id, "band=" + score.Band
                    + " conf=" + score.Confidence.ToString("0.00", CultureInfo.InvariantCulture)
                    + " dur=" + Delta(req, hit)
                    + " sync=" + doc.Sync
                    + " lines=" + doc.Lines.Count.ToString(CultureInfo.InvariantCulture));
                return new Candidate(id, prior, MatchBasis.MetadataSearch, doc)
                {
                    Confidence = score.Confidence,
                    Band = score.Band,
                    MatchNote = note,
                };
            }

            /// <summary>The always-on per-decision line (<c>[lyrics] source=kugou band=VeryHigh conf=0.93 dur=+320ms</c>).</summary>
            public static void Decision(string id, string text) => Log.Info(Diag.Category, "source=" + id + " " + text);

            /// <summary>Drop what a player cannot place: lines with a negative start, and (in a word-synced document)
            /// lines whose words are all blank.</summary>
            public static Doc DropBadLines(Doc doc)
            {
                List<Line>? kept = null;
                for (int i = 0; i < doc.Lines.Count; i++)
                {
                    var l = doc.Lines[i];
                    bool bad = l.StartMs < 0 || (doc.Sync == SyncKind.Syllable && string.IsNullOrWhiteSpace(l.Text));
                    if (bad)
                    {
                        if (kept is null)
                        {
                            kept = new List<Line>(doc.Lines.Count);
                            for (int j = 0; j < i; j++) kept.Add(doc.Lines[j]);
                        }
                        continue;
                    }
                    kept?.Add(l);
                }
                if (kept is null) return doc;
                return doc with { Lines = kept, IsSynced = kept.Count > 0 && doc.IsSynced };
            }

            public static T? TryParse<T>(string json, JsonTypeInfo<T> info) where T : class
            {
                if (string.IsNullOrWhiteSpace(json)) return null;
                try { return JsonSerializer.Deserialize(json, info); }
                catch (JsonException) { return null; }
                catch (NotSupportedException) { return null; }
            }

            public static T? TryParse<T>(JsonElement el, JsonTypeInfo<T> info) where T : class
            {
                try { return el.Deserialize(info); }
                catch (JsonException) { return null; }
                catch (NotSupportedException) { return null; }
                catch (InvalidOperationException) { return null; }
            }

            static string Delta(Request req, in Hit hit)
            {
                if (req.DurationMs <= 0 || hit.DurationMs <= 0) return "?";
                long d = hit.DurationMs - req.DurationMs;
                return (d >= 0 ? "+" : "") + d.ToString(CultureInfo.InvariantCulture) + "ms";
            }

            static string Age(long ms)
            {
                if (ms >= HourMs) return (ms / HourMs).ToString(CultureInfo.InvariantCulture) + "h";
                if (ms >= 60_000L) return (ms / 60_000L).ToString(CultureInfo.InvariantCulture) + "min";
                return (ms / 1000L).ToString(CultureInfo.InvariantCulture) + "s";
            }
        }
    }
}

// ── Kugou's wire shapes ─────────────────────────────────────────────────────────────────────────────────────────────
//
// Namespace level (the DTOs travel with their source-generated context), minimal: only what the flow reads. `data` stays
// a JsonElement because the PHP-style API answers `"data":[]` instead of an object when it has nothing.

internal sealed record KugouSongSearch(int Status, int ErrCode, JsonElement Data);

internal sealed record KugouSongData(KugouSong?[]? Info);

internal sealed record KugouSong(
    string? Hash,
    string? SongName,
    string? SingerName,
    [property: JsonPropertyName("album_name")] string? AlbumName,
    string? FileName,
    double Duration /* seconds */);

internal sealed record KugouLyricSearch(int Status, string? Info, KugouLyricCandidate?[]? Candidates);

internal sealed record KugouLyricCandidate(
    [property: JsonConverter(typeof(CnIdConverter))] string? Id,
    string? AccessKey);

internal sealed record KugouDownload(int Status, string? Info, string? Content);

/// <summary>AOT-safe SOURCE-GENERATED JSON for Kugou — no reflection-based serialization.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(KugouSongSearch))]
[JsonSerializable(typeof(KugouSongData))]
[JsonSerializable(typeof(KugouLyricSearch))]
[JsonSerializable(typeof(KugouDownload))]
internal sealed partial class KugouJson : JsonSerializerContext;

/// <summary>An id the Chinese services send as a string on one endpoint and a number on the next: read either as a
/// string (a number keeps its exact wire digits); anything else is null.</summary>
internal sealed class CnIdConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
