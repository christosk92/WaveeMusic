// ── Themes/Zune/Structure/Views/Playlist/ZuneMixEditor.cs — Zune's transition editor (#48 Mix M6, full-support plan §2.5) ────────────────────
//
// Zune's idiom (UX direction, Themes): a full-width band, flat, in Zune's voice. The accent bar at its lead (the Tune strip's form), then:
//
//   midnight drive → dalkom cafe                                                  ‹ 2 of 10 ›
//   118 → 120 bpm · 1a → 2a — these sit well together.
//   auto  fade  rise  blend  wave  melt  slam  dissolve  surge  afterglow  no transition  more ⌄     (large lowercase words; scrolls when narrow)
//   custom  a smooth crossfade; the bass hands over at the middle.
//   ┌ the timeline, flat on the ground ┐
//   mix out 3:44.0   in 0:08.2                                    overlap  −  4.1 s · 8 beats  +
//   customize ⌄                                                    (a second band while open:)
//     volume     smooth crossfade ⌄
//     eq         center bass swap ⌄       ...       beatmatching
//   undo  redo  hear it   <save line>                         retry  reset to auto  cancel  SAVE
//
// Display type is never shrunk (owner rule): the preset row scrolls instead of squeezing its words.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Wavee.Themes.Common;
using static FluentGpu.Dsl.Ui;
using E = Wavee.Themes.Zune.ZuneMixEditorRules;

namespace Wavee.Themes.Zune;

internal static class ZuneMixEditor
{
    /// <summary>The registered template of <see cref="ViewKeys.MixEditor"/>.</summary>
    public static Element Template(MixEditorProps p, in ViewContext ctx)
    {
        float target = ZunePlaylistTiers.Of(ctx.Tier()).Target;
        if (p.Presets.IsDefaultOrEmpty)
            return Band([Meta(p.Compat), Commands(p, target)]);
        return Band(
        [
            Head(p, target),
            Meta(p.Compat) with { Key = "mix:ed:compat" },
            Presets(p, target),
            Sentence(p),
            MixTimelineView.Create(p.Timeline, E.Look(target)),
            Readouts(p, target),
            Customize(p, target),
            Commands(p, target),
        ]);
    }

    static Element Band(Element[] rows) => new BoxEl
    {
        Key = E.Key, Direction = 0, Gap = E.BarGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
        Children =
        [
            new BoxEl { Key = "mix:ed:bar", Width = E.BarW, Shrink = 0f, Fill = Tok.AccentDefault },
            new BoxEl { Key = "mix:ed:body", Direction = 1, Gap = E.Gap, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = rows },
        ],
    };

    /// <summary>A row that leads with a command word: pulled back by the word's own padding, so its words start on the band's column.</summary>
    static Edges4 Pull => new(-E.WordPadX, 0f, 0f, 0f);

