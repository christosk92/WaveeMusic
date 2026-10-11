// ── Shell/NavMenuModel.cs ──────────────────────────────────────────────────────────────────────────────────────────
// what every navigation menu offers and what picking a row does: the menu for a target (pane, section header, sort,
// library options, the edit bar's Reset), the dispatch of a picked row, the section / item / kind verbs behind it, the
// folder-rename legality and commit, and the "· modified" layout name
//
// Role: CORE · Plan: technical-plan.md §5.1 row C11 (BP X29) · design C.1, C.4, Q7, Q15, Q17
//
// The rows themselves are `SidebarMenuModel`'s (pure data: no engine type, no label). This is the layer above it that the
// Classic view used to own inside `SidebarMenus`: WHICH rows a target gets, and what `Run` does with one. The view still maps
// rows to engine menu items (`SidebarMenus.Map`: labels, accelerators, the overlay) and still owns the two things that need
// a pane — the choreographed collapse of a section and a rootlist sibling step — which it passes in as an `INavMenuHost`.
//
// THREADING: UI thread only, like the service it drives.

using System.Globalization;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>What a menu row needs from the pane that opened it. Both are the pane's because they are about its plan: a section
/// toggle runs the pane's disclosure choreography, and a rootlist step is committed against the pane's tree.</summary>
public interface INavMenuHost
{
    /// <summary>Collapse (<paramref name="collapsed"/> true) or expand a section the way a header click does; a pick that names
    /// the way the section is already heading is a no-op.</summary>
    void SetSectionDirection(string sectionId, bool collapsed);

    /// <summary>One signed step through a rootlist row's sibling run (Move up -1, Move down +1).</summary>
    void MoveSibling(string entryId, int delta);
}

public enum NavMenuKind : byte
{
    /// <summary>The global pane menu (the ⋯, the seam's and the background's right-click).</summary>
    Pane,
    /// <summary>A section header's ⋯.</summary>
    Header,
    /// <summary>Your Library's sort button.</summary>
    Sort,
    /// <summary>Your Library's ⋯.</summary>
    LibraryOptions,
    /// <summary>The edit bar's Reset ▾.</summary>
    EditReset,
}

/// <summary>Which menu is wanted. <see cref="SectionId"/> is the header's section; <see cref="Filter"/> is the chip the Library
/// menus mark their rows against.</summary>
public readonly record struct NavMenuTarget(NavMenuKind Kind, string? SectionId = null, SidebarLibraryFilter Filter = SidebarLibraryFilter.None)
{
    public static NavMenuTarget Pane => new(NavMenuKind.Pane);
    public static NavMenuTarget Header(string sectionId) => new(NavMenuKind.Header, sectionId);
    public static NavMenuTarget Sort(SidebarLibraryFilter filter) => new(NavMenuKind.Sort, null, filter);
    public static NavMenuTarget LibraryOptions(SidebarLibraryFilter filter) => new(NavMenuKind.LibraryOptions, null, filter);
    public static NavMenuTarget EditReset => new(NavMenuKind.EditReset);
}

public static class NavMenuModel
{
    static readonly List<string> s_lockingNames = new(2);

    // ── the facts the labels read ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Classic" / "Classic · modified" (design C.1). The ONE layout name: the pane menu, the menu mapper, the reset
    /// toast, the undo labels and Settings › Sidebar all read it.</summary>
    public static string LayoutName(SidebarLayoutId layout)
    {
        string name = Loc.Get(layout == SidebarLayoutId.Library ? "sidebar.layoutName.library" : "sidebar.layoutName.classic");
        return SidebarLayoutRules.IsModified(Sidebar.State.Of(layout)) ? Loc.Format("sidebar.layoutName.modified", ("layout", name)) : name;
    }

    /// <summary>The names of the pins that lock Pinned (route and module pins, §P3.2), for the reason lines. A shared list: read
    /// it before the next call.</summary>
    public static IReadOnlyList<string> LockingNames()
    {
        SidebarVisibilityRules.LockingPins(Sidebar.Pins.Items, s_lockingNames);
        return s_lockingNames;
    }

    /// <summary>A section's title, as every menu, toast and Outline row names it.</summary>
    public static string SectionTitle(string id) => Loc.Get("sidebar.section.title." + id);

