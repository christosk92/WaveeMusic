// ── Themes/WinUi/Structure/Views/Screens/WinUiCrash.cs — Classic's `Dialog.Crash` (themes plan 6.1, T11c; tiers TC8) ──────────────────────────
//
// From `Crash.UI.cs`: the crash / hang consent card's BODY (the stock ContentDialog chrome, the launch decision, the consent flow and the outcome toasts
// stay core). Every signal is seeded once from the props at mount (the props freeze — the dialog is keyed by report id, and a re-render
// must not re-seed the user's in-progress choices). The header bar, the lead (not from the Saved reports list), the "what is sent" box with its
// off-thread scrubbed preview, the include-dump box (when the bundle has a dump), the "always send" box (not from the list) and the privacy and
// "Report on GitHub instead" links.
//
// The preview is composed off the UI thread (the scrub touches file I/O and the redaction pass): `props.PrepareCompose` runs on the UI thread and returns
// the pool-thread function; the result is posted back and cancelled on unmount. Send is VETOABLE: the card installs its submit, which says why with a
// toast and keeps the dialog open while the preview has not finished composing.
//
// THE TIERS (TC8; `Rules\Tiers.Dialogs.cs`, boards C-Crash-Compact / -Small / -Huge). The body is laid out by the record of the window tier the dialog OPENED in and the
// width its card left it (`WinUiScreenDialogShape`, frozen in a ref: the card is sized once, at open):
//   Compact   the card fills the window less 16; the check boxes, the links and the "Read the full report" row are at least 40 tall.
//   Small, Standard   the tuned 548 card: the report opens under its link, inside the "what is sent" box.
//   Huge      a second pane: the prompt on the left (472), a divider, the scrubbed report on the right, always shown (no link to open it).

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

