// ── Themes/Zune/Structure/Rules/ZuneStageMoreRules.cs — the numbers of the rest of Zune's stage, its video and its lyrics view (P5 Z4c) ──
//
// Read off Z-NowPlaying-Artist (the hero photo's scrims, the huge name, the "about the artist" block), Z-Queue-Page (the Up next column),
// Z-Lyrics (the reading column and the resync word), Z-Video (the fullscreen chrome and the placement menu) and Z-Mini (the floating
// mini player, the docked video), all at the 1440 x 900 Standard frame. The stage's own frame is Z4b's (`StageGeometry*.cs`); these are
// the numbers of the views that sit inside it. Production motion (the hero's Ken Burns, the pane's rise, the lane's row enter and exit)
// stays as the stage has it. Literal numbers are allowed here (WVTH003 exempts `Rules\**`); the classes are public so `Wavee.Tests` and the
// mounted tests read them (the assembly has no InternalsVisibleTo).

using FluentGpu.Foundation;
using FluentGpu.Theming;
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

    // ── the board's arrangement (wide stages, the lyrics off: `ZuneStageForm.Artist`) ───────────────────────────────────────
    // Z-NowPlaying-Artist at 1440 x 900: the identity in a flat plate under the head (48, 72, 420 x 120, padding 16, the 88 cover 16 from the
    // words), "about the artist" at the right gutter (88 from the top, 340 wide), and the name across the foot at 300, its line box 600..900
    // cut at the plate's top (788): 188 of its 300 show. Every length scales with the core's Artist unit (H / 900, 1 to 1.5).

    /// <summary>The plate: its gap under the head, its width, padding, cover and the cover's gap to the words; the veil it lays (0.6).</summary>
    public const float PlateTop = 24f, PlateW = 420f, PlatePad = 16f, PlateArt = 88f, PlateArtGap = 16f, PlateShade = 0.6f;

    /// <summary>The plate's words: the title 22/28 at 300 in the display face, the artist line 14/20, 4 apart.</summary>
    public const float PlateTitleSize = 22f, PlateTitleLine = 28f, PlateMetaSize = 14f, PlateMetaLine = 20f, PlateTextGap = 4f;

    /// <summary>"about the artist": its top under the head, its width, the most bio lines it shows (the board's three), and the room the block
    /// takes (the heading, the three lines, the actions) that the name must clear.</summary>
    public const float AboutTop = 40f, AboutW = 340f, AboutRoomH = 168f;

    public const int AboutLines = 3;

    /// <summary>The board's name: a third of the stage's height (300 at 900, never above 300); its line box shows this share of the size above the
    /// cut (188 / 300); it fades out over this share of the size into the cut, so the cut reads as the board's sink, never as a hard line.</summary>
    public const float BoardNameFrac = 1f / 3f, BoardNameShowEm = 188f / 300f, BoardNameFadeEm = 0.2f;

    /// <summary>The board name's size for a stage <paramref name="stageH"/> tall.</summary>
    public static float BoardNameSize(float stageH) => Math.Clamp(MathF.Round(BoardNameFrac * stageH), NameMin, NameMax);

    // ── the lyrics over the photo: the adaptive boost (audit B3bc) ─────────────────────────────────────────────────────────────
    // The left veil (LyricsLeft*) is tuned for an average photo; over a bright one the sung line in the accent read 2.3:1. A black boost over the
    // lyrics pane is SOLVED per photograph (MediaScrim, the band's V14 recipe): the least darkening that brings the accent to large text's ratio
    // against the photo's bright tail under each slice of the pane, after the left veil there. It fades out past the pane, so the photo stays a photo.

    /// <summary>The slices the pane is judged in (the veil thins to the right), the boost's ceiling and its fade past the pane (a share of W).</summary>
    public const int BoostSlices = 6;

    public const float BoostCeiling = 0.85f, BoostFadeFrac = 0.16f;

    /// <summary>The left veil's alpha at <paramref name="u"/> (0 the left edge, 1 the right), as its gradient draws it.</summary>
    public static float LyricsLeftAt(float u)
    {
        if (u <= LyricsLeftMidStop) return LyricsLeftA + (LyricsLeftMidA - LyricsLeftA) * (u / LyricsLeftMidStop);
        if (u <= LyricsLeftFarStop) return LyricsLeftMidA + (LyricsLeftFarA - LyricsLeftMidA) * ((u - LyricsLeftMidStop) / (LyricsLeftFarStop - LyricsLeftMidStop));
        return LyricsLeftFarA * (1f - Math.Clamp((u - LyricsLeftFarStop) / (1f - LyricsLeftFarStop), 0f, 1f));
    }

    /// <summary>The boost's opacity (0..<see cref="BoostCeiling"/>) over the pane <paramref name="left"/>..<paramref name="right"/> x
    /// <paramref name="top"/>..<paramref name="bottom"/> of a <paramref name="w"/> x <paramref name="h"/> stage whose photo (measured as
    /// <paramref name="map"/>) is cover-fitted at <paramref name="focusX"/>, so <paramref name="ink"/> reaches <paramref name="need"/> (with the solve's headroom).</summary>
    public static float LyricsBoost(MediaScrim.LumaMap map, float w, float h, float focusX, float left, float top, float right, float bottom, ColorF ink, float need)
        => Boost(map, w, h, focusX, left, top, right, bottom, ink, need, leftVeil: true);

    /// <summary>The darkening a region of words over the photo needs: the same solve over the region's rect for <paramref name="ink"/> at
    /// <paramref name="need"/>, over a uniform <paramref name="floor"/> veil already under it ("about the artist" and the head: their dimmest words in
    /// the secondary ink, the follow glyph in the accent, body text at 4.5 : 1, with no veil judged under them (the board's top wash is clear by the
    /// block, and thin over the head); the name, large text at 3 : 1; the identity's plate, its words over the plate's own veil).</summary>
    public static float RegionBoost(MediaScrim.LumaMap map, float w, float h, float focusX, float left, float top, float right, float bottom, ColorF ink, float need,
        float floor = 0f)
        => Boost(map, w, h, focusX, left, top, right, bottom, ink, need, leftVeil: false, floor);

    /// <summary>How far the hero's Ken Burns can carry the photo under a fixed point of an <paramref name="extent"/>-long axis: half the zoom's growth
    /// (<see cref="Stage.HeroRules.ScaleTo"/>) plus the pan's travel (<see cref="KenBurnsPan"/>, the core's `HeroRules.Pan`: 2.5 % of the width, at most
    /// 1 % of the height; the larger is taken on both axes).</summary>
    public static float KenBurnsReach(float extent) => ((Stage.HeroRules.ScaleTo - 1f) / 2f + KenBurnsPan) * extent;

    public const float KenBurnsPan = 0.025f;

    /// <summary>A region veil feathers over this much on its inner edges (a share of the gutter).</summary>
    public const float AboutVeilFeather = 3f;

    static float Boost(MediaScrim.LumaMap map, float w, float h, float focusX, float left, float top, float right, float bottom, ColorF ink, float need, bool leftVeil,
        float floor = 0f)
    {
        need *= MediaScrim.Headroom;
        // the photo drifts under the words (the hero's Ken Burns: up to 10 % zoom about the centre and the pan's travel): judge every part of it
        // that can pass under them, so the solve holds through the whole loop
        float reachX = KenBurnsReach(w), reachY = KenBurnsReach(h);
        left -= reachX; right += reachX; top -= reachY; bottom += reachY;
        float boost = 0f;
        for (int i = 0; i < BoostSlices; i++)
        {
            float x0 = left + (right - left) * i / BoostSlices, x1 = left + (right - left) * (i + 1) / BoostSlices;
            float grey = MediaScrim.GreyAt(map, CoverUv(map.Aspect, w, h, focusX, x0, top, x1, bottom), MediaScrim.Percentile);
            float under = grey * (1f - (leftVeil ? LyricsLeftAt(x1 / MathF.Max(1f, w)) : floor));   // the left veil is thinnest at the slice's right edge
            if (MediaScrim.Contrast(ink, under) >= need) continue;
            float lo = 0f, hi = 1f;
            for (int k = 0; k < 20; k++)
            {
                float mid = (lo + hi) / 2f;
                if (MediaScrim.Contrast(ink, under * (1f - mid)) >= need) hi = mid; else lo = mid;
            }
            boost = MathF.Max(boost, hi);
        }
        return MathF.Min(boost, BoostCeiling);
    }

    /// <summary>The image-space rectangle a box region shows of a photo cover-fitted into the stage with its crop at <paramref name="focusX"/>
    /// (0 left, 1 right) and centred vertically, as the hero's image draws it.</summary>
    static (float U0, float V0, float U1, float V1) CoverUv(float imageAspect, float w, float h, float focusX, float left, float top, float right, float bottom)
    {
        float boxAspect = w / MathF.Max(1f, h);
        float su = 1f, sv = 1f;
        if (imageAspect > boxAspect) su = boxAspect / imageAspect;
        else sv = imageAspect / boxAspect;
        float u0 = (1f - su) * Math.Clamp(focusX, 0f, 1f), v0 = (1f - sv) / 2f;
        return (u0 + su * Math.Clamp(left / MathF.Max(1f, w), 0f, 1f), v0 + sv * Math.Clamp(top / MathF.Max(1f, h), 0f, 1f),
                u0 + su * Math.Clamp(right / MathF.Max(1f, w), 0f, 1f), v0 + sv * Math.Clamp(bottom / MathF.Max(1f, h), 0f, 1f));
    }
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
    /// <summary>The window tier's row, heading and column metrics (Z-Queue-Page, -Small, -Huge, -Compact). Standard's are the constants below.</summary>
    public static ZuneStageQueueRow Of(WidthTier window) => window switch
    {
        WidthTier.Compact => s_compact,
        WidthTier.Small => s_small,
        WidthTier.Huge => s_huge,
        WidthTier.Huge4K => s_huge4K,
        _ => s_standard,
    };

    /// <summary>The row in the Up next column docked beside the lyrics (Z-Lyrics-Huge: the cover, the title over the artists, the duration; no
    /// position and no album column, which the Queue page's full width carries and a 440 column cannot).</summary>
    public static ZuneStageQueueRow Docked(ZuneStageQueueRow row) => row with { NumberW = 0f, NumberPadL = 0f, Album = false };

    static readonly ZuneStageQueueRow s_standard = new(RowH: 60f, ArtEdge: 44f, NumberW: 48f, NumberPadL: 4f, ColGap: 12f, TimeW: 56f, RemoveW: 40f, NumberSize: 22f, NumberLine: 28f,
        TitleSize: 17f, TitleLine: 22f, ArtistSize: 13f, ArtistLine: 18f, TimeSize: 13f, TimeLine: 18f, HeaderH: 48f, HeadingSize: 26f, HeadingLine: 32f, Album: false, SidePanelW: 0f, ModeChips: false);

    static readonly ZuneStageQueueRow s_small = new(52f, 40f, 40f, 4f, 8f, 44f, 32f, 22f, 28f, 15f, 20f, 12f, 16f, 12f, 16f, 36f, 22f, 28f, false, 0f, false);

    static readonly ZuneStageQueueRow s_compact = new(56f, 44f, 0f, 0f, 12f, 0f, 40f, 22f, 28f, 15f, 20f, 13f, 18f, 13f, 18f, 48f, 22f, 28f, false, 0f, true);

    static readonly ZuneStageQueueRow s_huge = new(64f, 52f, 56f, 8f, 12f, 72f, 56f, 22f, 28f, 17f, 22f, 13f, 18f, 13f, 18f, 56f, 40f, 48f, true, 440f, false);

    static readonly ZuneStageQueueRow s_huge4K = new(96f, 78f, 84f, 12f, 18f, 108f, 84f, 33f, 42f, 26f, 33f, 20f, 27f, 20f, 27f, 84f, 60f, 72f, true, 660f, false);

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
    public const float ToggleRowH = 32f, ToggleSize = 13f, ToggleLine = 18f, ModeGap = 16f, ChipPadX = 16f;

    /// <summary>Huge side panel: 64 from the list, its heading 26/32, the sentence 14/21, the switch 15/20.</summary>
    public const float SideGap = 64f, SideHeadingSize = 26f, SideHeadingLine = 32f, SideBodySize = 14f, SideBodyLine = 21f, SideSwitchSize = 15f, SideSwitchLine = 20f;

    /// <summary>Huge's "played before this" (Z-Queue-Page-Huge): its heading 26/32 on a 56 slot, six rows 64 tall at 0.7 (the 52 cover in a 64 column, the title 15, the artists
    /// and the "4 min ago" word 13, the word right-aligned in a 56 column that wraps to two lines), and 40 above the autoplay heading. The 4K tier scales every number by 1.5.</summary>
    public const float HistoryHeadH = 56f, HistoryRowH = 64f, HistoryArtCol = 64f, HistoryArt = 52f, HistoryTitleSize = 15f, HistoryTitleLine = 20f, HistoryMetaSize = 13f,
        HistoryMetaLine = 18f, HistoryWhenW = 56f, HistoryDim = 0.7f, HistorySectionGap = 40f;

    /// <summary>How many history rows the panel draws (the board's six; the props carry up to fifty).</summary>
    public const int HistoryShown = 6;

    /// <summary>The factor the history section's numbers take at <paramref name="window"/> (the 4K tier's rows are 96 / 64).</summary>
    public static float HistoryScale(WidthTier window) => window == WidthTier.Huge4K ? 1.5f : 1f;

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
    float BarH, float BarGap, float LabelSize, float LabelLine, float TitleSize, float TitleLine, bool LabelShown = true, bool TitleCentered = true,
    bool PageForm = false);

