// ── Platform/ItemMemo.cs ───────────────────────────────────────────────────────────────────────────────────────────
// One element per item, kept while the item shows the same thing.
//
// A template that re-renders for its tier (a docking panel's slide lands Home in another content tier) rebuilds every row it
// lists. A row whose surface carries ELEMENT slots (a trailing chip cluster, an equalizer subtitle, an icon plate) or a fresh
// click closure never compares equal to the one before (`Children` arrays and closures compare by reference), so every one of
// its hosts re-rendered with nothing changed. Handing the reconciler the SAME element instead skips the row outright.

using System.Runtime.CompilerServices;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>An element built from an item's key once and handed back as the same instance while the key is equal and the theme
/// (<see cref="Tok.Epoch"/>) and culture (<c>Localization.CultureEpoch</c>) are the ones it was built under — a token or string read
/// at build time then never goes stale. The key is everything the element shows (its strings, its art, its handle): what the
/// builder reads that is not in it must not change while the element lives.</summary>
public static class ItemMemo
{
    sealed class Entry<TKey>(TKey key, int theme, int culture, Element element)
    {
        public readonly TKey Key = key;
        public readonly int Theme = theme, Culture = culture;
        public readonly Element Element = element;
    }

    /// <summary>The element for <paramref name="key"/>: the one built last time at this call site (and loop position) when the key and
    /// the epochs match, else a fresh <paramref name="build"/>. A hook cell of <paramref name="hooks"/>, keyed like every hook by the
    /// caller's line and ordinal: call it in a fixed order (a loop appends or drops at the end). Null hooks (a skeleton proxy, which
    /// renders once) builds every time.</summary>
    public static Element Of<TKey>(RenderContext? hooks, TKey key, Func<TKey, Element> build,
                                   [CallerFilePath] string? __hf = null, [CallerLineNumber] int __hl = 0)
        where TKey : IEquatable<TKey>
    {
        if (hooks is null) return build(key);
        var cell = hooks.UseRef<Entry<TKey>?>(null, __hf, __hl);
        int theme = Tok.Epoch, culture = FluentGpu.Localization.Localization.CultureEpoch.Peek();
        if (cell.Value is { } e && e.Theme == theme && e.Culture == culture && e.Key.Equals(key)) return e.Element;
        var element = build(key).Freeze();
        cell.Value = new Entry<TKey>(key, theme, culture, element);
        return element;
    }
}
