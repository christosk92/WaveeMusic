// ── Shell/Sidebar.Edit.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the pure half of Edit mode, the menus and the undo ring: what the Outline shows and how it moves, what every menu
// offers in every state, and the 50-entry inverse ring
//
// Role: CORE
// Plan: docs/plans/wavee/sidebar-rework-implementation.md §P4.1-§P4.3 · design C.3-C.5, Q3, Q6-Q8, Q14-Q17
//
// Engine-free apart from the reorder enum; it allocates only per user action (never per frame), so Wavee.Tests pins
// every decision here.

using System;
using System.Collections.Generic;
using FluentGpu.Controls;

namespace Wavee;

/// <summary>One row of the Outline (design C.3), as data.</summary>
public enum SidebarOutlineRowKind : byte
{
    /// <summary>Home: locked, outside the band, first.</summary>
    Home = 0,
    /// <summary>A section block's header: Show checkbox (or a lock), title, summary, grip when movable.</summary>
    Section = 1,
    /// <summary>An item of an item section (Collections pages; Library's Filters kinds): checkbox, glyph, title, grip.</summary>
    Item = 2,
    /// <summary>A pin (Q3): art/glyph, title, Unpin, grip.</summary>
    Pin = 3,
    /// <summary>"Show all 312 pins" — the fold above <see cref="SidebarEditRules.PinFoldAt"/>.</summary>
    ShowAllPins = 4,
    /// <summary>A data section's one-line summary or an empty section's hint.</summary>
    Hint = 5,
    /// <summary>The footer's Settings row with its Show checkbox (the Outline replaces the footer, Q7).</summary>
    Settings = 6,
}

public readonly record struct SidebarOutlineRow(
    SidebarOutlineRowKind Kind,
    string SectionId,
    string ItemId,
    bool Shown,
    bool Movable,
    /// <summary>The checkbox is disabled (a locked section shows a lock; a locked Pinned shows the reason).</summary>
    bool Locked,
    int Count);

public static class SidebarEditRules
{
    /// <summary>Pins beyond this many fold behind "Show all" (Q3).</summary>
    public const int PinFoldAt = 50;
    public const float SectionHeaderHeight = 40f;
    public const float RowHeight = 40f;
    public const float HintHeight = 44f;

