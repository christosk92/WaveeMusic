// ── Themes/WinUi/Structure/Rules/WinUiPodcastRules.cs — the numbers of WinUI's podcast pages v2 (the shared kit and the show page) ────────────────
//
// Concept `winui-podcast-v2.html` (theme=winui): the 4 px grid, Title Large 40/52 for the show (Title 28/36 below 1008), Subtitle 20/28 sections, Body
// 14/20, Caption 12/16; 8 between buttons, 12 between cards, 16 inside surfaces; the page gutter is WinUI's (`WinUiTiers.InsetOf`, 36 / 24 / 16).
// `WinUiPodcastKitRules` holds the kit's numbers (the episode and audiobook pages reuse them); `WinUiPodcastShowTiers` the show page's tier record.
// The tiers are the WINDOW's (the concept's table: 1008 and up = list + a 360 pane; 641-1007 one column; 640 and under stacked).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>The podcast kit's geometry (DIP) and motion.</summary>
internal static class WinUiPodcastKitRules
{
    // ── the page scaffold ──
    /// <summary>The collapsed header bar (concept `.compact`): 48 tall, a 32 cover, 12 between its parts; it fades and drops 8 in over 167 ms.</summary>
    public const float BarH = 48f, BarCover = 32f, BarGap = 12f, BarDrop = -8f, BarCoverRadius = 4f;
    public const int BarCoverDecode = 64;
    public const float BarFadeMs = 167f;
    /// <summary>The cover-tone wash at the top of the page: 232 tall, fading to nothing before the list.</summary>
    public const float WashH = 232f;
    /// <summary>The wash's strength at its top and its middle (the concept's 64 % radial over a 22 % linear).</summary>
    public const float WashTop = 0.42f, WashMid = 0.16f, WashMidStop = 0.45f;
    /// <summary>The page's foot (the dock reserve is added by the caller).</summary>
    public const float PageFoot = 48f;

    // ── the smart SplitButton ──
    public const float SplitChevron = 12f, SplitDividerInk = 0.35f;

    // ── the episode row (concept `.row`) ──
    /// <summary>The play button (36; 40 at Compact), its glyph and its ring's stroke.</summary>
    public const float PlayButton = 36f, PlayButtonCompact = 40f, PlayGlyph = 14f, RingStroke = 2.5f, RingTrackAlpha = 0.16f;
    /// <summary>The row's padding (10 / 8; 8 at Compact), its gap between columns (12) and between rows (2), its corner (4).</summary>
    public const float RowPadY = 10f, RowPadX = 8f, RowPadCompact = 8f, RowColGap = 12f, RowGap = 2f;
    /// <summary>The selection pill on the row's leading edge (3 x 16).</summary>
    public const float SelectPillW = 3f, SelectPillH = 16f;
    /// <summary>The meta line: the New dot (6), the gap between its parts (6), a meta glyph (12).</summary>
    public const float NewDot = 6f, MetaGap = 6f, MetaGlyph = 12f;
    /// <summary>The row's action buttons (32; 40 at Compact) and their glyph.</summary>
    public const float RowAction = 32f, RowActionCompact = 40f;
    /// <summary>The expanded row's air above its body and between its parts.</summary>
    public const float ExpandTop = 10f, ExpandGap = 10f;
    public const float ExpandMs = 167f;

    // ── a section header and the cards ──
    public const float SectionGap = 8f, SectionTop = 24f, SectionBottom = 12f;
    /// <summary>"More like this": a card's cover, its corner, the gap between cards, a row card's cover (the pane's rows).</summary>
    public const float CardCover = 160f, CardRadius = 8f, CardGap = 12f, RowCardCover = 40f, RowCardRadius = 4f, RowCardGap = 12f;
    public const int CardDecode = 320, RowCardDecode = 96;

    /// <summary>The row expand: 167 ms on the standard decelerate (concept note 9), a FlowReveal so what follows rides it.</summary>
    public static readonly LayoutTransition Expand = new(TransitionChannels.Size, TransitionDynamics.Tween(ExpandMs, Easing.SmoothOut),
        Size: SizeMode.FlowReveal, Anchor: SizeAnchor.Parallax);

    /// <summary>The bar's entrance and exit: a fade with an 8 drop (concept `.compact` 167 ms).</summary>
    public static readonly EnterExit BarFade = new(Dy: BarDrop, Opacity: 0f, Active: true);

    /// <summary>The wash: the cover's tone from the top, fading out before the list. <paramref name="tone"/> 0xAARRGGBB, 0 = none.</summary>
    public static GradientSpec? Wash(uint tone)
    {
        if (tone == 0u) return null;
        var c = ColorF.FromRgba((byte)(tone >> 16), (byte)(tone >> 8), (byte)tone, 255);
        return GradientDown(new GradientStop(0f, c with { A = WashTop }), new GradientStop(WashMidStop, c with { A = WashMid }),
            new GradientStop(1f, c with { A = 0f }));
    }
}

