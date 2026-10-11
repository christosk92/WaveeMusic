// ── Theming/Options/ThemeState.cs — small persisted UI state a theme keeps for itself ───────────────────────────────────
//
// Not an option (the user does not choose it in Settings): a panel width, a collapsed section, the last tab. Stored under
// `theme.<themeId>.state.<key>` for the ACTIVE theme, clamped to the key's range on both read and write, so a value from
// an older build or a hand-edited store can never put a theme out of its bounds. Switching themes switches the scope; the
// values of the other themes stay where they are.

namespace Wavee.Theming;

/// <summary>One state value of a theme: its id, default and the range it is clamped to (ignored for strings).</summary>
public readonly record struct StateKey<T>(string Id, T Default, T Min, T Max);

/// <summary>Reads and writes <see cref="StateKey{T}"/> values of the active theme.</summary>
public static class ThemeState
{
    private static string s_scope = ThemeCatalog.FallbackId;

    /// <summary>The setting name of a state key of <paramref name="themeId"/>.</summary>
    public static string SettingName(string themeId, string keyId) => "theme." + themeId + ".state." + keyId;

    /// <summary>Points the state at <paramref name="themeId"/>. Called by <c>Themes</c> when the active theme changes.</summary>
    internal static void Bind(string themeId) => s_scope = themeId;

    /// <summary>The state value of the active theme (the default when nothing valid is stored).</summary>
    public static T Get<T>(StateKey<T> key) => Get(key, Platform.Settings, s_scope);

    /// <summary>Stores the state value of the active theme, clamped.</summary>
    public static void Set<T>(StateKey<T> key, T value) => Set(key, value, Platform.Settings, s_scope);

    /// <summary>As <see cref="Get{T}(StateKey{T})"/> over <paramref name="settings"/> for <paramref name="themeId"/>.</summary>
    public static T Get<T>(StateKey<T> key, IAppSettings settings, string themeId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        T stored = settings.Get(new SettingKey<T>(SettingName(themeId, key.Id), key.Default));
        return Clamp(key, stored);
    }

    /// <summary>As <see cref="Set{T}(StateKey{T}, T)"/> over <paramref name="settings"/> for <paramref name="themeId"/>.</summary>
    public static void Set<T>(StateKey<T> key, T value, IAppSettings settings, string themeId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Set(new SettingKey<T>(SettingName(themeId, key.Id), key.Default), Clamp(key, value));
    }

    private static T Clamp<T>(StateKey<T> key, T value)
    {
        if (typeof(T) == typeof(string) || typeof(T) == typeof(bool)) return value;
        if (value is float f && float.IsNaN(f)) return key.Default;
        var comparer = Comparer<T>.Default;
        if (comparer.Compare(value, key.Min) < 0) return key.Min;
        if (comparer.Compare(value, key.Max) > 0) return key.Max;
        return value;
    }
}
