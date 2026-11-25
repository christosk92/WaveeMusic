// ── Theming/Colours/ColourCatalog.cs — the built-in colour sets (#184, plan 3.4) ───────────────────────────────────────────
//
// Static data (`ColourCatalog.Data.cs`), one spec per mode: a family with a light and a dark flavour is two specs. "Theme default" is not
// a spec: it is index 0 of every picker and an empty stored id. A stored id the catalog does not know reads as the theme default.

using System.Collections.Immutable;
using FluentGpu.Dsl;

namespace Wavee.Theming.Colours;

/// <summary>The built-in colour sets.</summary>
public static partial class ColourCatalog
{
    /// <summary>Every built-in set, light ones first, in picker order.</summary>
    public static ImmutableArray<ColourSpec> BuiltIn { get; } = Build();

    private static readonly ImmutableArray<ColourSpec> s_light = Of(ThemeKind.Light);
    private static readonly ImmutableArray<ColourSpec> s_dark = Of(ThemeKind.Dark);

    /// <summary>The sets offered for <paramref name="kind"/>, in picker order (the picker's index 0, "Theme default", is not one).</summary>
    public static ImmutableArray<ColourSpec> For(ThemeKind kind) => kind == ThemeKind.Light ? s_light : s_dark;

    /// <summary>The set with <paramref name="id"/>, or null for an empty or unknown id (the theme default).</summary>
    public static ColourSpec? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var spec in BuiltIn)
            if (string.Equals(spec.Id, id, StringComparison.Ordinal)) return spec;
        return null;
    }

    /// <summary>The set with <paramref name="id"/> when it is offered for <paramref name="kind"/>, else null.</summary>
    public static ColourSpec? Find(string? id, ThemeKind kind) => Find(id) is { } spec && spec.Kind == kind ? spec : null;

    private static ImmutableArray<ColourSpec> Of(ThemeKind kind)
    {
        var b = ImmutableArray.CreateBuilder<ColourSpec>();
        foreach (var spec in BuiltIn)
            if (spec.Kind == kind) b.Add(spec);
        return b.ToImmutable();
    }
}
