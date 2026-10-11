// ── Entities/DetailRailPrefs.cs ────────────────────────────────────────────────────────────────────────────────────────
// the detail page's rail pairs (width + collapsed, one pair per scope) as a typed pref: the reads that seed a rail, the
// commit that persists it, the re-open verb, and the "Clear all remembered sizes" reset
//
// Role: CORE
// Spec: docs/themes/plan/technical-plan.md §5.1 C16 (X39, the persist half); ch 03 §6
//
// WHO WRITES: the frame HOST (`Detail.FrameHost`'s rail cells), never a view. A grip release calls `Commit`, the re-open
// chevron calls `SetCollapsed`, Settings' reset calls `ResetAll`; a theme's rail view only draws the cell it is handed.
// Every write that changes what a mounted frame shows goes through `Prefs.DetailHero.Set`, so the ONE epoch bump moves
// every mounted and KeepAlive-parked frame ("Keep left-rail same size" moves them all).
//
// WHICH KEYS: `Detail.RailPolicy.KeysFor(scope)` — the ten typed `Platform.Keys.Detail*Rail*` keys, one pair per scope,
// never another scope's by fallthrough. The WIDTH rules (clamps, the page-aware cap, what a drag may commit) stay in
// `Detail.RailPolicy`; this file only moves values between that policy and the store.

namespace Wavee;

public static class DetailRailPrefs
{
    /// <summary>The scope's REMEMBERED width, clamped to the live bounds before it may seed a layout
    /// (<see cref="Detail.RailPolicy.ClampStored"/>). The store is the truth; nothing is cached here.</summary>
    public static float StoredWidth(RailScope scope)
        => Detail.RailPolicy.ClampStored(Platform.Settings.Get(Detail.RailPolicy.KeysFor(scope).Width), scope);

    /// <summary>Is the scope's rail collapsed to its identity strip?</summary>
    public static bool Collapsed(RailScope scope)
        => Platform.Settings.Get(Detail.RailPolicy.KeysFor(scope).Collapsed);

    /// <summary>A grip release: persist the scope's remembered width and collapsed flag, with ONE epoch bump (the width
    /// goes to the store first, the collapsed write bumps — every frame re-syncs from a store that already has both).</summary>
    public static void Commit(RailScope scope, float storedWidth, bool collapsed)
    {
        var keys = Detail.RailPolicy.KeysFor(scope);
        Platform.Settings.Set(keys.Width, storedWidth);
        Prefs.DetailHero.Set(keys.Collapsed, collapsed);
    }

    /// <summary>Collapse or re-open the scope's rail: persist, then ONE epoch bump.</summary>
    public static void SetCollapsed(RailScope scope, bool collapsed)
        => Prefs.DetailHero.Set(Detail.RailPolicy.KeysFor(scope).Collapsed, collapsed);

    /// <summary>Has any of the four per-scope pairs moved from its authored default? The enable gate for Settings'
    /// "Clear all remembered sizes" (the Uniform pair is excluded on purpose; a destructive-looking no-op is never
    /// offered).</summary>
    public static bool HasCustomized()
    {
        var s = Platform.Settings;
        var album = Detail.RailPolicy.KeysFor(RailScope.Album);
        var playlist = Detail.RailPolicy.KeysFor(RailScope.Playlist);
        var liked = Detail.RailPolicy.KeysFor(RailScope.Liked);
        var show = Detail.RailPolicy.KeysFor(RailScope.Show);
        return Detail.RailPolicy.HasCustomizedRailPrefs(
            s.Get(album.Width), s.Get(album.Collapsed),
            s.Get(playlist.Width), s.Get(playlist.Collapsed),
            s.Get(liked.Width), s.Get(liked.Collapsed),
            s.Get(show.Width), s.Get(show.Collapsed));
    }

    /// <summary>Reset the FOUR per-scope pairs to their authored defaults (never the uniform pair), then ONE bump, so an
    /// open detail page's rail snaps to the reset widths now rather than next launch.</summary>
    public static void ResetAll()
    {
        var s = Platform.Settings;
        Span<RailScope> scopes = [RailScope.Album, RailScope.Playlist, RailScope.Liked, RailScope.Show];
        foreach (var scope in scopes)
        {
            var keys = Detail.RailPolicy.KeysFor(scope);
            s.Set(keys.Width, keys.Width.Default);
            s.Set(keys.Collapsed, false);
        }
        Prefs.DetailHero.Bump();
    }
}
