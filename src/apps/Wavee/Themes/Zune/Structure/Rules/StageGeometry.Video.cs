// ── Themes/Zune/Structure/Rules/StageGeometry.Video.cs — the numbers of Zune's stage in Video mode (Cinema and Theatre) ─────────────────
//
// Zune shows a music video the way the Zune software showed one full screen: the PICTURE is the hero, the chrome is flat and quiet.
//
//   CINEMA   the picture edge to edge. With the chrome: the stage's head (the pivots, "video" leading them, and the "switch to audio" word) over
//            the top veil; at the foot the PLATE the audio stage stands on (the same flat veil, the tier's seek row and control row, the accent
//            disc), the seek line across the picture and one row of plateless glyphs; above the plate the "up next" strip (a display-face
//            heading over flat, square-cornered art) and, above that, the accent artist line over the title in the light display face.
//   THEATRE  under the head the picture's 16:9 box on the left gutter (Zune aligns, it does not centre), the open list beside it on the right
//            (the Up next column's own text rows, or the lyrics under a "lyrics" heading); under the picture the accent artist line, the TITLE
//            in the large light display face (one line that bleeds out under a fade, never shrunk), the quiet "playing from …" line, then
//            "about the artist": a display-face heading, the round portrait, the name and listeners, the facts and three lines of the bio.
//   AUDIO    an audio-only track in Video mode: the picture's place is ONE FLAT FIELD of the cover's colour (Zune's flat colour, no gradient),
//            the cover inset, the accent artist and the light display title under it, the synced lyrics beside it.
//   END      "up next in 5" in the light display face over the next track's flat 16:9 still (a 4 accent progress line along its foot) and its
//            accent artist line, display title, "from …" and the two words: "play now" (the accent plate) and "cancel".
//
// Every size is the Standard tier's here, scaled per window tier by the tier's record (`ZuneStageVideo.Px`): Compact 0.75, Small 0.875,
// Standard 1, Huge 1.25, Huge 4K 1.75. The bar reads the tier's own plate (`ZuneStageLayout`: the seek row, the control row, the glyph box,
// the disc), so the video's plate is the audio stage's plate. Literals live here (WVTH003 allows `Rules\**`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Video mode in one window tier: the scale its Standard numbers draw at, the shelf's card, the end card's still.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="K">The scale of <see cref="ZuneStageVideoGeometry"/>'s Standard numbers at this tier.</param>
/// <param name="ShelfCardW">The Up next strip's card width (its 16:9 still); 0: no strip (Compact).</param>
/// <param name="EndThumbW">The end card's 16:9 still in Cinema (Theatre fits it to the picture's box).</param>
public sealed record ZuneStageVideo(WidthTier Tier, float K, float ShelfCardW, float EndThumbW)
{
    /// <summary><paramref name="standard"/> at this tier's scale, rounded to the DIP.</summary>
    public float Px(float standard) => MathF.Round(standard * K);

    /// <summary>A DISPLAY size (the display face's titles and headings) at this tier: grown with the tier, never below Standard's (Zune's large type is
    /// never shrunk to fit: the owner's rule).</summary>
    public float Dx(float standard) => MathF.Round(standard * MathF.Max(1f, K));
}

/// <summary>The numbers and rects of Zune's stage in Video mode.</summary>
public static class ZuneStageVideoGeometry
{
    // ── the tiers ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static ZuneStageVideo Compact { get; } = new(WidthTier.Compact, 0.75f, 0f, 240f);

    public static ZuneStageVideo Small { get; } = new(WidthTier.Small, 0.875f, 168f, 320f);

    public static ZuneStageVideo Standard { get; } = new(WidthTier.Standard, 1f, 208f, 400f);

    public static ZuneStageVideo Huge { get; } = new(WidthTier.Huge, 1.25f, 256f, 480f);

    public static ZuneStageVideo Huge4K { get; } = new(WidthTier.Huge4K, 1.75f, 360f, 640f);

    // ── motion ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The picture's move between Cinema and Theatre: the stage's pane spring (0.45 s, 0.90), Reflow.</summary>
    public const float MoveResponse = 0.45f, MoveDamping = 0.90f;

    /// <summary>Theatre's panel slides in from the right this far (the Zune slide), the title block and the strip rise this far with the chrome.</summary>
    public const float PaneSlide = 48f, RiseDy = 16f, BarEnterDy = 8f;

    /// <summary>How long after Theatre closes Cinema's letterbox ground comes back (ms): the growing picture has filled the stage by then.</summary>
    public const float GroundReturnMs = 600f;

