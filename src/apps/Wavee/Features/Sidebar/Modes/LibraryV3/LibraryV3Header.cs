using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// §3.2.3 — the header band: <c>["Your Library"]</c> · <c>[search]</c> · spacer · <c>[+]</c> · <c>[…]</c> ·
/// <c>[collapse]</c>. Folds in what used to be a second 36-DIP toolbar row (search + a standalone sort/view pill):
/// the decorative library glyph this row used to lead with was pure decoration next to a title that already says
/// "Your Library", and Sort/View now live as submenus of the <c>…</c> overflow (<see cref="V3SortViewMenu"/>)
/// rather than a second always-visible pill — a crowded 7-icon-across-two-rows header was the direct complaint
/// this collapses down to a title plus 2 icons.
///
/// <para>The overflow menu is where V3 carries locked entry point 3 (the quick sidebar-layout switch): it embeds
/// <c>SidebarLayoutMenu.Rows</c> as a SUB-MENU rather than re-declaring the three design radios, so the pane menu, the
/// Classic header button and this menu can never disagree about what switching a design does. Labels are resolved AT OPEN
/// TIME, never in the render body — <c>Loc.Get</c> reads the culture epoch, and a static header button must not subscribe
/// to it four times over (the docked pane and the drawer each keep an expanded and a compact body mounted).</para>
/// </summary>
sealed class LibraryV3Header : Component
{
    readonly LibraryV3Session _session;

