// ── Theming/Composition/ThemeInspection.cs — the identity reads of the `--inspect` server ────────────────────────────────
//
// The agent server (Screens/Diagnostics.Inspect*.cs) mounts a theme BY ID, reports which one is active and sets options on a
// named theme: identity reads, which WVTH004 allows only in the composition layer. They live here, in one place, so the
// diagnostics code never names ThemeHost.Current, ThemeCatalog or a manifest itself.

namespace Wavee.Theming;

/// <summary>Theme identity for the inspect server (diagnostics only; never a behaviour switch).</summary>
public static class ThemeInspection
{
    /// <summary>The active theme's id.</summary>
    public static string ActiveId => ThemeHost.Current.Peek().Manifest.Id;

    /// <summary>The installed theme with <paramref name="id"/>, or null.</summary>
    public static ThemeComposition? Find(string id) => ThemeCatalog.Find(id);

    /// <summary>The id of <paramref name="theme"/>.</summary>
    public static string IdOf(ThemeComposition theme) => theme.Manifest.Id;

    /// <summary>True when the installed theme <paramref name="id"/> is Developer-only (it resolves only with Developer mode on).</summary>
    public static bool IsDeveloperOnly(string id) => Find(id)?.Manifest.Availability == ThemeAvailability.Developer;
}
