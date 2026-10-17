// ── Themes/Zune/Structure/Rules/StageGeometry.Arrangement.cs — where Zune's Card and Large art layouts put their parts (P5 Z4b) ──────
//
// WHICH layout is drawn stays core (the look, `Stage.LayoutRules`, the allocator's classes). What the Zune boards change is WHERE the
// parts of two layouts sit, so the stage's views share one pure arrangement, read from the same signals in every view's render:
//
//   PLATE    every look but the two below: the allocator's geometry (the cover, the titles, the pane, the caption, the 460 now-playing
//            card) over the flat 112 plate at the foot (Z-NowPlaying, Z-Lyrics, Z-Queue-Page).
//   CARD     Visualizer mode in the Card layout on a wide stage (Z-NowPlaying-Card): the face full-bleed, and ONE well plate at the
//            bottom-left gutter, 600 x 376: padding 24, the 220 cover with the title column beside it (bottom-aligned: "now playing",
//            the 40 title, the accent artist line, "playing from …"), then 16 down the 36 seek row and the 56 transport row (shuffle ·
//            previous · disc · next · repeat, heart · "..." at the right). Mute · the volume line · the device word sit at the
//            bottom-right gutter, 36 tall. The lyric caption rises above the card.
//   COLUMN   Visualizer mode in the Large art layout on a wide stage (Z-NowPlaying-LargeArt): the cover runs from 96 down to the foot's
//            gutter at the left gutter, and the column right of it, 56 away: "playing from …" at its top, the lyrics pane under it (the
//            layout's pane: Large art keeps its lyrics), and at the foot the 368 block: the accent artist line (28/36), the title
//            (120/120 at the board's 532 column, scaled with the column), then 40 down the seek row, the transport row and the volume
//            row. The spectrum the user chose sits under the cover (bars, line) or rings it (the cover shrinks into its ring box).
//
// An arrangement that does not fit the stage (too narrow beside an open gallery, too short) is the PLATE arrangement: the allocator's
// layout of the same look. A change of arrangement moves the ONE cover node on its spring and cross-fades the plates, so nothing jumps.

namespace Wavee.Themes.Zune;

/// <summary>Where the parts of the stage sit: the allocator's (Plate), or the Zune Card and Large art arrangements.</summary>
public enum ZuneStageForm : byte { Plate = 0, Card = 1, Column = 2 }

/// <summary>A rectangle in stage DIPs.</summary>
public readonly record struct ZuneRect(float X, float Y, float W, float H)
{
    public float Right => X + W;

    public float Bottom => Y + H;

    public bool IsEmpty => W <= 0f || H <= 0f;
}

/// <summary>One arrangement of the stage. Every rect is empty in the <see cref="ZuneStageForm.Plate"/> form (the allocator's geometry
/// stands).</summary>
/// <param name="Form">The arrangement.</param>
/// <param name="Card">Card: the well plate.</param>
/// <param name="Cover">Card / Column: the cover.</param>
/// <param name="Titles">Card / Column: the title column (its block bottom-aligned in it).</param>
/// <param name="Source">Card / Column: the "playing from …" line.</param>
/// <param name="Controls">Card / Column: the seek row and the transport row (Column: and the volume row).</param>
/// <param name="Aside">Card: mute, the volume line and the device word.</param>
/// <param name="Pane">Column: the lyrics pane.</param>
/// <param name="Spectrum">Column: the strip under the cover, or the ring's box; empty: none.</param>
/// <param name="TitleSize">Card / Column: the title's type size.</param>
/// <param name="TitleLine">Card / Column: the title's line height.</param>
public readonly record struct ZuneStageArrangement(ZuneStageForm Form, ZuneRect Card, ZuneRect Cover, ZuneRect Titles, ZuneRect Source,
    ZuneRect Controls, ZuneRect Aside, ZuneRect Pane, ZuneRect Spectrum, float TitleSize, float TitleLine)
{
    /// <summary>The allocator's arrangement.</summary>
    public static ZuneStageArrangement Plate => default;
}

public static partial class ZuneStageGeometry
{
    // ── the Card arrangement (Z-NowPlaying-Card) ─────────────────────────────────────────────────────────────────────────

    /// <summary>The card: at most 600 wide (at least 520, else the plate arrangement), padding 24, the 220 cover 24 from the titles,
    /// the seek row 16 under the cover, the 56 transport row 20 apart.</summary>
    public const float CardMaxW = 600f, CardMinW = 520f, CardPad = 24f, CardArt = 220f, CardArtGap = 24f, CardSeekTop = 16f, CardRowH = 56f, CardRowGap = 20f;

