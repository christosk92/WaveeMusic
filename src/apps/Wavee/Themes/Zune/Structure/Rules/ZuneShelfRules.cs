// ── Themes/Zune/Structure/Rules/ZuneShelfRules.cs — the numbers of Zune's Spine Shelf ("because you like") ───────────────────────────────
//
// The concept's Zune skin (scratch/sphome/spine-shelf.html): flat, square spines in the cover's own colour carrying a huge light lowercase
// title turned to read upward (34/40, display type: never shrunk, it runs off the spine's top instead), the number in the spine's corner;
// the open card flat on the card fill, its title in the 36/40 light voice, the play and save rings. Spines are wider than WinUI's so the
// display line fits across them. Compact stands the shelf up (rows of 64 carrying the same 34 title: never shrunk). Data, not decisions (WVTH003).

using Wavee.HomeUi;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Spine Shelf numbers, per content tier.</summary>
public static class ZuneShelfRules
{
    /// <summary>The geometry of content tier <paramref name="tier"/>; <paramref name="lay"/> rides along for the rings.</summary>
    public static SpineShelfGeometry Of(WidthTier tier, ZuneHomeLayout lay) => tier switch
    {
        WidthTier.Compact => new(Standing: true, Height: 0f, SpineW: 0f, RowH: 64f, OpenH: 460f, Gap: 4f, Radius: 0f,
            FacePad: 12f, HeadH: 128f, HeadGap: 12f, TileGap: 4f, TileRadius: 0f, TileCover: 72f, MeterW: 3f, ProgressH: 3f, Tier: lay),
        WidthTier.Small => new(Standing: false, Height: 380f, SpineW: 64f, RowH: 0f, OpenH: 0f, Gap: 4f, Radius: 0f,
            FacePad: 14f, HeadH: 80f, HeadGap: 12f, TileGap: 4f, TileRadius: 0f, TileCover: 72f, MeterW: 3f, ProgressH: 3f, Tier: lay),
        WidthTier.Standard => new(Standing: false, Height: 400f, SpineW: 68f, RowH: 0f, OpenH: 0f, Gap: 4f, Radius: 0f,
            FacePad: 16f, HeadH: 80f, HeadGap: 14f, TileGap: 4f, TileRadius: 0f, TileCover: 96f, MeterW: 3f, ProgressH: 3f, Tier: lay),
        _ => new(Standing: false, Height: 440f, SpineW: 72f, RowH: 0f, OpenH: 0f, Gap: 6f, Radius: 0f,
            FacePad: 16f, HeadH: 80f, HeadGap: 16f, TileGap: 6f, TileRadius: 0f, TileCover: 110f, MeterW: 3f, ProgressH: 3f, Tier: lay),
    };

    /// <summary>The spine's title: 34/40 light, turned in the row form, along the row standing (display type, never shrunk).</summary>
    public const float SpineTitleSize = 34f, SpineTitleLine = 40f;
    /// <summary>The spine's top air (the number sits in it) and the title's inset from the spine's foot.</summary>
    public const float SpineTop = 30f, SpineBottom = 12f, NumberInset = 8f;
    /// <summary>A hovered spine lightens toward the ink by this much (the concept's 15 % white).</summary>
    public const float HoverMix = 0.15f;
    /// <summary>Standing: the row's side padding.</summary>
    public const float RowPad = 12f;

    /// <summary>The open card's head: its flat cover, the title 36/40 light.</summary>
    public const float HeadCover = 64f, HeadCoverStanding = 56f, HeadTitleSize = 36f, HeadTitleLine = 40f, HeadGap = 14f;
    /// <summary>The tile caption's padding and its title 16 light.</summary>
    public const float CaptionPad = 10f, CaptionTitleSize = 16f, CaptionTitleLine = 20f;
}
