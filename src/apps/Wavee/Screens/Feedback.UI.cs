// ── Screens/Feedback.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the report dialog (Bug / Feature / Question / Idea / Crash), ReportComposer (off-thread compose + redaction),
// Requests (the monotonic request signal) + Open, the zero-size report chrome (Feedback.Chrome: requests, the
// post-crash prompt in both shapes, the --crash-probe timer), the crash-reports card (Feedback.CrashReportsCard)
//
// Role: UI
// Owner: R
// Wave: 6
// Budget: 800 lines
// Spec: ch 28 §9.5 (W24, W25, §1.4, §3.4, §6.4, §9.1 #10); ch 27 W19 "Crash reports card"
//
// MOUNT ORDER (ch 28 §9.3.8, load-bearing): the overlay layer mounts Setup.WizardChrome() FIRST, Feedback.Chrome()
// SECOND, ReleaseNotes.AfterUpdateChrome() THIRD — the crash prompt sets ReleaseNotes.CrashNoticeThisLaunch before the
// after-update plate's effect reads it, so a launch that surfaced a crash defers the plate to the NEXT launch.
//
// THE SEAMS THIS FILE LEAVES (every one null-safe; the honest absent state is named on each):
//   · DiagnosticsText  — owner S: the "Copy diagnostics info" text (0.2.9 SettingsPage.DiagInfoText).
//   · PastSessionLog   — owner S: a past session's raw log lines (WaveeLogSessions). Called OFF the UI thread.
//   · ListCrashReports — owner S: CrashReportFiles.List over Platform.LogFolder.
//   · CrashProbeMode   — owner S: `--crash-probe` → "throw" | "failfast" (null = no probe).
// Read directly, no seam: CrashPromptPolicy.ThisLaunch (owner S latches it before the first frame; THIS chrome consumes
// it), the log ring (Log.Snapshot), Platform.Version, and the account/device names (Platform.Scope, User.Me,
// Playback.Devices).

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using FluentGpu.WindowsApi.Packaging;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Feedback
{
    // ══ 1. INSTALL, MOUNT POINTS, THE ONE OPEN DOOR ════════════════════════════════════════════════════════════════

    /// <summary>Owner R's contract call: fills <see cref="Shell.ReportDialogOpener"/> — the
    /// <c>wavee://open?route=report&amp;arg=bug|feature|crash|question|idea</c> deep link (an unknown arg is a Bug).</summary>
    public static void InstallUi()
        => Shell.ReportDialogOpener = static (overlay, arg) =>
            OpenDialog(overlay, ReportChannels.TryParseKind(arg, out var kind) ? kind : ReportKind.Bug, null, default);

    /// <summary>Zero-size chrome, mounted in the overlay layer SECOND (see the header).</summary>
    // MOUNT POINT (owner R contract)
    public static Element Chrome() => Embed.Comp(static () => new ChromeCore());

    /// <summary>Settings › About's collapsed "Crash reports" expander.</summary>
    // MOUNT POINT (owner R contract)
    public static Element CrashReportsCard() => Embed.Comp(static () => new CrashReportsCardCore());

    /// <summary>Open the report dialog from anywhere — About's "Report a problem…" (Bug) / "Suggest a feature…"
    /// (Feature), the Logs panel's "Report this session…" (Bug + <see cref="ReportPrefill.PastSessionId"/>), the crash
    /// card's "Report…" (Crash + <see cref="ReportPrefill.CrashReportPath"/>). UI thread.</summary>
    public static void Open(ReportKind kind, ReportPrefill? prefill = null) => Requests.Open(kind, prefill);

    // ══ 2. THE SEAMS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The diagnostics block's raw text, read on the UI thread when the dialog mounts, redacted off it. Null ⇒
    /// the bundle carries no diagnostics section.</summary>
    public static Func<string>? DiagnosticsText;

    /// <summary>A past session's raw log lines: the arg is a <see cref="ReportPrefill.PastSessionId"/>, or null for "the
    /// newest past session" (a WER-dump / unclean-exit crash prompt has no report file to quote). Runs OFF the UI thread.
    /// Null seam or null answer ⇒ this session's own ring.</summary>
    public static Func<string?, IReadOnlyList<string>?>? PastSessionLog;

    /// <summary>The newest crash-report files (at most the arg), newest first, never throwing. Null ⇒ the empty row.</summary>
    public static Func<int, IReadOnlyList<(string Path, DateTime Stamp)>>? ListCrashReports;

    /// <summary><c>--crash-probe</c>: "throw" (a managed exception on the UI thread) or "failfast", two seconds after the
    /// chrome mounts. Null ⇒ no probe.</summary>
    public static Func<string?>? CrashProbeMode;

    // ══ 3. REQUESTS (the monotonic request signal) ═════════════════════════════════════════════════════════════════

    /// <summary>"Open the report dialog" as a signal the chrome observes, so no entry point holds the overlay service.
    /// The payload rides IN the signal with its own monotonic <see cref="Request.Seq"/>: two opens in one flush cannot
    /// collapse, and no stale prefill can outlive its request.</summary>
    public static class Requests
    {
        public sealed record Request(int Seq, ReportKind Kind, ReportPrefill? Prefill, CrashPromptDecision Crash);

        static int s_seq;

        /// <summary>The most recent request, or null before the first.</summary>
        public static readonly Signal<Request?> Requested = new(null);

        public static void Open(ReportKind kind, ReportPrefill? prefill = null, CrashPromptDecision crash = default)
            => Requested.Value = new Request(++s_seq, kind, prefill, crash);
    }

    // ══ 4. THE CHROME ══════════════════════════════════════════════════════════════════════════════════════════════

    const string CrashToastKey = "crash.pendingReport";

    sealed class ChromeCore : Component
    {
        // A STATIC, not a UseRef(-1) baseline (ch 28 §9.1 #10): a UseRef treats "whatever Seq is current when I first
        // mount" as already served and swallows the first request after an OverlayHost subtree remount.
        static int s_lastOpenedSeq = -1;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var post = UsePost();

            var req = Requests.Requested.Value;
            UseEffect(() =>
            {
                if (req is null || req.Seq == s_lastOpenedSeq) return;
                s_lastOpenedSeq = req.Seq;
                OpenDialog(overlay, req.Kind, req.Prefill, req.Crash);
            }, req?.Seq ?? -1);

            // The post-crash prompt: CrashPromptPolicy.ThisLaunch is consumed (reset to default) the moment either arm
            // fires — one shot per launch — but only once the wizard deferral has passed, so a deferred prompt is still
            // armed when the marker epoch bumps.
            int wizardEpoch = Setup.WizardMarkerEpoch.Value;   // re-evaluate the deferral on every wizard marker bump
            UseEffect(() =>
            {
                var d = CrashPromptPolicy.ThisLaunch;
                if (d.Mode == CrashPromptMode.None) return;
                if (Setup.Gating.IsPending(Platform.Settings) || Setup.WizardOpen.Peek()) return;   // deferred — still armed
                CrashPromptPolicy.ThisLaunch = default;     // one-shot: consumed now, whichever arm fires below
                ReleaseNotes.CrashNoticeThisLaunch = true;  // the after-update plate waits for the next launch

                if (d.Mode == CrashPromptMode.Toast)
                {
                    // The opt-out arm: STICKY (0 ms — it waits for the user), deduped so a second evaluation cannot stack.
                    // Its action carries the whole decision, so a WER-dump toast still opens the crash form on that dump.
                    Notify.Say(Loc.Get(Strings.Common.CrashLastRun), InfoBarSeverity.Warning,
                        Loc.Get(Strings.Report.ReportOnGithub),
                        () => Requests.Open(ReportKind.Crash, new ReportPrefill(CrashReportPath: d.ReportPath),
                            d with { Mode = CrashPromptMode.Dialog }),
                        CrashToastKey, durationMs: 0f);
                    return;
                }
                OpenDialog(overlay, ReportKind.Crash, null, d);
            }, wizardEpoch);

            UseEffect(() =>
            {
                if (CrashProbeMode?.Invoke() is not { Length: > 0 } mode) return;
                _ = Task.Delay(2000).ContinueWith(_ => post(() =>
                {
                    if (mode == "failfast") Environment.FailFast("--crash-probe");
                    throw new InvalidOperationException("--crash-probe");
                }), TaskScheduler.Default);
            }, DepKey.Empty);

            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Shrink = 0f };
        }
    }

    // ══ 5. THE DIALOG ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>WinUI ContentDialog metrics: MaxWidth 548, Padding 24 — the form gets 500 DIP inside the stock card.</summary>
    const float DialogWidth = 548f, FieldWidth = DialogWidth - 48f;
    const float FieldHeight = 72f, BodyFieldHeight = 140f, PreviewHeight = 220f, ComboWidth = 260f;

    /// <summary>New loc keys (batch-loc/WP-6.R-feedback.json): the save picker's filter names, English literals in 0.2.9.</summary>
    const string LocFilterText = "report.saveFilterText", LocFilterAll = "report.saveFilterAll";

    /// <summary>A crash kind / crash prompt / crash-report prefill forces the fixed Crash form (<see cref="ReportForm.EffectiveKind"/>).
    /// The dialog is the stock <c>ContentDialog</c> at its 548 maximum.</summary>
    static void OpenDialog(IOverlayService? overlay, ReportKind kind, ReportPrefill? prefill, CrashPromptDecision crash)
    {
        if (overlay is null) return;
        ReportKind effective = ReportForm.EffectiveKind(kind, prefill?.CrashReportPath, crash.Mode != CrashPromptMode.None);
        bool isCrash = effective == ReportKind.Crash;

        // Names the kind that ACTUALLY arrived at the one place that decides what the dialog shows.
        Log.Info("report", $"report.open kind={kind} effective={effective} crash={crash.Mode} prefill={prefill is not null}");

        var session = new DialogSession();
        ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Report.Title);
            d.DialogWidth = DialogWidth;
            // Keyed by the kind shown: every signal in the body is seeded once from an open-time constant.
            d.Content = Embed.Comp(() => new DialogBody { InitialKind = effective, Prefill = prefill, Crash = crash, Session = session })
                with { Key = "report-body:" + (int)effective };
            d.PrimaryText = Loc.Get(isCrash ? Strings.Report.ReportOnGithub : Strings.Report.OpenGithub);
            d.CloseText = Loc.Get(isCrash ? Strings.Report.NotNow : Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            // VETOABLE: the body says why with a toast (still composing / no title) and the dialog stays open.
            d.PrimaryButtonClick = args => { if (!(session.Submit?.Invoke() ?? false)) args.Cancel = true; };
        });
    }

    /// <summary>The one handshake between the dialog chrome and the body: the card installs its submit while mounted.</summary>
    sealed class DialogSession
    {
        public Func<bool>? Submit;
    }

    static ReportLabels FormLabels() => new(
        Loc.Get(Strings.Report.TitleLabel), Loc.Get(Strings.Report.When), Loc.Get(Strings.Report.Reproduces),
        Loc.Get(Strings.Report.WhatWereYouDoing), Loc.Get(Strings.Report.WhatHappened), Loc.Get(Strings.Report.Steps),
        Loc.Get(Strings.Report.Expected), Loc.Get(Strings.Report.Area), Loc.Get(Strings.Report.Problem),
        Loc.Get(Strings.Report.Proposal), Loc.Get(Strings.Report.Alternatives), Loc.Get(Strings.Report.Body));

    /// <summary>The subtitle, the kind switch (never in crash mode) and the form. The Segmented selection lives HERE
    /// because switching kind must REMOUNT the whole form (its answer set is kind-shaped).</summary>
    sealed class DialogBody : Component
    {
        public required ReportKind InitialKind;
        public required ReportPrefill? Prefill;
        public required CrashPromptDecision Crash;
        public required DialogSession Session;

        public override Element Render()
        {
            bool isCrash = InitialKind == ReportKind.Crash;
            var kindIndex = UseSignal(ReportKindIndex.IndexOf(InitialKind));
            ReportKind kind = isCrash ? ReportKind.Crash : ReportKindIndex.KindAt(kindIndex.Value);

            var children = new List<Element>(3)
            {
                new TextEl(Loc.Get(Strings.Report.Subtitle)) { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = FieldWidth },
            };
            if (!isCrash)
                children.Add(Segmented.Create(
                [
                    new SegmentedItem(Loc.Get(Strings.Report.KindBug)),
                    new SegmentedItem(Loc.Get(Strings.Report.KindFeature)),
                    new SegmentedItem(Loc.Get(Strings.Report.KindQuestion)),
                    new SegmentedItem(Loc.Get(Strings.Report.KindIdea)),
                ], kindIndex));
            children.Add(Embed.Comp(() => new DialogCard { Kind = kind, Prefill = Prefill, Crash = Crash, Session = Session })
                with { Key = "report-card:" + (int)kind });

            return new BoxEl { Direction = 1, Gap = Spacing.M, Width = FieldWidth, Children = children.ToArray() };
        }
    }

    /// <summary>The form for ONE fixed kind — remounted whole on a kind switch, so every signal is seeded once. The
    /// preview recomputes per keystroke (subscribing reads); submit reads the same signals with Peek so it never goes
    /// stale.</summary>
    sealed class DialogCard : Component
    {
        public required ReportKind Kind;
        public required ReportPrefill? Prefill;
        public required CrashPromptDecision Crash;
        public required DialogSession Session;

        public override Element Render()
        {
            bool isCrash = Kind == ReportKind.Crash;
            var title = UseSignal(Prefill?.Title ?? "");
            var f1 = UseSignal("");
            var f2 = UseSignal("");
            var f3 = UseSignal("");
            var when = UseSignal(0);                                     // "On launch"
            var repro = UseSignal(0);                                    // "Every time"
            var area = UseSignal(ReportChannels.Areas.Length - 1);       // "Not sure"
            var includeLogs = UseSignal(ReportForm.IncludesLogsByDefault(Kind));
            var dontAsk = UseSignal(Platform.Settings.Get(Platform.Keys.CrashPromptOptOut));   // a prior opt-out shows ticked
            var composed = UseSignal<ComposedReport?>(null);
            var post = UsePost();
            var hooks = UseContext(InputHooks.Current);

            // Off-thread compose: file I/O + the whole-text redaction pass over up to ~60 KB. The UI-thread facts
            // (rules, identity, the diagnostics text) are gathered first; cancelled on unmount (Cancel / a kind switch).
            UseEffect(() =>
            {
                var cts = new CancellationTokenSource();
                var input = ReportComposer.Gather(Kind, Prefill, Crash);
                Task.Run(() =>
                {
                    ComposedReport result;
                    try { result = ReportComposer.Compose(input); }
                    catch (Exception ex) { Log.Warn("report", "compose failed", ex); return; }
                    if (!cts.IsCancellationRequested) post(() => { if (!cts.IsCancellationRequested) composed.Value = result; });
                });
                return (Action?)(() => cts.Cancel());
            }, DepKey.Empty);

            ReportDraft Draft() => new(title.Peek(), f1.Peek(), f2.Peek(), f3.Peek(), when.Peek(), repro.Peek(), area.Peek());

            var channel = ReportChannels.For(Kind);
            UseEffect(() =>
            {
                Session.Submit = () =>
                {
                    if (composed.Peek() is not { } c)
                    {
                        Notify.Say(Loc.Get(Strings.Report.Preparing), InfoBarSeverity.Informational);
                        return false;
                    }
                    if (!isCrash && title.Peek().Trim().Length == 0)
                    {
                        Notify.Say(Loc.Get(Strings.Report.TitleRequired), InfoBarSeverity.Warning);
                        return false;
                    }
                    OpenGithub(c, Draft(), includeLogs.Peek(), channel, hooks);
                    return true;
                };
                return (Action?)(() => Session.Submit = null);
            }, DepKey.Empty);

            ComposedReport? current = composed.Value;
            var labels = FormLabels();
            var draft = new ReportDraft(title.Value, f1.Value, f2.Value, f3.Value, when.Value, repro.Value, area.Value);
            bool logs = includeLogs.Value;

            var body = new List<Element>(12);
            if (isCrash)
                body.Add(new BoxEl { Direction = 1, Shrink = 0f, Width = FieldWidth, Children = [CrashInfoBar(current)] });

            body.Add(TextBox.Create(title, null, new TextBox.TextBoxOptions
            {
                Header = labels.Title, Placeholder = Loc.Get(Strings.Report.TitlePlaceholder), Width = FieldWidth,
            }));
            AddKindFields(body, labels, f1, f2, f3, when, repro, area);
            body.Add(CheckBox.Create(Strings.Report.IncludeLogs(isCrash ? ReportBundle.CrashLogLines : ReportBundle.ManualLogLines), includeLogs));

            string preview = current is null
                ? Loc.Get(Strings.Report.Preparing)
                : ReportBundle.Preview(BuildBundle(current, labels, draft, logs, DateTimeOffset.Now));
            body.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Width = FieldWidth, Shrink = 0f,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Report.Preview)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
                    Spacer(),
                    Button.Subtle(Loc.Get(Strings.Report.Copy), () => CopyReport(composed.Peek(), Draft(), includeLogs.Peek(), hooks),
                        isEnabled: current is not null),
                    Button.Subtle(Loc.Get(Strings.Report.SaveAs), () => SaveReport(composed.Peek(), Draft(), includeLogs.Peek()),
                        isEnabled: current is not null),
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
                            Children = [Design.Type.MicroMeta(preview) with { FontFamily = "Cascadia Code", Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = FieldWidth - 20f }],
                        },
                    },
                ],
            });
            body.Add(new TextEl(Loc.Get(Strings.Report.PreviewNote)) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = FieldWidth });

            if (isCrash)
                body.Add(CheckBox.Create(Loc.Get(Strings.Report.DontAskAgain), dontAsk,
                    v => Platform.Settings.Set(Platform.Keys.CrashPromptOptOut, v)));   // written immediately, not on close

            return new BoxEl { Direction = 1, Gap = Spacing.M, Width = FieldWidth, Children = body.ToArray() };
        }

        void AddKindFields(List<Element> body, ReportLabels l, Signal<string> f1, Signal<string> f2, Signal<string> f3,
            Signal<int> when, Signal<int> repro, Signal<int> area)
        {
            switch (Kind)
            {
                case ReportKind.Crash:
                    body.Add(ComboBox.Create(ReportChannels.When, when, header: l.When, width: ComboWidth));
                    body.Add(ComboBox.Create(ReportChannels.Reproduces, repro, header: l.Reproduces, width: ComboWidth));
                    body.Add(MultiLine(f1, l.WhatWereYouDoing, FieldHeight));
                    break;
                case ReportKind.Bug:
                    body.Add(MultiLine(f1, l.WhatHappened, FieldHeight));
                    body.Add(MultiLine(f2, l.Steps, FieldHeight));
                    body.Add(MultiLine(f3, l.Expected, FieldHeight));
                    body.Add(ComboBox.Create(ReportChannels.Areas, area, header: l.Area, width: ComboWidth));
                    break;
                case ReportKind.Feature:
                    body.Add(MultiLine(f1, l.Problem, FieldHeight));
                    body.Add(MultiLine(f2, l.Proposal, FieldHeight));
                    body.Add(ComboBox.Create(ReportChannels.Areas, area, header: l.Area, width: ComboWidth));
                    body.Add(MultiLine(f3, l.Alternatives, FieldHeight));
                    break;
                default:   // Question, Idea
                    body.Add(MultiLine(f1, l.Body, BodyFieldHeight));
                    break;
            }
        }

        static Element MultiLine(Signal<string> text, string header, float height) => TextBox.Create(text, null, new TextBox.TextBoxOptions
        {
            Header = header, Width = FieldWidth, AcceptsReturn = true, Height = height,
        });

        /// <summary>Three honest states: still composing (the report is read off the UI thread — "no report was written"
        /// would be a lie for those first frames), a report with its summary, or genuinely no report.</summary>
        static Element CrashInfoBar(ComposedReport? c)
        {
            string message;
            var severity = InfoBarSeverity.Error;
            if (c is null) { message = Loc.Get(Strings.Report.CrashReading); severity = InfoBarSeverity.Informational; }
            else if (c.CrashSummary.Length > 0)
                message = c.CrashTime.Length > 0 ? Strings.Report.CrashAt(c.CrashTime) + " · " + c.CrashSummary : c.CrashSummary;
            else message = Loc.Get(Strings.Report.CrashNoReport);
            return InfoBar.Create(severity, Loc.Get(Strings.Report.CrashTitle), message, isClosable: false, availableWidth: FieldWidth);
        }

        string BuildBundle(ComposedReport c, ReportLabels labels, in ReportDraft draft, bool includeLogs, DateTimeOffset now)
            => ReportBundle.Build(Kind, c.Identity, ReportForm.Answers(Kind, labels, c.Rules, draft), c.Diagnostics,
                c.CrashHead, c.LogLines, c.LogSource, includeLogs, now);

        void CopyReport(ComposedReport? c, ReportDraft draft, bool includeLogs, InputHooks hooks)
        {
            if (c is null) return;
            hooks.Clipboard?.SetText(BuildBundle(c, FormLabels(), draft, includeLogs, DateTimeOffset.Now));
            Notify.Say(Loc.Get(Strings.Report.Copied), InfoBarSeverity.Success);
        }

        void SaveReport(ComposedReport? c, ReportDraft draft, bool includeLogs)
        {
            if (c is null) return;
            var now = DateTimeOffset.Now;
            try
            {
                string? path = FilePicker.SaveFile(FluentApp.WindowHandle, Loc.Get(Strings.Report.SaveAs), ReportBundle.FileName(now),
                    (Loc.Get(LocFilterText), "*.txt"), (Loc.Get(LocFilterAll), "*.*"));
                if (path is null) return;
                File.WriteAllText(path, BuildBundle(c, FormLabels(), draft, includeLogs, now));
                Notify.Say(Strings.Report.Saved(path), InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                Notify.Say(ex.Message, InfoBarSeverity.Error);
            }
        }

        /// <summary>Clipboard + a <c>wavee-report-&lt;stamp&gt;.txt</c> beside the logs + the prefilled form in the browser +
        /// the 8-second "paste it into the … box" toast. The full report never rides the URL.</summary>
        void OpenGithub(ComposedReport c, ReportDraft draft, bool includeLogs, ReportChannel channel, InputHooks hooks)
        {
            var now = DateTimeOffset.Now;
            var labels = FormLabels();
            string bundle = BuildBundle(c, labels, draft, includeLogs, now);

            hooks.Clipboard?.SetText(bundle);
            try
            {
                Directory.CreateDirectory(Platform.LogFolder);
                File.WriteAllText(Path.Combine(Platform.LogFolder, ReportBundle.FileName(now)), bundle);
            }
            catch (Exception ex) { Log.Warn("report", "save bundle failed", ex); }

            string urlTitle = ReportForm.UrlTitle(draft.Title, c.CrashSummary, Loc.Get(Strings.Report.CrashTitle));
            string url = IssueFormUrl.Build(Kind, c.Identity, urlTitle,
                ReportForm.UrlFields(Kind, labels, c.Identity, c.Rules, draft), ReportForm.Labels(Kind, c.Identity, draft.Area));
            OpenUrl(url, hooks);

            Notify.Say(Strings.Report.CopiedPaste(channel.PasteBox), InfoBarSeverity.Success, durationMs: 8000f);
        }
    }

    /// <summary>The browser door: only an http(s) url with a host ever reaches the shell.</summary>
    static void OpenUrl(string url, InputHooks hooks)
    {
        if (!Actions.PlayLinkRules.IsWebUrl(url)) return;
        if (Actions.Services.OpenExternal is { } open) { open(url); return; }
        hooks.OpenUri?.Invoke(url);
    }

    // ══ 6. THE COMPOSER ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The UI-thread half of a compose: everything that reads live app state.</summary>
    sealed record ComposeInput(ReportKind Kind, ReportPrefill? Prefill, CrashPromptDecision Crash, RedactionRules Rules,
        ReportIdentity Identity, string DiagnosticsRaw);

    /// <summary>The fully-assembled, ALREADY-REDACTED material the dialog builds the bundle and URL from. <see cref="Rules"/>
    /// rides along so each keystroke's answers are redacted with the SAME rules the compose used.</summary>
    sealed record ComposedReport(ReportIdentity Identity, string Diagnostics, string[] LogLines, string? CrashHead,
        string? CrashReportPath, string LogSource, string CrashSummary, string CrashTime, RedactionRules Rules);

    /// <summary>The ONE place redaction runs over the big text (once, on a pool thread). <see cref="Compose"/> touches files,
    /// <see cref="Log.Snapshot"/> and the <see cref="PastSessionLog"/> seam only — no engine call.</summary>
    static class ReportComposer
    {
        /// <summary>UI thread: the rules (OS account + machine, and — when known — the signed-in account's id and display
        /// name plus every Connect device the roster holds), the identity, and the raw diagnostics text.</summary>
        public static ComposeInput Gather(ReportKind kind, ReportPrefill? prefill, CrashPromptDecision crash)
        {
            string diagnostics = "";
            try { diagnostics = DiagnosticsText?.Invoke() ?? ""; }
            catch (Exception ex) { Log.Warn("report", "diagnostics text failed", ex); }
            return new ComposeInput(kind, prefill, crash, Rules(), Identity(), diagnostics);
        }

        static RedactionRules Rules()
        {
            string? account = null, displayName = null;
            string[]? devices = null;
            try
            {
                if (Platform.Scope.Account is { Length: > 0 } a) account = a;
                var me = User.Me;
                if (me.IsValid && me.Knows(UserFields.Identity)) displayName = Entities.Strings.Resolve(me.NameId);
                var rows = Playback.Devices.Rows;
                if (rows.Length > 0)
                {
                    devices = new string[rows.Length];
                    for (int i = 0; i < rows.Length; i++) devices[i] = rows[i].Name ?? "";
                }
            }
            catch (Exception ex) { Log.Warn("report", "redaction rules incomplete", ex); }
            return new RedactionRules(Environment.UserName, Environment.MachineName, account, displayName, devices);
        }

        static ReportIdentity Identity()
        {
            var v = Platform.Version;
            return ReportIdentity.From(v.SemVer, v.Codename, v.Quad, v.Commit, v.Channel, v.IsStore, v.IsDev,
                PackageIdentity.IsPackaged, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                Environment.OSVersion.Version.Build);
        }

        /// <summary>Pool thread. Log source, in priority order: (1) the prefill's or the prompt's crash-report file — its
        /// tail IS the excerpt; (2) a specific past session, or the newest one for a WER-dump / unclean-exit prompt;
        /// (3) this session's ring, last <see cref="ReportBundle.ManualLogLines"/> entries.</summary>
        public static ComposedReport Compose(ComposeInput input)
        {
            var rules = input.Rules;
            var id = input.Identity;
            string diagnostics = ReportRedactor.Redact(input.DiagnosticsRaw, rules);

            string? reportPath = input.Prefill?.CrashReportPath is { Length: > 0 } p ? p : input.Crash.ReportPath;
            if (reportPath is { Length: > 0 } && TryReadFile(reportPath, out string fileText, out string crashTime))
            {
                var (head, tail) = ReportBundle.SplitCrashReport(fileText);
                string[] lines = new string[tail.Length];
                for (int i = 0; i < tail.Length; i++) lines[i] = ReportRedactor.Redact(tail[i], rules);
                string summary = ReportRedactor.Redact(ReportBundle.ExceptionSummary(head), rules);
                return new ComposedReport(id, diagnostics, lines, ReportRedactor.Redact(head, rules), reportPath,
                    "crash report", summary, crashTime, rules);
            }

            string? pastId = input.Prefill?.PastSessionId;
            bool wantsPast = pastId is not null || input.Crash.Source is CrashSource.WerDump or CrashSource.UncleanExit;
            if (wantsPast && PastSessionLog?.Invoke(pastId) is { } raw)
            {
                int cap = input.Kind == ReportKind.Crash ? ReportBundle.CrashLogLines : ReportBundle.ManualLogLines;
                int take = Math.Min(raw.Count, cap), start = raw.Count - take;
                string[] lines = new string[take];
                for (int i = 0; i < take; i++) lines[i] = ReportRedactor.Redact(raw[start + i], rules);
                string? head = input.Crash.Source == CrashSource.WerDump && input.Crash.DumpPath is { Length: > 0 } dump
                    ? ReportRedactor.Redact("Windows Error Reporting dump: " + dump, rules)
                    : null;
                return new ComposedReport(id, diagnostics, lines, head, null, "previous session", "", "", rules);
            }

            var snapshot = Log.Snapshot();
            int count = Math.Min(snapshot.Length, ReportBundle.ManualLogLines), from = snapshot.Length - count;
            var thisSession = new string[count];
            for (int i = 0; i < count; i++)
            {
                var e = snapshot[from + i];
                thisSession[i] = ReportRedactor.Redact("seq=" + e.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + e.Format(), rules);
            }
            return new ComposedReport(id, diagnostics, thisSession, null, null, "this session", "", "", rules);
        }

        /// <summary>The file's text plus its last-write time ("t") for the crash InfoBar — read here, off the UI thread,
        /// rather than per render.</summary>
        static bool TryReadFile(string path, out string text, out string time)
        {
            text = "";
            time = "";
            try
            {
                if (!File.Exists(path)) return false;
                text = File.ReadAllText(path);
                time = File.GetLastWriteTime(path).ToString("t");
                return true;
            }
            catch { return false; }
        }
    }

    // ══ 7. THE CRASH-REPORTS CARD (ch 27 W19) ══════════════════════════════════════════════════════════════════════

    const int MaxListedReports = 10;

    /// <summary>A collapsed SettingsExpander: one blank-label row of 12-DIP tertiary text when there are no reports,
    /// otherwise up to ten rows labelled with the file's "g" stamp and carrying [ Report… ] [ Open ]. The listing is a
    /// snapshot at mount — a report written this session lands after the tab remounts.</summary>
    sealed class CrashReportsCardCore : Component
    {
        public override Element Render()
        {
            var reports = UseMemo<IReadOnlyList<(string Path, DateTime Stamp)>>(
                static () => ListCrashReports?.Invoke(MaxListedReports) ?? Array.Empty<(string Path, DateTime Stamp)>(), DepKey.Empty);

            Element[] items;
            if (reports.Count == 0)
            {
                items =
                [
                    SettingsExpander.Item("", null, new TextEl(Loc.Get(Strings.Report.CrashReportsEmpty)) { Size = 12f, Color = Tok.TextTertiary })
                        with { Key = "crash-reports:empty" },
                ];
            }
            else
            {
                items = new Element[reports.Count];
                for (int i = 0; i < reports.Count; i++)
                {
                    var (path, stamp) = reports[i];
                    items[i] = SettingsExpander.Item(stamp.ToString("g"), null,
                        HStack(Spacing.S,
                            Button.Standard(Loc.Get(Strings.Report.ReportButton),
                                () => Open(ReportKind.Crash, new ReportPrefill(CrashReportPath: path))),
                            Button.Standard(Loc.Get(Strings.Report.OpenButton), () => RevealInExplorer(path))))
                        with { Key = "crash-reports:" + path };
                }
            }

            return SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(Strings.Report.CrashReports),
                Description = Loc.Get(Strings.Report.CrashReportsSub),
                HeaderIcon = Icons.StatusWarning,
                InitiallyExpanded = false,
                Items = items,
            });
        }
    }

    /// <summary>Explorer with the file SELECTED, falling back to its folder when the file is gone. Best-effort: a
    /// missing Explorer or a denied path never throws into the UI thread.</summary>
    static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                // /select, takes ONE argument, quoted as a whole — the comma is part of the switch.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                    { UseShellExecute = false })?.Dispose();
                return;
            }
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Log.Warn("report", "reveal crash report failed", ex); }
    }
}
