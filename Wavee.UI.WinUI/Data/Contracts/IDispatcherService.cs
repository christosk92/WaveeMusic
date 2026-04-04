using System;

namespace Wavee.UI.WinUI.Data.Contracts;

public interface IDispatcherService
{
    bool TryEnqueue(Action action);
    bool TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority priority, Action action);
}
