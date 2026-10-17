// ── Themes/Zune/Structure/Views/Screens/ZuneFeedback.cs — Zune's `Dialog.Feedback` (themes plan 6.3, P5 Z8b) ───────────────────────────────────
//
// Board Z-Dialogs ("report a problem"): the dialog's BODY, the subtitle, the kind switch (never in crash mode) and the form: ruled fields (a rule at 18 % ink, the ink when focused),
// the include-logs check, the report preview in the well with its note, and copy / save as beside the preview's heading. The stock ContentDialog chrome (the 26/300 title, the
// commands as words, the primary as the round accent button, the vetoable primary click) stays core; so does everything that decides or does: the off-thread compose, the preview
// text, the submit gate and the three doors are `IFeedbackForm` (`Screens\Feedback.Form.cs`).
//
// The kind selection lives in the template because switching kind must REMOUNT the whole form (its answer set is kind-shaped): the card is a nested component keyed by the kind, so
// every signal in it is seeded once and a kind switch drops them all. The preview recomputes per keystroke (subscribing reads); the submit the card installs reads the same signals
// with Peek so it never goes stale.
//
// THE TIERS (Z-Dialogs-Compact / -Small / -Huge; `Rules\ZuneSetupRules.cs`). The body is laid out by the record of the window tier the dialog OPENED in and the width its card left
// it (`ZuneDialogShape`, frozen in a ref: the card is sized once, at open, so a body that re-measured would outgrow it): Compact (fields 40 tall, the preview folds behind a 40 row),
// Small (the 752 card: a second pane, the form, a rule, the live report; a card too narrow for the pane keeps the report under the form), Standard and Huge (the report always
// under the form: the Huge board draws the same 548 card).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Z = Wavee.Themes.Zune.ZuneDialogRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's report dialog body.</summary>
internal static class ZuneFeedback
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogFeedback"/>: the subtitle, the kind switch and the form.</summary>
    public static Element Template(FeedbackDialogProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        TierFacts tiers = ctx.Tier();   // a read, so the template follows the facts; the shape below is the dialog's own, frozen at open
        var viewport = hooks.UseContextSignal(Viewport.Size);
        var shapeRef = hooks.UseRef<ZuneDialogShape?>(null);
        ZuneDialogShape shape = shapeRef.Value ??= ZuneDialogTiers.Open(in tiers, viewport.Peek().Width);

        bool isCrash = props.InitialKind == Feedback.ReportKind.Crash;
        var kindIndex = hooks.UseSignal(Feedback.ReportKindIndex.IndexOf(props.InitialKind));
        Feedback.ReportKind kind = isCrash ? Feedback.ReportKind.Crash : Feedback.ReportKindIndex.KindAt(kindIndex.Value);

        var head = new List<Element>(2)
        {
            ZuneText.Prose(Loc.Get(Strings.Report.Subtitle)) with { MaxWidth = shape.FormWidth },
        };
        if (!isCrash) head.Add(KindSwitch(kindIndex, shape));

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Width = shape.BodyWidth,
            Children =
            [
                Embed.Comp(new CardArgs(props.Dialog, kind, shape, head.ToArray()), static () => new FeedbackCard()) with { Key = "zreport-card:" + (int)kind },
            ],
        };
    }

    /// <summary>The kind switch: the chip row (Zune's segmented control), or at a hit target (Compact) a wrapping row of buttons that tall, so four labels never clip in a narrow card.</summary>
    static Element KindSwitch(Signal<int> kindIndex, ZuneDialogShape shape)
    {
        string[] labels =
        [
            Loc.Get(Strings.Report.KindBug), Loc.Get(Strings.Report.KindFeature), Loc.Get(Strings.Report.KindQuestion), Loc.Get(Strings.Report.KindIdea),
        ];
        if (!shape.Layout.Touch)
        {
            var items = new SegmentedItem[labels.Length];
            for (int i = 0; i < labels.Length; i++) items[i] = new SegmentedItem(labels[i]);
            return Segmented.Create(items, kindIndex);
        }

        var buttons = new Element[labels.Length];
        int selected = kindIndex.Value;
        for (int i = 0; i < labels.Length; i++)
        {
            int at = i;
            buttons[i] = Button.Create(labels[i], () => kindIndex.Value = at, at == selected ? ButtonAppearance.Accent : ButtonAppearance.Subtle, ControlSize.Large) with
            {
                Key = "zreport-kind:" + at, Grow = 1f, Basis = Z.KindMinWidth, MinWidth = 0f, Justify = FlexJustify.Center,
            };
        }
        return new BoxEl { Key = "zreport-kinds", Direction = 0, Wrap = true, Gap = Z.KindGap, Width = shape.FormWidth, Shrink = 0f, Children = buttons };
    }

    /// <summary>A check box whose label WRAPS inside its column (the stock label is one line, so a long label overruns a narrow Compact card) and whose row is at least
    /// <paramref name="target"/> tall; Zune's square 18 check box.</summary>
    internal static Element Check(string label, Signal<bool> isChecked, float target)
    {
        var parts = new TemplateParts();
        parts.Set<TextEl>(CheckBox.PartLabel, t => ZuneText.Cased(t, LabelKind.Label) with { Wrap = TextWrap.Wrap, MinWidth = 0f, Shrink = 1f });
        var style = target > 0f ? CheckBox.DefaultStyle with { MinHeight = target } : CheckBox.DefaultStyle;
        return CheckBox.Create(label, isChecked, style: style, parts: parts);
    }

    /// <summary>The scrolling monospace report (a fixed-height box the whole report scrolls inside), in the well.</summary>
    internal static Element PreviewBox(string text, float width, float height, string scrollKey) => new BoxEl
    {
        Width = width, Height = height, Shrink = 0f, ClipToBounds = true, Fill = R.Layer.PlayerBar,
        Children =
        [
            new ScrollEl
            {
                Height = height, ScrollKey = scrollKey, EdgeCues = ScrollEdgeCues.None,
                Content = new BoxEl
                {
                    Padding = Edges4.All(Z.PreviewPadding),
                    Children = [R.Type.MicroMeta(text) with { FontFamily = Z.Mono, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = width - Z.PreviewTextInset }],
                },
            },
        ],
    };

    /// <summary>What a card mounts with: the open dialog, the kind it shows (a switch remounts, so it never changes), the dialog's shape and the head (the subtitle and the kind
    /// switch) the card places in its form column.</summary>
    internal sealed record CardArgs(IFeedbackDialog Dialog, Feedback.ReportKind Kind, ZuneDialogShape Shape, Element[] Head);

    /// <summary>The form for ONE fixed kind, remounted whole on a kind switch, so every signal is seeded once.</summary>
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
            var k = new ZuneKit(ZuneSettingsTiers.For(layout.Tier));

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
            {
                var (message, severity) = form.CrashNotice(current);
                fields.Add(new BoxEl
                {
                    Direction = 1, Shrink = 0f, Width = column,
                    Children = [InfoBar.Create(severity, Loc.Get(Strings.Report.CrashTitle), message, isClosable: false, availableWidth: column)],
                });
            }

            fields.Add(TextBox.Create(title, null, new TextBox.TextBoxOptions
            {
                Header = labels.Title, Placeholder = Loc.Get(Strings.Report.TitlePlaceholder), Width = column, Height = layout.TextFieldHeight,
            }));
            AddKindFields(fields, kind, form, labels, f1, f2, f3, when, repro, area, layout, column);
            fields.Add(Check(Strings.Report.IncludeLogs(form.LogLineCount), includeLogs, layout.Target));

            // ── the preview, where the tier puts it ──
            Element[] doors =
            [
                k.Cmd(Loc.Get(Strings.Report.Copy), Copy, ready),
                k.Cmd(Loc.Get(Strings.Report.SaveAs), Save, ready),
            ];
            if (layout.TwoPane)
            {
                float pane = shape.PreviewPaneWidth;
                return new BoxEl
                {
                    Key = "zreport:body", Direction = 0, Width = shape.BodyWidth,
                    Children =
                    [
                        new BoxEl { Key = "zreport:form", Direction = 1, Gap = Spacing.M, Width = column, Shrink = 0f, Children = fields.ToArray() },
                        new BoxEl
                        {
                            Key = "zreport:divider", Width = Z.DividerWidth, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
                            Margin = new Edges4(Z.PaneGap, 0f, Z.PaneGap, 0f), Fill = Tok.StrokeDividerDefault,
                        },
                        new BoxEl
                        {
                            Key = "zreport:preview", Direction = 1, Gap = Spacing.S, Width = pane, Shrink = 0f,
                            Children =
                            [
                                PreviewHeading(),
                                PreviewBox(preview, pane, layout.PreviewHeight, "zreport-preview"),
                                new BoxEl { Direction = 0, Gap = Z.DoorGap, Shrink = 0f, Children = doors },
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
                        Key = "zreport:preview", Direction = 1, Gap = Spacing.S, Width = column, Shrink = 0f,
                        Children =
                        [
                            new BoxEl { Direction = 0, Gap = Z.DoorGap, Shrink = 0f, Children = doors },
                            PreviewBox(preview, column, layout.PreviewHeight, "zreport-preview"),
                            PreviewNote(column),
                        ],
                    });
                }
                return new BoxEl { Key = "zreport:body", Direction = 1, Gap = Spacing.M, Width = shape.BodyWidth, Children = fields.ToArray() };
            }

            fields.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Width = column, Shrink = 0f,
                Children = [PreviewHeading(), Spacer(), .. doors],
            });
            fields.Add(PreviewBox(preview, column, layout.PreviewHeight, "zreport-preview"));
            fields.Add(PreviewNote(column));
            return new BoxEl { Key = "zreport:body", Direction = 1, Gap = Spacing.M, Width = shape.BodyWidth, Children = fields.ToArray() };
        }

        static Element PreviewHeading() => ZuneText.Hint(Loc.Get(Strings.Report.Preview)) with { Color = Tok.TextSecondary };

        static Element PreviewNote(float width) => ZuneText.Hint(Loc.Get(Strings.Report.PreviewNote)) with { MaxWidth = width };

        /// <summary>Compact: the "report preview" row, a 40-DIP target that opens and closes the report under it.</summary>
        static Element FoldRow(bool open, Action toggle, float width, float target) => new BoxEl
        {
            Key = "zreport:fold", Direction = 0, Height = target, Width = width, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Z.FoldGap,
            Padding = new Edges4(Z.FoldPadX, 0f, Z.FoldPadX, 0f), Fill = Tok.FillSubtleSecondary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = toggle,
            Children =
            [
                ZuneText.Label(Loc.Get(Strings.Report.Preview)) with { Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Icon(open ? Icons.ChevronUp : Icons.ChevronDown, Z.FoldChevronSize, Tok.TextSecondary),
            ],
        }.Interactive(Interaction.Subtle);

        static void AddKindFields(List<Element> body, Feedback.ReportKind kind, IFeedbackForm form, Feedback.ReportLabels l,
            Signal<string> f1, Signal<string> f2, Signal<string> f3, Signal<int> when, Signal<int> repro, Signal<int> area,
            ZuneDialogLayout layout, float width)
        {
            switch (kind)
            {
                case Feedback.ReportKind.Crash:
                    body.Add(Choice(form.Whens, when, l.When, layout, width));
                    body.Add(Choice(form.Reproduces, repro, l.Reproduces, layout, width));
                    body.Add(MultiLine(f1, l.WhatWereYouDoing, Z.FieldHeight, width));
                    break;
                case Feedback.ReportKind.Bug:
                    body.Add(MultiLine(f1, l.WhatHappened, Z.FieldHeight, width));
                    body.Add(MultiLine(f2, l.Steps, Z.FieldHeight, width));
                    body.Add(MultiLine(f3, l.Expected, Z.FieldHeight, width));
                    body.Add(Choice(form.Areas, area, l.Area, layout, width));
                    break;
                case Feedback.ReportKind.Feature:
                    body.Add(MultiLine(f1, l.Problem, Z.FieldHeight, width));
                    body.Add(MultiLine(f2, l.Proposal, Z.FieldHeight, width));
                    body.Add(Choice(form.Areas, area, l.Area, layout, width));
                    body.Add(MultiLine(f3, l.Alternatives, Z.FieldHeight, width));
                    break;
                default:   // Question, Idea
                    body.Add(MultiLine(f1, l.Body, Z.BodyFieldHeight, width));
                    break;
            }
        }

        /// <summary>A dropdown: the ruled combo, or at a hit target the header over the combo at its 40-DIP field, filling the column.</summary>
        static Element Choice(IReadOnlyList<string> items, Signal<int> selected, string header, ZuneDialogLayout layout, float width)
            => layout.Touch
                ? new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Width = width, Shrink = 0f,
                    Children = [ZuneText.Hint(header), CommonTouch.Picker(items, selected, Z.ComboWidth, layout.Target)],
                }
                : ComboBox.Create(items, selected, header: header, width: Z.ComboWidth);

        static Element MultiLine(Signal<string> text, string header, float height, float width) => TextBox.Create(text, null, new TextBox.TextBoxOptions
        {
            Header = header, Width = width, AcceptsReturn = true, Height = height,
        });
    }
}
