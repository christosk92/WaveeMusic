using System.Linq;
using FluentGpu.Controls;
using Xunit;

namespace Wavee.Tests;

// The shell's tab model. The defect this class exists for: the engine TabStrip writes its SelectedIndex cell BEFORE it
// raises OnSelectionChanged (its controlled-prop idiom), so a shell that decides "did the tab change" from that cell
// sees "no" on every click and never swaps the content. TabWorkspace keys the decision on its OWN ActiveId, and a
// tab switch is a Restore (show the tab's page as it was), never a Push (no history entry, no origin rewrite).
public class TabWorkspaceTests
{
    static TabWorkspace TwoTabs(out int homeId, out int settingsId)
    {
        var ws = new TabWorkspace();
        homeId = ws.ActiveId;
        ws.Open(new Route("settings"));
        settingsId = ws.ActiveId;
        ws.ActivateById(homeId);
        return ws;
    }

    [Fact]
    public void Fresh_IsOneUnpinnedHomeTab_Active()
    {
        var ws = new TabWorkspace();
        Assert.Single(ws.Tabs);
        Assert.Equal("home", ws.Active.Route.Name);
        Assert.False(ws.Active.Pinned);
        Assert.Equal(0, ws.ActiveIndex);
        Assert.Equal(-1, ws.LastSelectedPinnedId);
    }

    [Fact]
    public void Activate_OtherTab_SwitchesActiveId_AndReturnsRestoreWithItsRoute()
    {
        var ws = TwoTabs(out _, out int settingsId);

        var r = ws.Activate(1);

        Assert.Equal(TabNavIntent.Restore, r.Intent);
        Assert.Equal(new Route("settings"), r.Route);
        Assert.Equal(1, r.ActiveIndex);
        Assert.Equal(settingsId, ws.ActiveId);
    }

    [Fact]
    public void Activate_SameTab_IsNone_RouteUntouched()
    {
        var ws = TwoTabs(out int homeId, out _);

        var r = ws.Activate(0);

        Assert.Equal(TabNavIntent.None, r.Intent);
        Assert.Null(r.Route);
        Assert.Equal(0, r.ActiveIndex);
        Assert.Equal(homeId, ws.ActiveId);
        Assert.Equal("home", ws.Active.Route.Name);
    }

    // The strip pre-writes its selection cell; whatever any external index cell says, only ActiveId decides. Two
    // activations of the same index in a row: the first switches, the second is a no-op — the model, not a cell.
    [Fact]
    public void Activate_IgnoresAnyExternalSelectionIndex()
    {
        var ws = TwoTabs(out _, out int settingsId);

        Assert.Equal(TabNavIntent.Restore, ws.Activate(1).Intent);
        Assert.Equal(settingsId, ws.ActiveId);
        Assert.Equal(TabNavIntent.None, ws.Activate(1).Intent);
        Assert.Equal(TabNavIntent.Restore, ws.Activate(0).Intent);
        Assert.Equal(TabNavIntent.None, ws.Activate(-1).Intent);
        Assert.Equal(TabNavIntent.None, ws.Activate(99).Intent);
        Assert.Equal(0, ws.ActiveIndex);
    }

