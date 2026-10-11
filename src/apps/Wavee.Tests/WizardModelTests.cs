// ── Wavee.Tests/WizardModelTests.cs — the setup wizard's model, per entry (C14, X32) ──────────────────────────────────
//
// `Setup.WizardModel` is the wizard's live session moved out of the view file: page, swap direction, the Primary /
// Secondary / Back verbs, the step facts the footer reads, and the open DECIDED AT BOOT (`Setup.Wizard.Decide`). The page
// ladder and the footer table are pinned in SetupTests / SetupWizardTests; these facts pin what the model adds, for each
// entry: FirstRun (a fresh install), Reauth (a completed install with no credential) and TermsRearm (a completed install
// whose terms were re-armed).
//
// Everything here runs engine-free over an in-memory settings store. The facts that would reach the network (a sign-in
// start, a log out) or quit the app are not driven; `Platform.Settings` and the wizard's statics are process state, so the
// class joins the platform collection and restores what it touches.

using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class WizardModelTests : IDisposable
{
    readonly MemoryAppSettings _store = new();
    readonly Setup.RuntimeHost? _host = Setup.Runtime.Host;

    public WizardModelTests()
    {
        Platform.UseSettings(_store);
        Setup.Wizard.ResetForTests();
        Setup.Runtime.Host = null;                 // no provisioning host: the Local playback page's model-null arm
    }

    public void Dispose()
    {
        Setup.Runtime.Host = _host;
        Setup.Wizard.ResetForTests();
        Platform.UseSettings(null);
    }

    static Setup.WizardModel Model(Setup.WizardEntry entry, bool signedIn) => new(entry, signedIn);

    // ── the open, decided in boot ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_armed_means_the_wizard_is_not_due()
    {
        Assert.Null(Setup.Wizard.Decide(_store, signedIn: false));
        Assert.Null(Setup.Wizard.Boot(_store, signedIn: false));
        Assert.Null(Setup.Wizard.Due);
    }

    [Fact]
    public void No_settings_store_means_the_wizard_is_never_due()
        => Assert.Null(Setup.Wizard.Decide(null, signedIn: true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pending_install_that_never_completed_opens_as_a_first_run_signed_in_or_not(bool signedIn)
    {
        _store.Set(Platform.Keys.SetupPending, true);

        Assert.Equal(Setup.WizardEntry.FirstRun, Setup.Wizard.Decide(_store, signedIn));
    }

    [Fact]
    public void A_completed_install_with_no_credential_reauthenticates()
    {
        _store.Set(Platform.Keys.SetupCompleted, true);
        _store.Set(Platform.Keys.SetupPending, true);

        Assert.Equal(Setup.WizardEntry.Reauth, Setup.Wizard.Decide(_store, signedIn: false));
    }

    [Fact]
    public void A_completed_install_that_is_re_armed_while_signed_in_re_accepts_the_terms()
    {
        _store.Set(Platform.Keys.SetupCompleted, true);
        _store.Set(Platform.Keys.SetupPending, true);

        Assert.Equal(Setup.WizardEntry.TermsRearm, Setup.Wizard.Decide(_store, signedIn: true));
    }

    [Fact]
    public void A_completed_install_nobody_re_armed_is_not_due()
    {
        _store.Set(Platform.Keys.SetupCompleted, true);
        _store.Set(Platform.Keys.TermsAcceptedVersion, Setup.Gating.TermsVersion);

        Assert.Null(Setup.Wizard.Decide(_store, signedIn: true));
    }

    [Fact]
    public void Booting_a_due_wizard_builds_the_model_the_chrome_will_open()
    {
        _store.Set(Platform.Keys.SetupPending, true);

        var model = Setup.Wizard.Boot(_store, signedIn: true);

        Assert.NotNull(model);
        Assert.Same(model, Setup.Wizard.Due);
        Assert.Equal(Setup.WizardEntry.FirstRun, model!.Entry);
        Assert.True(model.SkipSignIn);
        Assert.Equal(Setup.WizardPage.Terms, model.Page.Peek());
    }

    // ── the entry decides the start, the skips and the count ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(Setup.WizardEntry.FirstRun, Setup.WizardPage.Terms, false, 3)]
    [InlineData(Setup.WizardEntry.TermsRearm, Setup.WizardPage.Terms, true, 2)]
    [InlineData(Setup.WizardEntry.Reauth, Setup.WizardPage.SignIn, true, 2)]
    public void Each_entry_starts_where_it_should_and_skips_layout_off_a_first_run(
        Setup.WizardEntry entry, Setup.WizardPage start, bool skipLayout, int count)
    {
        var model = Model(entry, signedIn: false);

        Assert.Equal(start, model.Page.Peek());
        Assert.Equal(skipLayout, model.SkipLayout);
        Assert.Equal(count, model.Count);
        Assert.Equal(Design.NavTransitionKind.Neutral, model.Direction.Peek());
    }

    [Theory]
    [InlineData(Setup.WizardEntry.FirstRun, 1)]
    [InlineData(Setup.WizardEntry.Reauth, 1)]
    [InlineData(Setup.WizardEntry.TermsRearm, 2)]
    public void The_open_waits_one_post_except_over_an_already_painted_shell(Setup.WizardEntry entry, int posts)
        => Assert.Equal(posts, Model(entry, signedIn: true).OpenPosts);

    [Fact]
    public void A_first_run_opens_after_one_post()
    {
        var model = Model(Setup.WizardEntry.FirstRun, signedIn: false);
        var queue = new Queue<Action>();
        model.Bind(queue.Enqueue, null);
        int opened = 0;

        model.ScheduleOpen(() => opened++);

        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(1, opened);
        Assert.Empty(queue);
    }

    [Fact]
    public void A_terms_rearm_opens_after_two_posts_so_it_rises_over_a_painted_shell()
    {
        var model = Model(Setup.WizardEntry.TermsRearm, signedIn: true);
        var queue = new Queue<Action>();
        model.Bind(queue.Enqueue, null);
        int opened = 0;

        model.ScheduleOpen(() => opened++);

        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(0, opened);                    // the first post only schedules the second
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(1, opened);
    }

    [Fact]
    public void A_plate_opened_meanwhile_spends_the_launchs_one_open()
    {
        var model = Model(Setup.WizardEntry.FirstRun, signedIn: false);
        var queue = new Queue<Action>();
        model.Bind(queue.Enqueue, null);
        int opened = 0;

        model.ScheduleOpen(() => opened++);
        model.BeginOpen();                          // another path opened it first
        queue.Dequeue()();

        Assert.Equal(0, opened);
    }

    // ── the step facts ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_first_run_numbers_its_steps_out_of_three_and_its_progress_follows_the_page()
    {
        var model = Model(Setup.WizardEntry.FirstRun, signedIn: false);

        Assert.Null(model.StepNumber());                                           // Terms is "Pre-setup"
        Assert.Equal(0f, model.Progress());

        model.Advance(Setup.WizardPage.SignIn);
        Assert.Equal((1, 3), model.StepNumber()!.Value);
        Assert.Equal(1f / 3f, model.Progress(), 5);

        model.Advance(Setup.WizardPage.LocalPlayback);
        Assert.Equal((3, 3), model.StepNumber()!.Value);
        Assert.Equal(1f, model.Progress(), 5);
    }

    [Fact]
    public void A_reauth_numbers_its_steps_out_of_two()
    {
        var model = Model(Setup.WizardEntry.Reauth, signedIn: false);

        Assert.Equal((1, 2), model.StepNumber()!.Value);
        model.Advance(Setup.WizardPage.LocalPlayback);
        Assert.Equal((2, 2), model.StepNumber()!.Value);
    }

    // ── the verbs ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Accepting_the_terms_on_a_first_run_records_consent_then_moves_to_sign_in()
    {
        var model = Model(Setup.WizardEntry.FirstRun, signedIn: false);

        model.Primary();

        Assert.Equal(Setup.Gating.TermsVersion, _store.Get(Platform.Keys.TermsAcceptedVersion));
        Assert.True(_store.Get(Platform.Keys.CrashConsentAsked));                 // the card was shown
        Assert.Equal(Setup.WizardPage.SignIn, model.Page.Peek());
        Assert.Equal(Design.NavTransitionKind.Forward, model.Direction.Peek());
        Assert.False(_store.Get(Platform.Keys.SetupCompleted));                   // a first run is not finished by its terms
    }

    [Fact]
    public void A_first_run_that_is_already_signed_in_skips_the_sign_in_page()
    {
        var model = Model(Setup.WizardEntry.FirstRun, signedIn: true);

        model.Primary();

        Assert.Equal(Setup.WizardPage.Layout, model.Page.Peek());
    }

    [Fact]
    public void Accepting_a_re_armed_terms_finishes_the_wizard_at_once()
    {
        var model = Model(Setup.WizardEntry.TermsRearm, signedIn: true);
        int closed = 0;
        model.RequestClose = () => closed++;
        _store.Set(Platform.Keys.SetupPending, true);

        model.Primary();

        Assert.Equal(Setup.Gating.TermsVersion, _store.Get(Platform.Keys.TermsAcceptedVersion));
        Assert.True(_store.Get(Platform.Keys.CrashConsentAsked));
        Assert.True(_store.Get(Platform.Keys.SetupCompleted));
        Assert.False(_store.Get(Platform.Keys.SetupPending));
        Assert.Equal(1, closed);
        Assert.Equal(Setup.WizardPage.Terms, model.Page.Peek());                   // it never walks on
    }

    [Fact]
    public void Declining_a_re_armed_terms_just_closes_and_writes_nothing()
    {
        var model = Model(Setup.WizardEntry.TermsRearm, signedIn: true);
        int closed = 0;
        model.RequestClose = () => closed++;

        model.Secondary();

        Assert.Equal(1, closed);
        Assert.Equal(0, _store.WrittenCount);
    }

    [Fact]
    public void Back_walks_the_ladder_the_other_way_and_the_direction_says_so()
    {
        var model = Model(Setup.WizardEntry.FirstRun, signedIn: false);
        model.Advance(Setup.WizardPage.Layout);

        model.Back();

        Assert.Equal(Setup.WizardPage.SignIn, model.Page.Peek());
        Assert.Equal(Design.NavTransitionKind.Back, model.Direction.Peek());
    }

    [Fact]
    public void Back_skips_the_sign_in_page_for_someone_already_signed_in()
    {
        var model = Model(Setup.WizardEntry.FirstRun, signedIn: true);
        model.Advance(Setup.WizardPage.Layout);

        model.Back();

        Assert.Equal(Setup.WizardPage.Terms, model.Page.Peek());
    }

    // ── Local playback with no provisioning host ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Without_a_host_the_local_playback_page_has_no_model_and_its_primary_is_disabled()
    {
        var model = Model(Setup.WizardEntry.Reauth, signedIn: true);

        model.Advance(Setup.WizardPage.LocalPlayback);

        Assert.Null(model.Model);
        Assert.Null(model.RuntimePhaseNow());
        Assert.False(model.IsBusy);
        var row = model.Row();
        Assert.False(row.PrimaryEnabled);
        Assert.True(row.SecondaryEnabled);
    }

    [Fact]
    public void Not_now_on_local_playback_is_a_settled_no_and_finishes_the_wizard()
    {
        var model = Model(Setup.WizardEntry.Reauth, signedIn: true);
        int closed = 0;
        model.RequestClose = () => closed++;
        model.Advance(Setup.WizardPage.LocalPlayback);

        model.Secondary();

        Assert.True(_store.Get(Platform.Keys.PlaybackRuntimeSetupDismissed));     // the banner stays quiet
        Assert.True(_store.Get(Platform.Keys.SetupCompleted));
        Assert.Equal(1, closed);
    }

    [Fact]
    public void The_pairing_code_binds_empty_until_a_challenge_is_live()
    {
        var model = Model(Setup.WizardEntry.Reauth, signedIn: false);

        Assert.True(model.PairingCode.IsBound);
        Assert.Equal("", model.PairingCode.Current());
    }

    // ── the markers ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Opening_dims_the_shell_and_owns_sign_in_and_closing_lifts_both_and_bumps_the_epoch()
    {
        var model = Model(Setup.WizardEntry.FirstRun, signedIn: false);
        int epoch = Setup.WizardMarkerEpoch.Peek();

        model.Opened();
        Assert.Equal(Setup.Cover.Dim, Setup.Covering.Peek());
        Assert.True(Setup.WizardOpen.Peek());

        model.Closed();
        Assert.Equal(Setup.Cover.None, Setup.Covering.Peek());
        Assert.False(Setup.WizardOpen.Peek());
        Assert.False(Setup.Runtime.WizardCovering.Peek());
        Assert.Equal(epoch + 1, Setup.WizardMarkerEpoch.Peek());
    }

    // ── the crash-consent verb ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_consent_choice_writes_the_crash_reporting_mode_the_instant_it_changes()
    {
        Setup.WizardModel.SetCrashReporting((int)Crash.Reporting.Ask);

        Assert.Equal((int)Crash.Reporting.Ask, _store.Get(Platform.Keys.CrashReporting));
        Assert.Equal((int)Crash.Reporting.Ask, Setup.WizardModel.CrashReportingChoice(canSend: true));
    }

    [Fact]
    public void A_build_that_cannot_send_seeds_the_effective_mode_never_always_send()
    {
        Setup.WizardModel.SetCrashReporting((int)Crash.Reporting.Auto);

        Assert.Equal((int)Crash.Reporting.Auto, Setup.WizardModel.CrashReportingChoice(canSend: true));
        Assert.Equal((int)Crash.ConsentPolicy.Effective(Crash.Reporting.Auto, configured: false),
            Setup.WizardModel.CrashReportingChoice(canSend: false));
    }
}
