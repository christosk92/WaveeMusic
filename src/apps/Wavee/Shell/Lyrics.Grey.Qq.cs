// ── Shell/Lyrics.Grey.Qq.cs ────────────────────────────────────────────────────────────────────────────────────────
// the QQ Music grey source (QRC, word-synced; plain LRC fallback)
//
// Role: SHELL
// Spec: docs/plans/wavee/lyrics-grey-sources-implementation.md (W2-E1, frozen contracts)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE FLOW (0.2.9's QqMusicSource, over the shared guard and ladder; both requests carry Referer https://y.qq.com):
//
//   guard      breaker open / remembered miss → a note, null, no request
//   search     POST https://u.y.qq.com/cgi-bin/musicu.fcg, a JSON body written with Utf8JsonWriter:
//                {"comm":{"ct":"19","cv":"1859","uin":"0"},
//                 "req_1":{"method":"DoSearchForQQMusicDesktop","module":"music.search.SearchCgiService",
//                          "param":{"num_per_page":10,"page_num":1,"query":<keyword>,"search_type":0}}}
//              Without the `comm` block the service answers code 2001. A non-zero `code` (top level or req_1) is the
//              service refusing the request → a transport-class failure, never a remembered miss.
//              → `Hit`s (`interval` whole seconds) → `GreyLadder.SearchAsync`
//   lyric      POST https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg, form version=15&miniversion=82&lrctype=4
//              &musicid=<numeric song id> → XML (comment-wrapped) → `QrcXml.Split` → orig/ts/roma. ONLY `orig` is
//              decrypted: the romanization block is usually the longest, which is why 0.2.9's "longest hex run" rule
//              could pick it. `Crypto.DecryptQrc(orig)` → `Probe.CaptureRaw` (the decrypted QRC, the only capture) →
//              `WordFormats.ParseQrc`. When `orig` does not decrypt but carries plain LRC, that LRC is the (line-synced)
//              document. Lines with a negative start are dropped (QQ ships some).

using System.Buffers;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee;

public static partial class Lyrics
{
    public static partial class Sources
    {
        /// <summary>QQ Music (QRC, word-synced): musicu.fcg search → numeric song id → lyric_download.fcg (XML with the
        /// hex QRC) → decrypt → parse. Matched by metadata, so the candidate carries its <see cref="MatchBand"/> and the
        /// reranker weighs it by <see cref="Candidate.Confidence"/>.</summary>
        public sealed class Qq : ISource
        {
            const string SearchUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            const string LyricUrl = "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg";

            static readonly IReadOnlyDictionary<string, string> Headers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Referer"] = "https://y.qq.com" };

            static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

            readonly IHttpPost _http;
            readonly SourceGuard _guard;

            public Qq(IHttpPost http)
            {
                _http = http ?? throw new ArgumentNullException(nameof(http));
                _guard = SourceGuard.For("qq");
            }

            public string Id => "qq";
            public bool Enabled => true;
            public double Prior => 0.55;

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
                    Probe.Note(Id, "failed: " + e.GetType().Name);
                    CnGrey.Decision(Id, "error=" + e.GetType().Name);
                    return null;
                }
            }

            // ── song search → hits ──────────────────────────────────────────────────────────────────────────────────

