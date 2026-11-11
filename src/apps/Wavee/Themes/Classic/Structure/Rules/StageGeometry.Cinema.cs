// ── Themes/Classic/Structure/Rules/StageGeometry.Cinema.cs — the numbers of Classic's stage in Video mode (Cinema) ───────────────
//
// The board "Classic · Cinema" (the video full screen's concept canvas, Main.dc.html): the picture edge to edge, a scrim under the top
// bar and one under the title and the transport, the title block (36/44 Display semibold over a 15 meta line) at the transport card's
// content edge above it. The top bar, the transport card and the chrome's motion are the stage's own (`ClassicStageGeometry`, the tier
// record `ClassicStageLayout`); what this file adds is the scrims and the title block. Literals live here (WVTH003 allows `Rules\**`).
//
// THEATRE (the board "Classic · Theatre", Classic-Theatre.dc.html): under the top bar, the picture keeps its 16:9 shape in the left region
// with the title block (28/36 Display semibold over a 14 meta line and the quiet "Playing from" line) under it, and the panel (Lyrics or Up next,
// the tier's width: `StageTierMetrics.TheatrePaneW`) stands on the right as an open list; the region runs from 16 under the top bar (the board
// "B · Open list": the picture's top edge and the list's header row) down to the stage's gutter (the controls are the picture's own bar, inside
// it), at the gutter on both sides and between the two. The picture is centred across the left region, its title under it.
//
// THE CONTROL BAR (YouTube's player bar, `ClassicStageVideoBar`): in Video mode the transport is the picture's, at the foot of its rect in Cinema
// and Theatre alike: a scrim, the seek bar across the picture, and under it one row of 40 targets on dark pills (play · previous · next · mute
// and its revealed slider, then the time; heart · shuffle · repeat · device · "…" · the video's settings · Cinema/Theatre · Exit), folding the
// lower-priority controls into the "…" menu as the picture narrows.

using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's stage chrome over the picture (Video mode).</summary>
internal static class ClassicStageCinemaGeometry
{
    /// <summary>The scrim under the top bar: this tall, from the stage veil at <see cref="TopScrimA"/> to nothing (the board's 120 at 0.90;
    /// lighter, as the top bar keeps its own veil over it).</summary>
    public const float TopScrimH = 120f, TopScrimA = 0.72f;

    /// <summary>The scrim under the title and the transport: this share of the stage's height, from nothing to the veil at
    /// <see cref="BottomScrimA"/>, reached at <see cref="BottomScrimSolidAt"/> of the way down (the board's 480 of 810, 0.95 at 55 %).</summary>
    public const float BottomScrimFrac = 0.56f, BottomScrimA = 0.92f, BottomScrimSolidAt = 0.55f;

    /// <summary>The title: Display 36/44 semibold, two lines at most; the meta line ("Artist · Album") 15/20.</summary>
    public const float TitleSize = 36f, TitleLine = 44f, MetaSize = 15f, MetaLine = 20f;

    public const ushort TitleWeight = 600;

    public const int TitleMaxLines = 2;

    /// <summary>The gap between the title and the meta line (the board's 6).</summary>
    public const float TitleGap = 6f;

    /// <summary>The title block's widest share of the stage.</summary>
    public const float TitleMaxWFrac = 0.62f;

    /// <summary>The title block rises in from this far below with the chrome (and sinks back as it rests).</summary>
    public const float TitleEnterDy = 12f;

    /// <summary>The quiet "Playing from · source" line under the meta line (the transport card's left column before the control bar), 13/18.</summary>
    public const float SourceSize = 13f, SourceLine = 18f, SourceGap = 4f;

    /// <summary>The title block's gap above the control bar (no shelf on the tier).</summary>
    public const float TitleAboveBar = 8f;

    /// <summary>The Video tab over an audio-only track (disabled): its opacity at rest.</summary>
    public const float DisabledTabA = 0.4f;
}

