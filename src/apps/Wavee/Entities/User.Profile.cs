// ── Entities/User.Profile.cs — the profile page's relations, payload and handle reads (profile pages plan, D1) ──────
//
// A user's PROFILE (`spotify:user:`) is one row plus four shelves, all parented on the user's slot:
//
//        Users[u] ──┬── ProfilePlaylists   EdgeTable<ProfileCardEdge>  ┐ ride the profile view (UserFields.Social): no door
//                   ├── ProfileArtists     EdgeTable<ProfileCardEdge>  ┘ of their own — asked by asking the ROW for Social
//                   ├── ProfileFollowers   EdgeTable<ProfileCardEdge>  ┐ whole, unpaged lists behind their own endpoints
//                   └── ProfileFollowing   EdgeTable<ProfileCardEdge>  ┘ (FetchEdge.ProfileFollowers / ProfileFollowing)
//
// The names and covers of the people / artists / playlists on a card land on the TARGET row (Thin), so the payload is
// three numbers — which table the slot indexes, the follow bits and the follower count the card states — and holds no text
// (`Edges.ReleaseText` has nothing to walk here). Everything the page and the list page read goes through the `User`
// accessors below; the load rule over them is D3's (`User.Profile.Rules.cs`).

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>A profile card's follow bits. Viewer-relative vs owner-relative is the WIRE's distinction (research §2).</summary>
[Flags]
public enum ProfileCardFlags : byte
{
    None = 0,
    /// <summary>The VIEWER follows the card's target (playlist f7, following-list artist f7, list user f6).</summary>
    ViewerFollows = 1 << 0,
    /// <summary>The PROFILE OWNER follows the artist (recently-played f5).</summary>
    OwnerFollows = 1 << 1,
}

/// <summary>The four profile relations, as the UI names them.</summary>
public enum ProfileShelf : byte { Playlists, Artists, Followers, Following }

/// <summary>One card on a profile shelf or list: which TABLE the target slot indexes (Following mixes artists and users —
/// the <see cref="KindEdge"/> shape), its follow bits, and the follower count the card states. No text: the name and
/// cover land on the target ROW (Thin), so <see cref="Edges.ReleaseText"/> has nothing to walk here.</summary>
public readonly record struct ProfileCardEdge(EntityKind Kind, ProfileCardFlags Flags, int Followers)
{
    /// <summary>Pair this payload with its target slot — the cross-kind pointer a mixed card binds to.</summary>
    public EntityRef Ref(int target) => new(Kind, target);
    public bool ViewerFollows => (Flags & ProfileCardFlags.ViewerFollows) != 0;
    public bool OwnerFollows => (Flags & ProfileCardFlags.OwnerFollows) != 0;

    /// <summary>Which kinds a relation admits — the decoder's and the commit's one filter. PURE.</summary>
    public static bool Admits(Relation relation, EntityKind kind) => relation switch
    {
        Relation.ProfilePlaylists => kind == EntityKind.Playlist,
        Relation.ProfileArtists => kind == EntityKind.Artist,
        Relation.ProfileFollowers => kind == EntityKind.User,
        Relation.ProfileFollowing => kind is EntityKind.Artist or EntityKind.User,
        _ => false,
    };
}

public sealed partial class Edges
{
    /// <summary>Parent = the user row. Playlists/Artists RIDE the profile view (asked through UserFields.Social, never
    /// door-asked); Followers/Following are their own whole, unpaged reads (FetchEdge.ProfileFollowers/…Following).</summary>
    public readonly EdgeTable<ProfileCardEdge> ProfilePlaylists = new(), ProfileArtists = new(),
                                               ProfileFollowers = new(), ProfileFollowing = new();
}