    // ── the title (Cinema: over the picture; Theatre: under it) ───────────────────────────────────────────────────────────────

    /// <summary>The accent artist line (the display face, 600), the Cinema title over the picture (the light display face, two lines) and the
    /// Theatre title under it (one line, bleeding out under a fade).</summary>
    public const float ArtistSize = 20f, ArtistLine = 28f, CinemaTitleSize = 64f, CinemaTitleLine = 72f, TheatreTitleSize = 48f, TheatreTitleLine = 64f;

    public const int CinemaTitleLines = 2;

    public const ushort ArtistWeight = 600, TitleWeight = 300;

    /// <summary>The display face's tracking (thousandths of an em), as the stage's other display titles.</summary>
    public const float TitleTracking = -12f;

    /// <summary>"playing from …" (13/18) and the gaps of the block: the artist to the title, the title to the source line.</summary>
    public const float SourceSize = 13f, SourceLine = 18f, TitleGap = 0f, SourceGap = 8f;

    /// <summary>The Cinema title block's widest share of the stage, and its gap above the strip's place or the plate.</summary>
    public const float CinemaTitleMaxWFrac = 0.66f, CinemaTitleAbove = 24f;

    /// <summary>The fade the Theatre title bleeds out under at the picture's right edge.</summary>
    public const float TitleFadeBand = 48f;

    // ── the head and the plate over the picture ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The veil under the head over the picture: this tall, from the stage veil at <see cref="TopVeilA"/> to nothing.</summary>
    public const float TopVeilH = 120f, TopVeilA = 0.72f;

    /// <summary>The plate's veil (the audio stage's plate over a face: 0.85) and the fade above it into the picture.</summary>
    public const float PlateA = 0.85f, PlateFadeH = 96f;

    /// <summary>The veil under the Cinema title (from nothing at its top to <see cref="TitleVeilA"/> at the plate): this share of the stage.</summary>
    public const float TitleVeilFrac = 0.5f, TitleVeilA = 0.6f;

    /// <summary>Theatre: the plate's inset inside the picture's box, as a share of the tier's gutter.</summary>
    public const float TheatreInsetShare = 0.5f;

    /// <summary>The "1:14 / 3:04" time's least width (the fold's budget) and the gap around it.</summary>
    public const float TimeBudgetW = 96f, TimeGap = 16f;

    /// <summary>The device word's budget in the fold (its own component sizes it at the plate's DeviceMinW..DeviceMaxW).</summary>
    public const float DeviceBudgetW = 120f;

    /// <summary>The fold levels, in the order the row gives controls away: the volume line, shuffle · repeat · the device word, the mute, the time.</summary>
    public const int FoldNone = 0, FoldSlider = 1, FoldSatellites = 2, FoldMute = 3, FoldTime = 4;

    /// <summary>The plate's height over the picture: the tier's seek row and control row.</summary>
    public static float BarH(ZuneStageLayout t) => t.SeekRowH + t.PlateRowH;

    /// <summary>The plate's side inset: the tier's gutter in Cinema, half of it inside Theatre's box.</summary>
    public static float BarInset(ZuneStageLayout t, bool theatre) => theatre ? MathF.Round(t.Gutter * TheatreInsetShare) : t.Gutter;

    /// <summary>The disc in the control row (the tier's, capped by the row).</summary>
    public static float Disc(ZuneStageLayout t) => MathF.Min(t.Disc, t.PlateRowH);

    /// <summary>The left side at <paramref name="fold"/>: previous · the disc · next (· mute (· the volume line)) (· the time).</summary>
    public static float LeftW(ZuneStageLayout t, int fold)
        => 2f * t.GlyphBox + Disc(t) + 2f * t.RightGap + (fold < FoldMute ? t.GlyphBox + t.RightGap : 0f)
           + (fold < FoldSlider && t.VolumeW > 0f ? t.VolumeW + t.RightGap : 0f) + (fold < FoldTime ? TimeGap + TimeBudgetW : 0f);

    /// <summary>The right side at <paramref name="fold"/>: heart (· shuffle · repeat · device) · "…" · settings (· theatre) · exit.</summary>
    public static float RightW(ZuneStageLayout t, int fold, bool theatre)
    {
        int glyphs = 4 + (fold < FoldSatellites ? 2 : 0) + (theatre ? 1 : 0);
        return glyphs * t.GlyphBox + (fold < FoldSatellites ? DeviceBudgetW + t.RightGap : 0f) + (glyphs - 1) * t.RightGap;
    }

