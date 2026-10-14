// ── Entities/Profile.Lists.Verbs.cs — what the profile list view does and says that touches core state (themes plan 6.1, T10c2) ──────────────
//
// A structure's views may not name `Shell`, `Playback`, `Entities`, `Store` or `Prefs` (WVTH002): what the Following / Followers list's cards must DO or SAY that
// reaches them is a static verb here, the way `HomeVerbs` serves Home. The pictures call them as handlers; the decisions (which route, which phrase) are core.
//
// Role: CORE
// Spec: docs/themes/plan/technical-plan.md §6.1 (T10c2)

namespace Wavee;

/// <summary>The navigation and text verbs the profile list view calls.</summary>
public static class ProfileListVerbs
{
    /// <summary>The click of a person's card: opens their profile (the route is resolved once per render of the card; a user with no route opens nothing).</summary>
    public static Action OpenPerson(int slot)
    {
        var route = ProfileRoute.For(new User(slot));
        return () => { if (!route.IsNone) Shell.GoTo(route); };
    }

    /// <summary>An artist card's second line: "N followers" when the count is known, else the plain "Artist".</summary>
    public static string ArtistSubtitle(int followers)
        => followers > 0 ? Profile.FollowerCount(Strings.Person.List.Card.ArtistFollowersKey, followers) : FluentGpu.Localization.Loc.Get(Strings.Person.List.Card.Artist);

    /// <summary>A person card's second line: "N followers" when the count is known, else "Profile".</summary>
    public static string PersonSubtitle(int followers)
        => followers > 0 ? Profile.FollowerCount(Strings.Person.List.Card.FollowersKey, followers) : FluentGpu.Localization.Loc.Get(Strings.Person.List.Card.Profile);
}
