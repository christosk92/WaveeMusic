using Wavee.Core;

namespace Wavee;

/// <summary>
/// UI ownership of a requested seek target. Only the matching operation releases it; an old position tick or a
/// later, unrelated transport command cannot pretend that a seek completed. All methods run on the UI thread.
/// </summary>
internal sealed class PlaybackSeekLatch
{
    private long _revision;
    private PlaybackCommandId? _command;
    private PlaybackSeekState? _observed;
    private long _observedGeneration;

    public long? TargetMs { get; private set; }

    public long Begin(long targetMs)
    {
        _command = null;
        TargetMs = targetMs;
        return ++_revision;
    }

    public void Accept(long revision, PlaybackCommandReceipt receipt)
    {
        if (revision != _revision || TargetMs is null) return;
        _command = receipt.Id;
        Observe(_observed, _observedGeneration);
    }

    public void Observe(PlaybackSeekState? state, long itemGeneration = 0)
    {
        _observed = state;
        _observedGeneration = itemGeneration;
        if (_command is { } accepted && itemGeneration > accepted.ItemGeneration)
        {
            Clear();
            return;
        }
        if (state is not { } seek || _command is not { } command || seek.Id != command) return;
        if (seek.Status is PlaybackOperationStatus.Applied or PlaybackOperationStatus.Superseded
            or PlaybackOperationStatus.Failed)
            Clear();
    }

    public void Reject(long revision)
    {
        if (revision == _revision) Clear();
    }

    public void Clear()
    {
        ++_revision;
        _command = null;
        _observed = null;
        TargetMs = null;
    }
}
