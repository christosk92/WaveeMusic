// ── Themes/WinUi/Structure/Rules/WinUiStageGeometry.Video.cs — the numbers of WinUI's stage in Video mode (Cinema and Theatre) ─────────────
//
// WinUI's video full screen reads as the Windows 11 Media Player app's: the picture edge to edge (Cinema) or beside a play-queue pane (Theatre), and
// the transport a floating COMMAND BAR at the picture's foot. The behaviour is the stage's own (Classic's, the owner's decisions: YouTube's control
// order, the 3 s idle, the shelf, the end card, the audio face); what this file holds is WinUI's look of it, on WinUI's type ramp (Caption 12/16,
// Body 14/20, BodyLarge 18/24, Subtitle 20/28, Title 28/36, TitleLarge 40/52) and radii (4 controls, 8 cards and plates):
//
//   CINEMA    a short veil under the top bar (the bar's selector and "Switch to audio" stand on smoke plates), the title block bottom-left (TitleLarge
//             40/52 semibold over BodyLarge "Artist · Album" and the Body "Playing from" line), the Up next shelf of W-Video cards (an r8 still with
//             its duration badge, BodyStrong title, Caption artist) a gap above the command bar, and the bar itself: one r8 plate inset from the
//             picture's edges, the seek rail across its top, under it [play disc · previous · next · mute (+ the slider it reveals)] | elapsed / total
//             ... [heart · shuffle · repeat · device] | ["…" · settings · Cinema/Theatre · exit] with AppBarSeparators between the groups.
//   THEATRE   the picture's 16:9 box under the bar, the open play-queue list on the right (no frame), the title block under the picture (Title 28/36),
//             the channel row and the description in an r8 card under it (YouTube's grey description box as a Fluent card).
//
// WHY PLATES AND NOT ACRYLIC OVER THE PICTURE. The picture is a video HOLE (a DestOut erase with the video a DComp visual under the UI): in-window
// acrylic samples nothing there and the renderer resolves its backdrop over the opaque fallback, so an acrylic plate over the picture would be an
// opaque slab. Fluent's own answer when acrylic cannot render is its fallback colour, so the plates over the picture are the stage card's acrylic
// FALLBACK at <see cref="WinUiStageCinemaGeometry.PlateA"/> (smoke: the video reads through it).
//
// Literal numbers are allowed here (WVTH003 exempts `Rules\**`); the views read them by name.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Roles;

namespace Wavee.Themes.WinUi;

/// <summary>The constants of WinUI's stage chrome over the picture (Video mode, Cinema) and the plates it stands on.</summary>
internal static class WinUiStageCinemaGeometry
{
    /// <summary>The smoke plates over the picture (the command bar, the selector, the end card): the stage card's acrylic fallback at this alpha.</summary>
    public const float PlateA = 0.84f;

    /// <summary>The veil under the top bar: this tall, the stage veil at <see cref="TopScrimA"/> solid under the bar (to <see cref="TopScrimSolidAt"/> of
    /// the way down) and fading to nothing below it, so the wordmark reads in both arms over any picture or the letterbox (the light arm's dark ink over
    /// the black letterbox needs the veil nearly whole there).</summary>
    public const float TopScrimH = 112f, TopScrimA = 0.82f, TopScrimSolidAt = 0.42f;

    /// <summary>The veil under the title block and the shelf: this share of the stage's height, from nothing to <see cref="BottomScrimA"/>, reached at
    /// <see cref="BottomScrimSolidAt"/> of the way down.</summary>
    public const float BottomScrimFrac = 0.5f, BottomScrimA = 0.86f, BottomScrimSolidAt = 0.6f;

    /// <summary>The title: TitleLarge 40/52 semibold, two lines at most; the meta line ("Artist · Album") BodyLarge 18/24; "Playing from" Body 14/20.</summary>
    public const float TitleSize = 40f, TitleLine = 52f, MetaSize = 18f, MetaLine = 24f, SourceSize = 14f, SourceLine = 20f;

