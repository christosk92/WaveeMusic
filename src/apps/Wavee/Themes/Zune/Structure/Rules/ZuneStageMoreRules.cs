// ── Themes/Zune/Structure/Rules/ZuneStageMoreRules.cs — the numbers of the rest of Zune's stage, its video and its lyrics view (P5 Z4c) ──
//
// Read off Z-NowPlaying-Artist (the hero photo's scrims, the huge name, the "about the artist" block), Z-Queue-Page (the Up next column),
// Z-Lyrics (the reading column and the resync word), Z-Video (the fullscreen chrome and the placement menu) and Z-Mini (the floating
// mini player, the docked video), all at the 1440 x 900 Standard frame. The stage's own frame is Z4b's (`StageGeometry*.cs`); these are
// the numbers of the views that sit inside it. Production motion (the hero's Ken Burns, the pane's rise, the lane's row enter and exit)
// stays as the stage has it. Literal numbers are allowed here (WVTH003 exempts `Rules\**`); the classes are public so `Wavee.Tests` and the
// mounted tests read them (the assembly has no InternalsVisibleTo).

using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The Artist layout's hero photo and its huge name (Z-NowPlaying-Artist).</summary>
public static class ZuneStageHeroRules
{
    // ── the scrims over the photo (the stage veil, so they invert with the arm) ───────────────────────────────────────

    /// <summary>The board's vertical wash: 0.35 at the top (the top bar reads), clear by 25 %, 0.3 at 60 %, the foot's alpha (the "Image
    /// dimming" setting, the core's `FootAlpha`) at the bottom.</summary>
    public const float TopA = 0.35f, TopClearStop = 0.25f, MidStop = 0.6f, MidA = 0.3f;

    /// <summary>The lyrics-over-the-image arm: the left veil the lyrics pane reads on (0.92 at the edge, 0.78 at 38 %, 0.15 at 72 %, clear)
    /// and the foot's wash (clear to 70 %, 0.8 at the foot).</summary>
    public const float LyricsLeftA = 0.92f, LyricsLeftMidStop = 0.38f, LyricsLeftMidA = 0.78f, LyricsLeftFarStop = 0.72f, LyricsLeftFarA = 0.15f,
        LyricsFootClearStop = 0.70f, LyricsFootA = 0.80f;

    // ── the name ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The board's name is 300 on a 900 stage (a third of its height); the stage's room above the identity row is smaller, so the name
    /// is a quarter of the stage's height, between 72 and 300.</summary>
    public const float NameFrac = 0.25f, NameMin = 72f, NameMax = 300f;

    /// <summary>The line height is the size (the board's 300 / 300), the tracking -0.02 em (CharacterSpacing is 1/1000 em), the weight 300,
    /// the ink 0.92 white (the stage's ink at this alpha).</summary>
    public const float NameLineFrac = 1f, NameTracking = -20f, NameInkA = 0.92f;

    public const ushort NameWeight = 300;

    /// <summary>The name's size for a stage <paramref name="stageH"/> tall.</summary>
    public static float NameSize(float stageH) => Math.Clamp(MathF.Round(NameFrac * stageH), NameMin, NameMax);
}

/// <summary>The Artist pane ("about the artist", Z-NowPlaying-Artist, and the sections no board draws, built from Zune's parts).</summary>
public static class ZuneStageArtistRules
{
    /// <summary>The section heading (20/28, 600, the display face, the ink at 0.85) and the bio (14/21 in the secondary ink).</summary>
    public const float SectionTitleSize = 20f, SectionTitleLine = 28f, SectionTitleInkA = 0.85f, BioSize = 14f, BioLine = 21f;

    public const ushort SectionTitleWeight = 600, ActionWeight = 400, NameWeight = 300, CreditNameWeight = 600;

