using System;
using Wavee.Features.Browse;

namespace Wavee;

/// <summary>Search-entity routes that are not a first-class page of their own. Genre IDs share the browse
/// <c>spotify:page:{id}</c> namespace; a URI we cannot parse falls back to committing the genre name as a search.</summary>
static class SearchRoutes
{
    const string GenrePrefix = "spotify:genre:";

    public static void OpenGenre(string uri, string name, Action<string, string?> go,
        NavOrigin? origin = null, Action<string, string?, NavOrigin?>? goOrigin = null)
    {
        if (uri.StartsWith(GenrePrefix, StringComparison.Ordinal) && uri.Length > GenrePrefix.Length)
        {
            string route = BrowseRoutes.Page("spotify:page:" + uri[GenrePrefix.Length..]);
            if (goOrigin is not null) goOrigin(route, name, origin);
            else go(route, name);
        }
        else
        {
            string? arg = name.Length == 0 ? null : name;
            if (goOrigin is not null) goOrigin("search", arg, origin);
            else go("search", arg);
        }
    }
}
