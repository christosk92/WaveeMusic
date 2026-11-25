// ── Theming/Composition/DefinitionMemo.cs — a style's definitions, the last few input sets kept ─────────────────────────────
//
// A style returns the SAME instance for the same inputs (the composition's identity rule: an unchanged pick installs exactly the
// instance already in the engine, and `Tok.Use` adopts it by reference). Its inputs grew from the option values and the OS accent to the
// user's colour sets (#184), where every applied custom palette is a new key, so the memo is bounded: the least recently used entry
// goes once it holds <see cref="Capacity"/>. Thread-safe (a style's statics are read from more than one thread at boot).

using FluentGpu.Theming;

namespace Wavee.Theming;

/// <summary>A bounded, least-recently-used memo of one style's definitions (see the file header).</summary>
public sealed class DefinitionMemo<TKey> where TKey : notnull
{
    /// <summary>How many input sets a style keeps.</summary>
    public const int Capacity = 8;

    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, ThemeDefinition Definition)>> _map = new();
    private readonly LinkedList<(TKey Key, ThemeDefinition Definition)> _order = new();

    /// <summary>The definition for <paramref name="key"/>: the kept instance, or <paramref name="build"/>'s, kept from now on.</summary>
    public ThemeDefinition Get(TKey key, Func<TKey, ThemeDefinition> build)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var hit))
            {
                _order.Remove(hit);
                _order.AddFirst(hit);
                return hit.Value.Definition;
            }
            var node = _order.AddFirst((key, build(key)));
            _map.Add(key, node);
            if (_map.Count > Capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
            return node.Value.Definition;
        }
    }
}
