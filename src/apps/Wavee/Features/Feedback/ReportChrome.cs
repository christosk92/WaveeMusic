using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>Zero-size chrome that owns the report dialog's two triggers, mounted INSIDE the <c>OverlayHost</c>
/// subtree next to <c>AfterUpdateChrome</c> — the only place <c>UseContext(Overlay.Service)</c> resolves the real
/// service (see <c>AfterUpdateChrome</c>'s remarks for why this can't live in the shell's own render).
///
/// <para>Effect A: <see cref="ReportRequests.Requested"/> bumped ⇒ open the dialog for whatever kind/prefill the
/// request set — the "open from anywhere" entry point (About's links, the Diagnostics overflow, the Crash reports
/// card, the <c>wavee://open?route=report</c> deep link).</para>
///
/// <para>Effect B: once per launch, <see cref="CrashPromptPolicy.ThisLaunch"/> — set by <c>Program.cs</c> before the
/// first frame — decides whether the previous run needs surfacing. It is consumed here (reset to <c>default</c>
/// immediately) exactly the way <c>AfterUpdateDialog.CrashNoticeThisLaunch</c> is consumed: a one-shot latch that a
/// crash, a force-quit or a dismissal can never resurrect on the next launch. The SAME deferral <c>AfterUpdateChrome</c>
/// applies (setup pending / a wizard session live) applies here too, re-evaluated on <see cref="SetupSession.MarkerEpoch"/>
/// so a crash prompt still appears in the SAME launch once setup is out of the way rather than being lost.</para></summary>
sealed class ReportChrome(IAppSettings? settings) : Component
{
    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var svc = UseContext(Services.Slot);
        var hooks = UseContext(InputHooks.Current);

        int req = ReportRequests.Requested.Value;
        var last = UseRef(-1);
        UseEffect(() =>
        {
            if (last.Value < 0) { last.Value = req; return; }
            if (req == last.Value) return;
            last.Value = req;
            ReportDialog.Open(overlay, svc, hooks, settings, ReportRequests.Kind, ReportRequests.Prefill, default);
        }, req);

        int wizardEpoch = SetupSession.MarkerEpoch.Value;
        UseEffect(() =>
        {
            var d = CrashPromptPolicy.ThisLaunch;
            if (d.Mode == CrashPromptMode.None) return;
            if (SetupGating.IsPending(settings) || SetupSession.Current is not null) return;   // defer to the next check — still armed
            CrashPromptPolicy.ThisLaunch = default;   // one-shot: consumed now, whichever branch fires below

            if (d.Mode == CrashPromptMode.Toast)
            {
                Toast.Show(Loc.Get(Strings.Common.CrashLastRun), new ToastOptions
                {
                    Severity = InfoBarSeverity.Warning,
                    DurationMs = 0f,
                    ActionLabel = Loc.Get(Strings.Report.ReportOnGithub),
                    OnAction = () => ReportRequests.Open(ReportKind.Crash, new ReportPrefill(CrashReportPath: d.ReportPath)),
                    DedupeKey = "crash.pendingReport",
                });
                return;
            }

            ReportDialog.Open(overlay, svc, hooks, settings, ReportKind.Crash, null, d);
        }, wizardEpoch);

        if (CrashProbe.Mode is { } mode)
        {
            UseTimeout(() =>
            {
                if (mode == "failfast") System.Environment.FailFast("--crash-probe");
                throw new System.InvalidOperationException("--crash-probe");
            }, 2000f);
        }

        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Shrink = 0f };
    }
}
