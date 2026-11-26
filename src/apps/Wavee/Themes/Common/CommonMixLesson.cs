// ── Themes/Common/CommonMixLesson.cs — the Mix lesson's page and the editor's teaching mode, in each theme's voice (#48 Mix) ───────────
//
// One layout, three voices. Each theme registers ViewKeys.MixLesson with its own template, which hands this file its VOICE (a
// `MixLessonVoice` built in its Rules: its type roles, its controls, its corners, its plates) and its gutters; the theme's own Edit
// transition template draws the editor (`MixEditorProps.Teach`) through `CommonMixTeach.Editor` with its `MixTeachLook`.
//
//   wide (Standard and up)                                    narrow (Compact, Small)
//   ┌ Edit transition  ▪Night Drive → ▪Sunrise  [● status] ┐ ┌ Wavee · Learn Mix ┐       ┌ the editor, compact (names inside the lanes) ┐
//   │ What you hear                                         │ │ Let's build this  │       ├──────────────────────────────────────────────┤
//   │ Night Drive  ▍▌▍▌▍▌▍▌▍▌▍▌···                          │ │ transition        │       │ the guide, scrolling under it               │
//   │ Sunrise      ····▍▌▍▌▍▌▍▌▍▌▍▌                         │ │ together.         │       └──────────────────────────────────────────────┘
//   │ Layers                                                │ │ [▶ Listen first]  │
//   │ ① Volume     ‾‾‾╲__  __╱‾‾  ● ◌ Drag here             │ │ ─────────────     │
//   │ …                                                     │ │ ① Volume          │   the guide: the hero (Listen first, Show me, the
//   │ bar 4  5  6  7  8  9  10  11                          │ │   concept, tasks, │   sound note), the progress, the stepper; the open
//   └───────────────────────────────────────────────────────┘ │   Hear the orig.  │   step's body: its concept lines, its tasks, "Hear
//                                                             └───────────────────┘   the original", the done line, Next
//
// The editor never scrolls with the guide (it stays in view while the learner reads and drags); a step change reveals its body with the
// disclosure motion and moves nothing else at once.

using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using L = Wavee.Themes.Common.CommonMixLessonRules;

namespace Wavee.Themes.Common;

/// <summary>How a theme draws the editor in its teaching mode: its plate (a card, or flat), its padding, its title, its pair line and status chip.</summary>
public sealed record MixTeachLook
{
    /// <summary>The editor's plate and edge (null: flat on the ground), its corner and inner padding.</summary>
    public ColorF? Card { get; init; }
    public ColorF? CardStroke { get; init; }
    public required float Corner { get; init; }
    public required float Pad { get; init; }
    /// <summary>The head's height and the hairline under it (null: none).</summary>
    public required float HeadHeight { get; init; }
    public ColorF? HeadRule { get; init; }
    public required Func<string, TextEl> Title { get; init; }
    public required Func<string, TextEl> Meta { get; init; }
    /// <summary>The status chip: its plate, edge, corner, height and side padding.</summary>
    public required ColorF ChipFill { get; init; }
    public ColorF? ChipStroke { get; init; }
    public required float ChipCorner { get; init; }
    public required float ChipHeight { get; init; }
    public required float ChipPadX { get; init; }
    /// <summary>The pair line beside the title (false at a narrow width: the songs' lanes name them).</summary>
    public bool ShowPair { get; init; } = true;
}

