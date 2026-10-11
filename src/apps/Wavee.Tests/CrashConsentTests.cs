// ── Wavee.Tests/CrashConsentTests.cs — the crash prompt's consent model (C15, X38) ────────────────────────────────────
//
// `CrashConsent` decides what this launch's crash bundle does (stay armed behind the wizard, prompt, queue silently, or
// nothing), builds the scrubbed preview, and records the consent a launch prompt's Send leaves. The decision table itself
// is `Crash.ConsentPolicy` (CrashCoreTests); these facts pin the model around it. The arms that enqueue an upload touch the
// outbox on disk and are not driven here. `Crash.Host.ThisLaunch`, `Platform.Settings` and `CrashNoticeThisLaunch` are
// process state, so the class joins the platform collection and restores them.

using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class CrashConsentTests : IDisposable
{
    readonly MemoryAppSettings _store = new();
    readonly Crash.BundleInfo? _thisLaunch = Crash.Host.ThisLaunch;
    readonly bool _crashNotice = ReleaseNotes.CrashNoticeThisLaunch;

    public CrashConsentTests()
    {
        Platform.UseSettings(_store);
        Crash.Host.ThisLaunch = null;
        ReleaseNotes.CrashNoticeThisLaunch = false;
    }

    public void Dispose()
    {
        Crash.Host.ThisLaunch = _thisLaunch;
        ReleaseNotes.CrashNoticeThisLaunch = _crashNotice;
        Platform.UseSettings(null);
    }

    static Crash.BundleInfo Bundle(Crash.Kind kind, string reportId = "3f9ca1e2")
        => new(
            Dir: Path.Combine(Path.GetTempPath(), "wavee-no-such-bundle", reportId),
            Summary: new Crash.Summary(
                ReportId: reportId, InstallId: "8a1d9e0f", Kind: kind, StampUtc: "2026-10-02T13:05:00.000Z",
                Version: "0.3.0", Quad: "0.3.0.41", Commit: "7e209e37", Channel: "stable", Arch: "x64", OsBuild: "26100",
                Gpu: "GPU", GpuTier: "Strong", SoftwareAdapter: false, Packaged: true, Locale: "en-US",
                SessionId: "sess-1", UptimeMs: 12_345, BeforeFirstFrame: false, LastRoute: "home",
                ExceptionType: "System.InvalidOperationException", ExceptionMessage: "boom",
                Rvas: [0x7b1fc6], ModuleBase: 0x140000000, ModuleSize: 0x2000000,
                DebugId: "7E2C1B4A-0000-0000-0000-0000000000AA-1", ExitCode: 0, HasDump: false, DumpBytes: 0),
            StampLocal: new DateTime(2026, 10, 2, 15, 5, 0, DateTimeKind.Unspecified),
            Send: new Crash.SendRecord(Crash.SendState.NotSent, null, null, 0, false),
            HasDump: false,
            DumpBytes: 0);

    // ── the launch decision ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_clean_launch_has_nothing_to_resolve()
    {
        Assert.Null(CrashConsent.ResolveLaunch());
        Assert.False(ReleaseNotes.CrashNoticeThisLaunch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_prompt_waits_behind_the_wizard_and_stays_armed(bool wizardOpen)
    {
        var bundle = Bundle(Crash.Kind.Managed);
        Crash.Host.ThisLaunch = bundle;
        if (wizardOpen) Setup.WizardOpen.Value = true;
        else _store.Set(Platform.Keys.SetupPending, true);

        try
        {
            Assert.True(CrashConsent.DeferredBehindWizard());
            Assert.Null(CrashConsent.ResolveLaunch());

            Assert.Same(bundle, Crash.Host.ThisLaunch);                           // still armed
            Assert.False(ReleaseNotes.CrashNoticeThisLaunch);                      // and the after-update plate is not told to wait
        }
        finally { Setup.WizardOpen.Value = false; }
    }

    [Fact]
    public void Ask_mode_hands_the_bundle_back_to_prompt_and_consumes_the_one_shot()
    {
        var bundle = Bundle(Crash.Kind.Managed);
        Crash.Host.ThisLaunch = bundle;
        _store.Set(Platform.Keys.CrashReporting, (int)Crash.Reporting.Ask);

        var prompt = CrashConsent.ResolveLaunch();

        Assert.Same(bundle, prompt);
        Assert.Null(Crash.Host.ThisLaunch);                                       // one-shot
        Assert.True(ReleaseNotes.CrashNoticeThisLaunch);                           // the after-update plate waits for the next launch
    }

    [Fact]
    public void An_unclean_exit_never_prompts_but_is_still_consumed()
    {
        Crash.Host.ThisLaunch = Bundle(Crash.Kind.UncleanExit);
        _store.Set(Platform.Keys.CrashReporting, (int)Crash.Reporting.Ask);

        Assert.Null(CrashConsent.ResolveLaunch());

        Assert.Null(Crash.Host.ThisLaunch);
        Assert.True(ReleaseNotes.CrashNoticeThisLaunch);
    }

    [Theory]
    [InlineData(Crash.Reporting.Off)]
    [InlineData(Crash.Reporting.Ask)]
    [InlineData(Crash.Reporting.Auto)]
    public void The_effective_mode_is_the_policys_over_the_stored_mode_and_whether_the_build_can_send(Crash.Reporting stored)
    {
        _store.Set(Platform.Keys.CrashReporting, (int)stored);

        Assert.Equal(Crash.ConsentPolicy.Effective(stored, Crash.Uploader.Configured), CrashConsent.EffectiveMode());
    }

    // ── the consent record ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_launch_prompt_stamps_consent_asked()
    {
        CrashConsent.RecordConsent(fromList: false, always: false);

        Assert.True(_store.Get(Platform.Keys.CrashConsentAsked));
        Assert.False(_store.WasWritten(Platform.Keys.CrashReporting));            // "Always send" was not ticked
    }

    [Fact]
    public void Always_send_switches_the_mode_to_automatic()
    {
        CrashConsent.RecordConsent(fromList: false, always: true);

        Assert.Equal((int)Crash.Reporting.Auto, _store.Get(Platform.Keys.CrashReporting));
        Assert.True(_store.Get(Platform.Keys.CrashConsentAsked));
    }

    [Fact]
    public void A_manual_send_from_the_saved_reports_list_records_nothing()
    {
        CrashConsent.RecordConsent(fromList: true, always: true);

        Assert.Equal(0, _store.WrittenCount);
    }

    // ── the preview ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_preview_of_a_bundle_whose_files_are_gone_degrades_to_text_and_never_throws()
    {
        string preview = CrashConsent.ComposePreview(Bundle(Crash.Kind.Managed), Feedback.RedactionRules.None);

        Assert.NotNull(preview);
    }
}
