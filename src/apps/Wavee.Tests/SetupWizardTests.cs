// ── Wavee.Tests/SetupWizardTests.cs — the setup wizard's pure rules (WP-6.R) ─────────────────────────────────────────
//
// Ported from 0.2.9's SetupLayoutTests, SetupCommandsTests, SetupSignInPresentationTests and WaveeLottieRecolorTests (the
// sign-in facet fold itself is B5's and lives in SetupTests.SignInRulesTests; only its exhaustiveness is added here), plus
// the 0.3-only rules the wizard's chrome needs: when it opens, which entry and start page, who owns sign-in, the Busy ladder
// in session terms, and the Local playback folds. Pure: no engine loop, no window, no settings store, no Tok writes.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>The Rise reference metrics as arithmetic: the plate clamp, the single 770 breakpoint, the footer split, the
/// 314-of-325 sign-in lane, and the SettingsCard bands of W3b.</summary>
public class SetupWizardLayoutTests
{
    [Theory]
    [InlineData(0f, 320f)]      // no viewport known yet → the floor
    [InlineData(1200f, 762f)]
    [InlineData(900f, 762f)]
    [InlineData(800f, 736f)]    // below 826 (762 + 2×32) the plate shrinks
    [InlineData(300f, 320f)]
    public void Width_clamps_to_the_rise_plate(float viewport, float expected)
        => Assert.Equal(expected, Setup.Layout.Width(viewport));

    [Theory]
    [InlineData(0f, 184f)]
    [InlineData(1000f, 490f)]
    [InlineData(500f, 436f)]
    [InlineData(200f, 184f)]
    public void Height_clamps_to_the_rise_plate(float viewport, float expected)
        => Assert.Equal(expected, Setup.Layout.Height(viewport));

    [Theory]
    [InlineData(769f, false)]
    [InlineData(770f, true)]    // one on/off switch, no hysteresis band
    [InlineData(1200f, true)]
    public void The_icon_column_shows_at_the_rise_breakpoint(float viewport, bool expected)
        => Assert.Equal(expected, Setup.Layout.ShowsIcon(viewport));

    [Theory]
    [InlineData(true, 210f)]
    [InlineData(false, 0f)]
    public void The_progress_column_collapses_with_the_icon(bool large, float expected)
        => Assert.Equal(expected, Setup.Layout.ProgressColumnFor(large));

    [Theory]
    [InlineData(762f, true, 246f)]    // (762 − 48 − 210 − 12) / 2
    [InlineData(762f, false, 351f)]
    [InlineData(706f, true, 218f)]    // viewport 770: the icon column on a shrunken plate
    [InlineData(636f, false, 288f)]   // viewport 700
    public void The_footer_splits_the_remainder_equally(float plateW, bool large, float expected)
        => Assert.Equal(expected, Setup.Layout.FooterButtonWidth(plateW, large));

    [Fact]
    public void A_shell_behind_the_plate_is_dimmed_and_nothing_else_is()
    {
        Assert.Equal(Setup.Cover.Dim, Setup.Layout.CoverFor(shellBehind: true));
        Assert.Equal(Setup.Cover.None, Setup.Layout.CoverFor(shellBehind: false));
    }

    // 490 − 80 footer − 1 separator − 48 padding − 36 title = 325
    [Fact]
    public void The_body_lane_at_the_reference_plate_is_325()
        => Assert.Equal(325f, Setup.Layout.BodyLaneHeight(Setup.Layout.PlateHeight));

    // 40 + 20 + 68 + 20 + (82 + 32) + 20 + 32 = 314 ≤ 325 — the old 96-DIP QR and two stacked link rows summed to 388.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void The_sign_in_idle_body_fits_the_reference_lane(int leadLines)
        => Assert.True(Setup.Layout.SignInIdleBodyHeight(leadLines) <= Setup.Layout.BodyLaneHeight(Setup.Layout.PlateHeight),
            $"{Setup.Layout.SignInIdleBodyHeight(leadLines)} > {Setup.Layout.BodyLaneHeight(Setup.Layout.PlateHeight)}");

