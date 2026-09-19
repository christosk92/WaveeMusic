// ── Entities/Fetch.Edges.cs — CORE: the edge door (a named partial of Fetch.cs; gap batch B1, G-042, G-050) ──────────
//
// Role: CORE planner + SHELL settle · Owner: C · Budget: 260 lines · Spec: gap register G-042 ("plan edges like rows")
//
// RELATIONS WERE NEVER FETCHED. Rows had a door — `Entities.Ensure(rows, groups)` — and relations had none, so
// `Edges.Rootlist/Liked/SavedAlbums/…/ArtistPopular/SectionCards` filled only as a side effect of some row answer that
// happened to carry them, and every library, liked and sidebar surface showed skeletons forever. This is the door:
//
//     Entities.EnsureEdge(FetchEdge.Rootlist, me)            ── one ask per (relation, parent, page) per scope
//        │  WasAsked? → dedupe          (EdgeTableBase.Asked — the edge twin of Table.Asked)
//        │  MarkAsked
//        ├─ DISK FIRST (wave D2): a playlist's membership, first page, still Unknown, never offered to the disk this
//        │  scope, store open ─→ MarkDiskAsked · Store.ReadList ─→ the persisted list lands through the wire's own
//        │  commit, Complete, with its revision ─→ AfterDisk: THIS parent goes to its bucket below, found or not —
//        │  and with a revision now held, `FillRevisions` makes that network ask the `/diff`
//        ▼
//     bucket (provider, Edge, parent kind, relation, offset) → the tick's Drain (Playback: now) → FetchBatch
//        │   { Subject = Edge, Edge, Offset }, rows = the PARENTS (their ids, their text); held while CanSend refuses
//        ▼
//     provider: FetchRoutes.ForEdge(edge, offset) → the endpoint → decode → Fetch.Answer / Fetch.Failed
//        │
//        ├─ Answer: the commit lands the runs; a parent whose list is STILL Unknown was answered with nothing
//        │          → MarkUnanswered (NoRoute, kept asked), so the surface shows a vacancy, not a skeleton forever
//        └─ Failed: retryable → re-queued behind the backoff with the marks kept;
//                   terminal → MarkFailed(status): un-asked (the next mount retries) and readable (G-050's banner)
//
// The same planner owns both doors because they share everything that matters: the four-request ceiling, priority,
// the tick's drain, the online gate, the scope drop (C7) and the backoff. A relation is one parent per request on every
// route there is, so an edge bucket is batched only to share that machinery; the provider walks the parents one request
// each. Within one priority a ROW bucket leaves before an EDGE bucket (`Fetch.Pump`): the row batch is what indexes the
// routes this door's dedupe reads, so a page asking its row and its relation in one tick sends the shared route once.

namespace Wavee;