    /// <summary>The Outline's rows for <paramref name="layout"/> in display order: Home; then the BAND (Classic: every
    /// movable section, each with its body; Library: Pinned then Filters — a fixed order, design Q2); then Your Library
    /// (Library, locked); then Settings.</summary>
    public static void Outline(SidebarLayoutState state, SidebarLayoutId layout, IReadOnlyList<SidebarPin> pins,
                               bool pinnedLocked, bool showAllPins, int playlistCount, List<SidebarOutlineRow> into)
    {
        into.Clear();
        var overlay = state.Of(layout);
        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Home, "home", "", true, false, true, 0));
        for (int i = 0; i < overlay.Sections.Count; i++)
        {
            var s = overlay.Sections[i];
            if (!SidebarCatalogue.TryKindOf(s.Id, out var kind)) continue;
            if (kind is SidebarSectionKind.Home or SidebarSectionKind.Settings) continue;
            bool movable = SidebarCatalogue.Movable(layout, kind);
            bool locked = !SidebarCatalogue.Hideable(kind) || (kind == SidebarSectionKind.Pinned && pinnedLocked && !s.Hidden);
            int count = kind switch
            {
                SidebarSectionKind.Pinned => pins.Count,
                SidebarSectionKind.Playlists => playlistCount,
                _ => SidebarCatalogue.ItemsOf(layout, kind).Count - s.HiddenList.Count,
            };
            into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Section, s.Id, "", !s.Hidden, movable, locked, count));
            switch (kind)
            {
                case SidebarSectionKind.Pinned:
                    int shown = showAllPins ? pins.Count : Math.Min(pins.Count, PinFoldAt);
                    for (int p = 0; p < shown; p++)
                        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Pin, s.Id, pins[p].Id, true, true, false, 0));
                    if (pins.Count > shown)
                        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.ShowAllPins, s.Id, "", true, false, false, pins.Count));
                    if (pins.Count == 0)
                        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Hint, s.Id, "", true, false, false, 0));
                    break;
                case SidebarSectionKind.Collections:
                case SidebarSectionKind.Library:
                    var items = SidebarLayoutRules.EffectiveItemOrder(layout, kind, s);
                    for (int k = 0; k < items.Count; k++)
                        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Item, s.Id, items[k], !Contains(s.HiddenList, items[k]),
                            SidebarCatalogue.ItemsMovable(kind), false, 0));
                    break;
                default:
                    into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Hint, s.Id, "", !s.Hidden, false, false, count));
                    break;
            }
        }
        var settings = overlay.Find("settings");
        into.Add(new SidebarOutlineRow(SidebarOutlineRowKind.Settings, "settings", "", settings is { Hidden: false }, false, false, 0));
    }

    /// <summary>The SECTION band's sections (Classic: the movable ones in display order; Library: none — its sections keep
    /// a fixed order). Only the section band: the Pinned block's inner pins band exists in BOTH layouts (Q3, P.2a).</summary>
    public static void Band(LayoutOverlay overlay, List<string> into)
    {
        into.Clear();
        for (int i = 0; i < overlay.Sections.Count; i++)
            if (SidebarCatalogue.TryKindOf(overlay.Sections[i].Id, out var kind) && SidebarCatalogue.Movable(overlay.Layout, kind))
                into.Add(overlay.Sections[i].Id);
    }

    /// <summary>A band item's extent: its header plus its body rows (the Reorderable's variable-extent slot pitch).</summary>
    public static float ExtentOf(SidebarLayoutId layout, SectionState s, int pinCount, bool showAllPins)
    {
        if (!SidebarCatalogue.TryKindOf(s.Id, out var kind)) return SectionHeaderHeight;
        float body = kind switch
        {
            SidebarSectionKind.Pinned => pinCount == 0 ? HintHeight
                : RowHeight * (showAllPins ? pinCount : Math.Min(pinCount, PinFoldAt)) + (pinCount > PinFoldAt && !showAllPins ? RowHeight : 0f),
            SidebarSectionKind.Collections or SidebarSectionKind.Library => RowHeight * SidebarCatalogue.ItemsOf(layout, kind).Count,
            _ => HintHeight,
        };
        return SectionHeaderHeight + body;
    }

    /// <summary>A section header's one-line summary in the Outline (Caption, <c>TextTertiary</c>), as a loc key formatted
    /// with <c>("count", row.Count)</c> and <c>("total", SidebarCatalogue.ItemsOf(layout, kind).Count)</c>; null = no
    /// summary. Collections states its coupling (Q5): hiding its last page hides the section itself, and showing the
    /// section back restores its pages — so its summary says so where the user is about to uncheck the last one.</summary>
    public static string? SummaryKeyOf(SidebarSectionKind kind, int count) => kind switch
    {
        SidebarSectionKind.Pinned => "sidebar.edit.pinsSummary",
        SidebarSectionKind.Playlists => "sidebar.edit.playlistsSummary",
        SidebarSectionKind.Collections => count <= 1 ? "sidebar.edit.collectionsLastSummary" : "sidebar.edit.collectionsSummary",
        _ => null,
    };

    /// <summary>Alt+↑/↓ (one step) and Alt+Shift+↑/↓ (to the edge) over <paramref name="count"/> slots.</summary>
    public static int MoveTarget(int slot, int count, int direction, bool toEdge)
    {
        if (count <= 0) return -1;
        if (toEdge) return direction < 0 ? 0 : count - 1;
        return Math.Clamp(slot + direction, 0, count - 1);
    }

    /// <summary>The spoken sentence's loc key for a reorder milestone ("Grabbed {name}, position {pos} of {count}").</summary>
    public static string AnnounceKey(ReorderAnnounceKind kind) => kind switch
    {
        ReorderAnnounceKind.Grab => "sidebar.edit.announceGrabbed",
        ReorderAnnounceKind.Move => "sidebar.edit.announceMoved",
        ReorderAnnounceKind.Drop => "sidebar.edit.announceDropped",
        _ => "sidebar.edit.announceCancelled",
    };

    static bool Contains(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], id, StringComparison.Ordinal)) return true;
        return false;
    }
}