    // ── the rows of a target ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Does a section header have a ⋯ menu? Home, Settings and Your Library's own head do not.</summary>
    public static bool HasHeaderMenu(string sectionId)
        => SidebarCatalogue.TryKindOf(sectionId, out var kind)
           && kind is not (SidebarSectionKind.Library or SidebarSectionKind.Home or SidebarSectionKind.Settings);

    /// <summary>The rows <paramref name="target"/> offers right now, from the live layout, density, edit mode and pins. Empty
    /// when it offers none (a header with no menu).</summary>
    public static IReadOnlyList<SidebarMenuRow> Menu(in NavMenuTarget target)
    {
        switch (target.Kind)
        {
            case NavMenuKind.Pane:
                return SidebarMenuModel.Pane(Sidebar.Layout.Peek(), Sidebar.State, Sidebar.Density.Peek(),
                    Sidebar.Editing.Peek(), LockingNames());
            case NavMenuKind.Header:
                return target.SectionId is { } id && HasHeaderMenu(id)
                    ? SidebarMenuModel.Header(Sidebar.Layout.Peek(), Sidebar.State, id, LockingNames())
                    : Array.Empty<SidebarMenuRow>();
            case NavMenuKind.Sort:
                return SidebarMenuModel.Sort(Sidebar.State, target.Filter);
            case NavMenuKind.LibraryOptions:
                return SidebarMenuModel.LibraryOptions(Sidebar.State, target.Filter);
            case NavMenuKind.EditReset:
            {
                // §P4.5: this layout (recorded) and everything (confirmed, §P4.6).
                var layout = Sidebar.Layout.Peek();
                return new[]
                {
                    new SidebarMenuRow(SidebarMenuAction.ResetLayout, "sidebar.menu.resetLayout",
                        Enabled: SidebarLayoutRules.IsModified(Sidebar.State.Of(layout))),
                    new SidebarMenuRow(SidebarMenuAction.ResetEverything, "sidebar.menu.resetEverything"),
                };
            }
            default:
                return Array.Empty<SidebarMenuRow>();
        }
    }

    /// <summary>The sidebar's own verbs for one row (<see cref="SidebarMenuModel.Item"/>). The entity menu already carries the
    /// pin pair, so a Pinned row drops the model's Unpin (it would be a second "Unpin from sidebar").</summary>
    public static IReadOnlyList<SidebarMenuRow> ItemRows(SidebarSectionKind section, string arg, int index, int count,
                                                         bool unavailable, SidebarRootlistStep step)
    {
        var rows = SidebarMenuModel.Item(section, arg, index, count, unavailable, step);
        if (section != SidebarSectionKind.Pinned) return rows;
        var kept = new List<SidebarMenuRow>(rows.Count);
        for (int i = 0; i < rows.Count; i++) if (rows[i].Action != SidebarMenuAction.Unpin) kept.Add(rows[i]);
        return kept;
    }

