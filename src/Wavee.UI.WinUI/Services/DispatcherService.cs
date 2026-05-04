using System;
using Microsoft.UI.Dispatching;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services;

public sealed class DispatcherService : IDispatcherService
{
    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public bool TryEnqueue(Action action)
        => _dispatcherQueue.TryEnqueue(() => action());

    public bool TryEnqueue(DispatcherQueuePriority priority, Action action)
        => _dispatcherQueue.TryEnqueue(priority, () => action());
}