public static partial class Fetch
{
    /// <summary>"This relation of these parents, this page." THE edge door (through <c>Entities.EnsureEdge</c>).
    /// Dedupes per (relation, parent, page) for the scope; <paramref name="refresh"/> forgets the parent's asks first —
    /// what a dealer push, a pull-to-refresh or the login sync wants, and never what a page mount wants.
    /// <para><b>DISK BEFORE NETWORK, for a list</b> (wave D2, plan §3.2): a playlist's membership that nothing has
    /// answered this scope is offered to the disk once (<see cref="EdgeTableBase.WasDiskAsked"/>) before anything goes
    /// out. The parent is marked asked FIRST, so the dedupe holds while the read is out; its continuation
    /// (<see cref="AfterDisk"/>) then buckets the parent for the network directly, at the original priority — past the
    /// ask mark that is by then its own. A refresh never reads the disk: it is a question for the server. A refused read
    /// (no store, a full queue) goes straight to its network bucket.</para>
    /// <para>Nothing is SENT here: the parents owe the tick's <see cref="Drain"/> (a <see cref="FetchPriority.Playback"/>
    /// ask pumps now), so a relation whose route its parent's row ask of the same tick already carries is sent once — the
    /// row leaves first and the route index drops the edge (<see cref="Pump"/>'s order).</para></summary>
    public static void PlanEdge(Scope scope, FetchEdge edge, ReadOnlySpan<int> parents, int offset, FetchPriority priority,
                                bool refresh = false)
    {
        if (edge == FetchEdge.None || parents.IsEmpty) return;
        DropStaleScope(scope);

        EdgeTableBase? edges = EdgeTableOf(scope, edge);
        Table? table = ParentTableOf(scope, edge);
        if (edges is null || table is null) return;
        if (FetchRoutes.ForEdge(edge, offset).Transport == RouteTransport.None) return;
        offset = Math.Max(0, offset);

        bool queued = false;
        for (int i = 0; i < parents.Length; i++)
        {
            int parent = parents[i];
            if (parent <= Table.None || parent >= table.Count) continue;
            s_planned++;
            if (refresh) edges.ForgetAsked(parent);
            else if (edges.WasAsked(parent, offset)) { s_deduped++; continue; }

            EntityId id = table.Id[parent];
            EntityProvider provider = ProviderFor(scope, SubjectOf(scope, table, parent), id);
            if (provider == EntityProvider.None)
            {
                // Nobody can answer for this parent. Kept asked, so the verdict is not re-derived every drain — and
                // recorded unanswered, so the surface does not show a skeleton for a list that will never come.
                edges.MarkUnanswered(parent, offset);
                s_abandoned++;
                continue;
            }
            edges.MarkAsked(parent, offset);
            if (!refresh && offset == 0 && edge is (FetchEdge.PlaylistTracks or FetchEdge.ShowEpisodes) && Store.IsOpen
                && edges.State(parent) == EdgeState.Unknown && !edges.WasDiskAsked(parent))
            {
                edges.MarkDiskAsked(parent);
                if (AskDisk(scope, edge, parent, id, priority)) { s_toDisk++; continue; }
            }
            Bucket(table, FetchSubject.Edge, provider, 0, edge, offset, priority).Add(parent, id);
            s_toNetwork++;
            queued = true;
        }
        if (queued) SendOrOwe(priority);        // the tick's drain sends it (Fetch.cs, "THE DRAIN"); Playback now
    }

    /// <summary>Offer one parent's persisted list to the disk (<see cref="Store.ReadList"/>). Its own method for the
    /// reason <see cref="Store.Read"/>'s summary gives: the continuation's closure is built at the top of the method
    /// that declares it, and here it is built only for a parent that really goes to the disk — once per playlist per
    /// session — never on <see cref="PlanEdge"/>'s every call.</summary>
    static bool AskDisk(Scope scope, FetchEdge edge, int parent, EntityId id, FetchPriority priority)
        => Store.ReadList(scope, edge == FetchEdge.ShowEpisodes ? EdgeRelation.ShowEpisodes : EdgeRelation.PlaylistTracks, id, _ => AfterDisk(scope, edge, parent, id, priority));

    /// <summary>The disk answered about one parent's list — it landed, or there was none (UI thread, from the store's
    /// post). Either way THIS parent's network leg is bucketed now for the tick's drain, bypassing the ask mark
    /// <see cref="PlanEdge"/> already set: with the list restored Complete and its revision held,
    /// <see cref="FillRevisions"/> sends that revision and the provider reads the <c>/diff</c> (plan §3.2 — the first ask
    /// after a launch revalidates); after a miss it is the full read it always was. A replaced scope or a recycled slot
    /// continues nothing (C7).</summary>
    static void AfterDisk(Scope scope, FetchEdge edge, int parent, EntityId id, FetchPriority priority)
    {
        if (!ReferenceEquals(scope, Entities.Current)) return;
        DropStaleScope(scope);
        Table? table = ParentTableOf(scope, edge);
        if (table is null || parent <= Table.None || parent >= table.Count || table.Id[parent] != id) return;
        EntityProvider provider = ProviderFor(scope, SubjectOf(scope, table, parent), id);
        if (provider == EntityProvider.None) return;
        Bucket(table, FetchSubject.Edge, provider, 0, edge, 0, priority).Add(parent, id);
        s_toNetwork++;
        SendOrOwe(priority);                    // disk reads landing in one loop pass leave as one request per shape
    }

