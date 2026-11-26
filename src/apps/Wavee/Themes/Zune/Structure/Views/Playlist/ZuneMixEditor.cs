// ── Themes/Zune/Structure/Views/Playlist/ZuneMixEditor.cs — Zune's transition editor and chips (#48 Mix, owner 2026-11-26) ────────────────────
//
// THE EDITOR. Zune's voice in the right panel (where the pivots would stand, the editor's own head: its name as a heading, the commands as
// glyphs and "save" in the accent) and the same parts full-page, flat on the ground:
//
//   edit transition                                                   ↶ ↷  SAVE  ⤢  ✕
//   ▣ midnight drive  lumen                                           ⚠ 118 bpm  1a
//   ┌ the timeline, flat, hear it between its lanes ┐
//   ▣ dalkom cafe  seori                                                120 bpm  2a
//   mix out 3:44.0 · in 0:08.2                                 overlap  −  4.1 s · 8 beats  +
//   presets
//   auto  fade  rise  blend  wave  melt  slam  dissolve  surge  afterglow  no transition  more ⌄     (large lowercase words; scrolls when narrow)
//   auto chose fade  a smooth crossfade; the bass hands over at the middle.
//   volume          smooth crossfade ⌄          (the full page sets these rows in columns)
//   eq              center bass swap ⌄
//   ...             beatmatching
//   <save line>                                                         retry  reset to auto
//
// Display type is never shrunk (owner rule): the preset row scrolls instead of squeezing its words.
//
// THE CHIP. Between two rows of a mixed playlist, on a thin connector from the row's artwork to the next one's: Zune's flat chip (a square of 6 %
// ink, the accent fill while its editor is open), its word lowercase, Auto's spark in the accent.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
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
        var tier = ctx.Tier();
        float target = ZunePlaylistTiers.Of(tier).Target;
        // The Mix lesson's editor: the same editor in its teaching mode (its head and the timeline's layer view; the guide holds the rest).
        if (p.Teach is { } teach) return CommonMixTeach.Editor(p, teach, E.TeachLook(tier.Content), E.Teach(tier.Content));
        bool page = p.Expanded;
        var rows = new List<Element>(12);
        if (p.Presets.IsDefaultOrEmpty)
            rows.Add(Meta(p.Compat) with { Key = "mix:ed:compat", Wrap = TextWrap.Wrap, MaxLines = 0 });
        else
        {
            rows.Add(Song(p.A, p.Compat, "mix:ed:a"));
            rows.Add(MixTimelineView.Create(p.Timeline, E.Look(target, page), HearIt(p, target), overview: page));
            rows.Add(Song(p.B, p.Compat, "mix:ed:b"));
            rows.Add(Readouts(p, target));
            rows.Add(R.Type.MicroMeta(p.PresetsLabel) with
            {
                Key = "mix:ed:section", Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Margin = new Edges4(0f, E.RowGap, 0f, 0f),
            });
            rows.Add(Presets(p, target));
            rows.Add(Sentence(p));
            rows.Add(Ingredients(p, target, page));
        }
        rows.Add(Prompt(p, target));
        rows.Add(Foot(p, target, page));
        float gutter = page ? ZuneTiers.Frame(tier.Window).Gutter : E.Pad;
        var body = new BoxEl
        {
            Key = "mix:ed:body", Direction = 1, Gap = E.Gap, MinWidth = 0f, Padding = new Edges4(gutter, 0f, gutter, gutter), Children = rows.ToArray(),
        };
        return new BoxEl
        {
            Key = E.Key, Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f,
            Children = [Head(p, target, page, gutter), ScrollView(body) with { Key = "mix:ed:scroll", Grow = 1f, MinHeight = 0f }],
        };
    }

    static TextEl Meta(string s) => R.Type.DenseMeta(s) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f };

    /// <summary>A row that leads with a command word: pulled back by the word's own padding, so its words start on the column.</summary>
    static Edges4 Pull => new(-E.WordPadX, 0f, 0f, 0f);

    /// <summary>The head: the editor's name as a heading (lowercase), then (full page) undo and redo, "save" in the accent, expand or collapse, close.</summary>
    static Element Head(MixEditorProps p, float target, bool page, float gutter)
    {
        var kids = new List<Element>(7)
        {
            R.Type.RailHeader(p.Title) with
            {
                Key = page ? ZuneFrameRules.PageTitleKey : "mix:ed:title", Case = R.Case.For(LabelKind.Heading), Grow = 1f, Shrink = 1f, MinWidth = 0f,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (p.Learn is { } learn) kids.Add(ToolTip.Wrap(Controls.IconAction(Icons.Help, learn, size: target) with { Key = "mix:ed:learn" }, p.LearnLabel));
        if (page) kids.AddRange(UndoRedo(p, target));
        if (p.SaveLabel.Length > 0)
            kids.Add(Word("mix:ed:save", p.SaveLabel, p.Dirty && !p.Saving && p.PromptTitle.Length == 0 ? p.Save : null, target, primary: true));
        kids.Add(ToolTip.Wrap(Controls.IconAction(page ? Icons.BackToWindow : Icons.FullScreen, p.ToggleExpand, size: target) with { Key = "mix:ed:expand" }, p.ExpandLabel));
        kids.Add(ToolTip.Wrap(Controls.IconAction(Icons.ChromeClose, p.Close, size: target) with { Key = "mix:ed:close" }, p.CloseLabel));
        return new BoxEl
        {
            Key = "mix:ed:head", Direction = 0, AlignItems = FlexAlign.Center, MinHeight = E.HeadH, Shrink = 0f, MinWidth = 0f,
            Padding = new Edges4(gutter, 0f, E.RowGap, 0f),
            Children = kids.ToArray(),
        };
    }

    static Element[] UndoRedo(MixEditorProps p, float target) =>
    [
        ToolTip.Wrap(Controls.IconAction(Icons.Undo, p.Undo, size: target, isEnabled: p.CanUndo) with { Key = "mix:ed:undo" }, p.UndoLabel),
        ToolTip.Wrap(Controls.IconAction(Icons.Redo, p.Redo, size: target, isEnabled: p.CanRedo) with { Key = "mix:ed:redo" }, p.RedoLabel),
    ];

    /// <summary>One song: the square cover, the title in the label voice and its artists in the meta voice, the tempo (the caution mark when far
    /// apart) and the key.</summary>
    static Element Song(MixTrackFacts t, string compat, string key)
    {
        var tail = new List<Element>(3);
        if (t.TempoApart)
            tail.Add(ToolTip.Wrap(new BoxEl { Shrink = 0f, Children = [Icon(Icons.Warning, E.Glyph, Tok.SystemFillCaution)] }, compat));
        if (t.Tempo.Length > 0)
            tail.Add(R.Type.MicroMeta(t.Tempo) with { Color = t.TempoApart ? Tok.SystemFillCaution : Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 });
        if (t.Key.Length > 0)
            tail.Add(R.Type.MicroMeta(t.Key) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 });
        return new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                Controls.Artwork(t.CoverUrl, E.Cover, E.Cover, 0f),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        R.Type.DenseTitle(t.Title) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        Meta(t.Artist),
                    ],
                },
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, Shrink = 0f, Children = tail.ToArray() },
            ],
        };
    }

    /// <summary>"hear it" (or "stop") as a command word on the ground between the lanes.</summary>
    static Element HearIt(MixEditorProps p, float target)
        => new BoxEl
        {
            Fill = Tok.FillSolidBase, Children =
            [
                Word("mix:ed:preview", p.PreviewLabel, p.PreviewAvailable ? p.TogglePreview : null, target, lit: p.Previewing, glyph: p.Previewing ? Icons.Stop : Icons.Play),
            ],
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
            Key = "mix:ed:presets", MinWidth = 0f, Grow = 0f,
        };
    }

    static Element Sentence(MixEditorProps p)
    {
        var kids = new List<Element>(2);
        if (p.State.Length > 0)
            kids.Add(R.Type.DenseMeta(p.State) with { Key = "mix:ed:state", Color = R.Ink.AccentAction, Case = R.Case.For(LabelKind.Label), MaxLines = 1 });
        kids.Add(R.Type.DenseMeta(p.Sentence) with { Key = "mix:ed:sentence", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f });
        // Two lines reserved: "Auto chose Fade" leaving (an edit) must not lift everything under it.
        return new BoxEl
        {
            Key = "mix:ed:line", Direction = 0, Gap = E.RowGap, AlignItems = FlexAlign.Start, MinWidth = 0f, MinHeight = 2f * R.Type.DenseMeta("").LineHeight,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The points and the overlap stepper in the micro voice, read live (a drag moves them).</summary>
    static Element Readouts(MixEditorProps p, float target)
    {
        var len = p.Length;
        var read = p.Readout;
        return new BoxEl
        {
            Key = "mix:ed:readouts", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                R.Type.MicroMeta("") with
                {
                    Key = "mix:ed:points", Text = Prop.Of(() => read().Out + " · " + read().In), Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1,
                },
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
                            Children = [R.Type.DenseTitle("") with { Key = "mix:ed:length:value", Text = Prop.Of(() => read().Length), Color = Tok.TextPrimary, MaxLines = 1 }],
                        },
                        Controls.IconAction(Icons.Add, len.More, size: target, isEnabled: len.CanMore) with { Key = "mix:ed:more-length" },
                    ],
                },
            ],
        };
    }

    /// <summary>One row per ingredient (its name, the style as a menu word), beatmatching as a word that lights, "use the preset's own styles" while
    /// custom. Full-page the rows stand in columns.</summary>
    static Element Ingredients(MixEditorProps p, float target, bool page)
    {
        var rows = new List<Element>(p.Ingredients.Length + 2);
        foreach (var ing in p.Ingredients)
        {
            rows.Add(new BoxEl
            {
                Key = ing.Key, Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = page ? E.PageColumn : 0f, Grow = page ? 1f : 0f, Basis = page ? E.PageColumn : float.NaN,
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
        rows.Add(new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = page ? E.PageColumn : 0f, Grow = page ? 1f : 0f, Basis = page ? E.PageColumn : float.NaN,
            Margin = Pull, Children = beat.ToArray(),
        });
        if (p.Custom) rows.Add(new BoxEl { Margin = Pull, Children = [Word("mix:ed:clear", p.ClearCustomLabel, p.ClearCustom, target)] });
        return new BoxEl
        {
            Key = "mix:ed:ingredients", Direction = (byte)(page ? 0 : 1), Wrap = page, Gap = E.PickerGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
            Children = rows.ToArray(),
        };
    }

    /// <summary>(The panel) undo and redo; the save line; retry, reset to auto as words.</summary>
    static Element Foot(MixEditorProps p, float target, bool page)
    {
        var lead = page ? [] : UndoRedo(p, target);
        var trail = new List<Element>(2);
        if (p.ShowRetry) trail.Add(Word("mix:ed:retry", p.RetryLabel, p.Retry, target));
        if (p.ResetLabel.Length > 0) trail.Add(Word("mix:ed:reset", p.ResetLabel, p.CanReset ? p.Reset : null, target));
        return new BoxEl
        {
            Key = "mix:ed:foot", Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = lead },
                R.Type.MicroMeta(p.SaveLine) with
                {
                    Key = "mix:ed:saved", Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                },
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = trail.ToArray() },
            ],
        };
    }

    /// <summary>Save's prompt on a list the user can't edit, in Zune's voice: no plate, the notice in the title voice and its line in the meta voice,
    /// then "duplicate mix" (the accent) and "save on this pc" as command words. Its host eases open and shut.</summary>
    static Element Prompt(MixEditorProps p, float target)
    {
        Element[] kids = p.PromptTitle.Length == 0 ? [] :
        [
            new BoxEl
            {
                Key = "mix:ed:prompt", Direction = 1, Gap = E.RowGap, MinWidth = 0f,
                Children =
                [
                    R.Type.DenseTitle(p.PromptTitle) with { Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap, MinWidth = 0f },
                    R.Type.DenseMeta(p.PromptBody) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
                    new BoxEl
                    {
                        Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Margin = Pull,
                        Children =
                        [
                            Word("mix:ed:duplicate", p.DuplicateLabel, p.CanDuplicate ? p.Duplicate : null, target, primary: true),
                            Word("mix:ed:save-here", p.SaveHereLabel, p.Saving ? null : p.SaveHere, target),
                        ],
                    },
                ],
            },
        ];
        return new BoxEl { Key = "mix:ed:prompt:host", Direction = 1, MinWidth = 0f, ClipToBounds = true, Layout = E.Reveal, Children = kids };
    }

    /// <summary>A Zune command word: lowercase in the command voice, the accent when primary or lit, a quiet wash on hover; receded and inert without a verb.</summary>
    internal static Element Word(string key, string label, Action? click, float target, bool primary = false, bool lit = false, string? glyph = null)
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

    // ══ THE CHIP ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.MixSeam"/>: the thin connector at the artwork's centre, Zune's flat chip on it.</summary>
    public static Element Seam(MixSeamProps p, in ViewContext ctx)
    {
        bool on = p.Selected;
        var s = ToggleButton.DefaultStyle;
        string glyph = on ? Icons.Edit : p.Kind switch { MixSeamKind.Auto => Icons.RefineSparkle, MixSeamKind.None => "", _ => Icons.Equalizer };
        ColorF ink = on ? s.OnForeground : p.Kind == MixSeamKind.Auto ? Tok.TextSecondary : Tok.TextPrimary;
        var words = new List<Element>(3);
        if (glyph.Length > 0)
            words.Add(Icon(glyph, E.ChipGlyph, on ? ink : p.Kind == MixSeamKind.Auto ? R.Ink.AccentAction : Tok.TextSecondary) with { Margin = new Edges4(0f, 0f, E.ChipGlyphGap, 0f) });
        words.Add(R.Type.MicroMeta(p.Label) with { Color = ink, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap });
        if (p.Note.Length > 0)
            words.Add(R.Type.MicroMeta(" · " + p.Note) with { Color = on ? ink : Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap });
        var chip = new BoxEl
        {
            Key = "mix:chip", Direction = 0, AlignItems = FlexAlign.Center, Height = p.Touch ? E.CompactTarget : E.ChipH, Shrink = 0f,
            Padding = new Edges4(E.ChipPadX, 0f, E.ChipPadX, 0f),
            Fill = on ? s.OnBackground : s.OffBackground, HoverFill = on ? s.OnHover : s.OffHover, PressedFill = on ? s.OnPressed : s.OffPressed,
            BrushTransitionMs = s.BrushTransitionMs,
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
                    Children = [ToolTip.Wrap(chip with { Animate = MixSeamTimeline.ChipEnter(p, new EnterExit(Dx: -E.ChipEnterSlide, Opacity: 0f)) }, p.Name)],
                });
        return new BoxEl { Key = "mix:seam", ZStack = true, Height = p.LaneHeight, Shrink = 0f, MinWidth = 0f, Children = layers.ToArray() };
    }
}
