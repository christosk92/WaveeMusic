using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

// The PERSISTENT half of the aggregator's winner cache (wave H of the syllable-lyrics campaign). The in-memory LRU in
// AggregatingLyricsProvider is 64 entries and dies with the process, so every restart re-fans-out to seven network
// sources for songs the app already knows the words to — and with no network there are no lyrics at all. This makes a
// previously-played track resolve from disk, offline, before a single request goes out.
//
// SHAPE: one JSON file per track under %LOCALAPPDATA%\Wavee\lyrics (the library.db / logs / diag sibling idiom), named
// for the SHA-256 of the track id — Spotify ids are base62, so their case-sensitivity would collide on a
// case-insensitive filesystem; hashing is the AudioBodyDiskCache.Stem precedent and gives a fixed, always-safe name.
// The file is a VERSIONED ENVELOPE { v, at, id, doc }: a version mismatch, a track-id mismatch or an unparseable body
// is a MISS that also deletes the file — a cache is never allowed to fail a read.
//
// NEGATIVE ENTRIES: a track that returned nothing from every source writes the same envelope with no document, and a
// read inside NegativeTtl answers "known missing" WITHOUT a fan-out (instrumentals and obscure tracks are otherwise a
// full seven-source, multi-second miss on every single play). It expires so a lyric added later is still found. This is
// disjoint from AmllTtmlDbSource's per-source in-memory _misses, which stays exactly as it is.
//
// DURABILITY: writes are temp-file + File.Move(overwrite) — a crash mid-write can never leave a torn document behind
// (the PlayLogStore write-then-rename contract). Every path swallows: a cache is best-effort by definition.

/// <summary>What a disk lookup found. <see cref="KnownMissing"/> is a live negative marker — the caller must return
/// "no lyrics" WITHOUT fanning out.</summary>
public enum LyricsCacheOutcome { Miss, Hit, KnownMissing }

/// <summary>One disk lookup's answer. <see cref="SavedAtUnixMs"/> is when the entry was persisted (0 on a miss).</summary>
public readonly record struct LyricsCacheEntry(LyricsCacheOutcome Outcome, LyricsDocument? Document, long SavedAtUnixMs)
{
    public static LyricsCacheEntry Missing => default;   // Outcome == Miss
}

/// <summary>Persistent per-track lyrics cache: the offline/restart half of the aggregator's winner cache.</summary>
public sealed class LyricsDiskCache
{
    /// <summary>Envelope schema. BUMP whenever the persisted <see cref="LyricsDocument"/> shape changes meaning — an
    /// entry written by another version is discarded on read rather than misinterpreted.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Sweep caps. ~2000 word-synced documents is a deep listening history and still a small folder; the byte
    /// cap catches the pathological case (a few very long word-synced docs) before the file count would.</summary>
    public const int DefaultMaxFiles = 2000;
    public const long DefaultMaxBytes = 50L << 20;

    /// <summary>How long "this track has no lyrics anywhere" is trusted. Long enough that a repeat play of an
    /// instrumental is free, short enough that a newly published lyric is picked up within days.</summary>
    public static TimeSpan DefaultNegativeTtl => TimeSpan.FromDays(3);

    const double SweepTargetFraction = 0.8;   // trim to 80% of the cap so a sweep is not re-armed on the next write
    const int StemLength = 64;                // SHA-256 as lowercase hex

    readonly string _dir;
    readonly WaveeLogger _log;
    readonly Func<long> _nowUnixMs;
    readonly int _maxFiles;
    readonly long _maxBytes;
    readonly long _negativeTtlMs;
    int _sweepArmed;
    Task? _sweep;
    int _writeFaultLogged;

