// ── Themes/Zune/Structure/Rules/ZunePodcastRules.cs — the numbers of Zune's podcast pages v2 (show, episode, audiobook) and their tier record ─────────
//
// Concept `winui-podcast-v2.html`, theme=zune (CSS `.t-zune`, the views `showZune`, `epZune`, `bookZune`), boards Z-Show, Z-Episode, Z-Album. No chrome
// cards: the 120 light lowercase name on one line bleeding off the right edge under a fade (80 and wrapping at Compact: a tier's size,
// never a size fitted to the name), the pivots on one line (28/36 light, bleeding under a fade, sliding like a Windows Phone pivot), flat square art,
// square bordered words for buttons, the Zune accent for the one primary, state by colour and weight. WVTH003 keeps every number of
// `Views\Podcast\Zune*` here; the views read the record of their WINDOW tier (`ZunePodcastTiers.Of`) and compare no width.
//
//   tier       name          art (show / still)   hero          episode columns          controls
//   Compact    80/84 wraps   128 / 280 at most    stacked       one column               40
//   Small      120/130       216 / 320            art beside    one column               36
//   Standard+  120/130       216 / 320            art beside    380 chapters | transcript 36
//
// The art and the name keep one size from Small up (the concept's 168 at 900 and a 150 name at Huge would step the hero at the 1008 and 1920 edges:
// the list under it would jump; the owner's no-jump rule wins over the concept's step).
//
// Public, so the tier records can be pinned (the assembly has no InternalsVisibleTo).

using FluentGpu.Animation;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The podcast pages' numbers (DIP unless named otherwise).</summary>
public static class ZunePodcastRules
{
    // ── the page ──

    /// <summary>The air under the band before the name, and the page's foot past the last section.</summary>
    public const float PageTop = 0f, PageFoot = 56f;

    /// <summary>The name's fade band at its right edge (it bleeds past the page column to the window's edge).</summary>
    public const float TitleFade = 140f;

    /// <summary>The name's ink alpha (the concept's .92 opacity, as the panorama's).</summary>
    public const float TitleOpacity = 0.92f;

    /// <summary>The air under the name's last line (the descenders) and under the book's subtitle.</summary>
    public const float TitleBottom = 0f, SubBottom = 0f;

    /// <summary>The room the name's box keeps for its descenders under the exact line (the pivots' own air above is that much less).</summary>
    public const float TitleDescent = 6f;

    /// <summary>The pivots: the gap between two words (32 at every tier: the concept's 20 at Compact would step the words at the 640 edge, and the
    /// words bleed under the fade anyway), their row's height (the 40 floor holds), the air above and below, the edge fade's band, the small count's
    /// size and its lift onto the word's cap height.</summary>
    public const float PivotGap = 32f, PivotH = 44f, PivotTop = 0f, PivotBottom = 28f, PivotFade = 64f;
    public const float CountSize = 13f, CountLine = 16f, CountLift = 12f, CountGap = 6f, FilterCountLift = 6f, GroupCountLift = 8f;

    /// <summary>The swap of a pivot's body: the new body fades in where it lands.</summary>
    public static readonly EnterExit BodySwap = new(Opacity: 0f, Active: true);

    // ── the hero ──

    /// <summary>The hero: the gap beside the art, the gap between its lines, the air under it.</summary>
    public const float HeroColGap = 32f, HeroRowGap = 16f, HeroLineGap = 14f, HeroBottom = 36f;

    /// <summary>The still's aspect (16:9) and its corner tags' insets.</summary>
    public const float StillAspect = 16f / 9f, StillTagInset = 6f, StillTagPadX = 6f, StillTagH = 18f, StillTagGlyph = 10f, StillTagSize = 11f;

    /// <summary>The label over the primary ("continue", "new"): 13 in the accent ink.</summary>
    public const float LabelSize = 13f, LabelLine = 16f;

    // ── buttons ──

    /// <summary>A Zune command: square, a 1-DIP ring at this ink alpha (none on the accent), the words 14 inset; the hover wash's alpha.</summary>
    public const float ButtonPadX = 14f, ButtonGlyph = 14f, ButtonGap = 8f, ButtonRingAlpha = 0.28f, ButtonHoverAlpha = 0.08f, ButtonPressedAlpha = 0.14f;
    public const float ButtonTextSize = 14f, ButtonTextLine = 20f;

