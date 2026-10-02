// ── Entities/Profile.Route.cs — the profile pages' route vocabulary (profile pages plan, Appendix R §2; #161) ───────
//
// Two route kinds (Shell.RouteKind.User / ProfileList) and the ONE composer every click source uses:
//
//     user:<user uri>                    RouteKind.User         the profile (Arg = the display name)
//     people:<facet digit>:<user uri>    RouteKind.ProfileList  one of its two whole lists (Following | Followers)
//
// `people:` is Discography-shaped (DiscoRoute): the user is the Subject, the `<digit>:<uri>` suffix is the Arg, so the facet
// is part of the key and each facet is its own place/slot. Pure apart from the Shell.Parse calls (which intern) — UI thread.
//
// The page registration (`Profile.InstallPages`) lives with the page (`Entities/Profile.Page.cs`), not here.

using System.Globalization;

namespace Wavee;

/// <summary>The facet a <c>people:&lt;facet&gt;:&lt;uri&gt;</c> route addresses. The value IS the key digit — never renumber.
/// There is deliberately no Playlists facet: the profile view gives at most ten public playlists and no way to page past
/// them (profile-pages plan, reconciliation round 2), so "See all" exists for the two people lists only.</summary>
public enum ProfileFacet : byte { Following = 0, Followers = 1 }

/// <summary><c>people:&lt;digit&gt;:&lt;user uri&gt;</c> — <see cref="DiscoRoute"/>'s shape. Subject = the user; Arg =
/// <c>"&lt;digit&gt;:&lt;uri&gt;"</c>.</summary>
public static class ProfileListRoute
{
    public const string Prefix = "people:";

    /// <summary>The route key: <c>people:1:spotify:user:…</c>.</summary>
    public static string Key(ProfileFacet facet, ReadOnlySpan<char> userUri)
        => string.Concat(Prefix, ((int)facet).ToString(CultureInfo.InvariantCulture), ":", userUri);

    /// <summary>Parse the suffix a route's <c>Arg</c> carries: <c>&lt;digit&gt;:&lt;non-empty uri&gt;</c>, the digit a
    /// <see cref="ProfileFacet"/>. False for anything else — a bad digit, a missing colon or an empty uri.</summary>
    public static bool TryParseArg(ReadOnlySpan<char> suffix, out ProfileFacet facet, out ReadOnlySpan<char> userUri)
    {
        facet = ProfileFacet.Following;
        userUri = default;
        if (suffix.Length < 3 || suffix[1] != ':' || suffix[0] is < '0' or > '1') return false;
        var uri = suffix[2..].Trim();
        if (uri.IsEmpty) return false;
        facet = (ProfileFacet)(suffix[0] - '0');
        userUri = uri;
        return true;
    }

    /// <summary>The facet and the user a <see cref="Shell.RouteKind.ProfileList"/> route addresses; false for any other
    /// kind or a malformed one (this is also <see cref="Shell.IsKnown"/>'s strict test for the kind).</summary>
    public static bool TryParse(in Shell.Route route, out ProfileFacet facet, out EntityUri user)
    {
        facet = ProfileFacet.Following;
        user = default;
        if (route.Kind != Shell.RouteKind.ProfileList || route.Arg.IsEmpty) return false;
        if (!TryParseArg(Entities.Strings.Resolve(route.Arg), out facet, out _)) return false;
        user = route.Subject;
        return user.Id.Form != EntityForm.None;
    }

    /// <summary>Built through <see cref="Shell.Parse"/> so a key minted here and one minted by a deep link or a restored
    /// history row are the same route.</summary>
    public static Shell.Route For(EntityUri user, ProfileFacet facet)
        => user.Id.Form == EntityForm.None ? Shell.Route.None : Shell.Parse(Key(facet, user.Text));

    /// <inheritdoc cref="For(EntityUri,ProfileFacet)"/>
    public static Shell.Route For(User u, ProfileFacet facet) => u.IsValid ? For(u.Uri, facet) : Shell.Route.None;

    /// <summary>The loc KEY of a facet's label ("Following" / "Followers") — the selector bar, the tab strip and the
    /// history rows all read it (<c>person.pivot.*</c>, shared with the profile page's pivot).</summary>
    public static string FacetLabelKey(ProfileFacet facet) => facet switch
    {
        ProfileFacet.Followers => Strings.Person.Pivot.Followers,
        _ => Strings.Person.Pivot.Following,
    };
}

/// <summary>The one "open this person" composer every click source uses (a search hit, the owner block, the friend rail,
/// the account flyout). It goes through <see cref="Shell.Parse"/> rather than <see cref="Shell.For"/>'s kind switch so a row
/// whose uri is not a <c>spotify:user:</c> one — <c>--fake</c>'s bare account row — still routes.</summary>
public static class ProfileRoute
{
    /// <summary>The profile route for a user row, named after the row's display name when it is known;
    /// <see cref="Shell.Route.None"/> for an invalid handle.</summary>
    public static Shell.Route For(User u)
        => u.IsValid ? For(u.Uri, u.Knows(UserFields.Identity) ? u.Name : "") : Shell.Route.None;

    /// <summary>The same route off a bare uri (a detail page's <c>Identity.Owner</c> carries the owner's uri, not a row),
    /// named <paramref name="name"/>; <see cref="Shell.Route.None"/> for a uri with no id.</summary>
    public static Shell.Route For(EntityUri user, ReadOnlySpan<char> name = default)
        => user.Id.Form == EntityForm.None ? Shell.Route.None : Shell.Parse(string.Concat("user:", user.Text), name);
}
