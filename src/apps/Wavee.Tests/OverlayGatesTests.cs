// ── Wavee.Tests/OverlayGatesTests.cs — the overlay lane's gates, the update-toast model, the transient set (PR C8) ──
//
// `Shell.Overlays.UI.cs` read the facts and decided inside its watcher components. The decisions are `OverlayGates` and
// `UpdateToastModel` now; these tests drive them directly. Nothing here touches the toast door, the settings store or the
// engine's overlay host.

using FluentGpu.Controls;
using Xunit;

namespace Wavee.Tests;

public class OverlayGatesTests
{
    // ── the playback-runtime banner ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Setup.RuntimeIssue.Missing, false, false, false, true)]
    [InlineData(Setup.RuntimeIssue.None, false, false, false, false)]     // nothing to say: never flashes
    [InlineData(Setup.RuntimeIssue.Missing, true, false, false, false)]   // the user dismissed it
    [InlineData(Setup.RuntimeIssue.Missing, false, true, false, false)]   // the wizard's own Local-playback page is asking
    [InlineData(Setup.RuntimeIssue.Missing, false, false, true, false)]   // a still-pending wizard will ask
    [InlineData(Setup.RuntimeIssue.NoPack, false, false, false, true)]
    public void The_banner_shows_only_when_there_is_something_to_say_and_nobody_else_is_asking(
        Setup.RuntimeIssue issue, bool dismissed, bool covering, bool pending, bool expected)
        => Assert.Equal(expected, OverlayGates.ShowsRuntimeBanner(issue, dismissed, covering, pending));

    // ── the runtime-missing toast ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_runtime_missing_failure_with_a_host_raises_the_toast_when_nothing_else_is_saying_it()
        => Assert.True(OverlayGates.ShowsRuntimeMissingToast(Playback.Fault.RuntimeMissing, hostInstalled: true,
            wizardCovering: false, setupPending: false, Setup.RuntimeIssue.None, dismissed: false));

    [Theory]
    [InlineData(Playback.Fault.Network)]
    [InlineData(Playback.Fault.DecodeFailed)]
    [InlineData(Playback.Fault.None)]
    public void Any_other_fault_raises_no_setup_toast(Playback.Fault fault)
        => Assert.False(OverlayGates.ShowsRuntimeMissingToast(fault, hostInstalled: true, wizardCovering: false,
            setupPending: false, Setup.RuntimeIssue.None, dismissed: false));

    [Fact]
    public void Without_a_host_to_fix_it_the_toast_is_not_offered()
        => Assert.False(OverlayGates.ShowsRuntimeMissingToast(Playback.Fault.RuntimeMissing, hostInstalled: false,
            wizardCovering: false, setupPending: false, Setup.RuntimeIssue.None, dismissed: false));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_wizard_asking_the_same_question_suppresses_the_toast(bool covering, bool pending)
        => Assert.False(OverlayGates.ShowsRuntimeMissingToast(Playback.Fault.RuntimeMissing, hostInstalled: true,
            covering, pending, Setup.RuntimeIssue.None, dismissed: false));

    [Fact]
    public void A_banner_already_on_screen_suppresses_the_toast_but_a_dismissed_one_does_not()
    {
        // The banner would show for a Missing issue that was not dismissed: it already says it.
        Assert.False(OverlayGates.ShowsRuntimeMissingToast(Playback.Fault.RuntimeMissing, hostInstalled: true,
            wizardCovering: false, setupPending: false, Setup.RuntimeIssue.Missing, dismissed: false));
        // Dismissed, the banner is gone: the toast is the door left.
        Assert.True(OverlayGates.ShowsRuntimeMissingToast(Playback.Fault.RuntimeMissing, hostInstalled: true,
            wizardCovering: false, setupPending: false, Setup.RuntimeIssue.Missing, dismissed: true));
    }

    // ── the update toast's dial ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(NotifyLevel.Off, false)]
    [InlineData(NotifyLevel.InApp, true)]
    [InlineData(NotifyLevel.Windows, true)]
    public void A_silenced_app_updates_dial_kills_the_toast_outright(NotifyLevel level, bool expected)
        => Assert.Equal(expected, OverlayGates.ShowsUpdateToast(level));
}

