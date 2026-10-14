// ── Themes/Classic/Structure/Views/Screens/ClassicFeedback.cs — Classic's `Dialog.Feedback` (themes plan 6.1, T11c) ────────────────
//
// From `Feedback.UI.cs`: the report dialog's BODY — the subtitle, the kind switch (never in crash mode) and the form. The stock ContentDialog chrome
// (title, buttons, the vetoable primary click) stays core, and so does everything that decides or does: the off-thread compose, the preview text, the
// submit gate and the three doors are `IFeedbackForm` (`Screens\Feedback.Form.cs`).
//
// The Segmented selection lives in the template because switching kind must REMOUNT the whole form (its answer set is kind-shaped): the card is a
// nested component keyed by the kind, so every signal in it is seeded once and a kind switch drops them all. The preview recomputes per keystroke
// (subscribing reads); the submit the card installs reads the same signals with Peek so it never goes stale.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's report dialog body.</summary>
internal static class ClassicFeedback
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogFeedback"/>: the subtitle, the kind switch and the form.</summary>
    public static Element Template(FeedbackDialogProps props, in ViewContext ctx)
    {
        bool isCrash = props.InitialKind == Feedback.ReportKind.Crash;
        var kindIndex = ctx.Hooks.UseSignal(Feedback.ReportKindIndex.IndexOf(props.InitialKind));
        Feedback.ReportKind kind = isCrash ? Feedback.ReportKind.Crash : Feedback.ReportKindIndex.KindAt(kindIndex.Value);

        var children = new List<Element>(3)
        {
            new TextEl(Loc.Get(Strings.Report.Subtitle))
                { Size = ClassicFeedbackRules.SubtitleSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = ClassicFeedbackRules.FieldWidth },
        };
        if (!isCrash)
            children.Add(Segmented.Create(
            [
                new SegmentedItem(Loc.Get(Strings.Report.KindBug)),
                new SegmentedItem(Loc.Get(Strings.Report.KindFeature)),
                new SegmentedItem(Loc.Get(Strings.Report.KindQuestion)),
                new SegmentedItem(Loc.Get(Strings.Report.KindIdea)),
            ], kindIndex));
        children.Add(Embed.Comp(new CardArgs(props.Dialog, kind), static () => new FeedbackCard())
            with { Key = "report-card:" + (int)kind });

        return new BoxEl { Direction = 1, Gap = Spacing.M, Width = ClassicFeedbackRules.FieldWidth, Children = children.ToArray() };
    }

    /// <summary>What a card mounts with: the open dialog and the kind it shows (a switch remounts, so it never changes).</summary>
    internal sealed record CardArgs(IFeedbackDialog Dialog, Feedback.ReportKind Kind);

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

            var title = UseSignal(form.InitialTitle);
            var f1 = UseSignal("");
            var f2 = UseSignal("");
            var f3 = UseSignal("");
            var when = UseSignal(0);                    // "On launch"
            var repro = UseSignal(0);                   // "Every time"
            var area = UseSignal(form.DefaultArea);     // "Not sure"
            var includeLogs = UseSignal(form.IncludeLogsByDefault);
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

            var body = new List<Element>(12);
            if (isCrash)
                body.Add(new BoxEl { Direction = 1, Shrink = 0f, Width = ClassicFeedbackRules.FieldWidth, Children = [CrashInfoBar(form, current)] });

            body.Add(TextBox.Create(title, null, new TextBox.TextBoxOptions
            {
                Header = labels.Title, Placeholder = Loc.Get(Strings.Report.TitlePlaceholder), Width = ClassicFeedbackRules.FieldWidth,
            }));
            AddKindFields(body, kind, form, labels, f1, f2, f3, when, repro, area);
            body.Add(CheckBox.Create(Strings.Report.IncludeLogs(form.LogLineCount), includeLogs));

            string preview = form.Preview(current, draft, logs);
            body.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Width = ClassicFeedbackRules.FieldWidth, Shrink = 0f,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Report.Preview))
                        { Size = ClassicFeedbackRules.SmallTextSize, Weight = ClassicFeedbackRules.HeadingWeight, Color = Tok.TextSecondary },
                    Spacer(),
                    Button.Subtle(Loc.Get(Strings.Report.Copy), () => form.Copy(Draft(), includeLogs.Peek(), hooks),
                        isEnabled: current is not null),
                    Button.Subtle(Loc.Get(Strings.Report.SaveAs), () => form.Save(Draft(), includeLogs.Peek()),
                        isEnabled: current is not null),
                ],
            });
            body.Add(new BoxEl
            {
                Width = ClassicFeedbackRules.FieldWidth, Height = ClassicFeedbackRules.PreviewHeight, Shrink = 0f, ClipToBounds = true, Fill = Tok.FillSolidBase,
                BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault, Corners = Radii.ControlAll,
                Children =
                [
                    new ScrollEl
                    {
                        Height = ClassicFeedbackRules.PreviewHeight, ScrollKey = "report-preview", EdgeCues = ScrollEdgeCues.None,
                        Content = new BoxEl
                        {
                            Padding = Edges4.All(ClassicFeedbackRules.PreviewPadding),
                            Children = [R.Type.MicroMeta(preview)
                                with { FontFamily = "Cascadia Code", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap,
                                       MaxWidth = ClassicFeedbackRules.FieldWidth - ClassicFeedbackRules.PreviewTextInset }],
                        },
                    },
                ],
            });
            body.Add(new TextEl(Loc.Get(Strings.Report.PreviewNote))
                { Size = ClassicFeedbackRules.SmallTextSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = ClassicFeedbackRules.FieldWidth });

            return new BoxEl { Direction = 1, Gap = Spacing.M, Width = ClassicFeedbackRules.FieldWidth, Children = body.ToArray() };
        }

        static void AddKindFields(List<Element> body, Feedback.ReportKind kind, IFeedbackForm form, Feedback.ReportLabels l,
            Signal<string> f1, Signal<string> f2, Signal<string> f3, Signal<int> when, Signal<int> repro, Signal<int> area)
        {
            switch (kind)
            {
                case Feedback.ReportKind.Crash:
                    body.Add(ComboBox.Create(form.Whens, when, header: l.When, width: ClassicFeedbackRules.ComboWidth));
                    body.Add(ComboBox.Create(form.Reproduces, repro, header: l.Reproduces, width: ClassicFeedbackRules.ComboWidth));
                    body.Add(MultiLine(f1, l.WhatWereYouDoing, ClassicFeedbackRules.FieldHeight));
                    break;
                case Feedback.ReportKind.Bug:
                    body.Add(MultiLine(f1, l.WhatHappened, ClassicFeedbackRules.FieldHeight));
                    body.Add(MultiLine(f2, l.Steps, ClassicFeedbackRules.FieldHeight));
                    body.Add(MultiLine(f3, l.Expected, ClassicFeedbackRules.FieldHeight));
                    body.Add(ComboBox.Create(form.Areas, area, header: l.Area, width: ClassicFeedbackRules.ComboWidth));
                    break;
                case Feedback.ReportKind.Feature:
                    body.Add(MultiLine(f1, l.Problem, ClassicFeedbackRules.FieldHeight));
                    body.Add(MultiLine(f2, l.Proposal, ClassicFeedbackRules.FieldHeight));
                    body.Add(ComboBox.Create(form.Areas, area, header: l.Area, width: ClassicFeedbackRules.ComboWidth));
                    body.Add(MultiLine(f3, l.Alternatives, ClassicFeedbackRules.FieldHeight));
                    break;
                default:   // Question, Idea
                    body.Add(MultiLine(f1, l.Body, ClassicFeedbackRules.BodyFieldHeight));
                    break;
            }
        }

        static Element MultiLine(Signal<string> text, string header, float height) => TextBox.Create(text, null, new TextBox.TextBoxOptions
        {
            Header = header, Width = ClassicFeedbackRules.FieldWidth, AcceptsReturn = true, Height = height,
        });

        /// <summary>Three honest states (the form's): still composing, a report with its summary, or genuinely no report.</summary>
        static Element CrashInfoBar(IFeedbackForm form, Feedback.ComposedReport? current)
        {
            var (message, severity) = form.CrashNotice(current);
            return InfoBar.Create(severity, Loc.Get(Strings.Report.CrashTitle), message, isClosable: false, availableWidth: ClassicFeedbackRules.FieldWidth);
        }
    }
}
