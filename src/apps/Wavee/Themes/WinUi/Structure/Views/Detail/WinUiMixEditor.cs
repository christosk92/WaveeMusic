// ── Themes/WinUi/Structure/Views/Detail/WinUiMixEditor.cs — WinUI's transition editor (#48 Mix M6, full-support plan §2.5) ───────────────────
//
// WinUI's idiom (UX direction, Themes): an inline expander card. Under the Transitions list, an inset card:
//
//   Midnight Drive → Dalkom Cafe                                              ‹  2 of 10  ›
//   118 → 120 BPM · 1A → 2A — these sit well together.
//   [Auto][Fade][Rise][Blend][Wave][Melt][Slam][Dissolve][Surge][Afterglow][No transition][More ⌄]   (a row that scrolls when narrow)
//   Custom · A smooth crossfade; the bass hands over at the middle.
//   ┌ the timeline (Themes/Common/CommonMixTimeline.cs) ┐
//   Mix out 3:44.0   In 0:08.2                                           Overlap  [−]  4.1 s · 8 beats  [+]
//   ⌄ Customize                                                                                  (the expander's header)
//     Volume [Smooth crossfade ⌄]  EQ [Center bass swap ⌄]  Filter [None ⌄]  Effects [None ⌄]  Looping [None ⌄]   [Beatmatching]
//   ┌ You can't edit the transitions in this playlist                   [Save on this PC] [Duplicate Mix] ┐   (Save's prompt, a list
//   └ Duplicate the mix to keep this transition: …                                                          ┘    the user can't edit)
//   [↶][↷] [▶ Hear it]   <save line>                                     [Retry]  Reset to auto  [Cancel] [Save]
//
// At Compact every control takes the 40 touch target and the rows wrap.

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
        float target = E.TargetFor(window);
        bool compact = window == WidthTier.Compact;
        if (p.Presets.IsDefaultOrEmpty)
            return Card([Caption(p.Compat) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap }, Actions(p, target, compact)]);

        var rows = new List<Element>(9)
        {
            Head(p, target),
            Caption(p.Compat) with { Key = "mix:ed:compat", Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            Presets(p, target),
            Sentence(p),
            MixTimelineView.Create(p.Timeline, E.Look(window)),
            Readouts(p, target),
            Customize(p, target),
            Actions(p, target, compact),
        };
        return Card(rows.ToArray());
    }

    static Element Card(Element[] rows) => new BoxEl
    {
        Key = E.Key, Direction = 1, Gap = E.Gap, Padding = Edges4.All(E.Pad), MinWidth = 0f,
        Corners = Radii.CardAll, Fill = Tok.FillCardSecondary, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
        Children = rows,
    };

    /// <summary>"Midnight Drive → Dalkom Cafe" and the ‹ n of m › pair stepper.</summary>
    static Element Head(MixEditorProps p, float target) => new BoxEl
    {
        Key = "mix:ed:head", Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
        Children =
        [
            BodyStrong(p.From) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Caption("→") with { Color = Tok.TextSecondary },
            BodyStrong(p.To) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new BoxEl { Grow = 1f, MinWidth = 0f },
            ToolTip.Wrap(Sized(IconButton.Create(Icons.ChevronLeft, p.Prev, isEnabled: p.CanPrev), target) with { Key = "mix:ed:prev" }, p.PrevLabel),
            Caption(p.Position) with { Color = Tok.TextSecondary, MaxLines = 1 },
            ToolTip.Wrap(Sized(IconButton.Create(Icons.ChevronRight, p.Next, isEnabled: p.CanNext), target) with { Key = "mix:ed:next" }, p.NextLabel),
        ],
    };

    static BoxEl Sized(BoxEl b, float target) => b with { MinHeight = target, MinWidth = target };

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
            Key = "mix:ed:presets", MinWidth = 0f,
        };
    }

    /// <summary>"Custom" (or "Auto chose Fade") ahead of the preset's sentence.</summary>
    static Element Sentence(MixEditorProps p)
    {
        var kids = new List<Element>(2);
        if (p.State.Length > 0) kids.Add(BodyStrong(p.State) with { Key = "mix:ed:state", Color = Tok.AccentTextPrimary, MaxLines = 1 });
        kids.Add(Body(p.Sentence) with { Key = "mix:ed:sentence", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f });
        return new BoxEl { Key = "mix:ed:line", Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Start, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>The points under the timeline and the overlap stepper (the value cell reserves its widest form, so a step never moves "+").</summary>
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
                        Sized(IconButton.Create(Icons.Remove, len.Less, isEnabled: len.CanLess), target) with { Key = "mix:ed:less" },
                        new BoxEl
                        {
                            Width = E.StepperValue, Shrink = 0f, AlignItems = FlexAlign.Center,
                            Children = [Body(len.Value) with { Key = "mix:ed:length:value", MaxLines = 1 }],
                        },
                        Sized(IconButton.Create(Icons.Add, len.More, isEnabled: len.CanMore), target) with { Key = "mix:ed:more-length" },
                    ],
                },
            ],
        };
    }

    /// <summary>Customize: the expander's header, and while open its body (the five pickers, Beatmatching, "use the preset's styles"), easing in as
    /// the app's disclosures do.</summary>
    static Element Customize(MixEditorProps p, float target)
    {
        var header = Button.Create(p.CustomizeLabel, p.ToggleCustomize, ButtonAppearance.Subtle, glyph: p.CustomizeOpen ? Icons.ChevronUp : Icons.ChevronDown)
            with { Key = "mix:ed:customize", MinHeight = target, AlignSelf = FlexAlign.Start };
        if (!p.CustomizeOpen)
            return new BoxEl { Key = "mix:ed:customize:host", Direction = 1, MinWidth = 0f, Children = [header] };
        var pickers = new List<Element>(p.Ingredients.Length + 1);
        foreach (var ing in p.Ingredients)
        {
            pickers.Add(new BoxEl
            {
                Key = ing.Key, Direction = 1, Gap = E.PickerLabelGap, MinWidth = E.PickerMin, Shrink = 0f,
                Children =
                [
                    Caption(ing.Label) with { Color = ing.Overridden ? Tok.AccentTextPrimary : Tok.TextSecondary, MaxLines = 1 },
                    MixChoiceMenu.Create(ing.Key + ":menu", new MixMenuProps(ing.Value, ing.Choices, ing.Pick, ButtonAppearance.Standard) { Height = target, TrailingChevron = true }),
                ],
            });
        }
        var beat = new List<Element>(2)
        {
            ToggleButton.Controlled(p.BeatmatchLabel, p.Beatmatched, p.SetBeatmatch, isEnabled: p.BeatmatchAvailable) with { Key = "mix:ed:beatmatch" },
        };
        if (p.BeatmatchNote.Length > 0) beat.Add(Caption(p.BeatmatchNote) with { Color = Tok.TextTertiary, MaxLines = 1 });
        pickers.Add(new BoxEl
        {
            Direction = 1, Gap = E.PickerLabelGap, Shrink = 0f, Justify = FlexJustify.End,
            Children = beat.ToArray(),
        });
        var body = new List<Element>(2)
        {
            new BoxEl { Key = "mix:ed:pickers", Direction = 0, Wrap = true, Gap = E.PickerGap, AlignItems = FlexAlign.End, MinWidth = 0f, Children = pickers.ToArray() },
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

    /// <summary>Undo, Redo, Hear it; the save line (it grows, so Retry appearing never moves the rest); Reset to auto, Cancel, Save.</summary>
    static Element Actions(MixEditorProps p, float target, bool compact)
    {
        var lead = new List<Element>(3);
        if (p.UndoLabel.Length > 0)
        {
            lead.Add(ToolTip.Wrap(Sized(IconButton.Create(Icons.Undo, p.Undo, isEnabled: p.CanUndo), target) with { Key = "mix:ed:undo" }, p.UndoLabel));
            lead.Add(ToolTip.Wrap(Sized(IconButton.Create(Icons.Redo, p.Redo, isEnabled: p.CanRedo), target) with { Key = "mix:ed:redo" }, p.RedoLabel));
            lead.Add(Button.Create(p.PreviewLabel, p.TogglePreview, ButtonAppearance.Standard, glyph: p.Previewing ? Icons.Stop : Icons.Play,
                isEnabled: p.PreviewAvailable) with { Key = "mix:ed:preview", MinHeight = target });
        }
        var trail = new List<Element>(4);
        if (p.ShowRetry) trail.Add(Button.Create(p.RetryLabel, p.Retry, ButtonAppearance.Subtle) with { Key = "mix:ed:retry", MinHeight = target });
        if (p.ResetLabel.Length > 0)
            trail.Add(HyperlinkButton.Create(p.ResetLabel, p.Reset, isEnabled: p.CanReset) with { Key = "mix:ed:reset", MinHeight = target });
        trail.Add(Button.Create(p.CancelLabel, p.Cancel, ButtonAppearance.Standard, isEnabled: !p.Saving) with { Key = "mix:ed:cancel", MinHeight = target });
        if (p.SaveLabel.Length > 0)
            trail.Add(Button.Create(p.SaveLabel, p.Save, ButtonAppearance.Accent, isEnabled: p.Dirty && !p.Saving && p.PromptTitle.Length == 0) with { Key = "mix:ed:save", MinHeight = target });
        var line = Caption(p.SaveLine) with
        {
            Key = "mix:ed:saved", Color = Tok.TextSecondary, MaxLines = compact ? 2 : 1, Wrap = compact ? TextWrap.Wrap : TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis, Grow = 1f, Shrink = 1f, MinWidth = 0f, Basis = 0f,
        };
        var row = new BoxEl
        {
            Key = "mix:ed:actions", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = lead.ToArray() },
                line,
                new BoxEl { Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trail.ToArray() },
            ],
        };
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = [Prompt(p, target), row] };
    }

    /// <summary>Save's prompt on a list the user can't edit, as WinUI's informational InfoBar inline in the card (its plate, title, message and
    /// action buttons): Save on this PC, Duplicate Mix the primary. Its host eases open and shut, so the actions below slide.</summary>
    static Element Prompt(MixEditorProps p, float target)
    {
        Element[] kids = p.PromptTitle.Length == 0 ? [] :
        [
            new BoxEl
            {
                Key = "mix:ed:prompt", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
                Padding = Edges4.All(E.Pad), Margin = new Edges4(0f, 0f, 0f, E.Gap),
                Corners = Radii.ControlAll, Fill = Tok.FillCardDefault, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = E.LineGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = E.PickerMin,
                        Children =
                        [
                            BodyStrong(p.PromptTitle) with { Wrap = TextWrap.Wrap },
                            Body(p.PromptBody) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
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
