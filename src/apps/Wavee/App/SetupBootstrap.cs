namespace Wavee;

/// <summary>Startup-only setup-wizard arming, run ONCE per install from <c>Program.Main</c> BEFORE anything constructs
/// <c>Services</c> / opens <c>library.db</c> — the exact <see cref="SidebarBootstrap"/> ordering rule, for the exact
/// same reason: <c>WaveeApp</c>'s constructor synchronously calls <c>Services.CreateReal</c>, which opens/creates
/// <c>library.db</c>, so probing after that point would make every install look "existing". <c>Program.cs</c> cannot
/// show FluentGpu UI this early (there is no window yet), so this writes settings only — the wizard itself is shown
/// later by the shell, once it has painted, gated on <c>WaveeSettings.SetupPending</c>.
///
/// Reuses <see cref="SidebarBootstrap.IsFreshInstall"/> rather than a second fresh-install detector: there is exactly
/// one answer to "is this a fresh install", shared by the sidebar chooser and the setup wizard, and a second detector
/// is how the two features end up disagreeing about the same install.</summary>
static class SetupBootstrap
{
    /// <summary>Monotonic "has the setup wizard been armed/settled for this install" guard. Bump this AND add the new
    /// work to <see cref="Run"/> if a future release needs another one-time setup-wizard startup step.</summary>
    public const int TargetVersion = 1;

    /// <param name="localAppDataOverride">Test seam only: the directory to probe instead of the real
    /// <c>%LOCALAPPDATA%</c> — forwarded verbatim to <see cref="SidebarBootstrap.IsFreshInstall"/>. Production passes
    /// null.</param>
    public static void Run(IAppSettings settings, string? localAppDataOverride = null, IWaveeLog? log = null)
    {
        log ??= WaveeLog.Instance;

        // Re-probed on EVERY launch — not gated by SetupBootstrapVersion below, which only guards the ONE-TIME arm/
        // suppress decision a genuine first run makes. The data folder (%LOCALAPPDATA%\Wavee) can disappear on ANY
        // launch of an already-migrated install (the user wipes it, restores an image, migrates the registry to a
        // new PC without the data root); when it has, IsFreshInstall reads this launch as a first run even though
        // the registry still remembers a completed wizard — see SetupGating.NeedsFreshInstallReset.
        bool fresh = SidebarBootstrap.IsFreshInstall(settings, localAppDataOverride, log);
        ResetIfDataFolderGone(settings, fresh, log);

        if (settings.Get(WaveeSettings.SetupBootstrapVersion) >= TargetVersion) { RearmForTerms(settings, log); return; }

        if (fresh)
        {
            settings.Set(WaveeSettings.SetupPending, true);
            settings.Set(WaveeSettings.SetupCompleted, false);
            // The setup wizard is Welcome/Sign in/Local playback only now — it no longer has a Sidebar step, but the
            // separate one-time sidebar-design chooser popup must still stay suppressed on a fresh install: one
            // onboarding prompt, not two, on the very first launch. SidebarBootstrap already defaulted the design to
            // Classic (switchable later from the sidebar's own layout menu); this only suppresses the popup chooser,
            // it does not touch the chosen design.
            settings.Set(WaveeSettings.SidebarOnboardingSeen, true);
        }
        else
        {
            // Existing installs never retro-fit the wizard onto someone mid-use.
            settings.Set(WaveeSettings.SetupCompleted, true);
            settings.Set(WaveeSettings.SetupPending, false);
        }

        settings.Set(WaveeSettings.SetupBootstrapVersion, TargetVersion);
        log.Info("setup", "setup.bootstrap",
            fresh ? "Fresh install: first-run setup wizard armed." : "Existing install: first-run setup wizard suppressed.",
            WaveeLogField.Of("fresh", fresh));

        RearmForTerms(settings, log);
    }

    /// <summary>A wiped/missing DATA folder (<c>%LOCALAPPDATA%\Wavee</c>) on an install whose REGISTRY settings still
    /// claim a finished wizard (<see cref="SetupGating.NeedsFreshInstallReset"/>) is treated as a fresh install: the
    /// registry's memory of "already set up" is stale, so it is cleared right along with everything else that memory
    /// would otherwise suppress (Welcome/EULA, the "Is this you?" reauth shortcut, the local-playback offer). Runs on
    /// EVERY launch, same as <see cref="RearmForTerms"/> — not just the once-per-install migration above — because the
    /// data folder can vanish on any launch of an already-migrated install.</summary>
    static void ResetIfDataFolderGone(IAppSettings settings, bool fresh, IWaveeLog log)
    {
        bool completed = settings.Get(WaveeSettings.SetupCompleted);
        int termsAccepted = settings.Get(WaveeSettings.TermsAcceptedVersion);
        if (!SetupGating.NeedsFreshInstallReset(fresh, completed, termsAccepted)) return;

        settings.Set(WaveeSettings.SetupPending, true);
        settings.Set(WaveeSettings.SetupCompleted, false);
        settings.Set(WaveeSettings.TermsAcceptedVersion, 0);
        log.Info("setup", "setup.bootstrap.reset", "Data folder is gone — treating as a fresh install",
            WaveeLogField.Of("terms_accepted_was", termsAccepted));
    }

    /// <summary>Re-arm a COMPLETED install whose recorded terms acceptance predates this build
    /// (<see cref="SetupGating.TermsVersion"/>). Runs on EVERY launch — not once per <see cref="TargetVersion"/> — because
    /// the trigger is a shipped terms revision, not a one-time migration: bumping the terms version has to reach installs
    /// that already burned <c>SetupBootstrapVersion</c> long ago. The wizard's own Terms page writes the new version on
    /// Accept, which is what stops this from re-arming again on the next launch. Idempotent: a pending wizard stays
    /// pending, and an up-to-date acceptance writes nothing at all.</summary>
    static void RearmForTerms(IAppSettings settings, IWaveeLog log)
    {
        int accepted = settings.Get(WaveeSettings.TermsAcceptedVersion);
        if (SetupGating.GrandfathersTerms(settings.Get(WaveeSettings.SetupCompleted), accepted))
        {
            settings.Set(WaveeSettings.TermsAcceptedVersion, SetupGating.TermsVersion);   // completed before versioning existed
            return;
        }
        if (!SetupGating.NeedsTermsRearm(settings.Get(WaveeSettings.SetupCompleted), accepted, SetupGating.TermsVersion)) return;
        if (settings.Get(WaveeSettings.SetupPending)) return;   // already armed — nothing to say or write

        settings.Set(WaveeSettings.SetupPending, true);
        log.Info("setup", "setup.terms.rearm",
            "Terms revision changed since this install accepted; re-arming the setup wizard.",
            WaveeLogField.Of("accepted", accepted), WaveeLogField.Of("required", SetupGating.TermsVersion));
    }
}
