// ── Themes/Zune/Structure/Rules/StageGeometry.Arrangement.cs — where Zune's stage layouts put their parts (P5 Z4b, tiers ZT3) ───────
//
// WHICH layout is drawn stays core (the look, `Stage.LayoutRules`, the allocator's classes). What the Zune boards change is WHERE the
// parts of a layout sit, so the stage's views share one pure arrangement, read from the same signals in every view's render:
//
//   PLATE     every look but the ones below: the allocator's geometry (the cover, the titles, the pane, the caption, the 460 now-playing
//             card) over the flat plate at the foot (Z-NowPlaying, Z-Lyrics, Z-Queue-Page; the Artist layout).
//   CARD      Visualizer mode in the Card layout on a wide stage (Z-NowPlaying-Card, -Huge): the face full-bleed, ONE well plate at the
//             bottom-left gutter (Standard 600 x 376 with the 220 cover; Huge 860 x 560 with the 360 cover; Huge 4K 1120 with the 480
//             cover), the title column beside the cover, the seek row and the transport row under it; mute · volume · device at the
//             bottom-right gutter. At Huge and Huge 4K the face also carries the DISPLAY title (150/160, 200/212) under the bar.
//   COLUMN    Visualizer mode in the Large art layout on a wide stage (Z-NowPlaying-LargeArt, -Small): the cover from the column's top down
//             to the foot's gutter, the column right of it ("playing from …", the lyrics pane, the foot block of titles and controls).
//             Small's column carries no volume row: mute · volume · device sit under the block at the column's foot.
//   CENTERED  Visualizer mode in the Centered layout on a wide stage (Z-NowPlaying-Centered): the cover, the titles and the controls
//             centred down the stage (the ring around the cover when chosen), and the 76 gradient FOOT ("playing from …" left, heart ·
//             volume · device · "…" right) instead of the plate.
//   STACK     Visualizer mode in the Card layout on a Compact window (Z-NowPlaying-Compact): the cover, the titles and "playing from …"
//             down the column under the head, the controls (the seek row, the transport row, the heart · device · face · layout row) at
//             the foot.
//
// Each arrangement reads its window tier's record (`ZuneStageLayout`); one that does not fit at its tier's size tries Standard's, and
// one that still does not fit is the PLATE: the allocator's layout of the same look. A change of arrangement (a layout switch, a tier
// crossing) moves the ONE cover node on its spring and cross-fades the plates, so nothing jumps.

using FluentGpu.Foundation;

namespace Wavee.Themes.Zune;

/// <summary>Where the parts of the stage sit: the allocator's (Plate), or one of Zune's arrangements.</summary>
public enum ZuneStageForm : byte { Plate = 0, Card = 1, Column = 2, Centered = 3, Stack = 4 }

/// <summary>A rectangle in stage DIPs.</summary>
public readonly record struct ZuneRect(float X, float Y, float W, float H)
{
    public float Right => X + W;

    public float Bottom => Y + H;

    public bool IsEmpty => W <= 0f || H <= 0f;
}

/// <summary>One arrangement of the stage. Every rect is empty and <see cref="Voice"/> null in the <see cref="ZuneStageForm.Plate"/> form
/// (the allocator's geometry stands).</summary>
/// <param name="Form">The arrangement.</param>
/// <param name="Card">Card: the well plate.</param>
/// <param name="Cover">The cover.</param>
/// <param name="Titles">The title column (Card / Column: its block bottom-aligned in it).</param>
/// <param name="Source">The "playing from …" line (Centered: the foot's left column).</param>
/// <param name="Controls">The seek row and the transport row (Column: and the volume row; Stack: and the bottom row).</param>
/// <param name="Aside">Card / Small's column: mute, the volume line and the device word (Centered: the foot's right column).</param>
/// <param name="Pane">Column: the lyrics pane.</param>
/// <param name="Spectrum">Column / Centered: the strip under the cover, or the ring's box; empty: none.</param>
/// <param name="TitleSize">The title's type size.</param>
/// <param name="TitleLine">The title's line height.</param>
public readonly record struct ZuneStageArrangement(ZuneStageForm Form, ZuneRect Card, ZuneRect Cover, ZuneRect Titles, ZuneRect Source,
    ZuneRect Controls, ZuneRect Aside, ZuneRect Pane, ZuneRect Spectrum, float TitleSize, float TitleLine)
{
    /// <summary>The allocator's arrangement.</summary>
    public static ZuneStageArrangement Plate => default;

    /// <summary>The voice the arrangement draws in (null: the plate, which reads the tier's record).</summary>
    public ZuneStageVoice? Voice { get; init; }

    /// <summary>Card at Huge / Huge 4K: the display title over the face (and its artist line); empty: none.</summary>
    public ZuneRect Display { get; init; }

    /// <summary>The display block's sizes: the title and the artist line.</summary>
    public float DisplaySize { get; init; }

    public float DisplayLine { get; init; }

    public float DisplayMetaSize { get; init; }

    public float DisplayMetaLine { get; init; }

    /// <summary>Column: the gap over the seek row and over the volume row.</summary>
    public float SeekTop { get; init; }

    public float VolumeTop { get; init; }

    /// <summary>Centered: the gradient foot across the stage's bottom.</summary>
    public ZuneRect Foot { get; init; }

    /// <summary>Centered: the caption's bottom margin from the stage's foot (under the controls); 0: the allocator's.</summary>
    public float CaptionBottom { get; init; }
}

