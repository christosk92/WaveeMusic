// ── Shell/Lyrics.Grey.Musixmatch.cs ────────────────────────────────────────────────────────────────────────────────
// the Musixmatch grey source: usertoken → macro.subtitles.get (richsync word-synced, else the LRC subtitle)
//
// Role: SHELL
// Spec: docs/plans/wavee/lyrics-grey-sources-implementation.md (W2-E2, frozen contracts; corrections 11, 13, 14, 17, 18)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Ported from 0.2.9's `MusixmatchSource` (Backend/Lyrics/Sources/GreySources.cs), desktop profile (`MxmProfile`):
//
//   token    GET {base}token.get?app_id=…&t=…            12 h TTL, one fetch at a time (SemaphoreSlim); an
//                                                        `UpgradeOnly…` / blank token is refused
//   lyric    GET {base}macro.subtitles.get?namespace=lyrics_richsynched&optional_calls=track.richsync
//                &subtitle_format=lrc&<key>&q_duration=…&usertoken=…&format=json&app_id=…&t=…
//            <key> = `track_isrc` + `track_spotify_id` (the identity request, lyrics-plus's technique) or
//                    `q_track` + `q_artist` (the search request, first two TitleArtistVariants with an artist)
//
// The envelope's `message.header.status_code` is read BEFORE any body — Musixmatch answers a captcha or a quota with
// HTTP 200 and a decoy body underneath (and the grey client keeps non-2xx bodies, correction 13, so a transport-level
// 401 still carries its `hint`):
//   401 + hint "captcha"  → Trip 2 h          401 otherwise ("renew") → drop the token, retry ONCE per fetch
//   402                   → Trip 6 h (quota)  404 (no match)          → next request / SongNotFound
//
// The macro's `matcher.track.get` names the track Musixmatch matched. An identity request is trusted
// (MatchBasis.Isrc, Confidence 1, Perfect) ONLY when that track also passes MetadataMatch ≥ High; otherwise it is a
// metadata match with the matcher's band. A search request's band comes from the matcher's `confidence`
// (1000 Perfect, 950 VeryHigh, 900 High, 750 PrettyHigh, 600 Medium). Gold is decided elsewhere (correction 17).
//
// Bodies: `instrumental=1` → no lyric (the pipeline has no instrumental document: an instrumental notice collapses to
// an empty doc in `Clean.Apply`, i.e. a miss) — noted and remembered as NoLyricForSong. `has_richsync` → ParseRichsync;
// a missing, empty or unsingable richsync FALLS BACK to the LRC `track.subtitles.get` body in the same response
// (correction 11). Only the final payload is captured, its usertoken redacted (correction 18).

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wavee;

public static partial class Lyrics
{
    public static partial class Sources
    {
        /// <summary>Musixmatch: richsync (word-synced) or its LRC subtitle, by identity (ISRC / Spotify id) first and
        /// by title/artist search second.</summary>
        public sealed class Musixmatch : ISource
        {
            /// <summary>Everything that identifies the client profile, in one place so it can be swapped.</summary>
            public sealed record MxmProfile(
                string Name,
                string BaseUrl,
                string AppId,
                IReadOnlyDictionary<string, string> Headers,
                long TokenTtlMs,
                string RejectedTokenPrefix)
            {
                /// <summary>0.2.9's desktop profile.</summary>
                public static MxmProfile Desktop { get; } = new(
                    "desktop",
                    "https://apic-desktop.musixmatch.com/ws/1.1/",
                    "web-desktop-app-v1.0",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["Cookie"] = "x-mxm-token-guid=" },
                    12 * 3_600_000L,
                    "UpgradeOnly");
            }

            public const long CaptchaTripMs = 2 * 3_600_000L;
            public const long QuotaTripMs = 6 * 3_600_000L;

            /// <summary>Search requests per fetch (full, then feat-stripped title/artist).</summary>
            public const int MaxSearchRequests = 2;

            static MxmProfile Profile => MxmProfile.Desktop;

