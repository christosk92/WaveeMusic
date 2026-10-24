// ── Theming/Roles/ShapeRoles.cs — the corner radii a style gives each kind of surface (themes plan 5.4) ────────────────────
//
// The engine's radii ramp (`Radii.Control`, `Radii.Card`, `Radii.Overlay`, `Radii.Full` on `ThemeDefinition.Radii`) says how round a
// CONTROL is. A shape role says how round a kind of WAVEE surface is: a cover, a tile, a pin's art, the "you are here" mark, the seam
// where the content layer meets the panes, the omnibar's suggestion row, the seek line. Zune squares almost everything (covers 2, focus
// 4, round metro buttons full) and WinUI rounds at 4 and 8, so the radius belongs to the style, and a view reads it where it builds the
// box (`Corners = R.Shape.All(ShapeRole.Cover)`), never into a field.
//
// A shape role is a NON-colour role: a style swap that moves one changes geometry, so `WaveeStyleRoles.DiffersOnlyInColours` answers
// false and the swap is a theme transaction, never a live colour fade.

using FluentGpu.Foundation;

namespace Wavee.Theming.Roles;

/// <summary>The kinds of surface a style gives a corner radius (themes plan 5.4).</summary>
public enum ShapeRole : byte { Card, Cover, Control, Overlay, Tile, PinArt, Avatar, CurrentMark, ContentSeam, OmniRow, SeekTrack }

/// <summary>The shape roles of a style: one uniform corner radius in DIP per <see cref="ShapeRole"/>. Every style defines all of them
/// (themes plan S2). A radius at or above half the box is a full round (the engine clamps a corner to half the box when it records it,
/// so <c>RadiiScale.Full</c>'s 999 is "a circle or a capsule" at any size). Equality is by value.</summary>
/// <param name="Card">A media card or shelf cell.</param>
/// <param name="Cover">Artwork drawn as content (a row's or a hero's cover, the player's art).</param>
/// <param name="Control">A Wavee-drawn control plate (a chip, a quiet button) where the engine's control table does not draw it.</param>
/// <param name="Overlay">A flyout, a menu, a dialog card, a floating pane.</param>
/// <param name="Tile">A surface tile (a library or browse tile, a grid cell).</param>
/// <param name="PinArt">A pinned item's art (a nav row's cover, a band pin).</param>
/// <param name="Avatar">A person (a profile, an artist's face, a friend).</param>
/// <param name="CurrentMark">The "you are here" or "now playing" mark (a selection pill, a playing dot).</param>
/// <param name="ContentSeam">The corner where the content layer meets the panes beside it.</param>
/// <param name="OmniRow">A row of the omnibar's suggestions and of the command palette.</param>
/// <param name="SeekTrack">The seek line's track.</param>
public sealed record ShapeRoles(
    float Card, float Cover, float Control, float Overlay, float Tile, float PinArt, float Avatar,
    float CurrentMark, float ContentSeam, float OmniRow, float SeekTrack)
{
    /// <summary>The radius of <paramref name="role"/>.</summary>
    public float For(ShapeRole role) => role switch
    {
        ShapeRole.Card => Card,
        ShapeRole.Cover => Cover,
        ShapeRole.Control => Control,
        ShapeRole.Overlay => Overlay,
        ShapeRole.Tile => Tile,
        ShapeRole.PinArt => PinArt,
        ShapeRole.Avatar => Avatar,
        ShapeRole.CurrentMark => CurrentMark,
        ShapeRole.ContentSeam => ContentSeam,
        ShapeRole.OmniRow => OmniRow,
        ShapeRole.SeekTrack => SeekTrack,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>The four corners of <paramref name="role"/>, uniform.</summary>
    public CornerRadius4 All(ShapeRole role) => CornerRadius4.All(For(role));
}
