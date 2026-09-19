// ── Wavee.Tests/PaletteStoreTests.cs — the palette's disk half, against a real temp file ────────────────────────────
//
// Wave 1's gate for `Entities/Store.Palette.cs` (plan §WS-C), in `StoreTests.cs`'s own shape: a real sqlite file in
// the temp directory, `Store.Post` redirected to a queue this test drains itself, `Store.Flush()` to wait for the
// store thread.
//
// THE ONE THING A SINGLE PROCESS CANNOT FAKE: `Palette.Images` is process-wide (Palette.cs's file header) and a
// `Store.Shutdown`/`Store.Boot` cycle does not clear it — a real relaunch would start with an empty table, this test
// process does not. So "the row survived a restart" is exercised by blanking the live slot's columns by hand right
// before the reboot (they are public columns on `Palette.Images`, exactly what `PaletteTests.cs` already reads
// directly) — which is the same state a cold launch would have found — rather than by asserting something that would
// already be true from the row never having left memory.
//
// The class joins `EntitiesCollection` for the same reason `PaletteTests.cs` does: the palette table it exercises is
// shared by every test in the run.

using System.Collections.Concurrent;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PaletteStoreTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-palette-store-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();

    public PaletteStoreTests()
    {
        Store.Shutdown();
        Store.Post = a => _posted.Enqueue(a);
        Store.Use(_dbPath);
    }

    public void Dispose()
    {
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    void Boot()
    {
        Entities.Boot(CatalogScope.Fake());
        Store.Flush();
        DrainPosts();
    }

    void DrainPosts()
    {
        while (_posted.TryDequeue(out Action? a)) a();
    }

    /// <summary>Flush every dirty slot to disk, however many batches that takes. The palette table is process-wide
    /// (Palette.cs's file header), so a run's other fact classes may leave rows dirty; this clears them to a known
    /// baseline before a fact starts counting.</summary>
    void DrainAllDirty()
    {
        while (Palette.DirtyCount > 0)
        {
            Store.ArmPaletteFlush();
            Store.FlushPalette();
            Store.Flush();
        }
    }

    static string Url(string id) => "https://i.scdn.co/image/" + id;

    /// <summary>A believable 40-hex Spotify image id (see <c>PaletteTests.Id</c>): the size marker plus a unique
    /// 24-char tail, so each fact keys its own row in the process-wide table.</summary>
    static string Id(string tail24) => "ab67616d0000b273" + tail24;

    /// <summary>Blank a slot's live columns to what a cold launch would find: nothing. Only the columns
    /// <see cref="Palette.Restore"/> reads — <c>Known</c>/<c>Ts</c>/<c>Dark</c>/<c>Light</c> — never the identity, so
    /// the row's slot and key stay exactly as they were.</summary>
    static void ForgetLive(int slot)
    {
        Palette.Images.Known[slot] = 0;
        Palette.Images.Ts[slot] = 0;
        Palette.Images.Dark[slot] = default;
        Palette.Images.Light[slot] = default;
    }

    [Fact]
    public void A_graded_row_survives_a_restart_and_the_reload_asks_nothing()
    {
        Boot();
        DrainAllDirty();                                 // a clean baseline before this fact counts anything
        Entities.Now = 1_000_000;
        string id = Id("111111111111111111111111");
        var dark = new Scheme(0xFF102030, 0xFF203040, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF);
        var light = new Scheme(0xFFE0E8F0, 0xFFD0D8E0, 0xFF000000, 0xFF555555, 0xFF000000);
        Palette.SetGraded(id.AsSpan(), dark, light, hasLight: true, bestFitIsLight: true);
        int slot = Palette.Images.Slot(Palette.KeyOf(Url(id).AsSpan()));

        Assert.Equal(1, Palette.DirtyCount);
        DrainAllDirty();                                 // the write transaction has landed
        Assert.Equal(0, Palette.DirtyCount);

        Store.Shutdown();
        ForgetLive(slot);                                // what a real relaunch's empty table would look like
        Assert.Equal(0u, Palette.Images.Known[slot]);

        Store.Boot();
        Store.Flush();
        DrainPosts();                                    // runs Palette.Restore(loaded) posted from the warm read

        int pendingBefore = Palette.Pending;
        Assert.True(Palette.TryScheme(Url(id).AsSpan(), lightTheme: false, out Scheme gotDark));
        Assert.Equal(dark, gotDark);
        Assert.True(Palette.TryScheme(Url(id).AsSpan(), lightTheme: true, out Scheme gotLight));
        Assert.Equal(light, gotLight);
        Assert.Equal(pendingBefore, Palette.Pending);     // a hit off the restored row, never a new request
    }

    [Fact]
    public void A_row_past_its_ttl_on_disk_is_not_restored()
    {
        Boot();
        DrainAllDirty();
        // Write it as ANSWERED far enough in the past that it is already outside the hit TTL when the warm read's
        // SQL cutoff (and FreshOnLoad) look at it: negative app seconds map to unix time well before `Store.Epoch`.
        Entities.Now = -(Palette.HitTtlSeconds + 10_000);
        string id = Id("222222222222222222222222");
        Palette.SetGraded(id.AsSpan(), new Scheme(0xFF010203, 0, 0, 0, 0), default, hasLight: false, bestFitIsLight: false);
        int slot = Palette.Images.Slot(Palette.KeyOf(Url(id).AsSpan()));

        DrainAllDirty();

        Store.Shutdown();
        ForgetLive(slot);
        Entities.Now = 0;

        Store.Boot();
        Store.Flush();
        DrainPosts();

        int before = Palette.Pending;
        Assert.False(Palette.TryScheme(Url(id).AsSpan(), lightTheme: false, out _));   // still empty: not restored
        Assert.Equal(before + 1, Palette.Pending);                                     // and asked for again
    }

    [Fact]
    public void A_fresher_live_grading_wins_over_a_late_restore()
    {
        Boot();
        DrainAllDirty();
        Entities.Now = 1_000_000;
        string id = Id("333333333333333333333333");
        var stale = new Scheme(0xFFAAAAAA, 0, 0, 0, 0);
        Palette.SetGraded(id.AsSpan(), stale, default, hasLight: false, bestFitIsLight: false);
        int slot = Palette.Images.Slot(Palette.KeyOf(Url(id).AsSpan()));

        DrainAllDirty();                                  // the stale grading is now the disk's whole answer

        Store.Shutdown();
        Store.Boot();
        Store.Flush();                                  // WarmPaletteCore ran and posted Restore — NOT drained yet

        // The network answers again, newer, before the posted restore is applied — exactly the race a boot and a
        // fast provider answer can produce.
        Entities.Now = 2_000_000;
        var fresh = new Scheme(0xFFBBBBBB, 0, 0, 0, 0);
        Palette.SetGraded(id.AsSpan(), fresh, default, hasLight: false, bestFitIsLight: false);

        DrainPosts();                                    // Palette.Restore(loaded) runs now, with the STALE snapshot

        Assert.True(Palette.TryScheme(Url(id).AsSpan(), lightTheme: false, out Scheme got));
        Assert.Equal(fresh, got);                         // the live answer was not clobbered by the late restore
        Assert.Equal(fresh, Palette.Images.Dark[slot]);
    }
}
