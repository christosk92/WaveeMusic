// ── Entities/DetailRailPrefs.cs ────────────────────────────────────────────────────────────────────────────────────────
// the detail page's rail pairs (width + collapsed, one pair per scope) as THEME STATE: the reads that seed a rail, the
// commit that persists it, the re-open verb, and the "Clear all remembered sizes" reset (M8)
//
// Role: CORE
// Spec: docs/themes/plan/technical-plan.md §5.1 C16 (X39, the persist half), §7.2 M8; ch 03 §6
//
// WHO WRITES: the frame HOST (`Detail.FrameHost`'s rail cells), never a view. A grip release calls `Commit`, the re-open
// chevron calls `SetCollapsed`, the structure's "Clear all remembered sizes" action calls `ResetAll` (the `clearRememberedSizes`
// theme verb, registered at boot); a theme's rail view only draws the cell it is handed. Every write that changes what a mounted
// frame shows bumps `Epoch` ONCE, which moves every mounted and KeepAlive-parked frame ("Keep left-rail same size" moves them all).
//
// WHERE THE VALUES LIVE: a rail's remembered size is the ACTIVE THEME's state (`ThemeState`, `theme.<id>.state.rail.<scope>.width` /
// `.collapsed`; owner Q6: pane and panel sizes are per theme), never a core setting. The keys are declared here because the core host
// writes them; the clamp (the grip's bounds) is the key's. The WIDTH rules (the page-aware cap, what a drag may commit) stay in
// `Detail.RailPolicy`; this file only moves values between that policy and the store. The page LAYOUT and the rails' UNIFORMITY are not
// state but the structure's options, read as `DetailPolicy`.

using FluentGpu.Signals;
using Wavee.Theming;

namespace Wavee;

public static class DetailRailPrefs
{
    /// <summary>The cross-surface rail epoch: bumped by every write that changes what a mounted frame shows, so any mounted (including
    /// KeepAlive-parked) detail frame re-syncs its rails LIVE rather than on its next remount.</summary>
    public static readonly Signal<int> Epoch = new(0);

    /// <summary>Moves <see cref="Epoch"/>.</summary>
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

    // One StateKey per scope and member. The defaults are the authored widths (album-like 280, list-like and uniform 240); the range is the grip's.
    static readonly StateKey<float> s_albumWidth = WidthKeyOf("rail.album.width", RailScope.Album);
    static readonly StateKey<float> s_playlistWidth = WidthKeyOf("rail.playlist.width", RailScope.Playlist);
    static readonly StateKey<float> s_likedWidth = WidthKeyOf("rail.liked.width", RailScope.Liked);
    static readonly StateKey<float> s_showWidth = WidthKeyOf("rail.show.width", RailScope.Show);
    static readonly StateKey<float> s_uniformWidth = WidthKeyOf("rail.uniform.width", RailScope.Uniform);
    static readonly StateKey<bool> s_albumCollapsed = new("rail.album.collapsed", false, false, false);
    static readonly StateKey<bool> s_playlistCollapsed = new("rail.playlist.collapsed", false, false, false);
    static readonly StateKey<bool> s_likedCollapsed = new("rail.liked.collapsed", false, false, false);
    static readonly StateKey<bool> s_showCollapsed = new("rail.show.collapsed", false, false, false);
    static readonly StateKey<bool> s_uniformCollapsed = new("rail.uniform.collapsed", false, false, false);

    static StateKey<float> WidthKeyOf(string id, RailScope scope)
        => new(id, Detail.RailPolicy.DefaultWidthFor(scope), Detail.RailPolicy.MinWidthFor(scope), Detail.RailPolicy.MaxWidth);

    /// <summary>The state key of a scope's remembered width, one per scope (an episode's page reads the show's).</summary>
    public static StateKey<float> WidthKey(RailScope scope) => scope switch
    {
        RailScope.Album => s_albumWidth,
        RailScope.Playlist => s_playlistWidth,
        RailScope.Liked => s_likedWidth,
        RailScope.Show => s_showWidth,
        _ => s_uniformWidth,
    };

    /// <summary>The state key of a scope's collapsed flag.</summary>
    public static StateKey<bool> CollapsedKey(RailScope scope) => scope switch
    {
        RailScope.Album => s_albumCollapsed,
        RailScope.Playlist => s_playlistCollapsed,
        RailScope.Liked => s_likedCollapsed,
        RailScope.Show => s_showCollapsed,
        _ => s_uniformCollapsed,
    };

    /// <summary>Registers the theme verb of the structure's "Clear all remembered sizes" action (at boot). It is enabled only while some
    /// per-scope pair differs from its default, so a destructive-looking no-op is never offered.</summary>
    public static void RegisterVerbs()
        => ThemeVerbs.Register(DetailVerbIds.ClearRememberedSizes, static () => { ResetAll(); return true; }, enabled: HasCustomized);

    /// <summary>The scope's REMEMBERED width, clamped to the live bounds before it may seed a layout
    /// (<see cref="Detail.RailPolicy.ClampStored"/>). The store is the truth; nothing is cached here.</summary>
    public static float StoredWidth(RailScope scope)
        => Detail.RailPolicy.ClampStored(ThemeState.Get(WidthKey(scope)), scope);

    /// <summary>Is the scope's rail collapsed to its identity strip?</summary>
    public static bool Collapsed(RailScope scope)
        => ThemeState.Get(CollapsedKey(scope));

    /// <summary>A grip release: persist the scope's remembered width and collapsed flag, with ONE epoch bump (both go to the
    /// store first, the bump after — every frame re-syncs from a store that already has both).</summary>
    public static void Commit(RailScope scope, float storedWidth, bool collapsed)
    {
        ThemeState.Set(WidthKey(scope), storedWidth);
        ThemeState.Set(CollapsedKey(scope), collapsed);
        Bump();
    }

    /// <summary>Collapse or re-open the scope's rail: persist, then ONE epoch bump.</summary>
    public static void SetCollapsed(RailScope scope, bool collapsed)
    {
        ThemeState.Set(CollapsedKey(scope), collapsed);
        Bump();
    }

    /// <summary>Has any of the four per-scope pairs moved from its authored default? The enable gate for "Clear all remembered sizes"
    /// (the Uniform pair is excluded on purpose; a destructive-looking no-op is never offered).</summary>
    public static bool HasCustomized()
        => Detail.RailPolicy.HasCustomizedRailPrefs(
            ThemeState.Get(s_albumWidth), ThemeState.Get(s_albumCollapsed),
            ThemeState.Get(s_playlistWidth), ThemeState.Get(s_playlistCollapsed),
            ThemeState.Get(s_likedWidth), ThemeState.Get(s_likedCollapsed),
            ThemeState.Get(s_showWidth), ThemeState.Get(s_showCollapsed));

    /// <summary>Reset the FOUR per-scope pairs to their authored defaults (never the uniform pair), then ONE bump, so an
    /// open detail page's rail snaps to the reset widths now rather than next launch.</summary>
    public static void ResetAll()
    {
        Span<RailScope> scopes = [RailScope.Album, RailScope.Playlist, RailScope.Liked, RailScope.Show];
        foreach (var scope in scopes)
        {
            var width = WidthKey(scope);
            ThemeState.Set(width, width.Default);
            ThemeState.Set(CollapsedKey(scope), false);
        }
        Bump();
    }
}
