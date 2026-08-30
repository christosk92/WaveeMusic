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
/// §3.2.3 — the header band: <c>[library glyph · "Your Library"]</c> · spacer · <c>[+]</c> · <c>[…]</c> · <c>[collapse]</c>.
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

        var kids = new List<Element>(6)
        {
            // A dedicated box (not a bare glyph) so the library mark gets breathing room off the row's own inset and
            // the title's gap, instead of sitting flush against both. Segoe MDL2 Assets  (not Icons.List/Theme's
            // Segoe Fluent Icons face) is this glyph's own font, so it must be named explicitly (Icon's `family` param)
            // — reading only a codepoint against the wrong face is how an icon renders as tofu.
            new BoxEl
            {
                Width = 20f, Height = 20f, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon("", 16f, Tok.TextSecondary, "Segoe MDL2 Assets")],
            },
            new TextEl(Loc.Get(Strings.Sidebar.V3.Title))
            {
                Size = 15f, Weight = 600, Color = Tok.TextPrimary,
                Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            Embed.Comp(() => new SidebarCreateButton(
                _session.CreatePlaylist, menu: CreateMenu, box: 28f, glyph: 14f)),
            ToolTip.Wrap(new BoxEl
            {
                Key = "v3-overflow",
                Width = 28f, Height = 28f, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnRealized = h => anchor.Value = h,
                OnClick = ToggleOverflow,
                Children = [Icon(Icons.More, 14f, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle), Loc.Get(Strings.Sidebar.Layout.MenuTitle)),
        };

        // A drawer has no rail to collapse INTO, so the affordance is absent rather than dead (§3.2.14).
        if (!_session.InDrawer)
            kids.Add(ToolTip.Wrap(new BoxEl
            {
                Key = "v3-collapse",
                Width = 28f, Height = 28f, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = _session.Collapse,
                Children = [Icon(Icons.ChevronLeft, 14f, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle), Loc.Get(Strings.Sidebar.V3.Collapse)));

        return new BoxEl
        {
            Direction = 0, Height = LibraryV3Metrics.HeaderHeight, AlignItems = FlexAlign.Center, Gap = 4f,
            // THE CONTENT LANE, not a bare 8. This band is a SIBLING of the padded list, so SidebarPaneMetrics.PanePad
            // never reaches it: padding to 8 put the library glyph 6 DIP left of every row's glyph below it — the ragged
            // left edge. BandInset is PanePad + RowInset expressed once, so the two families cannot drift again.
            Padding = SidebarPaneMetrics.BandInset,
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

        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.V3.ClearFilters), Icons.Cancel,
                                    _session.AnyFilterActive, _session.ClearAllFilters));

        if (!_session.InDrawer)
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.V3.Collapse), Icons.ChevronLeft, prefs is not null,
                                        _session.Collapse));

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

/// <summary>§3.2.2 band 2 — the search + sort row. Its own component so opening the search field or flipping a sort
/// re-renders 36 DIP of chrome instead of the whole pane.</summary>
sealed class LibraryV3Toolbar : Component
{
    readonly LibraryV3Session _session;

    public LibraryV3Toolbar(LibraryV3Session session) => _session = session;

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);

        // The sort pill collapses to icon-only when the search field is open (the field gets the row) or when the pane is
        // simply too narrow for a label. A MEMO, not a raw width read: a seam drag writes the width every frame, and the
        // memo's equality cut-off means this component re-renders only when the BOOLEAN flips.
        //
        // The body reads the service off the SESSION, never off a captured local: a memo's compute delegate is frozen at
        // mount, so a local `prefs` that was still null on the first render would stay null forever.
        var iconOnly = UseComputed(() =>
        {
            bool open = _session.Prefs?.V3SearchOpen.Value ?? false;
            return open || _session.Width.Value < LibraryV3Metrics.SortIconOnlyWidth;
        });

        // Subscribed directly (not through the memo) because it changes the row's LAYOUT, not just the pill: an open field
        // grows, so the spacer must go — two growing siblings would split the row and half-collapse the field.
        bool searchOpen = prefs?.V3SearchOpen.Value ?? false;

        // KEYED: the child count changes with the field, so positional matching would pair the sort pill with the spacer
        // (different element types) and remount it on every open/close.
        Element search = Embed.Comp(() => new LibraryV3Search(_session)) with { Key = "v3-search" };
        Element trigger = Embed.Comp(() => new V3SortViewTrigger(iconOnly)) with { Key = "v3-sortview" };
        Element[] children = searchOpen
            ? [search, trigger]
            : [search, new BoxEl { Key = "v3-toolbar-spacer", Grow = 1f }, trigger];

        return new BoxEl
        {
            Direction = 0, Height = LibraryV3Metrics.ToolbarHeight, AlignItems = FlexAlign.Center, Gap = 4f,
            Padding = SidebarPaneMetrics.BandInset,   // the ONE content lane (see LibraryV3Header's band)
            Children = children,
        };
    }
}