    /// <summary>An edge batch was answered. Each parent whose list landed clears any old failure; a parent whose list is
    /// still Unknown was answered WITHOUT its relation — the route had nothing, or the decoder found nothing it could
    /// stage — and is recorded as <see cref="EdgeTableBase.NoRoute"/> while staying asked: the planner does not ask
    /// again this scope, and the surface renders a vacancy rather than a skeleton that never resolves.</summary>
    static void EdgesAnswered(FetchBatch batch)
    {
        Scope scope = Entities.Current;
        EdgeTableBase? edges = EdgeTableOf(scope, batch.Edge);
        Table? table = ParentTableOf(scope, batch.Edge);
        if (edges is null || table is null) return;
        Action<FetchEdge, int, bool>? settled = ListSettled;
        for (int i = 0; i < batch.Count; i++)
        {
            int parent = batch.Slots[i];
            if (parent <= Table.None || parent >= table.Count || table.Id[parent] != batch.Ids[i]) continue;
            if (edges.State(parent) != EdgeState.Unknown) edges.MarkAnswered(parent);
            else edges.MarkUnanswered(parent, batch.Offset);
            if (batch.Edge == FetchEdge.ShowEpisodes && edges.State(parent) == EdgeState.Complete)
                ListStamps.MarkRevalidated(table.Id[parent].Text, ListStamps.NowMs());
            settled?.Invoke(batch.Edge, parent, false);
        }
    }

    /// <summary>The answer path's report to whoever tracks a list's REVALIDATION (wave D3: <c>ListOpen.Settled</c>,
    /// installed by <c>Playlist.InstallPages</c>): (edge, parent, failed). An UNCHANGED <c>/diff</c> moves nothing in the
    /// tables, so this is the only place it becomes visible — it is what lets a page's reveal hold go the moment the diff
    /// says "unchanged" instead of at its 1 500 ms budget. UI thread; null = nobody listens (every unit test by default).</summary>
    public static Action<FetchEdge, int, bool>? ListSettled { get; set; }

    /// <summary>A terminal edge failure: un-ask each parent's page so the next mount really retries, and record the
    /// status so the mount that is showing now can say so (G-050).</summary>
    static void EdgesFailed(FetchBatch batch, Table table, int status)
    {
        EdgeTableBase? edges = EdgeTableOf(Entities.Current, batch.Edge);
        if (edges is null) return;
        Action<FetchEdge, int, bool>? settled = ListSettled;
        for (int i = 0; i < batch.Count; i++)
        {
            int parent = batch.Slots[i];
            if (parent <= Table.None || parent >= table.Count || table.Id[parent] != batch.Ids[i]) continue;
            edges.MarkFailed(parent, batch.Offset, status);
            settled?.Invoke(batch.Edge, parent, true);
        }
    }

    /// <summary>The relation's table in <paramref name="scope"/> — what the door marks and the surface reads.</summary>
    public static EdgeTableBase? EdgeTableOf(Scope scope, FetchEdge edge)
    {
        Edges e = scope.Edges;
        return edge switch
        {
            FetchEdge.Rootlist => e.Rootlist,
            FetchEdge.Liked => e.Liked,
            FetchEdge.SavedAlbums => e.SavedAlbums,
            FetchEdge.FollowedArtists => e.FollowedArtists,
            FetchEdge.SavedShows => e.SavedShows,
            FetchEdge.Pins => e.Pins,
            FetchEdge.Recents => e.Recents,
            FetchEdge.Friends => e.Friends,
            FetchEdge.PlaylistTracks => e.PlaylistTracks,
            FetchEdge.AlbumTracks => e.AlbumTracks,
            FetchEdge.AlbumRecommendations => e.AlbumRecommendations,
            FetchEdge.AlbumMerch => e.AlbumMerch,
            FetchEdge.AlbumMoreBy => e.AlbumMoreBy,
            FetchEdge.AlbumSimilar => e.AlbumSimilar,
            FetchEdge.ArtistPopular => e.ArtistPopular,
            FetchEdge.ArtistRelated => e.ArtistRelated,
            FetchEdge.ArtistReleases => e.ArtistReleases,
            FetchEdge.ShowEpisodes => e.ShowEpisodes,
            FetchEdge.TrackCredits => e.TrackCredits,
            FetchEdge.TrackVersions => e.TrackVersions,
            FetchEdge.TrackWaveform => e.TrackWaveform,
            FetchEdge.HomeSections => e.HomeSection,
            FetchEdge.HomeSectionCards or FetchEdge.BrowseSectionCards => e.SectionCards,
            FetchEdge.SearchResults => e.SearchResult,
            FetchEdge.BrowseCategories => e.BrowseDirectory,
            FetchEdge.BrowseSections => e.BrowseSections,
            FetchEdge.ArtistAlbums => e.ArtistAlbums,
            FetchEdge.ArtistSingles => e.ArtistSingles,
            FetchEdge.ArtistCompilations => e.ArtistCompilations,
            FetchEdge.ArtistConcerts => e.ArtistConcerts,
            _ => null,
        };
    }

