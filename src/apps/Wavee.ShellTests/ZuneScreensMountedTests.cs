// ── Wavee.ShellTests/ZuneScreensMountedTests.cs — Zune's screens, mounted (themes plan 6.3, P5 Z8b) ──────────────────────────────────────────────
//
// Settings, the theme picker and options, the setup wizard, the release notes, the feedback and crash dialogs are Zune's own since Z8b. What only a mounted tree can say:
//
//   registered   Zune is the registered template of every screen key (its registrations win over the Classic stand-in key by key)
//   settings     under Zune at 1440 x 960 the page is Zune's (the panorama title, the section list, the open tab's lane); Appearance shows the picker and Zune's options
//   accent       a swatch pick is live: the page stays mounted, no frame freezes, and the accent roles read the new swatch
//   setup        the plate, its accent column and its pages mount without an exception, Back comes with the pages that have one
//   release      the What's-new page (loading, empty, loaded), the viewer and the after-update plate mount
//   dialogs      the report and crash dialogs' bodies mount
//
// The decisions (the document, the gates, the models) are the core's and have their own tests; the tier records are pinned in `Wavee.Tests`. The installed themes, `theme.id` and
// Developer mode are process state: every fact puts Classic back alone (`ZuneHost`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Wavee.Themes.Zune;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZuneScreensMountedTests(ShellHost host)
{
    /// <summary>Zune's composition, rendered alone by the harness (the real frame is the Zune host's).</summary>
    static ViewHarnessOptions UnderZune(float width = 1440f, float height = 960f) => new() { Composition = ZuneTheme.Create(), WidthDip = width, HeightDip = height };

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_is_the_registered_template_of_every_screen_key()
        => host.Run(() =>
        {
            ZuneHost.Activate();
            try
            {
                Assert.Equal("zune.screens.settings", ThemeHost.Registry.TemplateIdOf(ViewKeys.SettingsPage.Id));
                Assert.Equal("zune.screens.settings.theme-picker", ThemeHost.Registry.TemplateIdOf(ViewKeys.SettingsThemePicker.Id));
                Assert.Equal("zune.screens.settings.theme-options", ThemeHost.Registry.TemplateIdOf(ViewKeys.SettingsThemeOptions.Id));
                Assert.Equal("zune.setup.wizard", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupWizard.Id));
                Assert.Equal("zune.setup.terms", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupTerms.Id));
                Assert.Equal("zune.setup.sign-in", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupSignIn.Id));
                Assert.Equal("zune.setup.theme", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupTheme.Id));
                Assert.Equal("zune.setup.local-playback", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupLocalPlayback.Id));
                Assert.Equal("zune.setup.sign-in-door", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupSignInDoor.Id));
                Assert.Equal("zune.setup.runtime-card", ThemeHost.Registry.TemplateIdOf(ViewKeys.SetupRuntimeCard.Id));
                Assert.Equal("zune.release-notes.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.WhatsNewPage.Id));
                Assert.Equal("zune.release-notes.viewer", ThemeHost.Registry.TemplateIdOf(ViewKeys.ReleaseNotesViewer.Id));
                Assert.Equal("zune.release-notes.after-update-plate", ThemeHost.Registry.TemplateIdOf(ViewKeys.ReleaseNotesAfterUpdatePlate.Id));
                Assert.Equal("zune.dialog.feedback", ThemeHost.Registry.TemplateIdOf(ViewKeys.DialogFeedback.Id));
                Assert.Equal("zune.dialog.crash", ThemeHost.Registry.TemplateIdOf(ViewKeys.DialogCrash.Id));
            }
            finally { ZuneHost.Restore(); }
        });

    // ── settings ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Settings_renders_Zunes_page_with_its_title_its_section_list_and_the_open_tabs_lane()
        => ZuneHost.Mounted(host, shell =>
        {
            Assert.Equal("zune.screens.settings", ThemeHost.Registry.TemplateIdOf(ViewKeys.SettingsPage.Id));
            Settings.Open(Settings.Tab.Playback);
            shell.RunFrames(60);

            Node(shell, "zs:title");
            Node(shell, "zs:sections");
            Node(shell, "zs:scroll:playback");
            // One word per tab, the open one included.
            foreach (string slug in Settings.TabSlugs) Node(shell, "zs:link:" + slug);
            // The other tabs' lanes are not mounted once the cross-fade settles: only the lane is keyed per tab.
            foreach (string other in Settings.TabSlugs)
                if (other != "playback") Assert.True(shell.Find("zs:scroll:" + other).IsNull, "the '" + other + "' lane is mounted beside 'playback'");
            // Classic's page is not on screen.
            Assert.True(shell.Find("settings:title").IsNull, "Classic's Settings title is mounted under Zune");
        }, 1440f, 960f);

    [Fact]
    public void Appearance_shows_the_theme_picker_and_Zunes_options()
        => ZuneHost.Mounted(host, shell =>
        {
            Settings.Open(Settings.Tab.Appearance);
            shell.RunFrames(90);

            Node(shell, "zs:scroll:appearance");
            Node(shell, "zs:picker");
            Node(shell, "zs:picker:card:zune");
            Node(shell, "zs:picker:card:classic");
            Node(shell, "zs:options:rows");
            // Zune's own options, drawn from the core's option model: the accent, the background, the pins.
            Node(shell, "opt:style.accent");
            Node(shell, "opt:structure.background");
            Node(shell, "opt:structure.bandPins");
        }, 1440f, 960f);

    [Fact]
    public void An_accent_pick_is_live_while_the_options_are_on_screen()
        => ZuneHost.Mounted(host, shell =>
        {
            Settings.Open(Settings.Tab.Appearance);
            shell.RunFrames(90);
            Node(shell, "opt:style.accent");

            var t = ThemeHost.Apply(new OptionChange(ZuneTheme.Id, "style.accent", ZuneAccent.Blue.Dark));
            for (int i = 0; i < 30; i++)
            {
                Assert.False(shell.Host.IsThemeTransactionRunning, "a Live option started a theme transaction");
                Assert.Equal(FreezePhase.None, shell.Host.FreezeState);
                shell.RunFrames(1);
            }
            Assert.True(t.IsCompletedSuccessfully);
            var blue = Tok.Theme == ThemeKind.Light ? ZuneAccent.Blue.Light : ZuneAccent.Blue.Dark;
            Assert.Equal(blue, R.Ink.AccentAction);
            // The page stayed mounted: the swatch row, the picker and the lane are the same tree.
            Node(shell, "zs:page");
            Node(shell, "zs:picker");
            Node(shell, "opt:style.accent");
        }, 1440f, 960f);

    [Theory]
    [InlineData(300f, 700f)]
    [InlineData(640f, 800f)]
    [InlineData(1008f, 800f)]
    [InlineData(1920f, 1080f)]
    [InlineData(3840f, 2160f)]
    public void Every_tab_mounts_at_every_tier_without_an_exception(float width, float height)
        => ZuneHost.Mounted(host, shell =>
        {
            foreach (var tab in new[] { Settings.Tab.General, Settings.Tab.Appearance, Settings.Tab.Playback, Settings.Tab.Notifications })
            {
                Settings.Open(tab);
                shell.RunFrames(40);
                Node(shell, "zs:title");
                Node(shell, "zs:scroll:" + Settings.TabSlugs[(int)tab]);
            }
        }, width, height);

    // ── setup ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The sign-in flows with nothing behind them: a facet and its facts.</summary>
    sealed class FixtureSignIn(Setup.SignInFacet facet) : ISetupSignInHost
    {
        public SetupSignInFacts Facts()
            => new(facet, Handed: false, Minting: false, BusyMessageKey: Strings.Auth.WaitingApproval,
                FailureKey: facet == Setup.SignInFacet.Failed ? Setup.SignInRules.LocDenied : "", NeedsPairingCode: false, UserCode: "");

        public SetupPairingFacts Pairing() => new(false, true, false, "", "", 0L);
        public SetupLoginStepFacts Step() => new(1, false);
        public bool NeedsPairingChallenge(Setup.WizardPage activePage) => false;
        public SetupAccountFacts Account() => new("Fixture Person", null, Premium: true);
        public void StartBrowser() { }
        public void StartPairing() { }
        public void CancelBrowser() { }
        public void Retry() { }
        public void SwitchAccount() { }
        public void CloseDoor() { }
        public void OpenSignUp() { }
        public void OpenPremium() { }
        public void OpenExternal(string uri) { }
    }

    static SetupWizardProps WizardProps(Setup.WizardModel session, ISetupSignInHost signIn) => new()
    {
        Session = session,
        Terms = new SetupTermsProps { Session = session, OpenPrivacy = static () => { }, Consent = new SetupConsent(3, static () => 0, static _ => { }) },
        SignIn = new SetupSignInPageProps { Session = session, Host = signIn },
        Theme = new SetupThemeProps { ThemePicker = static () => new BoxEl { Key = "test:theme-picker" }, ThemeOptions = static () => new BoxEl { Key = "test:theme-options" } },
        LocalPlayback = new SetupLocalPlaybackProps { Session = session, OpenSignature = static (_, _) => { }, RevealFolder = static _ => { } },
    };

    [Fact]
    public void The_setup_plate_mounts_with_its_accent_column_and_shows_back_only_where_a_page_has_one()
        => host.Run(() =>
        {
            var session = new Setup.WizardModel(Setup.WizardEntry.FirstRun, signedIn: false);
            try
            {
                using var harness = ViewHarness.Mount(ViewKeys.SetupWizard, WizardProps(session, new FixtureSignIn(Setup.SignInFacet.Idle)), UnderZune());
                harness.RunFrames(30);
                Assert.True(harness.Contains("zsetup:plate"));
                Assert.True(harness.Contains("zsetup:accent"), "the accent column is the plate's picture");
                Assert.True(harness.Contains("zsetup:page:terms"));
                Assert.True(harness.Contains("zsetup:frame:" + (int)Setup.WizardPage.Terms));
                Assert.False(harness.Contains("zsetup:footer:back"), "Terms has no Back");
                Assert.True(harness.Contains("zsetup:footer:primary"));

                session.Advance(Setup.WizardPage.SignIn);
                harness.RunFrames(60);
                Assert.True(harness.Contains("zsetup:page:sign-in"));

                session.Advance(Setup.WizardPage.Theme);
                harness.RunFrames(60);
                Assert.True(harness.Contains("zsetup:page:theme"));
                Assert.True(harness.Contains("test:theme-picker"));
                Assert.True(harness.Contains("test:theme-options"));
                Assert.True(harness.Contains("zsetup:footer:back"), "the Theme page shows Back");

                session.Advance(Setup.WizardPage.LocalPlayback);
                harness.RunFrames(60);
                Assert.True(harness.Contains("zsetup:page:local-playback"));
                Assert.True(harness.Contains("zsetup:footer:back"));
            }
            finally { session.End(); }
        });

    [Theory]
    [InlineData(300f, 700f)]
    [InlineData(762f, 490f)]
    [InlineData(3840f, 2160f)]
    public void The_setup_plate_holds_at_every_size(float width, float height)
        => host.Run(() =>
        {
            var session = new Setup.WizardModel(Setup.WizardEntry.FirstRun, signedIn: false);
            try
            {
                using var harness = ViewHarness.Mount(ViewKeys.SetupWizard, WizardProps(session, new FixtureSignIn(Setup.SignInFacet.Idle)), UnderZune(width, height));
                harness.RunFrames(30);
                Assert.True(harness.Contains("zsetup:plate"));
            }
            finally { session.End(); }
        });

    [Fact]
    public void The_sign_in_door_mounts_its_body_and_its_footer()
        => host.Run(() =>
        {
            foreach (var facet in new[] { Setup.SignInFacet.Idle, Setup.SignInFacet.Busy, Setup.SignInFacet.Failed })
                foreach (var part in new[] { SetupDialogPart.Body, SetupDialogPart.Footer })
                {
                    var props = new SetupSignInDoorProps { Part = part, Host = new FixtureSignIn(facet) };
                    ViewHarness.With(host, ViewKeys.SetupSignInDoor, props, harness => harness.RunFrames(10), UnderZune());
                }
        });

    // ── release notes ────────────────────────────────────────────────────────────────────────────────────────────────────

    static ReleaseNotes.ReleaseNotesDocument Doc(string version, params string[] highlightIds)
    {
        var doc = new ReleaseNotes.ReleaseNotesDocument
        {
            Version = version, Name = "Crest", Tagline = "A fixture release.", Date = "2026-10-01",
            Sections = [new ReleaseNotes.ReleaseSection { Kind = "added", Items = [new ReleaseNotes.ReleaseItem { Id = "i1", Text = "Something **new**." }] }],
            Highlights = highlightIds.Select(id => new ReleaseNotes.ReleaseHighlight { Id = id, Title = "Title " + id, Body = "Body of " + id }).ToArray(),
        };
        doc.Normalize();
        return doc;
    }

    static ReleaseNotes.HighlightItem[] Items(ReleaseNotes.ReleaseNotesDocument doc)
        => doc.Highlights.Select(h => new ReleaseNotes.HighlightItem(h, doc, null)).ToArray();

    static WhatsNewPageProps PageProps(WhatsNewState state, ReleaseNotes.ReleaseNotesView? view = null) => new()
    {
        State = state, View = view, OnlyLatest = false, RunningVersion = "0.3.0",
        ShowOnlyLatest = static () => { }, CopyLink = static _ => { }, OpenHighlight = static _ => { },
        OpenUrl = static _ => { }, OpenStoreListing = static () => { }, OpenRelease = static _ => { },
    };

    static ReleaseNotes.ReleaseNotesView LoadedView()
    {
        var doc = Doc("0.3.0", "h1", "h2");
        var index = new ReleaseNotes.ReleaseNotesIndex
        {
            Releases =
            [
                new ReleaseNotes.ReleaseNotesIndexEntry { Version = "0.3.0", Name = "Crest", Date = "2026-10-01" },
                new ReleaseNotes.ReleaseNotesIndexEntry { Version = "0.2.9", Name = "Dune", Date = "2026-09-01" },
            ],
        };
        return new ReleaseNotes.ReleaseNotesView([new ReleaseNotes.ReleaseEntry(doc, null, IsYou: true, IsUnread: false)], Items(doc), index, "0.3.0", "0.2.9");
    }

    [Fact]
    public void The_whats_new_page_mounts_loading_empty_and_loaded_under_Zune()
    {
        ViewHarness.With(host, ViewKeys.WhatsNewPage, PageProps(WhatsNewState.Loading), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("zrn:title"));
            Assert.False(harness.Contains("zrn:rail"), "a loading page draws no rail");
        }, UnderZune());
        ViewHarness.With(host, ViewKeys.WhatsNewPage, PageProps(WhatsNewState.Empty), harness =>
        {
            harness.RunFrames(10);
            Assert.False(harness.Contains("zrn:rail"), "an empty page draws no rail");
        }, UnderZune());
        ViewHarness.With(host, ViewKeys.WhatsNewPage, PageProps(WhatsNewState.Loaded, LoadedView()), harness =>
        {
            harness.RunFrames(30);
            Assert.True(harness.Contains("zrn:title"));
            Assert.True(harness.Contains("zrn:rail"), "a loaded page at Standard draws the release rail");
            Assert.True(harness.Contains("zrn:hl:0.3.0:h1"), "the first highlight card is keyed by its slide id");
            Assert.True(harness.Contains("zrn:hl:0.3.0:h2"), "the second highlight card is keyed by its slide id");
        }, UnderZune());
    }

    [Fact]
    public void The_whats_new_page_swaps_its_rail_for_a_picker_below_the_standard_tier()
        => ViewHarness.With(host, ViewKeys.WhatsNewPage, PageProps(WhatsNewState.Loaded, LoadedView()), harness =>
        {
            harness.RunFrames(30);
            Assert.False(harness.Contains("zrn:rail"), "a compact window has no rail");
            Assert.True(harness.Contains("zrn:picker"), "a compact window switches releases with a picker");
        }, UnderZune(400f, 800f));

    [Fact]
    public void The_viewer_keeps_its_chrome_and_pages_one_pip_per_highlight()
    {
        var items = Items(Doc("0.3.0", "h1", "h2", "h3"));
        var props = new HighlightViewerProps
        {
            Items = items, Initial = 0, Close = static () => { }, TryItFor = static _ => null, OpenUrl = static _ => { }, OpenStoreListing = static () => { },
        };
        ViewHarness.With(host, ViewKeys.ReleaseNotesViewer, props, harness =>
        {
            harness.RunFrames(60);
            Assert.True(harness.Contains("zhv:close"));
            Assert.True(harness.Contains("zhv:prev"));
            Assert.True(harness.Contains("zhv:next"));
            Assert.Equal(3, harness.FindPrefix("zpip:").Count);
        }, UnderZune());
    }

    [Fact]
    public void The_after_update_plate_draws_its_highlight_cards_and_its_footer()
    {
        var doc = Doc("0.3.0", "h1", "h2");
        var props = new AfterUpdatePlateProps
        {
            VersionRange = "0.2.9 → 0.3.0.1", VersionText = "Wavee 0.3.0", Tagline = doc.Tagline, Cards = Items(doc),
            OpenCard = static _ => { }, SetDontShowAgain = static _ => { }, ShowFull = static () => { }, Close = static () => { }, OpenStoreListing = static () => { },
        };
        ViewHarness.With(host, ViewKeys.ReleaseNotesAfterUpdatePlate, props, harness =>
        {
            harness.RunFrames(30);
            Assert.True(harness.Contains("zrn:plate"));
            Assert.True(harness.Contains("zrn:plate:ok"));
            Assert.Equal(2, harness.FindPrefix("zdlg-hl:").Count);
        }, UnderZune());
    }

    // ── dialogs ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_crash_prompt_body_mounts_with_its_report_and_its_checks()
    {
        var props = new CrashDialogProps
        {
            FromList = false, HeaderMessage = "Last run ended 3 minutes ago", Lead = "Last time, Wavee crashed. Send a report so it can be fixed?",
            HasDump = true, IncludeDumpLabel = "Include a memory snapshot", IncludeDumpDefault = true, Configured = true, PrivacyUrl = "https://example.test/privacy",
            PrepareCompose = static () => static () => "report text", InstallSubmit = static _ => { }, Submit = static (_, _) => { },
            NotifyPreparing = static () => { }, ReportOnGithub = static () => { },
        };
        ViewHarness.With(host, ViewKeys.DialogCrash, props, harness =>
        {
            harness.RunFrames(20);
            Assert.True(harness.Contains("zcrash:body"));
            Assert.True(harness.Contains("zcrash:prompt"));
        }, UnderZune());
    }
}
