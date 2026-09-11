using System.Collections.Frozen;

namespace Wavee.Core;

/// <summary>The local saved / liked / followed owner. Serialized writes persist a candidate through the injected
/// sink before publishing an immutable snapshot. Saved-state is cross-cutting, so it owns no uri namespace
/// (<see cref="Owns"/> is false); the federation routes to it by the <see cref="SourceCapabilities.Mutations"/> flag.</summary>
public sealed class LocalMutationSource : IMutationSource
{
    FrozenSet<string> _saved;
    readonly SemaphoreSlim _writer = new(1, 1);
    readonly SimpleSubject<IReadOnlySet<string>> _changed = new();
    readonly System.Action<IReadOnlySet<string>>? _persist;

    public LocalMutationSource(IEnumerable<string>? seed = null, System.Action<IReadOnlySet<string>>? persist = null)
    {
        _saved = (seed ?? []).ToFrozenSet(StringComparer.Ordinal);
        _persist = persist;
    }

    public string Id => "local-library";
    public bool Owns(string uri) => false;
    public SourceCapabilities Capabilities => SourceCapabilities.Mutations;

    public IReadOnlySet<string> Saved => Volatile.Read(ref _saved);
    public bool IsSaved(string uri) => Volatile.Read(ref _saved).Contains(uri);
    public IObservable<IReadOnlySet<string>> SavedChanged => _changed;

    public async Task SetSavedAsync(string uri, bool saved, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = new HashSet<string>(_saved, StringComparer.Ordinal);
            if (!(saved ? next.Add(uri) : next.Remove(uri))) return;
            var snapshot = next.ToFrozenSet(StringComparer.Ordinal);
            if (_persist is not null)
                await Task.Run(() => _persist(snapshot), ct).ConfigureAwait(false);
            Volatile.Write(ref _saved, snapshot);
            _changed.OnNext(snapshot);
        }
        finally { _writer.Release(); }
    }
}
