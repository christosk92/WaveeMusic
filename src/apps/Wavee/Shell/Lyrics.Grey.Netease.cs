// ── Shell/Lyrics.Grey.Netease.cs ───────────────────────────────────────────────────────────────────────────────────
// the NetEase Cloud Music grey source: keyword search → song id → lyric (YRC word-synced, else LRC line-synced)
//
// Role: SHELL
// Spec: docs/plans/wavee/lyrics-grey-sources-implementation.md (W2-E2, frozen contracts; corrections 12, 14, 18)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Ported from 0.2.9's `NeteaseSource` (Backend/Lyrics/Sources/GreySources.cs): the public web API (no eapi — W4), with
// the `Referer` + `Cookie: appver=8.9.70; os=pc` headers it needs.
//
//   search   GET https://music.163.com/api/search/get?type=1&offset=0&limit=10&s=<keyword>   (/get/web now encrypts)
//            walked through the shared `GreyLadder` (Query.Variants, MetadataMatch, early stop, ≤ 2 hits ≥ Medium);
//            durations are milliseconds (grain 1)
//   lyric    GET https://music.163.com/api/song/lyric?id=<id>&lv=1&kv=1&tv=1&yv=1
//            `yrc.lyric` (ParseYrc) preferred, else `lrc.lyric` (ParseLrc). `tlyric` / `romalrc` are only NOTED — the
//            structured translations are deferred (W3-H).
//
// Failure handling, per `SourceGuard`:
//   • JSON `code` -460 / -462 (risk control — answered with HTTP 200) → Trip 30 min, and not one more request.
//   • no answer / non-2xx / unparseable → one Failure per fetch (the ladder stops there, so one bad fetch never counts
//     three times), never cached.
//   • search answered, no hit ≥ Medium → Miss(SongNotFound); song found but `nolyric` / `uncollected` / no body →
//     Miss(NoLyricForSong).
//   • a cancellation (timeout or gold cancel) propagates and is never a Failure (correction 14).
// Only the final lyric payload is captured (correction 18).

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Wavee;

public static partial class Lyrics
{
    public static partial class Sources
    {
        /// <summary>NetEase Cloud Music: search → song id → YRC (word-synced) or LRC (line-synced). Matched by
        /// METADATA, so every candidate carries its stage-0 band and confidence.</summary>
        public sealed class Netease : ISource
        {
            // NOT `/api/search/get/web` (0.2.9's): as of 2026-09 it answers with `result` ENCRYPTED (a hex blob), so every
            // search would parse-fail and trip the breaker. `/api/search/get` returns the same plain shape (verified live
            // 2026-09-29: name / artists[] / album / duration ms). `/api/search/pc` is risk-controlled (-462).
            public const string SearchUrlPrefix = "https://music.163.com/api/search/get?type=1&offset=0&limit=10&s=";
            public const string LyricUrlPrefix = "https://music.163.com/api/song/lyric?id=";
            public const string LyricUrlSuffix = "&lv=1&kv=1&tv=1&yv=1";

            /// <summary>How long a risk-control answer (-460 / -462) parks the source.</summary>
            public const long RiskControlTripMs = 30 * 60_000L;

            /// <summary>NetEase answers durations in milliseconds.</summary>
            const int DurationGrainMs = 1;

            static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Referer"] = "https://music.163.com",
                ["Cookie"] = "appver=8.9.70; os=pc",
            };

            readonly IHttpPost _http;
            readonly SourceGuard? _guard;

            public Netease(IHttpPost http) : this(http, null) { }

            /// <summary>With an explicit guard (tests); null uses the process-wide <see cref="SourceGuard.For"/>.</summary>
            public Netease(IHttpPost http, SourceGuard? guard)
            {
                _http = http;
                _guard = guard;
            }

            public string Id => "netease";
            public bool Enabled => true;
            public double Prior => 0.50;

            // Resolved per call, so a test's `SourceGuard.ResetRegistry` is honoured.
            SourceGuard Guard => _guard ?? SourceGuard.For(Id);

