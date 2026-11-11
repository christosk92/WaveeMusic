// ── Themes/Classic/Structure/Rules/StageGeometry.Cinema.cs — the numbers of Classic's stage in Video mode (Cinema) ───────────────
//
// The board "Classic · Cinema" (the video full screen's concept canvas, Main.dc.html): the picture edge to edge, a scrim under the top
// bar and one under the title and the transport, the title block (36/44 Display semibold over a 15 meta line) at the transport card's
// content edge above it. The top bar, the transport card and the chrome's motion are the stage's own (`ClassicStageGeometry`, the tier
// record `ClassicStageLayout`); what this file adds is the scrims and the title block. Literals live here (WVTH003 allows `Rules\**`).
//
// THEATRE (the board "Classic · Theatre", Classic-Theatre.dc.html): under the top bar, the picture keeps its 16:9 shape in the left region
// with the title block (28/36 Display semibold over a 14 meta line) under it, and the panel (Lyrics or Up next, the tier's width:
// `StageTierMetrics.TheatrePaneW`) docks on the right; the region runs from 8 under the top bar down to a gutter above the transport card, at
// the transport card's gutter on both sides and between the two. The picture and its title sit centred in the left region's height.

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

    /// <summary>The title block's gap above the transport card, and its widest share of the stage.</summary>
    public const float TitleAboveTransport = 24f, TitleMaxWFrac = 0.62f;

    /// <summary>The title block rises in from this far below with the chrome (and sinks back as it rests).</summary>
    public const float TitleEnterDy = 12f;

    /// <summary>The Video tab over an audio-only track (disabled): its opacity at rest.</summary>
    public const float DisabledTabA = 0.4f;
}

/// <summary>The numbers of Video mode's Up next shelf over the picture (Cinema; the board "Classic · Cinema"): the "Up next" caption over a row of
/// 16:9 cards, each a thumbnail (the video's still full-bleed, or the audio-only track's cover inset on a plate) over its title and artist line,
/// the first card (what plays next) ringed in the accent. It sits a gap above the transport card at the title block's left edge; the title block
/// stands a gap above it, its place reserved whether the shelf shows or rests with the chrome.</summary>
internal static class ClassicStageShelfGeometry
{
    /// <summary>The shelf's foot above the transport card (the board's 20), and the title block's gap above the shelf's top.</summary>
    public const float AboveTransport = 20f, TitleAbove = 24f;

    /// <summary>The "Up next" caption (13/18 semibold) and its gap to the cards (the board's 10).</summary>
    public const float CaptionSize = 13f, CaptionLine = 18f, CaptionGap = 10f;

    public const ushort CaptionWeight = 600;

    /// <summary>The gap between cards (the board's 16) and the most cards a shelf shows, whatever its width.</summary>
    public const float CardGap = 16f;

    public const int MaxCards = 8;

    /// <summary>A card's thumbnail is 16:9; its corners (the board's 6) and the ring around what plays next (2, the accent) or around the rest
    /// (1, the stage's stroke).</summary>
    public const float ThumbAspect = 16f / 9f, ThumbCorners = 6f, NextRing = 2f, Ring = 1f;

    /// <summary>The title (13/18) and the artist line (12/16) under the thumbnail, 6 under it and 2 apart.</summary>
    public const float TextGap = 6f, TitleSize = 13f, TitleLine = 18f, MetaSize = 12f, MetaLine = 16f, LineGap = 2f;

    /// <summary>The thumbnail's height for a card <paramref name="cardW"/> wide.</summary>
    public static float ThumbH(float cardW) => MathF.Round(cardW / ThumbAspect);

    /// <summary>The shelf's height for cards <paramref name="cardW"/> wide (0: no shelf): the caption, the thumbnail, the two text lines.</summary>
    public static float Height(float cardW)
        => cardW <= 0f ? 0f : CaptionLine + CaptionGap + ThumbH(cardW) + TextGap + TitleLine + LineGap + MetaLine;

    /// <summary>How far a card may narrow to keep the cards the shelf shows (a resize, a tier crossing's narrowing frames) before one leaves: the
    /// count holds while every card keeps this share of the tier's width, so a crossing never drops a card and brings it back a frame later.</summary>
    public const float HoldShare = 0.85f;

    /// <summary>The card width that seats <paramref name="n"/> cards in <paramref name="width"/>, at most <paramref name="cardW"/>.</summary>
    public static float CardWFor(float width, float cardW, int n)
        => n <= 0 ? cardW : MathF.Min(cardW, MathF.Floor((width - (n - 1) * CardGap) / n));

