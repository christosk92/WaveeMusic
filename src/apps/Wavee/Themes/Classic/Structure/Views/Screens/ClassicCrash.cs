// ── Themes/Classic/Structure/Views/Screens/ClassicCrash.cs — Classic's `Dialog.Crash` (themes plan 6.1, T11c) ──────────────────────
//
// From `Crash.UI.cs`: the crash / hang consent card's BODY (the stock ContentDialog chrome, the launch decision, the consent flow and the outcome toasts
// stay core). Width 500; every signal is seeded once from the props at mount (the props freeze — the dialog is keyed by report id, and a re-render
// must not re-seed the user's in-progress choices). The header bar, the lead (not from the Saved reports list), the "what is sent" box with its
// off-thread scrubbed preview, the include-dump box (when the bundle has a dump), the "always send" box (not from the list) and the privacy and
// "Report on GitHub instead" links.
//
// The preview is composed off the UI thread (the scrub touches file I/O and the redaction pass): `props.PrepareCompose` runs on the UI thread and returns
// the pool-thread function; the result is posted back and cancelled on unmount. Send is VETOABLE: the card installs its submit, which says why with a
// toast and keeps the dialog open while the preview has not finished composing.

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

/// <summary>Wavee Classic's crash / hang consent card.</summary>
internal static class ClassicCrash
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogCrash"/>.</summary>
    public static Element Template(CrashDialogProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
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

        var children = new List<Element>(10) { HeaderInfoBar(props.HeaderMessage) };
        if (!props.FromList) children.Add(Lead(props.Lead));
        children.Add(WhatIsSentBox(showReport, preview.Value));
        if (props.HasDump) children.Add(IncludeDumpRow(includeDump, props.IncludeDumpLabel));
        if (!props.FromList) children.Add(CheckBox.Create(Loc.Get(Strings.Crash.PromptAlways), always));
        children.Add(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Width = ClassicCrashRules.BodyWidth,
            Children =
            [
                HyperlinkButton.Create(Loc.Get(Strings.Crash.PromptPrivacy), props.PrivacyUrl),
                new TextEl("·") { Color = Tok.TextTertiary },
                HyperlinkButton.Create(Loc.Get(Strings.Crash.PromptGithub), props.ReportOnGithub),
            ],
        });
        if (!props.Configured)
            children.Add(new TextEl(Loc.Get(Strings.Crash.PromptNotConfigured))
                { Size = ClassicCrashRules.SmallTextSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = ClassicCrashRules.BodyWidth });

        return new BoxEl { Direction = 1, Gap = Spacing.M, Width = ClassicCrashRules.BodyWidth, Children = children.ToArray() };
    }

    static Element HeaderInfoBar(string message)
        => InfoBar.Create(InfoBarSeverity.Warning, "", message, isClosable: false, availableWidth: ClassicCrashRules.BodyWidth);

    static Element Lead(string text) => new TextEl(text)
    {
        Size = ClassicCrashRules.LeadSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = ClassicCrashRules.BodyWidth,
    };

    static Element WhatIsSentBox(Signal<bool> showReport, string? preview)
    {
        var kids = new List<Element>(8)
        {
            new TextEl(Loc.Get(Strings.Crash.PromptWhatIsSent))
                { Size = ClassicCrashRules.SmallTextSize, Weight = ClassicCrashRules.HeadingWeight, Color = Tok.TextSecondary },
            Bullet(Loc.Get(Strings.Crash.PromptBullet1), positive: true),
            Bullet(Loc.Get(Strings.Crash.PromptBullet2), positive: true),
            Bullet(Loc.Get(Strings.Crash.PromptBullet3), positive: true),
            Bullet(Loc.Get(Strings.Crash.PromptBullet4), positive: false),
            HyperlinkButton.Create(Loc.Get(showReport.Value ? Strings.Crash.PromptHideFull : Strings.Crash.PromptReadFull),
                () => showReport.Value = !showReport.Peek()),
        };
        if (showReport.Value)
            kids.Add(new BoxEl
            {
                Width = ClassicCrashRules.BodyWidth - ClassicCrashRules.BoxInset, Height = ClassicCrashRules.PreviewHeight, Shrink = 0f,
                ClipToBounds = true, Fill = Tok.FillSolidBase,
                BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault, Corners = Radii.ControlAll,
                Children =
                [
                    new ScrollEl
                    {
                        Height = ClassicCrashRules.PreviewHeight, ScrollKey = "crash-prompt-preview", EdgeCues = ScrollEdgeCues.None,
                        Content = new BoxEl
                        {
                            Padding = Edges4.All(ClassicCrashRules.PreviewPadding),
                            Children = [R.Type.MicroMeta(preview ?? Loc.Get(Strings.Report.Preparing))
                                with { FontFamily = "Cascadia Code", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap,
                                       MaxWidth = ClassicCrashRules.BodyWidth - ClassicCrashRules.PreviewTextInset }],
                        },
                    },
                ],
            });
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Width = ClassicCrashRules.BodyWidth, Shrink = 0f, Padding = Edges4.All(Spacing.M),
            Fill = Tok.FillSolidSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault, Corners = Radii.CardAll,
            Children = kids.ToArray(),
        };
    }

    static Element Bullet(string text, bool positive) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start, Width = ClassicCrashRules.BodyWidth - ClassicCrashRules.BoxInset,
        Children =
        [
            Icon(positive ? Icons.Check : Icons.Cancel, ClassicCrashRules.BulletGlyphSize, positive ? Tok.SystemFillSuccess : Tok.SystemFillCritical)
                with { Margin = new Edges4(ClassicCrashRules.BulletGlyphTop, 0f, 0f, 0f) },
            new TextEl(text)
                { Size = ClassicCrashRules.BulletSize, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = ClassicCrashRules.BodyWidth - ClassicCrashRules.BulletTextInset },
        ],
    };

    static Element IncludeDumpRow(Signal<bool> includeDump, string label) => new BoxEl
    {
        Direction = 1, Gap = ClassicCrashRules.DumpGap, Width = ClassicCrashRules.BodyWidth,
        Children =
        [
            CheckBox.Create(label, includeDump),
            new TextEl(Loc.Get(Strings.Crash.PromptIncludeDumpSub))
            {
                Size = ClassicCrashRules.SmallTextSize, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap,
                MaxWidth = ClassicCrashRules.BodyWidth - ClassicCrashRules.DumpCaptionIndent, Margin = new Edges4(ClassicCrashRules.DumpCaptionIndent, 0f, 0f, 0f),
            },
        ],
    };
}