            public void Forget(string trackId)
            {
                if (!string.IsNullOrEmpty(trackId)) Guard.Forget(trackId);
            }

            /// <summary>Per-fetch state the ladder's search callback shares with the lyric step. The ladder calls the
            /// callback sequentially, so no locking.</summary>
            sealed class Run
            {
                public bool Stopped;
                public string StopReason = "";
                public bool Answered;

                public void Stop(string why)
                {
                    if (Stopped) return;
                    Stopped = true;
                    StopReason = why;
                }
            }

            enum LyricOutcome : byte { Found, NoLyric, Stop }

            public async Task<Candidate?> FetchAsync(Request req, CancellationToken ct)
            {
                var guard = Guard;
                if (string.IsNullOrWhiteSpace(req.Title)) { Probe.Note(Id, "no title to search with"); return null; }
                if (GreySkip.Check(Id, guard, req.TrackId)) return null;

                var run = new Run();
                var hits = await GreyLadder.SearchAsync(Id, req, (kw, t) => SearchAsync(kw, guard, run, t), ct).ConfigureAwait(false);

                if (run.Stopped)
                {
                    Decide(req, "stopped", null, run.StopReason);
                    return null;
                }
                if (hits.Count == 0)
                {
                    if (run.Answered && !string.IsNullOrEmpty(req.TrackId))
                    {
                        guard.Miss(req.TrackId, SourceGuard.MissKind.SongNotFound);
                        Probe.Note(Id, "song not found — remembered (SongNotFound)");
                    }
                    Decide(req, "not-found", null, "no hit ≥ Medium");
                    return null;
                }

                foreach (var (hit, score) in hits)
                {
                    ct.ThrowIfCancellationRequested();
                    var (outcome, cand) = await LyricAsync(req, hit, score, guard, run, ct).ConfigureAwait(false);
                    if (outcome == LyricOutcome.Found && cand is not null)
                    {
                        Decide(req, "hit", cand, $"id={hit.Id} dur={FormatDelta(hit, req)}");
                        return cand;
                    }
                    if (outcome == LyricOutcome.Stop)
                    {
                        Decide(req, "stopped", null, run.StopReason);
                        return null;
                    }
                }

                if (!string.IsNullOrEmpty(req.TrackId))
                {
                    guard.Miss(req.TrackId, SourceGuard.MissKind.NoLyricForSong);
                    Probe.Note(Id, "song found but it carries no lyric — remembered (NoLyricForSong)");
                }
                Decide(req, "no-lyric", null, $"{hits.Count} hit(s) without a lyric body");
                return null;
            }

            async Task<IReadOnlyList<Hit>?> SearchAsync(string keyword, SourceGuard guard, Run run, CancellationToken ct)
            {
                // After a trip or a transport failure no further request goes out for this fetch.
                if (run.Stopped || guard.IsOpen(out _, out _)) return null;

                string url = SearchUrlPrefix + Uri.EscapeDataString(keyword);
                var res = await _http.GetAsync(url, Headers, ct).ConfigureAwait(false);
                var dto = Answer(res, NeteaseJson.Default.NeteaseSearchDto, static d => d.Code, guard, run, "search");
                if (dto is null) return null;

                run.Answered = true;
                var songs = dto.Result?.Songs;
                if (songs is null || songs.Length == 0) return Array.Empty<Hit>();

                var hits = new List<Hit>(songs.Length);
                foreach (var s in songs)
                {
                    if (s is null || s.Id <= 0) continue;
                    var artists = new List<string>(s.Artists?.Length ?? 0);
                    if (s.Artists is not null)
                        foreach (var a in s.Artists)
                            if (!string.IsNullOrWhiteSpace(a?.Name)) artists.Add(a!.Name!);
                    hits.Add(new Hit(s.Id.ToString(CultureInfo.InvariantCulture), s.Name ?? "", artists, s.Album?.Name,
                        Math.Max(0L, s.Duration), DurationGrainMs));
                }
                return hits;
            }

