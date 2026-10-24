// ── Entities/Browse.Verbs.cs ───────────────────────────────────────────────────────────────────────────────────────────
// Browse's CORE vocabulary the pictures call: the verbs a category tile and "Explore all categories" run, and the directory's category list
// projected off its rows
//
// Role: CORE (verbs and data)
// Owner: P (stream P3); T10a2 moved the five cell densities, the link grid, the directory body and its bands, the category block, the
// Explore link and the loading directory into Classic's `Views\Pages\ClassicBrowseCells.cs` / `ClassicBrowseDirectory.cs`, and left what the
// pages and the Home zones still call here.
//
// EVERY CELL IS A LINK. A tile is a destination: its Open verb is `BrowseTiles.OpenCategory` (a category page, no origin — the route family
// composes the `Browse › X` trail itself) or `BrowseTiles.OpenFeature` (a client feature: only a resolvable one navigates). No menu, no drag —
// a browse category is a link, full stop (§6.3).

using FluentGpu.Foundation;

namespace Wavee;

public static partial class BrowseTiles
{
    /// <summary>Open a category page (0.2.9 <c>go(BrowseRoutes.Page(uri), title)</c>): no origin — the route family
    /// composes the <c>Browse › X</c> trail itself. UI thread (the route interns).</summary>
    public static readonly Action<string, string> OpenCategory = static (uri, title) => Shell.GoTo(PageRoute(uri, title));

    /// <summary>Open a client feature: only a resolvable one navigates; anything else declines and logs, rather than
    /// navigating to a key no page renders (0.2.9 <c>BrowseRoutes.FeatureRoute</c> + <c>browse.feature.unsupported</c>).</summary>
    public static readonly Action<string> OpenFeature = static uri =>
    {
        var route = FeatureRoute(uri);
        if (route.IsNone) Log.Warn("nav", "browse.feature.unsupported: " + uri);
        else Shell.GoTo(route);
    };

    /// <summary>A category → its tile model for a LIVE surface (both handlers wired) or the inert loading one.</summary>
    public static BrowseTileModel ModelOf(BrowseCategory c, bool live)
        => ToModel(c, live ? OpenCategory : null, live ? OpenFeature : null);
}

public readonly partial struct Browse
{
    /// <summary>The directory's category list projected off its rows (the rules' <see cref="BrowseCategory"/> value): the
    /// directory relation's targets in wire order, each tile's title, colour, artwork url and client-feature bit. A tile
    /// nobody has titled yet is skipped (it would render as an empty pill). UI thread; allocates — the page memoizes it
    /// per publication.</summary>
    public static BrowseCategory[] CategoriesOf(ReadOnlySpan<int> tileSlots)
    {
        var list = new List<BrowseCategory>(tileSlots.Length);
        for (int i = 0; i < tileSlots.Length; i++)
        {
            if (CategoryAt(tileSlots[i]) is { } c) list.Add(c);
        }
        return list.ToArray();
    }

    /// <summary>One browse node as a category, or null when it has no identity to render.</summary>
    public static BrowseCategory? CategoryAt(int slot)
    {
        var b = new Browse(slot);
        if (!b.IsValid || b.TitleId.IsEmpty) return null;
        var id = b.Id;
        if (id.Form == EntityForm.None) return null;
        return new BrowseCategory(id.Text, Entities.Strings.Resolve(b.TitleId), b.Color == 0u ? (uint?)null : b.Color,
                                  Controls.ArtUrl(b.ImageId), b.IsClientFeature);
    }

    /// <summary>The Explore-all verb: the directory, no origin.</summary>
    public static readonly Action GoDirectory = static () => Shell.GoTo(new Shell.Route(Shell.RouteKind.Browse));
}
