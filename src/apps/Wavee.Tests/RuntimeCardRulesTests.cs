// ── Wavee.Tests/RuntimeCardRulesTests.cs — the runtime provisioning card's pure phase rules ──────────────────────────
//
// `Setup.RuntimeRules` (Screens/Setup.cs, owner I's region, A18). Ported from _old/Wavee.Tests's
// SetupRuntimePresentationTests (G6) and extended with the decisions 0.2.9 kept inline in PlaybackRuntimeSetupCard /
// PlaybackRuntimeBanner: the footer table, the three Cancel destinations, the banner gate and message, the Advanced
// arm, the already-current guard and the signature ladder.

using Xunit;

namespace Wavee.Tests;

public class RuntimeCardRulesTests
{
    [Theory]
    [InlineData(-1L, 100L, 0f)]
    [InlineData(0L, 100L, 0f)]
    [InlineData(50L, 100L, 0.5f)]
    [InlineData(100L, 100L, 1f)]
    [InlineData(150L, 100L, 1f)]
    [InlineData(50L, 0L, 0f)]
    public void ProgressFraction_clamps_live_byte_counts(long received, long total, float expected)
        => Assert.Equal(expected, Setup.RuntimeRules.ProgressFraction(received, total));

    [Theory]
    [InlineData("9f31d02ac4a7", "9f31…c4a7")]
    [InlineData("12345678", "12345678")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ShortHash_preserves_useful_ends(string? hash, string expected)
        => Assert.Equal(expected, Setup.RuntimeRules.ShortHash(hash));

    [Theory]
    [InlineData(false, true)]   // the standalone dialog toasts
    [InlineData(true, false)]   // wizard-hosted: the page already shows Ready
    public void ShowsReadyToast_skips_only_when_wizard_hosted(bool wizardHosted, bool expected)
        => Assert.Equal(expected, Setup.RuntimeRules.ShowsReadyToast(wizardHosted));

    [Fact]
    public void The_progress_bar_is_the_dialog_rung_less_its_padding()
    {
        Assert.Equal(460f, Setup.RuntimeRules.DialogWidth);
        Assert.Equal(412f, Setup.RuntimeRules.ProgressWidth);
        Assert.Equal(548f, Setup.RuntimeRules.SignatureDialogWidth);
    }

    [Fact]
    public void Byte_readouts_are_one_decimal_megabytes_and_drop_the_total_when_unknown()
    {
        Assert.Equal("12.3 / 84.0 MB", Setup.RuntimeRules.DownloadBytes(12_300_000, 84_000_000));
        Assert.Equal("12.3 MB", Setup.RuntimeRules.DownloadBytes(12_300_000, 0));
        Assert.Equal("84.0 MB", Setup.RuntimeRules.VerifySize(84_000_000));
        Assert.Equal("—", Setup.RuntimeRules.VerifySize(0));
        Assert.Equal("—", Setup.RuntimeRules.OrDash("  "));
    }

    [Fact]
    public void Only_the_three_working_phases_block_dismissal()
    {
        foreach (var phase in Enum.GetValues<RuntimePhase>())
            Assert.Equal(phase is RuntimePhase.FetchingCatalog or RuntimePhase.Downloading or RuntimePhase.Verifying,
                Setup.RuntimeRules.IsBusy(phase));
    }

    [Fact]
    public void A_ready_machine_opens_at_ready_and_back_returns_there()
    {
        Assert.Equal(RuntimePhase.Ready, Setup.RuntimeRules.InitialPhase(isReady: true));
        Assert.Equal(RuntimePhase.Offer, Setup.RuntimeRules.InitialPhase(isReady: false));
        Assert.Equal(RuntimePhase.Ready, Setup.RuntimeRules.BackTarget(isReady: true));
        Assert.Equal(RuntimePhase.Offer, Setup.RuntimeRules.BackTarget(isReady: false));
    }

    [Fact]
    public void Three_cancels_three_destinations()
    {
        Assert.Equal(RuntimePhase.Offer, Setup.RuntimeRules.CancelTarget(Setup.RuntimeCancel.Download));
        Assert.Equal(RuntimePhase.Advanced, Setup.RuntimeRules.CancelTarget(Setup.RuntimeCancel.InstallSelected));
        Assert.Equal(RuntimePhase.Offer, Setup.RuntimeRules.CancelTarget(Setup.RuntimeCancel.Untrusted));
    }

    [Fact]
    public void The_active_pack_is_never_reinstalled_over_itself()
    {
        Assert.True(Setup.RuntimeRules.AlreadyCurrent(true, "pack-1", "pack-1"));
        Assert.False(Setup.RuntimeRules.AlreadyCurrent(false, "pack-1", "pack-1"));   // not ready: nothing is loaded
        Assert.False(Setup.RuntimeRules.AlreadyCurrent(true, "pack-1", "pack-2"));
    }

    [Fact]
    public void The_catalog_is_fetched_once_and_retried_only_after_a_failure()
    {
        Assert.True(Setup.RuntimeRules.ShouldFetchCatalog(Setup.RuntimeCatalog.NotFetched, hasHost: true));
        Assert.True(Setup.RuntimeRules.ShouldFetchCatalog(Setup.RuntimeCatalog.Failed, hasHost: true));
        Assert.False(Setup.RuntimeRules.ShouldFetchCatalog(Setup.RuntimeCatalog.Loaded, hasHost: true));
        Assert.False(Setup.RuntimeRules.ShouldFetchCatalog(Setup.RuntimeCatalog.Fetching, hasHost: true));
        Assert.False(Setup.RuntimeRules.ShouldFetchCatalog(Setup.RuntimeCatalog.NotFetched, hasHost: false));
    }

    [Fact]
    public void The_advanced_arm_has_no_copy_for_a_catalog_never_fetched()
    {
        Assert.Equal(Setup.RuntimeAdvancedArm.Nothing, Setup.RuntimeRules.AdvancedArm(Setup.RuntimeCatalog.NotFetched, 3));
        Assert.Equal(Setup.RuntimeAdvancedArm.Busy, Setup.RuntimeRules.AdvancedArm(Setup.RuntimeCatalog.Fetching, 0));
        Assert.Equal(Setup.RuntimeAdvancedArm.Picker, Setup.RuntimeRules.AdvancedArm(Setup.RuntimeCatalog.Loaded, 2));
        Assert.Equal(Setup.RuntimeAdvancedArm.NoPack, Setup.RuntimeRules.AdvancedArm(Setup.RuntimeCatalog.Loaded, 0));
        Assert.Equal(Setup.RuntimeAdvancedArm.Unreachable, Setup.RuntimeRules.AdvancedArm(Setup.RuntimeCatalog.Failed, 0));
    }

    [Fact]
    public void Every_phase_gets_exactly_one_command_row()
    {
        var offer = Setup.RuntimeRules.FooterFor(RuntimePhase.Offer, Setup.RuntimeCatalog.NotFetched, 0, true);
        Assert.Equal(new Setup.RuntimeFooter(Setup.RuntimeVerb.Advanced, Setup.RuntimeVerb.None, Setup.RuntimeVerb.NotNow,
            Setup.RuntimeVerb.DownloadSetup), offer);

        var verifying = Setup.RuntimeRules.FooterFor(RuntimePhase.Verifying, Setup.RuntimeCatalog.Loaded, 1, true);
        Assert.Equal(Setup.RuntimeVerb.Cancel, verifying.Standard);
        Assert.False(verifying.StandardEnabled);   // present but disabled: verification cannot be abandoned
        Assert.Equal(Setup.RuntimeVerb.None, verifying.Accent);

        var ready = Setup.RuntimeRules.FooterFor(RuntimePhase.Ready, Setup.RuntimeCatalog.Loaded, 1, true);
        Assert.Equal(Setup.RuntimeVerb.CheckUpdate, ready.Link);
        Assert.Equal(Setup.RuntimeVerb.Done, ready.Accent);

        var untrusted = Setup.RuntimeRules.FooterFor(RuntimePhase.Untrusted, Setup.RuntimeCatalog.Loaded, 1, true);
        Assert.Equal(Setup.RuntimeVerb.CancelUntrusted, untrusted.Standard);
        Assert.Equal(Setup.RuntimeVerb.LoadAnyway, untrusted.Accent);
    }

    [Fact]
    public void Failed_offers_diagnostics_only_where_there_is_somewhere_to_go()
    {
        Assert.Equal(Setup.RuntimeVerb.ViewDiagnostics,
            Setup.RuntimeRules.FooterFor(RuntimePhase.Failed, Setup.RuntimeCatalog.Failed, 0, canNavigate: true).Link2);
        Assert.Equal(Setup.RuntimeVerb.None,
            Setup.RuntimeRules.FooterFor(RuntimePhase.Failed, Setup.RuntimeCatalog.Failed, 0, canNavigate: false).Link2);
    }

    [Fact]
    public void Install_enables_only_with_a_loaded_catalog_that_offers_something()
    {
        Assert.True(Setup.RuntimeRules.FooterFor(RuntimePhase.Advanced, Setup.RuntimeCatalog.Loaded, 2, true).AccentEnabled);
        Assert.False(Setup.RuntimeRules.FooterFor(RuntimePhase.Advanced, Setup.RuntimeCatalog.Loaded, 0, true).AccentEnabled);
        Assert.False(Setup.RuntimeRules.FooterFor(RuntimePhase.Advanced, Setup.RuntimeCatalog.Fetching, 2, true).AccentEnabled);
    }

    [Fact]
    public void The_banner_needs_something_to_say_and_nobody_else_asking()
    {
        Assert.True(Setup.RuntimeRules.ShowsBanner(Setup.RuntimeIssue.Missing, false, false, false));
        Assert.False(Setup.RuntimeRules.ShowsBanner(Setup.RuntimeIssue.None, false, false, false));   // never flashes
        Assert.False(Setup.RuntimeRules.ShowsBanner(Setup.RuntimeIssue.Missing, dismissed: true, false, false));
        Assert.False(Setup.RuntimeRules.ShowsBanner(Setup.RuntimeIssue.Missing, false, wizardCovering: true, false));
        Assert.False(Setup.RuntimeRules.ShowsBanner(Setup.RuntimeIssue.Missing, false, false, setupPending: true));
    }

    [Fact]
    public void The_four_banner_messages()
    {
        Assert.Equal(Strings.Playback.Runtime.Missing, Setup.RuntimeRules.BannerLocKey(Setup.RuntimeIssue.Missing));
        Assert.Equal(Strings.Playback.Runtime.WrongArch, Setup.RuntimeRules.BannerLocKey(Setup.RuntimeIssue.WrongArch));
        Assert.Equal(Strings.Playback.Runtime.NoPack, Setup.RuntimeRules.BannerLocKey(Setup.RuntimeIssue.NoPack));
        Assert.Equal(Strings.Playback.Runtime.Unsupported, Setup.RuntimeRules.BannerLocKey(Setup.RuntimeIssue.Unsupported));
    }

    static Setup.RuntimeFacts Facts(string? subject, Setup.RuntimeTrust signatureTrust, bool pinned,
                                    Setup.RuntimeTrust fileTrust = Setup.RuntimeTrust.Unknown)
        => new(true, Setup.RuntimeIssue.None,
            Signature: subject is null && signatureTrust == Setup.RuntimeTrust.Unknown
                ? null
                : new Setup.RuntimeSignature(subject, "Issuer", signatureTrust, null, default, default, null, null),
            Trust: fileTrust, PinnedFingerprint: pinned);

    [Fact]
    public void The_signature_ladder_names_the_signer_first()
    {
        Assert.Equal(Setup.RuntimeSignatureLine.SignedTrusted,
            Setup.RuntimeRules.SignatureLine(Facts("Spotify AB", Setup.RuntimeTrust.Trusted, pinned: true)));
        Assert.Equal(Setup.RuntimeSignatureLine.SignedPinned,
            Setup.RuntimeRules.SignatureLine(Facts("Spotify AB", Setup.RuntimeTrust.Untrusted, pinned: true)));
        Assert.Equal(Setup.RuntimeSignatureLine.SignedOther,
            Setup.RuntimeRules.SignatureLine(Facts("Spotify AB", Setup.RuntimeTrust.Untrusted, pinned: false)));
    }

    [Fact]
    public void Without_a_signer_the_ladder_falls_to_the_fingerprint_then_the_trust_verdict()
    {
        Assert.Equal(Setup.RuntimeSignatureLine.PinnedFingerprint,
            Setup.RuntimeRules.SignatureLine(Facts(null, Setup.RuntimeTrust.Unknown, pinned: true)));
        Assert.Equal(Setup.RuntimeSignatureLine.VerifiedFingerprint,
            Setup.RuntimeRules.SignatureLine(Facts(null, Setup.RuntimeTrust.Unknown, false, Setup.RuntimeTrust.Trusted)));
        Assert.Equal(Setup.RuntimeSignatureLine.NotTrustedOverride,
            Setup.RuntimeRules.SignatureLine(Facts(null, Setup.RuntimeTrust.Unknown, false, Setup.RuntimeTrust.Untrusted)));
        Assert.Equal(Setup.RuntimeSignatureLine.CheckUnavailable,
            Setup.RuntimeRules.SignatureLine(Facts(null, Setup.RuntimeTrust.Unknown, false, Setup.RuntimeTrust.UnsupportedPlatform)));
        Assert.Equal(Setup.RuntimeSignatureLine.Unknown,
            Setup.RuntimeRules.SignatureLine(Facts(null, Setup.RuntimeTrust.Unknown, false)));
        // A blank subject is no subject.
        Assert.Equal(Setup.RuntimeSignatureLine.Unknown,
            Setup.RuntimeRules.SignatureLine(Facts("  ", Setup.RuntimeTrust.Trusted, false)));
    }
}
