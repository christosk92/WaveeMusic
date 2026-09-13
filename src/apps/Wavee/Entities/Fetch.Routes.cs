// ── Entities/Fetch.Routes.cs — CORE: the routing input contract (a named partial of Fetch.cs; gap batch B1, G-040 ────
//    planner half, G-041, G-042; consumed by `Spotify.Api`'s fetch provider, batch B1b)
//
// Role: CORE · Owner: C (the table) / F (its consumer) · Budget: 420 lines · Spec: gap register G-040/G-041/G-042
//
// WHAT A BATCH MUST SEND. The planner hands a provider rows that share a NEED — (subject, kind, groups) — and before
// this table the provider knew one thing to do with it: POST the kind's single extension kind, whatever the page had
// asked for (`CatalogKindOf`). So `TrackFields.PlayCount/Audio/Tags/Video`, `ArtistFields.Overview/Chart` and
// `PlaylistFields.Saves` never arrived, and every synthetic subject (home, search, browse) had no route at all. This
// file is the one place that says, per group, WHICH transport fills it:
//
//     (subject, kind, need) ──FetchRoutes.For──▶ [ Metadata 10 · Metadata 185 · Metadata 222 · … ]   ← ONE mixed-kind
//                                                 [ Pathfinder queryArtistOverview ]                  POST per uri,
//                                                 [ Spclient artist-top-tracks-extensions ]           the rest per subject
//                                   sealed = need & ~served                                          ← nobody fills these
//
//     (edge, offset)      ──FetchRoutes.ForEdge──▶ one route: the endpoint a relation's children come from
//
// THE RULES THE TABLE IS WRITTEN UNDER:
//   · A route names what it FILLS (`Groups`, every group its decoder marks known) and what JUSTIFIES sending it
//     (`Primary`). A route is taken when the need still has one of its primary groups unserved; what it fills is then
//     served. So an Identity-only ask of an artist is one kind-8 POST, and an Overview ask is the pathfinder query
//     ALONE — its answer carries Identity too, and a kind-8 POST beside it would be a second round trip for nothing.
//     Routes are listed most-capable first for exactly that reason.
//   · `Metadata` routes of one batch ride ONE BatchedEntityRequest, every kind under one EntityRequest per uri — the
//     envelope is keyed (kind, uri) and splitting a uri's kinds across two POSTs is how a half-answered row reads as
//     answered (Spotify.Api.cs §4). `ExtensionKinds` is that list, deduped.
//   · `Pathfinder` and `Spclient` routes are dispatched PER SUBJECT (one uri each): they are not batch endpoints.
//   · A group no route fills is SEALED: the planner has already marked it asked (`Table.Asked`), and the provider
//     answers the batch without it. That is an answer ("this build has no transport for it"), not a failure — a failure
//     would un-ask the row and the next mount would ask again, forever.
//   · Pure, static, allocation-free: the tables are `static readonly` arrays and the walk writes into a caller span.

namespace Wavee;

/// <summary>Which table a batch's rows live in, beyond their <see cref="EntityKind"/> (G-041). The four synthetic
/// tables all answer <see cref="EntityKind.Unknown"/>; this is what tells the planner's buckets and the provider's
/// routes apart. <see cref="Edge"/> is the relation door: the rows are PARENTS and <see cref="FetchBatch.Edge"/> names
/// the relation.</summary>
public enum FetchSubject : byte
{
    /// <summary>A row of one of the eight entity tables; <see cref="FetchBatch.Kind"/> names it.</summary>
    Entity = 0,
    /// <summary>A Home feed subject (<c>wavee:home[:&lt;facet&gt;]</c>). The facet is the uri's tail.</summary>
    Home = 1,
    /// <summary>A Home band fetched on its own — the "Show all" drill (<c>homeSection</c>).</summary>
    HomeSection = 2,
    /// <summary>A Browse band fetched on its own (<c>browseSection</c>).</summary>
    BrowseSection = 3,
    /// <summary>A search subject (<c>wavee:search:&lt;facet NN&gt;:&lt;query&gt;</c>).</summary>
    Search = 4,
    /// <summary>The Browse directory (<c>wavee:browse</c>, <c>browseAll</c>).</summary>
    BrowseDirectory = 5,
    /// <summary>One browse node (<c>spotify:page:&lt;id&gt;</c>, <c>browsePage</c>).</summary>
    BrowsePage = 6,
    /// <summary>The parents of a relation request (<c>Entities.EnsureEdge</c>).</summary>
    Edge = 7,
}

