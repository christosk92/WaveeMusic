using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Catalog;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Route-scoped detail presentation. The canonical query owns data/freshness; KeepAlive retains view state and
// parks the subscription's demand until this route becomes active again.
sealed class DetailPage : Component
{
    readonly Signal<Route> _route;   // the (per-pane) navigation route, read reactively so ONE instance serves successive detail pages
    public DetailPage(Signal<Route> route) { _route = route; }

    readonly Signal<IQuerySignalBinding?> _query = new(null);
    Image? _lastCover;

    // Route → (kind, id): album:/pl: carry the uri after the prefix; "liked" is the saved-tracks collection.
    internal static (DetailKind Kind, string? Id) ParseDetail(Route r) =>
        r.Name.StartsWith("album:", StringComparison.Ordinal) ? (DetailKind.Album, r.Name["album:".Length..])
        // Same kind, same config, same shell — only the id needs resolving before the load can read it.
        : r.Name.StartsWith("prerelease:", StringComparison.Ordinal) ? (DetailKind.Album, r.Name["prerelease:".Length..])
        : r.Name.StartsWith("pl:", StringComparison.Ordinal) ? (DetailKind.Playlist, r.Name["pl:".Length..])
        // W7: the "local" route (the Local Files collection page) is retired — no arm maps to it any more; LocalSource
        // itself still owns the `wavee:local:*` uri space for local FILE playback, which is a different, live feature.
        : r.Name.StartsWith("show:", StringComparison.Ordinal) ? (DetailKind.Show, r.Name["show:".Length..])
        : (DetailKind.Liked, null);

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        if (svc is null) return new BoxEl { Grow = 1f };
        var navPreview = UseContext(NavPreviewStore.Slot);
        // The create lifecycle, for the notice rule below: an optimistic create that is still riding the outbox must not
        // be reported as a deletion, and one the server REJECTED must be reported as exactly that.
        var lib = UseContext(LibraryBridge.Slot);

        // Subscribe the RAW route → re-render when navigation swaps the detail route in place. A nav to ANOTHER page
        // class (artist:/home) also writes this signal, but the reconciler's structural-effect ordering guarantees the
        // KeepAlive boundary parks this page before its render effect drains, so no stale cross-class render happens
        // (engine: ReactiveRuntime.Flush park-before-render; gate.reconciler.park-before-render).
        var route = _route.Value;
        var (kind, id) = ParseDetail(route);

        // Preview identity is route-scoped so a card's already-known header data can appear immediately while the full
        // model loads. It is deliberately not used as a shared-element/connected-animation key.
        string previewKey = route.Name;

