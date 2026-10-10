// ── Shell/Sidebar.Zune.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the Zune navigation style's pure rules: the top pivots and sub-pivots, the route → pivot mapping, what the band's
// second row carries for a route (and the seeds that fill it before a page publishes), the pin tiles and the viewport
// gate that decides whether the pins show beside the pivots
//
// Role: CORE
// Plan: docs/plans/wavee/sidebar-rework-implementation.md §P10 (NAV 3)
//
// Engine-free apart from Spacing (constants only) and the loc key constants, and allocation-light (PinTiles reuses the
// caller's list), so Wavee.Tests pins every decision here. The Zune band (Sidebar.UI.Zune.cs) only draws what these rules
// decide.
//
// The band is BandHeight tall under Zune: 84 with row 2, 52 when the route has nothing for it (ZuneSubRow.None). Its height
// follows the PRESENTED row kind (Shell.Ui.PresentedSubRow), and its change on a nav-style switch or a navigation
// is a Size Relayout on the content card's own tween (Shell.ZuneBandAnim) while the page-column card FLIPs down and
// relayouts its height in the same tween (Shell.PageColumnCardAnim): the column lays out once, the card moves once and its
// bottom edge stays on the dock. The band is the first child of the page column, beside the inline right panel. Every
// vertical DIP of the band is a named rhythm term (PivotTop, PivotLine, PivotToSub,
// SubRowHeight, SubToCard), so no literal height can drift from the two Zune type roles.
//
// ROW 2 IS ROUTE-DECIDED AND NEVER AN EMPTY STRIP. SubRowOf(route) picks what the second row carries from the ROUTE alone (no
// data input): Library's sub-pivots, the page's views (seeded from loc keys here, replaced in place when the page publishes
// through Shell.PageViews), the Browse categories or an entity's title row (seeded from the route's display name, replaced by
// Shell.PageBands once the page publishes). A route with nothing of its own to say gets no row 2 at all (None): row 2 never
// just repeats the page title. Seeds are loc KEYS, so these rules stay pure and the UI resolves them.

using System;
using System.Collections.Generic;
using FluentGpu.Dsl;

namespace Wavee;

/// <summary>What the Zune band's second row carries for a route. ROUTE-ONLY: no data decides it, so the row never pops in or
/// out as a page loads. When it has content it is <see cref="ZuneNavRules.SubRowHeight"/> tall.</summary>
public enum ZuneSubRow : byte
{
    /// <summary>Library's sub-pivots (liked · albums · artists · podcasts · audiobooks).</summary>
    Library,
    /// <summary>The page's own views: seeded from <see cref="ZuneNavRules.ViewSeedKeys"/>, replaced in place by the page's
    /// <c>Shell.PageViews</c> publication (Home, Recents, Settings, Search, people lists, discography).</summary>
    Views,
    /// <summary>The Browse categories (music · podcasts · audiobooks · live events), which navigate. The Browse root and its
    /// four top category pages.</summary>
    Categories,
    /// <summary>An entity's title row: the title from the route's display name, then same-size skeleton pivots and actions
    /// until the page publishes its band (<c>Shell.PageBands</c>). Artist, profile, episode and show.</summary>
    Context,
    /// <summary>Row 2 is collapsed: the route has nothing to say but its own title, which its head already shows. The band is
    /// the pivot row alone (<see cref="ZuneNavRules.PivotRowHeight"/> + <see cref="ZuneNavRules.SubToCard"/>). Album, prerelease,
    /// playlist and every other route without row-2 content.</summary>
    None,
}

/// <summary>The Zune style's pivot rules: which pivot a route sits under, where a pivot lands, which pins show.</summary>
public static class ZuneNavRules
{
    /// <summary>Pins shown beside the pivots (folders are skipped: they have no route).</summary>
    public const int MaxPins = 6;

    /// <summary>The pivot words' sizes are the <c>Design.Type.ZunePivot</c> / <c>ZuneSubPivot</c> roles. The band's left inset is
    /// DERIVED (<see cref="Shell.FrameRules.ZuneBandInset"/>: the page gutter, card-relative), never a literal here.</summary>
    public const float PinTile = 32f, PinGap = 8f, PivotGap = 24f, SubPivotGap = 20f;

    /// <summary>A pin's now-playing dot (size, gap under its tile): the dot is ALWAYS laid out (a 4-DIP box that is transparent
    /// when idle), so a pin starting to play changes ink only. 32 (tile) + 2 (gap) + 4 (dot) = 38 &lt;= <see cref="PivotRowHeight"/>.</summary>
    public const float PinDot = 4f, PinDotGap = 2f;

    // THE BAND'S RHYTHM. Row 1 is PivotTop + PivotLine + PivotToSub = 44; row 2 is SubRowHeight = 32; SubToCard = 8 closes
    // the band above the card. 44 + 32 + 8 = 84, or 44 + 8 = 52 with row 2 collapsed.

