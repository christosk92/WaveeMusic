// ── Theming/Composition/ThemeViewInspection.cs — which layer registered every view and style key of a theme (`--inspect` `views`) ──
//
// A theme still being built (an `IStandInStructure` with its stand-in on: WinUI and Zune today) registers its own views and fills every
// key it does not draw yet from a stand-in structure (Classic's, `ViewRegistryBuilder.AddMissing`). The report builds the structure's
// registry twice, with and without the stand-in: a key only the full registry holds is a stand-in, and its layer is the installed
// structure whose own registry registers it with the same template id. Style keys (`AddStyle`) carry no template id, so a style's layer
// is the structure whose own registry holds the key. Identity reads (a structure's id, the installed themes), so the code lives in the
// composition layer, beside ThemeInspection (WVTH004); Screens/Diagnostics.Inspect.Views.cs writes the reply.

using FluentGpu.Theming;

namespace Wavee.Theming;

/// <summary>The `views` report of the inspect server (diagnostics only; never a behaviour switch).</summary>
public static class ThemeViewInspection
{
    /// <summary>One registered key: its id, the template id (null for a style), the structure layer that registered it and whether
    /// that layer is a stand-in's (not the theme's own).</summary>
    public readonly record struct Entry(string Key, string? TemplateId, string Layer, bool StandIn);

    /// <summary>What `views` reports for one theme: its id, its structure's id, and every view and style key, sorted by key.</summary>
    public sealed record Report(string Theme, string Layer, IReadOnlyList<Entry> Views, IReadOnlyList<Entry> Styles)
    {
        /// <summary>The view keys a stand-in fills.</summary>
        public int StandInViews => Views.Count(static e => e.StandIn);

        /// <summary>The style keys a stand-in fills.</summary>
        public int StandInStyles => Styles.Count(static e => e.StandIn);
    }

    /// <summary>The registry of the structure's own views: a structure with its stand-in on registers them alone; any other
    /// structure is all its own.</summary>
    private static ViewRegistry OwnRegistry(IStructureLayer structure)
    {
        var builder = new ViewRegistryBuilder();
        if (structure is IStandInStructure { UsesStandIn: true } partial) partial.RegisterOwnViews(builder);
        else structure.Register(builder);
        return builder.Build();
    }

    /// <summary>The report of the theme <paramref name="themeId"/>, or null when no such theme is installed.</summary>
    public static Report? Of(string themeId)
    {
        if (ThemeCatalog.Find(themeId) is not { } theme) return null;
        ViewRegistry full = theme.Registry;
        ViewRegistry own = OwnRegistry(theme.Structure);
        string layer = theme.Structure.Id;
        // The other installed structures' own registries: where a stand-in key comes from.
        var others = new List<(string Layer, ViewRegistry Own, HashSet<string> Styles)>();
        foreach (ThemeManifest m in ThemeCatalog.Installed)
        {
            if (ThemeCatalog.Find(m.Id) is not { } other || other.Structure.Id == layer) continue;
            ViewRegistry otherOwn = OwnRegistry(other.Structure);
            others.Add((other.Structure.Id, otherOwn, new HashSet<string>(otherOwn.StyleKeyIds, StringComparer.Ordinal)));
        }
        var views = new List<Entry>(full.ViewCount);
        foreach (string key in full.ViewKeyIds)
        {
            string? template = full.TemplateIdOf(key);
            if (own.TemplateIdOf(key) is { } mine && mine == template) { views.Add(new Entry(key, template, layer, false)); continue; }
            string from = "?";
            foreach (var (otherLayer, otherOwn, _) in others)
                if (otherOwn.TemplateIdOf(key) == template) { from = otherLayer; break; }
            views.Add(new Entry(key, template, from, true));
        }
        var ownStyles = new HashSet<string>(own.StyleKeyIds, StringComparer.Ordinal);
        var styles = new List<Entry>();
        foreach (string key in full.StyleKeyIds)
        {
            if (ownStyles.Contains(key)) { styles.Add(new Entry(key, null, layer, false)); continue; }
            string from = "?";
            foreach (var (otherLayer, _, otherStyles) in others)
                if (otherStyles.Contains(key)) { from = otherLayer; break; }
            styles.Add(new Entry(key, null, from, true));
        }
        views.Sort(static (x, y) => string.CompareOrdinal(x.Key, y.Key));
        styles.Sort(static (x, y) => string.CompareOrdinal(x.Key, y.Key));
        return new Report(theme.Manifest.Id, layer, views, styles);
    }
}
