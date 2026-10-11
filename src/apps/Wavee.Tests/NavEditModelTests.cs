// ── Wavee.Tests/NavEditModelTests.cs — Edit mode as model state and verbs (Shell/NavEditModel.cs, themes plan §5.1 row C11) ──
//
// The Outline read, the ops a checkbox / drag / key makes (all through the one layout path, so the undo ring records them),
// the "you showed an empty section" note, undo and redo, and the key dispatch table — fed `NavEditKey` / `NavEditMods`, so no
// engine event is needed. The process-wide `Sidebar` service is put back in Dispose (layout, pins, edit mode, ring entries are
// left to the next fact's own Reset).

using System;
using System.Collections.Generic;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class NavEditModelTests : IDisposable
{
    readonly SidebarLayoutId _layout;
    readonly NavEditModel _edit = Sidebar.Model.Edit;

    public NavEditModelTests()
    {
        _layout = Sidebar.Layout.Peek();
        Sidebar.ExitEdit();
        Sidebar.SwitchLayout(SidebarLayoutId.Classic);
        Sidebar.Dispatch(new ResetLayout(SidebarLayoutId.Classic));
        Sidebar.Dispatch(new ResetLayout(SidebarLayoutId.Library));
        Sidebar.SetLibraryFilter(SidebarLibraryFilter.None);
        Sidebar.Pins.LoadFrom(null);
    }

    public void Dispose()
    {
        Sidebar.ExitEdit();
        Sidebar.SetLibraryFilter(SidebarLibraryFilter.None);
        Sidebar.Dispatch(new ResetLayout(SidebarLayoutId.Classic));
        Sidebar.Dispatch(new ResetLayout(SidebarLayoutId.Library));
        Sidebar.SwitchLayout(_layout);
        Sidebar.Pins.LoadFrom(null);
    }

    static SectionState Section(SidebarLayoutId layout, string id) => Sidebar.State.Of(layout).Find(id)!;

    static List<string> Band()
    {
        var band = new List<string>();
        SidebarEditRules.Band(Sidebar.State.Of(SidebarLayoutId.Classic), band);
        return band;
    }

    static string[] Order(string sectionId, SidebarSectionKind kind)
    {
        var order = SidebarLayoutRules.EffectiveItemOrder(SidebarLayoutId.Classic, kind, Section(SidebarLayoutId.Classic, sectionId));
        var copy = new string[order.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = order[i];
        return copy;
    }

    static void LoadPins(params string[] names)
    {
        var pins = new List<SidebarPin>();
        foreach (string n in names)
            pins.Add(new SidebarPin("pl:spotify:playlist:" + n, SidebarEntryKind.Playlist, "spotify:playlist:" + n, n, 1));
        Sidebar.Pins.LoadFrom(pins);
    }

    static string[] PinIds()
    {
        var ids = new string[Sidebar.Pins.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = Sidebar.Pins[i].Id;
        return ids;
    }

    // ── the Outline ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reading_the_outline_fills_the_rows_and_the_band_of_the_active_layout()
    {
        var rows = new List<SidebarOutlineRow>();
        var band = new List<string>();

        var layout = _edit.ReadOutline(showAllPins: false, rows, band);

        Assert.Equal(SidebarLayoutId.Classic, layout);
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Kind == SidebarOutlineRowKind.Home);
        Assert.Equal(Band(), band);
        Assert.Same(Sidebar.State, _edit.State);
    }

    // ── "you showed an empty section" ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Showing_a_section_with_nothing_in_it_is_allowed_and_remembered_for_done()
    {
        Assert.True(_edit.HasNothingToShow(SidebarSectionKind.Pinned));
        Assert.False(_edit.HasNothingToShow(SidebarSectionKind.Collections));   // its items are the catalogue's

        Assert.True(_edit.SetSectionShown(SidebarLayoutId.Classic, "recent", SidebarSectionKind.Recent, "Recently played", shown: true));
        Assert.Equal("Recently played", _edit.EnabledEmptySection);
        Assert.False(Section(SidebarLayoutId.Classic, "recent").Hidden);

        Assert.True(_edit.SetSectionShown(SidebarLayoutId.Classic, "recent", SidebarSectionKind.Recent, "Recently played", shown: false));
        Assert.Null(_edit.EnabledEmptySection);                                   // hiding it again withdraws the note
    }

    [Fact]
    public void Hiding_another_section_leaves_the_note_alone()
    {
        _edit.SetSectionShown(SidebarLayoutId.Classic, "recent", SidebarSectionKind.Recent, "Recently played", shown: true);
        _edit.SetSectionShown(SidebarLayoutId.Classic, "collections", SidebarSectionKind.Collections, "Collections", shown: false);

        Assert.Equal("Recently played", _edit.EnabledEmptySection);
    }

    [Fact]
    public void Leaving_edit_clears_the_note()
    {
        Sidebar.EnterEdit();
        Assert.True(Sidebar.Editing.Peek());
        _edit.SetSectionShown(SidebarLayoutId.Classic, "recent", SidebarSectionKind.Recent, "Recently played", shown: true);

        _edit.Exit();

        Assert.False(Sidebar.Editing.Peek());
        Assert.Null(_edit.EnabledEmptySection);
    }

    [Fact]
    public void Hiding_the_noted_section_clears_its_note()
    {
        _edit.SetSectionShown(SidebarLayoutId.Classic, "no-such-section", SidebarSectionKind.Recent, "Nothing", shown: true);
        _edit.SetSectionShown(SidebarLayoutId.Classic, "no-such-section", SidebarSectionKind.Recent, "Nothing", shown: false);
        Assert.Null(_edit.EnabledEmptySection);
    }

    // ── items, settings, moves ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hiding_a_library_kind_steps_the_active_chip_off_it()
    {
        Sidebar.SwitchLayout(SidebarLayoutId.Library);
        Sidebar.SetLibraryFilter(SidebarLibraryFilter.Albums);

        Assert.True(_edit.SetItemShown(SidebarLayoutId.Library, "library", "albums", SidebarSectionKind.Library, shown: false));

        Assert.Contains("albums", Section(SidebarLayoutId.Library, "library").HiddenList);
        Assert.Equal(SidebarLibraryFilter.None, Sidebar.LibraryFilter.Peek());
    }

    [Fact]
    public void Hiding_a_collections_page_does_not_touch_the_filter()
    {
        Sidebar.SetLibraryFilter(SidebarLibraryFilter.Albums);

        Assert.True(_edit.SetItemShown(SidebarLayoutId.Classic, "collections", "albums", SidebarSectionKind.Collections, shown: false));

        Assert.Contains("albums", Section(SidebarLayoutId.Classic, "collections").HiddenList);
        Assert.Equal(SidebarLibraryFilter.Albums, Sidebar.LibraryFilter.Peek());
    }

    [Fact]
    public void The_settings_row_has_its_own_show_op()
    {
        _edit.SetSettingsShown(SidebarLayoutId.Classic, false);
        Assert.True(Section(SidebarLayoutId.Classic, "settings").Hidden);
        _edit.SetSettingsShown(SidebarLayoutId.Classic, true);
        Assert.False(Section(SidebarLayoutId.Classic, "settings").Hidden);
    }

    [Fact]
    public void A_dragged_section_and_a_dragged_page_land_where_they_were_dropped()
    {
        var band = Band();
        string first = band[0];
        _edit.MoveSection(first, 1);
        Assert.Equal(first, Band()[1]);

        _edit.MoveItem("collections", "audiobooks", 0);
        Assert.Equal("audiobooks", Order("collections", SidebarSectionKind.Collections)[0]);
    }

    // ── undo and redo ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_op_is_one_undo_entry_and_undo_then_redo_walk_it()
    {
        Sidebar.Dispatch(new SetSectionShown(SidebarLayoutId.Classic, "recent", true));
        Assert.True(_edit.CanUndo);

        _edit.Undo();
        Assert.True(Section(SidebarLayoutId.Classic, "recent").Hidden);
        Assert.True(_edit.CanRedo);

        _edit.Redo();
        Assert.False(Section(SidebarLayoutId.Classic, "recent").Hidden);
    }

    [Fact]
    public void The_ring_version_moves_with_the_ring()
    {
        int before = _edit.RingVersion.Peek();
        Sidebar.Dispatch(new SetSectionShown(SidebarLayoutId.Classic, "recent", true));
        Assert.True(_edit.RingVersion.Peek() > before);
    }

    // ── the key table: the pane ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Esc_leaves_edit_and_a_modified_esc_is_not_ours()
    {
        Sidebar.EnterEdit();

        Assert.False(_edit.OnPaneKey(NavEditKey.Escape, NavEditMods.Shift));
        Assert.True(Sidebar.Editing.Peek());

        Assert.True(_edit.OnPaneKey(NavEditKey.Escape, NavEditMods.None));
        Assert.False(Sidebar.Editing.Peek());
    }

    [Fact]
    public void Ctrl_Z_and_Ctrl_Y_undo_and_redo_but_only_with_ctrl_alone()
    {
        Sidebar.Dispatch(new SetSectionShown(SidebarLayoutId.Classic, "recent", true));

        Assert.False(_edit.OnPaneKey(NavEditKey.Z, NavEditMods.Ctrl | NavEditMods.Shift));
        Assert.False(_edit.OnPaneKey(NavEditKey.Z, NavEditMods.None));
        Assert.False(Section(SidebarLayoutId.Classic, "recent").Hidden);

        Assert.True(_edit.OnPaneKey(NavEditKey.Z, NavEditMods.Ctrl));
        Assert.True(Section(SidebarLayoutId.Classic, "recent").Hidden);

        Assert.True(_edit.OnPaneKey(NavEditKey.Y, NavEditMods.Ctrl));
        Assert.False(Section(SidebarLayoutId.Classic, "recent").Hidden);
    }

    // ── the key table: a section wrapper ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Enter_on_a_section_toggles_its_show_when_it_can_be_hidden()
    {
        Assert.True(Section(SidebarLayoutId.Classic, "recent").Hidden);
        Assert.True(_edit.OnSectionKey("recent", 3, 5, NavEditKey.Enter, NavEditMods.None));
        Assert.False(Section(SidebarLayoutId.Classic, "recent").Hidden);
        Assert.True(_edit.OnSectionKey("recent", 3, 5, NavEditKey.Enter, NavEditMods.None));
        Assert.True(Section(SidebarLayoutId.Classic, "recent").Hidden);

        Assert.True(_edit.OnSectionKey("home", 0, 5, NavEditKey.Enter, NavEditMods.None));   // Home is locked: handled, unchanged
        Assert.False(Section(SidebarLayoutId.Classic, "home").Hidden);

        Assert.False(_edit.OnSectionKey("recent", 3, 5, NavEditKey.Enter, NavEditMods.Ctrl)); // not ours
        Assert.True(Section(SidebarLayoutId.Classic, "recent").Hidden);
    }

    [Fact]
    public void Alt_arrows_move_a_section_one_step_and_alt_shift_arrows_to_the_edge()
    {
        var band = Band();
        string first = band[0];

        Assert.True(_edit.OnSectionKey(first, 0, band.Count, NavEditKey.Down, NavEditMods.Alt));
        Assert.Equal(first, Band()[1]);

        Assert.True(_edit.OnSectionKey(first, 1, band.Count, NavEditKey.Down, NavEditMods.Alt | NavEditMods.Shift));
        Assert.Equal(first, Band()[band.Count - 1]);

        Assert.True(_edit.OnSectionKey(first, band.Count - 1, band.Count, NavEditKey.Up, NavEditMods.Alt | NavEditMods.Shift));
        Assert.Equal(first, Band()[0]);

        Assert.False(_edit.OnSectionKey(first, 0, band.Count, NavEditKey.Down, NavEditMods.None));   // plain arrows are the list's
    }

    // ── the key table: a pin ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Alt_arrows_move_a_pin_and_enter_is_swallowed_so_it_cannot_toggle_pinned()
    {
        LoadPins("a", "b", "c");
        string a = "pl:spotify:playlist:a", b = "pl:spotify:playlist:b", c = "pl:spotify:playlist:c";

        Assert.True(_edit.OnPinKey(0, NavEditKey.Down, NavEditMods.Alt));
        Assert.Equal(new[] { b, a, c }, PinIds());

        Assert.True(_edit.OnPinKey(2, NavEditKey.Up, NavEditMods.Alt | NavEditMods.Shift));      // to the edge
        Assert.Equal(new[] { c, b, a }, PinIds());

        Assert.True(_edit.OnPinKey(0, NavEditKey.Enter, NavEditMods.None));
        Assert.True(_edit.OnPinKey(0, NavEditKey.Enter, NavEditMods.Alt));                         // any modifier: a pin has no Show
        Assert.Equal(new[] { c, b, a }, PinIds());

        Assert.False(_edit.OnPinKey(0, NavEditKey.Down, NavEditMods.None));                        // plain arrows are not ours
        Assert.False(_edit.OnPinKey(0, NavEditKey.Other, NavEditMods.Alt));
    }

    [Fact]
    public void A_pin_at_the_edge_does_not_move_but_the_key_is_still_ours()
    {
        LoadPins("a", "b");
        Assert.True(_edit.OnPinKey(0, NavEditKey.Up, NavEditMods.Alt));
        Assert.Equal(new[] { "pl:spotify:playlist:a", "pl:spotify:playlist:b" }, PinIds());
    }

    // ── the key table: a collections page ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Enter_on_a_page_toggles_that_pages_show_never_the_sections()
    {
        var pages = Order("collections", SidebarSectionKind.Collections);

        Assert.True(_edit.OnItemKey(1, pages, "albums", shown: true, NavEditKey.Enter, NavEditMods.None));
        Assert.Contains("albums", Section(SidebarLayoutId.Classic, "collections").HiddenList);
        Assert.False(Section(SidebarLayoutId.Classic, "collections").Hidden);

        Assert.True(_edit.OnItemKey(1, pages, "albums", shown: false, NavEditKey.Enter, NavEditMods.None));
        Assert.DoesNotContain("albums", Section(SidebarLayoutId.Classic, "collections").HiddenList);

        Assert.False(_edit.OnItemKey(1, pages, "albums", shown: true, NavEditKey.Enter, NavEditMods.Alt));   // Enter wants no modifier
    }

    [Fact]
    public void Alt_arrows_move_a_page_in_its_order()
    {
        var pages = Order("collections", SidebarSectionKind.Collections);
        string second = pages[1];

        Assert.True(_edit.OnItemKey(1, pages, second, shown: true, NavEditKey.Up, NavEditMods.Alt));
        Assert.Equal(second, Order("collections", SidebarSectionKind.Collections)[0]);

        // The view hands the CURRENT order each time (it rebuilds the list every render): so does this.
        pages = Order("collections", SidebarSectionKind.Collections);
        Assert.Equal(second, pages[0]);
        Assert.True(_edit.OnItemKey(0, pages, second, shown: true, NavEditKey.Down, NavEditMods.Alt | NavEditMods.Shift));
        Assert.Equal(second, Order("collections", SidebarSectionKind.Collections)[pages.Length - 1]);

        Assert.False(_edit.OnItemKey(1, pages, second, shown: true, NavEditKey.Up, NavEditMods.None));
    }
}
