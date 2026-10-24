// ── Entities/Profile.Verbs.cs — the core verbs the profile page's view calls (themes plan 6.1, T10c2) ───────────────────────
//
// A structure's views may not name `Shell`, `Playback`, `Entities`, `Store` or `Prefs` (WVTH002): what a profile card opens, what a title resolves to and the
// account link are static verbs here, the way `ArtistVerbs` and `DetailVerbs` serve the artist and detail views. The decisions (which route a card opens)
// are core; the picture is the structure's.
//
// Role: CORE
// Spec: docs/themes/plan/technical-plan.md §6.1 (T10c2)

namespace Wavee;

/// <summary>The navigation and text verbs the structure's profile views call.</summary>
public static class ProfileVerbs
{
    /// <summary>The Spotify account page (subscription, privacy, password) — web-only; opened in the user's browser.</summary>
    public const string AccountUrl = "https://www.spotify.com/account";

    /// <summary>The "Open account" button's verb: the account page in the user's browser.</summary>
    public static readonly Action OpenAccount = static () => Actions.Services.OpenExternal?.Invoke(AccountUrl);

    /// <summary>A shelf card's click: a playlist, an artist or a person opens its page.</summary>
    public static void Open(in ProfileCard c)
    {
        switch (c.Kind)
        {
            case EntityKind.Playlist:
            {
                var pl = new Playlist(c.Slot);
                Shell.GoTo(Shell.For(pl.Uri, TitleOf(pl)));
                break;
            }
            case EntityKind.Artist:
                Track.GoToArtist(new Artist(c.Slot));
                break;
            case EntityKind.User:
            {
                var u = new User(c.Slot);
                Shell.GoTo(Shell.For(u.Uri, u.Name));
                break;
            }
        }
    }

    /// <summary>A playlist's title as text.</summary>
    public static string TitleOf(Playlist p) => Entities.Strings.Resolve(p.TitleId);
}