    [Fact]
    public void A_two_line_lead_sums_to_314_and_a_three_line_lead_overflows_so_the_scrollbar_must_show()
    {
        Assert.Equal(314f, Setup.Layout.SignInIdleBodyHeight(2));
        Assert.True(Setup.Layout.SignInIdleBodyHeight(3) > Setup.Layout.BodyLaneHeight(Setup.Layout.PlateHeight) - Setup.Layout.BodySpacing);
    }

    /// <summary>The budget charges the PAINTED v4 symbol at 80 DIP (82), never the request.</summary>
    [Fact]
    public void The_qr_budget_is_the_v4_symbol_painted_at_80()
        => Assert.Equal(82f, Setup.Layout.QrPlateBudget);

    /// <summary>W3b: the scan card's QR wraps below its header while the card is narrower than SettingsCard's 476, and every
    /// card drops its icon under 286 — two bands inside the wizard's own clamp ladder.</summary>
    [Theory]
    [InlineData(770f, 442f)]
    [InlineData(803f, 475f)]
    [InlineData(804f, 476f)]
    [InlineData(587f, 475f)]
    [InlineData(588f, 476f)]
    [InlineData(397f, 285f)]
    [InlineData(398f, 286f)]
    public void The_card_width_bands_land_on_the_settings_card_thresholds(float viewport, float expected)
        => Assert.Equal(expected, Setup.Layout.CardWidth(viewport));

    [Fact]
    public void The_thresholds_the_bands_are_measured_against_are_the_engine_cards()
    {
        Assert.True(Setup.Layout.CardWidth(803f) < FluentGpu.Controls.SettingsCard.WrapThreshold);
        Assert.False(Setup.Layout.CardWidth(804f) < FluentGpu.Controls.SettingsCard.WrapThreshold);
        Assert.True(Setup.Layout.CardWidth(397f) < FluentGpu.Controls.SettingsCard.WrapNoIconThreshold);
        Assert.False(Setup.Layout.CardWidth(398f) < FluentGpu.Controls.SettingsCard.WrapNoIconThreshold);
    }

    [Fact]
    public void The_back_spacer_applies_only_without_the_icon_column_on_the_page_that_shows_back()
    {
        Assert.True(Setup.Layout.BackSpacerApplies(Setup.WizardPage.LocalPlayback, iconShown: false));
        Assert.False(Setup.Layout.BackSpacerApplies(Setup.WizardPage.LocalPlayback, iconShown: true));
        Assert.False(Setup.Layout.BackSpacerApplies(Setup.WizardPage.Terms, iconShown: false));
        Assert.False(Setup.Layout.BackSpacerApplies(Setup.WizardPage.SignIn, iconShown: false));
    }

    [Fact]
    public void Each_page_plays_its_own_oobe_scene()
    {
        Assert.Equal("eula", Setup.Layout.HeroAsset(Setup.WizardPage.Terms));
        Assert.Equal("connect", Setup.Layout.HeroAsset(Setup.WizardPage.SignIn));
        Assert.Equal("patch", Setup.Layout.HeroAsset(Setup.WizardPage.LocalPlayback));
    }
}

/// <summary>The wizard's whole footer table and its folds.</summary>
public class SetupWizardCommandsTests
{
    static Setup.WizardCtx Terms() => new(Setup.WizardPage.Terms, default, default);
    static Setup.WizardCtx SignIn(Setup.SignInFacet f) => new(Setup.WizardPage.SignIn, f, default);
    static Setup.WizardCtx Runtime(Setup.RuntimeFacet f) => new(Setup.WizardPage.LocalPlayback, default, f);

    static IEnumerable<Setup.WizardCtx> AllCtxs()
    {
        yield return Terms();
        foreach (Setup.SignInFacet f in Enum.GetValues<Setup.SignInFacet>()) yield return SignIn(f);
        foreach (Setup.RuntimeFacet f in Enum.GetValues<Setup.RuntimeFacet>()) yield return Runtime(f);
    }