public static partial class ZuneStageGeometry
{
    // ── the Standard board's constants (the tier records carry every tier's; these name the Standard ones) ───────────────

    /// <summary>The Standard card: at most 600 wide (at least 520), padding 24, the 220 cover 24 from the titles, the seek row 16 under it.</summary>
    public const float CardMaxW = 600f, CardMinW = 520f, CardPad = 24f, CardArt = 220f, CardArtGap = 24f, CardSeekTop = 16f, CardRowH = 56f, CardRowGap = 20f;

    /// <summary>The Standard card's height.</summary>
    public const float CardH = CardPad + CardArt + CardSeekTop + SeekRowH + CardRowH + CardPad;

    /// <summary>The title column's text gap and "playing from …"'s gap over its foot.</summary>
    public const float CardTextGap = 4f, SourceGap = 8f;

    /// <summary>The Standard column's pane floor.</summary>
    public const float ColumnPaneMinH = 120f;

    /// <summary>The display title's gap under the head and above the card.</summary>
    public const float DisplayTop = 40f, DisplayGapAboveCard = 24f;

    /// <summary>The display title's tracking (-0.02 em) and its ink (0.92: Z-NowPlaying-Huge).</summary>
    public const float DisplayTracking = -20f, DisplayInkA = 0.92f;

    /// <summary>The column's least gap under the head.</summary>
    public const float ColumnHeadGap = 16f;

    // ── the decision ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The arrangement of look <paramref name="look"/> on <paramref name="layout"/> in the window tier's record, with the gallery
    /// docked or not. Pure: every stage view calls it with the same signals' values and gets the same answer.</summary>
    public static ZuneStageArrangement Arrange(in Stage.Layout layout, in Stage.Look look, ZuneStageLayout tier, bool galleryShown)
    {
        if (look.Mode != Stage.Mode.Visualizer) return ZuneStageArrangement.Plate;
        if (tier.Stack is { } stack)
            return look.IsLayout ? ZuneStageArrangement.Plate : Stack(in layout, tier, stack);
        bool wide = layout.Aspect is Stage.Aspect.Desktop or Stage.Aspect.Ultrawide;
        if (!wide) return ZuneStageArrangement.Plate;
        var standard = ZuneStageTiers.Stage(WidthTier.Standard);
        if (!look.IsLayout && !layout.IdentityCardOn(look.Mode))
        {
            var card = Card(in layout, tier, tier.Card, galleryShown);
            return card.Form == ZuneStageForm.Card || ReferenceEquals(tier.Card, standard.Card) ? card : Card(in layout, tier, standard.Card, galleryShown);
        }
        if (look.Eff == Stage.VizLayout.LargeArt)
        {
            var column = Column(in layout, in look, tier, tier.Column);
            return column.Form == ZuneStageForm.Column || ReferenceEquals(tier.Column, standard.Column) ? column : Column(in layout, in look, tier, standard.Column);
        }
        if (look.Eff == Stage.VizLayout.Centered && tier.Centered is { } centered)
        {
            var c = Centered(in layout, in look, tier, centered);
            return c.Form == ZuneStageForm.Centered || standard.Centered is null || ReferenceEquals(centered, standard.Centered)
                ? c : Centered(in layout, in look, tier, standard.Centered);
        }
        return ZuneStageArrangement.Plate;
    }

