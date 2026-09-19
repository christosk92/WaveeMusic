// ── Wavee.Tests/EpisodeShapeAuthorityTests.cs — an episode row reloads each group at its OWN authority ──────────────
//
// `EpisodeShape` (Entities/Episode.cs) persists three groups with three authority columns: identity and the about clamp
// are written by the wire (Full), the resume position by the player (Local). The first load restored the whole row at the
// HIGHEST of the three, so once this device had paused an episode, the next launch reloaded its identity at Local too —
// and every later wire refresh of the title, the cover or the about clamp bounced off `Table.Accepts` for good.
//
//   THE SPLIT (pure). `EpisodeShape.RunsOf` turns a persisted row's known groups and three authorities into runs of equal
//   authority — one staged row each, in group order, groups sharing a rung sharing a run.
//   THE ROUND TRIP (a real sqlite file in the temp directory, StoreTests' harness): a row saved with Full identity + Local
//   progress reloads with identity at Full and progress at Local; a newer wire identity then lands, and a Full wire
//   position still cannot rewind the player's. The progress arrives in a SECOND batch on purpose: the row was inserted
//   without it, which is exactly the case the save side must bind its authority column as 0 and never NULL for (sqlite's
//   multi-argument max() is NULL when either side is, so the upsert's merge would have kept that rung NULL for good).

using System.Collections.Concurrent;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class EpisodeShapeRunsTests
{
    const uint Identity = (uint)EpisodeFields.Identity, About = (uint)EpisodeFields.About, Progress = (uint)EpisodeFields.Progress;

    static EpisodeShape.GroupRun[] Runs(uint known, Authority identity, Authority about, Authority progress)
    {
        Span<EpisodeShape.GroupRun> runs = stackalloc EpisodeShape.GroupRun[3];
        int n = EpisodeShape.RunsOf(known, identity, about, progress, runs);
        return runs[..n].ToArray();
    }

    [Fact]
    public void Local_progress_under_a_Full_identity_is_two_runs_each_at_its_own_rung()
        => Assert.Equal(
            new[] { new EpisodeShape.GroupRun(Identity | About, Authority.Full), new EpisodeShape.GroupRun(Progress, Authority.Local) },
            Runs(Identity | About | Progress, Authority.Full, Authority.Full, Authority.Local));

    [Fact]
    public void One_rung_for_every_group_is_one_run()
        => Assert.Equal(new[] { new EpisodeShape.GroupRun(Identity | About | Progress, Authority.Full) },
                        Runs(Identity | About | Progress, Authority.Full, Authority.Full, Authority.Full));

    [Fact]
    public void Three_rungs_are_three_runs_in_group_order()
        => Assert.Equal(
            new[]
            {
                new EpisodeShape.GroupRun(Identity, Authority.Full),
                new EpisodeShape.GroupRun(About, Authority.Thin),
                new EpisodeShape.GroupRun(Progress, Authority.Local),
            },
            Runs(Identity | About | Progress, Authority.Full, Authority.Thin, Authority.Local));

    /// <summary>A group the row does not know is not restored, whatever its authority column says — and a row that knows
    /// nothing restores nothing.</summary>
    [Fact]
    public void Only_the_known_groups_are_restored()
    {
        Assert.Equal(new[] { new EpisodeShape.GroupRun(Progress, Authority.Local) },
                     Runs(Progress, Authority.Full, Authority.Full, Authority.Local));
        Assert.Empty(Runs(0, Authority.Full, Authority.Full, Authority.Local));
    }
}

[Collection(EntitiesCollection.Name)]
public class EpisodeShapeAuthorityTests : IDisposable
{
    const string EpisodeUri = "spotify:episode:0Q86acNRm6V9GYx55SXKwf";
    const long At = 1_788_000_500_000;

    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-episode-auth-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();

    public EpisodeShapeAuthorityTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Post = a => _posted.Enqueue(a);     // the UI drain, run by the test thread where it belongs
        Store.Register(new EpisodeShape());
        Store.Use(_dbPath);
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    void DrainPosts()
    {
        while (_posted.TryDequeue(out Action? a)) a();
    }

    [Fact]
    public void Local_progress_reloads_at_Local_and_leaves_the_identity_at_Full_so_a_newer_wire_identity_still_lands()
    {
        Entities.Boot(CatalogScope.Fake());
        Store.Flush();                             // let Warm resolve the scope id before anything reads or writes
        Scope scope = Entities.Current;
        EpisodeTable episodes = scope.Episodes;
        Assert.True(EntityId.TryParseGid(EpisodeUri.AsSpan(), out EntityId id));
        int slot = episodes.Slot(id);

        // The wire's identity + about clamp at Full, then the player's own pause at Local: two batches, as in the app.
        Staging wire = Staging.Rent();
        ref StagedEpisode first = ref wire.Episodes.RowFor(id, Authority.Full, (uint)(EpisodeFields.Identity | EpisodeFields.About));
        first.Title = wire.Text("Episode One");
        first.Description = wire.Text("About one");
        first.DurationMs = 1_800_000;
        Assert.True(Store.WriteBehind(wire));      // the store owns the staging from here
        Staging local = Staging.Rent();
        Entities.StageLocalProgress(local, id, 600_000, At);
        Assert.True(Store.WriteBehind(local));
        Store.Flush();

        // A genuinely COLD read into a fresh slot: what the FILE restores, not what memory remembers.
        episodes.FreeSlot(slot);
        int cold = episodes.Slot(id);
        Assert.True(Store.Read(scope, episodes, new[] { cold }, (uint)EpisodeFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();

        Assert.Equal("Episode One", Entities.Strings.Resolve(episodes.Title[cold]));
        Assert.Equal(600_000, episodes.ProgressMs[cold]);
        Assert.Equal((byte)Authority.Full, episodes.IdentityAuthority[cold]);
        Assert.Equal((byte)Authority.Full, episodes.AboutAuthority[cold]);
        Assert.Equal((byte)Authority.Local, episodes.ProgressAuthority[cold]);

        // The next wire answer refreshes the identity and the about clamp — and still cannot rewind the player's position.
        Staging next = Staging.Rent();
        ref StagedEpisode again = ref next.Episodes.RowFor(id, Authority.Full,
            (uint)(EpisodeFields.Identity | EpisodeFields.About | EpisodeFields.Progress));
        again.Title = next.Text("Episode One (remastered)");
        again.Description = next.Text("About one, revised");
        again.DurationMs = 1_800_000;
        again.ProgressMs = 100_000;
        TestScope.CommitAndPublish(next);

        Assert.Equal("Episode One (remastered)", Entities.Strings.Resolve(episodes.Title[cold]));
        Assert.Equal("About one, revised", Entities.Strings.Resolve(episodes.Description[cold]));
        Assert.Equal(600_000, episodes.ProgressMs[cold]);
        Assert.Equal((byte)Authority.Local, episodes.ProgressAuthority[cold]);
    }
}