    [Fact]
    public void Resolve_matches_the_approved_table()
    {
        (Setup.WizardCtx Ctx, string? P, bool PEnabled, string? S, bool SEnabled, bool Blocks)[] rows =
        [
            (Terms(), Strings.Setup.Accept, true, Strings.Setup.Decline, true, false),

            (SignIn(Setup.SignInFacet.Idle), Strings.Auth.LogIn, true, Strings.Auth.Close, true, false),
            (SignIn(Setup.SignInFacet.Busy), Strings.Auth.SigningIn, false, Strings.Auth.Cancel, true, false),
            (SignIn(Setup.SignInFacet.Done), Strings.Setup.SignIn.YesContinue, true, Strings.Setup.SignIn.NotMe, true, false),
            (SignIn(Setup.SignInFacet.Failed), Strings.Auth.TryAgain, true, Strings.Auth.Close, true, false),
            (SignIn(Setup.SignInFacet.Expired), Strings.Auth.GetNewCode, true, Strings.Auth.Close, true, false),
            (SignIn(Setup.SignInFacet.Premium), Strings.Auth.Upgrade, true, Strings.Auth.UseAnotherAccount, true, false),

            (Runtime(Setup.RuntimeFacet.Offer), Strings.Playback.Runtime.DownloadSetup, true, Strings.Playback.Runtime.NotNow, true, false),
            (Runtime(Setup.RuntimeFacet.Catalog), Strings.Playback.Runtime.Checking, false, Strings.Auth.Cancel, true, true),
            (Runtime(Setup.RuntimeFacet.Versions), Strings.Playback.Runtime.Install, true, Strings.Playback.Runtime.Back, true, false),
            (Runtime(Setup.RuntimeFacet.Downloading), Strings.Playback.Runtime.Downloading, false, Strings.Auth.Cancel, true, true),
            (Runtime(Setup.RuntimeFacet.Verifying), Strings.Playback.Runtime.Verifying, false, null, false, true),
            (Runtime(Setup.RuntimeFacet.Untrusted), Strings.Playback.Runtime.LoadAnyway, true, Strings.Playback.Runtime.Back, true, false),
            (Runtime(Setup.RuntimeFacet.Ready), Strings.Setup.OpenWavee, true, null, false, false),
            (Runtime(Setup.RuntimeFacet.Failed), Strings.Playback.Runtime.TryAgain, true, Strings.Playback.Runtime.NotNow, true, false),
        ];

        foreach (var r in rows)
        {
            // One record compare per row: a mismatch prints both whole rows, which names the row.
            var expected = new Setup.CommandRow(r.P, r.S, Setup.ButtonKind.Accent, r.PEnabled, r.SEnabled, r.Blocks,
                r.Ctx.Page == Setup.WizardPage.LocalPlayback);
            Assert.Equal(expected, Setup.Commands.Resolve(r.Ctx));
        }
    }

    [Fact]
    public void Back_is_page_based_local_playback_only_whatever_the_runtime_facet()
    {
        Assert.False(Setup.Commands.Resolve(Terms()).ShowBack);
        Assert.False(Setup.Commands.Resolve(SignIn(Setup.SignInFacet.Idle)).ShowBack);
        Assert.True(Setup.Commands.Resolve(Runtime(Setup.RuntimeFacet.Offer)).ShowBack);
        Assert.True(Setup.Commands.Resolve(Runtime(Setup.RuntimeFacet.Catalog)).ShowBack);
    }

    [Fact]
    public void The_primary_is_always_offered_and_disabled_where_it_cannot_act()
    {
        foreach (var ctx in AllCtxs())
        {
            var row = Setup.Commands.Resolve(ctx);
            Assert.NotNull(row.PrimaryKey);   // a row without an action is a disabled key, never a missing one
            if (ctx.Page == Setup.WizardPage.LocalPlayback && ctx.Runtime == Setup.RuntimeFacet.Verifying) Assert.False(row.PrimaryEnabled);
        }
    }