            readonly IHttpPost _http;
            readonly SourceGuard? _guard;
            readonly Func<long> _nowMs;
            readonly SemaphoreSlim _tokenGate = new(1, 1);
            TokenState? _token;

            sealed record TokenState(string Value, long AtMs);

            public Musixmatch(IHttpPost http) : this(http, null, null) { }

            /// <summary>With an explicit guard and token clock (tests); nulls use <see cref="SourceGuard.For"/> and the
            /// wall clock.</summary>
            public Musixmatch(IHttpPost http, SourceGuard? guard, Func<long>? nowMs)
            {
                _http = http;
                _guard = guard;
                _nowMs = nowMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            public string Id => "musixmatch";
            public bool Enabled => true;
            public double Prior => 0.70;

            SourceGuard Guard => _guard ?? SourceGuard.For(Id);

            public void Forget(string trackId)
            {
                if (!string.IsNullOrEmpty(trackId)) Guard.Forget(trackId);
            }

            // ── pure helpers (public for the tests) ─────────────────────────────────────────────────────────────────

            /// <summary>The <c>macro.subtitles.get</c> URL. With an <paramref name="isrc"/> and/or a
            /// <paramref name="spotifyUri"/> it is the IDENTITY request (<c>track_isrc</c> / <c>track_spotify_id</c>,
            /// no query terms); otherwise the SEARCH request (<c>q_track</c> / <c>q_artist</c>). Pure.</summary>
            public static string BuildSubtitlesUrl(string token, string? isrc, string? spotifyUri, string? qTrack, string? qArtist,
                long durSec, string rnd)
            {
                var p = Profile;
                var sb = new StringBuilder(320);
                sb.Append(p.BaseUrl).Append("macro.subtitles.get")
                  .Append("?namespace=lyrics_richsynched&optional_calls=track.richsync&subtitle_format=lrc");
                bool identity = !string.IsNullOrWhiteSpace(isrc) || !string.IsNullOrWhiteSpace(spotifyUri);
                if (identity)
                {
                    if (!string.IsNullOrWhiteSpace(isrc)) sb.Append("&track_isrc=").Append(Uri.EscapeDataString(isrc!.Trim()));
                    if (!string.IsNullOrWhiteSpace(spotifyUri)) sb.Append("&track_spotify_id=").Append(Uri.EscapeDataString(spotifyUri!.Trim()));
                }
                else
                {
                    sb.Append("&q_track=").Append(Uri.EscapeDataString(qTrack ?? ""))
                      .Append("&q_artist=").Append(Uri.EscapeDataString(qArtist ?? ""));
                }
                if (durSec > 0) sb.Append("&q_duration=").Append(durSec.ToString(CultureInfo.InvariantCulture));
                sb.Append("&usertoken=").Append(Uri.EscapeDataString(token ?? ""))
                  .Append("&format=json&app_id=").Append(Uri.EscapeDataString(p.AppId))
                  .Append("&t=").Append(Uri.EscapeDataString(rnd ?? ""));
                return sb.ToString();
            }

            /// <summary>The <c>token.get</c> URL. Pure.</summary>
            public static string BuildTokenUrl(string rnd)
                => Profile.BaseUrl + "token.get?app_id=" + Uri.EscapeDataString(Profile.AppId) + "&t=" + Uri.EscapeDataString(rnd ?? "");

            /// <summary>The matcher's <c>confidence</c> (0–1000) as a band: 1000 Perfect, ≥ 950 VeryHigh, ≥ 900 High,
            /// ≥ 750 PrettyHigh, ≥ 600 Medium; below that Low (not fetched).</summary>
            public static MatchBand BandOfConfidence(int confidence) => confidence switch
            {
                >= 1000 => MatchBand.Perfect,
                >= 950 => MatchBand.VeryHigh,
                >= 900 => MatchBand.High,
                >= 750 => MatchBand.PrettyHigh,
                >= 600 => MatchBand.Medium,
                > 0 => MatchBand.Low,
                _ => MatchBand.None,
            };

            /// <summary>The Spotify URI Musixmatch's <c>track_spotify_id</c> takes (<c>spotify:track:…</c>), or null.</summary>
            public static string? SpotifyUriOf(Request req)
            {
                if (!string.IsNullOrEmpty(req.Uri) && req.Uri.StartsWith("spotify:track:", StringComparison.Ordinal)) return req.Uri;
                return string.IsNullOrEmpty(req.TrackId) ? null : "spotify:track:" + req.TrackId;
            }

            // ── the fetch ───────────────────────────────────────────────────────────────────────────────────────────

            /// <summary>Per-fetch state: the token in use and whether the one renew-retry is spent.</summary>
            sealed class CallState(string token)
            {
                public string Token = token;
                public bool Renewed;
            }

            enum Kind : byte { Found, NotFound, NoLyric, Instrumental, Stop }

            readonly record struct MacroResult(Kind Kind, Candidate? Candidate, string Detail);

            public async Task<Candidate?> FetchAsync(Request req, CancellationToken ct)
            {
                var guard = Guard;
                if (GreySkip.Check(Id, guard, req.TrackId)) return null;

                string? token = await TokenAsync(guard, ct).ConfigureAwait(false);
                if (token is null)
                {
                    Probe.Note(Id, "no usertoken");
                    Decide(req, "stopped", null, "no usertoken");
                    return null;
                }
                var call = new CallState(token);
                long durSec = req.DurationMs > 0 ? req.DurationMs / 1000 : 0;
                bool asked = false;

                // 1. identity: ISRC and/or Spotify id.
                string? isrc = string.IsNullOrWhiteSpace(req.Isrc) ? null : req.Isrc!.Trim();
                string? spotify = SpotifyUriOf(req);
                if (isrc is not null || spotify is not null)
                {
                    asked = true;
                    var r = await MacroAsync(req, call, guard, identity: true,
                        tok => BuildSubtitlesUrl(tok, isrc, spotify, null, null, durSec, Rnd()),
                        "identity" + (isrc is not null ? " isrc=" + isrc : "") + (spotify is not null ? " spotify" : ""), ct).ConfigureAwait(false);
                    if (r.Kind != Kind.NotFound) return Finish(req, guard, r);
                }

                // 2. search: q_track / q_artist over the first two variants that carry an artist (a title-only query
                //    is too prone to wrong-language covers).
                int sent = 0;
                foreach (var (title, artist) in Query.TitleArtistVariants(req))
                {
                    if (artist.Length == 0) continue;
                    if (sent++ >= MaxSearchRequests) break;
                    ct.ThrowIfCancellationRequested();
                    asked = true;
                    string t = title, a = artist;
                    var r = await MacroAsync(req, call, guard, identity: false,
                        tok => BuildSubtitlesUrl(tok, null, null, t, a, durSec, Rnd()),
                        $"search '{t}' / '{a}'", ct).ConfigureAwait(false);
                    if (r.Kind != Kind.NotFound) return Finish(req, guard, r);
                }

                if (asked && !string.IsNullOrEmpty(req.TrackId))
                {
                    guard.Miss(req.TrackId, SourceGuard.MissKind.SongNotFound);
                    Probe.Note(Id, "no match — remembered (SongNotFound)");
                }
                Decide(req, "not-found", null, asked ? "no match" : "nothing to ask with");
                return null;
            }

            Candidate? Finish(Request req, SourceGuard guard, in MacroResult r)
            {
                switch (r.Kind)
                {
                    case Kind.Found:
                        Decide(req, "hit", r.Candidate, r.Detail);
                        return r.Candidate;
                    case Kind.Instrumental:
                        Probe.Note(Id, "instrumental");
                        if (!string.IsNullOrEmpty(req.TrackId)) guard.Miss(req.TrackId, SourceGuard.MissKind.NoLyricForSong);
                        Decide(req, "instrumental", null, r.Detail);
                        return null;
                    case Kind.NoLyric:
                        if (!string.IsNullOrEmpty(req.TrackId))
                        {
                            guard.Miss(req.TrackId, SourceGuard.MissKind.NoLyricForSong);
                            Probe.Note(Id, "matched, no usable lyric — remembered (NoLyricForSong)");
                        }
                        Decide(req, "no-lyric", null, r.Detail);
                        return null;
                    default:
                        Decide(req, "stopped", null, r.Detail);
                        return null;
                }
            }

            /// <summary>One macro request, with the status gate and the single renew-retry.</summary>
            async Task<MacroResult> MacroAsync(Request req, CallState call, SourceGuard guard, bool identity,
                Func<string, string> build, string label, CancellationToken ct)
            {
                while (true)
                {
                    if (guard.IsOpen(out string open, out _)) return new MacroResult(Kind.Stop, null, "breaker open: " + open);

                    string url = build(call.Token);
                    var res = await _http.GetAsync(url, Profile.Headers, ct).ConfigureAwait(false);
                    if (res.Status == 0 || string.IsNullOrEmpty(res.Body))
                    {
                        string why = $"{label}: no answer" + (res.Status != 0 ? $" (HTTP {res.Status.ToString(CultureInfo.InvariantCulture)})" : "");
                        guard.Failure(why);
                        return new MacroResult(Kind.Stop, null, why);
                    }

                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(res.Body); }
                    catch (JsonException)
                    {
                        string why = $"{label}: unparseable body (HTTP {res.Status.ToString(CultureInfo.InvariantCulture)})";
                        guard.Failure(why);
                        return new MacroResult(Kind.Stop, null, why);
                    }

                    using (doc)
                    {
                        var root = doc.RootElement;
                        Header(root, out int code, out string hint);
                        if (code == 0) code = res.Status;
                        bool haveMacro = MacroCalls(root, out var macro);
                        if (code == 200 && haveMacro && MacroCall(macro, "matcher.track.get", out var matcher))
                        {
                            Header(matcher, out int mc, out string mh);
                            if (mc != 0 && mc != 200) { code = mc; hint = mh; }
                        }
                        Probe.Note(Id, $"{label} → status_code={code.ToString(CultureInfo.InvariantCulture)}"
                            + (hint.Length > 0 ? " hint=" + hint : ""));

                        switch (code)
                        {
                            case 200:
                                break;
                            case 401 when hint.Contains("captcha", StringComparison.OrdinalIgnoreCase):
                                DropToken(call.Token);
                                guard.Trip("captcha", CaptchaTripMs);
                                return new MacroResult(Kind.Stop, null, "401 captcha");
                            case 401:
                                if (call.Renewed)
                                {
                                    Probe.Note(Id, "401 again after renewing the token — giving up");
                                    guard.Failure("401 after token renew");
                                    return new MacroResult(Kind.Stop, null, "401 after renew");
                                }
                                call.Renewed = true;
                                DropToken(call.Token);
                                Probe.Note(Id, "token rejected (" + (hint.Length > 0 ? hint : "401") + ") — renewing once");
                                string? fresh = await TokenAsync(guard, ct).ConfigureAwait(false);
                                if (fresh is null) return new MacroResult(Kind.Stop, null, "token renew failed");
                                call.Token = fresh;
                                continue;
                            case 402:
                                guard.Trip("quota", QuotaTripMs);
                                return new MacroResult(Kind.Stop, null, "402 quota");
                            case 404:
                                return new MacroResult(Kind.NotFound, null, label + ": 404");
                            default:
                                {
                                    string why = $"{label}: status_code={code.ToString(CultureInfo.InvariantCulture)}";
                                    guard.Failure(why);
                                    return new MacroResult(Kind.Stop, null, why);
                                }
                        }

                        guard.Success();
                        if (!haveMacro)
                        {
                            Probe.Note(Id, $"{label}: answer without macro_calls");
                            return new MacroResult(Kind.NotFound, null, label + ": no macro_calls");
                        }
                        return Inspect(req, macro, identity, url, res.Body!, label);
                    }
                }
            }