    /// <summary>The header: the name 40/48 at 300 (the stage's title voice at 40), the meta line 14/20, the tour line.</summary>
    public const float NameSize = 40f, NameLine = 48f, NameTracking = -12f, MetaSize = 14f, MetaLine = 20f, TourEyebrowSize = 12f, TourHeadlineSize = 13f,
        TourHeadlineLine = 18f;

    /// <summary>A word action ("follow", "go to artist"): 13/18, 20 apart, a 36 target (40 at Compact: the tier's).</summary>
    public const float ActionSize = 13f, ActionLine = 18f, ActionGap = 20f;

    /// <summary>"read more" / "show less": 13/18.</summary>
    public const float ReadMoreSize = 13f, ReadMoreLine = 18f;

    /// <summary>The gallery strip's tile gap and the verified badge's gap from the name.</summary>
    public const float GalleryGap = 8f, VerifiedGap = 8f;

    /// <summary>"This song": a credit row 36 tall, 15/22 (the name at 600, the roles after a dot).</summary>
    public const float CreditSize = 15f, CreditLine = 22f, CreditRowMinH = 36f;

    /// <summary>"Popular": the Queue board's number column in the display face (22/28 at 300), the 40 art, the 15/22 title, the 12/16 plays, the
    /// 13/18 duration, the 52 row.</summary>
    public const float PopularNumberW = 36f, PopularNumberSize = 22f, PopularNumberLine = 28f, PopularArt = 40f, PopularTitleSize = 15f,
        PopularTitleLine = 22f, PopularPlaysSize = 12f, PopularPlaysLine = 16f, PopularDurationSize = 13f, PopularDurationLine = 18f, PopularRowH = 52f,
        ExplicitBadge = 14f;

    public const ushort PopularNumberWeight = 300;

    /// <summary>"Where people listen": the board's 2-px line, in the accent, over the plate rung.</summary>
    public const float CityBar = 2f, CityNameSize = 15f, CityNameLine = 22f, CityCountSize = 13f, CityCountLine = 18f;

    /// <summary>"Fans also like": the avatars, their names.</summary>
    public const float FanAvatar = 72f, FanNameSize = 12f, FanNameLine = 16f;

    /// <summary>The section's gap under it and between the heading and its body.</summary>
    public const float SectionGap = 12f, ColumnGap = 40f;
}

/// <summary>The Up next column (Z-Queue-Page). Every metric is multiplied by the stage's queue scale and rounded as rendered: the lane's drag
/// maths needs each slot's height exact.</summary>
public static class ZuneStageUpNextRules
{
    /// <summary>The row: 60 tall, the number column 48 (the display face at 22, 300, the tertiary ink), the 44 art, the time 56 at the right,
    /// the remove target 40; 12 between the columns.</summary>
    public const float RowH = 60f, ArtEdge = 44f, NumberW = 48f, TimeW = 56f, RemoveW = 40f, ColGap = 12f, NumberPadL = 4f;

    public const float NumberSize = 22f, NumberLine = 28f, TitleSize = 17f, TitleLine = 22f, ArtistSize = 13f, ArtistLine = 18f, TimeSize = 13f, TimeLine = 18f;

    public const ushort NumberWeight = 300, TitleWeight = 400;

    /// <summary>The remove glyph, the explicit badge, the cover's decode size.</summary>
    public const float RemoveGlyph = 13f, ExplicitBadge = 14f, ArtDecode = 96f;

    /// <summary>A section's heading slot: 48 tall, the heading 26/32 at 300 (the display face), its sub line and its word 13/18, 12 apart.</summary>
    public const float HeaderH = 48f, HeadingSize = 26f, HeadingLine = 32f, HeadingGap = 12f, SubSize = 13f, SubLine = 18f;

    public const ushort HeadingWeight = 300;

    /// <summary>The toggle row above the sections: 32 tall, the "autoplay" word 13/18.</summary>
    public const float ToggleRowH = 32f, ToggleSize = 13f, ToggleLine = 18f;

