using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Services;

public enum UpdateStatus { Idle, Checking, UpdateAvailable, Downloading, UpToDate, Error }
public enum DistributionMode { Store, Sideloaded, Unpackaged }

/// <summary>
/// Checks for app updates via GitHub Releases and exposes version/changelog state.
/// </summary>
public interface IUpdateService : INotifyPropertyChanged
{
    UpdateStatus Status { get; }
    string CurrentVersion { get; }
    string? LatestVersion { get; }
    string? Changelog { get; }
    string? ReleaseUrl { get; }
    string? ErrorMessage { get; }
    DateTimeOffset? LastChecked { get; }
    DistributionMode Distribution { get; }
    bool IsUpdateAvailable { get; }

    /// <summary>
    /// True once a newer package has been genuinely DOWNLOADED + STAGED for this
    /// <c>.appinstaller</c> install, so a restart will apply it. Always false for
    /// Store / Unpackaged installs.
    /// </summary>
    bool IsRestartUpdateReady { get; }

    /// <summary>
    /// When true, an available update is downloaded + staged automatically so a restart
    /// applies it. Persisted as <c>AppSettings.AutoUpdate</c>. Sideloaded installs only.
    /// </summary>
    bool IsAutoUpdateEnabled { get; set; }

    /// <summary>True while an MSIX update is being downloaded + staged.</summary>
    bool IsDownloadingUpdate { get; }

    /// <summary>Download/stage progress in [0, 1] while <see cref="IsDownloadingUpdate"/> is true.</summary>
    double DownloadProgress { get; }

    Task CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// Actively downloads + stages the newer MSIX from this install's <c>.appinstaller</c> via
    /// <c>PackageManager.AddPackageByAppInstallerFileAsync</c> (instead of waiting on Windows App
    /// Installer's own schedule). On success the staged package applies on the next restart and
    /// <see cref="IsRestartUpdateReady"/> is set. No-op (false) for Store / Unpackaged installs.
    /// Self-update of the same package family needs no package-management capability.
    /// </summary>
    Task<bool> DownloadAndStageUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks whether the installed MSIX has a newer version staged via its <c>.appinstaller</c>
    /// (<c>Package.CheckUpdateAvailabilityAsync</c>). Shows a "restart to apply" nudge when ready.
    /// No-op for Store / Unpackaged installs. Requires no package-management capability.
    /// </summary>
    Task CheckPackageUpdateAsync(CancellationToken ct = default);

    /// <summary>Restarts the app so Windows applies the staged MSIX update.</summary>
    Task RestartToApplyUpdateAsync();
}