            /// <summary>A 200 macro answer: who matched, how well, and which body to parse.</summary>
            MacroResult Inspect(Request req, JsonElement macro, bool identity, string url, string body, string label)
            {
                if (!MacroCall(macro, "matcher.track.get", out var matcher)
                    || !Body(matcher, out var mBody) || !Obj(mBody, "track", out var track))
                {
                    Probe.Note(Id, $"{label}: no matched track");
                    return new MacroResult(Kind.NotFound, null, label + ": no matched track");
                }

                string name = Str(track, "track_name") ?? "";
                string artist = Str(track, "artist_name") ?? "";
                string? album = Str(track, "album_name");
                long lengthSec = Num(track, "track_length");
                long hasRichsync = Num(track, "has_richsync");
                long instrumental = Num(track, "instrumental");
                long restricted = Num(track, "restricted");
                string trackId = Str(track, "commontrack_id") ?? Str(track, "track_id") ?? "mxm";
                long confidence = -1;
                string mode = "";
                if (Obj(matcher, "message", out var mMsg) && Obj(mMsg, "header", out var mHead))
                {
                    confidence = Num(mHead, "confidence");
                    mode = Str(mHead, "mode") ?? "";
                }

                Probe.Note(Id, $"{label}: matched '{name}' / '{artist}' {lengthSec.ToString(CultureInfo.InvariantCulture)}s"
                    + $" richsync={hasRichsync.ToString(CultureInfo.InvariantCulture)} instrumental={instrumental.ToString(CultureInfo.InvariantCulture)}"
                    + $" restricted={restricted.ToString(CultureInfo.InvariantCulture)}"
                    + (confidence >= 0 ? $" confidence={confidence.ToString(CultureInfo.InvariantCulture)}" : "")
                    + (mode.Length > 0 ? $" mode={mode}" : ""));

                // ── how far to trust the match ──
                var hit = new Hit(trackId, name, artist.Length > 0 ? new[] { artist } : Array.Empty<string>(), album,
                    lengthSec > 0 ? lengthSec * 1000 : 0, 1000);
                MatchScore meta = MetadataMatch.Score(req, in hit);
                string note = MetadataMatch.Describe(in hit, in meta);

                MatchBasis basis;
                MatchBand band;
                double conf;
                if (identity && meta.Band >= MatchBand.High)
                {
                    basis = MatchBasis.Isrc;
                    band = MatchBand.Perfect;
                    conf = 1.0;
                }
                else
                {
                    basis = MatchBasis.MetadataSearch;
                    if (meta.Band == MatchBand.None && meta.Reason.Contains("gate", StringComparison.Ordinal))
                    {
                        Probe.Note(Id, $"{label}: rejected '{name}': {meta.Reason}");
                        return new MacroResult(Kind.NotFound, null, label + ": rejected by the metadata gate");
                    }
                    if (identity)
                    {
                        Probe.Note(Id, $"{label}: identity match only {meta.Band} — treated as a metadata match");
                        band = meta.Band;
                        conf = meta.Confidence;
                    }
                    else if (confidence > 0)
                    {
                        band = BandOfConfidence((int)Math.Min(confidence, 1000L));
                        conf = Math.Clamp(confidence / 1000.0, 0d, 1d);
                    }
                    else
                    {
                        band = meta.Band;
                        conf = meta.Confidence;
                    }
                    if (band < MatchBand.Medium)
                    {
                        Probe.Note(Id, $"{label}: match is {band} — below Medium, not used");
                        return new MacroResult(Kind.NotFound, null, label + ": match below Medium");
                    }
                }

                if (instrumental == 1)
                    return new MacroResult(Kind.Instrumental, null, label + ": instrumental");

                // ── the body: richsync, else the LRC subtitle in the same answer ──
                Doc? docOut = null;
                if (hasRichsync != 0 && RichsyncBody(macro, out string rich))
                {
                    var parsed = WordFormats.ParseRichsync(rich, req.TrackId, Id);
                    if (parsed.Lines.Count == 0)
                        Probe.Note(Id, "richsync parsed to no lines — LRC subtitle fallback");
                    else if (Timing.HasUnusableWordTiming(parsed))
                        Probe.Note(Id, "richsync word timing is not singable — LRC subtitle fallback");
                    else
                    {
                        docOut = parsed;
                        Probe.Note(Id, $"richsync → {parsed.Lines.Count} word-synced lines");
                    }
                }
                else Probe.Note(Id, hasRichsync == 0 ? "has_richsync=0 — LRC subtitle" : "no richsync body — LRC subtitle");

                if (docOut is null && SubtitleBody(macro, out string lrc))
                {
                    var parsed = Text.ParseLrc(lrc, req.TrackId, Id);
                    if (parsed.Lines.Count > 0)
                    {
                        docOut = parsed;
                        Probe.Note(Id, $"subtitle → {parsed.Lines.Count} lines");
                    }
                }

                if (docOut is null)
                {
                    if (restricted == 1) Probe.Note(Id, "restricted — no lyric body in this region");
                    Probe.Note(Id, $"{label}: matched, no richsync / subtitle body");
                    return new MacroResult(Kind.NoLyric, null, label + ": no lyric body");
                }

                Probe.CaptureRaw(Id, Probe.Redact(url), "json", body);
                var cand = new Candidate(Id, Prior, basis, docOut)
                {
                    Confidence = conf,
                    Band = band,
                    MatchNote = note,
                };
                return new MacroResult(Kind.Found, cand, $"{label} basis={basis} {note}");
            }

