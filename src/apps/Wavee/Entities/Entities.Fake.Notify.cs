// ── Entities/Entities.Fake.Notify.cs ───────────────────────────────────────────────────────────────────────────────
// The --fake gander feed: the Spotify rows the notification panel shows when no account is asked (Home.Host.Refresh).
//
// Role: CORE (fixture). Each row's action target is one SPELLING the live feed hands out, so the panel exercises the link
// door (Shell.Links.cs) end to end: an `https://open.spotify.com/episode/…?si=…` web target (the shape that used to leave
// the app for the browser), an `intl-xx` locale-prefixed artist link, a `spotify:` album uri and a genuinely external page.
//
// Every row is READ: `Notify.Rebuild` escalates unread rows to OS toasts, and a demo launch must not raise Windows banners
// (the rule Entities.Fake.Home.cs states for the what's-new timeline). PURE IN now0, like every other fixture: each
// timestamp is now0 minus a constant.

namespace Wavee;

public static partial class Entities
{
    /// <summary>The --fake gander rows, newest first. <paramref name="now0"/> is the seed epoch in unix SECONDS.</summary>
    public static IReadOnlyList<Notification> FakeSocialFeed(long now0)
    {
        const long Hour = 3_600;
        long ms = now0 * 1000;
        return
        [
            NotifyRows.ForSocial("fake:gander:episode", ms - 2 * Hour * 1000, false,
                "New episode of " + s_showNames[0] + ": catch up before the weekend",
                "https://open.spotify.com/episode/sh0e1?si=fake0share", SocialActionType.Navigate, Cover(3), s_showNames[0], null),
            NotifyRows.ForSocial("fake:gander:artist", ms - 9 * Hour * 1000, false,
                "An artist you follow has a new profile",
                "https://open.spotify.com/intl-de/artist/ar3", SocialActionType.Navigate, Cover(5), null, null),
            NotifyRows.ForSocial("fake:gander:album", ms - 30 * Hour * 1000, false,
                "Your weekly album pick is ready",
                "spotify:album:al1", SocialActionType.Navigate, Cover(7), null, null),
            NotifyRows.ForSocial("fake:gander:web", ms - 52 * Hour * 1000, false,
                "Read the Spotify newsroom story",
                "https://newsroom.spotify.com/", SocialActionType.NavigateWebview, null, null, null),
        ];
    }
}
