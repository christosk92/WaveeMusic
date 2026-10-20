// ── Themes/Classic/Structure/Rules/ClassicTrackRowLayout.cs — the numbers of Classic's track table rows (themes plan 6.1, T8a) ──
//
// What `Entities/Track.UI.Bound.cs` and `TableHost.Skin` wrote as literals beside the row, moved verbatim with the row's picture:
// the type rungs of the cells that are not the shared factual rung, the small boxes (the episode badge, the tempo swatch, the
// Classic explicit word-mark), the selection pill, the row radius, and the two motions (the slot's mount entrance, the check lane's
// shift). They are data, not decisions: `Lists\ClassicTrackTable.cs` reads them (WVTH003 allows literals only here, in `Rules\**`).
//
// WHAT IS NOT HERE. The lane widths, the row heights per density and the art ladder are the core's (`Track.Lane`, `Track.TableRules`,
// `Track.RowMetrics`): the table's relief ladder and the header run on them, and the lane tables stay with the core's lane sets (the row style reaches them as `ThemePolicies.Lists`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's track table rows.</summary>
internal static class ClassicTrackRowLayout
{
    // ── the type rungs ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Modern factual rung: Caption 12 on a 16 line (the # cell's number, the Modern subline, the Added-by name).</summary>
    public const float CaptionSize = 12f, CaptionLine = 16f;

    /// <summary>The title rung and the Classic factual rung: Body 14 on a 20 line.</summary>
    public const float BodySize = 14f, BodyLine = 20f;

    /// <summary>The chart mark beside the number (up / down / new): 8 on a 12 line.</summary>
    public const float ChartMarkSize = 8f, ChartMarkLine = 12f;

    /// <summary>The Classic explicit word-mark's text: 9 on a 12 line.</summary>
    public const float ClassicBadgeSize = 9f, ClassicBadgeLine = 12f;

    /// <summary>A withheld row's short reason in the duration lane ("Unavailable", the release date): 11, on the lane's own line
    /// (tuned Classic L4).</summary>
    public const float ReasonSize = 11f;

    /// <summary>The film glyph after a title that has a video (both row styles, tuned Classic L3).</summary>
    public const float FilmGlyphSize = 12f;

    /// <summary>The expand lane's disclosure chevron.</summary>
    public const float ChevronSize = 12f;

    /// <summary>What leads the duration a Compact Modern row states in its subline ("Daft Punk · 4:34", board C-TrackList compact).</summary>
    public const string InlineDurationLead = " · ";

    // ── the small boxes ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The gap between the # cell's number and its chart mark.</summary>
    public const float NumberGap = 2f;

    /// <summary>The gap between the Modern subline's parts (EPISODE, the E badge, the credit run).</summary>
    public const float MetaGap = 4f;

    /// <summary>The Modern episode "E" badge as asked of <c>Controls.ExplicitBadge</c>: 14, which the badge grows to 16 so its letter meets
    /// the 11-DIP text floor.</summary>
    public const float EpisodeBadge = 14f;

    /// <summary>The Classic explicit word-mark's height.</summary>
    public const float ClassicBadgeHeight = 14f;

    /// <summary>The tempo lane's key swatch: a 6 DIP dot at 0.85.</summary>
    public const float TempoDot = 6f, TempoDotOpacity = 0.85f;

    // ── the row skin ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Modern's row radius (Classic rows are square), and a quiet list's full-width plate's (board C-TrackList, liked: r4).</summary>
    public const float RowRadius = 6f, QuietRowRadius = 4f;

    /// <summary>The title's weight, and a quiet list's (tuned Classic D16: regular weight on Liked Songs).</summary>
    public const ushort TitleWeight = 600, QuietTitleWeight = 400;

    /// <summary>The selection pill: 3 × 16, a 1.5 radius (Modern only), 2 DIP in from the row's leading edge.</summary>
    public const float PillWidth = 3f, PillHeight = 16f, PillRadius = 1.5f, PillLead = 2f;

    // ── motion ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A slot's mount entrance (a re-cut list's crossfade replays it): 280 ms, opacity only.</summary>
    public static readonly LayoutTransition MountEntrance = new(TransitionChannels.Opacity,
        TransitionDynamics.Tween(280f, Easing.FluentDecelerate), Enter: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The check lane appearing: the content shifts over by its width on the slow beat.</summary>
    public static readonly LayoutTransition CheckShift = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Design.Motion.Slow, Easing.FluentDecelerate));
}