/// <summary>The video surfaces' numbers and their per-window-tier record.</summary>
public static class ZuneStageVideoRules
{
    /// <summary>The fullscreen bar: the gap between the title and its "official video · artist" run, and between the words at the right.</summary>
    public const float TitleGap = 8f, WordGap = 24f;

    // ── the Compact page form (Z-Video-Compact) ──

    /// <summary>The picture's aspect under the bar (the boards' 16:9 at the window's width).</summary>
    public const float PageAspect = 9f / 16f;

    /// <summary>The page's inset, the air above the title, under the subtitle and above the list's heading, and the gap between the list's rows.</summary>
    public const float PagePad = 16f, PageTitleTop = 16f, PageSubGap = 2f, PageBlockGap = 16f;

    /// <summary>The page's transport block: the strip's seek row, its button row and its padding (ZuneOnMediaLayout).</summary>
    public static float PageTransportH => ZuneOnMediaLayout.SeekRowH + ZuneOnMediaLayout.GlyphBox + 2f * ZuneOnMediaLayout.Pad;

    /// <summary>The page's list rows: their height (the Compact 40 target plus air) and the radio dot's ring.</summary>
    public const float PageRowH = 48f, PageDot = 8f, PageDotGap = 16f;

    /// <summary>The bar's collapse chevron (the page form's way out, "⌄ video").</summary>
    public const float PageChevron = 16f;

