// ── Wavee.Tests/SetupTests.cs — the setup markers, the install bootstrap, the sign-in surface's rules, the QR ──────────
//
// Gap batch B5 (G-030, G-095): the pure region of `Screens/Setup.cs` and the read-only disk probe of `Setup.Host.cs`.
// Ported from 0.2.9's `SetupGatingTests` (markers, page ladder, bootstrap, the wiped-data-folder reset) and `QrTests`
// (the encoder decoded by an independent reader), plus the two sidebar facts `SidebarDesignTests` had to drop while
// `SidebarBootstrap` was unported (its fresh-install decision is folded into `Setup.Bootstrap` now, sidebar miner D6), and
// the sign-in surface's facet fold and door rules. Pure: a memory settings store and a temp directory, never the profile.

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SetupGatingTests
{
    [Fact]
    public void Pending_and_completed_read_their_keys_and_tolerate_no_store()
    {
        Assert.False(Setup.Gating.IsPending(null));
        Assert.False(Setup.Gating.IsCompleted(null));
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.SetupPending, true);
        Assert.True(Setup.Gating.IsPending(settings));
        Assert.False(Setup.Gating.IsCompleted(settings));
    }

    [Fact]
    public void Completing_sets_completed_clears_pending_and_reports_the_transition_once()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.SetupPending, true);
        Assert.True(Setup.Gating.MarkCompleted(settings));
        Assert.True(settings.Get(Platform.Keys.SetupCompleted));
        Assert.False(settings.Get(Platform.Keys.SetupPending));
        Assert.False(Setup.Gating.MarkCompleted(settings));
        Assert.False(Setup.Gating.MarkCompleted(null));
    }

    /// <summary>A terms re-arm finishes an install that is ALREADY completed: completing must still clear Pending, or the
    /// wizard re-opens on every launch with no way to satisfy it.</summary>
    [Fact]
    public void Completing_clears_pending_even_when_already_completed()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.SetupCompleted, true);
        settings.Set(Platform.Keys.SetupPending, true);
        Assert.False(Setup.Gating.MarkCompleted(settings));
        Assert.False(settings.Get(Platform.Keys.SetupPending));
    }

    [Fact]
    public void Deferring_clears_pending_only_and_never_after_completion()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.SetupPending, true);
        Assert.True(Setup.Gating.MarkDeferred(settings));
        Assert.False(settings.Get(Platform.Keys.SetupPending));
        Assert.False(settings.Get(Platform.Keys.SetupCompleted));
        Assert.False(Setup.Gating.MarkDeferred(settings));

        var done = new MemoryAppSettings();
        done.Set(Platform.Keys.SetupPending, true);
        Setup.Gating.MarkCompleted(done);
        Assert.False(Setup.Gating.MarkDeferred(done));
        Assert.True(done.Get(Platform.Keys.SetupCompleted));
    }

    [Theory]
    [InlineData(Setup.WizardEntry.TermsRearm, false, true)]
    [InlineData(Setup.WizardEntry.TermsRearm, true, false)]
    [InlineData(Setup.WizardEntry.FirstRun, false, false)]
    [InlineData(Setup.WizardEntry.Reauth, false, false)]
    public void Only_an_idle_terms_rearm_is_dismissible(Setup.WizardEntry entry, bool busy, bool expected)
        => Assert.Equal(expected, Setup.Gating.CanDismiss(entry, busy));

    [Theory]
    [InlineData(true, 0, 1, true)]
    [InlineData(true, 1, 1, false)]
    [InlineData(true, 2, 1, false)]
    [InlineData(false, 0, 1, false)]
    public void The_terms_rearm_is_for_a_completed_install_behind_the_current_revision(bool completed, int accepted, int current, bool expected)
        => Assert.Equal(expected, Setup.Gating.NeedsTermsRearm(completed, accepted, current));

    [Theory]
    [InlineData(false, false, 0, false)]
    [InlineData(false, true, 1, false)]
    [InlineData(true, false, 0, false)]
    [InlineData(true, true, 0, true)]
    [InlineData(true, false, 1, true)]
    public void A_fresh_disk_under_a_remembering_registry_resets(bool fresh, bool completed, int termsAccepted, bool expected)
        => Assert.Equal(expected, Setup.Gating.NeedsFreshInstallReset(fresh, completed, termsAccepted));

    [Fact]
    public void The_page_ladder_skips_sign_in_when_asked_and_clamps_at_both_ends()
    {
        Assert.Equal(Setup.WizardPage.SignIn, Setup.Gating.NextPage(Setup.WizardPage.Terms, skipSignIn: false));
        Assert.Equal(Setup.WizardPage.LocalPlayback, Setup.Gating.NextPage(Setup.WizardPage.Terms, skipSignIn: true));
        Assert.Equal(Setup.WizardPage.LocalPlayback, Setup.Gating.NextPage(Setup.WizardPage.LocalPlayback, skipSignIn: false));
        Assert.Equal(Setup.WizardPage.Terms, Setup.Gating.PrevPage(Setup.WizardPage.LocalPlayback, skipSignIn: true));
        Assert.Equal(Setup.WizardPage.SignIn, Setup.Gating.PrevPage(Setup.WizardPage.LocalPlayback, skipSignIn: false));
        Assert.Equal(Setup.WizardPage.Terms, Setup.Gating.PrevPage(Setup.WizardPage.Terms, skipSignIn: false));
        Assert.NotEqual(Setup.WizardPage.SignIn,
            Setup.Gating.PrevPage(Setup.Gating.NextPage(Setup.WizardPage.Terms, true), true));
    }

    [Fact]
    public void The_footer_counts_two_steps_after_pre_setup()
    {
        Assert.Null(Setup.Gating.StepNumber(Setup.WizardPage.Terms));
        Assert.Equal(1, Setup.Gating.StepNumber(Setup.WizardPage.SignIn)!.Value.Step);
        Assert.Equal(2, Setup.Gating.StepNumber(Setup.WizardPage.LocalPlayback)!.Value.Step);
        Assert.Equal(Setup.Gating.StepTotal, Setup.Gating.StepNumber(Setup.WizardPage.LocalPlayback)!.Value.Total);
        Assert.Equal(0f, Setup.Gating.Progress(Setup.WizardPage.Terms), 5);
        Assert.Equal(0.5f, Setup.Gating.Progress(Setup.WizardPage.SignIn), 5);
        Assert.Equal(1f, Setup.Gating.Progress(Setup.WizardPage.LocalPlayback), 5);
        Assert.True(Setup.Gating.ShowsBack(Setup.WizardPage.LocalPlayback));
        Assert.False(Setup.Gating.ShowsBack(Setup.WizardPage.SignIn));
    }

    [Fact]
    public void A_sign_in_required_launch_reauthenticates_a_completed_install_and_first_runs_the_rest()
    {
        Assert.Equal(Setup.WizardEntry.Reauth, Setup.Gating.EntryFor(completed: true));
        Assert.Equal(Setup.WizardEntry.FirstRun, Setup.Gating.EntryFor(completed: false));
        Assert.True(Setup.Gating.SkipsLocalPlayback(Setup.WizardEntry.Reauth, runtimeReady: true));
        Assert.False(Setup.Gating.SkipsLocalPlayback(Setup.WizardEntry.FirstRun, runtimeReady: true));
        Assert.True(Setup.Gating.SuppressesRuntimePrompts(pending: true, sessionOpen: false));
        Assert.False(Setup.Gating.SuppressesRuntimePrompts(pending: false, sessionOpen: false));
    }
}

