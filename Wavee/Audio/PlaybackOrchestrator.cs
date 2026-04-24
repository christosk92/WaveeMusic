using System.Collections.Generic;
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
    private readonly Subject<EndOfContextEvent> _endOfContextSubject = new();

    private bool _repeatContext;
    private bool _repeatTrack;
    private bool _disposed;

    // Context subtitle (artist name, playlist title) — set from PlayCommand at
    // play time, emitted on every subsequent state publish until the next PlayAsync.
    private string? _currentContextDescription;

    // Context artwork, kind, and size — likewise set from PlayCommand and re-emitted
    // with every state push so remote "Now Playing" cards stay consistent.
    private string? _currentContextImageUrl;
    private string? _currentContextFeature;
    private int? _currentContextTrackCount;
    private IReadOnlyDictionary<string, string>? _currentContextFormatAttributes;

    // Pagination state. _currentNextPageUrl is the hm://... URL to fetch the
    // next batch of tracks for the active context (artist discography, long
    // playlist, etc.). Set from ContextLoadResult on PlayAsync (or from a
    // background resolve in the UI-initiated path) and advanced each time
    // LoadMoreTracksAsync succeeds. Null once the context is fully loaded.
    private string? _currentNextPageUrl;
    private int _currentContextPageCount = 1;
    private readonly System.Threading.SemaphoreSlim _loadMoreLock = new(1, 1);

    // Autoplay fallback state. When the page chain exhausts on a bounded
    // non-radio context (album, playlist, artist discography), we call
    // ContextResolver.LoadAutoplayAsync once to prefetch station tracks. Once
    // that's done, the queue's ContextUri switches to the station URI and
    // _currentNextPageUrl picks up autoplay's own pagination — so subsequent
    // NeedsMoreTracks signals flow through the regular LoadNextPageAsync path.
    // _originalContextUri is what we send to the autoplay endpoint (the album /
    // playlist / artist URI we were playing before switchover).
    private bool _autoplayTriggered;
    private string? _originalContextUri;

    // Reference to the in-flight autoplay fetch, so OnTrackFinishedAsync can
    // await it if end-of-queue hits before autoplay returns (rapid-skip or
    // "started on last track" edge cases). Null outside the fetch.
    private Task? _pendingAutoplayTask;

    /// <summary>
    /// Lazy-read check for whether autoplay is enabled. Invoked once per
    /// candidate autoplay trigger in <see cref="TryTriggerAutoplayAsync"/>;
    /// when it returns false, the trigger short-circuits and playback reaches
    /// end-of-context as a clean stop. Wired from the UI layer to read from
    /// <c>AppSettings.AutoplayEnabled</c>. Null = assume enabled.
    /// </summary>
    public Func<bool>? AutoplayEnabledProvider { get; set; }

    // Latch: set true for auto-advance / transfer-resume / autoplay rollover,
    // false for user-initiated play. Sticks until the next PlayAsync flips it.
    private bool _isSystemInitiated;

    // Prefetch dedup: remembers which upcoming track URI we've already kicked a
    // prefetch for during the current track. Reset on track change / play / skip.
    // Spotify's PortAudio buffer underruns at transitions all trace back to the
    // AudioKey being requested reactively under HTTP contention; prefetching while
    // the current track plays avoids the race entirely.
    private string? _lastPrefetchedTrackUri;
    private CancellationTokenSource? _prefetchCts;

    // Trigger thresholds. Librespot prefetches when "≈30 s remaining OR past 50 %".
    // We use the same shape: the earlier of the two fires.
    private const long PrefetchRemainingMsThreshold = 20_000;
    private const double PrefetchHalfwayFraction = 0.5;

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

    /// <summary>
    /// Fires when playback reaches end-of-context and every advance tier has
    /// failed (direct next track, repeat-context, autoplay in-flight wait,
    /// autoplay synchronous trigger). The UI subscribes and surfaces a "reached
    /// the end" notification with whatever phrasing fits the event.
    /// </summary>
    public IObservable<EndOfContextEvent> EndOfContext => _endOfContextSubject.AsObservable();
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
            _currentContextImageUrl = command.ContextImageUrl;
            _currentContextFeature = command.ContextFeature;
            _currentContextTrackCount = command.ContextTrackCount;
            _currentContextFormatAttributes = command.ContextFormatAttributes;
            // Reset pagination state; the specific branches below repopulate these.
            _currentNextPageUrl = null;
            _currentContextPageCount = 1;
            _autoplayTriggered = false;
            _pendingAutoplayTask = null;
            _originalContextUri = !string.IsNullOrEmpty(command.ContextUri)
                                  && command.ContextUri != "spotify:internal:queue"
                ? command.ContextUri
                : null;
            ResetPrefetch();

            // Build queue from context and/or explicit track list.
            var hasRealContext = !string.IsNullOrEmpty(command.ContextUri)
                                 && command.ContextUri != "spotify:internal:queue";
            var hasPageTracks = command.PageTracks?.Count > 0;

            if (hasRealContext && hasPageTracks)
            {
                // UI-initiated play with a known context. Use the provided tracks
                // (caller may have filtered/shuffled) but publish the real context URI.
                var tracks = command.PageTracks!
                    .Select(t => new QueueTrack(t.Uri, t.Uid) { Metadata = t.Metadata })
                    .ToList();
                _queue.Clear();
                _queue.SetContext(command.ContextUri!, isInfinite: false, totalTracks: tracks.Count);
                _queue.SetTracks(tracks, command.SkipToIndex ?? 0);

                // The UI supplies its own page-0 tracks (e.g. an extended top-tracks
                // view) but doesn't know the next-page URL or total page count. Kick
                // a background resolve so LoadMoreTracksAsync has somewhere to page
                // to once the user plays through what the UI provided.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var resolved = await _contextResolver.LoadContextAsync(command.ContextUri!);
                        _currentNextPageUrl = resolved.NextPageUrl;
                        _currentContextPageCount = resolved.PageCount;
                        _logger?.LogDebug(
                            "Pagination seeded for {Uri}: nextPage={HasNext}, pageCount={Count}",
                            command.ContextUri, resolved.NextPageUrl != null, resolved.PageCount);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Pagination seed failed for {Uri}", command.ContextUri);
                    }
                });
            }
            else if (hasRealContext)
            {
                // Remote transfer / deep-link: resolve the full context from Spotify.
                var context = await _contextResolver.LoadContextAsync(command.ContextUri!, ct: ct);
                _queue.SetContext(command.ContextUri!, context.IsInfinite, context.TotalCount);
                _queue.SetTracks(context.Tracks);

                // /context-resolve/v1/ returns rich context-level metadata
                // (format_list_type, tag, request_id, image_url, header_image_url_desktop,
                //  session_control_display.displayName.*, context_description, …).
                // Forward it so our putstate publishes the same context_metadata
                // Spotify desktop would, not an empty dict.
                if (context.ContextMetadata is { Count: > 0 })
                {
                    _currentContextFormatAttributes = context.ContextMetadata;
                    if (context.ContextMetadata.TryGetValue("context_description", out var desc)
                        && !string.IsNullOrEmpty(desc))
                    {
                        _currentContextDescription = desc;
                    }
                    if (context.ContextMetadata.TryGetValue("image_url", out var img)
                        && !string.IsNullOrEmpty(img))
                    {
                        _currentContextImageUrl = img;
                    }
                }
                _currentContextTrackCount ??= context.TotalCount ?? context.Tracks.Count;

                // Pagination state from the resolver — LoadMoreTracksAsync uses
                // this to fetch the next batch once the queue runs low.
                _currentNextPageUrl = context.NextPageUrl;
                _currentContextPageCount = context.PageCount;

                var startIndex = ContextResolver.FindTrackIndex(
                    context.Tracks, command.TrackUri, command.TrackUid, command.SkipToIndex);
                _queue.SkipTo(startIndex);
            }
            else if (hasPageTracks)
            {
                // True internal queue — no originating context.
                var tracks = command.PageTracks!
                    .Select(t => new QueueTrack(t.Uri, t.Uid) { Metadata = t.Metadata })
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

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        // Post-end-of-context case: EndOfContextAsync reset the queue cursor
        // to 0 and called proxy.StopAsync. The engine has no loaded track, so
        // a plain Resume is a no-op (the symptom users reported as "Play button
        // does nothing"). Detect that state and replay the queue's current
        // track from position 0 — "Play" behaves as "restart the context."
        var engineState = _stateSubject.Value;
        var engineIdle = !engineState.IsPlaying && !engineState.IsPaused && !engineState.IsBuffering;
        if (engineIdle && _queue.Current != null)
        {
            _logger?.LogInformation("Orchestrator: ResumeAsync from Stopped → restarting queue from track 0");
            await PlayCurrentTrackAsync(0, ct);
            return;
        }

        _logger?.LogInformation("Orchestrator: ResumeAsync → forwarding to proxy");
        await _proxy.ResumeAsync(ct);
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
        // Shared end-of-queue handling with OnTrackFinishedAsync. Without this
        // delegation, skip-next past the last track bypassed Phase E's autoplay
        // fallback entirely — it just called StopAsync and left the engine in
        // an ambiguous "Stopped with mid-track position" state.
        if (!await TryAdvanceOrAutoplayAsync(ct))
        {
            await EndOfContextAsync(ct);
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
            ResetPrefetch();
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

            if (!await TryAdvanceOrAutoplayAsync())
            {
                await EndOfContextAsync();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error advancing to next track");
        }
    }

    /// <summary>
    /// Shared end-of-queue handling. Attempts, in order: direct advance to next
    /// track → repeat-context wrap → wait for in-flight autoplay prefetch →
    /// trigger autoplay synchronously. Returns true if any tier succeeded and
    /// playback continued; false if all exhausted.
    /// </summary>
    private async Task<bool> TryAdvanceOrAutoplayAsync(CancellationToken ct = default)
    {
        // Tier 0: direct advance
        var next = _queue.MoveNext();
        if (next != null)
        {
            ResetPrefetch();
            _logger?.LogInformation("Orchestrator: advance → {Uri}", next.Uri);
            await PlayCurrentTrackAsync(0, ct);
            return true;
        }

        // Tier 1: repeat context
        if (_repeatContext)
        {
            _queue.SkipTo(0);
            await PlayCurrentTrackAsync(0, ct);
            return true;
        }

        // Tier 2: autoplay fetch in-flight, wait briefly. Signal fires at T-5
        // tracks remaining; in the normal case autoplay is long since staged
        // and Tier 0 succeeded. This tier catches the rapid-skip race.
        var pending = _pendingAutoplayTask;
        if (pending is { IsCompleted: false })
        {
            _logger?.LogInformation("End of queue — autoplay in-flight, waiting up to 3s");
            var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(3), ct));
            if (completed == pending && _queue.HasNext)
            {
                next = _queue.MoveNext();
                if (next != null)
                {
                    await PlayCurrentTrackAsync(0, ct);
                    return true;
                }
            }
            else
            {
                _logger?.LogWarning("Autoplay fetch exceeded grace timeout — giving up");
            }
        }

        // Tier 3: autoplay never triggered (user started on / skipped directly
        // to a track past the T-5 threshold, so NeedsMoreTracks never fired).
        // Fire synchronously — brief stall while HTTP returns, then advance.
        if (!_autoplayTriggered)
        {
            _logger?.LogInformation("End of queue without autoplay trigger — firing synchronously");
            await TryTriggerAutoplayAsync();
            if (_queue.HasNext)
            {
                next = _queue.MoveNext();
                if (next != null)
                {
                    await PlayCurrentTrackAsync(0, ct);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Called when all advance tiers in <see cref="TryAdvanceOrAutoplayAsync"/>
    /// fail. Stops the engine in a coherent state (position 0, queue cursor
    /// reset to 0) and emits an <see cref="EndOfContextEvent"/> so the UI can
    /// show a toast / restart button.
    ///
    /// Without this, the engine's last-published state was "Status=Stopped,
    /// track=lastUri, pos=3600ms" — an ambiguous mid-track frozen state. The
    /// PlayerBar rendered it as "kind of playing", the Play button had nothing
    /// to resume (engine was stopped), and the queue's CurrentIndex was past
    /// the end, so there was no defined recovery path.
    /// </summary>
    private async Task EndOfContextAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation("End of context — all advance tiers exhausted");

        // Reset queue cursor to the start so a subsequent "Play" click has
        // a well-defined target (track 0 of the current context) instead of
        // firing from an out-of-bounds index.
        if (_queue.LoadedCount > 0)
            _queue.SkipTo(0);

        // Full stop — position resets to 0 and the engine unloads the track.
        await _proxy.StopAsync(ct);

        // Signal the UI. OriginalContextUri is populated by PlayAsync on every
        // play-with-context; null on internal-queue playthroughs.
        _endOfContextSubject.OnNext(new EndOfContextEvent(
            OriginalContextUri: _originalContextUri,
            AutoplayAttempted: _autoplayTriggered,
            ContextSupportsAutoplay: ContextSupportsAutoplayFor(_originalContextUri)));
    }

    private static bool ContextSupportsAutoplayFor(string? uri)
        => !string.IsNullOrEmpty(uri)
           && !uri.Contains(":station:")
           && !uri.Contains(":radio:")
           && !uri.Contains(":autoplay:")
           && !uri.Contains(":internal:");

    private async Task LoadMoreTracksAsync()
    {
        // PlaybackQueue.NeedsMoreTracks fires near end-of-queue. Two fallbacks
        // in priority order:
        //   1. If we know a next-page URL → paginate within the current context.
        //   2. Otherwise → trigger autoplay (once per context) for bounded,
        //      non-radio contexts. The resulting tracks are appended, the queue
        //      context URI flips to the station URI, and autoplay's own
        //      NextPageUrl takes over subsequent pagination.
        //
        // Semaphore guards against overlapping fires — NeedsMoreTracks can pulse
        // repeatedly as the user skips around near end-of-queue and we don't want
        // to stack concurrent pagination requests.
        if (!await _loadMoreLock.WaitAsync(0))
        {
            _logger?.LogDebug("LoadMoreTracks already in flight — skipping duplicate signal");
            return;
        }

        try
        {
            var pageUrl = _currentNextPageUrl;
            if (!string.IsNullOrEmpty(pageUrl))
            {
                _logger?.LogInformation("Queue needs more tracks — fetching next page {Url}", pageUrl);
                var result = await _contextResolver.LoadNextPageAsync(pageUrl);

                if (result.Tracks.Count > 0)
                {
                    _queue.AppendTracks(result.Tracks);
                    _logger?.LogDebug("Appended {Count} tracks from next page", result.Tracks.Count);
                }

                _currentNextPageUrl = result.NextPageUrl;
                return;
            }

            // No more pages — try autoplay.
            await TryTriggerAutoplayAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LoadMoreTracksAsync failed");
        }
        finally
        {
            _loadMoreLock.Release();
        }
    }

    /// <summary>
    /// Projects <paramref name="nextTracks"/> into an <c>IQueueItem</c> list,
    /// injecting a <c>QueueDelimiter</c> at the first context→autoplay provider
    /// boundary so remote UIs render the "You've reached the end of X, now
    /// playing similar songs" separator between regular and autoplay tracks.
    /// </summary>
    private static List<IQueueItem> BuildNextQueueItems(IReadOnlyList<QueueTrack> nextTracks)
    {
        var result = new List<IQueueItem>(nextTracks.Count + 1);
        var delimInserted = false;
        string? prevProvider = null;

        for (var i = 0; i < nextTracks.Count; i++)
        {
            var t = nextTracks[i];
            if (!delimInserted
                && t.Provider == "autoplay"
                && prevProvider is not null
                && prevProvider != "autoplay")
            {
                result.Add(new QueueDelimiter(
                    AdvanceAction: "pause",
                    SkipAction: "pause",
                    Provider: "autoplay"));
                delimInserted = true;
            }
            result.Add(t);
            prevProvider = t.Provider;
        }

        return result;
    }

    /// <summary>
    /// Guards + publishes the autoplay Task to <see cref="_pendingAutoplayTask"/>
    /// so end-of-queue handling can await it if the race hits. The actual HTTP
    /// fetch + queue mutation lives in <see cref="DoAutoplayFetchAsync"/>.
    /// </summary>
    private Task TryTriggerAutoplayAsync()
    {
        if (_autoplayTriggered) return Task.CompletedTask;
        if (AutoplayEnabledProvider?.Invoke() == false) return Task.CompletedTask;
        if (string.IsNullOrEmpty(_originalContextUri)) return Task.CompletedTask;

        // Don't autoplay out of an already-radio/station/autoplay context.
        // Those are infinite and paginate themselves via LoadNextPageAsync.
        if (_originalContextUri.Contains(":station:") ||
            _originalContextUri.Contains(":radio:") ||
            _originalContextUri.Contains(":autoplay:"))
            return Task.CompletedTask;

        _autoplayTriggered = true;
        var task = DoAutoplayFetchAsync();
        _pendingAutoplayTask = task;

        // Clear the reference when the fetch completes so later code doesn't
        // await a long-settled task. ExecuteSynchronously keeps the window
        // between "task finishes" and "field clears" to a single instruction.
        _ = task.ContinueWith(_ => _pendingAutoplayTask = null,
            TaskContinuationOptions.ExecuteSynchronously);
        return task;
    }

    private async Task DoAutoplayFetchAsync()
    {
        var recent = _queue.GetRecentTrackUris(5);

        _logger?.LogInformation("Queue exhausted for {Uri} — prefetching autoplay", _originalContextUri);
        ContextLoadResult autoplay;
        try
        {
            autoplay = await _contextResolver.LoadAutoplayAsync(_originalContextUri!, recent);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Autoplay fetch failed for {Uri}", _originalContextUri);
            return;
        }

        if (autoplay.Tracks.Count == 0)
        {
            _logger?.LogDebug("Autoplay returned no tracks for {Uri}", _originalContextUri);
            return;
        }

        // Append the autoplay tracks (each already carries Provider="autoplay"
        // from LoadAutoplayAsync). StampContextUri inside AppendTracks bakes
        // the CURRENT queue context URI into each appended track — at this
        // point we haven't flipped it yet, so we flip FIRST, then append.
        var stationUri = autoplay.ResolvedContextUri ?? _originalContextUri!;
        _queue.UpdateContext(stationUri, isInfinite: true);
        _queue.AppendTracks(autoplay.Tracks);

        // Pagination cursor moves to autoplay's own chain.
        _currentNextPageUrl = autoplay.NextPageUrl;
        _currentContextPageCount = autoplay.PageCount;

        // Swap context metadata to the station's. Server pre-bakes
        // context_description (e.g. "Jason Derulo Radio") and the various
        // session_control_display.* keys — forward verbatim.
        if (autoplay.ContextMetadata is { Count: > 0 } md)
        {
            _currentContextFormatAttributes = md;
            if (md.TryGetValue("context_description", out var desc) && !string.IsNullOrEmpty(desc))
                _currentContextDescription = desc;
            if (md.TryGetValue("image_url", out var img) && !string.IsNullOrEmpty(img))
                _currentContextImageUrl = img;
        }

        _logger?.LogInformation(
            "Autoplay active: station={Station}, tracks={Count}, nextPage={HasNext}",
            stationUri, autoplay.Tracks.Count, autoplay.NextPageUrl != null);
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
            NextQueueItems = BuildNextQueueItems(nextTracks),
            QueueRevision = _queue.GetQueueRevision(),
            ContextDescription = _currentContextDescription,
            ContextImageUrl = _currentContextImageUrl,
            ContextFeature = _currentContextFeature,
            ContextTrackCount = _currentContextTrackCount,
            ContextFormatAttributes = _currentContextFormatAttributes,
            ContextPageCount = _currentContextPageCount,
            IsSystemInitiated = _isSystemInitiated,
        };
        _stateSubject.OnNext(enriched);

        // When the current track changed, throw away any in-flight prefetch for the
        // *old* successor and let the prefetch dedup fire afresh for the new successor.
        if (prev.TrackUri != engineState.TrackUri)
        {
            ResetPrefetch();
        }

        MaybeTriggerPrefetch(enriched);
    }

    /// <summary>
    /// Fires an AudioKey/head/CDN prefetch for the next track once the current one
    /// is past its halfway point or within ~20 s of ending. Librespot does the same;
    /// without it we request the key reactively at track-finish, racing a burst of
    /// HTTP work and blowing the 5 s timeout under contention.
    /// </summary>
    private void MaybeTriggerPrefetch(LocalPlaybackState state)
    {
        if (!state.IsPlaying) return;
        if (_repeatTrack) return;                             // next target = current
        if (state.DurationMs <= 0) return;                    // unknown duration, can't decide
        if (_lastPrefetchedTrackUri != null) return;          // already queued for this track

        var remainingMs = state.DurationMs - state.PositionMs;
        var pastHalf = state.PositionMs >= (long)(state.DurationMs * PrefetchHalfwayFraction);
        var nearEnd = remainingMs > 0 && remainingMs <= PrefetchRemainingMsThreshold;
        if (!pastHalf && !nearEnd) return;

        // Peek next — don't advance. Skip hidden markers / delimiters (they come
        // through QueueTrack only, which is what GetNextTracks returns).
        var nextTracks = _queue.GetNextTracks();
        if (nextTracks.Count == 0) return;
        var target = nextTracks[0];
        if (string.IsNullOrEmpty(target.Uri)) return;

        _lastPrefetchedTrackUri = target.Uri;
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _prefetchCts, cts);
        try { prev?.Cancel(); prev?.Dispose(); } catch { /* best effort */ }

        _logger?.LogDebug("Orchestrator: prefetching next track {Uri} (pos={Pos}ms/{Dur}ms)",
            target.Uri, state.PositionMs, state.DurationMs);

        _ = Task.Run(async () =>
        {
            try { await _trackResolver.PrefetchAsync(target.Uri, cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Prefetch task fault (non-fatal)"); }
        }, cts.Token);
    }

    /// <summary>
    /// Invalidate the current prefetch bookmark so the next state update can
    /// kick off a fresh prefetch for whatever is now the successor track.
    /// Also cancels any in-flight prefetch — the network work would be wasted
    /// if the user skipped or the queue layout changed.
    /// </summary>
    private void ResetPrefetch()
    {
        _lastPrefetchedTrackUri = null;
        var cts = Interlocked.Exchange(ref _prefetchCts, null);
        if (cts != null)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* best effort */ }
        }
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
        ResetPrefetch();
        _subs.Dispose();
        _stateSubject.Dispose();
        _errorSubject.Dispose();
        _endOfContextSubject.Dispose();
    }
}

/// <summary>
/// Published by <see cref="PlaybackOrchestrator.EndOfContext"/> when playback
/// reaches end-of-context and every advance / autoplay tier has failed. The UI
/// layer subscribes and shows a contextual notification.
/// <para/>
/// <see cref="ContextSupportsAutoplay"/> reflects whether the original context
/// URI is of a kind that Spotify generates autoplay recommendations for
/// (albums, playlists, artist pages) versus kinds where autoplay is meaningless
/// (radio / station / internal queue — those are already autoplay or have no
/// source).
/// </summary>
public sealed record EndOfContextEvent(
    string? OriginalContextUri,
    bool AutoplayAttempted,
    bool ContextSupportsAutoplay);