    public const ushort TitleWeight = 600;

    public const int TitleMaxLines = 2;

    /// <summary>The gap between the block's lines.</summary>
    public const float TitleGap = 4f;

    /// <summary>The title block's widest share of the stage.</summary>
    public const float TitleMaxWFrac = 0.6f;

    /// <summary>The title block and the shelf rise in from this far below with the chrome (and sink back as it rests).</summary>
    public const float TitleEnterDy = 12f;

    /// <summary>The title block's gap above the command bar where the tier has no shelf.</summary>
    public const float TitleAboveBar = 16f;

    /// <summary>The cross-fade of a part that comes and goes as the picture narrows (a folded command, "Switch to audio" labelled or as its glyph):
    /// WinUI's declared swap for its Video mode.</summary>
    public static readonly EnterExit VideoSwap = SwapFamilies.Declare("WinUi.Stage.VideoSwap", new(Opacity: 0f, Active: true));

    public static MotionTokenDef VideoSwapMotion => MotionTok.ControlFast;
}

/// <summary>The numbers of the COMMAND BAR over the picture (`WinUiStageVideoBar`): one r8 smoke plate inset from the picture's edges (the tier's
/// <c>BarInset</c>; Theatre's smaller box at most <see cref="TheatreInset"/>), the seek rail across its top and under it one row of 40 targets, the groups
/// split by AppBarSeparators. The row folds as the picture narrows (<see cref="Fold"/>): the volume slider, then shuffle · repeat · device, then the
/// mute, then the time (the "…" menu and the player bar keep them).</summary>
internal static class WinUiStageVideoBarGeometry
{
    /// <summary>A command's box (the Compact target rule at every tier), its glyph, the accent play disc and its solid glyph.</summary>
    public const float Button = 40f, Glyph = 16f, Disc = 40f, DiscGlyph = 16f;

    /// <summary>The plate's padding (sides, top above the seek row, bottom under the row), the seek row (the rail's 32 hit box) and the row.</summary>
    public const float PadX = 8f, PadTop = 4f, PadBottom = 4f, SeekH = 32f, RowH = 40f;

    /// <summary>The seek rail's inset inside the plate (it lines up with the row's first glyph's ink, a command's own padding in).</summary>
    public const float SeekInsetX = 12f;

    /// <summary>The plate's height.</summary>
    public const float CardH = PadTop + SeekH + RowH + PadBottom;

    /// <summary>Theatre's box is smaller: the bar's inset there is at most this.</summary>
    public const float TheatreInset = 12f;

    /// <summary>The AppBarSeparator: 1 wide, 20 tall, 4 clear on each side.</summary>
    public const float SepW = 1f, SepH = 20f, SepGap = 4f;

    /// <summary>A separator's whole width.</summary>
    public const float Sep = SepW + 2f * SepGap;

    /// <summary>The time's side padding, and the width the fold budgets for "0:00:00 / 0:00:00".</summary>
    public const float TimePadX = 8f, TimeBudgetW = 112f;

    /// <summary>The least gap between the row's two sides.</summary>
    public const float MinSpread = 16f;

    /// <summary>The volume slider revealed beside the mute (hover or focus of the group), its thickness, and the reveal's tween.</summary>
    public const float VolumeW = 96f, VolumeThickness = 32f, VolumeRevealMs = 200f;

    /// <summary>The bar rises in from this far below as the chrome comes back (and sinks as it rests).</summary>
    public const float EnterDy = 8f;

    /// <summary>The fold levels, in the order the row gives commands away.</summary>
    public const int FoldNone = 0, FoldSlider = 1, FoldSatellites = 2, FoldMute = 3, FoldTime = 4;

    /// <summary>The bar's height from the picture's foot at inset <paramref name="inset"/>.</summary>
    public static float BarH(float inset) => inset + CardH;

