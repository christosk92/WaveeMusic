using System;
using Microsoft.CodeAnalysis;
using Wavee.Analyzers.Baselines;

namespace Wavee.Analyzers.Themes;

/// <summary>Where a source file sits in the theming layout, which decides what it may reference.</summary>
public enum ScopeKind
{
    /// <summary>Everything outside <c>Themes\</c>: may not reference any <c>Wavee.Themes.*</c> type.</summary>
    Core,
    /// <summary><c>App.cs</c> (the composition root) and <c>Theming\Composition\ThemeCatalog.cs</c>: may reference anything.</summary>
    Unrestricted,
    /// <summary><c>Themes\Common\**</c>: Common only.</summary>
    Common,
    /// <summary><c>Themes\&lt;X&gt;\&lt;X&gt;Theme.cs</c>: any layer, including Common.</summary>
    Composition,
    /// <summary><c>Themes\&lt;X&gt;\Structure\**</c>: its own theme and Common.</summary>
    Structure,
    /// <summary>Any other file of theme X: its own theme only.</summary>
    ThemeOther,
}

public readonly struct FileScope
{
    public FileScope(ScopeKind kind, string? theme)
    {
        Kind = kind;
        Theme = theme;
    }

    public ScopeKind Kind { get; }

    /// <summary>The theme folder name for Composition, Structure and ThemeOther; null otherwise.</summary>
    public string? Theme { get; }

    public string Describe() => Kind switch
    {
        ScopeKind.Unrestricted => "the composition root",
        ScopeKind.Common => "Themes\\Common",
        ScopeKind.Composition => "the composition file of theme '" + Theme + "'",
        ScopeKind.Structure => "a structure of theme '" + Theme + "'",
        ScopeKind.ThemeOther => "theme '" + Theme + "'",
        _ => "core code",
    };
}

/// <summary>The pure layering decision behind WVTH001: classify the referencing file, classify the referenced namespace,
/// answer whether the edge is allowed. Kept free of Roslyn syntax so the tables are easy to read against the plan.</summary>
public static class ThemeLayering
{
    public const string CommonLayer = "Common";

    /// <summary>The file path relative to the Wavee project, forward slashes, or null when it cannot be placed. Uses
    /// <paramref name="projectDir"/> (normalised, trailing slash) when given, then falls back to locating the project by its
    /// path suffix.</summary>
    public static string? ToProjectRelative(string filePath, string? projectDir)
    {
        string norm = BaselinePaths.Normalize(filePath);
        if (projectDir is not null && norm.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase))
            return norm.Substring(projectDir.Length);

        const string AppsMarker = "/apps/Wavee/";
        int i = norm.IndexOf(AppsMarker, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
            return norm.Substring(i + AppsMarker.Length);

        // Case-sensitive on purpose: a checkout folder such as C:\WAVEE\ must not be taken for the project folder.
        const string ProjectMarker = "/Wavee/";
        i = norm.LastIndexOf(ProjectMarker, StringComparison.Ordinal);
        return i >= 0 ? norm.Substring(i + ProjectMarker.Length) : null;
    }

    /// <summary>Normalises an MSBuild <c>ProjectDir</c> to forward slashes with one trailing slash; null when empty.</summary>
    public static string? NormalizeProjectDir(string? projectDir)
    {
        if (projectDir is null)
            return null;
        string p = BaselinePaths.Normalize(projectDir);
        if (p.Length == 0)
            return null;
        return p.EndsWith("/", StringComparison.Ordinal) ? p : p + "/";
    }

    public static FileScope Classify(string? relativePath)
    {
        if (relativePath is null)
            return new FileScope(ScopeKind.Core, null);

        if (string.Equals(relativePath, "App.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(relativePath, "Theming/Composition/ThemeCatalog.cs", StringComparison.OrdinalIgnoreCase))
            return new FileScope(ScopeKind.Unrestricted, null);

        string[] seg = relativePath.Split('/');
        if (seg.Length < 3 || !string.Equals(seg[0], "Themes", StringComparison.OrdinalIgnoreCase))
            return new FileScope(ScopeKind.Core, null);

        string theme = seg[1];
        if (string.Equals(theme, CommonLayer, StringComparison.OrdinalIgnoreCase))
            return new FileScope(ScopeKind.Common, null);

        if (seg.Length == 3 && string.Equals(seg[2], theme + "Theme.cs", StringComparison.OrdinalIgnoreCase))
            return new FileScope(ScopeKind.Composition, theme);

        if (seg.Length >= 4 && string.Equals(seg[2], "Structure", StringComparison.OrdinalIgnoreCase))
            return new FileScope(ScopeKind.Structure, theme);

        return new FileScope(ScopeKind.ThemeOther, theme);
    }

    /// <summary>Whether a file in <paramref name="scope"/> may reference a type whose namespace is
    /// <c>Wavee.Themes.&lt;layer&gt;</c> (<paramref name="layer"/> null = a type directly in <c>Wavee.Themes</c>).</summary>
    public static bool IsAllowed(FileScope scope, string? layer)
    {
        switch (scope.Kind)
        {
            case ScopeKind.Unrestricted:
            case ScopeKind.Composition:
                return true;
            case ScopeKind.Common:
                return IsLayer(layer, CommonLayer);
            case ScopeKind.Structure:
                return IsLayer(layer, scope.Theme) || IsLayer(layer, CommonLayer);
            case ScopeKind.ThemeOther:
                return IsLayer(layer, scope.Theme);
            default:
                return false;
        }
    }

    private static bool IsLayer(string? layer, string? expected)
        => layer is not null && expected is not null && string.Equals(layer, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="ns"/> is <c>Wavee.Themes</c> or below it; <paramref name="layer"/> is the segment
    /// right under <c>Wavee.Themes</c> (null for <c>Wavee.Themes</c> itself).</summary>
    public static bool TryGetLayer(INamespaceSymbol? ns, out string? layer)
    {
        layer = null;
        INamespaceSymbol? child = null;
        for (INamespaceSymbol? cur = ns; cur is not null && !cur.IsGlobalNamespace; cur = cur.ContainingNamespace)
        {
            if (cur.Name == "Themes"
                && cur.ContainingNamespace is { Name: "Wavee" } wavee
                && wavee.ContainingNamespace is { IsGlobalNamespace: true })
            {
                layer = child?.Name;
                return true;
            }
            child = cur;
        }
        return false;
    }
}