/// <summary>THE RELATIONS THE EDGE DOOR CAN ASK FOR (G-042) — each one a parent table, an <see cref="EdgeTable{TEdge}"/>
/// and a route (<see cref="FetchRoutes.ForEdge"/>). A relation that arrives only as a side effect of a row answer (an
/// album's artists, a track's tags) is not here: it is asked by asking for the row's group.</summary>
public enum FetchEdge : byte
{
    None = 0,
    // the account's library (parent = the account's own user row, Scope.MeSlot)
    /// <summary>The rootlist marker stream (<c>Edges.Rootlist</c>).</summary>
    Rootlist,
    Liked, SavedAlbums, FollowedArtists, SavedShows, Pins,
    /// <summary>The recents snapshot (<c>Edges.Recents</c> / <c>RecentsMembers</c>).</summary>
    Recents,
    /// <summary>The friend-activity seed (<c>Edges.Friends</c>).</summary>
    Friends,
    // containers
    PlaylistTracks,
    AlbumTracks, AlbumRecommendations, AlbumMerch, AlbumMoreBy,
    ArtistPopular, ArtistRelated, ArtistReleases,
    ShowEpisodes,
    // the track drawer's traits (parent = track)
    TrackCredits, TrackVersions, TrackWaveform,
    // synthetic subjects' children
    /// <summary>A Home feed's bands (<c>Edges.HomeSection</c>).</summary>
    HomeSections,
    /// <summary>A Home band's cards, paged (<c>Edges.SectionCards</c>, <c>homeSection</c>).</summary>
    HomeSectionCards,
    /// <summary>A Browse band's cards, paged (<c>Edges.SectionCards</c>, <c>browseSection</c>).</summary>
    BrowseSectionCards,
    /// <summary>One search facet's hits, paged (<c>Edges.SearchResult</c>).</summary>
    SearchResults,
    /// <summary>The Browse directory's category tiles (<c>Edges.BrowseDirectory</c>).</summary>
    BrowseCategories,
    /// <summary>A browse page's bands, paged by section (<c>Edges.BrowseSections</c>).</summary>
    BrowseSections,
}

/// <summary>The transport family a route runs on.</summary>
public enum RouteTransport : byte
{
    None = 0,
    /// <summary>One extension kind on the extended-metadata POST (<see cref="FetchRoute.Extension"/>).</summary>
    Metadata = 1,
    /// <summary>One persisted pathfinder query (<see cref="FetchRoute.Op"/>).</summary>
    Pathfinder = 2,
    /// <summary>One spclient REST route (<see cref="FetchRoute.Rest"/>).</summary>
    Spclient = 3,
}

/// <summary>The pathfinder operations a route can name. The Api maps each to its persisted-query row
/// (<c>Spotify.Api.Queries</c>); an op with no row there is a route that answers with nothing and seals its groups.</summary>
public enum PathfinderOp : byte
{
    None = 0,
    GetAlbum, GetTrack, ArtistOverview, Discography,
    /// <summary><c>fetchPlaylist</c> — the playlist header's accent, daylist and chart facts (ch 06 §7). NOT in the
    /// Api's persisted-query table today (REPORTED, G-040): until it is, the four groups it serves seal.</summary>
    FetchPlaylist,
    Home, HomeSection, BrowseAll, BrowsePage, BrowseSection,
    /// <summary>The facet's own search operation; the facet is the subject uri's <c>NN</c>.</summary>
    Search, SearchGenres, SearchSuggestions,
    Concert, AlbumMerch,
}