    /// <summary>The bar's inset in a layout: the tier's in Cinema, at most <see cref="TheatreInset"/> in Theatre.</summary>
    public static float Inset(float tierInset, bool theatre) => theatre ? MathF.Min(tierInset, TheatreInset) : tierInset;

    /// <summary>The left side at <paramref name="fold"/>: play · previous · next (· mute (· slider)), then a separator and the time.</summary>
    public static float LeftW(int fold)
        => Button * (fold < FoldMute ? 4f : 3f) + (fold < FoldSlider ? VolumeW : 0f) + (fold < FoldTime ? Sep + TimeBudgetW : 0f);

    /// <summary>The right side at <paramref name="fold"/>: heart (· shuffle · repeat · device) | "…" · settings (· Cinema/Theatre) · exit.</summary>
    public static float RightW(int fold, bool theatre)
        => Button * (4f + (fold < FoldSatellites ? 3f : 0f) + (theatre ? 1f : 0f)) + Sep;

    /// <summary>The fold for a picture <paramref name="pictureW"/> wide at the bar's inset <paramref name="inset"/>: the least that seats the row.</summary>
    public static int Fold(float pictureW, float inset, bool theatre)
    {
        float inner = pictureW - 2f * inset - 2f * PadX;
        for (int f = FoldNone; f < FoldTime; f++)
            if (LeftW(f) + MinSpread + RightW(f, theatre) <= inner) return f;
        return FoldTime;
    }
}

/// <summary>The numbers of Video mode's Up next shelf over the picture (Cinema): "Up next" (BodyStrong) over a row of W-Video's cards, each its art (a
/// video's 16:9 still, an audio-only track's square cover at the same height: two widths, the shape tells them apart) with the duration badge, over
/// its title (BodyStrong) and artist (Caption) at the art's width, the first card (what plays next) ringed in the accent. A gap above the command
/// bar; the title block stands a gap above it, its place reserved whether the shelf shows or rests.</summary>
internal static class WinUiStageShelfGeometry
{
    /// <summary>The shelf's foot above the command bar, and the title block's gap above the shelf's top.</summary>
    public const float AboveBar = 12f, TitleAbove = 20f;

    /// <summary>"Up next" (BodyStrong 14/20 semibold) and its gap to the cards.</summary>
    public const float CaptionSize = 14f, CaptionLine = 20f, CaptionGap = 8f;

    public const ushort CaptionWeight = 600, TitleWeight = 600;

    /// <summary>The gap between cards (W-Video's 12) and the most cards a shelf shows.</summary>
    public const float CardGap = 12f;

    public const int MaxCards = 8;

    /// <summary>A video card's art is 16:9; the ring around what plays next.</summary>
    public const float ThumbAspect = 16f / 9f, NextRing = 2f;

    /// <summary>The title (BodyStrong 14/20) and the artist (Caption 12/16) under the art, 8 under it.</summary>
    public const float TextGap = 8f, TitleSize = 14f, TitleLine = 20f, MetaSize = 12f, MetaLine = 16f;

    /// <summary>The film glyph's gap after the title (a track with a music video: the track lists' mark).</summary>
    public const float FilmGap = 6f;

    /// <summary>The duration badge (Caption 12/16 on the media scrim, 6 in from the art's corner, r4).</summary>
    public const float BadgeSize = 12f, BadgeLine = 16f, BadgeInset = 6f, BadgePadX = 6f, BadgePadY = 1f, BadgeA = 0.8f;

    /// <summary>The art's height for the tier's card width <paramref name="cardW"/>.</summary>
    public static float ThumbH(float cardW) => MathF.Round(cardW / ThumbAspect);

    /// <summary>A card's art width: a video's still is the card width, an audio-only cover is square at its height.</summary>
    public static float ArtW(float cardW, bool video) => video ? cardW : ThumbH(cardW);

