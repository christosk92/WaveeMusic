// ── Theming/Contracts/StyleKeys.LibraryControls.cs — the library page's choice controls (contract 1.85, winui menus/podcasts) ─────────────
//
// The library page's head and the artists' reader carry five CHOICE controls: the navigator's sort, Podcasts' two modes (Followed shows / Your
// Episodes), the view toggle with its view and size menu, the reader's scope (in your library / all releases · N) and the reader's own sort. A
// structure that draws the library page draws them in its own voice (Classic: Zune's word rails and the sort button). A structure that borrows that
// page (WinUI, through the stand-in) registers this style so the borrowed page shows ITS controls instead (WinUI: a DropDownButton over a
// MenuFlyout of RadioMenuFlyoutItems, SelectorBar pivots): a theme's features stay in that theme. Optional: a page that finds none draws its own.
//
// The core keeps every signal (they are the persisted library prefs); a template reads and writes them and builds nothing else.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class StyleKeys
{
    /// <summary>The library page's choice controls (<see cref="LibraryControlsStyle"/>). Optional: a library page draws its own without one.</summary>
    public static readonly StyleKey<LibraryControlsStyle> LibraryControls = new("Library.Controls");
}

/// <summary>The library page's choice controls, as a theme draws them. Every member is a pure builder over its args.</summary>
/// <param name="Sort">The navigator's sort (the kind's words, <see cref="LibraryWordRail.WordsFor"/>, and the direction).</param>
/// <param name="Modes">Podcasts' two modes: 0 Followed shows, 1 Your Episodes.</param>
/// <param name="View">The view toggle (list / grid) and its menu (the four views, the grid's size).</param>
/// <param name="Scope">The artists' reader's scope: 0 in your library, 1 all releases (with its total).</param>
/// <param name="ReaderSort">The artists' reader's own sort: 0 newest, 1 oldest, 2 alphabetical.</param>
/// <param name="Kinds">The library's kinds where the page has no nav pane to switch them from (a Compact head); null: the page's own.</param>
/// <param name="Head">The page's HEAD across its whole width, above the navigator and the pane (W-Library: "Library" over a SelectorBar of the kinds). A page
/// that finds one draws it over its columns and leaves its own title row and kind chips out of the navigator's toolbar (the toolbar keeps the count); null: the
/// page's own head. Appended (contract 1.88, winui show pages).</param>
/// <param name="Nav">The navigator column's leads for the tier the page is drawn in (<see cref="LibraryNavMetrics"/>): a structure whose page column stands
/// on another lead than the borrowed page's own numbers (WinUI: every left edge on the page inset) hands them here; null: the page's own. Appended
/// (contract 1.109, winui library).</param>
public sealed record LibraryControlsStyle(
    Func<LibrarySortArgs, Element> Sort,
    Func<LibraryModesArgs, Element> Modes,
    Func<LibraryViewArgs, Element> View,
    Func<LibraryScopeArgs, Element> Scope,
    Func<LibraryReaderSortArgs, Element> ReaderSort,
    Func<LibraryKindsArgs, Element>? Kinds = null,
    Func<LibraryHeadArgs, Element>? Head = null,
    Func<TierFacts, LibraryNavMetrics>? Nav = null);

/// <summary>The navigator column's leads (<see cref="LibraryControlsStyle.Nav"/>, contract 1.109). The page's column G is the head's lead.</summary>
/// <param name="LeadAir">The air before the navigator column (G less <paramref name="ItemLead"/>): an eased spacer, so a tier crossing glides it with the head's lead.</param>
/// <param name="ItemLead">How far the navigator's head (the sort, the filter, the count, the letter plates) and a grid's first cards stand in from the column's own edge.</param>
/// <param name="RowArtLead">Where a list row's art stands in from its plate's left edge (the row plate hangs left of G by this much).</param>
/// <param name="PaneTrail">The panes' right inset: their last ink and control plates end this far in from the card's right edge, on the head's own trail.</param>
public sealed record LibraryNavMetrics(Element LeadAir, float ItemLead, float RowArtLead, float PaneTrail)
{
    /// <summary>The bound list row's chrome pads its content this far in from the plate (the engine's WinUI ListViewItem: Padding 16,0,12,0), so a row's art
    /// stands <see cref="RowArtLead"/> in when its content sits <c>RowArtLead - ChromeContentPad</c> from the chrome's content edge.</summary>
    public const float ChromeContentPad = 16f;
}

/// <summary>The library page's head (<see cref="LibraryControlsStyle.Head"/>, contract 1.88).</summary>
/// <param name="Kinds">The kinds in order, the page on screen <see cref="LibraryKindChip.Selected"/>.</param>
/// <param name="Title">The page's own title ("Podcasts"), for a head that names it.</param>
public sealed record LibraryHeadArgs(IReadOnlyList<LibraryKindChip> Kinds, string Title);

/// <summary>The navigator's sort.</summary>
/// <param name="Kind">The library kind (its words).</param>
/// <param name="Sort">The persisted sort code (<see cref="LibraryNavSort"/>; clamp it with <see cref="LibraryWordRail.Clamp"/>).</param>
/// <param name="Desc">The persisted direction.</param>
/// <param name="Height">The control's height at this tier (the Compact target, else the stock control height).</param>
public sealed record LibrarySortArgs(EntityKind Kind, Signal<int> Sort, Signal<bool> Desc, float Height);

/// <summary>Podcasts' modes.</summary>
/// <param name="Mode">The persisted mode: 0 Followed shows, 1 Your Episodes.</param>
/// <param name="Height">The control's height at this tier.</param>
public sealed record LibraryModesArgs(Signal<int> Mode, float Height);

/// <summary>The view toggle.</summary>
/// <param name="View">The persisted view code: 0 compact list, 1 list, 2 compact grid, 3 grid.</param>
/// <param name="Size">The persisted grid size: 0 small, 1 medium, 2 large.</param>
/// <param name="Edge">A toggle cell's edge at this tier.</param>
public sealed record LibraryViewArgs(Signal<int> View, Signal<int> Size, float Edge);

/// <summary>The artists' reader's scope.</summary>
/// <param name="Scope">0 in your library, 1 all releases.</param>
/// <param name="Total">The artist's release total (0 until the facets answer).</param>
/// <param name="Compact">True while the reader is narrow (a short word fits better); null: never.</param>
public sealed record LibraryScopeArgs(Signal<int> Scope, Prop<int> Total, Func<bool>? Compact);

/// <summary>The artists' reader's own sort.</summary>
/// <param name="Sort">0 newest, 1 oldest, 2 alphabetical.</param>
public sealed record LibraryReaderSortArgs(Signal<int> Sort);

/// <summary>The library's kinds (Playlists, Albums, Artists, Podcasts, Audiobooks).</summary>
/// <param name="Kinds">The kinds in order, the page on screen <see cref="LibraryKindChip.Selected"/>; a kind's <see cref="LibraryKindChip.Open"/> goes there.</param>
/// <param name="Height">The control's height at this tier.</param>
public sealed record LibraryKindsArgs(IReadOnlyList<LibraryKindChip> Kinds, float Height);
