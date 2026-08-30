using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The Core→engine bridge for the friends-feed facet (see <see cref="LibraryBridge"/> for the pattern) — the presence
/// snapshot as reactive <see cref="Signal{T}"/>s. Subscribes the service's <see cref="IFriendActivityService.Changed"/>
/// stream, marshalling each callback onto the UI thread via the post delegate, and copies Snapshot/State/LastError into
/// the signals so the <c>FriendsPanel</c> re-renders on any push. A go-live / go-offline swap re-emits through the
/// <see cref="SwitchableFriendActivityService"/>. Provided once at the app root via <see cref="Slot"/>.
/// </summary>
public sealed class FriendsBridge
{
    public static readonly Context<FriendsBridge?> Slot = new(null);

    readonly IFriendActivityService _svc;
    readonly List<IDisposable> _subs = [];
    bool _active;

    /// <summary>The current feed rows, sorted most-recent first — the panel keys its rows on these.</summary>
    public Signal<IReadOnlyList<FriendActivity>> Items { get; }
    /// <summary>The feed's coarse state — drives which surface (skeleton / rows / empty / offline / error) the panel shows.</summary>
    public Signal<FriendFeedState> State { get; }
    /// <summary>The last error message when <see cref="State"/> is <see cref="FriendFeedState.Error"/>, else null.</summary>
    public Signal<string?> Error { get; }
    /// <summary>Bumped by the panel's 30-second tick while visible so relative times / live state recompute per render.</summary>
    public Signal<long> NowTick { get; } = new(0);

    public FriendsBridge(IFriendActivityService svc)
    {
        _svc = svc;
        Items = new Signal<IReadOnlyList<FriendActivity>>(svc.Snapshot);
        State = new Signal<FriendFeedState>(svc.State);
        Error = new Signal<string?>(svc.LastError);
    }

    /// <summary>Subscribe the service's change stream → the signals. Idempotent. Call once from a mount effect with <c>UsePost()</c>.</summary>
    public void Activate(Action<Action> post)
    {
        if (_active) return;
        _active = true;
        _subs.Add(_svc.Changed.Subscribe(_ => post(Pull)));
        Pull();   // seed the signals from the current snapshot (and pick up a swap that happened before Activate)
    }

    // Copy the service's authoritative snapshot into the signals (on the UI thread, via post).
    void Pull()
    {
        Items.Value = _svc.Snapshot;
        State.Value = _svc.State;
        Error.Value = _svc.LastError;
    }

    /// <summary>Mark the feed panel visible/hidden — the service starts/stops its watchdog + lazy-seeds on first show.</summary>
    public void SetActive(bool active) => _svc.SetActive(active);

    /// <summary>Force a full re-seed (the Retry pill). Fire-and-forget; the service never throws into callers.</summary>
    public void Refresh() => _ = _svc.RefreshAsync(CancellationToken.None);
}
