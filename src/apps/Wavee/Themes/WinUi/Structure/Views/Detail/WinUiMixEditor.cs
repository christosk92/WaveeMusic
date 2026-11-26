// ── Themes/WinUi/Structure/Views/Detail/WinUiMixEditor.cs — WinUI's transition editor and chips (#48 Mix, owner 2026-11-26) ─────────────────────
//
// THE EDITOR. WinUI's idiom: a pane like the right pane's other bodies (its 48 head, then a scrolling body), and the same parts full-page:
//
//   Edit transition                                   [↶][↷]  [Save]  [⤢]  [✕]      the head (undo and redo live in the foot in the pane)
//   ┌ overview: both songs whole, the join marked ┐                                  (full page only)
//   ▣ Midnight Drive · Lumen                                    ⚠ 118 BPM  [1A]
//   ┌ the timeline (Themes/Common/CommonMixTimeline.cs), [▶ Hear it] between the lanes ┐
//   ▣ Dalkom Cafe · Seori                                             120 BPM  [2A]
//   Mix out 3:44.0 · In 0:08.2                                  Overlap [−] 4.1 s · 8 beats [+]
//   ───────────────────────────── Presets ─────────────────────────────
//   [Auto][Fade][Rise][Blend][Wave][Melt][Slam][Dissolve][Surge][Afterglow][No transition][More ⌄]   (a row that scrolls)
//   Auto chose Fade · A smooth crossfade; the bass hands over at the middle.
//   ┌ Volume            Smooth crossfade ⌄ ┐  ┌ EQ   Center bass swap ⌄ ┐ ...                (SettingsCard rows; three columns full-page)
//   ┌ Beatmatching                       [■] ┐
//   [↶][↷]   <save line>                                  [Retry]  Reset to auto
//
// THE CHIP. Between two rows of a mixed playlist, on a hairline connector from the row's artwork to the next one's: a small control-plate chip
// with its glyph (✦ Auto in the accent, ∿ a preset or Custom), the accent plate with a pencil while its editor is open.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using static FluentGpu.Dsl.Ui;
using E = Wavee.Themes.WinUi.WinUiMixEditorRules;

namespace Wavee.Themes.WinUi;

