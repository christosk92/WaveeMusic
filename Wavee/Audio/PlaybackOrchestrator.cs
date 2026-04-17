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

    // Context subtitle (artist name, playlist title) — set from PlayCommand at
    // play time, emitted on every subsequent state publish until the next PlayAsync.
    private string? _currentContextDescription;

    // Latch: set true for auto-advance / transfer-resume / autoplay rollover,
    // false for user-initiated play. Sticks until the next PlayAsync flips it.
    private bool _isSystemInitiated;

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

            // PlayCommand arrives either from user click (local ConnectCommandExecutor)
            // or from a remote transfer (command handler pipe). Either way, treat the
            // user as the initiator — the auto-advance path flips the latch back to
            // true before calling PlayCurrentTrackAsync.
            _isSystemInitiated = false;
            _currentContextDescription = command.ContextDescription;

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

    public Task PauseAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation("Orchestrator: PauseAsync → forwarding to proxy");
        return _proxy.PauseAsync(ct);
    }

    public Task ResumeAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation("Orchestrator: ResumeAsync → forwarding to proxy");
        return _proxy.ResumeAsync(ct);
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation("Orchestrator: StopAsync → forwarding to proxy");
        return _proxy.StopAsync(ct);
    }

    public Task SeekAsync(long positionMs, CancellationToken ct = default)
    {
        _logger?.LogInformation("Orchestrator: SeekAsync({Pos}ms) → forwarding to proxy", positionMs);
        return _proxy.SeekAsync(positionMs, ct);
    }

    public Task SetVolumeAsync(float volume, CancellationToken ct = default)
    {
        _logger?.LogDebug("Orchestrator: SetVolumeAsync({Volume:P0}) → forwarding to proxy", volume);
        return _proxy.SetVolumeAsync(volume, ct);
    }

    public Task SwitchAudioOutputAsync(int deviceIndex, CancellationToken ct = default)
    {
        _logger?.LogInformation("Orchestrator: SwitchAudioOutputAsync(idx={DeviceIndex}) → forwarding to proxy", deviceIndex);
        return _proxy.SwitchAudioOutputAsync(deviceIndex, ct);
    }

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

        // 4. Wait for audio key (always needed); CDN URL only if not using local cache
        await Task.WhenAll(resolution.AudioKeyTask, resolution.FileSizeTask);

        // 5. Send deferred resolution → AudioHost seamlessly continues
        if (resolution.LocalCacheFileId != null)
        {
            // File is fully cached on disk — no CDN URL needed
            await _proxy.SendDeferredCachedAsync(
                deferredId,
                resolution.LocalCacheFileId,
                await resolution.AudioKeyTask,
                await resolution.FileSizeTask,
                ct);
            _logger?.LogInformation("Deferred resolved from local cache for {Title}", resolution.Metadata?.Title);
        }
        else
        {
            await _proxy.SendDeferredResolvedAsync(
                deferredId,
                await resolution.CdnUrlTask,
                await resolution.AudioKeyTask,
                await resolution.FileSizeTask,
                spotifyFileId: resolution.SpotifyFileId,
                ct: ct);
            _logger?.LogInformation("Deferred CDN resolved — seamless playback continuing");
        }

        PublishQueueState();
    }

    private async Task OnTrackFinishedAsync(Playback.Contracts.TrackFinishedMessage msg)
    {
        try
        {
            // Auto-advance (or repeat-track rollover) is system-initiated — flip the
            // latch so the next publish carries is_system_initiated=true until the
            // user next calls PlayAsync.
            _isSystemInitiated = true;
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
        if (handler == null)
        {
            _logger?.LogWarning("Orchestrator: no ConnectCommandHandler — remote commands will NOT be handled");
            return;
        }

        _logger?.LogInformation("Orchestrator: subscribing to remote commands from ConnectCommandHandler");

        _subs.Add(handler.PlayCommands.Subscribe(cmd =>
        {
            _logger?.LogInformation("Remote cmd: play context={Context}, track={Track}, index={Index}, sender={Sender}",
                cmd.ContextUri ?? "<none>", cmd.TrackUri ?? "<none>", cmd.SkipToIndex, cmd.SenderDeviceId ?? "<none>");
            FireAndLog(PlayAsync(cmd), "play");
        }));
        _subs.Add(handler.PauseCommands.Subscribe(cmd =>
        {
            _logger?.LogInformation("Remote cmd: pause (sender={Sender})", cmd.SenderDeviceId ?? "<none>");
            FireAndLog(PauseAsync(), "pause");
        }));
        _subs.Add(handler.ResumeCommands.Subscribe(cmd =>
        {
            _logger?.LogInformation("Remote cmd: resume (sender={Sender})", cmd.SenderDeviceId ?? "<none>");
            FireAndLog(ResumeAsync(), "resume");
        }));
        _subs.Add(handler.SeekCommands.Subscribe(cmd =>
        {
            _logger?.LogInformation("Remote cmd: seek → {Pos}ms (sender={Sender})", cmd.PositionMs, cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SeekAsync(cmd.PositionMs), "seek");
        }));
        _subs.Add(handler.SkipNextCommands.Subscribe(cmd =>
        {
            _logger?.LogInformation("Remote cmd: skip_next (sender={Sender})", cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SkipNextAsync(), "skip_next");
        }));
        _subs.Add(handler.SkipPrevCommands.Subscribe(cmd =>
        {
            _logger?.LogInformation("Remote cmd: skip_prev (sender={Sender})", cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SkipPreviousAsync(), "skip_prev");
        }));
        _subs.Add(handler.ShuffleCommands.Subscribe(cmd =>
        {
            _logger?.LogInformation("Remote cmd: shuffle={Enabled} (sender={Sender})", cmd.Enabled, cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SetShuffleAsync(cmd.Enabled), "shuffle");
        }));
        _subs.Add(handler.RepeatContextCommands.Subscribe(cmd =>
        {
            _logger?.LogInformation("Remote cmd: repeat_context={Enabled} (sender={Sender})", cmd.Enabled, cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SetRepeatContextAsync(cmd.Enabled), "repeat_context");
        }));
        _subs.Add(handler.RepeatTrackCommands.Subscribe(cmd =>
        {
            _logger?.LogInformation("Remote cmd: repeat_track={Enabled} (sender={Sender})", cmd.Enabled, cmd.SenderDeviceId ?? "<none>");
            FireAndLog(SetRepeatTrackAsync(cmd.Enabled), "repeat_track");
        }));
    }

    /// <summary>
    /// Bare <c>_ = SomeAsync()</c> swallows faults silently (the Task is GC'd
    /// before TaskScheduler.UnobservedTaskException sees it). Route all
    /// fire-and-forget remote commands through this so failures land in the log
    /// with a label.
    /// </summary>
    private void FireAndLog(System.Threading.Tasks.Task task, string label)
    {
        if (task.IsCompletedSuccessfully) return;
        _ = task.ContinueWith(t =>
            {
                if (t.Exception != null)
                    _logger?.LogError(t.Exception.GetBaseException(), "Orchestrator command '{Label}' failed", label);
            },
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
            | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
    }

    // ── State ──

    private void OnProxyStateChanged(LocalPlaybackState engineState)
    {
        var prev = _stateSubject.Value;

        // Log significant state transitions
        if (prev.TrackUri != engineState.TrackUri)
            _logger?.LogInformation("Orchestrator: track changed: {From} → {To}", prev.TrackUri ?? "<none>", engineState.TrackUri ?? "<none>");
        if (prev.IsPlaying != engineState.IsPlaying)
            _logger?.LogInformation("Orchestrator: IsPlaying changed: {From} → {To} (track={Track}, pos={Pos}ms)",
                prev.IsPlaying, engineState.IsPlaying, engineState.TrackUri ?? "<none>", engineState.PositionMs);
        if (prev.IsBuffering != engineState.IsBuffering)
            _logger?.LogDebug("Orchestrator: IsBuffering changed: {From} → {To}", prev.IsBuffering, engineState.IsBuffering);

        // Enrich engine state with queue info — fetch each list once and reuse it
        // for both the typed projection and the IQueueItem cast. The previous code
        // called GetPrevTracks/GetNextTracks twice each, so every state push (which
        // can fire several times per second during normal playback) re-enumerated
        // the queue 4× and allocated 4 lists.
        var prevTracks = _queue.GetPrevTracks();
        var nextTracks = _queue.GetNextTracks();
        var prevRefs = new List<TrackReference>(prevTracks.Count);
        for (int i = 0; i < prevTracks.Count; i++)
        {
            var t = prevTracks[i];
            prevRefs.Add(new TrackReference(t.Uri, t.Uid ?? "", t.AlbumUri, t.ArtistUri, t.IsUserQueued));
        }
        var nextRefs = new List<TrackReference>(nextTracks.Count);
        for (int i = 0; i < nextTracks.Count; i++)
        {
            var t = nextTracks[i];
            nextRefs.Add(new TrackReference(t.Uri, t.Uid ?? "", t.AlbumUri, t.ArtistUri, t.IsUserQueued));
        }

        var enriched = engineState with
        {
            ContextUri = _queue.ContextUri ?? engineState.ContextUri,
            Shuffling = _queue.IsShuffled,
            RepeatingContext = _repeatContext,
            RepeatingTrack = _repeatTrack,
            CurrentIndex = _queue.CurrentIndex,
            PrevTracks = prevRefs,
            NextTracks = nextRefs,
            PrevQueueItems = prevTracks.Cast<IQueueItem>().ToList(),
            NextQueueItems = nextTracks.Cast<IQueueItem>().ToList(),
            QueueRevision = _queue.GetQueueRevision(),
            ContextDescription = _currentContextDescription,
            IsSystemInitiated = _isSystemInitiated,
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
