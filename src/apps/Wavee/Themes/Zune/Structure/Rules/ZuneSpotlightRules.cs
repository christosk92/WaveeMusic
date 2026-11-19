// ── Themes/Zune/Structure/Rules/ZuneSpotlightRules.cs — the numbers of Zune's Release Spotlight ("from artists you follow") ────────────────
//
// The concept's Zune skin (scratch/sphome/release-spotlight.html): the whole card a flat block of the cover's colour, the cover square and
// edge to edge on its left, the canvas edge to edge on its right, the words padded between them: the title huge, light and lowercase on one
// line (display type: never shrunk, it bleeds off the words' edge under a gentle fade), the eyebrow and the meta in the label case, the rings.
// Compact runs the cover the card's width (16:9) over the words and drops the canvas. Data, not decisions (WVTH003).

using FluentGpu.Foundation;
using Wavee.HomeUi;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>What Zune's skin reads beside the core geometry: the tier's rings, and the title's size (display type: one size per form, never
/// shrunk to fit; the narrow form's is the concept's 80 rung, the wide form's its 104).</summary>
public sealed record ZuneSpotlightTier(ZuneHomeLayout Lay, float TitleSize);

/// <summary>Zune's Release Spotlight numbers, per content tier.</summary>
public static class ZuneSpotlightRules
{
    /// <summary>The geometry of content tier <paramref name="tier"/>; <paramref name="lay"/> rides along for the rings.</summary>
    public static ReleaseSpotlightGeometry Of(WidthTier tier, ZuneHomeLayout lay) => tier switch
    {
        WidthTier.Compact => new(Narrow: true, Height: 560f, Pad: 0f, Gap: 0f, Radius: 0f, Cover: 0f, CoverRadius: 0f, NarrowCoverH: 207f,
            CoverBleeds: true, CopyPad: new Edges4(16f, 16f, 16f, 20f), CanvasW: 0f, CanvasH: 0f, CanvasRadius: 0f, TrackRows: 3, TrackRowH: 40f,
            TrackRadius: 0f, NextCover: 48f, NextGap: 4f, StripGap: 4f, PageGap: 12f, ProgressH: 3f, Tier: new ZuneSpotlightTier(lay, NarrowTitle)),
        WidthTier.Small => new(Narrow: false, Height: 316f, Pad: 0f, Gap: 0f, Radius: 0f, Cover: 316f, CoverRadius: 0f, NarrowCoverH: 0f,
            CoverBleeds: true, CopyPad: new Edges4(24f, 20f, 24f, 20f), CanvasW: 178f, CanvasH: 316f, CanvasRadius: 0f, TrackRows: 2, TrackRowH: 32f,
            TrackRadius: 0f, NextCover: 48f, NextGap: 4f, StripGap: 4f, PageGap: 12f, ProgressH: 3f, Tier: new ZuneSpotlightTier(lay, NarrowTitle)),
        WidthTier.Standard => new(Narrow: false, Height: 366f, Pad: 0f, Gap: 0f, Radius: 0f, Cover: 366f, CoverRadius: 0f, NarrowCoverH: 0f,
            CoverBleeds: true, CopyPad: new Edges4(28f, 22f, 28f, 22f), CanvasW: 206f, CanvasH: 366f, CanvasRadius: 0f, TrackRows: 3, TrackRowH: 32f,
            TrackRadius: 0f, NextCover: 56f, NextGap: 4f, StripGap: 4f, PageGap: 12f, ProgressH: 3f, Tier: new ZuneSpotlightTier(lay, WideTitle)),
        _ => new(Narrow: false, Height: 412f, Pad: 0f, Gap: 0f, Radius: 0f, Cover: 412f, CoverRadius: 0f, NarrowCoverH: 0f,
            CoverBleeds: true, CopyPad: new Edges4(32f, 24f, 32f, 24f), CanvasW: 232f, CanvasH: 412f, CanvasRadius: 0f, TrackRows: 4, TrackRowH: 32f,
            TrackRadius: 0f, NextCover: 64f, NextGap: 4f, StripGap: 4f, PageGap: 12f, ProgressH: 3f, Tier: new ZuneSpotlightTier(lay, WideTitle)),
    };

    /// <summary>The title's size and line (light, lowercase, one line that bleeds): the narrow forms' and the wide forms'.</summary>
    public const float NarrowTitle = 80f, WideTitle = 104f;

    /// <summary>A glyph's contrast floor on the block (WCAG's non-text 3 : 1).</summary>
    public const float GlyphContrast = 3f;

    /// <summary>The fade the title bleeds under at the words' right edge.</summary>
    public const float TitleFade = 48f;
    /// <summary>A song row: its number's column and the title's light 17 voice.</summary>
    public const float NumberW = 24f, TrackTitleSize = 17f, TrackTitleLine = 22f;
}
