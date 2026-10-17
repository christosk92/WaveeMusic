// ── Themes/Classic/Structure/Views/Home/ClassicHomeDaylistClock.cs — Classic's `Home.DaylistClock` (themes plan 6.1, T10a1) ──
//
// From `Home\Daylist.UI.cs` (`DaylistClockBlock` and the faces' trees): the clock block every face (and the card's skeleton) is built from:
// [ring · (line / second line)] over the five-segment timeline. The faces' state — the window's phase, the rollover ladder, the clock mode,
// the 1-Hz tick and the signals it writes — is the core hosts' (`DaylistClock`, `DaylistCountingClock`); this view draws one face of it.
//
//   Counting    a stock determinate `ProgressRing` beside TWO lines ("Next daylist in **hh:mm:ss**" body primary, then the caption "{weekday}
//               {next daypart} arrives at HH:mm" secondary) over five stock determinate `ProgressBar`s (done / current / future,
//               `DaypartTimeline.Fill`) over their five daypart labels (the current one primary 600, never truncated). The ring, the bars
//               and the count reach the screen through BINDS on the host's signals: rendering this template reads none of them, so nothing
//               re-renders on the tick.
//   UpdateTime  (#185) the SAME block with nothing on it that moves: "Next update at HH:mm" over the second line, the ring and the bars at the
//               elapsed fraction of the host's render.
//   Updating    once the window has ended: an indeterminate ring, "Updating your daylist…", "{Next daypart} is on its way", the next segment an
//               indeterminate bar.
//   Late        when the rollover ladder gives up: a full static ring, "Your next daylist is running late" and a "Check again" link.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's daylist clock.</summary>
internal static class ClassicHomeDaylistClock
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeDaylistClock"/>.</summary>
    public static Element Template(HomeDaylistClockProps p, in ViewContext ctx)
    {
        // Hooks run unconditionally (a slot's face never changes, but the hook order must not depend on it).
        var spans = ctx.Hooks.UseRef(new SpanBuffer()).Value;
        int current = (int)p.Current;
        // The tier's form of the block (Compact: ring, countdown and daypart over bars; Small: one line; Huge: a plate; Short: none) and its first-frame width.
        var facts = ctx.Tier();
        var dl = ClassicHomeTiers.Of(facts).Daylist;
        var form = dl.Clock;
        float firstW = ClassicHomeDaylist.TextWidthEstimate(dl, ClassicRegion.EstimateHomeBand(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width, hero: true));
        switch (p.Face)
        {
            case HomeDaylistClockFace.Counting when p.Countdown is { } countdown && p.Elapsed is { } elapsed && p.Segments is { } segs:
            {
                // The sentence stays the translator's; only the count is cut out of it to be its own semibold span.
                var (before, after) = DaylistCountdownLine.Split(Strings.Home.NextDaylistIn(DaylistCountdownLine.Slot));
                // The bound line: one fill of the reused buffer per tick (the scene copies it), a relayout of this one run.
                var line = Line(Prop.Of(() =>
                {
                    string count = countdown.Value;                        // subscribe the bind → the per-second update
                    spans.Clear();
                    if (before.Length > 0) spans.Add(new TextSpan(before));
                    spans.Add(new TextSpan(count, Weight: 600));
                    if (after.Length > 0) spans.Add(new TextSpan(after));
                    return spans.Current;
                }));
                var ring = ProgressRing.Create(elapsed, ClassicHomeRules.RingSize, track: Tok.StrokeControlStrongDefault);
                return Shape(ring, line, p.Caption, segs, current, form, firstW);
            }
            case HomeDaylistClockFace.Updating:
                return Shape(ProgressRing.Indeterminate(ClassicHomeRules.RingSize), StaticLine(p.Line), p.Caption, null, current, form, firstW,
                             DaylistClockFace.Face.Updating);
            case HomeDaylistClockFace.Late:
            {
                var check = HyperlinkButton.Create(Loc.Get(Strings.Home.Daylist.CheckAgain), p.CheckAgain ?? (static () => { }),
                                                   size: ControlSize.Small) with { AlignSelf = FlexAlign.Start, BlocksDragArm = true };
                return Shape(ProgressRing.Determinate(1f, ClassicHomeRules.RingSize, foreground: Tok.TextTertiary, track: Tok.StrokeControlStrongDefault),
                             StaticLine(p.Line), "", null, current, form, firstW, DaylistClockFace.Face.Late, check);
            }
            default:
                // UpdateTime: the ring and the five bars are drawn once at the window's elapsed fraction (no signal is ever written to them).
                return Shape(ProgressRing.Determinate(p.ElapsedValue, ClassicHomeRules.RingSize, track: Tok.StrokeControlStrongDefault),
                             StaticLine(p.Line), p.Caption, p.Segments, current, form, firstW);
        }
    }

    /// <summary>The first line of a face with a fixed sentence (no per-second count to bind).</summary>
    static SpanTextEl StaticLine(string text) => Line((TextSpans)new TextSpan[] { new(text) });

    /// <summary>The countdown line's paragraph: body 14/20, primary, one line; it stretches to the lines column's width
    /// and ellipsises only when the column is narrower than the sentence (the ring never gives way).</summary>
    internal static SpanTextEl Line(Prop<TextSpans> spans)
    {
        var body = Ui.Body("");
        return new SpanTextEl(spans)
        {
            Size = body.Size, LineHeight = body.LineHeight, Color = Tok.TextPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
    }

    /// <summary>The clock block, shared by every face and the card's skeleton, in the tier's <paramref name="form"/>: the tuned block (<see cref="ClassicClockForm.Full"/>:
    /// [ring, (line over second line)], then the timeline at the block's measured width; the ring is centred against the two-line column, the Fluent
    /// settings-card stack), Compact's ring, line and daypart word over unlabelled bars, Small's single line, Huge's plate around the full block, or nothing (Short).
    /// The second line is <paramref name="action"/> when given (the Late face's link), else the <paramref name="caption"/>; an empty
    /// caption leaves the first line alone. <paramref name="segs"/> null = static bars: empty for the Counting face (the
    /// skeleton), the finished segments full for Updating/Late; Updating's next segment an indeterminate bar. <paramref name="firstWidth"/> is the timeline's
    /// width on its first frame (the tier's estimate, not a fixed 340).</summary>
    internal static Element Shape(Element ring, SpanTextEl line, string caption, FloatSignal[]? segs, int current,
                                  ClassicClockForm form, float firstWidth,
                                  DaylistClockFace.Face face = DaylistClockFace.Face.Counting, Element? action = null)
    {
        switch (form)
        {
            case ClassicClockForm.None:
                return new BoxEl { HitTestVisible = false };
            case ClassicClockForm.Line:
                return new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        ring,
                        line with { Grow = 0f, Shrink = 0f },
                        action ?? (caption.Length > 0 ? (Element)(CaptionLine(caption) with { Shrink = 1f }) : new BoxEl()),
                    ],
                };
            case ClassicClockForm.Bars:
                return new BoxEl
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                            Children =
                            [
                                ring,
                                line with { Grow = 1f, Shrink = 1f },
                                Ui.Caption(Loc.Get(DaypartTimeline.LabelKey(Math.Clamp(current, 0, DaypartTimeline.Segments - 1)))) with
                                    { Weight = 600, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f },
                            ],
                        },
                        Responsive.Of("home:daylist:timeline", w => Timeline(w, segs, current, face, labels: false), fallback: firstWidth),
                    ],
                };
            case ClassicClockForm.Boxed:
                return new BoxEl
                {
                    Width = ClassicHomeRules.ClockPlateW, Padding = ClassicHomeRules.ClockPlatePadding, Corners = Radii.CardAll,
                    Fill = ClassicHomeRules.ClockPlateFill, Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    Children = [Full(ring, line, caption, segs, current, face, action, ClassicHomeRules.ClockPlateW - ClassicHomeRules.ClockPlatePadding.Horizontal)],
                };
            default:
                return Full(ring, line, caption, segs, current, face, action, firstWidth);
        }
    }

    static Element Full(Element ring, SpanTextEl line, string caption, FloatSignal[]? segs, int current, DaylistClockFace.Face face, Element? action,
                        float firstWidth) => new BoxEl
    {
        Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    ring,
                    // Grow 1 / Basis 0: the column takes exactly what the ring leaves, so both lines ellipsise at it.
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                        Children = Lines(line, caption, action),
                    },
                ],
            },
            Responsive.Of("home:daylist:timeline", w => Timeline(w, segs, current, face, labels: true), fallback: firstWidth),
        ],
    };

    /// <summary>The lines column's children: the first line alone, or under it the action (the Late face's link) or the caption.</summary>
    static Element[] Lines(SpanTextEl line, string caption, Element? action)
    {
        if (action is not null) return [line, action];
        return caption.Length > 0 ? [line, CaptionLine(caption)] : [line];
    }

    /// <summary>The second line: "{weekday} {next daypart} arrives at HH:mm" at the caption rung (12/16), secondary,
    /// one line with an ellipsis.</summary>
    static TextEl CaptionLine(string caption)
        => Ui.Caption(caption).Secondary() with
        {
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    /// <summary>The timeline at width <paramref name="w"/>: five equal cells (<see cref="DaypartTimeline.CellWidth"/>)
    /// of stock bars, and the labels on the same cells — tertiary and ellipsised, except the current one, primary 600,
    /// which holds at least its cell and never truncates (its neighbours give way instead). The bars are determinate
    /// (live per <paramref name="segs"/>, or static: empty for the skeleton, the finished window's fills once it has
    /// ended); Updating makes the segment after the current one an indeterminate bar.</summary>
    static Element Timeline(float w, FloatSignal[]? segs, int current, DaylistClockFace.Face face, bool labels)
    {
        float cell = DaypartTimeline.CellWidth(w);
        int next = (int)DaypartRules.Next((Daypart)current);
        var bars = new Element[DaypartTimeline.Segments];
        var labelCells = new Element[DaypartTimeline.Segments];
        bool showLabels = labels;
        for (int i = 0; i < DaypartTimeline.Segments; i++)
        {
            bars[i] = face == DaylistClockFace.Face.Updating && i == next ? ProgressBar.Indeterminate(cell)
                : segs is not null ? ProgressBar.Create(segs[i], cell)
                : ProgressBar.Determinate(face == DaylistClockFace.Face.Counting ? 0f : DaypartTimeline.Fill(i, current, 1f), cell);
            string label = Loc.Get(DaypartTimeline.LabelKey(i));
            labelCells[i] = i == current
                ? Ui.Caption(label).Primary() with { Weight = 600, MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = cell, Shrink = 0f }
                : Ui.Caption(label).Tertiary() with
                {
                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                    Basis = cell, Shrink = 1f, MinWidth = 0f,
                };
        }
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = DaypartTimeline.Gap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Margin = new Edges4(0f, Spacing.S + Spacing.XXS, 0f, 0f), Children = bars,
                },
                showLabels ? new BoxEl
                {
                    Direction = 0, Gap = DaypartTimeline.Gap, MinWidth = 0f,
                    Margin = new Edges4(0f, Spacing.XS + Spacing.XXS, 0f, 0f), Children = labelCells,
                } : new BoxEl(),
            ],
        };
    }
}
