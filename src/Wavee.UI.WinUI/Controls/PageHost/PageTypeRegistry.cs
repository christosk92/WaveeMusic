using System;
using System.Collections.Generic;

namespace Wavee.UI.WinUI.Controls.PageHost;

/// <summary>
/// Stable string-key ↔ Type round-trip for any page type that can appear as a
/// persisted shell-tab entry. We use compile-time string keys (nameof) instead
/// of <see cref="Type.AssemblyQualifiedName"/> because the AOT/trim toolchain
/// does not guarantee type name stability across builds — the trimmer may
/// rename or remove types, and Type.GetType(string) is an IL2057 site.
///
/// Populated alongside <see cref="PageRegistry"/> in
/// <see cref="PageRegistration.RegisterAll"/>. Consumed by
/// <see cref="Wavee.UI.WinUI.Data.Parameters.TabItemParameter"/> to persist
/// + restore the active page across app restarts.
/// </summary>
public static class PageTypeRegistry
{
    private static readonly Dictionary<string, Type> _byKey = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, string> _byType = new();

    public static void Register(string key, Type pageType)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key must be non-empty.", nameof(key));
        ArgumentNullException.ThrowIfNull(pageType);

        _byKey[key] = pageType;
        _byType[pageType] = key;
    }

    public static bool TryGetType(string key, out Type? pageType)
        => _byKey.TryGetValue(key, out pageType!);

    public static bool TryGetKey(Type pageType, out string? key)
        => _byType.TryGetValue(pageType, out key!);
}
