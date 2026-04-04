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

    Task CheckForUpdateAsync(CancellationToken ct = default);
}
