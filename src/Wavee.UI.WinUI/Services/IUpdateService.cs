using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Services;

public enum UpdateStatus { Idle, Checking, UpdateAvailable, UpToDate, Error }
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
    /// True once Windows App Installer has a newer package available/staged for this
    /// <c>.appinstaller</c> install — the app should nudge the user to restart to apply it.
    /// Always false for Store / Unpackaged installs.
    /// </summary>
    bool IsRestartUpdateReady { get; }

    Task CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks whether the installed MSIX has a newer version staged via its <c>.appinstaller</c>
    /// (<c>Package.CheckUpdateAvailabilityAsync</c>). Shows a "restart to apply" nudge when ready.
    /// No-op for Store / Unpackaged installs. Requires no package-management capability.
    /// </summary>
    Task CheckPackageUpdateAsync(CancellationToken ct = default);

    /// <summary>Restarts the app so Windows applies the staged MSIX update.</summary>
    Task RestartToApplyUpdateAsync();
}