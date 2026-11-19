// ── Themes/Zune/Structure/Rules/ZuneHintRules.cs — the numbers of Zune's in-app hints (settings rework phase 6, design 7.3) ──────────────────
//
// Data, not decisions: `Views\Overlays\ZuneHint.cs` reads them (WVTH003 allows literals only here, in `Rules\**`). A Zune hint is one
// lowercase line in the meta voice (Z-InContext: 12/18, the secondary ink), its words the Settings kit's commands; these are where it sits.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;

namespace Wavee.Themes.Zune;

/// <summary>Where Zune's hint line sits and how it arrives.</summary>
public static class ZuneHintRules
{
    /// <summary>Between the line and its first word (the Settings home suggestion's own gap).</summary>
    public const float WordGap = ZuneSettingsRules.ControlGap;

    /// <summary>The lyrics pane's line: on the lyrics' own gutter (the core insets the slot by the surface's side pad), 12 above the pane's foot.</summary>
    public static readonly Edges4 LyricsMargin = new(0f, 0f, 0f, 12f);

    /// <summary>The devices picker's line under its quality row: on the rows' text column (the row's 16 inset, then the 8 dot and its 12 gap).</summary>
    public static readonly Edges4 PickerMargin = new(ZunePanelBodies.PickerPadX + ZunePanelBodies.PickerDot + ZunePanelBodies.PickerGap, 0f, ZunePanelBodies.PickerPadX, 4f);

    /// <summary>The bell panel's line under its header: the panel's own padding holds it.</summary>
    public static readonly Edges4 BellMargin = new(0f, 0f, 0f, 4f);

    /// <summary>A line that floats by a control (the player bar's, the queue's) or over Home's top lies on a band of the bar's own ink: its padding
    /// (the bar's 24 row inset, 6 above and below the 18 line), never a card's ring or corners.</summary>
    public static readonly Edges4 BandPadding = new(24f, 6f, 24f, 6f);

    /// <summary>A floating line is a reading line, not the window.</summary>
    public const float FloatMaxWidth = 480f;

    /// <summary>Above the player bar a floating line keeps the bar's own row inset from the bar's ends.</summary>
    public const float FloatInset = 24f;

    /// <summary>In the queue panel the band runs from edge to edge (no inset) and its words sit on the panel's content column (the panel's own
    /// inset as the band's padding).</summary>
    public const float QueueInset = 0f;
    public static readonly Edges4 QueueBandPadding = new(ZunePanelBodies.Inset.Left, BandPadding.Top, ZunePanelBodies.Inset.Right, BandPadding.Bottom);

    /// <summary>The Home banner's widest (the runtime banner's lane cell).</summary>
    public const float BannerMaxWidth = ZuneOverlayRules.BannerMaxWidth;

    /// <summary>The Home banner's air under the band.</summary>
    public const float BannerGap = 8f;

    /// <summary>How far the Home banner drops below the overlay lane's own top pad so it starts under the title bar and the band (the pivots
    /// and the page's views stay readable).</summary>
    public static float BannerTop(in TierFacts tiers)
        => MathF.Max(0f, ZuneTiers.FrameOf(tiers).TitleBarH + ZuneBandRules.BandOf(tiers).ExpandedH + BannerGap - Setup.RuntimeRules.BannerTopPad);

    /// <summary>The entrance: the line slides in from the right as it fades (Zune's words arrive sideways).</summary>
    public const float EnterDx = 24f;
}