/// <summary>The spclient REST routes a route can name.</summary>
public enum SpclientRoute : byte
{
    None = 0,
    /// <summary><c>/playlist/v2/playlist/&lt;id&gt;</c> with decorations (header, capabilities, a page of items).</summary>
    PlaylistRead,
    /// <summary><c>/playlist-permission/v1/playlist/&lt;id&gt;/permission/base</c>.</summary>
    PermissionBase,
    /// <summary><c>/popcount/v2/playlist/&lt;id&gt;/count</c>.</summary>
    Popcount,
    /// <summary><c>/content-filter/v1/liked-songs</c>.</summary>
    LikedContentFilters,
    /// <summary><c>/artistplaycontext/v1/page/spotify/artist-top-tracks-extensions/&lt;uri&gt;</c>.</summary>
    ArtistTopTracksExtended,
    /// <summary><c>/playlist/v2/user/&lt;u&gt;/rootlist?decorate=revision,attributes,length,owner,capabilities,picture</c>.</summary>
    Rootlist,
    /// <summary>The collection-v2 page for one set (<see cref="FetchRoutes.LibrarySetOf"/>).</summary>
    CollectionPage,
    /// <summary><c>/playlist/v2/list/recents/page</c> (and its <c>/diff</c> once a revision is held).</summary>
    Recents,
    /// <summary><c>/presence-view/v2/init-friend-feed/&lt;connection&gt;</c>.</summary>
    FriendFeed,
}

/// <summary>ONE ROUTE: a transport, the groups it fills, and the groups that justify sending it. Exactly one of
/// <see cref="Extension"/> / <see cref="Op"/> / <see cref="Rest"/> is meaningful, by <see cref="Transport"/>.</summary>
public readonly record struct FetchRoute(RouteTransport Transport, uint Primary, uint Groups, int Extension,
                                         PathfinderOp Op, SpclientRoute Rest)
{
    public static FetchRoute Metadata(int extension, uint groups, uint primary = 0)
        => new(RouteTransport.Metadata, primary == 0 ? groups : primary, groups, extension, PathfinderOp.None, SpclientRoute.None);

    public static FetchRoute Pathfinder(PathfinderOp op, uint groups, uint primary = 0)
        => new(RouteTransport.Pathfinder, primary == 0 ? groups : primary, groups, 0, op, SpclientRoute.None);

    public static FetchRoute Spclient(SpclientRoute rest, uint groups, uint primary = 0)
        => new(RouteTransport.Spclient, primary == 0 ? groups : primary, groups, 0, PathfinderOp.None, rest);
}

/// <summary>THE ROUTING TABLE — the input contract <c>Spotify.Api</c>'s fetch provider codes against (batch B1b). Pure,
/// static and allocation-free; see the file header for the rules.</summary>
public static class FetchRoutes
{
    /// <summary>More routes than any one need can select — the stack span a caller sizes with.</summary>
    public const int MaxRoutes = 8;

    // ── the extension kinds (extension_kind.proto; the numbers ARE the protocol) ──
    public const int TrackV4 = 10, EpisodeV4 = 12, AlbumV4 = 9, ArtistV4 = 8, ShowV4 = 11, ListMetadataV2 = 205;
    public const int TrackDescriptor = 6, VideoAssociations = 99, AudioAssociations = 98, PreRelease = 138;
    public const int PlayCount = 185, CreditsV2 = 186, AudioAttributes = 222, ThreeBandWaveforms = 237;
    public const int UserProfile = 15, RecommendedPlaylists = 151;

    // ── the entity tables' routes, most capable first ──

    static readonly FetchRoute[] s_track =
    [
        FetchRoute.Metadata(TrackV4, (uint)(TrackFields.Identity | TrackFields.Year | TrackFields.Availability
                                           | TrackFields.Isrc | TrackFields.Canonical)),
        FetchRoute.Metadata(PlayCount, (uint)TrackFields.PlayCount),
        FetchRoute.Metadata(AudioAttributes, (uint)TrackFields.Audio),
        FetchRoute.Metadata(TrackDescriptor, (uint)TrackFields.Tags),
        FetchRoute.Metadata(VideoAssociations, (uint)TrackFields.Video),
        // The ladder: kind 5 on the DERIVED spotify:audio: entity, in its own batch (FetchBatch.Extension == 5).
        FetchRoute.Metadata(Fetch.AudioFilesKind, (uint)TrackFields.Files),
        // TrackFields.Publishing has NO route, deliberately: kind 183 is an ALBUM trait (0.2.9 PublishingProjector,
        // "albums only") and the track payload is date-only. Asking a track seals it.
    ];

