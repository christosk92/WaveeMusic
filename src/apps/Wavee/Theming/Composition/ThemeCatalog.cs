// ── Theming/Composition/ThemeCatalog.cs — the installed compositions and the resolution of a theme id ──────────────────
//
// Filled once by `ThemeHost.Install` (App.cs), read everywhere through `Resolve` and `Installed`. Resolution never fails
// for an id: an unknown id, an empty one and a Developer-only theme without Developer mode all resolve to the fallback
// (Classic), so a stored `theme.id` of a theme that was removed, renamed or gated can never leave the app without a theme.
//
// This is one of the two files (with App.cs) WVTH001 allows to reference `Wavee.Themes.*`; it needs no such reference
// today because compositions arrive as values.

namespace Wavee.Theming;

/// <summary>The installed compositions.</summary>
public static class ThemeCatalog
{
    /// <summary>The theme every unresolvable id falls back to.</summary>
    public const string FallbackId = "classic";

    private static ThemeComposition[] s_installed = [];

    /// <summary>The manifests of the installed compositions, in install order. Empty before <c>ThemeHost.Install</c>.</summary>
    public static IReadOnlyList<ThemeManifest> Installed
    {
        get
        {
            var installed = s_installed;
            var manifests = new ThemeManifest[installed.Length];
            for (int i = 0; i < installed.Length; i++) manifests[i] = installed[i].Manifest;
            return manifests;
        }
    }

    /// <summary>The composition for <paramref name="id"/> (case-insensitive), or the fallback when the id is null, empty,
    /// unknown or Developer-only while Developer mode is off. Throws only when nothing is installed.</summary>
    public static ThemeComposition Resolve(string? id) => Resolve(id, Platform.Developer.Enabled.Peek());

    /// <summary>As <see cref="Resolve(string?)"/> with the Developer-mode state given (a test, or a caller that already read it).</summary>
    public static ThemeComposition Resolve(string? id, bool developerMode)
    {
        var installed = s_installed;
        if (installed.Length == 0)
            throw new InvalidOperationException("ThemeCatalog.Resolve: no theme is installed (ThemeHost.Install runs in App.BootCore).");
        if (!string.IsNullOrEmpty(id))
        {
            foreach (var t in installed)
            {
                if (!string.Equals(t.Manifest.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (t.Manifest.Availability == ThemeAvailability.Developer && !developerMode) break;
                return t;
            }
        }
        return Fallback(installed);
    }

    /// <summary>The installed composition with exactly <paramref name="id"/>, whatever its availability, or null.</summary>
    internal static ThemeComposition? Find(string id)
    {
        foreach (var t in s_installed)
            if (string.Equals(t.Manifest.Id, id, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    /// <summary>The compositions themselves (not the manifests).</summary>
    internal static IReadOnlyList<ThemeComposition> Compositions => s_installed;

    /// <summary>Replaces the installed set. Validated by the caller.</summary>
    internal static void Set(ThemeComposition[] installed) => s_installed = installed;

    private static ThemeComposition Fallback(ThemeComposition[] installed)
    {
        foreach (var t in installed)
            if (string.Equals(t.Manifest.Id, FallbackId, StringComparison.OrdinalIgnoreCase)) return t;
        // No Classic installed (a test catalog): the first public theme, else the first.
        foreach (var t in installed)
            if (t.Manifest.Availability == ThemeAvailability.Public) return t;
        return installed[0];
    }
}