/// <summary>The numbers of Video mode's Up next shelf over the picture (Cinema; the board "Classic · Cinema", its cards the board "E · Shelf",
/// "1 · No frame"): the "Up next" caption over a row of frameless cards, each its art (a video's 16:9 still, an audio-only track's square cover at
/// the same height: two widths) over its title and artist line, the first card (what plays next) ringed in the accent. It sits a gap above the control bar at the title block's left edge; the title block
/// stands a gap above it, its place reserved whether the shelf shows or rests with the chrome.</summary>
internal static class ClassicStageShelfGeometry
{
    /// <summary>The shelf's foot above the control bar, and the title block's gap above the shelf's top.</summary>
    public const float AboveBar = 4f, TitleAbove = 24f;

    /// <summary>The "Up next" caption (13/18 semibold) and its gap to the cards (the board's 10).</summary>
    public const float CaptionSize = 13f, CaptionLine = 18f, CaptionGap = 10f;

    public const ushort CaptionWeight = 600;

    /// <summary>The gap between cards (the board's 16) and the most cards a shelf shows, whatever its width.</summary>
    public const float CardGap = 16f;

    public const int MaxCards = 8;

    /// <summary>A video card's art is 16:9 (an audio card's is square at its height); the ring around what plays next (2, the accent).</summary>
    public const float ThumbAspect = 16f / 9f, NextRing = 2f;

    /// <summary>The title (13/18) and the artist line (12/16) under the thumbnail, 6 under it and 2 apart.</summary>
    public const float TextGap = 6f, TitleSize = 13f, TitleLine = 18f, MetaSize = 12f, MetaLine = 16f, LineGap = 2f;

    /// <summary>The art's height for the tier's card width <paramref name="cardW"/> (a video card's 16:9 still at that width).</summary>
    public static float ThumbH(float cardW) => MathF.Round(cardW / ThumbAspect);

    /// <summary>A card's art width at the tier's card width: a video's 16:9 still is the card width, an audio-only cover is square at its height.</summary>
    public static float ArtW(float cardW, bool video) => video ? cardW : ThumbH(cardW);

    /// <summary>The shelf's height for cards <paramref name="cardW"/> wide (0: no shelf): the caption, the thumbnail, the two text lines.</summary>
    public static float Height(float cardW)
        => cardW <= 0f ? 0f : CaptionLine + CaptionGap + ThumbH(cardW) + TextGap + TitleLine + LineGap + MetaLine;

    /// <summary>How far the cards may narrow to keep the cards the shelf shows (a resize, a tier crossing's narrowing frames) before one leaves: the
    /// count holds while every card keeps this share of its width, so a crossing never drops a card and brings it back a frame later.</summary>
    public const float HoldShare = 0.85f;

    /// <summary>The cards <paramref name="width"/> seats, from the cards' art widths in order (<paramref name="widths"/>, at most <see cref="MaxCards"/>):
    /// as many as fit at their own widths, or the count shown before (<paramref name="held"/>) while all of them still fit at <see cref="HoldShare"/>,
    /// then narrowed together by <c>Scale</c> (1: their own widths). At least one card when there is any.</summary>
    public static (int N, float Scale) Seat(float width, ReadOnlySpan<float> widths, int held)
    {
        int fit = Fits(width, widths, 1f);
        if (held > fit && held <= widths.Length && Fits(width, widths, HoldShare) >= held)
        {
            float sum = 0f;
            for (int i = 0; i < held; i++) sum += widths[i];
            return (held, sum > 0f ? MathF.Min(1f, (width - (held - 1) * CardGap) / sum) : 1f);
        }
        // one card wider than the shelf (a narrow window) narrows to it
        return (fit, fit == 1 && widths[0] > width ? MathF.Max(0f, width / widths[0]) : 1f);
    }

    /// <summary>How many leading cards fit in <paramref name="width"/> at <paramref name="share"/> of their widths (at least one when there is any).</summary>
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

/// <summary>The numbers of the stage's 16:9 thumbnail in Video mode (the shelf's cards and the Theatre list's rows): an audio-only track's cover sits
/// inset on a dark plate the thumbnail's size (the board's 92 of 122: this share of the height), and each thumbnail carries a small tag ("Video" or
/// "Audio", 11 semibold, 6 in from the corner) so the two kinds are told apart at a glance.</summary>
internal static class ClassicStageThumbGeometry
{
    /// <summary>The inset cover's share of the plate's height.</summary>
    public const float CoverShare = 0.75f;