    static readonly FetchRoute[] s_episode =
    [
        FetchRoute.Metadata(EpisodeV4, (uint)(EpisodeFields.Identity | EpisodeFields.About)),
        // Progress: no transport in this build (the resume position is a playback-state fact). Seals.
    ];

    static readonly FetchRoute[] s_album =
    [
        FetchRoute.Metadata(AlbumV4, (uint)(AlbumFields.Identity | AlbumFields.Release | AlbumFields.Publishing)),
        FetchRoute.Metadata(PreRelease, (uint)(AlbumFields.Availability | AlbumFields.PreReleaseLink)),
        // TopTrack is DERIVED at commit from the tracks' play counts (Album.DeriveTopTrack); it has no request.
    ];

    static readonly FetchRoute[] s_artist =
    [
        // The overview answer carries identity too, so it is listed first and only its own groups justify it.
        FetchRoute.Pathfinder(PathfinderOp.ArtistOverview,
            (uint)(ArtistFields.Overview),
            primary: (uint)(ArtistFields.Header | ArtistFields.Stats | ArtistFields.Bio | ArtistFields.Pick
                          | ArtistFields.PreRelease | ArtistFields.Latest | ArtistFields.Tour)),
        FetchRoute.Metadata(ArtistV4, (uint)ArtistFields.Identity),
        FetchRoute.Spclient(SpclientRoute.ArtistTopTracksExtended, (uint)ArtistFields.Chart),
    ];

    static readonly FetchRoute[] s_playlist =
    [
        // The v2 read is the one answer that carries the capability block; justified by it alone.
        FetchRoute.Spclient(SpclientRoute.PlaylistRead,
            (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities | PlaylistFields.Format),
            primary: (uint)PlaylistFields.Capabilities),
        // The detail page's own facts justify the per-playlist pathfinder read; the ACCENT alone does not — it is the
        // cold-start colour a card already has from the home / search / library answer it came in, and a sidebar asking
        // `PlaylistFields.Row` must stay one batchable kind-205 POST, not four hundred per-subject reads. Asked alone it
        // seals.
        FetchRoute.Pathfinder(PathfinderOp.FetchPlaylist,
            (uint)(PlaylistFields.Identity | PlaylistFields.Accent | PlaylistFields.Daylist | PlaylistFields.Chart
                 | PlaylistFields.Tuning),
            primary: (uint)(PlaylistFields.Daylist | PlaylistFields.Chart | PlaylistFields.Tuning)),
        FetchRoute.Metadata(ListMetadataV2, (uint)(PlaylistFields.Identity | PlaylistFields.Format)),
        FetchRoute.Spclient(SpclientRoute.PermissionBase, (uint)PlaylistFields.Visibility),
        FetchRoute.Spclient(SpclientRoute.Popcount, (uint)PlaylistFields.Saves),
    ];

    static readonly FetchRoute[] s_show =
    [
        FetchRoute.Metadata(ShowV4, (uint)(ShowFields.Identity | ShowFields.About)),
    ];

    static readonly FetchRoute[] s_user =
    [
        // Kind 15 first; the REST profile (`/user-profile-view/v3/profile/<u>`) is the PROVIDER's fallback for the
        // users kind 15 left unanswered — a retry policy inside one route, not a second route (0.2.9's two-arm fetch).
        FetchRoute.Metadata(UserProfile, (uint)UserFields.Identity),
        FetchRoute.Spclient(SpclientRoute.LikedContentFilters, (uint)UserFields.ContentFilters),
        // Social: no transport carries follower counts in this build. Seals.
    ];

    static readonly FetchRoute[] s_concert =
    [
        FetchRoute.Pathfinder(PathfinderOp.Concert, (uint)ConcertFields.All),
    ];

    // ── the synthetic subjects' routes ──

    static readonly FetchRoute[] s_home = [FetchRoute.Pathfinder(PathfinderOp.Home, (uint)HomeFields.All)];
    static readonly FetchRoute[] s_homeSection = [FetchRoute.Pathfinder(PathfinderOp.HomeSection, (uint)SectionFields.All)];
    static readonly FetchRoute[] s_browseSection = [FetchRoute.Pathfinder(PathfinderOp.BrowseSection, (uint)SectionFields.All)];
    static readonly FetchRoute[] s_browseDirectory = [FetchRoute.Pathfinder(PathfinderOp.BrowseAll, (uint)BrowseFields.All)];
    static readonly FetchRoute[] s_browsePage = [FetchRoute.Pathfinder(PathfinderOp.BrowsePage, (uint)BrowseFields.All)];

