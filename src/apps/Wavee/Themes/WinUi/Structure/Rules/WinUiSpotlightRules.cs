// ── Themes/WinUi/Structure/Rules/WinUiSpotlightRules.cs — the numbers of WinUI's Release Spotlight ("From artists you follow") ──────────────
//
// The concept (scratch/sphome/release-spotlight.html, WinUI skin) at Standard: a card washed in the cover's colour from its corner, the cover
// lifted on the card's left, the words beside it, the canvas as a tall 9:16 tile on the right, 24 inside the card's 8 corners. Each content tier
// states its own card; Compact stacks the cover over the words and drops the canvas. The card's height is exact per tier (the copy's tallest
// form: the eyebrow, the hero title's line, the meta, an EP's song rows and the actions). Data, not decisions (WVTH003): the mechanics are core
// (Home/ReleaseSpotlight), the view is WinUiHomeSpotlight.

using FluentGpu.Foundation;
using Wavee.HomeUi;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>What WinUI's skin reads beside the core geometry: the title's voice and the actions' height.</summary>
public readonly record struct WinUiSpotlightTier(bool HeroTitle, float ActionH, bool Standing);

/// <summary>WinUI's Release Spotlight numbers, per content tier.</summary>
public static class WinUiSpotlightRules
{
    /// <summary>The geometry of content tier <paramref name="tier"/>.</summary>
    public static ReleaseSpotlightGeometry Of(WidthTier tier) => tier switch
    {
        WidthTier.Compact => new(Narrow: true, Height: 476f, Pad: 16f, Gap: 16f, Radius: 8f, Cover: 152f, CoverRadius: 6f, NarrowCoverH: 152f,
            CoverBleeds: false, CopyPad: default, CanvasW: 0f, CanvasH: 0f, CanvasRadius: 6f, TrackRows: 3, TrackRowH: 40f, TrackRadius: 4f,
            NextCover: 40f, NextGap: 8f, StripGap: 12f, PageGap: 16f, ProgressH: 3f, Tier: new WinUiSpotlightTier(false, 40f, true)),
        WidthTier.Small => new(Narrow: false, Height: 284f, Pad: 20f, Gap: 20f, Radius: 8f, Cover: 168f, CoverRadius: 6f, NarrowCoverH: 0f,
            CoverBleeds: false, CopyPad: default, CanvasW: 118f, CanvasH: 210f, CanvasRadius: 6f, TrackRows: 3, TrackRowH: 32f, TrackRadius: 4f,
            NextCover: 40f, NextGap: 8f, StripGap: 12f, PageGap: 16f, ProgressH: 3f, Tier: new WinUiSpotlightTier(false, 32f, false)),
        WidthTier.Standard => new(Narrow: false, Height: 312f, Pad: 24f, Gap: 24f, Radius: 8f, Cover: 200f, CoverRadius: 6f, NarrowCoverH: 0f,
            CoverBleeds: false, CopyPad: default, CanvasW: 149f, CanvasH: 264f, CanvasRadius: 6f, TrackRows: 3, TrackRowH: 32f, TrackRadius: 4f,
            NextCover: 44f, NextGap: 8f, StripGap: 12f, PageGap: 16f, ProgressH: 3f, Tier: new WinUiSpotlightTier(true, 32f, false)),
        _ => new(Narrow: false, Height: 344f, Pad: 24f, Gap: 28f, Radius: 8f, Cover: 224f, CoverRadius: 6f, NarrowCoverH: 0f,
            CoverBleeds: false, CopyPad: default, CanvasW: 167f, CanvasH: 296f, CanvasRadius: 6f, TrackRows: 4, TrackRowH: 32f, TrackRadius: 4f,
            NextCover: 48f, NextGap: 8f, StripGap: 12f, PageGap: 16f, ProgressH: 3f, Tier: new WinUiSpotlightTier(true, 32f, false)),
    };

    /// <summary>The eyebrow's round avatar (the artist's own photo) and the air before the words.</summary>
    public const float Avatar = 24f;
    /// <summary>A song row's number column (tabular, right-aligned) and its sides' padding.</summary>
    public const float NumberW = 20f, RowPad = 8f;
    /// <summary>The waiting covers' corners.</summary>
    public const float NextRadius = 4f;
    /// <summary>The previews' sound button's glyph.</summary>
    public const float SoundGlyph = 16f;

    // ── the wash: the cover's colour from the card's top-left corner into the card fill (the concept's radial at 0 0) ──
    /// <summary>The colour's share at the corner, and where it has given way to the card (a radial of the card's diagonal).</summary>
    public const float WashMix = 0.5f, WashMid = 0.2f, WashMidAt = 0.45f, WashFadeAt = 0.85f;
}