    /// <summary>The tag: its type, its inset from the top-left corner, its padding and the plate's alpha under it (over a still).</summary>
    public const float TagSize = 11f, TagLine = 14f, TagInset = 6f, TagPadX = 5f, TagPadY = 1f, TagPlateA = 0.8f, TagCorners = 3f;

    public const ushort TagWeight = 600;

    /// <summary>A small thumbnail (the Theatre list's 96 wide rows) takes the tag at this size.</summary>
    public const float SmallTagSize = 11f, SmallTagLine = 13f, SmallTagInset = 4f, SmallTagPadX = 4f, SmallBelowW = 120f;

    /// <summary>The duration badge in the shelf card's bottom-right corner (11, on the same plate as the tag).</summary>
    public const float BadgeSize = 11f, BadgeLine = 14f;
}

/// <summary>The numbers of the AUDIO FACE: Video mode's picture place while an audio-only track plays (the board "Classic · An audio-only track
/// plays"): the cover with the title and the "Artist · Album" line under it on the left, the synced lyrics beside it (Cinema); in Theatre the cover
/// and the lyrics share the picture's 16:9 box (the title block stays under the box), or the cover alone sits centred in it beside Theatre's own
/// Lyrics panel.</summary>
internal static class ClassicStageAudioFaceGeometry
{
    /// <summary>Cinema: the face's top under the top bar (the board's 84 at 48 + 36) and its gap above the reserved title / shelf block.</summary>
    public const float TopUnderBar = 36f, BottomGap = 24f;

    /// <summary>Cinema: the cover's gap to its title (the board's 14), the title (Display 28/34 semibold, two lines at most) and the meta line (14/20).</summary>
    public const float TitleGap = 14f, TitleSize = 28f, TitleLine = 34f, MetaSize = 14f, MetaLine = 20f, MetaGap = 2f;

    public const ushort TitleWeight = 600;

    public const int TitleMaxLines = 2;

    /// <summary>The gap between the cover column and the lyrics (Cinema: this share of the stage's width; Theatre: the box's padding).</summary>
    public const float LyricsGapFrac = 0.05f;

    /// <summary>Theatre: the padding inside the picture's box around the cover and the lyrics, and the box's corners.</summary>
    public const float BoxPad = 24f;

    /// <summary>The cover's floor in Cinema (the stage's hero floor).</summary>
    public const float CoverMin = 168f;
}

/// <summary>The numbers of Video mode's END CARD (the board "Classic · End of video", Classic-End.dc.html, 1440 x 810): over the picture's last frame,
/// dimmed, "Up next in 5" (14, the number white semibold) a gap above a row of the next track's 16:9 thumbnail (the tier's width, an accent ring and
/// a 4 high progress line at its foot) and its text column: the title (Display 28/34 semibold, two lines at most), the artist (15/20), "From ..."
/// (12/16), then Play now (accent) and Cancel. Cinema centres it between the top bar and the shelf's reserved place; Theatre centres it in the
/// picture's box, the thumbnail at most <see cref="TheatreThumbShare"/> of the box.</summary>
internal static class ClassicStageEndCardGeometry
{
    /// <summary>The veil over the last frame (the stage veil at this alpha).</summary>
    public const float DimA = 0.5f;

    /// <summary>The caption row's gap to the card row (the board's 20), the caption (14/20; the number 600).</summary>
    public const float CaptionGap = 20f, CaptionSize = 14f, CaptionLine = 20f;

    public const ushort NumberWeight = 600;

    /// <summary>The gap between the thumbnail and the text column (the board's 24), and the text column's widest and narrowest.</summary>
    public const float RowGap = 24f, TextMaxW = 336f, TextMinW = 180f;

    /// <summary>The thumbnail's corners (the board's 8), its accent ring (2), the progress line (4 high, the track at this ink alpha).</summary>
    public const float ThumbCorners = 8f, Ring = 2f, ProgressH = 4f, ProgressTrackA = 0.15f;

