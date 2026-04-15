using Wavee.UI.Threading;

namespace Wavee.UI.Tests.Helpers;

/// <summary>
/// Test dispatcher that runs callbacks synchronously on the calling thread.
/// Matches the observable behavior assumed by CardPreviewPlaybackCoordinatorTests
/// (which were originally written against a real DispatcherQueue + real time).
///
/// If a future test genuinely needs queued-but-deferred execution, add a
/// QueueingUiDispatcher with an explicit ProcessAll() method alongside this one.
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;

    public bool TryEnqueue(Action callback)
    {
        callback();
        return true;
    }
}