internal static class WinUiMixEditor
{
    /// <summary>The registered template of <see cref="ViewKeys.MixEditor"/>.</summary>
    public static Element Template(MixEditorProps p, in ViewContext ctx)
    {
        var window = ctx.Tier().Window;
        // The Mix lesson's editor: the same editor in its teaching mode (its head and the timeline's layer view; the guide holds the rest).
        if (p.Teach is { } teach) return CommonMixTeach.Editor(p, teach, E.TeachLook(window, ctx.Tier().Content), E.Teach(ctx.Tier().Content));
        float target = E.TargetFor(window);
        bool page = p.Expanded;
        var rows = new List<Element>(12);
        if (p.Presets.IsDefaultOrEmpty)
            rows.Add(Caption(p.Compat) with { Key = "mix:ed:compat", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
        else
        {
            rows.Add(Song(p.A, p.Compat, "mix:ed:a"));
            rows.Add(MixTimelineView.Create(p.Timeline, E.Look(window, page), HearIt(p, target), overview: page));
            rows.Add(Song(p.B, p.Compat, "mix:ed:b"));
            rows.Add(Readouts(p, target));
            rows.Add(Section(p.PresetsLabel));
            rows.Add(Presets(p, target));
            rows.Add(Sentence(p));
            rows.Add(Ingredients(p, target, page ? E.PageColumns : 1));
            if (p.Custom)
                rows.Add(HyperlinkButton.Create(p.ClearCustomLabel, p.ClearCustom) with { Key = "mix:ed:clear", AlignSelf = FlexAlign.Start, MinHeight = target });
        }
        rows.Add(Prompt(p, target));
        rows.Add(Foot(p, target, page));
        float inset = page ? WinUiTiers.Frame(window).InsetX : E.Pad;
        var body = new BoxEl
        {
            Key = "mix:ed:body", Direction = 1, Gap = E.Gap, MinWidth = 0f,
            Padding = new Edges4(inset, page ? E.Gap : 0f, inset, E.Pad),
            Children = rows.ToArray(),
        };
        return new BoxEl
        {
            Key = E.Key, Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f,
            Children =
            [
                Head(p, target, page, inset),
                ScrollView(body) with { Key = "mix:ed:scroll", Grow = 1f, MinHeight = 0f },
            ],
        };
    }

    static BoxEl Sized(BoxEl b, float target) => b with { MinHeight = target, MinWidth = target };

    /// <summary>The head: the title, (full page) undo and redo, Save, expand or collapse, close.</summary>
    static Element Head(MixEditorProps p, float target, bool page, float inset)
    {
        var kids = new List<Element>(7)
        {
            R.Type.RailHeader(p.Title) with
            {
                Key = page ? WinUiFrameLayout.PageTitleKey : "mix:ed:title", Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (p.Learn is { } learn) kids.Add(ToolTip.Wrap(Sized(IconButton.Create(Icons.Help, learn), target) with { Key = "mix:ed:learn" }, p.LearnLabel));
        if (page) kids.AddRange(UndoRedo(p, target));
        if (p.SaveLabel.Length > 0)
            kids.Add(Button.Create(p.SaveLabel, p.Save, ButtonAppearance.Accent, isEnabled: p.Dirty && !p.Saving && p.PromptTitle.Length == 0)
                with { Key = "mix:ed:save", MinHeight = target, Margin = new Edges4(E.RowGap, 0f, E.RowGap, 0f) });
        kids.Add(ToolTip.Wrap(Sized(IconButton.Create(page ? Icons.BackToWindow : Icons.FullScreen, p.ToggleExpand), target) with { Key = "mix:ed:expand" }, p.ExpandLabel));
        kids.Add(ToolTip.Wrap(Sized(IconButton.Create(Icons.ChromeClose, p.Close), target) with { Key = "mix:ed:close" }, p.CloseLabel));
        return new BoxEl
        {
            Key = "mix:ed:head", Direction = 0, AlignItems = FlexAlign.Center, MinHeight = E.HeadH, Shrink = 0f, MinWidth = 0f,
            Padding = new Edges4(inset, 0f, page ? inset - E.RowGap : E.RowGap, 0f), Gap = E.LineGap,
            Children = kids.ToArray(),
        };
    }

    static Element[] UndoRedo(MixEditorProps p, float target) =>
    [
        ToolTip.Wrap(Sized(IconButton.Create(Icons.Undo, p.Undo, isEnabled: p.CanUndo), target) with { Key = "mix:ed:undo" }, p.UndoLabel),
        ToolTip.Wrap(Sized(IconButton.Create(Icons.Redo, p.Redo, isEnabled: p.CanRedo), target) with { Key = "mix:ed:redo" }, p.RedoLabel),
    ];

    /// <summary>One song of the pair: its cover, title and artists, its tempo (the caution mark when the two are far apart) and key.</summary>
    static Element Song(MixTrackFacts t, string compat, string key)
    {
        var tail = new List<Element>(3);
        if (t.TempoApart)
            tail.Add(ToolTip.Wrap(new BoxEl { Shrink = 0f, Children = [Icon(Icons.Warning, E.ChipGlyph, Tok.SystemFillCaution)] }, compat));
        if (t.Tempo.Length > 0)
            tail.Add(Caption(t.Tempo) with { Color = t.TempoApart ? Tok.SystemFillCaution : Tok.TextSecondary, MaxLines = 1 });
        if (t.Key.Length > 0)
            tail.Add(new BoxEl
            {
                Shrink = 0f, Padding = new Edges4(E.KeyPadX, 0f, E.KeyPadX, 0f), Corners = CornerRadius4.All(E.KeyCorner), Fill = Tok.FillSubtleSecondary,
                Children = [Caption(t.Key) with { Color = Tok.TextSecondary, MaxLines = 1 }],
            });
        return new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                Controls.Artwork(t.CoverUrl, E.Cover, E.Cover, Radii.Control),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Margin = new Edges4(E.CoverGap - E.RowGap, 0f, 0f, 0f),
                    Children =
                    [
                        BodyStrong(t.Title) with { Shrink = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, MaxWidth = float.PositiveInfinity },
                        Caption(t.Artist) with { Color = Tok.TextSecondary, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, Shrink = 0f, Children = tail.ToArray() },
            ],
        };
    }

    /// <summary>Hear it (Stop while it plays), between the timeline's lanes.</summary>
    static Element HearIt(MixEditorProps p, float target)
        => Button.Create(p.PreviewLabel, p.TogglePreview, ButtonAppearance.Standard, glyph: p.Previewing ? Icons.Stop : Icons.Play,
            isEnabled: p.PreviewAvailable) with { Key = "mix:ed:preview", MinHeight = target };

    /// <summary>The points and the overlap stepper, read live (a drag moves them); the value cell reserves its widest form, so a step never moves "+".</summary>
    static Element Readouts(MixEditorProps p, float target)
    {
        var len = p.Length;
        var read = p.Readout;
        return new BoxEl
        {
            Key = "mix:ed:readouts", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                Caption("") with { Key = "mix:ed:points", Text = Prop.Of(() => read().Out + " · " + read().In), Color = Tok.TextSecondary, MaxLines = 1 },
                new BoxEl { Grow = 1f, MinWidth = 0f },
                new BoxEl
                {
                    Key = "mix:ed:length", Direction = 0, AlignItems = FlexAlign.Center, Gap = E.LineGap, Shrink = 0f,
                    Children =
                    [
                        Caption(len.Label) with { Color = Tok.TextSecondary, MaxLines = 1, Margin = new Edges4(0f, 0f, E.RowGap, 0f) },
                        Sized(IconButton.Create(Icons.Remove, len.Less, isEnabled: len.CanLess), target) with { Key = "mix:ed:less" },
                        new BoxEl
                        {
                            Width = E.StepperValue, Shrink = 0f, AlignItems = FlexAlign.Center,
                            Children = [Body("") with { Key = "mix:ed:length:value", Text = Prop.Of(() => read().Length), MaxLines = 1 }],
                        },
                        Sized(IconButton.Create(Icons.Add, len.More, isEnabled: len.CanMore), target) with { Key = "mix:ed:more-length" },
                    ],
                },
            ],
        };
    }

    /// <summary>A section's label between two hairlines ("Presets").</summary>
    static Element Section(string label) => new BoxEl
    {
        Key = "mix:ed:section", Direction = 0, AlignItems = FlexAlign.Center, Gap = E.Gap, MinWidth = 0f, Margin = new Edges4(0f, E.RowGap, 0f, 0f),
        Children =
        [
            new BoxEl { Height = 1f, Grow = 1f, Basis = 0f, Fill = Tok.StrokeDividerDefault },
            Caption(label) with { Color = Tok.TextSecondary, MaxLines = 1 },
            new BoxEl { Height = 1f, Grow = 1f, Basis = 0f, Fill = Tok.StrokeDividerDefault },
        ],
    };

    /// <summary>The presets as WinUI's filter chips, in Spotify's order, with "More" (the internal thirteen) as a menu; a row that scrolls when narrow.</summary>
    static Element Presets(MixEditorProps p, float target)
    {
        var parts = new TemplateParts { [ToggleButton.PartRoot] = e => e with { Shrink = 0f, MinHeight = target } };
        var chips = new List<Element>(p.Presets.Length + 1);
        var pick = p.PickPreset;
        foreach (var c in p.Presets)
        {
            int id = c.Id;
            chips.Add(Controls.Chip(c.Label, c.On, true, () => pick(id), parts) with { Key = "mix:ed:preset:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        chips.Add(MixChoiceMenu.Create("mix:ed:more", new MixMenuProps(p.MoreLabel, p.MorePresets, p.PickPreset,
            p.MoreOn ? ButtonAppearance.Accent : ButtonAppearance.Standard) { Height = target, TrailingChevron = true }));
        return ScrollView(new BoxEl { Direction = 0, Gap = E.ChipGap, AlignItems = FlexAlign.Center, Children = chips.ToArray() }, horizontal: true) with
        {
            Key = "mix:ed:presets", MinWidth = 0f, Grow = 0f,
        };
    }

    /// <summary>"Custom" (or "Auto chose Fade") ahead of the preset's sentence.</summary>
    static Element Sentence(MixEditorProps p)
    {
        var kids = new List<Element>(2);
        if (p.State.Length > 0) kids.Add(Caption(p.State) with { Key = "mix:ed:state", Color = Tok.AccentTextPrimary, MaxLines = 1 });
        kids.Add(Caption(p.Sentence) with { Key = "mix:ed:sentence", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f });
        // Two lines reserved: "Auto chose Fade" leaving (an edit) must not lift everything under it.
        return new BoxEl
        {
            Key = "mix:ed:line", Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Start, MinWidth = 0f, MinHeight = 2f * Caption("").LineHeight,
            Children = kids.ToArray(),
        };
    }

    /// <summary>Volume, EQ, Filter, Effects, Looping and Beatmatching as WinUI's settings-card rows (the label, the choice as a drop-down at the
    /// row's end), one column in the pane, a grid full-page.</summary>
    static Element Ingredients(MixEditorProps p, float target, int columns)
    {
        var cards = new List<Element>(p.Ingredients.Length + 1);
        foreach (var ing in p.Ingredients)
            cards.Add(Card(ing.Key,
                Body(ing.Label) with { Color = ing.Overridden ? Tok.AccentTextPrimary : Tok.TextPrimary, MaxLines = 1, Shrink = 0f },
                MixChoiceMenu.Create(ing.Key + ":menu", new MixMenuProps(ing.Value, ing.Choices, ing.Pick, ButtonAppearance.Subtle) { Height = target, TrailingChevron = true })));
        var beat = new List<Element>(2) { Body(p.BeatmatchLabel) with { MaxLines = 1, Shrink = 0f } };
        if (p.BeatmatchNote.Length > 0) beat.Add(Caption(p.BeatmatchNote) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis });
        cards.Add(Card("mix:ed:beatmatch:row",
            new BoxEl { Direction = 1, Shrink = 1f, MinWidth = 0f, Children = beat.ToArray() },
            // The bare switch at the row's end (the queue pane's switch rows). The draft owns the value: a flip re-keys the switch onto it.
            ToggleSwitch.Create(new FluentGpu.Signals.Signal<bool>(p.Beatmatched), p.SetBeatmatch, onContent: "", offContent: "", isEnabled: p.BeatmatchAvailable,
                style: ToggleSwitch.DefaultStyle with { MinWidth = 0f }) with { Key = p.Beatmatched ? "mix:ed:beatmatch:on" : "mix:ed:beatmatch:off" }));
        if (columns <= 1)
            return new BoxEl { Key = "mix:ed:ingredients", Direction = 1, Gap = E.PickerGap, MinWidth = 0f, Children = cards.ToArray() };
        var grid = new List<Element>((cards.Count + columns - 1) / columns);
        for (int i = 0; i < cards.Count; i += columns)
        {
            var line = new Element[columns];
            for (int c = 0; c < columns; c++)
                line[c] = i + c < cards.Count ? cards[i + c] with { } : new BoxEl { Grow = 1f, Basis = 0f };
            for (int c = 0; c < columns; c++) if (line[c] is BoxEl b) line[c] = b with { Grow = 1f, Basis = 0f, MinWidth = 0f };
            grid.Add(new BoxEl { Direction = 0, Gap = E.PickerGap, MinWidth = 0f, Children = line });
        }
        return new BoxEl { Key = "mix:ed:ingredients", Direction = 1, Gap = E.PickerGap, MinWidth = 0f, Children = grid.ToArray() };
    }

    static BoxEl Card(string key, Element label, Element control) => new()
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinHeight = E.CardMinH, MinWidth = 0f,
        Padding = new Edges4(E.CardPadX, 0f, E.RowGap, 0f), Corners = Radii.ControlAll, Fill = Tok.FillCardDefault,
        BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
        Children = [label, new BoxEl { Grow = 1f, MinWidth = 0f }, control],
    };

