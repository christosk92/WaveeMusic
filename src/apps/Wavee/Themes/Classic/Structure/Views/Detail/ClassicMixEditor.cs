// ── Themes/Classic/Structure/Views/Detail/ClassicMixEditor.cs — Classic's transition editor (#48 Mix M6, full-support plan §2.5) ─────────────
//
// Classic's idiom: the Tune strip's plate (a layer with a hairline) under the Transitions list. Its head (the pair, the stepper), the compatibility line,
// the presets as a WRAPPING chip set (Classic shows every choice at once rather than scrolling a row), the preset's line, the timeline, the points and the
// overlap stepper, Customize as a disclosure whose pickers form a wrapping grid with Beatmatching as a toggle, and the actions right-aligned under a
// hairline with the save line at their lead.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
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
        if (p.Presets.IsDefaultOrEmpty)
            return Plate([Caption(p.Compat) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap }, Actions(p, target)]);
        return Plate(
        [
            Head(p, target),
            Caption(p.Compat) with { Key = "mix:ed:compat", Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            Presets(p, target),
            Sentence(p),
            MixTimelineView.Create(p.Timeline, E.Look(window)),
            Readouts(p, target),
            Customize(p, target),
            Divider() with { Key = "mix:ed:rule" },
            Actions(p, target),
        ]);
    }

    static Element Plate(Element[] rows) => new BoxEl
    {
        Key = E.Key, Direction = 1, Gap = E.Gap, Padding = Edges4.All(E.Pad), MinWidth = 0f,
        Corners = Radii.CardAll, Fill = Tok.FillLayerDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = E.Stroke,
        Children = rows,
    };

    static Element Head(MixEditorProps p, float target) => new BoxEl
    {
        Key = "mix:ed:head", Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
        Children =
        [
            BodyStrong(p.From) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Caption("→") with { Color = Tok.TextSecondary },
            BodyStrong(p.To) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new BoxEl { Grow = 1f, MinWidth = 0f },
            ToolTip.Wrap(Controls.IconAction(Icons.ChevronLeft, p.Prev, size: target, isEnabled: p.CanPrev) with { Key = "mix:ed:prev" }, p.PrevLabel),
            Caption(p.Position) with { Color = Tok.TextSecondary, MaxLines = 1 },
            ToolTip.Wrap(Controls.IconAction(Icons.ChevronRight, p.Next, size: target, isEnabled: p.CanNext) with { Key = "mix:ed:next" }, p.NextLabel),
        ],
    };

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
        if (p.State.Length > 0) kids.Add(BodyStrong(p.State) with { Key = "mix:ed:state", Color = Tok.AccentTextPrimary, MaxLines = 1 });
        kids.Add(Body(p.Sentence) with { Key = "mix:ed:sentence", Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f });
        return new BoxEl { Key = "mix:ed:line", Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Start, MinWidth = 0f, Children = kids.ToArray() };
    }

    static Element Readouts(MixEditorProps p, float target)
    {
        var len = p.Length;
        return new BoxEl
        {
            Key = "mix:ed:readouts", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                Caption(p.OutReadout) with { Key = "mix:ed:out", Color = Tok.TextSecondary, MaxLines = 1 },
                Caption(p.InReadout) with { Key = "mix:ed:in", Color = Tok.TextSecondary, MaxLines = 1 },
                new BoxEl { Grow = 1f, MinWidth = 0f },
                new BoxEl
                {
                    Key = "mix:ed:length", Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, Shrink = 0f,
                    Children =
                    [
                        Caption(len.Label) with { Color = Tok.TextSecondary, MaxLines = 1 },
                        Controls.IconAction(Icons.Remove, len.Less, size: target, isEnabled: len.CanLess) with { Key = "mix:ed:less" },
                        new BoxEl
                        {
                            Width = E.StepperValue, Shrink = 0f, AlignItems = FlexAlign.Center,
                            Children = [Body(len.Value) with { Key = "mix:ed:length:value", MaxLines = 1 }],
                        },
                        Controls.IconAction(Icons.Add, len.More, size: target, isEnabled: len.CanMore) with { Key = "mix:ed:more-length" },
                    ],
                },
            ],
        };
    }

    static Element Customize(MixEditorProps p, float target)
    {
        var parts = new TemplateParts { [ToggleButton.PartRoot] = e => e with { MinHeight = target } };
        var header = Button.Create(p.CustomizeLabel, p.ToggleCustomize, ButtonAppearance.Subtle, glyph: p.CustomizeOpen ? Icons.ChevronUp : Icons.ChevronDown)
            with { Key = "mix:ed:customize", MinHeight = target, AlignSelf = FlexAlign.Start };
        if (!p.CustomizeOpen)
            return new BoxEl { Key = "mix:ed:customize:host", Direction = 1, MinWidth = 0f, Children = [header] };
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
        return new BoxEl
        {
            Key = "mix:ed:customize:host", Direction = 1, Gap = E.RowGap, MinWidth = 0f, ClipToBounds = true, Layout = E.Reveal,
            Children = [header, new BoxEl { Key = "mix:ed:customize:body", Direction = 1, Gap = E.RowGap, MinWidth = 0f, Children = body.ToArray() }],
        };
    }

    static Element Actions(MixEditorProps p, float target)
    {
        var lead = new List<Element>(3);
        if (p.UndoLabel.Length > 0)
        {
            lead.Add(ToolTip.Wrap(Controls.IconAction(Icons.Undo, p.Undo, size: target, isEnabled: p.CanUndo) with { Key = "mix:ed:undo" }, p.UndoLabel));
            lead.Add(ToolTip.Wrap(Controls.IconAction(Icons.Redo, p.Redo, size: target, isEnabled: p.CanRedo) with { Key = "mix:ed:redo" }, p.RedoLabel));
            lead.Add(Button.Create(p.PreviewLabel, p.TogglePreview, ButtonAppearance.Subtle, glyph: p.Previewing ? Icons.Stop : Icons.Play,
                isEnabled: p.PreviewAvailable) with { Key = "mix:ed:preview", MinHeight = target });
        }
        var trail = new List<Element>(4);
        if (p.ShowRetry) trail.Add(Button.Create(p.RetryLabel, p.Retry, ButtonAppearance.Subtle) with { Key = "mix:ed:retry", MinHeight = target });
        if (p.ResetLabel.Length > 0)
            trail.Add(Button.Create(p.ResetLabel, p.Reset, ButtonAppearance.Subtle, isEnabled: p.CanReset) with { Key = "mix:ed:reset", MinHeight = target });
        trail.Add(Button.Create(p.CancelLabel, p.Cancel, ButtonAppearance.Standard, isEnabled: !p.Saving) with { Key = "mix:ed:cancel", MinHeight = target });
        if (p.SaveLabel.Length > 0)
            trail.Add(Button.Create(p.SaveLabel, p.Save, ButtonAppearance.Accent, isEnabled: p.Dirty && !p.Saving && p.PromptTitle.Length == 0) with { Key = "mix:ed:save", MinHeight = target });
        var row = new BoxEl
        {
            Key = "mix:ed:actions", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = lead.ToArray() },
                Caption(p.SaveLine) with
                {
                    Key = "mix:ed:saved", Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                },
                new BoxEl { Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trail.ToArray() },
            ],
        };
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = [Prompt(p, target), row] };
    }

    /// <summary>Save's prompt on a list the user can't edit: the notice on the plate's card layer, Save on this PC and Duplicate Mix (the accent)
    /// at its end. Its host eases open and shut, so the actions below slide.</summary>
    static Element Prompt(MixEditorProps p, float target)
    {
        Element[] kids = p.PromptTitle.Length == 0 ? [] :
        [
            new BoxEl
            {
                Key = "mix:ed:prompt", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
                Padding = Edges4.All(E.Pad), Margin = new Edges4(0f, 0f, 0f, E.Gap),
                Corners = Radii.ControlAll, Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = E.Stroke,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = E.PickerMin,
                        Children =
                        [
                            BodyStrong(p.PromptTitle) with { Wrap = TextWrap.Wrap },
                            Caption(p.PromptBody) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                        Children =
                        [
                            Button.Create(p.SaveHereLabel, p.SaveHere ?? Nothing, ButtonAppearance.Standard, isEnabled: !p.Saving) with { Key = "mix:ed:save-here", MinHeight = target },
                            Button.Create(p.DuplicateLabel, p.Duplicate ?? Nothing, ButtonAppearance.Accent, isEnabled: p.CanDuplicate) with { Key = "mix:ed:duplicate", MinHeight = target },
                        ],
                    },
                ],
            },
        ];
        return new BoxEl { Key = "mix:ed:prompt:host", Direction = 1, MinWidth = 0f, ClipToBounds = true, Layout = E.Reveal, Children = kids };
    }

    static readonly Action Nothing = static () => { };
}