public enum SidebarMenuAction : byte
{
    None = 0,
    SwitchLayout, ResetLayout, ShowSection, SetDensity, EditSidebar, ResetEverything,
    SetSort, SetView, ToggleLiked, ToggleKind, SetLimit, ShowItem, Collapse, Expand, MoveUp, MoveDown, HideSection,
    UnpinAllShortcuts, Unpin, MovePinUp, MovePinDown, HideItem, MoveRootlistUp, MoveRootlistDown,
    ToggleDescending, NavigatePage,
}

/// <summary>A rootlist row's one-step moves (from <c>PaneView.RootlistStepOf</c>: <c>TreeMoves</c>' sibling run and the marker
/// legality). <see cref="Shown"/> is false for a non-rootlist row, for a row inside a ≥2 selection, and in Your Library
/// unless Playlists · Custom order is on screen (the rootlist order is then not what the user sees).</summary>
public readonly record struct SidebarRootlistStep(bool Shown, bool CanMoveUp, bool CanMoveDown);

/// <summary>One menu row: a verb (or a submenu when <see cref="Children"/> is set, or a separator), its label key and
/// argument, its state, and — when disabled — the reason key the row shows as its tooltip.</summary>
public sealed record SidebarMenuRow(
    SidebarMenuAction Action,
    string LabelKey,
    string Arg = "",
    bool Enabled = true,
    bool Checked = false,
    bool Radio = false,
    string? ReasonKey = null,
    IReadOnlyList<SidebarMenuRow>? Children = null,
    bool Separator = false)
{
    public static readonly SidebarMenuRow Divider = new(SidebarMenuAction.None, "", Separator: true);
}

public static class SidebarMenuModel
{
    const string FinishEditing = "sidebar.pane.finishEditing";