    /// <param name="directory">Cache root. Tests inject a temp directory; production passes null for
    /// <see cref="DefaultDirectory"/> and the real %LOCALAPPDATA% is never touched by a test.</param>
    /// <param name="nowUnixMs">Clock seam for the negative TTL (the CoverColorPlane <c>nowUnix</c> precedent).</param>
    public LyricsDiskCache(
        string? directory = null,
        WaveeLogger log = default,
        Func<long>? nowUnixMs = null,
        int maxFiles = DefaultMaxFiles,
        long maxBytes = DefaultMaxBytes,
        TimeSpan? negativeTtl = null)
    {
        _dir = Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory!);
        _log = log;
        _nowUnixMs = nowUnixMs ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _maxFiles = Math.Max(16, maxFiles);
        _maxBytes = Math.Max(1L << 20, maxBytes);
        _negativeTtlMs = (long)Math.Max(0d, (negativeTtl ?? DefaultNegativeTtl).TotalMilliseconds);
    }

    /// <summary>%LOCALAPPDATA%\Wavee\lyrics — beside library.db, logs\ and diag\.</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "lyrics");

    public string Directory => _dir;

    // ── read ──────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Look one track up. Async file I/O; NEVER throws for a bad file (a corrupt/stale/foreign entry is a miss
    /// and is deleted on the way out). Only a real caller cancellation propagates.</summary>
    public async Task<LyricsCacheEntry> TryLoadAsync(string trackId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackId)) return LyricsCacheEntry.Missing;
        ArmSweep();

        string path = PathFor(trackId);
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return LyricsCacheEntry.Missing;
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return LyricsCacheEntry.Missing; }   // locked / racing delete / unreadable — a miss, never a throw

        LyricsCacheEnvelope? env = null;
        try { env = JsonSerializer.Deserialize(bytes, LyricsCacheJson.Default.LyricsCacheEnvelope); }
        catch { /* unparseable → discarded below */ }

        if (env is null) { Discard(path, trackId, "unparseable"); return LyricsCacheEntry.Missing; }
        if (env.V != SchemaVersion) { Discard(path, trackId, "schema v" + env.V); return LyricsCacheEntry.Missing; }
        if (!string.IsNullOrEmpty(env.Id) && !string.Equals(env.Id, trackId, StringComparison.Ordinal))
        { Discard(path, trackId, "track id mismatch"); return LyricsCacheEntry.Missing; }

        if (env.Doc is null)
        {
            long age = _nowUnixMs() - env.At;
            if (age >= 0 && age < _negativeTtlMs) return new(LyricsCacheOutcome.KnownMissing, null, env.At);
            Discard(path, trackId, "negative marker expired");
            return LyricsCacheEntry.Missing;
        }

        // A document with no lines is unusable — the view would render an empty lyric instead of searching. Treat it
        // exactly like a corrupt file so the next play re-fetches.
        if (env.Doc.Lines is not { Count: > 0 }) { Discard(path, trackId, "no lines"); return LyricsCacheEntry.Missing; }
        return new(LyricsCacheOutcome.Hit, env.Doc, env.At);
    }

    // ── write ─────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Persist a winning document. Fire-and-forget: serialization and I/O both run off the caller's thread and
    /// every failure is swallowed (logged once).</summary>
    public void Save(string trackId, LyricsDocument document)
    {
        if (string.IsNullOrEmpty(trackId) || document is null) return;
        _ = Task.Run(() => SaveAsync(trackId, document));
    }

    /// <summary>Persist the "no lyrics anywhere" marker (TTL-bounded). Fire-and-forget, like <see cref="Save"/>.</summary>
    public void SaveMissing(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return;
        _ = Task.Run(() => SaveAsync(trackId, null));
    }

    /// <summary>The awaitable core of both writes (the deterministic seam tests use). Never throws.</summary>
    public async Task SaveAsync(string trackId, LyricsDocument? document, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackId)) return;
        ArmSweep();
        string path = PathFor(trackId);
        string tmp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            var env = new LyricsCacheEnvelope(SchemaVersion, _nowUnixMs(), trackId, document);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(env, LyricsCacheJson.Default.LyricsCacheEnvelope);
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);   // write-then-rename: a crash can't leave a torn document
        }
        catch (Exception e)
        {
            TryDelete(tmp);
            if (Interlocked.Exchange(ref _writeFaultLogged, 1) == 0)
                _log.Warn($"lyrics disk cache write failed ({e.GetType().Name}) — lyrics still work, they just won't persist");
        }
    }

    // ── maintenance ───────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Drop every entry this cache owns (the disk half of <c>AggregatingLyricsProvider.ClearCache</c>). Only
    /// files matching the cache's own name shape are touched, so a mis-pointed directory can never be wiped.</summary>
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

    /// <summary>Drop ONE track's entry (positive or negative). Backs the lyrics inspector's "re-fetch from providers":
    /// the next lookup has to MISS or the fan-out never runs and there is still no raw payload to show.</summary>
    public void Forget(string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return;
        try { TryDelete(PathFor(trackId)); } catch { }
    }

    /// <summary>Enforce the size caps: oldest-first (by save time, which is the file's write time) down to 80% of both
    /// caps. Best-effort and synchronous — production reaches it through <see cref="ArmSweep"/>, off-thread, once.</summary>
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
                    // A torn write from a crashed session. Anything younger than 10 minutes may be a live intermediate
                    // of a concurrent SaveAsync (the AudioBodyDiskCache reconcile rule).
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
            if (removed > 0) _log.Info($"lyrics disk cache swept: removed {removed} oldest entries, {count} remain ({bytes} bytes)");
        }
        catch { }
    }

    /// <summary>The lazily-armed sweep task (test seam). <see cref="Task.CompletedTask"/> before the first use.</summary>
    public Task SweepInFlight => Volatile.Read(ref _sweep) ?? Task.CompletedTask;

    /// <summary>First use in the process schedules ONE sweep off-thread. A read never waits on it.</summary>
    void ArmSweep()
    {
        if (Interlocked.CompareExchange(ref _sweepArmed, 1, 0) != 0) return;
        Volatile.Write(ref _sweep, Task.Run(Sweep));
    }

    // ── paths ─────────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The file a track id maps to. SHA-256 hex: base62 Spotify ids are case-sensitive and would alias on a
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

    void Discard(string path, string trackId, string why)
    {
        TryDelete(path);
        _log.Debug($"lyrics disk cache discarded entry for {trackId}: {why}");
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}

// The persisted envelope. Short keys: a word-synced document is tens of KB of syllables and one is written per played
// track (the PlayLogEntryDto precedent). `doc` is absent on a negative marker (WhenWritingNull).
internal sealed record LyricsCacheEnvelope(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("at")] long At,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("doc")] LyricsDocument? Doc);

// AOT-safe source-generated JSON (the HistoryJsonCtx / PlayLogJsonCtx / EntityJson precedent) — no reflection-based
// serialization anywhere in this app.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LyricsCacheEnvelope))]
internal sealed partial class LyricsCacheJson : JsonSerializerContext { }
