// ── Themes/Classic/Structure/Views/Detail/ClassicMixEditor.cs — Classic's transition editor and chips (#48 Mix, owner 2026-11-26) ─────────────
//
// THE EDITOR. Classic's idiom, in the right panel under Classic's own panel header (the title in the rail header's voice, the glyph buttons at
// its end, the close last), and the same parts full-page: each song on its line, the timeline with Hear it between its lanes, the points and
// the overlap stepper, the presets as a WRAPPING chip set (Classic shows every choice at once rather than scrolling a row), the preset's line,
// the ingredients as a wrapping grid of pickers (the caption over the drop-down) with Beatmatching as a toggle, and under a hairline the save
// line, Retry and Reset to auto.
//
// THE CHIP. Between two rows of a mixed playlist, on a hairline connector from the row's artwork to the next one's: the stock toggle's off plate
// (Classic's control layer, its hairline) at caption size, its glyph on the connector; the accent plate with a pencil while its editor is open.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using static FluentGpu.Dsl.Ui;
using E = Wavee.Themes.Classic.ClassicMixEditorRules;

namespace Wavee.Themes.Classic;

internal static class ClassicMixEditor
{
    /// <summary>The registered template of <see cref="ViewKeys.MixEditor"/>.</summary>
    public static Element Template(MixEditorProps p, in ViewContext ctx)
    {
        var window = ctx.Tier().Window;
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
            rows.Add(Divider() with { Key = "mix:ed:rule:presets" });
            rows.Add(Caption(p.PresetsLabel) with { Key = "mix:ed:section", Color = Tok.TextSecondary, MaxLines = 1 });
            rows.Add(Presets(p, target));
            rows.Add(Sentence(p));
            rows.Add(Ingredients(p, target));
        }
        rows.Add(Prompt(p, target));
        rows.Add(Divider() with { Key = "mix:ed:rule" });
        rows.Add(Foot(p, target, page));
        float pad = page ? E.PagePad : E.Pad;
        var body = new BoxEl
        {
            Key = "mix:ed:body", Direction = 1, Gap = E.Gap, MinWidth = 0f, Padding = new Edges4(pad, 0f, pad, pad), Children = rows.ToArray(),
        };
        return new BoxEl
        {
            Key = E.Key, Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f,
            Children = [Head(p, target, page, pad), ScrollView(body) with { Key = "mix:ed:scroll", Grow = 1f, MinHeight = 0f }],
        };
    }

    /// <summary>The head in Classic's panel header form: the title, (full page) undo and redo, Save, expand or collapse, close.</summary>
    static Element Head(MixEditorProps p, float target, bool page, float pad)
    {
        var kids = new List<Element>(7)
        {
            R.Type.RailHeader(p.Title) with { Key = "mix:ed:title", Grow = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };
        if (page) kids.AddRange(UndoRedo(p, target));
        if (p.SaveLabel.Length > 0)
            kids.Add(Button.Create(p.SaveLabel, p.Save, ButtonAppearance.Accent, isEnabled: p.Dirty && !p.Saving && p.PromptTitle.Length == 0)
                with { Key = "mix:ed:save", MinHeight = target, Margin = new Edges4(E.RowGap, 0f, E.RowGap, 0f) });
        kids.Add(ToolTip.Wrap(Controls.IconAction(page ? Icons.BackToWindow : Icons.FullScreen, p.ToggleExpand, size: target) with { Key = "mix:ed:expand" }, p.ExpandLabel));
        kids.Add(ToolTip.Wrap(Controls.IconAction(Icons.ChromeClose, p.Close, size: target) with { Key = "mix:ed:close" }, p.CloseLabel));
        return new BoxEl
        {
            Key = "mix:ed:head", Direction = 0, AlignItems = FlexAlign.Center, MinHeight = E.HeadH, Shrink = 0f, MinWidth = 0f, Gap = E.LineGap,
            Padding = new Edges4(pad, 0f, E.RowGap, 0f),
            Children = kids.ToArray(),
        };
    }

    static Element[] UndoRedo(MixEditorProps p, float target) =>
    [
        ToolTip.Wrap(Controls.IconAction(Icons.Undo, p.Undo, size: target, isEnabled: p.CanUndo) with { Key = "mix:ed:undo" }, p.UndoLabel),
        ToolTip.Wrap(Controls.IconAction(Icons.Redo, p.Redo, size: target, isEnabled: p.CanRedo) with { Key = "mix:ed:redo" }, p.RedoLabel),
    ];

    /// <summary>One song: its cover, title and artists, its tempo (the caution mark when the two are far apart) and key.</summary>
    static Element Song(MixTrackFacts t, string compat, string key)
    {
        var tail = new List<Element>(3);
        if (t.TempoApart)
            tail.Add(ToolTip.Wrap(new BoxEl { Shrink = 0f, Children = [Icon(Icons.Warning, E.Glyph, Tok.SystemFillCaution)] }, compat));
        if (t.Tempo.Length > 0) tail.Add(Caption(t.Tempo) with { Color = t.TempoApart ? Tok.SystemFillCaution : Tok.TextSecondary, MaxLines = 1 });
        if (t.Key.Length > 0)
            tail.Add(new BoxEl
            {
                Shrink = 0f, Padding = new Edges4(E.KeyPadX, 0f, E.KeyPadX, 0f), Corners = Radii.ControlAll, Fill = Tok.FillControlAltSecondary,
                BorderColor = Tok.StrokeCardDefault, BorderWidth = E.Stroke,
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
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        BodyStrong(t.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        Caption(t.Artist) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                },
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, Shrink = 0f, Children = tail.ToArray() },
            ],
        };
    }

    static Element HearIt(MixEditorProps p, float target)
        => Button.Create(p.PreviewLabel, p.TogglePreview, ButtonAppearance.Standard, glyph: p.Previewing ? Icons.Stop : Icons.Play,
            isEnabled: p.PreviewAvailable) with { Key = "mix:ed:preview", MinHeight = target };

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
            p.MoreOn ? ButtonAppearance.Accent : ButtonAppearance.Standard) { Height = target }));
        return new BoxEl { Key = "mix:ed:presets", Direction = 0, Wrap = true, Gap = E.ChipGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = chips.ToArray() };
    }

    static Element Sentence(MixEditorProps p)
    {
        var kids = new List<Element>(2);
        if (p.State.Length > 0) kids.Add(Caption(p.State) with { Key = "mix:ed:state", Color = Tok.AccentTextPrimary, MaxLines = 1 });
        kids.Add(Caption(p.Sentence) with { Key = "mix:ed:sentence", Wrap = TextWrap.Wrap, Color = Tok.TextSecondary, Shrink = 1f, MinWidth = 0f });
        // Two lines reserved: "Auto chose Fade" leaving (an edit) must not lift everything under it.
        return new BoxEl
        {
            Key = "mix:ed:line", Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Start, MinWidth = 0f, MinHeight = 2f * Caption("").LineHeight,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The points and the overlap stepper, read live (a drag moves them).</summary>
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
                        Controls.IconAction(Icons.Remove, len.Less, size: target, isEnabled: len.CanLess) with { Key = "mix:ed:less" },
                        new BoxEl
                        {
                            Width = E.StepperValue, Shrink = 0f, AlignItems = FlexAlign.Center,
                            Children = [Body("") with { Key = "mix:ed:length:value", Text = Prop.Of(() => read().Length), MaxLines = 1 }],
                        },
                        Controls.IconAction(Icons.Add, len.More, size: target, isEnabled: len.CanMore) with { Key = "mix:ed:more-length" },
                    ],
                },
            ],
        };
    }

    /// <summary>The ingredients as a wrapping grid of pickers (the caption over the drop-down), Beatmatching a toggle at its end, and "use the preset's
    /// own styles" while custom.</summary>
    static Element Ingredients(MixEditorProps p, float target)
    {
        var parts = new TemplateParts { [ToggleButton.PartRoot] = e => e with { MinHeight = target } };
        var cells = new List<Element>(p.Ingredients.Length + 1);
        foreach (var ing in p.Ingredients)
        {
            cells.Add(new BoxEl
            {
                Key = ing.Key, Direction = 1, Gap = E.PickerLabelGap, MinWidth = E.PickerMin, Grow = 1f, Basis = E.PickerMin,
                Children =
                [
                    Caption(ing.Label) with { Color = ing.Overridden ? Tok.AccentTextPrimary : Tok.TextSecondary, MaxLines = 1 },
                    MixChoiceMenu.Create(ing.Key + ":menu", new MixMenuProps(ing.Value, ing.Choices, ing.Pick, ButtonAppearance.Standard) { Height = target }),
                ],
            });
        }
        var beat = new List<Element>(2)
        {
            ToggleButton.Controlled(p.BeatmatchLabel, p.Beatmatched, p.SetBeatmatch, isEnabled: p.BeatmatchAvailable, parts: parts) with { Key = "mix:ed:beatmatch" },
        };
        if (p.BeatmatchNote.Length > 0) beat.Add(Caption(p.BeatmatchNote) with { Color = Tok.TextTertiary, MaxLines = 1 });
        cells.Add(new BoxEl { Direction = 1, Gap = E.PickerLabelGap, MinWidth = E.PickerMin, Grow = 1f, Basis = E.PickerMin, Justify = FlexJustify.End, Children = beat.ToArray() });
        var body = new List<Element>(2)
        {
            new BoxEl { Key = "mix:ed:pickers", Direction = 0, Wrap = true, Gap = E.PickerGap, AlignItems = FlexAlign.End, MinWidth = 0f, Children = cells.ToArray() },
        };
        // The link's words start on the pickers' column (the control's own inset pulled back).
        if (p.Custom)
            body.Add(HyperlinkButton.Create(p.ClearCustomLabel, p.ClearCustom) with
            {
                Key = "mix:ed:clear", AlignSelf = FlexAlign.Start, MinHeight = target, Margin = new Edges4(-E.LinkInset, 0f, 0f, 0f),
            });
        return new BoxEl { Key = "mix:ed:ingredients", Direction = 1, Gap = E.RowGap, MinWidth = 0f, Children = body.ToArray() };
    }

    /// <summary>(The panel) undo and redo; the save line; Retry, Reset to auto.</summary>
    static Element Foot(MixEditorProps p, float target, bool page)
    {
        var lead = page ? [] : UndoRedo(p, target);
        var trail = new List<Element>(2);
        if (p.ShowRetry) trail.Add(Button.Create(p.RetryLabel, p.Retry, ButtonAppearance.Subtle) with { Key = "mix:ed:retry", MinHeight = target });
        if (p.ResetLabel.Length > 0)
            trail.Add(Button.Create(p.ResetLabel, p.Reset, ButtonAppearance.Subtle, isEnabled: p.CanReset) with { Key = "mix:ed:reset", MinHeight = target });
        return new BoxEl
        {
            Key = "mix:ed:foot", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = lead },
                Caption(p.SaveLine) with
                {
                    Key = "mix:ed:saved", Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                },
                new BoxEl { Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trail.ToArray() },
            ],
        };
    }

    /// <summary>Save's prompt on a list the user can't edit: the notice on the plate's card layer, Duplicate Mix (the accent) and Save on this PC.
    /// Its host eases open and shut, so the foot below slides.</summary>
    static Element Prompt(MixEditorProps p, float target)
    {
        Element[] kids = p.PromptTitle.Length == 0 ? [] :
        [
            new BoxEl
            {
                Key = "mix:ed:prompt", Direction = 1, Gap = E.RowGap, MinWidth = 0f,
                Padding = Edges4.All(E.Pad), Corners = Radii.ControlAll, Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = E.Stroke,
                Children =
                [
                    BodyStrong(p.PromptTitle) with { Wrap = TextWrap.Wrap },
                    Caption(p.PromptBody) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
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
        var s = ToggleButton.DefaultStyle;
        string glyph = on ? Icons.Edit : p.Kind switch { MixSeamKind.Auto => Icons.RefineSparkle, MixSeamKind.None => "", _ => Icons.Equalizer };
        ColorF ink = on ? Tok.TextOnAccentPrimary : p.Kind == MixSeamKind.Auto ? Tok.TextSecondary : Tok.TextPrimary;
        var words = new List<Element>(3);
        if (glyph.Length > 0)
            words.Add(Icon(glyph, E.ChipGlyph, on ? ink : p.Kind == MixSeamKind.Auto ? Tok.AccentTextPrimary : Tok.TextSecondary) with { Margin = new Edges4(0f, 0f, E.ChipGlyphGap, 0f) });
        words.Add(Caption(p.Label) with { Color = ink, MaxLines = 1, Wrap = TextWrap.NoWrap });
        if (p.Note.Length > 0) words.Add(Caption(" · " + p.Note) with { Color = on ? ink : Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap });
        var chip = new BoxEl
        {
            Key = "mix:chip", Direction = 0, AlignItems = FlexAlign.Center, Height = p.Touch ? E.CompactTarget : E.ChipH, Shrink = 0f,
            Padding = new Edges4(E.ChipPadX, 0f, E.ChipPadX, 0f), Corners = CornerRadius4.All(s.CornerRadius),
            Fill = on ? Tok.AccentDefault : s.OffBackground, HoverFill = on ? Tok.AccentSecondary : s.OffHover, PressedFill = on ? Tok.AccentTertiary : s.OffPressed,
            BorderWidth = s.BorderWidth, BorderBrush = on ? null : s.OffBorder, BrushTransitionMs = s.BrushTransitionMs,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = p.Open,
            Children = words.ToArray(),
        };
        // The timeline's two segments, joined at the chip's edges (Themes/Common/CommonMixSeam.cs): the theme's quiet ink, the accent where
        // the pair is edited; broken at "No transition".
        var layers = new List<Element>(3);
        p = p with { Indent = p.Indent + E.SeamInset, Lead = p.Lead + E.SeamInset };
        layers.AddRange(MixSeamTimeline.Segments(p, p.Touch ? E.CompactTarget : E.ChipH, p.Kind is MixSeamKind.Preset or MixSeamKind.Custom ? E.LineEdited : E.Line, E.LineStroke));
        layers.Add(new BoxEl
                {
                    Direction = 0, AlignSelf = FlexAlign.Center, MinWidth = 0f,
                    Padding = new Edges4(p.Lead, 0f, 0f, 0f),
                    Children = [ToolTip.Wrap(chip with { Animate = MixSeamTimeline.ChipEnter(p, new EnterExit(Dy: E.ChipEnterRise, Opacity: 0f)) }, p.Name)],
                });
        return new BoxEl { Key = "mix:seam", ZStack = true, Height = p.LaneHeight, Shrink = 0f, MinWidth = 0f, Children = layers.ToArray() };
    }
}
