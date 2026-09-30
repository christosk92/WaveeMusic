// ── Shell/Lyrics.Grey.cs ───────────────────────────────────────────────────────────────────────────────────────────
// what the four grey sources share: the per-source guard (negative cache + circuit breaker), its persisted miss
// ledger, and the keyword search ladder
//
// Role: SHELL
// Spec: docs/plans/wavee/lyrics-grey-sources-implementation.md (W1-C, frozen contracts)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The grey sources (kugou, qq, netease, musixmatch — `Lyrics.Grey.*.cs`) are unofficial endpoints searched by
// metadata. Three things keep them from costing more than they give:
//
//   `SourceGuard`   one per source (`SourceGuard.For(id)`). A per-track NEGATIVE CACHE ("song not found" 6 h, "song
//                   found, no lyric" 24 h — transport errors are never cached) and a CIRCUIT BREAKER (3 consecutive
//                   transport failures → open 10 min; a source can also Trip it itself: a Musixmatch captcha, a quota,
//                   NetEase risk control). An open breaker or a cached miss answers null before a request goes out.
//   `MissLedger`    the negative cache's store, PERSISTED to `%LOCALAPPDATA%\Wavee\lyrics\source-misses.json`: a disk
//                   hit with line lyrics re-runs the fan-out on every play (the upgrade path), so a memory-only miss
//                   would re-query all four grey sources for every cached track every session. Loaded lazily once,
//                   TTL-pruned on load, capped, written debounced and off the caller's thread, atomically.
//   `GreyLadder`    the shared search: walk `Query.Variants(req)`, score every hit with `MetadataMatch`, pool the hits
//                   across variants, stop early on a VeryHigh/Perfect hit, hand back at most two hits ≥ Medium.
//
// Pure where it can be: the guard and the ledger take their clock, and the ledger its path, so the tests drive them
// with a fake clock and a temp folder and never touch the real profile.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee;

public static partial class Lyrics
{
    // ── 1. the per-source guard ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One grey source's negative cache and circuit breaker. Thread-safe. A source asks
    /// <see cref="IsOpen"/> and <see cref="TryGetMiss"/> first and returns null (after a <see cref="Probe.Note"/>) when
    /// either says so; it reports <see cref="Miss"/> for a real "not there", <see cref="Failure"/> for a transport
    /// error and <see cref="Success"/> for any answered request.
    /// <para>A cancellation is NEVER a <see cref="Failure"/>: the per-source timeout and the aggregator's gold cancel both
    /// arrive as <see cref="OperationCanceledException"/>, and neither says anything about the host's health.</para></summary>
    public sealed class SourceGuard
    {
        public enum MissKind : byte { SongNotFound, NoLyricForSong }

        /// <summary>Consecutive transport failures that open the breaker.</summary>
        public const int FailuresToOpen = 3;

        /// <summary>How long a failure-opened breaker stays open.</summary>
        public const long FailureOpenMs = 10 * 60_000L;

        const long HourMs = 3_600_000L;

        static readonly ConcurrentDictionary<string, SourceGuard> Registry = new(StringComparer.Ordinal);
        static MissLedger? s_shared;
        static readonly Lock SharedGate = new();

        readonly Func<long> _nowMs;
        readonly MissLedger _ledger;
        readonly Lock _gate = new();
        int _failures;
        long _openUntilMs;
        string _openReason = "";

        /// <summary>A guard with an IN-MEMORY miss store (nothing persisted).</summary>
        public SourceGuard(string sourceId, Func<long> nowMs)
            : this(sourceId, nowMs, new MissLedger(null, nowMs)) { }

        /// <summary>A guard over <paramref name="ledger"/> — the persisted store <see cref="For"/> uses, or a test's
        /// temp-folder ledger.</summary>
        public SourceGuard(string sourceId, Func<long> nowMs, MissLedger ledger)
        {
            SourceId = sourceId ?? "";
            _nowMs = nowMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _ledger = ledger ?? new MissLedger(null, _nowMs);
        }

        public string SourceId { get; }

        /// <summary>How long a miss is trusted: a song that was not found at all may be added any day (6 h); a song that
        /// is there without a lyric changes rarely (24 h).</summary>
        public static long Ttl(MissKind k) => k == MissKind.SongNotFound ? 6 * HourMs : 24 * HourMs;

        /// <summary>A live remembered miss for this source and track, with how long ago it was recorded.</summary>
        public bool TryGetMiss(string trackId, out MissKind kind, out long ageMs)
            => _ledger.TryGet(SourceId, trackId, out kind, out ageMs);

