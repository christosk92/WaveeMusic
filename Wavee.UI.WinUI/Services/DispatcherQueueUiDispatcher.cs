using System;
using Microsoft.UI.Dispatching;
using Wavee.UI.Threading;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Adapter that wraps the WinUI <see cref="DispatcherQueue"/> as an <see cref="IUiDispatcher"/>
/// so plain-C# services in <c>Wavee.UI</c> can marshal callbacks onto the UI thread without
/// taking a direct dependency on Microsoft.UI.Dispatching.
/// </summary>
public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherQueueUiDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public bool HasThreadAccess => _dispatcherQueue.HasThreadAccess;

    public bool TryEnqueue(Action callback) => _dispatcherQueue.TryEnqueue(() => callback());
}