    /// <summary>The title (Display 28/34 semibold, two lines), the artist (15/20) and the "From" line (12/16), 8 apart.</summary>
    public const float TitleSize = 28f, TitleLine = 34f, ArtistSize = 15f, ArtistLine = 20f, FromSize = 12f, FromLine = 16f, TextGap = 8f;

    public const ushort TitleWeight = 600;

    public const int TitleMaxLines = 2;

    /// <summary>The buttons' row: 12 more under the text (the board's margin), 8 between them.</summary>
    public const float ButtonsAbove = 12f, ButtonGap = 8f;

    /// <summary>Theatre: the card's padding inside the picture's box, and the thumbnail's largest share of the box's width.</summary>
    public const float TheatrePad = 24f, TheatreThumbShare = 0.42f;

    /// <summary>The card rises in from this far below (and sinks back as it goes).</summary>
    public const float EnterDy = 12f;

    /// <summary>The thumbnail's width in a region <paramref name="regionW"/> wide: the tier's (<paramref name="tierW"/>), narrowed so the text column
    /// keeps its narrowest beside it, and in Theatre at most <see cref="TheatreThumbShare"/> of the picture's box.</summary>
    public static float ThumbW(float tierW, float regionW, bool theatre)
    {
        float w = MathF.Min(tierW, regionW - RowGap - TextMinW);
        if (theatre) w = MathF.Min(w, MathF.Round(regionW * TheatreThumbShare));
        return MathF.Max(0f, MathF.Round(w));
    }

    /// <summary>The text column's width beside a thumbnail <paramref name="thumbW"/> wide in a region <paramref name="regionW"/> wide.</summary>
    public static float TextW(float thumbW, float regionW) => MathF.Max(0f, MathF.Min(TextMaxW, regionW - thumbW - RowGap));
}

/// <summary>The constants and rects of Classic's stage in Video mode's Theatre (the picture with a panel beside it).</summary>
internal static class ClassicStageTheatreGeometry
{
    /// <summary>The region's top under the top bar (the board "B · Open list": 16), where the picture's top edge and the open list's header row stand.</summary>
    public const float RegionTop = 16f;

    /// <summary>The picture's shape: the region keeps a 16:9 box (a picture of another shape letterboxes inside it).</summary>
    public const float PictureAspect = 16f / 9f;

    /// <summary>The gap between the picture and its title block (the board's 14).</summary>
    public const float TitleGap = 14f;

    /// <summary>The title: Display 28/36 semibold, one line; the meta line ("Artist · Album") 14/20, 4 under it.</summary>
    public const float TitleSize = 28f, TitleLine = 36f, MetaSize = 14f, MetaLine = 20f, MetaGap = 4f;

    public const ushort TitleWeight = 600;

    /// <summary>The quiet "Playing from · source" line under the meta line (13/18, 4 under it).</summary>
    public const float SourceSize = 13f, SourceLine = 18f, SourceGap = 4f;

    /// <summary>The title block's height (the title line, the gap, the meta line, the gap, the source line).</summary>
    public const float TitleBlockH = TitleLine + MetaGap + MetaLine + SourceGap + SourceLine;

    /// <summary>How far the panel slides in from the right (the Huge 4K Up next column's slide).</summary>
    public const float PaneSlide = 32f;

    /// <summary>The panel is an OPEN LIST on the page (the board "B · Open list"): no card. Its rows' inner padding (their stills and its header's
    /// text stand this far inside the list, so they line up a gutter from the picture and a gutter from the stage's edge), its header row (the
    /// picture's top edge is its top; "Up next" or "Lyrics" in BodyStrong), the gap under it, and the fade into the page over its foot.</summary>
    public const float ListPad = 8f, HeaderH = 32f, HeaderGap = 4f, ListFadeH = 72f;