    /// <summary>"Show more": 40 tall, 12/16.</summary>
    public const float MoreRowH = 40f, MoreSize = 12f, MoreLine = 16f, MoreGlyph = 12f;

    /// <summary>The autoplay rows dim to this.</summary>
    public const float AutoplayDim = 0.68f;

    /// <summary>The empty sentence.</summary>
    public const float EmptySize = 14f, EmptyLine = 20f, EmptyTop = 24f;

    /// <summary>A row rises in 6 and leaves upward 4 (the queue's production recipe).</summary>
    public const float RowEnterDy = 6f, RowExitDy = -4f;
}

/// <summary>The lyrics view's overlays (Z-Lyrics): the reading column, the resync word and its ring, the video note, the breathing dots.</summary>
public static class ZuneStageLyricsRules
{
    /// <summary>The stage's reading column: capped at 1000 times the type scale, its gutter spelled as trailing padding.</summary>
    public const float StageColumnMaxW = 1000f, StageColumnGutter = 24f;

    /// <summary>The video note (12, 600) and the resync word (13/18 in the accent, 600), the ring (18), the gaps and pad of the flat plate.</summary>
    public const float VideoNoteSize = 12f, ResyncLabelSize = 13f, ResyncLabelLine = 18f, ResyncRing = 18f, ResyncGap = 8f, ResyncPadX = 12f,
        ResyncPadY = 6f, ResyncRiseDy = 4f;

    public const ushort NoteWeight = 600, ResyncWeight = 600;

    /// <summary>The dots' entrance scale and corner fraction (a dot is round: Zune's one round part on the lyrics surface).</summary>
    public const float DotsEnterScale = 0.72f, DotRadiusFraction = 0.5f;
}

/// <summary>What the video surfaces draw in one window tier: the strips and their targets (Z-Video, Z-Mini; Compact has 40 targets).</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="WordH">A word action's target height (the fullscreen bar's and the docked strip's: 36, 40 at Compact).</param>
/// <param name="StripH">The floating mini player's top strip (32, 40 at Compact).</param>
/// <param name="GlyphBox">A glyph button of the mini player (28, 40 at Compact).</param>
/// <param name="ChromeAtRest">The hover chrome shows at rest (there is no hover on a Compact window's touch).</param>
/// <param name="BarPadL">The fullscreen bar's left padding (the frame's gutter: 16 / 24 / 48 / 48 / 64).</param>
/// <param name="BarPadR">The fullscreen bar's right padding.</param>
/// <param name="BarH">The fullscreen bar's height (Z-Video 48; Z-Video-Huge 56; 72 at Huge 4K, the stage's head).</param>
/// <param name="BarGap">The gap between the fullscreen bar's parts.</param>
/// <param name="LabelSize">The bar's label and words: type size (13; 14 at Huge; 17 at Huge 4K).</param>
/// <param name="LabelLine">Their line height.</param>
/// <param name="TitleSize">The track's title in the bar.</param>
/// <param name="TitleLine">Its line height.</param>
public sealed record ZuneStageVideoLayout(WidthTier Tier, float WordH, float StripH, float GlyphBox, bool ChromeAtRest, float BarPadL, float BarPadR,
    float BarH, float BarGap, float LabelSize, float LabelLine, float TitleSize, float TitleLine);

/// <summary>The video surfaces' numbers and their per-window-tier record.</summary>
public static class ZuneStageVideoRules
{
    /// <summary>The fullscreen bar: the gap between the title and its "official video · artist" run, and between the words at the right.</summary>
    public const float TitleGap = 8f, WordGap = 24f;