    [Fact]
    public void Blocks_dismiss_is_exactly_the_three_network_steps()
    {
        foreach (var ctx in AllCtxs())
        {
            bool expected = ctx.Page == Setup.WizardPage.LocalPlayback
                            && ctx.Runtime is Setup.RuntimeFacet.Catalog or Setup.RuntimeFacet.Downloading or Setup.RuntimeFacet.Verifying;
            Assert.Equal(expected, Setup.Commands.Resolve(ctx).BlocksDismiss);
        }
    }

    /// <summary>Every emitted key is a generated `Strings.*` constant (so the compiler already proves it exists in en-US.json);
    /// what is left to pin is that none is blank.</summary>
    [Fact]
    public void Every_emitted_key_is_a_dotted_loc_key()
    {
        foreach (var ctx in AllCtxs())
        {
            var row = Setup.Commands.Resolve(ctx);
            Assert.Contains(".", row.PrimaryKey!);
            if (row.SecondaryKey is { } s) Assert.Contains(".", s);
        }
    }

    [Fact]
    public void Without_a_provisioning_model_local_playback_offers_its_primary_disabled_and_nothing_else_changes()
    {
        foreach (var ctx in AllCtxs())
        {
            var plain = Setup.Commands.Resolve(ctx);
            var hostless = Setup.Commands.ResolveFor(ctx, runtimeActive: false);
            Assert.Equal(plain, Setup.Commands.ResolveFor(ctx, runtimeActive: true));
            if (ctx.Page == Setup.WizardPage.LocalPlayback)
            {
                Assert.False(hostless.PrimaryEnabled);
                Assert.Equal(plain with { PrimaryEnabled = false }, hostless);
            }
            else Assert.Equal(plain, hostless);
        }
        // "Not now" still finishes a hostless wizard.
        Assert.True(Setup.Commands.ResolveFor(Runtime(Setup.RuntimeFacet.Offer), runtimeActive: false).SecondaryEnabled);
    }

    [Theory]
    [InlineData(RuntimePhase.Offer, Setup.RuntimeFacet.Offer)]
    [InlineData(RuntimePhase.FetchingCatalog, Setup.RuntimeFacet.Catalog)]
    [InlineData(RuntimePhase.Downloading, Setup.RuntimeFacet.Downloading)]
    [InlineData(RuntimePhase.Verifying, Setup.RuntimeFacet.Verifying)]
    [InlineData(RuntimePhase.Untrusted, Setup.RuntimeFacet.Untrusted)]
    [InlineData(RuntimePhase.Ready, Setup.RuntimeFacet.Ready)]
    [InlineData(RuntimePhase.Failed, Setup.RuntimeFacet.Failed)]
    [InlineData(RuntimePhase.Advanced, Setup.RuntimeFacet.Versions)]
    public void Every_runtime_phase_folds_to_its_footer_facet(RuntimePhase phase, Setup.RuntimeFacet expected)
        => Assert.Equal(expected, Setup.Commands.FacetFor(phase));

    [Fact]
    public void The_page_header_and_lead_swap_per_runtime_phase()
    {
        Assert.Equal(Strings.Playback.Runtime.SignatureInvalid, Setup.Commands.RuntimeHeaderKey(RuntimePhase.Untrusted));
        Assert.Equal(Strings.Playback.Runtime.Ready, Setup.Commands.RuntimeHeaderKey(RuntimePhase.Ready));
        Assert.Equal(Strings.Playback.Runtime.ChooseVersion, Setup.Commands.RuntimeHeaderKey(RuntimePhase.Advanced));
        Assert.Equal(Strings.Setup.LocalPlayback.Header, Setup.Commands.RuntimeHeaderKey(RuntimePhase.Downloading));
        Assert.Equal(Strings.Setup.LocalPlayback.Header, Setup.Commands.RuntimeHeaderKey(null));
        Assert.Equal(Strings.Setup.LocalPlayback.ReadyLead, Setup.Commands.RuntimeLeadKey(RuntimePhase.Ready));
        Assert.Equal(Strings.Setup.LocalPlayback.Lead, Setup.Commands.RuntimeLeadKey(RuntimePhase.Offer));
        Assert.Equal(Strings.Setup.LocalPlayback.Lead, Setup.Commands.RuntimeLeadKey(null));
    }