            // ── the token ───────────────────────────────────────────────────────────────────────────────────────────

            bool Fresh(TokenState? t)
            {
                if (t is null) return false;
                long age = _nowMs() - t.AtMs;
                return age >= 0 && age < Profile.TokenTtlMs;
            }

            void DropToken(string token)
            {
                var current = Volatile.Read(ref _token);
                if (current is not null && string.Equals(current.Value, token, StringComparison.Ordinal))
                    Interlocked.CompareExchange(ref _token, null, current);
            }

            /// <summary>The cached usertoken while it is younger than the profile's TTL; otherwise one
            /// <c>token.get</c> (serialized — concurrent fetches share it). Null when none could be had.</summary>
            async Task<string?> TokenAsync(SourceGuard guard, CancellationToken ct)
            {
                var t = Volatile.Read(ref _token);
                if (Fresh(t)) return t!.Value;

                await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    t = Volatile.Read(ref _token);
                    if (Fresh(t)) return t!.Value;
                    if (guard.IsOpen(out _, out _)) return null;

                    string url = BuildTokenUrl(Rnd());
                    var res = await _http.GetAsync(url, Profile.Headers, ct).ConfigureAwait(false);
                    if (res.Status == 0 || string.IsNullOrEmpty(res.Body))
                    {
                        guard.Failure("token.get: no answer" + (res.Status != 0 ? $" (HTTP {res.Status.ToString(CultureInfo.InvariantCulture)})" : ""));
                        return null;
                    }

                    string? token;
                    try
                    {
                        using var doc = JsonDocument.Parse(res.Body);
                        var root = doc.RootElement;
                        Header(root, out int code, out string hint);
                        if (code == 0) code = res.Status;
                        if (code != 200)
                        {
                            Probe.Note(Id, $"token.get → status_code={code.ToString(CultureInfo.InvariantCulture)}" + (hint.Length > 0 ? " hint=" + hint : ""));
                            if (code == 401 && hint.Contains("captcha", StringComparison.OrdinalIgnoreCase)) guard.Trip("captcha", CaptchaTripMs);
                            else if (code == 402) guard.Trip("quota", QuotaTripMs);
                            else guard.Failure($"token.get status_code={code.ToString(CultureInfo.InvariantCulture)}");
                            return null;
                        }
                        token = Body(root, out var b) ? Str(b, "user_token") : null;
                    }
                    catch (JsonException)
                    {
                        guard.Failure("token.get: unparseable body");
                        return null;
                    }

                    if (!Usable(token))
                    {
                        Probe.Note(Id, "token.get returned an unusable token (" + (string.IsNullOrEmpty(token) ? "blank" : Profile.RejectedTokenPrefix + "…") + ")");
                        guard.Failure("unusable usertoken");
                        return null;
                    }
                    guard.Success();
                    Volatile.Write(ref _token, new TokenState(token!, _nowMs()));
                    Probe.Note(Id, "fresh usertoken");
                    return token;
                }
                finally { _tokenGate.Release(); }
            }

