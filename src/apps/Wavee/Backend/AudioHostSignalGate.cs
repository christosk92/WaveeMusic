namespace Wavee.Backend;

/// <summary>Orders transport intent against asynchronous audio-host reports. A queued physical pause can leave the
/// audio core reporting Playing briefly; those reports must not undo the pause shown to the user. The callback and
/// intent edge share one lock, so an already-running report finishes before Pause, or is rejected after it.</summary>
public sealed class AudioHostSignalGate(Action<AudioHostSignal> publish)
{
    // Host signal observers are synchronous and may reenter transport (Monitor is reentrant). PlaybackController's
    // signal handler never waits for its command semaphore; NowPlayingProjection releases its private lock before
    // notifying observers. Neither path calls transport while holding the projection lock, avoiding reverse order.
    readonly object _gate = new();
    bool _playIntent;

    public bool PlayIntent => Volatile.Read(ref _playIntent);

    public void SetPlayIntent(bool playing)
    {
        lock (_gate) Volatile.Write(ref _playIntent, playing);
    }

    /// <summary>Publish the pause edge immediately. The physical session can pause asynchronously and stop its timer
    /// without depending on one last timer callback to discover and report its Paused state.</summary>
    public void Pause(long positionMs)
    {
        lock (_gate)
        {
            Volatile.Write(ref _playIntent, false);
            publish(new AudioHostSignal(AudioHostSignalKind.Paused, positionMs));
        }
    }

    public void Publish(AudioHostSignal signal)
    {
        lock (_gate)
        {
            if (!_playIntent && (signal.IsPlaying || signal.IsBuffering || signal.IsPrebuffering)) return;
            publish(signal);
        }
    }
}
