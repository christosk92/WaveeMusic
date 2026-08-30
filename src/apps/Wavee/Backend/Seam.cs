using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── §7 — the thin SEAM ADAPTERS (engines → Wavee.Core facets) ────────────────────────────────────────────────────────
// The plan's "facet adapters present the engines as the seam." These wire the store + mutation/session engines behind the
// existing Wavee.Core interfaces, so the UI/aggregate consume them unchanged. (Catalog/Remote adapters follow the same
// shape over the Resource engine + Transport; Mutation + Session are the two wired here.)

/// <summary>IMutationSource over the store + the Mutation engine: SetSavedAsync = optimistic Save + drain; the Saved set
/// mirrors the store and emits on every change (the bridge mirrors it into an engine Signal).</summary>
public sealed class EngineMutationSource : IMutationSource, IDisposable
{
    readonly IStore _store;
    readonly MutationEngine _mut;
    readonly ITransport _transport;
    readonly Func<SessionContext> _ctx;
    readonly string _setId;
    readonly SimpleSubject<IReadOnlySet<string>> _savedChanged;
    readonly IDisposable _sub;
    readonly object _savedGate = new();
    HashSet<string> _saved;   // immutable snapshot, swapped under _savedGate; readers (IsSaved/Saved) read the reference lock-free

    public EngineMutationSource(IStore store, MutationEngine mut, ITransport transport, Func<SessionContext> ctx, string setId = "liked")
    {
        _store = store; _mut = mut; _transport = transport; _ctx = ctx; _setId = setId;
        _saved = BuildUnion(store);
        _savedChanged = new SimpleSubject<IReadOnlySet<string>>(_saved);
        _sub = store.Changes.Subscribe(Observers.From<StoreChange>(OnStoreChange));
    }

    public string Id => "spotify";
    public bool Owns(string uri) => EntityUri.Parse(uri).Provider == EntityProviders.Spotify;
    public SourceCapabilities Capabilities => SourceCapabilities.Mutations;

    public IReadOnlySet<string> Saved => _saved;
    public bool IsSaved(string uri) => _saved.Contains(uri);
    public IObservable<IReadOnlySet<string>> SavedChanged => _savedChanged;

    /// <summary>Set at go-live (§6 hardening): routes the post-write drain through the LibrarySync loop so replay/
    /// reconcile serializes with inbound diffs instead of racing them from the caller's thread. Null (offline / tests /
    /// fake backend) keeps the inline drain. GoOffline resets it to null.</summary>
    public Action? ScheduleDrain { get; set; }

    public async Task SetSavedAsync(string uri, bool saved, CancellationToken ct = default)
    {
        // store.SetSaved → Bump → OnStoreChange updates _saved + emits, synchronously — no explicit recompute needed.
        // §2.5 — a playlist follow/unfollow is a rootlist ADD/REM (routed to Follow), NOT a collection set write; every other
        // uri kind is a collection save with the set inferred from the uri kind.
        if (EntityUri.KindOf(uri) == EntityKind.Playlist) _mut.Follow(uri, saved);
        else _mut.Save(SetForUri(uri), uri, saved);                        // optimistic + outbox; set inferred from the uri kind
        if (ScheduleDrain is { } viaLoop) { viaLoop(); return; }           // §6 — the loop drains, serialized with inbound
        await _mut.Drain(_transport, _ctx(), ct).ConfigureAwait(false);    // replay + reconcile (stub transport = succeeds)
    }