    /// <summary>The least fold that seats the row in a picture <paramref name="pictureW"/> wide at <paramref name="inset"/>.</summary>
    public static int Fold(ZuneStageLayout t, float pictureW, float inset, bool theatre)
    {
        for (int f = FoldNone; f < FoldTime; f++)
            if (2f * inset + LeftW(t, f) + TimeGap + RightW(t, f, theatre) <= pictureW) return f;
        return FoldTime;
    }

    // ── the Up next strip (Cinema) ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"up next" (the display face, light 20/28), its gap to the cards, the gap between cards, the most cards, the strip's foot above the plate.</summary>
    public const float ShelfHeadSize = 20f, ShelfHeadLine = 28f, ShelfHeadGap = 8f, ShelfCardGap = 16f, ShelfAbovePlate = 16f;

    public const int ShelfMaxCards = 8;

    /// <summary>A card: the still's 16:9, the title (15/20) and the artists (13/18) under it, the accent line under what plays next.</summary>
    public const float ThumbAspect = 16f / 9f, ShelfTextGap = 8f, ShelfTitleSize = 15f, ShelfTitleLine = 20f, ShelfMetaSize = 13f, ShelfMetaLine = 18f, NextLineH = 4f;

    /// <summary>How far the cards narrow to keep their count across a resize before one leaves.</summary>
    public const float ShelfHoldShare = 0.85f;

    /// <summary>The still's height at card width <paramref name="cardW"/>.</summary>
    public static float ThumbH(float cardW) => MathF.Round(cardW / ThumbAspect);

    /// <summary>A card's art width: a video's still is the card's width, an audio-only cover is square at the still's height.</summary>
    public static float ArtW(float cardW, bool video) => video ? cardW : ThumbH(cardW);

    /// <summary>The strip's height at <paramref name="v"/> (0: none).</summary>
    public static float ShelfH(ZuneStageVideo v)
        => v.ShelfCardW <= 0f ? 0f : v.Dx(ShelfHeadLine) + v.Px(ShelfHeadGap) + ThumbH(v.ShelfCardW) + v.Px(ShelfTextGap) + v.Px(ShelfTitleLine) + v.Px(ShelfMetaLine);

    /// <summary>The cards <paramref name="width"/> seats from the art widths in order, holding <paramref name="held"/> while they fit at
    /// <see cref="ShelfHoldShare"/>, then narrowed together by <c>Scale</c>. At least one card when there is any.</summary>
    public static (int N, float Scale) Seat(float width, ReadOnlySpan<float> widths, int held, float gap)
    {
        int fit = Fits(width, widths, 1f, gap);
        if (held > fit && held <= widths.Length && Fits(width, widths, ShelfHoldShare, gap) >= held)
        {
            float sum = 0f;
            for (int i = 0; i < held; i++) sum += widths[i];
            return (held, sum > 0f ? MathF.Min(1f, (width - (held - 1) * gap) / sum) : 1f);
        }
        return (fit, fit == 1 && widths[0] > width ? MathF.Max(0f, width / widths[0]) : 1f);
    }

    static int Fits(float width, ReadOnlySpan<float> widths, float share, float gap)
    {
        float x = 0f;
        int n = 0;
        for (; n < widths.Length; n++)
        {
            float need = (n > 0 ? gap : 0f) + widths[n] * share;
            if (x + need > width) break;
            x += need;
        }
        return widths.Length > 0 ? Math.Max(1, n) : 0;
    }

    /// <summary>The top of Cinema's lower block (the strip's reserved place where the tier has one, else the plate).</summary>
    public static float LowerBlockTop(in Stage.Layout L, ZuneStageLayout t, bool shelf)
    {
        float shelfH = shelf ? ShelfH(t.Video) : 0f;
        return L.H - BarH(t) - (shelfH > 0f ? t.Video.Px(ShelfAbovePlate) + shelfH : 0f);
    }

    // ── Theatre ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The region's top under the head, and the picture's gap to the title block.</summary>
    public const float RegionTop = 16f, UnderGap = 16f;