    [Fact]
    public void The_sign_in_page_mints_one_code_only_while_it_is_active_and_nothing_is_out()
    {
        var idle = Spotify.SignInState.Idle;
        Assert.True(Setup.Commands.NeedsPairingChallenge(Setup.WizardPage.SignIn, Setup.SignInFacet.Idle, idle));
        Assert.False(Setup.Commands.NeedsPairingChallenge(Setup.WizardPage.Terms, Setup.SignInFacet.Idle, idle));
        Assert.False(Setup.Commands.NeedsPairingChallenge(Setup.WizardPage.LocalPlayback, Setup.SignInFacet.Idle, idle));
        Assert.False(Setup.Commands.NeedsPairingChallenge(Setup.WizardPage.SignIn, Setup.SignInFacet.Done, idle));
        Assert.False(Setup.Commands.NeedsPairingChallenge(Setup.WizardPage.SignIn, Setup.SignInFacet.Idle,
            idle with { Code = Spotify.SignInStage.Waiting, UserCode = "ABCD" }));
        Assert.False(Setup.Commands.NeedsPairingChallenge(Setup.WizardPage.SignIn, Setup.SignInFacet.Expired,
            idle with { Code = Spotify.SignInStage.Expired }));
    }

    [Fact]
    public void The_busy_ladder_waits_on_its_first_rung_until_the_hand_off_then_walks_with_the_session()
    {
        Assert.Equal(0, Setup.Commands.LoginStep(handed: false, Spotify.SessionPhase.Minting));
        Assert.Equal(0, Setup.Commands.LoginStep(handed: true, Spotify.SessionPhase.Resolving));
        Assert.Equal(1, Setup.Commands.LoginStep(handed: true, Spotify.SessionPhase.Handshaking));
        Assert.Equal(2, Setup.Commands.LoginStep(handed: true, Spotify.SessionPhase.Authenticating));
        Assert.Equal(3, Setup.Commands.LoginStep(handed: true, Spotify.SessionPhase.Minting));
        Assert.Equal(Setup.Commands.LoginSteps, Setup.Commands.LoginStep(handed: true, Spotify.SessionPhase.Online));

        Spotify.SessionPhase[] order =
        [
            Spotify.SessionPhase.Resolving, Spotify.SessionPhase.Connecting, Spotify.SessionPhase.Handshaking,
            Spotify.SessionPhase.Authenticating, Spotify.SessionPhase.Minting, Spotify.SessionPhase.Online,
        ];
        for (int i = 1; i < order.Length; i++)
            Assert.True(Setup.Commands.LoginStep(true, order[i]) >= Setup.Commands.LoginStep(true, order[i - 1]));

        Assert.Equal(Strings.Auth.StepConnecting, Setup.Commands.LoginStepKey(0));
        Assert.Equal(Strings.Auth.StepMetadata, Setup.Commands.LoginStepKey(1));
        Assert.Equal(Strings.Auth.StepAudio, Setup.Commands.LoginStepKey(2));
        Assert.Equal(Strings.Auth.StepProfile, Setup.Commands.LoginStepKey(3));
    }

    [Fact]
    public void The_busy_message_says_getting_your_code_only_while_nothing_is_out_yet()
    {
        var idle = Spotify.SignInState.Idle;
        Assert.Equal(Strings.Auth.GettingCode,
            Setup.Commands.BusyMessageKey(idle.With(Spotify.SignInMethod.Browser, Spotify.SignInStage.Starting)));
        Assert.Equal(Strings.Auth.WaitingApproval,
            Setup.Commands.BusyMessageKey(idle.With(Spotify.SignInMethod.Browser, Spotify.SignInStage.Waiting)));
        Assert.Equal(Strings.Auth.WaitingApproval, Setup.Commands.BusyMessageKey(idle with { Handed = true }));
    }

