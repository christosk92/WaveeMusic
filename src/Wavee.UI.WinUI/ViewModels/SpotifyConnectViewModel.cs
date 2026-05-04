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
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Services;
using Windows.Storage.Streams;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Coarse phase the dialog is in. Drives panel visibility (Auth / Progress
/// / Error). The fine-grained phase tracking inside Progress is private to
/// the VM and surfaced through <see cref="MainText"/>/<see cref="SubText"/>/
/// <see cref="OverallProgress"/>.
/// </summary>
public enum ConnectStep
{
    Authenticate,
    Progress,
    Error
}

/// <summary>
/// Backs <c>SpotifyConnectDialog</c>. Replaces the prior fake-progress
/// implementation (six hardcoded stages with <c>Task.Delay</c>s) with live
/// subscriptions to real backend signals:
///
///   AuthStatusChangedMessage          — auth phase
///   AudioProcessStateChangedMessage   — audio engine sub-text
///   LibrarySyncStartedMessage         — library phase entry
///   LibrarySyncProgressMessage        — per-collection sub-text
///   LibrarySyncCompletedMessage       — library phase exit
///   LibrarySyncFailedMessage          — error template
///   PlaylistPrefetchStartedMessage    — playlist phase entry
///   PlaylistPrefetchProgressMessage   — per-playlist sub-text
///   HomeFeedLoadedMessage             — auto-close trigger
///
/// Five user-visible weighted phases (Authenticating 10% / Connecting 10%
/// / Library 25% / Playlists 35% / Home 20%) sum to 100%; the bar is the
/// weighted sum of each phase's internal progress so it advances smoothly
/// rather than in big jumps.
/// </summary>
public sealed partial class SpotifyConnectViewModel : ObservableObject, IDisposable
{
    private readonly IAuthState _authState;
    private readonly IMessenger _messenger;
    private readonly DispatcherQueue _dispatcherQueue;
    private CancellationTokenSource? _deviceCodeCts;
    private CancellationTokenSource? _browserCts;

    // ---- Phase weighting (percentages, must sum to 100) ----
    private const double WAuth      = 10;
    private const double WConnect   = 10;
    private const double WLibrary   = 25;
    private const double WPlaylists = 35;
    private const double WHome      = 20;

    // ---- Per-phase 0..1 progress, mutated by message handlers ----
    private double _phaseAuth;
    private double _phaseConnect;
    private double _phaseLibrary;
    private double _phasePlaylists;
    private double _phaseHome;

    [ObservableProperty]
    private ConnectStep _currentStep = ConnectStep.Authenticate;

    [ObservableProperty]
    private string? _userCode;

    [ObservableProperty]
    private string? _verificationUri;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage = AppLocalization.GetString("Connect_RequestingPairingCode");

    [ObservableProperty]
    private string? _mainText;

    [ObservableProperty]
    private string? _subText;

    /// <summary>0..100 — sum of weighted phase progresses. Bound to the dialog's progress bar.</summary>
    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private bool _isDeviceCodeReady;

    [ObservableProperty]
    private WriteableBitmap? _qrCodeImage;

    /// <summary>
    /// Accent color for theme-aware QR code rendering. Set by dialog code-behind before Initialize().
    /// </summary>
    public Windows.UI.Color AccentColor { get; set; } = Windows.UI.Color.FromArgb(255, 29, 185, 84); // fallback: Spotify green

    public event Action? RequestClose;

    public SpotifyConnectViewModel(IAuthState authState, IMessenger? messenger = null)
    {
        _authState = authState;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        WireMessageSubscriptions();
    }

    private void WireMessageSubscriptions()
    {
        _messenger.Register<SpotifyConnectViewModel, AuthStatusChangedMessage>(this, (r, m) =>
            r._dispatcherQueue.TryEnqueue(() => r.OnAuthStatusChanged(m.Value)));

        _messenger.Register<SpotifyConnectViewModel, AudioProcessStateChangedMessage>(this, (r, m) =>
            r._dispatcherQueue.TryEnqueue(() => r.OnAudioStateChanged(m.Value.State, m.Value.Message)));

        _messenger.Register<SpotifyConnectViewModel, LibrarySyncStartedMessage>(this, (r, _) =>
            r._dispatcherQueue.TryEnqueue(() => r.OnLibrarySyncStarted()));

        _messenger.Register<SpotifyConnectViewModel, LibrarySyncProgressMessage>(this, (r, m) =>
            r._dispatcherQueue.TryEnqueue(() => r.OnLibrarySyncProgress(m.Value.Collection, m.Value.Done, m.Value.Total)));

        _messenger.Register<SpotifyConnectViewModel, LibrarySyncCompletedMessage>(this, (r, _) =>
            r._dispatcherQueue.TryEnqueue(r.OnLibrarySyncCompleted));

        _messenger.Register<SpotifyConnectViewModel, LibrarySyncFailedMessage>(this, (r, m) =>
            r._dispatcherQueue.TryEnqueue(() => r.OnLibrarySyncFailed(m.Value)));

        _messenger.Register<SpotifyConnectViewModel, PlaylistPrefetchStartedMessage>(this, (r, m) =>
            r._dispatcherQueue.TryEnqueue(() => r.OnPlaylistPrefetchStarted(m.Value)));

        _messenger.Register<SpotifyConnectViewModel, PlaylistPrefetchProgressMessage>(this, (r, m) =>
            r._dispatcherQueue.TryEnqueue(() => r.OnPlaylistPrefetchProgress(m.Value.PlaylistName, m.Value.Done, m.Value.Total)));

        _messenger.Register<SpotifyConnectViewModel, HomeFeedLoadedMessage>(this, (r, _) =>
            r._dispatcherQueue.TryEnqueue(r.OnHomeFeedLoaded));
    }

    /// <summary>
    /// Called by dialog on Loaded. Auto-starts device code flow to get QR code + pairing code.
    /// </summary>
    public void Initialize()
    {
        _deviceCodeCts?.Cancel();
        _deviceCodeCts = new CancellationTokenSource();
        StatusMessage = AppLocalization.GetString("Connect_RequestingPairingCode");

        _ = RunDeviceCodeFlowAsync(_deviceCodeCts.Token);
    }

    private async Task RunDeviceCodeFlowAsync(CancellationToken ct)
    {
        try
        {
            await _authState.LoginWithDeviceCodeAsync(OnDeviceCodeReceived, ct);
            // Auth completion is signalled via AuthStatusChangedMessage; no Task.Delay here.
        }
        catch (OperationCanceledException) { /* user switched to browser flow or closed dialog */ }
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
            StatusMessage = AppLocalization.GetString("Connect_ReadyToAuthenticate");
            IsDeviceCodeReady = true;

            // Auth phase progresses to ~30% as soon as the pairing code is ready.
            _phaseAuth = 0.3;
            RecalcProgress();

            if (!string.IsNullOrEmpty(info.VerificationUriComplete))
                QrCodeImage = await GenerateQrCodeAsync(info.VerificationUriComplete);
        });
    }

    [RelayCommand]
    private async Task ConnectWithBrowserAsync()
    {
        _deviceCodeCts?.Cancel();
        _browserCts?.Cancel();
        _browserCts = new CancellationTokenSource();

        try
        {
            await _authState.LoginWithAuthorizationCodeAsync(_browserCts.Token);
            // Same as device-code: AuthStatusChangedMessage drives the phase advance.
        }
        catch (OperationCanceledException) { /* user cancelled */ }
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
        SubText = null;
        MainText = null;
        OverallProgress = 0;
        _phaseAuth = _phaseConnect = _phaseLibrary = _phasePlaylists = _phaseHome = 0;
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
            catch (Exception ex) { Debug.WriteLine($"Failed to open verification URI: {ex.Message}"); }
        }
    }

    // ---- Message handlers (dispatcher-thread) ----

    private void OnAuthStatusChanged(AuthStatus status)
    {
        switch (status)
        {
            case AuthStatus.Authenticating:
                CurrentStep = ConnectStep.Progress;
                MainText = AppLocalization.GetString("Connect_Authenticating");
                SubText = AppLocalization.GetString("Connect_ExchangingAuthorizationCode");
                _phaseAuth = 0.7;
                RecalcProgress();
                break;

            case AuthStatus.Authenticated:
                CurrentStep = ConnectStep.Progress;
                MainText = AppLocalization.GetString("Connect_ConnectingToSpotify");
                SubText = AppLocalization.GetString("Connect_ReachingAccessPoint");
                _phaseAuth = 1.0;
                _phaseConnect = 0.4; // dealer + connect already done by the time Authenticated fires
                RecalcProgress();
                break;

            case AuthStatus.Error:
                ErrorMessage = _authState.ConnectionError ?? AppLocalization.Format("Connect_ErrorGeneric", "");
                CurrentStep = ConnectStep.Error;
                break;
        }
    }

    private void OnAudioStateChanged(string state, string? message)
    {
        // Only refine sub-text while we're in the Connecting phase. Once
        // library sync starts, the audio init is no longer the user's
        // mental model of what's happening.
        if (_phaseConnect >= 1.0) return;

        if (string.Equals(state, "Connecting", StringComparison.OrdinalIgnoreCase))
        {
            SubText = AppLocalization.GetString("Connect_StartingAudioEngine");
            _phaseConnect = Math.Max(_phaseConnect, 0.6);
        }
        else if (string.Equals(state, "Connected", StringComparison.OrdinalIgnoreCase))
        {
            SubText = AppLocalization.GetString("Connect_AudioEngineReady");
            _phaseConnect = 1.0;
        }
        RecalcProgress();
    }

    private void OnLibrarySyncStarted()
    {
        CurrentStep = ConnectStep.Progress;
        MainText = AppLocalization.GetString("Connect_LoadingLibrary");
        SubText = AppLocalization.GetString("Connect_StartingLibrarySync");
        _phaseConnect = 1.0;
        _phaseLibrary = 0.05;
        RecalcProgress();
    }

    private void OnLibrarySyncProgress(string collection, int done, int total)
    {
        if (total <= 0) return;
        _phaseLibrary = Math.Clamp(done / (double)total, 0, 1);

        // Map collection key → human label for sub-text
        SubText = collection switch
        {
            "tracks"        => AppLocalization.GetString("Connect_SyncingLikedSongs"),
            "albums"        => AppLocalization.GetString("Connect_SyncingAlbums"),
            "artists"       => AppLocalization.GetString("Connect_SyncingArtists"),
            "shows"         => AppLocalization.GetString("Connect_SyncingShows"),
            "listen-later"  => AppLocalization.GetString("Connect_SyncingListenLater"),
            "done"          => AppLocalization.GetString("Connect_LibraryReady"),
            _               => collection
        };
        RecalcProgress();
    }

    private void OnLibrarySyncCompleted()
    {
        _phaseLibrary = 1.0;
        // Sub-text is briefly "Library ready" until prefetch start arrives.
        SubText = AppLocalization.GetString("Connect_LibraryReady");
        RecalcProgress();
    }

    private void OnLibrarySyncFailed(string error)
    {
        ErrorMessage = error;
        CurrentStep = ConnectStep.Error;
    }

    private int _playlistTotal;

    private void OnPlaylistPrefetchStarted(int total)
    {
        _playlistTotal = total;
        CurrentStep = ConnectStep.Progress;
        MainText = AppLocalization.GetString("Connect_LoadingPlaylists");
        if (total <= 0)
        {
            // Skip phase entirely if the user has zero playlists.
            _phasePlaylists = 1.0;
            SubText = AppLocalization.GetString("Connect_NoPlaylists");
        }
        else
        {
            _phasePlaylists = 0.02;
            SubText = AppLocalization.Format("Connect_PlaylistProgress", "…", "0", total.ToString());
        }
        RecalcProgress();
    }

    private void OnPlaylistPrefetchProgress(string playlistName, int done, int total)
    {
        if (total <= 0) return;
        _phasePlaylists = Math.Clamp(done / (double)total, 0, 1);
        SubText = AppLocalization.Format("Connect_PlaylistProgress", playlistName, done.ToString(), total.ToString());
        RecalcProgress();
    }

    private void OnHomeFeedLoaded()
    {
        _phaseHome = 1.0;
        // If playlist phase is still in flight, force it complete here so
        // the bar reaches 100% — playlist prefetch can outlive the home
        // load on slow connections, but we don't keep the dialog open
        // for that.
        if (_phasePlaylists < 1.0) _phasePlaylists = 1.0;
        MainText = AppLocalization.GetString("Connect_AllSet");
        SubText = null;
        RecalcProgress();
        // Auto-close on next dispatcher tick so the 100% bar paints once.
        _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => RequestClose?.Invoke());
    }

    private void RecalcProgress()
    {
        var pct = _phaseAuth * WAuth
                + _phaseConnect * WConnect
                + _phaseLibrary * WLibrary
                + _phasePlaylists * WPlaylists
                + _phaseHome * WHome;
        OverallProgress = Math.Clamp(pct, 0, 100);
    }

    // ---- helpers (unchanged from prior implementation) ----

    private async Task<WriteableBitmap> GenerateQrCodeAsync(string url)
    {
        var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.H);
        var qrCode = new PngByteQRCode(qrCodeData);
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
            return AppLocalization.GetString("Connect_ErrorAuthorizationDenied");
        if (ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
            return AppLocalization.GetString("Connect_ErrorPairingCodeExpired");
        if (ex.Message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            ex is System.Net.Http.HttpRequestException)
            return AppLocalization.GetString("Connect_ErrorNetwork");

        return AppLocalization.Format("Connect_ErrorGeneric", ex.Message);
    }

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        _deviceCodeCts?.Dispose();
        _browserCts?.Dispose();
    }
}