        /// <summary>Remember a REAL miss (the service answered "not found" / "no lyric"). Persisted. Never call it for
        /// a transport error or a timeout — those are <see cref="Failure"/> (or nothing, for a cancellation).</summary>
        public void Miss(string trackId, MissKind kind) => _ledger.Put(SourceId, trackId, kind);

        /// <summary>Drop this source's remembered miss for one track (the inspector's re-fetch). Persisted.</summary>
        public void Forget(string trackId) => _ledger.Remove(SourceId, trackId);

        /// <summary>Is the breaker open right now? <paramref name="untilMs"/> is on this guard's clock (unix ms for
        /// <see cref="For"/>).</summary>
        public bool IsOpen(out string reason, out long untilMs)
        {
            bool closedNow = false;
            lock (_gate)
            {
                long now = _nowMs();
                if (_openUntilMs > 0 && now < _openUntilMs)
                {
                    reason = _openReason;
                    untilMs = _openUntilMs;
                    return true;
                }
                if (_openUntilMs > 0)
                {
                    _openUntilMs = 0;
                    _openReason = "";
                    closedNow = true;
                }
            }
            if (closedNow) Log.Info(Diag.Category, $"breaker source={SourceId} closed");
            reason = "";
            untilMs = 0;
            return false;
        }

        /// <summary>Open the breaker for <paramref name="forMs"/> (Musixmatch captcha 2 h, quota 6 h, NetEase risk
        /// control 30 min). A shorter trip never shortens a longer one already in force.</summary>
        public void Trip(string reason, long forMs)
        {
            long until;
            lock (_gate)
            {
                until = _nowMs() + Math.Max(0L, forMs);
                _failures = 0;
                if (until <= _openUntilMs) return;
                _openUntilMs = until;
                _openReason = reason ?? "";
            }
            Log.Info(Diag.Category, $"breaker source={SourceId} open {FormatSpan(forMs)} reason={reason}");
            Probe.Note(SourceId, $"breaker tripped for {FormatSpan(forMs)}: {reason}");
        }

        /// <summary>A transport failure (no answer, 5xx, unparseable). <see cref="FailuresToOpen"/> in a row open the
        /// breaker for <see cref="FailureOpenMs"/>. Never call it for an <see cref="OperationCanceledException"/>.</summary>
        public void Failure(string reason)
        {
            int failures;
            lock (_gate)
            {
                failures = ++_failures;
            }
            Probe.Note(SourceId, $"transport failure {failures}/{FailuresToOpen}: {reason}");
            if (failures >= FailuresToOpen)
                Trip($"{failures} consecutive failures (last: {reason})", FailureOpenMs);
        }

        /// <summary>The host answered: the consecutive-failure count starts over.</summary>
        public void Success()
        {
            lock (_gate) _failures = 0;
        }

        /// <summary>The process-wide guard for a source: wall clock, misses persisted in the shared ledger. The grey
        /// sources call this; tests construct their own guard (or repoint the registry with
        /// <see cref="ResetRegistry"/>).</summary>
        public static SourceGuard For(string sourceId)
            => Registry.GetOrAdd(sourceId ?? "", static id => new SourceGuard(id, WallClock, SharedLedger()));

        /// <summary>TEST SEAM: drop every registered guard and point the registry at <paramref name="ledger"/> (a temp
        /// folder or an in-memory ledger) so a source under test never reads or writes the real profile, and no
        /// remembered miss or open breaker leaks from one test into the next. Production never calls it.</summary>
        public static void ResetRegistry(MissLedger ledger)
        {
            lock (SharedGate)
            {
                s_shared = ledger;
                Registry.Clear();
            }
        }

        static MissLedger SharedLedger()
        {
            lock (SharedGate)
                return s_shared ??= new MissLedger(MissLedger.DefaultPath(), WallClock);
        }

