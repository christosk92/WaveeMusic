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
/// §3.2.3 — the header band, its shape resolved once per render by <see cref="LibraryV3HeaderRules.Resolve"/> (the
/// ONE Priority+ ladder: the title is the last thing to yield):
/// <c>[title = collapse toggle]</c> · <c>[search]</c> · spacer · <c>[+]</c> · <c>[…]</c>. The title itself is text
/// only, <c>Shrink = 0</c>, never ellipsized, never squeezed by the search field — it hides ENTIRELY (never shrinks)
/// while <c>Shape.SearchTakesRow</c>, and reappears the instant the field closes or the pane widens past
/// <see cref="LibraryV3HeaderRules.InlineSearchWidth"/>. The "‹" collapse chevron this row used to carry is gone —
/// the title is the collapse affordance now (docked only; the drawer has no rail to collapse into, so it renders a
/// plain, non-interactive title there) — and a "Collapse" row is back in the "…" overflow to replace it.
///
/// <para>The overflow menu is where V3 carries locked entry point 3 (the quick sidebar-layout switch): it embeds
/// <c>SidebarLayoutMenu.Rows</c> as a SUB-MENU rather than re-declaring the three design radios, so the pane menu, the
/// Classic header button and this menu can never disagree about what switching a design does. Sort/View moved OFF this
/// menu entirely (they now live on the lens row below the chips, a separate workstream) — this header no longer
/// mentions <c>V3SortViewMenu</c> at all. Labels are resolved AT OPEN TIME, never in the render body — <c>Loc.Get</c>
/// reads the culture epoch, and a static header button must not subscribe to it four times over (the docked pane and
/// the drawer each keep an expanded and a compact body mounted).</para>
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
        var destAnchor = UseRef<NodeHandle>(default);
        var destHandle = UseRef<OverlayHandle?>(null);

        // The title's flyout: the library's fixed destinations (Liked Songs · Albums · Artists · Podcasts), each a row
        // that opens its page and a pin toggle that puts it in the pinned band. This is where the destination word
        // rail went in V3.1 — one click behind the title instead of 30 DIP of chrome — and, unlike the rail, every
        // destination is pinnable in place. Same popup discipline as the overflow: built at OPEN time, light-dismiss.
        void ToggleDestinations()
        {
            if (svc is null) return;
            if (destHandle.Value is { IsOpen: true } open) { open.Close(); return; }
            destHandle.Value = svc.Open(
                () => destAnchor.Value,
                () => Embed.Comp(() => new LibraryV3DestinationsFlyout(_session, () => destHandle.Value?.Close())),
                FlyoutPlacement.BottomLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            destHandle.Value.ClosedAction = () => destHandle.Value = null;
        }

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

        // W1 — the ONE rule for the whole row's shape (LibraryV3HeaderRules.Resolve), mirrored from the search
        // host's own equality-gated read: a seam drag re-renders this component only when the SHAPE flips, not per
        // frame. Priority+: the title is the last thing to yield — it is never a parameter of the ladder's own
        // narrowing logic, only the search field and the create button react to width.
        var shape = UseComputed(() => LibraryV3HeaderRules.Resolve(
            _session.Width.Value, _session.SearchOpen.Value, _session.Prefs?.V3Search.Value is { Length: > 0 })).Value;

        // The title opens the library flyout (ToggleDestinations) in the docked pane AND the drawer — a flyout needs
        // no rail to exist. Collapse lives in the "…" overflow only. Text-only (no leading glyph: the engine's Icons
        // table has no library glyph, and the width budget at the 180 floor has no room for one anyway), Shrink = 0,
        // no Trim — it never ellipsizes, on the strength of LibraryV3HeaderRules alone.
        Element title = new BoxEl
        {
            Direction = 0, Height = 28f, Padding = new Edges4(4f, 0f, 6f, 0f), Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = ToggleDestinations, OnRealized = h => destAnchor.Value = h, Shrink = 0f,
            Children = [new TextEl(Loc.Get(Strings.Sidebar.V3.Title))
            {
                Size = 15f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
            }],
        }.Interactive(Interaction.Subtle);

        var kids = new List<Element>(6)
        {
            // Hidden — never shrunk — while the search field takes the whole row (Flow.Show, never Width = 0: a
            // zero-size flex child still collects the row's Gap, pitfalls.md "Geometry and virtualization").
            Flow.Show(() => !shape.SearchTakesRow, title),
            Embed.Comp(() => new LibraryV3Search(_session)) with { Key = "v3-search" },
            // Grow=0 whenever the search host is the row's flexible sibling — inline, or OPEN on a narrow pane (it takes
            // the row then): two Grow=1 elements would split the remaining space by ratio, which is exactly the
            // half-width open field the first V3.1 build showed. Grow=1 only around the closed magnifier, where the
            // title keeps its natural ("Your Library" is short and fixed) width and the spacer pushes the icons out.
            new BoxEl { Key = "v3-header-spacer", Grow = shape.InlineSearch || shape.SearchTakesRow ? 0f : 1f },
        };

        if (shape.ShowsCreate)
            // H2 (#85) — the same drop spec Classic's section-header "+" gets: a rootlist selection files into a new
            // top-level folder, a track set becomes a new playlist. V3 never sets SidebarPaneConfig.HeaderCreate (it
            // renders no section headers at all), so this button needed its own copy of that spec. Folds into the
            // "…" overflow (New playlist / New folder) below LibraryV3HeaderRules.CreateFoldWidth.
            kids.Add(Embed.Comp(() => new SidebarCreateButton(
                _session.CreatePlaylist, menu: CreateMenu,
                drop: _session.HeaderCreateDropSpec(), dropActive: () => _session.HeaderCreateDropActive.Value,
                box: 28f, glyph: 14f)));

        // W5 — IconButton.Create gives the overflow button its focus ring, Space/Enter activation and
        // AutomationRole for free; the hand-rolled BoxEl this used to be had none of those.
        kids.Add(ToolTip.Wrap(
            IconButton.Create(Icons.More, ToggleOverflow, parts: overflowParts, size: ControlSize.Small)
                with { Key = "v3-overflow" },
            Loc.Get(Strings.Sidebar.Layout.MenuTitle)));

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

    /// <summary>The overflow rows, built at OPEN time (§3.2.3's exact order): Sidebar layout ▸ · separator ·
    /// [New playlist · New folder — only when the header's own "+" is folded away] · Clear filters · Collapse
    /// (docked only) · separator · API Console (developer mode). Sort/View are GONE from this menu entirely — they
    /// now live on the lens row under the chips (a separate workstream), never re-declared here.</summary>
    List<MenuFlyoutItem> BuildOverflow(SidebarPreferences? prefs)
    {
        var rows = new List<MenuFlyoutItem>(8);
        var layoutRows = SidebarLayoutMenu.Rows(prefs, _session.Go);
        if (layoutRows.Count > 0)
        {
            rows.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Sidebar.Layout.MenuTitle), layoutRows, Icons.SplitView));
            rows.Add(MenuFlyoutItem.Separator);
        }

        // The header's own "+" folds away below LibraryV3HeaderRules.CreateFoldWidth — reuse the exact two verbs
        // CreateMenu() already builds so the header and this overflow can never disagree about what "New playlist"/
        // "New folder" do. `Peek`, not `Value`: built at OPEN time, nothing to subscribe.
        bool showsCreate = LibraryV3HeaderRules.Resolve(
            _session.Width.Peek(), _session.SearchOpen.Peek(), _session.Prefs?.V3Search.Peek() is { Length: > 0 }).ShowsCreate;
        if (!showsCreate && CreateMenu() is { Rows.Count: > 0 } createMenu)
            rows.AddRange(createMenu.Rows);

        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.V3.ClearFilters), Icons.Cancel,
                                    _session.AnyFilterActive, _session.ClearAllFilters));

        // The "Collapse" row is BACK — the header's own "‹" chevron is gone (the title is the collapse toggle now),
        // so this is the only remaining path to it. Docked only: a drawer has no rail to collapse into.
        if (!_session.InDrawer)
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Sidebar.V3.Collapse), Icons.ChevronLeft, true, _session.Collapse));

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