    [Fact]
    public void Every_sign_in_state_folds_to_a_defined_facet()
    {
        foreach (Spotify.SignInStage browser in Enum.GetValues<Spotify.SignInStage>())
        foreach (Spotify.SignInStage code in Enum.GetValues<Spotify.SignInStage>())
        foreach (Spotify.SessionPhase phase in Enum.GetValues<Spotify.SessionPhase>())
        foreach (Spotify.SessionFault fault in Enum.GetValues<Spotify.SessionFault>())
        foreach (bool handed in new[] { false, true })
        {
            var st = Spotify.SignInState.Idle with { Browser = browser, Code = code, Handed = handed };
            Assert.True(Enum.IsDefined(Setup.SignInRules.Project(st, phase, fault)));
        }
    }
}

/// <summary>When the wizard opens, as what, and who owns sign-in meanwhile.</summary>
public class SetupWizardRulesTests
{
    [Theory]
    [InlineData(true, false, 0, true)]     // armed first run
    [InlineData(true, true, 1, true)]      // armed re-arm
    [InlineData(false, true, 1, false)]    // a finished install on the current terms
    [InlineData(false, true, 0, false)]    // grandfathered: stamped by the bootstrap, never re-shown
    [InlineData(false, false, 0, false)]   // deferred / never armed
    public void The_wizard_opens_when_armed_or_a_terms_rearm_is_due(bool pending, bool completed, int accepted, bool expected)
        => Assert.Equal(expected, Setup.WizardRules.ShouldOpen(pending, completed, accepted));

    [Theory]
    [InlineData(false, false, Setup.WizardEntry.FirstRun, Setup.WizardPage.Terms)]
    [InlineData(false, true, Setup.WizardEntry.FirstRun, Setup.WizardPage.Terms)]
    [InlineData(true, true, Setup.WizardEntry.TermsRearm, Setup.WizardPage.Terms)]
    [InlineData(true, false, Setup.WizardEntry.Reauth, Setup.WizardPage.SignIn)]   // re-walking accepted terms is nonsense
    public void The_entry_and_its_start_page(bool completed, bool signedIn, Setup.WizardEntry entry, Setup.WizardPage start)
    {
        Assert.Equal(entry, Setup.WizardRules.EntryFor(completed, signedIn));
        Assert.Equal(start, Setup.WizardRules.StartPage(entry));
    }

    [Fact]
    public void A_first_run_rises_with_the_window_and_a_rearm_over_a_painted_shell()
    {
        Assert.Equal(1, Setup.WizardRules.OpenPosts(Setup.WizardEntry.FirstRun));
        Assert.Equal(1, Setup.WizardRules.OpenPosts(Setup.WizardEntry.Reauth));
        Assert.Equal(2, Setup.WizardRules.OpenPosts(Setup.WizardEntry.TermsRearm));
    }

    [Theory]
    [InlineData(false, true, true, true)]     // the plate is up
    [InlineData(true, false, false, true)]    // armed, about to open
    [InlineData(true, false, true, false)]    // shown and closed with the marker still armed: the door must work again
    [InlineData(false, false, false, false)]  // nothing pending: the door owns sign-in
    public void The_wizard_and_the_sign_in_door_never_both_own_sign_in(bool pending, bool open, bool shown, bool expected)
        => Assert.Equal(expected, Setup.WizardRules.OwnsSignIn(pending, open, shown));

    [Theory]
    [InlineData(Setup.WizardEntry.TermsRearm, false, false, true)]
    [InlineData(Setup.WizardEntry.TermsRearm, true, false, false)]    // never under a running download
    [InlineData(Setup.WizardEntry.TermsRearm, false, true, false)]    // a nested popup closes first
    [InlineData(Setup.WizardEntry.FirstRun, false, false, false)]     // Escape cannot strand a first run
    [InlineData(Setup.WizardEntry.Reauth, false, false, false)]
    public void Escape_closes_only_an_idle_terms_rearm(Setup.WizardEntry entry, bool busy, bool nested, bool expected)
        => Assert.Equal(expected, Setup.WizardRules.EscapeClosesPlate(nested, entry, busy));