    /// <summary>"about the artist" (the display face, light 26/32), its gap under the title block and to the artist row, the 48 row (the round
    /// portrait, the name 17/22 over the listeners 13/18), the facts and bio lines (15/22), three of the bio, 4 apart, and the "read more" word.</summary>
    public const float AboutGap = 32f, AboutHeadSize = 26f, AboutHeadLine = 32f, AboutHeadGap = 12f, AboutRowH = 48f, AboutImage = 48f, AboutRowGap = 12f,
        AboutNameSize = 17f, AboutNameLine = 22f, AboutMetaSize = 13f, AboutMetaLine = 18f, AboutDescGap = 12f, AboutBodySize = 15f, AboutBodyLine = 22f,
        AboutLineGap = 4f, AboutMoreLine = 22f;

    public const int AboutBioLines = 3;

    /// <summary>The title block's height under the picture (the artist line, the title, the source line).</summary>
    public static float TitleBlockH(ZuneStageVideo v) => v.Dx(ArtistLine) + v.Px(TitleGap) + v.Dx(TheatreTitleLine) + v.Px(SourceGap) + v.Px(SourceLine);

    /// <summary>The about block's height, reserved whole from the first frame.</summary>
    public static float AboutH(ZuneStageVideo v)
        => v.Dx(AboutHeadLine) + v.Px(AboutHeadGap) + v.Px(AboutRowH) + v.Px(AboutDescGap) + v.Px(AboutBodyLine) + v.Px(AboutLineGap)
           + AboutBioLines * v.Px(AboutBodyLine) + v.Px(AboutLineGap) + v.Px(AboutMoreLine);

    /// <summary>What the picture leaves under itself: its gap, the title block, the about block's gap and the about block.</summary>
    public static float UnderH(ZuneStageVideo v) => v.Px(UnderGap) + TitleBlockH(v) + v.Px(AboutGap) + AboutH(v);

    /// <summary>Theatre's rects: the picture, its title block, the open list beside it, the about block (0 wide where it does not fit whole).</summary>
    public readonly record struct TheatreRects(RectF Picture, RectF Title, RectF Pane, RectF About)
    {
        public bool AboutFits => About.W > 0f;
    }

    /// <summary>The picture is the largest 16:9 box the left column holds that still leaves room under it for the title and the about block, on
    /// the left gutter, its top <see cref="RegionTop"/> under the head; the open list stands on the right at the tier's panel width, from the
    /// picture's top edge to the stage's foot. One continuous function of the stage's size.</summary>
    public static TheatreRects Theatre(in Stage.Layout L, ZuneStageLayout t)
    {
        var v = t.Video;
        float gutter = t.Gutter;
        float top = t.HeadH + v.Px(RegionTop);
        float bottom = L.H - gutter;
        float paneW = L.TheatrePaneW;
        float paneX = L.W - gutter - paneW;
        float columnW = MathF.Max(0f, paneX - 2f * gutter);
        float roomH = MathF.Max(0f, bottom - top - UnderH(v));
        float picW = columnW, picH = MathF.Round(columnW / ThumbAspect);
        if (picH > roomH) { picH = roomH; picW = MathF.Round(roomH * ThumbAspect); }
        float x = gutter, y = top;
        float titleY = y + picH + v.Px(UnderGap);
        float aboutY = titleY + TitleBlockH(v) + v.Px(AboutGap);
        float aboutH = AboutH(v);
        // the title and the about block keep the column's width (the type is the column's, not the picture's)
        var about = picH > 0f && aboutY + aboutH <= bottom + 0.5f ? new RectF(x, aboutY, columnW, aboutH) : default;
        return new TheatreRects(new RectF(x, y, picW, picH), new RectF(x, titleY, columnW, TitleBlockH(v)),
            new RectF(paneX, y, paneW, MathF.Max(0f, L.H - y)), about);
    }

    /// <summary>The picture's rect: the whole stage in Cinema, the left column's 16:9 box in Theatre.</summary>
    public static RectF Picture(in Stage.Layout L, ZuneStageLayout t, bool theatre) => theatre ? Theatre(in L, t).Picture : new RectF(0f, 0f, L.W, L.H);

    /// <summary>Theatre's page: the floor tinted flat toward the track's colour by this much (Zune's flat colour, one still fill per track).</summary>
    public const float PageTint = 0.10f;

    /// <summary>The open list's heading row ("lyrics" over the Theatre lyrics: the display face, light 26/32) and its gap.</summary>
    public const float ListHeadH = 48f, ListHeadSize = 26f, ListHeadLine = 32f;

    // ── the audio face ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Cinema: the face's top under the head and its gap above the lower block; the cover's gap to the artist line; the title (the light
    /// display face, two lines); the gap between the cover column and the lyrics (a share of the stage); the cover's floor.</summary>
    public const float FaceTop = 32f, FaceBottomGap = 24f, FaceTitleGap = 16f, FaceTitleSize = 40f, FaceTitleLine = 48f, FaceLyricsGapFrac = 0.05f, FaceCoverMin = 168f;

