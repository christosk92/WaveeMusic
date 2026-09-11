using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;

namespace Wavee;

/// <summary>Two-way bridge between the store's "pins" set (the ylpin mirror) and the sidebar's pin list
/// (<c>docs/plans/wavee/pin-spotify-sync-implementation.md</c> §1.5). Folders ARE syncable (<c>PinSyncRules</c> maps
/// <c>folder:&lt;hex&gt;</c> ↔ <c>spotify:folder:&lt;hex&gt;</c>), so <see cref="MigrateLocalPins"/> pushes a local
/// folder pin absent from the server set on the first converged walk exactly like it does a playlist — intended, not
/// a gap.
///
/// <para>UI-thread affine on the <see cref="SidebarPinStore"/> side (every mutation goes through the dispatcher handed
/// to <see cref="Activate"/>); store subscriptions fire on the sync loop / drain thread. Engine-free apart from the
/// store/mutation seams, so <c>SidebarPinSyncTests</c> drives it with an <c>InMemoryStore</c> and a recording
/// <see cref="IPinMutations"/> fake.</para>
///
/// <para><b>Deviation from the plan's §1.5 design</b>: this ctor takes the <see cref="SidebarPinStore"/> directly
/// rather than the full <c>SidebarPreferences</c> — <c>SidebarPreferences</c> is engine-bound (FluentGpu.Hooks/Signals
/// + the Curated/undo/document graph) and is NOT source-included into Wavee.Tests, so depending on it directly would
/// have made <c>SidebarPinSyncTests</c> impossible without pulling in the engine. <see cref="SidebarPinStore"/> already
/// is source-included and already carries everything this bridge needs (<c>ApplyRemote</c>, <c>OnLocalPinChanged</c>).
/// This also gains its own <see cref="Activate"/> method (the same pattern <c>SidebarPreferences.Activate</c> uses)
/// instead of taking the UI dispatcher as a ctor arg, so a bridge constructed before the app's mount effect has run
/// queues its remote-apply work rather than dropping it.</para></summary>
public sealed class SidebarPinSync : IDisposable
{
    readonly IStore _store;
    readonly SidebarPinStore _pins;
    readonly IPinMutations _mutations;
    readonly IAppSettings _settings;
    readonly Func<string> _username;
    readonly Func<bool> _converged;          // () => cold.GetCollectionRevision("ylpin") is not null
    readonly Func<string, bool> _hasPending; // (uri) => mutEngine.HasPending("pins", uri)
    readonly Func<string, string>? _routeTitle; // (routeKey) => its display title, e.g. ShellNav.Dest(key).Title
    readonly IDisposable _sub;
    readonly object _queueGate = new();
    readonly List<Action> _queuedBeforeActivate = new();
    Action<Action>? _post;
    bool _migrated;

    public SidebarPinSync(IStore store, SidebarPinStore pins, IPinMutations mutations, IAppSettings settings,
        Func<string> username, Func<bool> converged, Func<string, bool> hasPending,
        Func<string, string>? routeTitle = null)
    {
        _store = store;
        _pins = pins;
        _mutations = mutations;
        _settings = settings;
        _username = username;
        _converged = converged;
        _hasPending = hasPending;
        _routeTitle = routeTitle;
        _migrated = settings.Get(SidebarKeys.PinsMigratedToServer);
        _pins.OnLocalPinChanged = OnLocalPinChanged;
        _sub = store.Changes.Subscribe(Observers.From<StoreChange>(OnStoreChange));
    }

    /// <summary>Attach the app's UI-thread dispatcher (the <c>SidebarPreferences.Activate</c> precedent). Idempotent;
    /// flushes any remote-apply work queued before this ran (a store change that fired before the app's mount effect —
    /// very early boot — is queued, never dropped).</summary>
    public void Activate(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        List<Action> queued;
        lock (_queueGate)
        {
            _post = post;
            queued = new List<Action>(_queuedBeforeActivate);
            _queuedBeforeActivate.Clear();
        }
        foreach (var a in queued) post(a);
    }

    void Post(Action a)
    {
        Action<Action>? post;
        lock (_queueGate)
        {
            post = _post;
            if (post is null) { _queuedBeforeActivate.Add(a); return; }
        }
        post(a);
    }

    // remote → local. Bulk (a page walk finished) or a pins-kind bump both re-read the whole mirror: it is at most a
    // few dozen rows and the apply is idempotent, so a diff would only add a second source of truth.
    void OnStoreChange(StoreChange c)
    {
        if (!c.IsBulk && c.Kind != CollectionKind.Pins) return;
        var items = _store.SavedItems("pins");
        var server = new List<SidebarPin>(items.Count);
        foreach (var it in items.OrderBy(i => i.AddedAtMs))          // oldest first → appended in pin order
        {
            if (PinSyncRules.TryPinId(it.Uri) is not { } id) continue;
            var kind = SidebarPinId.KindOf(id);
            // A route pin (e.g. Liked Songs, id "liked") has no library entity behind it, so nothing else ever fills
            // in its display name — without this it renders with an empty Name, which SidebarPaneSlot.EntryRow then
            // dims and disables (Task B). Every other kind keeps "" and waits for the projection's Touch to fill it.
            string name = kind == SidebarEntryKind.AppRoute ? _routeTitle?.Invoke(id) ?? "" : "";
            server.Add(new SidebarPin(id, kind, SidebarPinId.UriOf(id), name, it.AddedAtMs));
        }
        bool removeMissing = _converged() && _migrated;
        string user = _username();
        Post(() =>
        {
            _pins.ApplyRemote(server,
                isSyncable: id => PinSyncRules.IsSyncable(id, user) && !_hasPending(PinSyncRules.TryWireUri(id, user)!),
                removeMissing);
            if (!_migrated && _converged()) MigrateLocalPins(server, user);
        });
    }

    // local → remote. Only syncable kinds; everything else is a silent local pin as before.
    void OnLocalPinChanged(SidebarPin pin, bool pinned)
    {
        if (PinSyncRules.TryWireUri(pin.Id, _username()) is not { } uri) return;
        _ = _mutations.SetPinnedAsync(uri, pinned, CancellationToken.None);   // outbox: durable, replays after a restart/offline
    }

    // §1.7 — the upgrade path. The user's JSON may hold pins the server does not; deleting them would be data loss, so
    // the FIRST converged walk migrates instead of sweeping: push every syncable local pin absent from the server set,
    // then latch the flag so future convergences may remove instead. Runs on the UI thread (called from inside Post).
    void MigrateLocalPins(IReadOnlyList<SidebarPin> server, string user)
    {
        var onServer = new HashSet<string>(server.Count, StringComparer.Ordinal);
        for (int i = 0; i < server.Count; i++) onServer.Add(server[i].Id);
        for (int i = 0; i < _pins.Count; i++)
        {
            var pin = _pins[i];
            if (onServer.Contains(pin.Id)) continue;
            if (PinSyncRules.TryWireUri(pin.Id, user) is not { } uri) continue;   // not syncable — stays local-only
            _ = _mutations.SetPinnedAsync(uri, true, CancellationToken.None);
        }
        _migrated = true;
        _settings.Set(SidebarKeys.PinsMigratedToServer, true);
    }

    public void Dispose()
    {
        _sub.Dispose();
        _pins.OnLocalPinChanged = null;
    }
}