    /// <summary>The page's colour (the playing track's palette, a STILL wash): a glow around the picture's box, strongest at its centre and fading
    /// to a faint tint of the whole page (YouTube's ambient mode). The glow's reach over the box's size (per axis), the alphas at its centre, at
    /// <see cref="WashMidAt"/> of its reach and at its edge (the page beyond), and the cross-fade when the track changes.</summary>
    public const float WashReachX = 0.85f, WashReachY = 1.05f, WashCentreA = 0.55f, WashMidA = 0.24f, WashMidAt = 0.6f, WashEdgeA = 0.07f;

    /// <summary>How long after Theatre closes Cinema's letterbox ground comes back (ms, then a fade): the reservation's spring
    /// (<c>ClassicStageGeometry.PaneMoveResponse</c> 0.45 s, damping 0.90; envelope e^(-ζ·2π/response·t)) has brought the growing picture
    /// within half a DIP of the whole stage by then from a move of 1000 DIP, so the ground appears under the picture and never around it.</summary>
    public const float GroundReturnMs = 600f;

    /// <summary>The ABOUT block under the title (YouTube's channel row and description, `ClassicStageTheatreAbout`): its gap under the title block,
    /// the artist row (a 40 round portrait beside the name and the listeners line, the Follow toggle), the gap to the description, the facts line, up
    /// to <see cref="BioLines"/> lines of the bio and the "Read more" line, 4 apart.</summary>
    public const float AboutGap = 16f, ArtistRowH = 40f, ArtistImage = 40f, ArtistRowGap = 12f, DescGap = 12f, FactsLine = 20f, BioLine = 20f, MoreLine = 20f, DescLineGap = 4f;

    public const int BioLines = 3;

    /// <summary>The round portrait's corner (half its side) and its decode size (twice its side).</summary>
    public const float ArtistImageRadius = ArtistImage / 2f;

    public const int ArtistImageDecode = (int)(ArtistImage * 2f);

    /// <summary>The block's height, reserved whole from the first frame (the skeleton, the facts landing, a track change move nothing).</summary>
    public const float AboutH = ArtistRowH + DescGap + FactsLine + DescLineGap + BioLines * BioLine + DescLineGap + MoreLine;

    /// <summary>The picture's rect, its title block's and the panel's, in stage DIP, for the stage layout <paramref name="L"/>. The panel is the open
    /// list's box: from the picture's top edge to the stage's foot, <see cref="ListPad"/> outside the tier's panel width on both sides (its rows'
    /// content keeps the gutters).</summary>
    /// <para>The about block (<c>About</c>) stands under the title, as wide as the picture, only where the room down to the stage's gutter holds it whole;
    /// elsewhere it is empty (0 wide) and the title block stands alone.</para>
    public readonly record struct Rects(RectF Picture, RectF Title, RectF Pane, RectF About)
    {
        /// <summary>The about block shows (it fits whole under the title).</summary>
        public bool AboutFits => About.W > 0f;
    }

    public static Rects Of(Stage.Layout L)
    {
        float gutter = L.TransportInset;
        float top = Stage.Layout.TopBarH + RegionTop;
        float bottom = L.H - gutter;   // the controls are the picture's own bar (ClassicStageVideoBarGeometry), inside its rect
        float regionH = MathF.Max(0f, bottom - top);
        float paneW = L.TheatrePaneW;
        float paneX = L.W - gutter - paneW;
        float columnW = MathF.Max(0f, paneX - 2f * gutter);
        float roomH = MathF.Max(0f, regionH - TitleGap - TitleBlockH);
        float picW = columnW, picH = MathF.Round(columnW / PictureAspect);
        if (picH > roomH) { picH = roomH; picW = MathF.Round(roomH * PictureAspect); }
        float x = gutter + MathF.Round((columnW - picW) * 0.5f);
        float y = top;   // the picture's top edge under the top bar (the board "B · Open list"), its title under it
        float aboutY = y + picH + TitleGap + TitleBlockH + AboutGap;
        var about = aboutY + AboutH <= bottom ? new RectF(x, aboutY, picW, AboutH) : default;
        return new Rects(new RectF(x, y, picW, picH), new RectF(x, y + picH + TitleGap, picW, TitleBlockH),
            new RectF(paneX - ListPad, y, paneW + 2f * ListPad, MathF.Max(0f, L.H - y)), about);
    }
}

/// <summary>The numbers of Video mode's CONTROL BAR over the picture (YouTube's player bar; `ClassicStageVideoBar`): at the foot of the picture's rect,
/// a scrim, the seek bar across the picture at the stage's gutter, and under it one row on dark pills. Every control is a 40 target at every tier.
/// The row folds as the picture narrows (<see cref="Fold"/>): the volume slider first (the mute stays), then shuffle, repeat and the device picker
/// (the stacked transport card's folds: the "…" menu and the player bar keep them), then the mute, then the time.</summary>
internal static class ClassicStageVideoBarGeometry
{
    /// <summary>A control's box (the Compact target rule at every tier), its glyph, the play glyph and the heart's.</summary>
    public const float Button = 40f, Glyph = 16f, PlayGlyph = 20f, HeartGlyph = 18f;