    /// <summary>The global pane menu (design C.4): Layout ▸ · Show section ▸ · [Library: Hide pinned · Unpin all shortcuts]
    /// · Density ▸ · Edit sidebar… · ─ · Reset everything…. While editing, the layout radios and Reset everything are disabled
    /// with the reason (Q7); Density stays. Library has no Pinned header (P.2a), so its two Pinned verbs live HERE: "Hide
    /// pinned" (disabled with "Unpin {names} first" while route/module pins lock it) and "Unpin all shortcuts" (Q17, while
    /// such pins exist). <paramref name="lockingPinNames"/> = <see cref="SidebarVisibilityRules.LockingPins"/>.</summary>
    public static IReadOnlyList<SidebarMenuRow> Pane(SidebarLayoutId layout, SidebarLayoutState state, SidebarDensity density, bool editing,
                                                     IReadOnlyList<string> lockingPinNames)
    {
        var hidden = new List<SidebarMenuRow>();
        var overlay = state.Of(layout);
        for (int i = 0; i < overlay.Sections.Count; i++)
            if (overlay.Sections[i].Hidden)
                hidden.Add(new SidebarMenuRow(SidebarMenuAction.ShowSection, "sidebar.section.title." + overlay.Sections[i].Id, overlay.Sections[i].Id));
        var pinnedRows = new List<SidebarMenuRow>(2);
        if (layout == SidebarLayoutId.Library)
        {
            bool locked = lockingPinNames.Count > 0;
            if (overlay.Find("pinned") is { Hidden: false })
                pinnedRows.Add(new SidebarMenuRow(SidebarMenuAction.HideSection, "sidebar.menu.hidePinned", "pinned",
                    Enabled: !locked, ReasonKey: locked ? "sidebar.menu.unpinFirst" : null));
            if (locked)
                pinnedRows.Add(new SidebarMenuRow(SidebarMenuAction.UnpinAllShortcuts, "sidebar.menu.unpinShortcuts"));
        }
        return
        [
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.layout", Children:
            [
                new SidebarMenuRow(SidebarMenuAction.SwitchLayout, "sidebar.layoutName.classic", "classic",
                    Enabled: !editing, Checked: layout == SidebarLayoutId.Classic, Radio: true, ReasonKey: editing ? FinishEditing : null),
                new SidebarMenuRow(SidebarMenuAction.SwitchLayout, "sidebar.layoutName.library", "library",
                    Enabled: !editing, Checked: layout == SidebarLayoutId.Library, Radio: true, ReasonKey: editing ? FinishEditing : null),
                SidebarMenuRow.Divider,
                new SidebarMenuRow(SidebarMenuAction.ResetLayout, "sidebar.menu.resetLayout",
                    Enabled: SidebarLayoutRules.IsModified(overlay)),
            ]),
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.showSection",
                Enabled: hidden.Count > 0, ReasonKey: hidden.Count == 0 ? "sidebar.menu.nothingHidden" : null, Children: hidden),
            .. pinnedRows,
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.density", Children:
            [
                new SidebarMenuRow(SidebarMenuAction.SetDensity, "sidebar.density.default", "default", Checked: density == SidebarDensity.Default, Radio: true),
                new SidebarMenuRow(SidebarMenuAction.SetDensity, "sidebar.density.compact", "compact", Checked: density == SidebarDensity.Compact, Radio: true),
            ]),
            new SidebarMenuRow(SidebarMenuAction.EditSidebar, "sidebar.menu.edit", Enabled: !editing),
            SidebarMenuRow.Divider,
            new SidebarMenuRow(SidebarMenuAction.ResetEverything, "sidebar.menu.resetEverything",
                Enabled: !editing, ReasonKey: editing ? FinishEditing : null),
        ];
    }

    /// <summary>A section header's ⋯ (design C.4, section-scoped). <paramref name="lockingPinNames"/> non-empty ⇒ Pinned
    /// cannot be hidden ("Unpin {names} first") and offers "Unpin all shortcuts" (Q17).</summary>
    public static IReadOnlyList<SidebarMenuRow> Header(SidebarLayoutId layout, SidebarLayoutState state, string sectionId,
                                                       IReadOnlyList<string> lockingPinNames)
    {
        var rows = new List<SidebarMenuRow>(8);
        var overlay = state.Of(layout);
        var s = overlay.Find(sectionId);
        if (s is null || !SidebarCatalogue.TryKindOf(sectionId, out var kind)) return rows;
        if (kind is SidebarSectionKind.Recent or SidebarSectionKind.NewReleases)
        {
            int limit = s.Limit ?? SidebarCatalogue.DefaultLimit(kind);
            var choices = new List<SidebarMenuRow>(3);
            foreach (int n in SidebarCatalogue.LimitChoices)
                choices.Add(new SidebarMenuRow(SidebarMenuAction.SetLimit, "sidebar.menu.showCount", n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Checked: limit == n, Radio: true));
            rows.Add(new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.show", Children: choices));
        }
        if (kind == SidebarSectionKind.Collections && s.HiddenList.Count > 0)
        {
            var hidden = new List<SidebarMenuRow>(s.HiddenList.Count);
            for (int i = 0; i < s.HiddenList.Count; i++)
                hidden.Add(new SidebarMenuRow(SidebarMenuAction.ShowItem, "sidebar.item." + s.HiddenList[i], s.HiddenList[i]));
            rows.Add(new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.showHidden", Children: hidden));
        }
        if (kind == SidebarSectionKind.Pinned && lockingPinNames.Count > 0)
            rows.Add(new SidebarMenuRow(SidebarMenuAction.UnpinAllShortcuts, "sidebar.menu.unpinShortcuts"));
        if (SidebarCatalogue.Collapsible(kind))
            rows.Add(new SidebarMenuRow(s.Collapsed ? SidebarMenuAction.Expand : SidebarMenuAction.Collapse,
                s.Collapsed ? "sidebar.menu.expand" : "sidebar.menu.collapse"));
        if (SidebarCatalogue.Movable(layout, kind))
        {
            var band = new List<string>(8);
            SidebarEditRules.Band(overlay, band);
            int slot = band.IndexOf(sectionId);
            rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveUp, "sidebar.menu.moveUp", Enabled: slot > 0));
            rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveDown, "sidebar.menu.moveDown", Enabled: slot >= 0 && slot < band.Count - 1));
        }
        if (SidebarCatalogue.Hideable(kind))
        {
            bool locked = kind == SidebarSectionKind.Pinned && lockingPinNames.Count > 0;
            rows.Add(SidebarMenuRow.Divider);
            rows.Add(new SidebarMenuRow(SidebarMenuAction.HideSection, "sidebar.menu.hideSection", Enabled: !locked,
                ReasonKey: locked ? "sidebar.menu.unpinFirst" : null));
        }
        return rows;
    }

    /// <summary>Your Library's ⋯ (P5's toolbar): Sort ▸ · View ▸ · Filters ▸ · Show Liked Songs.</summary>
    public static IReadOnlyList<SidebarMenuRow> LibraryOptions(SidebarLayoutState state, SidebarLibraryFilter filter)
    {
        var lib = state.Library.Find("library") ?? SidebarCatalogue.DefaultState(SidebarSectionKind.Library);
        var view = lib.View ?? SidebarLibraryView.List;
        var kinds = new List<SidebarMenuRow>(4);
        var items = SidebarCatalogue.ItemsOf(SidebarLayoutId.Library, SidebarSectionKind.Library);
        for (int i = 0; i < items.Count; i++)
            kinds.Add(new SidebarMenuRow(SidebarMenuAction.ToggleKind, "sidebar.item." + items[i], items[i],
                Checked: !Contains(lib.HiddenList, items[i])));
        return
        [
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.sort", Children: Sort(state, filter)),
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.view", Children:
            [
                new SidebarMenuRow(SidebarMenuAction.SetView, "sidebar.view.list", "list", Checked: view == SidebarLibraryView.List, Radio: true),
                new SidebarMenuRow(SidebarMenuAction.SetView, "sidebar.view.grid", "grid", Checked: view == SidebarLibraryView.Grid, Radio: true),
            ]),
            new SidebarMenuRow(SidebarMenuAction.None, "sidebar.menu.filters", Children: kinds),
            new SidebarMenuRow(SidebarMenuAction.ToggleLiked, "sidebar.menu.showLiked", Checked: lib.ShowLiked ?? true),
        ];
    }

    /// <summary>The toolbar's sort button: the five sorts (Custom order only under Playlists, Q-P.2) · ─ · Reverse order.</summary>
    public static IReadOnlyList<SidebarMenuRow> Sort(SidebarLayoutState state, SidebarLibraryFilter filter)
    {
        var lib = state.Library.Find("library") ?? SidebarCatalogue.DefaultState(SidebarSectionKind.Library);
        var stored = lib.Sort ?? SidebarLibrarySort.Recents;
        var sort = SidebarLibraryHeadRules.Effective(stored, filter);
        bool custom = SidebarLibraryHeadRules.CustomOrderOffered(filter);
        var rows = new List<SidebarMenuRow>(7);
        for (int i = 0; i <= (int)SidebarLibrarySort.CustomOrder; i++)
        {
            var sv = (SidebarLibrarySort)i;
            bool isCustom = sv == SidebarLibrarySort.CustomOrder;
            rows.Add(new SidebarMenuRow(SidebarMenuAction.SetSort, SidebarLibraryHeadRules.SortKey(sv), SidebarStoreV3.SortName(sv),
                Enabled: !isCustom || custom, Checked: sort == sv, Radio: true,
                ReasonKey: isCustom && !custom ? "sidebar.sort.customOnlyPlaylists" : null));
        }
        rows.Add(SidebarMenuRow.Divider);
        rows.Add(new SidebarMenuRow(SidebarMenuAction.ToggleDescending, "sidebar.sort.reverse",
            Enabled: sort != SidebarLibrarySort.CustomOrder, Checked: lib.Descending ?? false));
        return rows;
    }

    /// <summary>The "Your Library ▾" page menu: one row per page (glyph + count), the current page checked. Navigation only.</summary>
    public static IReadOnlyList<SidebarMenuRow> Pages(IReadOnlyList<SidebarLibraryPage> pages, string? currentPage)
    {
        var rows = new List<SidebarMenuRow>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
            rows.Add(new SidebarMenuRow(SidebarMenuAction.NavigatePage, pages[i].TitleKey, pages[i].Route,
                Checked: string.Equals(pages[i].Route, currentPage, System.StringComparison.Ordinal)));
        return rows;
    }

    /// <summary>The sidebar-specific rows a row's context menu adds (the entity verbs stay the action registry's): a pin
    /// → Move up/down + Unpin; a Collections page → Move up/down + Hide from sidebar; the Library's Liked row and the
    /// footer's Settings → Hide from sidebar; a rootlist row (Classic Playlists always, Your Library under Playlists ·
    /// Custom order) → Move up/down through the rootlist (design C.4), each verb absent at its end of the sibling run, as
    /// every rootlist verb is (<c>SidebarTreeNavLayout</c>). An unavailable pin offers Unpin only.</summary>
    public static IReadOnlyList<SidebarMenuRow> Item(SidebarSectionKind section, string key, int index, int count, bool unavailable,
                                                     SidebarRootlistStep rootlist = default)
    {
        var rows = new List<SidebarMenuRow>(4);
        switch (section)
        {
            case SidebarSectionKind.Pinned:
                if (!unavailable)
                {
                    rows.Add(new SidebarMenuRow(SidebarMenuAction.MovePinUp, "sidebar.menu.moveUp", key, Enabled: index > 0));
                    rows.Add(new SidebarMenuRow(SidebarMenuAction.MovePinDown, "sidebar.menu.moveDown", key, Enabled: index < count - 1));
                }
                rows.Add(new SidebarMenuRow(SidebarMenuAction.Unpin, "sidebar.pin.unpin", key));
                break;
            case SidebarSectionKind.Collections:
                rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveUp, "sidebar.menu.moveUp", key, Enabled: index > 0));
                rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveDown, "sidebar.menu.moveDown", key, Enabled: index < count - 1));
                rows.Add(new SidebarMenuRow(SidebarMenuAction.HideItem, "sidebar.menu.hideFromSidebar", key));
                break;
            case SidebarSectionKind.Library when key == SidebarCatalogue.LikedRoute:
            case SidebarSectionKind.Settings:
                rows.Add(new SidebarMenuRow(SidebarMenuAction.HideItem, "sidebar.menu.hideFromSidebar", key));
                break;
            case SidebarSectionKind.Playlists:
            case SidebarSectionKind.Library:
                if (rootlist.Shown && rootlist.CanMoveUp)
                    rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveRootlistUp, "sidebar.menu.moveUp", key));
                if (rootlist.Shown && rootlist.CanMoveDown)
                    rows.Add(new SidebarMenuRow(SidebarMenuAction.MoveRootlistDown, "sidebar.menu.moveDown", key));
                break;
        }
        return rows;
    }

    static bool Contains(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], id, StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>One undo kind. <see cref="Batch"/> groups several leaf entries under one action.</summary>