    // ── Card (Z-NowPlaying-Card, Z-NowPlaying-Huge) ──────────────────────────────────────────────────────────────────────

    static ZuneStageArrangement Card(in Stage.Layout layout, ZuneStageLayout tier, ZuneStageCard r, bool galleryShown)
    {
        var v = r.Voice;
        float g = tier.Gutter;
        float reserve = galleryShown ? layout.FaceRight(true) : r.AsideW + 2f * g;
        float w = Stage.Layout.Q4(MathF.Min(r.MaxW, layout.W - g - reserve));
        float h = r.H;
        float faceRoom = layout.H - tier.HeadH - h - 2f * g;
        if (w < r.MinW || faceRoom < r.FaceRoomMin) return ZuneStageArrangement.Plate;
        var card = new ZuneRect(g, layout.H - g - h, w, h);
        var cover = new ZuneRect(card.X + r.Pad, card.Y + r.Pad, r.Art, r.Art);
        float tx = cover.Right + r.ArtGap;
        float tw = MathF.Max(0f, card.Right - r.Pad - tx);
        // the display title over the face: under the head, left of the gallery, only where it clears the card
        ZuneRect display = default;
        if (r.DisplayH > 0f)
        {
            float dy = tier.HeadH + DisplayTop;
            float dx = MathF.Max(0f, g - r.DisplayInset);
            float dw = MathF.Max(0f, layout.W - layout.FaceRight(galleryShown) - g - dx);
            if (dy + r.DisplayH + DisplayGapAboveCard <= card.Y) display = new ZuneRect(dx, dy, dw, r.DisplayH);
        }
        return new ZuneStageArrangement(
            ZuneStageForm.Card,
            Card: card,
            Cover: cover,
            Titles: new ZuneRect(tx, cover.Y, tw, r.Art - v.SourceLine - SourceGap),
            Source: new ZuneRect(tx, cover.Bottom - v.SourceLine, tw, v.SourceLine),
            Controls: new ZuneRect(cover.X, cover.Bottom + r.SeekTop, w - 2f * r.Pad, v.SeekH + v.RowH),
            Aside: new ZuneRect(MathF.Max(0f, layout.W - g - r.AsideW), layout.H - g - v.AsideH, r.AsideW, v.AsideH),
            Pane: default,
            Spectrum: default,
            TitleSize: v.TitleSize,
            TitleLine: v.TitleLine)
        {
            Voice = v, Display = display,
            DisplaySize = r.DisplaySize, DisplayLine = r.DisplayLine, DisplayMetaSize = r.DisplayMetaSize, DisplayMetaLine = r.DisplayMetaLine,
        };
    }

    // ── Column (Z-NowPlaying-LargeArt, Z-NowPlaying-Small) ───────────────────────────────────────────────────────────────

