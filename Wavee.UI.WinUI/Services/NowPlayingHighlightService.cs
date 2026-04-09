using System;
using CommunityToolkit.Mvvm.Messaging;
using Wavee.UI.WinUI.Data.Messages;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Single observer for the "now playing" highlight state. Replaces the previous
/// pattern where every <c>ContentCard</c> on a page (310+ on HomePage) individually
/// registered with <see cref="WeakReferenceMessenger"/> for
/// <see cref="NowPlayingChangedMessage"/>.
///
/// <para>
/// The per-card registration had two costs:
/// </para>
/// <list type="number">
///   <item>Each <c>OnLoaded</c> call paid messenger bookkeeping overhead — weak ref
///     allocation + hash insertion + type-key lookup. Across 310 cards that's a
///     measurable slice of page realization time.</item>
///   <item>Every state change fanned out through the messenger's recipient enumeration,
///     which is slower than a direct C# event invocation list.</item>
/// </list>
///
/// <para>
/// This service subscribes ONCE to the messenger at construction time, caches the
/// current state, and exposes a plain C# event that cards subscribe to directly.
/// Cards unsubscribe in <c>OnUnloaded</c> to prevent leaks — safe because we rely
/// on the strong-reference event semantics and the caller's lifecycle.
/// </para>
///
/// <para>
/// Registered as a singleton in <c>AppLifecycleHelper.ConfigureHost</c>.
/// </para>
/// </summary>
public sealed class NowPlayingHighlightService
{
    private (string? ContextUri, bool IsPlaying) _current;
    private readonly object _lock = new();

    /// <summary>
    /// Snapshot of the current highlight state. Safe to read from any thread.
    /// </summary>
    public (string? ContextUri, bool IsPlaying) Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>
    /// Fires when the highlight state changes. Handlers receive the new
    /// (contextUri, isPlaying) tuple. Subscribers MUST unsubscribe when they
    /// leave the visual tree — this is a strong event.
    /// </summary>
    public event Action<string?, bool>? CurrentChanged;

    public NowPlayingHighlightService(IMessenger messenger)
    {
        // Subscribe once at construction. The messenger holds a weak ref to us,
        // but because we're a singleton we live forever, so effectively this is
        // a one-time registration.
        messenger.Register<NowPlayingHighlightService, NowPlayingChangedMessage>(
            this,
            static (recipient, msg) => recipient.OnMessengerUpdate(msg));
    }

    private void OnMessengerUpdate(NowPlayingChangedMessage msg)
    {
        (string? ContextUri, bool IsPlaying) snapshot;
        lock (_lock)
        {
            _current = msg.Value;
            snapshot = _current;
        }
        CurrentChanged?.Invoke(snapshot.ContextUri, snapshot.IsPlaying);
    }
}
