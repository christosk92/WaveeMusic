namespace FluentGpu.Signals;

// Minimal stand-ins for the two FluentGpu.Signals types VirtualCollection<T> depends on, so the REAL production
// VirtualCollection.cs (source-included from src/FluentGpu.Controls) compiles into this test assembly WITHOUT pulling
// the whole FluentGpu.Engine reference (which would shadow the source-included Backend's System.Threading.Channels /
// DealerRouter and break the build). VirtualCollection only ever creates one Signal<int>, writes it via Value, reads
// Peek(), and exposes it as IReadSignal<int> Version — the reconciliation logic under test never needs real reactivity.

/// <summary>Read-only view of a reactive value (shim — see file header).</summary>
public interface IReadSignal<out T>
{
    T Value { get; }
    T Peek();
}

/// <summary>A trivial value cell (shim — no subscription graph; sufficient for VirtualCollection's Version bump).</summary>
public sealed class Signal<T> : IReadSignal<T>
{
    private T _value;
    public Signal(T initial, System.Collections.Generic.IEqualityComparer<T>? comparer = null) => _value = initial;
    public T Value { get => _value; set => _value = value; }
    public T Peek() => _value;
}
