// ── Wavee.ShellTests/SetupMountedTests.cs — the setup screens, mounted (themes plan 6.1, T11c) ──────────────────────────────────────────
//
// The wizard's plate, its footer, its pages, the sign-in door and the playback runtime card are the structure's since T11c; `Setup.WizardModel`, `Setup.RuntimeModel` and the sign-in
// flows keep every decision (`Wavee.Tests` pins the pure ones: `SetupWizardTests`, `SetupTests`, `WizardModelTests`, `RuntimeCardRulesTests`). The views read the sign-in flows only
// through `ISetupSignInHost`, so a fixture host stands in for them:
//
//   registered   Classic is the registered template of every setup key
//   plate        the plate stands under its key, the page stack shows the session's page and Back appears with the pages that have one; the icon column is the window's breakpoint
//   terms        the crash-consent card shows on a first run and not on a re-auth
//   sign-in      the door's body keeps one tree for Idle and the retry facets, a Busy and a Done body each have their own; the wizard's Sign in page keys its body by the facet
//   runtime      the card's command row asks the host whether the diagnostics page exists

using FluentGpu.Dsl;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.ShellTests;

public sealed class SetupMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    /// <summary>The sign-in flows with nothing behind them: a facet and its facts, and a count of what the views asked.</summary>
    sealed class FixtureSignIn(Setup.SignInFacet facet, bool needsCode = false, bool challenge = false) : ISetupSignInHost
    {
        public int PairingStarts, FactsReads;

        public SetupSignInFacts Facts()
        {
            FactsReads++;
            return new SetupSignInFacts(facet, Handed: false, Minting: false, BusyMessageKey: Strings.Auth.WaitingApproval,
                FailureKey: facet == Setup.SignInFacet.Failed ? Setup.SignInRules.LocDenied : "", NeedsPairingCode: needsCode, UserCode: challenge ? "ABCD-1234" : "");
        }

        public SetupPairingFacts Pairing()
            => challenge
                ? new SetupPairingFacts(true, false, false, "ABCD-1234", "https://example.test/pair", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000)
                : new SetupPairingFacts(false, true, false, "", "", 0L);

        public SetupLoginStepFacts Step() => new(1, false);
        public bool NeedsPairingChallenge(Setup.WizardPage activePage) => needsCode && activePage == Setup.WizardPage.SignIn;
        public SetupAccountFacts Account() => new("Fixture Person", null, Premium: true);
        public void StartBrowser() { }
        public void StartPairing() => PairingStarts++;
        public void CancelBrowser() { }
        public void Retry() { }
        public void SwitchAccount() { }
        public void CloseDoor() { }
        public void OpenSignUp() { }
        public void OpenPremium() { }
        public void OpenExternal(string uri) { }
    }

    static SetupConsent Consent() => new(3, static () => 0, static _ => { });

    static SetupWizardProps WizardProps(Setup.WizardModel session, ISetupSignInHost signIn) => new()
    {
        Session = session,
        Terms = new SetupTermsProps { Session = session, OpenPrivacy = static () => { }, Consent = Consent() },
        SignIn = new SetupSignInPageProps { Session = session, Host = signIn },
        Layout = new SetupLayoutPageProps { Cards = static () => new BoxEl { Key = "test:layout-cards" } },
        LocalPlayback = new SetupLocalPlaybackProps { Session = session, OpenSignature = static (_, _) => { }, RevealFolder = static _ => { } },
    };

    [Fact]
    public void Classic_is_the_registered_template_of_every_setup_key()
        => host.Run(() =>
        {
            Assert.Equal("classic.setup.wizard", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupWizard.Id));
            Assert.Equal("classic.setup.terms", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupTerms.Id));
            Assert.Equal("classic.setup.sign-in", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupSignIn.Id));
            Assert.Equal("classic.setup.theme", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupTheme.Id));
            Assert.Equal("classic.setup.local-playback", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupLocalPlayback.Id));
            Assert.Equal("classic.setup.sign-in-door", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupSignInDoor.Id));
            Assert.Equal("classic.setup.runtime-card", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupRuntimeCard.Id));
        });

    // ── the plate ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_plate_shows_the_sessions_page_and_back_comes_with_the_pages_that_have_one()
        => host.Run(() =>
        {
            var session = new Setup.WizardModel(Setup.WizardEntry.FirstRun, signedIn: false);
            try
            {
                using var harness = ViewHarness.Mount(ViewKeys.SetupWizard, WizardProps(session, new FixtureSignIn(Setup.SignInFacet.Idle)), UnderClassic());
                harness.RunFrames(30);
                Assert.True(harness.Contains("setup:chrome"));
                Assert.True(harness.Contains("setup:page:terms"));
                Assert.True(harness.Contains("setup:frame:" + (int)Setup.WizardPage.Terms));
                Assert.False(harness.Contains("setup:back"), "Terms has no Back");

                session.Advance(Setup.WizardPage.Layout);
                harness.RunFrames(60);
                Assert.True(harness.Contains("setup:page:layout"));
                Assert.True(harness.Contains("test:layout-cards"));
                Assert.True(harness.Contains("setup:back"), "the Layout page shows Back");

                session.Advance(Setup.WizardPage.LocalPlayback);
                harness.RunFrames(60);
                Assert.True(harness.Contains("setup:page:local-playback"));
                Assert.True(harness.Contains("setup:frame:" + (int)Setup.WizardPage.LocalPlayback));
                Assert.True(harness.Contains("setup:back"));
            }
            finally { session.End(); }
        });

    [Fact]
    public void The_icon_column_is_the_windows_breakpoint_and_the_compact_frame_has_none()
        => host.Run(() =>
        {
            var session = new Setup.WizardModel(Setup.WizardEntry.FirstRun, signedIn: false);
            try
            {
                using var harness = ViewHarness.Mount(ViewKeys.SetupWizard, WizardProps(session, new FixtureSignIn(Setup.SignInFacet.Idle)), UnderClassic());
                harness.RunFrames(30);
                Assert.True(harness.Contains("setup:layout:wide"), "1180 wide: the icon column shows");

                harness.Resize(600f, 700f);
                harness.RunFrames(30);
                Assert.True(harness.Contains("setup:layout:compact"), "under 770: the compact frame");
                Assert.False(harness.Contains("setup:layout:wide"));
            }
            finally { session.End(); }
        });

    // ── the terms page ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Setup.WizardEntry.FirstRun, true)]
    [InlineData(Setup.WizardEntry.TermsRearm, true)]
    [InlineData(Setup.WizardEntry.Reauth, false)]
    public void The_consent_card_shows_where_the_wizard_hosts_a_terms_page(Setup.WizardEntry entry, bool shown)
        => host.Run(() =>
        {
            var session = new Setup.WizardModel(entry, signedIn: false);
            var props = new SetupTermsProps { Session = session, OpenPrivacy = static () => { }, Consent = Consent() };
            using var harness = ViewHarness.Mount(ViewKeys.SetupTerms, props, UnderClassic());
            harness.RunFrames(20);
            Assert.True(harness.Contains("setup:frame:" + (int)Setup.WizardPage.Terms));
            Assert.Equal(shown, harness.Contains("setup:terms:crash-consent"));
        });

    // ── the sign-in surfaces ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Setup.SignInFacet.Idle, "signin:cards")]
    [InlineData(Setup.SignInFacet.Failed, "signin:cards")]
    [InlineData(Setup.SignInFacet.Expired, "signin:cards")]
    [InlineData(Setup.SignInFacet.Premium, "signin:cards")]
    [InlineData(Setup.SignInFacet.Busy, "signin:busy")]
    [InlineData(Setup.SignInFacet.Done, "signin:done")]
    public void The_door_body_keeps_one_tree_for_the_cards_and_one_for_each_of_busy_and_done(Setup.SignInFacet facet, string key)
        => ViewHarness.With(host, ViewKeys.SetupSignInDoor,
            new SetupSignInDoorProps { Part = SetupDialogPart.Body, Host = new FixtureSignIn(facet) }, harness =>
            {
                harness.RunFrames(20);
                Assert.True(harness.Contains(key));
                if (key == "signin:cards")
                {
                    Assert.True(harness.Contains("signin:head"));
                    Assert.True(harness.Contains("signin:browser"));
                    Assert.True(harness.Contains("signin:pairing"));
                    Assert.True(harness.Contains("signin:premium"));
                }
            }, UnderClassic());

    [Fact]
    public void The_door_asks_for_a_pairing_code_as_it_shows_and_draws_the_live_one_keyed_by_its_code()
    {
        var idle = new FixtureSignIn(Setup.SignInFacet.Idle, needsCode: true);
        ViewHarness.With(host, ViewKeys.SetupSignInDoor, new SetupSignInDoorProps { Part = SetupDialogPart.Body, Host = idle }, harness =>
        {
            harness.RunFrames(20);
            Assert.Equal(1, idle.PairingStarts);
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.SetupSignInDoor,
            new SetupSignInDoorProps { Part = SetupDialogPart.Body, Host = new FixtureSignIn(Setup.SignInFacet.Idle, challenge: true) }, harness =>
            {
                harness.RunFrames(20);
                Assert.True(harness.Contains("qr:ABCD-1234"), "the QR is keyed by the code");
            }, UnderClassic());
    }

    [Fact]
    public void The_door_footer_reads_the_facet()
    {
        var fixture = new FixtureSignIn(Setup.SignInFacet.Failed);
        ViewHarness.With(host, ViewKeys.SetupSignInDoor, new SetupSignInDoorProps { Part = SetupDialogPart.Footer, Host = fixture }, harness =>
        {
            harness.RunFrames(10);
            Assert.True(fixture.FactsReads > 0);
        }, UnderClassic());
    }

    [Theory]
    [InlineData(Setup.SignInFacet.Idle, "signin:Idle")]
    [InlineData(Setup.SignInFacet.Busy, "signin:Busy")]
    [InlineData(Setup.SignInFacet.Done, "signin:Done")]
    [InlineData(Setup.SignInFacet.Failed, "signin:Failed")]
    public void The_wizards_sign_in_page_keys_its_body_by_the_facet(Setup.SignInFacet facet, string key)
        => host.Run(() =>
        {
            var session = new Setup.WizardModel(Setup.WizardEntry.Reauth, signedIn: false);   // a re-auth starts on Sign in
            var props = new SetupSignInPageProps { Session = session, Host = new FixtureSignIn(facet) };
            using var harness = ViewHarness.Mount(ViewKeys.SetupSignIn, props, UnderClassic());
            harness.RunFrames(20);
            Assert.True(harness.Contains("setup:frame:" + (int)Setup.WizardPage.SignIn));
            Assert.True(harness.Contains(key));
            if (facet == Setup.SignInFacet.Busy) Assert.Equal(4, harness.FindPrefix("login-step:").Count);
        });

    // ── the runtime card ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_runtime_cards_command_row_asks_the_host_for_the_diagnostics_page()
    {
        int asked = 0;
        var model = Setup.Runtime.Begin(static a => a());
        try
        {
            var props = new SetupRuntimeCardProps
            {
                Part = SetupDialogPart.Footer, OpenSignature = static (_, _) => { }, HasDiagnosticsPage = () => { asked++; return false; },
            };
            ViewHarness.With(host, ViewKeys.SetupRuntimeCard, props, harness =>
            {
                harness.RunFrames(10);
                Assert.True(asked > 0);
            }, UnderClassic());
        }
        finally { Setup.Runtime.End(model); }
    }
}