    /// <summary>Air above the pivot line (<c>Spacing.XS</c>, 4).</summary>
    public const float PivotTop = Spacing.XS;

    /// <summary><c>Design.Type.ZunePivot</c>'s line height (28 / 36). A test pins the equality, so this file stays engine-free.</summary>
    public const float PivotLine = 36f;

    /// <summary>Pivot line to row 2 (<c>Spacing.XS</c>, 4).</summary>
    public const float PivotToSub = Spacing.XS;

    /// <summary>Row 2's height: <c>Controls.ButtonHeight</c> (32), so a 32-DIP action pill in an entity's title row never grows it.</summary>
    public const float SubRowHeight = 32f;

    /// <summary>Row 2 to the content card (<c>Spacing.S</c>, 8).</summary>
    public const float SubToCard = Spacing.S;

    /// <summary>Row 1: the pivot line with its air above and below (44). The pin column (32 + 2 + 4) fits inside it.</summary>
    public const float PivotRowHeight = PivotTop + PivotLine + PivotToSub;

    /// <summary>The narrowest band (the page column, which stops at the inline right panel) at which the pins show beside the pivots.</summary>
    public const float PinsMinBandW = 720f;

    public const string LibraryPivot = "library";

    /// <summary>The top pivots, in order: home · browse · library · recents.</summary>
    public static readonly string[] Top = ["home", "browse", LibraryPivot, "recents"];

    /// <summary>Library's sub-pivots, in order: liked · albums · artists · podcasts · audiobooks.</summary>
    public static readonly string[] LibraryPages = ["liked", "albums", "artists", "podcasts", "audiobooks"];

    /// <summary>The pivot a route sits under, or null when no pivot does (the route then shows no pivot selected).</summary>
    public static string? TopOf(string routeName)
    {
        if (routeName == "home" || routeName.StartsWith("home-section:", StringComparison.Ordinal)) return "home";
        if (routeName is "browse" or "search" || routeName.StartsWith("browse:", StringComparison.Ordinal)
            || routeName.StartsWith("browse-section:", StringComparison.Ordinal)) return "browse";
        if (routeName == "local") return LibraryPivot;
        for (int i = 0; i < LibraryPages.Length; i++) if (routeName == LibraryPages[i]) return LibraryPivot;
        if (routeName == "recents") return "recents";
        return null;
    }

    /// <summary>The route a pivot lands on: Library lands on Liked Songs, every other pivot is its own route.</summary>
    public static string LandingOf(string pivot) => pivot == LibraryPivot ? LibraryPages[0] : pivot;

    /// <summary>A PIVOT DESTINATION: a route the Zune pivots land on, whose page head hoists into the band while Zune is
    /// presented. Home, Browse's ROOT, Recents and every Library page; Local, the Home and Browse sections, nested Browse
    /// routes and Search are not (they keep their own head).</summary>
    public static bool IsPivotDestination(string routeName)
    {
        if (routeName is "home" or "browse" or "recents") return true;
        for (int i = 0; i < LibraryPages.Length; i++) if (routeName == LibraryPages[i]) return true;
        return false;
    }

    /// <summary>What the band's second row carries for a route. ROUTE-ONLY (no data parameter), so the row is decided on the
    /// first frame of a navigation: the library pages carry Library's sub-pivots; Home, Recents, Settings, Search, the people
    /// lists and the discography carry their views; the Browse root and its four top categories carry the category words; an
    /// entity route carries its title; everything else has no row 2 (<see cref="ZuneSubRow.None"/>).</summary>
    public static ZuneSubRow SubRowOf(in Shell.Route route)
    {
        if (TopOf(Shell.NameOf(route)) == LibraryPivot) return ZuneSubRow.Library;
        switch (route.Kind)
        {
            case Shell.RouteKind.Home:
            case Shell.RouteKind.Recents:
            case Shell.RouteKind.Settings:
            case Shell.RouteKind.Search:
            case Shell.RouteKind.ProfileList:
            case Shell.RouteKind.Discography:
                return ZuneSubRow.Views;
            case Shell.RouteKind.Browse:
                return ZuneSubRow.Categories;
            case Shell.RouteKind.BrowseCategory:
                return IsTopCategory(route.Subject.Text) ? ZuneSubRow.Categories : ZuneSubRow.None;
            case Shell.RouteKind.Artist:
            case Shell.RouteKind.User:
            case Shell.RouteKind.Episode:
            case Shell.RouteKind.Show:
                return ZuneSubRow.Context;
            default:
                return ZuneSubRow.None;
        }
    }

