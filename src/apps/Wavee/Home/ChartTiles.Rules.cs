// ── Home/ChartTiles.Rules.cs — where Home's three Charts tiles go (pure) ──────────────────────────────────────────────
//
// Role: CORE (pure; no engine, no tables, no I/O)
// Spec: RCA 2026-09-30 (Home Charts tiles): "Charts" opens the Charts CATEGORY page; "Top 50" / "Viral 50" open the
//        ACTUAL playlists, taken data-driven from the Featured Charts section's items (never hard-coded playlist ids),
//        falling back to the Charts category page while that section has not landed or carries no match.
//
// Zones.UI.cs's `ChartsBlock` reads the Featured Charts section (through `SectionReader`, demanded through
// `Home.EnsureSection` — the same path Browse's Charts band and the section drill use) into `FeaturedChartItem`s and
// hands them here; everything below is plain string work, so the pick is unit-tested with no scope, no table and no
// element (the "no source-text tests" rule).

using System;
using System.Collections.Generic;

namespace Wavee.HomeUi;

/// <summary>One Featured Charts item as the pick sees it: its uri (text form, "" for a blank or unresolved card) and its
/// title as the card reads it right now (null while the row has none).</summary>
public readonly record struct FeaturedChartItem(string Uri, string? Title);

/// <summary>Where one chart tile goes: a Featured Charts item (<see cref="Uri"/> + <see cref="Title"/>, the title being
/// the route's frame-one arg), or — <see cref="IsFallback"/> — the Charts category page.</summary>
public readonly record struct ChartTileTarget(string Uri, string Title)
{
    /// <summary>The Charts category page: the section has not landed, or none of its items matched.</summary>
    public static ChartTileTarget Fallback => new("", "");

    public bool IsFallback => string.IsNullOrEmpty(Uri);
}

/// <summary>The Charts block's model: the two data-driven tiles' targets ("Charts" itself always opens the category
/// page). Value equality, so a recompute that picks the same items is silent downstream.</summary>
public readonly record struct ChartTilesModel(ChartTileTarget Top50, ChartTileTarget Viral50)
{
    /// <summary>Both tiles on the category page — the model before the Featured Charts section lands.</summary>
    public static ChartTilesModel Fallback => new(ChartTileTarget.Fallback, ChartTileTarget.Fallback);
}

/// <summary>The pick: the FIRST Featured Charts item, in section order, whose trimmed title starts with the tile's
/// prefix (case-insensitive). The prefixes are the wire's own playlist names ("Top 50 - Global", "Viral 50 - Global"),
/// not display copy.</summary>
public static class ChartTilePick
{
    public const string Top50Prefix = "Top 50";
    public const string Viral50Prefix = "Viral 50";

    /// <summary>Does <paramref name="title"/>, trimmed, start with <paramref name="prefix"/>, trimmed, ignoring case? A
    /// blank title or a blank prefix never matches.</summary>
    public static bool Matches(string? title, string prefix)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(prefix)) return false;
        return title.AsSpan().Trim().StartsWith(prefix.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first item (section order) matching <paramref name="prefix"/> that has a uri to open, as a target
    /// carrying its trimmed title; <see cref="ChartTileTarget.Fallback"/> when none does (including an empty list — the
    /// section not landed yet).</summary>
    public static ChartTileTarget Pick(IReadOnlyList<FeaturedChartItem> items, string prefix)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrEmpty(item.Uri) || !Matches(item.Title, prefix)) continue;
            return new ChartTileTarget(item.Uri, item.Title!.Trim());
        }
        return ChartTileTarget.Fallback;
    }

    /// <summary>Both data-driven tiles from the Featured Charts section's items.</summary>
    public static ChartTilesModel Of(IReadOnlyList<FeaturedChartItem> items)
        => new(Pick(items, Top50Prefix), Pick(items, Viral50Prefix));
}