    /// <summary>A pill's padding around its controls, its height, the gap between pills (and the least gap between the row's two sides), and its
    /// ground: the on-media scrim at this alpha (the board's 0.5 black, deepened so white reads at 4.5 : 1 over the light arm's pale veil too).</summary>
    public const float PillPad = 4f, PillH = Button + 2f * PillPad, PillGap = 8f, PillA = 0.62f;

    /// <summary>The row's gap to the picture's foot, and the seek row's height over it (the seek rail's 32 hit box).</summary>
    public const float FootInset = 8f, SeekH = 32f;

    /// <summary>The bar's height from the picture's foot: the row and the seek row.</summary>
    public const float BarH = FootInset + PillH + SeekH;

    /// <summary>The scrim under the bar: the bar's height plus this fade above it, from nothing to the on-media scrim at <see cref="ScrimA"/>,
    /// reached at <see cref="ScrimSolidAt"/> of the way down.</summary>
    public const float ScrimFade = 96f, ScrimA = 0.72f, ScrimSolidAt = 0.55f;

    /// <summary>The volume slider revealed beside the mute (hover or focus of the group), its thickness, and the reveal's tween.</summary>
    public const float VolumeW = 80f, VolumeThickness = 32f, VolumeRevealMs = 200f;

    /// <summary>The time pill's padding, and the width the row budgets for "0:00:00 / 0:00:00" when it decides the fold.</summary>
    public const float TimePadX = 12f, TimeBudgetW = 120f;

    /// <summary>A control's hover and press plates (the on-media ink at these alphas) and a latched toggle's hover over the accent.</summary>
    public const float GlyphHoverA = 0.14f, GlyphPressedA = 0.08f, LatchedHoverA = 0.9f;

    /// <summary>The bar rises in from this far below as the chrome comes back (and sinks as it rests).</summary>
    public const float EnterDy = 8f;

    /// <summary>The fold levels, in the order the row gives controls away.</summary>
    public const int FoldNone = 0, FoldSlider = 1, FoldSatellites = 2, FoldMute = 3, FoldTime = 4;

    /// <summary>The left side at <paramref name="fold"/>: play · previous · next (· mute (· slider)) on one pill, then the time pill.</summary>
    public static float LeftW(int fold)
        => 2f * PillPad + Button * (fold < FoldMute ? 4f : 3f) + (fold < FoldSlider ? VolumeW : 0f) + (fold < FoldTime ? PillGap + TimeBudgetW : 0f);

    /// <summary>The right side at <paramref name="fold"/>: heart (· shuffle · repeat · device) · "…" · settings (· Cinema/Theatre) · Exit.</summary>
    public static float RightW(int fold, bool theatre)
        => 2f * PillPad + Button * (4f + (fold < FoldSatellites ? 3f : 0f) + (theatre ? 1f : 0f));

    /// <summary>The fold for a picture <paramref name="pictureW"/> wide at the gutter <paramref name="inset"/>: the least that seats the row.</summary>
    public static int Fold(float pictureW, float inset, bool theatre)
    {
        for (int f = FoldNone; f < FoldTime; f++)
            if (2f * inset + LeftW(f) + PillGap + RightW(f, theatre) <= pictureW) return f;
        return FoldTime;
    }
}