    /// <summary>Do two routes sit on the same PAGE (same tab, same keep-alive slot)? A facet switch inside one page (Home's
    /// views, a discography's facet) is the same page: nothing swaps, so nothing waits for a swap to land.</summary>
    public static bool SamePage(in Shell.Route a, in Shell.Route b)
        => a.Tab == b.Tab && string.Equals(Shell.SlotKey(in a), Shell.SlotKey(in b), StringComparison.Ordinal);

    /// <summary>Does row 2 present <paramref name="current"/> NOW? Row 2 and the masthead's crumb head change WITH the page swap,
    /// not ahead of it: while the old page is still leaving (<paramref name="shown"/> has not reached <paramref name="current"/>)
    /// it keeps drawing the route it is on. A same-page facet switch presents at once. A None <paramref name="shown"/> is boot,
    /// which counts as landed. A None <paramref name="current"/> presents nothing.</summary>
    public static bool PresentsNow(in Shell.Route shown, in Shell.Route current, in Shell.Route presented)
        => !current.IsNone && (SamePage(in presented, in current) || shown.IsNone || shown == current);

    /// <summary>Has the page swap landed for the masthead's crumb head? A None <paramref name="shown"/> is boot (landed), and a
    /// same-page switch has nothing to wait for.</summary>
    public static bool HeadLanded(in Shell.Route shown, in Shell.Route current)
        => shown.IsNone || shown == current || SamePage(in shown, in current);