    /// <summary>The shelf's height for cards <paramref name="cardW"/> wide (0: no shelf).</summary>
    public static float Height(float cardW) => cardW <= 0f ? 0f : CaptionLine + CaptionGap + ThumbH(cardW) + TextGap + TitleLine + MetaLine;

    /// <summary>The cards keep their count while each keeps this share of its width (a crossing's narrowing frames never drop a card and bring it back).</summary>
    public const float HoldShare = 0.85f;

    /// <summary>The cards <paramref name="width"/> seats from the cards' art widths (<paramref name="widths"/>), or the count shown before
    /// (<paramref name="held"/>) while all of them still fit at <see cref="HoldShare"/>, narrowed together by <c>Scale</c>.</summary>
    public static (int N, float Scale) Seat(float width, ReadOnlySpan<float> widths, int held)
    {
        int fit = Fits(width, widths, 1f);
        if (held > fit && held <= widths.Length && Fits(width, widths, HoldShare) >= held)
        {
            float sum = 0f;
            for (int i = 0; i < held; i++) sum += widths[i];
            return (held, sum > 0f ? MathF.Min(1f, (width - (held - 1) * CardGap) / sum) : 1f);
        }
        return (fit, fit == 1 && widths[0] > width ? MathF.Max(0f, width / widths[0]) : 1f);
    }

    static int Fits(float width, ReadOnlySpan<float> widths, float share)
    {
        float x = 0f;
        int n = 0;
        for (; n < widths.Length; n++)
        {
            float need = (n > 0 ? CardGap : 0f) + widths[n] * share;
            if (x + need > width) break;
            x += need;
        }
        return widths.Length > 0 ? Math.Max(1, n) : 0;
    }
}

/// <summary>The numbers of the stage's 16:9 thumbnail in Video mode (the end card's): the video's still, or the audio-only cover inset on a plate of
/// the same size, tagged "Video" / "Audio" top-left.</summary>
internal static class WinUiStageThumbGeometry
{
    /// <summary>The inset cover's share of the plate's height.</summary>
    public const float CoverShare = 0.75f;

    /// <summary>The tag: Caption 12/16 semibold, 6 in from the corner, on the media scrim, r4.</summary>
    public const float TagSize = 12f, TagLine = 16f, TagInset = 6f, TagPadX = 6f, TagPadY = 1f, TagPlateA = 0.8f;

    public const ushort TagWeight = 600;
}

/// <summary>The numbers of the END CARD (a music video ended; the next one waits for the countdown): over the picture's dimmed last frame a ContentDialog
/// -like smoke plate (r8, the dialog's 24 padding): "Up next in 5" (Body, the number semibold) over the next track's 16:9 thumbnail (the accent ring, a
/// thin progress line at its foot) and its text column (Subtitle 20/28 semibold title, Body artist, Caption "From ..."), then Play now (accent) and
/// Cancel. Cinema centres it between the top bar and the shelf's reserved place; Theatre centres it in the picture's box.</summary>
internal static class WinUiStageEndCardGeometry
{
    /// <summary>The veil over the last frame (the stage veil at this alpha).</summary>
    public const float DimA = 0.5f;

    /// <summary>The plate's padding (a ContentDialog's 24) and, in Theatre's smaller box, 16.</summary>
    public const float PlatePad = 24f, TheatrePlatePad = 16f;

    /// <summary>The caption row's gap to the card row, the caption (Body 14/20; the number semibold).</summary>
    public const float CaptionGap = 16f, CaptionSize = 14f, CaptionLine = 20f;

    public const ushort NumberWeight = 600, TitleWeight = 600;

    /// <summary>The gap between the thumbnail and the text column, and the text column's widest and narrowest.</summary>
    public const float RowGap = 20f, TextMaxW = 320f, TextMinW = 180f;

    /// <summary>The thumbnail's accent ring and the progress line (3 high, the track at this ink alpha).</summary>
    public const float Ring = 2f, ProgressH = 3f, ProgressTrackA = 0.2f;