    static ZuneStageArrangement Column(in Stage.Layout layout, in Stage.Look look, ZuneStageLayout tier, ZuneStageColumn r)
    {
        var v = r.Voice;
        float g = tier.Gutter;
        float top = MathF.Max(r.Top, tier.HeadH + ColumnHeadGap);
        float regionH = layout.H - top - g;
        if (regionH < r.BlockH + r.FootH + v.SourceLine + r.PaneTop + r.PaneMinH + r.PaneGap) return ZuneStageArrangement.Plate;
        float slot = Stage.Layout.Q4(MathF.Min(regionH, layout.W - 2f * g - r.Gap - r.MinW));
        float cover = slot;
        float stripH = 0f;
        bool ring = look.Spectrum == Stage.SpectrumStyle.Ring;
        if (ring) cover = Stage.Layout.Q4(slot / Stage.Layout.RingBoxRatio);
        else if (look.Spectrum is Stage.SpectrumStyle.Bars or Stage.SpectrumStyle.Line)
        {
            stripH = layout.StripH(in look);
            cover = Stage.Layout.Q4(MathF.Min(slot, regionH - stripH - Stage.Layout.StripGutter));
        }
        if (cover < r.CoverMin) return ZuneStageArrangement.Plate;
        float used = ring ? slot : cover;
        float x = g + used + r.Gap;
        float w = MathF.Max(0f, layout.W - x - g);
        float inset = ring ? (slot - cover) * 0.5f : 0f;
        float blockTop = layout.H - g - r.FootH - r.BlockH;
        float paneTop = top + v.SourceLine + r.PaneTop;
        float title = Math.Clamp(Stage.Layout.Q4(w * r.TitleMax / r.ControlsMaxW), r.TitleMin, r.TitleMax);
        float cw = MathF.Min(w, r.ControlsMaxW);
        ZuneRect spectrum = ring ? new ZuneRect(g, top, slot, slot)
            : stripH > 0f ? new ZuneRect(g, top + cover + Stage.Layout.StripGutter, cover, stripH) : default;
        return new ZuneStageArrangement(
            ZuneStageForm.Column,
            Card: default,
            Cover: new ZuneRect(g + inset, top + inset, cover, cover),
            Titles: new ZuneRect(x, blockTop, w, r.TitlesH),
            Source: new ZuneRect(x, top, w, v.SourceLine),
            Controls: new ZuneRect(x, blockTop + r.TitlesH, cw, r.ControlsH),
            Aside: r.VolumeRow ? default : new ZuneRect(x + MathF.Max(0f, cw - r.AsideW), layout.H - g - v.AsideH, MathF.Min(cw, r.AsideW), v.AsideH),
            Pane: new ZuneRect(x, paneTop, w, MathF.Max(0f, blockTop - r.PaneGap - paneTop)),
            Spectrum: spectrum,
            TitleSize: title,
            TitleLine: title) { Voice = v, SeekTop = r.SeekTop, VolumeTop = r.VolumeTop };
    }

    // ── Centered (Z-NowPlaying-Centered) ─────────────────────────────────────────────────────────────────────────────────

    static ZuneStageArrangement Centered(in Stage.Layout layout, in Stage.Look look, ZuneStageLayout tier, ZuneStageCentered r)
    {
        var v = r.Voice;
        float g = tier.Gutter;
        float top = tier.HeadH + Stage.Layout.Pad;
        float bottom = (layout.StripH(in look) > 0f ? layout.ContentBottom(in look) : layout.H - r.FootH) - Stage.Layout.Pad;
        float textH = r.TitleTop + v.TitleLine + r.MetaGap + v.MetaLine;
        float controlsH = r.SeekTop + v.SeekH + v.RowH;
        float capH = look.Caption ? r.CaptionGap + Stage.Layout.CenteredCaptionLine : 0f;
        bool ring = look.Spectrum == Stage.SpectrumStyle.Ring;
        float room = bottom - top - textH - controlsH - capH;
        float fit = ring ? room / Stage.Layout.RingBoxRatio : room;
        float cover = Stage.Layout.Q4(MathF.Min(MathF.Min(r.CoverMax, fit), MathF.Min(r.CoverFrac * layout.H, r.CoverWFrac * layout.W)));
        if (cover < r.CoverMin) return ZuneStageArrangement.Plate;
        float slot = ring ? Stage.Layout.Q4(cover * Stage.Layout.RingBoxRatio) : cover;
        float blockH = slot + textH + controlsH + capH;
        float blockTop = top + MathF.Max(0f, (bottom - top - blockH) * 0.5f);
        float tY = blockTop + slot + r.TitleTop;
        float cw = MathF.Min(r.SeekMaxW, layout.W - 2f * g);
        var controls = new ZuneRect((layout.W - cw) * 0.5f, tY + v.TitleLine + r.MetaGap + v.MetaLine + r.SeekTop, cw, v.SeekH + v.RowH);
        return new ZuneStageArrangement(
            ZuneStageForm.Centered,
            Card: default,
            Cover: new ZuneRect((layout.W - cover) * 0.5f, blockTop + (slot - cover) * 0.5f, cover, cover),
            Titles: new ZuneRect(Stage.Layout.Pad, tY, MathF.Max(0f, layout.W - 2f * Stage.Layout.Pad), v.TitleLine + r.MetaGap + v.MetaLine),
            Source: new ZuneRect(g, layout.H - r.FootH, r.FootSideW, r.FootH),
            Controls: controls,
            Aside: new ZuneRect(MathF.Max(0f, layout.W - g - r.FootSideW), layout.H - r.FootH, r.FootSideW, r.FootH),
            Pane: default,
            Spectrum: ring ? new ZuneRect((layout.W - slot) * 0.5f, blockTop, slot, slot) : default,
            TitleSize: v.TitleSize,
            TitleLine: v.TitleLine)
        {
            Voice = v,
            Foot = new ZuneRect(0f, layout.H - r.FootH, layout.W, r.FootH),
            CaptionBottom = look.Caption ? MathF.Max(0f, layout.H - controls.Bottom - r.CaptionGap - Stage.Layout.CenteredCaptionLine) : 0f,
        };
    }