public class UpdateToastModelTests
{
    static AppUpdateSnapshot Downloading(int percent)
        => new(AppUpdateState.Downloading, "0.3.5.1", "0.3.5", null, percent, null, true, 0);

    [Fact]
    public void The_progress_is_written_on_every_snapshot_clamped_to_the_unit_range()
    {
        var model = new UpdateToastModel();
        model.Advance(Downloading(40), static () => NotifyLevel.Windows);
        Assert.Equal(0.4f, model.Progress.Value, 3);

        model.Advance(Downloading(250), static () => NotifyLevel.Windows);
        Assert.Equal(1f, model.Progress.Value);
        model.Advance(Downloading(-5), static () => NotifyLevel.Windows);
        Assert.Equal(0f, model.Progress.Value);
    }

    [Fact]
    public void A_plan_is_the_notify_plan_between_the_previous_and_the_next_snapshot()
    {
        var model = new UpdateToastModel();
        var first = Downloading(10);
        var second = Downloading(20);

        var a = model.Advance(first, static () => NotifyLevel.Windows);
        var b = model.Advance(second, static () => NotifyLevel.Windows);

        Assert.Equal(Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, first)?.Title, a?.Title);
        Assert.Equal(Notify.AppUpdateToasts.Plan(first, second)?.Title, b?.Title);
        Assert.Equal(Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, first) is not null, a is not null);
        Assert.Equal(Notify.AppUpdateToasts.Plan(first, second) is not null, b is not null);
    }

    [Fact]
    public void A_silenced_dial_swallows_the_plan_but_still_moves_the_state()
    {
        var model = new UpdateToastModel();
        var snapshot = Downloading(30);
        bool planned = Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, snapshot) is not null;
        Assert.True(planned);   // the fixture is a state that DOES raise a toast, or this test proves nothing

        Assert.Null(model.Advance(snapshot, static () => NotifyLevel.Off));
        Assert.Equal(0.3f, model.Progress.Value, 3);
        // The state moved: the same snapshot again is no transition, whatever the dial says.
        Assert.Equal(Notify.AppUpdateToasts.Plan(snapshot, snapshot) is not null,
            model.Advance(snapshot, static () => NotifyLevel.Windows) is not null);
    }

    [Fact]
    public void The_dial_is_asked_only_when_there_is_a_plan()
    {
        var model = new UpdateToastModel();
        model.Advance(AppUpdateSnapshot.Idle, static () => NotifyLevel.Windows);   // Idle -> Idle: nothing to plan
        int asked = 0;
        model.Advance(AppUpdateSnapshot.Idle, () => { asked++; return NotifyLevel.Windows; });
        Assert.Equal(0, asked);
    }

    [Fact]
    public void Whats_new_opens_the_target_version_and_anything_else_is_not_the_models_to_navigate()
    {
        var withSemVer = Downloading(100);
        Assert.Equal("0.3.5", UpdateToastModel.WhatsNewVersion(withSemVer));
        var quadOnly = withSemVer with { TargetSemVer = null };
        Assert.Equal(Notify.AppUpdateVersion.ReleaseTagVersion("0.3.5.1"), UpdateToastModel.WhatsNewVersion(quadOnly));

        string? opened = null;
        UpdateToastModel.RunAction(ToastActionKind.WhatsNew, withSemVer, v => opened = v);
        Assert.Equal("0.3.5", opened);
    }
}

public class OverlayServiceTests
{
    [Fact]
    public void A_handle_that_is_not_open_falls_out_of_the_set_and_closing_it_is_harmless()
    {
        var transients = new OverlayService();
        var never = new OverlayHandle();      // a host-less mount hands back an inert handle
        transients.Add(never);

        Assert.Equal(0, transients.OpenCount);
        transients.CloseTransients();
        transients.CloseTransients(except: never);
        Assert.Equal(0, transients.OpenCount);
    }

    [Fact]
    public void Adding_nothing_is_a_programming_error()
        => Assert.Throws<ArgumentNullException>(() => new OverlayService().Add(null!));
}