    /// <summary>The split's chevron half and the dark seam between the halves.</summary>
    public const float ChevronGlyph = 10f, SeamAlpha = 0.25f;

    /// <summary>The commands' gap.</summary>
    public const float CommandGap = 8f;

    // ── the up line beside the primary (the show) ──

    public const float UpGap = 20f, UpTitleSize = 20f, UpTitleLine = 28f, UpBarW = 160f, UpSubGap = 8f, UpLineGap = 4f;

    /// <summary>The 2-DIP Zune progress line and its track's ink alpha.</summary>
    public const float BarH = 2f, BarTrackAlpha = 0.16f;

    // ── the meta line ──

    public const float MetaGap = 6f, MetaStar = 11f;

    // ── the toolbar and the groups (the show) ──

    /// <summary>The toolbar: its height, the filter words (20/28 light) and their gap, the search pill's width (0 at Compact: the glyph).</summary>
    public const float ToolbarH = 56f, FilterSize = 20f, FilterLine = 28f, FilterGap = 24f, SearchW = 208f, ToolGlyph = 16f;

    /// <summary>A group header: its height (the 40 floor for its target) and the word's voice (26/32 light).</summary>
    public const float GroupH = 52f, GroupGlyph = 12f, GroupGap = 8f;

    /// <summary>The jump list: its width, a cell's height, the gap and the inset.</summary>
    public const float JumpW = 320f, JumpCellH = 64f, JumpGap = 4f, JumpPad = 16f;
    public const int JumpCols = 3;

    /// <summary>An episode row: the play column, the vertical padding, the title (18/24 light), the description's lines and the expanded body's air.</summary>
    public const float RowPlay = 36f, RowPlayCompact = 40f, RowPadY = 10f, RowGap = 12f, RowGlyph = 14f, RowTitleSize = 18f, RowTitleLine = 24f;
    public const float RowAction = 36f, RowActionCompact = 40f, RowDescSize = 13f, RowDescLine = 18f, ExpandTop = 8f, ExpandGap = 12f;

    /// <summary>The row's hover wash and the new mark (a square accent dot).</summary>
    public const float RowHoverAlpha = 0.04f, RowPressedAlpha = 0.08f, NewDot = 6f;

    /// <summary>The list's seeded extents (the measured ones replace them on realize) and the prefix parts' recycle pools (past the item kinds).</summary>
    public const float RowEstimate = 64f, RowEstimateTall = 84f, FootEstimate = 64f;
    public const int PrefixType = 16;

    /// <summary>The empty line and the foot's air.</summary>
    public const float EmptyPad = 24f, MoreTop = 24f;

    /// <summary>The about pivot's measure, the facts' label column, the rows' gaps.</summary>
    public const float AboutMax = 720f, FactLabelW = 120f, FactsRowGap = 8f, FactsColGap = 16f, SectionGap = 32f, SectionHeadGap = 12f;
    public const float BodySize = 15f, BodyLine = 22f;

    /// <summary>The big stars of the rating and its numeral.</summary>
    public const float RatingStar = 16f;

    /// <summary>"More like this": the cards' width and gap.</summary>
    public const float CardW = 176f, CardGap = 24f;

    // ── the episode ──

    /// <summary>The chapter map: the head (20/28 light), the gap, the segments (6 tall, 2 apart, at least 3 wide; 10 while hovered), the band's target
    /// height, the air under it.</summary>
    public const float MapHeadSize = 20f, MapHeadLine = 28f, MapGap = 12f, SegH = 6f, SegHover = 10f, SegGap = 2f, SegMin = 3f, MapBand = 24f, MapBottom = 32f;
    public const float SegTrackAlpha = 0.16f;

    /// <summary>The two columns from Standard: the chapters' 380 and the gap to the transcript.</summary>
    public const float ChaptersW = 380f, ColsGap = 48f, ColsTop = 0f;