public class SetupBootstrapTests
{
    static readonly Setup.InstallWitnesses Fresh = new(LibraryDb: false, StoredCredential: false, History: false);
    static readonly Setup.InstallWitnesses Existing = new(LibraryDb: true, StoredCredential: false, History: false);

    [Fact]
    public void A_fresh_install_arms_the_wizard_and_suppresses_the_sidebar_chooser()
    {
        var settings = new MemoryAppSettings();
        Setup.Bootstrap.Run(settings, in Fresh);

        Assert.True(settings.Get(Platform.Keys.SetupPending));
        Assert.False(settings.Get(Platform.Keys.SetupCompleted));
        Assert.Equal(Setup.Bootstrap.TargetVersion, settings.Get(Platform.Keys.SetupBootstrapVersion));
        // One onboarding prompt on a first launch, not two (0.2.9's rule; the dropped SidebarDesign fact, restored here).
        Assert.False(SidebarDesignGating.ShouldShowChooser(settings));
        Assert.Equal(SidebarDesign.Classic, SidebarDesignGating.ActiveDesign(settings));
    }

    [Fact]
    public void An_existing_install_is_marked_completed_grandfathers_the_terms_and_never_sees_the_chooser()
    {
        var settings = new MemoryAppSettings();
        Setup.Bootstrap.Run(settings, in Existing);

        Assert.True(settings.Get(Platform.Keys.SetupCompleted));
        Assert.False(settings.Get(Platform.Keys.SetupPending));
        Assert.Equal(Setup.Gating.TermsVersion, settings.Get(Platform.Keys.TermsAcceptedVersion));
        Assert.False(SidebarDesignGating.ShouldShowChooser(settings));
        Assert.False(settings.WasWritten(Platform.Keys.SidebarDesign));   // an existing install's design is never stomped
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Any_one_disk_witness_makes_an_install_existing(bool library, bool credential, bool history)
        => Assert.False(Setup.Bootstrap.IsFreshInstall(new Setup.InstallWitnesses(library, credential, history), new MemoryAppSettings()));

    [Fact]
    public void A_pane_preference_from_an_older_build_makes_an_install_existing()
    {
        var settings = new MemoryAppSettings();
        Assert.True(Setup.Bootstrap.IsFreshInstall(in Fresh, settings));
        settings.Set(Platform.Keys.SidebarCollapsedLegacy, true);
        Assert.False(Setup.Bootstrap.IsFreshInstall(in Fresh, settings));
    }

    [Fact]
    public void A_second_launch_writes_nothing()
    {
        var settings = new MemoryAppSettings();
        Setup.Bootstrap.Run(settings, in Fresh);
        settings.Set(Platform.Keys.SetupPending, false);   // the wizard was shown and deferred
        int before = settings.WrittenCount;

        Setup.Bootstrap.Run(settings, in Fresh);

        Assert.Equal(before, settings.WrittenCount);
        Assert.False(settings.Get(Platform.Keys.SetupPending));
    }

    [Fact]
    public void A_wiped_data_folder_resets_a_completed_install_to_fresh()
    {
        var settings = new MemoryAppSettings();
        Setup.Bootstrap.Run(settings, in Existing);
        Assert.True(settings.Get(Platform.Keys.SetupCompleted));

        Setup.Bootstrap.Run(settings, in Fresh);           // the user wiped %LOCALAPPDATA%\Wavee; the registry survived

        Assert.True(settings.Get(Platform.Keys.SetupPending));
        Assert.False(settings.Get(Platform.Keys.SetupCompleted));
        Assert.Equal(0, settings.Get(Platform.Keys.TermsAcceptedVersion));
    }

    [Fact]
    public void Accepting_the_terms_stops_the_rearm()
    {
        var settings = new MemoryAppSettings();
        Setup.Bootstrap.Run(settings, in Existing);
        settings.Set(Platform.Keys.SetupPending, true);                         // a later terms bump re-armed it
        settings.Set(Platform.Keys.TermsAcceptedVersion, Setup.Gating.TermsVersion);
        Setup.Gating.MarkCompleted(settings);

        Setup.Bootstrap.Run(settings, in Existing);

        Assert.False(settings.Get(Platform.Keys.SetupPending));
    }
}

/// <summary>The read-only disk probe, over a temp directory (never the profile).</summary>
public class SetupInstallProbeTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "wavee-setup-probe-tests", Guid.NewGuid().ToString("N"));

    public SetupInstallProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void An_empty_data_root_has_no_witness_and_the_probe_creates_nothing()
    {
        Assert.Equal(new Setup.InstallWitnesses(false, false, false), Setup.ProbeInstall(_root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Each_witness_is_read_from_its_0_2_9_path()
    {
        File.WriteAllText(Path.Combine(_root, "library.db"), "sqlite");
        Directory.CreateDirectory(Path.Combine(_root, "WaveeMusic"));
        File.WriteAllText(Path.Combine(_root, "WaveeMusic", "history.json"), "[]");
        File.WriteAllText(Path.Combine(_root, "store.json"), "{\"" + Platform.CredentialKey + "\":\"none:e30=\",\"device.id\":\"d\"}");

        Assert.Equal(new Setup.InstallWitnesses(true, true, true), Setup.ProbeInstall(_root));
    }

    [Fact]
    public void A_store_without_a_credential_is_not_a_credential_witness()
    {
        File.WriteAllText(Path.Combine(_root, "store.json"), "{\"device.id\":\"d\"}");
        Assert.False(Setup.ProbeInstall(_root).StoredCredential);
    }
}

public class SignInRulesTests
{
    static Spotify.SignInState Idle => Spotify.SignInState.Idle;

    [Fact]
    public void Online_is_done_and_a_premium_refusal_wins_over_everything_else()
    {
        Assert.Equal(Setup.SignInFacet.Done, Setup.SignInRules.Project(Idle, Spotify.SessionPhase.Online, Spotify.SessionFault.None));
        var busy = Idle.With(Spotify.SignInMethod.Browser, Spotify.SignInStage.Waiting) with { Handed = true };
        Assert.Equal(Setup.SignInFacet.Premium,
            Setup.SignInRules.Project(busy, Spotify.SessionPhase.Failed, Spotify.SessionFault.NotPremium));
    }

    [Fact]
    public void After_the_hand_off_the_session_is_the_progress()
    {
        var handed = Idle with { Handed = true };
        Assert.Equal(Setup.SignInFacet.Busy, Setup.SignInRules.Project(handed, Spotify.SessionPhase.Offline, Spotify.SessionFault.None));
        Assert.Equal(Setup.SignInFacet.Busy, Setup.SignInRules.Project(handed, Spotify.SessionPhase.Authenticating, Spotify.SessionFault.None));
        Assert.Equal(Setup.SignInFacet.Busy, Setup.SignInRules.Project(handed, Spotify.SessionPhase.Minting, Spotify.SessionFault.None));
        Assert.Equal(Setup.SignInFacet.Failed, Setup.SignInRules.Project(handed, Spotify.SessionPhase.Failed, Spotify.SessionFault.CredentialRejected));
        Assert.Equal(Setup.SignInRules.LocRejected, Setup.SignInRules.FailureKey(handed, Spotify.SessionFault.CredentialRejected));
        Assert.Equal(Setup.SignInRules.LocRefused, Setup.SignInRules.FailureKey(handed, Spotify.SessionFault.LoginRefused));
    }

    [Theory]
    [InlineData(Spotify.SignInStage.Starting, Setup.SignInFacet.Busy)]
    [InlineData(Spotify.SignInStage.Waiting, Setup.SignInFacet.Busy)]
    [InlineData(Spotify.SignInStage.Exchanging, Setup.SignInFacet.Busy)]
    [InlineData(Spotify.SignInStage.Failed, Setup.SignInFacet.Failed)]
    [InlineData(Spotify.SignInStage.Denied, Setup.SignInFacet.Failed)]
    [InlineData(Spotify.SignInStage.Expired, Setup.SignInFacet.Failed)]
    [InlineData(Spotify.SignInStage.Idle, Setup.SignInFacet.Idle)]
    public void The_browser_flow_folds_to_its_facet(Spotify.SignInStage stage, Setup.SignInFacet expected)
        => Assert.Equal(expected, Setup.SignInRules.Project(Idle.With(Spotify.SignInMethod.Browser, stage),
            Spotify.SessionPhase.Failed, Spotify.SessionFault.NoCredential));

    [Fact]
    public void The_pairing_code_retries_in_place_and_a_lapse_is_expired_not_failed()
    {
        var coded = Idle with { Code = Spotify.SignInStage.Waiting, UserCode = "ABCD" };
        Assert.Equal(Setup.SignInFacet.Idle, Setup.SignInRules.Project(coded, Spotify.SessionPhase.Failed, Spotify.SessionFault.NoCredential));
        Assert.Equal(Setup.SignInFacet.Expired, Setup.SignInRules.Project(coded with { Code = Spotify.SignInStage.Expired },
            Spotify.SessionPhase.Failed, Spotify.SessionFault.NoCredential));
        Assert.Equal(Setup.SignInFacet.Failed, Setup.SignInRules.Project(coded with { Code = Spotify.SignInStage.Failed },
            Spotify.SessionPhase.Failed, Spotify.SessionFault.NoCredential));
    }

    [Fact]
    public void A_stored_credential_the_ap_rejected_opens_on_failed_and_says_why()
    {
        Assert.Equal(Setup.SignInFacet.Failed, Setup.SignInRules.Project(Idle, Spotify.SessionPhase.Failed, Spotify.SessionFault.CredentialRejected));
        Assert.Equal(Setup.SignInRules.LocRejected, Setup.SignInRules.FailureKey(Idle, Spotify.SessionFault.CredentialRejected));
    }

    [Fact]
    public void The_failure_sentence_names_the_flows_reason()
    {
        Assert.Equal(Setup.SignInRules.LocDenied,
            Setup.SignInRules.FailureKey(Idle.With(Spotify.SignInMethod.Browser, Spotify.SignInStage.Denied), Spotify.SessionFault.None));
        Assert.Equal(Setup.SignInRules.LocBrowserTimedOut,
            Setup.SignInRules.FailureKey(Idle.With(Spotify.SignInMethod.Browser, Spotify.SignInStage.Expired), Spotify.SessionFault.None));
        Assert.Equal(Setup.SignInRules.LocBrowserUnavailable,
            Setup.SignInRules.FailureKey(Idle.With(Spotify.SignInMethod.Browser, Spotify.SignInStage.Failed, Spotify.SignInError.BrowserUnavailable), Spotify.SessionFault.None));
        Assert.Equal(Strings.Auth.GenericError,
            Setup.SignInRules.FailureKey(Idle.With(Spotify.SignInMethod.Browser, Spotify.SignInStage.Failed, Spotify.SignInError.Refused), Spotify.SessionFault.None));
        Assert.Equal(Strings.Auth.NetworkError,
            Setup.SignInRules.FailureKey(Idle.With(Spotify.SignInMethod.DeviceCode, Spotify.SignInStage.Failed, Spotify.SignInError.Network), Spotify.SessionFault.None));
    }

    [Theory]
    [InlineData(true, false, true, false, true)]      // an explicit request opens even with a credential stored
    [InlineData(false, true, false, false, true)]     // entering SignInRequired with nothing stored opens
    [InlineData(false, true, true, false, false)]     // …but never over a resume behind the cache-first shell
    [InlineData(false, false, false, false, false)]   // staying in SignInRequired is not a new reason to open
    [InlineData(true, true, false, true, false)]      // never a second surface
    public void The_door_opens_on_a_request_or_on_entering_sign_in_required_with_nothing_stored(
        bool requested, bool entered, bool stored, bool open, bool expected)
        => Assert.Equal(expected, Setup.SignInRules.OpensDoor(requested, entered, stored, open));

    [Fact]
    public void The_door_closes_only_once_online()
    {
        Assert.True(Setup.SignInRules.ClosesDoor(Spotify.SessionPhase.Online));
        Assert.False(Setup.SignInRules.ClosesDoor(Spotify.SessionPhase.Minting));
        Assert.False(Setup.SignInRules.ClosesDoor(Spotify.SessionPhase.Failed));
    }

    [Fact]
    public void A_pairing_code_is_asked_for_only_from_rest_and_never_after_a_hand_off()
    {
        Assert.True(Setup.SignInRules.NeedsPairingCode(Idle));
        Assert.False(Setup.SignInRules.NeedsPairingCode(Idle with { Code = Spotify.SignInStage.Starting }));
        Assert.False(Setup.SignInRules.NeedsPairingCode(Idle with { Code = Spotify.SignInStage.Expired }));
        Assert.False(Setup.SignInRules.NeedsPairingCode(Idle with { Code = Spotify.SignInStage.Failed }));
        Assert.False(Setup.SignInRules.NeedsPairingCode(Idle with { Handed = true }));
    }

    [Fact]
    public void The_countdown_is_minutes_and_seconds_rounded_up_and_never_negative()
    {
        Assert.Equal("10:00", Setup.SignInRules.Countdown(600_000, 0));
        Assert.Equal("09:42", Setup.SignInRules.Countdown(600_000, 18_500));
        Assert.Equal("00:01", Setup.SignInRules.Countdown(1_000, 999));
        Assert.Equal("00:00", Setup.SignInRules.Countdown(1_000, 5_000));
    }
}

/// <summary>The QR encoder, decoded by an INDEPENDENT reader (format BCH → mask removal → zig-zag → RS syndromes → byte
/// segment), so a Reed–Solomon, placement or mask regression is a decode mismatch rather than a silently dead code (0.2.9's
/// bug: the symbol rendered and no phone could read it).</summary>
public class SetupQrTests
{
    [Theory]
    [InlineData("HELLO")]                                          // v1-M
    [InlineData("https://spotify.com/pair")]                       // v2-M
    [InlineData("https://spotify.com/pair?code=WZY5Q6TX")]         // v3-M (the live pairing url)
    public void An_encoded_symbol_round_trips_through_a_standard_reader(string text)
    {
        bool[,] m = Setup.Qr.Encode(text, Setup.Qr.Ecc.M);
        var (decoded, ecc, syndromesZero) = QrReader.Decode(m);
        Assert.True(syndromesZero, "RS syndromes must be all-zero");
        Assert.Equal("M", ecc);
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void A_payload_beyond_version_ten_is_refused()
        => Assert.Throws<ArgumentException>(() => Setup.Qr.Encode(new string('x', 400), Setup.Qr.Ecc.M));

    [Fact]
    public void The_plate_snaps_to_whole_modules_with_a_four_module_quiet_zone()
    {
        Assert.Equal(2, Setup.QrPlate.CellFor(80f, 29));        // 80 / 37 → 2, never rounded up to 3
        Assert.Equal(74, Setup.QrPlate.PlateFor(80f, 29));
        Assert.Equal(3, Setup.QrPlate.CellFor(96f, 21));        // v1: 96 / 29 → 3
        Assert.Equal(87, Setup.QrPlate.PlateFor(96f, 21));
        Assert.Equal(2, Setup.QrPlate.CellFor(10f, 57));         // the floor
    }

    /// <summary>A minimal, standard-conformant reader for single-block ECC-M symbols (v1–v3), written independently of the
    /// encoder's write path.</summary>
    static class QrReader
    {
        public static (string Text, string Ecc, bool SyndromesZero) Decode(bool[,] m)
        {
            int n = m.GetLength(0);
            int version = (n - 17) / 4;

            int fmtRaw = 0, bi = 0;
            void F(int x, int y) { fmtRaw |= (m[x, y] ? 1 : 0) << bi; bi++; }
            for (int i = 0; i <= 5; i++) F(8, i);
            F(8, 7); F(8, 8); F(7, 8);
            for (int i = 9; i < 15; i++) F(14 - i, 8);
            int best = 0, bestDist = 99;
            for (int d = 0; d < 32; d++)
            {
                int rem = d;
                for (int i = 0; i < 10; i++) rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
                int enc = ((d << 10) | rem) ^ 0x5412;
                int dist = System.Numerics.BitOperations.PopCount((uint)(enc ^ fmtRaw));
                if (dist < bestDist) { bestDist = dist; best = d; }
            }
            int mask = best & 7;
            string ecc = ((best >> 3) & 3) switch { 1 => "L", 0 => "M", 3 => "Q", _ => "H" };

            bool Func(int x, int y) =>
                (x < 8 && y < 8) || (x >= n - 8 && y < 8) || (x < 8 && y >= n - 8) ||
                x == 6 || y == 6 ||
                (version >= 2 && x >= n - 9 && x <= n - 5 && y >= n - 9 && y <= n - 5) ||
                (y == 8 && (x <= 8 || x >= n - 8)) || (x == 8 && (y <= 8 || y >= n - 8));

            bool MaskBit(int x, int y) => mask switch
            {
                0 => (x + y) % 2 == 0, 1 => y % 2 == 0, 2 => x % 3 == 0, 3 => (x + y) % 3 == 0,
                4 => (y / 2 + x / 3) % 2 == 0, 5 => (x * y) % 2 + (x * y) % 3 == 0,
                6 => ((x * y) % 2 + (x * y) % 3) % 2 == 0, _ => ((x + y) % 2 + (x * y) % 3) % 2 == 0,
            };

            var bits = new List<int>();
            for (int col = n - 1, up = 1; col > 0; col -= 2, up ^= 1)
            {
                if (col == 6) col--;
                for (int r = 0; r < n; r++)
                {
                    int y = up == 1 ? n - 1 - r : r;
                    for (int c = 0; c < 2; c++)
                    {
                        int x = col - c;
                        if (Func(x, y)) continue;
                        bits.Add((m[x, y] ? 1 : 0) ^ (MaskBit(x, y) ? 1 : 0));
                    }
                }
            }
            var cw = new List<int>();
            for (int i = 0; i + 8 <= bits.Count; i += 8) { int v = 0; for (int k = 0; k < 8; k++) v = (v << 1) | bits[i + k]; cw.Add(v); }

            int[] exp = new int[512], log = new int[256];
            { int xx = 1; for (int i = 0; i < 255; i++) { exp[i] = xx; log[xx] = i; xx <<= 1; if ((xx & 0x100) != 0) xx ^= 0x11D; } for (int i = 255; i < 512; i++) exp[i] = exp[i - 255]; }
            int Mul(int a, int b) => a == 0 || b == 0 ? 0 : exp[log[a] + log[b]];
            int ecLen = version switch { 1 => 10, 2 => 16, _ => 26 };
            bool syn = true;
            for (int j = 0; j < ecLen; j++) { int acc = 0; foreach (int c in cw) acc = Mul(acc, exp[j]) ^ c; if (acc != 0) syn = false; }

            int bp = 0;
            int Read(int nb) { int v = 0; for (int k = 0; k < nb; k++) { v = (v << 1) | ((cw[bp >> 3] >> (7 - (bp & 7))) & 1); bp++; } return v; }
            int modeBits = Read(4), count = Read(8);
            var bytes = new byte[count];
            for (int i = 0; i < count; i++) bytes[i] = (byte)Read(8);
            return (modeBits == 4 ? Encoding.UTF8.GetString(bytes) : "<mode " + modeBits + ">", ecc, syn);
        }
    }
}
