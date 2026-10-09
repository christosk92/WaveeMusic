// ── Shell/Sidebar.Zune.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the Zune navigation style's pure rules: the top pivots and sub-pivots, the route → pivot mapping, the pin tiles and
// the viewport gate that decides whether the pins show beside the pivots
//
// Role: CORE
// Plan: docs/plans/wavee/sidebar-rework-implementation.md §P10 (NAV 3)
//
// Engine-free and allocation-light (PinTiles reuses the caller's list), so Wavee.Tests pins every decision here. The
// Zune band (Shell.Zune*.cs, a later package) only draws what these rules decide.

using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>What the Zune band's second row carries for a route. ROUTE-ONLY: no data decides it, so the row never pops
/// in or out as a page loads.</summary>
public enum ZuneSubRow : byte
{
    /// <summary>The row holds nothing for this route (it is still laid out: see <see cref="ZuneNavRules.BandHeight"/>).</summary>
    None,
    /// <summary>Library's sub-pivots.</summary>
    Library,
    /// <summary>The page's own views, published through <c>Shell.PageViews</c> (Home, Recents).</summary>
    PageViews,
}

/// <summary>The Zune style's pivot rules: which pivot a route sits under, where a pivot lands, which pins show.</summary>
public static class ZuneNavRules
{
    /// <summary>Pins shown beside the pivots (folders are skipped: they have no route).</summary>
    public const int MaxPins = 6;

    public const float PivotSize = 28f, SubPivotSize = 14f, PinTile = 32f, PinGap = 8f, PivotGap = 24f, SubPivotGap = 20f;

    /// <summary>The page frame's x (Shell's MastheadFrameX): the band's content starts where the page content does.</summary>
    public const float InsetX = 36f;

    public const float PivotRowHeight = 52f, SubRowHeight = 32f;

    /// <summary>The narrowest viewport at which the pins show beside the pivots.</summary>
    public const float PinsMinViewportW = 720f;

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

    /// <summary>What the band's second row carries for a route: Library's sub-pivots under Library, the page's own views
    /// for Home and Recents, else nothing. ROUTE-ONLY (no data parameter), so the row is decided on the first frame.</summary>
    public static ZuneSubRow SubRowOf(string routeName)
        => TopOf(routeName) == LibraryPivot ? ZuneSubRow.Library
         : routeName is "home" or "recents" ? ZuneSubRow.PageViews
         : ZuneSubRow.None;

    /// <summary>The band's whole height for a nav style: both rows under Zune, nothing otherwise. Row 2 is ALWAYS laid out
    /// under Zune (empty for a route with no sub row), so the band is the same height on every route and a navigation
    /// never moves the content card.</summary>
    public static float BandHeight(ShellNavStyle style) => style == ShellNavStyle.Zune ? PivotRowHeight + SubRowHeight : 0f;

    /// <summary>The sub-pivots show only under Library.</summary>
    public static bool ShowsSub(string routeName) => TopOf(routeName) == LibraryPivot;

    /// <summary>The pins show when the setting is on and the viewport is wide enough for them beside the pivots.</summary>
    public static bool ShowsPins(bool setting, float viewportW) => setting && float.IsFinite(viewportW) && viewportW >= PinsMinViewportW;

    /// <summary>The pin tiles: the first <see cref="MaxPins"/> pins that have a route, in order. A folder has no route and is
    /// skipped. <paramref name="into"/> is cleared and refilled, so the caller's list is reused between renders.</summary>
    public static void PinTiles(IReadOnlyList<SidebarPin> pins, List<SidebarPin> into)
    {
        into.Clear();
        for (int i = 0; i < pins.Count && into.Count < MaxPins; i++)
            if (pins[i].RouteKey.Length > 0) into.Add(pins[i]);
    }
}