    /// <summary>A chapter row: the number column (16/28), the name (18/24 light), the row's floor, its padding.</summary>
    public const float ChNumW = 36f, ChNumSize = 16f, ChNumLine = 28f, ChNameSize = 18f, ChNameLine = 24f, ChMinH = 44f, ChPadY = 6f, ChGap = 8f;
    public const float ChCheck = 24f, ChCheckGlyph = 12f;

    /// <summary>The transcript: its height (it scrolls the lines inside, the house virtual list), the head, the find pill, the lines (16/24), the time
    /// column, the heading's voice (20/28 light).</summary>
    public const float TrH = 520f, TrHeadGap = 12f, FindW = 200f, TrLineSize = 16f, TrLineLine = 24f, TrTimeW = 64f, TrColGap = 16f, TrLineGap = 8f;
    public const float TrLineEstimate = 32f, TrHeadingEstimate = 64f, TrHeadingSize = 20f, TrHeadingLine = 28f, TrHeadingTop = 8f, TrTextMax = 720f;
    public const float FollowGlyph = 14f;

    /// <summary>The find marks' wash (the accent at this alpha).</summary>
    public const float FindMarkAlpha = 0.35f;

    /// <summary>"Next in this show" and "more like this" on the episode: the heading's air.</summary>
    public const float MoreGap = 16f;

    // ── the audiobook ──

    /// <summary>The percentage (72/72 thin), the words beside it (16), the gap, the bar's cap.</summary>
    public const float BigSize = 72f, BigLine = 72f, BigWordsSize = 16f, BigWordsLine = 22f, BigGap = 16f, BookBarMax = 520f;
    public const ushort BigWeight = 200, Light = 350, Strong = 600;

    /// <summary>The byline (16, lowercase words, the names in the primary ink).</summary>
    public const float BylineSize = 16f, BylineLine = 22f, BylineGap = 24f;

    /// <summary>The premium line: its ring (the notice ink), padding, gap.</summary>
    public const float PremiumPad = 12f, PremiumGap = 12f;

    /// <summary>The book line: the segments (6 tall), the gap; the line's head.</summary>
    public const float LineGap = 12f;

    /// <summary>The contents: the margin line (2), a row's floor (44), the number (16/28), the name (20/28; matter 16), the leaders (1-DIP dots 1 on 3,
    /// lifted 4 onto the baseline), the "starts at" column.</summary>
    public const float MarginW = 2f, ContentsRowMin = 44f, ContentsNumW = 32f, ContentsNameSize = 20f, ContentsNameLine = 28f, MatterSize = 16f;
    public const float LeaderH = 1f, LeaderDot = 1f, LeaderGapDash = 3f, LeaderMin = 20f, LeaderLift = 4f, StartsW = 72f, ContentsGap = 10f, ContentsPadL = 12f;
    public const float MarginRestAlpha = 0.08f, MarginDoneAlpha = 0.3f, LeaderAlpha = 0.24f, LeaderDoneAlpha = 0.12f;

    /// <summary>The sample row's button.</summary>
    public const float SampleGap = 12f;

    /// <summary>The about text's lines before "read more".</summary>
    public const int AboutLines = 8;
}

/// <summary>The podcast pages in one WINDOW tier.</summary>
public sealed record ZunePodcastLayout
{
    public required WidthTier Tier { get; init; }

    /// <summary>The name: its size and line, and whether it wraps (Compact) or runs off the edge under the fade.</summary>
    public required float TitleSize { get; init; }
    public required float TitleLine { get; init; }
    public required bool TitleWraps { get; init; }

    /// <summary>The hero: stacked (art over the words) or the art beside them.</summary>
    public required bool Stacked { get; init; }

    /// <summary>The show's and the book's square art, the episode's still width (0: the column's, capped at <see cref="StillMax"/>).</summary>
    public required float Art { get; init; }
    public required float Still { get; init; }
    public required float StillMax { get; init; }

    /// <summary>A control's height (36; 40 at Compact).</summary>
    public required float Target { get; init; }

    /// <summary>The pivots' gap.</summary>
    public required float PivotGap { get; init; }

    /// <summary>Rows carry one line of description (not at Compact).</summary>
    public required bool RowDescription { get; init; }

