using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Wavee.Audio.Queue;
using Wavee.AudioIpc;
using Wavee.Connect;
using Wavee.Connect.Commands;

namespace Wavee.Audio;

/// <summary>
/// Central playback orchestration layer. Sits between ConnectCommandExecutor and AudioPipelineProxy.
/// Manages queue, resolves tracks, handles remote commands, auto-advances on track finish.
/// Implements IPlaybackEngine so it slots into the existing executor/state manager wiring.
/// </summary>
public sealed class PlaybackOrchestrator : IPlaybackEngine, IAsyncDisposable
{
    private readonly AudioPipelineProxy _proxy;
    private readonly TrackResolver _trackResolver;
    private readonly ContextResolver _contextResolver;
    private readonly PlaybackQueue _queue;
    private readonly ILogger? _logger;
    private readonly CompositeDisposable _subs = new();

    private readonly BehaviorSubject<LocalPlaybackState> _stateSubject = new(LocalPlaybackState.Empty);
    private readonly Subject<PlaybackError> _errorSubject = new();

    private bool _repeatContext;
    private bool _repeatTrack;
    private bool _disposed;

    public PlaybackOrchestrator(
        AudioPipelineProxy proxy,
        TrackResolver trackResolver,
        ContextResolver contextResolver,
        ConnectCommandHandler? commandHandler,
        ILogger? logger)
    {
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        _trackResolver = trackResolver ?? throw new ArgumentNullException(nameof(trackResolver));
        _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        _logger = logger;
        _queue = new PlaybackQueue(logger);

        // Forward proxy state enriched with queue info
        _subs.Add(_proxy.StateChanges.Subscribe(OnProxyStateChanged));

        // Forward proxy errors
        _subs.Add(_proxy.Errors.Subscribe(err => _errorSubject.OnNext(err)));

        // Auto-advance on track finish
        _subs.Add(_proxy.TrackFinished.Subscribe(msg => { _ = OnTrackFinishedAsync(msg); }));

        // Lazy load more tracks when queue runs low
        _subs.Add(_queue.NeedsMoreTracks.Subscribe(unit => { _ = LoadMoreTracksAsync(); }));

        // Subscribe to incoming remote commands from Spotify Connect
        SubscribeToRemoteCommands(commandHandler);
    }

    // ── IPlaybackEngine ──

    public IObservable<LocalPlaybackState> StateChanges => _stateSubject.AsObservable();
    public IObservable<PlaybackError> Errors => _errorSubject.AsObservable();
    public LocalPlaybackState CurrentState => _stateSubject.Value;