    private static readonly ZuneStageVideoLayout[] s_tiers =
    [
        // Z-Video-Compact: 40 targets, the chrome at rest (touch), the 16 gutter
        new(WidthTier.Compact, WordH: 40f, StripH: 40f, GlyphBox: 40f, ChromeAtRest: true, BarPadL: 16f, BarPadR: 4f,
            BarH: 48f, BarGap: 4f, LabelSize: 13f, LabelLine: 18f, TitleSize: 15f, TitleLine: 20f),
        // Z-Video-Small
        new(WidthTier.Small, WordH: 36f, StripH: 32f, GlyphBox: 28f, ChromeAtRest: false, BarPadL: 24f, BarPadR: 12f,
            BarH: 48f, BarGap: 20f, LabelSize: 13f, LabelLine: 18f, TitleSize: 15f, TitleLine: 20f),
        // Z-Video, Z-Mini (Standard)
        new(WidthTier.Standard, WordH: 36f, StripH: 32f, GlyphBox: 28f, ChromeAtRest: false, BarPadL: 48f, BarPadR: 24f,
            BarH: 48f, BarGap: 24f, LabelSize: 13f, LabelLine: 18f, TitleSize: 15f, TitleLine: 20f),
        // Z-Video-Huge: the 56 bar, 64 / 24, 14 type, 44 targets
        new(WidthTier.Huge, WordH: 44f, StripH: 32f, GlyphBox: 28f, ChromeAtRest: false, BarPadL: 64f, BarPadR: 24f,
            BarH: 56f, BarGap: 28f, LabelSize: 14f, LabelLine: 20f, TitleSize: 17f, TitleLine: 22f),
        // Huge 4K (no video board: the stage's Huge 4K head): the 72 bar, 96 / 32, 17 type, 56 targets
        new(WidthTier.Huge4K, WordH: 56f, StripH: 40f, GlyphBox: 36f, ChromeAtRest: false, BarPadL: 96f, BarPadR: 32f,
            BarH: 72f, BarGap: 36f, LabelSize: 17f, LabelLine: 22f, TitleSize: 20f, TitleLine: 26f),
    ];

    /// <summary>The record for window tier <paramref name="window"/>.</summary>
    public static ZuneStageVideoLayout Of(WidthTier window)
    {
        var tier = s_tiers[Math.Clamp((int)window, 0, s_tiers.Length - 1)];
        return Wavee.Theming.InspectSwitches.HoverChromeAtRest && !tier.ChromeAtRest ? tier with { ChromeAtRest = true } : tier;   // `mount chrome=shown`: an agent has no pointer
    }

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneStageVideoLayout OfFacts(in TierFacts facts) => Of(facts.Policy is null ? WidthTier.Standard : facts.Window);

    // ── the poster and its notice ────────────────────────────────────────────────────────────────────────────────────────

    public const float PosterOpacity = 0.4f, PosterDecodePx = 256f;

    public const float NoticeRing = 20f, NoticeText = 12f, NoticeGlyph = 18f, NoticeGap = 8f;

    public const ushort NoticeWeight = 600;

    // ── the strips ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The fullscreen bar: 48 tall (Z-Video), its parts 24 apart, the label 13/18, the title 15, the words 13/18.</summary>
    public const float FullscreenBarH = 48f, BarGap = 24f, LabelSize = 13f, LabelLine = 18f, TitleSize = 15f, TitleLine = 20f, WordSize = 13f, WordLine = 18f;

    public const ushort TitleWeight = 400;

    /// <summary>The mini player's glyph (13) and its hover and pressed plates (the on-media ink at these alphas), the grip mark.</summary>
    public const float GlyphSize = 13f, GlyphHoverAlpha = 0.14f, GlyphPressedAlpha = 0.22f, GripMark = 8f, GripMarkInset = 2f;

    /// <summary>The docked strip's gap between its words.</summary>
    public const float StripGap = 16f, StripPadX = 12f;

    /// <summary>The pop-out's band: the inset beside the OS resize strip.</summary>
    public const float BandInset = 12f;

    /// <summary>The watch page's play disc.</summary>
    public const float PlayDisc = 64f;
}