    /// <summary>The title (Subtitle 20/28 semibold, two lines), the artist (Body 14/20) and the "From" line (Caption 12/16), 4 apart.</summary>
    public const float TitleSize = 20f, TitleLine = 28f, ArtistSize = 14f, ArtistLine = 20f, FromSize = 12f, FromLine = 16f, TextGap = 4f;

    public const int TitleMaxLines = 2;

    /// <summary>The buttons' row: 16 under the text (a dialog's command row), 8 between them.</summary>
    public const float ButtonsAbove = 16f, ButtonGap = 8f;

    /// <summary>Theatre: the region's padding inside the picture's box, and the thumbnail's largest share of the box's width.</summary>
    public const float TheatrePad = 16f, TheatreThumbShare = 0.38f;

    /// <summary>The card rises in from this far below (and sinks back as it goes).</summary>
    public const float EnterDy = 12f;

    /// <summary>The thumbnail's width in a region <paramref name="regionW"/> wide (the plate's padding inside it): the tier's, narrowed so the text
    /// column keeps its narrowest, and in Theatre at most <see cref="TheatreThumbShare"/> of the box.</summary>
    public static float ThumbW(float tierW, float regionW, float pad, bool theatre)
    {
        float inner = regionW - 2f * pad;
        float w = MathF.Min(tierW, inner - RowGap - TextMinW);
        if (theatre) w = MathF.Min(w, MathF.Round(regionW * TheatreThumbShare));
        return MathF.Max(0f, MathF.Round(w));
    }

    /// <summary>The text column's width beside a thumbnail <paramref name="thumbW"/> wide.</summary>
    public static float TextW(float thumbW, float regionW, float pad) => MathF.Max(0f, MathF.Min(TextMaxW, regionW - 2f * pad - thumbW - RowGap));
}

/// <summary>The numbers of the AUDIO FACE (Video mode over an audio-only track): the frame keeps the picture's place and shape and is filled with the
/// track's own colour, the cover inset on it (r8 with the dialog's shadow), the title under it (Cinema) and the synced lyrics beside it. WinUI's field
/// is MICA-like: a still diagonal wash of the cover's colours veiled toward the THEME's floor until the theme arm's lyric inks read on it (pale on the
/// light theme, deep on the dark), so every ink on the face is the theme's own.</summary>
internal static class WinUiStageAudioFaceGeometry
{
    /// <summary>Cinema: the face's top under the top bar and its gap above the reserved title / shelf block.</summary>
    public const float TopUnderBar = 32f, BottomGap = 24f;

    /// <summary>Cinema: the cover's gap to its title, the title (Title 28/36 semibold, two lines) and the meta line (Body 14/20).</summary>
    public const float TitleGap = 16f, TitleSize = 28f, TitleLine = 36f, MetaSize = 14f, MetaLine = 20f, MetaGap = 4f;

    public const ushort TitleWeight = 600;

    public const int TitleMaxLines = 2;

    /// <summary>Cinema: the gap between the cover column and the lyrics, as a share of the stage's width.</summary>
    public const float LyricsGapFrac = 0.05f;

    /// <summary>A Portrait stage stacks the face: the cover at most this share of the region's height, its title under it, the lyrics this far below.</summary>
    public const float PortraitCoverShare = 0.34f, PortraitLyricsGap = 16f;

    /// <summary>The cover's floor in Cinema.</summary>
    public const float CoverMin = 168f;

    /// <summary>The cover's decode size: the larger above this side.</summary>
    public const float CoverDecodeSmallW = 400f;

    public const int CoverDecodeSmall = 512, CoverDecodeLarge = 1024;