    /// <summary>The table a relation's PARENTS live in.</summary>
    public static Table? ParentTableOf(Scope scope, FetchEdge edge) => edge switch
    {
        FetchEdge.Rootlist or FetchEdge.Liked or FetchEdge.SavedAlbums or FetchEdge.FollowedArtists
            or FetchEdge.SavedShows or FetchEdge.Pins or FetchEdge.Recents or FetchEdge.Friends => scope.Users,
        FetchEdge.PlaylistTracks => scope.Playlists,
        FetchEdge.AlbumTracks or FetchEdge.AlbumRecommendations or FetchEdge.AlbumMerch or FetchEdge.AlbumMoreBy or FetchEdge.AlbumSimilar => scope.Albums,
        FetchEdge.ArtistPopular or FetchEdge.ArtistRelated or FetchEdge.ArtistReleases or FetchEdge.ArtistAlbums
            or FetchEdge.ArtistSingles or FetchEdge.ArtistCompilations or FetchEdge.ArtistConcerts => scope.Artists,
        FetchEdge.ShowEpisodes => scope.Shows,
        FetchEdge.TrackCredits or FetchEdge.TrackVersions or FetchEdge.TrackWaveform => scope.Tracks,
        FetchEdge.HomeSections => scope.Homes,
        FetchEdge.HomeSectionCards or FetchEdge.BrowseSectionCards => scope.Sections,
        FetchEdge.SearchResults => scope.Searches,
        FetchEdge.BrowseCategories or FetchEdge.BrowseSections => scope.Browses,
        _ => null,
    };
}

public static partial class Entities
{
    /// <summary>"This relation of these parents, this page, this urgency" — the edge door (G-042). The relation twin of
    /// <see cref="Ensure(Table,ReadOnlySpan{int},uint,FetchPriority)"/>: a page demands the lists it shows, the planner
    /// dedupes them for the scope and batches them with every other request (P4). Paging is an offset: the next page of
    /// a partial list is <c>EnsureEdge(edge, parent, offset: relation.Count(parent))</c>.</summary>
    public static void EnsureEdge(FetchEdge edge, ReadOnlySpan<int> parents, int offset = 0,
                                  FetchPriority priority = FetchPriority.Visible)
        => Fetch.PlanEdge(Current, edge, parents, offset, priority);

    /// <inheritdoc cref="EnsureEdge(FetchEdge,ReadOnlySpan{int},int,FetchPriority)"/>
    public static void EnsureEdge(FetchEdge edge, int parent, int offset = 0, FetchPriority priority = FetchPriority.Visible)
        => Fetch.PlanEdge(Current, edge, new ReadOnlySpan<int>(in parent), offset, priority);

    /// <summary>Ask a relation AGAIN, whatever was asked before — a dealer push, the login-time library sync, a
    /// pull-to-refresh. The rows already there keep rendering until the answer replaces them.</summary>
    public static void RefreshEdge(FetchEdge edge, int parent, FetchPriority priority = FetchPriority.Visible)
        => Fetch.PlanEdge(Current, edge, new ReadOnlySpan<int>(in parent), 0, priority, refresh: true);

    /// <summary>A relation the parent HAS belongs to an ended edition: forget the ask and fetch the first page again
    /// while the rows already there keep rendering — the edge twin of
    /// <see cref="Invalidate(Table,ReadOnlySpan{int},uint,FetchPriority)"/>. A relation still
    /// <see cref="EdgeState.Unknown"/> has nothing to invalidate and is left to its page's own
    /// <see cref="EnsureEdge(FetchEdge,int,int,FetchPriority)"/>. Never <c>Clear</c>: that renders a skeleton.</summary>
    public static void InvalidateEdge(FetchEdge edge, int parent, FetchPriority priority = FetchPriority.Prefetch)
    {
        EdgeTableBase? edges = Fetch.EdgeTableOf(Current, edge);
        if (edges is null || edges.State(parent) == EdgeState.Unknown) return;
        Fetch.PlanEdge(Current, edge, new ReadOnlySpan<int>(in parent), 0, priority, refresh: true);
    }
}