            async Task<(LyricOutcome Outcome, Candidate? Candidate)> LyricAsync(Request req, Hit hit, MatchScore score,
                SourceGuard guard, Run run, CancellationToken ct)
            {
                if (guard.IsOpen(out string reason, out _)) { run.Stop("breaker open: " + reason); return (LyricOutcome.Stop, null); }

                string url = LyricUrlPrefix + Uri.EscapeDataString(hit.Id) + LyricUrlSuffix;
                var res = await _http.GetAsync(url, Headers, ct).ConfigureAwait(false);
                var dto = Answer(res, NeteaseJson.Default.NeteaseLyricDto, static d => d.Code, guard, run, "lyric");
                if (dto is null) return (LyricOutcome.Stop, null);

                bool hasTranslation = !string.IsNullOrWhiteSpace(dto.Tlyric?.Lyric);
                bool hasRoman = !string.IsNullOrWhiteSpace(dto.Romalrc?.Lyric);
                if (hasTranslation || hasRoman)
                    Probe.Note(Id, $"id={hit.Id} also carries" + (hasTranslation ? " tlyric" : "") + (hasRoman ? " romalrc" : "")
                        + " (translations deferred)");

                if (dto.Nolyric || dto.Uncollected)
                {
                    Probe.Note(Id, $"id={hit.Id} → " + (dto.Nolyric ? "nolyric" : "uncollected"));
                    return (LyricOutcome.NoLyric, null);
                }

                Doc? doc = null;
                string? yrc = dto.Yrc?.Lyric;
                if (!string.IsNullOrWhiteSpace(yrc))
                {
                    var parsed = WordFormats.ParseYrc(yrc!, req.TrackId, Id);
                    if (parsed.Lines.Count > 0) { doc = parsed; Probe.Note(Id, $"id={hit.Id} yrc → {parsed.Lines.Count} word-synced lines"); }
                    else Probe.Note(Id, $"id={hit.Id} yrc parsed to no lines — lrc fallback");
                }
                if (doc is null)
                {
                    string? lrc = dto.Lrc?.Lyric;
                    if (!string.IsNullOrWhiteSpace(lrc))
                    {
                        var parsed = Text.ParseLrc(lrc!, req.TrackId, Id);
                        if (parsed.Lines.Count > 0) { doc = parsed; Probe.Note(Id, $"id={hit.Id} lrc → {parsed.Lines.Count} lines"); }
                    }
                }
                if (doc is null)
                {
                    Probe.Note(Id, $"id={hit.Id} carries no yrc/lrc lyric");
                    return (LyricOutcome.NoLyric, null);
                }

                Probe.CaptureRaw(Id, Probe.Redact(url), "json", res.Body);
                var cand = new Candidate(Id, Prior, MatchBasis.MetadataSearch, doc)
                {
                    Confidence = score.Confidence,
                    Band = score.Band,
                    MatchNote = MetadataMatch.Describe(in hit, in score),
                };
                return (LyricOutcome.Found, cand);
            }

            /// <summary>Parse one answer and apply the guard: risk control trips the breaker; no answer, a non-2xx or
            /// an unparseable body is ONE failure and stops this fetch; an answer resets the failure count.</summary>
            T? Answer<T>(HttpResult res, JsonTypeInfo<T> info, Func<T, int> codeOf, SourceGuard guard, Run run, string what)
                where T : class
            {
                if (res.Status == 0 || string.IsNullOrEmpty(res.Body))
                {
                    string why = $"{what}: no answer" + (res.Status != 0 ? $" (HTTP {res.Status})" : "");
                    run.Stop(why);
                    guard.Failure(why);
                    return null;
                }

                T? dto = null;
                try { dto = JsonSerializer.Deserialize(res.Body, info); }
                catch (JsonException) { dto = null; }
                catch (NotSupportedException) { dto = null; }

                if (dto is not null)
                {
                    int code = codeOf(dto);
                    if (code is -460 or -462)
                    {
                        string why = $"risk control code={code.ToString(CultureInfo.InvariantCulture)}";
                        run.Stop(why);
                        guard.Trip(why, RiskControlTripMs);
                        return null;
                    }
                    if (res.IsSuccess && code is 200 or 0)
                    {
                        guard.Success();
                        return dto;
                    }
                    string bad = $"{what}: HTTP {res.Status.ToString(CultureInfo.InvariantCulture)} code={code.ToString(CultureInfo.InvariantCulture)}";
                    run.Stop(bad);
                    guard.Failure(bad);
                    return null;
                }

                string unparseable = $"{what}: unparseable body (HTTP {res.Status.ToString(CultureInfo.InvariantCulture)})";
                run.Stop(unparseable);
                guard.Failure(unparseable);
                return null;
            }

