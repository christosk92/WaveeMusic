// ── Wavee.Tests/SidebarMenuModelTests.cs — what every sidebar menu offers, in every state, and that no raw key shows ─────
//
// docs/plans/wavee/sidebar-rework-implementation.md §P4.2 and §P4.10 (design C.4, Q7, Q15, Q17). Pure: the rows are data, so
// every label and reason key a menu can emit is checked against the base catalogue here, not on screen.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class SidebarMenuModelTests
{
    static readonly string[] None = [];
    static readonly string[] Search = ["Search"];

    static IReadOnlyList<SidebarMenuRow> Pane(SidebarLayoutId layout, SidebarLayoutState state, SidebarDensity density = SidebarDensity.Default,
        bool editing = false, IReadOnlyList<string>? locking = null)
        => SidebarMenuModel.Pane(layout, state, density, editing, locking ?? None);

    static IReadOnlyList<SidebarMenuRow> Header(SidebarLayoutId layout, SidebarLayoutState state, string section, IReadOnlyList<string>? locking = null)
        => SidebarMenuModel.Header(layout, state, section, locking ?? None);

    /// <summary>The first row with this action, at any depth, or null.</summary>
    static SidebarMenuRow? Find(IReadOnlyList<SidebarMenuRow> rows, SidebarMenuAction action)
    {
        foreach (var r in rows)
        {
            if (r.Action == action) return r;
            if (r.Children is { } kids && Find(kids, action) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>Every row with this action, at any depth.</summary>
    static List<SidebarMenuRow> FindAll(IReadOnlyList<SidebarMenuRow> rows, SidebarMenuAction action)
    {
        var found = new List<SidebarMenuRow>();
        foreach (var r in rows)
        {
            if (r.Action == action) found.Add(r);
            if (r.Children is { } kids) found.AddRange(FindAll(kids, action));
        }
        return found;
    }

    static IReadOnlyList<SidebarMenuRow> Children(IReadOnlyList<SidebarMenuRow> rows, string labelKey)
        => rows.Single(r => r.LabelKey == labelKey).Children!;

    static SidebarLayoutState Apply(SidebarLayoutState state, SidebarOp op)
        => SidebarLayoutRules.Apply(state, op, pinnedLocked: false).State;

    [Fact]
    public void Pane_Default_ResetDisabledUntilModified()
    {
        var rows = Pane(SidebarLayoutId.Classic, SidebarLayoutState.Default);
        Assert.False(Find(rows, SidebarMenuAction.ResetLayout)!.Enabled);

        var moved = Apply(SidebarLayoutState.Default, new SetSectionShown(SidebarLayoutId.Classic, "collections", false));
        Assert.True(Find(Pane(SidebarLayoutId.Classic, moved), SidebarMenuAction.ResetLayout)!.Enabled);
    }

    [Fact]
    public void Pane_Editing_LayoutAndResetEverythingDisabledWithReason_DensityLive()
    {
        var rows = Pane(SidebarLayoutId.Classic, SidebarLayoutState.Default, SidebarDensity.Compact, editing: true);
        var switches = FindAll(rows, SidebarMenuAction.SwitchLayout);
        Assert.Equal(2, switches.Count);
        Assert.All(switches, r =>
        {
            Assert.False(r.Enabled);
            Assert.Equal("sidebar.pane.finishEditing", r.ReasonKey);
        });
        var reset = Find(rows, SidebarMenuAction.ResetEverything)!;
        Assert.False(reset.Enabled);
        Assert.Equal("sidebar.pane.finishEditing", reset.ReasonKey);
        Assert.False(Find(rows, SidebarMenuAction.EditSidebar)!.Enabled);
        // Density stays live while editing: both radios enabled, Compact checked.
        var density = FindAll(rows, SidebarMenuAction.SetDensity);
        Assert.Equal(2, density.Count);
        Assert.All(density, r => Assert.True(r.Enabled));
        Assert.True(density.Single(r => r.Arg == "compact").Checked);
        AssertEveryLabelKeyResolves(rows);
    }

    [Fact]
    public void Pane_ShowSection_ListsHiddenSettingsAndPinned()
    {
        var state = SidebarLayoutState.Default;
        state = SidebarLayoutRules.Apply(state, new SetSectionShown(SidebarLayoutId.Classic, "settings", false), pinnedLocked: false).State;
        state = SidebarLayoutRules.Apply(state, new SetSectionShown(SidebarLayoutId.Classic, "pinned", false), pinnedLocked: false).State;
        var rows = SidebarMenuModel.Pane(SidebarLayoutId.Classic, state, SidebarDensity.Default, false, None);
        var show = rows.First(r => r.LabelKey == "sidebar.menu.showSection");
        Assert.Contains(show.Children!, c => c.Arg == "settings");   // Q15
        Assert.Contains(show.Children!, c => c.Arg == "pinned");
        AssertEveryLabelKeyResolves(rows);                           // no raw "sidebar.section.title.settings" on screen (D9)
    }

    [Fact]
    public void Pane_Library_HidePinned_EnabledWithoutShortcutPins()
    {
        var rows = Pane(SidebarLayoutId.Library, SidebarLayoutState.Default);
        var hide = Find(rows, SidebarMenuAction.HideSection);
        Assert.NotNull(hide);
        Assert.Equal("pinned", hide!.Arg);
        Assert.True(hide.Enabled);
        Assert.Null(Find(rows, SidebarMenuAction.UnpinAllShortcuts));
        AssertEveryLabelKeyResolves(rows);
    }

    [Fact]
    public void Pane_Library_HidePinned_DisabledWithReason_AndUnpinAllShortcuts_WhileLocked()
    {
        var rows = Pane(SidebarLayoutId.Library, SidebarLayoutState.Default, locking: Search);
        var hide = Find(rows, SidebarMenuAction.HideSection)!;
        Assert.False(hide.Enabled);
        Assert.Equal("sidebar.menu.unpinFirst", hide.ReasonKey);
        Assert.NotNull(Find(rows, SidebarMenuAction.UnpinAllShortcuts));
        AssertEveryLabelKeyResolves(rows);
    }

    [Fact]
    public void Pane_Library_PinnedHidden_NoHideRow_ShowSectionListsIt()
    {
        var state = Apply(SidebarLayoutState.Default, new SetSectionShown(SidebarLayoutId.Library, "pinned", false));
        var rows = Pane(SidebarLayoutId.Library, state);
        Assert.Null(Find(rows, SidebarMenuAction.HideSection));
        var show = rows.First(r => r.LabelKey == "sidebar.menu.showSection");
        Assert.Contains(show.Children!, c => c.Arg == "pinned");
    }

    [Fact]
    public void Pane_Classic_NoPinnedRows()
    {
        var rows = Pane(SidebarLayoutId.Classic, SidebarLayoutState.Default, locking: Search);
        Assert.Null(Find(rows, SidebarMenuAction.HideSection));
        Assert.Null(Find(rows, SidebarMenuAction.UnpinAllShortcuts));   // Classic keeps them on the Pinned header (Q17)
    }

    [Fact]
    public void Header_Recent_ShowCountRadios()
    {
        var rows = Header(SidebarLayoutId.Classic, SidebarLayoutState.Default, "recent");
        var show = Children(rows, "sidebar.menu.show");
        Assert.Equal(new[] { "5", "10", "20" }, show.Select(c => c.Arg));
        Assert.Equal(new[] { true, false, false }, show.Select(c => c.Checked));
        Assert.All(show, c => Assert.True(c.Radio && c.Action == SidebarMenuAction.SetLimit));
        Assert.NotNull(Find(rows, SidebarMenuAction.Collapse));

        var ten = Apply(SidebarLayoutState.Default, new SetSectionLimit(SidebarLayoutId.Classic, "recent", 10));
        var tenRows = Header(SidebarLayoutId.Classic, ten, "recent");
        Assert.Equal(new[] { false, true, false }, Children(tenRows, "sidebar.menu.show").Select(c => c.Checked));
        AssertEveryLabelKeyResolves(rows);
    }

    [Fact]
    public void Header_Pinned_LockedHideHasReason_AndUnpinAllShortcuts()
    {
        var classic = Header(SidebarLayoutId.Classic, SidebarLayoutState.Default, "pinned", Search);
        var hide = Find(classic, SidebarMenuAction.HideSection)!;
        Assert.False(hide.Enabled);
        Assert.Equal("sidebar.menu.unpinFirst", hide.ReasonKey);
        Assert.NotNull(Find(classic, SidebarMenuAction.UnpinAllShortcuts));
        AssertEveryLabelKeyResolves(classic);

        // The Library's Pinned block opens the same header menu.
        var library = Header(SidebarLayoutId.Library, SidebarLayoutState.Default, "pinned", Search);
        Assert.False(Find(library, SidebarMenuAction.HideSection)!.Enabled);
        Assert.NotNull(Find(library, SidebarMenuAction.UnpinAllShortcuts));
        Assert.Null(Find(library, SidebarMenuAction.MoveUp));           // Library's sections keep a fixed order
    }

    [Fact]
    public void Header_Playlists_NoHide()
    {
        var rows = Header(SidebarLayoutId.Classic, SidebarLayoutState.Default, "playlists");
        Assert.Null(Find(rows, SidebarMenuAction.HideSection));
        Assert.NotNull(Find(rows, SidebarMenuAction.MoveUp));
        AssertEveryLabelKeyResolves(rows);
    }

    [Fact]
    public void Header_Movable_EdgesDisabled()
    {
        var first = Header(SidebarLayoutId.Classic, SidebarLayoutState.Default, "pinned");   // band slot 0
        Assert.False(Find(first, SidebarMenuAction.MoveUp)!.Enabled);
        Assert.True(Find(first, SidebarMenuAction.MoveDown)!.Enabled);

        var last = Header(SidebarLayoutId.Classic, SidebarLayoutState.Default, "newReleases");  // band slot 4 of 5
        Assert.True(Find(last, SidebarMenuAction.MoveUp)!.Enabled);
        Assert.False(Find(last, SidebarMenuAction.MoveDown)!.Enabled);
    }

    [Fact]
    public void Header_Library_None()
    {
        // Your Library has no header menu: its options are LibraryOptions.
        Assert.Empty(Header(SidebarLayoutId.Library, SidebarLayoutState.Default, "library"));
    }

    [Fact]
    public void LibraryOptions_CustomOrderOnlyUnderPlaylists()
    {
        var custom = SidebarStoreV3.SortName(SidebarLibrarySort.CustomOrder);
        var unfiltered = Children(SidebarMenuModel.LibraryOptions(SidebarLayoutState.Default, SidebarLibraryFilter.None), "sidebar.menu.sort");
        var off = unfiltered.Single(c => c.Arg == custom);
        Assert.False(off.Enabled);
        Assert.Equal("sidebar.sort.customOnlyPlaylists", off.ReasonKey);

        var playlists = Children(SidebarMenuModel.LibraryOptions(SidebarLayoutState.Default, SidebarLibraryFilter.Playlists), "sidebar.menu.sort");
        var on = playlists.Single(c => c.Arg == custom);
        Assert.True(on.Enabled);
        Assert.Null(on.ReasonKey);
        AssertEveryLabelKeyResolves(SidebarMenuModel.LibraryOptions(SidebarLayoutState.Default, SidebarLibraryFilter.Playlists));
    }

    [Fact]
    public void LibraryOptions_FiltersReflectHiddenKinds()
    {
        var state = Apply(SidebarLayoutState.Default, new SetItemShown(SidebarLayoutId.Library, "library", "podcasts", false));
        var rows = SidebarMenuModel.LibraryOptions(state, SidebarLibraryFilter.None);
        var filters = Children(rows, "sidebar.menu.filters");
        Assert.False(filters.Single(c => c.Arg == "podcasts").Checked);
        Assert.True(filters.Single(c => c.Arg == "albums").Checked);
        AssertEveryLabelKeyResolves(rows);
    }

    [Fact]
    public void Item_Pin_MoveAndUnpin()
    {
        var first = SidebarMenuModel.Item(SidebarSectionKind.Pinned, "pl:a", 0, 3, false);
        Assert.False(first.Single(r => r.Action == SidebarMenuAction.MovePinUp).Enabled);
        Assert.True(first.Single(r => r.Action == SidebarMenuAction.MovePinDown).Enabled);
        Assert.Equal("pl:a", first.Single(r => r.Action == SidebarMenuAction.Unpin).Arg);

        var last = SidebarMenuModel.Item(SidebarSectionKind.Pinned, "pl:a", 2, 3, false);
        Assert.False(last.Single(r => r.Action == SidebarMenuAction.MovePinDown).Enabled);
        AssertEveryLabelKeyResolves(first);
    }

    [Fact]
    public void Item_UnavailablePin_UnpinOnly()
    {
        var rows = SidebarMenuModel.Item(SidebarSectionKind.Pinned, "pl:a", 1, 3, unavailable: true);
        Assert.Equal(SidebarMenuAction.Unpin, Assert.Single(rows).Action);
    }

    [Fact]
    public void Item_Collections_MoveAndHide()
    {
        var rows = SidebarMenuModel.Item(SidebarSectionKind.Collections, "albums", 0, 5, false);
        Assert.False(rows.Single(r => r.Action == SidebarMenuAction.MoveUp).Enabled);
        Assert.True(rows.Single(r => r.Action == SidebarMenuAction.MoveDown).Enabled);
        Assert.Equal(SidebarMenuAction.HideItem, rows.Single(r => r.Action == SidebarMenuAction.HideItem).Action);
        AssertEveryLabelKeyResolves(rows);
    }

    [Fact]
    public void Item_LibraryLiked_Hide()
    {
        var rows = SidebarMenuModel.Item(SidebarSectionKind.Library, SidebarCatalogue.LikedRoute, 0, 1, false);
        Assert.Equal(SidebarMenuAction.HideItem, Assert.Single(rows).Action);
        AssertEveryLabelKeyResolves(rows);
    }

    [Fact]
    public void Item_Settings_Hide()
    {
        var rows = SidebarMenuModel.Item(SidebarSectionKind.Settings, SidebarCatalogue.SettingsRoute, 0, 1, false);
        Assert.Equal(SidebarMenuAction.HideItem, Assert.Single(rows).Action);
    }

    [Fact]
    public void Item_RootlistRow_MovesOnlyWhileTheRootlistOrderIsShown()
    {
        var mid = new SidebarRootlistStep(Shown: true, CanMoveUp: true, CanMoveDown: true);
        var rows = SidebarMenuModel.Item(SidebarSectionKind.Playlists, "pl:a", 3, 10, false, mid);
        Assert.Equal(new[] { SidebarMenuAction.MoveRootlistUp, SidebarMenuAction.MoveRootlistDown }, rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal("pl:a", r.Arg));
        // Your Library under a non-Custom sort: the caller passes Shown false ⇒ no positional verbs.
        Assert.Empty(SidebarMenuModel.Item(SidebarSectionKind.Library, "pl:a", 3, 10, false, default));
        // Library under Playlists · Custom order, the first sibling: Move down only (an end is absent, never disabled).
        var first = SidebarMenuModel.Item(SidebarSectionKind.Library, "pl:a", 0, 10, false, new SidebarRootlistStep(true, false, true));
        Assert.Equal(SidebarMenuAction.MoveRootlistDown, Assert.Single(first).Action);
        // Liked keeps its Hide row and never gets a rootlist move.
        var liked = SidebarMenuModel.Item(SidebarSectionKind.Library, SidebarCatalogue.LikedRoute, 0, 1, false, mid);
        Assert.Equal(SidebarMenuAction.HideItem, Assert.Single(liked).Action);
        AssertEveryLabelKeyResolves(rows);
    }

    /// <summary>Every LabelKey and ReasonKey a menu emits exists in the base catalog: a raw key on screen is the bug.
    /// Call it from the Header_*, LibraryOptions_* and Item_* facts too.</summary>
    static void AssertEveryLabelKeyResolves(IReadOnlyList<SidebarMenuRow> rows)
    {
        string? locDir = FindLocDir();
        if (locDir is null) return;   // running outside the repo layout — nothing to assert against
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(locDir, "en-US.json")));
        var root = doc.RootElement;
        void Walk(IReadOnlyList<SidebarMenuRow> list)
        {
            foreach (var r in list)
            {
                if (!r.Separator) Assert.True(Has(root, r.LabelKey), r.LabelKey + " is not in the base catalog");
                if (r.ReasonKey is { } reason) Assert.True(Has(root, reason), reason + " is not in the base catalog");
                if (r.Children is { } kids) Walk(kids);
            }
        }
        Walk(rows);
    }

    static bool Has(JsonElement root, string dotted)
    {
        var e = root;
        foreach (var part in dotted.Split('.'))
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(part, out e)) return false;
        return e.ValueKind == JsonValueKind.String;
    }

    /// <summary>ModulePageGateTests' helper, copied: walk up from the test binary to src/apps/Wavee/assets/loc.</summary>
    static string? FindLocDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
            candidate = Path.Combine(dir.FullName, "src", "apps", "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
        }
        return null;
    }

    /// <summary>The drag chip's unpinnable-drop captions (§P3.9 <c>SidebarPinRules.RefusalKeyOf</c>) exist: a missing key
    /// would put the raw key on the chip.</summary>
    [Fact]
    public void PinRefusalKeys_Resolve()
    {
        string? locDir = FindLocDir();
        if (locDir is null) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(locDir, "en-US.json")));
        foreach (var kind in new[] { DragKind.Track, DragKind.Episode })
            Assert.True(Has(doc.RootElement, SidebarPinRules.RefusalKeyOf(kind, "x", "spotify:x")!));
        Assert.True(Has(doc.RootElement, SidebarPinRules.RefusalKeyOf(DragKind.Track, "x", "wavee:local:track:1")!));
        Assert.True(Has(doc.RootElement, SidebarPinRules.RefusalKeyOf(DragKind.Route, "home", "")!));
        Assert.True(Has(doc.RootElement, SidebarPinRules.RefusalKeyOf(DragKind.Route, "liked", "")!));
    }
}