public enum SidebarUndoKind : byte { Layout = 0, Density = 1, Pin = 2, Batch = 3 }
public enum SidebarPinChange : byte { Pinned = 0, Unpinned = 1, Moved = 2 }

/// <summary>One undoable edit (design C.5): a layout overlay pre/post image, a density pair, a pin change with what
/// its inverse needs — or a <see cref="SidebarUndoKind.Batch"/> of those (<see cref="Parts"/>) when ONE user action made
/// several changes (pin while Pinned is hidden, Reset everything, Unpin all shortcuts): one action, one entry, one toast,
/// one Undo. <see cref="Label"/> is the localized "Unpin Running" the toast says.</summary>
public sealed record SidebarUndoEntry(
    int Id,
    SidebarUndoKind Kind,
    SidebarLayoutId Layout,
    string Label,
    LayoutOverlay? Before = null,
    LayoutOverlay? After = null,
    SidebarDensity DensityBefore = SidebarDensity.Default,
    SidebarDensity DensityAfter = SidebarDensity.Default,
    SidebarPinChange PinChange = SidebarPinChange.Pinned,
    SidebarPin? Pin = null,
    int PinFrom = -1,
    int PinTo = -1,
    IReadOnlyList<SidebarUndoEntry>? Parts = null)
{
    /// <summary>One action's several changes as ONE entry. The parts are in the order they were APPLIED.</summary>
    public static SidebarUndoEntry Batch(SidebarLayoutId layout, string label, IReadOnlyList<SidebarUndoEntry> parts)
        => new(0, SidebarUndoKind.Batch, layout, label, Parts: parts);

    /// <summary>The leaf entries to apply, in order: an undo walks the parts BACKWARDS (the last change is reverted first,
    /// so an unpin batch re-inserts lowest index first), a redo FORWARDS. A leaf is itself. Nested batches flatten.</summary>
    public static void Flatten(SidebarUndoEntry entry, bool undo, List<SidebarUndoEntry> into)
    {
        if (entry.Kind != SidebarUndoKind.Batch || entry.Parts is not { } parts) { into.Add(entry); return; }
        if (undo) for (int i = parts.Count - 1; i >= 0; i--) Flatten(parts[i], true, into);
        else for (int i = 0; i < parts.Count; i++) Flatten(parts[i], false, into);
    }
}

