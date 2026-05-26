using System;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Bridges our out-of-process playback state to Windows
/// <see cref="SystemMediaTransportControls"/> — the system surface that powers
/// the volume-flyout media tile, the lock-screen now-playing card, the Game
/// Bar Now Playing widget, and headset / Bluetooth / keyboard hardware media
/// keys (play / pause / next / prev). Without SMTC, none of those work.
///
/// Audio in Wavee runs out-of-process and we don't host a real
/// <see cref="MediaPlayer"/> that plays Spotify content. The documented
/// WinUI 3 desktop way to obtain an SMTC instance without an HWND interop
/// dance is to instantiate an unused <c>MediaPlayer</c> purely as the SMTC
/// container — the same trick the Windows Community Toolkit and many
/// production WinUI 3 apps use. CommandManager.IsEnabled = false disables
/// the container's automatic SMTC handling so we drive every field
/// (title / artist / thumbnail / status / button enable bits) ourselves
/// from the real playback state.
/// </summary>
public sealed class SystemMediaTransportControlsService : IDisposable
{
    private readonly IPlaybackStateService _state;
    private readonly IPlaybackService _playback;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger? _logger;
    private readonly MediaPlayer _smtcHost = new();
    private readonly SystemMediaTransportControls _smtc;
    private bool _initialized;
    private bool _disposed;

    public SystemMediaTransportControlsService(
        IPlaybackStateService state,
        IPlaybackService playback,
        ILogger<SystemMediaTransportControlsService>? logger = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("SystemMediaTransportControlsService must be constructed on a UI thread.");
        _logger = logger;

        _smtcHost.CommandManager.IsEnabled = false;
        _smtc = _smtcHost.SystemMediaTransportControls;
    }

    /// <summary>
    /// Wires the SMTC button handlers and subscribes to playback state. Idempotent;
    /// safe to call from <c>MainWindow</c> activation.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.IsStopEnabled = false;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButtonPressed;

        _state.PropertyChanged += OnStateChanged;
        RefreshAll();
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // SMTC fires on a system thread; the IPlaybackService implementations
        // marshal to their own queues, so we don't need to hop the UI dispatcher
        // before calling them.
        try
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    _ = _playback.ResumeAsync();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    _ = _playback.PauseAsync();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    _ = _playback.SkipNextAsync();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    _ = _playback.SkipPreviousAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SMTC button {Button} handler threw.", args.Button);
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        // Title / artist / art fire as separate property notifications; coalesce
        // by reposting RefreshAll onto the UI thread — SMTC.DisplayUpdater is
        // affordably cheap to write but a single batched update avoids flicker.
        _dispatcher.TryEnqueue(RefreshAll);
    }

    private void RefreshAll()
    {
        if (_disposed) return;
        UpdatePlaybackStatus();
        UpdateDisplay();
    }

    private void UpdatePlaybackStatus()
    {
        if (_state.IsBuffering)
            _smtc.PlaybackStatus = MediaPlaybackStatus.Changing;
        else if (string.IsNullOrEmpty(_state.CurrentTrackId))
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        else if (_state.IsPlaying)
            _smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
        else
            _smtc.PlaybackStatus = MediaPlaybackStatus.Paused;
    }

    private void UpdateDisplay()
    {
        var updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        var music = updater.MusicProperties;
        music.Title = _state.CurrentTrackTitle ?? string.Empty;
        music.Artist = _state.CurrentArtistName ?? string.Empty;
        music.AlbumTitle = string.Empty;

        // The playback state ships album art as raw Spotify URIs (e.g.
        // "spotify:image:HEX") that Windows can't fetch directly. Normalize
        // through SpotifyImageHelper which maps spotify:image:* →
        // https://i.scdn.co/image/* and wavee-artwork://HASH → file:///…,
        // then hand the resolved absolute URI to SMTC. Without this step the
        // tile falls back to the package icon (the Wavee "W" logo).
        var rawUrl = _state.CurrentAlbumArtLarge ?? _state.CurrentAlbumArt;
        var resolvedUrl = SpotifyImageHelper.ToHttpsUrl(rawUrl);
        if (!string.IsNullOrEmpty(resolvedUrl) && Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var artUri))
        {
            try
            {
                updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(artUri);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "SMTC thumbnail set failed for {Url}", resolvedUrl);
                updater.Thumbnail = null;
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(rawUrl))
                _logger?.LogDebug("SMTC: dropping unresolvable album art uri {RawUrl}", rawUrl);
            updater.Thumbnail = null;
        }

        updater.Update();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _state.PropertyChanged -= OnStateChanged; } catch { }
        try { _smtc.ButtonPressed -= OnButtonPressed; } catch { }
        try { _smtc.IsEnabled = false; } catch { }
        _smtcHost.Dispose();
    }
}