    /// <summary>Theatre: the cover's side as a share of the frame's height, its inset from the frame's left and top (shares of its width and height),
    /// the gap to the lyrics, the lyrics column's top and its right inset; the cover's largest share of the width left beside the lyrics.</summary>
    public const float TheatreCoverH = 0.62f, TheatreInsetX = 0.05f, TheatreInsetY = 0.1f, TheatreLyricsGap = 0.05f, TheatreLyricsTop = 0.13f,
        TheatreLyricsRight = 0.05f, TheatreCoverMaxW = 0.45f;

    /// <summary>The field's diagonal (degrees: top-left to bottom-right) and its tones: the light end lifts the cover's colour toward white, the deep
    /// end takes the palette's deep tone with some of its second colour.</summary>
    public const float FieldAngle = 35f, FieldLift = 0.12f, FieldDeepSecond = 0.3f, FieldMidAt = 0.5f;

    /// <summary>The contrast every tone keeps for the lyrics' unsung and secondary inks, and the veil's step toward the floor per try.</summary>
    public const float FieldContrast = 4.5f, FieldVeilStep = 0.1f;

    /// <summary>The field's light, mid and deep tones for <paramref name="p"/> on the arm of <paramref name="kind"/> (the theme's), each veiled toward
    /// its floor until the lyrics' other inks read at <see cref="FieldContrast"/>.</summary>
    public static (ColorF Light, ColorF Mid, ColorF Deep) FieldTones(in Visualizer.Palette p, ThemeKind kind)
    {
        var arm = StageInk.Arm(kind);
        var a = p.A with { A = 1f };
        var deep = ColorF.Lerp(p.Deep with { A = 1f }, p.B with { A = 1f }, FieldDeepSecond);
        var light = ColorF.Lerp(a, ColorF.FromRgba(0xff, 0xff, 0xff), FieldLift);
        var mid = ColorF.Lerp(a, deep, 0.5f);
        var other = arm.Ink with { A = Lyrics.Wipe.UnsungAlpha };
        return (Clamp(light, arm.Floor, other, arm.InkSecondary), Clamp(mid, arm.Floor, other, arm.InkSecondary), Clamp(deep, arm.Floor, other, arm.InkSecondary));
    }

    /// <summary><paramref name="tone"/> veiled toward <paramref name="floor"/> until both inks read on it.</summary>
    static ColorF Clamp(ColorF tone, ColorF floor, ColorF other, ColorF other2)
    {
        floor = floor with { A = 1f };
        for (float veil = 0f; veil < 1f; veil += FieldVeilStep)
        {
            var c = ColorF.Lerp(tone, floor, veil);
            if (ColorContrast.Ratio(ColorContrast.Flatten(other, c), c) >= FieldContrast && ColorContrast.Ratio(ColorContrast.Flatten(other2, c), c) >= FieldContrast)
                return c;
        }
        return floor;
    }
}

/// <summary>The constants and rects of WinUI's Theatre (the picture with the play-queue pane beside it).</summary>
internal static class WinUiStageTheatreGeometry
{
    /// <summary>The region's top under the bar: the picture's top edge and the list's header row.</summary>
    public const float RegionTop = 16f;

    /// <summary>The picture keeps a 16:9 box (another shape letterboxes inside it).</summary>
    public const float PictureAspect = 16f / 9f;

    /// <summary>The gap between the picture and its title block.</summary>
    public const float TitleGap = 12f;

    /// <summary>The title (Title 28/36 semibold, one line), the meta line (Body 14/20) 4 under it, "Playing from" (Caption 12/16) 2 under that.</summary>
    public const float TitleSize = 28f, TitleLine = 36f, MetaSize = 14f, MetaLine = 20f, MetaGap = 4f, SourceSize = 12f, SourceLine = 16f, SourceGap = 2f;

    public const ushort TitleWeight = 600;

    /// <summary>The title block's height.</summary>
    public const float TitleBlockH = TitleLine + MetaGap + MetaLine + SourceGap + SourceLine;

    /// <summary>How far the pane slides in from the right.</summary>
    public const float PaneSlide = 32f;