/// <summary>THE ONE RING (design C.5): 50 inverse entries, memory only, layout AND pin edits (collapse toggles are never
/// recorded). Survives Done; cleared on a layout switch and an account switch only. Pure: the service applies the inverse.</summary>
public sealed class SidebarUndoRing
{
    public const int Capacity = 50;
    readonly List<SidebarUndoEntry> _undo = new(Capacity), _redo = new(Capacity);
    int _nextId = 1;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int Count => _undo.Count;
    public SidebarUndoEntry? Peek => _undo.Count > 0 ? _undo[^1] : null;

    /// <summary>Record an edit (assigns its id). Clears redo; drops the oldest beyond <see cref="Capacity"/>.</summary>
    public SidebarUndoEntry Push(SidebarUndoEntry entry)
    {
        var stamped = entry with { Id = _nextId++ };
        _undo.Add(stamped);
        if (_undo.Count > Capacity) _undo.RemoveAt(0);
        _redo.Clear();
        return stamped;
    }

    public bool TryUndo(out SidebarUndoEntry entry)
    {
        if (_undo.Count == 0) { entry = null!; return false; }
        entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        return true;
    }

    public bool TryRedo(out SidebarUndoEntry entry)
    {
        if (_redo.Count == 0) { entry = null!; return false; }
        entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        return true;
    }

