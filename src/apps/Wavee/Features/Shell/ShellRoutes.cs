using System;
using Wavee.Features.Browse;
using Wavee.Features.Concerts;

namespace Wavee;

/// <summary>The closed set of route keys this shell can actually render — the truth <see cref="ShellNav"/> cannot
/// tell you. <c>ShellNav.Dest</c> answers "what would this route be CALLED", and it answers for every string, because
/// its job is to label a tab even for a destination it has never heard of ("Your Library"). This class answers the
/// different question the deep-link intake, the history rows and the not-found page each need: "is there a page
/// behind this key at all?".
///
/// <para>Deliberately ENGINE-FREE (string operations plus the pure route helpers): it is source-included by
/// src/apps/Wavee.Tests, which cannot see the engine-bound page classes that own some of these constants. That is why
/// <c>disco:</c>, <c>album:</c> and friends are spelled as literals here — the same call <see cref="ShellNav"/>
/// already makes, for the same reason.</para>
///
/// <para>OWNERSHIP: the exact names and prefixes below must stay in step with <c>ContentHost.PageFor</c>, which is the
/// one place that maps a route onto a page. A key that PageFor can render but this class rejects is a page the user
/// can no longer deep-link to; a key this class accepts but PageFor cannot render lands on the not-found page.</para>
/// </summary>
static class ShellRoutes
{
    // Exact route names. Every one of these has an arm in ContentHost.PageFor (or, for the library kinds, a shared
    // LibraryPage arm) — see the ownership note above.
    static readonly string[] s_exact =
    [
        "home",
        BrowseRoutes.Home,          // "browse" — the directory (exact; BrowseRoutes.Is is prefix-only)
        "search",
        "albums",
        "artists",
        "liked",
        "podcasts",
        "local",
        "history",
        "recents",
        "settings",
        "api-console",
        "playback-diagnostics",
        "whatsnew",
        "sidebar-customize",
        "home-customize",
    ];

    // Prefix families: "<prefix><entity uri>". A bare prefix with nothing after it addresses no entity, so it is NOT
    // a known route — the deep-link intake is the caller that would otherwise open an empty detail page.
    static readonly string[] s_prefixes =
    [
        "album:",
        "pl:",
        "artist:",
        "show:",
        "prerelease:",
        "disco:",
        "module:",
        BrowseRoutes.Prefix,        // "browse:"
        HomeSectionRoutes.Prefix,   // "home-section:"
        BrowseSectionRoutes.Prefix, // "browse-section:"
    ];

    /// <summary>True when <paramref name="routeKey"/> names a destination this shell can render.</summary>
    public static bool IsKnown(string? routeKey)
    {
        if (string.IsNullOrEmpty(routeKey)) return false;

        for (int i = 0; i < s_exact.Length; i++)
            if (string.Equals(routeKey, s_exact[i], StringComparison.Ordinal)) return true;

        for (int i = 0; i < s_prefixes.Length; i++)
        {
            string p = s_prefixes[i];
            if (routeKey.Length > p.Length && routeKey.StartsWith(p, StringComparison.Ordinal)) return true;
        }

        // The concert family owns its own parse (hub / artist schedule / detail), including the emptiness rules.
        return ConcertRoutes.TryParse(routeKey, out _);
    }
}
