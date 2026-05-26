using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.UI.Contracts;

namespace Wavee.UI.Services.Playback;

/// <summary>
/// Single-process sleep timer. Hosts (PlayerBar flyout, Settings) call
/// <see cref="Start"/> with a delay; the service fires
/// <see cref="IPlaybackService.PauseAsync"/> when the timer elapses.
/// <see cref="Cancel"/> aborts an active timer. UI binds to
/// <see cref="IsActive"/> / <see cref="RemainingSeconds"/> for the live
/// countdown.
///
/// "End of track" mode is supported by passing
/// <see cref="EndOfTrackSentinel"/> as the delay; the service then listens to
/// the playback state for the next track change and fires Pause on the next
/// "track ended" transition (whichever comes first). Implemented as a
/// best-effort flag rather than a precise event subscription — the existing
/// PlaybackState property-changed feed already updates near-track-end.
/// </summary>
public sealed class SleepTimerService : INotifyPropertyChanged, IDisposable
{
    /// <summary>Pass this to <see cref="Start"/> to end at the next track boundary.</summary>
    public static readonly TimeSpan EndOfTrackSentinel = TimeSpan.MaxValue;

    private readonly IPlaybackService _playback;
    private readonly IPlaybackStateService _state;
    private readonly ILogger? _logger;

    private CancellationTokenSource? _cts;
    private DateTimeOffset _firesAt;
    private bool _endOfTrackMode;
    private string? _endOfTrackTrackId;
    private bool _isActive;
    private int _remainingSeconds;
    private bool _disposed;

    public SleepTimerService(
        IPlaybackService playback,
        IPlaybackStateService state,
        ILogger<SleepTimerService>? logger = null)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logger = logger;

        _state.PropertyChanged += OnPlaybackStateChanged;
    }

    public bool IsActive
    {
        get => _isActive;
        private set { if (_isActive != value) { _isActive = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Seconds remaining for a duration-based timer, or 0 in end-of-track mode.
    /// Updated approximately once per second while the timer runs.
    /// </summary>
    public int RemainingSeconds
    {
        get => _remainingSeconds;
        private set { if (_remainingSeconds != value) { _remainingSeconds = value; OnPropertyChanged(); } }
    }

    /// <summary>True when the timer fires at the next track boundary, not on a clock.</summary>
    public bool EndOfTrackMode
    {
        get => _endOfTrackMode;
        private set { if (_endOfTrackMode != value) { _endOfTrackMode = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Start the timer. Cancels any active timer first. <paramref name="duration"/>
    /// equal to <see cref="EndOfTrackSentinel"/> enables end-of-track mode.
    /// </summary>
    public void Start(TimeSpan duration)
    {
        Cancel();

        if (duration == EndOfTrackSentinel)
        {
            EndOfTrackMode = true;
            _endOfTrackTrackId = _state.CurrentTrackId;
            IsActive = true;
            RemainingSeconds = 0;
            _logger?.LogInformation("Sleep timer armed: end of current track ({TrackId}).", _endOfTrackTrackId);
            return;
        }

        if (duration <= TimeSpan.Zero)
            return;

        EndOfTrackMode = false;
        _firesAt = DateTimeOffset.UtcNow + duration;
        IsActive = true;
        RemainingSeconds = (int)Math.Ceiling(duration.TotalSeconds);
        _cts = new CancellationTokenSource();
        _logger?.LogInformation("Sleep timer armed: {Seconds}s.", RemainingSeconds);
        _ = RunCountdownAsync(_cts.Token);
    }

    /// <summary>Cancel the active timer. Idempotent.</summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        IsActive = false;
        RemainingSeconds = 0;
        EndOfTrackMode = false;
        _endOfTrackTrackId = null;
    }

    private async Task RunCountdownAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var remaining = _firesAt - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                RemainingSeconds = (int)Math.Ceiling(remaining.TotalSeconds);
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
            if (ct.IsCancellationRequested) return;

            _logger?.LogInformation("Sleep timer fired — pausing playback.");
            await _playback.PauseAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Sleep timer countdown failed.");
        }
        finally
        {
            IsActive = false;
            RemainingSeconds = 0;
        }
    }

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!EndOfTrackMode || !IsActive) return;
        if (e.PropertyName != nameof(IPlaybackStateService.CurrentTrackId)) return;

        var current = _state.CurrentTrackId;
        if (string.IsNullOrEmpty(_endOfTrackTrackId)) return;

        // Track changed away from the one we armed against → fire pause.
        // This intentionally fires even if the user manually skipped — the
        // semantics of "sleep at end of track" align with "after this song".
        if (!string.Equals(current, _endOfTrackTrackId, StringComparison.Ordinal))
        {
            _logger?.LogInformation("Sleep timer fired at end of track ({TrackId}) — pausing playback.", _endOfTrackTrackId);
            EndOfTrackMode = false;
            IsActive = false;
            _endOfTrackTrackId = null;
            _ = _playback.PauseAsync();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PropertyChanged -= OnPlaybackStateChanged;
        Cancel();
    }
}