    // ── a picked row ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Do what <paramref name="r"/> says. <paramref name="sectionId"/> is the header's section (null for the pane
    /// menu). <paramref name="host"/> is the pane that opened the menu, when there is one: without it a collapse writes the
    /// layout directly and a rootlist step does nothing. <paramref name="confirmResetEverything"/> opens the confirm dialog (a
    /// view concern); null where no overlay is at hand, which makes "Reset everything…" a no-op, as it always was.</summary>
    public static void Run(SidebarMenuRow r, string? sectionId, INavMenuHost? host, Action? confirmResetEverything)
    {
        var layout = Sidebar.Layout.Peek();
        switch (r.Action)
        {
            case SidebarMenuAction.SwitchLayout: Sidebar.SwitchLayout(r.Arg == "library" ? SidebarLayoutId.Library : SidebarLayoutId.Classic); break;
            case SidebarMenuAction.ResetLayout: Sidebar.Dispatch(new ResetLayout(layout), Loc.Format("sidebar.toast.reset", ("name", LayoutName(layout)))); break;
            case SidebarMenuAction.ShowSection: Sidebar.Dispatch(new SetSectionShown(layout, r.Arg, true)); break;
            case SidebarMenuAction.SetDensity: Sidebar.SetDensity(r.Arg == "compact" ? SidebarDensity.Compact : SidebarDensity.Default); break;
            case SidebarMenuAction.EditSidebar: Sidebar.EnterEdit(); break;
            case SidebarMenuAction.ResetEverything when confirmResetEverything is not null: confirmResetEverything(); break;
            case SidebarMenuAction.SetSort:
                SidebarStoreV3.TryParseSort(r.Arg, out var sort);
                Sidebar.Dispatch(new SetLibrarySort(sort, Sidebar.Doc.Library.Descending));
                break;
            case SidebarMenuAction.ToggleDescending: Sidebar.Dispatch(new SetLibrarySort(Sidebar.Doc.Library.Sort, !Sidebar.Doc.Library.Descending)); break;
            case SidebarMenuAction.SetView: Sidebar.Dispatch(new SetLibraryView(r.Arg == "grid" ? SidebarLibraryView.Grid : SidebarLibraryView.List)); break;
            case SidebarMenuAction.ToggleLiked: Sidebar.Dispatch(new SetShowLiked(!Sidebar.Doc.Library.ShowLiked)); break;
            case SidebarMenuAction.ToggleKind: ToggleKind(r.Arg); break;
            case SidebarMenuAction.SetLimit when sectionId is not null:
                Sidebar.Dispatch(new SetSectionLimit(layout, sectionId, int.Parse(r.Arg, CultureInfo.InvariantCulture)));
                break;
            case SidebarMenuAction.ShowItem when sectionId is not null: Sidebar.Dispatch(new SetItemShown(layout, sectionId, r.Arg, true)); break;
            // From a header menu the pane is at hand: the toggle runs the same choreography as a click.
            case SidebarMenuAction.Collapse when sectionId is not null:
                if (host is not null) host.SetSectionDirection(sectionId, true);
                else Sidebar.Dispatch(new SetSectionCollapsed(layout, sectionId, true));
                break;
            case SidebarMenuAction.Expand when sectionId is not null:
                if (host is not null) host.SetSectionDirection(sectionId, false);
                else Sidebar.Dispatch(new SetSectionCollapsed(layout, sectionId, false));
                break;
            case SidebarMenuAction.MoveUp when sectionId is not null: MoveBy(layout, sectionId, r.Arg, -1); break;
            case SidebarMenuAction.MoveDown when sectionId is not null: MoveBy(layout, sectionId, r.Arg, +1); break;
            case SidebarMenuAction.HideSection when (r.Arg.Length > 0 ? r.Arg : sectionId) is { } hideId:
                // The pane menu's "Hide pinned" carries its section in Arg; a header's Hide uses the header's id.
                Sidebar.Dispatch(new SetSectionShown(layout, hideId, false), Loc.Format("sidebar.toast.hidden", ("name", SectionTitle(hideId))));
                break;
            case SidebarMenuAction.UnpinAllShortcuts: Sidebar.UnpinAllShortcutsRecorded(); break;
            case SidebarMenuAction.Unpin: Sidebar.Model.Unpin(r.Arg, null); break;
            case SidebarMenuAction.MovePinUp: Sidebar.Model.MovePin(Sidebar.Model.Pins.IndexOf(r.Arg), Sidebar.Model.Pins.IndexOf(r.Arg) - 1); break;
            case SidebarMenuAction.MovePinDown: Sidebar.Model.MovePin(Sidebar.Model.Pins.IndexOf(r.Arg), Sidebar.Model.Pins.IndexOf(r.Arg) + 1); break;
            case SidebarMenuAction.HideItem: HideItem(layout, sectionId, r.Arg); break;
            // One sibling step through WaveeResourceDrop.MoveRootlist: legality, undo anchors and the refusal toast are the
            // drop's own (the pane's MoveSibling → CommitMove).
            case SidebarMenuAction.MoveRootlistUp when host is not null: host.MoveSibling(r.Arg, -1); break;
            case SidebarMenuAction.MoveRootlistDown when host is not null: host.MoveSibling(r.Arg, 1); break;
        }
    }

    /// <summary>A Library kind's Filters row. Showing a kind back is quiet (its chip reappears); hiding one toasts
    /// "{name} hidden · Undo" (C.5 / Q14) and steps the filter off a kind that just vanished.</summary>
    public static void ToggleKind(string kind)
    {
        bool wasHidden = (Sidebar.Doc.Library.HiddenKinds & SidebarCatalogue.KindFlagOf(kind)) != 0;
        Sidebar.Dispatch(new SetItemShown(SidebarLayoutId.Library, "library", kind, wasHidden),
                         wasHidden ? null : Loc.Format("sidebar.toast.hidden", ("name", Loc.Get("sidebar.item." + kind))));
        if (!wasHidden)
            Sidebar.SetLibraryFilter(SidebarLibraryFilters.Effective((int)Sidebar.LibraryFilter.Peek(), Sidebar.Doc.Library.HiddenKinds));
    }

