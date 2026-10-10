// ── Wavee.Tests/SidebarEditRulesTests.cs — the Outline's rows, the band, the pin fold, the extents and the move targets ──
//
// docs/plans/wavee/sidebar-rework-implementation.md §P4.1 and §P4.10 (design C.3, Q3, Q5). Pure: no engine, no store.

using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarEditRulesTests
{
    static readonly SidebarPin[] NoPins = [];

    static SidebarPin Pin(int i) => new("pl:" + i, SidebarEntryKind.Playlist, "spotify:playlist:" + i, "Pin " + i, 0);

    static SidebarPin[] Pins(int n)
    {
        var pins = new SidebarPin[n];
        for (int i = 0; i < n; i++) pins[i] = Pin(i);
        return pins;
    }

    static List<SidebarOutlineRow> Rows(SidebarLayoutState state, SidebarLayoutId layout, IReadOnlyList<SidebarPin> pins,
        bool pinnedLocked = false, bool showAll = false, int playlistCount = 0)
    {
        var rows = new List<SidebarOutlineRow>();
        SidebarEditRules.Outline(state, layout, pins, pinnedLocked, showAll, playlistCount, rows);
        return rows;
    }

    static SidebarOutlineRow Section(IEnumerable<SidebarOutlineRow> rows, string id)
        => rows.Single(r => r.Kind == SidebarOutlineRowKind.Section && r.SectionId == id);

    static SidebarLayoutState Hide(SidebarLayoutState state, SidebarLayoutId layout, string id)
        => SidebarLayoutRules.Apply(state, new SetSectionShown(layout, id, false), pinnedLocked: false).State;

    [Fact]
    public void Outline_Classic_HomeThenBand_NoSettingsRow()
    {
        var rows = Rows(SidebarLayoutState.Default, SidebarLayoutId.Classic, NoPins);
        const SidebarOutlineRowKind Sec = SidebarOutlineRowKind.Section, Hint = SidebarOutlineRowKind.Hint, Item = SidebarOutlineRowKind.Item;
        Assert.Equal(new[]
        {
            SidebarOutlineRowKind.Home,
            Sec, Hint,                  // Pinned, no pins yet
            Sec, Item, Item, Item, Item, Item,   // Collections and its five pages
            Sec, Hint,                  // Playlists
            Sec, Hint,                  // Recently played (hidden by default)
            Sec, Hint,                  // New releases (hidden by default)
        }, rows.Select(r => r.Kind));
        Assert.False(Section(rows, "recent").Shown);

        var band = new List<string>();
        SidebarEditRules.Band(SidebarLayoutState.Default.Classic, band);
        Assert.Equal(new[] { "pinned", "collections", "playlists", "recent", "newReleases" }, band);
    }

    [Fact]
    public void Outline_Library_FixedOrder_FiltersAsItems_NoBand()
    {
        var rows = Rows(SidebarLayoutState.Default, SidebarLayoutId.Library, NoPins);
        var band = new List<string>();
        SidebarEditRules.Band(SidebarLayoutState.Default.Library, band);
        Assert.Empty(band);
        Assert.Equal(new[]
        {
            SidebarOutlineRowKind.Home,
            SidebarOutlineRowKind.Section, SidebarOutlineRowKind.Hint,
            SidebarOutlineRowKind.Section, SidebarOutlineRowKind.Item, SidebarOutlineRowKind.Item,
            SidebarOutlineRowKind.Item, SidebarOutlineRowKind.Item,
        }, rows.Select(r => r.Kind));
        var items = rows.Where(r => r.Kind == SidebarOutlineRowKind.Item).ToList();
        Assert.Equal(new[] { "albums", "artists", "podcasts", "audiobooks" }, items.Select(r => r.ItemId));
        Assert.All(items, r => Assert.False(r.Movable));
        Assert.False(Section(rows, "library").Movable);
    }

    [Fact]
    public void Outline_PinsAsRows_FoldAbove50()
    {
        var pins = Pins(60);
        var rows = Rows(SidebarLayoutState.Default, SidebarLayoutId.Classic, pins);
        Assert.Equal(50, rows.Count(r => r.Kind == SidebarOutlineRowKind.Pin));
        var more = Assert.Single(rows, r => r.Kind == SidebarOutlineRowKind.ShowAllPins);
        Assert.Equal(60, more.Count);

        var all = Rows(SidebarLayoutState.Default, SidebarLayoutId.Classic, pins, showAll: true);
        Assert.Equal(60, all.Count(r => r.Kind == SidebarOutlineRowKind.Pin));
        Assert.DoesNotContain(all, r => r.Kind == SidebarOutlineRowKind.ShowAllPins);
    }

    [Fact]
    public void Outline_PinnedLocked_ByRoutePins()
    {
        var locked = Rows(SidebarLayoutState.Default, SidebarLayoutId.Classic, NoPins, pinnedLocked: true);
        Assert.True(Section(locked, "pinned").Locked);

        var free = Rows(SidebarLayoutState.Default, SidebarLayoutId.Classic, NoPins, pinnedLocked: false);
        Assert.False(Section(free, "pinned").Locked);

        // A hidden Pinned is not locked: the lock guards its Show checkbox, and a hidden section has nothing to lock.
        var hidden = Rows(Hide(SidebarLayoutState.Default, SidebarLayoutId.Classic, "pinned"), SidebarLayoutId.Classic, NoPins,
            pinnedLocked: true);
        Assert.False(Section(hidden, "pinned").Shown);
        Assert.False(Section(hidden, "pinned").Locked);
    }

    [Fact]
    public void Outline_HiddenSectionShownFalse()
    {
        var state = Hide(SidebarLayoutState.Default, SidebarLayoutId.Classic, "collections");
        var rows = Rows(state, SidebarLayoutId.Classic, NoPins);
        var collections = Section(rows, "collections");
        Assert.False(collections.Shown);
        Assert.True(collections.Movable);
        Assert.True(Section(rows, "pinned").Shown);
    }

    [Fact]
    public void Outline_Library_PinsInnerBand()
    {
        // Library has no SECTION band, but its pins stay reorderable in the Outline (drag, Space lift, Alt+↑/↓).
        var pins = new[] { new SidebarPin("pl:spotify:playlist:a", SidebarEntryKind.Playlist, "spotify:playlist:a", "A", 0),
                           new SidebarPin("search", SidebarEntryKind.AppRoute, "", "Search", 0) };
        var rows = Rows(SidebarLayoutState.Default, SidebarLayoutId.Library, pins, pinnedLocked: true);
        var band = new List<string>();
        SidebarEditRules.Band(SidebarLayoutState.Default.Of(SidebarLayoutId.Library), band);
        Assert.Empty(band);                                                              // no section band
        var pinRows = rows.Where(r => r.Kind == SidebarOutlineRowKind.Pin).ToList();
        Assert.Equal(2, pinRows.Count);
        Assert.All(pinRows, r => Assert.True(r.Movable));                                // the inner pins band
        Assert.False(Section(rows, "pinned").Movable);
    }

    [Fact]
    public void Band_MovableOnly_InDisplayOrder()
    {
        var band = new List<string>();
        SidebarEditRules.Band(SidebarLayoutState.Default.Classic, band);
        Assert.DoesNotContain("home", band);
        Assert.DoesNotContain("settings", band);
        SidebarEditRules.Band(SidebarLayoutState.Default.Library, band);
        Assert.Empty(band);
    }

    [Fact]
    public void Extents_HeaderPlusBody()
    {
        var classic = SidebarLayoutState.Default.Classic;
        Assert.Equal(40f + 5 * 40f, SidebarEditRules.ExtentOf(SidebarLayoutId.Classic, classic.Find("collections")!, 0, false));
        Assert.Equal(40f + 44f, SidebarEditRules.ExtentOf(SidebarLayoutId.Classic, classic.Find("playlists")!, 0, false));
        // Pinned: an empty hint, the first 50 pins, the fold row past 50, and every pin once shown all.
        Assert.Equal(40f + 44f, SidebarEditRules.ExtentOf(SidebarLayoutId.Classic, classic.Find("pinned")!, 0, false));
        Assert.Equal(40f + 40f * 50 + 40f, SidebarEditRules.ExtentOf(SidebarLayoutId.Classic, classic.Find("pinned")!, 60, false));
        Assert.Equal(40f + 40f * 60, SidebarEditRules.ExtentOf(SidebarLayoutId.Classic, classic.Find("pinned")!, 60, true));
    }

    [Fact]
    public void MoveTarget_StepAndEdge()
    {
        Assert.Equal(3, SidebarEditRules.MoveTarget(2, 5, +1, toEdge: false));
        Assert.Equal(1, SidebarEditRules.MoveTarget(2, 5, -1, toEdge: false));
        Assert.Equal(0, SidebarEditRules.MoveTarget(0, 5, -1, toEdge: false));     // clamped at the top
        Assert.Equal(4, SidebarEditRules.MoveTarget(4, 5, +1, toEdge: false));     // clamped at the bottom
        Assert.Equal(0, SidebarEditRules.MoveTarget(2, 5, -1, toEdge: true));      // Alt+Shift+↑
        Assert.Equal(4, SidebarEditRules.MoveTarget(2, 5, +1, toEdge: true));      // Alt+Shift+↓
        Assert.Equal(-1, SidebarEditRules.MoveTarget(0, 0, +1, toEdge: false));    // nothing to move among
    }

    [Fact]
    public void AnnounceKeys()
    {
        Assert.Equal("sidebar.edit.announceGrabbed", SidebarEditRules.AnnounceKey(ReorderAnnounceKind.Grab));
        Assert.Equal("sidebar.edit.announceMoved", SidebarEditRules.AnnounceKey(ReorderAnnounceKind.Move));
        Assert.Equal("sidebar.edit.announceDropped", SidebarEditRules.AnnounceKey(ReorderAnnounceKind.Drop));
        Assert.Equal("sidebar.edit.announceCancelled", SidebarEditRules.AnnounceKey(ReorderAnnounceKind.Cancel));
    }

    [Fact]
    public void Summary_Collections_NamesTheCoupling()
    {
        Assert.Equal("sidebar.edit.collectionsSummary", SidebarEditRules.SummaryKeyOf(SidebarSectionKind.Collections, 5));
        Assert.Equal("sidebar.edit.collectionsLastSummary", SidebarEditRules.SummaryKeyOf(SidebarSectionKind.Collections, 1));
        Assert.Equal("sidebar.edit.collectionsLastSummary", SidebarEditRules.SummaryKeyOf(SidebarSectionKind.Collections, 0));
        Assert.Null(SidebarEditRules.SummaryKeyOf(SidebarSectionKind.Recent, 3));
    }
}