    // ── Stack (Z-NowPlaying-Compact) ─────────────────────────────────────────────────────────────────────────────────────

    static ZuneStageArrangement Stack(in Stage.Layout layout, ZuneStageLayout tier, ZuneStageStack r)
    {
        var v = r.Voice;
        float g = tier.Gutter;
        float top = tier.HeadH + r.Top;
        float textH = r.TitleTop + v.TitleLine + v.MetaLine + v.SourceLine;
        float controlsH = r.ControlsH;
        float w = MathF.Max(0f, layout.W - 2f * g);
        float room = layout.H - top - textH - r.SeekTop - controlsH - r.BottomPad;
        float cover = Stage.Layout.Q4(MathF.Min(MathF.Min(r.CoverFrac * layout.W, w), room));
        if (cover < r.CoverMin) return ZuneStageArrangement.Plate;
        var titles = new ZuneRect(g, top + cover + r.TitleTop, w, v.TitleLine + v.MetaLine);
        return new ZuneStageArrangement(
            ZuneStageForm.Stack,
            Card: default,
            Cover: new ZuneRect(g, top, cover, cover),
            Titles: titles,
            Source: new ZuneRect(g, titles.Bottom, w, v.SourceLine),
            Controls: new ZuneRect(g, layout.H - r.BottomPad - controlsH, w, controlsH),
            Aside: default,
            Pane: default,
            Spectrum: default,
            TitleSize: v.TitleSize,
            TitleLine: v.TitleLine) { Voice = v };
    }

    // ── what the arrangement means for the face and the caption ──────────────────────────────────────────────────────────

    /// <summary>Where a face may centre its subject. A backdrop face (Large art, Centered) centres on the whole stage left of the gallery;
    /// the Card arrangement keeps it under the head (and the display title) and above the card (and above the caption rising over the
    /// card); the Stack keeps it on the cover's band; the plate arrangement is the allocator's.</summary>
    public static Stage.FaceSafeRect FaceSafe(in Stage.Layout layout, in ZuneStageArrangement a, bool galleryShown, bool captionShown, bool backdrop,
        float headH = Stage.Layout.TopBarH)
    {
        if (backdrop) return new Stage.FaceSafeRect(0f, 0f, MathF.Max(1f, layout.W - layout.FaceRight(galleryShown)), MathF.Max(1f, layout.H));
        if (a.Form == ZuneStageForm.Stack) return new Stage.FaceSafeRect(0f, a.Cover.Y, MathF.Max(1f, layout.W), MathF.Max(1f, a.Cover.H));
        if (a.Form != ZuneStageForm.Card) return layout.FaceSafe(galleryShown, captionShown);
        float top = (a.Display.IsEmpty ? headH : a.Display.Bottom) + Stage.Layout.Pad;
        float bottom = (captionShown ? a.Card.Y - Stage.Layout.Pad - layout.CaptionBlockMaxH : a.Card.Y) - Stage.Layout.Pad;
        float right = MathF.Max(1f, layout.W - layout.FaceRight(galleryShown));
        return new Stage.FaceSafeRect(0f, top, right, MathF.Max(1f, bottom - top));
    }

    /// <summary>The caption block's bottom margin from the stage's foot: above the card in the Card arrangement (a gutter over it), under
    /// the controls in the Centered one, the allocator's elsewhere.</summary>
    public static float CaptionBottom(in Stage.Layout layout, in ZuneStageArrangement a, float allocated) => a.Form switch
    {
        ZuneStageForm.Card => layout.H - a.Card.Y + Stage.Layout.Pad,
        ZuneStageForm.Centered when a.CaptionBottom > 0f => a.CaptionBottom,
        _ => allocated,
    };

    /// <summary>The caption shows in this arrangement: the Stack has no room for it (the titles and the controls fill the column).</summary>
    public static bool CaptionAllowed(in ZuneStageArrangement a) => a.Form != ZuneStageForm.Stack;
}