    /// <summary>(The pane) undo and redo; the save line (it grows, so Retry appearing never moves the rest); Retry, Reset to auto.</summary>
    static Element Foot(MixEditorProps p, float target, bool page)
    {
        var lead = page ? [] : UndoRedo(p, target);
        var trail = new List<Element>(2);
        if (p.ShowRetry) trail.Add(Button.Create(p.RetryLabel, p.Retry, ButtonAppearance.Subtle) with { Key = "mix:ed:retry", MinHeight = target });
        if (p.ResetLabel.Length > 0)
            trail.Add(HyperlinkButton.Create(p.ResetLabel, p.Reset, isEnabled: p.CanReset) with { Key = "mix:ed:reset", MinHeight = target });
        return new BoxEl
        {
            Key = "mix:ed:foot", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = lead },
                Caption(p.SaveLine) with
                {
                    Key = "mix:ed:saved", Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f,
                },
                new BoxEl { Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trail.ToArray() },
            ],
        };
    }

    /// <summary>Save's prompt on a list the user can't edit, as WinUI's informational InfoBar: Save on this PC, Duplicate Mix the primary. Its host
    /// eases open and shut, so the foot below slides.</summary>
    static Element Prompt(MixEditorProps p, float target)
    {
        Element[] kids = p.PromptTitle.Length == 0 ? [] :
        [
            new BoxEl
            {
                Key = "mix:ed:prompt", Direction = 1, Gap = E.RowGap, MinWidth = 0f,
                Padding = Edges4.All(E.Pad), Corners = Radii.ControlAll, Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Children =
                [
                    BodyStrong(p.PromptTitle) with { Wrap = TextWrap.Wrap },
                    Body(p.PromptBody) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                    new BoxEl
                    {
                        Direction = 0, Wrap = true, Gap = E.RowGap, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            Button.Create(p.DuplicateLabel, p.Duplicate ?? Nothing, ButtonAppearance.Accent, isEnabled: p.CanDuplicate) with { Key = "mix:ed:duplicate", MinHeight = target },
                            Button.Create(p.SaveHereLabel, p.SaveHere ?? Nothing, ButtonAppearance.Standard, isEnabled: !p.Saving) with { Key = "mix:ed:save-here", MinHeight = target },
                        ],
                    },
                ],
            },
        ];
        return new BoxEl { Key = "mix:ed:prompt:host", Direction = 1, MinWidth = 0f, ClipToBounds = true, Layout = E.Reveal, Children = kids };
    }

    static readonly Action Nothing = static () => { };

    // ══ THE CHIP ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.MixSeam"/>: the hairline connector at the artwork's centre, the chip on it.</summary>
    public static Element Seam(MixSeamProps p, in ViewContext ctx)
    {
        bool on = p.Selected;
        string glyph = on ? Icons.Edit : p.Kind switch { MixSeamKind.Auto => Icons.RefineSparkle, MixSeamKind.None => "", _ => Icons.Equalizer };
        ColorF ink = on ? Tok.TextOnAccentPrimary : p.Kind == MixSeamKind.Auto ? Tok.TextSecondary : Tok.TextPrimary;
        var words = new List<Element>(3);
        if (glyph.Length > 0)
            words.Add(Icon(glyph, E.ChipGlyph, on ? ink : p.Kind == MixSeamKind.Auto ? Tok.AccentTextPrimary : Tok.TextSecondary) with { Margin = new Edges4(0f, 0f, E.ChipGlyphGap, 0f) });
        words.Add(Caption(p.Label) with { Color = ink, MaxLines = 1, Wrap = TextWrap.NoWrap });
        if (p.Note.Length > 0) words.Add(Caption(" · " + p.Note) with { Color = on ? ink : Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap });
        float h = p.Touch ? E.CompactTarget : E.ChipH;
        var chip = new BoxEl
        {
            Key = "mix:chip", Direction = 0, AlignItems = FlexAlign.Center, Height = h, Shrink = 0f,
            Padding = new Edges4(E.ChipPadX, 0f, E.ChipPadX, 0f), Corners = Radii.ControlAll,
            Fill = on ? Tok.AccentDefault : Tok.FillControlDefault, HoverFill = on ? Tok.AccentSecondary : Tok.FillControlSecondary,
            PressedFill = on ? Tok.AccentTertiary : Tok.FillControlTertiary,
            BorderColor = on ? ColorF.Transparent : Tok.StrokeControlDefault, BorderWidth = 1f, BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = p.Open,
            Children = words.ToArray(),
        };
        // The chip starts on the artwork's left edge (Spotify's line-up); the connector runs behind it.
        // The timeline's two segments, joined at the chip's edges (Themes/Common/CommonMixSeam.cs): the theme's quiet ink, the accent where
        // the pair is edited; broken at "No transition".
        var layers = new List<Element>(3);
        p = p with { Indent = p.Indent + E.SeamInset, Lead = p.Lead + E.SeamInset };
        layers.AddRange(MixSeamTimeline.Segments(p, p.Touch ? E.CompactTarget : E.ChipH, p.Kind is MixSeamKind.Preset or MixSeamKind.Custom ? E.LineEdited : E.Line, E.LineStroke));
        layers.Add(new BoxEl
                {
                    Direction = 0, AlignSelf = FlexAlign.Center, MinWidth = 0f,
                    Padding = new Edges4(p.Lead, 0f, 0f, 0f),
                    Children = [ToolTip.Wrap(chip with { Animate = MixSeamTimeline.ChipEnter(p, new EnterExit(Opacity: 0f, Sx: E.ChipEnterScale, Sy: E.ChipEnterScale)) }, p.Name)],
                });
        return new BoxEl { Key = "mix:seam", ZStack = true, Height = p.LaneHeight, Shrink = 0f, MinWidth = 0f, Children = layers.ToArray() };
    }
}
