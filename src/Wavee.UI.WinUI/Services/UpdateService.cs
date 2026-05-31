using System;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Services;

public sealed partial class UpdateService : IUpdateService
{
    // /releases (list) NOT /releases/latest: the "latest" endpoint silently
    // excludes pre-releases, and every Wavee build is a pre-release (alpha/beta/rc),
    // so it always 404'd and the in-app updater never saw a new build. The list
    // endpoint returns all releases (newest first) including pre-releases.
    private const string GitHubReleasesUrl = "https://api.github.com/repos/christosk92/WaveeMusic/releases?per_page=30";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settingsService;
    private readonly INotificationService? _notificationService;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private UpdateStatus _status = UpdateStatus.Idle;
    private string? _latestVersion;
    private string? _changelog;
    private string? _releaseUrl;
    private string? _errorMessage;
    private DateTimeOffset? _lastChecked;
    private bool _isUpdateAvailable;
    private bool _isRestartUpdateReady;
    private bool _isAutoUpdateEnabled;
    private bool _isDownloadingUpdate;
    private double _downloadProgress;

    // Serializes staging so the startup check and the Settings OnAboutLoaded re-check can't both
    // kick AddPackageByAppInstallerFileAsync for the same update.
    private readonly SemaphoreSlim _stageLock = new(1, 1);

    // Per-arch fallback when GetAppInstallerInfo() can't supply the channel URI (e.g. the package
    // was installed by an older client). Mirrors the experimental asset names emitted by
    // signing/Generate-AppInstaller.ps1 — keep in sync with signing/Wavee.appinstaller.template.
    private const string ExperimentalAppInstallerUrlBase =
        "https://github.com/christosk92/WaveeMusic/releases/download/experimental-latest/Wavee.Experimental.";

    public UpdateService(
        IHttpClientFactory httpClientFactory,
        ISettingsService settingsService,
        INotificationService? notificationService = null,
        ILogger<UpdateService>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;
        _notificationService = notificationService;
        _logger = logger;

        // Detect distribution mode and current version
        DetectDistribution();

        // Restore last checked time
        _lastChecked = _settingsService.Settings.LastUpdateCheck;
        _isAutoUpdateEnabled = _settingsService.Settings.AutoUpdate;

        // Fire-and-forget initial check after a short delay to let the app settle. The work
        // lives in an async method (not Task.Delay(...).ContinueWith(async _ => ...)) so the
        // inner task's exceptions are observed there instead of being rethrown by the finalizer
        // as an unobserved task exception.
        _ = RunStartupUpdateChecksAsync();
    }

