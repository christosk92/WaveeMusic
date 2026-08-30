using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;

namespace Wavee;

// O3 — THE NAV BAND'S PURE HALF (the SidebarRowGeometry / SidebarBinderPipeline split, applied to the shortcut band).
//
// The band itself (`SidebarNavBand`) is engine-bound — rows, tiles, covers, menus — and is therefore NOT source-included
// by Wavee.Tests. Everything the band DECIDES lives here instead (System + Wavee.Core.Sidebar + SidebarPinId only), so
// the ordering, the empty-collapse and the rail truncation are pinned by `SidebarNavBandTests` against production code
// rather than against a copy of it.
//
// WHAT IT DOES NOT OWN: the band's CONTENT. That is `SidebarCustomLayout.EffectiveTopBar` — one global list on the
// layout document, mutated only through the existing `AddTopBarItem`/`MoveTopBarItem`/`RemoveTopBarItem` commands. This
// file adds no schema, no command and no second source of truth; it is a projection of that list into the four render
// shapes the band draws.

/// <summary>Which of the four shapes a band tile draws. Mirrors <see cref="SidebarItemTarget"/> deliberately rather than
/// reusing it: the RENDERER's vocabulary is "route glyph / artwork / play / bound action", and an item target that a
/// future build adds must degrade to the route shape here instead of failing to draw.</summary>
public enum SidebarNavBandTileKind : byte
{
    Route = 0,
    Entity = 1,
    Track = 2,
    Action = 3,
}

/// <summary>One shaped band tile. <paramref name="Index"/> is the tile's index in the EFFECTIVE band, so the renderer
/// resolves the full <see cref="SidebarItemSpec"/> (label override, icon override, fallback title/art, binding) from the
/// same list it was shaped from — the shaping never copies display state, which is what keeps this record POD and this
/// file engine-free.</summary>
readonly record struct SidebarNavBandTile(
    int Index,
    string ItemId,
    SidebarNavBandTileKind Kind,
    string Key,
    string? RouteKey);

static class SidebarNavBandModel
{
    /// <summary>The band's hard cap — the reducer's, not a second one. Enforced there
    /// (<see cref="SidebarLayoutReducer.MaxTopBarItems"/>) as a rejection; re-read here purely as the truncation bound so
    /// a hand-edited document that somehow carries more cannot make the band outgrow the pane's head.</summary>
    public const int MaxTiles = SidebarLayoutReducer.MaxTopBarItems;

    /// <summary>Does the band render at all? An EMPTY list means the user emptied it on purpose (Home is genuinely
    /// removable), and an emptied band draws NOTHING in both forms — no head chrome, no rail divider. Null never reaches
    /// here: <c>SidebarPreferences.TopBar</c> is <c>EffectiveTopBar</c>, which resolves null to the built-in default.</summary>
    public static bool Renders(IReadOnlyList<SidebarItemSpec>? band) => band is { Count: > 0 };

    /// <summary>The tile shape for an item. An unknown//future target degrades to <see cref="SidebarNavBandTileKind.Route"/>
    /// — the one shape that can always draw something (a glyph + a label) instead of a hole.</summary>
    public static SidebarNavBandTileKind KindOf(SidebarItemSpec item) => item.Target switch
    {
        SidebarItemTarget.Entity => SidebarNavBandTileKind.Entity,
        SidebarItemTarget.Track => SidebarNavBandTileKind.Track,
        SidebarItemTarget.Action => SidebarNavBandTileKind.Action,
        _ => SidebarNavBandTileKind.Route,
    };

    /// <summary>The app route this tile navigates to — and therefore the route it draws SELECTED for. Null means the tile
    /// has no destination: a Track plays, an Action executes, and an entity uri the pin scheme refuses (an episode, a
    /// hand-edited document) has nowhere to go and renders visible-but-inert.
    ///
    /// <para>The uri → route map is <see cref="SidebarPinId.FromUri"/>'s, never a second one: a playlist/album/artist/show
    /// id IS its route key, and that rule already has an owner.</para></summary>
    public static string? RouteKeyOf(SidebarItemSpec item) => item.Target switch
    {
        SidebarItemTarget.Entity => SidebarPinId.FromUri(item.Key),
        SidebarItemTarget.Track or SidebarItemTarget.Action => null,
        _ => item.Key is { Length: > 0 } key ? key : null,
    };

    /// <summary>Does this tile draw its selection mark for <paramref name="route"/>? One owner, so the expanded row's mark
    /// and the rail tile's selected treatment can never disagree.</summary>
    public static bool SelectsRoute(SidebarItemSpec item, string? route)
        => route is { Length: > 0 }
           && RouteKeyOf(item) is { Length: > 0 } target
           && string.Equals(target, route, StringComparison.Ordinal);

    /// <summary>Shape the effective band into <paramref name="into"/> (cleared first), in DOCUMENT ORDER — the band is a
    /// flat authored list and its order is the user's, so nothing here sorts, groups or promotes.
    ///
    /// <para>Truncation is a tail drop at <paramref name="cap"/>: over-cap is already a reducer REJECTION, so a band that
    /// arrives over-cap is a hand-edited or newer-build document, and dropping the tail keeps the head's geometry
    /// predictable instead of letting the band push the pane's list off-screen. Hidden items are deliberately NOT
    /// filtered — the band's three commands never set <c>Hidden</c>, and the landed toolbar drew the list verbatim.</para></summary>
    /// <returns>The number of tiles written.</returns>
    public static int Shape(IReadOnlyList<SidebarItemSpec>? band, List<SidebarNavBandTile> into, int cap = MaxTiles)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (band is null || cap <= 0) return 0;

        for (int i = 0; i < band.Count && into.Count < cap; i++)
        {
            var item = band[i];
            if (item is null) continue;
            into.Add(new SidebarNavBandTile(i, item.Id, KindOf(item), item.Key, RouteKeyOf(item)));
        }
        return into.Count;
    }
}
