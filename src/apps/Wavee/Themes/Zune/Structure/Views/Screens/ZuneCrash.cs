// ── Themes/Zune/Structure/Views/Screens/ZuneCrash.cs — Zune's `Dialog.Crash` (themes plan 6.3, P5 Z8b) ────────────────────────────────────────────
//
// Board Z-Dialogs ("wavee closed unexpectedly"): the crash / hang consent card's BODY (the stock ContentDialog chrome, the launch decision, the consent flow and the outcome toasts
// stay core). Every signal is seeded once from the props at mount (the props freeze: the dialog is keyed by report id, and a re-render must not re-seed the user's in-progress
// choices). The header line (the amber "last run ended 3 minutes ago"), the lead (not from the Saved reports list), the "what's in the report" list (a check for what is sent, a
// cross for what never is) with its "read the full report" command, the include-dump check (when the bundle has a dump), the "always send" check (not from the list) and the
// privacy and "report on github instead" commands.
//
// The preview is composed off the UI thread (the scrub touches file I/O and the redaction pass): `props.PrepareCompose` runs on the UI thread and returns the pool-thread function;
// the result is posted back and cancelled on unmount. Send is VETOABLE: the card installs its submit, which says why with a toast and keeps the dialog open while the preview has
// not finished composing.
//
// THE TIERS (`Rules\ZuneSetupRules.cs`, `ZuneDialogTiers`): the body is laid out by the record of the window tier the dialog OPENED in and the width its card left it
// (`ZuneDialogShape`, frozen in a ref): Compact fills the window less 16 with 40-tall targets; Small and Standard the tuned 548 card with the report under its command; Huge a
// second pane, the report always shown.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Z = Wavee.Themes.Zune.ZuneDialogRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's crash / hang consent card.</summary>
internal static class ZuneCrash
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogCrash"/>.</summary>
    public static Element Template(CrashDialogProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        TierFacts tiers = ctx.Tier();   // a read, so the template follows the facts; the shape below is the dialog's own, frozen at open
        var viewport = hooks.UseContextSignal(Viewport.Size);
        var shapeRef = hooks.UseRef<ZuneDialogShape?>(null);
        ZuneDialogShape shape = shapeRef.Value ??= ZuneDialogTiers.Open(in tiers, viewport.Peek().Width);
        var layout = shape.Layout;
        float column = shape.FormWidth;
        var k = new ZuneKit(ZuneSettingsTiers.For(layout.Tier));

        var showReport = hooks.UseSignal(false);
        var includeDump = hooks.UseSignal(props.IncludeDumpDefault);
        var always = hooks.UseSignal(false);
        var preview = hooks.UseSignal<string?>(null);
        var post = hooks.UsePost();

        // Off-thread compose; cancelled on unmount. The UI-thread half (the live account / device seams) runs first.
        hooks.UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            var compose = props.PrepareCompose();
            Task.Run(() =>
            {
                string text = compose();
                if (!cts.IsCancellationRequested) post(() => { if (!cts.IsCancellationRequested) preview.Value = text; });
            });
            return (Action?)(() => cts.Cancel());
        }, DepKey.Empty);

        hooks.UseEffect(() =>
        {
            props.InstallSubmit(() =>
            {
                if (preview.Peek() is null)
                {
                    props.NotifyPreparing();
                    return false;
                }
                props.Submit(always.Peek(), includeDump.Peek());
                return true;
            });
            return (Action?)(() => props.InstallSubmit(null));
        }, DepKey.Empty);

        var children = new List<Element>(10)
        {
            ZuneText.Cased(R.Type.DenseMeta(props.HeaderMessage), LabelKind.Label)
                with { Color = Tok.SystemFillCaution, Wrap = TextWrap.Wrap, MinWidth = 0f, MaxWidth = column },
        };
        if (!props.FromList) children.Add(R.Type.DenseTitle(props.Lead) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, MaxWidth = column });
        children.Add(WhatIsSent(k, showReport, preview.Value, layout, column));
        if (props.HasDump) children.Add(IncludeDumpRow(includeDump, props.IncludeDumpLabel, layout, column));
        if (!props.FromList) children.Add(ZuneFeedback.Check(Loc.Get(Strings.Crash.PromptAlways), always, layout.Target));
        children.Add(new BoxEl
        {
            Direction = 0, Gap = Z.LinkGap, AlignItems = FlexAlign.Center, Wrap = true, Width = column,
            Children =
            [
                k.Link(Loc.Get(Strings.Crash.PromptPrivacy), props.PrivacyUrl),
                k.Cmd(Loc.Get(Strings.Crash.PromptGithub), props.ReportOnGithub),
            ],
        });
        if (!props.Configured)
            children.Add(ZuneText.Hint(Loc.Get(Strings.Crash.PromptNotConfigured)) with { MaxWidth = column });

        var prompt = new BoxEl { Key = "zcrash:prompt", Direction = 1, Gap = Spacing.M, Width = column, Shrink = 0f, Children = children.ToArray() };
        if (!layout.TwoPane) return new BoxEl { Key = "zcrash:body", Direction = 1, Width = shape.BodyWidth, Children = [prompt] };

        float pane = shape.PreviewPaneWidth;
        return new BoxEl
        {
            Key = "zcrash:body", Direction = 0, Width = shape.BodyWidth,
            Children =
            [
                prompt,
                new BoxEl
                {
                    Key = "zcrash:divider", Width = Z.DividerWidth, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
                    Margin = new Edges4(Z.PaneGap, 0f, Z.PaneGap, 0f), Fill = Tok.StrokeDividerDefault,
                },
                new BoxEl
                {
                    Key = "zcrash:report", Direction = 1, Gap = Spacing.S, Width = pane, Shrink = 0f,
                    Children =
                    [
                        ZuneText.Hint(Loc.Get(Strings.Report.Preview)) with { Color = Tok.TextSecondary },
                        ZuneFeedback.PreviewBox(preview.Value ?? Loc.Get(Strings.Report.Preparing), pane, layout.PreviewHeight, "zcrash-prompt-preview"),
                    ],
                },
            ],
        };
    }

    /// <summary>"what's in the report": a check for each thing sent, a cross for what never is, and the command that opens the scrubbed report under it.</summary>
    static Element WhatIsSent(ZuneKit k, Signal<bool> showReport, string? preview, ZuneDialogLayout layout, float width)
    {
        var kids = new List<Element>(8)
        {
            ZuneText.Hint(Loc.Get(Strings.Crash.PromptWhatIsSent)) with { Color = Tok.TextSecondary },
            Bullet(Loc.Get(Strings.Crash.PromptBullet1), positive: true, width),
            Bullet(Loc.Get(Strings.Crash.PromptBullet2), positive: true, width),
            Bullet(Loc.Get(Strings.Crash.PromptBullet3), positive: true, width),
            Bullet(Loc.Get(Strings.Crash.PromptBullet4), positive: false, width),
        };
        // A two-pane body shows the report beside the prompt, always: there is nothing to open.
        if (!layout.TwoPane)
        {
            kids.Add(new BoxEl
            {
                Direction = 0, AlignSelf = FlexAlign.Start,
                Children = [k.Cmd(Loc.Get(showReport.Value ? Strings.Crash.PromptHideFull : Strings.Crash.PromptReadFull), () => showReport.Value = !showReport.Peek())],
            });
            if (showReport.Value)
                kids.Add(ZuneFeedback.PreviewBox(preview ?? Loc.Get(Strings.Report.Preparing), width, layout.PreviewHeight, "zcrash-prompt-preview"));
        }
        return new BoxEl { Direction = 1, Gap = Spacing.S, Width = width, Shrink = 0f, Children = kids.ToArray() };
    }

    static Element Bullet(string text, bool positive, float width) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start, Width = width,
        Children =
        [
            Icon(positive ? Icons.Check : Icons.Cancel, Z.BulletGlyphSize, positive ? Tok.SystemFillSuccess : Tok.SystemFillCritical)
                with { Margin = new Edges4(0f, Z.BulletGlyphTop, 0f, 0f), Shrink = 0f },
            R.Type.DenseTitle(text) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f, Grow = 1f, Basis = 0f },
        ],
    };

    static Element IncludeDumpRow(Signal<bool> includeDump, string label, ZuneDialogLayout layout, float width) => new BoxEl
    {
        Direction = 1, Gap = Z.DumpGap, Width = width,
        Children =
        [
            ZuneFeedback.Check(label, includeDump, layout.Target),
            ZuneText.Hint(Loc.Get(Strings.Crash.PromptIncludeDumpSub)) with { MaxWidth = width - Z.DumpCaptionIndent, Margin = new Edges4(Z.DumpCaptionIndent, 0f, 0f, 0f) },
        ],
    };
}