    /// <summary>Move up / Move down on a row: an item arg moves a Collections page inside its effective order (±1, the reducer
    /// rejects an out-of-range step); an empty arg moves the section one band slot.</summary>
    public static void MoveBy(SidebarLayoutId layout, string sectionId, string itemArg, int delta)
    {
        if (itemArg.Length > 0)
        {
            if (!SidebarCatalogue.TryKindOf(sectionId, out var kind) || Sidebar.State.Of(layout).Find(sectionId) is not { } section) return;
            int at = IndexOfItem(SidebarLayoutRules.EffectiveItemOrder(layout, kind, section), itemArg);
            if (at >= 0) Sidebar.Dispatch(new MoveItem(layout, sectionId, itemArg, at + delta));
            return;
        }
        var band = new List<string>(8);
        SidebarEditRules.Band(Sidebar.State.Of(layout), band);
        int slot = band.IndexOf(sectionId);
        if (slot >= 0) Sidebar.Dispatch(new MoveSection(layout, sectionId, slot + delta));
    }

    /// <summary>Hide a row's item (§P4.6): a Collections page; the Library's Liked row; the footer's Settings. Decided by the
    /// row's SECTION, not the item id: Classic's Collections "Liked Songs" is a page like any other. Hiding the last visible
    /// page hides Collections itself, so its toast names the SECTION (design C.4 corner case).</summary>
    public static void HideItem(SidebarLayoutId layout, string? sectionId, string item)
    {
        string settings = SidebarCatalogue.IdOf(SidebarSectionKind.Settings);
        if (string.Equals(item, settings, StringComparison.Ordinal))
        {
            // The toast says where Settings went: the profile menu keeps it (§P4.6).
            Sidebar.Dispatch(new SetSectionShown(layout, settings, false), Loc.Get("sidebar.toast.settingsHidden"));
            return;
        }
        if (sectionId is null || !SidebarCatalogue.TryKindOf(sectionId, out var kind)) return;
        if (kind == SidebarSectionKind.Library)
        {
            // Your Library's Liked row: the Library layout's ShowLiked flag.
            if (string.Equals(item, SidebarCatalogue.LikedRoute, StringComparison.Ordinal))
                Sidebar.Dispatch(new SetShowLiked(false), Loc.Format("sidebar.toast.hidden", ("name", Loc.Get("sidebar.item." + item))));
            return;
        }
        // A Collections page (Liked included in Classic).
        bool last = Sidebar.Doc.Find(SidebarSectionKind.Collections) is { Items.Count: 1 };
        string name = last ? SectionTitle(SidebarCatalogue.IdOf(SidebarSectionKind.Collections)) : Loc.Get("sidebar.item." + item);
        Sidebar.Dispatch(new SetItemShown(layout, sectionId, item, false), Loc.Format("sidebar.toast.hidden", ("name", name)));
    }

    static int IndexOfItem(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], id, StringComparison.Ordinal)) return i;
        return -1;
    }

    // ── rename a folder ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Can this folder be renamed: it has a group id and the library seam has a rename.</summary>
    public static bool CanRenameFolder(string folderId) => folderId.Length > 0 && Sidebar.LibraryWrites?.RenameFolder is not null;

    /// <summary>The gate in front of the name prompt: false for no folder (silently) or no seam (with the refusal sentence, never
    /// a silent click — G-171). True means the view may open its prompt.</summary>
    public static bool BeginRenameFolder(string folderId)
    {
        if (folderId.Length == 0) return false;
        if (Sidebar.LibraryWrites?.RenameFolder is null)
        {
            NavModel.RefuseWrite("rename folder");
            return false;
        }
        return true;
    }

    /// <summary>What the prompt commits: only a real change (<see cref="SidebarFolderRename.Commit"/>) reaches the seam, which is
    /// re-read now (the dialog outlives the menu that opened it); the group id is untouched, so expansion and pins ride through.
    /// Returns whether a rename was sent.</summary>
    public static bool CommitFolderRename(string folderId, string currentName, string? typed)
    {
        if (SidebarFolderRename.Commit(typed, currentName) is not { } next) return false;
        if (Sidebar.LibraryWrites?.RenameFolder is { } rename)
        {
            rename(folderId, next);
            return true;
        }
        NavModel.RefuseWrite("rename folder");
        return false;
    }
}