    private static readonly ZuneStageVideoLayout[] s_tiers =
    [
        // Z-Video-Compact: 40 targets, the chrome at rest (touch), the 16 gutter
        // the PAGE form: the 16:9 picture under the bar, then the title, the transport and the placement list (no overlay over the picture)
        new(WidthTier.Compact, WordH: 40f, StripH: 40f, GlyphBox: 40f, ChromeAtRest: true, BarPadL: 16f, BarPadR: 4f,
            BarH: 48f, BarGap: 4f, LabelSize: 13f, LabelLine: 18f, TitleSize: 15f, TitleLine: 20f, PageForm: true),
        // Z-Video-Small
        new(WidthTier.Small, WordH: 36f, StripH: 32f, GlyphBox: 28f, ChromeAtRest: false, BarPadL: 24f, BarPadR: 12f,
            BarH: 48f, BarGap: 20f, LabelSize: 13f, LabelLine: 18f, TitleSize: 15f, TitleLine: 20f, LabelShown: false, TitleCentered: false),   // the title leads the bar, no "video · full screen"
        // Z-Video, Z-Mini (Standard)
        new(WidthTier.Standard, WordH: 36f, StripH: 32f, GlyphBox: 28f, ChromeAtRest: false, BarPadL: 48f, BarPadR: 24f,
            BarH: 48f, BarGap: 24f, LabelSize: 13f, LabelLine: 18f, TitleSize: 15f, TitleLine: 20f),
        // Z-Video-Huge: the 56 bar, 64 / 24, 14 type, 44 targets
        new(WidthTier.Huge, WordH: 44f, StripH: 32f, GlyphBox: 28f, ChromeAtRest: false, BarPadL: 64f, BarPadR: 24f,
            BarH: 56f, BarGap: 28f, LabelSize: 14f, LabelLine: 20f, TitleSize: 17f, TitleLine: 22f, TitleCentered: false),   // the title follows the label
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

/// <summary>One window tier's Up next page metrics: the row (its columns and type), the section heading, the album column and the side panel (Huge).</summary>
public sealed record ZuneStageQueueRow(float RowH, float ArtEdge, float NumberW, float NumberPadL, float ColGap, float TimeW, float RemoveW, float NumberSize, float NumberLine,
    float TitleSize, float TitleLine, float ArtistSize, float ArtistLine, float TimeSize, float TimeLine, float HeaderH, float HeadingSize, float HeadingLine, bool Album, float SidePanelW, bool ModeChips);