    /// <summary>The toolbar's search: the pill (false: a glyph that opens it over the filters).</summary>
    public required bool SearchPill { get; init; }

    /// <summary>The episode's chapters beside the transcript (Standard and up); else one column, the transcript a pivot.</summary>
    public required bool TwoColumns { get; init; }

    /// <summary>The primary fills the row (Compact).</summary>
    public required bool PrimaryFills { get; init; }

    /// <summary>A command shows its word (save, mark as played, share); else its glyph alone.</summary>
    public required bool Words { get; init; }
}

/// <summary>The podcast pages' tier records.</summary>
public static class ZunePodcastTiers
{
    static readonly ZunePodcastLayout s_compact = new()
    {
        Tier = WidthTier.Compact, TitleSize = 80f, TitleLine = 84f, TitleWraps = true, Stacked = true, Art = 128f, Still = 0f, StillMax = 280f,
        Target = 40f, PivotGap = ZunePodcastRules.PivotGap, RowDescription = false, SearchPill = false, TwoColumns = false, PrimaryFills = true,
        Words = false,
    };
    static readonly ZunePodcastLayout s_small = new()
    {
        Tier = WidthTier.Small, TitleSize = 120f, TitleLine = 130f, TitleWraps = false, Stacked = false, Art = 216f, Still = 320f, StillMax = 320f,
        Target = 36f, PivotGap = ZunePodcastRules.PivotGap, RowDescription = true, SearchPill = true, TwoColumns = false, PrimaryFills = false, Words = true,
    };
    static readonly ZunePodcastLayout s_standard = s_small with { Tier = WidthTier.Standard, TwoColumns = true };
    static readonly ZunePodcastLayout s_huge = s_standard with { Tier = WidthTier.Huge };

    /// <summary>The record for <paramref name="facts"/>' WINDOW tier; Standard's where no frame resolved the tiers.</summary>
    public static ZunePodcastLayout Of(in TierFacts facts) => facts.Policy is null ? s_standard : For(facts.Window);

    public static ZunePodcastLayout For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => s_compact,
        WidthTier.Small => s_small,
        WidthTier.Standard => s_standard,
        _ => s_huge,
    };
}

/// <summary>The loading seeds (the loaded layout's shape for the derived flat skeleton; figure-space runs measure without ink).</summary>
internal static class ZunePodcastSeed
{
    static string Run(int n) => new((char)0x2007, n);

    static readonly Wavee.Theming.Contracts.PodcastEpisodeVm s_ep = new("seed", Run(28), Run(60), 0, false, Run(6), 0, Run(6), "", 0f,
        Wavee.Theming.Contracts.PodcastRowState.Unplayed, false, false, false, true, 0, false, null);

    /// <summary>The episode page's seed: a title, the meta, twelve chapters, a page of lines, two rows.</summary>
    public static Wavee.Theming.Contracts.PodcastEpisodeView Episode { get; } = Wavee.Theming.Contracts.PodcastEpisodeView.Empty with
    {
        Load = Wavee.Theming.Contracts.PodcastLoad.Ready, Episode = s_ep, ShowTitle = Run(16), Crumb = Run(9), Meta = [Run(11), Run(6), Run(11)],
        PrimaryLabel = Run(6), MapHead = Run(24), MapSub = Run(12), MapTime = Run(7),
        Chapters = [.. Enumerable.Range(0, 12).Select(i => new Wavee.Theming.Contracts.PodcastChapterVm(i, Run(20 + i % 4 * 4), i * 100, i * 100 + 100,
            Run(5), Run(6), 0f, false, false))],
        Transcript = [.. Enumerable.Range(0, 12).Select(i => new Wavee.Theming.Contracts.PodcastLineVm(i * 10, Run(4), Run(40 + i % 3 * 12), i == 0 ? Run(20) : null))],
        TranscriptLoad = Wavee.Theming.Contracts.PodcastLoad.Ready, TranscriptSub = Run(20),
        MoreSub = Run(30), More = [s_ep with { Id = "seed:1" }, s_ep with { Id = "seed:2" }],
    };
}