    /// <summary>The card's height: its padding, the cover, the seek row and the transport row.</summary>
    public const float CardH = CardPad + CardArt + CardSeekTop + SeekRowH + CardRowH + CardPad;

    /// <summary>The least height the face keeps above the card (else the plate arrangement).</summary>
    public const float CardFaceRoomMin = 120f;

    /// <summary>The card's title column: "now playing" 13/18, the title 40/48 (300, -0.5 px), the artist line 20/28 in the accent, 4 apart.</summary>
    public const float CardEyebrowSize = 13f, CardEyebrowLine = 18f, CardTitleSize = 40f, CardTitleLine = 48f, CardTitleTracking = -12f,
        CardMetaSize = 20f, CardMetaLine = 28f, CardTextGap = 4f;

    /// <summary>"playing from …" (13/18) and its gap over the title column's foot.</summary>
    public const float SourceH = 18f, SourceGap = 8f;

    /// <summary>The aside at the bottom-right gutter: mute, the volume line and the device word, 36 tall, 8 apart.</summary>
    public const float AsideW = 320f, AsideH = 36f;

    // ── the Column arrangement (Z-NowPlaying-LargeArt) ───────────────────────────────────────────────────────────────────

    /// <summary>The cover's and the column's top (the 48 bar and 48 under it), the gap between the cover and the column, the column's floor
    /// and the cover's (else the plate arrangement), and the widest the controls run (the board's 532).</summary>
    public const float ColumnTop = 96f, ColumnGap = 56f, ColumnMinW = 420f, ColumnCoverMin = 240f, ColumnControlsMaxW = 532f;

    /// <summary>The foot block: the titles (the accent artist line 28/36 at 600, the title) then the controls (40 down the seek row, the 56
    /// transport row, 16 down the 36 volume row).</summary>
    public const float ColumnTitlesH = 184f, ColumnMetaSize = 28f, ColumnMetaLine = 36f, ColumnSeekTop = 40f, ColumnVolumeTop = 16f;

    public const float ColumnControlsH = ColumnSeekTop + SeekRowH + CardRowH + ColumnVolumeTop + AsideH;

    public const float ColumnBlockH = ColumnTitlesH + ColumnControlsH;

    /// <summary>The title: 120/120 at 200 (-2 px) on the board's 532 column, scaled with the column, at least 64.</summary>
    public const float ColumnTitleMax = 120f, ColumnTitleMin = 64f, ColumnTitleTracking = -17f;

    /// <summary>The lyrics pane under "playing from …": its gap under the line, its gap over the foot block, its floor.</summary>
    public const float ColumnPaneTop = 16f, ColumnPaneGap = 24f, ColumnPaneMinH = 120f;

    // ── the decision ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The arrangement of look <paramref name="look"/> on <paramref name="layout"/> in the window tier's record, with the gallery
    /// docked or not. Pure: every stage view calls it with the same signals' values and gets the same answer.</summary>
    public static ZuneStageArrangement Arrange(in Stage.Layout layout, in Stage.Look look, ZuneStageLayout tier, bool galleryShown)
    {
        bool wide = layout.Aspect is Stage.Aspect.Desktop or Stage.Aspect.Ultrawide;
        if (!wide || look.Mode != Stage.Mode.Visualizer) return ZuneStageArrangement.Plate;
        if (!look.IsLayout && !layout.IdentityCardOn(look.Mode)) return Card(in layout, tier, galleryShown);
        if (look.Eff == Stage.VizLayout.LargeArt) return Column(in layout, in look, tier);
        return ZuneStageArrangement.Plate;
    }

    static ZuneStageArrangement Card(in Stage.Layout layout, ZuneStageLayout tier, bool galleryShown)
    {
        float g = tier.Gutter;
        float reserve = galleryShown ? layout.FaceRight(true) : AsideW + 2f * g;
        float w = Stage.Layout.Q4(MathF.Min(CardMaxW, layout.W - g - reserve));
        float faceRoom = layout.H - Stage.Layout.TopBarH - CardH - 2f * g;
        if (w < CardMinW || faceRoom < CardFaceRoomMin) return ZuneStageArrangement.Plate;
        var card = new ZuneRect(g, layout.H - g - CardH, w, CardH);
        var cover = new ZuneRect(card.X + CardPad, card.Y + CardPad, CardArt, CardArt);
        float tx = cover.Right + CardArtGap;
        float tw = MathF.Max(0f, card.Right - CardPad - tx);
        return new ZuneStageArrangement(
            ZuneStageForm.Card,
            Card: card,
            Cover: cover,
            Titles: new ZuneRect(tx, cover.Y, tw, CardArt - SourceH - SourceGap),
            Source: new ZuneRect(tx, cover.Bottom - SourceH, tw, SourceH),
            Controls: new ZuneRect(cover.X, cover.Bottom + CardSeekTop, w - 2f * CardPad, SeekRowH + CardRowH),
            Aside: new ZuneRect(MathF.Max(0f, layout.W - g - AsideW), layout.H - g - AsideH, AsideW, AsideH),
            Pane: default,
            Spectrum: default,
            TitleSize: CardTitleSize,
            TitleLine: CardTitleLine);
    }