    /// <summary>The open list's inner padding (its rows' stills and the header's text stand this far inside it: the stage queue's own), its header row
    /// (BodyStrong) and the gap under it.</summary>
    public const float ListPad = 8f, HeaderH = 32f, HeaderGap = 4f;

    /// <summary>The page's colour in Theatre (Mica: the window tinted by what plays): the floor blended toward the palette's colour, this much at the
    /// top-left and this much at the bottom-right, still, cross-faded on a track change.</summary>
    public const float WashTopA = 0.22f, WashBottomA = 0.05f, WashAngle = 60f;

    /// <summary>How long after Theatre closes Cinema's letterbox ground comes back (the reservation's spring has filled the stage by then).</summary>
    public const float GroundReturnMs = 600f;

    /// <summary>The ABOUT block under the title (YouTube's channel row over its description box, as a Fluent card): its gap under the title block, the
    /// channel row (a 40 round portrait, the name and listeners, Follow), the gap to the card, the card's padding, the facts line, up to
    /// <see cref="BioLines"/> lines of the bio and "Read more", 4 apart.</summary>
    public const float AboutGap = 16f, ArtistRowH = 40f, ArtistImage = 40f, ArtistRowGap = 12f, DescGap = 12f, DescPad = 12f, FactsLine = 20f, BioLine = 20f,
        MoreLine = 20f, DescLineGap = 4f;

    public const int BioLines = 3;

    public const float ArtistImageRadius = ArtistImage / 2f;

    public const int ArtistImageDecode = (int)(ArtistImage * 2f);

    /// <summary>The description card's height and the block's, reserved whole from the first frame.</summary>
    public const float DescH = 2f * DescPad + FactsLine + DescLineGap + BioLines * BioLine + DescLineGap + MoreLine;

    public const float AboutH = ArtistRowH + DescGap + DescH;

    /// <summary>What the picture leaves under itself: the title block and the about block with their gaps.</summary>
    public const float UnderH = TitleGap + TitleBlockH + AboutGap + AboutH;

    /// <summary>The picture's, its title block's, the pane's and the about block's rects in stage DIP.</summary>
    public readonly record struct Rects(RectF Picture, RectF Title, RectF Pane, RectF About)
    {
        public bool AboutFits => About.W > 0f;
    }

    /// <summary>The rects for stage <paramref name="L"/> under a bar <paramref name="barH"/> tall at the gutter <paramref name="gutter"/> (the bar's
    /// inset: the wordmark's edge). The picture is the largest 16:9 box the left column holds that still leaves room under it for the title and the
    /// about block, down to the gutter: one continuous function of the stage's size.</summary>
    public static Rects Of(Stage.Layout L, float barH, float gutter)
    {
        float top = barH + RegionTop;
        float bottom = L.H - gutter;
        float regionH = MathF.Max(0f, bottom - top);
        float paneW = L.TheatrePaneW;
        float paneX = L.W - gutter - paneW;
        float columnW = MathF.Max(0f, paneX - 2f * gutter);
        float roomH = MathF.Max(0f, regionH - UnderH);
        float picW = columnW, picH = MathF.Round(columnW / PictureAspect);
        if (picH > roomH) { picH = roomH; picW = MathF.Round(roomH * PictureAspect); }
        float x = gutter + MathF.Round((columnW - picW) * 0.5f);
        float y = top;
        float aboutY = y + picH + TitleGap + TitleBlockH + AboutGap;
        var about = picH > 0f && aboutY + AboutH <= bottom + 0.5f ? new RectF(x, aboutY, picW, AboutH) : default;
        return new Rects(new RectF(x, y, picW, picH), new RectF(x, y + picH + TitleGap, picW, TitleBlockH),
            new RectF(paneX - ListPad, y, paneW + 2f * ListPad, MathF.Max(0f, L.H - y)), about);
    }
}