            /// <summary>The musicu.fcg search body, <c>comm</c> block first (header comment).</summary>
            public static byte[] SearchBody(string keyword)
            {
                var buffer = new ArrayBufferWriter<byte>(256);
                using (var w = new Utf8JsonWriter(buffer, WriterOptions))
                {
                    w.WriteStartObject();
                    w.WriteStartObject("comm");
                    w.WriteString("ct", "19");
                    w.WriteString("cv", "1859");
                    w.WriteString("uin", "0");
                    w.WriteEndObject();
                    w.WriteStartObject("req_1");
                    w.WriteString("method", "DoSearchForQQMusicDesktop");
                    w.WriteString("module", "music.search.SearchCgiService");
                    w.WriteStartObject("param");
                    w.WriteNumber("num_per_page", 10);
                    w.WriteNumber("page_num", 1);
                    w.WriteString("query", keyword ?? "");
                    w.WriteNumber("search_type", 0);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                return buffer.WrittenSpan.ToArray();
            }

            async Task<IReadOnlyList<Hit>?> SearchAsync(string keyword, CnGrey.FetchState st, CancellationToken ct)
            {
                string? body = await CnGrey.SendAsync(Id, st, "song search", () =>
                {
                    var content = new ByteArrayContent(SearchBody(keyword));
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                    return _http.PostAsync(SearchUrl, content, Headers, ct);
                }).ConfigureAwait(false);
                if (body is null) return null;

                var parsed = CnGrey.TryParse(body, QqJson.Default.QqSearchResponse);
                if (parsed is null) { st.Fail("song search: unparseable answer"); return null; }

                int code = parsed.Code != 0 ? parsed.Code : parsed.Req1?.Code ?? 0;
                if (code != 0)
                {
                    // 2001 = the request was refused (the `comm` block): the service, not the song — a failure, never a
                    // remembered miss.
                    Probe.Note(Id, $"song search '{keyword}': code {code}" + (code == 2001 ? " (request refused)" : ""));
                    st.Fail("song search: code " + code.ToString(CultureInfo.InvariantCulture));
                    return null;
                }
                st.Answered = true;

                var list = parsed.Req1?.Data?.Body?.Song?.List;
                if (list is null || list.Length == 0) return Array.Empty<Hit>();

                var hits = new List<Hit>(list.Length);
                foreach (var s in list)
                {
                    if (s is null) continue;
                    string? id = s.Id is { Length: > 0 } && s.Id != "0" ? s.Id : s.SongId;
                    if (string.IsNullOrWhiteSpace(id) || id == "0") continue;
                    string title = FirstNonBlank(s.Name, s.Title, s.SongName) ?? "";
                    var artists = new List<string>(2);
                    if (s.Singer is { } singers)
                        foreach (var a in singers)
                            if (a is not null && FirstNonBlank(a.Name, a.Title) is { } name) artists.Add(name);
                    string? album = AlbumName(s.Album) ?? FirstNonBlank(s.AlbumName);
                    long durMs = s.Interval > 0 ? (long)Math.Round(s.Interval * 1000d) : 0;
                    hits.Add(new Hit(id, title, artists, album, durMs, 1000));
                }
                return hits;
            }

            // ── one hit → its QRC (or plain LRC) document ───────────────────────────────────────────────────────────

            async Task<Doc?> LyricAsync(Request req, Hit hit, CnGrey.FetchState st, CancellationToken ct)
            {
                string form = "version=15&miniversion=82&lrctype=4&musicid=" + Uri.EscapeDataString(hit.Id);
                string? xml = await CnGrey.SendAsync(Id, st, "lyric download", () =>
                    _http.PostAsync(LyricUrl, new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"), Headers, ct))
                    .ConfigureAwait(false);
                if (xml is null) return null;
                st.Answered = true;

                var (orig, ts, roma) = QrcXml.Split(xml);
                Probe.Note(Id, $"{hit.Id}: xml orig {Size(orig)}, ts {Size(ts)}, roma {Size(roma)} → orig");
                if (orig is null)
                {
                    Probe.Note(Id, $"{hit.Id}: song found, no lyric in the download");
                    return null;
                }

                Doc? doc = null;
                string? qrc = Crypto.DecryptQrc(orig);
                if (qrc is not null)
                {
                    // The DECRYPTED body — the text the parser is handed — and the only payload this source captures.
                    Probe.CaptureRaw(Id, "decrypted QRC (musicid " + hit.Id + ")", "qrc", qrc);
                    doc = CnGrey.DropBadLines(WordFormats.ParseQrc(qrc, req.TrackId, Id));
                    if (doc.Lines.Count == 0)
                    {
                        // A decrypted body without word lines can still be LRC.
                        var lrc = CnGrey.DropBadLines(Text.ParseLrc(qrc, req.TrackId, Id));
                        if (lrc.Lines.Count > 0) doc = lrc;
                        else Probe.Note(Id, $"{hit.Id}: QRC parsed to 0 lines");
                    }
                }
                else if (LooksLikeLrc(orig))
                {
                    Probe.Note(Id, $"{hit.Id}: orig does not decrypt — plain LRC");
                    Probe.CaptureRaw(Id, "plain LRC (musicid " + hit.Id + ")", "lrc", orig);
                    doc = CnGrey.DropBadLines(Text.ParseLrc(orig, req.TrackId, Id));
                }
                else
                {
                    Probe.Note(Id, $"{hit.Id}: QRC decrypt failed ({orig.Length} chars)");
                }

                return doc is { Lines.Count: > 0 } ? doc : null;
            }

            /// <summary>At least one <c>[mm:ss</c> line stamp.</summary>
            static bool LooksLikeLrc(string s)
            {
                for (int i = 0; i + 3 < s.Length; i++)
                    if (s[i] == '[' && char.IsAsciiDigit(s[i + 1]) && char.IsAsciiDigit(s[i + 2]) && s[i + 3] == ':') return true;
                return false;
            }

            static string? AlbumName(JsonElement album)
            {
                if (album.ValueKind == JsonValueKind.String) return FirstNonBlank(album.GetString());
                if (album.ValueKind != JsonValueKind.Object) return null;
                return FirstNonBlank(Str(album, "name"), Str(album, "title"));

                static string? Str(JsonElement el, string key)
                    => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }

            static string? FirstNonBlank(params ReadOnlySpan<string?> values)
            {
                foreach (var v in values)
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                return null;
            }

            static string Size(string? s)
                => s is null ? "-" : (s.Length / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        }
    }
}

// ── QQ Music's wire shapes ──────────────────────────────────────────────────────────────────────────────────────────
//
// Minimal, namespace level. The song item accepts both the musicu desktop shape (`id`/`name`/`album{name}`) and the older
// client_search shape (`songid`/`songname`/`albumname`) 0.2.9 read; `album` stays a JsonElement because it is an object
// in one and absent (or a string) in the other.

internal sealed record QqSearchResponse(
    int Code,
    [property: JsonPropertyName("req_1")] QqSearchReq? Req1);

internal sealed record QqSearchReq(int Code, QqSearchData? Data);

internal sealed record QqSearchData(QqSearchBody? Body);

internal sealed record QqSearchBody(QqSongList? Song);

internal sealed record QqSongList(QqSong?[]? List);

internal sealed record QqSong(
    [property: JsonConverter(typeof(CnIdConverter))] string? Id,
    [property: JsonConverter(typeof(CnIdConverter))] string? SongId,
    string? Name,
    string? Title,
    string? SongName,
    QqSinger?[]? Singer,
    JsonElement Album,
    string? AlbumName,
    double Interval /* seconds */);

internal sealed record QqSinger(string? Name, string? Title);

/// <summary>AOT-safe SOURCE-GENERATED JSON for QQ Music — no reflection-based serialization.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(QqSearchResponse))]
internal sealed partial class QqJson : JsonSerializerContext;