    // Incremental: a single change costs an O(1) IsSaved lookup, not an O(saved-set) rebuild + SetEquals on EVERY store
    // change. A bulk load (one StoreChange.Bulk) does a full re-read. The RMW on _saved is guarded; the emit is outside the lock.
    void OnStoreChange(StoreChange c)
    {
        IReadOnlySet<string>? toEmit = null;
        lock (_savedGate)
        {
            if (c.IsBulk)
            {
                var full = BuildUnion(_store);
                if (!full.SetEquals(_saved)) { _saved = full; toEmit = full; }
            }
            else
            {
                bool now = _store.IsSaved(SetForUri(c.Uri), c.Uri);
                bool was = _saved.Contains(c.Uri);
                if (now != was)
                {
                    var next = new HashSet<string>(_saved, StringComparer.Ordinal);
                    if (now) next.Add(c.Uri); else next.Remove(c.Uri);
                    _saved = next;
                    toEmit = next;
                }
            }
        }
        if (toEmit is not null) _savedChanged.OnNext(toEmit);
    }

    // "prerelease" is here so BuildUnion restores PRE-SAVE hearts from the persisted collection_items rows at boot.
    // It is deliberately absent from the inbound sync's set list (LibrarySync.Sets): the wire `set` string is inferred,
    // and fetching a set the server does not know would let the mark-and-sweep unsave every local pre-save.
    static readonly string[] AllSets = { "liked", "albums", "artists", "shows", "episodes", "playlists", "prerelease" };   // §2.8 — followed playlists fold into Saved

    // The save set is inferred from the uri kind (track→liked, album→albums, artist→artists, show→shows, episode→episodes),
    // so ONE source covers every library type while Saved/IsSaved stay a single aggregated snapshot. Non-standard uris fall
    // back to the configured set.
    string SetForUri(string uri) => EntityUri.KindOf(uri) switch
    {
        EntityKind.Track => "liked",
        EntityKind.Album => "albums",
        EntityKind.Artist => "artists",
        EntityKind.Show => "shows",
        EntityKind.Episode => "episodes",
        EntityKind.Playlist => "playlists",       // §2.8 — a single-uri follow change updates Saved incrementally
        EntityKind.Prerelease => "prerelease",    // a pre-save (kind-138 entity); rides the "collection" wire set
        _ => _setId,
    };

    HashSet<string> BuildUnion(IStore store)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in AllSets) foreach (var u in store.SavedUris(s)) set.Add(u);
        foreach (var u in store.SavedUris(_setId)) set.Add(u);   // include the fallback set
        return set;
    }

    public void Dispose() => _sub.Dispose();
}

/// <summary>ISpotifySession over ⑤ SessionContext. ConnectAsync is STUBBED (real connect = the AP/Shannon handshake via
/// the Transport); status/user are projected from the ambient session.</summary>
public sealed class EngineSessionSource : ISpotifySession
{
    readonly SessionContextHost _session;
    readonly SimpleSubject<AuthStatus> _status = new(AuthStatus.LoggedOut);
    AuthStatus _cur = AuthStatus.LoggedOut;

    public EngineSessionSource(SessionContextHost session) => _session = session;

    public AuthStatus Status => _cur;
    public WaveeUser? CurrentUser { get; private set; }
    public IObservable<AuthStatus> StatusChanged => _status;

    public Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        var ctx = _session.Current;
        // Premium-only gate: refuse a Free account outright (Wavee requires Premium for now).
        if (!SessionGate.IsAllowed(ctx.Tier))
        {
            _cur = AuthStatus.Error;
            CurrentUser = null;
            _status.OnNext(_cur);
            return Task.FromResult(false);
        }
        _cur = AuthStatus.Authenticated;   // STUB: real = AP connect + DH/Shannon handshake + APWelcome (Transport ③)
        CurrentUser = new WaveeUser(ctx.Account, "Me", null, true);
        _status.OnNext(_cur);
        return Task.FromResult(true);
    }

    public Task LogoutAsync(CancellationToken ct = default)
    {
        _cur = AuthStatus.LoggedOut;
        CurrentUser = null;
        _status.OnNext(_cur);
        return Task.CompletedTask;
    }
}

static class Observers
{
    public static IObserver<T> From<T>(Action<T> onNext) => new Inline<T>(onNext);

    sealed class Inline<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}