/// <summary>The show page in one WINDOW tier.</summary>
public sealed record WinUiPodcastShowLayout
{
    public required WidthTier Tier { get; init; }
    /// <summary>The list with the 360 details pane beside it (1008 and up); else one column with About as an Expander above the list.</summary>
    public required bool Pane { get; init; }
    /// <summary>Stacked header (640 and under): the cover beside the words, then the full-width button, then the commands; a ComboBox filter.</summary>
    public required bool Stacked { get; init; }
    /// <summary>What a row's click does: select (pane), expand in place (medium) or open the episode (small).</summary>
    public required PodcastRowMode RowMode { get; init; }
    /// <summary>Rows carry one line of description (medium).</summary>
    public required bool RowDescription { get; init; }
    public required float Cover { get; init; }
    public required float CoverRadius { get; init; }
    /// <summary>The hero's air above and below, and the gap beside the cover.</summary>
    public required float HeroTop { get; init; }
    public required float HeroBottom { get; init; }
    public required float CoverGap { get; init; }
    /// <summary>The title: Title Large (40/52) or Title (28/36).</summary>
    public required bool TitleLarge { get; init; }
    /// <summary>The share button carries its word.</summary>
    public required bool ShareWord { get; init; }
    /// <summary>A control's height (32; 40 at Compact) and the play button's.</summary>
    public required float Target { get; init; }
    public required float PlayButton { get; init; }
    /// <summary>The search box's width (0 = the search icon button).</summary>
    public required float Search { get; init; }
    /// <summary>The up-next label's progress bar width.</summary>
    public required float UpBar { get; init; }
}

/// <summary>What a row's click does in a tier.</summary>
public enum PodcastRowMode : byte { Select, Expand, Open }

/// <summary>The show page's fixed numbers.</summary>
internal static class WinUiPodcastShowRules
{
    /// <summary>The details pane (360), the gap between the list and it (24), its sticky top (the bar + 16) and its padding.</summary>
    public const float PaneW = 360f, ColGap = 24f, PaneTop = 64f, PanePad = 16f, PanePadTop = 4f, PaneFoot = 24f;
    /// <summary>The toolbar (52 tall, sticky under the bar) and a group header (36, sticky under the toolbar).</summary>
    public const float ToolbarH = 52f, GroupH = 36f, GroupButtonH = 28f, GroupButtonPad = 8f;
    /// <summary>The jump grid: three columns of 56-tall cells, 4 apart, 12 inside, at most 340 wide.</summary>
    public const float JumpCellH = 56f, JumpGap = 4f, JumpPad = 12f, JumpW = 340f;
    public const int JumpCols = 3;
    /// <summary>The up row (min 40 tall), its gap; the commands' gap.</summary>
    public const float UpMinH = 40f, UpGap = 16f, UpLabelGap = 2f, UpSubGap = 10f, CommandGap = 8f, IdGap = 4f, EyebrowGap = 6f;
    /// <summary>The pane's description clamp (4 lines), the rating card's number (Title 28/36).</summary>
    public const int PaneClamp = 4;
    public const float RatingCardPad = 12f, RatingStar = 16f, FactsColGap = 16f, FactsRowGap = 6f, FactLabelW = 96f;
    /// <summary>The empty list's air.</summary>
    public const float EmptyPad = 32f;
    public const string PageKey = "podcast-show";
}

/// <summary>The show page's tier records.</summary>
public static class WinUiPodcastShowTiers
{
    static readonly WinUiPodcastShowLayout s_compact = new()
    {
        Tier = WidthTier.Compact, Pane = false, Stacked = true, RowMode = PodcastRowMode.Open, RowDescription = false,
        Cover = 96f, CoverRadius = 6f, HeroTop = 20f, HeroBottom = 16f, CoverGap = 16f, TitleLarge = false, ShareWord = false,
        Target = 40f, PlayButton = WinUiPodcastKitRules.PlayButtonCompact, Search = 0f, UpBar = 96f,
    };
    static readonly WinUiPodcastShowLayout s_small = new()
    {
        Tier = WidthTier.Small, Pane = false, Stacked = false, RowMode = PodcastRowMode.Expand, RowDescription = true,
        Cover = 136f, CoverRadius = 8f, HeroTop = 32f, HeroBottom = 24f, CoverGap = 24f, TitleLarge = false, ShareWord = false,
        Target = 32f, PlayButton = WinUiPodcastKitRules.PlayButton, Search = 168f, UpBar = 160f,
    };
    static readonly WinUiPodcastShowLayout s_standard = new()
    {
        Tier = WidthTier.Standard, Pane = true, Stacked = false, RowMode = PodcastRowMode.Select, RowDescription = false,
        Cover = 160f, CoverRadius = 8f, HeroTop = 32f, HeroBottom = 24f, CoverGap = 24f, TitleLarge = true, ShareWord = true,
        Target = 32f, PlayButton = WinUiPodcastKitRules.PlayButton, Search = 208f, UpBar = 160f,
    };

    /// <summary>The record for <paramref name="facts"/>' WINDOW tier (Huge reads Standard's); Standard where no frame resolved the tiers.</summary>
    public static WinUiPodcastShowLayout Of(in TierFacts facts) => facts.Policy is null ? s_standard : For(facts.Window);

    public static WinUiPodcastShowLayout For(WidthTier window) => window switch
    {
        WidthTier.Compact => s_compact,
        WidthTier.Small => s_small,
        _ => s_standard,
    };
}
