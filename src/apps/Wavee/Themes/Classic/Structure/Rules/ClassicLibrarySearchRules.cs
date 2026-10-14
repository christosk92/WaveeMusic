// ── Themes/Classic/Structure/Rules/ClassicLibrarySearchRules.cs — the numbers of Classic's library search rows (themes plan 6.1, T10c1; P22) ────────
//
// `LibrarySearchRowRules` from `Entities\User.UI.cs`, moved with the rows it sizes: the row shape of each kind of hit and which hits wear a circular cover, engine-free so a fact pins each one.
// The adapters (`ClassicLibraryPage.Search.cs`) only feed them to `Controls.Surface`.
// A shared row is its art plus `SurfaceGeometry.RowPad` on both sides, and never under `Shape.RowFloor` unless it states its own floor — so a library hit states the floor it actually renders at
// (its art + padding) instead of the 64 every other media row has: an artist / album hit keeps the 56 it has always had (a 40 cover), a track hit is 52 (a 36 cover) or 48 with its artwork hidden.
//
// Public, not internal: this assembly has no `InternalsVisibleTo`, and the rule is pinned by a fact rather than by reading this source.

namespace Wavee.Themes.Classic;

/// <summary>The library search rows' shapes.</summary>
public static class ClassicLibrarySearchRules
{
    /// <summary>The artist / album hit's cover edge and row floor (40 + 2 × 8 = 56).</summary>
    public const float NameArt = 40f, NameRowFloor = 56f;

    /// <summary>The track hit's cover edge while track artwork shows (the hidden-artwork square is <see cref="TrackRowRules.NoArtworkEdge"/>).</summary>
    public const float TrackArt = 36f;

    /// <summary>An artist is a person: a circular cover. An album (or any other hit) is a square one.</summary>
    public static bool IsCircular(EntityKind kind) => kind == EntityKind.Artist;

    /// <summary>The artist / album hit's shape.</summary>
    public static readonly SurfaceShape NameShape = Shape.Row(NameArt) with { MinHeight = NameRowFloor };

    static readonly SurfaceShape s_trackArt = TrackShapeOf(TrackArt), s_trackGlyph = TrackShapeOf(TrackRowRules.NoArtworkEdge);

    /// <summary>The track hit's shape: the art edge follows the hidden-artwork rule (<see cref="TrackRowRules.ArtEdge"/>).</summary>
    public static SurfaceShape TrackShape(bool showArtwork) => showArtwork ? s_trackArt : s_trackGlyph;

    static SurfaceShape TrackShapeOf(float edge) => Shape.Row(edge) with { MinHeight = edge + 2f * SurfaceGeometry.RowPad };
}