    [Fact]
    public void Page_swaps_travel_forward_back_or_not_at_all()
    {
        Assert.Equal(Design.NavTransitionKind.Forward, Setup.WizardRules.DirectionFor(Setup.WizardPage.Terms, Setup.WizardPage.SignIn));
        Assert.Equal(Design.NavTransitionKind.Back, Setup.WizardRules.DirectionFor(Setup.WizardPage.LocalPlayback, Setup.WizardPage.SignIn));
        Assert.Equal(Design.NavTransitionKind.Neutral, Setup.WizardRules.DirectionFor(Setup.WizardPage.SignIn, Setup.WizardPage.SignIn));
    }

    [Fact]
    public void Only_a_terms_rearm_retitles_the_terms_page()
    {
        Assert.Equal(Strings.Setup.Terms.UpdatedTitle, Setup.WizardRules.TermsHeaderKey(Setup.WizardEntry.TermsRearm));
        Assert.Equal(Strings.Setup.Terms.Header, Setup.WizardRules.TermsHeaderKey(Setup.WizardEntry.FirstRun));
        Assert.Equal(Strings.Setup.Terms.Header, Setup.WizardRules.TermsHeaderKey(Setup.WizardEntry.Reauth));
    }

    [Fact]
    public void A_signed_in_walk_from_terms_never_reaches_sign_in()
    {
        var page = Setup.WizardPage.Terms;
        bool skip = Setup.Gating.SkipSignIn(authed: true);
        while (page != Setup.WizardPage.LocalPlayback)
        {
            page = Setup.Gating.NextPage(page, skip);
            Assert.NotEqual(Setup.WizardPage.SignIn, page);
        }
    }
}

/// <summary>Which facets keep the two option cards, the "raw Spotify id" name fix, and the spelled-out code.</summary>
public class SetupWizardSignInPresentationTests
{
    [Theory]
    [InlineData(Setup.SignInFacet.Idle, true)]
    [InlineData(Setup.SignInFacet.Busy, false)]
    [InlineData(Setup.SignInFacet.Done, false)]
    [InlineData(Setup.SignInFacet.Failed, true)]     // retries in place, under the error InfoBar
    [InlineData(Setup.SignInFacet.Expired, true)]
    [InlineData(Setup.SignInFacet.Premium, true)]
    public void The_idle_cards_stay_for_idle_and_the_three_retry_facets(Setup.SignInFacet facet, bool expected)
        => Assert.Equal(expected, Setup.SignInPresentation.ShowsIdleCards(facet));

    static Setup.AccountName User(string id, string name) => new(id, name);

    [Fact]
    public void The_live_name_wins_even_with_a_good_snapshot()
        => Assert.Equal("Christos", Setup.SignInPresentation.DisplayNameFor(User("31unjfmo", "Christos"), User("31unjfmo", "Snapshot Name")));

    [Fact]
    public void An_id_standing_in_for_the_live_name_falls_back_to_the_snapshot()
        => Assert.Equal("Christos", Setup.SignInPresentation.DisplayNameFor(User("31unjfmo", "31unjfmo"), User("31unjfmo", "Christos")));

    [Fact]
    public void No_live_account_falls_back_to_the_snapshot()
        => Assert.Equal("Christos", Setup.SignInPresentation.DisplayNameFor(null, User("31unjfmo", "Christos")));

    [Fact]
    public void Neither_source_carrying_a_real_name_is_null()
    {
        Assert.Null(Setup.SignInPresentation.DisplayNameFor(User("31unjfmo", "31unjfmo"), User("31unjfmo", "31unjfmo")));
        Assert.Null(Setup.SignInPresentation.DisplayNameFor(null, null));
        Assert.Null(Setup.SignInPresentation.DisplayNameFor(User("31unjfmo", ""), null));   // the row before its identity lands
    }

