using System;
using System.IO;
using Wavee.Backend.Persistence;

namespace Wavee;

/// <summary>Startup-only sidebar work, run ONCE per install from <c>Program.Main</c> BEFORE anything constructs
/// <c>Services</c> / opens <c>library.db</c> (F.4.1): (1) fold the legacy global pane keys into Classic's bag (F.3.3),
/// (2) decide whether this is a FRESH install and, if so, default it to Spotify Classic and arm the one-time chooser.
/// Settings writes only — no UI, no directory creation, no disk mutation.
///
/// The ordering is load-bearing: <c>WaveeApp</c>'s constructor synchronously calls <c>Services.CreateReal</c>, which
/// opens/creates <c>library.db</c>. Probing after that point would always see the file and EVERY install would look
/// "existing". <c>Program.cs</c> cannot show FluentGpu UI (there is no window yet), so this writes settings only — the
/// chooser is scheduled later by <c>SidebarOnboardingChrome</c>, and <c>WaveeSettings.SidebarOnboardingSeen</c> IS the
/// hand-off (no static field).</summary>
static class SidebarBootstrap
{
    /// <summary>Monotonic "which sidebar startup migrations have run". Bump this AND add the new work to <see cref="Run"/>
    /// when a future release needs another one-time startup step.</summary>
    public const int TargetVersion = 1;

    /// <param name="localAppDataOverride">Test seam only: the directory to probe instead of the real
    /// <c>%LOCALAPPDATA%</c>. Production passes null. (<c>Environment.SpecialFolder.LocalApplicationData</c> is not
    /// injectable on Windows, so the fresh-install truth table cannot be exercised without this.)</param>
    public static void Run(IAppSettings settings, string? localAppDataOverride = null, IWaveeLog? log = null)
    {
        if (settings.Get(WaveeSettings.SidebarBootstrapVersion) >= TargetVersion) return;
        log ??= WaveeLog.Instance;

        MigrateLegacyPaneKeys(settings);

        bool fresh = IsFreshInstall(settings, localAppDataOverride, log);
        if (fresh)
        {
            settings.Set(WaveeSettings.SidebarDesign, (int)SidebarDesign.Classic);
            settings.Set(WaveeSettings.SidebarOnboardingSeen, false);                // the chooser will show ONCE
        }
        else
        {
            // Existing installs stay on Classic (the key's default 0 — deliberately NOT written, so a user who already
            // changed it in a newer build is not stomped) and never see the chooser.
            settings.Set(WaveeSettings.SidebarOnboardingSeen, true);
        }

        settings.Set(WaveeSettings.SidebarBootstrapVersion, TargetVersion);
        log.Info("sidebar", "sidebar.bootstrap",
            fresh ? "Fresh install: Spotify Classic sidebar chooser armed." : "Existing install: sidebar chooser suppressed.",
            WaveeLogField.Of("fresh", fresh));
    }

    /// <summary>v0 → v1: fold the single GLOBAL pane state into CLASSIC's bag. Classic is the design every existing
    /// install stays on, so its remembered width/collapse must be exactly what the user had. The legacy keys are LEFT IN
    /// PLACE (never deleted): a downgrade to an older build must still find a sane pane width. V3/Curated bags are NOT
    /// seeded — their keys' own defaults are their first-run values.</summary>
    internal static void MigrateLegacyPaneKeys(IAppSettings settings)
    {
        settings.Set(SidebarKeys.Width(SidebarDesign.Classic), settings.Get(WaveeSettings.SidebarWidth));
        settings.Set(SidebarKeys.Collapsed(SidebarDesign.Classic), settings.Get(WaveeSettings.SidebarCollapsed));
        settings.Set(SidebarKeys.WidthUserSet(SidebarDesign.Classic), settings.Get(WaveeSettings.SidebarWidthUserSet));
    }

    /// <summary>Fresh iff NONE of the four "this app has run before" witnesses exists. Pure filesystem/settings reads — no
    /// directory is created, no file is opened for write, <c>library.db</c> is never touched.
    ///
    /// Why four witnesses, not one: <c>library.db</c> alone is wrong for a fake-backend/demo run (<c>Services.CreateFake</c>
    /// never creates it) and for a user who deleted their cache; credentials alone is wrong for a never-signed-in user who
    /// nonetheless used the app; <c>history.json</c> is the strongest generic "has run" marker; the settings witnesses
    /// catch a user who ran an older build with a different data root.
    ///
    /// SHARED with <see cref="SetupBootstrap"/> (already <c>internal static</c>, so no widening needed): "is this a
    /// fresh install" is one decision, and the setup wizard must never disagree with the sidebar chooser about it.</summary>
    internal static bool IsFreshInstall(IAppSettings settings, string? localAppDataOverride = null, IWaveeLog? log = null)
    {
        log ??= WaveeLog.Instance;
        string local = localAppDataOverride
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 1 — the backend account database (Services.CreateReal's path, recomputed identically)
        if (Exists(Path.Combine(local, "Wavee", "library.db"), log, "library_db")) return false;

        // 2 — stored Spotify credentials (LocalCredentialStore over %LOCALAPPDATA%\Wavee\store.json)
        if (HasStoredCredential(Path.Combine(local, "Wavee", "store.json"), log)) return false;

        // 3 — the navigation log (written on the FIRST launch by WaveeShell's ctor, so absent on a true first run)
        if (Exists(Path.Combine(local, "Wavee", "WaveeMusic", "history.json"), log, "history")) return false;

        // 4 — any pre-existing sidebar preference from a build that predates this feature
        if (settings.Get(WaveeSettings.SidebarWidthUserSet)) return false;
        if (settings.Get(WaveeSettings.SidebarCollapsed)) return false;

        return true;
    }

    static bool Exists(string path, IWaveeLog log, string witness)
    {
        try { return File.Exists(path); }
        catch (Exception ex)
        {
            log.Warn("sidebar", "sidebar.bootstrap.probe_failed",
                "A sidebar install-state witness could not be inspected.",
                WaveeLogField.Of("witness", witness),
                WaveeLogField.Of("exception_type", ex.GetType().Name));
            return false;
        }
    }

    /// <summary>True when <c>store.json</c> carries the <c>LocalCredentialStore</c> key. Reads the raw JSON through the
    /// same <c>FileLocalStore</c> accessor the credential store uses, constructed with an EXPLICIT path (never
    /// <c>ForApp</c>, whose <c>Directory.CreateDirectory</c> side effect would itself create the data root and make the
    /// next launch look "existing"). A missing/corrupt file simply reads as no credential.</summary>
    static bool HasStoredCredential(string storePath, IWaveeLog log)
    {
        if (!Exists(storePath, log, "credentials_file")) return false;
        try { return new FileLocalStore(storePath).Get(LocalCredentialStore.CredentialKey) is { Length: > 0 }; }
        catch (Exception ex)
        {
            log.Warn("sidebar", "sidebar.bootstrap.probe_failed",
                "The credential witness could not be inspected.",
                WaveeLogField.Of("witness", "credentials"),
                WaveeLogField.Of("exception_type", ex.GetType().Name));
            return false;
        }
    }
}
