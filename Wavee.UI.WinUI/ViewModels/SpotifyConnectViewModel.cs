using System;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Windows.Storage.Streams;

namespace Wavee.UI.WinUI.ViewModels;

public enum ConnectStep
{
    Authenticate,
    Connecting,
    Syncing,
    Complete,
    Error
}

public sealed partial class SpotifyConnectViewModel : ObservableObject
{
    private readonly IAuthState _authState;
    private readonly DispatcherQueue _dispatcherQueue;
    private CancellationTokenSource? _deviceCodeCts;
    private CancellationTokenSource? _browserCts;

    [ObservableProperty]
    private ConnectStep _currentStep = ConnectStep.Authenticate;

    [ObservableProperty]
    private string? _userCode;

    [ObservableProperty]
    private string? _verificationUri;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage = "Requesting pairing code...";

    [ObservableProperty]
    private string? _syncStatus;

    [ObservableProperty]
    private double _syncProgress;

    [ObservableProperty]
    private bool _isDeviceCodeReady;

    [ObservableProperty]
    private WriteableBitmap? _qrCodeImage;

    /// <summary>
    /// Accent color for theme-aware QR code rendering. Set by dialog code-behind before Initialize().
    /// </summary>
    public Windows.UI.Color AccentColor { get; set; } = Windows.UI.Color.FromArgb(255, 29, 185, 84); // fallback: Spotify green

    public event Action? RequestClose;

    public SpotifyConnectViewModel(IAuthState authState)
    {
        _authState = authState;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// Called by dialog on Loaded. Auto-starts device code flow to get QR code + pairing code.
    /// </summary>
    public void Initialize()
    {
        _deviceCodeCts?.Cancel();
        _deviceCodeCts = new CancellationTokenSource();
        StatusMessage = "Requesting pairing code...";

        // Fire-and-forget: start device code flow in background
        _ = RunDeviceCodeFlowAsync(_deviceCodeCts.Token);
    }

    private async Task RunDeviceCodeFlowAsync(CancellationToken ct)
    {
        try
        {
            await _authState.LoginWithDeviceCodeAsync(OnDeviceCodeReceived, ct);

            // Show "Connecting..." step with artificial dwell
            _dispatcherQueue.TryEnqueue(() => CurrentStep = ConnectStep.Connecting);
            await Task.Delay(1500, CancellationToken.None);

            await RunSyncAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Cancelled (user clicked browser login or closed dialog)
        }
        catch (Exception ex)
        {
            var msg = GetFriendlyError(ex);
            _dispatcherQueue.TryEnqueue(() =>
            {
                ErrorMessage = msg;
                CurrentStep = ConnectStep.Error;
            });
        }
    }

    private void OnDeviceCodeReceived(DeviceCodeInfo info)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            UserCode = info.UserCode;
            VerificationUri = info.VerificationUri;
            StatusMessage = "Ready to authenticate. Use the QR code, pairing code, or open in browser.";
            IsDeviceCodeReady = true;

            if (!string.IsNullOrEmpty(info.VerificationUriComplete))
            {
                QrCodeImage = await GenerateQrCodeAsync(info.VerificationUriComplete);
            }
        });
    }

    [RelayCommand]
    private async Task ConnectWithBrowserAsync()
    {
        // Cancel device code flow since user chose browser
        _deviceCodeCts?.Cancel();
        _browserCts?.Cancel();
        _browserCts = new CancellationTokenSource();

        try
        {
            await _authState.LoginWithAuthorizationCodeAsync(_browserCts.Token);

            // Show "Connecting..." step with artificial dwell
            CurrentStep = ConnectStep.Connecting;
            await Task.Delay(1500);

            await RunSyncAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // User cancelled
        }
        catch (Exception ex)
        {
            ErrorMessage = GetFriendlyError(ex);
            CurrentStep = ConnectStep.Error;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _deviceCodeCts?.Cancel();
        _browserCts?.Cancel();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Retry()
    {
        ErrorMessage = null;
        SyncStatus = null;
        SyncProgress = 0;
        CurrentStep = ConnectStep.Authenticate;
        IsDeviceCodeReady = false;
        QrCodeImage = null;
        UserCode = null;
        Initialize();
    }

    [RelayCommand]
    private void OpenVerificationUri()
    {
        if (!string.IsNullOrEmpty(VerificationUri))
        {
            try
            {
                Process.Start(new ProcessStartInfo(VerificationUri) { UseShellExecute = true });
            }
            catch { /* URI is already displayed for manual copy */ }
        }
    }

    private async Task RunSyncAsync(CancellationToken ct)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            CurrentStep = ConnectStep.Syncing;
            SyncStatus = "Fetching your profile...";
            SyncProgress = 0;
        });

        var syncStart = DateTimeOffset.UtcNow;

        // Artificial progress stages to feel responsive even if sync is fast
        var stages = new (string Message, double Progress, int DelayMs)[]
        {
            ("Fetching your profile...", 0.1, 600),
            ("Loading playlists...", 0.25, 800),
            ("Syncing liked songs...", 0.45, 700),
            ("Syncing albums...", 0.6, 600),
            ("Syncing artists...", 0.75, 500),
        };

        // Library sync is handled by LibrarySyncOrchestrator — reacts to AuthStatusChangedMessage
        // No sync code here. Just animate the loading stages.

        // Animate through stages regardless of real sync speed
        foreach (var (message, progress, delayMs) in stages)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                SyncStatus = message;
                SyncProgress = progress;
            });
            await Task.Delay(delayMs, CancellationToken.None);
        }

        // Sync runs in background via LibrarySyncOrchestrator (no awaiting here)

        // Final stage
        _dispatcherQueue.TryEnqueue(() =>
        {
            SyncStatus = "Finishing up...";
            SyncProgress = 0.95;
        });
        await Task.Delay(400, CancellationToken.None);

        _dispatcherQueue.TryEnqueue(() => CurrentStep = ConnectStep.Complete);
        await Task.Delay(800, CancellationToken.None);
        _dispatcherQueue.TryEnqueue(() => RequestClose?.Invoke());
    }

    private async Task<WriteableBitmap> GenerateQrCodeAsync(string url)
    {
        var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.H);
        var qrCode = new PngByteQRCode(qrCodeData);
        // Darken accent color for better contrast on white
        var c = AccentColor;
        var r = (byte)(c.R * 0.55);
        var g = (byte)(c.G * 0.55);
        var b = (byte)(c.B * 0.55);
        var pngBytes = qrCode.GetGraphic(8,
            darkColorRgba: [r, g, b, 255],
            lightColorRgba: [255, 255, 255, 255]);

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pngBytes.AsBuffer());
        stream.Seek(0);

        var bitmap = new WriteableBitmap(1, 1);
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private static string GetFriendlyError(Exception ex)
    {
        if (ex.Message.Contains("access_denied", StringComparison.OrdinalIgnoreCase))
            return "Authorization was denied. Please try again.";
        if (ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
            return "The pairing code expired. Please try again.";
        if (ex.Message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            ex is System.Net.Http.HttpRequestException)
            return "Network error. Check your connection and try again.";

        return $"Something went wrong: {ex.Message}";
    }
}
