// ── Wavee.Tests/HomeUi/LayoutFileTests.cs — home-layout.json v2: wire, reducer, atomic-write store ───────────────────
//
// Wave 1, owner A5. Every file fact runs in its OWN temp directory (never the real profile folder — `DefaultPath` and
// `ForApp` are not called here) and deletes it on dispose, same discipline as the old HomeLayoutStoreTests.

using System.IO;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public sealed class LayoutFileTests
{
    // ── the wire ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Read_MissingText_IsTheDefaultWithNoFault()
    {
        var r = LayoutWire.Read(null);
        Assert.Equal(LayoutReadFault.None, r.Fault);
        Assert.Equal(LayoutZones.DefaultOrder.Length, r.Doc.Zones.Count);
        foreach (var z in r.Doc.Zones) Assert.True(z.Visible);
    }

    [Fact]
    public void RoundTrip_WriteThenRead_ReproducesTheDocument()
    {
        var doc = LayoutCommands.Toggle(LayoutFile.DefaultDoc(), LayoutZone.Radio);
        doc = LayoutCommands.Move(doc, 0, doc.Zones.Count - 1);

        string json = LayoutWire.Write(doc);
        var back = LayoutWire.Read(json);

        Assert.Equal(LayoutReadFault.None, back.Fault);
        Assert.Equal(doc.Zones.Count, back.Doc.Zones.Count);
        for (int i = 0; i < doc.Zones.Count; i++)
        {
            Assert.Equal(doc.Zones[i].Kind, back.Doc.Zones[i].Kind);
            Assert.Equal(doc.Zones[i].Visible, back.Doc.Zones[i].Visible);
        }
        Assert.True(back.Doc.IsHidden(LayoutZone.Radio));
    }

    [Fact]
    public void V1Document_ReadsAsTheV2Default_AnAcceptedBreak()
    {
        var r = LayoutWire.Read("""{ "version": 1, "modules": [ { "kind": "hero", "hidden": true } ] }""");
        Assert.Equal(LayoutReadFault.LegacyVersion, r.Fault);
        Assert.Equal(LayoutZones.DefaultOrder.Length, r.Doc.Zones.Count);
        foreach (var z in r.Doc.Zones) Assert.True(z.Visible);
    }

    [Fact]
    public void ZeroVersion_IsMalformed_NotLegacy()
    {
        var r = LayoutWire.Read("""{ "version": 0, "zones": [] }""");
        Assert.Equal(LayoutReadFault.Malformed, r.Fault);
    }

    [Fact]
    public void TooNewVersion_FailsSoft_WithTheDefaultInMemory()
    {
        var r = LayoutWire.Read("""{ "version": 99, "zones": [ { "kind": "daylist", "visible": false } ] }""");
        Assert.Equal(LayoutReadFault.TooNew, r.Fault);
        Assert.False(r.Doc.IsHidden(LayoutZone.Daylist));   // the v99 zone was NOT applied — pure default
    }

    [Fact]
    public void MalformedJson_FailsSoft_WithTheDefaultInMemory()
    {
        var r = LayoutWire.Read("{ \"version\": 2, \"zones\": [");
        Assert.Equal(LayoutReadFault.Malformed, r.Fault);
        Assert.Equal(LayoutZones.DefaultOrder.Length, r.Doc.Zones.Count);
    }

    [Fact]
    public void UnknownZoneKinds_AreDroppedOnRead_NotCarried()
    {
        var r = LayoutWire.Read("""{ "version": 2, "zones": [ { "kind": "futureZone", "visible": false }, { "kind": "daylist", "visible": false } ] }""");
        Assert.Equal(LayoutReadFault.None, r.Fault);
        Assert.DoesNotContain(r.Doc.Zones, z => z.Kind == "futureZone");
        Assert.True(r.Doc.IsHidden(LayoutZone.Daylist));
        Assert.Equal(LayoutZones.DefaultOrder.Length, r.Doc.Zones.Count);   // every known zone still present, appended visible
    }

    [Fact]
    public void AKnownZoneMissingFromTheDocument_IsAppendedVisible()
    {
        var r = LayoutWire.Read("""{ "version": 2, "zones": [ { "kind": "daylist", "visible": false } ] }""");
        Assert.Equal(LayoutReadFault.None, r.Fault);
        Assert.True(r.Doc.IsHidden(LayoutZone.Daylist));
        Assert.False(r.Doc.IsHidden(LayoutZone.Recents));
        Assert.Equal(LayoutZones.DefaultOrder.Length, r.Doc.Zones.Count);
    }

    // ── the reducer ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Toggle_OnAnAbsentZone_AppendsItHidden()
    {
        var doc = new LayoutDoc(2, Array.Empty<LayoutEntry>(), 0);
        var next = LayoutCommands.Toggle(doc, LayoutZone.Browse);
        Assert.True(next.IsHidden(LayoutZone.Browse));
    }

    [Fact]
    public void Toggle_OnAVisibleZone_HidesIt_AndToggleAgainShowsIt()
    {
        var doc = LayoutFile.DefaultDoc();
        Assert.False(doc.IsHidden(LayoutZone.MadeForYou));

        var hidden = LayoutCommands.Toggle(doc, LayoutZone.MadeForYou);
        Assert.True(hidden.IsHidden(LayoutZone.MadeForYou));

        var shown = LayoutCommands.Toggle(hidden, LayoutZone.MadeForYou);
        Assert.False(shown.IsHidden(LayoutZone.MadeForYou));
    }

    [Fact]
    public void Move_ReordersAfterRemoval()
    {
        var doc = LayoutFile.DefaultDoc();
        string firstKind = doc.Zones[0].Kind;

        var moved = LayoutCommands.Move(doc, 0, doc.Zones.Count - 1);

        Assert.Equal(firstKind, moved.Zones[^1].Kind);
        Assert.Equal(doc.Zones.Count, moved.Zones.Count);
    }

    [Fact]
    public void Move_WithAnOutOfRangeFromIndex_IsANoOp()
    {
        var doc = LayoutFile.DefaultDoc();
        var moved = LayoutCommands.Move(doc, -1, 0);
        Assert.Same(doc, moved);

        var moved2 = LayoutCommands.Move(doc, doc.Zones.Count, 0);
        Assert.Same(doc, moved2);
    }

    [Fact]
    public void Reset_ReturnsTheDefaultDocument()
    {
        var doc = LayoutCommands.Toggle(LayoutFile.DefaultDoc(), LayoutZone.Daylist);
        var reset = LayoutCommands.Reset();
        Assert.False(reset.IsHidden(LayoutZone.Daylist));
        Assert.Equal(LayoutZones.DefaultOrder.Length, reset.Zones.Count);
    }

    // ── the store ─────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class TempDir : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "wavee-home-layout-file-tests", Guid.NewGuid().ToString("n"));
        public TempDir() => Directory.CreateDirectory(Dir);
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch (Exception) { } }
    }

    [Fact]
    public void PathUnder_ComposesWaveeMusicHomeLayoutJson()
    {
        using var t = new TempDir();
        Assert.Equal(Path.Combine(t.Dir, "WaveeMusic", "home-layout.json"), LayoutStore.PathUnder(t.Dir));
    }

    [Fact]
    public void FirstRun_IsNotAFault_AndWritesNothing()
    {
        using var t = new TempDir();
        var store = new LayoutStore(LayoutStore.PathUnder(t.Dir));
        var load = store.Load();
        Assert.Equal(LayoutReadFault.None, load.Fault);
        Assert.False(store.WritesBlocked);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void Commit_WritesAReadableFile_AndRotatesOneBackupOnTheNextCommit()
    {
        using var t = new TempDir();
        var store = new LayoutStore(LayoutStore.PathUnder(t.Dir));

        store.Commit(LayoutFile.DefaultDoc());
        Assert.True(store.WaitForWrites(10_000));
        Assert.True(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.BakPath));
        byte[] first = File.ReadAllBytes(store.FilePath);

        var hidden = LayoutCommands.Toggle(LayoutFile.DefaultDoc(), LayoutZone.Daylist);
        store.Commit(hidden);
        Assert.True(store.WaitForWrites(10_000));

        Assert.True(File.Exists(store.BakPath));
        Assert.Equal(first, File.ReadAllBytes(store.BakPath));
        var reload = new LayoutStore(store.FilePath).Load();
        Assert.True(reload.Doc.IsHidden(LayoutZone.Daylist));
    }

    [Fact]
    public void ALegacyV1File_LoadsAsTheDefault_AndIsFreelyOverwritten()
    {
        using var t = new TempDir();
        string path = LayoutStore.PathUnder(t.Dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "version": 1, "modules": [] }""");

        var store = new LayoutStore(path);
        var load = store.Load();
        Assert.Equal(LayoutReadFault.LegacyVersion, load.Fault);
        Assert.False(store.WritesBlocked);

        store.Commit(LayoutFile.DefaultDoc());
        Assert.True(store.WaitForWrites(10_000));
        var reload = store.Load();
        Assert.Equal(LayoutReadFault.None, reload.Fault);
    }

    [Fact]
    public void ATooNewFile_BlocksWrites_AndTheFileIsLeftUntouched()
    {
        using var t = new TempDir();
        string path = LayoutStore.PathUnder(t.Dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "version": 99, "zones": [] }""");
        byte[] before = File.ReadAllBytes(path);

        var store = new LayoutStore(path);
        var load = store.Load();
        Assert.Equal(LayoutReadFault.TooNew, load.Fault);
        Assert.True(store.WritesBlocked);

        store.Commit(LayoutFile.DefaultDoc());
        store.WaitForWrites(2000);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void AMalformedFile_BlocksWrites_AndTheFileIsLeftUntouched()
    {
        using var t = new TempDir();
        string path = LayoutStore.PathUnder(t.Dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"version\": 2, \"zones\": [");
        byte[] before = File.ReadAllBytes(path);

        var store = new LayoutStore(path);
        var load = store.Load();
        Assert.Equal(LayoutReadFault.Malformed, load.Fault);
        Assert.True(store.WritesBlocked);

        store.Commit(LayoutFile.DefaultDoc());
        store.WaitForWrites(2000);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void WaitForWrites_WithNothingPending_IsTrue()
    {
        using var t = new TempDir();
        Assert.True(new LayoutStore(LayoutStore.PathUnder(t.Dir)).WaitForWrites(0));
    }
}