public readonly partial struct User
{
    // ── identity (UI-frozen names) ──
    public string Name => Entities.Strings.Resolve(T.Name[Slot]);
    /// <summary>The avatar as a renderable url (<see cref="Controls.ArtUrl"/>), or null → paint the initial on <see cref="Color"/>.</summary>
    public string? Image => Controls.ArtUrl(T.Image[Slot]);
    /// <summary>0xFFRRGGBB brand colour, 0 = none. Gated on Identity: a recycled slot's stale column never paints.</summary>
    public uint Color => Knows(UserFields.Identity) ? T.Color[Slot] : 0u;

    // ── social (read when Knows(UserFields.Social)) ──
    public int PublicPlaylists => T.PublicPlaylists[Slot];
    public UserFlags Flags => (UserFlags)T.Flags[Slot];
    public bool IsCurrentUser => (Slot > Wavee.Table.None && Slot == Entities.Current.MeSlot) || Has(UserFields.Social, UserFlags.CurrentUser);
    public bool IsFollowedByViewer => Has(UserFields.Follow, UserFlags.Followed);
    public bool ShowFollows => Has(UserFields.Social, UserFlags.ShowFollows);
    public bool AllowFollows => Has(UserFields.Social, UserFlags.AllowFollows);
    public bool IsUnavailable => Has(UserFields.Social, UserFlags.Unavailable);
    bool Has(UserFields group, UserFlags flag) => (T.Known[Slot] & (uint)group) != 0 && (T.Flags[Slot] & (uint)flag) != 0;

    // ── the load marks a readiness rule reads (ProfileLoadRule) ──
    public bool IsAsked(UserFields groups) => (T.Asked[Slot] & (uint)groups) != 0;
    public bool IsInflight => T.Inflight[Slot] != 0;
    public bool IsFailed(UserFields groups) => T.IsFailed(Slot, (uint)groups);
    public bool IsStale(UserFields groups) => T.IsStale(Slot, (uint)groups);

    // ── the relations ──
    public static EdgeTable<ProfileCardEdge> ProfileRelation(ProfileShelf shelf) => shelf switch
    {
        ProfileShelf.Artists => Entities.Current.Edges.ProfileArtists,
        ProfileShelf.Followers => Entities.Current.Edges.ProfileFollowers,
        ProfileShelf.Following => Entities.Current.Edges.ProfileFollowing,
        _ => Entities.Current.Edges.ProfilePlaylists,
    };
    /// <summary>The door a shelf is asked through: the two lists have their own; the riding shelves have none (Social).</summary>
    public static FetchEdge FetchEdgeOf(ProfileShelf shelf) => shelf switch
    {
        ProfileShelf.Followers => FetchEdge.ProfileFollowers,
        ProfileShelf.Following => FetchEdge.ProfileFollowing,
        _ => FetchEdge.None,
    };
    public ReadOnlySpan<int> ProfileTargets(ProfileShelf shelf) => ProfileRelation(shelf).Targets(Slot);
    public ReadOnlySpan<ProfileCardEdge> ProfileCards(ProfileShelf shelf) => ProfileRelation(shelf).Payload(Slot);
    public EdgeState ProfileReadiness(ProfileShelf shelf) => ProfileRelation(shelf).Readiness(Slot);
    public int ProfileFailure(ProfileShelf shelf) => ProfileRelation(shelf).FailureOf(Slot);
    public int ProfileCount(ProfileShelf shelf) => ProfileRelation(shelf).Count(Slot);
    public int ProfileTotal(ProfileShelf shelf) => ProfileRelation(shelf).Total(Slot);
    public uint ProfileVersion(ProfileShelf shelf) => ProfileRelation(shelf).Version(Slot);
    /// <summary>The own account's "Top artists this month" (Home.Feeds.EnsureTopContent; ranked).</summary>
    public ReadOnlySpan<int> TopArtistSlots => E.UserTopArtists.Targets(Slot);
}

public static partial class Entities
{
    /// <summary>The user twin of <c>Invalidate(Playlist, …)</c>: known groups go stale (still rendering) and are asked again.</summary>
    public static void Invalidate(User row, UserFields groups, FetchPriority priority = FetchPriority.Visible)
    {
        int slot = row.Slot;
        Fetch.Invalidate(Current, Current.Users, new ReadOnlySpan<int>(in slot), (uint)groups, priority);
    }
}
