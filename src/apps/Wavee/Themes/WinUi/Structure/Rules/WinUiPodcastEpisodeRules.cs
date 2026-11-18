// ── Themes/WinUi/Structure/Rules/WinUiPodcastEpisodeRules.cs — the WinUI episode page's numbers and keys (podcast pages v2, concept "Episode") ─────────────
//
// The concept's CSS (winui-podcast-v2.html, "episode page"): the hero (a 16:9 still beside the words), the chapter map card, the "Up next" card, the two
// 456-tall cards (Chapters | About | Comments at 400 beside the Transcript) and the rows under them. The per-tier numbers are `Tiers.PodcastEpisode.cs`.

using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The WinUI episode page's fixed numbers and node keys.</summary>
internal static class WinUiPodcastEpisodeRules
{
    // ── keys ──
    public const string PageKey = "pe-page", ScrollKeyPrefix = "pe-scroll:", HeroKey = "pe-hero", MapKey = "pe-map", NextUpKey = "pe-nextup",
        ColsKey = "pe-cols", SectionsKey = "pe-sections", TranscriptKey = "pe-transcript", MoreKey = "pe-more", SimilarKey = "pe-similar", BarKey = "pe-bar";

    // ── the hero ──
    /// <summary>The still's aspect (16:9) and its corner clock and Video tag insets.</summary>
    public const float StillAspect = 16f / 9f, StillTagInset = 8f, StillTagPadX = 6f, StillTagH = 20f;
    /// <summary>The crumb: the show's tiny art, the gap, the chevron.</summary>
    public const float CrumbArt = 20f, CrumbArtRadius = 3f, CrumbGap = 8f, CrumbChevron = 12f;
    /// <summary>The gap between the words' rows and the action row's top margin.</summary>
    public const float MainGap = 6f, ActionTop = 10f, ActionGap = 8f;
    /// <summary>The in-progress mini bar beside the split: its width and the gap over the bar.</summary>
    public const float MiniBarW = 220f, MiniBarCompactW = 120f, BarGap = 4f;
    /// <summary>The WinUI progress bar: a 3-DIP fill on a 1-DIP track.</summary>
    public const float BarFill = 3f, BarTrack = 1f, BarRadius = 1.5f;

    // ── the chapter map ──
    public const float MapPadX = 16f, MapPadTop = 12f, MapPadBottom = 14f, MapGap = 10f, CardGap = 12f;
    /// <summary>The segment band: its height, the gap between segments, a segment's rest and hover height, its floor and radius.</summary>
    public const float SegBand = 20f, SegGap = 3f, SegRest = 8f, SegHover = 12f, SegMin = 6f, SegRadius = 4f;
    /// <summary>A crowded map (a long episode's hundred chapters): from <see cref="SegDenseFrom"/> segments the gap and the floor shrink so every chapter
    /// still fits a Compact row (100 x 2 + 99 x 1 = 299).</summary>
    public const int SegDenseFrom = 40;
    public const float SegGapDense = 1f, SegMinDense = 2f;
    public static float SegGapFor(int count) => count >= SegDenseFrom ? SegGapDense : SegGap;
    public static float SegMinFor(int count) => count >= SegDenseFrom ? SegMinDense : SegMin;

    /// <summary>The thumb on the current segment: its outer and inner discs.</summary>
    public const float Thumb = 18f, ThumbDot = 10f, ThumbHalf = Thumb / 2f, ThumbDotHalf = ThumbDot / 2f;
    /// <summary>The follow button's glyph.</summary>
    public const float FollowGlyph = 16f;
    /// <summary>The middle dot between joined facts.</summary>
    public const string Dot = " · ";
    /// <summary>The thumb's ring stroke and the segments' rest and hover fills (white on the card, light and dark through alpha).</summary>
    public const float ThumbStroke = 1f;

    // ── the "Up next" card ──
    public const float NextArt = 48f, NextArtRadius = 4f, NextPadY = 12f, NextGap = 16f;

