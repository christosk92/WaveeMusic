// ── Theming/Composition/ThemeInspection.cs — the identity reads of the `--inspect` server ────────────────────────────────
//
// The agent server (Screens/Diagnostics.Inspect*.cs) mounts a theme BY ID, reports which one is active and sets options on a
// named theme: identity reads, which WVTH004 allows only in the composition layer. They live here, in one place, so the
// diagnostics code never names ThemeHost.Current, ThemeCatalog or a manifest itself. The lint's per-theme edge guides
// (ThemeLintGuide.cs) are registered and looked up here for the same reason, and so are the names of the icon glyphs a theme declares
// itself (the ones the engine's Icons table does not name), so a dump or a lint message prints `glyph:PlaySolid`, not `glyph:U+F5B0`.

using System.Collections.Concurrent;

namespace Wavee.Theming;

/// <summary>Theme identity for the inspect server (diagnostics only; never a behaviour switch).</summary>
public static class ThemeInspection
{
    private static readonly ConcurrentDictionary<string, IThemeLintGuide> s_guides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> s_glyphNames = new(StringComparer.Ordinal);

    /// <summary>The active theme's id.</summary>
    public static string ActiveId => ThemeHost.Current.Peek().Manifest.Id;

    /// <summary>The installed theme with <paramref name="id"/>, or null.</summary>
    public static ThemeComposition? Find(string id) => ThemeCatalog.Find(id);

    /// <summary>The id of <paramref name="theme"/>.</summary>
    public static string IdOf(ThemeComposition theme) => theme.Manifest.Id;

    /// <summary>True when the installed theme <paramref name="id"/> is Developer-only (it resolves only with Developer mode on).</summary>
    public static bool IsDeveloperOnly(string id) => Find(id)?.Manifest.Availability == ThemeAvailability.Developer;

    /// <summary>Register the lint guide of theme <paramref name="id"/> (its composition file does, when it builds the theme; a second call replaces the first).</summary>
    public static void RegisterLintGuide(string id, IThemeLintGuide guide)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(guide);
        s_guides[id] = guide;
    }

    /// <summary>The active theme's lint guide, or null when it registered none.</summary>
    public static IThemeLintGuide? ActiveLintGuide => s_guides.TryGetValue(ActiveId, out IThemeLintGuide? guide) ? guide : null;

    /// <summary>Name the icon glyphs a theme declares itself (a constant of its rules: <c>(nameof(X.PlaySolid), X.PlaySolid)</c>), for the
    /// inspector's `glyph:<name>`. Its composition file registers them when it builds the theme. Every theme's names stand at once (a page
    /// can be another theme's stand-in), so a glyph keeps the first name it was given; the engine's <c>Icons</c> names come before these.</summary>
    public static void RegisterGlyphNames(params (string Name, string Glyph)[] glyphs)
    {
        foreach ((string name, string glyph) in glyphs)
            if (name.Length > 0 && glyph.Length > 0) s_glyphNames.TryAdd(glyph, name);
    }

    /// <summary>The name a theme registered for <paramref name="glyph"/> (<see cref="RegisterGlyphNames"/>).</summary>
    public static bool TryGetGlyphName(string glyph, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name)
        => s_glyphNames.TryGetValue(glyph, out name);
}