    public async Task PlayAsync(PlayCommand command, CancellationToken ct = default)
    {
        try
        {
            _logger?.LogInformation("Orchestrator: PlayAsync contextUri={Context} trackUri={Track}",
                command.ContextUri, command.TrackUri);

            // Build queue from context or explicit track list
            if (!string.IsNullOrEmpty(command.ContextUri) && command.ContextUri != "spotify:internal:queue")
            {
                var context = await _contextResolver.LoadContextAsync(command.ContextUri, ct: ct);
                _queue.SetContext(command.ContextUri, context.IsInfinite, context.TotalCount);
                _queue.SetTracks(context.Tracks);

                // Merge page tracks if provided (updates UIDs)
                if (command.PageTracks?.Count > 0)
                {
                    var existing = context.Tracks.Select(t => (t.Uri, (string?)t.Uid)).ToList();
                    ContextResolver.MergePageTracks(existing, command.PageTracks);
                }

                var startIndex = ContextResolver.FindTrackIndex(
                    context.Tracks, command.TrackUri, command.TrackUid, command.SkipToIndex);
                _queue.SkipTo(startIndex);
            }
            else if (command.PageTracks?.Count > 0)
            {
                var tracks = command.PageTracks
                    .Select(t => new QueueTrack(t.Uri, t.Uid))
                    .ToList();
                _queue.Clear();
                _queue.SetContext("spotify:internal:queue", false);
                _queue.SetTracks(tracks, command.SkipToIndex ?? 0);
            }

            await PlayCurrentTrackAsync(command.PositionMs ?? 0, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PlayAsync failed");
            _errorSubject.OnNext(new PlaybackError(PlaybackErrorType.Unknown, ex.Message, ex));
        }
    }

    public Task PauseAsync(CancellationToken ct = default) => _proxy.PauseAsync(ct);
    public Task ResumeAsync(CancellationToken ct = default) => _proxy.ResumeAsync(ct);
    public Task StopAsync(CancellationToken ct = default) => _proxy.StopAsync(ct);
    public Task SeekAsync(long positionMs, CancellationToken ct = default) => _proxy.SeekAsync(positionMs, ct);
    public Task SetVolumeAsync(float volume, CancellationToken ct = default) => _proxy.SetVolumeAsync(volume, ct);

    public async Task SkipNextAsync(CancellationToken ct = default)
    {
        var next = _queue.MoveNext();
        if (next != null)
        {
            _logger?.LogInformation("Orchestrator: skip next → {Uri}", next.Uri);
            await PlayCurrentTrackAsync(0, ct);
        }
        else
        {
            _logger?.LogInformation("Orchestrator: end of queue, stopping");
            await _proxy.StopAsync(ct);
        }
    }

    public async Task SkipPreviousAsync(CancellationToken ct = default)
    {
        // If more than 3 seconds in, restart current track
        var state = _stateSubject.Value;
        if (state.PositionMs > 3000)
        {
            await _proxy.SeekAsync(0, ct);
            return;
        }

        var prev = _queue.MovePrevious();
        if (prev != null)
        {
            _logger?.LogInformation("Orchestrator: skip prev → {Uri}", prev.Uri);
            await PlayCurrentTrackAsync(0, ct);
        }
        else
        {
            await _proxy.SeekAsync(0, ct);
        }
    }

    public Task SetShuffleAsync(bool enabled, CancellationToken ct = default)
    {
        _queue.SetShuffle(enabled);
        PublishQueueState();
        return Task.CompletedTask;
    }

    public Task SetRepeatContextAsync(bool enabled, CancellationToken ct = default)
    {
        _repeatContext = enabled;
        if (enabled) _repeatTrack = false;
        PublishQueueState();
        return Task.CompletedTask;
    }

    public Task SetRepeatTrackAsync(bool enabled, CancellationToken ct = default)
    {
        _repeatTrack = enabled;
        if (enabled) _repeatContext = false;
        PublishQueueState();
        return Task.CompletedTask;
    }

    // ── Core ──

    private async Task PlayCurrentTrackAsync(long positionMs, CancellationToken ct = default)
    {
        var current = _queue.Current;
        if (current == null)
        {
            _logger?.LogWarning("No current track in queue");
            return;
        }

        _logger?.LogInformation("Resolving track (deferred): {Uri}", current.Uri);

        // 1. Resolve with head file (instant start) + deferred CDN/key
        var resolution = await _trackResolver.ResolveWithHeadAsync(current.Uri, ct);

        // 2. Generate deferred ID
        var deferredId = Guid.NewGuid().ToString("N");

        // 3. Send head data to AudioHost immediately — playback starts NOW
        await _proxy.PlayTrackDeferredAsync(new Wavee.Playback.Contracts.PlayTrackCommand
        {
            DeferredId = deferredId,
            TrackUri = current.Uri,
            TrackUid = current.Uid,
            Codec = resolution.Codec,
            DurationMs = resolution.DurationMs,
            PositionMs = positionMs,
            NormalizationGain = resolution.Normalization.TrackGainDb,
            NormalizationPeak = resolution.Normalization.TrackPeak,
            HeadData = resolution.HeadData != null ? Convert.ToBase64String(resolution.HeadData) : null,
            Metadata = resolution.Metadata,
        }, ct);

        _logger?.LogInformation("Head data sent — audio starting instantly for {Title}", resolution.Metadata?.Title);

        // 4. Wait for CDN + key (runs in background while head plays)
        await Task.WhenAll(resolution.AudioKeyTask, resolution.CdnUrlTask, resolution.FileSizeTask);

        // 5. Send deferred resolution → AudioHost seamlessly continues from CDN
        await _proxy.SendDeferredResolvedAsync(
            deferredId,
            await resolution.CdnUrlTask,
            await resolution.AudioKeyTask,
            await resolution.FileSizeTask,
            ct);

        _logger?.LogInformation("Deferred CDN resolved — seamless playback continuing");

        PublishQueueState();
    }

    private async Task OnTrackFinishedAsync(Playback.Contracts.TrackFinishedMessage msg)
    {
        try
        {
            _logger?.LogInformation("Track finished: {Uri} reason={Reason}", msg.TrackUri, msg.Reason);

            if (_repeatTrack)
            {
                await PlayCurrentTrackAsync(0);
                return;
            }

            var next = _queue.MoveNext();
            if (next != null)
            {
                await PlayCurrentTrackAsync(0);
            }
            else if (_repeatContext)
            {
                _queue.SkipTo(0);
                await PlayCurrentTrackAsync(0);
            }
            else
            {
                _logger?.LogInformation("End of queue — playback complete");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error advancing to next track");
        }
    }

    private async Task LoadMoreTracksAsync()
    {
        // PlaybackQueue signals NeedsMoreTracks when approaching end
        // Load next page from context if available
        _logger?.LogDebug("Queue needs more tracks — loading next page");
        // TODO: implement pagination via _contextResolver.LoadNextPageAsync
    }

    // ── Remote commands ──

    private void SubscribeToRemoteCommands(ConnectCommandHandler? handler)
    {
        if (handler == null) return;

        _subs.Add(handler.PlayCommands.Subscribe(cmd =>
        {
            _logger?.LogInformation("Remote: play {Context}/{Track}", cmd.ContextUri, cmd.TrackUri);
            _ = PlayAsync(cmd);
        }));
        _subs.Add(handler.PauseCommands.Subscribe(cmd => { _ = PauseAsync(); }));
        _subs.Add(handler.ResumeCommands.Subscribe(cmd => { _ = ResumeAsync(); }));
        _subs.Add(handler.SeekCommands.Subscribe(cmd => { _ = SeekAsync(cmd.PositionMs); }));
        _subs.Add(handler.SkipNextCommands.Subscribe(cmd => { _ = SkipNextAsync(); }));
        _subs.Add(handler.SkipPrevCommands.Subscribe(cmd => { _ = SkipPreviousAsync(); }));
        _subs.Add(handler.ShuffleCommands.Subscribe(cmd => { _ = SetShuffleAsync(cmd.Enabled); }));
        _subs.Add(handler.RepeatContextCommands.Subscribe(cmd => { _ = SetRepeatContextAsync(cmd.Enabled); }));
        _subs.Add(handler.RepeatTrackCommands.Subscribe(cmd => { _ = SetRepeatTrackAsync(cmd.Enabled); }));
    }

    // ── State ──

    private void OnProxyStateChanged(LocalPlaybackState engineState)
    {
        // Enrich engine state with queue info
        var enriched = engineState with
        {
            ContextUri = _queue.ContextUri ?? engineState.ContextUri,
            Shuffling = _queue.IsShuffled,
            RepeatingContext = _repeatContext,
            RepeatingTrack = _repeatTrack,
            CurrentIndex = _queue.CurrentIndex,
            PrevTracks = _queue.GetPrevTracks()
                .Select(t => new TrackReference(t.Uri, t.Uid ?? "", t.AlbumUri, t.ArtistUri, t.IsUserQueued))
                .ToList(),
            NextTracks = _queue.GetNextTracks()
                .Select(t => new TrackReference(t.Uri, t.Uid ?? "", t.AlbumUri, t.ArtistUri, t.IsUserQueued))
                .ToList(),
            PrevQueueItems = _queue.GetPrevTracks().Cast<IQueueItem>().ToList(),
            NextQueueItems = _queue.GetNextTracks().Cast<IQueueItem>().ToList(),
            QueueRevision = _queue.GetQueueRevision(),
        };
        _stateSubject.OnNext(enriched);
    }

    private void PublishQueueState()
    {
        // Re-enrich current state with latest queue info
        var current = _stateSubject.Value;
        OnProxyStateChanged(current);
    }

    // ── Disposal ──

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _subs.Dispose();
        _stateSubject.Dispose();
        _errorSubject.Dispose();
    }
}
