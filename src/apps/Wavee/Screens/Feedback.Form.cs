// ── Screens/Feedback.Form.cs — the report form's core half (themes plan 6.0, T11c) ──────────────────────────────────────
//
// Role: CORE. The report dialog's body is the theme's picture (`Dialog.Feedback`: `Themes\Classic\Structure\Views\Screens\ClassicFeedback.cs`); what the
// form decides and does lives here, moved from the old dialog card unchanged:
//   · the off-thread compose (`ReportComposer.Gather` on the UI thread, `Compose` on a pool thread, posted back; cancelled with the card),
//   · the live preview text, and the crash form's three honest header states,
//   · the submit gate (still composing / no title, each with its toast) and the door it opens (clipboard + the file beside the logs + the prefilled
//     GitHub form + the "paste it" toast), and the Copy and Save As doors (the picker runs on its own thread, #155).
// The view owns the field signals (the title, the three free-text boxes, the three dropdown indices, the include-logs box).

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Feedback
{
    /// <summary>One open dialog: the veto handshake with the chrome (the card installs its submit while mounted) and the form factory.</summary>
    sealed class DialogSession(ReportPrefill? prefill) : IFeedbackDialog
    {
        /// <summary>The mounted card's submit, or null.</summary>
        public Func<bool>? Submit;

        public void InstallSubmit(Func<bool>? submit) => Submit = submit;

        public IFeedbackForm OpenForm(ReportKind kind) => new FormModel(kind, prefill);
    }

    /// <summary>The form of one fixed kind. Remounted whole on a kind switch (the view asks for a fresh one), so nothing here outlives its card.</summary>
    sealed class FormModel(ReportKind kind, ReportPrefill? prefill) : IFeedbackForm
    {
        const string LocFilterText = "report.saveFilterText", LocFilterAll = "report.saveFilterAll";

        readonly Signal<ComposedReport?> _composed = new(null);

        public ReportKind Kind => kind;
        public string InitialTitle => prefill?.Title ?? "";
        public bool IncludeLogsByDefault => ReportForm.IncludesLogsByDefault(kind);
        public string[] Whens => ReportChannels.When;
        public string[] Reproduces => ReportChannels.Reproduces;
        public string[] Areas => ReportChannels.Areas;
        public int DefaultArea => ReportChannels.Areas.Length - 1;   // "Not sure"
        public ReportLabels Labels => FormLabels();
        public int LogLineCount => kind == ReportKind.Crash ? ReportBundle.CrashLogLines : ReportBundle.ManualLogLines;
        public IReadSignal<ComposedReport?> Composed => _composed;

        /// <summary>Off-thread compose: file I/O + the whole-text redaction pass over up to ~60 KB. The UI-thread facts (rules, identity, the
        /// diagnostics text) are gathered first; cancelled on unmount (Cancel / a kind switch).</summary>
        public Action? BeginCompose(Action<Action> post)
        {
            var cts = new CancellationTokenSource();
            var input = ReportComposer.Gather(kind, prefill);
            Task.Run(() =>
            {
                ComposedReport result;
                try { result = ReportComposer.Compose(input); }
                catch (Exception ex) { Log.Warn("report", "compose failed", ex); return; }
                if (!cts.IsCancellationRequested) post(() => { if (!cts.IsCancellationRequested) _composed.Value = result; });
            });
            return () => cts.Cancel();
        }

        public string Preview(ComposedReport? composed, in ReportDraft draft, bool includeLogs)
            => composed is null
                ? Loc.Get(Strings.Report.Preparing)
                : ReportBundle.Preview(BuildBundle(composed, FormLabels(), draft, includeLogs, DateTimeOffset.Now));

        /// <summary>Three honest states: still composing (the report is read off the UI thread — "no report was written"
        /// would be a lie for those first frames), a report with its summary, or genuinely no report.</summary>
        public (string Message, InfoBarSeverity Severity) CrashNotice(ComposedReport? c)
        {
            if (c is null) return (Loc.Get(Strings.Report.CrashReading), InfoBarSeverity.Informational);
            if (c.CrashSummary.Length > 0)
                return (c.CrashTime.Length > 0 ? Strings.Report.CrashAt(c.CrashTime) + " · " + c.CrashSummary : c.CrashSummary, InfoBarSeverity.Error);
            return (Loc.Get(Strings.Report.CrashNoReport), InfoBarSeverity.Error);
        }

        public bool Submit(in ReportDraft draft, bool includeLogs, InputHooks hooks)
        {
            if (_composed.Peek() is not { } c)
            {
                Notify.Say(Loc.Get(Strings.Report.Preparing), InfoBarSeverity.Informational);
                return false;
            }
            if (kind != ReportKind.Crash && draft.Title.Trim().Length == 0)
            {
                Notify.Say(Loc.Get(Strings.Report.TitleRequired), InfoBarSeverity.Warning);
                return false;
            }
            OpenGithub(c, draft, includeLogs, ReportChannels.For(kind), hooks);
            return true;
        }

        string BuildBundle(ComposedReport c, ReportLabels labels, in ReportDraft draft, bool includeLogs, DateTimeOffset now)
            => ReportComposer.BuildBundle(kind, c, labels, in draft, includeLogs, now);

        public void Copy(in ReportDraft draft, bool includeLogs, InputHooks hooks)
        {
            if (_composed.Peek() is not { } c) return;
            hooks.Clipboard?.SetText(BuildBundle(c, FormLabels(), draft, includeLogs, DateTimeOffset.Now));
            Notify.Say(Loc.Get(Strings.Report.Copied), InfoBarSeverity.Success);
        }

        public void Save(in ReportDraft draft, bool includeLogs)
        {
            if (_composed.Peek() is not { } c) return;
            var now = DateTimeOffset.Now;
            var saved = draft;
            // The Save As dialog runs on its own thread (Pickers, #155); the write happens when it answers, UI thread.
            Pickers.Pick(
                PickerRequest.Save(Loc.Get(Strings.Report.SaveAs), ReportBundle.FileName(now),
                    (Loc.Get(LocFilterText), "*.txt"), (Loc.Get(LocFilterAll), "*.*")),
                done: path =>
                {
                    if (path is null) return;
                    Exception? failure;
                    try { failure = ReportComposer.TryWriteFile(path, BuildBundle(c, FormLabels(), saved, includeLogs, now)); }
                    catch (Exception ex) { failure = ex; }   // the bundle build itself failing is toasted like the write
                    if (failure is null) Notify.Say(Strings.Report.Saved(path), InfoBarSeverity.Success);
                    else Notify.Say(failure.Message, InfoBarSeverity.Error);
                },
                failed: ex => Notify.Say(ex.Message, InfoBarSeverity.Error));
        }

        /// <summary>Clipboard + a <c>wavee-report-&lt;stamp&gt;.txt</c> beside the logs + the prefilled form in the browser +
        /// the 8-second "paste it into the … box" toast. The full report never rides the URL.</summary>
        void OpenGithub(ComposedReport c, in ReportDraft draft, bool includeLogs, ReportChannel channel, InputHooks hooks)
        {
            var now = DateTimeOffset.Now;
            var labels = FormLabels();
            string bundle = BuildBundle(c, labels, draft, includeLogs, now);

            hooks.Clipboard?.SetText(bundle);
            ReportComposer.SaveBesideLogs(bundle, now);

            string urlTitle = ReportForm.UrlTitle(draft.Title, c.CrashSummary, Loc.Get(Strings.Report.CrashTitle));
            string url = IssueFormUrl.Build(kind, c.Identity, urlTitle,
                ReportForm.UrlFields(kind, labels, c.Identity, c.Rules, draft, prefill?.ReportId), ReportForm.Labels(kind, c.Identity, draft.Area));
            OpenUrl(url, hooks);

            Notify.Say(Strings.Report.CopiedPaste(channel.PasteBox), InfoBarSeverity.Success, durationMs: 8000f);
        }
    }
}
