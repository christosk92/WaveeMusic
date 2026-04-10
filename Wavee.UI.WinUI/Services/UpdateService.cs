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
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var rawVersion = tagName.TrimStart('v', 'V');
            LatestVersion = rawVersion;
            Changelog = root.TryGetProperty("body", out var body) ? body.GetString() : null;
            ReleaseUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;

            if (Version.TryParse(rawVersion, out var latest) &&
                Version.TryParse(CurrentVersion, out var current))
            {
                IsUpdateAvailable = latest > current;
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
        try
        {
            var pkg = Package.Current;
            Distribution = pkg.SignatureKind == PackageSignatureKind.Store
                ? DistributionMode.Store
                : DistributionMode.Sideloaded;
            var v = pkg.Id.Version;
            CurrentVersion = $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            Distribution = DistributionMode.Unpackaged;
            CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
