// ── Themes/WinUi/Structure/Views/Discovery/Home/WinUiHomeDaylistClock.cs — WinUI's `Home.DaylistClock` (themes plan 6.2; P6 W5) ──────────
//
// Boards W-Home / W-Home-Huge (the clock column behind the card's hairline), W-Home-Small (one line on the eyebrow) and W-Home-Compact (the footer under a
// hairline). The faces' state — the window's phase, the rollover ladder, the clock mode, the 1-Hz tick and the signals it writes — is the core hosts'
// (`DaylistClock`, `DaylistCountingClock`); this view draws one face of it:
//
//   Counting    a stock determinate `ProgressRing` beside the countdown ("Next daylist in 01:35:05", the count its own semibold span; Small: "Next in ...") and
//               the caption ("{weekday} {next daypart} arrives at HH:mm"), over five daypart ROWS (label, then a 4-px bar: done parts the quiet grey, the
//               current one the accent at the window's elapsed fraction, future ones empty). The ring, the bars and the count reach the screen through BINDS on
//               the host's signals: rendering this template reads none of them, so nothing re-renders on the tick.
//   UpdateTime  the SAME block with nothing on it that moves: "Next update at HH:mm", the ring and bars at the elapsed fraction of the host's render.
//   Updating    once the window has ended: an indeterminate ring, "Updating your daylist...", "{Next daypart} is on its way", the next row an indeterminate bar.
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

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's daylist clock.</summary>
internal static class WinUiHomeDaylistClock
{
    /// <summary>The registered template of <see cref="ViewKeys.HomeDaylistClock"/>.</summary>
    public static Element Template(HomeDaylistClockProps p, in ViewContext ctx)
    {
        // Hooks run unconditionally (a slot's face never changes, but the hook order must not depend on it).
        var spans = ctx.Hooks.UseRef(new SpanBuffer()).Value;
        int current = (int)p.Current;
        // The tier's form of the block (the rows, the one line, the footer) and its first-frame width.
        var facts = ctx.Tier();
        var dl = WinUiHomeTiers.Of(facts).Daylist;
        float firstW = WinUiHomeDaylist.ClockWidthEstimate(dl, WinUiRegion.EstimateHomeBand(facts, ctx.Hooks.UseContextSignal(Viewport.Size).Peek().Width, hero: true));
        switch (p.Face)
        {
            case HomeDaylistClockFace.Counting when p.Countdown is { } countdown && p.Elapsed is { } elapsed && p.Segments is { } segs:
            {
                // The sentence stays the translator's; only the count is cut out of it to be its own semibold span.
                string sentence = dl.Clock == WinUiClockForm.Line
                    ? Strings.Theme.Winui.Discovery.NextIn(DaylistCountdownLine.Slot)
                    : Strings.Home.NextDaylistIn(DaylistCountdownLine.Slot);
                var (before, after) = DaylistCountdownLine.Split(sentence);
                // The bound line: one fill of the reused buffer per tick (the scene copies it), a relayout of this one run.
                var line = Line(Prop.Of(() =>
                {
                    string count = countdown.Value;                        // subscribe the bind → the per-second update
                    spans.Clear();
                    if (before.Length > 0) spans.Add(new TextSpan(before));
                    spans.Add(new TextSpan(count, Weight: 600));
                    if (after.Length > 0) spans.Add(new TextSpan(after));
                    return spans.Current;
                }), dl);
                var ring = ProgressRing.Create(elapsed, dl.Ring, track: Tok.StrokeControlStrongDefault);
                return Shape(ring, line, p.Caption, segs, current, dl, firstW);
            }
            case HomeDaylistClockFace.Updating:
                return Shape(ProgressRing.Indeterminate(dl.Ring), StaticLine(p.Line, dl), p.Caption, null, current, dl, firstW,
                             DaylistClockFace.Face.Updating);
            case HomeDaylistClockFace.Late:
            {
                var check = HyperlinkButton.Create(Loc.Get(Strings.Home.Daylist.CheckAgain), p.CheckAgain ?? (static () => { }),
                                                   size: ControlSize.Small) with { AlignSelf = FlexAlign.Start, BlocksDragArm = true };
                return Shape(ProgressRing.Determinate(1f, dl.Ring, foreground: Tok.TextTertiary, track: Tok.StrokeControlStrongDefault),
                             StaticLine(p.Line, dl), "", null, current, dl, firstW, DaylistClockFace.Face.Late, check);
            }
            default:
                // UpdateTime: the ring and the five bars are drawn once at the window's elapsed fraction (no signal is ever written to them).
                return Shape(ProgressRing.Determinate(p.ElapsedValue, dl.Ring, track: Tok.StrokeControlStrongDefault),
                             StaticLine(p.Line, dl), p.Caption, p.Segments, current, dl, firstW);
        }
    }

    /// <summary>The first line of a face with a fixed sentence (no per-second count to bind).</summary>
    static SpanTextEl StaticLine(string text, WinUiDaylistLayout dl) => Line((TextSpans)new TextSpan[] { new(text) }, dl);

