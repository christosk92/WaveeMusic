// ── Entities/Entities.Fake.Profile.cs — SEED-SURFACES for the profile pages (profile pages plan, D1) ───────────────────
//
// THE SAME RULES AS `Entities.Fake.cs`: rows go through `Staging` + `Commit` at `Authority.Seed`, the four shelves through
// the edge table's whole-run write; every value is a pure function of a fixture index; nothing publishes — `SeedFake`
// publishes once. Why this file exists at all: the four fake users are `spotify:user:` rows with no registered provider, so
// a profile page asking Social would shimmer forever. With the facts and shelves seeded the page renders at once, and the
// mount's `Invalidate` leaves it rendering (Stale) while the (absent) provider never answers.

namespace Wavee;

public static partial class Entities
{
    /// <summary>The four seeded users' profile facts and shelves.</summary>
    static partial void SeedProfileSurfaces(long now0)
    {
        _ = now0;
        var s = Staging.Rent();
        try
        {
            for (int u = 0; u < 4; u++)
            {
                ref var row = ref s.Users.RowFor(s.AddText(Utf8(UserUri(u))), Authority.Seed,
                    (uint)(UserFields.Social | UserFields.Follow));
                row.Followers = 3;
                row.Following = 6;
                row.PublicPlaylists = 3;
                row.Flags = (uint)(UserFlags.ShowFollows | UserFlags.AllowFollows)
                          | (u == 0 ? (uint)UserFlags.CurrentUser : 0u) | (u == 1 ? (uint)UserFlags.Followed : 0u);
            }
            Commit(s);
        }
        finally { Staging.Return(s); }

        var edges = Current.Edges;
        Span<int> targets = stackalloc int[8];
        Span<ProfileCardEdge> cards = stackalloc ProfileCardEdge[8];
        for (int u = 0; u < 4; u++)
        {
            int user = ResolveSeedSlot(EntityKind.User, UserUri(u));
            if (user == Table.None) continue;
            int n = 0;
            for (int k = 0; k < 3; k++)
            {
                targets[n] = ResolveSeedSlot(EntityKind.Playlist, PlaylistUri(u + k));
                cards[n++] = new ProfileCardEdge(EntityKind.Playlist, ProfileCardFlags.None, 3 + 5 * k);
            }
            edges.ProfilePlaylists.ReplaceRun(user, targets[..n], cards[..n]);
            n = 0;
            for (int a = 0; a < 4; a++)
            {
                targets[n] = ResolveSeedSlot(EntityKind.Artist, ArtistUri(3 * u + a));
                cards[n++] = new ProfileCardEdge(EntityKind.Artist, a == 0 ? ProfileCardFlags.OwnerFollows : ProfileCardFlags.None, 10_000 * (a + 1));
            }
            edges.ProfileArtists.ReplaceRun(user, targets[..n], cards[..n]);
            n = 0;
            for (int o = 0; o < 4; o++)
            {
                if (o == u) continue;
                targets[n] = ResolveSeedSlot(EntityKind.User, UserUri(o));
                cards[n++] = new ProfileCardEdge(EntityKind.User, ProfileCardFlags.None, 3);
            }
            edges.ProfileFollowers.ReplaceRun(user, targets[..n], cards[..n]);
            n = 0;
            for (int a = 0; a < 3; a++)
            {
                targets[n] = ResolveSeedSlot(EntityKind.Artist, ArtistUri(u + a));
                cards[n++] = new ProfileCardEdge(EntityKind.Artist, a == 1 ? ProfileCardFlags.ViewerFollows : ProfileCardFlags.None, 25_000 * (a + 1));
            }
            for (int o = 0; o < 4; o++)
            {
                if (o == u) continue;
                targets[n] = ResolveSeedSlot(EntityKind.User, UserUri(o));
                cards[n++] = new ProfileCardEdge(EntityKind.User, o == 1 ? ProfileCardFlags.ViewerFollows : ProfileCardFlags.None, 3);
            }
            edges.ProfileFollowing.ReplaceRun(user, targets[..n], cards[..n]);
        }
    }
}
