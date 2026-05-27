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

public sealed class UpdateService : IUpdateService
{
    private const string GitHubReleasesUrl = "https://api.github.com/repos/christosk92/WaveeMusic/releases/latest";

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

        // Fire-and-forget initial check after a short delay to let the app settle
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(async _ =>
        {
            await CheckForUpdateAsync();
            if (IsUpdateAvailable && Distribution != DistributionMode.Store)
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
        }, TaskScheduler.Default);
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
                // GitHub returns 404 here when the repo has no published releases yet.
                // Treat that as "no update available" instead of surfacing a startup error.
                IsUpdateAvailable = false;
                LatestVersion = null;
                Changelog = null;
                ReleaseUrl = null;
                Status = UpdateStatus.UpToDate;
                LastChecked = DateTimeOffset.UtcNow;
                _logger?.LogDebug("Update check skipped because no GitHub releases are published yet");
                return;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var rawVersion = tagName.TrimStart('v', 'V');
            LatestVersion = rawVersion;
            Changelog = root.TryGetProperty("body", out var body) ? body.GetString() : null;
            ReleaseUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;

            if (TryParseReleaseVersion(rawVersion, out var latest) &&
                TryParseReleaseVersion(CurrentVersion, out var current))
            {
                IsUpdateAvailable = CompareReleaseVersions(latest, current) > 0;
                Status = IsUpdateAvailable ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate;
            }
            else
            {
                // Can't parse — treat as up to date
                IsUpdateAvailable = false;
                Status = UpdateStatus.UpToDate;
            }

            LastChecked = DateTimeOffset.UtcNow;

            _logger?.LogInformation(
                "Update check: current={Current}, latest={Latest}, available={Available}",
                CurrentVersion, LatestVersion, IsUpdateAvailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Update check failed");
            Status = UpdateStatus.Error;
            ErrorMessage = ex.Message;

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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
