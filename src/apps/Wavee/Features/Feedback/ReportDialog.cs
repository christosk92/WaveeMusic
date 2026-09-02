using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The in-app report dialog: bug / feature / question / idea, and the crash-reopen prompt (a fixed
/// <see cref="ReportKind.Crash"/> with an <see cref="InfoBar"/> and a "Don't ask again" opt-out). Hosted in the engine's
/// <c>ContentDialog</c> at its 548-DIP maximum (<see cref="DialogWidth"/>): the same card as every other Wavee dialog,
/// whose body scroller feathers its own alpha at the edges when the form overflows.
///
/// <para>Everything the reporter can send is composed OFF the UI thread by <see cref="ReportComposer"/> (file I/O +
/// the whole-text redaction pass), then only the small per-keystroke free-text answers are re-redacted here — the
/// diagnostics block, the crash head and the log excerpt are already redacted once and never touched again.</para>
///
/// <para>Nothing here rides the URL except the short per-field answers (<see cref="IssueFormUrl"/> truncates those
/// itself if the assembled URL is over budget); the full report goes to the clipboard and a saved
/// <c>wavee-report-&lt;stamp&gt;.txt</c> instead.</para></summary>
static class ReportDialog
{
    /// <summary>WinUI ContentDialog metrics (ContentDialog_themeresources.xaml): MaxWidth 548, Padding 24 — the form
    /// gets 500 DIP of content width inside the engine's <see cref="ContentDialog"/>, the same card every other
    /// Wavee dialog uses (title in the top overlay, separator, equal-width command buttons).</summary>
    public const float DialogWidth = 548f;
    public const float ContentWidth = DialogWidth - 48f;

    /// <summary>Open the dialog. <paramref name="crash"/> non-<see cref="CrashPromptMode.None"/> OR a
    /// <paramref name="prefill"/> naming a crash-report path both force the Crash kind, regardless of
    /// <paramref name="kind"/> — the "Report…" button on a listed crash file and the automatic relaunch prompt both
    /// go through this same fixed-kind path.</summary>
    public static void Open(IOverlayService overlay, Services? svc, InputHooks? hooks, IAppSettings? settings,
        ReportKind kind, ReportPrefill? prefill, CrashPromptDecision crash)
    {
        if (overlay is null) return;

        bool isCrash = crash.Mode != CrashPromptMode.None || prefill?.CrashReportPath is { Length: > 0 };
        ReportKind effectiveKind = isCrash ? ReportKind.Crash : kind;
        var session = new ReportDialogSession();

        ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Report.Title);
            d.DialogWidth = DialogWidth;
            d.Content = Embed.Comp(() => new ReportDialogBody
            {
                Svc = svc,
                Hooks = hooks,
                Settings = settings,
                InitialKind = effectiveKind,
                Prefill = prefill,
                Crash = crash,
                Session = session,
            });
            d.PrimaryText = isCrash ? Loc.Get(Strings.Report.ReportOnGithub) : Loc.Get(Strings.Report.OpenGithub);
            d.CloseText = isCrash ? Loc.Get(Strings.Report.NotNow) : Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            // The body owns the answers; the dialog's primary button asks it to submit and stays open when it can't
            // (report still composing, or no title yet) — the body says why with a toast.
            d.PrimaryButtonClick = args => { if (!(session.Submit?.Invoke() ?? false)) args.Cancel = true; };
        });
    }
}

/// <summary>The one handshake between the dialog chrome and the body: the body installs its submit while mounted.</summary>
sealed class ReportDialogSession
{
    public Func<bool>? Submit;
}

/// <summary>The dialog's Content: the subtitle, the kind switch (never in crash mode) and the form. The
/// <c>Segmented</c> selection signal is owned HERE because switching kind must REMOUNT the form (its whole answer set
/// is kind-shaped — component props freeze at mount), so the form is <c>Embed.Comp</c>'d with a Key that carries the
/// kind, exactly like <c>BrowsePageHost</c> remounts its page by route.</summary>
sealed class ReportDialogBody : Component
{
    public required Services? Svc;
    public required InputHooks? Hooks;
    public required IAppSettings? Settings;
    public required ReportKind InitialKind;
    public required ReportPrefill? Prefill;
    public required CrashPromptDecision Crash;
    public required ReportDialogSession Session;

