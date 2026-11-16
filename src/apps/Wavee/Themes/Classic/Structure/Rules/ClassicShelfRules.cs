// ── Themes/Classic/Structure/Rules/ClassicShelfRules.cs — the numbers of Classic's Spine Shelf ("Because you like") ─────────────────────────
//
// The concept (scratch/sphome/spine-shelf.html) at Standard: a 380 shelf, 60 spines 6 apart, the open card padded 16, a 56 head 14 over
// the song tiles 8 apart, 8 corners outside and 6 on the tiles. Each content tier states its own row; Compact stands the shelf up (rows of
// 56, the open card 440). Data, not decisions (WVTH003): the mechanics are core (Home/SpineShelf), the views are ClassicHomeShelf. Classic's skin
// (Figtree, the green pill) shares WinUI's proportions: the concept draws both at one geometry.

using Wavee.Theming.Contracts;
using Wavee.HomeUi;

namespace Wavee.Themes.Classic;

/// <summary>Classic's Spine Shelf numbers, per content tier.</summary>
public static class ClassicShelfRules
{
    /// <summary>The geometry of content tier <paramref name="tier"/>.</summary>
    public static SpineShelfGeometry Of(WidthTier tier) => tier switch
    {
        WidthTier.Compact => new(Standing: true, Height: 0f, SpineW: 0f, RowH: 56f, OpenH: 440f, Gap: 6f, Radius: 8f,
            FacePad: 12f, HeadH: 88f, HeadGap: 12f, TileGap: 6f, TileRadius: 6f, TileCover: 72f, MeterW: 3f, ProgressH: 3f),
        WidthTier.Small => new(Standing: false, Height: 360f, SpineW: 52f, RowH: 0f, OpenH: 0f, Gap: 6f, Radius: 8f,
            FacePad: 14f, HeadH: 52f, HeadGap: 12f, TileGap: 6f, TileRadius: 6f, TileCover: 72f, MeterW: 3f, ProgressH: 3f),
        WidthTier.Standard => new(Standing: false, Height: 380f, SpineW: 60f, RowH: 0f, OpenH: 0f, Gap: 6f, Radius: 8f,
            FacePad: 16f, HeadH: 56f, HeadGap: 14f, TileGap: 8f, TileRadius: 6f, TileCover: 96f, MeterW: 3f, ProgressH: 3f),
        _ => new(Standing: false, Height: 420f, SpineW: 64f, RowH: 0f, OpenH: 0f, Gap: 8f, Radius: 8f,
            FacePad: 16f, HeadH: 56f, HeadGap: 16f, TileGap: 10f, TileRadius: 6f, TileCover: 110f, MeterW: 3f, ProgressH: 3f),
    };

    // ── the spine (the row form: a column; standing: a row) ──
    /// <summary>The spine's top and bottom air, and the air between its parts.</summary>
    public const float SpineTop = 8f, SpineBottom = 10f, SpineGap = 10f;
    /// <summary>The number's line (10/14, tabular) and the cover inside the spine's sides.</summary>
    public const float NumberLine = 14f, SpineCoverInset = 14f, SpineCoverRadius = 4f;
    /// <summary>The artist's avatar at the spine's foot; the title's line across the turned words.</summary>
    public const float Avatar = 22f, TitleLine = 20f;
    /// <summary>Standing: the row's cover and its side padding.</summary>
    public const float RowCover = 38f, RowPadStart = 6f, RowPadEnd = 12f;

    // ── the open card's head ──
    /// <summary>The head's cover (Standing: the smaller one) and the reason line's avatar.</summary>
    public const float HeadCover = 56f, HeadCoverStanding = 48f, ReasonAvatar = 16f;
    /// <summary>Play's and the heart's height: the Compact 40 target standing, the 32 floor above it.</summary>
    public static float ActionH(SpineShelfGeometry g) => g.Standing ? 40f : 32f;

    // ── the washes: the cover's colour over the card fill ──
    /// <summary>How much of the cover's colour a closed spine carries at its head (fading to the card at 70 %), the hover's, and the open card's.</summary>
    public const float SpineMix = 0.34f, SpineFadeAt = 0.7f, FaceMix = 0.3f, FaceFadeAt = 0.6f, FaceAngle = 70f, SpineAngle = 90f;
    /// <summary>A spine under the pointer: more of the colour, reaching further down (the concept's 46 % to 75 %).</summary>
    public const float SpineHoverMix = 0.46f, SpineHoverFadeAt = 0.75f;

    /// <summary>The reason's artist name: Classic's semibold.</summary>
    public const ushort ReasonWeight = 600;

    /// <summary>The tile caption's inner padding.</summary>
    public const float CaptionPad = 10f;
}
