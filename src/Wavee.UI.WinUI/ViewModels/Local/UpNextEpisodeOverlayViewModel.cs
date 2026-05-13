using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Local.Models;
using Wavee.UI.Contracts;
using Wavee.UI.Library.Local;
using Wavee.UI.Services;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels.Local;

/// <summary>
/// Drives the Netflix-style "Up Next" card on
/// <see cref="Views.VideoPlayerPage"/> for local TV episodes. Triggers the
/// card late in the file (chapter-aware via
/// <see cref="LocalEpisodeChapterScanner"/> +
/// <see cref="LocalCreditsDetector"/>; otherwise 30 s before end), runs a
/// 10-second countdown, and auto-advances to the next on-disk episode of
/// the same show unless the user cancels.
///
/// <para>Lives only while the page is showing a local TV episode. Created
/// fresh per episode so internal state (shown / dismissed / countdown)
/// can't leak across boundaries.</para>
/// </summary>
public sealed partial class UpNextEpisodeOverlayViewModel : ObservableObject, IDisposable
{
    private const int CountdownSecondsTotal = 10;
    private const int PositionPollIntervalMs = 500;
    // Most files have TimedMetadataTracks resolved by the time playback
    // starts, but a slow MediaFoundation parse can lag a couple of
    // seconds. Re-scan once after this delay if the first scan returned
    // nothing — costs nothing, fixes flaky containers.
    private const int LateChapterRescanDelayMs = 2_000;

    private readonly IPlaybackStateService _playback;
    private readonly ILocalLibraryFacade _facade;
    private readonly LocalEpisodeChapterScanner _chapterScanner;
    private readonly LocalMediaPlayer? _localPlayer;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<UpNextEpisodeOverlayViewModel>? _logger;

    private CancellationTokenSource? _activationCts;
    private DispatcherQueueTimer? _positionTimer;
    private DispatcherQueueTimer? _countdownTimer;
    private IDisposable? _trackFinishedSub;

    private LocalEpisode? _currentEpisode;
    private long _triggerMs;
    private bool _shown;
    private bool _dismissed;
    private bool _advanceRequested;
    private bool _pointerOverCard;
    private bool _disposed;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private LocalEpisode? _nextEpisode;
    [ObservableProperty] private string? _nextEpisodePosterUri;
    [ObservableProperty] private string? _nextEpisodeSeasonEpisodeLabel;
    [ObservableProperty] private string? _nextEpisodeTitle;
    [ObservableProperty] private int _countdownSeconds = CountdownSecondsTotal;
    /// <summary>0..100 ring fill, suited for direct binding to a determinate ProgressRing.</summary>
    [ObservableProperty] private double _countdownProgress = 100.0;