/// <summary>Wavee Classic's crash / hang consent card.</summary>
internal static class WinUiCrash
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogCrash"/>.</summary>
    public static Element Template(CrashDialogProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        TierFacts tiers = ctx.Tier();   // a read, so the template follows the facts; the shape below is the dialog's own, frozen at open
        var viewport = hooks.UseContextSignal(Viewport.Size);
        var shapeRef = hooks.UseRef<WinUiScreenDialogShape?>(null);
        WinUiScreenDialogShape shape = shapeRef.Value ??= WinUiScreenDialogTiers.Open(in tiers, viewport.Peek().Width);
        var layout = shape.Layout;
        float column = shape.FormWidth;

        var showReport = hooks.UseSignal(false);
        var includeDump = hooks.UseSignal(props.IncludeDumpDefault);
        var always = hooks.UseSignal(false);
        var preview = hooks.UseSignal<string?>(null);
        var post = hooks.UsePost();

        // Off-thread compose; cancelled on unmount. The UI-thread half (the live account/device seams) runs first.
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

        var children = new List<Element>(10) { HeaderInfoBar(props.HeaderMessage, column) };
        if (!props.FromList) children.Add(Lead(props.Lead, column));
        children.Add(WhatIsSentBox(showReport, preview.Value, layout, column));
        if (props.HasDump) children.Add(IncludeDumpRow(includeDump, props.IncludeDumpLabel, layout, column));
        if (!props.FromList) children.Add(WinUiFeedback.Check(Loc.Get(Strings.Crash.PromptAlways), always, layout.Target));
        children.Add(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = true, Width = column,
            Children =
            [
                CommonTouch.Lift(HyperlinkButton.Create(Loc.Get(Strings.Crash.PromptPrivacy), props.PrivacyUrl), layout.Target),
                new TextEl("·") { Color = Tok.TextTertiary },
                CommonTouch.Lift(HyperlinkButton.Create(Loc.Get(Strings.Crash.PromptGithub), props.ReportOnGithub), layout.Target),
            ],
        });
        if (!props.Configured)
            children.Add(new TextEl(Loc.Get(Strings.Crash.PromptNotConfigured))
                { Size = WinUiCrashRules.SmallTextSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = column });

        var prompt = new BoxEl { Key = "crash:prompt", Direction = 1, Gap = Spacing.M, Width = column, Shrink = 0f, Children = children.ToArray() };
        if (!layout.TwoPane) return new BoxEl { Key = "crash:body", Direction = 1, Width = shape.BodyWidth, Children = [prompt] };

        float pane = shape.PreviewPaneWidth;
        return new BoxEl
        {
            Key = "crash:body", Direction = 0, Width = shape.BodyWidth,
            Children =
            [
                prompt,
                new BoxEl
                {
                    Key = "crash:divider", Width = WinUiScreenDialogTiers.DividerWidth, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
                    Margin = new Edges4(WinUiScreenDialogTiers.PaneGap, 0f, WinUiScreenDialogTiers.PaneGap, 0f), Fill = Tok.StrokeDividerDefault,
                },
                new BoxEl
                {
                    Key = "crash:report", Direction = 1, Gap = Spacing.S, Width = pane, Shrink = 0f,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Report.Preview))
                            { Size = WinUiCrashRules.SmallTextSize, Weight = WinUiCrashRules.HeadingWeight, Color = Tok.TextSecondary },
                        PreviewBox(preview.Value, pane, layout.PreviewHeight),
                    ],
                },
            ],
        };
    }

    static Element HeaderInfoBar(string message, float width)
        => InfoBar.Create(InfoBarSeverity.Warning, "", message, isClosable: false, availableWidth: width);

    static Element Lead(string text, float width) => new TextEl(text)
    {
        Size = WinUiCrashRules.LeadSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = width,
    };

    /// <summary>The scrubbed report: a fixed-height box it scrolls inside.</summary>
    static Element PreviewBox(string? preview, float width, float height) => new BoxEl
    {
        Width = width, Height = height, Shrink = 0f,
        ClipToBounds = true, Fill = Tok.FillSolidBase,
        BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault, Corners = Radii.ControlAll,
        Children =
        [
            new ScrollEl
            {
                Height = height, ScrollKey = "crash-prompt-preview", EdgeCues = ScrollEdgeCues.None,
                Content = new BoxEl
                {
                    Padding = Edges4.All(WinUiCrashRules.PreviewPadding),
                    Children = [R.Type.MicroMeta(preview ?? Loc.Get(Strings.Report.Preparing))
                        with { FontFamily = "Cascadia Code", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap,
                               MaxWidth = width - WinUiCrashRules.PreviewPadding * 2f }],
                },
            },
        ],
    };

    static Element WhatIsSentBox(Signal<bool> showReport, string? preview, WinUiScreenDialogLayout layout, float width)
    {
        float inner = width - WinUiCrashRules.BoxInset;
        var kids = new List<Element>(8)
        {
            new TextEl(Loc.Get(Strings.Crash.PromptWhatIsSent))
                { Size = WinUiCrashRules.SmallTextSize, Weight = WinUiCrashRules.HeadingWeight, Color = Tok.TextSecondary },
            Bullet(Loc.Get(Strings.Crash.PromptBullet1), positive: true, width),
            Bullet(Loc.Get(Strings.Crash.PromptBullet2), positive: true, width),
            Bullet(Loc.Get(Strings.Crash.PromptBullet3), positive: true, width),
            Bullet(Loc.Get(Strings.Crash.PromptBullet4), positive: false, width),
        };
        // A two-pane body shows the report beside the prompt, always: there is nothing to open.
        if (!layout.TwoPane)
        {
            kids.Add(CommonTouch.Lift(HyperlinkButton.Create(Loc.Get(showReport.Value ? Strings.Crash.PromptHideFull : Strings.Crash.PromptReadFull),
                () => showReport.Value = !showReport.Peek()), layout.Target));
            if (showReport.Value) kids.Add(PreviewBox(preview, inner, layout.PreviewHeight));
        }
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Width = width, Shrink = 0f, Padding = Edges4.All(Spacing.M),
            Fill = Tok.FillSolidSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault, Corners = Radii.CardAll,
            Children = kids.ToArray(),
        };
    }

    static Element Bullet(string text, bool positive, float width) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start, Width = width - WinUiCrashRules.BoxInset,
        Children =
        [
            Icon(positive ? Icons.Check : Icons.Cancel, WinUiCrashRules.BulletGlyphSize, positive ? Tok.SystemFillSuccess : Tok.SystemFillCritical)
                with { Margin = new Edges4(WinUiCrashRules.BulletGlyphTop, 0f, 0f, 0f) },
            new TextEl(text)
                { Size = WinUiCrashRules.BulletSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = width - WinUiCrashRules.BulletTextInset },
        ],
    };

    static Element IncludeDumpRow(Signal<bool> includeDump, string label, WinUiScreenDialogLayout layout, float width) => new BoxEl
    {
        Direction = 1, Gap = WinUiCrashRules.DumpGap, Width = width,
        Children =
        [
            WinUiFeedback.Check(label, includeDump, layout.Target),
            new TextEl(Loc.Get(Strings.Crash.PromptIncludeDumpSub))
            {
                Size = WinUiCrashRules.SmallTextSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap,
                MaxWidth = width - WinUiCrashRules.DumpCaptionIndent, Margin = new Edges4(WinUiCrashRules.DumpCaptionIndent, 0f, 0f, 0f),
            },
        ],
    };
}
