using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Lyrics;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Lyrics;

// LyricsDiskCache (wave H): the persistent half of the aggregator's winner cache — a versioned JSON envelope per track
// under a cache root, read through BEFORE the source fan-out so a previously-played track resolves offline.
//
// Every test injects its own temp directory (and, where TTL matters, its own clock); the real %LOCALAPPDATA% is never
// touched. The last two tests drive the cache THROUGH AggregatingLyricsProvider, which is what actually pins the
// offline promise: a second provider over the same directory must serve without calling a single source.
public class LyricsDiskCacheTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-lyrics-cache-tests", Guid.NewGuid().ToString("n"));

    public LyricsDiskCacheTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } }

    const long Now = 1753000000000L;
    LyricsDiskCache Cache(long? now = null, TimeSpan? negativeTtl = null, int maxFiles = LyricsDiskCache.DefaultMaxFiles)
        => new(_dir, nowUnixMs: () => now ?? Now, maxFiles: maxFiles, negativeTtl: negativeTtl);

    static LyricsDocument WordDoc(string trackId = "t1") => new(
        trackId, true,
        new List<LyricLine>
        {
            new(1000, "line one here",
                new List<LyricSyllable> { new(1000, 1400, "line"), new(1400, 1700, "one"), new(1700, 2200, "here") },
                EndMs: 2200, Translation: "eerste regel", Romanization: "rain wan hia", IsWordByWord: true),
            new(5000, "line two there", new List<LyricSyllable> { new(5000, 5800, "line two there") },
                EndMs: 5800, IsWordByWord: true),
        },
        LyricsSyncKind.Syllable, "amll", OffsetMsApplied: -500);

    // The same two lines as WordDoc, LINE-synced (richness 2) — the "an earlier session cached the poor version" seed.
    static LyricsDocument LineDoc(string provider = "seed", string trackId = "t1") => new(
        trackId, true,
        new List<LyricLine>
        {
            new(1000, "line one here", Array.Empty<LyricSyllable>(), EndMs: 2200),
            new(5000, "line two there", Array.Empty<LyricSyllable>(), EndMs: 5800),
        },
        LyricsSyncKind.Line, provider);

    // Richness 1 — strictly WORSE than LineDoc, so a background upgrade that finds it must change nothing.
    static LyricsDocument UnsyncedDoc(string provider = "lrclib", string trackId = "t1") => new(
        trackId, false,
        new List<LyricLine>
        {
            new(0, "line one here", Array.Empty<LyricSyllable>()),
            new(0, "line two there", Array.Empty<LyricSyllable>()),
        },
        LyricsSyncKind.Unsynced, provider);

    static void AssertSameDocument(LyricsDocument expected, LyricsDocument actual)
    {
        Assert.Equal(expected.TrackId, actual.TrackId);
        Assert.Equal(expected.IsSynced, actual.IsSynced);
        Assert.Equal(expected.Sync, actual.Sync);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.OffsetMsApplied, actual.OffsetMsApplied);
        Assert.Equal(expected.Lines.Count, actual.Lines.Count);
        for (int i = 0; i < expected.Lines.Count; i++)
        {
            var e = expected.Lines[i];
            var a = actual.Lines[i];
            Assert.Equal(e.StartMs, a.StartMs);
            Assert.Equal(e.Text, a.Text);
            Assert.Equal(e.EndMs, a.EndMs);
            Assert.Equal(e.Translation, a.Translation);
            Assert.Equal(e.Romanization, a.Romanization);
            Assert.Equal(e.IsWordByWord, a.IsWordByWord);
            Assert.Equal(e.Syllables.Count, a.Syllables.Count);
            for (int s = 0; s < e.Syllables.Count; s++)
            {
                Assert.Equal(e.Syllables[s].StartMs, a.Syllables[s].StartMs);
                Assert.Equal(e.Syllables[s].EndMs, a.Syllables[s].EndMs);
                Assert.Equal(e.Syllables[s].Text, a.Syllables[s].Text);
            }
        }
    }

    // ── round trip ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_RestoresEveryFieldIncludingSyllablesAndSecondaryLines()
    {
        var cache = Cache();
        var doc = WordDoc();

        await cache.SaveAsync("t1", doc);
        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.Hit, entry.Outcome);
        Assert.Equal(Now, entry.SavedAtUnixMs);
        AssertSameDocument(doc, entry.Document!);
    }

    [Fact]
    public async Task RoundTrip_SurvivesANewCacheInstance_AndTheFileIsOnDiskUnderTheTrackHash()
    {
        await Cache().SaveAsync("t1", WordDoc());

        Assert.True(File.Exists(Cache().PathFor("t1")));
        Assert.Equal(LyricsCacheOutcome.Hit, (await Cache().TryLoadAsync("t1")).Outcome);
    }

    [Fact]
    public async Task UnknownTrack_IsAMiss()
    {
        var entry = await Cache().TryLoadAsync("never-seen");
        Assert.Equal(LyricsCacheOutcome.Miss, entry.Outcome);
        Assert.Null(entry.Document);
    }

    // ── envelope discipline ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VersionMismatch_IsAMissAndDeletesTheFile()
    {
        var cache = Cache();
        await cache.SaveAsync("t1", WordDoc());
        string path = cache.PathFor("t1");

        string json = File.ReadAllText(path);
        string bumped = json.Replace("\"v\":" + LyricsDiskCache.SchemaVersion, "\"v\":999", StringComparison.Ordinal);
        Assert.NotEqual(json, bumped);   // the envelope really does carry the version we think it does
        File.WriteAllText(path, bumped);

        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.Miss, entry.Outcome);
        Assert.False(File.Exists(path));   // a superseded entry is dropped, not left to be re-read forever
    }

    [Fact]
    public async Task CorruptFile_IsAMissAndDeletesTheFile_WithoutThrowing()
    {
        var cache = Cache();
        string path = cache.PathFor("t1");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{\"v\":1,\"at\":123,\"doc\":{\"lines\":[{ truncated");

        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.Miss, entry.Outcome);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task EmptyDocument_IsAMissAndDeletesTheFile()
    {
        var cache = Cache();
        await cache.SaveAsync("t1", new LyricsDocument("t1", false, Array.Empty<LyricLine>(), LyricsSyncKind.None, "lrclib"));

        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.Miss, entry.Outcome);
        Assert.False(File.Exists(cache.PathFor("t1")));
    }

    // ── negative cache ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NegativeMarker_IsKnownMissingWhileFresh()
    {
        long now = Now;
        var cache = new LyricsDiskCache(_dir, nowUnixMs: () => now, negativeTtl: TimeSpan.FromDays(3));

        await cache.SaveAsync("t1", null);
        now += (long)TimeSpan.FromDays(2).TotalMilliseconds;
        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.KnownMissing, entry.Outcome);
        Assert.Null(entry.Document);
        Assert.Equal(Now, entry.SavedAtUnixMs);
        Assert.True(File.Exists(cache.PathFor("t1")));
    }

    [Fact]
    public async Task NegativeMarker_ExpiresToAMiss_AndIsDeletedSoTheNextPlayRefetches()
    {
        long now = Now;
        var cache = new LyricsDiskCache(_dir, nowUnixMs: () => now, negativeTtl: TimeSpan.FromDays(3));

        await cache.SaveAsync("t1", null);
        now += (long)TimeSpan.FromDays(4).TotalMilliseconds;
        var entry = await cache.TryLoadAsync("t1");

        Assert.Equal(LyricsCacheOutcome.Miss, entry.Outcome);
        Assert.False(File.Exists(cache.PathFor("t1")));
    }

    // ── LRU sweep ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_OverTheFileCap_TrimsToEightyPercent_OldestFirst()
    {
        const int cap = 20, written = 25, expected = 16;   // 80% of 20
        var cache = Cache(maxFiles: cap);
        var paths = new string[written];
        for (int i = 0; i < written; i++)
        {
            string id = "track-" + i;
            await cache.SaveAsync(id, WordDoc(id));
            paths[i] = cache.PathFor(id);
            // The save time IS the file's write time; stamp it explicitly so "oldest first" is deterministic.
            File.SetLastWriteTimeUtc(paths[i], new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
        }

        cache.Sweep();

        Assert.Equal(expected, Directory.EnumerateFiles(_dir, "*.json").Count());
        for (int i = 0; i < written - expected; i++) Assert.False(File.Exists(paths[i]), $"entry {i} should have been evicted");
        for (int i = written - expected; i < written; i++) Assert.True(File.Exists(paths[i]), $"entry {i} should have survived");
    }

    [Fact]
    public async Task Sweep_UnderTheCap_KeepsEverything()
    {
        var cache = Cache(maxFiles: 20);
        for (int i = 0; i < 5; i++) await cache.SaveAsync("track-" + i, WordDoc("track-" + i));

        cache.Sweep();

        Assert.Equal(5, Directory.EnumerateFiles(_dir, "*.json").Count());
    }

    [Fact]
    public async Task Sweep_IsArmedByTheFirstUse_AndOnlyOnce()
    {
        var cache = Cache(maxFiles: 20);
        Assert.True(cache.SweepInFlight.IsCompleted);   // nothing armed before first use

        await cache.SaveAsync("t1", WordDoc());
        await cache.SweepInFlight;                      // armed, and a read never waits on it in production

        Assert.True(File.Exists(cache.PathFor("t1")));
    }

    [Fact]
    public async Task Clear_DropsEveryEntryButLeavesForeignFilesAlone()
    {
        var cache = Cache();
        await cache.SaveAsync("t1", WordDoc("t1"));
        await cache.SaveAsync("t2", null);
        string foreign = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(foreign, "not ours");

        cache.Clear();

        Assert.Empty(Directory.EnumerateFiles(_dir, "*.json"));
        Assert.True(File.Exists(foreign));
    }

    // ── through the aggregator: the actual offline promise ────────────────────────────────────────────────────────

    sealed class CountingSource : ILyricCandidateSource
    {
        readonly LyricsCandidate? _result;
        public int Calls;
        public CountingSource(string id, LyricsCandidate? result) { Id = id; _result = result; }
        public string Id { get; }
        public bool Enabled => true;
        public double Prior => 0.9;
        public Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(_result); }
    }

    static LyricsRequest Req() => new("t1", "spotify:track:t1", "Test Song", new[] { "Artist" }, "Album", 200000);

    async Task WaitForFile(string path, bool shouldExist)
    {
        for (int i = 0; i < 200 && File.Exists(path) != shouldExist; i++) await Task.Delay(10);
        Assert.Equal(shouldExist, File.Exists(path));
    }

    [Fact]
    public async Task Provider_SecondSession_ServesFromDisk_WithoutQueryingAnySource()
    {
        var doc = WordDoc();
        var first = new CountingSource("amll", new LyricsCandidate("amll", 0.9, MatchBasis.Identity, doc));
        var online = new AggregatingLyricsProvider(new ILyricCandidateSource[] { first }, (_, _) => Task.FromResult<LyricsRequest?>(Req()),
            diskCache: Cache());
        var live = await online.GetLyricsAsync("t1");
        Assert.NotNull(live);
        Assert.Equal(1, first.Calls);
        await WaitForFile(Cache().PathFor("t1"), shouldExist: true);

        // A fresh process (fresh provider, empty in-memory LRU) with NO working sources and NO resolvable request —
        // i.e. offline — still has the words.
        var offlineSource = new CountingSource("amll", null);
        var offline = new AggregatingLyricsProvider(new ILyricCandidateSource[] { offlineSource },
            (_, _) => Task.FromResult<LyricsRequest?>(null), diskCache: Cache());

        var restored = await offline.GetLyricsAsync("t1");

        Assert.NotNull(restored);
        AssertSameDocument(live!, restored!);   // exactly the document the online session won, syllables and all
        Assert.Equal(0, offlineSource.Calls);   // no fan-out at all
    }

    [Fact]
    public async Task Provider_NothingAnywhere_WritesANegativeMarker_AndTheNextSessionSkipsTheFanOut()
    {
        var missing = new CountingSource("amll", null);
        var provider = new AggregatingLyricsProvider(new ILyricCandidateSource[] { missing },
            (_, _) => Task.FromResult<LyricsRequest?>(Req()), diskCache: Cache());

        Assert.Null(await provider.GetLyricsAsync("t1"));
        Assert.Equal(1, missing.Calls);
        await WaitForFile(Cache().PathFor("t1"), shouldExist: true);

        var next = new CountingSource("amll", null);
        var second = new AggregatingLyricsProvider(new ILyricCandidateSource[] { next },
            (_, _) => Task.FromResult<LyricsRequest?>(Req()), diskCache: Cache());

        Assert.Null(await second.GetLyricsAsync("t1"));
        Assert.Equal(0, next.Calls);   // the negative marker answered inside its TTL
    }

    [Fact]
    public async Task Provider_ClearCache_ClearsDiskToo_SoTheNextRequestRefetches()
    {
        var doc = WordDoc();
        var src = new CountingSource("amll", new LyricsCandidate("amll", 0.9, MatchBasis.Identity, doc));
        var provider = new AggregatingLyricsProvider(new ILyricCandidateSource[] { src },
            (_, _) => Task.FromResult<LyricsRequest?>(Req()), diskCache: Cache());

        Assert.NotNull(await provider.GetLyricsAsync("t1"));
        await WaitForFile(Cache().PathFor("t1"), shouldExist: true);

        provider.ClearCache();

        Assert.False(File.Exists(Cache().PathFor("t1")));
        Assert.NotNull(await provider.GetLyricsAsync("t1"));
        Assert.Equal(2, src.Calls);   // memory AND disk were cleared, so the source ran again
    }

    // ── a cached document is never a DEAD END ─────────────────────────────────────────────────────────────────────
    // Positive entries have no TTL by design, so before the background upgrade below a LOW-RICHNESS document cached in
    // an earlier session was permanent: the read-through short-circuited resolve + fan-out + upgrade forever and the
    // track could never reach syllable lyrics.

    // A source whose answer the test releases, so "two callers, one fan-out" and "the winner write waits for the
    // upgrade" are pinned by construction rather than by a sleep.
    sealed class GatedSource : ILyricCandidateSource
    {
        readonly LyricsCandidate? _result;
        readonly Task _gate;
        public int Calls;
        public GatedSource(string id, LyricsCandidate? result, Task gate) { Id = id; _result = result; _gate = gate; }
        public string Id { get; }
        public bool Enabled => true;
        public double Prior => 0.9;
        public async Task<LyricsCandidate?> FetchAsync(LyricsRequest req, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await _gate.ConfigureAwait(false);
            return _result;
        }
    }

    static async Task WaitUntil(Func<Task<bool>> condition, string what)
    {
        for (int i = 0; i < 300; i++)
        {
            if (await condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("timed out waiting for " + what);
    }

    async Task<LyricsDocument?> OnDisk() => (await Cache().TryLoadAsync("t1")).Document;

    [Fact]
    public async Task Provider_LineSyncedDiskHit_ServesItThenUpgradesInTheBackground_AndTheSyllableDocOverwritesDisk()
    {
        await Cache().SaveAsync("t1", LineDoc());

        var src = new CountingSource("amll", new LyricsCandidate("amll", 0.9, MatchBasis.Identity, WordDoc()));
        var provider = new AggregatingLyricsProvider(new ILyricCandidateSource[] { src },
            (_, _) => Task.FromResult<LyricsRequest?>(Req()), diskCache: Cache());
        var upgrades = new List<LyricsDocument>();
        provider.LyricsUpgraded.Subscribe(Observers.From<LyricsDocument>(d => { lock (upgrades) upgrades.Add(d); }));

        var served = await provider.GetLyricsAsync("t1");

        Assert.Equal(LyricsSyncKind.Line, served!.Sync);   // instant, from disk — the offline promise is untouched
        // …and the fan-out still ran behind it, promoted the richer document and re-persisted it.
        await WaitUntil(async () => (await OnDisk())?.Sync == LyricsSyncKind.Syllable, "the promoted document on disk");
        Assert.Equal(1, src.Calls);
        lock (upgrades) Assert.Single(upgrades);
        lock (upgrades) Assert.Equal(LyricsSyncKind.Syllable, upgrades[0].Sync);
        Assert.Equal(LyricsSyncKind.Syllable, (await provider.GetLyricsAsync("t1"))!.Sync);   // memory promoted too
    }

    [Fact]
    public async Task Provider_BackgroundUpgradeThatFindsSomethingWorse_NeverDowngradesTheDisk()
    {
        await Cache().SaveAsync("t1", LineDoc());

        var worse = new CountingSource("lrclib", new LyricsCandidate("lrclib", 0.4, MatchBasis.MetadataSearch, UnsyncedDoc()));
        var provider = new AggregatingLyricsProvider(new ILyricCandidateSource[] { worse },
            (_, _) => Task.FromResult<LyricsRequest?>(Req()), diskCache: Cache());

        var served = await provider.GetLyricsAsync("t1");

        Assert.Equal(LyricsSyncKind.Line, served!.Sync);
        await WaitUntil(() => Task.FromResult(worse.Calls == 1), "the background fan-out to run");
        // A Save never downgrades: the unsynced winner is worse than what the file already holds, so nothing is
        // written — for the whole window in which a stray write could have landed.
        for (int i = 0; i < 25; i++)
        {
            var doc = await OnDisk();
            Assert.Equal(LyricsSyncKind.Line, doc!.Sync);
            Assert.Equal("seed", doc.Provider);
            await Task.Delay(10);
        }
        Assert.Equal(LyricsSyncKind.Line, (await provider.GetLyricsAsync("t1"))!.Sync);   // nor is memory downgraded
    }

    [Fact]
    public async Task Provider_SyllableDiskHit_ShortCircuitsCompletely_NoResolveAndNoSourceCall()
    {
        await Cache().SaveAsync("t1", WordDoc());

        int resolves = 0;
        var src = new CountingSource("amll", new LyricsCandidate("amll", 0.9, MatchBasis.Identity, WordDoc()));
        var provider = new AggregatingLyricsProvider(new ILyricCandidateSource[] { src },
            (_, _) => { Interlocked.Increment(ref resolves); return Task.FromResult<LyricsRequest?>(Req()); },
            diskCache: Cache());

        var served = await provider.GetLyricsAsync("t1");

        Assert.Equal(LyricsSyncKind.Syllable, served!.Sync);
        await Task.Delay(200);          // a background upgrade would have resolved + fanned out well inside this
        Assert.Equal(0, src.Calls);     // already at the top of the ladder ⇒ nothing to upgrade to
        Assert.Equal(0, resolves);      // and the resolve (which can itself hit the network) never happens either
    }

    [Fact]
    public async Task Provider_ContinuationOwnsTheDiskWrite_SoTheLineWinnerNeverRacesTheSyllableUpgrade()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new CountingSource("lrclib", new LyricsCandidate("lrclib", 0.4, MatchBasis.MetadataSearch, LineDoc("lrclib")));
        var slow = new GatedSource("amll", new LyricsCandidate("amll", 0.9, MatchBasis.Identity, WordDoc()), gate.Task);
        var provider = new AggregatingLyricsProvider(new ILyricCandidateSource[] { fast, slow },
            (_, _) => Task.FromResult<LyricsRequest?>(Req()), new LyricsOptions(FirstHitGraceMs: 0), diskCache: Cache());

        var first = await provider.GetLyricsAsync("t1");

        Assert.Equal(LyricsSyncKind.Line, first!.Sync);         // served on the grace window
        Assert.False(File.Exists(Cache().PathFor("t1")));       // …but NOT written: the continuation owns the write,
                                                                // so the two fire-and-forget saves cannot race.
        gate.SetResult();
        await WaitUntil(async () => (await OnDisk())?.Sync == LyricsSyncKind.Syllable, "the upgraded document on disk");
    }

    [Fact]
    public async Task Provider_TwoConcurrentCallsForOneTrack_ShareASingleFanOut()
    {
        // The rail lyrics panel and the immersive surface each mount their own doc host: same track, two concurrent
        // requests, which used to be two full fan-outs racing each other to the same file.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var src = new GatedSource("amll", new LyricsCandidate("amll", 0.9, MatchBasis.Identity, WordDoc()), gate.Task);
        int resolves = 0;
        var provider = new AggregatingLyricsProvider(new ILyricCandidateSource[] { src },
            (_, _) => { Interlocked.Increment(ref resolves); return Task.FromResult<LyricsRequest?>(Req()); },
            diskCache: Cache());

        var rail = provider.GetLyricsAsync("t1");
        var immersive = provider.GetLyricsAsync("t1");
        gate.SetResult();
        var railDoc = await rail;
        var immersiveDoc = await immersive;

        Assert.NotNull(railDoc);
        Assert.Same(railDoc, immersiveDoc);   // one shared task ⇒ literally the same document instance
        Assert.Equal(1, src.Calls);
        Assert.Equal(1, resolves);
        await WaitForFile(Cache().PathFor("t1"), shouldExist: true);   // and exactly one writer reached the file
    }
}