        static long WallClock() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>"2h", "10min", "45s" — for the breaker's log line and note.</summary>
        internal static string FormatSpan(long ms)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (ms >= HourMs && ms % HourMs == 0) return (ms / HourMs).ToString(inv) + "h";
            if (ms >= 60_000L) return (ms / 60_000L).ToString(inv) + "min";
            return (ms / 1000L).ToString(inv) + "s";
        }
    }

    // ── 2. the persisted miss ledger ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every grey source's remembered misses, keyed by (source, track). With a path it is persisted to one small
    /// JSON file; without one it lives in memory only.
    /// <para>LOAD: lazily, once, on first use; expired entries (per <see cref="SourceGuard.Ttl"/>) are dropped on the
    /// way in, and an unreadable file is an empty ledger — a cache never fails a read. CAP: <see cref="Cap"/> entries;
    /// past it the oldest go, down to 90 %. WRITE: debounced (<see cref="DefaultDebounceMs"/>) and off the caller's
    /// thread, temp-file + move so a crash never leaves a torn file; every failure is swallowed (logged once).</para></summary>
    public sealed class MissLedger
    {
        public const int Cap = 5000;
        public const int SchemaVersion = 1;
        public const int DefaultDebounceMs = 2000;
        public const string FileName = "source-misses.json";

        /// <summary>Entries older than a TTL, but also entries stamped this far in the FUTURE (a clock that jumped back)
        /// are dropped on load — otherwise they would outlive their TTL.</summary>
        const long FutureSkewMs = 3_600_000L;

        readonly record struct Entry(SourceGuard.MissKind Kind, long AtMs);

        readonly string? _path;
        readonly Func<long> _nowMs;
        readonly int _debounceMs;
        readonly Lock _gate = new();
        readonly Lock _writeGate = new();
        Dictionary<(string Source, string Track), Entry>? _entries;
        int _scheduled;
        int _writeFaultLogged;

        /// <param name="path">The ledger file, or null for an in-memory ledger.</param>
        /// <param name="nowMs">Clock seam for the TTLs.</param>
        /// <param name="debounceMs">Delay between a change and its write. -1 (<c>Timeout.Infinite</c>) writes only on
        /// <see cref="Flush"/> (the tests' deterministic seam).</param>
        public MissLedger(string? path, Func<long> nowMs, int debounceMs = DefaultDebounceMs)
        {
            _path = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
            _nowMs = nowMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _debounceMs = debounceMs;
        }

        /// <summary>The profile's ledger: beside the lyrics disk cache's entries (which never touches this name).</summary>
        public static string DefaultPath() => System.IO.Path.Combine(DiskCache.DefaultDirectory(), FileName);

        public string? Path => _path;

        /// <summary>Entries held right now (loads the file on first use).</summary>
        public int Count
        {
            get { lock (_gate) return Loaded().Count; }
        }

        public bool TryGet(string sourceId, string trackId, out SourceGuard.MissKind kind, out long ageMs)
        {
            kind = default;
            ageMs = 0;
            if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(trackId)) return false;
            bool expired;
            lock (_gate)
            {
                var entries = Loaded();
                if (!entries.TryGetValue((sourceId, trackId), out var e)) return false;
                long age = _nowMs() - e.AtMs;
                expired = age < 0 ? age < -FutureSkewMs : age >= SourceGuard.Ttl(e.Kind);
                if (!expired)
                {
                    kind = e.Kind;
                    ageMs = Math.Max(0L, age);
                    return true;
                }
                entries.Remove((sourceId, trackId));
            }
            ScheduleWrite();
            return false;
        }

        public void Put(string sourceId, string trackId, SourceGuard.MissKind kind)
        {
            if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(trackId)) return;
            lock (_gate)
            {
                var entries = Loaded();
                entries[(sourceId, trackId)] = new Entry(kind, _nowMs());
                if (entries.Count > Cap) TrimOldest(entries, Cap * 9 / 10);
            }
            ScheduleWrite();
        }

        public void Remove(string sourceId, string trackId)
        {
            if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(trackId)) return;
            bool removed;
            lock (_gate) removed = Loaded().Remove((sourceId, trackId));
            if (removed) ScheduleWrite();
        }

        /// <summary>Write the ledger NOW, synchronously (the debounced writer's body; tests call it directly). A no-op
        /// for an in-memory ledger or one never loaded. Never throws.</summary>
        public void Flush()
        {
            if (_path is null) return;
            lock (_writeGate)
            {
                SourceMissFile file;
                lock (_gate)
                {
                    if (_entries is null) return;
                    var rows = new SourceMissDto[_entries.Count];
                    int i = 0;
                    foreach (var (key, e) in _entries) rows[i++] = new SourceMissDto(key.Source, key.Track, (byte)e.Kind, e.AtMs);
                    file = new SourceMissFile(SchemaVersion, rows);
                }

                string tmp = _path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    string? dir = System.IO.Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(file, SourceMissJson.Default.SourceMissFile);
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, _path, overwrite: true);   // write-then-rename: never a torn file
                }
                catch (Exception e)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    if (Interlocked.Exchange(ref _writeFaultLogged, 1) == 0)
                        Log.Warn(Diag.Category, "lyrics source-miss ledger write failed (" + e.GetType().Name
                            + ") — grey sources will re-query remembered misses next session");
                }
            }
        }

        void ScheduleWrite()
        {
            if (_path is null || _debounceMs < 0) return;
            if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0) return;
            _ = Task.Run(async () =>
            {
                try { if (_debounceMs > 0) await Task.Delay(_debounceMs).ConfigureAwait(false); }
                finally { Volatile.Write(ref _scheduled, 0); }   // a change after this point schedules its own write
                Flush();
            });
        }

        /// <summary>The entries, loading the file on first use. Caller holds the gate.</summary>
        Dictionary<(string Source, string Track), Entry> Loaded()
        {
            if (_entries is not null) return _entries;
            var entries = new Dictionary<(string Source, string Track), Entry>();
            _entries = entries;
            if (_path is null) return entries;

            SourceMissFile? file = null;
            try
            {
                if (!File.Exists(_path)) return entries;
                byte[] bytes = File.ReadAllBytes(_path);
                file = JsonSerializer.Deserialize(bytes, SourceMissJson.Default.SourceMissFile);
            }
            catch { file = null; }   // unreadable / unparseable → an empty ledger, rewritten on the next change

            if (file is null || file.V != SchemaVersion || file.Misses is null) return entries;

            long now = _nowMs();
            int pruned = 0;
            foreach (var row in file.Misses)
            {
                if (row is null || string.IsNullOrEmpty(row.S) || string.IsNullOrEmpty(row.T)
                    || row.K > (byte)SourceGuard.MissKind.NoLyricForSong)
                { pruned++; continue; }
                var kind = (SourceGuard.MissKind)row.K;
                long age = now - row.At;
                if (age >= SourceGuard.Ttl(kind) || age < -FutureSkewMs) { pruned++; continue; }
                var key = (row.S!, row.T!);
                if (entries.TryGetValue(key, out var had) && had.AtMs >= row.At) { pruned++; continue; }
                entries[key] = new Entry(kind, row.At);
            }
            if (entries.Count > Cap) { pruned += entries.Count - Cap * 9 / 10; TrimOldest(entries, Cap * 9 / 10); }
            if (pruned > 0) ScheduleWrite();   // the file shrinks the next time it is written
            return entries;
        }

        static void TrimOldest(Dictionary<(string Source, string Track), Entry> entries, int target)
        {
            if (entries.Count <= target) return;
            var all = new List<KeyValuePair<(string Source, string Track), Entry>>(entries);
            all.Sort(static (a, b) => a.Value.AtMs.CompareTo(b.Value.AtMs));   // oldest first
            int drop = entries.Count - Math.Max(0, target);
            for (int i = 0; i < drop; i++) entries.Remove(all[i].Key);
        }
    }

    // ── 3. the shared search ladder ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The keyword ladder every grey source searches through.</summary>
    public static class GreyLadder
    {
        /// <summary>The grey client's User-Agent (set once by <see cref="HttpFetch.Grey"/>).</summary>
        public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Wavee/1.0";

        /// <summary>At most this many hits come back: the source fetches the best, and falls through to the second only
        /// when the best has no lyric body.</summary>
        public const int MaxResults = 2;

        /// <summary>How many of the best-scored hits are noted for the inspector.</summary>
        public const int NotedTop = 3;

        static readonly IReadOnlyList<(Hit Hit, MatchScore Score)> NoHits = Array.Empty<(Hit Hit, MatchScore Score)>();

        /// <summary>Walks <see cref="Query.Variants"/>; pools the hits across variants (deduplicated by
        /// <see cref="Hit.Id"/>, keeping the best <see cref="MatchScore"/>); stops early once a hit reaches
        /// <see cref="MatchBand.VeryHigh"/>; returns up to <see cref="MaxResults"/> hits at or above
        /// <see cref="MatchBand.Medium"/>, best first, each with its score. Notes the top <see cref="NotedTop"/> hits and
        /// every hard-gate rejection. A search that answers null or throws is noted and skipped; only an
        /// <see cref="OperationCanceledException"/> propagates.</summary>
        /// <param name="search">One keyword search: the parsed hit list, or null when the service gave no usable
        /// answer.</param>
        public static async Task<IReadOnlyList<(Hit Hit, MatchScore Score)>> SearchAsync(string sourceId, Request req,
            Func<string /*keyword*/, CancellationToken, Task<IReadOnlyList<Hit>?>> search, CancellationToken ct)
        {
            var pool = new Dictionary<string, (Hit Hit, MatchScore Score)>(StringComparer.Ordinal);
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            int searches = 0;
            try
            {
                foreach (string keyword in Query.Variants(req))
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(keyword)) continue;
                    searches++;

                    IReadOnlyList<Hit>? hits;
                    try { hits = await search(keyword, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        Probe.Note(sourceId, $"search '{keyword}' failed: {e.GetType().Name}");
                        continue;
                    }
                    if (hits is null)
                    {
                        Probe.Note(sourceId, $"search '{keyword}' → no answer");
                        continue;
                    }

                    int fresh = 0;
                    for (int i = 0; i < hits.Count; i++)
                    {
                        Hit hit = hits[i];
                        if (string.IsNullOrEmpty(hit.Id) || rejected.Contains(hit.Id)) continue;
                        MatchScore score = MetadataMatch.Score(req, in hit);
                        if (score.Band == MatchBand.None)
                        {
                            if (rejected.Add(hit.Id)) Probe.Note(sourceId, $"rejected '{hit.Title}' ({hit.Id}): {score.Reason}");
                            continue;
                        }
                        if (pool.TryGetValue(hit.Id, out var had))
                        {
                            if (Compare(score, had.Score) <= 0) continue;
                        }
                        else fresh++;
                        pool[hit.Id] = (hit, score);
                    }
                    Probe.Note(sourceId, $"search '{keyword}' → {hits.Count} hit(s), {fresh} new");

                    if (BestBand(pool) >= MatchBand.VeryHigh)
                    {
                        Probe.Note(sourceId, $"stopped the ladder at '{keyword}' — {BestBand(pool)} hit");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                // A scoring or keyword bug must cost this source its answer, never the fan-out.
                Probe.Note(sourceId, $"search ladder failed: {e.GetType().Name}");
            }

            if (pool.Count == 0)
            {
                Probe.Note(sourceId, $"no usable hit in {searches} search(es)");
                return NoHits;
            }

            var ranked = new List<(Hit Hit, MatchScore Score)>(pool.Values);
            ranked.Sort(static (a, b) =>
            {
                int c = Compare(b.Score, a.Score);   // best first
                return c != 0 ? c : string.CompareOrdinal(a.Hit.Id, b.Hit.Id);
            });

            for (int i = 0; i < ranked.Count && i < NotedTop; i++)
            {
                var (h, s) = ranked[i];
                string line;
                try { line = MetadataMatch.Describe(in h, in s); }
                catch (Exception e) when (e is not OperationCanceledException) { line = h.Id + " " + s.Band; }
                Probe.Note(sourceId, $"#{i + 1} {line}");
            }

            var result = new List<(Hit Hit, MatchScore Score)>(MaxResults);
            foreach (var entry in ranked)
            {
                if (entry.Score.Band < MatchBand.Medium) break;   // sorted: nothing after it is better
                result.Add(entry);
                if (result.Count == MaxResults) break;
            }
            if (result.Count == 0)
                Probe.Note(sourceId, $"best hit is {ranked[0].Score.Band} — below Medium, not fetched");
            return result;
        }

        /// <summary>Band first, then confidence.</summary>
        static int Compare(in MatchScore a, in MatchScore b)
        {
            int c = a.Band.CompareTo(b.Band);
            return c != 0 ? c : a.Confidence.CompareTo(b.Confidence);
        }

        static MatchBand BestBand(Dictionary<string, (Hit Hit, MatchScore Score)> pool)
        {
            var best = MatchBand.None;
            foreach (var v in pool.Values) if (v.Score.Band > best) best = v.Score.Band;
            return best;
        }
    }
}

// ── the ledger's wire shape ──────────────────────────────────────────────────────────────────────────────────────────
//
// Namespace level, not nested in the static `Lyrics`: the DTOs travel with their source-generated context. Short keys —
// the file holds up to 5000 rows.

internal sealed record SourceMissFile(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("misses")] SourceMissDto[]? Misses);

internal sealed record SourceMissDto(
    [property: JsonPropertyName("s")] string? S,
    [property: JsonPropertyName("t")] string? T,
    [property: JsonPropertyName("k")] byte K,
    [property: JsonPropertyName("at")] long At);

/// <summary>AOT-safe SOURCE-GENERATED JSON for the miss ledger — no reflection-based serialization.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SourceMissFile))]
internal sealed partial class SourceMissJson : JsonSerializerContext;
