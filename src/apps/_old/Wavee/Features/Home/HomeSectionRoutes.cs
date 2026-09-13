// HomeSectionRoutes and BrowseSectionRoutes are split out of HomeSectionNavigation.cs so they stay engine-free and
// source-includable into Wavee.Tests, the same way HomeSectionPaging.cs is: HomeSectionPreviewStore and HomeCardNav
// are Context-bound (FluentGpu.Hooks) and stay behind in HomeSectionNavigation.cs.
using System;

namespace Wavee;

static class HomeSectionRoutes
{
    public const string Prefix = "home-section:";

    /// <summary>The scheme Home mints for a section the SERVER gave no URI for: <c>wavee:local:&lt;hash&gt;</c>. It is a
    /// purely LOCAL route identity — it addresses a <see cref="HomeSectionPreviewStore"/> entry and nothing else. It must
    /// never reach a paging endpoint: neither <c>homeSection</c> nor <c>browseSection</c> can resolve it, which the
    /// section page used to surface as a hard error page once the bounded preview store had evicted the seed.
    /// <para>OWNER: this const. <c>HomePage.OpenSection</c> still builds the same string as a literal — that literal is
    /// redundant and should migrate here, so the minting side and the recognising side share one definition.</para>
    /// </summary>
    public const string LocalPrefix = "wavee:local:";

    public static string Page(string sectionUri) => Prefix + sectionUri;
    public static bool Is(string route) => route.StartsWith(Prefix, StringComparison.Ordinal);
    public static string UriOf(string route) => Is(route) ? route[Prefix.Length..] : "";

    /// <summary>True for a client-minted section identity — there is no server resource behind it, so it is never a
    /// legal argument to a browse read.</summary>
    public static bool IsLocal(string? uri) =>
        uri is not null && uri.StartsWith(LocalPrefix, StringComparison.Ordinal);
}

/// <summary>A route that addresses a BROWSE section — a section paged through <c>IBrowseService.GetSectionAsync</c>
/// (the <c>browseSection</c> operation), never the Home document. Some browse sections (the hardcoded Charts ids in
/// <see cref="Wavee.Features.Browse.BrowseTaxonomy.ChartSections"/>) carry a <c>spotify:section:</c> uri that looks
/// exactly like a Home section uri — that ambiguity is exactly why the ROUTE PREFIX, not the uri, is what selects the
/// API. A route built through this class can never be resolved by <c>homeSection</c>, whatever its uri looks like.</summary>
static class BrowseSectionRoutes
{
    public const string Prefix = "browse-section:";
    public static string Page(string sectionUri) => Prefix + sectionUri;
    public static bool Is(string route) => route.StartsWith(Prefix, StringComparison.Ordinal);
    public static string UriOf(string route) => Is(route) ? route[Prefix.Length..] : "";
}