/// <summary>How a theme speaks the lesson's guide (built in its Rules from its own roles and controls).</summary>
public sealed record MixLessonVoice
{
    /// <summary>The page's side gutter, the gap between the editor and the guide, the guide's width beside the editor, the editor's top and foot.</summary>
    public required float Gutter { get; init; }
    public required float ColumnGap { get; init; }
    public required float GuideWidth { get; init; }
    public required float Top { get; init; }
    /// <summary>The gap between a body's parts, and the hero's.</summary>
    public required float Gap { get; init; }
    public required float HeroGap { get; init; }
    /// <summary>The theme's tier swap (a declared swap family) and its timing: the two-column and the stacked page cross-fade at the tier edge.</summary>
    public required EnterExit Swap { get; init; }
    public required MotionTokenDef SwapMotion { get; init; }
    public required Func<string, TextEl> Eyebrow { get; init; }
    public required Func<string, TextEl> HeroTitle { get; init; }
    public required Func<string, TextEl> Lead { get; init; }
    /// <summary>Body copy (a concept line; its runs bold the terms), a strong label, a caption.</summary>
    public required Func<string, TextEl> Body { get; init; }
    public required Func<string, TextEl> Strong { get; init; }
    public required Func<string, TextEl> Caption { get; init; }
    /// <summary>A button: key, label, action, the page's primary action, a leading glyph, enabled.</summary>
    public required Func<string, string, Action, bool, string?, bool, Element> Button { get; init; }
    /// <summary>A glyph button (play / stop the comparison, close): key, glyph, action, its name, enabled.</summary>
    public required Func<string, string, Action, string, bool, Element> GlyphButton { get; init; }
    /// <summary>Two choices side by side (Original | With …): key, labels, selected (−1: none), pick, enabled.</summary>
    public required Func<string, string, string, int, Action<int>, bool, Element> Choice { get; init; }
    /// <summary>A note: informational (false) or done (true), its rich words.</summary>
    public required Func<bool, string, Element> Note { get; init; }
    public required Stepper.Style Stepper { get; init; }
    /// <summary>The task list's plate (null: flat rows), its corner; the current task's plate; a task's check size.</summary>
    public ColorF? TaskCard { get; init; }
    public ColorF? TaskCardStroke { get; init; }
    public required float TaskCorner { get; init; }
    public required ColorF TaskNow { get; init; }
    public required float CheckSize { get; init; }
    /// <summary>Null: round checks and badges. A value: squares with this corner.</summary>
    public float? Square { get; init; }
    public required ColorF Accent { get; init; }
    public required ColorF Rule { get; init; }
    public required ColorF Track { get; init; }
}

