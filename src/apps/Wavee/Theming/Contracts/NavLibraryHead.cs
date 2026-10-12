// ── Theming/Contracts/NavLibraryHead.cs — the props of `Shell.NavLibraryHead` and what the head may ask of its session (T5b) ───
//
// YOUR LIBRARY'S HEAD IS A CORE SESSION AND A THEME VIEW. The Library layout is one `PaneView` with a fixed head above the list
// (Home, the "Your Library" page dropdown, the chips, the toolbar, the breadcrumb, the retry banner and the empty band; the compact
// rail's form is Home, the search tile and the separator). The core keeps what decides: the model's session (`NavLibrary`: the drill
// stack, the shaping, the facts and every verb), the head<->list focus ring (the head's focus targets live on the session, because
// the list's Up-from-the-first-row and the pane's pill rule read them), the pill flag, the signals (document, search, filter) and
// the menus' rows (T5c moves the menus). The structure draws the head and reaches all of it through `INavLibraryHeadHost`.
//
// The props carry the host, one cached instance per Library mount (the session is built once at mount and equal across the pane's
// re-renders, so an equal record skips the template). The host is internal (it speaks `INavRowHost` and the pane's rows), so the
// record keeps it internal too: only the assembly's structures read it, as `NavRowKit.Slot` does for the row host.
//
// Every member is live (a signal read inside a render, a method called when asked), never a value captured at mount.
//
// WVTH006: a sealed record with no writable member.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellNavLibraryHead"/>.</summary>
public sealed record NavLibraryHeadProps
{
    internal NavLibraryHeadProps(INavLibraryHeadHost host) => Host = host;

    /// <summary>The Library mount's session, as the head sees it.</summary>
    internal INavLibraryHeadHost Host { get; }
}

/// <summary>Your Library's session, as the head sees it. Implemented by <c>Sidebar.LibrarySession</c>.</summary>
internal interface INavLibraryHeadHost
{
    // ── the model's session ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The model's Library session: the drill stack, the shaping, the facts and the verbs (navigate, back, filter, search,
    /// create, retry). The head reads facts and calls verbs, it never writes a store.</summary>
    NavLibrary Lib { get; }

    /// <summary>The nav model (the dropdown's page counts).</summary>
    NavModel Model { get; }

    /// <summary>Is this mount the narrow-window drawer's? A drawer is always expanded and never a rail.</summary>
    bool InDrawer { get; }

    // ── the sidebar's shared signals ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The active layout's resolved document (a peek: a render that must re-plan reads <see cref="LayoutVersion"/>).</summary>
    SidebarLayoutDoc Doc { get; }

    /// <summary>Bumped on every overlay, density or layout change (SUBSCRIBES the reader).</summary>
    IReadSignal<int> LayoutVersion { get; }

    /// <summary>Your Library's search text (session-only).</summary>
    Signal<string> Search { get; }

    /// <summary>Is the toolbar's search box open?</summary>
    Signal<bool> SearchOpen { get; }

    /// <summary>The active chip.</summary>
    Signal<SidebarLibraryFilter> Filter { get; }

    /// <summary>Is the pane presented as the compact rail right now (not the drawer, mode Compact, no drag peek)? SUBSCRIBES the
    /// reader to the mode and, while compact, to the drag peek.</summary>
    bool RailPresented { get; }

    /// <summary>The current route's name (SUBSCRIBES the reader).</summary>
    string Route { get; }

    /// <summary>The current route's name without subscribing.</summary>
    string RoutePeek { get; }

    /// <summary>The title and glyph of an app route key.</summary>
    (string Title, string Glyph) DestOf(string routeKey);

    /// <summary>The glyph of an app route key (the dropdown's pages).</summary>
    string GlyphOf(string routeKey);

    /// <summary>Show the full pane from wherever it is (the rail's search tile expands the pane with the search box open).</summary>
    void OpenPane();

    // ── the pane the head sits above ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pane (as the row host), or null before the mount factory handed it over. Its published plan, its overlay, its
    /// route menu and its header "+" drop spec are what the head reads.</summary>
    INavRowHost? PaneRef { get; }

    /// <summary>The pane's selected route key (SUBSCRIBES the reader: the pills re-render on a navigation).</summary>
    string PaneSelectedRoute { get; }

    /// <summary>Give keyboard focus to the list's item <paramref name="index"/> (the head ring's Down off the last stop).</summary>
    void FocusListItem(int index);

    /// <summary>The "+" menu factory (the pane's own, the session wires it at mount).</summary>
    Func<ContextMenuModel?> CreateMenuFn { get; }

    /// <summary>Is the header "+" the armed drop destination?</summary>
    Func<bool> HeaderDropActiveFn { get; }

    // ── the menus (T5c moves them) ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The sort menu's rows for the active chip.</summary>
    IReadOnlyList<MenuFlyoutItem> SortMenu(SidebarLibraryFilter filter);

    /// <summary>The "..." options menu's rows for the active chip.</summary>
    IReadOnlyList<MenuFlyoutItem> OptionsMenu(SidebarLibraryFilter filter);

    /// <summary>The page dropdown's rows: a radio per page, the current one checked, choosing one navigates.</summary>
    IReadOnlyList<MenuFlyoutItem> PagesMenu(IReadOnlyList<SidebarLibraryPage> pages, string? page);

    /// <summary>The overlay host the menu being opened lives on (a menu verb's confirm dialog opens on the same host).</summary>
    void BindMenuOverlay(IOverlayService overlay);

    // ── the head<->list ring and the pill ────────────────────────────────────────────────────────────────────────────

    /// <summary>A list row carries the pill now (the head's dropdown carries it only when no row does).</summary>
    Signal<bool> RowPill { get; }

    /// <summary>The input hooks the head runs under (written each render; the ring focuses through them).</summary>
    InputHooks? Hooks { get; set; }

    /// <summary>The Home row's node (a ring stop).</summary>
    NodeHandle HomeNode { get; set; }

    /// <summary>The page dropdown's node (a ring stop).</summary>
    NodeHandle DropdownNode { get; set; }

    /// <summary>The toolbar's search button (focused again after the box closes with Esc).</summary>
    NodeHandle SearchButtonNode { get; set; }

    /// <summary>Does the dropdown offer a menu (at least one page not hidden through Filters)?</summary>
    bool HasPages { get; set; }

    /// <summary>Set by the search box's Esc-close: the toolbar's search button takes focus when it remounts.</summary>
    bool FocusSearchButtonOnMount { get; set; }

    /// <summary>Focus a ring stop of the head; false when its node is not realized.</summary>
    bool FocusHead(SidebarLibraryHeadRules.HeadStop stop);
}