        // The PARTIAL model the Home card already had (cover/title/artist) — optional: deep links / search have none.
        var preview = UseMemo(() => navPreview?.Take(previewKey), previewKey);
        var model = UseLoadable(Loadable<DetailModel>.Pending(preview ?? PendingSeed(kind)));
        var revealWatch = PageRevealWatch.Use(Context, () => model.IsReady, kind + "Detail");
        var scope = svc.CatalogScope;
        var post = UsePost();
        var active = UseIsActive();
        UseEffect(() =>
        {
            _lastCover = null;
            model.SetPending(preview ?? PendingSeed(kind));
            var cancellation = new CancellationTokenSource();
            void Publish<T>(QuerySnapshot<T> snapshot, DetailModel mapped)
            {
                if (snapshot.Revision == 0 || cancellation.IsCancellationRequested || _route.Peek().Name != route.Name) return;
                if (snapshot.Failure is { } failure)
                {
                    if (!model.IsReady) model.SetFailed(new InvalidOperationException(failure.Message));
                    return;
                }
                // This page owns readiness: publishing its mapped model explicitly reveals the page, and it does so
                // once, when the membership and every row identity the initial window asked for have landed
                // (DetailPageReadiness). A list that reveals on membership alone paints each row as its identity
                // arrives; a partly filled list reads worse than a skeleton that lifts once.
                bool reveal = !model.IsReady;
                if (reveal && !DetailPageReadiness.InitialLoadComplete(snapshot)) return;
                var next = WithNotice(kind, model, mapped, lib, id);
                var current = model.Value.Peek();
                next = next with { Cover = ImageSource.PreferVisible(next.Cover, current.Cover ?? _lastCover) };
                if (next.Accent == 0 && preview is { Accent: not 0u }) next = next with { Accent = preview.Accent };
                // An active drag retains its insertion geometry until drop; this is interaction state.
                if (kind == DetailKind.Playlist && PlaylistReorderDefer.TryHold(model, next, id)) return;
                model.SetReady(next);
                _lastCover = next.Cover;
            }
            void Attach<T>(QuerySpec<T> specification, Func<T, Func<string, bool>, DetailModel> map)
            {
                if (cancellation.IsCancellationRequested) return;
                // `map` — MapPlaylist/MapAlbum/MapShow/MapLiked, which walk every track — runs inside the binding's
                // projection, on the POOL; Publish only does the cheap UI-state merges below.
                DetailModel? previousProjection = null;
                // The has-video roll-up (which decides whether the table keeps a Video lane at all) is read from THIS
                // publication's video-association facts, never by probing the catalog repository per track: that probe
                // took the repository's gate — the one a whole-membership join holds for its entire duration — once for
                // every one of a 1,494-row playlist's tracks. The two planes that do not travel on a publication (a
                // user attachment, a module's own verdict) fold in through VideoPresence.HasVideoOutsideCatalog.
                DetailModel Project(QuerySnapshot<T> snapshot)
                {
                    var facts = new PublicationTrackFacts(snapshot.Facts, scope, svc.Data.ProviderForSubject,
                        VideoPresence.HasVideoOutsideCatalog);
                    var next = map(snapshot.Value, facts.HasVideo);
                    if (previousProjection is { } previous && LikedFactsRules.TracksEquivalent(previous.Tracks, next.Tracks))
                        next = next with { Tracks = previous.Tracks };
                    previousProjection = next;
                    return next;
                }
                var binding = QuerySignalBinding<T, DetailModel>.OverSnapshot(svc.Queries.Acquire(specification), post, Project,
                    (s, mapped) => Publish(s, mapped), failed: error => { if (!model.IsReady) model.SetFailed(error); });
                _query.Value = binding;
                binding.SetActive(active.Peek());
            }
            switch (kind)
            {
                case DetailKind.Playlist:
                    Attach(new PlaylistDetailQuery(scope, PlaylistUri(id ?? "")),
                        (p, hasVideo) => MapPlaylist(p, membershipLoaded: p.MembershipLoaded, hasVideo: hasVideo));
                    break;
                case DetailKind.Show:
                    Attach(new ShowDetailQuery(scope, id ?? ""), static (s, _) => MapShow(s));
                    break;
                case DetailKind.Liked:
                    Attach(new LikedSongsQuery(scope), static (t, hasVideo) => MapLiked(t, hasVideo));
                    break;
                default:
                    if (PreReleaseUris.IsPreRelease(id ?? "")) _ = ResolvePreRelease();
                    else Attach(new AlbumDetailQuery(scope, id ?? ""), static (a, hasVideo) => MapAlbum(a, hasVideo: hasVideo));
                    break;
            }
            async Task ResolvePreRelease()
            {
                try
                {
                    var link = await svc.PreRelease.ResolveAsync(id!, cancellation.Token).ConfigureAwait(false);
                    post(() =>
                    {
                        if (cancellation.IsCancellationRequested) return;
                        if (link is null) model.SetFailed(new InvalidOperationException("This release is not available."));
                        else Attach(new AlbumDetailQuery(scope, link.AlbumUri), (a, hasVideo) => MapAlbum(a, link, hasVideo));
                    });
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception error) { post(() => { if (!cancellation.IsCancellationRequested) model.SetFailed(error); }); }
            }
            return (Action?)(() =>
            {
                cancellation.Cancel();
                cancellation.Dispose();
                _query.Peek()?.Dispose();
                _query.Value = null;
            });
        }, DepKey.From(HashCode.Combine(route.Name, scope)));
        UseActivation(onActivated: () => _query.Peek()?.SetActive(true),
            onDeactivated: () => _query.Peek()?.SetActive(false));