public static class CommonMixTeach
{
    /// <summary>The editor in its teaching mode: its head (the title, the pair, what plays now) and the timeline's layer view, nothing else.</summary>
    public static Element Editor(MixEditorProps p, MixTeachModel t, MixTimelineLook look, MixTeachLook tl)
    {
        var pair = new BoxEl
        {
            Key = "mix:ed:pair", Direction = 0, AlignItems = FlexAlign.Center, Gap = L.PairGap, Shrink = 1f, MinWidth = 0f,
            Children =
            [
                Swatch(t.HueA, look), tl.Meta(t.SongA) with { MaxLines = 1, Shrink = 0f },
                tl.Meta("→") with { Shrink = 0f },
                Swatch(t.HueB, look), tl.Meta(t.SongB) with { MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
            ],
        };
        var chipLead = t.Playing
            ? Ui.Icon(Icons.Volume, L.ChipGlyph, Tok.AccentTextPrimary)
            : (Element)new BoxEl
            {
                Width = L.ChipDot, Height = L.ChipDot, Shrink = 0f, Corners = CornerRadius4.All(look.SquareCorner ?? L.ChipDot * 0.5f),
                Fill = t.StatusHue == 0 ? Tok.TextTertiary : R.Ink.DataDot(t.StatusHue), BrushTransitionMs = L.BrushMs,
            };
        var chip = new BoxEl
        {
            Key = "mix:ed:status", Direction = 0, AlignItems = FlexAlign.Center, Gap = L.ChipGap, Height = tl.ChipHeight, Shrink = 1f, MinWidth = 0f,
            Padding = new Edges4(tl.ChipPadX, 0f, tl.ChipPadX, 0f), Corners = CornerRadius4.All(tl.ChipCorner), Fill = tl.ChipFill,
            BorderColor = tl.ChipStroke ?? ColorF.Transparent, BorderWidth = tl.ChipStroke is null ? 0f : 1f,
            Children = [chipLead, tl.Meta(t.Status) with { Key = "mix:ed:status:text", Color = t.Playing ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1,
                Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f }],
        };
        var head = new BoxEl
        {
            Key = "mix:ed:head", Direction = 0, AlignItems = FlexAlign.Center, Gap = L.HeadGap, MinHeight = tl.HeadHeight, MinWidth = 0f,
            Padding = new Edges4(tl.Pad, 0f, tl.Pad, 0f), BorderColor = tl.HeadRule ?? ColorF.Transparent,
            Children = tl.ShowPair
                ? [tl.Title(p.Title) with { Key = "mix:ed:title", MaxLines = 1, Shrink = 0f }, pair, new BoxEl { Grow = 1f, MinWidth = 0f }, chip]
                : [tl.Title(p.Title) with { Key = "mix:ed:title", MaxLines = 1, Shrink = 0f }, new BoxEl { Grow = 1f, MinWidth = 0f }, chip],
        };
        var kids = new List<Element>(3) { head };
        if (tl.HeadRule is { } rule) kids.Add(new BoxEl { Height = 1f, Shrink = 0f, Fill = rule });
        kids.Add(new BoxEl
        {
            Key = "mix:ed:body", Direction = 1, MinWidth = 0f, Padding = new Edges4(tl.Pad, tl.Pad, tl.Pad, tl.Pad),
            Children = [MixTimelineView.Create(p.Timeline, look)],
        });
        return new BoxEl
        {
            Key = "mix:ed", Direction = 1, MinWidth = 0f, Shrink = 0f, Corners = CornerRadius4.All(tl.Corner), ClipToBounds = tl.Card is not null,
            Fill = tl.Card ?? ColorF.Transparent, BorderColor = tl.CardStroke ?? ColorF.Transparent, BorderWidth = tl.CardStroke is null ? 0f : 1f,
            Children = kids.ToArray(),
        };
    }

    static Element Swatch(uint hue, MixTimelineLook look) => new BoxEl
    {
        Width = L.Swatch, Height = L.Swatch, Shrink = 0f, Corners = CornerRadius4.All(look.SquareCorner ?? L.SwatchCorner), Fill = R.Ink.DataDot(hue),
    };
}

public static class CommonMixLesson
{
    /// <summary>The lesson's page in <paramref name="v"/>'s voice: the editor beside the guide (or over it, <paramref name="stacked"/>). The two
    /// forms are different trees, so a tier crossing cross-fades them under the theme's swap family (none on the page's first render: it
    /// arrives with the page's own entrance).</summary>
    public static Element Page(MixLessonProps p, MixLessonVoice v, bool stacked, in FluentGpu.Theming.ViewContext ctx)
    {
        var seen = ctx.Hooks.UseRef(false);
        bool rendered = seen.Value;
        seen.Value = true;
        var form = Form(p, v, stacked) with { Enter = rendered ? v.Swap : null, Exit = v.Swap, Transition = v.SwapMotion };
        return new BoxEl { Key = "mix:lesson", Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, Children = [form] };
    }