    // ── the two cards ──
    public const float ColsAside = 400f, CardsH = 456f, CardsNarrowH = 440f, SelBarPad = 4f, ListBodyPad = 8f;
    /// <summary>A chapter row: the number column, the check column, the row's gap, padding, floor and the current row's accent tick.</summary>
    /// <summary>A chapter row's fixed height (the virtual list's extent): its padding, the name (20), the meta line (16) and the current one's bar.</summary>
    public const float ChRowH = 54f;
    public const float ChNum = 28f, ChCheck = 20f, ChGap = 8f, ChPadX = 8f, ChPadY = 6f, ChMinH = 52f, ChCheckGlyph = 12f,
        ChTickW = 3f, ChTickH = 16f, ChTickRadius = 2f, ChBarTop = 3f;
    /// <summary>Following jumps (no glide) to a current line further than this many viewports away.</summary>
    public const double FollowJumpViewports = 2.0;
    /// <summary>The transcript header and body.</summary>
    public const float TrHeadMinH = 48f, TrHeadPadL = 16f, TrHeadPad = 8f, FindW = 176f, TrPadX = 16f, TrPadBottom = 16f;
    /// <summary>The sticky chapter heading's padding.</summary>
    public const float TrHeadingTop = 10f, TrHeadingBottom = 8f, TrHeadingGap = 10f;
    /// <summary>A transcript line: the time column, the column gap, the text's padding, its type (15/22) and the time's (12/24), the gap over a line, the
    /// current line's accent stroke, and the measured list's estimate per line.</summary>
    public const float TrTimeW = 64f, TrColGap = 8f, TrTextPadX = 8f, TrTextPadY = 4f, TrTextSize = 15f, TrTextLine = 22f, TrTimeLine = 24f,
        TrLineGap = 4f, TrCurrentStroke = 3f, TrLineEstimate = 34f, TrHeadingEstimate = 74f, TrTextMax = 600f;

    // ── the sections under the cards ──
    public const float SectionTop = 28f, SectionBottom = 8f, RowMinH = 64f, RowGap = 12f, RowArt = 48f, GridGap = 12f, GridCardPad = 12f, GridCardArtGap = 8f,
        GridSmallCard = 136f;

    public const float GridDecode = 256f;

    // ── the 48 bar ──
    public const float BarH = 48f, BarCover = 32f, BarCoverRadius = 4f, BarGapX = 12f, BarReveal = 44f;

    // ── the wash: the cover's dark colour at the page top, fading out before the cards ──
    public const float WashH = 420f, WashTop = 0.30f, WashMid = 0.10f, WashMidAt = 0.55f;

    /// <summary>A show tone (0xAARRGGBB, 0 = none) as a colour; null when there is none.</summary>
    public static ColorF? ToneOf(uint argb)
        => argb == 0u ? null : ColorF.FromRgba((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, byte.MaxValue);

    /// <summary>A tab's count, held to at least <see cref="CountRoom"/> figures (U+2007 FIGURE SPACE pads it on the right, invisible): the count's room
    /// is there while it is pending and stays when it lands, so the SelectorBar's items never shift. A known 0 says 0.</summary>
    public static string CountLabel(int count, bool pending)
        => pending ? new string(Figure, CountRoom) : count.ToString(System.Globalization.CultureInfo.InvariantCulture).PadRight(CountRoom, Figure);

    const int CountRoom = 2;
    const char Figure = (char)0x2007;

    /// <summary>The find highlight (the concept's rgba(255,214,10,.3)).</summary>
    public static ColorF FindMark => ColorF.FromRgba(255, 214, 10, 77);
}

/// <summary>The episode page's loading seed: the loaded layout's shape (a title, the meta, twelve chapters, a page of lines, two rows, six cards) for the
/// derived flat skeleton. Figure-space runs measure without ink.</summary>
internal static class WinUiPodcastEpisodeSeed
{
    static string Run(int n) => new((char)0x2007, n);

    static readonly Wavee.Theming.Contracts.PodcastEpisodeVm s_ep = new("", Run(28), Run(60), 0, false, Run(6), 0, Run(6), "", 0f,
        Wavee.Theming.Contracts.PodcastRowState.Unplayed, false, false, false, true, 0, false, null);

    public static Wavee.Theming.Contracts.PodcastEpisodeView View { get; } = Wavee.Theming.Contracts.PodcastEpisodeView.Empty with
    {
        Load = Wavee.Theming.Contracts.PodcastLoad.Ready, Episode = s_ep, ShowTitle = Run(16), Crumb = Run(9), Meta = [Run(11), Run(6), Run(11)],
        PrimaryLabel = Run(6), MapHead = Run(24), MapSub = Run(12), MapTime = Run(7),
        Chapters = [.. Enumerable.Range(0, 12).Select(i => new Wavee.Theming.Contracts.PodcastChapterVm(i, Run(20 + i % 4 * 4), i * 100, i * 100 + 100,
            Run(5), Run(6), 0f, false, false))],
        Transcript = [.. Enumerable.Range(0, 12).Select(i => new Wavee.Theming.Contracts.PodcastLineVm(i * 10, Run(4), Run(40 + i % 3 * 12), i == 0 ? Run(20) : null))],
        TranscriptLoad = Wavee.Theming.Contracts.PodcastLoad.Ready, TranscriptSub = Run(20),
        MoreSub = Run(30), More = [s_ep, s_ep],
        Similar = [.. Enumerable.Range(0, 6).Select(i => new Wavee.Theming.Contracts.PodcastCardVm("seed:" + i, Run(14), Run(10), null))],
    };
}