    public const int FaceTitleLines = 2;

    /// <summary>Theatre: the cover's share of the box's height, its insets (shares of the box), the gap to the lyrics and the lyrics' top and right.</summary>
    public const float FaceTheatreCoverH = 0.62f, FaceTheatreInsetX = 0.048f, FaceTheatreInsetY = 0.09f, FaceTheatreGap = 0.048f, FaceTheatreLyricsTop = 0.09f,
        FaceTheatreRight = 0.048f, FaceTheatreCoverMaxW = 0.45f;

    /// <summary>Theatre: the share of the plate's fade the face keeps clear of (the fade's dense half).</summary>
    public const float FaceFadeShare = 0.5f;

    /// <summary>The cover's decode: the larger above this side.</summary>
    public const float FaceDecodeSmallW = 400f;

    /// <summary>The FLAT FIELD: the cover's first colour pulled this far toward its deep tone, then veiled toward the arm's floor in
    /// <see cref="FieldVeilStep"/> steps until the lyrics' other lines (the unsung ink, the secondary ink) read at <see cref="FieldContrast"/>.</summary>
    public const float FieldDeep = 0.35f, FieldContrast = 4.5f, FieldVeilStep = 0.1f;

    /// <summary>The field's one colour for <paramref name="p"/> on <paramref name="kind"/>'s arm.</summary>
    public static ColorF Field(in Visualizer.Palette p, ThemeKind kind)
    {
        var arm = StageInk.Arm(kind);
        var raw = Raw(p);
        var floor = arm.Floor with { A = 1f };
        var other = arm.Ink with { A = Lyrics.Wipe.UnsungAlpha };
        for (float veil = 0f; veil < 1f; veil += FieldVeilStep)
        {
            var c = ColorF.Lerp(raw, floor, veil);
            if (ColorContrast.Ratio(ColorContrast.Flatten(other, c), c) >= FieldContrast && ColorContrast.Ratio(ColorContrast.Flatten(arm.InkSecondary, c), c) >= FieldContrast)
                return c;
        }
        return floor;
    }

    static ColorF Raw(in Visualizer.Palette p) => ColorF.Lerp(p.A with { A = 1f }, p.Deep with { A = 1f }, FieldDeep);

    // ── the end card ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The veil over the last frame.</summary>
    public const float EndDimA = 0.6f;

    /// <summary>"up next in 5" (the display face, light 26/32), its gap to the row, the row's gap, the text column's widest and narrowest.</summary>
    public const float EndCountSize = 26f, EndCountLine = 32f, EndCountGap = 20f, EndRowGap = 32f, EndTextMaxW = 400f, EndTextMinW = 200f;

    /// <summary>The text column: the accent artist line (20/28), the title (the light display face 40/48, two lines), "from …" (13/18), 4 apart;
    /// the words 16 under it, 24 apart, the play plate's padding; the progress line under the still.</summary>
    public const float EndTitleSize = 40f, EndTitleLine = 48f, EndFromSize = 13f, EndFromLine = 18f, EndTextGap = 4f, EndWordsAbove = 16f, EndWordGap = 24f,
        EndPlatePadX = 16f, EndWordSize = 15f, EndWordLine = 20f, EndProgressH = 4f, EndProgressTrackA = 0.2f;

    public const int EndTitleLines = 2;

    /// <summary>Theatre: the card's padding inside the picture's box and the still's largest share of the box's width.</summary>
    public const float EndTheatrePad = 24f, EndTheatreThumbShare = 0.4f;

    /// <summary>The still's width in a region <paramref name="regionW"/> wide.</summary>
    public static float EndThumbW(ZuneStageVideo v, float regionW, bool theatre)
    {
        float w = MathF.Min(v.EndThumbW, regionW - v.Px(EndRowGap) - v.Px(EndTextMinW));
        if (theatre) w = MathF.Min(w, MathF.Round(regionW * EndTheatreThumbShare));
        return MathF.Max(0f, MathF.Round(w));
    }

    /// <summary>The text column's width beside the still.</summary>
    public static float EndTextW(ZuneStageVideo v, float thumbW, float regionW) => MathF.Max(0f, MathF.Min(v.Px(EndTextMaxW), regionW - thumbW - v.Px(EndRowGap)));
}