    [Fact]
    public void Activate_NeverReturnsPush()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("settings"));
        ws.Open(new Route("albums"), pinned: true);

        for (int i = 0; i < ws.Count; i++)
        {
            Assert.NotEqual(TabNavIntent.Push, ws.Activate(i).Intent);
            Assert.NotEqual(TabNavIntent.Push, ws.ActivateById(ws.Tabs[i].Id).Intent);
        }
    }

    [Fact]
    public void Open_AppendsAfterPinnedBlock_ActivatesIt_AndReturnsPush()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("albums"), pinned: true);
        ws.Open(new Route("artists"), pinned: true);

        var r = ws.Open(new Route("settings"));

        Assert.Equal(TabNavIntent.Push, r.Intent);
        Assert.Equal(new Route("settings"), r.Route);
        Assert.Equal(ws.Count - 1, r.ActiveIndex);
        Assert.Equal("settings", ws.Active.Route.Name);
        Assert.Equal(new[] { "albums", "artists", "home", "settings" }, ws.Tabs.Select(t => t.Route.Name).ToArray());

        // A pinned open joins the END of the pinned block, never the tail of the strip.
        var pinned = ws.Open(new Route("liked"), pinned: true);
        Assert.Equal(TabNavIntent.Push, pinned.Intent);
        Assert.Equal(2, pinned.ActiveIndex);
        Assert.Equal(new[] { "albums", "artists", "liked", "home", "settings" }, ws.Tabs.Select(t => t.Route.Name).ToArray());
        Assert.Equal(ws.ActiveId, ws.LastSelectedPinnedId);
    }

    [Fact]
    public void Close_ActiveTab_SelectsRightNeighbour_ElseLeft_ReturnsRestore()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("settings"));
        ws.Open(new Route("albums"));
        ws.Activate(1);

        var right = ws.Close(1);
        Assert.Equal(TabNavIntent.Restore, right.Intent);
        Assert.Equal(new Route("albums"), right.Route);
        Assert.Equal(1, right.ActiveIndex);
        Assert.Equal("albums", ws.Active.Route.Name);

        var left = ws.Close(1);
        Assert.Equal(TabNavIntent.Restore, left.Intent);
        Assert.Equal(new Route("home"), left.Route);
        Assert.Equal(0, left.ActiveIndex);
    }

    // Closing a tab you are not looking at must not move you: the old shell navigated on every close.
    [Fact]
    public void Close_BackgroundTab_KeepsActive_ReturnsNone()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("settings"));
        ws.Open(new Route("albums"));
        int albumsId = ws.ActiveId;

        var r = ws.Close(0);

        Assert.Equal(TabNavIntent.None, r.Intent);
        Assert.Null(r.Route);
        Assert.Equal(albumsId, ws.ActiveId);
        Assert.Equal(1, r.ActiveIndex);
        Assert.Equal(new[] { "settings", "albums" }, ws.Tabs.Select(t => t.Route.Name).ToArray());
    }

    [Fact]
    public void Close_LastTab_IsRefused()
    {
        var ws = new TabWorkspace();

        var r = ws.Close(0);

        Assert.Equal(TabNavIntent.None, r.Intent);
        Assert.Single(ws.Tabs);
        Assert.Equal(0, r.ActiveIndex);
        Assert.Equal(TabNavIntent.None, ws.Close(5).Intent);
    }

    [Fact]
    public void CloseWhere_ActiveSurvives_IsNone()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("settings"));
        ws.Open(new Route("albums"));
        int albumsId = ws.ActiveId;

        var r = ws.CloseWhere(t => t.Id != albumsId && !t.Pinned);

        Assert.Equal(TabNavIntent.None, r.Intent);
        Assert.Single(ws.Tabs);
        Assert.Equal(albumsId, ws.ActiveId);
        Assert.Equal(0, r.ActiveIndex);
    }

    [Fact]
    public void CloseWhere_ActiveRemoved_TakesTheTabNowAtItsIndex_Restore()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("albums"), pinned: true);   // index 0
        ws.Open(new Route("settings"));               // index 2
        ws.Open(new Route("artists"));                // index 3
        ws.Activate(2);

        var r = ws.CloseWhere(t => t.Route.Name is "settings" or "home");

        Assert.Equal(TabNavIntent.Restore, r.Intent);
        Assert.Equal(new Route("artists"), r.Route);
        Assert.Equal(1, r.ActiveIndex);
        Assert.Equal(new[] { "albums", "artists" }, ws.Tabs.Select(t => t.Route.Name).ToArray());
    }

    [Fact]
    public void CloseWhere_Everything_ReseedsHome_Restore()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("settings"));
        int oldHomeId = ws.Tabs[0].Id;

        var r = ws.CloseWhere(_ => true);

        Assert.Equal(TabNavIntent.Restore, r.Intent);
        Assert.Equal(new Route("home"), r.Route);
        Assert.Single(ws.Tabs);
        Assert.Equal("home", ws.Active.Route.Name);
        Assert.NotEqual(oldHomeId, ws.ActiveId);   // a fresh identity — the old slot is gone
        Assert.Equal(-1, ws.LastSelectedPinnedId);
    }

    [Fact]
    public void SetPinned_MovesToBoundary_KeepsActiveIdentity()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("settings"));
        ws.Open(new Route("albums"));
        int albumsId = ws.ActiveId;

        var pin = ws.SetPinned(albumsId, true);

        Assert.Equal(TabNavIntent.None, pin.Intent);
        Assert.Equal(new[] { "albums", "home", "settings" }, ws.Tabs.Select(t => t.Route.Name).ToArray());
        Assert.True(ws.Tabs[0].Pinned);
        Assert.Equal(albumsId, ws.ActiveId);
        Assert.Equal(0, pin.ActiveIndex);
        Assert.Equal(albumsId, ws.LastSelectedPinnedId);

        var unpin = ws.SetPinned(albumsId, false);

        Assert.Equal(new[] { "albums", "home", "settings" }, ws.Tabs.Select(t => t.Route.Name).ToArray());   // boundary is 0 again: first unpinned slot
        Assert.False(ws.Tabs[0].Pinned);
        Assert.Equal(albumsId, ws.ActiveId);
        Assert.Equal(0, unpin.ActiveIndex);
        Assert.Equal(-1, ws.LastSelectedPinnedId);
        Assert.Equal(TabNavIntent.None, ws.SetPinned(albumsId, false).Intent);   // idempotent
    }

    [Fact]
    public void SetPinned_UnpinningTheRememberedPin_FallsBackToTheFirstPin()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("albums"), pinned: true);
        int albumsId = ws.ActiveId;
        ws.Open(new Route("artists"), pinned: true);
        int artistsId = ws.ActiveId;
        Assert.Equal(artistsId, ws.LastSelectedPinnedId);

        ws.SetPinned(artistsId, false);

        // Unpinning parks the tab at the pinned boundary (first unpinned slot), exactly where the shell always put it.
        Assert.Equal(albumsId, ws.LastSelectedPinnedId);
        Assert.Equal(new[] { "albums", "artists", "home" }, ws.Tabs.Select(t => t.Route.Name).ToArray());
    }

    [Fact]
    public void SetActiveRoute_RewritesOnlyTheActiveTab_ReportsPinned()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("settings"), pinned: true);
        ws.Activate(1);   // home
        int rev = ws.PinnedRevision;

        Assert.False(ws.SetActiveRoute(new Route("albums")));
        Assert.Equal(new[] { "settings", "albums" }, ws.Tabs.Select(t => t.Route.Name).ToArray());
        Assert.Equal(rev, ws.PinnedRevision);   // an unpinned tab's route is session-only

        ws.Activate(0);
        rev = ws.PinnedRevision;
        Assert.True(ws.SetActiveRoute(new Route("liked")));
        Assert.Equal(new[] { "liked", "albums" }, ws.Tabs.Select(t => t.Route.Name).ToArray());
        Assert.True(ws.PinnedRevision > rev);
    }

    [Fact]
    public void RestorePinned_ReplacesTheWorkspace_SelectsLastSelected_AndNormalizes()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("settings"));
        var snapshot = new WorkspaceTabsSnapshot(
            [new("albums", null), new("search", ""), new("pl:spotify:playlist:1", "Mix")], LastSelected: 2);

        var active = ws.RestorePinned(snapshot, (name, arg) => name == "search" && arg == "" ? new Route("browse") : new Route(name, arg));

        Assert.Equal(new Route("pl:spotify:playlist:1", "Mix"), active);
        Assert.Equal(3, ws.Count);
        Assert.All(ws.Tabs, t => Assert.True(t.Pinned));
        Assert.Equal("browse", ws.Tabs[1].Route.Name);   // the legacy empty search went through normalize
        Assert.Equal(2, ws.ActiveIndex);
        Assert.Equal(ws.ActiveId, ws.LastSelectedPinnedId);
        Assert.Equal(3, ws.Tabs.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void RestorePinned_EmptySnapshot_IsASingleHomeTab()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("settings"), pinned: true);

        var active = ws.RestorePinned(new WorkspaceTabsSnapshot([], -1), (n, a) => new Route(n, a));

        Assert.Equal(new Route("home"), active);
        Assert.Single(ws.Tabs);
        Assert.False(ws.Active.Pinned);
        Assert.Equal(-1, ws.LastSelectedPinnedId);
        Assert.Equal(0, ws.ActiveIndex);
    }

    [Fact]
    public void PinnedSnapshot_RoundTripsThroughWorkspaceTabsPersistence()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("albums"), pinned: true);
        ws.Open(new Route("pl:spotify:playlist:1", "Mix"), pinned: true);
        int mixId = ws.ActiveId;
        ws.Open(new Route("settings"));   // session-only — must not persist
        ws.ActivateById(mixId);
        ws.Activate(ws.Count - 1);        // back on the unpinned tab: the remembered pin is still Mix

        var snap = ws.PinnedSnapshot();
        var decoded = WorkspaceTabsPersistence.Decode(WorkspaceTabsPersistence.Encode(snap.Tabs, snap.LastSelected));

        Assert.Equal(2, decoded.Tabs.Length);
        Assert.Equal(new PersistedWorkspaceTab("albums", null), decoded.Tabs[0]);
        Assert.Equal(new PersistedWorkspaceTab("pl:spotify:playlist:1", "Mix"), decoded.Tabs[1]);
        Assert.Equal(1, decoded.LastSelected);

        var restored = new TabWorkspace();
        restored.RestorePinned(decoded, (n, a) => new Route(n, a));
        Assert.Equal("Mix", restored.Active.Route.Arg);
    }

    [Fact]
    public void Ids_AreStableAcrossReorder()
    {
        var ws = new TabWorkspace();
        int homeId = ws.ActiveId;
        ws.Open(new Route("settings"));
        int settingsId = ws.ActiveId;
        ws.Open(new Route("albums"));
        int albumsId = ws.ActiveId;

        ws.SetPinned(albumsId, true);
        ws.SetPinned(settingsId, true);
        ws.Close(ws.IndexOf(homeId));
        ws.Open(new Route("artists"));

        Assert.Equal(albumsId, ws.Tabs.Single(t => t.Route.Name == "albums").Id);
        Assert.Equal(settingsId, ws.Tabs.Single(t => t.Route.Name == "settings").Id);
        Assert.DoesNotContain(ws.Tabs, t => t.Id == homeId);
        Assert.Equal(ws.Tabs.Count, ws.Tabs.Select(t => t.Id).Distinct().Count());
        Assert.True(ws.Tabs.Single(t => t.Route.Name == "artists").Id > albumsId);   // minted, never recycled
    }

    [Fact]
    public void TrySelect_ReselectsWithoutTouchingTheRememberedPin()
    {
        var ws = new TabWorkspace();
        ws.Open(new Route("albums"), pinned: true);
        int albumsId = ws.ActiveId;
        ws.Open(new Route("artists"), pinned: true);
        int artistsId = ws.ActiveId;

        Assert.True(ws.TrySelect(albumsId));
        Assert.Equal(albumsId, ws.ActiveId);
        Assert.Equal(artistsId, ws.LastSelectedPinnedId);
        Assert.False(ws.TrySelect(12345));
        Assert.Equal(albumsId, ws.ActiveId);
    }
}
