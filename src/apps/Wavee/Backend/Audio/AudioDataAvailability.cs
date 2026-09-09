using System.Threading;

namespace Wavee.Backend.Audio;

/// <summary>Lost-wakeup-free availability notification. No resettable-event polling and no timeout-as-EOF.</summary>
internal sealed class AudioDataAvailability
{
    readonly object _gate = new();
    long _version;
    public long Version => Interlocked.Read(ref _version);

    public void Pulse()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _version);
            Monitor.PulseAll(_gate);
        }
    }

    public void Wait(long observedVersion, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.UnsafeRegister(static state =>
        {
            var gate = (object)state!;
            lock (gate) Monitor.PulseAll(gate);
        }, _gate);
        lock (_gate)
        {
            while (Version == observedVersion)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_gate);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
