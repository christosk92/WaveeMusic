using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

/// <summary>The app-facing lyrics provider that fans out to every enabled candidate source in parallel, normalizes each to
/// a <see cref="LyricsCandidate"/>, picks the Spotify-native candidate as the reranker reference, and returns the single
/// best <see cref="LyricsDocument"/> (docs/lyrics-aggregator-reranker-plan.md §7). NOT first-hit: a later word-synced
/// candidate can still beat an earlier line-synced one. A per-source miss/timeout/throw degrades to null for that source
/// and never fails the aggregate. Winners are cached by track id; the decision is logged for explainability.</summary>
public sealed class AggregatingLyricsProvider : IUpgradingLyricsProvider, ILyricsRefetch
{
    const int UnsyncedFirstHitGraceMs = 6000;
    readonly IReadOnlyList<ILyricCandidateSource> _sources;
    readonly Func<string, CancellationToken, Task<LyricsRequest?>> _resolve;
    readonly LyricsOptions _opt;
    readonly string _referenceSourceId;
    readonly WaveeLogger _log;
    // The PERSISTENT half of the winner cache (null = memory only, which is what every unit test uses so no test can
    // touch the real %LOCALAPPDATA%). Read-through happens before the fan-out — before even resolving the request — so a
    // track played in an earlier session resolves with no network at all.
    readonly LyricsDiskCache? _disk;
    readonly Dictionary<string, LyricsDocument> _cache = new();
    // ONE shared fetch per track id. The rail lyrics panel and the immersive surface each mount their own doc host, so
    // opening the immersive surface asks for the SAME track twice, concurrently — which used to mean two full
    // seven-source fan-outs plus two racing disk writes. Entered under _gate before _resolve, removed in the runner's
    // finally. See GetLyricsAsync for the cancellation contract.
    readonly Dictionary<string, Task<LyricsDocument?>> _inFlight = new(StringComparer.Ordinal);
    // What the DISK is known to hold for a track, as the same monotone Grade the promotion ladder uses. Seeded by a
    // read-through hit and updated by every write we issue, so a Save can never DOWNGRADE a better persisted document
    // (SaveToDiskIfBetter). Pruned with the LRU below; the only way an entry can outlive its cache entry is an eviction
    // landing between a winner write and a still-running continuation, which costs one stale long.
    readonly Dictionary<string, long> _diskGrade = new(StringComparer.Ordinal);
    readonly SimpleEvent<LyricsDocument> _upgrades = new();
    readonly object _gate = new();
    // Bound the winner cache: a long session touches thousands of distinct tracks and each LyricsDocument is tens of KB
    // (word-synced). A miss re-fetches (self-healing), so an LRU cap is safe. Touched/evicted under _gate; MRU at the end.
    const int CacheCap = 64;
    readonly List<string> _lru = new();
    void TouchLru(string id) { _lru.Remove(id); _lru.Add(id); }
    void EvictLru() { while (_lru.Count > CacheCap) { var oldest = _lru[0]; _lru.RemoveAt(0); _cache.Remove(oldest); _diskGrade.Remove(oldest); } }
    public IObservable<LyricsDocument> LyricsUpgraded => _upgrades;

    public AggregatingLyricsProvider(
        IEnumerable<ILyricCandidateSource> sources,
        Func<string, CancellationToken, Task<LyricsRequest?>> resolveRequest,
        LyricsOptions? options = null,
        string referenceSourceId = "spotify",
        WaveeLogger log = default,
        LyricsDiskCache? diskCache = null)
    {
        _sources = sources.Where(s => s.Enabled).ToList();
        _resolve = resolveRequest;
        _opt = options ?? LyricsOptions.Default;
        _referenceSourceId = referenceSourceId;
        _log = log;
        _disk = diskCache;
    }

    readonly record struct Probed(LyricsCandidate? Cand, LyricsOutcome Outcome, long Ms, string Detail);

    // An exact-recording word-synced lyric (matched by Spotify identity or ISRC) is the best result possible — nothing a
    // slower source could return beats it, so the moment one arrives we stop waiting.
    static bool IsGold(LyricsCandidate? c)
        => c is { Sync: LyricsSyncKind.Syllable, Basis: MatchBasis.Identity or MatchBasis.Isrc };

