namespace Wavee.UI.Threading;

/// <summary>
/// Minimal UI-thread dispatch abstraction. Lets services in this library
/// marshal callbacks onto the UI thread without taking a hard dependency on
/// Microsoft.UI.Dispatching.DispatcherQueue (which would force them into the
/// WinUI packaging chain and make them untestable from plain class libraries).
///
/// The real WinUI app registers a <c>DispatcherQueueUiDispatcher</c> adapter
/// wrapping the app's UI-thread DispatcherQueue; tests register an
/// <c>ImmediateUiDispatcher</c> that runs callbacks inline.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the calling thread is the UI thread (callbacks can run inline without enqueuing).</summary>
    bool HasThreadAccess { get; }

    /// <summary>
    /// Post <paramref name="callback"/> to run on the UI thread. Returns false if the dispatcher
    /// is shutting down and the callback cannot be enqueued.
    /// </summary>
    bool TryEnqueue(Action callback);
}
