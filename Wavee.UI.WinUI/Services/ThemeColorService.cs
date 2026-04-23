using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Provides fast, cached, theme-aware brush access.
/// Brushes are resolved from a FrameworkElement's resource chain (which IS theme-aware,
/// unlike Application.Current.Resources which acts like StaticResource).
/// Refreshes automatically on theme change.
/// </summary>
public sealed class ThemeColorService
{
    private FrameworkElement? _root;

    // Pre-resolved cached brushes -- direct property access, no dictionary lookups
    public Brush TextPrimary { get; private set; } = new SolidColorBrush(Colors.Black);
    public Brush TextSecondary { get; private set; } = new SolidColorBrush(Colors.Gray);
    public Brush TextTertiary { get; private set; } = new SolidColorBrush(Colors.DarkGray);
    public Brush AccentText { get; private set; } = new SolidColorBrush(Colors.Blue);
    public Brush AccentFill { get; private set; } = new SolidColorBrush(Colors.Blue);
    public Brush AccentFillTertiary { get; private set; } = new SolidColorBrush(Colors.Blue);
    /// <summary>App-defined accent brush (e.g. Spotify green #1DB954), not the Fluent system accent.</summary>
    public Brush AppAccent { get; private set; } = new SolidColorBrush(Colors.Blue);
    public Brush CardBackground { get; private set; } = new SolidColorBrush(Colors.Transparent);
    public Brush CardBackgroundSecondary { get; private set; } = new SolidColorBrush(Colors.Transparent);
    public Brush SubtleFillTransparent { get; private set; } = new SolidColorBrush(Colors.Transparent);
    public Brush TransparentBrush { get; } = new SolidColorBrush(Colors.Transparent);

    /// <summary>
    /// Initialize with a theme-aware root element. Call once after the root Frame is created.
    /// </summary>
    public void Initialize(FrameworkElement root)
    {
        if (root is null) return;
        _root = root;
        RefreshBrushes();
        root.ActualThemeChanged += OnThemeChanged;
    }

    /// <summary>
    /// Raised after brushes are refreshed due to a theme change.
    /// Listeners should re-apply any cached brush values to their UI elements.
    /// </summary>
    public event Action? ThemeChanged;

    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        RefreshBrushes();
        ThemeChanged?.Invoke();
    }

    // Cached theme dictionaries -- rebuilt once per theme change, used for all lookups
    private readonly List<ResourceDictionary> _resolvedThemeDicts = [];

    private void RefreshBrushes()
    {
        if (_root == null) return;

        // Rebuild the cached dictionary list (once per theme change, not per-brush)
        RebuildThemeDictionaryCache();

        TextPrimary = Resolve("TextFillColorPrimaryBrush");
        TextSecondary = Resolve("TextFillColorSecondaryBrush");
        TextTertiary = Resolve("TextFillColorTertiaryBrush");
        AccentText = Resolve("AccentTextFillColorPrimaryBrush");
        AccentFill = Resolve("AccentFillColorDefaultBrush");
        AccentFillTertiary = Resolve("AccentFillColorTertiaryBrush");
        AppAccent = Resolve("App.Theme.AccentBrush");
        CardBackground = Resolve("CardBackgroundFillColorDefaultBrush");
        CardBackgroundSecondary = Resolve("CardBackgroundFillColorSecondaryBrush");
        SubtleFillTransparent = Resolve("SubtleFillColorTransparentBrush");
    }

    private void RebuildThemeDictionaryCache()
    {
        _resolvedThemeDicts.Clear();

        var themeKey = _root!.ActualTheme switch
        {
            ElementTheme.Light => "Light",
            ElementTheme.Dark => "Dark",
            _ => "Default"
        };

        // Collect all theme-specific dictionaries (searched in order for Resolve)
        var appRes = Application.Current.Resources;
        AddThemeDict(appRes, themeKey);
        if (themeKey != "Default") AddThemeDict(appRes, "Default");

        foreach (var merged in appRes.MergedDictionaries)
        {
            AddThemeDict(merged, themeKey);
            if (themeKey != "Default") AddThemeDict(merged, "Default");
            foreach (var nested in merged.MergedDictionaries)
            {
                AddThemeDict(nested, themeKey);
            }
        }
    }

    private void AddThemeDict(ResourceDictionary source, string themeKey)
    {
        if (source.ThemeDictionaries.TryGetValue(themeKey, out var dict) && dict is ResourceDictionary rd)
            _resolvedThemeDicts.Add(rd);
    }

    private Brush Resolve(string key)
    {
        // Fast path: search cached theme dictionaries (pre-built, no repeated theme key lookups)
        foreach (var dict in _resolvedThemeDicts)
        {
            if (dict.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }

        // Fallback: flat (non-themed) Application resources
        if (Application.Current.Resources.TryGetValue(key, out var flat) && flat is Brush flatBrush)
            return flatBrush;

        return new SolidColorBrush(Colors.Magenta);
    }

    private static bool TryResolveFromThemeDictionaries(
        ResourceDictionary resources, string themeKey, string key, out Brush brush)
    {
        brush = null!;
        if (resources.ThemeDictionaries.TryGetValue(themeKey, out var themeDict)
            && themeDict is ResourceDictionary dict
            && dict.TryGetValue(key, out var value)
            && value is Brush b)
        {
            brush = b;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resolve any brush by key. For uncommon brushes not exposed as properties.
    /// </summary>
    public Brush GetBrush(string resourceKey) => Resolve(resourceKey);
}