    static ZuneStageArrangement Column(in Stage.Layout layout, in Stage.Look look, ZuneStageLayout tier)
    {
        float g = tier.Gutter;
        float regionH = layout.H - ColumnTop - g;
        if (regionH < ColumnBlockH + SourceH + ColumnPaneTop + ColumnPaneMinH + ColumnPaneGap) return ZuneStageArrangement.Plate;
        float slot = Stage.Layout.Q4(MathF.Min(regionH, layout.W - 2f * g - ColumnGap - ColumnMinW));
        float cover = slot;
        float stripH = 0f;
        bool ring = look.Spectrum == Stage.SpectrumStyle.Ring;
        if (ring) cover = Stage.Layout.Q4(slot / Stage.Layout.RingBoxRatio);
        else if (look.Spectrum is Stage.SpectrumStyle.Bars or Stage.SpectrumStyle.Line)
        {
            stripH = layout.StripH(in look);
            cover = Stage.Layout.Q4(MathF.Min(slot, regionH - stripH - Stage.Layout.StripGutter));
        }
        if (cover < ColumnCoverMin) return ZuneStageArrangement.Plate;
        float used = ring ? slot : cover;
        float x = g + used + ColumnGap;
        float w = MathF.Max(0f, layout.W - x - g);
        float inset = ring ? (slot - cover) * 0.5f : 0f;
        float blockTop = layout.H - g - ColumnBlockH;
        float paneTop = ColumnTop + SourceH + ColumnPaneTop;
        float title = Math.Clamp(Stage.Layout.Q4(w * ColumnTitleMax / ColumnControlsMaxW), ColumnTitleMin, ColumnTitleMax);
        ZuneRect spectrum = ring ? new ZuneRect(g, ColumnTop, slot, slot)
            : stripH > 0f ? new ZuneRect(g, ColumnTop + cover + Stage.Layout.StripGutter, cover, stripH) : default;
        return new ZuneStageArrangement(
            ZuneStageForm.Column,
            Card: default,
            Cover: new ZuneRect(g + inset, ColumnTop + inset, cover, cover),
            Titles: new ZuneRect(x, blockTop, w, ColumnTitlesH),
            Source: new ZuneRect(x, ColumnTop, w, SourceH),
            Controls: new ZuneRect(x, blockTop + ColumnTitlesH, MathF.Min(w, ColumnControlsMaxW), ColumnControlsH),
            Aside: default,
            Pane: new ZuneRect(x, paneTop, w, MathF.Max(0f, blockTop - ColumnPaneGap - paneTop)),
            Spectrum: spectrum,
            TitleSize: title,
            TitleLine: title);
    }

    // ── what the arrangement means for the face and the caption ──────────────────────────────────────────────────────────

    /// <summary>Where a face may centre its subject. A backdrop face (Large art, Centered) centres on the whole stage left of the gallery;
    /// the Card arrangement keeps it under the top bar and above the card (and above the caption rising over the card); the plate
    /// arrangement is the allocator's.</summary>
    public static Stage.FaceSafeRect FaceSafe(in Stage.Layout layout, in ZuneStageArrangement a, bool galleryShown, bool captionShown, bool backdrop)
    {
        if (backdrop) return new Stage.FaceSafeRect(0f, 0f, MathF.Max(1f, layout.W - layout.FaceRight(galleryShown)), MathF.Max(1f, layout.H));
        if (a.Form != ZuneStageForm.Card) return layout.FaceSafe(galleryShown, captionShown);
        float top = Stage.Layout.TopBarH + Stage.Layout.Pad;
        float bottom = (captionShown ? a.Card.Y - Stage.Layout.Pad - layout.CaptionBlockMaxH : a.Card.Y) - Stage.Layout.Pad;
        float right = MathF.Max(1f, layout.W - layout.FaceRight(galleryShown));
        return new Stage.FaceSafeRect(0f, top, right, MathF.Max(1f, bottom - top));
    }

    /// <summary>The caption block's bottom margin from the stage's foot: above the card in the Card arrangement (a gutter over it), the
    /// allocator's elsewhere.</summary>
    public static float CaptionBottom(in Stage.Layout layout, in ZuneStageArrangement a, float allocated)
        => a.Form == ZuneStageForm.Card ? layout.H - a.Card.Y + Stage.Layout.Pad : allocated;
}