    static readonly FetchRoute[] s_search =
    [
        FetchRoute.Pathfinder(PathfinderOp.Search, (uint)(SearchFields.Chips | SearchFields.Results)),
        FetchRoute.Pathfinder(PathfinderOp.SearchGenres, (uint)SearchFields.Genres),
        FetchRoute.Pathfinder(PathfinderOp.SearchSuggestions, (uint)SearchFields.Related),
    ];

    /// <summary>Every route a (subject, kind) has, most capable first. Empty for a table nothing fetches.</summary>
    public static ReadOnlySpan<FetchRoute> Of(FetchSubject subject, EntityKind kind) => subject switch
    {
        FetchSubject.Entity => kind switch
        {
            EntityKind.Track => s_track,
            EntityKind.Episode => s_episode,
            EntityKind.Album => s_album,
            EntityKind.Artist => s_artist,
            EntityKind.Playlist => s_playlist,
            EntityKind.Show => s_show,
            EntityKind.User => s_user,
            EntityKind.Concert => s_concert,
            _ => default,
        },
        FetchSubject.Home => s_home,
        FetchSubject.HomeSection => s_homeSection,
        FetchSubject.BrowseSection => s_browseSection,
        FetchSubject.Search => s_search,
        FetchSubject.BrowseDirectory => s_browseDirectory,
        FetchSubject.BrowsePage => s_browsePage,
        _ => default,
    };

    /// <summary>THE WALK: the routes that fill <paramref name="need"/>, written into <paramref name="into"/> in table
    /// order. A route is taken while the need still has one of its <see cref="FetchRoute.Primary"/> groups unserved,
    /// and what it <see cref="FetchRoute.Groups"/> fills is then served. <paramref name="sealedGroups"/> is
    /// <c>need &amp; ~served</c> — the groups this build has no transport for, which the provider answers WITHOUT.
    /// Returns how many routes were written (at most <see cref="MaxRoutes"/>).</summary>
    public static int For(FetchSubject subject, EntityKind kind, uint need, Span<FetchRoute> into, out uint sealedGroups)
    {
        var routes = Of(subject, kind);
        uint served = 0;
        int n = 0;
        for (int i = 0; i < routes.Length && n < into.Length; i++)
        {
            ref readonly var route = ref routes[i];
            if ((need & route.Primary & ~served) == 0) continue;
            into[n++] = route;
            served |= route.Groups;
        }
        sealedGroups = need & ~served;
        return n;
    }

    /// <inheritdoc cref="For(FetchSubject,EntityKind,uint,Span{FetchRoute},out uint)"/>
    public static int For(FetchBatch batch, Span<FetchRoute> into, out uint sealedGroups)
    {
        if (batch.Subject == FetchSubject.Edge)
        {
            sealedGroups = 0;
            if (into.IsEmpty) return 0;
            into[0] = ForEdge(batch.Edge, batch.Offset);
            return into[0].Transport == RouteTransport.None ? 0 : 1;
        }
        return For(batch.Subject, batch.Kind, batch.Wanted, into, out sealedGroups);
    }

    /// <summary>The extension kinds ONE mixed-kind POST carries for this need: the <see cref="RouteTransport.Metadata"/>
    /// routes of <see cref="For(FetchSubject,EntityKind,uint,Span{FetchRoute},out uint)"/>, deduped, in table order.
    /// Every uri of the batch gets all of them under its one <c>EntityRequest</c>. Kind 5 never appears beside another
    /// kind: the planner peels Files into its own batch (its request is a different uri).</summary>
    public static int ExtensionKinds(FetchSubject subject, EntityKind kind, uint need, Span<int> into)
    {
        Span<FetchRoute> routes = stackalloc FetchRoute[MaxRoutes];
        int count = For(subject, kind, need, routes, out _);
        int n = 0;
        for (int i = 0; i < count && n < into.Length; i++)
        {
            if (routes[i].Transport != RouteTransport.Metadata) continue;
            if (into[..n].IndexOf(routes[i].Extension) >= 0) continue;
            into[n++] = routes[i].Extension;
        }
        return n;
    }