    /// <summary>The cards shown at <paramref name="width"/>: as many as fit at the tier's width, or the count shown before (<paramref name="held"/>)
    /// while each of them still keeps <see cref="HoldShare"/> of it.</summary>
    public static int CardsHeld(float width, float cardW, int held)
    {
        int fit = Cards(width, cardW);
        return held > fit && held <= MaxCards && CardWFor(width, cardW, held) >= HoldShare * cardW ? held : fit;
    }

    /// <summary>How many cards <paramref name="width"/> holds at <paramref name="cardW"/> (at least one, at most <see cref="MaxCards"/>; 0 without a shelf).</summary>
    public static int Cards(float width, float cardW)
        => cardW <= 0f ? 0 : Math.Clamp((int)MathF.Floor((width + CardGap) / (cardW + CardGap)), 1, MaxCards);
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
    /// <summary>The region's top under the top bar (the board's 8).</summary>
    public const float RegionTop = 8f;

    /// <summary>The picture's shape: the region keeps a 16:9 box (a picture of another shape letterboxes inside it).</summary>
    public const float PictureAspect = 16f / 9f;

    /// <summary>The gap between the picture and its title block (the board's 14).</summary>
    public const float TitleGap = 14f;

    /// <summary>The title: Display 28/36 semibold, one line; the meta line ("Artist · Album") 14/20, 4 under it.</summary>
    public const float TitleSize = 28f, TitleLine = 36f, MetaSize = 14f, MetaLine = 20f, MetaGap = 4f;

    public const ushort TitleWeight = 600;

    /// <summary>The title block's height (the title line, the gap, the meta line).</summary>
    public const float TitleBlockH = TitleLine + MetaGap + MetaLine;

    /// <summary>The panel's padding (the board's 16) and how far it slides in from the right (the Huge 4K Up next column's slide).</summary>
    public const float PanePad = 16f, PaneSlide = 32f;

    /// <summary>How long after Theatre closes Cinema's letterbox ground comes back (ms, then a fade): the reservation's spring
    /// (<c>ClassicStageGeometry.PaneMoveResponse</c> 0.45 s, damping 0.90; envelope e^(-ζ·2π/response·t)) has brought the growing picture
    /// within half a DIP of the whole stage by then from a move of 1000 DIP, so the ground appears under the picture and never around it.</summary>
    public const float GroundReturnMs = 600f;

    /// <summary>The panel card's and the transport card's ground in Theatre: the stage's card acrylic (<c>StageInk.Card</c>) resolved over the
    /// stage floor they sit on, through the acrylic's own recipe (<see cref="FluentGpu.Foundation.AcrylicSpec.CompositeOver"/>: a blur of a flat
    /// floor is that floor). The same card, painted once, instead of a backdrop capture and a blur on every frame something beside it moves.
    /// Read where the card is built (a theme or arm flip re-renders it).</summary>
    public static ColorF CardOnFloor => Wavee.Theming.Roles.StageInk.Card.CompositeOver(Wavee.Theming.Roles.StageInk.Floor);

    /// <summary>The picture's rect, its title block's and the panel's, in stage DIP, for the stage layout <paramref name="L"/>.</summary>
    public readonly record struct Rects(RectF Picture, RectF Title, RectF Pane);

    public static Rects Of(Stage.Layout L)
    {
        float gutter = L.TransportInset;
        float top = Stage.Layout.TopBarH + RegionTop;
        float bottom = L.TransportTop - gutter;
        float regionH = MathF.Max(0f, bottom - top);
        float paneW = L.TheatrePaneW;
        float paneX = L.W - gutter - paneW;
        float columnW = MathF.Max(0f, paneX - 2f * gutter);
        float roomH = MathF.Max(0f, regionH - TitleGap - TitleBlockH);
        float picW = columnW, picH = MathF.Round(columnW / PictureAspect);
        if (picH > roomH) { picH = roomH; picW = MathF.Round(roomH * PictureAspect); }
        float x = gutter + MathF.Round((columnW - picW) * 0.5f);
        float y = top + MathF.Max(0f, MathF.Round((regionH - (picH + TitleGap + TitleBlockH)) * 0.5f));
        return new Rects(new RectF(x, y, picW, picH), new RectF(x, y + picH + TitleGap, picW, TitleBlockH), new RectF(paneX, top, paneW, regionH));
    }
}
