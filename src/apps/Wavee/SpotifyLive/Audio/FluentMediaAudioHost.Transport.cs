using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Media;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;

namespace Wavee.SpotifyLive.Audio;

public sealed partial class FluentMediaAudioHost
{
    CancellationTokenSource? _seekCancellation;
    readonly SemaphoreSlim _seekWorker = new(1, 1);
    // Findings #1/#2 (library-v3-1): a seek that cannot be applied to anything real RIGHT NOW — a deferred load still
    // resolving (finding #1) or a fast-start body not attached yet (finding #2's third case) — parks here instead of
    // touching a stale session or faulting. `_requestedStartMs` (already the host's "initial position for the next
    // open" seam) carries the target; this pair remembers WHICH operation to resolve once that open happens, and at
    // what seek-revision it was parked at, so a newer seek arriving meanwhile supersedes it instead of double-resolving.
    AudioTransportRequest? _parkedSeek;
    long _parkedSeekRevision;

    public PlaybackCommandReceipt Submit(AudioTransportRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (IsOlder(request.Command, _commandSnapshot))
            {
                Enqueue(() => { PublishOperation(request, PlaybackOperationStatus.Superseded); return Task.CompletedTask; });
                return new PlaybackCommandReceipt(request.Command);
            }
            _commandSnapshot = request.Command;
            if (request.Action != AudioTransportAction.Adopt) _playIntent = request.PlayWhenReady;
        }
        if (request.Action == AudioTransportAction.Seek)
        {
            Interlocked.Increment(ref _seekRevision);
            CancelPendingSeek();
        }
        long revision = Volatile.Read(ref _seekRevision);
        Enqueue(async () =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (IsOlder(request.Command, _latestCommand) && request.Action != AudioTransportAction.Seek)
                {
                    PublishOperation(request, PlaybackOperationStatus.Superseded);
                    return;
                }
                PublishOperation(request, PlaybackOperationStatus.Accepted);
                // Findings #1/#2: a seek that parks itself (ApplySeekAsync returns true) has already decided its own
                // resolution — either it published one explicitly (no session / non-seekable) or it deliberately left
                // the operation Accepted for a later open/body-attach to settle (see ResolveParkedSeekAfterOpen /
                // FlushParkedSeekAfterBodyAttachAsync). Either way the generic post-switch publish below must not
                // also fire — it would overwrite an explicit resolution, or prematurely resolve a still-parked one
                // using whatever stale position the CURRENT (wrong) session happens to report.
                bool seekHandledItself = false;
                switch (request.Action)
                {
                    case AudioTransportAction.Adopt:
                        break;
                    case AudioTransportAction.Play:
                        if (_playIntent && _session is { } playing) await playing.PlayAsync();
                        StartTicker();
                        break;
                    case AudioTransportAction.Pause:
                        if (!_playIntent && _session is { } pausing)
                        {
                            await pausing.PauseAsync();
                        }
                        StartTicker();
                        break;
                    case AudioTransportAction.Stop:
                    {
                        var stopping = _session;
                        _loadCancellation.Cancel();
                        CancelPendingSeek();
                        Interlocked.Increment(ref _loadEpoch);
                        _clockStale = true;
                        // Finding #1/#2: a Stop cancels the very load a parked seek was waiting on — its own flush
                        // point (ResolveParkedSeekAfterOpen / FlushParkedSeekAfterBodyAttachAsync) will never run now,
                        // so settle it here instead of leaving the controller's operation stuck at Accepted forever.
                        if (_parkedSeek is { } parkedAtStop)
                        {
                            _parkedSeek = null;
                            PublishOperationAt(parkedAtStop, PlaybackOperationStatus.Superseded, PositionMs);
                        }
                        if (stopping is PcmAudioSession pcm)
                        {
                            using var drain = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                            try { await pcm.FadeOutAsync(TimeSpan.FromMilliseconds(20), drain.Token); }
                            catch (OperationCanceledException) when (drain.IsCancellationRequested) { }
                        }
                        if (request.Command == _latestCommand && ReferenceEquals(stopping, _session))
                        {
                            await DisposeSessionAsync();
                            StopTicker();
                        }
                        else if (ReferenceEquals(stopping, _session)) _clockStale = false;
                        break;
                    }
                    case AudioTransportAction.Skip:
                        if (_session is PcmAudioSession skipping)
                            await skipping.FadeOutAsync(TimeSpan.FromMilliseconds(50), _loadCancellation.Token);
                        break;
                    case AudioTransportAction.Seek:
                        seekHandledItself = await ApplySeekAsync(request, revision);
                        break;
                }
                _lastCommandApplicationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (!seekHandledItself)
                {
                    var status = request.Action == AudioTransportAction.Seek
                        ? revision == Volatile.Read(ref _seekRevision) ? PlaybackOperationStatus.Applied : PlaybackOperationStatus.Superseded
                        : request.Command == _latestCommand ? PlaybackOperationStatus.Applied : PlaybackOperationStatus.Superseded;
                    PublishOperation(request, status);
                    _log.Info($"[audio] command seq={request.Command.Sequence} action={request.Action} status={status} elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
                }
            }
            catch (OperationCanceledException) { PublishOperation(request, PlaybackOperationStatus.Superseded); }
            catch (Exception error)
            {
                PublishSignal(AudioHostSignal.Fault(PositionMs, AudioKeyFailureReason.None, error.Message) with
                {
                    Command = request.Command,
                    OperationStatus = PlaybackOperationStatus.Failed,
                    PlayWhenReady = _playIntent
                });
            }
        });
        return new PlaybackCommandReceipt(request.Command);
    }

    static bool IsOlder(PlaybackCommandId candidate, PlaybackCommandId current)
        => candidate.ItemGeneration < current.ItemGeneration
            || candidate.ItemGeneration == current.ItemGeneration && candidate.Sequence < current.Sequence;

    void PublishOperation(AudioTransportRequest request, PlaybackOperationStatus status) => PublishOperationAt(request, status, PositionMs);

    // Findings #1/#2: the explicit-position twin of PublishOperation — used wherever the operation resolves to a
    // position the live clock does not (yet, or ever) reflect: "applied at the target" for a seek parked against no
    // session at all, and the various parked-seek flush points below.
    void PublishOperationAt(AudioTransportRequest request, PlaybackOperationStatus status, long positionMs)
    {
        AudioHostSignalKind kind = _playIntent
            ? IsPlaying ? AudioHostSignalKind.Playing : AudioHostSignalKind.Buffering
            : AudioHostSignalKind.Paused;
        PublishSignal(new AudioHostSignal(kind, positionMs, IsPlaying, IsBuffering, false)
        {
            Command = request.Command,
            OperationStatus = status,
            PlayWhenReady = _playIntent
        });
    }

    // Returns true when THIS call has already fully settled the operation's resolution (either by publishing it
    // explicitly, or by deliberately leaving it Accepted for a later open/body-attach to settle) — see the
    // `seekHandledItself` guard in Submit above. False means "ran the real seek", in which case Submit's generic
    // revision-based Applied/Superseded publish still applies, exactly as before these findings.
    async Task<bool> ApplySeekAsync(AudioTransportRequest request, long revision)
    {
        if (revision != Volatile.Read(ref _seekRevision)) return false;   // superseded already — the newer seek resolves it
        long target = Math.Max(0, request.PositionMs);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation.Token);
        lock (_gate) _seekCancellation = cancellation;
        bool ownsWorker = false;
        long operationEpoch = Volatile.Read(ref _loadEpoch);
        try
        {
            await _seekWorker.WaitAsync(cancellation.Token);
            ownsWorker = true;

            // Finding #2 (no session): nothing is installed for this host at all — a fresh host, or after Stop().
            // Checked BEFORE the loadPending mismatch below: on a fresh host `_lastStart.TrackUri` (never set) and
            // `_activeUri` ("") are not equal either, but there is no STALE session to protect from a wrongful
            // reopen — finding #1's concern doesn't apply. Resolve it NOW rather than risk it never being consumed:
            // still honest ("applied at target" is exactly what the NEXT load will open at, since it also reads
            // `_requestedStartMs`), and never a Fault. The old host's Seek() silently returned on a null session;
            // this stays at least that benign, but logs once instead of being silent.
            if (_session is not PcmAudioSession session || _activeBytes is not { } original)
            {
                _requestedStartMs = target;
                _log.Info($"[audio] seek with no session loaded — parked targetMs={target} seq={request.Command.Sequence}");
                PublishOperationAt(request, PlaybackOperationStatus.Applied, target);
                return true;
            }

            // Finding #1: a REAL session is installed (checked above) — but `_lastStart.TrackUri` (set the instant
            // LoadFastStart[Async] runs for the load actually in flight) may already name a DIFFERENT track than
            // `_activeUri` (which only catches up once THAT load's own session is installed by OpenSessionAsync).
            // A mismatch means the installed session — however real and playing — belongs to an OLDER track.
            // Reopening it here (the old bug) would audibly seek/replay the track the user is navigating AWAY from.
            // Park instead: `_requestedStartMs` is the seam every session-open already honours as its initial
            // position, so the load that is coming opens directly at the seek's target; ResolveParkedSeekAfterOpen
            // settles the operation once that happens.
            bool loadPending = !string.Equals(_activeUri, _lastStart.TrackUri, StringComparison.Ordinal);
            if (loadPending)
            {
                _requestedStartMs = target;
                _parkedSeek = request;
                _parkedSeekRevision = revision;
                _log.Info($"[audio] seek parked seq={request.Command.Sequence} targetMs={target} — a load is still resolving (installed={_activeUri}, loading={_lastStart.TrackUri})");
                return true;
            }
            if (!original.ReadStream.AsStream().CanSeek)
            {
                // A genuinely non-seekable source (live/ICY, a module stream that reports no length). Reject WITHOUT
                // a Fault — Superseded (not Applied) so PlaybackController.CompleteSeekAsync never rewrites the
                // projected position, i.e. the seek bar snaps back to wherever it honestly is instead of freezing at
                // the target.
                _log.Info($"[audio] seek rejected — source has no seekable timeline seq={request.Command.Sequence}");
                PublishOperationAt(request, PlaybackOperationStatus.Superseded, PositionMs);
                return true;
            }
            if (original.ReopenBody is null)
            {
                // Fast-start window: the clear head is already open/playing, but the encrypted body has not
                // attached — ReopenSourceAsync cannot build an independent reopen yet. Park; SupplyBodyAsync's own
                // attach flushes this once the body lands (FlushParkedSeekAfterBodyAttachAsync), performing the real
                // seek then.
                _requestedStartMs = target;
                _parkedSeek = request;
                _parkedSeekRevision = revision;
                _log.Info($"[audio] seek parked seq={request.Command.Sequence} targetMs={target} — the body has not attached yet");
                return true;
            }

            long epoch = Volatile.Read(ref _loadEpoch);
            _clockStale = true;
            AbandonPendingJoin(session, "seek");
            await DisposePreparedSlotAsync();
            await session.FadeOutAsync(TimeSpan.FromMilliseconds(5), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var fresh = await ReopenSourceAsync(original, cancellation.Token);
            using var registration = cancellation.Token.Register(fresh.Cancel);
            IPreparedItem? item = null;
            bool installed = false;
            long actual;
            try
            {
                var backend = await GetBackendAsync();
                item = await backend.PrepareAtAsync(MediaSource.FromPull(fresh).WithKind(MediaKind.PcmAudio),
                    PrepareContext.For(session.Format, session.NormalizationMode, session.ReferenceLufsValue),
                    MsToFrames(target, session.Format.SampleRate), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (epoch != Volatile.Read(ref _loadEpoch) || revision != Volatile.Read(ref _seekRevision)) return false;
                actual = item is AudioPreparedItem prepared ? prepared.StartPositionFrames : MsToFrames(target, session.Format.SampleRate);
                await session.ReplacePreparedAsync(item, actual, cancellation.Token);
                installed = true;
                _activeBytes = fresh;
                _activeStream = fresh.ReadStream as Wavee.Backend.Audio.SpotifyAudioStream;
                original.Cancel();
                _crossfadeInFlight = false;
                _activePrimaryId = session.PrimaryVoiceIdValue;
                target = actual * 1000 / session.Format.SampleRate;
            }
            finally
            {
                if (!installed)
                {
                    fresh.Cancel();
                    if (item is not null) await item.DisposeAsync();
                    fresh.DisposeSource();
                }
            }
            _activeStartMs = 0;
            _activeStartFrame = session.SampleClock - MsToFrames(target, session.Format.SampleRate);
            _activeJoinFrame = GaplessJoinClock.JoinFrameFor(session.SampleClock, _activeDurMs, target, session.Format.SampleRate);
            _clockStale = false;
            _gaplessArmed = 0; _prepRearmSent = 0; _endedHold = 0;
            if (_playIntent)
            {
                session.FadeIn(TimeSpan.FromMilliseconds(5));
                await session.PlayAsync();
            }
            StartTicker();
            return false;   // the real seek ran — Submit's generic revision-based publish reports Applied/Superseded
        }
        catch
        {
            if (operationEpoch == Volatile.Read(ref _loadEpoch) && revision == Volatile.Read(ref _seekRevision))
                _clockStale = false;
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_seekCancellation, cancellation)) _seekCancellation = null;
            }
            if (ownsWorker) _seekWorker.Release();
        }
    }

    void CancelPendingSeek()
    {
        lock (_gate) _seekCancellation?.Cancel();
    }

    // Finding #1: called once OpenSessionAsync installs a session — the natural place for the load a parked seek was
    // waiting on to have finally attached. The seek's target was already baked into that open via `_requestedStartMs`
    // (no need to seek again), so this only has to SETTLE the operation at the position the open actually achieved.
    // A no-op when nothing is parked, or when a newer seek has already superseded the parked one (its own Submit
    // enqueue owns the resolution instead).
    void ResolveParkedSeekAfterOpen()
    {
        if (_parkedSeek is not { } parked) return;
        _parkedSeek = null;
        if (_parkedSeekRevision != Volatile.Read(ref _seekRevision)) return;
        PublishOperationAt(parked, PlaybackOperationStatus.Applied, PositionMs);
    }

    // Finding #2 (no ReopenBody yet): called once SupplyBodyAsync attaches the encrypted body to an ALREADY-open
    // session (the fast path that does not go through OpenSessionAsync again) — unlike ResolveParkedSeekAfterOpen,
    // the position was never baked into anything, so this re-runs the real seek now that ReopenSourceAsync can
    // actually build an independent reopen. Mirrors Submit's own "not handled → generic completion" tail since this
    // runs outside Submit's enqueue wrapper.
    async Task FlushParkedSeekAfterBodyAttachAsync()
    {
        if (_parkedSeek is not { } parked) return;
        long parkedRevision = _parkedSeekRevision;
        _parkedSeek = null;
        if (parkedRevision != Volatile.Read(ref _seekRevision)) return;
        bool handled = await ApplySeekAsync(parked, parkedRevision);
        if (!handled)
        {
            var status = parkedRevision == Volatile.Read(ref _seekRevision) ? PlaybackOperationStatus.Applied : PlaybackOperationStatus.Superseded;
            PublishOperationAt(parked, status, PositionMs);
        }
    }
}