    public override Element Render()
    {
        bool isCrash = Crash.Mode != CrashPromptMode.None || Prefill?.CrashReportPath is { Length: > 0 };
        var kindIndex = UseSignal(KindIndex(InitialKind));
        ReportKind kind = isCrash ? ReportKind.Crash : IndexToKind(kindIndex.Value);

        var children = new List<Element>(3)
        {
            new TextEl(Loc.Get(Strings.Report.Subtitle))
                { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = ReportDialog.ContentWidth },
        };

        if (!isCrash)
            children.Add(Segmented.Create(
                [
                    new SegmentedItem(Loc.Get(Strings.Report.KindBug)),
                    new SegmentedItem(Loc.Get(Strings.Report.KindFeature)),
                    new SegmentedItem(Loc.Get(Strings.Report.KindQuestion)),
                    new SegmentedItem(Loc.Get(Strings.Report.KindIdea)),
                ],
                kindIndex));

        children.Add(Embed.Comp(() => new ReportDialogCard
        {
            Svc = Svc,
            Hooks = Hooks,
            Settings = Settings,
            Kind = kind,
            Prefill = Prefill,
            Crash = Crash,
            Session = Session,
        }) with { Key = "report-card:" + (int)kind });

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Width = ReportDialog.ContentWidth,
            Children = children.ToArray(),
        };
    }

    static int KindIndex(ReportKind kind) => kind switch
    {
        ReportKind.Bug => 0,
        ReportKind.Feature => 1,
        ReportKind.Question => 2,
        ReportKind.Idea => 3,
        _ => 0,
    };

    static ReportKind IndexToKind(int index) => index switch
    {
        0 => ReportKind.Bug,
        1 => ReportKind.Feature,
        2 => ReportKind.Question,
        3 => ReportKind.Idea,
        _ => ReportKind.Bug,
    };
}

/// <summary>The form for ONE fixed <see cref="Kind"/> — remounted whole by <see cref="ReportDialogBody"/> whenever the
/// kind changes, so every signal here is safe to seed once from an open-time constant. Installs its submit into
/// <see cref="Session"/> while mounted; the dialog's primary button calls it.</summary>
sealed class ReportDialogCard : Component
{
    public required Services? Svc;
    public required InputHooks? Hooks;
    public required IAppSettings? Settings;
    public required ReportKind Kind;
    public required ReportPrefill? Prefill;
    public required CrashPromptDecision Crash;
    public required ReportDialogSession Session;

    const float FieldWidth = ReportDialog.ContentWidth;
    const float FieldHeight = 72f;               // multi-line TextBox height
    const float PreviewHeight = 220f;
    const float ComboWidth = 260f;

