using Wavee.Backend.Audio;

namespace Wavee;

/// <summary>The Done page's checklist states (settings, sidebar, library, runtime — in that fixed display order),
/// computed from REAL observables rather than a synthetic apply-stage counter. Settings and sidebar are pinned
/// <see cref="SetupStepState.Done"/>: both are written synchronously, per-page, before Done ever mounts
/// (Terms/Appearance/Sidebar/Sound/Notifications each persist their own settings on the spot), so there is nothing
/// left to observe for either row by the time this page is on screen. Library reflects whether the real library-sync
/// service (when one exists) has caught up; runtime reflects the actual playback-runtime provisioning outcome, with
/// an explicit decline counted as done (the user chose not to have local playback, which is a settled state, not a
/// pending one).
///
/// <para>ENGINE-FREE BY CONSTRUCTION (no <c>FluentGpu.*</c>, no <c>Loc</c>, no <c>Signal&lt;T&gt;</c>), exactly like
/// <see cref="SetupRuntimePresentation"/> — source-included by <c>Wavee.Tests</c> so a step-state theory test drives
/// the REAL decision, not a copy of it.</para>
///
/// <para><b>Honesty note for <c>SetupSession.ApplyStage</c>'s doc comment:</b> that signal is a vestige of the
/// deleted "Applying" pane's four-stage progress list — it is written to 4 exactly once, in <c>PrimaryDone</c>, and
/// never advances through 0..3 in practice (<c>SetupSession.Apply</c> itself never enters <c>Running</c>). The Done
/// page no longer reads it at all: every row's state below comes from the thing that row actually claims to
/// represent, not from that counter. <c>ApplyStage</c>'s own doc comment should say so (it still describes itself as
/// what <c>SetupDonePage</c> drives the checklist off, which is no longer true).</para></summary>
static class SetupDoneSteps
{
    public static SetupStepState[] Compute(bool hasRealSync, bool libraryIdle, bool runtimeDeclined, ProvisioningOutcome outcome)
    {
        SetupStepState library = !hasRealSync || libraryIdle ? SetupStepState.Done : SetupStepState.Current;
        SetupStepState runtime = runtimeDeclined || outcome == ProvisioningOutcome.Ready
            ? SetupStepState.Done
            : outcome == ProvisioningOutcome.NeverAttempted
                ? SetupStepState.Current
                : SetupStepState.Failed;

        return [SetupStepState.Done, SetupStepState.Done, library, runtime];
    }
}
