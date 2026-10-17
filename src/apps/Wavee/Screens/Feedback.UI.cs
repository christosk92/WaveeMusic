// ── Screens/Feedback.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the report dialog's chrome and doors (Bug / Feature / Question / Idea / Crash), Requests (the monotonic request signal) + Open,
// the zero-size report chrome (Feedback.Chrome: requests only — the crash prompt and the --crash-probe timer moved to
// Screens/Crash.UI.cs, WP-E, crash-diagnostics-implementation.md)
//
// THE BODY IS THE THEME'S (T11c): the stock ContentDialog chrome (title, buttons, the vetoable primary click) is here; the dialog's
// content is `Dialog.Feedback` (`Themes\Classic\Structure\Views\Screens\ClassicFeedback.cs`), whose core half (the off-thread
// compose, the preview, the submit gate and the three doors) is `Screens/Feedback.Form.cs`.
//
// Role: CORE host + doors
// Owner: R
// Wave: 6
// Budget: 800 lines
// Spec: ch 28 §9.5 (W24, W25, §1.4, §3.4, §6.4, §9.1 #10); docs/plans/wavee/crash-diagnostics-implementation.md §E
//       "Deletions" (the crash-prompt arm, the probe timer and the crash-reports card retired here)
//
// MOUNT ORDER: Feedback.Chrome() now carries only the manual report-dialog request signal; the crash prompt / hang
// toast / probe timer live in Crash.Chrome() (Screens/Crash.UI.cs), mounted right after this one in the shell overlay
// layer (Shell.Overlays.UI.cs) — see that file for the load-bearing ordering note against ReleaseNotes.AfterUpdateChrome.
//
// THE SEAMS THIS FILE LEAVES (every one null-safe; the honest absent state is named on each):
//   · DiagnosticsText  — owner S: the "Copy diagnostics info" text (0.2.9 SettingsPage.DiagInfoText).
//   · PastSessionLog   — owner S: a past session's raw log lines (WaveeLogSessions). Called OFF the UI thread.
// Read directly, no seam: the log ring (Log.Snapshot), Platform.Version, and the account/device names (Platform.Scope,
// User.Me, Playback.Devices).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Feedback
{
    // ══ 1. INSTALL, MOUNT POINTS, THE ONE OPEN DOOR ════════════════════════════════════════════════════════════════

    /// <summary>Owner R's contract call: fills <see cref="Shell.ReportDialogOpener"/> — the
    /// <c>wavee://open?route=report&amp;arg=bug|feature|crash|question|idea</c> deep link (an unknown arg is a Bug).</summary>
    public static void InstallUi()
        => Shell.ReportDialogOpener = static (overlay, arg) =>
            OpenDialog(overlay, ReportChannels.TryParseKind(arg, out var kind) ? kind : ReportKind.Bug, null);

    /// <summary>Zero-size chrome, mounted in the overlay layer SECOND (see the header).</summary>
    // MOUNT POINT (owner R contract)
    public static Element Chrome() => Embed.Comp(static () => new ChromeCore());

    /// <summary>Open the report dialog from anywhere — About's "Report a problem…" (Bug) / "Suggest a feature…"
    /// (Feature), the Logs panel's "Report this session…" (Bug + <see cref="ReportPrefill.PastSessionId"/>). UI thread.</summary>
    public static void Open(ReportKind kind, ReportPrefill? prefill = null) => Requests.Open(kind, prefill);

    /// <summary>The crash form's manual door: the crash prompt's "Report on GitHub instead" (WP-E,
    /// <c>Crash.CrashPromptBody</c> — the launch prompt and the Reports list's per-row Send) lands here rather
    /// than a fixed <see cref="ReportKind.Crash"/> + a decision record — the crash & diagnostics pipeline (WP-A/B/C)
    /// owns consent and upload now; this dialog is only ever the MANUAL "tell the developer by hand" door for a
    /// specific bundle's <c>report.txt</c>. The bundle's report id rides along so the GitHub form's <c>report-id</c>
    /// field names the same report the crash service holds (#165). UI thread.</summary>
    public static void OpenCrashReport(Crash.BundleInfo bundle)
        => Requests.Open(ReportKind.Crash, new ReportPrefill(CrashReportPath: bundle.ReportTxt, ReportId: bundle.Summary.ReportId));

    // ══ 2. THE SEAMS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The diagnostics block's raw text, read on the UI thread when the dialog mounts, redacted off it. Null ⇒
    /// the bundle carries no diagnostics section.</summary>
    public static Func<string>? DiagnosticsText;

    /// <summary>A past session's raw log lines: the arg is a <see cref="ReportPrefill.PastSessionId"/>, or null for
    /// "the newest past session". Runs OFF the UI thread. Null seam or null answer ⇒ this session's own ring.</summary>
    public static Func<string?, IReadOnlyList<string>?>? PastSessionLog;

    // ══ 3. REQUESTS (the monotonic request signal) ═════════════════════════════════════════════════════════════════

    /// <summary>"Open the report dialog" as a signal the chrome observes, so no entry point holds the overlay service.
    /// The payload rides IN the signal with its own monotonic <see cref="Request.Seq"/>: two opens in one flush cannot
    /// collapse, and no stale prefill can outlive its request.</summary>
    public static class Requests
    {
        public sealed record Request(int Seq, ReportKind Kind, ReportPrefill? Prefill);

        static int s_seq;

        /// <summary>The most recent request, or null before the first.</summary>
        public static readonly Signal<Request?> Requested = new(null);

        public static void Open(ReportKind kind, ReportPrefill? prefill = null)
            => Requested.Value = new Request(++s_seq, kind, prefill);
    }

    // ══ 4. THE CHROME ══════════════════════════════════════════════════════════════════════════════════════════════

    sealed class ChromeCore : Component
    {
        // A STATIC, not a UseRef(-1) baseline (ch 28 §9.1 #10): a UseRef treats "whatever Seq is current when I first
        // mount" as already served and swallows the first request after an OverlayHost subtree remount.
        static int s_lastOpenedSeq = -1;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);

            var req = Requests.Requested.Value;
            UseEffect(() =>
            {
                if (req is null || req.Seq == s_lastOpenedSeq) return;
                s_lastOpenedSeq = req.Seq;
                OpenDialog(overlay, req.Kind, req.Prefill);
            }, req?.Seq ?? -1);

            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Shrink = 0f };
        }
    }

    // ══ 5. THE DIALOG ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The dialog chrome's width: the WinUI ContentDialog maximum (548, padding 24). The structure's overlay fit re-sizes it per window band
    /// (`OverlayFit.ShowForm`: the window less 16 at Compact, a two-pane card at Huge), and the body reads the width its card leaves it.</summary>
    const float DialogWidth = 548f;

    /// <summary>A crash kind / crash-report prefill forces the fixed Crash form (<see cref="ReportForm.EffectiveKind"/>) —
    /// the crash & diagnostics pipeline (WP-A/B/C, <c>Crash.UI.cs</c>) owns consent and upload; this is only ever the
    /// manual "tell the developer by hand" door. The dialog is the stock <c>ContentDialog</c> at its 548 maximum.</summary>
    static void OpenDialog(IOverlayService? overlay, ReportKind kind, ReportPrefill? prefill)
    {
        if (overlay is null) return;
        ReportKind effective = ReportForm.EffectiveKind(kind, prefill?.CrashReportPath, false);
        bool isCrash = effective == ReportKind.Crash;

        // Names the kind that ACTUALLY arrived at the one place that decides what the dialog shows.
        Log.Info("report", $"report.open kind={kind} effective={effective} prefill={prefill is not null}");

        var session = new DialogSession(prefill);
        OverlayFit.ShowForm(overlay, DialogWidth, d =>
        {
            d.Title = Loc.Get(Strings.Report.Title);
            // Keyed by the kind shown: every signal in the body is seeded once from an open-time constant.
            d.Content = Slots.View(ViewKeys.DialogFeedback, new FeedbackDialogProps { InitialKind = effective, Dialog = session })
                with { Key = "report-body:" + (int)effective };
            d.PrimaryText = Loc.Get(isCrash ? Strings.Report.ReportOnGithub : Strings.Report.OpenGithub);
            d.CloseText = Loc.Get(isCrash ? Strings.Report.NotNow : Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            // VETOABLE: the body says why with a toast (still composing / no title) and the dialog stays open.
            d.PrimaryButtonClick = args => { if (!(session.Submit?.Invoke() ?? false)) args.Cancel = true; };
        });
    }

    static ReportLabels FormLabels() => new(
        Loc.Get(Strings.Report.TitleLabel), Loc.Get(Strings.Report.When), Loc.Get(Strings.Report.Reproduces),
        Loc.Get(Strings.Report.WhatWereYouDoing), Loc.Get(Strings.Report.WhatHappened), Loc.Get(Strings.Report.Steps),
        Loc.Get(Strings.Report.Expected), Loc.Get(Strings.Report.Area), Loc.Get(Strings.Report.Problem),
        Loc.Get(Strings.Report.Proposal), Loc.Get(Strings.Report.Alternatives), Loc.Get(Strings.Report.Body));

    /// <summary>The browser door: only an http(s) url with a host ever reaches the shell.</summary>
    static void OpenUrl(string url, InputHooks hooks)
    {
        if (!Actions.PlayLinkRules.IsWebUrl(url)) return;
        if (Actions.Services.OpenExternal is { } open) { open(url); return; }
        hooks.OpenUri?.Invoke(url);
    }
}