    public override Element Render()
    {
        bool isCrash = Kind == ReportKind.Crash;

        var title = UseSignal(Prefill?.Title ?? "");
        var f1 = UseSignal("");
        var f2 = UseSignal("");
        var f3 = UseSignal("");
        var when = UseSignal(0);
        var repro = UseSignal(0);
        var area = UseSignal(ReportChannels.Areas.Length - 1);   // "Not sure" by default
        var includeLogs = UseSignal(isCrash || Kind == ReportKind.Bug);
        var dontAsk = UseSignal(Settings?.Get(WaveeSettings.CrashPromptOptOut) ?? false);
        var composed = UseSignal<ComposedReport?>(null);
        var post = UsePost();

        // Off-thread compose: file I/O + the whole-text redaction pass over up to ~60 KB of diagnostics/log text.
        // Cancelled on unmount (a quick Cancel/Not now, or a kind switch that remounts this card, must not race a
        // stale write into a torn-down component).
        UseEffect(() =>
        {
            var cts = new CancellationTokenSource();
            var kind = Kind; var prefill = Prefill; var crash = Crash; var svc = Svc;
            Task.Run(() =>
            {
                ComposedReport result;
                try { result = ReportComposer.Compose(kind, prefill, crash, svc); }
                catch (Exception ex) { WaveeLog.Instance.Warn("report", "compose failed", ex); return; }
                if (!cts.IsCancellationRequested) post(() => composed.Value = result);
            });
            return () => cts.Cancel();
        }, DepKey.Empty);

        // The submit the dialog's primary button calls. Reads the signals at click time (Peek: no subscription), so
        // it is installed once per mount and never goes stale.
        var channel = ReportChannels.For(Kind);
        UseEffect(() =>
        {
            Session.Submit = () =>
            {
                var c = composed.Peek();
                if (c is null)
                {
                    Toast.Show(Loc.Get(Strings.Report.Preparing), new ToastOptions { Severity = InfoBarSeverity.Informational });
                    return false;
                }
                if (!isCrash && title.Peek().Trim().Length == 0)
                {
                    Toast.Show(Loc.Get(Strings.Report.TitleRequired), new ToastOptions { Severity = InfoBarSeverity.Warning });
                    return false;
                }
                OpenGithub(c, title.Peek(), f1.Peek(), f2.Peek(), f3.Peek(), when.Peek(), repro.Peek(), area.Peek(), includeLogs.Peek(), channel);
                return true;
            };
            return () => Session.Submit = null;
        }, DepKey.Empty);

        ComposedReport? c = composed.Value;

        var body = new List<Element>(10);

        if (isCrash)
            body.Add(new BoxEl { Direction = 1, Shrink = 0f, Width = FieldWidth, Children = [ CrashInfoBar(c) ] });

        body.Add(TextBox.Create(title, null, new TextBox.TextBoxOptions
        {
            Header = Loc.Get(Strings.Report.TitleLabel),
            Placeholder = Loc.Get(Strings.Report.TitlePlaceholder),
            Width = FieldWidth,
        }));

        AddKindFields(body, f1, f2, f3, when, repro, area);

        int logLineCount = isCrash ? ReportBundle.CrashLogLines : ReportBundle.ManualLogLines;
        body.Add(CheckBox.Create(Strings.Report.IncludeLogs(logLineCount), includeLogs));

        string preview = PreviewText(c, title.Value, f1.Value, f2.Value, f3.Value, when.Value, repro.Value, area.Value, includeLogs.Value);
        body.Add(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Width = FieldWidth, Shrink = 0f,
            Children =
            [
                new TextEl(Loc.Get(Strings.Report.Preview)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
                Spacer(),
                Button.Subtle(Loc.Get(Strings.Report.Copy),
                    () => CopyReport(c, title.Value, f1.Value, f2.Value, f3.Value, when.Value, repro.Value, area.Value, includeLogs.Value),
                    isEnabled: c is not null),
                Button.Subtle(Loc.Get(Strings.Report.SaveAs),
                    () => SaveReport(c, title.Value, f1.Value, f2.Value, f3.Value, when.Value, repro.Value, area.Value, includeLogs.Value),
                    isEnabled: c is not null),
            ],
        });
        body.Add(new BoxEl
        {
            Width = FieldWidth, Height = PreviewHeight, Shrink = 0f, ClipToBounds = true, Fill = Tok.FillSolidBase,
            BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault, Corners = Radii.ControlAll,
            Children =
            [
                new ScrollEl
                {
                    Height = PreviewHeight, ScrollKey = "report-preview", EdgeCues = ScrollEdgeCues.None,
                    Content = new BoxEl
                    {
                        Padding = Edges4.All(10f),
                        Children = [ new TextEl(preview) { Size = 11f, FontFamily = "Cascadia Code", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = FieldWidth - 20f } ],
                    },
                },
            ],
        });
        body.Add(new TextEl(Loc.Get(Strings.Report.PreviewNote))
            { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = FieldWidth });

        if (isCrash)
            body.Add(CheckBox.Create(Loc.Get(Strings.Report.DontAskAgain), dontAsk,
                v => Settings?.Set(WaveeSettings.CrashPromptOptOut, v)));

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Width = FieldWidth,
            Children = body.ToArray(),
        };
    }

    // ── kind → fields ────────────────────────────────────────────────────────────────────────────────────────────

    void AddKindFields(List<Element> body, Signal<string> f1, Signal<string> f2, Signal<string> f3,
        Signal<int> when, Signal<int> repro, Signal<int> area)
    {
        switch (Kind)
        {
            case ReportKind.Crash:
                body.Add(ComboBox.Create(ReportChannels.When, when, header: Loc.Get(Strings.Report.When), width: ComboWidth));
                body.Add(ComboBox.Create(ReportChannels.Reproduces, repro, header: Loc.Get(Strings.Report.Reproduces), width: ComboWidth));
                body.Add(TextBox.Create(f1, null, new TextBox.TextBoxOptions
                    { Header = Loc.Get(Strings.Report.WhatWereYouDoing), Width = FieldWidth, AcceptsReturn = true, Height = FieldHeight }));
                break;

            case ReportKind.Bug:
                body.Add(TextBox.Create(f1, null, new TextBox.TextBoxOptions
                    { Header = Loc.Get(Strings.Report.WhatHappened), Width = FieldWidth, AcceptsReturn = true, Height = FieldHeight }));
                body.Add(TextBox.Create(f2, null, new TextBox.TextBoxOptions
                    { Header = Loc.Get(Strings.Report.Steps), Width = FieldWidth, AcceptsReturn = true, Height = FieldHeight }));
                body.Add(TextBox.Create(f3, null, new TextBox.TextBoxOptions
                    { Header = Loc.Get(Strings.Report.Expected), Width = FieldWidth, AcceptsReturn = true, Height = FieldHeight }));
                body.Add(ComboBox.Create(ReportChannels.Areas, area, header: Loc.Get(Strings.Report.Area), width: ComboWidth));
                break;

            case ReportKind.Feature:
                body.Add(TextBox.Create(f1, null, new TextBox.TextBoxOptions
                    { Header = Loc.Get(Strings.Report.Problem), Width = FieldWidth, AcceptsReturn = true, Height = FieldHeight }));
                body.Add(TextBox.Create(f2, null, new TextBox.TextBoxOptions
                    { Header = Loc.Get(Strings.Report.Proposal), Width = FieldWidth, AcceptsReturn = true, Height = FieldHeight }));
                body.Add(ComboBox.Create(ReportChannels.Areas, area, header: Loc.Get(Strings.Report.Area), width: ComboWidth));
                body.Add(TextBox.Create(f3, null, new TextBox.TextBoxOptions
                    { Header = Loc.Get(Strings.Report.Alternatives), Width = FieldWidth, AcceptsReturn = true, Height = FieldHeight }));
                break;

            default:   // Question, Idea
                body.Add(TextBox.Create(f1, null, new TextBox.TextBoxOptions
                    { Header = Loc.Get(Strings.Report.Body), Width = FieldWidth, AcceptsReturn = true, Height = 140f }));
                break;
        }
    }

    Element CrashInfoBar(ComposedReport? c)
    {
        // Three honest states: still composing (the report is read off the UI thread — "no report was written" would
        // be a lie for those first frames), a managed report with its summary, or genuinely no report.
        string message;
        var severity = InfoBarSeverity.Error;
        if (c is null) { message = Loc.Get(Strings.Report.CrashReading); severity = InfoBarSeverity.Informational; }
        else if (c is { CrashSummary.Length: > 0 })
        {
            string when = CrashTime();
            message = when.Length > 0 ? Strings.Report.CrashAt(when) + " · " + c.CrashSummary : c.CrashSummary;
        }
        else message = Loc.Get(Strings.Report.CrashNoReport);

        return InfoBar.Create(severity, Loc.Get(Strings.Report.CrashTitle), message,
            isClosable: false, availableWidth: FieldWidth);
    }

    string CrashTime()
    {
        string? path = Prefill?.CrashReportPath ?? Crash.ReportPath;
        if (path is not { Length: > 0 } || !File.Exists(path)) return "";
        try { return File.GetLastWriteTime(path).ToString("t"); }
        catch { return ""; }
    }

    // ── answers / preview / submit ──────────────────────────────────────────────────────────────────────────────

    static int Clamp(int i, int len) => i < 0 || i >= len ? 0 : i;

    List<(string Label, string Text)> BuildAnswers(RedactionRules rules, string title, string f1, string f2, string f3,
        int when, int repro, int area)
    {
        var list = new List<(string, string)>(6);
        string t = title.Trim();
        if (t.Length > 0) list.Add((Loc.Get(Strings.Report.TitleLabel), t));

        switch (Kind)
        {
            case ReportKind.Crash:
                list.Add((Loc.Get(Strings.Report.When), ReportChannels.When[Clamp(when, ReportChannels.When.Length)]));
                list.Add((Loc.Get(Strings.Report.Reproduces), ReportChannels.Reproduces[Clamp(repro, ReportChannels.Reproduces.Length)]));
                list.Add((Loc.Get(Strings.Report.WhatWereYouDoing), ReportRedactor.Redact(f1, rules)));
                break;
            case ReportKind.Bug:
                list.Add((Loc.Get(Strings.Report.WhatHappened), ReportRedactor.Redact(f1, rules)));
                list.Add((Loc.Get(Strings.Report.Steps), ReportRedactor.Redact(f2, rules)));
                list.Add((Loc.Get(Strings.Report.Expected), ReportRedactor.Redact(f3, rules)));
                list.Add((Loc.Get(Strings.Report.Area), ReportChannels.Areas[Clamp(area, ReportChannels.Areas.Length)]));
                break;
            case ReportKind.Feature:
                list.Add((Loc.Get(Strings.Report.Problem), ReportRedactor.Redact(f1, rules)));
                list.Add((Loc.Get(Strings.Report.Proposal), ReportRedactor.Redact(f2, rules)));
                list.Add((Loc.Get(Strings.Report.Area), ReportChannels.Areas[Clamp(area, ReportChannels.Areas.Length)]));
                list.Add((Loc.Get(Strings.Report.Alternatives), ReportRedactor.Redact(f3, rules)));
                break;
            default:
                list.Add((Loc.Get(Strings.Report.Body), ReportRedactor.Redact(f1, rules)));
                break;
        }
        return list;
    }

    string PreviewText(ComposedReport? c, string title, string f1, string f2, string f3, int when, int repro, int area, bool includeLogs)
    {
        if (c is null) return Loc.Get(Strings.Report.Preparing);
        var answers = BuildAnswers(c.Rules, title, f1, f2, f3, when, repro, area);
        string bundle = ReportBundle.Build(Kind, c.Identity, answers, c.Diagnostics, c.CrashHead, c.LogLines, c.LogSource, includeLogs, DateTimeOffset.Now);
        return ReportBundle.Preview(bundle);
    }

    string BuildBundle(ComposedReport c, string title, string f1, string f2, string f3, int when, int repro, int area,
        bool includeLogs, DateTimeOffset now)
    {
        var answers = BuildAnswers(c.Rules, title, f1, f2, f3, when, repro, area);
        return ReportBundle.Build(Kind, c.Identity, answers, c.Diagnostics, c.CrashHead, c.LogLines, c.LogSource, includeLogs, now);
    }

    List<KeyValuePair<string, string>> BuildUrlFields(ComposedReport c, string f1, string f2, string f3, int when, int repro, int area)
    {
        var fields = new List<KeyValuePair<string, string>>(4);
        switch (Kind)
        {
            // GitHub's issue UI prefills inputs and textareas from the URL but NEVER dropdowns (verified 2026-09-02),
            // so every dropdown answer is ALSO written as the first line of the neighbouring text field. The dropdown
            // params stay in the URL: harmless today, and they light up if GitHub ever adds support.
            case ReportKind.Crash:
            {
                string whenText = ReportChannels.When[Clamp(when, ReportChannels.When.Length)];
                string reproText = ReportChannels.Reproduces[Clamp(repro, ReportChannels.Reproduces.Length)];
                fields.Add(new("when", whenText));
                fields.Add(new("reproduces", reproText));
                fields.Add(new("what-were-you-doing",
                    Loc.Get(Strings.Report.When) + ": " + whenText + " \u00b7 " + Loc.Get(Strings.Report.Reproduces) + ": " + reproText
                    + "\n\n" + ReportRedactor.Redact(f1, c.Rules)));
                break;
            }
            case ReportKind.Bug:
                fields.Add(new("what-happened", AreaLine(area) + ReportRedactor.Redact(f1, c.Rules)));
                fields.Add(new("steps-to-reproduce", ReportRedactor.Redact(f2, c.Rules)));
                fields.Add(new("expected-behaviour", ReportRedactor.Redact(f3, c.Rules)));
                break;
            case ReportKind.Feature:
                fields.Add(new("problem", AreaLine(area) + ReportRedactor.Redact(f1, c.Rules)));
                fields.Add(new("proposal", ReportRedactor.Redact(f2, c.Rules)));
                fields.Add(new("area", ReportChannels.Areas[Clamp(area, ReportChannels.Areas.Length)]));
                fields.Add(new("alternatives", ReportRedactor.Redact(f3, c.Rules)));
                break;
            default:
                string body = ReportRedactor.Redact(f1, c.Rules);
                body += "\n\n" + c.Identity.VersionLine + " · " + c.Identity.Architecture + " · " + c.Identity.WindowsVersion;
                fields.Add(new("body", body));
                break;
        }
        return fields;
    }

    /// <summary>"Area: playback" + a blank line when an area was picked; empty for "Not sure".</summary>
    static string AreaLine(int area)
    {
        string slug = ReportChannels.Areas[Clamp(area, ReportChannels.Areas.Length)];
        return slug.Length == 0 || slug == "Not sure" ? "" : Loc.Get(Strings.Report.Area) + ": " + slug + "\n\n";
    }

    List<string> BuildLabels(ComposedReport c, int area)
    {
        var labels = new List<string>(3);
        if (Kind is ReportKind.Question or ReportKind.Idea) return labels;
        if (c.Identity.ArchLabel.Length > 0) labels.Add(c.Identity.ArchLabel);
        if (c.Identity.InstallLabel.Length > 0) labels.Add(c.Identity.InstallLabel);
        if (Kind == ReportKind.Bug)
        {
            string areaSlug = ReportChannels.Areas[Clamp(area, ReportChannels.Areas.Length)];
            if (areaSlug.Length > 0 && areaSlug != "Not sure") labels.Add("area: " + areaSlug);
        }
        return labels;
    }

    void CopyReport(ComposedReport? c, string title, string f1, string f2, string f3, int when, int repro, int area, bool includeLogs)
    {
        if (c is null) return;
        string bundle = BuildBundle(c, title, f1, f2, f3, when, repro, area, includeLogs, DateTimeOffset.Now);
        Hooks?.Clipboard?.SetText(bundle);
        Toast.Show(Loc.Get(Strings.Report.Copied), new ToastOptions { Severity = InfoBarSeverity.Success });
    }

    void SaveReport(ComposedReport? c, string title, string f1, string f2, string f3, int when, int repro, int area, bool includeLogs)
    {
        if (c is null) return;
        var now = DateTimeOffset.Now;
        string suggested = ReportBundle.FileName(now);
        string? path = FilePicker.SaveFile(FluentApp.WindowHandle, Loc.Get(Strings.Report.SaveAs), suggested,
            ("Text files", "*.txt"), ("All files", "*.*"));
        if (path is null) return;

        string bundle = BuildBundle(c, title, f1, f2, f3, when, repro, area, includeLogs, now);
        try
        {
            File.WriteAllText(path, bundle);
            Toast.Show(Strings.Report.Saved(path), new ToastOptions { Severity = InfoBarSeverity.Success });
        }
        catch (Exception ex)
        {
            Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error });
        }
    }

    void OpenGithub(ComposedReport? c, string title, string f1, string f2, string f3, int when, int repro, int area,
        bool includeLogs, ReportChannel channel)
    {
        if (c is null) return;
        var now = DateTimeOffset.Now;
        string bundle = BuildBundle(c, title, f1, f2, f3, when, repro, area, includeLogs, now);

        Hooks?.Clipboard?.SetText(bundle);
        try
        {
            Directory.CreateDirectory(CrashReport.DefaultDirectory);
            File.WriteAllText(Path.Combine(CrashReport.DefaultDirectory, ReportBundle.FileName(now)), bundle);
        }
        catch (Exception ex) { WaveeLog.Instance.Warn("report", "save bundle failed", ex); }

        string urlTitle = title.Trim();
        if (urlTitle.Length == 0)
            urlTitle = c.CrashSummary.Length > 0 ? c.CrashSummary : Loc.Get(Strings.Report.CrashTitle);

        var fields = BuildUrlFields(c, f1, f2, f3, when, repro, area);
        var labels = BuildLabels(c, area);
        string url = IssueFormUrl.Build(Kind, c.Identity, urlTitle, fields, labels);
        ShellOpen.OpenUrl(url);

        Toast.Show(Strings.Report.CopiedPaste(channel.PasteBox), new ToastOptions { Severity = InfoBarSeverity.Success, DurationMs = 8000f });
    }
}
