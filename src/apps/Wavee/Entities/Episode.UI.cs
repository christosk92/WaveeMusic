// ── Entities/Episode.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the episode card (W7) — eager and BOUND — its progress rule and play disc, and the figure-space seed card
//
// Role: UI
// Owner: M
// Wave: 5
// Budget: 400 lines
// Spec: ch 09 W6, W7, §3 "Episode row", §5 (hover/press), §6.1, §9.1, §9.2 (zero-allocation scroll), §9.4 (loc minutes,
//       explicit culture, the disc's role + name, figure-space seeds) · 0.2.9 `Features/Detail/EpisodeList.cs:188-253`
//
// ONE CARD, TWO SHAPES. `Row` is the eager card (a value, re-created by its caller's render); `BoundRow` is the SAME card
// for `ItemsView.CreateBound` — its template runs once per persistent slot and every per-episode read is a bind over the
// slot's equality-gated `RowItem` (the episode handle + its row version), so a scroll frame re-binds and allocates
// nothing: titles and descriptions resolve interned strings, the date and the minutes go through bounded `FormatCache`s,
// and the progress rule is a compositor scale, not a re-layout.
//
// THE CARD (W7): 1 px `StrokeDividerDefault` hairline · r8 · padding 12 · column gap 8 · transparent at rest with a
// `FillSubtleSecondary` hover veil and NO scale · row A = 56 art · the 14/700 two-line title over the 12 secondary
// two-line description (gap 4) · the 40 accent disc; row B = "MMM d · N min [· In progress]" 12 tertiary (gap 8); row C =
// the 3-DIP rule, or a zero-height box that keeps the column's gap (112 unplayed / 115 with progress). The card itself
// is not clickable and not focusable (ch 09 items 20-21): the disc is the row's only affordance.
//
// DECISIONS (ch 09 W7 "0.3 must DECIDE"): an episode with no description DROPS its description run (a presence bind —
// shape-stable, no remount) instead of mounting an empty line; the row keeps its 112 geometry because the art sets row
// A's height. Progress UNKNOWN reads 0 (`Rules.PctOf`): an unplayed card, never a half-drawn bar.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Episode
{
    /// <summary>What one bound card slot binds: the episode handle and its row version, so a data change to THIS
    /// episode re-fires the slot's binds and a publish of any other row does not (the slot item is equality-gated).</summary>
    public readonly record struct RowItem(Episode Episode, uint Version)
    {
        public static RowItem Of(Episode e) => new(e, e.IsValid ? e.Version : 0u);
    }

    const float ArtEdge = 56f, DiscEdge = 40f, DiscGlyph = 15f, RuleHeight = 3f, RuleCorner = 2f;

    /// <summary>U+2007 FIGURE SPACE runs: a derived shimmer draws a bar only for a run that MEASURES, and an empty seed
    /// string measures zero (ch 09 W6 / §9.4 — the one "port the pixels, not the hole" call).</summary>
    static readonly string SeedTitle = new('\u2007', 22);
    static readonly string SeedDescription = new('\u2007', 34);

    /// <summary>"MMM d" keyed by the LOCAL calendar day (yyyymmdd) and "N min" keyed by whole minutes — bounded caches,
    /// so a recycled slot formats nothing it has formatted before. The culture is passed explicitly (§9.4).</summary>
    static readonly FormatCache<int> s_dates = new();
    static readonly FormatCache<int> s_minutes = new();
    static readonly Func<int, string> s_dateFormat = static key => key <= 0
        ? ""
        : new DateTime(key / 10000, key / 100 % 100, key % 100).ToString("MMM d", CultureInfo.CurrentCulture);
    static readonly Func<int, string> s_minutesFormat = static minutes => Strings.Podcast.Minutes(minutes);

    /// <summary>The seed's date: 0.2.9's seed stamps the unix epoch, which reads "Jan 1" (W6's one REAL derived line).</summary>
    const int SeedDateKey = 20000101;
    const int SeedDurationMs = 180_000;

    internal static int DateKey(int unixSeconds)
    {
        if (unixSeconds <= 0) return 0;
        var local = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
        return local.Year * 10000 + local.Month * 100 + local.Day;
    }

    /// <summary>"MMM d" for a unix-seconds stamp; empty for 0 (an unknown date is never invented).</summary>
    internal static string DateLabel(int unixSeconds) => s_dates.Get(DateKey(unixSeconds), s_dateFormat);
    /// <summary>"N min" through <c>podcast.minutes</c> (never a hard-coded English suffix, §9.4).</summary>
    internal static string MinutesLabel(int durationMs) => s_minutes.Get(Rules.Minutes(durationMs), s_minutesFormat);

    // ── selectors (every one guarded: a recycled slot can hold the default item for a frame) ──

    static string TitleOf(Episode e) => e.IsValid && e.Knows(EpisodeFields.Title) ? e.Title : "";
    static bool HasDescription(Episode e) => e.IsValid && e.Knows(EpisodeFields.About) && !e.DescriptionId.IsEmpty;
    static string DescriptionOf(Episode e) => HasDescription(e) ? Entities.Strings.Resolve(e.DescriptionId) : "";
    static string? ArtOf(Episode e) => e.IsValid && e.Knows(EpisodeFields.Image) ? Controls.ArtUrl(e.ImageId) : null;
    static float PctOf(Episode e) => e.IsValid ? Rules.PctOf(e) : 0f;
    static int DateKeyOf(Episode e) => e.IsValid && e.Knows(EpisodeFields.Published) ? DateKey(e.PublishedAt) : 0;
    static int MinutesOf(Episode e) => e.IsValid && e.Knows(EpisodeFields.Duration) ? Rules.Minutes(e.DurationMs) : 0;

    // ══ THE EAGER CARD ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The episode card (W7) as a value.</summary>
    public static Element Row(Episode e, Action play)
    {
        float pct = PctOf(e);
        bool inProgress = Rules.InProgress(pct);
        var meta = new List<Element>(5)
        {
            MetaText(s_dates.Get(DateKeyOf(e), s_dateFormat)), MetaDot(), MetaText(s_minutes.Get(MinutesOf(e), s_minutesFormat)),
        };
        if (inProgress) { meta.Add(MetaDot()); meta.Add(InProgressText()); }
        var copy = new List<Element>(2) { CardTitle(TitleOf(e)) };
        if (HasDescription(e)) copy.Add(CardDescription(DescriptionOf(e)));
        return Card(
            RowA(Controls.Artwork(ArtOf(e), ArtEdge, ArtEdge, Radii.Card), copy.ToArray(), PlayCircle(play)),
            new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = meta.ToArray() },
            Rules.HasRule(pct) ? ProgressRule(pct) : new BoxEl { Height = 0f });
    }

    /// <summary>The cold-skeleton card (W6): figure-space title and description bars, "Jan 1 · 3 min", the art tile and
    /// the disc — no rule, no "In progress" (the seed invents no progress).</summary>
    internal static Element SeedRow() => Card(
        RowA(new BoxEl { Width = ArtEdge, Height = ArtEdge, Corners = CornerRadius4.All(Radii.Card), Fill = Design.PlaceholderFor(default(ReadOnlySpan<char>)) },
             [CardTitle(SeedTitle), CardDescription(SeedDescription)],
             PlayCircle(static () => { })),
        new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children = [MetaText(s_dates.Get(SeedDateKey, s_dateFormat)), MetaDot(), MetaText(MinutesLabel(SeedDurationMs))],
        },
        new BoxEl { Height = 0f });

    /// <summary>The 3-DIP accent rule on its faint ground (W7 / W8): two flex halves, so the fill is exact at any width.</summary>
    public static Element ProgressRule(float pct) => new BoxEl
    {
        Direction = 0, Height = RuleHeight, Corners = CornerRadius4.All(RuleCorner), Fill = Tok.FillSubtleTertiary,
        ClipToBounds = true, Shrink = 0f,
        Children =
        [
            new BoxEl { Grow = MathF.Max(0.001f, pct), Fill = Tok.AccentDefault },
            new BoxEl { Grow = MathF.Max(0.001f, 1f - pct) },
        ],
    };

    /// <summary>The 40-DIP accent play disc: 1.07 hover / 0.92 press (reduced motion collapses both to 1 — a VALUE), a
    /// 15-DIP glyph in the on-accent ink, a Button role and the "Play" name + tooltip (ch 09 §9.4's accessibility fix).</summary>
    public static Element PlayCircle(Action play) => Controls.Named(new BoxEl
    {
        Width = DiscEdge, Height = DiscEdge, Shrink = 0f, Corners = CornerRadius4.All(DiscEdge / 2f), Fill = Tok.AccentDefault,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shadow = Elevation.Card,
        HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BlocksDragArm = true, OnClick = play,
        Children = [Icon(Icons.Play, DiscGlyph, Tok.TextOnAccentPrimary)],
    }, Loc.Get(Strings.Detail.Play));

    // ══ THE BOUND CARD ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The episode card for a bound list. Build it ONCE per slot (template time); <paramref name="play"/> is
    /// resolved against the slot's CURRENT item at click time, never captured per episode.</summary>
    public static Element BoundRow(in BoundItemScope<RowItem> item, Action<Episode> play)
    {
        var art = new ImageEl
        {
            Source = item.Image(static it => ArtOf(it.Episode)), Width = ArtEdge, Height = ArtEdge, Fit = ImageFit.Cover,
            DecodePx = ArtEdge, Corners = CornerRadius4.All(Radii.Card),
            Placeholder = item.Color(static it => Design.PlaceholderFor(ArtOf(it.Episode))),
        };
        Element[] copy =
        [
            CardTitle(item.Text(static it => TitleOf(it.Episode))),
            CardDescription(item.Text(static it => DescriptionOf(it.Episode))) with
            {
                Visible = item.Show(static it => HasDescription(it.Episode)),
            },
        ];
        var meta = new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                MetaText(item.Text(static it => DateKeyOf(it.Episode), s_dates, s_dateFormat)),
                MetaDot(),
                MetaText(item.Text(static it => MinutesOf(it.Episode), s_minutes, s_minutesFormat)),
                MetaDot() with { Visible = item.Show(static it => Rules.InProgress(PctOf(it.Episode))) },
                InProgressText() with { Visible = item.Show(static it => Rules.InProgress(PctOf(it.Episode))) },
            ],
        };
        // The rule's slot keeps the column gap at zero height (W7 row C); the fill is a compositor scale from the left.
        var rule = new BoxEl
        {
            Direction = 0, Corners = CornerRadius4.All(RuleCorner), Fill = Tok.FillSubtleTertiary, ClipToBounds = true, Shrink = 0f,
            Height = item.Value(static it => Rules.HasRule(PctOf(it.Episode)) ? RuleHeight : 0f),
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, Fill = Tok.AccentDefault, TransformOriginX = 0f,
                    Transform = item.Value(static it => Affine2D.Scale(MathF.Max(0.001f, PctOf(it.Episode)), 1f)),
                },
            ],
        };
        return Card(RowA(art, copy, PlayCircle(item.Invoke(it => play(it.Episode)))), meta, rule);
    }

    // ══ SHARED PIECES ════════════════════════════════════════════════════════════════════════════════════════════════

    static BoxEl Card(Element rowA, Element meta, Element rule) => new()
    {
        Direction = 1, Gap = Spacing.S, Padding = Edges4.All(Spacing.M), MinWidth = 0f,
        Corners = CornerRadius4.All(Radii.Card), HoverFill = Tok.FillSubtleSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
        Children = [rowA, meta, rule],
    };

    // The text column is Grow 1 / Basis 0, so the 56 art and the 40 disc never give; the row grows instead (W7).
    static BoxEl RowA(Element art, Element[] copy, Element disc) => new()
    {
        Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Width = ArtEdge, Height = ArtEdge, Shrink = 0f, Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
                Children = [art],
            },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS, Children = copy },
            disc,
        ],
    };

    // 14/700 and 12 are 0.2.9's raw pixels (ch 09 §9.4 "port the pixels"); the named aliases EpisodeCardTitle /
    // EpisodeBannerTitle are owner L's (00-design-system §12.1) — requested in the WP-5.M report, not invented here.
    static TextEl CardTitle(Prop<string> text) => new(text)
    {
        Size = 14f, LineHeight = 20f, Weight = 700, Color = Tok.TextPrimary,
        MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    static TextEl CardDescription(Prop<string> text) => new(text)
    {
        Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary,
        MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    static TextEl MetaText(Prop<string> text) => new(text)
    {
        Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
    };

    static TextEl MetaDot() => new("·") { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary };

    static TextEl InProgressText() => new(Loc.Get(Strings.Podcast.InProgress))
    {
        Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.AccentTextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap,
    };
}
