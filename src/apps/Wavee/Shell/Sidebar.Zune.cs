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