        // No data at click (deep link): Skel.Region derives the full-page shimmer from the real responsive shell rendered
        // against PendingSeed(kind). The plain Grow=1 wrapper gives the boundary synchronous layout participation.
        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            Children =
            [
                Skel.Region(
                    model,
                    onFailed: () => ErrorState.Build(model.Error),
                    // Pass the SHARED loadable (Ready when content runs), not a fresh Loadable.Ready(m): the shell is REUSED
                    // across detail routes, so it must read the one re-driven loadable — a per-render wrapper would leave the
                    // reused shell pinned to the first album's value.
                    content: _ => PageRevealWatch.Include(revealWatch, model.IsReady, new BoxEl
                    {
                        Grow = 1f, Direction = 0,
                        Children =
                        [
                            Ctx.Provide(QueryView.Slot, _query,
                                Embed.Comp(() => new DetailShell(_route, model, settings: svc.Settings))
                                    with { DeriveRenderedOutput = true }),
                        ],
                    }),
                    reveal: SkelReveal.FadeOnly,
                    smoothResize: false),
            ],
        };
    }

    /// <summary>Fold the live-refresh verdict into the model that is about to be published. Playlists carry the
    /// deleted/revoked/create verdicts; albums carry exactly one — <see cref="DetailNotice.MinifiedAlbum"/> — and
    /// Liked/Show carry none (nothing can be deleted, revoked, or minified under the reader there).
    /// <para>The CONTENT is deliberately kept when the verdict is <see cref="DetailNotice.Deleted"/>: the reload that
    /// found nothing is exactly the moment the user is looking at the rows, and replacing them with an empty page (or a
    /// skeleton) both loses their place and says less than the notice strip does.</para></summary>
    static DetailModel WithNotice(DetailKind kind, Loadable<DetailModel> model, DetailModel fresh,
                                 LibraryBridge? lib, string? uri)
    {
        // ALBUM: recomputed from the model's OWN rows on every projection — MapAlbum stamps the cold open, and this
        // arm keeps the live pump honest (a store change re-projects through here, so the notice clears in place the
        // frame the TrackV4 repair fills the names). No probe, no store read: the tracklist in hand is the fact.
        if (kind == DetailKind.Album) return fresh;
        if (kind != DetailKind.Playlist) return fresh;
        var cur = model.Value.Peek();
        bool freshIsNull = string.IsNullOrEmpty(fresh.ContextUri);
        // A create the server REJECTED is terminal and wins outright: the optimistic row has already been rolled back,
        // so every later reload finds nothing, and letting the ordinary rule speak would relabel "couldn't be created"
        // as "was deleted" — a different, and wrong, story about a playlist that never existed.
        if (uri is { Length: > 0 } created && lib is not null && lib.IsCreateFailed(created))
            return (freshIsNull && cur.ContextUri is { Length: > 0 } ? cur : fresh) with { Notice = DetailNotice.CreateFailed };
        var notice = PlaylistPageNoticeRules.Next(
            cur.Notice, freshIsNull, fresh.DeletedByOwner,
            capabilitiesKnown: freshIsNull || fresh.Capabilities.Known,
            canView: freshIsNull || fresh.Capabilities.CanView,
            isOwner: freshIsNull || fresh.Capabilities.IsOwner,
            // While the create is still riding the outbox the server has genuinely never heard of this playlist, so
            // "it is not there" is the EXPECTED state and must not be reported as a deletion.
            isCreatePending: uri is { Length: > 0 } pending && lib is not null && lib.IsCreatePending(pending));
        if (notice == DetailNotice.Deleted
            && (LoadState)model.State.Peek() == LoadState.Ready
            && cur.ContextUri is { Length: > 0 })
            return cur with { Notice = notice, DeletedByOwner = true };
        return fresh with { Notice = notice };
    }

    // Album cfg is release-kind-dependent (single = one-track layout, compilation = various-artists rows); playlist/liked fixed.
    internal static DetailConfig ResolveConfig(DetailKind kind, DetailModel m) => kind switch
    {
        DetailKind.Playlist => DetailConfig.Playlist,
        DetailKind.Liked => DetailConfig.Liked,
        DetailKind.Show => DetailConfig.Show,
        _ => m.ReleaseKind switch
        {
            AlbumKind.Single => DetailConfig.Single,
            AlbumKind.Compilation => DetailConfig.Compilation,
            _ => DetailConfig.Album,   // Album + EP share the album layout
        },
    };

    // Representative DATA for content(seed) derivation. Eight blank records give the real track/episode components a
    // useful viewport shape without encoding any playlist length (1494, 1600, or otherwise) into loading geometry.
    internal static DetailModel PendingSeed(DetailKind kind)
    {
        if (kind == DetailKind.Show)
        {
            var episodes = new Episode[8];
            for (int i = 0; i < episodes.Length; i++)
                episodes[i] = new Episode($"pending-episode-{i}", $"pending:episode:{i}", "", "", null,
                    180_000, DateTimeOffset.UnixEpoch);
            return DetailModel.Empty with
            {
                ContextUri = "pending:show",
                BadgeType = " ",
                MetaLine = " ",
                Episodes = episodes,
                Publisher = " ",
            };
        }

        var tracks = new Track[8];
        for (int i = 0; i < tracks.Length; i++)
            tracks[i] = new Track(
                $"pending-track-{i}", $"pending:track:{i}", "",
                Array.Empty<ArtistRef>(), new AlbumRef("", "", ""),
                180_000, false, null);

        return DetailModel.Empty with
        {
            ContextUri = kind == DetailKind.Liked ? "spotify:collection:tracks" : "pending:detail",
            BadgeType = kind == DetailKind.Album ? " " : null,
            OwnerName = kind == DetailKind.Playlist ? " " : null,
            MetaLine = " ",
            Tracks = tracks,
        };
    }

    // A podcast show folds onto the shared detail surface: rail = cover + PODCAST pill + publisher/episode-count meta +
    // description + Play/Follow; the right column renders Episodes (DetailConfig.Show.Content == Episodes → EpisodeList).
    internal static DetailModel MapShow(Show? s)
    {
        if (s is null) return DetailModel.Empty;
        var eps = s.Episodes ?? Array.Empty<Episode>();
        // The header counts what the show HAS, not what has been paged in — a 700-episode show that opened with 300
        // resident rows still says 700 episodes.
        int total = s.TotalEpisodes > eps.Count ? s.TotalEpisodes : eps.Count;
        string meta = s.Publisher + " · " + Strings.Podcast.EpisodeCount(total);
        return new DetailModel(
            Title: s.Name, Cover: s.Cover, ContextUri: s.Uri,
            BadgeType: Loc.Get(Strings.Podcast.Show), Year: null, OwnerName: null, OwnerImage: null,
            Artists: Array.Empty<ArtistRef>(), Description: s.Description, MetaLine: meta,
            Tracks: Array.Empty<Track>(), AboutArtist: null,
            Episodes: eps, Publisher: s.Publisher, TotalEpisodes: total)
        {
            // Carried through verbatim: the load-more gate is the CURSOR, not `total > eps.Count` (an episode that
            // cannot hydrate would otherwise pin the pill on screen forever). See Show.PagedThrough.
            PagedThrough = Math.Max(s.PagedThrough, eps.Count),
        };
    }

    /// <summary>The playlist route id as a uri. Ids arrive bare from the route but a full uri also flows through some
    /// call paths, so accept both rather than producing `spotify:playlist:spotify:playlist:…`.</summary>
    static string PlaylistUri(string id)
        => EntityUri.Parse(id).IsSpotify ? id : "spotify:playlist:" + id;   // "is it already a uri?" via the ONE parser

    /// <param name="hasVideo">The has-video answer for one playable, from the publication being mapped. Null only for
    /// callers that hold no publication (a pane mapping a stored entity); those fall back to the shared probe, which
    /// reads the catalog repository's lock-free published view.</param>
    static DetailModel MapPlaylist(Playlist p, long? saveCount = null, bool membershipLoaded = true,
        Func<string, bool>? hasVideo = null)
    {
        var video = hasVideo ?? VideoPresence.HasVideo;
        var tracks = p.Tracks ?? Array.Empty<Track>();
        // Data-drive the optional columns: show Date-added if any track has one, and Added-by only when the playlist is
        // collaborative (≥2 distinct contributors) — matching the reference app's "hide unless it carries signal" rule.
        bool hasDate = false, hasVideoColumn = false;
        int episodes = 0;
        var contributors = new HashSet<string>();
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].AddedAt is not null) hasDate = true;
            if (video(tracks[i].Uri)) hasVideoColumn = true;   // a user-attached mp4 also earns the Video column
            if (tracks[i].AddedBy is { } by) contributors.Add(by);
            // A playlist's membership is a set of PLAYABLES, and an episode is one (EpisodeAsTrack, design §1.5). The
            // header counts what is actually in there instead of calling every row a song.
            if (EntityUri.KindOf(tracks[i].Uri) == EntityKind.Episode) episodes++;
        }
        // "50 songs · 12,345 saves · 2 hr 59 min" when the count is known, else the existing two-segment line. A
        // playlist genuinely at 0 saves (a brand-new private one) also omits the segment rather than reading "0 saves".
        // MIXED content states both kinds ("48 songs · 3 episodes"): the header count is the SERVER's item total, so
        // the songs half is that total minus the episodes we joined — a songs-only playlist is byte-identical to before.
        string songs = episodes > 0
            ? Strings.Detail.SongCount(Math.Max(0, p.TrackCount - episodes)) + " · " + Strings.Podcast.EpisodeCount(episodes)
            : Strings.Detail.SongCount(p.TrackCount);
        var duration = TrackMetadataReadiness.CompleteDuration(tracks, p.TrackCount, membershipLoaded);
        string count = saveCount is > 0 and var saves ? songs + " · " + SaveCountText(saves) : songs;
        // No membership yet ⇒ no meta line at all: "0 songs · 1 min" is a count we do not have. The rail renders a
        // shimmer bar in the row's place while MembershipLoaded is false (DetailRail), so the slot is still held.
        string meta = !membershipLoaded ? ""
            : duration is { } ms ? Strings.Detail.MetaLine(count, DetailFormat.TotalTime(ms)) : count;
        LogVideoSweep("playlist", p.Uri, tracks);
        return new DetailModel(
            Title: p.Name, Cover: p.Cover, ContextUri: p.Uri,
            BadgeType: null, Year: null, OwnerName: p.OwnerName, OwnerImage: p.Owner?.Avatar,
            Artists: Array.Empty<ArtistRef>(), Description: p.Description, MetaLine: meta,
            Tracks: tracks, AboutArtist: null,
            HasDateAdded: hasDate, HasAddedBy: contributors.Count >= 2, HasVideo: hasVideoColumn,
            Capabilities: p.Capabilities,
            Collaborators: p.Collaborators,
            UserProfilesById: UserProfileMap(p),
            IsPublic: p.IsPublic,
            BasePermissionRevision: p.BasePermissionRevision,
            Tuning: p.Tuning,
            ShareUrl: SpotifyPlaylistWebUrl(p.Uri),
            ExpiresAtMs: p.DaylistExpiresAtMs, CreatedAtMs: p.DaylistCreatedAtMs,
            ChartNewEntries: p.ChartNewEntries, ChartUpdatedAtMs: p.ChartUpdatedAtMs)
        {
            // A cold open of a tombstoned / revoked uri renders the SHELL with a notice, never the error state: the
            // header and (evicted) membership are still the truest thing we can show, and "this playlist was deleted"
            // is a better answer than a generic failure page.
            DeletedByOwner = p.DeletedByOwner,
            // Known gates CanView: a thin header's all-false rights are placeholders, not a revocation.
            Notice = PlaylistPageNoticeRules.Cold(p.DeletedByOwner, p.Capabilities.Known, p.Capabilities.CanView, p.Capabilities.IsOwner),
            MembershipLoaded = membershipLoaded,
        };
    }

    // ── the per-page-open association sweep (video.assoc.page) ────────────────────────────────────────────────────────
    // Runs where the HasVideo roll-up is computed — inside the async LOAD (LoadAsync / the debounced live re-map), never on
    // a render or a frame. VideoPresence.HasVideo stays the row path's single silent boolean probe; this walks the same
    // tracks once more through the DIAGNOSTIC accessor to split the "no" into its two very different causes:
    //   noRow    — the plane holds nothing for this uri: either nobody ever requested it (a coverage hole) or the request
    //              came back with no kind-99 entry at all.
    //   negative — a row that says "no video": a real 404/empty-200 verdict, or a sealed miss cached from one.
    // The uri SAMPLE is the load-bearing field: the reported symptom ("the playlist says no, searching the same song says
    // yes") is only decidable by comparing the uri a playlist row carries against the uri the search response carried, and
    // relinked/alternative track uris are the expected way for those to differ. The app persists no alias→canonical map
    // (only the VideoProjector's canonical recovery derives one, transiently), so an "an alternate uri HAS a video"
    // count cannot be computed here without inventing a resolver — read `video.assoc.recover*` for that half instead.
    /// <summary>Info-level daylist-stale signature: PreferVisible kept the already-shown cover while the loaded title
    /// changed, OR took a new cover at all while a previous title was known — title-unchanged-with-a-new-cover is the
    /// ordinary same-identity re-decode; title-CHANGED-with-a-new-cover is the interesting case this used to miss
    /// entirely (the predicate excluded `tookIncoming && titleChanged`, so the exact moment a rolled-over identity's
    /// header and cover land TOGETHER — the daylist-rollover fix actually working — never got a log line). The Debug
    /// <c>DetailCoverTrace</c> path still owns the same-art CDN-size flash; this line is the identity mismatch.</summary>
    static void LogCoverLatch(string? previousTitle, string loadedTitle, Image? incoming, Image? visible, Image? chosen)
    {
        if (!WaveeLog.Instance.IsEnabled(WaveeLogLevel.Info)) return;
        bool sameArt = ImageSource.SameArt(incoming, visible);
        bool keptVisible = sameArt && ImageSource.IsUsable(visible);
        bool tookIncoming = ImageSource.IsUsable(incoming) && !sameArt;
        bool titleChanged = previousTitle is { Length: > 0 }
            && !string.Equals(previousTitle, loadedTitle, StringComparison.Ordinal);
        if (!((keptVisible && titleChanged) || (tookIncoming && previousTitle is { Length: > 0 })))
            return;

        static string CoverId(Image? image)
        {
            string? url = ImageSource.UrlFor(image, preferLargest: false);
            if (string.IsNullOrEmpty(url)) return "-";
            var id = ImageSource.ImageIdSpan(url);
            return id.Length == 0 ? "-" : id.ToString();
        }

        WaveeLog.Instance.Event(WaveeLogLevel.Info, "detail", "detail.cover.latch",
            "PreferVisible kept a cover/title that does not match the loaded identity",
            fields:
            [
                WaveeLogField.Of("keptCover", keptVisible),
                WaveeLogField.Of("tookIncoming", tookIncoming),
                WaveeLogField.Of("titleChanged", titleChanged),
                WaveeLogField.Of("title.prev", previousTitle ?? ""),
                WaveeLogField.Of("title.loaded", loadedTitle ?? ""),
                WaveeLogField.Of("cover.visible", CoverId(visible)),
                WaveeLogField.Of("cover.incoming", CoverId(incoming)),
                WaveeLogField.Of("cover.chosen", CoverId(chosen)),
                WaveeLogField.Of("sameArt", sameArt),
            ]);
    }

    static void LogVideoSweep(string kind, string contextUri, IReadOnlyList<Track> tracks)
    {
        var log = WaveeLog.Instance;
        if (!log.IsEnabled(WaveeLogLevel.Debug)) return;
        int withVideo = 0, overrideOnly = 0, noRow = 0, negative = 0;
        var missSample = new System.Text.StringBuilder();
        int sampled = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            var uri = tracks[i].Uri;
            var assoc = VideoPresence.Association(uri);
            if (assoc is { HasVideo: true }) { withVideo++; continue; }
            if (VideoPresence.HasOverride(uri)) { overrideOnly++; continue; }
            if (assoc is null) noRow++; else negative++;
            if (sampled < 6 && EntityUri.Parse(uri) is { IsSpotify: true, Kind: EntityKind.Track })
            {
                if (sampled > 0) missSample.Append(',');
                missSample.Append(EntityUri.IdOf(uri));
                sampled++;
            }
        }
        log.Event(WaveeLogLevel.Debug, "detail", "video.assoc.page", "detail-page music-video roll-up computed",
            fields:
            [
                WaveeLogField.Of("kind", kind), WaveeLogField.Of("contextUri", contextUri),
                WaveeLogField.Of("tracks", tracks.Count), WaveeLogField.Of("withVideo", withVideo),
                WaveeLogField.Of("overrideOnly", overrideOnly), WaveeLogField.Of("noRow", noRow),
                WaveeLogField.Of("negative", negative),
                WaveeLogField.Of("missIds", missSample.Length == 0 ? "-" : missSample.ToString()),
            ]);
    }

    static IReadOnlyDictionary<string, Owner>? UserProfileMap(Playlist p)
    {
        var map = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
        Add(p.Owner);
        if (p.Collaborators is { Count: > 0 } collaborators)
            for (int i = 0; i < collaborators.Count; i++) Add(collaborators[i]);
        return map.Count == 0 ? null : map;

        void Add(Owner? owner)
        {
            if (owner is null) return;
            if (owner.Id.Length > 0) map[owner.Id] = owner;
            var canonical = UserProfileIds.Normalize(owner.Id);
            if (canonical is not null)
            {
                map[canonical] = owner;
                map[UserProfileIds.BareId(canonical)] = owner;
            }
        }
    }

    /// <param name="hasVideo">See <see cref="MapPlaylist"/>.</param>
    static DetailModel MapLiked(IReadOnlyList<Track> tracks, Func<string, bool>? hasVideo = null)
    {
        string count = Strings.Detail.SongCount(tracks.Count);
        string meta = TrackMetadataReadiness.CompleteDuration(tracks, tracks.Count) is { } ms
            ? Strings.Detail.MetaLine(count, DetailFormat.TotalTime(ms)) : count;
        LogVideoSweep("liked", "spotify:collection:tracks", tracks);
        return new DetailModel(
            Title: Loc.Get(Strings.Detail.LikedSongs), Cover: null, ContextUri: "spotify:collection:tracks",
            BadgeType: null, Year: null, OwnerName: null, OwnerImage: null,
            Artists: Array.Empty<ArtistRef>(), Description: null, MetaLine: meta,
            Tracks: tracks, AboutArtist: null,
            HasDateAdded: tracks.Any(t => t.AddedAt is not null),   // liked rows carry the collection add time → Date-added column + sort
            HasVideo: AnyVideo(tracks, hasVideo));
    }

    /// <summary>Does any row in this membership have a video (⇒ the table keeps its Video lane)? One pass, one probe
    /// per row, and it stops at the first hit.</summary>
    static bool AnyVideo(IReadOnlyList<Track> tracks, Func<string, bool>? hasVideo)
    {
        var video = hasVideo ?? VideoPresence.HasVideo;
        for (int i = 0; i < tracks.Count; i++)
            if (video(tracks[i].Uri)) return true;
        return false;
    }

    // The album model: hero + tracklist + the "More by" shelf the getAlbum payload carries. The below-the-fold
    // enrichment (About-the-artist / Fans-also-like / Featured-on / Merch / Similar) is deliberately NOT awaited here —
    // AlbumTrailing loads each section independently so the hero and track list render immediately and no slow or failed
    // enrichment can block (or sink) them.
    // `link` is the resolved kind-138 pre-release identity, when the loader had reason to ask for one (a full
    // prerelease route, or an album that already looks upcoming). Optional + null by default: every ordinary album open
    // keeps its single request.
    /// <param name="hasVideo">See <see cref="MapPlaylist"/>.</param>
    internal static DetailModel MapAlbum(Album a, PreReleaseLink? link = null, Func<string, bool>? hasVideo = null)
    {
        var tracks = a.Tracks ?? Array.Empty<Track>();
        string badge = a.Kind switch
        {
            AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
            AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
            AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
            _ => Loc.Get(Strings.Detail.Badge.Album),
        };
        string count = Strings.Detail.SongCount(a.TrackCount);
        string meta = TrackMetadataReadiness.CompleteDuration(tracks, a.TrackCount, a.Tracks is not null, releasedOnly: true) is { } ms
            ? Strings.Detail.MetaLineYear(count, DetailFormat.TotalTime(ms), a.Year)
            : a.Year > 0 ? count + " · " + a.Year : count;
        LogVideoSweep("album", a.Uri, tracks);
        // `now` is read ONCE, here at the mapper boundary — never in a Render — so every release-tense fact this
        // model carries (UpcomingAt's countdown gate, AlbumReleaseFactsRules' Released/Releases caption) agrees on the
        // same instant instead of drifting apart across re-renders (the LikedFactsPanel precedent: "`now` is read
        // ONCE at the panel boundary").
        var now = DateTimeOffset.UtcNow;
        var releaseInstant = PreReleaseDerivation.ReleaseInstant(a.ReleaseDate);
        return new DetailModel(
            Title: a.Name, Cover: a.Cover, ContextUri: a.Uri,
            BadgeType: badge, Year: a.Year.ToString(), OwnerName: null, OwnerImage: null,
            Artists: a.Artists, Description: null, MetaLine: meta,
            Tracks: tracks, AboutArtist: null,
            HasVideo: AnyVideo(tracks, hasVideo), ReleaseKind: a.Kind, MoreByArtist: a.MoreByArtist,
            Label: a.Label, Copyright: a.Copyright, ReleaseDate: AlbumReleaseFactsRules.FormatReleaseDate(a.ReleaseDate, a.ReleaseDatePrecision), AlbumArtists: a.ArtistsDetailed,
            OtherVersions: a.OtherVersions, CourtesyLine: a.CourtesyLine, ReleaseDatePrecision: a.ReleaseDatePrecision,
            DiscCount: a.DiscCount, ShareUrl: a.ShareUrl, IsPreRelease: a.IsPreRelease, PreReleaseEnd: a.PreReleaseEnd)
        {
            ReleaseInstant = releaseInstant,
            UpcomingAt = PreReleaseDerivation.UpcomingAt(a, now),
            // Only while genuinely ahead of us: a kind-138 link is cached for up to 30 days and must not turn the heart
            // into a "Pre-save" for a record that shipped last week.
            PreReleaseUri = link is { IsUpcoming: true } l ? l.PreReleaseUri : null,
            // "About this release" as DATA (AlbumReleaseFactsRules): computed ONCE here from the raw album fields, so
            // AlbumTrailing's grid composition (Songs/Length row + a full-width Released row) never depends on WHICH
            // hydration rung (Open/Rich/Full) last landed — only the strings inside an already-placed tile refine.
            ReleaseFacts = AlbumReleaseFactsRules.For(tracks, a.ReleaseDate, a.ReleaseDatePrecision,
                a.Year > 0 ? a.Year : null, releaseInstant, a.Label, a.CourtesyLine, a.Copyright, now, a.TrackCount),
        };
    }

    // Delegates to the ONE consolidated converter (Actions/SpotifyLink.cs); keeps this surface's bare-id fallback
    // (a caller passing a raw playlist id — no spotify: prefix — still gets a playlist url).
    internal static string SpotifyPlaylistWebUrl(string uri)
        => SpotifyLink.WebUrl(uri) ?? $"https://open.spotify.com/playlist/{uri}";

    /// <summary>The save count as it reads in the meta line. Six- and eight-figure counts are the norm on an editorial
    /// playlist ("18713647 saves"), and at caption size the full number is a wall of digits that also pushes the line
    /// past its measure. Compact above 999, through the app's one compact formatter — the same one the Plays column
    /// and the artist page's listener counts use, so a playlist's saves and a track's plays cannot format differently.
    /// <para>Below 1000 the ICU plural key still runs, because "1 save" must not read "1 saves" and a three-digit
    /// count is short enough to print in full.</para></summary>
    static string SaveCountText(long n)
        => n >= 1000 ? Strings.Detail.SaveCountCompact(HomeCards.CompactNumber(n)) : Strings.Detail.SaveCount(n);
}
