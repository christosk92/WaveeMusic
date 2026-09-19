// ── Shell/Lyrics.Host.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the fetch aggregator, sources, rerank, disk cache, upgrades, the per-track diagnostics store
//
// Role: SHELL
// Owner: K
// Wave: 4
// Budget: 2300 lines
// Spec: ch 22 §9
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE IMPURE HALF OF LYRICS. Everything with a socket, a file or a clock in it. Every DECISION it makes it asks
// `Lyrics.cs` for — the parsers, the cleaner, the timing gate, the credit rules and the reranker are all CORE, so
// this file is a fan-out, a cache and a bookkeeper and never a second opinion.
//
// THE SHAPE, whole:
//
//   `Lyrics.Store`        the UI's door. `Doc(track)` is one dictionary read; `Ensure(track)` demands a fetch;
//                         `Changed` bumps once per publication. Keyed by the base62 TRACK ID, not by a slot — a slot
//                         is scope-local and dies on a scope switch, while the id is what the disk cache and the
//                         aggregator already key by, so one key spans all three.
//   `Lyrics.Aggregator`   the fan-out. Parallel sources → clean each at ONE chokepoint → rerank against the
//                         Spotify-native reference → one winner, cached in memory and on disk. NOT first-hit: a
//                         slower word-synced candidate can still beat an earlier line-synced one, and when it lands
//                         after the UI already has a document it is published as an UPGRADE.
//   `Lyrics.DiskCache`    one JSON envelope per track under `%LOCALAPPDATA%\Wavee\lyrics`, so a previously-played
//                         track resolves offline before a single request goes out. Negative markers are TTL-bounded.
//   `Lyrics.Diag`         the per-track explainability store the inspector reads: per-source traces, raw payloads,
//                         every candidate's PARSED document. Bounded (§7 gap 7's caps) and published on EVERY search.
//   `Lyrics.Sources.*`    the three clean-by-default sources: AMLL (identity, word-synced), Spotify-native (the
//                         reranker's REFERENCE and a line candidate) and LRCLIB (metadata search).
//   `Lyrics.ResolveRequest` track id → the search `Request`, parked on `Lyrics.Requests` until the publication that
//                         commits the row's identity (G-252) — never polled.
//
// CONCURRENCY (C1/C4/C8). Async is allowed here (SHELL), but: nothing writes a table; every published value crosses
// back to the UI thread through `Lyrics.Store`'s post (`Platform`-installed dispatcher); there is exactly ONE shared
// fetch per track id (`_inFlight`), so the rail panel and the immersive surface asking for the same track concurrently
// cost one fan-out, not two; the winner cache is an LRU with a hard cap; and the demand queue is bounded and
// latest-wins. The work itself runs on `CancellationToken.None` and is never cancelled by a caller — it exists to
// POPULATE the caches, so the first caller walking away must not cancel the second caller's lyrics. Each caller
// observes its OWN token through `WaitAsync`.
//
// WHAT IS DELIBERATELY NOT HERE (named, per the wave's report):
//   • The GREY CJK/Musixmatch sources (KRC/YRC/QRC/richsync network fetchers + the DES payload decrypt, 866 lines of
//     0.2.9). They are OFF BY DEFAULT in 0.2.9 too (`Options.EnableGreyProviders == false`), and their FORMAT
//     PARSERS are already ported verbatim into `Lyrics.cs` §15 and pinned by the fixture tests — so re-adding the
//     fetchers later is additive against a frozen `ISource` seam and needs no core change.
//   • The inspector DIALOG. A14: the STORE stays here (`Lyrics.Diag`), the dialog is
//     `Diagnostics.LyricsInspector.Open(overlay, trackId)` in `Screens/Diagnostics.UI.cs` (Wave 6, owner S), and
//     `Rail.UI.cs` keeps only the dev-mode-gated glyph that calls it.
//   • The `--lyrics-advance-probe` CLI arm. Wave 6's `Screens/Diagnostics.Probe.cs` (plan §9.6 Q1).

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using FluentGpu.Signals;

namespace Wavee;

public static partial class Lyrics
{
    // ── 1. the aggregator's configuration and seams ─────────────────────────────────────────────────────────────────

    /// <summary>Which sources run by default and the per-source budget. The grey CJK/Musixmatch sources are OFF by
    /// default; only AMLL + Spotify-native + LRCLIB are clean-by-default.</summary>
    /// <param name="FirstHitGraceMs">How long the UI waits AFTER the first usable candidate before it takes what it
    /// has. Slower sources keep running in the background and can publish a richer replacement.</param>
    public sealed record Options(
        bool EnableGreyProviders = false,
        int PerSourceTimeoutMs = 6000,
        int TotalTimeoutMs = 9000,
        int FirstHitGraceMs = 2000)
    {
        public static Options Default { get; } = new();
    }

    /// <summary>A candidate source: fetch + parse + normalize one provider's lyrics. Returns null on a miss (NOT an
    /// exception — a miss must not fail the aggregate). Implementations own their own HTTP and per-source negative
    /// caching.</summary>
    public interface ISource
    {
        string Id { get; }
        bool Enabled { get; }
        double Prior { get; }
        Task<Candidate?> FetchAsync(Request req, CancellationToken ct);
    }

