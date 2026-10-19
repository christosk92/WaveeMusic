// ── Themes/WinUi/Structure/Views/Screens/WinUiFeedback.cs — Classic's `Dialog.Feedback` (themes plan 6.1, T11c; tiers TC8) ────────────────────
//
// From `Feedback.UI.cs`: the report dialog's BODY — the subtitle, the kind switch (never in crash mode) and the form. The stock ContentDialog chrome
// (title, buttons, the vetoable primary click) stays core, and so does everything that decides or does: the off-thread compose, the preview text, the
// submit gate and the three doors are `IFeedbackForm` (`Screens\Feedback.Form.cs`).
//
// The Segmented selection lives in the template because switching kind must REMOUNT the whole form (its answer set is kind-shaped): the card is a
// nested component keyed by the kind, so every signal in it is seeded once and a kind switch drops them all. The preview recomputes per keystroke
// (subscribing reads); the submit the card installs reads the same signals with Peek so it never goes stale.
//
// THE TIERS (TC8; `Rules\Tiers.Dialogs.cs`, boards C-Feedback-Compact / -Small / -Huge). The body is laid out by the record of the window tier the dialog OPENED in and
// the width its card left it (`WinUiScreenDialogShape`, frozen in a ref: the card is sized once, at open, so a body that re-measured would outgrow it):
//   Compact   the kind switch wraps in a pill of 40-DIP buttons, the fields, the check box and the pickers are 40 tall, and the report preview folds behind a
//             40-DIP "Report preview" row; the card's own command row is the engine's, 40-DIP buttons in two equal columns (the band's `DialogFit`).
//   Small, Standard   today's design: the preview always under the form, its Copy / Save over it.
//   Huge      a second pane: the form on the left (472), a divider, the live report on the right with Copy / Save under it.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee Classic's report dialog body.</summary>
internal static class WinUiFeedback
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogFeedback"/>: the subtitle, the kind switch and the form.</summary>
    public static Element Template(FeedbackDialogProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        TierFacts tiers = ctx.Tier();   // a read, so the template follows the facts; the shape below is the dialog's own, frozen at open
        var viewport = hooks.UseContextSignal(Viewport.Size);
        var shapeRef = hooks.UseRef<WinUiScreenDialogShape?>(null);
        WinUiScreenDialogShape shape = shapeRef.Value ??= WinUiScreenDialogTiers.Open(in tiers, viewport.Peek().Width);

        bool isCrash = props.InitialKind == Feedback.ReportKind.Crash;
        var kindIndex = hooks.UseSignal(Feedback.ReportKindIndex.IndexOf(props.InitialKind));
        Feedback.ReportKind kind = isCrash ? Feedback.ReportKind.Crash : Feedback.ReportKindIndex.KindAt(kindIndex.Value);

        var head = new List<Element>(2)
        {
            new TextEl(Loc.Get(Strings.Report.Subtitle))
                { Size = WinUiFeedbackRules.SubtitleSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = shape.FormWidth },
        };
        if (!isCrash) head.Add(KindSwitch(kindIndex, shape));

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Width = shape.BodyWidth,
            Children =
            [
                Embed.Comp(new CardArgs(props.Dialog, kind, shape, head.ToArray()), static () => new FeedbackCard())
                    with { Key = "report-card:" + (int)kind },
            ],
        };
    }

    /// <summary>The kind switch (W-Feedback): four radio buttons in one wrapping row, Bug · Feature · Question · Idea, each a hit target tall at Compact (where they sit two by
    /// two, an equal share of the row each).</summary>
    static Element KindSwitch(Signal<int> kindIndex, WinUiScreenDialogShape shape)
    {
        string[] labels =
        [
            Loc.Get(Strings.Report.KindBug), Loc.Get(Strings.Report.KindFeature), Loc.Get(Strings.Report.KindQuestion), Loc.Get(Strings.Report.KindIdea),
        ];
        var buttons = new Element[labels.Length];
        int selected = kindIndex.Value;
        for (int i = 0; i < labels.Length; i++)
        {
            int at = i;
            var style = shape.Layout.Touch ? RadioButton.DefaultStyle with { MinHeight = shape.Layout.Target } : RadioButton.DefaultStyle;
            buttons[i] = RadioButton.Create(labels[i], at == selected, () => kindIndex.Value = at, style) with
            {
                Key = "report-kind:" + at, Grow = shape.Layout.Touch ? 1f : 0f, Basis = shape.Layout.Touch ? WinUiScreenDialogTiers.KindBasis(shape.FormWidth) : float.NaN, MinWidth = 0f,
            };
        }
        return new BoxEl
        {
            Key = "report-kinds", Direction = 0, Wrap = true, Gap = WinUiScreenDialogTiers.KindGap, Width = shape.FormWidth, Shrink = 0f, Children = buttons,
        };
    }

    /// <summary>A check box whose label WRAPS inside its column (the stock label is one line, so a long label overruns a narrow Compact card) and whose row is at least
    /// <paramref name="target"/> tall; the stock control where the tier asks for none and the label fits as it always did.</summary>
    internal static Element Check(string label, Signal<bool> isChecked, float target)
    {
        var parts = new TemplateParts();
        parts.Set<TextEl>(CheckBox.PartLabel, t => t with { Wrap = TextWrap.Wrap, MinWidth = 0f, Shrink = 1f });
        var style = target > 0f ? CheckBox.DefaultStyle with { MinHeight = target } : CheckBox.DefaultStyle;
        return CheckBox.Create(label, isChecked, style: style, parts: parts);
    }

    /// <summary>What a card mounts with: the open dialog, the kind it shows (a switch remounts, so it never changes), the dialog's shape and the head (the subtitle and the kind switch)
    /// the card places in its form column.</summary>
    internal sealed record CardArgs(IFeedbackDialog Dialog, Feedback.ReportKind Kind, WinUiScreenDialogShape Shape, Element[] Head);

    /// <summary>The form for ONE fixed kind — remounted whole on a kind switch, so every signal is seeded once.</summary>
    sealed class FeedbackCard : Component
    {
        public override Element Render()
        {
            var args = UseProps<CardArgs>();
            var formRef = UseRef<IFeedbackForm?>(null);
            var form = formRef.Value ??= args.Dialog.OpenForm(args.Kind);
            var dialog = args.Dialog;
            var kind = form.Kind;
            bool isCrash = kind == Feedback.ReportKind.Crash;
            var shape = args.Shape;
            var layout = shape.Layout;
            float column = shape.FormWidth;

            var title = UseSignal(form.InitialTitle);
            var f1 = UseSignal("");
            var f2 = UseSignal("");
            var f3 = UseSignal("");
            var when = UseSignal(0);                    // "On launch"
            var repro = UseSignal(0);                   // "Every time"
            var area = UseSignal(form.DefaultArea);     // "Not sure"
            var includeLogs = UseSignal(form.IncludeLogsByDefault);
            var showPreview = UseSignal(false);         // Compact: the folded preview's state
            var post = UsePost();
            var hooks = UseContext(InputHooks.Current);

            // Off-thread compose (the form's); cancelled on unmount (Cancel / a kind switch).
            UseEffect(() =>
            {
                Action? cancel = form.BeginCompose(post);
                return cancel;
            }, DepKey.Empty);

            Feedback.ReportDraft Draft() => new(title.Peek(), f1.Peek(), f2.Peek(), f3.Peek(), when.Peek(), repro.Peek(), area.Peek());

            UseEffect(() =>
            {
                dialog.InstallSubmit(() => form.Submit(Draft(), includeLogs.Peek(), hooks));
                return (Action?)(() => dialog.InstallSubmit(null));
            }, DepKey.Empty);

            var current = form.Composed.Value;
            var labels = form.Labels;
            var draft = new Feedback.ReportDraft(title.Value, f1.Value, f2.Value, f3.Value, when.Value, repro.Value, area.Value);
            bool logs = includeLogs.Value;
            string preview = form.Preview(current, draft, logs);
            bool ready = current is not null;

            void Copy() => form.Copy(Draft(), includeLogs.Peek(), hooks);
            void Save() => form.Save(Draft(), includeLogs.Peek());

            // ── the form column ──
            var fields = new List<Element>(12);
            fields.AddRange(args.Head);
            if (isCrash)
                fields.Add(new BoxEl { Direction = 1, Shrink = 0f, Width = column, Children = [CrashInfoBar(form, current, column)] });

            fields.Add(TextBox.Create(title, null, new TextBox.TextBoxOptions
            {
                Header = labels.Title, Placeholder = Loc.Get(Strings.Report.TitlePlaceholder), Width = column, Height = layout.TextFieldHeight,
            }));
            AddKindFields(fields, kind, form, labels, f1, f2, f3, when, repro, area, layout, column);
            fields.Add(Check(Strings.Report.IncludeLogs(form.LogLineCount), includeLogs, layout.Target));

            // ── the preview, where the tier puts it ──
            if (layout.TwoPane)
            {
                float pane = shape.PreviewPaneWidth;
                return new BoxEl
                {
                    Key = "report:body", Direction = 0, Width = shape.BodyWidth,
                    Children =
                    [
                        new BoxEl { Key = "report:form", Direction = 1, Gap = Spacing.M, Width = column, Shrink = 0f, Children = fields.ToArray() },
                        new BoxEl
                        {
                            Key = "report:divider", Width = WinUiScreenDialogTiers.DividerWidth, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
                            Margin = new Edges4(WinUiScreenDialogTiers.PaneGap, 0f, WinUiScreenDialogTiers.PaneGap, 0f), Fill = Tok.StrokeDividerDefault,
                        },
                        new BoxEl
                        {
                            Key = "report:preview", Direction = 1, Gap = Spacing.S, Width = pane, Shrink = 0f,
                            Children =
                            [
                                PreviewHeading(),
                                PreviewBox(preview, pane, layout.PreviewHeight),
                                new BoxEl
                                {
                                    Direction = 0, Gap = Spacing.S, Shrink = 0f,
                                    Children =
                                    [
                                        Button.Create(Loc.Get(Strings.Report.Copy), Copy, ButtonAppearance.Standard, glyph: Icons.Copy, isEnabled: ready),
                                        Button.Standard(Loc.Get(Strings.Report.SaveAs), Save, isEnabled: ready),
                                    ],
                                },
                                PreviewNote(pane),
                            ],
                        },
                    ],
                };
            }

            if (layout.PreviewFolds)
            {
                bool open = showPreview.Value;
                fields.Add(FoldRow(open, () => showPreview.Value = !showPreview.Peek(), column, layout.Target));
                if (open)
                {
                    fields.Add(new BoxEl
                    {
                        Key = "report:preview", Direction = 1, Gap = Spacing.S, Width = column, Shrink = 0f,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0, Gap = Spacing.S, Shrink = 0f,
                                Children =
                                [
                                    CommonTouch.Button(Loc.Get(Strings.Report.Copy), Copy, layout.Target, enabled: ready) with { Grow = 1f, Basis = 0f, MinWidth = 0f },
                                    CommonTouch.Button(Loc.Get(Strings.Report.SaveAs), Save, layout.Target, enabled: ready) with { Grow = 1f, Basis = 0f, MinWidth = 0f },
                                ],
                            },
                            PreviewBox(preview, column, layout.PreviewHeight),
                            PreviewNote(column),
                        ],
                    });
                }
                return new BoxEl { Key = "report:body", Direction = 1, Gap = Spacing.M, Width = shape.BodyWidth, Children = fields.ToArray() };
            }

            fields.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Width = column, Shrink = 0f,
                Children =
                [
                    PreviewHeading(),
                    Spacer(),
                    Button.Subtle(Loc.Get(Strings.Report.Copy), Copy, isEnabled: ready),
                    Button.Subtle(Loc.Get(Strings.Report.SaveAs), Save, isEnabled: ready),
                ],
            });
            fields.Add(PreviewBox(preview, column, layout.PreviewHeight));
            fields.Add(PreviewNote(column));
            return new BoxEl { Key = "report:body", Direction = 1, Gap = Spacing.M, Width = shape.BodyWidth, Children = fields.ToArray() };
        }

        static Element PreviewHeading() => new TextEl(Loc.Get(Strings.Report.Preview))
            { Size = WinUiFeedbackRules.SmallTextSize, Weight = WinUiFeedbackRules.HeadingWeight, Color = Tok.TextSecondary };

        static Element PreviewNote(float width) => new TextEl(Loc.Get(Strings.Report.PreviewNote))
            { Size = WinUiFeedbackRules.SmallTextSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = width };

        /// <summary>The scrolling monospace report (a fixed-height box the whole report scrolls inside).</summary>
        static Element PreviewBox(string text, float width, float height) => new BoxEl
        {
            Width = width, Height = height, Shrink = 0f, ClipToBounds = true, Fill = Tok.FillSolidBase,
            BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault, Corners = Radii.ControlAll,
            Children =
            [
                new ScrollEl
                {
                    Height = height, ScrollKey = "report-preview", EdgeCues = ScrollEdgeCues.None,
                    Content = new BoxEl
                    {
                        Padding = Edges4.All(WinUiFeedbackRules.PreviewPadding),
                        Children = [R.Type.MicroMeta(text)
                            with { FontFamily = "Cascadia Code", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap,
                                   MaxWidth = width - WinUiFeedbackRules.PreviewTextInset }],
                    },
                },
            ],
        };

        /// <summary>Compact: the "Report preview" row, a 40-DIP target that opens and closes the report under it.</summary>
        static Element FoldRow(bool open, Action toggle, float width, float target) => new BoxEl
        {
            Key = "report:fold", Direction = 0, Height = target, Width = width, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = WinUiScreenDialogTiers.FoldGap,
            Padding = new Edges4(WinUiScreenDialogTiers.FoldPadX, 0f, WinUiScreenDialogTiers.FoldPadX, 0f), Corners = Radii.ControlAll,
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = toggle,
            Children =
            [
                Body(Loc.Get(Strings.Report.Preview)) with { Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Icon(open ? Icons.ChevronUp : Icons.ChevronDown, WinUiFeedbackRules.FoldChevronSize, Tok.TextSecondary),
            ],
        };

        static void AddKindFields(List<Element> body, Feedback.ReportKind kind, IFeedbackForm form, Feedback.ReportLabels l,
            Signal<string> f1, Signal<string> f2, Signal<string> f3, Signal<int> when, Signal<int> repro, Signal<int> area,
            WinUiScreenDialogLayout layout, float width)
        {
            switch (kind)
            {
                case Feedback.ReportKind.Crash:
                    body.Add(Choice(form.Whens, when, l.When, layout, width));
                    body.Add(Choice(form.Reproduces, repro, l.Reproduces, layout, width));
                    body.Add(MultiLine(f1, l.WhatWereYouDoing, WinUiFeedbackRules.FieldHeight, width));
                    break;
                case Feedback.ReportKind.Bug:
                    body.Add(MultiLine(f1, l.WhatHappened, WinUiFeedbackRules.FieldHeight, width));
                    body.Add(MultiLine(f2, l.Steps, WinUiFeedbackRules.FieldHeight, width));
                    body.Add(MultiLine(f3, l.Expected, WinUiFeedbackRules.FieldHeight, width));
                    body.Add(Choice(form.Areas, area, l.Area, layout, width));
                    break;
                case Feedback.ReportKind.Feature:
                    body.Add(MultiLine(f1, l.Problem, WinUiFeedbackRules.FieldHeight, width));
                    body.Add(MultiLine(f2, l.Proposal, WinUiFeedbackRules.FieldHeight, width));
                    body.Add(Choice(form.Areas, area, l.Area, layout, width));
                    body.Add(MultiLine(f3, l.Alternatives, WinUiFeedbackRules.FieldHeight, width));
                    break;
                default:   // Question, Idea
                    body.Add(MultiLine(f1, l.Body, WinUiFeedbackRules.BodyFieldHeight, width));
                    break;
            }
        }

        /// <summary>A dropdown: the stock ComboBox, or at a hit target the header over the stock ComboBox at its 40-DIP field, filling the column.</summary>
        static Element Choice(IReadOnlyList<string> items, Signal<int> selected, string header, WinUiScreenDialogLayout layout, float width)
            => layout.Touch
                ? new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Width = width, Shrink = 0f,
                    Children = [Body(header), CommonTouch.Picker(items, selected, WinUiFeedbackRules.ComboWidth, layout.Target)],
                }
                : ComboBox.Create(items, selected, header: header, width: WinUiFeedbackRules.ComboWidth);

        static Element MultiLine(Signal<string> text, string header, float height, float width) => TextBox.Create(text, null, new TextBox.TextBoxOptions
        {
            Header = header, Width = width, AcceptsReturn = true, Height = height,
        });

        /// <summary>Three honest states (the form's): still composing, a report with its summary, or genuinely no report.</summary>
        static Element CrashInfoBar(IFeedbackForm form, Feedback.ComposedReport? current, float width)
        {
            var (message, severity) = form.CrashNotice(current);
            return InfoBar.Create(severity, Loc.Get(Strings.Report.CrashTitle), message, isClosable: false, availableWidth: width);
        }
    }
}
