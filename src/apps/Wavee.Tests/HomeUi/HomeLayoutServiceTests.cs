// ── Wavee.Tests/HomeUi/HomeLayoutServiceTests.cs — the HomeLayout service: one document, one Dispatch path ────────────
//
// Wave 2, F29 (docs/plans/wavee/home-redesign-remediation.md §3.7). Exercises the INSTANCE api only — never the
// process-lifetime `HomeLayout.Instance`/`UseInstance` singleton, which is shared, boot-owned global state that other
// test classes may touch concurrently (xunit parallelizes across classes by default). Every fact here constructs its
// own `HomeLayout` over its own temp-directory `LayoutStore`, the same discipline `LayoutFileTests` uses for the
// store beneath it.

using System.IO;
using Wavee.HomeUi;
using Xunit;

namespace Wavee.Tests.HomeUi;

public sealed class HomeLayoutServiceTests
{
    sealed class TempDir : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "wavee-home-layout-service-tests", Guid.NewGuid().ToString("n"));
        public TempDir() => Directory.CreateDirectory(Dir);
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch (Exception) { } }
    }

    [Fact]
    public void Construction_SeedsDocFromTheStore()
    {
        using var t = new TempDir();
        var layout = new HomeLayout(new LayoutStore(LayoutStore.PathUnder(t.Dir)));

        Assert.Equal(LayoutZones.DefaultOrder.Length, layout.Doc.Value.Zones.Count);
        foreach (var z in layout.Doc.Value.Zones) Assert.True(z.Visible);
    }

    [Fact]
    public void Dispatch_PublishesTheReducedDocument_OnTheSameSignalInstance()
    {
        using var t = new TempDir();
        var layout = new HomeLayout(new LayoutStore(LayoutStore.PathUnder(t.Dir)));

        // "both screens see one signal": whatever HomeScreen and CustomizeScreen both hold is THIS Doc instance —
        // Dispatch never replaces it, only publishes into it, so a reference captured once keeps working forever.
        var docSignal = layout.Doc;

        layout.Dispatch(doc => LayoutCommands.Toggle(doc, LayoutZone.Radio));

        Assert.Same(docSignal, layout.Doc);
        Assert.True(layout.Doc.Value.IsHidden(LayoutZone.Radio));
    }

    [Fact]
    public void Dispatch_ReducesFromTheCurrentPublishedDocument_NotAStaleCapture()
    {
        using var t = new TempDir();
        var layout = new HomeLayout(new LayoutStore(LayoutStore.PathUnder(t.Dir)));

        layout.Dispatch(doc => LayoutCommands.Toggle(doc, LayoutZone.Daylist));
        layout.Dispatch(doc => LayoutCommands.Toggle(doc, LayoutZone.Radio));

        Assert.True(layout.Doc.Value.IsHidden(LayoutZone.Daylist));
        Assert.True(layout.Doc.Value.IsHidden(LayoutZone.Radio));
    }

    [Fact]
    public void Dispatch_CommitsToDisk_ReadableByAFreshStoreOverTheSamePath()
    {
        using var t = new TempDir();
        string path = LayoutStore.PathUnder(t.Dir);
        var store = new LayoutStore(path);
        var layout = new HomeLayout(store);

        layout.Dispatch(doc => LayoutCommands.Move(doc, 0, doc.Zones.Count - 1));
        Assert.True(store.WaitForWrites(10_000));

        var reload = new LayoutStore(path).Load();
        Assert.Equal(LayoutReadFault.None, reload.Fault);
        Assert.Equal(layout.Doc.Value.Zones[^1].Kind, reload.Doc.Zones[^1].Kind);
    }

    [Fact]
    public void Reset_ThroughDispatch_RestoresTheDefaultOrderAndVisibility()
    {
        using var t = new TempDir();
        var layout = new HomeLayout(new LayoutStore(LayoutStore.PathUnder(t.Dir)));

        layout.Dispatch(doc => LayoutCommands.Toggle(doc, LayoutZone.MadeForYou));
        layout.Dispatch(doc => LayoutCommands.Move(doc, 0, doc.Zones.Count - 1));
        Assert.True(layout.Doc.Value.IsHidden(LayoutZone.MadeForYou));

        layout.Dispatch(_ => LayoutCommands.Reset());

        Assert.False(layout.Doc.Value.IsHidden(LayoutZone.MadeForYou));
        Assert.Equal(LayoutZones.DefaultOrder.Length, layout.Doc.Value.Zones.Count);
        for (int i = 0; i < LayoutZones.DefaultOrder.Length; i++)
            Assert.Equal(LayoutZones.KindName(LayoutZones.DefaultOrder[i]), layout.Doc.Value.Zones[i].Kind);
    }
}
