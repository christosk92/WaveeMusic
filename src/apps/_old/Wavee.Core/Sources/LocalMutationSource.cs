namespace Wavee.Core;

/// <summary>The in-process Mutations source (docs/plans/wavee/architecture.md §4.2): owns the user's saved / liked / followed set as
/// an optimistic in-memory set, persisted through an injected <c>persist</c> sink (the outbox analog — a real source
/// would reconcile with the server + revision conflicts). Saved-state is cross-cutting, so it owns no uri namespace
/// (<see cref="Owns"/> is false); the federation routes to it by the <see cref="SourceCapabilities.Mutations"/> flag.</summary>
public sealed class LocalMutationSource : IMutationSource
{
    readonly HashSet<string> _saved;
    readonly SimpleSubject<IReadOnlySet<string>> _changed = new();
    readonly System.Action<IReadOnlySet<string>>? _persist;

    public LocalMutationSource(IEnumerable<string>? seed = null, System.Action<IReadOnlySet<string>>? persist = null)
    {
        _saved = seed is null ? new HashSet<string>() : new HashSet<string>(seed);
        _persist = persist;
    }

    public string Id => "local-library";
    public bool Owns(string uri) => false;
    public SourceCapabilities Capabilities => SourceCapabilities.Mutations;

    public IReadOnlySet<string> Saved => new HashSet<string>(_saved);   // immutable snapshot
    public bool IsSaved(string uri) => _saved.Contains(uri);
    public IObservable<IReadOnlySet<string>> SavedChanged => _changed;

    public Task SetSavedAsync(string uri, bool saved, CancellationToken ct = default)
    {
        bool changed = saved ? _saved.Add(uri) : _saved.Remove(uri);
        if (changed)
        {
            var snapshot = new HashSet<string>(_saved);
            _persist?.Invoke(snapshot);     // the outbox: flush the new full set to durable storage
            _changed.OnNext(snapshot);      // notify the bridge → engine Signal
        }
        return Task.CompletedTask;
    }
}
