using System;
using Wavee.Core;

namespace Wavee;

/// <summary>§(app update) — the PURE decision behind "was this launch an update, and if so from what version": read
/// <c>WaveeSettings.LastRunVersion</c>, emit the always-on identity log line, decide via
/// <see cref="AppUpdateVersion.IsFirstRunAfterUpdate"/>, arm the after-update plate (<c>ReleaseNotesPendingFrom</c> +
/// <c>ReleaseNotesPreviousVersion</c>) on an update launch, and write <c>LastRunVersion</c> UNCONDITIONALLY on every
/// launch.
///
/// ENGINE-FREE BY CONSTRUCTION (System + <see cref="IAppSettings"/> + <see cref="WaveeVersionInfo"/> +
/// <see cref="IWaveeLog"/> + the pure <see cref="AppUpdateVersion"/> rule — nothing else). That is load-bearing
/// exactly like <see cref="SetupGating"/>: this file is source-included by <c>Wavee.Tests</c> (which has no
/// FluentGpu.Engine reference), so <c>AppLaunchVersionTests</c> drives the REAL arming logic instead of a copy of it.
/// The package-identity string the log line names is taken as a PARAMETER rather than read from
/// <c>FluentGpu.WindowsApi.Packaging.PackageIdentity</c> directly, for the same engine-free reason.
///
/// WHY A SEPARATE FILE. Before this, the arming lived inline in <c>AppInstallerUpdateService</c>'s constructor — the
/// ONE place that ever ran it. A Store-installed build's composition root never constructs that class (see
/// <c>Services.cs</c>'s <c>IsStore</c> branch), so it silently skipped writing <c>LastRunVersion</c> and never armed
/// the "you were updated" plate: <c>AfterUpdateChrome</c> reads <c>ReleaseNotesPendingFrom</c> straight off
/// <see cref="IAppSettings"/>, with no dependency on which updater is live, so a Store build's after-update plate
/// could never open no matter how long <c>ReleaseNotesAutoShow</c> stayed on. The decision moved OUT of the updater
/// and INTO a shape both the Store and AppInstaller composition paths call — BEFORE either updater is constructed —
/// so both install shapes arm identically and <c>LastRunVersion</c> is written exactly once per launch either
/// way.</summary>
static class AppLaunchVersion
{
    const string LogCategory = "update";

    /// <summary>Runs once per process launch, before either updater is constructed. Reads the previous run's
    /// version, logs the always-on identity line (see the ordering note below), decides whether this launch is an
    /// update, and — when it is, and the build is not a dev build — arms the after-update plate. Writes
    /// <c>LastRunVersion</c> UNCONDITIONALLY before returning, so the write happens exactly once per launch no
    /// matter which branch was taken.
    ///
    /// <para>The log line is emitted BEFORE the decision, deliberately: "was this launch an update?" is answered
    /// from exactly three facts (the running package identity, the previous <c>LastRunVersion</c>, and the
    /// unpackaged app-data root), and when the answer is wrong — as in the local end-to-end run where the first
    /// process of a new build saw an EMPTY <c>lastRun</c>, so no "updated: A -&gt; B" line was written and
    /// <c>previousVersion</c> stayed blank — the only way to tell WHICH fact moved is to have recorded all three at
    /// the moment they were read.</para></summary>
    /// <param name="settings">The persisted settings store.</param>
    /// <param name="me">This build's parsed identity.</param>
    /// <param name="log">The app log.</param>
    /// <param name="packageIdentity">The running package's full name, or an app-chosen sentinel (e.g.
    /// "unpackaged") when there is none. Named by the caller — see the class remarks for why this is a plain string
    /// rather than a read of <c>PackageIdentity</c> here.</param>
    /// <returns>The version this launch updated FROM, or "" when this launch was not an update (a first-ever
    /// launch, an unchanged version, or a dev build).</returns>
    public static string Arm(IAppSettings settings, WaveeVersionInfo me, IWaveeLog log, string packageIdentity)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(me);
        ArgumentNullException.ThrowIfNull(log);

        string lastRun = settings.Get(WaveeSettings.LastRunVersion);

        log.Info(LogCategory, "identity: " + (packageIdentity is { Length: > 0 } pfn ? pfn : "unpackaged")
            + "; lastRun='" + lastRun + "'; appData="
            + System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee"));

        string from = "";
        if (!me.IsDev && AppUpdateVersion.IsFirstRunAfterUpdate(lastRun, me.LastRunKey))
        {
            from = lastRun;
            // The after-update plate needs to know where the user came FROM, and this is the only moment that value
            // exists. It is written TWICE, on purpose, because the two readers have opposite lifetimes:
            //   pendingFrom      — the ARMING flag for the automatic plate. A one-shot: AfterUpdateDialog.Open clears
            //                      it whether or not the user reads the plate, so it never re-raises itself.
            //   previousVersion  — the same value as a FACT about this install, never cleared by anyone. Settings ›
            //                      About's "Show the update summary again" and the local E2E harness both need the
            //                      from-quad long after the one-shot has been consumed.
            settings.Set(WaveeSettings.ReleaseNotesPendingFrom, lastRun);
            settings.Set(WaveeSettings.ReleaseNotesPreviousVersion, lastRun);
            log.Info(LogCategory, "updated: " + lastRun + " -> " + me.LastRunKey);
        }
        settings.Set(WaveeSettings.LastRunVersion, me.LastRunKey);
        return from;
    }
}