    public LibraryV3Header(LibraryV3Session session) => _session = session;

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);

        void ToggleOverflow()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var items = BuildOverflow(prefs);
            if (items.Count == 0) return;
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close(), minWidth: 220f),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        // W5 — the overflow anchor's NodeHandle must survive IconButton.Create's own re-assertion of
        // OnClick/Role/Children on its returned root (IconButton.cs's final `with { ... }`) — a Parts modifier is the
        // one thing that DOES win over that re-assertion, so OnRealized rides through it. Memoised once (mutating a
        // TemplateParts bumps its Epoch, which must not happen per render).
        var overflowParts = UseMemo(() =>
        {
            var m = new TemplateParts();
            m[IconButton.PartRoot] = b => b with { OnRealized = h => anchor.Value = h };
            return m;
        }, DepKey.Empty);

        // The search host now shares this row (it used to own a whole second toolbar row alone with the sort/view
        // pill, which is gone). ONE rule for the row's shape (LibraryV3SearchRules.Resolve): Inline lets the field
        // grow to fill the row (the title takes only its natural width, see the spacer's Grow below); Narrow keeps
        // the field a fixed-width host and an explicit spacer pushes the button cluster to the trailing edge.
        var layout = UseComputed(() => LibraryV3SearchRules.Resolve(
            _session.Width.Value, _session.SearchOpen.Value, _session.Prefs?.V3Search.Value is { Length: > 0 }));
        bool inline = layout.Value.Inline;

        var kids = new List<Element>(6)
        {
            new TextEl(Loc.Get(Strings.Sidebar.V3.Title))
            {
                // Ui.BodyStrong is 14/20/600; the header title sits one rung up (15) and no alias covers that exact
                // size, so it stays an explicit override rather than a one-call-site alias (W5).
                // Grow=0 (not the old 1/Basis=0): the search host is the row's OTHER flexible sibling now, and two
                // Grow=1 elements would split the remaining space by ratio instead of the title staying its natural
                // ("Your Library" is short and fixed) width.
                Size = 15f, Weight = 600, Color = Tok.TextPrimary,
                Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            Embed.Comp(() => new LibraryV3Search(_session)) with { Key = "v3-search" },
            new BoxEl { Key = "v3-header-spacer", Grow = inline ? 0f : 1f },
            // H2 (#85) — the same drop spec Classic's section-header "+" gets: a rootlist selection files into a new
            // top-level folder, a track set becomes a new playlist. V3 never sets SidebarPaneConfig.HeaderCreate (it
            // renders no section headers at all), so this button needed its own copy of that spec.
            Embed.Comp(() => new SidebarCreateButton(
                _session.CreatePlaylist, menu: CreateMenu,
                drop: _session.HeaderCreateDropSpec(), dropActive: () => _session.HeaderCreateDropActive.Value,
                box: 28f, glyph: 14f)),
            // W5 — IconButton.Create gives the overflow button its focus ring, Space/Enter activation and
            // AutomationRole for free; the hand-rolled BoxEl this used to be had none of those.
            ToolTip.Wrap(
                IconButton.Create(Icons.More, ToggleOverflow, parts: overflowParts, size: ControlSize.Small)
                    with { Key = "v3-overflow" },
                Loc.Get(Strings.Sidebar.Layout.MenuTitle)),
        };

        // A drawer has no rail to collapse INTO, so the affordance is absent rather than dead (§3.2.14).
        if (!_session.InDrawer)
            kids.Add(ToolTip.Wrap(
                IconButton.Create(Icons.ChevronLeft, _session.Collapse, size: ControlSize.Small)
                    with { Key = "v3-collapse" },
                Loc.Get(Strings.Sidebar.V3.Collapse)));

        return new BoxEl
        {
            Direction = 0, Height = LibraryV3Metrics.HeaderHeight, AlignItems = FlexAlign.Center, Gap = 4f,
            // W7 — LeadBandInset, not BandInset: the header's leading glyph must land on the rows' ART COLUMN (27) -
            // the same edge the nav band and the closed search host share — rather than merely the content lane (14)
            // BandInset gives full-width rules/dividers. This band is a SIBLING of the padded list either way, so
            // SidebarPaneMetrics.PanePad never reaches it.
            Padding = SidebarPaneMetrics.LeadBandInset,
            Children = [.. kids],
        };
    }

    /// <summary>The header "+"'s flyout — [New playlist · New folder], the same two verbs every other "+" in the app
    /// offers. Built at OPEN time; null (no library bridge yet) makes the button fall back to its plain click.</summary>
    ContextMenuModel? CreateMenu()
    {
        if (_session is not { Acts: { Library: not null } acts } session) return null;
        return new ContextMenuModel(new List<MenuFlyoutItem>(2)
        {
            new(Loc.Get(Strings.Detail.NewPlaylist), ActionIcons.Resolve(ActionIcons.Add), true, session.CreatePlaylist),
            new(Loc.Get(Strings.Sidebar.CreateFolder), ActionIcons.Resolve(ActionIcons.Folder),
                acts.Overlay is not null, () => FolderActions.NewFolder(acts, null)),
        });
    }

    /// <summary>The overflow rows, built at OPEN time (§3.2.3's exact order).</summary>
    List<MenuFlyoutItem> BuildOverflow(SidebarPreferences? prefs)
    {
        var rows = new List<MenuFlyoutItem>(8);
        var layoutRows = SidebarLayoutMenu.Rows(prefs, _session.Go);
        if (layoutRows.Count > 0)
        {
            rows.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Sidebar.Layout.MenuTitle), layoutRows, Icons.SplitView));
            rows.Add(MenuFlyoutItem.Separator);
        }

        // Sort/View USED TO be a standalone always-visible pill (V3SortViewTrigger, now deleted) sharing a second
        // toolbar row with search. Folded in here as two submenus — the same "misc controls live in the overflow"
        // precedent the layout-mode submenu above already set — so the header collapses to title + 2 icons.
        if (prefs is not null)
        {
            rows.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Library.SortBy), V3SortViewMenu.SortRows(prefs), Icons.Sort));
            rows.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Library.ViewAs), V3SortViewMenu.ViewRows(prefs),
                LibraryV3Labels.ViewGlyph(LibraryV3Metrics.NormalizeView(prefs.V3View.Value))));
            rows.Add(MenuFlyoutItem.Separator);
        }

        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.V3.ClearFilters), Icons.Cancel,
                                    _session.AnyFilterActive, _session.ClearAllFilters));

        // The standalone "Collapse" row that used to live here too is gone: it was a same-screen duplicate of the
        // header's own "<" chevron (both visible together whenever this overflow is open) — a safe, independent cut
        // (the chevron itself, and the rail footer's opposite-direction expand tile, are NOT duplicates of each
        // other: they live in mutually exclusive mount states and both stay).

        // DEVELOPER SURFACE, hidden unless developer mode is on (Settings ▸ Diagnostics) — the same gate Classic's
        // Tools section rides. Deliberately unlocalized, matching Classic's DevToolsRow: it is a dev entry point, not
        // product surface. `Peek`, not `Value`: this list is built at OPEN time (a click handler), not inside a render,
        // so there is nothing to subscribe — the next open simply reads the switch again.
        if (DeveloperMode.Enabled.Peek())
        {
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(new MenuFlyoutItem("API Console", Icons.Code, true,
                                        () => _session.Go(DeveloperMode.ApiConsoleRoute, null)));
        }
        return rows;
    }
}
