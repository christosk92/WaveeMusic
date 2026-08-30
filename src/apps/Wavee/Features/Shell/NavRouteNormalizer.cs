// Engine-free (System + FluentGpu.Controls.Route + BrowseRoutes) so NavRouteNormalizerTests can source-include it.
using System;
using FluentGpu.Controls;
using Wavee.Features.Browse;

namespace Wavee;

/// <summary>The ONE back-compat rewrite for committed navigation. Called from <c>WaveeShell.Go</c> (every nav verb)
/// and the two restore paths (pinned workspace + session snapshot). Empty/whitespace <c>search</c> becomes the
/// Browse directory; the synthetic recents home-section route becomes the canonical recents page. A page must never
/// see the pre-normalized form — ContentHost receives already-rewritten <see cref="Route"/> values.</summary>
static class NavRouteNormalizer
{
    /// <summary>Older Home documents and persisted history can still carry this synthetic section route. It was never
    /// page-able (<c>spotify:list:recents:main</c> is not a home-section resource).</summary>
    public const string LegacyRecentsRoute = "home-section:spotify:list:recents:main";

    public static Route Apply(string name, string? arg)
    {
        if (string.Equals(name, "search", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(arg))
            return new Route(BrowseRoutes.Home);
        if (string.Equals(name, LegacyRecentsRoute, StringComparison.Ordinal))
            return new Route("recents", arg);
        return new Route(name, arg);
    }

    public static Route Apply(in Route r) => Apply(r.Name, r.Arg);
}