    public UpNextEpisodeOverlayViewModel(
        IPlaybackStateService playback,
        ILocalLibraryFacade facade,
        LocalEpisodeChapterScanner chapterScanner,
        LocalMediaPlayer? localPlayer,
        ILogger<UpNextEpisodeOverlayViewModel>? logger = null)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        _chapterScanner = chapterScanner ?? throw new ArgumentNullException(nameof(chapterScanner));
        _localPlayer = localPlayer;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "UpNextEpisodeOverlayViewModel must be constructed on the UI thread.");

        if (_localPlayer is not null)
        {
            _trackFinishedSub = _localPlayer.TrackFinished.Subscribe(_ =>
                _dispatcher.TryEnqueue(OnCurrentTrackFinished));
        }
    }

    /// <summary>
    /// Bind the overlay to the playback state currently exposed by
    /// <see cref="IPlaybackStateService"/>. Safe to call on every
    /// "playing item changed" notification — same-episode calls are
    /// no-ops; different-episode calls reset all state.
    /// </summary>
    public void RefreshFromPlaybackState()
    {
        if (_disposed) return;

        var trackUri = _playback.CurrentTrackId;
        var seriesId = _playback.CurrentLocalSeriesId;
        var kind = _playback.CurrentLocalContentKind;

        var isLocalTv = kind == Wavee.Local.Classification.LocalContentKind.TvEpisode
                        && !string.IsNullOrEmpty(trackUri)
                        && !string.IsNullOrEmpty(seriesId);

        if (!isLocalTv)
        {
            DeactivateInternal();
            return;
        }

        if (string.Equals(trackUri, _currentEpisode?.TrackUri, StringComparison.Ordinal))
            return;

        _ = ActivateAsync(trackUri!, seriesId!);
    }

    private async Task ActivateAsync(string currentTrackUri, string seriesId)
    {
        DeactivateInternal();

        _activationCts = new CancellationTokenSource();
        var ct = _activationCts.Token;

        try
        {
            var seasons = await _facade.GetShowSeasonsAsync(seriesId, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            // Locate the live episode row. Walk seasons because we don't
            // store a flat lookup; the list is small (one show's roster).
            LocalEpisode? current = null;
            foreach (var season in seasons)
            {
                foreach (var ep in season.Episodes)
                {
                    if (string.Equals(ep.TrackUri, currentTrackUri, StringComparison.Ordinal))
                    {
                        current = ep;
                        break;
                    }
                }
                if (current is not null) break;
            }
            if (current is null) return;

            var next = LocalShowEpisodeQueue.GetNextEpisode(current, seasons);
            if (next is null)
            {
                _logger?.LogDebug(
                    "[UpNext] No next episode for {TrackUri} (last in show?) — overlay disabled",
                    currentTrackUri);
                return;
            }

            _currentEpisode = current;
            NextEpisode = next;
            NextEpisodePosterUri = next.StillImageUri;
            NextEpisodeSeasonEpisodeLabel = $"S{next.Season} · E{next.Episode}";
            NextEpisodeTitle = string.IsNullOrWhiteSpace(next.Title)
                ? $"Episode {next.Episode}"
                : next.Title;

            await ComputeTriggerAsync(currentTrackUri, current.DurationMs, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            EnsurePositionTimer();
        }
        catch (OperationCanceledException) { /* re-activated for another item */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[UpNext] Failed to activate overlay for {TrackUri}", currentTrackUri);
        }
    }

    private async Task ComputeTriggerAsync(string trackUri, long episodeDurationMs, CancellationToken ct)
    {
        // Prefer the live MediaPlayer's reported duration when available —
        // some files report 0 in LocalEpisode.DurationMs until the scanner
        // re-runs.
        var durationMs = ResolveDurationMs(episodeDurationMs);

        var item = _localPlayer?.CurrentPlaybackItem;
        var chapters = await _chapterScanner.ScanAsync(trackUri, item, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;

        var trigger = LocalCreditsDetector.GetTriggerMs(chapters, durationMs);
        if (trigger is not null)
        {
            _triggerMs = trigger.Value;
            _logger?.LogDebug(
                "[UpNext] Trigger set to {Trigger} ms (duration={Duration} ms, chapters={ChapterCount}) for {TrackUri}",
                _triggerMs, durationMs, chapters.Count, trackUri);
        }

        if (chapters.Count != 0) return;

        // Re-scan after a short delay in case TimedMetadataTracks were not
        // resolved at first activation. Late-arriving chapters can move the
        // trigger earlier than the time fallback.
        try
        {
            await Task.Delay(LateChapterRescanDelayMs, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        var lateItem = _localPlayer?.CurrentPlaybackItem;
        var lateChapters = await _chapterScanner.ScanAsync(trackUri, lateItem, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested || lateChapters.Count == 0) return;

        var lateDuration = ResolveDurationMs(episodeDurationMs);
        var lateTrigger = LocalCreditsDetector.GetTriggerMs(lateChapters, lateDuration);
        if (lateTrigger is not null)
        {
            _triggerMs = lateTrigger.Value;
            _logger?.LogDebug(
                "[UpNext] Late chapter scan promoted trigger to {Trigger} ms for {TrackUri}",
                _triggerMs, trackUri);
        }
    }

    private long ResolveDurationMs(long episodeDurationMs)
    {
        var live = (long)_playback.Duration;
        return live > 0 ? live : episodeDurationMs;
    }

    private void EnsurePositionTimer()
    {
        if (_positionTimer is not null) return;
        _positionTimer = _dispatcher.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(PositionPollIntervalMs);
        _positionTimer.IsRepeating = true;
        _positionTimer.Tick += OnPositionTick;
        _positionTimer.Start();
    }

    private void OnPositionTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _currentEpisode is null) return;
        if (!_playback.IsPlaying)
        {
            _countdownTimer?.Stop();
            return;
        }

        if (IsVisible && CountdownSeconds > 0 && !_pointerOverCard)
            _countdownTimer?.Start();

        var positionMs = (long)_playback.Position;

        // Seek backward past the trigger after the card was shown — assume
        // the user wants to re-watch; reset so a re-cross can re-trigger.
        if (_shown && positionMs + 1_000 < _triggerMs)
        {
            HideAndResetForReTrigger();
            return;
        }

        if (_shown || _dismissed) return;
        if (_triggerMs <= 0) return;
        if (positionMs < _triggerMs) return;

        ShowOverlay();
    }

    private void ShowOverlay()
    {
        _shown = true;
        IsVisible = true;
        StartCountdown();
    }

    private void StartCountdown()
    {
        CountdownSeconds = CountdownSecondsTotal;
        CountdownProgress = 100.0;

        _countdownTimer ??= _dispatcher.CreateTimer();
        _countdownTimer.Tick -= OnCountdownTick;
        _countdownTimer.Interval = TimeSpan.FromSeconds(1);
        _countdownTimer.IsRepeating = true;
        _countdownTimer.Tick += OnCountdownTick;
        if (!_pointerOverCard && _playback.IsPlaying) _countdownTimer.Start();
    }

    private void OnCountdownTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed) return;
        if (!_playback.IsPlaying)
        {
            sender.Stop();
            return;
        }
        if (_pointerOverCard) return; // hover pause

        CountdownSeconds = Math.Max(0, CountdownSeconds - 1);
        CountdownProgress = (double)CountdownSeconds / CountdownSecondsTotal * 100.0;
        if (CountdownSeconds <= 0)
        {
            sender.Stop();
            RequestAdvance();
        }
    }

    private void HideAndResetForReTrigger()
    {
        _countdownTimer?.Stop();
        IsVisible = false;
        _shown = false;
        _dismissed = false;
        CountdownSeconds = CountdownSecondsTotal;
        CountdownProgress = 100.0;
    }

    /// <summary>"Watch now" button — advance immediately, skipping the countdown.</summary>
    public void WatchNow()
    {
        if (_disposed) return;
        _countdownTimer?.Stop();
        RequestAdvance();
    }

    /// <summary>Dismiss button — hide the card and stop auto-advance for this episode.</summary>
    public void Cancel()
    {
        if (_disposed) return;
        _countdownTimer?.Stop();
        _dismissed = true;
        IsVisible = false;
    }

    /// <summary>Pointer entered the card — pause the countdown so the user can read it.</summary>
    public void NotifyPointerEnteredCard()
    {
        _pointerOverCard = true;
        _countdownTimer?.Stop();
    }

    /// <summary>Pointer left the card — resume the countdown.</summary>
    public void NotifyPointerExitedCard()
    {
        _pointerOverCard = false;
        if (IsVisible && CountdownSeconds > 0 && _playback.IsPlaying)
            _countdownTimer?.Start();
    }

    private void OnCurrentTrackFinished()
    {
        if (_disposed) return;
        if (_currentEpisode is null || NextEpisode is null) return;
        if (_dismissed) return;
        // If the card never surfaced (e.g. the user joined past the
        // trigger or seeked over it), advance once on natural end so the
        // viewer still gets Netflix-style continuity.
        if (_advanceRequested) return;
        RequestAdvance();
    }

    private void RequestAdvance()
    {
        if (_disposed) return;
        if (NextEpisode is null) return;
        if (string.IsNullOrEmpty(NextEpisode.TrackUri)) return;
        if (_advanceRequested) return;
        _advanceRequested = true;

        IsVisible = false;
        var nextUri = NextEpisode.TrackUri!;
        _logger?.LogInformation("[UpNext] Auto-advancing to {NextUri}", nextUri);
        try
        {
            LocalPlaybackLauncher.PlayOne(nextUri);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[UpNext] PlayOne failed for {NextUri}", nextUri);
        }
    }

    private void DeactivateInternal()
    {
        _activationCts?.Cancel();
        _activationCts?.Dispose();
        _activationCts = null;

        _positionTimer?.Stop();
        if (_positionTimer is not null) _positionTimer.Tick -= OnPositionTick;
        _positionTimer = null;

        _countdownTimer?.Stop();
        if (_countdownTimer is not null) _countdownTimer.Tick -= OnCountdownTick;
        _countdownTimer = null;

        _currentEpisode = null;
        _triggerMs = 0;
        _shown = false;
        _dismissed = false;
        _advanceRequested = false;
        _pointerOverCard = false;
        IsVisible = false;
        NextEpisode = null;
        NextEpisodePosterUri = null;
        NextEpisodeSeasonEpisodeLabel = null;
        NextEpisodeTitle = null;
        CountdownSeconds = CountdownSecondsTotal;
        CountdownProgress = 100.0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _trackFinishedSub?.Dispose();
        _trackFinishedSub = null;

        DeactivateInternal();
    }
}
