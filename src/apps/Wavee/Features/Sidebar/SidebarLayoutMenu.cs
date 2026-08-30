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

// §3.1.5 / §C6.4 — the QUICK SIDEBAR-LAYOUT MENU. Switching designs must never require a trip to Settings, so the pane
// itself carries the switch: a hover/focus-revealed button on a section header, an always-visible button for the modes
// that have a header overflow, and the pane's own background context menu. All three open the SAME row list.
//
// Two rules this file exists to keep:
//
//  1. LABELS RESOLVE AT OPEN TIME, never at render time. Loc.Get reads Localization.CultureEpoch, so resolving the five
//     row labels in a render would subscribe an otherwise-static button to the culture epoch and re-render it on every
//     flush that touches it — ×4, because the sidebar keeps the expanded AND compact bodies mounted and the shell mounts a
//     second sidebar for the narrow drawer. Deferring also picks up a culture change that happened while it was closed.
//     (The same rationale the landed SidebarCreateButton carries.)
//  2. The design switch goes through SidebarPreferences.SwitchDesign — never a raw `Design.Value = …` write. SwitchDesign
//     snapshots the outgoing design's pane + view state and reseeds the incoming design's before flipping; a bare signal
//     write would silently drop the "per-mode remembered state" contract (locked decision 3).

static class SidebarLayoutMenu
{
    /// <summary>The customizer's route key (§C4.1). ContentHost registers the page in Wave 4; navigating to an
    /// unregistered route is already a no-op-safe path, so the row can ship now.</summary>
    public const string CustomizeRoute = "sidebar-customize";

    /// <summary>One-shot route argument used by shell top-bar chrome to focus the global card without changing design.</summary>
    public const string TopBarFocusArg = "topbar";

    // ── entry points ──────────────────────────────────────────────────────────────────────────────────────────────────

    // DEFECT 15 — `HeaderButton` is GONE. It was the 24-DIP hover/focus-revealed variant for a section header, and it
    // had ZERO invocations anywhere in src/apps, including inside this file: the unification gave every mode the
    // always-visible `Button` below (hung off the pane's FIRST section header by `SidebarPaneSlot.Header`), and the
    // reveal-on-hover form never got a call site. The component behind it (`SidebarLayoutMenuButton`) still takes the
    // `revealed` signal — that parameter is what `Button` passes null for, and it is the shape a future revealed
    // caller would use, so the COMPONENT keeps it while the unused entry point goes.

    /// <summary>The always-visible button (Curated's header; V3 embeds <see cref="Rows"/> in its overflow menu instead).</summary>
    public static Element Button(SidebarPreferences? prefs, Action<string, string?> go, float box = 28f)
        => Embed.Comp(() => new SidebarLayoutMenuButton(prefs, go, null, box, box <= 24f ? 14f : 16f));

    // ── the rows ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The menu rows — also embeddable as a <c>MenuFlyoutItem.SubMenu</c> inside another mode's overflow menu.
    /// Call this at OPEN time only (see the class remarks). Returns an empty list when the preference service is absent,
    /// so the caller opens nothing rather than a menu of dead rows.</summary>
    public static IReadOnlyList<MenuFlyoutItem> Rows(SidebarPreferences? prefs, Action<string, string?> go)
    {
        if (prefs is null) return Array.Empty<MenuFlyoutItem>();
        var design = prefs.Design.Peek();   // Peek: this runs at open time, outside any render — never subscribe here

        var rows = new List<MenuFlyoutItem>(7)
        {
            Radio(Strings.Sidebar.Layout.Classic, design == SidebarDesign.Classic, prefs, SidebarDesign.Classic),
            Radio(Strings.Sidebar.Layout.LibraryV3, design == SidebarDesign.LibraryV3, prefs, SidebarDesign.LibraryV3),
            Radio(Strings.Sidebar.Layout.Curated, design == SidebarDesign.Curated, prefs, SidebarDesign.Curated),
            MenuFlyoutItem.Separator,
            // The customizer edits the CURATED document, so it always switches first (§C4.2's one live apply, no restart).
            new MenuFlyoutItem(Loc.Get(Strings.Sidebar.Layout.Customize), ActionIcons.Resolve(ActionIcons.Rename), true,
                () =>
                {
                    prefs.SwitchDesign(SidebarDesign.Curated);   // no-op when already Curated
                    go(CustomizeRoute, null);
                }),
            MenuFlyoutItem.Separator,
            // Hands the pane's width back to the responsive tier ladder. Dead unless a committed drag pinned it.
            new MenuFlyoutItem(Loc.Get(Strings.Sidebar.Menu.ResetWidth), default, prefs.WidthUserSet, prefs.ResetWidth),
        };
        return rows;
    }

    /// <summary>The pane-level context-menu model (right-click the pane background). Row-level menus still win:
    /// <c>ContextMenu.Attach</c> dispatches to the nearest self-or-ancestor handler, so right-clicking a playlist row
    /// opens that row's menu and only empty pane chrome reaches this one.</summary>
    public static ContextMenuModel? Model(SidebarPreferences? prefs, Action<string, string?> go)
    {
        var rows = Rows(prefs, go);
        if (rows.Count == 0) return null;
        return new ContextMenuModel(rows, new ContextMenuHeader(null, Loc.Get(Strings.Sidebar.Layout.MenuTitle), null));
    }

    static MenuFlyoutItem Radio(string labelKey, bool active, SidebarPreferences prefs, SidebarDesign design)
        => MenuFlyoutItem.RadioItem(Loc.Get(labelKey), active, () => prefs.SwitchDesign(design));
}

/// <summary>The icon button behind <see cref="SidebarLayoutMenu.Button"/>.
/// A Component because it needs the overlay service, an anchor node and the open handle. Every ctor arg is mount-constant
/// or a signal/stable delegate, per the component-props-freeze contract.</summary>
sealed class SidebarLayoutMenuButton : Component
{
    readonly SidebarPreferences? _prefs;
    readonly Action<string, string?> _go;
    readonly Signal<bool>? _revealed;   // null ⇒ always visible
    readonly float _box, _glyph;

    public SidebarLayoutMenuButton(SidebarPreferences? prefs, Action<string, string?> go, Signal<bool>? revealed,
                                   float box, float glyph)
    {
        _prefs = prefs; _go = go; _revealed = revealed; _box = box; _glyph = glyph;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var focused = UseSignal(false);
        var svc = UseContext(Overlay.Service);

        void Toggle()
        {
            if (svc is null || _prefs is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var items = SidebarLayoutMenu.Rows(_prefs, _go);   // built HERE, at open time (culture-epoch note)
            if (items.Count == 0) return;
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        var reveal = _revealed;
        Prop<float> opacity = 1f;
        if (reveal is not null) opacity = Prop.Of(() => reveal.Value || focused.Value ? 1f : 0f);

        var button = new BoxEl
        {
            Width = _box, Height = _box, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            // Visible on hover (the header drives `revealed`) AND whenever it holds keyboard focus.
            Opacity = opacity,
            OnFocusChanged = f => focused.SetIfChanged(f),
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [Icon(Icons.SplitView, _glyph, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);

        return ToolTip.Wrap(button, Loc.Get(Strings.Sidebar.Layout.Tooltip));
    }
}
