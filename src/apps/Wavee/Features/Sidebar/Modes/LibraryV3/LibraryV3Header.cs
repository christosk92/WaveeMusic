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

        var kids = new List<Element>(6)
        {
            // W7 — the glyph box IS the art column (SidebarCover.S32 == SidebarRowMetrics.ArtFor(Cozy) == 32), so the
            // header's library mark and every row's cover/glyph below it share one edge; the 16-DIP glyph is centred
            // inside it exactly as a row's bare-glyph art is. Segoe MDL2 Assets (not Icons.List/Theme's Segoe Fluent
            // Icons face) is this glyph's own font, so it must be named explicitly (Icon's `family` param) — reading
            // only a codepoint against the wrong face is how an icon renders as tofu.
            new BoxEl
            {
                Width = SidebarCover.S32, Height = SidebarCover.S32, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                // The row's label sits a LeadingGap (10) past the art column (W7), not the row's own 4-DIP button
                // rhythm — so the extra 6 rides on the glyph box's own margin rather than widening the whole row's
                // Gap (which would also push the create/overflow/collapse buttons apart from each other).
                Margin = new Edges4(0f, 0f, 6f, 0f),
                // U+E71C (Segoe MDL2 "Filter"/library mark) as an ESCAPE, never a literal: the private-use character is
                // invisible in every editor and was silently dropped once already when this file was rewritten.
                Children = [Icon("", 16f, Tok.TextSecondary, "Segoe MDL2 Assets")],
            },
            new TextEl(Loc.Get(Strings.Sidebar.V3.Title))
            {
                // Ui.BodyStrong is 14/20/600; the header title sits one rung up (15) and no alias covers that exact
                // size, so it stays an explicit override rather than a one-call-site alias (W5).
                Size = 15f, Weight = 600, Color = Tok.TextPrimary,
                Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
            Embed.Comp(() => new SidebarCreateButton(
                _session.CreatePlaylist, menu: CreateMenu, box: 28f, glyph: 14f)),
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
        // The sort pill collapses to icon-only when the search host is open (it owns the row) or when the pane is
        // simply too narrow for a label. A MEMO, not a raw width read: a seam drag writes the width every frame, and
        // the memo's equality cut-off means this component re-renders only when the BOOLEAN flips.
        //
        // W1 — reads _session.SearchOpen (a session Signal), never prefs.V3SearchOpen: the open flag left
        // SidebarPreferences entirely (it is no longer a persisted setting), so the toolbar's own idea of "is search
        // open" lives on the same object LibraryV3Search itself writes to.
        // ONE rule for the row's shape (LibraryV3SearchRules.Resolve), read by the host and by this toolbar alike, so
        // the pill can never be icon-only while the field is a button, nor labelled while the field owns the row.
        var layout = UseComputed(() => LibraryV3SearchRules.Resolve(
            _session.Width.Value, _session.SearchOpen.Value, _session.Prefs?.V3Search.Value is { Length: > 0 }));
        var iconOnly = UseComputed(() => layout.Value.SortIconOnly);
        bool inline = layout.Value.Inline;

        // W1 — children are ALWAYS [search, spacer, trigger], never keyed on the open flag: in the NARROW shape the
        // search HOST's own explicit Width does the morph (LibraryV3SearchRules.OpenWidth) and the spacer (Grow=1)
        // simply shrinks to (near) 0 while it is open; in the INLINE shape the host itself grows and the spacer
        // yields (Grow=0), or the two would split the row. Swapping the child SET would remount the spacer and
        // reintroduce the old file's cross-fade-instead-of-morph bug at the toolbar level.
        Element search = Embed.Comp(() => new LibraryV3Search(_session)) with { Key = "v3-search" };
        Element spacer = new BoxEl { Key = "v3-toolbar-spacer", Grow = inline ? 0f : 1f };
        Element trigger = Embed.Comp(() => new V3SortViewTrigger(iconOnly)) with { Key = "v3-sortview" };

        return new BoxEl
        {
            Direction = 0, Height = LibraryV3Metrics.ToolbarHeight, AlignItems = FlexAlign.Center, Gap = 4f,
            // W7 — LeadBandInset: the search host's closed 28-DIP box must sit on the rows' art column (27), exactly
            // like the header's glyph and the nav band's rows, so the whole chrome stack shares one left edge.
            Padding = SidebarPaneMetrics.LeadBandInset,
            Children = [search, spacer, trigger],
        };
    }
}