            static bool Usable(string? token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                if (token.StartsWith(Profile.RejectedTokenPrefix, StringComparison.OrdinalIgnoreCase)) return false;
                foreach (char c in token) if (char.IsWhiteSpace(c)) return false;
                return true;
            }

            static string Rnd() => Guid.NewGuid().ToString("N")[..8];

            // ── envelope readers (the body can be [] or "" as well as an object) ────────────────────────────────────

            static bool Obj(JsonElement e, string name, out JsonElement v)
            {
                v = default;
                return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Object;
            }

            static bool Body(JsonElement envelope, out JsonElement body)
            {
                body = default;
                return Obj(envelope, "message", out var m) && Obj(m, "body", out body);
            }

            static bool MacroCalls(JsonElement root, out JsonElement calls)
            {
                calls = default;
                return Body(root, out var b) && Obj(b, "macro_calls", out calls);
            }

            static bool MacroCall(JsonElement macro, string name, out JsonElement call) => Obj(macro, name, out call);

            /// <summary><c>message.header.status_code</c> (0 when absent) and <c>hint</c> ("" when absent).</summary>
            static void Header(JsonElement envelope, out int code, out string hint)
            {
                code = 0;
                hint = "";
                if (!Obj(envelope, "message", out var m) || !Obj(m, "header", out var h)) return;
                code = (int)Num(h, "status_code");
                if (code < 0) code = 0;
                hint = Str(h, "hint") ?? "";
            }