    [Theory]
    [InlineData("WZY5-Q6TX", "W Z Y 5 Q 6 T X")]
    [InlineData("ABCD", "A B C D")]
    [InlineData("", "")]
    public void The_pairing_code_is_announced_one_character_at_a_time(string code, string expected)
        => Assert.Equal(expected, Setup.SignInPresentation.SpellCode(code));
}

/// <summary>The Lottie hero recolour against a deliberately un-accent-like green pair, so a rule that left a source colour
/// unchanged still fails. Reads no live Tok state.</summary>
public class SetupWizardRecolorTests
{
    static readonly ColorF Accent = ColorF.FromRgba(0x1E, 0xB9, 0x5A);
    static readonly ColorF AccentDeep = ColorF.FromRgba(0x0B, 0x5C, 0x28);

    [Fact]
    public void The_exact_source_accent_maps_to_the_accent_with_the_sources_alpha()
    {
        var c = ColorF.FromRgba(0x00, 0x78, 0xD4, 0x80);
        var got = Setup.Recolor.Apply(c, Accent, AccentDeep);
        Assert.Equal(Accent.R, got.R, 3);
        Assert.Equal(Accent.G, got.G, 3);
        Assert.Equal(Accent.B, got.B, 3);
        Assert.Equal(c.A, got.A, 3);
    }

    [Fact]
    public void The_exact_navy_maps_to_the_deep_accent_with_the_sources_alpha()
    {
        var c = ColorF.FromRgba(0x00, 0x2B, 0x67, 0x40);
        var got = Setup.Recolor.Apply(c, Accent, AccentDeep);
        Assert.Equal(AccentDeep.R, got.R, 3);
        Assert.Equal(AccentDeep.G, got.G, 3);
        Assert.Equal(AccentDeep.B, got.B, 3);
        Assert.Equal(c.A, got.A, 3);
    }

    [Fact]
    public void The_hue_band_keeps_saturation_and_lightness_but_rotates_the_hue()
    {
        var c = ColorF.FromRgba(0x2E, 0x5D, 0xD3);
        var (h, s, l) = Design.Palette.ToHsl(c);
        Assert.InRange(h, 195f, 285f);
        Assert.True(s >= 0.35f);

        var (gh, gs, gl) = Design.Palette.ToHsl(Setup.Recolor.Apply(c, Accent, AccentDeep));
        Assert.Equal(s, gs, 2);
        Assert.Equal(l, gl, 2);
        Assert.NotEqual(h, gh, 1);
    }

    [Fact]
    public void Neutrals_are_untouched()
    {
        foreach (var c in new[]
                 {
                     ColorF.FromRgba(0xFF, 0xFF, 0xFF), ColorF.FromRgba(0xEE, 0xEE, 0xEE), ColorF.FromRgba(0xF1, 0xF0, 0xEF),
                     ColorF.FromRgba(0xE0, 0xDE, 0xDC), ColorF.FromRgba(0, 0, 0),
                 })
            Assert.Equal(c, Setup.Recolor.Apply(c, Accent, AccentDeep));
    }

    [Fact]
    public void The_teal_outside_the_hue_band_is_untouched()
    {
        var teal = ColorF.FromRgba(0x96, 0xE9, 0xDC);
        var (h, s, _) = Design.Palette.ToHsl(teal);
        Assert.True(h < 195f || s < 0.35f);
        Assert.Equal(teal, Setup.Recolor.Apply(teal, Accent, AccentDeep));
    }

    [Fact]
    public void Alpha_is_always_the_shapes_own()
    {
        var exact = ColorF.FromRgba(0x00, 0x78, 0xD4, 0x33);
        Assert.Equal(exact.A, Setup.Recolor.Apply(exact, Accent, AccentDeep).A);
        var band = ColorF.FromRgba(0x2E, 0x5D, 0xD3, 0x99);
        Assert.Equal(band.A, Setup.Recolor.Apply(band, Accent, AccentDeep).A);
        var neutral = ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xCC);
        Assert.Equal(neutral.A, Setup.Recolor.Apply(neutral, Accent, AccentDeep).A);
    }
}