    /// <summary>Is <paramref name="uri"/> one of the four top Browse categories (compared as text, never re-parsed from a name)?</summary>
    public static bool IsTopCategory(string uri)
    {
        var top = BrowseTaxonomy.TopUris;
        for (int i = 0; i < top.Count; i++) if (string.Equals(top[i], uri, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The band's whole height for a nav style and the PRESENTED row-2 kind: both rows under Zune (44 + 32 + 8 = 84), the
    /// pivot row alone when row 2 is collapsed (<see cref="ZuneSubRow.None"/>, 44 + 8 = 52), nothing otherwise. The row kind is
    /// <c>Shell.Ui.PresentedSubRow</c>, which lands one commit after a navigation, so the card moves on its own tween and never
    /// in the commit in which the incoming page mounts.</summary>
    public static float BandHeight(ShellNavStyle style, ZuneSubRow row)
        => style == ShellNavStyle.Zune ? PivotRowHeight + (row == ZuneSubRow.None ? 0f : SubRowHeight) + SubToCard : 0f;

    // ══ SEEDS ═════════════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // Before a page publishes its views (or its entity band) the band shows the ROUTE'S OWN seed, so row 2 is filled from
    // the first frame and the publication replaces the words in place. Seeds are loc keys: the UI resolves them.

    /// <summary>Home's seed words, All first.</summary>
    public static readonly string[] HomeViewKeys =
        [Strings.Home.FacetAll, Strings.Home.Facet.Music, Strings.Home.Facet.Podcasts, Strings.Home.Facet.Audiobooks];

    /// <summary>The chip id behind each of <see cref="HomeViewKeys"/> (All has the empty id).</summary>
    public static readonly string[] HomeViewIds = ["", "music-chip", "podcasts-chip", "audiobooks-chip"];

    /// <summary>Recents' four views in <c>RecentsView.PivotOrder</c>'s order. The page's bar and the band share THIS list.</summary>
    public static readonly string[] RecentsViewKeys =
        [Strings.Detail.Filter.All, Strings.Recents.Chip.Music, Strings.Recents.Chip.Podcasts, Strings.Recents.Pivot.Artists];

    /// <summary>Settings' seven tabs in <c>Settings.Tab</c> order. The page's bar and the band share THIS list.</summary>
    public static readonly string[] SettingsTabKeys =
    [
        Strings.Settings.Tabs.General, Strings.Settings.Tabs.Appearance, Strings.Settings.Tabs.Playback,
        Strings.Settings.Notify.Title, Strings.Settings.Tabs.Storage, Strings.Settings.Tabs.Privacy, Strings.Settings.Tabs.About,
    ];

    /// <summary>A profile's two lists in <c>ProfileListFacets.Order</c>'s order.</summary>
    public static readonly string[] ProfileListViewKeys = [Strings.Person.Pivot.Following, Strings.Person.Pivot.Followers];

    /// <summary>An artist's discography facets in <c>DiscoFacet</c> order. The page's bar and the band share THIS list.</summary>
    public static readonly string[] DiscographyViewKeys = [Strings.Artist.Albums, Strings.Artist.SinglesEps, Strings.Artist.Compilations];

    /// <summary>The four top Browse categories' words, in <c>BrowseTaxonomy.TopUris</c> order.</summary>
    public static readonly string[] CategoryLabelKeys =
    [
        Strings.Browse.TopCategory.Music, Strings.Browse.TopCategory.Podcasts, Strings.Browse.TopCategory.Audiobooks,
        Strings.Browse.TopCategory.LiveEvents,
    ];

    /// <summary>The views' loc keys, in display order, for a kind that has seeded views. Null for Search (its facets depend on
    /// the results, so it seeds skeleton words) and for every kind without views.</summary>
    public static string[]? ViewSeedKeys(Shell.RouteKind kind) => kind switch
    {
        Shell.RouteKind.Home => HomeViewKeys,
        Shell.RouteKind.Recents => RecentsViewKeys,
        Shell.RouteKind.Settings => SettingsTabKeys,
        Shell.RouteKind.ProfileList => ProfileListViewKeys,
        Shell.RouteKind.Discography => DiscographyViewKeys,
        _ => null,
    };

    /// <summary>The seed's selected word for a route: Home's facet (by chip id), Recents' pivot, Settings' remembered tab,
    /// the profile list's or discography's facet in the route; 0 otherwise.</summary>
    public static int SeedSelected(in Shell.Route route)
    {
        switch (route.Kind)
        {
            case Shell.RouteKind.Home:
            {
                string arg = Entities.Strings.Resolve(route.Arg).Replace("-following-chip", "-chip", StringComparison.Ordinal);
                for (int i = 1; i < HomeViewIds.Length; i++)
                    if (string.Equals(HomeViewIds[i], arg, StringComparison.Ordinal)) return i;
                return 0;
            }
            case Shell.RouteKind.Recents:
                return RecentsView.PivotIndexOf(Entities.Strings.Resolve(route.Arg));
            case Shell.RouteKind.Settings:
                return Settings.TabIndex;
            case Shell.RouteKind.ProfileList:
                return ProfileListRoute.TryParse(route, out var pf, out _) ? ProfileListFacets.IndexOf(pf) : 0;
            case Shell.RouteKind.Discography:
                return DiscoRoute.TryParse(route, out var df, out _) ? (int)df : 0;
            default:
                return 0;
        }
    }

    /// <summary>The skeleton word counts an entity's title row shows before its page publishes the band: the pivots and the
    /// action pills. Artist (3, 2), profile (2, 1), episode (4, 0), show (0, 0).</summary>
    public static (int Pivots, int Actions) ContextSeed(Shell.RouteKind kind) => kind switch
    {
        Shell.RouteKind.Artist => (3, 2),
        Shell.RouteKind.User => (2, 1),
        Shell.RouteKind.Episode => (4, 0),
        _ => (0, 0),
    };

    // ══ PINS ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A pin's now-playing dot is lit: the pin has a uri, playback has an active context, and the pin relates to it.</summary>
    public static bool PinShowsPlaying(string uri, bool activeContext, bool relatesTo) => uri.Length > 0 && activeContext && relatesTo;

    /// <summary>The pins show when the setting is on and the band is wide enough for them beside the pivots. <paramref name="bandW"/>
    /// is the PAGE COLUMN's width (<c>Shell.PageColumnWidth</c>), not the viewport's: with the right panel inline the band is
    /// narrower than the window.</summary>
    public static bool ShowsPins(bool setting, float bandW) => setting && float.IsFinite(bandW) && bandW >= PinsMinBandW;

    /// <summary>Whether the pin group is MOUNTED in the band's row while the page column's width is still easing (a rail toggle).
    /// <see cref="ShowsPins"/> reads the FINAL column width, the band tweens to it, so the group must not change the row's layout
    /// at the target's flip: it mounts only once the band has settled (<paramref name="settled"/>), and a group that is going
    /// away stays mounted, faded out by opacity, until the band has settled too. The group's VISIBILITY is the target itself.</summary>
    public static bool PinsMounted(bool target, bool mounted, bool settled)
        => target ? mounted || settled : mounted && !settled;

    /// <summary>Whether the mounted pin group takes width in the pivot row. Only a group that is staying (the target) does: a group
    /// that is fading out is laid out as a 0-wide end-aligned layer, so while the band narrows or widens on a rail toggle the
    /// pivot strip is never solved narrower than its final width (a group still in flow would crush the trailing pivots for the
    /// whole fade and they would pop back when it unmounts).</summary>
    public static bool PinsTakeRowWidth(bool target, bool mounted) => target && mounted;

    /// <summary>The pin tiles: the first <see cref="MaxPins"/> pins that have a route, in order. A folder has no route and is
    /// skipped. <paramref name="into"/> is cleared and refilled, so the caller's list is reused between renders.</summary>
    public static void PinTiles(IReadOnlyList<SidebarPin> pins, List<SidebarPin> into)
    {
        into.Clear();
        for (int i = 0; i < pins.Count && into.Count < MaxPins; i++)
            if (pins[i].RouteKey.Length > 0) into.Add(pins[i]);
    }
}
