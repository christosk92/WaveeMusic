// ── Wavee.Tests/ReleaseNotesModelTests.cs — the What's-new surfaces' model (C15, X37) ─────────────────────────────────
//
// `ReleaseNotesModel` is what the page and the after-update plate used to do inline: the last-seen marker, the deep-link
// read, the gate / latch / load / resolve of the automatic plate, and the auto-show write. The four gates themselves are
// pinned in ReleaseNotesTests (`AfterUpdateGate.Decide`, `Consume`); these facts pin the model's settings traffic and the
// once-per-launch latch. `Platform.Settings` and the latch are process state, so the class joins the platform collection.

using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class ReleaseNotesModelTests : IDisposable
{
    readonly MemoryAppSettings _store = new();
    readonly bool _crashNotice = ReleaseNotes.CrashNoticeThisLaunch;

    public ReleaseNotesModelTests()
    {
        Platform.UseSettings(_store);
        ReleaseNotesModel.ResetLatchForTests();
        ReleaseNotes.CrashNoticeThisLaunch = false;
    }

    public void Dispose()
    {
        ReleaseNotes.CrashNoticeThisLaunch = _crashNotice;
        ReleaseNotesModel.ResetLatchForTests();
        Platform.UseSettings(null);
    }

    // ── last seen ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Notes_nobody_opened_are_unread_and_opening_them_marks_the_running_core_seen()
    {
        Assert.Equal("", ReleaseNotesModel.LastSeen());
        Assert.True(ReleaseNotesModel.RunningNotesUnread());

        ReleaseNotesModel.MarkSeen();

        Assert.Equal(Platform.Version.Core, ReleaseNotesModel.LastSeen());
        Assert.False(ReleaseNotesModel.RunningNotesUnread());
        Assert.Equal(Platform.Version.Core, _store.Get(Platform.Keys.ReleaseNotesLastSeen));
    }

    [Fact]
    public void The_running_build_is_the_one_the_loaders_take()
    {
        var running = ReleaseNotesModel.Running();

        Assert.Equal(Platform.Version.Core, running.Core);
        Assert.Equal(Platform.Version.Channel, running.Channel);
        Assert.Equal(Platform.Version.IsStore, running.IsStore);
    }

    [Fact]
    public void The_summary_again_source_is_the_durable_previous_version_else_the_running_quad()
    {
        Assert.Equal(Platform.Version.Quad, ReleaseNotesModel.SummaryFromQuad());

        _store.Set(Platform.Keys.ReleaseNotesPreviousVersion, "0.2.8.4");

        Assert.Equal("0.2.8.4", ReleaseNotesModel.SummaryFromQuad());
    }

    // ── the deep-link read ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a link")]
    [InlineData("wavee://open?route=definitely-not-a-route")]
    public void A_highlight_without_an_openable_link_has_no_route(string? deepLink)
        => Assert.Null(ReleaseNotesModel.OpenRouteFor(deepLink));

    [Fact]
    public void An_open_link_resolves_to_its_route()
    {
        var route = ReleaseNotesModel.OpenRouteFor("wavee://open?route=home");

        Assert.NotNull(route);
        Assert.Equal(Shell.RouteKind.Home, route!.Value.Kind);
    }

    // ── the auto-show write ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dont_show_again_switches_auto_show_off_and_unchecking_it_switches_it_back_on()
    {
        Assert.True(_store.Get(Platform.Keys.ReleaseNotesAutoShow));

        ReleaseNotesModel.SetDontShowAgain(true);
        Assert.False(_store.Get(Platform.Keys.ReleaseNotesAutoShow));

        ReleaseNotesModel.SetDontShowAgain(false);
        Assert.True(_store.Get(Platform.Keys.ReleaseNotesAutoShow));
    }

    // ── the gate ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_pending_is_nothing_to_show()
        => Assert.Equal(ReleaseNotes.AfterUpdateVerdict.NothingPending, ReleaseNotesModel.Gate());

    [Fact]
    public void An_armed_update_opens_unless_auto_show_is_off_the_wizard_is_up_or_a_crash_notice_came_first()
    {
        _store.Set(Platform.Keys.ReleaseNotesPendingFrom, "0.2.8.4");
        Assert.Equal("0.2.8.4", ReleaseNotesModel.PendingFrom());
        Assert.Equal(ReleaseNotes.AfterUpdateVerdict.Open, ReleaseNotesModel.Gate());

        ReleaseNotes.CrashNoticeThisLaunch = true;
        Assert.Equal(ReleaseNotes.AfterUpdateVerdict.CrashNoticeDeferred, ReleaseNotesModel.Gate());
        ReleaseNotes.CrashNoticeThisLaunch = false;

        _store.Set(Platform.Keys.SetupPending, true);
        Assert.Equal(ReleaseNotes.AfterUpdateVerdict.WizardDeferred, ReleaseNotesModel.Gate());
        _store.Set(Platform.Keys.SetupPending, false);

        ReleaseNotesModel.SetDontShowAgain(true);
        Assert.Equal(ReleaseNotes.AfterUpdateVerdict.AutoShowOff, ReleaseNotesModel.Gate());
    }

    // ── the latch ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_automatic_load_is_claimed_once_per_launch_and_only_through_an_open_gate()
    {
        Assert.False(ReleaseNotesModel.TryBeginAutomaticLoad());              // nothing pending: the gate is shut

        _store.Set(Platform.Keys.ReleaseNotesPendingFrom, "0.2.8.4");
        Assert.True(ReleaseNotesModel.TryBeginAutomaticLoad());
        Assert.False(ReleaseNotesModel.TryBeginAutomaticLoad());              // one load at a time, one plate per launch
    }

    [Fact]
    public void A_deferred_gate_does_not_claim_the_latch()
    {
        _store.Set(Platform.Keys.ReleaseNotesPendingFrom, "0.2.8.4");
        _store.Set(Platform.Keys.SetupPending, true);

        Assert.False(ReleaseNotesModel.TryBeginAutomaticLoad());

        _store.Set(Platform.Keys.SetupPending, false);                        // the wizard finished: the next bump claims it
        Assert.True(ReleaseNotesModel.TryBeginAutomaticLoad());
    }

    // ── the resolve step ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_automatic_load_that_found_notes_opens_and_consumes_the_one_shot()
    {
        _store.Set(Platform.Keys.ReleaseNotesPendingFrom, "0.2.8.4");
        Assert.True(ReleaseNotesModel.TryBeginAutomaticLoad());

        Assert.True(ReleaseNotesModel.Resolve(automatic: true, loaded: true));

        Assert.Equal("", _store.Get(Platform.Keys.ReleaseNotesPendingFrom));
    }

    [Fact]
    public void An_automatic_load_that_found_nothing_opens_nothing_and_leaves_the_key_armed()
    {
        _store.Set(Platform.Keys.ReleaseNotesPendingFrom, "0.2.8.4");
        Assert.True(ReleaseNotesModel.TryBeginAutomaticLoad());

        Assert.False(ReleaseNotesModel.Resolve(automatic: true, loaded: false));

        Assert.Equal("0.2.8.4", _store.Get(Platform.Keys.ReleaseNotesPendingFrom));   // the next launch tries again
        Assert.False(ReleaseNotesModel.TryBeginAutomaticLoad());                      // and this launch spent its one load
    }

    [Fact]
    public void A_wizard_that_opened_meanwhile_frees_the_latch_for_the_next_marker_bump()
    {
        _store.Set(Platform.Keys.ReleaseNotesPendingFrom, "0.2.8.4");
        Assert.True(ReleaseNotesModel.TryBeginAutomaticLoad());
        _store.Set(Platform.Keys.SetupPending, true);                                 // the wizard armed while the load ran

        Assert.False(ReleaseNotesModel.Resolve(automatic: true, loaded: true));

        Assert.Equal("0.2.8.4", _store.Get(Platform.Keys.ReleaseNotesPendingFrom));   // still armed
        _store.Set(Platform.Keys.SetupPending, false);
        Assert.True(ReleaseNotesModel.TryBeginAutomaticLoad());                       // retry on the next bump
    }

    [Fact]
    public void A_manual_open_always_opens_and_consumes_the_one_shot()
    {
        _store.Set(Platform.Keys.ReleaseNotesPendingFrom, "0.2.8.4");

        Assert.True(ReleaseNotesModel.Resolve(automatic: false, loaded: false));      // the user asked: "Show the update summary again"

        Assert.Equal("", _store.Get(Platform.Keys.ReleaseNotesPendingFrom));
    }
}