    /// <summary>A tiny GET seam so the public sources are unit-testable with a fake (no network in tests).</summary>
    public interface IHttp
    {
        Task<string?> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct);
    }

    public readonly record struct HttpResult(int Status, string? Body)
    {
        public bool IsSuccess => Status is >= 200 and < 300;
    }

    /// <summary>The status-aware form. AMLL needs it: a 404 is a PERMANENT per-track miss worth remembering, while a
    /// transport failure is not.</summary>
    public interface IHttpWithStatus : IHttp
    {
        Task<HttpResult> GetAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct);
    }

    /// <summary>The real fetch, over one pooled third-party client. Returns null on any non-success or transport error
    /// so a source miss is a clean null, never a throw that aborts the fan-out.</summary>
    public sealed class HttpFetch : IHttpWithStatus
    {
        /// <summary>One pooled handler for every third-party lyrics host. Named so a connection dump attributes it.</summary>
        static readonly HttpClient Client = new(Wire.Handler("lyrics", new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            MaxConnectionsPerServer = 4,
        }))
        { Timeout = TimeSpan.FromSeconds(20) };

        readonly string _userAgent;

        public HttpFetch(string userAgent = "Wavee/1.0 (https://github.com/christosk92/Wavee)")
            => _userAgent = userAgent;

        public async Task<string?> GetStringAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            var result = await GetAsync(url, headers, ct).ConfigureAwait(false);
            return result.IsSuccess ? result.Body : null;
        }

        public async Task<HttpResult> GetAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                if (headers is not null)
                    foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
                using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
                var body = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
                return new HttpResult((int)resp.StatusCode, body);
            }
            catch (OperationCanceledException) { throw; }
            catch { return new HttpResult(0, null); }
        }
    }

    // ── 2. the explainability store ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>What one source did within a single track search.</summary>
    public enum Outcome { Pending, Hit, Miss, Timeout, Error, Skipped }

    /// <summary>One source's result within a single track search — outcome + timing + a human "why" (its probe
    /// breadcrumbs), plus the reranker's verdict once ranked.
    /// <para>The reranker's score BREAKDOWN rides along because a bare 0.885-vs-0.740 says who won but not why, and
    /// "why" is always the actual question: a word-synced candidate losing to a line-synced one is a completely
    /// different problem depending on whether it lost on text agreement or on the sync tier being worth too
    /// little.</para></summary>
    public sealed record SourceTrace(
        string SourceId,
        Outcome Outcome,
        long ElapsedMs,
        string Detail,
        SyncKind Sync,
        int LineCount,
        double Score,
        bool Winner,
        string RerankReason,
        double Text = 0d,
        double Coverage = 0d,
        double Timing = 0d,
        double SyncScore = 0d);

    /// <summary>The full explainable record of ONE fetch: the request metadata the sources searched with, the
    /// per-source traces, and a one-line summary.</summary>
    public sealed record SearchReport(
        string TrackId,
        string Title,
        string Artist,
        string Album,
        long DurationMs,
        string? Isrc,
        long WhenUnixMs,
        string Summary,
        IReadOnlyList<SourceTrace> Sources);

    /// <summary>One provider payload captured EXACTLY as it arrived — the HTTP body, or, for the encrypted formats,
    /// the decrypted text the parser actually sees. <see cref="Label"/> is the CREDENTIAL-REDACTED url.</summary>
    public sealed record RawPayload(string SourceId, string Label, string Format, string Text, int OriginalLength)
    {
        public bool Truncated => OriginalLength > Text.Length;
    }

    /// <summary>One source's PARSED document, kept alongside the raw payload it came from so the inspector can show
    /// the two side by side. This is the candidate BEFORE the reranker's offset correction.</summary>
    public sealed record ParsedCandidate(string SourceId, MatchBasis Basis, double Prior, Doc Document);

    /// <summary>The heavy half of one track's record: every payload captured verbatim, every candidate's parsed
    /// document, and the document the UI ended up with. Republished (REPLACING the previous entry) each time a pass
    /// finishes for the track, because a later pass always carries a superset.</summary>
    public sealed record Inspection(
        string TrackId,
        long WhenUnixMs,
        string Note,
        IReadOnlyList<RawPayload> Raw,
        IReadOnlyList<ParsedCandidate> Candidates,
        Doc? Final);

    /// <summary>Ambient (AsyncLocal) per-search breadcrumb collector. The aggregator sets <see cref="Current"/> before
    /// the fan-out; each source calls the static <see cref="Note"/> at its decision points and <see cref="CaptureRaw"/>
    /// with each payload it receives. Because the fan-out tasks are STARTED while Current is set, the value flows into
    /// each task; the probe object itself is shared, so notes and payloads from all sources land in one place.</summary>
    public sealed class Probe
    {
        public static readonly AsyncLocal<Probe?> Current = new();

        // Capture caps (ch 22 §7 gap 7 — LOAD-BEARING, not belt and braces: the report is published on EVERY search
        // regardless of any developer flag, so without these a long session accumulates payload bytes forever).
        public const int MaxPayloadChars = 128_000;
        public const int MaxPayloadsPerSource = 6;
        public const int MaxTotalChars = 640_000;

        readonly Lock _gate = new();
        readonly Dictionary<string, List<string>> _notes = new(StringComparer.Ordinal);
        readonly List<RawPayload> _raw = [];
        int _rawChars;

        /// <summary>Record a breadcrumb for <paramref name="sourceId"/> (a no-op when no probe is active).</summary>
        public static void Note(string sourceId, string message)
        {
            var p = Current.Value;
            if (p is null) return;
            lock (p._gate)
            {
                if (!p._notes.TryGetValue(sourceId, out var list)) p._notes[sourceId] = list = [];
                list.Add(message);
            }
        }

        /// <summary>Keep one payload verbatim for the inspector. A no-op when no probe is active (unit tests, the
        /// disk-hit path) or when this probe has spent its capture budget. Pass the url through <see cref="Redact"/>
        /// first — the captured text is copied to the clipboard by a human.</summary>
        public static void CaptureRaw(string sourceId, string label, string format, string? payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            var p = Current.Value;
            if (p is null) return;
            lock (p._gate)
            {
                int room = MaxTotalChars - p._rawChars;
                if (room <= 0) return;
                int perSource = 0;
                foreach (var r in p._raw) if (StringComparer.Ordinal.Equals(r.SourceId, sourceId)) perSource++;
                if (perSource >= MaxPayloadsPerSource) return;

                int keep = Math.Min(Math.Min(MaxPayloadChars, room), payload!.Length);
                string text = keep == payload.Length ? payload : payload[..keep];
                p._rawChars += text.Length;
                p._raw.Add(new RawPayload(sourceId, label, format, text, payload.Length));
            }
        }

        public string NotesFor(string sourceId)
        {
            lock (_gate) return _notes.TryGetValue(sourceId, out var list) ? string.Join("; ", list) : "";
        }

        public IReadOnlyList<RawPayload> RawPayloads()
        {
            lock (_gate) return _raw.Count == 0 ? [] : _raw.ToArray();
        }

        // Query keys whose VALUE is a credential. A macro url carries a usertoken and a download url an accesskey;
        // both would otherwise ride along into whatever the user pastes into a bug report.
        static readonly string[] SecretKeys =
            ["usertoken", "user_token", "accesskey", "access_token", "token", "api_key", "apikey", "auth", "authorization", "signature", "sign"];

        /// <summary>Replace the value of every credential-bearing query parameter with a placeholder. The path and
        /// every other parameter — the actual search terms, the interesting part — survive intact.</summary>
        public static string Redact(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            int q = url.IndexOf('?');
            if (q < 0) return url;

            var sb = new StringBuilder(url.Length);
            sb.Append(url, 0, q + 1);
            string[] pairs = url[(q + 1)..].Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                if (i > 0) sb.Append('&');
                int eq = pairs[i].IndexOf('=');
                if (eq <= 0) { sb.Append(pairs[i]); continue; }
                string key = pairs[i][..eq];
                bool secret = false;
                foreach (string s in SecretKeys)
                    if (string.Equals(key, s, StringComparison.OrdinalIgnoreCase)) { secret = true; break; }
                sb.Append(key).Append('=').Append(secret ? "***redacted***" : pairs[i][(eq + 1)..]);
            }
            return sb.ToString();
        }
    }

    /// <summary>Process-wide store of the most recent lyric searches, read by the inspector.
    ///
    /// <para>TWO STORES, deliberately different sizes. The REPORT store is pure metadata and is cheap enough to keep
    /// for the last <see cref="Cap"/> searches / <see cref="TrackCap"/> distinct tracks. The INSPECTION store is the
    /// heavy half — untouched provider payloads and every candidate's parsed document — and keeps only
    /// <see cref="InspectionCap"/> tracks, because the only track anyone inspects is the one playing.</para></summary>
    public static class Diag
    {
        public const int Cap = 24;
        /// <summary>Bound the per-track store: it is published on EVERY search, so a long session would otherwise
        /// accumulate a report per distinct track forever.</summary>
        public const int TrackCap = 256;
        /// <summary>Two orders of magnitude below <see cref="TrackCap"/> on purpose: this store holds real payload
        /// bytes.</summary>
        public const int InspectionCap = 3;

        /// <summary>The log category. Always on — no env switch, ever.</summary>
        public const string Category = "lyrics";

        static readonly Lock Gate = new();
        static readonly LinkedList<SearchReport> Recent_ = new();
        static readonly Dictionary<string, SearchReport> ByTrack = new(StringComparer.Ordinal);
        static readonly Queue<string> Order = new();   // first-seen order of distinct track ids, for FIFO eviction
        static readonly Dictionary<string, Inspection> Inspections = new(StringComparer.Ordinal);
        static readonly List<string> InspectionLru = [];   // MRU at the end
        static long s_version;

        /// <summary>Monotonic publish counter — read it to detect a fresh report without holding the lock.</summary>
        public static long Version => Interlocked.Read(ref s_version);

        public static void Publish(SearchReport report)
        {
            lock (Gate)
            {
                if (ByTrack.TryAdd(report.TrackId, report)) Order.Enqueue(report.TrackId);
                else ByTrack[report.TrackId] = report;      // re-publish → update in place, keep its order
                Recent_.AddFirst(report);
                while (Recent_.Count > Cap) Recent_.RemoveLast();
                while (ByTrack.Count > TrackCap && Order.Count > 0) ByTrack.Remove(Order.Dequeue());
            }
            Interlocked.Increment(ref s_version);
        }

        public static SearchReport? ForTrack(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return null;
            lock (Gate) return ByTrack.TryGetValue(trackId, out var r) ? r : null;
        }

        public static IReadOnlyList<SearchReport> Recent()
        {
            lock (Gate) return Recent_.ToArray();
        }

        /// <summary>Record (REPLACING any previous entry for the track) the raw payloads + parsed candidates of one
        /// pass. A later pass for the same track always carries a superset, so replace — never merge.</summary>
        public static void PublishInspection(Inspection inspection)
        {
            if (string.IsNullOrEmpty(inspection.TrackId)) return;
            lock (Gate)
            {
                Inspections[inspection.TrackId] = inspection;
                InspectionLru.Remove(inspection.TrackId);
                InspectionLru.Add(inspection.TrackId);
                while (InspectionLru.Count > InspectionCap)
                {
                    Inspections.Remove(InspectionLru[0]);
                    InspectionLru.RemoveAt(0);
                }
            }
            Interlocked.Increment(ref s_version);
        }

        public static Inspection? InspectionFor(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return null;
            lock (Gate) return Inspections.TryGetValue(trackId, out var i) ? i : null;
        }

        /// <summary>Drop everything (logout / provider-config change).</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Recent_.Clear(); ByTrack.Clear(); Order.Clear();
                Inspections.Clear(); InspectionLru.Clear();
            }
            Interlocked.Increment(ref s_version);
        }

        /// <summary>A timestamp as <c>m:ss</c>, for the decoy verdicts and the inspector's own lines.</summary>
        public static string Ts(long ms)
        {
            if (ms < 0) ms = 0;
            long m = ms / 60000, s = (ms % 60000) / 1000;
            return m.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
                 + s.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // ── 3. the disk cache ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a disk lookup found. <see cref="KnownMissing"/> is a LIVE negative marker — the caller must
    /// return "no lyrics" WITHOUT fanning out.</summary>
    public enum CacheOutcome { Miss, Hit, KnownMissing }

    /// <summary>One disk lookup's answer. <see cref="SavedAtUnixMs"/> is when the entry was persisted (0 on a
    /// miss).</summary>
    public readonly record struct CacheEntry(CacheOutcome Outcome, Doc? Document, long SavedAtUnixMs)
    {
        public static CacheEntry Missing => default;   // Outcome == Miss
    }

    /// <summary>The PERSISTENT half of the aggregator's winner cache. The in-memory LRU dies with the process, so
    /// every restart would otherwise re-fan-out for songs the app already knows the words to — and with no network
    /// there would be no lyrics at all.
    ///
    /// <para>SHAPE: one JSON file per track under <c>%LOCALAPPDATA%\Wavee\lyrics</c>, named for the SHA-256 of the
    /// track id (base62 ids are case-sensitive and would collide on a case-insensitive filesystem). The file is a
    /// VERSIONED ENVELOPE: a version mismatch, a track-id mismatch or an unparseable body is a MISS that also deletes
    /// the file — a cache is never allowed to fail a read.</para>
    ///
    /// <para>DURABILITY: temp-file + move-with-overwrite, so a crash mid-write can never leave a torn document behind.
    /// Every path swallows: a cache is best-effort by definition.</para></summary>
    public sealed class DiskCache
    {
        /// <summary>Envelope schema. BUMP whenever the persisted document shape changes MEANING — an entry written by
        /// another version is discarded on read rather than misinterpreted.</summary>
        public const int SchemaVersion = 1;

        /// <summary>Sweep caps. ~2000 word-synced documents is a deep listening history and still a small folder; the
        /// byte cap catches the pathological case before the file count would.</summary>
        public const int DefaultMaxFiles = 2000;
        public const long DefaultMaxBytes = 50L << 20;

        /// <summary>How long "this track has no lyrics anywhere" is trusted. Long enough that a repeat play of an
        /// instrumental is free, short enough that a newly published lyric is picked up within days.</summary>
        public static TimeSpan DefaultNegativeTtl => TimeSpan.FromDays(3);

        const double SweepTargetFraction = 0.8;   // trim to 80 % so a sweep is not re-armed on the next write
        const int StemLength = 64;                // SHA-256 as lowercase hex

        readonly string _dir;
        readonly Func<long> _nowUnixMs;
        readonly int _maxFiles;
        readonly long _maxBytes;
        readonly long _negativeTtlMs;
        int _sweepArmed;
        Task? _sweep;
        int _writeFaultLogged;

        /// <param name="directory">Cache root. Tests inject a temp directory; production passes null for
        /// <see cref="DefaultDirectory"/>, so the real profile is never touched by a test.</param>
        /// <param name="nowUnixMs">Clock seam for the negative TTL.</param>
        public DiskCache(
            string? directory = null,
            Func<long>? nowUnixMs = null,
            int maxFiles = DefaultMaxFiles,
            long maxBytes = DefaultMaxBytes,
            TimeSpan? negativeTtl = null)
        {
            _dir = Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory!);
            _nowUnixMs = nowUnixMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _maxFiles = Math.Max(16, maxFiles);
            _maxBytes = Math.Max(1L << 20, maxBytes);
            _negativeTtlMs = (long)Math.Max(0d, (negativeTtl ?? DefaultNegativeTtl).TotalMilliseconds);
        }

        /// <summary>The profile's <c>lyrics</c> folder — beside <c>logs\</c> and the entity store.</summary>
        public static string DefaultDirectory() => Path.Combine(Platform.LocalFolder, "lyrics");

        public string Directory => _dir;

        // ── read ────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Look one track up. NEVER throws for a bad file (a corrupt/stale/foreign entry is a miss and is
        /// deleted on the way out). Only a real caller cancellation propagates.</summary>
        public async Task<CacheEntry> TryLoadAsync(string trackId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(trackId)) return CacheEntry.Missing;
            ArmSweep();

            string path = PathFor(trackId);
            byte[] bytes;
            try
            {
                if (!File.Exists(path)) return CacheEntry.Missing;
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return CacheEntry.Missing; }   // locked / racing delete / unreadable — a miss, never a throw

            CacheEnvelope? env = null;
            try { env = JsonSerializer.Deserialize(bytes, CacheJson.Default.CacheEnvelope); }
            catch { /* unparseable → discarded below */ }

            if (env is null) { Discard(path, trackId, "unparseable"); return CacheEntry.Missing; }
            if (env.V != SchemaVersion) { Discard(path, trackId, "schema v" + env.V); return CacheEntry.Missing; }
            if (!string.IsNullOrEmpty(env.Id) && !string.Equals(env.Id, trackId, StringComparison.Ordinal))
            { Discard(path, trackId, "track id mismatch"); return CacheEntry.Missing; }

            if (env.Doc is null)
            {
                long age = _nowUnixMs() - env.At;
                if (age >= 0 && age < _negativeTtlMs) return new(CacheOutcome.KnownMissing, null, env.At);
                Discard(path, trackId, "negative marker expired");
                return CacheEntry.Missing;
            }

            // A document with no lines is unusable — the view would render an empty lyric instead of searching. Treat
            // it exactly like a corrupt file so the next play re-fetches.
            if (env.Doc.Lines is not { Count: > 0 }) { Discard(path, trackId, "no lines"); return CacheEntry.Missing; }
            return new(CacheOutcome.Hit, env.Doc, env.At);
        }

        // ── write ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Persist a winning document. Fire-and-forget: serialization and I/O both run off the caller's
        /// thread and every failure is swallowed (logged once).</summary>
        public void Save(string trackId, Doc document)
        {
            if (string.IsNullOrEmpty(trackId) || document is null) return;
            _ = Task.Run(() => SaveAsync(trackId, document));
        }

        /// <summary>Persist the "no lyrics anywhere" marker (TTL-bounded).</summary>
        public void SaveMissing(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return;
            _ = Task.Run(() => SaveAsync(trackId, null));
        }

        /// <summary>The awaitable core of both writes (the deterministic seam tests use). Never throws.</summary>
        public async Task SaveAsync(string trackId, Doc? document, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(trackId)) return;
            ArmSweep();
            string path = PathFor(trackId);
            string tmp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
                var env = new CacheEnvelope(SchemaVersion, _nowUnixMs(), trackId, document);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(env, CacheJson.Default.CacheEnvelope);
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);   // write-then-rename: a crash cannot leave a torn document
            }
            catch (Exception e)
            {
                TryDelete(tmp);
                if (Interlocked.Exchange(ref _writeFaultLogged, 1) == 0)
                    Log.Warn(Diag.Category, "lyrics disk cache write failed (" + e.GetType().Name
                        + ") — lyrics still work, they just will not persist");
            }
        }

        // ── maintenance ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Drop every entry this cache owns. Only files matching the cache's OWN name shape are touched, so a
        /// mis-pointed directory can never be wiped.</summary>
        public void Clear()
        {
            try
            {
                if (!System.IO.Directory.Exists(_dir)) return;
                foreach (string file in System.IO.Directory.EnumerateFiles(_dir, "*", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(file);
                    if (IsOwnedEntry(name) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) TryDelete(file);
                }
            }
            catch { }
        }

        /// <summary>Drop ONE track's entry (positive or negative). Backs the inspector's "re-fetch from providers":
        /// the next lookup has to MISS or the fan-out never runs and there is still no raw payload to show.</summary>
        public void Forget(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return;
            try { TryDelete(PathFor(trackId)); } catch { }
        }

        /// <summary>Enforce the size caps: oldest-first (by save time) down to 80 % of both caps. Best-effort and
        /// synchronous — production reaches it through <see cref="ArmSweep"/>, off-thread, once.</summary>
        public void Sweep()
        {
            try
            {
                if (!System.IO.Directory.Exists(_dir)) return;

                var entries = new List<FileInfo>();
                long bytes = 0;
                foreach (string file in System.IO.Directory.EnumerateFiles(_dir, "*", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(file);
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        // A torn write from a crashed session. Anything younger than 10 minutes may be a live
                        // intermediate of a concurrent save.
                        try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(10)) TryDelete(file); }
                        catch { }
                        continue;
                    }
                    if (!IsOwnedEntry(name)) continue;
                    try { var fi = new FileInfo(file); bytes += fi.Length; entries.Add(fi); } catch { }
                }

                if (entries.Count <= _maxFiles && bytes <= _maxBytes) return;

                entries.Sort(static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));   // oldest first
                int fileTarget = (int)(_maxFiles * SweepTargetFraction);
                long byteTarget = (long)(_maxBytes * SweepTargetFraction);
                int count = entries.Count, removed = 0;
                foreach (var fi in entries)
                {
                    if (count <= fileTarget && bytes <= byteTarget) break;
                    long len; try { len = fi.Length; } catch { len = 0; }
                    TryDelete(fi.FullName);
                    count--; bytes -= len; removed++;
                }
                if (removed > 0)
                    Log.Info(Diag.Category, $"lyrics disk cache swept: removed {removed} oldest entries, {count} remain ({bytes} bytes)");
            }
            catch { }
        }

        /// <summary>The lazily-armed sweep task (test seam).</summary>
        public Task SweepInFlight => Volatile.Read(ref _sweep) ?? Task.CompletedTask;

        /// <summary>First use in the process schedules ONE sweep off-thread. A read never waits on it.</summary>
        void ArmSweep()
        {
            if (Interlocked.CompareExchange(ref _sweepArmed, 1, 0) != 0) return;
            Volatile.Write(ref _sweep, Task.Run(Sweep));
        }

        // ── paths ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The file a track id maps to. SHA-256 hex: base62 ids are case-sensitive and would alias on a
        /// case-insensitive filesystem, and a local/podcast id is not filesystem-safe at all.</summary>
        public string PathFor(string trackId) => Path.Combine(_dir, Stem(trackId) + ".json");

        static string Stem(string trackId)
            => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(trackId)));

        static bool IsOwnedEntry(string fileName)
        {
            if (fileName.Length != StemLength + 5 || !fileName.EndsWith(".json", StringComparison.Ordinal)) return false;
            for (int i = 0; i < StemLength; i++)
            {
                char c = fileName[i];
                if (!((uint)(c - '0') <= 9u || (uint)(c - 'a') <= 5u)) return false;
            }
            return true;
        }

        static void Discard(string path, string trackId, string why)
        {
            TryDelete(path);
            Log.Debug(Diag.Category, $"lyrics disk cache discarded entry for {trackId}: {why}");
        }

        static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }

    /// <summary>The persisted envelope. Short keys: a word-synced document is tens of KB of syllables and one is
    /// written per played track. <c>doc</c> is absent on a negative marker.</summary>
    internal sealed record CacheEnvelope(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("at")] long At,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("doc")] Doc? Doc);

    /// <summary>AOT-safe SOURCE-GENERATED JSON — no reflection-based serialization anywhere in this app.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(CacheEnvelope))]
    internal sealed partial class CacheJson : JsonSerializerContext { }

    // ── 4. the three clean-by-default sources ───────────────────────────────────────────────────────────────────────

    /// <summary>The sources that ship enabled. Each is a thin adapter: fetch, hand the body to a CORE parser, wrap the
    /// result as a <see cref="Candidate"/>. None of them decides anything.</summary>
    public static class Sources
    {
        /// <summary>The community word-by-word (SYLLABLE) TTML library, indexed directly by Spotify track id. The
        /// cleanest word-synced source and the highest provider prior: a direct IDENTITY match, so when present it is
        /// the best karaoke candidate — this is what gives per-syllable timing, which the line-only sources cannot. A
        /// miss (no file → 404 → null) just drops it from the fan-out, and the 404 is remembered for the session
        /// because it is permanent for that id.</summary>
        public sealed class Amll : ISource
        {
            public const string DefaultTemplate =
                "https://raw.githubusercontent.com/amll-dev/amll-ttml-db/main/spotify-lyrics/{0}.ttml";

            readonly IHttp _http;
            readonly string _template;
            readonly ConcurrentDictionary<string, byte> _misses = new(StringComparer.Ordinal);

            public Amll(IHttp http, string? urlTemplate = null)
            {
                _http = http;
                _template = urlTemplate ?? DefaultTemplate;
            }

            public string Id => "amll";
            public bool Enabled => true;
            public double Prior => 0.9;   // top prior: a curated, identity-matched, word-synced source

            public async Task<Candidate?> FetchAsync(Request req, CancellationToken ct)
            {
                if (string.IsNullOrEmpty(req.TrackId)) return null;
                if (req.HasSpotifyLyrics == true) return null;
                if (_misses.ContainsKey(req.TrackId)) return null;
                string url = string.Format(System.Globalization.CultureInfo.InvariantCulture, _template, req.TrackId);
                string? ttml;
                if (_http is IHttpWithStatus statusHttp)
                {
                    var result = await statusHttp.GetAsync(url, null, ct).ConfigureAwait(false);
                    if (result.Status == 404) { _misses.TryAdd(req.TrackId, 0); return null; }
                    if (!result.IsSuccess) return null;
                    ttml = result.Body;
                }
                else
                {
                    ttml = await _http.GetStringAsync(url, null, ct).ConfigureAwait(false);
                }
                if (string.IsNullOrWhiteSpace(ttml)) { _misses.TryAdd(req.TrackId, 0); return null; }
                Probe.CaptureRaw(Id, Probe.Redact(url), "ttml", ttml);

                var doc = Text.ParseTtml(ttml!, req.TrackId, Id);
                if (doc.Lines.Count == 0) { _misses.TryAdd(req.TrackId, 0); return null; }
                return new Candidate(Id, Prior, MatchBasis.Identity, doc);
            }
        }

        /// <summary>Spotify-native colour-lyrics. Primarily the reranker's TEXT/TIMING REFERENCE (Spotify knows the
        /// exact track), but also a line-synced candidate in its own right. Uses an INJECTED authed GET delegate so
        /// this stays testable and decoupled from the transport. Skipped when the wire says the track has no Spotify
        /// lyrics — which never suppresses the other sources.</summary>
        public sealed class SpotifyNative : ISource
        {
            readonly Func<string, CancellationToken, Task<string?>> _get;
            readonly Func<string> _baseUrl;

            /// <param name="get">Authed GET against the spclient (returns the JSON body, or null on miss/error).</param>
            /// <param name="baseUrl">The resolved spclient base url.</param>
            public SpotifyNative(Func<string, CancellationToken, Task<string?>> get, Func<string> baseUrl)
            {
                _get = get;
                _baseUrl = baseUrl;
            }

            public string Id => "spotify";
            public bool Enabled => true;
            public double Prior => 0.55;

            public async Task<Candidate?> FetchAsync(Request req, CancellationToken ct)
            {
                if (req.HasSpotifyLyrics == false) return null;   // the wire says none → skip THIS source only
                if (string.IsNullOrEmpty(req.TrackId)) return null;
                string url = _baseUrl().TrimEnd('/') + "/color-lyrics/v2/track/" + req.TrackId
                    + "?format=json&vocalRemoval=false&market=" + req.Market;
                string? json = await _get(url, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json)) return null;
                Probe.CaptureRaw(Id, Probe.Redact(url), "json", json);

                var doc = Parse(json!, req.TrackId);
                if (doc is null || doc.Lines.Count == 0) return null;
                return new Candidate(Id, Prior, MatchBasis.Identity, doc);
            }

            /// <summary>Parse the colour-lyrics JSON: <c>lyrics.syncType</c> + <c>lyrics.lines[].startTimeMs/words</c>
            /// (the start is a STRING on the wire). Syllables are normally empty → line-synced.</summary>
            public static Doc? Parse(string json, string trackId)
            {
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (!d.RootElement.TryGetProperty("lyrics", out var lyr) || lyr.ValueKind != JsonValueKind.Object) return null;
                    string syncType = lyr.TryGetProperty("syncType", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()! : "";
                    bool synced = syncType.Contains("SYNCED", StringComparison.OrdinalIgnoreCase)
                        && !syncType.Equals("UNSYNCED", StringComparison.OrdinalIgnoreCase);
                    if (!lyr.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return null;

                    var outLines = new List<Line>();
                    foreach (var ln in lines.EnumerateArray())
                    {
                        long start = 0;
                        if (ln.TryGetProperty("startTimeMs", out var sm))
                        {
                            if (sm.ValueKind == JsonValueKind.String) long.TryParse(sm.GetString(), out start);
                            else if (sm.ValueKind == JsonValueKind.Number) start = sm.GetInt64();
                        }
                        string words = ln.TryGetProperty("words", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString()! : "";
                        outLines.Add(new Line(start, words, []));
                    }
                    if (outLines.Count == 0) return null;
                    return new Doc(trackId, synced, outLines, synced ? SyncKind.Line : SyncKind.Unsynced, "spotify");
                }
                catch { return null; }
            }
        }

        /// <summary>A clean, free, no-auth synced-lyrics database. Primary lookup is the exact get (artist + track +
        /// album + duration); on a miss it falls back to search and picks the closest result BY DURATION. Synced
        /// payloads are LRC; plain payloads become unsynced. Matched by METADATA, so the reranker validates it against
        /// the Spotify-native reference before trusting it.</summary>
        public sealed class LrcLib : ISource
        {
            /// <summary>How far a search result's duration may be from the track's before it is rejected outright —
            /// the wrong-song guardrail on a metadata match.</summary>
            public const long MaxDurationDeltaMs = 5000;

            readonly IHttp _http;
            public LrcLib(IHttp http) => _http = http;

            public string Id => "lrclib";
            public bool Enabled => true;
            public double Prior => 0.45;

            public async Task<Candidate?> FetchAsync(Request req, CancellationToken ct)
            {
                long sec = req.DurationMs > 0 ? req.DurationMs / 1000 : 0;
                string get = "https://lrclib.net/api/get"
                    + "?artist_name=" + Uri.EscapeDataString(req.PrimaryArtist)
                    + "&track_name=" + Uri.EscapeDataString(req.Title)
                    + "&album_name=" + Uri.EscapeDataString(req.Album)
                    + (sec > 0 ? "&duration=" + sec.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
                string? body = await _http.GetStringAsync(get, null, ct).ConfigureAwait(false);
                Probe.CaptureRaw(Id, Probe.Redact(get), "json", body);

                Doc? doc = body is not null ? FromObject(body, req) : null;
                Probe.Note(Id, doc is not null ? "exact /api/get hit" : "exact /api/get miss → /api/search");
                doc ??= await SearchAsync(req, ct).ConfigureAwait(false);
                if (doc is null || doc.Lines.Count == 0) return null;
                return new Candidate(Id, Prior, MatchBasis.MetadataSearch, doc);
            }

            // The query-variant ladder: full "title/artist" → feat-stripped "title/artist" → "title" only. Returns the
            // first variant that yields a usable doc (each keeps the duration-closest pick + the reject guardrail).
            async Task<Doc?> SearchAsync(Request req, CancellationToken ct)
            {
                foreach (var (title, artist) in Query.TitleArtistVariants(req))
                {
                    var doc = await SearchOnce(title, artist, req, ct).ConfigureAwait(false);
                    if (doc is { Lines.Count: > 0 })
                    {
                        Probe.Note(Id, $"/api/search hit on '{title}'" + (artist.Length > 0 ? $" / '{artist}'" : " (title-only)"));
                        return doc;
                    }
                    ct.ThrowIfCancellationRequested();
                }
                return null;
            }

            async Task<Doc?> SearchOnce(string title, string artist, Request req, CancellationToken ct)
            {
                string search = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title)
                    + (artist.Length > 0 ? "&artist_name=" + Uri.EscapeDataString(artist) : "");
                string? json = await _http.GetStringAsync(search, null, ct).ConfigureAwait(false);
                Probe.CaptureRaw(Id, Probe.Redact(search), "json", json);
                if (json is null) return null;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.ValueKind != JsonValueKind.Array) return null;
                    string? bestSynced = null, bestPlain = null; long bestDelta = long.MaxValue; int n = 0;
                    foreach (var el in d.RootElement.EnumerateArray())
                    {
                        n++;
                        if (el.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True) continue;
                        string? sl = Str(el, "syncedLyrics");
                        string? pl = Str(el, "plainLyrics");
                        if (string.IsNullOrWhiteSpace(sl) && string.IsNullOrWhiteSpace(pl)) continue;
                        long dur = el.TryGetProperty("duration", out var du) && du.TryGetDouble(out var ds) ? (long)(ds * 1000) : 0;
                        long delta = req.DurationMs > 0 && dur > 0 ? Math.Abs(dur - req.DurationMs) : 0;
                        if (delta < bestDelta) { bestDelta = delta; bestSynced = sl; bestPlain = pl; }
                    }
                    Probe.Note(Id, $"/api/search '{title}' → {n} results" + (bestDelta != long.MaxValue ? $" (best Δ{bestDelta}ms)" : ""));
                    if (req.DurationMs > 0 && bestDelta > MaxDurationDeltaMs)
                    { Probe.Note(Id, "closest result's duration > 5s off — rejected"); return null; }
                    if (!string.IsNullOrWhiteSpace(bestSynced)) return Text.ParseLrc(bestSynced!, req.TrackId, Id);
                    if (!string.IsNullOrWhiteSpace(bestPlain)) return Unsynced(bestPlain!, req.TrackId);
                    return null;
                }
                catch { return null; }
            }

            Doc? FromObject(string json, Request req)
            {
                try
                {
                    using var d = JsonDocument.Parse(json);
                    var el = d.RootElement;
                    if (el.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True) return null;
                    string? synced = Str(el, "syncedLyrics");
                    if (!string.IsNullOrWhiteSpace(synced)) return Text.ParseLrc(synced!, req.TrackId, Id);
                    string? plain = Str(el, "plainLyrics");
                    if (!string.IsNullOrWhiteSpace(plain)) return Unsynced(plain!, req.TrackId);
                    return null;
                }
                catch { return null; }
            }

            static string? Str(JsonElement el, string name)
                => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            static Doc Unsynced(string plain, string trackId)
            {
                var lines = new List<Line>();
                foreach (var raw in Text.SplitLines(plain))
                {
                    var t = raw.Trim();
                    if (t.Length > 0) lines.Add(new Line(0, t, []));
                }
                return new Doc(trackId, false, lines, SyncKind.Unsynced, "lrclib");
            }
        }
    }

    // ── 5. the aggregator ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fans out to every enabled source in parallel, cleans each at ONE chokepoint, picks the Spotify-native
    /// candidate as the reranker's reference, and returns the single best document. NOT first-hit: a later word-synced
    /// candidate can still beat an earlier line-synced one, and lands as an UPGRADE. A per-source miss, timeout or
    /// throw degrades to null for that source and never fails the aggregate.</summary>
    public sealed class Aggregator
    {
        /// <summary>An UNSYNCED first hit buys a longer grace window: a plain-text lyric is barely better than none,
        /// and a synced source arriving a second later changes the whole surface.</summary>
        const int UnsyncedFirstHitGraceMs = 6000;

        /// <summary>Bound the winner cache: a long session touches thousands of distinct tracks and each document is
        /// tens of KB. A miss re-fetches (self-healing), so an LRU cap is safe.</summary>
        const int CacheCap = 64;

        /// <summary>ONE monotone "how good is this document" key: richness tier first, then — inside the syllable tier
        /// only — syllable count. Promotion and the never-downgrade disk-write guard both order by it, so the two can
        /// never disagree about which of two documents is better.</summary>
        const long GradeTier = 1_000_000L;

        readonly IReadOnlyList<ISource> _sources;
        readonly Func<string, CancellationToken, Task<Request?>> _resolve;
        readonly Options _opt;
        readonly string _referenceSourceId;
        readonly DiskCache? _disk;

        readonly Dictionary<string, Doc> _cache = new(StringComparer.Ordinal);
        // ONE shared fetch per track id: the rail panel and the immersive surface each mount their own doc host, so
        // opening the immersive surface asks for the SAME track twice, concurrently — which used to mean two full
        // fan-outs plus two racing disk writes.
        readonly Dictionary<string, Task<Doc?>> _inFlight = new(StringComparer.Ordinal);
        // What the DISK is known to hold for a track, as the same monotone grade the promotion ladder uses. Seeded by
        // a read-through hit and updated by every write we issue, so a save can never DOWNGRADE a better persisted
        // document.
        readonly Dictionary<string, long> _diskGrade = new(StringComparer.Ordinal);
        readonly List<string> _lru = [];
        readonly Lock _gate = new();

        /// <summary>Raised (off the UI thread) when a background pass promotes a richer document for a track that
        /// already has one. `Store` marshals it.</summary>
        public event Action<Doc>? Upgraded;

        public Aggregator(
            IEnumerable<ISource> sources,
            Func<string, CancellationToken, Task<Request?>> resolveRequest,
            Options? options = null,
            string referenceSourceId = "spotify",
            DiskCache? diskCache = null)
        {
            var list = new List<ISource>();
            foreach (var s in sources) if (s.Enabled) list.Add(s);
            _sources = list;
            _resolve = resolveRequest;
            _opt = options ?? Options.Default;
            _referenceSourceId = referenceSourceId;
            _disk = diskCache;
        }

        readonly record struct Probed(Candidate? Cand, Outcome Outcome, long Ms, string Detail);

        static readonly Dictionary<string, Probed> EmptyProbed = new(StringComparer.Ordinal);

        // An exact-recording word-synced lyric (matched by identity or ISRC) is the best result possible — nothing a
        // slower source could return beats it, so the moment one arrives we stop waiting.
        static bool IsGold(Candidate? c)
            => c is { Sync: SyncKind.Syllable, Basis: MatchBasis.Identity or MatchBasis.Isrc };

        void TouchLru(string id) { _lru.Remove(id); _lru.Add(id); }

        void EvictLru()
        {
            while (_lru.Count > CacheCap)
            {
                var oldest = _lru[0];
                _lru.RemoveAt(0);
                _cache.Remove(oldest);
                _diskGrade.Remove(oldest);
            }
        }

        /// <summary>The one entry point. Returns the winner, or null when nothing anywhere has lyrics.
        ///
        /// <para>CANCELLATION CONTRACT of the shared fetch: the work itself runs on <c>CancellationToken.None</c> and
        /// is never cancelled by any caller. It exists to POPULATE the caches, so the first caller walking away (a rail
        /// panel unmounting the instant the immersive surface takes over) must not cancel the second caller's lyrics,
        /// and a finished-but-unobserved fan-out is still worth persisting. Each caller observes its OWN token through
        /// <c>WaitAsync</c>.</para></summary>
        public async Task<Doc?> GetAsync(string trackId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(trackId)) return null;

            Task<Doc?> shared;
            TaskCompletionSource<Doc?>? owner = null;
            lock (_gate)
            {
                if (_cache.TryGetValue(trackId, out var cached)) { TouchLru(trackId); return cached; }
                if (_inFlight.TryGetValue(trackId, out var running)) shared = running;
                else
                {
                    owner = new TaskCompletionSource<Doc?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _inFlight[trackId] = shared = owner.Task;
                }
            }
            // Started OUTSIDE the lock so a fan-out that happens to complete synchronously cannot run under the gate.
            if (owner is not null) _ = RunSharedAsync(trackId, owner);
            return await shared.WaitAsync(ct).ConfigureAwait(false);
        }

        /// <summary>What the memory cache already holds for a track, without starting anything. The Store's hot read.</summary>
        public Doc? Peek(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return null;
            lock (_gate) return _cache.TryGetValue(trackId, out var d) ? d : null;
        }

        async Task RunSharedAsync(string trackId, TaskCompletionSource<Doc?> tcs)
        {
            try { tcs.TrySetResult(await FetchAndCacheAsync(trackId).ConfigureAwait(false)); }
            catch (Exception e)
            {
                tcs.TrySetException(e);
                _ = tcs.Task.Exception;   // observe it: every caller may already have walked away on its own token
            }
            finally { lock (_gate) _inFlight.Remove(trackId); }
        }

        async Task<Doc?> FetchAndCacheAsync(string trackId)
        {
            // Read-through, BEFORE resolve and before the fan-out: resolving can itself hit the network, so a disk hit
            // must short-circuit both. This is the whole point of the disk cache — lyrics for a previously-played track
            // with no network at all.
            if (_disk is { } disk)
            {
                var entry = await disk.TryLoadAsync(trackId, CancellationToken.None).ConfigureAwait(false);
                if (entry.Outcome == CacheOutcome.Hit && entry.Document is { } loaded)
                {
                    // A cached document NEVER passes through the reranker, so the word-timing gate would miss it
                    // entirely — including every entry an older build persisted before the gate existed. Without this
                    // repair a track whose broken karaoke was cached once keeps serving it forever.
                    bool unsingable = loaded.Sync == SyncKind.Syllable
                        && Timing.HasImplausibleWordTiming(loaded, out _, out _);
                    var fromDisk = unsingable ? Timing.StripWordTiming(loaded) : loaded;
                    // Same self-heal for junk lines. The header rule is metadata-driven and a disk hit deliberately
                    // never resolves the track, so it is skipped here — the other two families are metadata-free and
                    // cover the rows that actually render.
                    var swept = Clean.Apply(fromDisk);
                    int junk = fromDisk.Lines.Count - swept.Lines.Count;
                    if (swept.Lines.Count > 0 && junk > 0) fromDisk = swept;
                    else junk = 0;
                    // Overwrite the file DIRECTLY, not through the never-downgrade guard: this is the one case where a
                    // "downgrade" is the correction, and the guard exists to stop exactly the write we want here.
                    if (unsingable || junk > 0)
                    {
                        disk.Save(trackId, fromDisk);
                        string why = unsingable && junk > 0 ? $"unsingable word timing + {junk} non-lyric line(s)"
                            : unsingable ? "unsingable word timing (stripped to line sync)"
                            : $"{junk} non-lyric line(s)";
                        Log.Info(Diag.Category, $"repaired the cached document for {trackId} — {why} — and re-persisted it");
                    }
                    lock (_gate)
                    {
                        _cache[trackId] = fromDisk; TouchLru(trackId);
                        _diskGrade[trackId] = Grade(fromDisk);   // what the file now holds — no save may go below it
                        EvictLru();
                    }
                    PublishDiskReport(trackId, fromDisk, entry.SavedAtUnixMs);
                    // A POSITIVE entry has no TTL by design, so without this a low-richness document cached in an
                    // earlier session would be PERMANENT: the read-through short-circuits resolve + fan-out + upgrade
                    // forever and the track could never reach syllable lyrics. Serve the cached document immediately
                    // (the offline promise is untouched) and, when it is not already at the top of the ladder, run the
                    // ordinary resolve + fan-out + upgrade in the BACKGROUND.
                    if (Authority.Richness(fromDisk) < 3) _ = UpgradeDiskHitAsync(trackId, fromDisk);
                    return fromDisk;
                }
                if (entry.Outcome == CacheOutcome.KnownMissing)
                {
                    PublishDiskReport(trackId, null, entry.SavedAtUnixMs);
                    return null;   // TTL-bounded: after it expires the very same call fans out again
                }
            }

            Request? req;
            try { req = await _resolve(trackId, CancellationToken.None).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { req = null; }
            if (req is null)
            {
                Diag.Publish(new SearchReport(trackId, "", "", "", 0L, null,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    "could not resolve track metadata — no title/artist to search with", []));
                PublishInspection(trackId, EmptyProbed, null, null,
                    "no request was ever built — the track's metadata could not be resolved");
                return null;
            }

            // Ambient probe: flows (AsyncLocal) into each parallel source task so a source can record WHY it missed.
            var probe = new Probe();
            Probe.Current.Value = probe;

            // Fan out in parallel. The UI waits only for the short first-hit grace window; slower sources keep running
            // in the background and can publish a richer replacement without delaying the initial lyric.
            long startedAt = Stopwatch.GetTimestamp();
            var srcCts = new CancellationTokenSource();
            var started = new List<(ISource Source, Task<Probed> Task)>(_sources.Count);
            for (int i = 0; i < _sources.Count; i++)
                started.Add((_sources[i], FetchOne(_sources[i], req, probe, srcCts.Token)));
            var collected = new Dictionary<string, Probed>(StringComparer.Ordinal);

            var pending = new List<(ISource Source, Task<Probed> Task)>(started);
            Task? grace = null;
            bool graceFromUnsynced = false;
            bool goldCollected = false;
            while (pending.Count > 0)
            {
                var waiters = new List<Task>(pending.Count + 1);
                foreach (var p in pending) waiters.Add(p.Task);
                if (grace is not null) waiters.Add(grace);

                var done = await Task.WhenAny(waiters).ConfigureAwait(false);
                if (ReferenceEquals(done, grace)) break;   // grace elapsed → stop waiting for slow stragglers

                int idx = pending.FindIndex(p => ReferenceEquals(p.Task, done));
                var entry = pending[idx];
                pending.RemoveAt(idx);
                var pr = await entry.Task.ConfigureAwait(false);   // FetchOne never throws
                collected[entry.Source.Id] = pr;
                if (grace is null && pr.Cand is not null)
                {
                    graceFromUnsynced = pr.Cand.Sync == SyncKind.Unsynced;
                    grace = Task.Delay(InitialGraceMs(pr.Cand));
                }
                else if (graceFromUnsynced && pr.Cand is { Sync: not SyncKind.Unsynced })
                {
                    graceFromUnsynced = false;
                    grace = Task.Delay(Math.Clamp(_opt.FirstHitGraceMs, 0, int.MaxValue));
                }
                if (IsGold(pr.Cand)) goldCollected = true;
                if (goldCollected && (collected.ContainsKey(_referenceSourceId) || !HasPending(pending, _referenceSourceId)))
                    break;   // gold is unbeatable, but keep the reference when it is already nearly here
            }
            bool continueInBackground = pending.Count > 0;

            var candidates = CandidatesOf(collected);
            var reference = ReferenceOf(candidates);
            TrimEdgesAgainstReference(candidates, collected, reference);
            Ranked ranked = candidates.Count > 0 ? Reranker.Rank(candidates, reference) : new Ranked(null, null, []);

            PublishReport(trackId, req, collected, ranked, candidates,
                continueInBackground ? "" : "");
            LogDecision(trackId, ranked, candidates);

            var winner = ranked.Winner;
            PublishInspection(trackId, collected, probe, winner,
                continueInBackground ? "first pass — slower sources are still running in the background" : "complete");
            if (winner is not null) lock (_gate) { _cache[trackId] = winner; TouchLru(trackId); EvictLru(); }
            // ONE writer per request. Both saves are fire-and-forget, so issuing the winner write here AND the upgrade
            // write from the continuation left two unordered file writes racing — the worse document could land last.
            // When a continuation is going to run it therefore OWNS the write and persists once, with the best document
            // it ends up holding (its finally writes even on a cancel, so a skipped winner save can never be lost).
            bool willContinue = continueInBackground && winner is not null && Authority.Richness(winner) < 3;
            if (_disk is { } wdisk)
            {
                if (winner is not null && !willContinue) SaveToDiskIfBetter(trackId, winner);
                // The negative marker is written ONLY when every source actually ran and none produced a candidate —
                // never when the grace window cut a still-running fan-out short, and never when the reranker merely
                // rejected what it was given.
                else if (winner is null && candidates.Count == 0 && collected.Count > 0 && !continueInBackground)
                    wdisk.SaveMissing(trackId);
            }
            if (willContinue)
                _ = ContinueForUpgradeAsync(trackId, req, srcCts, pending, collected, winner!, startedAt, probe);
            else
            {
                srcCts.Cancel();
                srcCts.Dispose();
            }
            return winner;
        }

        static bool HasPending(List<(ISource Source, Task<Probed> Task)> pending, string id)
        {
            for (int i = 0; i < pending.Count; i++) if (pending[i].Source.Id == id) return true;
            return false;
        }

        static List<Candidate> CandidatesOf(Dictionary<string, Probed> collected)
        {
            var list = new List<Candidate>(collected.Count);
            foreach (var pr in collected.Values) if (pr.Cand is { } c) list.Add(c);
            return list;
        }

        Doc? ReferenceOf(List<Candidate> candidates)
        {
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i].ProviderId == _referenceSourceId) return candidates[i].Document;
            return null;
        }

        /// <summary>Tier 2 of the credit rules, which can only run once the reference is in hand: a search-matched
        /// candidate's leading/trailing lines that align to nothing the reference sings are padding — the credits the
        /// grammar tier could not name, in whatever language. The reference itself and the identity/ISRC-matched
        /// candidates are left alone: they ARE the exact recording, and a fuzzy alignment is not allowed to cut a
        /// trusted document.</summary>
        void TrimEdgesAgainstReference(List<Candidate> candidates, Dictionary<string, Probed> collected, Doc? reference)
        {
            if (reference is null) return;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.ProviderId == _referenceSourceId || c.Basis is MatchBasis.Identity or MatchBasis.Isrc) continue;
                var trimmed = CreditRules.TrimUnalignedEdges(c.Document, reference, out int leading, out int trailing);
                if (ReferenceEquals(trimmed, c.Document)) continue;
                candidates[i] = c with { Document = trimmed };
                if (collected.TryGetValue(c.ProviderId, out var pr)) collected[c.ProviderId] = pr with { Cand = candidates[i] };
                Probe.Note(c.ProviderId,
                    $"dropped {leading + trailing} edge line(s) that align to nothing in the reference ({leading} leading, {trailing} trailing)");
            }
        }

        /// <summary>Background half of a LOW-RICHNESS disk hit: resolve and fan out exactly like a cold request, then
        /// hand the still-running sources to the SAME continuation the live path uses, with the cached document as the
        /// incumbent. A richer winner promotes, publishes and re-persists; anything else changes nothing. Offline (no
        /// resolution) it is a no-op.</summary>
        async Task UpgradeDiskHitAsync(string trackId, Doc fromDisk)
        {
            CancellationTokenSource? owned = null;
            try
            {
                Request? req;
                try { req = await _resolve(trackId, CancellationToken.None).ConfigureAwait(false); }
                catch { req = null; }
                if (req is null) return;

                var probe = new Probe();
                Probe.Current.Value = probe;
                long startedAt = Stopwatch.GetTimestamp();
                owned = new CancellationTokenSource();
                var pending = new List<(ISource Source, Task<Probed> Task)>(_sources.Count);
                for (int i = 0; i < _sources.Count; i++)
                    pending.Add((_sources[i], FetchOne(_sources[i], req, probe, owned.Token)));
                var srcCts = owned;
                owned = null;   // handed over: the continuation cancels and disposes it in its finally
                await ContinueForUpgradeAsync(trackId, req, srcCts, pending,
                    new Dictionary<string, Probed>(StringComparer.Ordinal), fromDisk, startedAt, probe).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Info(Diag.Category, $"background upgrade of the cached lyrics for {trackId} failed: {e.GetType().Name}");
            }
            finally { owned?.Dispose(); }
        }

        /// <summary>The ONE place a document reaches the disk cache. A save never DOWNGRADES: the grade of what the
        /// file holds is tracked per track and a write whose document is not strictly better is dropped. Without it a
        /// later line-only winner could overwrite the syllable document an earlier session had persisted.</summary>
        void SaveToDiskIfBetter(string trackId, Doc doc, bool allowSameGrade = false)
        {
            if (_disk is not { } disk) return;
            long grade = Grade(doc);
            lock (_gate)
            {
                if (_diskGrade.TryGetValue(trackId, out long known) && (allowSameGrade ? known > grade : known >= grade)) return;
                _diskGrade[trackId] = grade;
            }
            disk.Save(trackId, doc);
        }

        int InitialGraceMs(Candidate candidate)
        {
            int normal = Math.Clamp(_opt.FirstHitGraceMs, 0, int.MaxValue);
            return candidate.Sync == SyncKind.Unsynced ? Math.Max(normal, UnsyncedFirstHitGraceMs) : normal;
        }

        async Task ContinueForUpgradeAsync(
            string trackId,
            Request req,
            CancellationTokenSource srcCts,
            List<(ISource Source, Task<Probed> Task)> pending,
            Dictionary<string, Probed> collected,
            Doc initialWinner,
            long startedAt,
            Probe probe)
        {
            // The document this track must END UP persisted with. The caller deliberately skips its own winner save
            // when it spawns us, so the single write in the finally below is the only one — and it has to happen even
            // when the continuation is cancelled or finds nothing better.
            Doc bestDoc = initialWinner;
            bool replacedUnverified = false;   // bestDoc is a same-grade replacement of an uncorroborated incumbent
            try
            {
                long elapsed = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                long remaining = _opt.TotalTimeoutMs - elapsed;
                if (remaining <= 0) return;

                Task budget = Task.Delay((int)Math.Min(int.MaxValue, remaining), srcCts.Token);
                while (pending.Count > 0)
                {
                    var waiters = new List<Task>(pending.Count + 1);
                    foreach (var p in pending) waiters.Add(p.Task);
                    waiters.Add(budget);

                    var done = await Task.WhenAny(waiters).ConfigureAwait(false);
                    if (ReferenceEquals(done, budget)) break;

                    int idx = pending.FindIndex(p => ReferenceEquals(p.Task, done));
                    if (idx < 0) continue;
                    var entry = pending[idx];
                    pending.RemoveAt(idx);
                    var pr = await entry.Task.ConfigureAwait(false);
                    collected[entry.Source.Id] = pr;

                    if (IsGold(pr.Cand) && (collected.ContainsKey(_referenceSourceId) || !HasPending(pending, _referenceSourceId)))
                        break;
                }

                var candidates = CandidatesOf(collected);
                var reference = ReferenceOf(candidates);
                TrimEdgesAgainstReference(candidates, collected, reference);
                Ranked ranked = candidates.Count > 0 ? Reranker.Rank(candidates, reference) : new Ranked(null, null, []);

                PublishReport(trackId, req, collected, ranked, candidates, "background complete");
                LogDecision(trackId, ranked, candidates);

                // Final = what the UI is left holding, which is the INCUMBENT unless this pass actually beat it. Two
                // ways a challenger legitimately replaces it: (1) it is a richer document outright (the ordinary
                // promotion path), or (2) tier alone never separated them but the incumbent itself was never
                // corroborated — unverified, or zero text agreement with the reference — while THIS pass's winner is
                // verified and scores higher. (2) is the decoy's other escape hatch: an incumbent that was cached or
                // promoted before the stage-one text floor existed must not survive forever just because nothing in
                // this pass out-tiers it.
                var winner = ranked.Winner;
                bool richer = winner is not null && Grade(winner) > Grade(initialWinner);
                bool replacesUnverifiedIncumbent = false;
                if (!richer && winner is not null && ranked.Best is { Verified: true } bestDec)
                {
                    Decision? incumbentDec = null;
                    for (int i = 0; i < ranked.All.Count; i++)
                        if (ranked.All[i].ProviderId == initialWinner.Provider) { incumbentDec = ranked.All[i]; break; }
                    bool incumbentWeak = incumbentDec is null || !incumbentDec.Verified || incumbentDec.TextAgreement <= 0;
                    replacesUnverifiedIncumbent = incumbentWeak && bestDec.Score > (incumbentDec?.Score ?? double.NegativeInfinity);
                }
                if (!richer && !replacesUnverifiedIncumbent)
                {
                    PublishInspection(trackId, collected, probe, initialWinner,
                        "background pass complete — nothing beat the first winner");
                    return;
                }
                PublishInspection(trackId, collected, probe, winner!,
                    richer
                        ? "background pass complete — it promoted a richer document"
                        : "background pass complete — it replaced the unverified first winner");
                bestDoc = winner!;
                replacedUnverified = !richer;

                bool promoted = false;
                lock (_gate)
                {
                    // A same-grade replacement of an unverified incumbent is not "richer", so a richness gate alone
                    // would drop it here: the UI would keep the decoy and the upgrade would never fire. It may replace
                    // exactly the incumbent it was judged against — never a document a concurrent pass has already
                    // promoted past it.
                    if (!_cache.TryGetValue(trackId, out var current) || Grade(winner!) > Grade(current)
                        || (replacesUnverifiedIncumbent && ReferenceEquals(current, initialWinner)))
                    {
                        _cache[trackId] = winner!;
                        TouchLru(trackId);
                        EvictLru();
                        promoted = true;
                    }
                }

                if (promoted) Upgraded?.Invoke(winner!);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Log.Info(Diag.Category, $"background lyrics upgrade failed for {trackId}: {e.GetType().Name}");
            }
            finally
            {
                // Write-through, ONCE, with the best document — so disk holds the best doc and never a downgrade of
                // it. A same-grade replacement of an unverified incumbent must still overwrite it.
                SaveToDiskIfBetter(trackId, bestDoc, allowSameGrade: replacedUnverified);
                srcCts.Cancel();
                srcCts.Dispose();
            }
        }

        void PublishReport(
            string trackId,
            Request req,
            IReadOnlyDictionary<string, Probed> collected,
            Ranked ranked,
            IReadOnlyList<Candidate> candidates,
            string suffix)
        {
            string? winnerId = ranked.Best?.ProviderId;
            var traces = new List<SourceTrace>(_sources.Count);
            foreach (var s in _sources)
            {
                Decision? dec = null;
                for (int i = 0; i < ranked.All.Count; i++)
                    if (ranked.All[i].ProviderId == s.Id) { dec = ranked.All[i]; break; }
                if (collected.TryGetValue(s.Id, out var pr))
                    traces.Add(new SourceTrace(s.Id, pr.Outcome, pr.Ms, pr.Detail,
                        pr.Cand?.Sync ?? SyncKind.None, pr.Cand?.LineCount ?? 0,
                        dec?.Score ?? 0d, dec is not null && s.Id == winnerId, dec?.Reason ?? "",
                        dec?.TextAgreement ?? 0d, dec?.Coverage ?? 0d, dec?.TimingScore ?? 0d, dec?.SyncScore ?? 0d));
                else
                    traces.Add(new SourceTrace(s.Id, Outcome.Skipped, 0L,
                        suffix.Length > 0 ? suffix : "skipped — a faster match returned first",
                        SyncKind.None, 0, 0d, false, ""));
            }

            int hits = candidates.Count;
            int ran = collected.Count;
            string summary = hits == 0
                ? $"0/{ran} sources returned lyrics — no match anywhere"
                : ranked.Best is { } sb
                    ? $"{hits}/{ran} returned; winner={sb.ProviderId} ({sb.Sync}, score {sb.Score:F2}, offset {sb.AppliedOffsetMs}ms)"
                    : $"{hits}/{ran} returned";
            if (suffix.Length > 0) summary += $" — {suffix}";
            Diag.Publish(new SearchReport(
                trackId, req.Title, req.ArtistsJoined, req.Album, req.DurationMs, req.Isrc,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), summary, traces));
            LogReport(trackId, req, summary, traces);
        }

        /// <summary>Keep the "why did this song get these lyrics" surface honest on a disk hit: without a report the
        /// inspector would show nothing (or the previous session's stale entry) for a track that resolved instantly.
        /// The request metadata is empty BY CONSTRUCTION — a disk hit deliberately never resolves the track.</summary>
        void PublishDiskReport(string trackId, Doc? doc, long savedAtUnixMs)
        {
            string when = savedAtUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(savedAtUnixMs).UtcDateTime.ToString("u",
                    System.Globalization.CultureInfo.InvariantCulture)
                : "unknown";
            string detail = doc is not null
                ? "not queried — served from the local lyrics cache"
                : "not queried — the local lyrics cache remembers this track has no lyrics";
            var traces = new List<SourceTrace>(_sources.Count);
            foreach (var s in _sources)
                traces.Add(new SourceTrace(s.Id, Outcome.Skipped, 0L, detail, SyncKind.None, 0, 0d, false, ""));

            string summary = doc is not null
                ? $"served from the on-disk cache (saved {when}); winner={doc.Provider ?? "?"} ({doc.Sync}, {doc.Lines.Count} lines)"
                : $"no lyrics anywhere — cached negative result from {when}";
            Diag.Publish(new SearchReport(trackId, "", "", "", 0L, null,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), summary, traces));
            // Final only: a disk hit is by construction a NO-ROUND-TRIP answer, so there is no raw payload and no
            // losing candidate to show. The inspector says exactly that, and offers the re-fetch that produces both.
            PublishInspection(trackId, EmptyProbed, null, doc,
                doc is not null
                    ? $"served from the on-disk cache (saved {when}) — no provider was contacted, so there is no raw response this session. Use “Re-fetch from providers”."
                    : $"the on-disk cache remembers (since {when}) that this track has no lyrics anywhere — no provider was contacted.");
            Log.Debug(Diag.Category, $"track={trackId} {summary}");
        }

        /// <summary>Record the heavy half of this pass: every payload the probe captured and every candidate's PARSED
        /// document (winners and LOSERS alike — the losers are the whole point), plus the document the UI is left
        /// with. Cheap: the strings and documents already exist; this only keeps them reachable.</summary>
        void PublishInspection(string trackId, IReadOnlyDictionary<string, Probed> collected, Probe? probe, Doc? final, string note)
        {
            var candidates = new List<ParsedCandidate>(collected.Count);
            foreach (var pr in collected.Values)
                if (pr.Cand is { } c) candidates.Add(new ParsedCandidate(c.ProviderId, c.Basis, c.Prior, c.Document));

            Diag.PublishInspection(new Inspection(
                trackId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                note,
                probe?.RawPayloads() ?? [],
                candidates,
                final));
        }

        /// <summary>Forget ONE track everywhere this provider caches it — memory, the never-downgrade grade ledger and
        /// the on-disk entry — and fetch it again. The inspector's escape hatch: without it, a track played in any
        /// earlier session answers from disk with no round-trip, so there is no raw payload to inspect. Joins an
        /// already-running fan-out for the same track rather than starting a second one.</summary>
        public Task<Doc?> RefetchAsync(string trackId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(trackId)) return Task.FromResult<Doc?>(null);
            lock (_gate)
            {
                _cache.Remove(trackId);
                _lru.Remove(trackId);
                _diskGrade.Remove(trackId);   // the file is about to go, so what we knew about it goes too
            }
            _disk?.Forget(trackId);
            return GetAsync(trackId, ct);
        }

        /// <summary>Clear the winner cache (logout / provider-config change) — BOTH halves. "Clear" has to mean the
        /// next request re-fetches, so the persistent entries (INCLUDING the negative markers) go too; leaving them
        /// would make a post-logout request keep serving the pre-logout answer forever.</summary>
        public void ClearCache()
        {
            lock (_gate) { _cache.Clear(); _lru.Clear(); _diskGrade.Clear(); }
            _disk?.Clear();
        }

        void LogReport(string trackId, Request req, string summary, IReadOnlyList<SourceTrace> traces)
        {
            // Debug: the per-search + per-source trace lines are the bulk of the [lyrics] file volume. The single
            // final-winner line stays Info.
            if (!Log.IsEnabled(WaveeLogLevel.Debug)) return;

            Log.Debug(Diag.Category, $"search track={trackId} title=\"{LogValue(req.Title)}\" artist=\"{LogValue(req.ArtistsJoined)}\" "
                + $"album=\"{LogValue(req.Album)}\" duration={req.DurationMs}ms isrc={LogValue(req.Isrc ?? "-")} summary=\"{LogValue(summary)}\"");
            foreach (var t in traces)
                Log.Debug(Diag.Category, $"source track={trackId} id={t.SourceId} outcome={t.Outcome} elapsed={t.ElapsedMs}ms "
                    + $"sync={t.Sync} lines={t.LineCount} score={t.Score:F3} winner={t.Winner} detail=\"{LogValue(t.Detail)}\" "
                    + $"rerank=\"{LogValue(t.RerankReason)}\"");
        }

        static string LogValue(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');

        void LogDecision(string trackId, Ranked ranked, IReadOnlyList<Candidate> candidates)
        {
            if (ranked.Best is not { } b) return;
            var ids = new StringBuilder();
            for (int i = 0; i < candidates.Count; i++) { if (i > 0) ids.Append(','); ids.Append(candidates[i].ProviderId); }
            Log.Info(Diag.Category, $"track={trackId} winner={b.ProviderId} sync={b.Sync} score={b.Score:F3} "
                + $"text={b.TextAgreement:F2} timing={b.TimingScore:F2} offset={b.AppliedOffsetMs}ms candidates=[{ids}] ({b.Reason})");
        }

        static long Grade(Doc doc)
        {
            int r = Authority.Richness(doc);
            return r * GradeTier + (r >= 3 ? Math.Min(Authority.SyllableCount(doc), GradeTier - 1) : 0L);
        }

        async Task<Probed> FetchOne(ISource source, Request req, Probe probe, CancellationToken ct)
        {
            long t0 = Stopwatch.GetTimestamp();
            long Ms() => (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            string With(string head) { var n = probe.NotesFor(source.Id); return n.Length > 0 ? head + " — " + n : head; }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_opt.PerSourceTimeoutMs);
            Log.Debug(Diag.Category, $"source track={req.TrackId} id={source.Id} started");
            try
            {
                var c = await source.FetchAsync(req, cts.Token).ConfigureAwait(false);
                if (c is null) return new Probed(null, Outcome.Miss, Ms(), With("no match"));

                // THE CLEANING CHOKEPOINT. Every provider passes through here with the track's own metadata in scope,
                // and the document used as the reranker's REFERENCE is itself a candidate — so both sides of every
                // comparison are cleaned by one rule, and `coverage` finally counts lyrics rather than padding.
                var cleaned = Clean.Apply(c.Document, req.Title, req.ArtistsJoined, out int credits);
                int removed = c.Document.Lines.Count - cleaned.Lines.Count;
                if (cleaned.Lines.Count == 0)
                    return new Probed(null, Outcome.Miss, Ms(), With("every line was provider metadata or instrumental filler"));
                if (removed > 0)
                {
                    Probe.Note(source.Id, credits > 0
                        ? $"dropped {removed} non-lyric line(s) ({credits} credit header(s), {removed - credits} blank/♪/boilerplate/title header)"
                        : $"dropped {removed} non-lyric line(s) (blank/♪/boilerplate/title header)");
                    c = c with { Document = cleaned };
                }
                // DECOY gate — the anti-scraping tell (206 nonsense lines, every one exactly 4000 ms, running to 13:56
                // on a 3:30 track). Neither check is about whether the timing is SINGABLE (that is the word-timing gate
                // in the reranker); a decoy is line-synced and internally "consistent" by construction, which is
                // exactly why it used to sail through untouched. Checked HERE, not in the reranker: a decoy must never
                // even become a candidate — an ISRC basis makes it VERIFIED, and stage two picks the highest verified
                // tier ignoring score, so a decoy that got this far would win outright.
                if (Timing.ExceedsTrackDuration(cleaned, req.DurationMs))
                    return new Probed(null, Outcome.Miss, Ms(),
                        With($"decoy: runs to {Diag.Ts(cleaned.Lines[^1].EndMs ?? cleaned.Lines[^1].StartMs)} on a "
                            + $"{Diag.Ts(req.DurationMs)} track"));
                if (Timing.HasUniformLineDurations(cleaned))
                    return new Probed(null, Outcome.Miss, Ms(),
                        With($"decoy: {cleaned.Lines.Count} lines all exactly identical timing steps — uniform decoy timing"));
                return new Probed(c, Outcome.Hit, Ms(), With($"{c.Sync}, {c.LineCount} lines, basis={c.Basis}"));
            }
            catch (OperationCanceledException)
            {
                // Our per-source CancelAfter fired = a real timeout; otherwise the aggregate cancelled us.
                bool timedOut = cts.IsCancellationRequested && !ct.IsCancellationRequested;
                return new Probed(null, timedOut ? Outcome.Timeout : Outcome.Skipped, Ms(),
                    timedOut ? With($"timed out (> {_opt.PerSourceTimeoutMs}ms)") : "cancelled");
            }
            catch (Exception e)
            {
                Log.Debug(Diag.Category, $"source {source.Id} failed for {req.TrackId}: {e.GetType().Name}");
                return new Probed(null, Outcome.Error, Ms(), With($"{e.GetType().Name}: {e.Message}"));
            }
        }
    }

    // ── 6. the UI's door: the per-track document store ──────────────────────────────────────────────────────────────

    /// <summary>The lyrics side table (ch 22 §7 gap 1): a managed object graph per track, so it cannot be an unmanaged
    /// column. Keyed by the base62 TRACK ID rather than an entity slot — a slot is scope-local and dies on a scope
    /// switch, while the id is already the aggregator's and the disk cache's key, so one key spans all three.
    ///
    /// <para>THE UI CONTRACT, in three calls: <see cref="Ensure"/> on the playing track (once, on the track change —
    /// never per frame, never per row), <see cref="Doc(Track)"/> inside a bound thunk that also reads
    /// <see cref="Changed"/>, and nothing else. A miss is a real answer: the peek renders NOTHING and the view renders
    /// its shimmer. Pages demand their whole model on mount, and this surface's whole model is one document.</para>
    ///
    /// <para>C1: every write to <see cref="Changed"/> and to the dictionary happens on the UI thread, through
    /// <see cref="ToUi"/>. The aggregator's threads only ever POST.</para></summary>
    public static class Store
    {
        /// <summary>How the host hops a completed fetch back to the UI thread. Installed by `Shell.Host.cs` at
        /// composition; the identity default lets a test drive the store synchronously.</summary>
        public static Action<Action> ToUi { get; set; } = static a => a();

        /// <summary>The live aggregator. Null until <see cref="Attach"/> — every read then answers "unknown", which is
        /// exactly what a backend with no lyrics provider can honestly say.</summary>
        public static Aggregator? Provider { get; private set; }

        static readonly Dictionary<string, Doc?> Docs = new(StringComparer.Ordinal);
        static readonly HashSet<string> Asked = new(StringComparer.Ordinal);

        /// <summary>Bumped once per publication. Read it in a bound thunk beside <see cref="Doc(Track)"/>; a bare
        /// document read subscribes to nothing.</summary>
        public static readonly Signal<uint> Changed = new(0u);

        /// <summary>Bumped when a BACKGROUND pass replaced the document a surface is already showing. The view keys
        /// its "hold the swap until the next handoff" rule off this rather than off <see cref="Changed"/>, which also
        /// fires for a first arrival.</summary>
        public static readonly Signal<uint> Upgraded = new(0u);

        /// <summary>Install the provider (composition root). Passing null detaches it — used by the fake backend and
        /// by logout.</summary>
        public static void Attach(Aggregator? provider)
        {
            if (Provider is { } old) old.Upgraded -= OnUpgraded;
            Provider = provider;
            if (provider is not null) provider.Upgraded += OnUpgraded;
            lock (Docs) { Docs.Clear(); Asked.Clear(); }
            Changed.Value++;
        }

        /// <summary>The base62 id a track's uri carries — the key everything here shares with the disk cache.</summary>
        public static string IdOf(Track track)
        {
            if (!track.IsValid) return "";
            var span = EntityUri.IdOf(track.Uri.Text);
            return span.IsEmpty ? "" : span.ToString();
        }

        /// <summary>What the store holds for this track, or null (not asked yet, still fetching, or a real miss).
        /// One dictionary read — safe from a bound thunk.</summary>
        public static Doc? Doc(Track track) => Doc(IdOf(track));

        /// <inheritdoc cref="Doc(Track)"/>
        public static Doc? Doc(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return null;
            lock (Docs) return Docs.TryGetValue(trackId, out var d) ? d : null;
        }

        /// <summary>Has this track been asked for yet? The view's "shimmer vs empty" discriminator: not asked ⇒ the
        /// surface has no opinion, asked-and-null ⇒ a real miss.</summary>
        public static bool Answered(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return false;
            lock (Docs) return Docs.ContainsKey(trackId);
        }

        /// <summary>Demand this track's lyrics. Idempotent per id: the second caller joins the ONE shared fetch the
        /// first started. Call it on a TRACK CHANGE, never per frame.</summary>
        public static void Ensure(Track track) => Ensure(IdOf(track));

        /// <inheritdoc cref="Ensure(Track)"/>
        public static void Ensure(string trackId)
        {
            if (string.IsNullOrEmpty(trackId) || Provider is not { } provider) return;
            lock (Docs)
            {
                if (Docs.ContainsKey(trackId) || !Asked.Add(trackId)) return;
            }
            // The memory cache may already hold it (a re-play inside the session): take that synchronously so the
            // surface never shimmers for a document it already has.
            if (provider.Peek(trackId) is { } warm) { Commit(trackId, warm, upgrade: false); return; }
            _ = FetchAsync(provider, trackId);
        }

        static async Task FetchAsync(Aggregator provider, string trackId)
        {
            Doc? doc = null;
            try { doc = await provider.GetAsync(trackId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { Log.Debug(Diag.Category, $"lyrics fetch for {trackId} failed: {e.GetType().Name}"); }
            Commit(trackId, doc, upgrade: false);
        }

        static void OnUpgraded(Doc doc) => Commit(doc.TrackId, doc, upgrade: true);

        static void Commit(string trackId, Doc? doc, bool upgrade)
        {
            ToUi(() =>
            {
                lock (Docs) Docs[trackId] = doc;
                // Derived facts live on the model: the capability bits are computed at COMMIT, never scanned by the
                // UI, and the secondary-line toggle is composed only when they are non-zero.
                if (doc is not null) Prefs.Available.Value = doc.SecondaryAvailable;
                Changed.Value++;
                if (upgrade) Upgraded.Value++;
            });
        }

        /// <summary>Drop everything (logout, a scope switch, a provider change).</summary>
        public static void Clear()
        {
            lock (Docs) { Docs.Clear(); Asked.Clear(); }
            Provider?.ClearCache();
            Prefs.Available.Value = 0;
            Changed.Value++;
        }

        /// <summary>Re-fetch one track from the providers, bypassing every cache — the inspector's escape hatch.</summary>
        public static void Refetch(string trackId)
        {
            if (string.IsNullOrEmpty(trackId) || Provider is not { } provider) return;
            lock (Docs) { Docs.Remove(trackId); Asked.Remove(trackId); }
            Changed.Value++;
            _ = RefetchAsync(provider, trackId);
        }

        static async Task RefetchAsync(Aggregator provider, string trackId)
        {
            Doc? doc = null;
            try { doc = await provider.RefetchAsync(trackId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { Log.Debug(Diag.Category, $"lyrics re-fetch for {trackId} failed: {e.GetType().Name}"); }
            lock (Docs) Asked.Add(trackId);
            Commit(trackId, doc, upgrade: false);
        }
    }

    // ── 6b. the request resolver: track id → Request, without a poll (G-252) ─────────────────────────────────────────
    //
    //   aggregator worker ─ ResolveRequest(id) ─ ToUi ─▶ ready now? ─ yes ─▶ answer
    //                                                        │ no: Ensure(Identity) once, PARK on Requests
    //   UI thread: Entities.Publish() ─▶ Requests.AfterPublish() ─▶ parked row knows its identity? ─▶ answer
    //   worker: no answer within RequestTimeoutMs (or the caller cancels) ─▶ null, and the parked entry is released
    //
    // The old resolver re-posted a probe every 50 ms for up to 4 s (a TCS, a closure, a publish-wrapped post and a
    // Task.Delay each), waking the UI loop ~20×/s while a lookup waited. Now a wait costs nothing until a publication.

    /// <summary>How long a resolve waits for its row's identity group before answering null — the aggregator's worker must
    /// eventually get an answer either way.</summary>
    public const int RequestTimeoutMs = 4_000;

    /// <summary>The ONE waiter the resolver parks on. UI thread only (C1); the host calls <see cref="RequestWaiter.AfterPublish"/>
    /// right after every <c>Entities.Publish()</c>.</summary>
    public static readonly RequestWaiter Requests = new();

    /// <summary>The <c>resolveRequest</c> half of <see cref="Boot"/> (G-008, G-252): base62 track id →
    /// <see cref="Request"/>. Entity columns are single-writer on the UI thread (C1), so the read, the one
    /// <c>Entities.Ensure</c> and the park hop through <see cref="Store.ToUi"/>; a row whose identity has not landed is
    /// completed by the publication that commits it (<see cref="Requests"/>). Null after <see cref="RequestTimeoutMs"/>;
    /// the caller's token is honoured. The pure mapping is <see cref="RequestFrom"/>.</summary>
    public static async Task<Request?> ResolveRequest(string trackId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(trackId) || !Base62.TryDecode(trackId.AsSpan(), out UInt128 gid)) return null;
        EntityId id = EntityId.ForGid(EntityKind.Track, gid);
        if (!id.IsValid) return null;
        ct.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<Request?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Store.ToUi(() =>
        {
            if (Entities.Current is not null)
            {
                Track track = Entities.Track(id);
                if (!track.Knows(TrackFields.Identity)) Entities.Ensure(track, TrackFields.Identity, FetchPriority.Visible);
            }
            Requests.Begin(id, trackId, completion);
        });
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromMilliseconds((double)RequestTimeoutMs), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            completion.TrySetResult(null);   // releases the parked entry: the next look prunes it
            return null;
        }
        catch (OperationCanceledException)
        {
            completion.TrySetResult(null);
            throw;
        }
    }

    /// <summary>A small bounded list of resolves waiting for their row's <see cref="TrackFields.Identity"/> group, checked
    /// after a publication instead of polled. UI THREAD ONLY (C1): every member touches entity columns.
    /// <para>Rows are held by <see cref="EntityId"/>, never by slot — a slot is scope-local, and a scope switch between the
    /// park and the publication must not read another row. An entry whose completion is already done (the resolver timed
    /// out, the caller cancelled) is dropped at the next look; a full list completes its OLDEST with null.</para></summary>
    public sealed class RequestWaiter
    {
        /// <summary>The most resolves parked at once. The aggregator's demand queue is latest-wins, so a handful is the
        /// real ceiling; past it the oldest gives way.</summary>
        public const int Capacity = 16;

        struct Pending
        {
            public EntityId Id;
            public string TrackId;
            public TaskCompletionSource<Request?> Completion;
        }

        readonly Pending[] _pending = new Pending[Capacity];
        int _count;
        uint _lookedAt;

        /// <summary>Resolves parked right now.</summary>
        public int PendingCount => _count;

        /// <summary>Answer at once when the row already knows its identity; otherwise PARK until a publication commits it.
        /// The caller asks for the identity group itself (the resolver's one <c>Entities.Ensure</c>).</summary>
        public void Begin(EntityId id, string trackId, TaskCompletionSource<Request?> completion)
        {
            if (completion.Task.IsCompleted) return;
            if (Entities.Current is null) { completion.TrySetResult(null); return; }
            if (RequestFrom(Entities.Track(id), trackId) is { } ready) { completion.TrySetResult(ready); return; }

            Prune();
            if (_count == Capacity)
            {
                _pending[0].Completion.TrySetResult(null);
                Array.Copy(_pending, 1, _pending, 0, Capacity - 1);
                _count--;
            }
            _pending[_count++] = new Pending { Id = id, TrackId = trackId, Completion = completion };
            _lookedAt = 0;   // the next publication looks, whatever its number
        }

        /// <summary>Right after <c>Entities.Publish()</c>: complete every parked resolve whose row now knows its identity.
        /// One compare when nothing is parked or nothing was published since the last look; allocation-free.</summary>
        public void AfterPublish()
        {
            if (_count == 0) return;
            uint publication = Entities.Publication;
            if (publication == _lookedAt) return;
            _lookedAt = publication;
            bool scope = Entities.Current is not null;
            int kept = 0;
            for (int i = 0; i < _count; i++)
            {
                var p = _pending[i];
                if (p.Completion.Task.IsCompleted) continue;
                if (scope && RequestFrom(Entities.Track(p.Id), p.TrackId) is { } ready)
                {
                    p.Completion.TrySetResult(ready);
                    continue;
                }
                _pending[kept++] = p;
            }
            Array.Clear(_pending, kept, _count - kept);
            _count = kept;
        }

        void Prune()
        {
            int kept = 0;
            for (int i = 0; i < _count; i++)
                if (!_pending[i].Completion.Task.IsCompleted) _pending[kept++] = _pending[i];
            Array.Clear(_pending, kept, _count - kept);
            _count = kept;
        }
    }

    // ── 7. composition ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Build the production stack and attach it. Called once from the composition root; `--fake` calls
    /// <see cref="Store.Attach"/> with null (or a seeded aggregator) instead.</summary>
    /// <param name="resolveRequest">Track id → the metadata the metadata-matched sources search with. Owner P's
    /// entity layer supplies it; it may hit the network, which is why the disk read-through runs BEFORE it.</param>
    /// <param name="spotifyGet">An authed GET against the spclient, or null to run without the reference source —
    /// which costs the reranker its reference and is a real, handled state, not an error.</param>
    /// <param name="spotifyBaseUrl">The resolved spclient base url.</param>
    public static Aggregator Boot(
        Func<string, CancellationToken, Task<Request?>> resolveRequest,
        Func<string, CancellationToken, Task<string?>>? spotifyGet = null,
        Func<string>? spotifyBaseUrl = null,
        Options? options = null,
        DiskCache? diskCache = null)
    {
        var http = new HttpFetch();
        var sources = new List<ISource> { new Sources.Amll(http), new Sources.LrcLib(http) };
        if (spotifyGet is not null && spotifyBaseUrl is not null)
            sources.Insert(0, new Sources.SpotifyNative(spotifyGet, spotifyBaseUrl));

        var aggregator = new Aggregator(sources, resolveRequest, options ?? Options.Default,
            referenceSourceId: "spotify", diskCache: diskCache ?? new DiskCache());
        Store.Attach(aggregator);
        Log.Info(Diag.Category, $"lyrics stack booted with {sources.Count} source(s)");
        return aggregator;
    }
}
