// ── Theming/Contracts/ListStyleLookup.cs — how a core host reads the structure's list style (themes plan 6.1, T8c) ───────────────
//
// A list-shaped surface is a STYLE, not a view (`TrackListStyle`, `EagerRowStyle`, `EpisodeRowStyle`, `SurfaceStyle`,
// `ArtistChartStyle`, `TrackDrawerStyle`, `DiscographyStyle`): the core host keeps its hooks, its state and its decisions and calls the templates of the
// style while it renders. The registry is ambient (`ViewRegistry.Context`; a theme switch remounts the root, so a mounted host
// never sees it change). A host mounted where no registry provides the key (a test that mounts outside the real shell) logs the
// miss ONCE per key and draws nothing, exactly as the track table's host always did since T8a.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

/// <summary>Resolves a style from the ambient registry, logging the first miss per key.</summary>
public static class ListStyleLookup
{
    static readonly object s_gate = new();
    static readonly HashSet<string> s_reported = [];

    /// <summary>The style registered under <paramref name="key"/>, or null (and one warning per key) when the registry is null or has none.</summary>
    public static TStyle? Of<TStyle>(ViewRegistry? registry, StyleKey<TStyle> key) where TStyle : class
    {
        if (registry is not null && registry.TryGetStyle(key, out var style)) return style;
        bool first;
        lock (s_gate) first = s_reported.Add(key.Id);
        if (first)
            Log.Warn("ui", "no style is registered for " + key.Id + "; the surface renders nothing");
        return null;
    }
}