    public async Task<LyricsDocument?> GetLyricsAsync(string trackId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackId)) return null;

        // CANCELLATION CONTRACT of the shared fetch: the work itself runs on CancellationToken.None and is never
        // cancelled by any caller. It exists to POPULATE the caches, so the first caller walking away (a rail panel
        // unmounting the instant the immersive surface takes over) must not cancel the second caller's lyrics, and a
        // finished-but-unobserved fan-out is still worth persisting. Each caller observes its OWN token through
        // WaitAsync instead: it stops awaiting immediately and throws OperationCanceledException exactly as before,
        // while the shared fetch runs to completion in the background. Same contract as SwitchableLyrics one layer up.
        Task<LyricsDocument?> shared;
        TaskCompletionSource<LyricsDocument?>? owner = null;
        lock (_gate)
        {
            if (_cache.TryGetValue(trackId, out var cached)) { TouchLru(trackId); return cached; }
            if (_inFlight.TryGetValue(trackId, out var running)) shared = running;
            else
            {
                owner = new TaskCompletionSource<LyricsDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight[trackId] = shared = owner.Task;
            }
        }
        // Started OUTSIDE the lock so a fan-out that happens to complete synchronously cannot run under _gate.
        if (owner is not null) _ = RunSharedAsync(trackId, owner);
        return await shared.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Runs the one shared fetch for a track id and retires its in-flight slot. Never cancelled by a caller.</summary>
    async Task RunSharedAsync(string trackId, TaskCompletionSource<LyricsDocument?> tcs)
    {
        try { tcs.TrySetResult(await FetchAndCacheAsync(trackId).ConfigureAwait(false)); }
        catch (Exception e)
        {
            tcs.TrySetException(e);
            _ = tcs.Task.Exception;   // observe it: every caller may already have walked away on its own token
        }
        finally { lock (_gate) _inFlight.Remove(trackId); }
    }

    async Task<LyricsDocument?> FetchAndCacheAsync(string trackId)
    {
        // Read-through, BEFORE _resolve and before the fan-out: resolving can itself hit the network (a thin cluster
        // track re-resolves through the metadata resolver), so a disk hit must short-circuit both. This is the whole
        // point of the disk cache — lyrics for a previously-played track with no network at all.
        if (_disk is { } disk)
        {
            var entry = await disk.TryLoadAsync(trackId, CancellationToken.None).ConfigureAwait(false);
            if (entry.Outcome == LyricsCacheOutcome.Hit && entry.Document is { } loaded)
            {
                // A cached document NEVER passes through the reranker, so the word-timing gate would miss it entirely —
                // including every entry an older build persisted before the gate existed. Without this repair a track
                // whose broken karaoke was cached once keeps serving it forever: the read-through short-circuits the
                // fan-out, and at richness 3 not even the background upgrade runs. Repair here and the entry drops to
                // the line tier, which re-arms that upgrade on the very same call.
                bool unsingable = loaded.Sync == LyricsSyncKind.Syllable
                    && LyricsTiming.HasImplausibleWordTiming(loaded, out _, out _);
                var fromDisk = unsingable ? LyricsTiming.StripWordTiming(loaded) : loaded;
                // Same self-heal reasoning for junk lines: an entry cached before the cleaning pass existed still holds
                // its blank/♪/credit rows, and a disk hit never reaches FetchOne. The header rule is metadata-driven and
                // a disk hit deliberately never resolves the track, so it is skipped here — the other two families are
                // metadata-free and cover the rows that actually render.
                var swept = LyricsClean.Apply(fromDisk);
                int junk = fromDisk.Lines.Count - swept.Lines.Count;
                if (swept.Lines.Count > 0 && junk > 0) fromDisk = swept;
                else junk = 0;
                // Overwrite the file directly, NOT through SaveToDiskIfBetter: this is the one case where a "downgrade"
                // is the correction, and the guard exists to stop exactly the write we want here.
                if (unsingable || junk > 0)
                {
                    disk.Save(trackId, fromDisk);
                    string why = unsingable && junk > 0 ? $"unsingable word timing + {junk} non-lyric line(s)"
                        : unsingable ? "unsingable word timing (stripped to line sync)"
                        : $"{junk} non-lyric line(s)";
                    _log.Info($"lyrics: repaired the cached document for {trackId} — {why} — and re-persisted it");
                }
                lock (_gate)
                {
                    _cache[trackId] = fromDisk; TouchLru(trackId);
                    _diskGrade[trackId] = Grade(fromDisk);   // what the file now holds — no Save may go below it
                    EvictLru();
                }
                PublishDiskReport(trackId, fromDisk, entry.SavedAtUnixMs);
                // A POSITIVE entry has no TTL by design, so without this a low-richness document cached in an earlier
                // session would be PERMANENT: the read-through short-circuits resolve + fan-out + upgrade forever and
                // the track could never reach syllable lyrics. Serve the cached document immediately (the offline
                // promise is untouched) and, when it is not already at the top of the ladder, run the ordinary
                // resolve + fan-out + upgrade in the BACKGROUND — a richer winner promotes, publishes on
                // LyricsUpgraded and re-persists exactly like a live upgrade does.
                if (Richness(fromDisk) < 3) _ = UpgradeDiskHitAsync(trackId, fromDisk);
                return fromDisk;
            }
            if (entry.Outcome == LyricsCacheOutcome.KnownMissing)
            {
                PublishDiskReport(trackId, null, entry.SavedAtUnixMs);
                return null;   // TTL-bounded: after it expires the very same call fans out again
            }
        }

        LyricsRequest? req;
        try { req = await _resolve(trackId, CancellationToken.None).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { req = null; }
        if (req is null)
        {
            LyricsDiagnostics.Publish(new LyricsSearchReport(trackId, "", "", "", 0L, null,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                "could not resolve track metadata — no title/artist to search with", Array.Empty<LyricsSourceTrace>()));
            PublishInspection(trackId, EmptyProbed, null, null, "no request was ever built — the track's metadata could not be resolved");
            return null;
        }

        // Ambient probe: flows (AsyncLocal) into each parallel source task so a source can record WHY it missed.
        var probe = new LyricsProbe();
        LyricsProbe.Current.Value = probe;

        // Fan out in parallel. The UI waits only for the short first-hit grace window; slower sources keep running in the
        // background and can publish a richer replacement without delaying the initial lyric.
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var srcCts = new CancellationTokenSource();
        var started = _sources.Select(s => (Source: s, Task: FetchOne(s, req, probe, srcCts.Token))).ToList();
        var collected = new Dictionary<string, Probed>(StringComparer.Ordinal);

        var pending = started.ToList();
        Task? grace = null;
        bool graceFromUnsynced = false;
        bool goldCollected = false;
        while (pending.Count > 0)
        {
            var waiters = new List<Task>(pending.Count + 1);
            foreach (var p in pending) waiters.Add(p.Task);
            if (grace is not null) waiters.Add(grace);

            var done = await Task.WhenAny(waiters).ConfigureAwait(false);
            if (ReferenceEquals(done, grace)) break;   // grace window elapsed → stop waiting for slow stragglers

            int idx = pending.FindIndex(p => ReferenceEquals(p.Task, done));
            var entry = pending[idx];
            pending.RemoveAt(idx);
            var pr = await entry.Task.ConfigureAwait(false);   // FetchOne never throws
            collected[entry.Source.Id] = pr;
            if (grace is null && pr.Cand is not null)
            {
                graceFromUnsynced = pr.Cand.Sync == LyricsSyncKind.Unsynced;
                grace = Task.Delay(InitialGraceMs(pr.Cand));
            }
            else if (graceFromUnsynced && pr.Cand is { Sync: not LyricsSyncKind.Unsynced })
            {
                graceFromUnsynced = false;
                grace = Task.Delay(Math.Clamp(_opt.FirstHitGraceMs, 0, int.MaxValue));
            }
            if (IsGold(pr.Cand)) goldCollected = true;
            if (goldCollected && (collected.ContainsKey(_referenceSourceId) || !pending.Any(p => p.Source.Id == _referenceSourceId)))
                break;   // gold is unbeatable, but keep the Spotify reference when it is already nearly here
        }
        bool continueInBackground = pending.Count > 0;

        var candidates = collected.Values.Where(p => p.Cand is not null).Select(p => p.Cand!).ToList();
        var reference = candidates.FirstOrDefault(c => c.ProviderId == _referenceSourceId)?.Document;
        RankedLyrics ranked = candidates.Count > 0
            ? LyricsReranker.Rank(candidates, reference)
            : new RankedLyrics(null, null, Array.Empty<LyricsDecision>());

        // Fold the reranker verdicts back into the per-source traces (a source we stopped waiting on is "skipped").
        string? winnerId = ranked.Best?.ProviderId;
        var traces = new List<LyricsSourceTrace>(_sources.Count);
        foreach (var s in _sources)
        {
            var dec = ranked.All.FirstOrDefault(d => d.ProviderId == s.Id);
            if (collected.TryGetValue(s.Id, out var pr))
                traces.Add(new LyricsSourceTrace(s.Id, pr.Outcome, pr.Ms, pr.Detail,
                    pr.Cand?.Sync ?? LyricsSyncKind.None, pr.Cand?.LineCount ?? 0,
                    dec?.Score ?? 0d, dec is not null && s.Id == winnerId, dec?.Reason ?? "",
                    dec?.TextAgreement ?? 0d, dec?.Coverage ?? 0d, dec?.TimingScore ?? 0d, dec?.SyncScore ?? 0d));
            else
                traces.Add(new LyricsSourceTrace(s.Id, LyricsOutcome.Skipped, 0L,
                    continueInBackground ? "background still checking richer sources" : "skipped — a faster match returned first",
                    LyricsSyncKind.None, 0, 0d, false, ""));
        }

        int hits = candidates.Count;
        int ran = collected.Count;
        string summary = hits == 0
            ? $"0/{ran} sources returned lyrics — no match anywhere"
            : ranked.Best is { } sb
                ? $"{hits}/{ran} returned; winner={sb.ProviderId} ({sb.Sync}, score {sb.Score:F2}, offset {sb.AppliedOffsetMs}ms)"
                : $"{hits}/{ran} returned";
        LyricsDiagnostics.Publish(new LyricsSearchReport(
            trackId, req.Title, req.ArtistsJoined, req.Album, req.DurationMs, req.Isrc,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), summary, traces));
        LogReport(trackId, req, summary, traces);

        if (ranked.Best is { } b)
            _log.Info($"track={trackId} winner={b.ProviderId} sync={b.Sync} score={b.Score:F3} text={b.TextAgreement:F2} " +
                $"timing={b.TimingScore:F2} offset={b.AppliedOffsetMs}ms candidates=[{string.Join(",", candidates.Select(c => c.ProviderId))}] ({b.Reason})");

        var winner = ranked.Winner;
        PublishInspection(trackId, collected, probe, winner,
            continueInBackground ? "first pass — slower sources are still running in the background" : "complete");
        if (winner is not null) lock (_gate) { _cache[trackId] = winner; TouchLru(trackId); EvictLru(); }
        // ONE writer per request. Both Saves are fire-and-forget, so issuing the winner write here AND the upgrade
        // write from the continuation left two unordered file writes racing — the worse document could land last. When
        // a continuation is going to run it therefore OWNS the write and persists once, with the best document it ends
        // up holding (its finally writes even on a cancel, so a skipped winner Save can never be lost).
        bool willContinue = continueInBackground && winner is not null && Richness(winner) < 3;
        if (_disk is { } wdisk)
        {
            if (winner is not null && !willContinue) SaveToDiskIfBetter(trackId, winner);
            // The negative marker is written ONLY when every source actually ran and none produced a candidate — never
            // when the grace window cut a still-running fan-out short, and never when the reranker merely rejected what
            // it was given.
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

    /// <summary>Background half of a LOW-RICHNESS disk hit (see the read-through): resolve and fan out exactly like a
    /// cold request, then hand the still-running sources to the SAME continuation the live path uses, with the cached
    /// document as the incumbent. A richer winner promotes, publishes and re-persists; anything else changes nothing.
    /// Offline (no resolution) it is a no-op — the cached document simply stays what it is.</summary>
    async Task UpgradeDiskHitAsync(string trackId, LyricsDocument fromDisk)
    {
        CancellationTokenSource? owned = null;
        try
        {
            LyricsRequest? req;
            try { req = await _resolve(trackId, CancellationToken.None).ConfigureAwait(false); }
            catch { req = null; }
            if (req is null) return;

            var probe = new LyricsProbe();
            LyricsProbe.Current.Value = probe;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            owned = new CancellationTokenSource();
            var pending = _sources.Select(s => (Source: s, Task: FetchOne(s, req, probe, owned.Token))).ToList();
            var srcCts = owned;
            owned = null;   // handed over: ContinueForUpgradeAsync cancels and disposes it in its finally
            await ContinueForUpgradeAsync(trackId, req, srcCts, pending,
                new Dictionary<string, Probed>(StringComparer.Ordinal), fromDisk, startedAt, probe).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Info($"background upgrade of the cached lyrics for {trackId} failed: {e.GetType().Name}");
        }
        finally { owned?.Dispose(); }
    }

    /// <summary>The ONE place a document reaches the disk cache. A Save never DOWNGRADES: the grade of what the file
    /// holds is tracked per track (seeded by the read-through, updated by every write we issue) and a write whose
    /// document is not strictly better is dropped. Without it a later line-only winner could overwrite the syllable
    /// document an earlier session had already persisted.</summary>
    void SaveToDiskIfBetter(string trackId, LyricsDocument doc)
    {
        if (_disk is not { } disk) return;
        long grade = Grade(doc);
        lock (_gate)
        {
            if (_diskGrade.TryGetValue(trackId, out long known) && known >= grade) return;
            _diskGrade[trackId] = grade;
        }
        disk.Save(trackId, doc);
    }

    int InitialGraceMs(LyricsCandidate candidate)
    {
        int normal = Math.Clamp(_opt.FirstHitGraceMs, 0, int.MaxValue);
        return candidate.Sync == LyricsSyncKind.Unsynced
            ? Math.Max(normal, UnsyncedFirstHitGraceMs)
            : normal;
    }

    async Task ContinueForUpgradeAsync(
        string trackId,
        LyricsRequest req,
        CancellationTokenSource srcCts,
        List<(ILyricCandidateSource Source, Task<Probed> Task)> pending,
        Dictionary<string, Probed> collected,
        LyricsDocument initialWinner,
        long startedAt,
        LyricsProbe probe)
    {
        // The document this track must END UP persisted with. The caller deliberately skips its own winner Save when it
        // spawns us, so the single write in the finally below is the only one — and it has to happen even when the
        // continuation is cancelled or finds nothing better, or a skipped winner Save would simply be lost.
        LyricsDocument bestDoc = initialWinner;
        try
        {
            long elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
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

                if (IsGold(pr.Cand) &&
                    (collected.ContainsKey(_referenceSourceId) || !pending.Any(p => p.Source.Id == _referenceSourceId)))
                    break;
            }

            var candidates = collected.Values.Where(p => p.Cand is not null).Select(p => p.Cand!).ToList();
            var reference = candidates.FirstOrDefault(c => c.ProviderId == _referenceSourceId)?.Document;
            RankedLyrics ranked = candidates.Count > 0
                ? LyricsReranker.Rank(candidates, reference)
                : new RankedLyrics(null, null, Array.Empty<LyricsDecision>());

            PublishReport(trackId, req, collected, ranked, candidates, "background complete");
            LogDecision(trackId, ranked, candidates);

            // Final = what the UI is left holding, which is the INCUMBENT unless this pass actually beat it.
            var winner = ranked.Winner;
            if (winner is null || !IsRicher(winner, initialWinner))
            {
                PublishInspection(trackId, collected, probe, initialWinner,
                    "background pass complete — nothing beat the first winner");
                return;
            }
            PublishInspection(trackId, collected, probe, winner,
                "background pass complete — it promoted a richer document");
            bestDoc = winner;

            bool promoted = false;
            lock (_gate)
            {
                if (!_cache.TryGetValue(trackId, out var current) || IsRicher(winner, current))
                {
                    _cache[trackId] = winner;
                    TouchLru(trackId);
                    EvictLru();
                    promoted = true;
                }
            }

            if (promoted) _upgrades.OnNext(winner);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _log.Info($"background lyrics upgrade failed for {trackId}: {e.GetType().Name}");
        }
        finally
        {
            // Write-through, once, with the best document — so disk holds the BEST doc and never a downgrade of it.
            SaveToDiskIfBetter(trackId, bestDoc);
            srcCts.Cancel();
            srcCts.Dispose();
        }
    }

    void PublishReport(
        string trackId,
        LyricsRequest req,
        IReadOnlyDictionary<string, Probed> collected,
        RankedLyrics ranked,
        IReadOnlyList<LyricsCandidate> candidates,
        string suffix)
    {
        string? winnerId = ranked.Best?.ProviderId;
        var traces = new List<LyricsSourceTrace>(_sources.Count);
        foreach (var s in _sources)
        {
            var dec = ranked.All.FirstOrDefault(d => d.ProviderId == s.Id);
            if (collected.TryGetValue(s.Id, out var pr))
                traces.Add(new LyricsSourceTrace(s.Id, pr.Outcome, pr.Ms, pr.Detail,
                    pr.Cand?.Sync ?? LyricsSyncKind.None, pr.Cand?.LineCount ?? 0,
                    dec?.Score ?? 0d, dec is not null && s.Id == winnerId, dec?.Reason ?? "",
                    dec?.TextAgreement ?? 0d, dec?.Coverage ?? 0d, dec?.TimingScore ?? 0d, dec?.SyncScore ?? 0d));
            else
                traces.Add(new LyricsSourceTrace(s.Id, LyricsOutcome.Skipped, 0L,
                    suffix.Length > 0 ? suffix : "skipped — a faster match returned first",
                    LyricsSyncKind.None, 0, 0d, false, ""));
        }

        int hits = candidates.Count;
        int ran = collected.Count;
        string summary = hits == 0
            ? $"0/{ran} sources returned lyrics — no match anywhere"
            : ranked.Best is { } sb
                ? $"{hits}/{ran} returned; winner={sb.ProviderId} ({sb.Sync}, score {sb.Score:F2}, offset {sb.AppliedOffsetMs}ms)"
                : $"{hits}/{ran} returned";
        if (suffix.Length > 0) summary += $" — {suffix}";
        LyricsDiagnostics.Publish(new LyricsSearchReport(
            trackId, req.Title, req.ArtistsJoined, req.Album, req.DurationMs, req.Isrc,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), summary, traces));
        LogReport(trackId, req, summary, traces);
    }

    /// <summary>Keep the "why did this song get these lyrics" debug surface honest on a disk hit: without a report the
    /// panel would show nothing (or the previous session's stale entry) for a track that resolved instantly. The request
    /// metadata is empty by construction — a disk hit deliberately never resolves the track.</summary>
    void PublishDiskReport(string trackId, LyricsDocument? doc, long savedAtUnixMs)
    {
        string when = savedAtUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(savedAtUnixMs).UtcDateTime.ToString("u")
            : "unknown";
        string detail = doc is not null
            ? "not queried — served from the local lyrics cache"
            : "not queried — the local lyrics cache remembers this track has no lyrics";
        var traces = new List<LyricsSourceTrace>(_sources.Count);
        foreach (var s in _sources)
            traces.Add(new LyricsSourceTrace(s.Id, LyricsOutcome.Skipped, 0L, detail, LyricsSyncKind.None, 0, 0d, false, ""));

        string summary = doc is not null
            ? $"served from the on-disk cache (saved {when}); winner={doc.Provider ?? "?"} ({doc.Sync}, {doc.Lines.Count} lines)"
            : $"no lyrics anywhere — cached negative result from {when}";
        LyricsDiagnostics.Publish(new LyricsSearchReport(trackId, "", "", "", 0L, null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), summary, traces));
        // Final only: a disk hit is by construction a NO-ROUND-TRIP answer, so there is no raw payload and no losing
        // candidate to show. The inspector says exactly that, and offers the re-fetch that produces both.
        PublishInspection(trackId, EmptyProbed, null, doc,
            doc is not null
                ? $"served from the on-disk cache (saved {when}) — no provider was contacted, so there is no raw response this session. Use “Re-fetch from providers”."
                : $"the on-disk cache remembers (since {when}) that this track has no lyrics anywhere — no provider was contacted.");
        _log.Debug($"track={trackId} {summary}");
    }

    static readonly Dictionary<string, Probed> EmptyProbed = new(StringComparer.Ordinal);

    /// <summary>Record the heavy half of this pass for the lyrics-source inspector: every payload the probe captured and
    /// every candidate's PARSED document (winners and losers alike — the losers are the whole point), plus the document
    /// the UI is left with. Cheap: the strings and documents already exist; this only keeps them reachable.</summary>
    void PublishInspection(
        string trackId,
        IReadOnlyDictionary<string, Probed> collected,
        LyricsProbe? probe,
        LyricsDocument? final,
        string note)
    {
        var candidates = new List<LyricsParsedCandidate>(collected.Count);
        foreach (var pr in collected.Values)
            if (pr.Cand is { } c)
                candidates.Add(new LyricsParsedCandidate(c.ProviderId, c.Basis, c.Prior, c.Document));

        var inspection = new LyricsInspection(
            trackId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            note,
            probe?.RawPayloads() ?? Array.Empty<LyricsRawPayload>(),
            candidates,
            final);
        LyricsDiagnostics.PublishInspection(inspection);
        AutoDump(trackId, inspection);
    }

    /// <summary>WAVEE_LYRICS_DUMP=1: write an evidence bundle for every track whose word timing trips the gate. One
    /// broken track is an anecdote; the question that actually matters — is EVERY richsync body like this, or only
    /// machine-generated ones for old catalogue tracks? — needs a corpus, and this collects one passively while the app
    /// is simply used. Off by default (it writes provider payloads to disk), and skipped when there is no payload to
    /// write, so a cache hit never produces an empty folder.</summary>
    static readonly bool _autoDump =
        Environment.GetEnvironmentVariable("WAVEE_LYRICS_DUMP") is "1" or "true" or "TRUE";

    void AutoDump(string trackId, LyricsInspection inspection)
    {
        if (!_autoDump || inspection.Raw.Count == 0) return;
        bool suspect = false;
        foreach (var c in inspection.Candidates)
            if (LyricsTiming.HasImplausibleWordTiming(c.Document, out _, out _)) { suspect = true; break; }
        if (!suspect) return;

        // Off the fetch path: this is diagnostics, and a slow disk must never lengthen a lyrics fetch.
        var report = LyricsDiagnostics.ForTrack(trackId);
        _ = Task.Run(() =>
        {
            string? folder = LyricsInspectionExport.WriteBundle(trackId, report, inspection);
            if (folder is not null) _log.Info($"lyrics: implausible word timing for {trackId} — evidence written to {folder}");
        });
    }

    /// <summary>Forget ONE track everywhere this provider caches it — memory, the never-downgrade grade ledger and the
    /// on-disk entry — and fetch it again. The inspector's escape hatch: without it, a track played in any earlier
    /// session answers from disk with no round-trip, so there is no raw payload to inspect. Joins an already-running
    /// fan-out for the same track rather than starting a second one.</summary>
    public Task<LyricsDocument?> RefetchAsync(string trackId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackId)) return Task.FromResult<LyricsDocument?>(null);
        lock (_gate)
        {
            _cache.Remove(trackId);
            _lru.Remove(trackId);
            _diskGrade.Remove(trackId);   // the file is about to go, so what we knew about it goes too
        }
        _disk?.Forget(trackId);
        return GetLyricsAsync(trackId, ct);
    }

    void LogReport(string trackId, LyricsRequest req, string summary, IReadOnlyList<LyricsSourceTrace> traces)
    {
        // Debug: the per-search + per-source trace lines are the bulk of the [lyrics] file volume (one search line plus one
        // per candidate source, per track). The single final-winner line stays Info (LogDecision / the caller).
        if (!_log.IsEnabled(WaveeLogLevel.Debug)) return;

        _log.Debug($"search track={trackId} title=\"{LogValue(req.Title)}\" artist=\"{LogValue(req.ArtistsJoined)}\" " +
            $"album=\"{LogValue(req.Album)}\" duration={req.DurationMs}ms isrc={LogValue(req.Isrc ?? "-")} summary=\"{LogValue(summary)}\"");
        foreach (var t in traces)
        {
            _log.Debug($"source track={trackId} id={t.SourceId} outcome={t.Outcome} elapsed={t.ElapsedMs}ms sync={t.Sync} " +
                $"lines={t.LineCount} score={t.Score:F3} winner={t.Winner} detail=\"{LogValue(t.Detail)}\" rerank=\"{LogValue(t.RerankReason)}\"");
        }
    }

    static string LogValue(string s)
        => s.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');

    void LogDecision(string trackId, RankedLyrics ranked, IReadOnlyList<LyricsCandidate> candidates)
    {
        if (ranked.Best is { } b)
            _log.Info($"track={trackId} winner={b.ProviderId} sync={b.Sync} score={b.Score:F3} text={b.TextAgreement:F2} " +
                $"timing={b.TimingScore:F2} offset={b.AppliedOffsetMs}ms candidates=[{string.Join(",", candidates.Select(c => c.ProviderId))}] ({b.Reason})");
    }

    static bool IsRicher(LyricsDocument next, LyricsDocument current) => Grade(next) > Grade(current);

    // ONE monotone "how good is this document" key: richness tier first, then — inside the syllable tier only —
    // syllable count. Promotion (IsRicher) and the never-downgrade disk-write guard (SaveToDiskIfBetter) both order by
    // it, so the two can never disagree about which of two documents is better.
    const long GradeTier = 1_000_000L;

    static long Grade(LyricsDocument doc)
    {
        int r = Richness(doc);
        return r * GradeTier + (r >= 3 ? Math.Min((long)SyllableCount(doc), GradeTier - 1) : 0L);
    }

    static int Richness(LyricsDocument doc)
    {
        if (doc.Lines.Any(l => l.IsWordByWord && l.Syllables.Count > 0)) return 3;
        return doc.Sync switch
        {
            LyricsSyncKind.Syllable => 3,
            LyricsSyncKind.Line => 2,
            LyricsSyncKind.Unsynced => 1,
            _ => 0,
        };
    }

    static int SyllableCount(LyricsDocument doc)
    {
        int n = 0;
        foreach (var l in doc.Lines) n += l.Syllables.Count;
        return n;
    }

    async Task<Probed> FetchOne(ILyricCandidateSource source, LyricsRequest req, LyricsProbe probe, CancellationToken ct)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long Ms() => (long)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        string With(string head) { var n = probe.NotesFor(source.Id); return n.Length > 0 ? head + " — " + n : head; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opt.PerSourceTimeoutMs);
        _log.Debug($"source track={req.TrackId} id={source.Id} started");
        try
        {
            var c = await source.FetchAsync(req, cts.Token).ConfigureAwait(false);
            if (c is null) return new Probed(null, LyricsOutcome.Miss, Ms(), With("no match"));

            // THE cleaning chokepoint. Every provider passes through here with the track's own metadata in scope, and
            // the Spotify document used as the reranker's REFERENCE is itself a candidate — so both sides of every
            // comparison are cleaned by one rule, and `coverage` finally counts lyrics rather than padding.
            var cleaned = LyricsClean.Apply(c.Document, req.Title, req.ArtistsJoined);
            int removed = c.Document.Lines.Count - cleaned.Lines.Count;
            if (cleaned.Lines.Count == 0)
                return new Probed(null, LyricsOutcome.Miss, Ms(), With("every line was provider metadata or instrumental filler"));
            if (removed > 0)
            {
                LyricsProbe.Note(source.Id, $"dropped {removed} non-lyric line(s) (blank/♪/credits/title header)");
                c = c with { Document = cleaned };
            }
            return new Probed(c, LyricsOutcome.Hit, Ms(), With($"{c.Sync}, {c.LineCount} lines, basis={c.Basis}"));
        }
        catch (OperationCanceledException)
        {
            // Our per-source CancelAfter fired = a real timeout; otherwise the aggregate cancelled us (early-exit / caller).
            bool timedOut = cts.IsCancellationRequested && !ct.IsCancellationRequested;
            return new Probed(null, timedOut ? LyricsOutcome.Timeout : LyricsOutcome.Skipped, Ms(),
                timedOut ? With($"timed out (> {_opt.PerSourceTimeoutMs}ms)") : "cancelled");
        }
        catch (Exception e)
        {
            _log.Debug($"source {source.Id} failed for {req.TrackId}: {e.GetType().Name}");
            return new Probed(null, LyricsOutcome.Error, Ms(), With($"{e.GetType().Name}: {e.Message}"));
        }
    }

    /// <summary>Clear the winner cache (e.g. on logout / provider-config change) — BOTH halves. "Clear" has to mean the
    /// next request re-fetches, so the persistent entries (including the negative markers) go too; leaving them would
    /// make a post-logout request keep serving the pre-logout answer forever.</summary>
    public void ClearCache()
    {
        lock (_gate) { _cache.Clear(); _lru.Clear(); _diskGrade.Clear(); }   // the files go too ⇒ so does what we knew about them
        _disk?.Clear();
    }
}