    static TextEl Meta(string s) => R.Type.DenseMeta(s) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f };

    /// <summary>"midnight drive → dalkom cafe" in the label voice, and the pair stepper.</summary>
    static Element Head(MixEditorProps p, float target) => new BoxEl
    {
        Key = "mix:ed:head", Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
        Children =
        [
            Title(p.From),
            R.Type.DenseTitle("→") with { Color = Tok.TextSecondary },
            Title(p.To),
            new BoxEl { Grow = 1f, MinWidth = 0f },
            ToolTip.Wrap(Controls.IconAction(Icons.ChevronLeft, p.Prev, size: target, isEnabled: p.CanPrev) with { Key = "mix:ed:prev" }, p.PrevLabel),
            R.Type.DenseMeta(p.Position) with { Color = Tok.TextSecondary, MaxLines = 1 },
            ToolTip.Wrap(Controls.IconAction(Icons.ChevronRight, p.Next, size: target, isEnabled: p.CanNext) with { Key = "mix:ed:next" }, p.NextLabel),
        ],
    };

    static TextEl Title(string s) => R.Type.DenseTitle(s) with
    {
        Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
    };

    /// <summary>The presets as Zune's pivot words: the one that plays in the primary ink, the others receded; "more" opens the internal thirteen.</summary>
    static Element Presets(MixEditorProps p, float target)
    {
        var words = new List<Element>(p.Presets.Length + 1);
        var pick = p.PickPreset;
        foreach (var c in p.Presets)
        {
            int id = c.Id;
            words.Add(new BoxEl
            {
                Key = "mix:ed:preset:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Direction = 0, AlignItems = FlexAlign.Center, MinHeight = target, Shrink = 0f, Cursor = CursorId.Hand,
                Role = AutomationRole.RadioButton, Focusable = true, OnClick = () => pick(id), BrushTransitionMs = Design.Motion.Faster,
                Children =
                [
                    R.Type.Pivot(c.Label) with
                    {
                        Color = c.On ? Tok.TextPrimary : Tok.TextTertiary, Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Wrap = TextWrap.NoWrap,
                    },
                ],
            });
        }
        words.Add(MixChoiceMenu.Create("mix:ed:more", new MixMenuProps(p.MoreLabel, p.MorePresets, p.PickPreset,
            p.MoreOn ? ButtonAppearance.Accent : ButtonAppearance.Subtle) { Height = target }));
        return ScrollView(new BoxEl { Direction = 0, Gap = E.WordGap, AlignItems = FlexAlign.Center, Children = words.ToArray() }, horizontal: true) with
        {
            Key = "mix:ed:presets", MinWidth = 0f,
        };
    }

    static Element Sentence(MixEditorProps p)
    {
        var kids = new List<Element>(2);
        if (p.State.Length > 0)
            kids.Add(R.Type.DenseTitle(p.State) with { Key = "mix:ed:state", Color = R.Ink.AccentAction, Case = R.Case.For(LabelKind.Label), MaxLines = 1 });
        kids.Add(R.Type.DenseMeta(p.Sentence) with { Key = "mix:ed:sentence", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f });
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
                R.Type.MicroMeta(p.OutReadout) with { Key = "mix:ed:out", Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 },
                R.Type.MicroMeta(p.InReadout) with { Key = "mix:ed:in", Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 },
                new BoxEl { Grow = 1f, MinWidth = 0f },
                new BoxEl
                {
                    Key = "mix:ed:length", Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, Shrink = 0f,
                    Children =
                    [
                        R.Type.MicroMeta(len.Label) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 },
                        Controls.IconAction(Icons.Remove, len.Less, size: target, isEnabled: len.CanLess) with { Key = "mix:ed:less" },
                        new BoxEl
                        {
                            Width = E.StepperValue, Shrink = 0f, AlignItems = FlexAlign.Center,
                            Children = [R.Type.DenseTitle(len.Value) with { Key = "mix:ed:length:value", Color = Tok.TextPrimary, MaxLines = 1 }],
                        },
                        Controls.IconAction(Icons.Add, len.More, size: target, isEnabled: len.CanMore) with { Key = "mix:ed:more-length" },
                    ],
                },
            ],
        };
    }

    /// <summary>"customize" as a word that opens a second band: one row per ingredient (its name, the style as a menu word), beatmatching as a word that
    /// lights, and "use the preset's own styles" while custom.</summary>
    static Element Customize(MixEditorProps p, float target)
    {
        var header = Word("mix:ed:customize", p.CustomizeLabel, p.ToggleCustomize, target, lit: p.CustomizeOpen, glyph: p.CustomizeOpen ? Icons.ChevronUp : Icons.ChevronDown);
        if (!p.CustomizeOpen)
            return new BoxEl { Key = "mix:ed:customize:host", Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Start, Margin = Pull, Children = [header] };
        var rows = new List<Element>(p.Ingredients.Length + 2);
        foreach (var ing in p.Ingredients)
        {
            rows.Add(new BoxEl
            {
                Key = ing.Key, Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Width = E.LabelColumn, Shrink = 0f,
                        Children = [R.Type.DenseMeta(ing.Label) with { Color = ing.Overridden ? R.Ink.AccentAction : Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 }],
                    },
                    MixChoiceMenu.Create(ing.Key + ":menu", new MixMenuProps(ing.Value, ing.Choices, ing.Pick, ButtonAppearance.Subtle) { Height = target }),
                ],
            });
        }
        var beat = new List<Element>(2)
        {
            Word("mix:ed:beatmatch", p.BeatmatchLabel, p.BeatmatchAvailable ? () => p.SetBeatmatch(!p.Beatmatched) : null, target, lit: p.Beatmatched),
        };
        if (p.BeatmatchNote.Length > 0) beat.Add(R.Type.MicroMeta(p.BeatmatchNote) with { Color = Tok.TextTertiary, MaxLines = 1 });
        rows.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f, Margin = Pull, Children = beat.ToArray() });
        if (p.Custom) rows.Add(new BoxEl { Margin = Pull, Children = [Word("mix:ed:clear", p.ClearCustomLabel, p.ClearCustom, target)] });
        return new BoxEl
        {
            Key = "mix:ed:customize:host", Direction = 1, Gap = E.PickerGap, MinWidth = 0f, ClipToBounds = true, Layout = E.Reveal, AlignItems = FlexAlign.Start,
            Children =
            [
                new BoxEl { Margin = Pull, Children = [header] },
                new BoxEl { Key = "mix:ed:customize:body", Direction = 1, Gap = E.PickerGap, MinWidth = 0f, Children = rows.ToArray() },
            ],
        };
    }

    /// <summary>The commands as words: undo, redo, hear it; the save line; retry, reset to auto, cancel, save (save in the accent).</summary>
    static Element Commands(MixEditorProps p, float target)
    {
        var lead = new List<Element>(3);
        if (p.UndoLabel.Length > 0)
        {
            lead.Add(Word("mix:ed:undo", p.UndoLabel, p.CanUndo ? p.Undo : null, target));
            lead.Add(Word("mix:ed:redo", p.RedoLabel, p.CanRedo ? p.Redo : null, target));
            lead.Add(Word("mix:ed:preview", p.PreviewLabel, p.PreviewAvailable ? p.TogglePreview : null, target, lit: p.Previewing,
                glyph: p.Previewing ? Icons.Stop : Icons.Play));
        }
        var trail = new List<Element>(4);
        if (p.ShowRetry) trail.Add(Word("mix:ed:retry", p.RetryLabel, p.Retry, target));
        if (p.ResetLabel.Length > 0) trail.Add(Word("mix:ed:reset", p.ResetLabel, p.CanReset ? p.Reset : null, target));
        trail.Add(Word("mix:ed:cancel", p.CancelLabel, p.Saving ? null : p.Cancel, target));
        if (p.SaveLabel.Length > 0) trail.Add(Word("mix:ed:save", p.SaveLabel, p.Dirty && !p.Saving ? p.Save : null, target, primary: true));
        return new BoxEl
        {
            Key = "mix:ed:actions", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Margin = Pull, Children = lead.ToArray() },
                R.Type.MicroMeta(p.SaveLine) with
                {
                    Key = "mix:ed:saved", Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                },
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trail.ToArray() },
            ],
        };
    }

    /// <summary>A Zune command word: lowercase in the command voice, the accent when primary or lit, a quiet wash on hover; receded and inert without a verb.</summary>
    static Element Word(string key, string label, Action? click, float target, bool primary = false, bool lit = false, string? glyph = null)
    {
        bool on = click is not null;
        var ink = !on ? Tok.TextDisabled : primary || lit ? R.Ink.AccentAction : Tok.TextPrimary;
        var kids = new List<Element>(2);
        if (glyph is not null) kids.Add(Icon(glyph, E.Glyph, ink));
        kids.Add(R.Type.DenseTitle(label) with { Color = ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap });
        return new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Gap = E.GlyphGap, Height = target, MinWidth = target, Shrink = 0f,
            Padding = new Edges4(E.WordPadX, 0f, E.WordPadX, 0f), Justify = FlexJustify.Center,
            HoverFill = on ? R.Layer.RowHover : ColorF.Transparent, PressedFill = on ? R.Layer.RowPressed : ColorF.Transparent,
            Role = AutomationRole.Button, Focusable = on, IsEnabled = on, Cursor = on ? CursorId.Hand : null, OnClick = click,
            Children = kids.ToArray(),
        };
    }
}
