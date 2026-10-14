// ── Themes/Classic/Structure/Rules/ClassicQueueRules.cs — the numbers and decisions of Classic's queue surfaces (themes plan 6.1, T7d) ──
//
// What `Entities/Queue.UI.cs` spelled beside the pictures, moved verbatim with them: the rail panel's art edges, header and "Show more"
// heights, the row's height under each skin (the reorder lane's drag maths needs it EXACT, and the surface's floor is not it), the
// autoplay dim, which adapter feeds a row and where a click on the playing card goes, and the stage column's row, Next row, grip, duration
// and header metrics (every one of them scales by the stage's queue scale, which the view applies). They are data and pure decisions, not
// state: the core hosts (`Queue.RailPanelHost`, `Queue.StagePaneHost`) keep the lane, the pages and the verbs, and read none of them.
// WVTH003 allows literals only here, in `Rules\**`.

using FluentGpu.Dsl;

namespace Wavee.Themes.Classic;

/// <summary>The constants and pure decisions of Classic's queue surfaces.</summary>
public static class ClassicQueueRules
{
    // ── the rail panel ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The queue rows' art edge, the now-playing card's, and the hairline row's height (no art).</summary>
    public const float QueueArt = 34f, NowArt = 44f, HairlineExtent = 44f;

    /// <summary>A section header's eyebrow row, and the "Show more" row.</summary>
    public const float HeaderRowH = 20f, MoreRowH = 40f;

    /// <summary>A section header's slot: the eyebrow row between its paddings (36)…</summary>
    public static float HeaderExtent => Spacing.M + HeaderRowH + Spacing.XS;

    /// <summary>…and the Autoplay header's, with its hint line under it (54).</summary>
    public static float HeaderSubExtent => HeaderExtent + Spacing.XXS + 16f;

    /// <summary>The "Show more" slot: its row and its margins.</summary>
    public static float MoreExtent => MoreRowH + Spacing.XS + Spacing.XXS;

    /// <summary>The gap between the mode pills.</summary>
    public const float PillGap = 8f;

    /// <summary>The small text of the panel: header counts, "Clear", the hint, "Show more" and "Playing from" (size, line height), and the
    /// glyphs beside them (size).</summary>
    public const float SmallSize = 12f, SmallLine = 16f, GlyphSize = 12f;

    /// <summary>The "Playing from" header's least height.</summary>
    public const float PlayingFromMinH = 28f;

    /// <summary>The heart's lane (leading cell of the hairline row, trailing cell of the surface row), and the now-playing hairline row's.</summary>
    public const float HeartLaneW = 26f, NowHeartLaneW = 30f;

    /// <summary>The hairline row's one line of text: size, line height; the thin row's single bar: width, height.</summary>
    public const float IdentitySize = 14f, IdentityLine = 20f, ThinBarW = 160f, ThinBarH = 14f;

    // ── the row's height, the dim, the adapter and the click ────────────────────────────────────────────────────────

    /// <summary>The Autoplay rows' dim in the rail — the radio's tail is not the user's own queue.</summary>
    public const float AutoplayDim = 0.72f;

    /// <summary>The surface row's text column: a title line, the label gap and a caption line — what a row with artists
    /// stacks beside its art.</summary>
    public const float TextColumn = SurfaceGeometry.CardTitleLineH + SurfaceGeometry.CardLabelGap + SurfaceGeometry.CardCaptionLineH;

    /// <summary>One row's resting height under a skin: the hairline row's own, else the surface's.</summary>
    public static float Extent(bool hairline, float artEdge) => hairline ? HairlineExtent : SurfaceExtent(artEdge);

    /// <summary>The shared surface's row height: its 8-DIP padding round the taller of the art and the text column. It is
    /// what the surface renders, so the lane's slot maths (<c>Reorderable.ExtentOf</c>) and the shape's floor both state it.</summary>
    public static float SurfaceExtent(float artEdge) => 2f * SurfaceGeometry.RowPad + MathF.Max(artEdge, TextColumn);

    /// <summary>The Autoplay section's rows are dimmed; the queue's own are not.</summary>
    public static float Dim(QueueSection section) => section == QueueSection.Autoplay ? AutoplayDim : 1f;

    /// <summary>Which adapter feeds a row: the seed face while its title is unknown, the episode adapter for an episode,
    /// the track adapter for everything else.</summary>
    public enum Face : byte { Seed, Track, Episode }

    public static Face FaceOf(EntityKind kind, bool thin)
        => thin ? Face.Seed : kind == EntityKind.Episode ? Face.Episode : Face.Track;

    /// <summary>Where a click on the playing card goes: the playing context's page when there is one, else nowhere — the
    /// card body is then DISPLAY-ONLY (<c>CardData.OnClick</c> null: no hand, no Button role, no tab stop), and the play/pause
    /// FAB is the one control that still toggles playback.</summary>
    public enum NowPlayingClick : byte { OpenContext, None }

    public static NowPlayingClick NowPlayingClickOf(bool hasPage) => hasPage ? NowPlayingClick.OpenContext : NowPlayingClick.None;

    // ── the stage column (every metric below is multiplied by the stage's queue scale and rounded, as rendered) ─────────

    /// <summary>A row's height, its art and its duration slot, and the grip's lane.</summary>
    public const float StageRowH = 56f, StageRowArt = 38f, StageTimeW = 44f, StageGripW = 28f;

    /// <summary>The first upcoming song set apart: its height and art.</summary>
    public const float StageNextH = 104f, StageNextArt = 80f;

    /// <summary>The gap under the Next row.</summary>
    public static float StageNextGap => Spacing.M;

    /// <summary>A section caption's height.</summary>
    public const float StageHeaderH = 40f;

    /// <summary>The Autoplay rows' dim on the stage.</summary>
    public const float StageAutoplayDim = 0.68f;

    /// <summary>The Autoplay switch's least width, and the empty sentence's text (size, line height).</summary>
    public const float SwitchMinW = 40f, EmptySize = 14f, EmptyLine = 20f;
}
