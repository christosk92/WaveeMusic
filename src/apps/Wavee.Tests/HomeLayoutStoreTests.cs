// ── Wavee.Tests/HomeLayoutStoreTests.cs — home-layout.json: fault classes, the .bak, the preferences over it ─────────
//
// Wave 5, owner P. The store is 0.2.9's fail-soft contract: a first run is not a fault; a too-new or unreadable file
// blocks every write and is left untouched; a malformed primary recovers from a good .bak (and stays writable), and
// without one it is Corrupt, blocked and untouched until "Start fresh" sets it aside. `HomePreferences` is the one owner
// over it: an accepted command publishes and persists, a rejected one does neither.
//
// Every fact runs in its OWN temp directory (never the real profile folder — `DefaultPath`, `ForApp` and
// `HomePreferences.Current` are not called here) and deletes it on dispose.

using System.Text;
using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]   // HomePreferences publishes through an engine signal; keep it off the parallel lanes
public sealed class HomeLayoutStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-home-layout-store-tests", Guid.NewGuid().ToString("n"));
    readonly string _path;

    public HomeLayoutStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = HomeLayoutStore.PathUnder(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    HomeLayoutStore Store() => new(_path);

    static HomeLayoutDocDto Envelope(HomeLayoutDoc layout) => HomeLayoutWire.Write(layout, null);

    static void CommitAndWait(HomeLayoutStore store, HomeLayoutDocDto doc)
    {
        store.Commit(doc);
        Assert.True(store.WaitForWrites(10_000), "the pool write did not finish inside 10 s");
    }

    void WritePrimary(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, json);
    }

    // ── the path ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PathUnder_ComposesWaveeMusicHomeLayoutJson_AndTheSiblings()
    {
        Assert.Equal(Path.Combine(_dir, "WaveeMusic", "home-layout.json"), HomeLayoutStore.PathUnder(_dir));
        var store = Store();
        Assert.Equal(_path, store.FilePath);
        Assert.Equal(_path + ".bak", store.BakPath);
        Assert.Equal(_path + ".tmp", store.TmpPath);
        Assert.Equal(_path + ".corrupt", store.CorruptPath);
    }

    // ── load ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstRun_IsNotAFault()
    {
        var store = Store();
        var load = store.Load();
        Assert.Null(load.Doc);
        Assert.Equal(HomeLayoutLoadFault.None, load.Fault);
        Assert.False(store.WritesBlocked);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void AGoodFile_Loads()
    {
        WritePrimary("""{ "version": 1, "modules": [ { "kind": "mixBand", "hidden": true }, { "kind": "hero" } ] }""");

        var store = Store();
        var load = store.Load();

        Assert.Equal(HomeLayoutLoadFault.None, load.Fault);
        Assert.Null(load.Detail);
        Assert.False(store.WritesBlocked);
        var layout = HomeLayoutWire.Read(load.Doc).Layout;
        Assert.Equal(HomeGroupKind.MixBand, layout.Modules[0].Kind);
        Assert.True(layout.IsHidden(HomeGroupKind.MixBand));
    }

    [Fact]
    public void TooNew_BlocksWrites_AndKeepsFile()
    {
        WritePrimary("""{ "version": 99, "modules": [] }""");
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();
        Assert.Null(load.Doc);
        Assert.Equal(HomeLayoutLoadFault.TooNew, load.Fault);
        Assert.NotNull(load.Detail);
        Assert.True(store.WritesBlocked);

        store.Commit(Envelope(HomeLayoutDoc.Default));
        store.WaitForWrites(2000);
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void AMalformedPrimary_RecoversFromAGoodBackup_AndStaysWritable()
    {
        WritePrimary("{ \"version\": 1, \"modules\": [");
        File.WriteAllText(_path + ".bak", """{ "version": 1, "modules": [ { "kind": "hero", "hidden": true } ] }""");

        var store = Store();
        var load = store.Load();

        Assert.Equal(HomeLayoutLoadFault.None, load.Fault);
        Assert.Equal("recovered from .bak", load.Detail);
        Assert.NotNull(load.Doc);
        Assert.True(HomeLayoutWire.Read(load.Doc).Layout.IsHidden(HomeGroupKind.Hero));
        Assert.False(store.WritesBlocked);
    }

    [Fact]
    public void AZeroVersion_IsMalformed_NotAnEmptyLayout()
    {
        WritePrimary("""{ "modules": [] }""");
        var load = Store().Load();
        Assert.Equal(HomeLayoutLoadFault.Corrupt, load.Fault);
    }

    [Fact]
    public void CorruptFile_FailSoft_DoesNotOverwriteUntilDiscard()
    {
        WritePrimary("{ \"version\": 1, \"modules\": [");
        byte[] before = File.ReadAllBytes(_path);

        var store = Store();
        var load = store.Load();
        Assert.Null(load.Doc);
        Assert.Equal(HomeLayoutLoadFault.Corrupt, load.Fault);
        Assert.True(store.WritesBlocked);
        Assert.Equal(before, File.ReadAllBytes(_path));

        store.Commit(Envelope(HomeLayoutDoc.Default));
        store.WaitForWrites(2000);
        Assert.Equal(before, File.ReadAllBytes(_path));

        store.DiscardCorrupt();
        Assert.False(store.WritesBlocked);
        Assert.Equal(HomeLayoutSaveFault.None, store.SaveFault);
        Assert.True(File.Exists(store.CorruptPath));
        Assert.Equal(before, File.ReadAllBytes(store.CorruptPath));
        Assert.False(File.Exists(_path));

        CommitAndWait(store, Envelope(HomeLayoutDoc.Default));
        Assert.True(File.Exists(_path));
        Assert.Equal(HomeLayoutLoadFault.None, Store().Load().Fault);
    }

    // ── write ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Commit_WritesAReadableStampedFile_AndRotatesOneBackupOnTheNextCommit()
    {
        var store = Store();
        var dto = Envelope(HomeLayoutDoc.Default);
        dto.Version = 0;
        CommitAndWait(store, dto);

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(store.BakPath));
        Assert.False(File.Exists(store.TmpPath));
        var back = store.Load();
        Assert.Equal(HomeLayoutLoadFault.None, back.Fault);
        Assert.Equal(HomeLayoutStore.CurrentVersion, back.Doc!.Version);
        Assert.True(back.Doc.UpdatedAtMs > 0);
        Assert.Equal(HomeLayoutModules.DefaultOrder.Length, HomeLayoutWire.Read(back.Doc).Layout.ModuleCount);
        byte[] first = File.ReadAllBytes(_path);

        var hidden = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new SetHomeModuleHidden(HomeGroupKind.Hero, true)).Layout;
        CommitAndWait(store, Envelope(hidden));

        Assert.True(File.Exists(store.BakPath));
        Assert.Equal(first, File.ReadAllBytes(store.BakPath));
        Assert.False(File.Exists(store.TmpPath));
        Assert.True(HomeLayoutWire.Read(Store().Load().Doc).Layout.IsHidden(HomeGroupKind.Hero));
    }

    [Fact]
    public void WaitForWrites_WithNothingPending_IsTrue()
        => Assert.True(Store().WaitForWrites(0));

    [Fact]
    public void AWrittenFile_IsTheSourceGeneratedWireShape()
    {
        var store = Store();
        CommitAndWait(store, Envelope(HomeLayoutDoc.Default));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("hero", doc.RootElement.GetProperty("modules")[0].GetProperty("kind").GetString());
    }

    // ── the preferences over a store ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Preferences_OnAFirstRun_HoldTheDefaultWithNoFault()
    {
        var prefs = new HomePreferences(Store());
        Assert.Equal(HomeLayoutLoadFault.None, prefs.Fault);
        Assert.False(prefs.WritesBlocked);
        Assert.Equal(HomeLayoutModules.DefaultOrder.Length, prefs.Layout.ModuleCount);
        Assert.Equal(0, prefs.LayoutVersion.Peek());
        Assert.Equal(_path, prefs.FilePath);
    }

    [Fact]
    public void Preferences_Dispatch_ChangesTheLayout_BumpsTheVersion_AndPersists()
    {
        var prefs = new HomePreferences(Store());

        Assert.Equal(HomeLayoutRejectReason.None, prefs.Dispatch(new SetHomeModuleHidden(HomeGroupKind.Hero, true)));

        Assert.True(prefs.Layout.IsHidden(HomeGroupKind.Hero));
        Assert.Equal(1, prefs.LayoutVersion.Peek());
        Assert.True(prefs.WaitForWrites(10_000));
        Assert.True(new HomePreferences(Store()).Layout.IsHidden(HomeGroupKind.Hero));
    }

    [Fact]
    public void Preferences_ARejectedCommand_ReturnsItsReason_AndNeitherPublishesNorPersists()
    {
        var prefs = new HomePreferences(Store());

        Assert.Equal(HomeLayoutRejectReason.NoChange, prefs.Dispatch(new SetHomeModuleHidden(HomeGroupKind.Hero, false)));
        Assert.Equal(HomeLayoutRejectReason.UnknownModule, prefs.Dispatch(new MoveHomeModule(99, 0)));

        Assert.Equal(0, prefs.LayoutVersion.Peek());
        Assert.True(prefs.WaitForWrites(1000));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Preferences_OverACorruptFile_ShowTheFault_AndStartFreshUnblocksAndSaves()
    {
        WritePrimary("{ \"version\": 1, \"modules\": [");
        var prefs = new HomePreferences(Store());

        Assert.Equal(HomeLayoutLoadFault.Corrupt, prefs.Fault);
        Assert.NotNull(prefs.FaultDetail);
        Assert.True(prefs.WritesBlocked);
        Assert.True(Home.CustomizerShowsFaultBanner(prefs.Fault, prefs.WritesBlocked, dismissed: false));
        Assert.Equal(HomeLayoutModules.DefaultOrder, prefs.Layout.Modules.Select(m => m.Kind));

        prefs.DiscardCorrupt();

        Assert.Equal(HomeLayoutLoadFault.None, prefs.Fault);
        Assert.Null(prefs.FaultDetail);
        Assert.False(prefs.WritesBlocked);
        Assert.Equal(1, prefs.LayoutVersion.Peek());
        Assert.False(Home.CustomizerShowsFaultBanner(prefs.Fault, prefs.WritesBlocked, dismissed: false));
        Assert.True(prefs.WaitForWrites(10_000));
        Assert.True(File.Exists(_path));
        Assert.True(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void Preferences_CarryUnknownModulesAcrossAnEdit()
    {
        WritePrimary("""{ "version": 1, "futureTop": 7, "modules": [ { "kind": "hero" }, { "kind": "futureModule", "n": 1 } ] }""");
        var prefs = new HomePreferences(Store());

        prefs.Dispatch(new SetHomeModuleHidden(HomeGroupKind.Hero, true));
        Assert.True(prefs.WaitForWrites(10_000));

        string written = Encoding.UTF8.GetString(File.ReadAllBytes(_path));
        Assert.Contains("futureModule", written, StringComparison.Ordinal);
        Assert.Contains("futureTop", written, StringComparison.Ordinal);
    }
}
