using System;

namespace Wavee.Features.Detail;

/// <summary>Which persisted rail preference pair (width + collapsed) a two-column detail surface reads and writes.
/// One scope per surface family so Liked never silently shares the ALBUM width and collapse flag (the old
/// "not playlist ⇒ album" fallback), and a podcast show keeps its own too.
/// <para><see cref="Uniform"/> is the fifth, synthetic scope "Keep left-rail same size" resolves every surface to
/// (<see cref="DetailRailPolicy.ScopeFor"/>) — its own key pair, never one of the four above.</para></summary>
public enum RailScope : byte { Album, Playlist, Liked, Show, Uniform }

/// <summary>The PURE rail-resize policy for the shared detail surface — engine-free so <c>DetailRailPolicyTests</c>
/// pins which surfaces get a grip and at which layout mode. <c>DetailShell</c> composes the grip from this; the
/// per-surface answer lives on the <c>DetailConfig</c> literal (<c>RailResizable</c> / <c>RailScope</c>).</summary>
public static class DetailRailPolicy
{
    /// <summary>The only layout mode with a user-resizable rail: the wide two-column arm. The responsive mid/narrow
    /// modes (224 / 188) compose their breakpoint rail and ignore the stored width — the grip that could undo a
    /// collapse does not exist there either, so the collapsed preference is honoured only here.</summary>
    public const int ResizableMode = 0;

    // The floor is 180 rather than the old 220 because every rail's own content genuinely survives it: the cover floors
    // at CoverEdge, the hero title auto-fits down to MinSize 18, the CTA cluster wraps, and the fact bentos (album and
    // Liked) are Basis=0/MinWidth=0 wrap-grow tiles.
    public const float MinWidth = 180f, MaxWidth = 480f;

    /// <summary>May THIS surface show the rail grip at THIS mode? The surface's own knob AND the wide-mode gate.</summary>
    public static bool ResizableFor(bool railResizable, int mode) => railResizable && mode == ResizableMode;

    /// <summary>The authored rail width a scope opens at before the user ever drags: album-like surfaces (album,
    /// show) are 280 wide, list-like ones (playlist, Liked) 240.</summary>
    public static float DefaultWidthFor(RailScope scope) => scope switch
    {
        RailScope.Album or RailScope.Show => WaveeSize.RailAlbum,
        _ => WaveeSize.RailPlaylist,
    };

    /// <summary>The grip's floor for a scope. One value today; the per-scope switch is the seam for a surface whose
    /// rail content cannot take 180 (the Liked facts bento was checked — its tiles wrap-grow — but it is the one to
    /// raise here if a narrower window proves otherwise).</summary>
    public static float MinWidthFor(RailScope scope) => MinWidth;

    /// <summary>A STORED width clamped to the live grip bounds before it may seed the layout: a value written by
    /// another build with a different floor, or a hand-edited store, must never seed raw.</summary>
    public static float ClampStored(float stored, RailScope scope)
        => Math.Clamp(stored, MinWidthFor(scope), MaxWidth);

    /// <summary>Which scope's persisted pair <c>DetailShell.RailFor</c> should actually read/write: the REQUESTED
    /// (per-surface) scope normally, or the one shared <see cref="RailScope.Uniform"/> scope when "Keep left-rail same
    /// size" is on — every surface then resolves to the SAME width + collapse pair, so resizing once on any page
    /// applies everywhere. Pure so <c>DetailRailPolicyTests</c> can pin the resolution rule without the engine.</summary>
    public static RailScope ScopeFor(RailScope requested, bool uniform) => uniform ? RailScope.Uniform : requested;

    /// <summary>Whether ANY of the four per-scope rail preferences has ever moved from its authored default — the
    /// enable gate for Settings' "Clear all remembered sizes": offering a reset when nothing has ever been changed
    /// would be a destructive-looking button that does nothing. Deliberately excludes <see cref="RailScope.Uniform"/>'s
    /// own pair — that row is the per-surface case and only ever shown while uniform mode is off.</summary>
    public static bool HasCustomizedRailPrefs(
        float albumWidth, bool albumCollapsed,
        float playlistWidth, bool playlistCollapsed,
        float likedWidth, bool likedCollapsed,
        float showWidth, bool showCollapsed)
        => albumWidth != DefaultWidthFor(RailScope.Album) || albumCollapsed
        || playlistWidth != DefaultWidthFor(RailScope.Playlist) || playlistCollapsed
        || likedWidth != DefaultWidthFor(RailScope.Liked) || likedCollapsed
        || showWidth != DefaultWidthFor(RailScope.Show) || showCollapsed;
}