            static bool RichsyncBody(JsonElement macro, out string body)
            {
                body = "";
                if (!MacroCall(macro, "track.richsync.get", out var call) || !Body(call, out var b) || !Obj(b, "richsync", out var rs)) return false;
                body = Str(rs, "richsync_body") ?? "";
                return !string.IsNullOrWhiteSpace(body);
            }

            static bool SubtitleBody(JsonElement macro, out string body)
            {
                body = "";
                if (!MacroCall(macro, "track.subtitles.get", out var call) || !Body(call, out var b)) return false;
                if (!b.TryGetProperty("subtitle_list", out var list) || list.ValueKind != JsonValueKind.Array) return false;
                foreach (var item in list.EnumerateArray())
                {
                    if (!Obj(item, "subtitle", out var sub)) continue;
                    body = Str(sub, "subtitle_body") ?? "";
                    if (!string.IsNullOrWhiteSpace(body)) return true;
                }
                return false;
            }

            static string? Str(JsonElement e, string name)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null,
                };
            }

            /// <summary>A number, a numeric string or a boolean (1/0) as a long; -1 when absent or unreadable.</summary>
            static long Num(JsonElement e, string name)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return -1;
                switch (v.ValueKind)
                {
                    case JsonValueKind.Number:
                        if (v.TryGetInt64(out long l)) return l;
                        return v.TryGetDouble(out double d) ? (long)d : -1;
                    case JsonValueKind.String:
                        return long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : -1;
                    case JsonValueKind.True:
                        return 1;
                    case JsonValueKind.False:
                        return 0;
                    default:
                        return -1;
                }
            }

            void Decide(Request req, string outcome, Candidate? c, string detail)
            {
                string line = c is not null
                    ? $"source={Id} track={req.TrackId} outcome={outcome} basis={c.Basis} band={c.Band} conf={c.Confidence:F2} sync={c.Sync} lines={c.LineCount} {detail}"
                    : $"source={Id} track={req.TrackId} outcome={outcome} {detail}";
                Log.Info(Diag.Category, line.Replace('\r', ' ').Replace('\n', ' '));
            }
        }
    }
}
