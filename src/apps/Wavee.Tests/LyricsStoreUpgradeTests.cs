// ── Wavee.Tests/LyricsStoreUpgradeTests.cs — Lyrics.Store.Upgrade, the AI aligner's seam ─────────────────────────────
//
// The store is process-wide static state, so this class runs alone (its own non-parallel collection). The aggregator
// gets an in-memory fake source and a temp-directory disk cache; the real profile is never touched. ToUi keeps its
// synchronous default, so every commit is visible as soon as the call returns.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LyricsStoreCollection
{
    public const string Name = "lyrics-store";
}

[Collection(LyricsStoreCollection.Name)]
public class LyricsStoreUpgradeTests
{
    sealed class OneSource : Lyrics.ISource
    {
        public string Id => "spotify";
        public bool Enabled => true;
        public double Prior => 0.5;
        public Task<Lyrics.Candidate?> FetchAsync(Lyrics.Request req, CancellationToken ct)
            => Task.FromResult<Lyrics.Candidate?>(new Lyrics.Candidate(Id, Prior, Lyrics.MatchBasis.Identity, LineDoc(req.TrackId)));
    }

    static Lyrics.Doc LineDoc(string id) => new(id, true,
        [new Lyrics.Line(1000, "one two three", []), new Lyrics.Line(5000, "four five six", []), new Lyrics.Line(9000, "seven eight nine", [])],
        Lyrics.SyncKind.Line, "spotify");

    static Lyrics.Doc Generated(string id, int wordLines)
    {
        var src = LineDoc(id);
        var lines = new List<Lyrics.Line>();
        for (int i = 0; i < src.Lines.Count; i++)
        {
            var l = src.Lines[i];
            lines.Add(i < wordLines
                ? l with { Syllables = [new Lyrics.Syllable(l.StartMs, l.StartMs + 400, "one "), new Lyrics.Syllable(l.StartMs + 400, l.StartMs + 900, "two "), new Lyrics.Syllable(l.StartMs + 900, l.StartMs + 1500, "three")], IsWordByWord = true }
                : l);
        }
        return src with { Lines = lines, Sync = Lyrics.SyncKind.Syllable, Provider = AiLyrics.ProviderId, Origin = "spotify", Generated = true };
    }

    static async Task<(string Id, string Dir)> AnsweredTrack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var agg = new Lyrics.Aggregator([new OneSource()],
            (id, _) => Task.FromResult<Lyrics.Request?>(new Lyrics.Request(id, "spotify:track:" + id, "Song", ["Artist"], "Album", 200_000)),
            new Lyrics.Options(PerSourceTimeoutMs: 5000, TotalTimeoutMs: 5000, FirstHitGraceMs: 0), diskCache: new Lyrics.DiskCache(dir));
        Lyrics.Store.Attach(agg);
        string id = "store-" + Guid.NewGuid().ToString("N");
        Lyrics.Store.Ensure(id);
        for (int i = 0; i < 200 && !Lyrics.Store.Answered(id); i++) await Task.Delay(25);
        Assert.True(Lyrics.Store.Answered(id));
        return (id, dir);
    }

    static void Cleanup(string dir)
    {
        Lyrics.Store.Attach(null);
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task A_generated_doc_for_an_answered_track_replaces_the_line_doc_and_bumps_Upgraded()
    {
        var (id, dir) = await AnsweredTrack();
        try
        {
            uint before = Lyrics.Store.Upgraded.Value;
            Lyrics.Store.Upgrade(Generated(id, 1));
            var doc = Lyrics.Store.Doc(id);
            Assert.NotNull(doc);
            Assert.True(doc!.Generated);
            Assert.Equal(before + 1, Lyrics.Store.Upgraded.Value);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task A_later_revision_with_more_word_lines_replaces_the_earlier_one()
    {
        var (id, dir) = await AnsweredTrack();
        try
        {
            Lyrics.Store.Upgrade(Generated(id, 1));
            Lyrics.Store.Upgrade(Generated(id, 3));
            Assert.Equal(3, Lyrics.Store.Doc(id)!.Lines.Count(l => l.IsWordByWord));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task A_doc_that_is_not_generated_is_refused()
    {
        var (id, dir) = await AnsweredTrack();
        try
        {
            Lyrics.Store.Upgrade(Generated(id, 2) with { Generated = false });
            Assert.False(Lyrics.Store.Doc(id)!.Generated);
            Assert.Equal(Lyrics.SyncKind.Line, Lyrics.Store.Doc(id)!.Sync);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task A_track_the_store_never_answered_is_ignored()
    {
        var (_, dir) = await AnsweredTrack();
        try
        {
            string other = "never-" + Guid.NewGuid().ToString("N");
            Lyrics.Store.Upgrade(Generated(other, 1));
            Assert.False(Lyrics.Store.Answered(other));
            Assert.Null(Lyrics.Store.Doc(other));
        }
        finally { Cleanup(dir); }
    }
}