    /// <summary>The countdown line's paragraph: body 14/20, primary, one line (the Small form's is the caption rung in the secondary ink); it stretches to the
    /// lines column's width and ellipsises only when the column is narrower than the sentence (the ring never gives way).</summary>
    internal static SpanTextEl Line(Prop<TextSpans> spans, WinUiDaylistLayout dl)
    {
        bool small = dl.Clock == WinUiClockForm.Line;
        var rung = small ? Ui.Caption("") : Ui.Body("");
        return new SpanTextEl(spans)
        {
            Size = rung.Size, LineHeight = rung.LineHeight, Color = small ? Tok.TextSecondary : Tok.TextPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
    }

    /// <summary>The clock block, shared by every face and the card's skeleton, in the tier's form: the ROWS (Standard, Huge: [ring, (line over caption)] over
    /// the five daypart rows), the Small LINE (the ring and "Next in ..." together), or the Compact FOOTER (a hairline over [ring, (line over caption)]).
    /// The second line is <paramref name="action"/> when given (the Late face's link), else the <paramref name="caption"/>; an empty caption leaves the first
    /// line alone. <paramref name="segs"/> null = static bars: empty for the Counting face (the skeleton), the finished segments full for Updating / Late;
    /// Updating's next row an indeterminate bar. <paramref name="firstWidth"/> is the block's width on its first frame.</summary>
    internal static Element Shape(Element ring, SpanTextEl line, string caption, FloatSignal[]? segs, int current,
                                  WinUiDaylistLayout dl, float firstWidth,
                                  DaylistClockFace.Face face = DaylistClockFace.Face.Counting, Element? action = null)
    {
        switch (dl.Clock)
        {
            case WinUiClockForm.Line:
                return new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f,
                    Children = [ring, action ?? line with { Grow = 0f, Shrink = 1f }],
                };
            case WinUiClockForm.Footer:
                return new BoxEl
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Gap = WinUiHomeRules.FooterPad,
                    Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }, Head(ring, line, caption, action)],
                };
            default:
                return new BoxEl
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Gap = WinUiHomeRules.ClockGap,
                    Children =
                    [
                        Head(ring, line, caption, action),
                        Responsive.Of("home:daylist:rows", w => Rows(w, segs, current, face, dl), fallback: firstWidth),
                    ],
                };
        }
    }

    /// <summary>[ring, (line over the second line)]: the ring beside two lines, the ring centred against the pair.</summary>
    static Element Head(Element ring, SpanTextEl line, string caption, Element? action) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Children =
        [
            ring,
            // Grow 1 / Basis 0: the column takes exactly what the ring leaves, so both lines ellipsise at it.
            new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = Lines(line, caption, action) },
        ],
    };

    /// <summary>The lines column's children: the first line alone, or under it the action (the Late face's link) or the caption.</summary>
    static Element[] Lines(SpanTextEl line, string caption, Element? action)
    {
        if (action is not null) return [line, action];
        return caption.Length > 0 ? [line, CaptionLine(caption)] : [line];
    }

    /// <summary>The second line: "{weekday} {next daypart} arrives at HH:mm" at the caption rung (12/16), secondary, one line with an ellipsis.</summary>
    static TextEl CaptionLine(string caption)
        => Ui.Caption(caption).Secondary() with
        {
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    /// <summary>The five daypart rows at width <paramref name="w"/> (W-Home: a 92 label, 12, then the 4-px bar): the current label primary, the others
    /// secondary; done rows the quiet grey, the current one the accent (live per <paramref name="segs"/>, or static: empty for the skeleton, the finished
    /// window's fills once it has ended); Updating makes the row after the current one an indeterminate bar.</summary>
    static Element Rows(float w, FloatSignal[]? segs, int current, DaylistClockFace.Face face, WinUiDaylistLayout dl)
    {
        float bar = MathF.Max(0f, w - dl.LabelW - WinUiHomeRules.DaypartLabelGap);
        int next = (int)DaypartRules.Next((Daypart)current);
        var rows = new Element[DaypartTimeline.Segments];
        for (int i = 0; i < rows.Length; i++)
        {
            Element fill = face == DaylistClockFace.Face.Updating && i == next ? ProgressBar.Indeterminate(bar)
                : i < current ? ProgressBar.Determinate(1f, bar, parts: DoneParts)
                : segs is not null ? ProgressBar.Create(segs[i], bar)
                : ProgressBar.Determinate(face == DaylistClockFace.Face.Counting ? 0f : DaypartTimeline.Fill(i, current, 1f), bar);
            string label = Loc.Get(DaypartTimeline.LabelKey(i));
            rows[i] = new BoxEl
            {
                Direction = 0, Gap = WinUiHomeRules.DaypartLabelGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    (i == current ? Ui.Caption(label).Primary() : Ui.Caption(label).Secondary()) with
                    { Width = dl.LabelW, Shrink = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    fill,
                ],
            };
        }
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Gap = WinUiHomeRules.DaypartGap, Margin = new Edges4(0f, WinUiHomeRules.DaypartTop, 0f, 0f), Children = rows,
        };
    }

    /// <summary>A finished daypart's bar: the fill in the quiet grey (W-Home: white at 30 %) instead of the accent.</summary>
    static readonly TemplateParts DoneParts = MakeDoneParts();

    static TemplateParts MakeDoneParts()
    {
        var parts = new TemplateParts();
        parts.Set<BoxEl>(ProgressBar.PartFill, f => f with { Fill = Tok.TextTertiary });
        return parts;
    }
}