            void Decide(Request req, string outcome, Candidate? c, string detail)
            {
                string line = c is not null
                    ? $"source={Id} track={req.TrackId} outcome={outcome} band={c.Band} conf={c.Confidence:F2} sync={c.Sync} lines={c.LineCount} {detail}"
                    : $"source={Id} track={req.TrackId} outcome={outcome} {detail}";
                Log.Info(Diag.Category, line.Replace('\r', ' ').Replace('\n', ' '));
            }

            static string FormatDelta(in Hit hit, Request req)
            {
                if (hit.DurationMs <= 0 || req.DurationMs <= 0) return "?";
                long d = hit.DurationMs - req.DurationMs;
                return (d >= 0 ? "+" : "") + d.ToString(CultureInfo.InvariantCulture) + "ms";
            }
        }

        /// <summary>The guard pre-check every grey source runs before a request goes out: an open breaker or a live
        /// remembered miss answers null, after a note saying which.</summary>
        internal static class GreySkip
        {
            public static bool Check(string sourceId, SourceGuard guard, string trackId)
            {
                if (guard.IsOpen(out string reason, out long untilMs))
                {
                    Probe.Note(sourceId, $"skipped: breaker open until {ClockOf(untilMs)} ({reason})");
                    return true;
                }
                if (!string.IsNullOrEmpty(trackId) && guard.TryGetMiss(trackId, out var kind, out long ageMs))
                {
                    Probe.Note(sourceId, $"cached miss {kind} {SourceGuard.FormatSpan(ageMs)} ago");
                    return true;
                }
                return false;
            }

            /// <summary>"14:32" (local) for a unix-ms instant; the raw number when it is not one (a test clock).</summary>
            static string ClockOf(long unixMs)
            {
                if (unixMs <= 0 || unixMs > 253_402_300_799_999L) return unixMs.ToString(CultureInfo.InvariantCulture);
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            }
        }
    }
}

// ── NetEase wire shapes ──────────────────────────────────────────────────────────────────────────────────────────────
//
// Namespace level, beside their source-generated context. Only the fields the source reads; everything else is ignored.

internal sealed record NeteaseSearchDto(int Code, NeteaseSearchResultDto? Result);

internal sealed record NeteaseSearchResultDto(NeteaseSongDto?[]? Songs);

internal sealed record NeteaseSongDto(long Id, string? Name, NeteaseNamedDto?[]? Artists, NeteaseNamedDto? Album, long Duration);

internal sealed record NeteaseNamedDto(string? Name);

internal sealed record NeteaseLyricDto(
    int Code,
    NeteaseLyricBodyDto? Lrc,
    NeteaseLyricBodyDto? Yrc,
    NeteaseLyricBodyDto? Tlyric,
    NeteaseLyricBodyDto? Romalrc,
    bool Nolyric,
    bool Uncollected);

internal sealed record NeteaseLyricBodyDto(string? Lyric);

/// <summary>AOT-safe SOURCE-GENERATED JSON for the NetEase source — no reflection-based serialization.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(NeteaseSearchDto))]
[JsonSerializable(typeof(NeteaseLyricDto))]
internal sealed partial class NeteaseJson : JsonSerializerContext;