    /// <summary>THE EDGE ROUTE: where a relation's children come from, for the page at <paramref name="offset"/>.
    /// <see cref="RouteTransport.None"/> for a relation this build cannot fetch on its own.</summary>
    public static FetchRoute ForEdge(FetchEdge edge, int offset = 0) => edge switch
    {
        FetchEdge.Rootlist => FetchRoute.Spclient(SpclientRoute.Rootlist, 0),
        FetchEdge.Liked or FetchEdge.SavedAlbums or FetchEdge.FollowedArtists or FetchEdge.SavedShows
            or FetchEdge.Pins => FetchRoute.Spclient(SpclientRoute.CollectionPage, 0),
        FetchEdge.Recents => FetchRoute.Spclient(SpclientRoute.Recents, 0),
        FetchEdge.Friends => FetchRoute.Spclient(SpclientRoute.FriendFeed, 0),
        FetchEdge.PlaylistTracks => FetchRoute.Spclient(SpclientRoute.PlaylistRead, 0),
        // AlbumV4 carries every disc row in one answer; only a page past it (a 300-track box set through getAlbum's
        // own paging) goes to the pathfinder.
        FetchEdge.AlbumTracks => offset <= 0 ? FetchRoute.Metadata(AlbumV4, 0) : FetchRoute.Pathfinder(PathfinderOp.GetAlbum, 0),
        FetchEdge.AlbumRecommendations => FetchRoute.Metadata(RecommendedPlaylists, 0),
        FetchEdge.AlbumMerch => FetchRoute.Pathfinder(PathfinderOp.AlbumMerch, 0),
        FetchEdge.AlbumMoreBy => FetchRoute.Pathfinder(PathfinderOp.GetAlbum, 0),
        FetchEdge.ArtistPopular or FetchEdge.ArtistRelated => FetchRoute.Pathfinder(PathfinderOp.ArtistOverview, 0),
        FetchEdge.ArtistReleases => FetchRoute.Pathfinder(PathfinderOp.Discography, 0),
        FetchEdge.ShowEpisodes => FetchRoute.Metadata(ShowV4, 0),
        FetchEdge.TrackCredits => FetchRoute.Metadata(CreditsV2, 0),
        FetchEdge.TrackVersions => FetchRoute.Metadata(AudioAssociations, 0),
        FetchEdge.TrackWaveform => FetchRoute.Metadata(ThreeBandWaveforms, 0),
        FetchEdge.HomeSections => FetchRoute.Pathfinder(PathfinderOp.Home, 0),
        FetchEdge.HomeSectionCards => FetchRoute.Pathfinder(PathfinderOp.HomeSection, 0),
        FetchEdge.BrowseSectionCards => FetchRoute.Pathfinder(PathfinderOp.BrowseSection, 0),
        FetchEdge.SearchResults => FetchRoute.Pathfinder(PathfinderOp.Search, 0),
        FetchEdge.BrowseCategories => FetchRoute.Pathfinder(PathfinderOp.BrowseAll, 0),
        FetchEdge.BrowseSections => FetchRoute.Pathfinder(PathfinderOp.BrowsePage, 0),
        _ => default,
    };

    /// <summary>The collection-v2 set behind a library relation — what <see cref="SpclientRoute.CollectionPage"/> pages
    /// (the Api's <c>WireSet</c> spells it). False for a relation that is not a collection set.</summary>
    public static bool LibrarySetOf(FetchEdge edge, out LibraryEdgeKind set)
    {
        set = edge switch
        {
            FetchEdge.SavedAlbums => LibraryEdgeKind.SavedAlbums,
            FetchEdge.FollowedArtists => LibraryEdgeKind.FollowedArtists,
            FetchEdge.SavedShows => LibraryEdgeKind.SavedShows,
            FetchEdge.Pins => LibraryEdgeKind.Pins,
            _ => LibraryEdgeKind.Liked,
        };
        return edge is FetchEdge.Liked or FetchEdge.SavedAlbums or FetchEdge.FollowedArtists or FetchEdge.SavedShows
                    or FetchEdge.Pins;
    }
}