    private async Task RunStartupUpdateChecksAsync()
    {
        // Detached startup flow. Every fault is caught and logged here so it can never escape
        // as an unobserved task exception. UI-affecting work marshals to the UI thread on its own
        // (PropertyChanged via SetField, toasts via NotificationService.Show), so the awaits
        // intentionally do not capture context.
        try
        {
            // Let the app settle before the first check.
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            // GitHub-sourced version/changelog drives the Settings "what's new" surface
            // regardless of how the app was installed.
            await CheckForUpdateAsync().ConfigureAwait(false);

            switch (Distribution)
            {
                case DistributionMode.Sideloaded:
                    // Real auto-update path: Windows App Installer silently downloads + stages
                    // the new MSIX from the .appinstaller; this nudges the user to restart.
                    await CheckPackageUpdateAsync().ConfigureAwait(false);
                    break;
                case DistributionMode.Unpackaged:
                    // Dev build — no in-place update channel; point at the release page if newer.
                    if (IsUpdateAvailable)
                        ShowUpdateAvailableNudge();
                    break;
                // Store: the Microsoft Store handles updates; no in-app nudge.
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Startup update check failed");
        }
    }

    private void ShowUpdateAvailableNudge()
    {
        _notificationService?.Show(new NotificationInfo
        {
            Message = AppLocalization.Format("Update_AvailableMessage", LatestVersion),
            Severity = NotificationSeverity.Informational,
            AutoDismissAfter = TimeSpan.FromSeconds(8),
            ActionLabel = AppLocalization.GetString("Update_View"),
            Action = async () =>
            {
                if (ReleaseUrl != null)
                    await Windows.System.Launcher.LaunchUriAsync(new Uri(ReleaseUrl));
            }
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public UpdateStatus Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string CurrentVersion { get; private set; } = "0.0.0";

    public string? LatestVersion
    {
        get => _latestVersion;
        private set => SetField(ref _latestVersion, value);
    }

    public string? Changelog
    {
        get => _changelog;
        private set => SetField(ref _changelog, value);
    }

    public string? ReleaseUrl
    {
        get => _releaseUrl;
        private set => SetField(ref _releaseUrl, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetField(ref _errorMessage, value);
    }

    public DateTimeOffset? LastChecked
    {
        get => _lastChecked;
        private set
        {
            if (SetField(ref _lastChecked, value))
                _settingsService.Update(s => s.LastUpdateCheck = value);
        }
    }

    public DistributionMode Distribution { get; private set; }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => SetField(ref _isUpdateAvailable, value);
    }

    public bool IsRestartUpdateReady
    {
        get => _isRestartUpdateReady;
        private set => SetField(ref _isRestartUpdateReady, value);
    }

    public bool IsAutoUpdateEnabled
    {
        get => _isAutoUpdateEnabled;
        set
        {
            if (SetField(ref _isAutoUpdateEnabled, value))
                _settingsService.Update(s => s.AutoUpdate = value);
        }
    }

    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        private set => SetField(ref _isDownloadingUpdate, value);
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetField(ref _downloadProgress, value);
    }

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (!await _checkLock.WaitAsync(0, ct))
            return; // Already checking

        try
        {
            Status = UpdateStatus.Checking;
            ErrorMessage = null;

            var client = _httpClientFactory.CreateClient("Wavee");
            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubReleasesUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd($"Wavee/{CurrentVersion}");

            var response = await client.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Repo/endpoint not found — treat as "no update" rather than a startup error.
                ReportNoUpdate("releases endpoint returned 404");
                return;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Channel-aware selection over the full release list. A pre-release
            // build (current version carries a -alpha/-beta/-rc tag) accepts ANY
            // non-draft release — a stable release ranks above a pre-release of the
            // same core version, so it's still a valid "newer". A stable build only
            // accepts stable releases (never offers a pre-release as an update).
            var currentParsed = TryParseReleaseVersion(CurrentVersion, out var current);
            var includePrereleases = !currentParsed || current.Prerelease is not null;

            JsonElement best = default;
            ReleaseVersion bestVersion = default;
            var haveBest = false;
            var considered = 0;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in root.EnumerateArray())
                {
                    if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                        continue;

                    var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                    if (!TryParseReleaseVersion(tag, out var ver))
                        continue;

                    if (!includePrereleases && ver.Prerelease is not null)
                        continue;

                    considered++;
                    if (!haveBest || CompareReleaseVersions(ver, bestVersion) > 0)
                    {
                        best = rel;
                        bestVersion = ver;
                        haveBest = true;
                    }
                }
            }

            if (!haveBest)
            {
                ReportNoUpdate($"no applicable releases (includePrereleases={includePrereleases})");
                return;
            }

            var rawVersion = (best.TryGetProperty("tag_name", out var tn) ? tn.GetString() ?? "" : "").TrimStart('v', 'V');
            LatestVersion = rawVersion;
            Changelog = best.TryGetProperty("body", out var body) ? body.GetString() : null;
            ReleaseUrl = best.TryGetProperty("html_url", out var url) ? url.GetString() : null;

            IsUpdateAvailable = currentParsed && CompareReleaseVersions(bestVersion, current) > 0;
            Status = IsUpdateAvailable ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate;
            LastChecked = DateTimeOffset.UtcNow;

            _logger?.LogInformation(
                "Update check: current={Current}, latest={Latest}, considered={Considered}, available={Available}",
                CurrentVersion, LatestVersion, considered, IsUpdateAvailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Update check failed");
            Status = UpdateStatus.Error;
            ErrorMessage = ex.Message;

            // Safe from the startup threadpool continuation: NotificationService.Show marshals
            // to the UI thread itself.
            _notificationService?.Show(new NotificationInfo
            {
                Message = AppLocalization.GetString("Update_CheckFailed"),
                Severity = NotificationSeverity.Error,
                AutoDismissAfter = TimeSpan.FromSeconds(8),
                ActionLabel = AppLocalization.GetString("Retry"),
                Action = () => CheckForUpdateAsync()
            });
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public async Task CheckPackageUpdateAsync(CancellationToken ct = default)
    {
        // Only sideloaded (.appinstaller) installs have a Windows-managed update channel.
        // Store updates itself; unpackaged dev builds have no package at all.
        if (Distribution != DistributionMode.Sideloaded)
            return;

        PackageUpdateAvailability availability;
        try
        {
            // CheckUpdateAvailabilityAsync inspects the install's .appinstaller channel and
            // reports whether App Installer has staged (or can fetch) a newer package. It needs
            // NO package-management capability — unlike the PackageManager add/stage APIs.
            var result = await Package.Current.CheckUpdateAvailabilityAsync();
            ct.ThrowIfCancellationRequested();
            availability = result.Availability;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Throws when the package wasn't installed from an .appinstaller, or the channel is
            // unreachable. Treat as "no in-place update" — the GitHub check still covers "what's new".
            _logger?.LogDebug(ex, "CheckUpdateAvailabilityAsync failed (treating as no update)");
            return;
        }

        var ready = availability is PackageUpdateAvailability.Available
                                 or PackageUpdateAvailability.Required;
        if (!ready)
        {
            _logger?.LogDebug("Package update check: none staged (availability={Availability})", availability);
            return;
        }

        if (IsRestartUpdateReady)
            return; // already staged + nudged this session

        if (IsAutoUpdateEnabled)
        {
            // Actively download + stage now instead of waiting on Windows App Installer's own
            // schedule. On success this sets IsRestartUpdateReady and posts the restart nudge.
            _logger?.LogInformation(
                "Newer MSIX available (availability={Availability}); auto-staging via PackageManager",
                availability);
            await DownloadAndStageUpdateAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // Auto-update off: don't claim it's already downloaded — offer download + restart.
            _logger?.LogInformation(
                "Newer MSIX available (availability={Availability}); auto-update off, offering download",
                availability);
            ShowDownloadAvailableNudge();
        }
    }

    private void ShowRestartReadyNudge()
    {
        IsRestartUpdateReady = true;
        _notificationService?.Show(new NotificationInfo
        {
            Message = AppLocalization.GetString("Update_RestartReadyMessage"),
            Severity = NotificationSeverity.Informational,
            AutoDismissAfter = TimeSpan.FromSeconds(12),
            ActionLabel = AppLocalization.GetString("Update_RestartNow"),
            Action = RestartToApplyUpdateAsync
        });
    }

    private void ShowDownloadAvailableNudge()
    {
        _notificationService?.Show(new NotificationInfo
        {
            Message = AppLocalization.Format("Update_AvailableDownloadMessage", LatestVersion),
            Severity = NotificationSeverity.Informational,
            AutoDismissAfter = TimeSpan.FromSeconds(10),
            ActionLabel = AppLocalization.GetString("Update_DownloadAndRestart"),
            Action = async () =>
            {
                if (await DownloadAndStageUpdateAsync().ConfigureAwait(false))
                    await RestartToApplyUpdateAsync().ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// The <c>.appinstaller</c> URI this package updates from — the OS-recorded value
    /// (<c>Package.GetAppInstallerInfo</c>), falling back to the per-arch experimental constant.
    /// Null when neither resolves (e.g. not installed from an .appinstaller).
    /// </summary>
    private Uri? ResolveAppInstallerUri()
    {
        try
        {
            if (Package.Current.GetAppInstallerInfo()?.Uri is { } uri)
                return uri;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "GetAppInstallerInfo failed; falling back to per-arch constant");
        }

        var arch = Package.Current.Id.Architecture switch
        {
            Windows.System.ProcessorArchitecture.X64 => "x64",
            Windows.System.ProcessorArchitecture.Arm64 => "arm64",
            _ => null
        };
        if (arch is null)
        {
            _logger?.LogWarning("No .appinstaller URI and unsupported arch {Arch}", Package.Current.Id.Architecture);
            return null;
        }
        return new Uri($"{ExperimentalAppInstallerUrlBase}{arch}.appinstaller");
    }

    public async Task<bool> DownloadAndStageUpdateAsync(CancellationToken ct = default)
    {
        if (Distribution != DistributionMode.Sideloaded)
            return false;
        if (IsRestartUpdateReady)
            return true; // already staged this session
        if (!await _stageLock.WaitAsync(0, ct).ConfigureAwait(false))
            return false; // a stage is already in flight

        try
        {
            var uri = ResolveAppInstallerUri();
            if (uri is null)
                return false;

            Status = UpdateStatus.Downloading;
            IsDownloadingUpdate = true;
            DownloadProgress = 0;
            ErrorMessage = null;

            var packageManager = new Windows.Management.Deployment.PackageManager();
            // None: registration of the newer package DEFERS while this one is in use and commits
            // on the next launch (zero processes); we apply on demand via the AudioHost-kill
            // restart. Self-update of the same package family needs no packageManagement
            // capability (runFullTrust suffices) — Microsoft "non-store-developer-updates".
            var operation = packageManager.AddPackageByAppInstallerFileAsync(
                uri,
                Windows.Management.Deployment.AddPackageByAppInstallerOptions.None,
                null);
            operation.Progress = (_, p) => DownloadProgress = p.percentage / 100.0;

            var result = await operation.AsTask(ct).ConfigureAwait(false);

            if (result.ExtendedErrorCode is { HResult: not 0 } err)
            {
                _logger?.LogWarning("Update stage failed: hr=0x{Hr:X8} {ErrorText}", err.HResult, result.ErrorText);
                Status = UpdateStatus.Error;
                ErrorMessage = string.IsNullOrWhiteSpace(result.ErrorText)
                    ? AppLocalization.GetString("Update_DownloadFailed")
                    : result.ErrorText;
                return false;
            }

            _logger?.LogInformation("MSIX update staged via PackageManager; will apply on next restart");
            Status = UpdateStatus.UpdateAvailable;
            IsRestartUpdateReady = true;
            ShowRestartReadyNudge();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Access-denied (capability), network, signature, downgrade — degrade to the passive
            // App-Installer path; the user is no worse off than before.
            _logger?.LogWarning(ex, "DownloadAndStageUpdateAsync failed; falling back to passive update path");
            Status = UpdateStatus.Error;
            ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsDownloadingUpdate = false;
            DownloadProgress = 0;
            _stageLock.Release();
        }
    }

    public async Task RestartToApplyUpdateAsync()
    {
        try
        {
            // A staged MSIX update is only committed when the package has NO
            // running processes left. The out-of-process audio host
            // (Wavee.AudioHost — same package identity) is deliberately kept
            // alive across UI restarts for the UI-only handoff: its job object
            // uses LimitFlags=0, so it survives the UI process exiting. For an
            // UPDATE restart we want the opposite — a full cold restart of the
            // whole package — so tear the audio host down first. Without this,
            // AppInstance.Restart relaunches the still-registered OLD version
            // (the package never reached zero processes) and the "restart to
            // update" nudge just reappears, which is the bug this fixes.
            var audio = Wavee.UI.WinUI.Helpers.Application.AppLifecycleHelper.AudioProcessManager;
            if (audio is not null)
            {
                try
                {
                    await audio.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to stop audio host before update restart; continuing");
                }
            }

            // Hard process replacement; on success it terminates the current
            // process and never returns. With the audio host gone the package is
            // fully unloaded, so Windows commits the staged MSIX on relaunch.
            var reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
            _logger?.LogWarning("AppInstance.Restart returned without restarting ({Reason})", reason);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Restart to apply update failed");
        }
    }

    private void ReportNoUpdate(string reason)
    {
        IsUpdateAvailable = false;
        LatestVersion = null;
        Changelog = null;
        ReleaseUrl = null;
        Status = UpdateStatus.UpToDate;
        LastChecked = DateTimeOffset.UtcNow;
        _logger?.LogDebug("Update check: up to date ({Reason})", reason);
    }

    private void DetectDistribution()
    {
        var informationalVersion = GetCurrentInformationalVersion();

        try
        {
            var pkg = Package.Current;
            Distribution = pkg.SignatureKind == PackageSignatureKind.Store
                ? DistributionMode.Store
                : DistributionMode.Sideloaded;
            CurrentVersion = informationalVersion;
        }
        catch
        {
            Distribution = DistributionMode.Unpackaged;
            CurrentVersion = informationalVersion;
        }
    }

    private static string GetCurrentInformationalVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        var buildMetadataIndex = version.IndexOf('+');
        return buildMetadataIndex >= 0 ? version[..buildMetadataIndex] : version;
    }

    private static bool TryParseReleaseVersion(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = value.Trim().TrimStart('v', 'V');
        var buildMetadataIndex = normalized.IndexOf('+');
        if (buildMetadataIndex >= 0)
            normalized = normalized[..buildMetadataIndex];

        var prereleaseIndex = normalized.IndexOf('-');
        var core = prereleaseIndex >= 0 ? normalized[..prereleaseIndex] : normalized;
        var prerelease = prereleaseIndex >= 0 ? normalized[(prereleaseIndex + 1)..] : null;
        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts.Length > 4) return false;

        if (!TryParseVersionPart(parts[0], out var major) ||
            !TryParseVersionPart(parts[1], out var minor) ||
            !TryParseVersionPart(parts[2], out var patch))
        {
            return false;
        }

        var revision = 0;
        if (parts.Length == 4 && !TryParseVersionPart(parts[3], out revision))
            return false;

        version = new ReleaseVersion(major, minor, patch, revision, prerelease);
        return true;
    }

    private static bool TryParseVersionPart(string value, out int part)
        => int.TryParse(value, out part) && part >= 0;

    private static int CompareReleaseVersions(ReleaseVersion left, ReleaseVersion right)
    {
        var core = ComparePart(left.Major, right.Major);
        if (core != 0) return core;
        core = ComparePart(left.Minor, right.Minor);
        if (core != 0) return core;
        core = ComparePart(left.Patch, right.Patch);
        if (core != 0) return core;

        if (left.Prerelease is null && right.Prerelease is not null) return 1;
        if (left.Prerelease is not null && right.Prerelease is null) return -1;
        if (left.Prerelease is not null && right.Prerelease is not null)
        {
            var prerelease = ComparePrerelease(left.Prerelease, right.Prerelease);
            if (prerelease != 0) return prerelease;
        }

        return ComparePart(left.Revision, right.Revision);
    }

    private static int ComparePart(int left, int right)
        => left < right ? -1 : left > right ? 1 : 0;

    private static int ComparePrerelease(string left, string right)
    {
        var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Min(leftParts.Length, rightParts.Length);

        for (var i = 0; i < count; i++)
        {
            var leftIsNumber = int.TryParse(leftParts[i], out var leftNumber);
            var rightIsNumber = int.TryParse(rightParts[i], out var rightNumber);

            if (leftIsNumber && rightIsNumber)
            {
                var numeric = ComparePart(leftNumber, rightNumber);
                if (numeric != 0) return numeric;
                continue;
            }

            if (leftIsNumber != rightIsNumber)
                return leftIsNumber ? -1 : 1;

            var ordinal = string.CompareOrdinal(leftParts[i], rightParts[i]);
            if (ordinal != 0) return ordinal < 0 ? -1 : 1;
        }

        return ComparePart(leftParts.Length, rightParts.Length);
    }

    private readonly record struct ReleaseVersion(
        int Major,
        int Minor,
        int Patch,
        int Revision,
        string? Prerelease);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    // PropertyChanged feeds x:Bind on the Settings About page, so it must reach those bindings
    // on the UI thread. The startup update check runs as a detached continuation on the thread
    // pool (no UI SynchronizationContext); raising the event there would set DependencyObject
    // properties (e.g. SettingsExpander.Description bound to Status) off-thread and throw
    // RPC_E_WRONG_THREAD (0x8001010E). Marshal when not already on the UI thread. (Before
    // MainWindow exists nothing is bound, so a direct raise is harmless.)
    private void RaisePropertyChanged(string? propertyName)
    {
        var handler = PropertyChanged;
        if (handler is null)
            return;

        var dispatcher = MainWindow.Instance?.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
            handler(this, new PropertyChangedEventArgs(propertyName));
        else
            dispatcher.TryEnqueue(() => handler(this, new PropertyChangedEventArgs(propertyName)));
    }
}
