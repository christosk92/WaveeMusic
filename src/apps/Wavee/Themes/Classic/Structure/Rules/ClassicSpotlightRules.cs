// ── Themes/Classic/Structure/Rules/ClassicSpotlightRules.cs — the numbers of Classic's Release Spotlight ("From artists you follow") ────────
//
// Wavee Classic's own Home card (the daylist card: a Fluent card frame, its copy 24 in, the 40/52 hero title, one action row of the Accent Play
// with its label, the heart and the round sound button) holding the release the way Classic's album head does (the cover on the left at the
// album page's corner, the type and the date over the title, the songs as Classic track rows). The canvas stands as a tall 9:16 tile on the
// card's right. Each content tier states its card's exact height (the copy's tallest form: an EP's song rows); Compact stacks the cover over
// the words and drops the canvas, its buttons at the 40 target. Data, not decisions (WVTH003).

using FluentGpu.Controls;
using FluentGpu.Foundation;
using Wavee.HomeUi;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>What Classic's skin reads beside the core geometry: the title's rung, the action row's control size and height.</summary>
public readonly record struct ClassicSpotlightTier(bool HeroTitle, ControlSize Buttons, float ActionH);

/// <summary>Classic's Release Spotlight numbers, per content tier.</summary>
public static class ClassicSpotlightRules
{
    /// <summary>The geometry of content tier <paramref name="tier"/>.</summary>
    public static ReleaseSpotlightGeometry Of(WidthTier tier) => tier switch
    {
        WidthTier.Compact => new(Narrow: true, Height: 484f, Pad: 16f, Gap: 16f, Radius: 8f, Cover: 152f, CoverRadius: 4f, NarrowCoverH: 152f,
            CoverBleeds: false, CopyPad: default, CanvasW: 0f, CanvasH: 0f, CanvasRadius: 8f, TrackRows: 3, TrackRowH: 40f, TrackRadius: 4f,
            NextCover: 40f, NextGap: 8f, StripGap: 12f, PageGap: 16f, ProgressH: 3f, Tier: new ClassicSpotlightTier(false, ControlSize.Large, 40f)),
        WidthTier.Small => new(Narrow: false, Height: 292f, Pad: 20f, Gap: 20f, Radius: 8f, Cover: 172f, CoverRadius: 4f, NarrowCoverH: 0f,
            CoverBleeds: false, CopyPad: default, CanvasW: 142f, CanvasH: 252f, CanvasRadius: 8f, TrackRows: 3, TrackRowH: 32f, TrackRadius: 4f,
            NextCover: 40f, NextGap: 8f, StripGap: 12f, PageGap: 16f, ProgressH: 3f, Tier: new ClassicSpotlightTier(false, ControlSize.Medium, 32f)),
        WidthTier.Standard => new(Narrow: false, Height: 324f, Pad: 24f, Gap: 24f, Radius: 8f, Cover: 216f, CoverRadius: 4f, NarrowCoverH: 0f,
            CoverBleeds: false, CopyPad: default, CanvasW: 155f, CanvasH: 276f, CanvasRadius: 8f, TrackRows: 3, TrackRowH: 32f, TrackRadius: 4f,
            NextCover: 44f, NextGap: 8f, StripGap: 12f, PageGap: 16f, ProgressH: 3f, Tier: new ClassicSpotlightTier(true, ControlSize.Medium, 32f)),
        _ => new(Narrow: false, Height: 352f, Pad: 24f, Gap: 28f, Radius: 8f, Cover: 240f, CoverRadius: 4f, NarrowCoverH: 0f,
            CoverBleeds: false, CopyPad: default, CanvasW: 171f, CanvasH: 304f, CanvasRadius: 8f, TrackRows: 4, TrackRowH: 32f, TrackRadius: 4f,
            NextCover: 48f, NextGap: 8f, StripGap: 12f, PageGap: 16f, ProgressH: 3f, Tier: new ClassicSpotlightTier(true, ControlSize.Medium, 32f)),
    };

    /// <summary>The artist's round photo in the eyebrow (the Spine Shelf's reason avatar's voice, a step up).</summary>
    public const float Avatar = 20f;
    /// <summary>The artist's name: Classic's semibold, as the shelf's reason names it.</summary>
    public const ushort ArtistWeight = 600;
    /// <summary>The non-hero title: the daylist card's 28/36 display at Classic's semibold.</summary>
    public const ushort TitleWeight = 600;
    /// <summary>A song row's number column and its sides' padding (Classic's track list: the number right-aligned in its own column).</summary>
    public const float NumberW = 20f, RowPad = 8f;
    /// <summary>The waiting covers' corners.</summary>
    public const float NextRadius = 4f;

    // ── the wash: the cover's colour running into the card from the left, behind the cover (Classic's artist hero veil, turned) ──
    /// <summary>The colour's share at the card's left edge, at <see cref="WashMidAt"/>, and where it has given way to the card.</summary>
    public const float WashMix = 0.42f, WashMid = 0.16f, WashMidAt = 0.4f, WashFadeAt = 0.75f;
}
