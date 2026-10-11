// ── Theming/Options/OptionValues.cs — an immutable snapshot of one layer's option values ───────────────────────────────
//
// A style or structure is a function of its option values. The values are handed to it as an `OptionValues` snapshot so
// the function stays pure and can be memoized on `Stamp`: two snapshots with the same content have the same stamp, so
// `IStyleLayer.Definition(values, osAccent)` returns the same instance for the same (stamp, accent).

using System.Globalization;
using System.Text;
using FluentGpu.Theming;

namespace Wavee.Theming;

/// <summary>Immutable values of one layer's options, keyed by option id.</summary>
public sealed class OptionValues
{
    private static readonly Dictionary<string, int> s_stamps = new(StringComparer.Ordinal);
    private static readonly object s_stampGate = new();

    private readonly Dictionary<string, object> _values;

    private OptionValues(Dictionary<string, object> values)
    {
        _values = values;
        Stamp = StampOf(values);
    }

    /// <summary>The snapshot with no values: every key answers its default.</summary>
    public static OptionValues Empty { get; } = new(new Dictionary<string, object>(0, StringComparer.Ordinal));

    /// <summary>Identifies the CONTENT: equal values (ids and values) give an equal stamp within one process run (the number itself depends on creation order).</summary>
    public int Stamp { get; }

    /// <summary>The number of values held.</summary>
    public int Count => _values.Count;

    /// <summary>The value for <paramref name="key"/>; its default when the snapshot holds none or holds another type.</summary>
    public T Get<T>(OptionKey<T> key) => _values.TryGetValue(key.Id, out var o) && o is T typed ? typed : key.Default;

    /// <summary>True when the snapshot holds a value for <paramref name="id"/>.</summary>
    public bool Has(string id) => _values.ContainsKey(id);

    /// <summary>The value for <paramref name="id"/>, boxed, or null.</summary>
    public object? GetBoxed(string id) => _values.TryGetValue(id, out var o) ? o : null;

    /// <summary>The ids held, unordered.</summary>
    public IEnumerable<string> Ids => _values.Keys;

    /// <summary>A snapshot of <paramref name="values"/> (id to value). A null value is skipped.</summary>
    public static OptionValues Create(IEnumerable<KeyValuePair<string, object?>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var kv in values)
            if (kv.Value is not null) copy[kv.Key] = kv.Value;
        return copy.Count == 0 ? Empty : new OptionValues(copy);
    }

    /// <summary>This snapshot with <paramref name="id"/> set to <paramref name="value"/>.</summary>
    public OptionValues With(string id, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(value);
        var copy = new Dictionary<string, object>(_values, StringComparer.Ordinal) { [id] = value };
        return new OptionValues(copy);
    }

    private static int StampOf(Dictionary<string, object> values)
    {
        if (values.Count == 0) return 0;
        var ids = new List<string>(values.Keys);
        ids.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (string id in ids)
            sb.Append(id).Append('=').Append(Convert.ToString(values[id], CultureInfo.InvariantCulture)).Append(';');
        string signature = sb.ToString();
        lock (s_stampGate)
        {
            if (!s_stamps.TryGetValue(signature, out int stamp))
            {
                stamp = s_stamps.Count + 1;
                s_stamps.Add(signature, stamp);
            }
            return stamp;
        }
    }
}

/// <summary>The three layers' values of one composition: what <c>ThemeOptionStore.Load</c> returns and what a composition
/// resolves its definition, metrics, policies and capabilities from.</summary>
public sealed record ThemeOptionValues(OptionValues Structure, OptionValues Style, OptionValues Composition)
{
    /// <summary>All three layers empty.</summary>
    public static ThemeOptionValues Empty { get; } = new(OptionValues.Empty, OptionValues.Empty, OptionValues.Empty);

    /// <summary>The three stamps as text (<c>2.0.5</c>): identifies the content of the whole set.</summary>
    public string Signature => Structure.Stamp.ToString(CultureInfo.InvariantCulture) + "." + Style.Stamp.ToString(CultureInfo.InvariantCulture)
        + "." + Composition.Stamp.ToString(CultureInfo.InvariantCulture);

    /// <summary>The layer's snapshot.</summary>
    public OptionValues Of(OptionLayer layer) => layer switch
    {
        OptionLayer.Structure => Structure,
        OptionLayer.Style => Style,
        _ => Composition,
    };

    /// <summary>These values with the layer's snapshot replaced.</summary>
    public ThemeOptionValues With(OptionLayer layer, OptionValues values) => layer switch
    {
        OptionLayer.Structure => this with { Structure = values },
        OptionLayer.Style => this with { Style = values },
        _ => this with { Composition = values },
    };
}