    /// <summary>A toast's Undo acts only on ITS entry and only while it is the newest one; otherwise it is a no-op.</summary>
    public bool IsTop(int id) => _undo.Count > 0 && _undo[^1].Id == id;

    /// <summary>An "Undid … · Redo" toast's Redo acts only while ITS entry is the newest redo (a later undo or any new edit
    /// makes it stale).</summary>
    public bool IsRedoTop(int id) => _redo.Count > 0 && _redo[^1].Id == id;

    public void Clear() { _undo.Clear(); _redo.Clear(); }

    /// <summary>Q6: Ctrl+Z / Ctrl+Y act while editing, or — outside Edit — only while a sidebar toast is open; never while a
    /// text editor has focus (its own undo stack wins). Called only from the sidebar's own key handlers (focused routing:
    /// <c>PaneView.OnPaneKey</c>, <c>EditPane.OnPaneKey</c>, §P4.6), so design V.11's "focus in the pane, the head or the
    /// edit bar" is structural, not a parameter: there is no frame-global chord.</summary>
    public static bool KeyAllowed(bool editing, bool sidebarToastOpen, bool textEditorFocused)
        => !textEditorFocused && (editing || sidebarToastOpen);

    /// <summary>Is this op recorded? Collapse toggles are not (design C.5).</summary>
    public static bool Records(SidebarOp op) => op is not SetSectionCollapsed;
}