    static BoxEl Form(MixLessonProps p, MixLessonVoice v, bool stacked)
    {
        var guide = new BoxEl
        {
            Key = "mix:lesson:guide", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Padding = new Edges4(0f, stacked ? v.Gap : v.Top, 0f, L.GuideFoot),
            Children = [Hero(p, v), Rule(v), StepsHead(p, v), Steps(p, v)],
        };
        if (stacked)
            return new BoxEl
            {
                Key = "mix:lesson:stacked", Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f,
                Children =
                [
                    Gutters("mix:lesson:editor-col", new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Padding = new Edges4(0f, v.Top, 0f, v.Gap), Children = [p.Editor] }, v, true, true)
                        with { Shrink = 0f },
                    new BoxEl { Height = 1f, Shrink = 0f, Fill = v.Rule },
                    Ui.ScrollView(Gutters("mix:lesson:guide-row", guide, v, true, true)) with { Key = "mix:lesson:scroll", Grow = 1f, MinHeight = 0f },
                ],
            };
        return new BoxEl
        {
            Key = "mix:lesson:wide", Direction = 0, Grow = 1f, MinHeight = 0f, MinWidth = 0f, Gap = v.ColumnGap,
            Children =
            [
                Ui.ScrollView(Gutters("mix:lesson:editor-row", new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Padding = new Edges4(0f, v.Top, 0f, v.Gap), Children = [p.Editor] }, v, true, false))
                    with { Key = "mix:lesson:editor-col", Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f },
                Ui.ScrollView(Gutters("mix:lesson:guide-row", guide, v, false, true)) with { Key = "mix:lesson:scroll", Width = v.GuideWidth, Grow = 0f, Shrink = 0f, MinHeight = 0f },
            ],
        };
    }

    /// <summary><paramref name="body"/> between the page's gutters, each a spacer whose width eases when a tier steps the gutter (padding cannot ease),
    /// so the page's edges glide instead of stepping.</summary>
    static BoxEl Gutters(string key, BoxEl body, MixLessonVoice v, bool lead, bool trail)
    {
        var kids = new List<Element>(3);
        if (lead) kids.Add(new BoxEl { Key = key + ":lead", Width = v.Gutter, Shrink = 0f, Layout = L.GutterReflow });
        kids.Add(body);
        if (trail) kids.Add(new BoxEl { Key = key + ":trail", Width = v.Gutter, Shrink = 0f, Layout = L.GutterReflow });
        return new BoxEl { Key = key, Direction = 0, MinWidth = 0f, Children = kids.ToArray() };
    }

    static Element Rule(MixLessonVoice v) => new BoxEl { Height = 1f, Shrink = 0f, Fill = v.Rule };

    static Element Hero(MixLessonProps p, MixLessonVoice v) => new BoxEl
    {
        Key = "mix:lesson:hero", Direction = 1, Gap = v.HeroGap, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, v.HeroGap + v.Gap),
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = L.EyebrowGap, MinWidth = 0f,
                Children =
                [
                    Ui.Icon(Icons.Equalizer, L.EyebrowGlyph, v.Accent),
                    v.Eyebrow(p.Eyebrow) with { Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1 },
                    v.GlyphButton("mix:lesson:close", Icons.ChromeClose, p.Close, p.CloseLabel, true),
                ],
            },
            v.HeroTitle(p.Title) with { Key = "mix:lesson:title", Wrap = TextWrap.Wrap },
            v.Lead(p.Lead) with { Wrap = TextWrap.Wrap },
            new BoxEl
            {
                Direction = 0, Wrap = true, Gap = L.CtaGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    v.Button("mix:lesson:listen", p.ListenLabel, p.Listen, true, p.ListenPlaying ? Icons.Stop : Icons.Play, true),
                    v.Button("mix:lesson:show-me", p.ShowMeLabel, p.ShowMe, false, null, true),
                ],
            },
            v.Note(false, p.SoundNote),
        ],
    };

    static Element StepsHead(MixLessonProps p, MixLessonVoice v) => new BoxEl
    {
        Key = "mix:lesson:steps-head", Direction = 1, Gap = L.HeadGap, MinWidth = 0f, Padding = new Edges4(0f, v.Gap + L.HeadGap, 0f, v.Gap),
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.End, Gap = L.HeadGap, MinWidth = 0f,
                Children =
                [
                    v.Strong(p.StepsTitle) with { Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    v.Caption(p.StepsCount) with { Key = "mix:lesson:count", Shrink = 0f, MaxLines = 1 },
                ],
            },
            Progress(p.Progress, v),
        ],
    };

    /// <summary>The lesson's progress: a track and its fill, the fill easing to each new share (it never snaps).</summary>
    static Element Progress(float value, MixLessonVoice v)
    {
        value = Math.Clamp(value, 0f, 1f);
        float corner = v.Square ?? L.ProgressHeight * 0.5f;
        return new BoxEl
        {
            Key = "mix:lesson:progress", Direction = 0, Height = L.ProgressHeight, Shrink = 0f, MinWidth = 0f, Fill = v.Track, Corners = CornerRadius4.All(corner),
            ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Key = "mix:lesson:progress:fill", Grow = value, Basis = 0f, Fill = v.Accent, Corners = CornerRadius4.All(corner),
                    Layout = new LayoutTransition(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(), Size: SizeMode.Reflow),
                },
                new BoxEl { Grow = 1f - value, Basis = 0f },
            ],
        };
    }

    static Element Steps(MixLessonProps p, MixLessonVoice v)
    {
        var items = new List<StepperItem>(p.Steps.Length);
        for (int i = 0; i < p.Steps.Length; i++)
        {
            var s = p.Steps[i];
            items.Add(new StepperItem(s.Key, s.Title, s.Subtitle, s.State, s.Marker, s.MarkerIsGlyph,
                i == p.Open && p.Body is { } b ? Body(b, s, p.TargetHue, v) : null, s.Passed));
        }
        return Stepper.Create(items, p.Select, v.Stepper, "mix:lesson:steps");
    }

    // ── the open step's body ─────────────────────────────────────────────────────────────────────────────────────────

    static Element Body(MixLessonBody b, MixLessonStepModel step, uint targetHue, MixLessonVoice v)
    {
        var kids = new List<Element>(8);
        foreach (string line in b.Lines) kids.Add(Rich(line, v, targetHue, step.Hue));
        if (!b.Recap.IsDefaultOrEmpty) kids.Add(Recap(b.Recap, v));
        var tasks = new List<Element>(4);
        foreach (var t in b.Tasks) tasks.Add(Task(t, v, targetHue));
        if (b.Compare is { } c) tasks.Add(Compare(c, v));
        if (tasks.Count > 0)
            kids.Add(new BoxEl
            {
                Key = "mix:lesson:tasks", Direction = 1, Gap = L.TaskGap, MinWidth = 0f, Padding = Edges4.All(v.TaskCard is null ? 0f : L.TaskCardPad),
                Corners = CornerRadius4.All(v.TaskCorner), Fill = v.TaskCard ?? ColorF.Transparent,
                BorderColor = v.TaskCardStroke ?? ColorF.Transparent, BorderWidth = v.TaskCardStroke is null ? 0f : 1f, Children = tasks.ToArray(),
            });
        if (b.DoneLine.Length > 0)
            kids.Add(new BoxEl
            {
                Key = "mix:lesson:done", Direction = 1, MinWidth = 0f, Enter = new EnterExit(Opacity: 0f, Dy: L.Rise, Active: true),
                Children = [v.Note(true, "**" + b.DoneLine + "**")],
            });
        if (b.ClosingNote.Length > 0) kids.Add(v.Note(false, b.ClosingNote));
        var foot = new List<Element>(2);
        if (b.Next is { } next)
        {
            foot.Add(v.Button("mix:lesson:next", b.NextLabel, next, b.NextAccent, Icons.Forward, true));
            if (b.NextHint.Length > 0) foot.Add(v.Caption(b.NextHint) with { Shrink = 1f, MinWidth = 0f, MaxLines = 2, Wrap = TextWrap.Wrap });
        }
        if (b.Restart is { } restart) foot.Add(v.Button("mix:lesson:restart", b.RestartLabel, restart, false, null, true));
        if (foot.Count > 0)
            kids.Add(new BoxEl { Key = "mix:lesson:foot", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = L.FootGap, MinWidth = 0f, Children = foot.ToArray() });
        return new BoxEl
        {
            Key = "mix:lesson:body:" + step.Key, Direction = 1, Gap = v.Gap, MinWidth = 0f,
            Layout = new LayoutTransition(TransitionChannels.Size, MotionTok.Reveal.ToDynamics(), Size: SizeMode.FlowReveal),
            Children = kids.ToArray(),
        };
    }

    /// <summary>A line of the guide's copy: <c>**term**</c> in the strong ink, <c>[[marker]]</c> in the hint colour.</summary>
    static Element Rich(string text, MixLessonVoice v, uint targetHue, uint termHue)
    {
        var baseText = v.Body("");
        ColorF ink = Tok.TextSecondary, strong = Tok.TextPrimary, marker = L.Hint(targetHue);
        var spans = new List<TextSpan>(5);
        int i = 0;
        while (i < text.Length)
        {
            int b = text.IndexOf("**", i, StringComparison.Ordinal), m = text.IndexOf("[[", i, StringComparison.Ordinal);
            int next = b < 0 ? m : m < 0 ? b : Math.Min(b, m);
            if (next < 0) { spans.Add(new TextSpan(text[i..], Color: ink)); break; }
            if (next > i) spans.Add(new TextSpan(text[i..next], Color: ink));
            bool bold = next == b;
            string close = bold ? "**" : "]]";
            int end = text.IndexOf(close, next + 2, StringComparison.Ordinal);
            if (end < 0) { spans.Add(new TextSpan(text[next..], Color: ink)); break; }
            spans.Add(new TextSpan(text[(next + 2)..end], Weight: L.StrongWeight, Color: bold ? strong : marker));
            i = end + 2;
        }
        return new SpanTextEl(spans.ToArray())
        {
            Size = baseText.Size, LineHeight = baseText.LineHeight, FontFamily = baseText.FontFamily, Color = ink, Wrap = TextWrap.Wrap, MinWidth = 0f,
        };
    }

    static Element Recap(ImmutableArray<(int Number, uint Hue, string Name, string Job)> rows, MixLessonVoice v)
    {
        var kids = new List<Element>(rows.Length * 2);
        for (int i = 0; i < rows.Length; i++)
        {
            var (n, hue, name, job) = rows[i];
            if (i > 0) kids.Add(new BoxEl { Height = 1f, Shrink = 0f, Fill = v.Rule });
            kids.Add(new BoxEl
            {
                Key = "mix:lesson:recap:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), Direction = 0, AlignItems = FlexAlign.Start,
                Gap = L.RecapGap, MinWidth = 0f, Padding = new Edges4(L.TaskPadX, L.RecapPadY, L.TaskPadX, L.RecapPadY),
                Children =
                [
                    Badge(n, hue, v),
                    v.Strong(name) with { Width = L.RecapName, Shrink = 0f, MaxLines = 1 },
                    v.Body(job) with { Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap },
                ],
            });
        }
        return new BoxEl
        {
            Key = "mix:lesson:recap", Direction = 1, MinWidth = 0f, Corners = CornerRadius4.All(v.TaskCorner), Fill = v.TaskCard ?? ColorF.Transparent,
            BorderColor = v.TaskCardStroke ?? ColorF.Transparent, BorderWidth = v.TaskCardStroke is null ? 0f : 1f, Children = kids.ToArray(),
        };
    }

    static Element Badge(int n, uint hue, MixLessonVoice v) => new BoxEl
    {
        Width = L.Badge, Height = L.Badge, Shrink = 0f, Margin = new Edges4(0f, L.BadgeTop, 0f, 0f), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(v.Square ?? L.Badge * 0.5f), Fill = R.Ink.DataDot(hue),
        Children = [new TextEl(n.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Size = L.BadgeText, Weight = L.StrongWeight, Color = Tok.TextOnAccentPrimary, MaxLines = 1 }],
    };

    /// <summary>One task: a drag (its words and its live value, bound), a listen (a button: play, stop; locked until the one before is done).</summary>
    static Element Task(MixLessonTask t, MixLessonVoice v, uint targetHue)
    {
        bool now = t.State == MixLessonTaskState.Now, done = t.State == MixLessonTaskState.Done, playing = t.State == MixLessonTaskState.Playing;
        bool wait = t.State == MixLessonTaskState.Wait;
        bool listen = t.Kind == MixLessonTaskKind.Listen;
        // The check: a drag's ring (done: the accent disc and its check); a listen's play, stop or check on the accent disc (waiting: a quiet ring).
        bool filled = done || playing || listen && now;
        string glyph = done ? Icons.Accept : playing ? Icons.Stop : listen ? Icons.Play : "";
        ColorF ring = wait ? Tok.TextDisabled : filled || now ? v.Accent : Tok.TextTertiary;
        var check = new BoxEl
        {
            Width = v.CheckSize, Height = v.CheckSize, Shrink = 0f, Margin = new Edges4(0f, L.CheckTop, 0f, 0f), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(v.Square ?? v.CheckSize * 0.5f), Fill = filled ? v.Accent : ColorF.Transparent, BorderColor = ring, BorderWidth = 1f,
            BrushTransitionMs = L.BrushMs,
            Children = glyph.Length > 0
                ? [Ui.Icon(glyph, L.CheckGlyph, filled ? Tok.TextOnAccentPrimary : ring)]
                : [new BoxEl { Width = L.DragDot, Height = L.DragDot, Corners = CornerRadius4.All(v.Square ?? L.DragDot * 0.5f), BorderColor = ring, BorderWidth = L.DragRing }],
        };
        var text = new List<Element>(2);
        if (t.Kind == MixLessonTaskKind.Drag) text.Add(Rich(t.Words, v, targetHue, 0));
        else text.Add(v.Strong(t.Words) with { Color = wait ? Tok.TextDisabled : Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f });
        if (t.Value is { } value)
            text.Add(v.Caption("") with { Key = t.Key + ":value", Text = Prop.Of(value), Color = Tok.TextSecondary, MaxLines = 1 });
        else if (t.Hint.Length > 0 && (now || playing))
            text.Add(v.Caption(t.Hint) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3 });
        var row = new BoxEl
        {
            Key = t.Key, Direction = 0, AlignItems = FlexAlign.Start, Gap = L.TaskInnerGap, MinWidth = 0f, Padding = new Edges4(L.TaskPadX, L.TaskPadY, L.TaskPadX, L.TaskPadY),
            Corners = CornerRadius4.All(v.Square ?? L.TaskRowCorner), Fill = now || playing ? v.TaskNow : ColorF.Transparent, BrushTransitionMs = L.BrushMs,
            Children =
            [
                check,
                new BoxEl { Direction = 1, Gap = L.TextGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = text.ToArray() },
            ],
        };
        if (listen && t.Press is { } press)
            row = row with
            {
                Role = AutomationRole.Button, Focusable = !wait, IsEnabled = !wait, Cursor = wait ? null : CursorId.Hand, OnClick = wait ? null : press,
                HoverFill = wait ? row.Fill : Tok.FillSubtleSecondary, PressedFill = wait ? row.Fill : Tok.FillSubtleTertiary,
            };
        // The current task carries the selection pill at its leading edge.
        if (!(now || playing)) return row;
        return new BoxEl
        {
            ZStack = true, MinWidth = 0f,
            Children =
            [
                row,
                new BoxEl { JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Center, Width = L.PillWidth, Height = L.PillHeight, Fill = v.Accent,
                    Corners = CornerRadius4.All(v.Square ?? L.PillWidth * 0.5f), HitTestVisible = false },
            ],
        };
    }

    /// <summary>"Hear the original": play / stop, and the two versions; locked until the step's target is reached.</summary>
    static Element Compare(MixLessonCompare c, MixLessonVoice v)
    {
        ColorF ink = c.Enabled ? Tok.TextPrimary : Tok.TextDisabled;
        return new BoxEl
        {
            Key = "mix:lesson:compare", Direction = 0, AlignItems = FlexAlign.Start, Gap = L.TaskInnerGap, MinWidth = 0f,
            Padding = new Edges4(L.TaskPadX, L.TaskPadY, L.TaskPadX, L.TaskPadY),
            Children =
            [
                new BoxEl
                {
                    Width = v.CheckSize, Height = v.CheckSize, Shrink = 0f, Margin = new Edges4(0f, L.CheckTop, 0f, 0f), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(v.Square ?? v.CheckSize * 0.5f), BorderColor = c.Enabled ? v.Accent : Tok.TextDisabled, BorderWidth = 1f,
                    Children = [Ui.Icon(Icons.Switch, L.CheckGlyph, c.Enabled ? v.Accent : Tok.TextDisabled)],
                },
                new BoxEl
                {
                    Direction = 1, Gap = L.TextGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        v.Strong(c.Title) with { Color = ink, MaxLines = 1 },
                        v.Caption(c.Hint) with { Color = c.Enabled ? Tok.TextSecondary : Tok.TextDisabled, Wrap = TextWrap.Wrap, MaxLines = 3 },
                        new BoxEl
                        {
                            Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = L.CtaGap, MinWidth = 0f, Margin = new Edges4(0f, L.CompareTop, 0f, 0f),
                            Children =
                            [
                                v.GlyphButton("mix:lesson:compare:play", c.Playing ? Icons.Stop : Icons.Play, c.Toggle, c.Playing ? c.StopLabel : c.PlayLabel, c.Enabled),
                                v.Choice("mix:lesson:compare:choice", c.OriginalLabel, c.WithLabel, c.Selected, c.Select, c.Enabled),
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
