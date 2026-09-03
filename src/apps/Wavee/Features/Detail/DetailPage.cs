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
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The shared detail page (playlist / album / single / liked). A Component keyed per route in ContentHost, so the
// existing KeepAlive boundary caches it. It loads the matching IMusicLibrary slice through UseResource (which
// cancels on unmount — a fast nav-away aborts in flight), shows a matched skeleton via Skel.Region, then reveals the
// two-column shell. The per-context config is resolved POST-load (an album with ≤2 tracks becomes a "single").
sealed class DetailPage : Component
{
    readonly Signal<Route> _route;   // the (per-pane) navigation route, read reactively so ONE instance serves successive detail pages
    public DetailPage(Signal<Route> route) { _route = route; }

    // ── the open page's owner identities (the store-change predicate's User arm) ──────────────────────────────────────
    // A profile landing after the page mapped IS a "the model is stale" event — a byline or an Added-by cell renders an
    // Owner ROW (P4-C). But keying the reload on the KIND alone matched EVERY spotify:user: bump in the process: the
    // sidebar's profile prefetch, a Liked-Episodes sweep's added-by closure, any other page's owners. On a library with
    // many collaborative playlists that is a full re-map + re-project of the open page per resolved stranger.
    // So the ids the page actually renders are captured WHEN THE MODEL IS MAPPED and published as an immutable snapshot;
    // the predicate then compares against them. Immutable + a single volatile publish = safe to read from the store's
    // change thread without a lock, and never a torn set.
    sealed record OwnerScope(string? Pid, System.Collections.Generic.HashSet<string> Ids);
    static readonly OwnerScope NoOwners = new(null, new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase));
    volatile OwnerScope _owners = NoOwners;

    // The NO-PREVIEW cover latch's "visible" fallback (see the cover latch below and the live-refresh latch in the
    // UseSignalEffect further down): a deep link / search hit opens with no nav-preview card to latch a first cover
    // against, so PreferVisible had nothing to compare the loaded cover to and simply always took it — fine for the
    // FIRST load, but a route-reused instance's SECOND load (this same route re-fetching) then re-decoded and faded
    // in a cover that was already on screen the moment the wire named it under a different size hash. Tracking the
    // last cover THIS instance actually published closes that gap without touching the route/preview identity: it is
    // written on the async loader's background-thread continuation (ConfigureAwait(false) throughout), hence
    // volatile, and it is read-only input to PreferVisible's SameArt check — a stale cover from a DIFFERENT context
    // can never leak in as the wrong art, because PreferVisible only keeps it when the incoming cover is the SAME art.
    volatile Image? _lastCover;

    /// <summary>The page's owner identities (<see cref="DetailOwnerIds"/>), scoped to the page id they were read from
    /// so a stale snapshot cannot answer for the next route.</summary>
    static OwnerScope OwnersOf(string? pid, DetailModel m)
        => new(pid, DetailOwnerIds.From(m.OwnerName, m.Collaborators, m.UserProfilesById, m.Tracks));

    /// <summary>Publish the snapshot for a freshly-mapped model and hand the model straight back, so a mapping site is
    /// one wrapped expression rather than two statements that can drift apart.</summary>
    DetailModel WithOwners(DetailKind kind, string? pid, DetailModel m)
    {
        // Only the playlist arm consults it — an album/show/liked page never re-maps on a User bump — so the walk is
        // not paid for those at all.
        _owners = kind == DetailKind.Playlist ? OwnersOf(pid, m) : NoOwners;
        return m;
    }

    /// <summary>Does a <c>spotify:user:</c> bump belong to the page currently mapped? Reads ONE volatile reference.</summary>
    bool RendersOwner(string pid, string userUri)
    {
        var scope = _owners;
        return scope.Pid == pid && DetailOwnerIds.Matches(scope.Ids, userUri);
    }

    // Route → (kind, id): album:/pl: carry the uri after the prefix; "liked" is the saved-tracks collection.
    internal static (DetailKind Kind, string? Id) ParseDetail(Route r) =>
        r.Name.StartsWith("album:", StringComparison.Ordinal) ? (DetailKind.Album, r.Name["album:".Length..])
        // Same kind, same config, same shell — only the id needs resolving before the load can read it.
        : r.Name.StartsWith("prerelease:", StringComparison.Ordinal) ? (DetailKind.Album, r.Name["prerelease:".Length..])
        : r.Name.StartsWith("pl:", StringComparison.Ordinal) ? (DetailKind.Playlist, r.Name["pl:".Length..])
        : r.Name == "local" ? (DetailKind.Playlist, "wavee:local:all")   // the Local Files collection (LocalSource owns it)
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
        // Dep-keyed on the route: when navigation swaps the detail route on a REUSED instance, cancel the prior load and
        // refetch for the new id (resetting to the new preview/skeleton). Fires once at mount when nothing is reused.
        // Stable per-instance loadable, re-driven by the route dep key — DetailShell freezes the model at construction,
        // so the loadable INSTANCE must be stable across route swaps (a fresh store-cache instance per route would leave
        // the reused shell pinned to the first item — the master-detail reactivity bug). KeepAlive caches the parked page.
        var model = UseResource(async ct =>
        {
            var loaded = await LoadAsync(svc, kind, id, ct).ConfigureAwait(false);
            // The daylist rollover window may be absent from the playlist4 wire (unpinned by any capture); the Home
            // card's Pathfinder attributes rode in on the nav preview — keep them when the full load returned none.
            // (The loader closure is re-pointed each render, so a route swap merges against ITS route's preview.)
            if (loaded.ExpiresAtMs == 0 && preview is { ExpiresAtMs: > 0 })
                loaded = loaded with { ExpiresAtMs = preview.ExpiresAtMs, CreatedAtMs = preview.CreatedAtMs };
            if (loaded.Accent == 0 && preview is { Accent: not 0u })
                loaded = loaded with { Accent = preview.Accent };
            // A ROLLING-IDENTITY container (a daylist and friends) can already have rolled to a NEW edition by the
            // time this call's own store composition catches up: `loaded` is whatever the LAST revalidation wrote,
            // and THIS open's own revalidation (LibrarySync's dirty/stale gates, the /diff header re-read, the 205
            // catalogue cache — all fixed to actually run, but still asynchronous) has not landed yet. The pure rule
            // (DetailHeaderMergeRules) trusts the independently-fresh preview for Title/Cover in that case only.
            bool rollingIdentity = DetailHeaderMergeRules.IsRollingIdentity(loaded.ExpiresAtMs, preview?.ExpiresAtMs ?? 0);
            loaded = loaded with { Title = DetailHeaderMergeRules.ResolveTitle(rollingIdentity, loaded.Title, preview?.Title) };
            // THE cover latch, at the point the model is published — not in one arm of one renderer. The card preview
            // painted a 300px CDN rendition; the detail payload names the same art by its 640px hash. A different url
            // is a different ImageCache key ⇒ Pending ⇒ placeholder ⇒ a 220ms fade of the picture already on screen —
            // the "cover flashes out and back in" report. PreferVisible keeps the visible rendition for the SAME ART
            // (ImageSource.SameArt: the size-independent id tail) and takes the incoming cover for different art, so
            // every consumer of this loadable — the two-column rail, the vertical hero, the editable playlist cover, the
            // tone plane — reads ONE stable cover. (The old per-shell latch covered only the rail; the vertical hero and
            // EditableCover read the raw model and re-decoded on every hash change.)
            // No preview (deep link / search hit)? Fall back to the last cover THIS instance actually published
            // (_lastCover) — mirrors the live-refresh latch below — so a route-reused instance's later load still has
            // something to latch a same-art cover against instead of always taking whatever the wire just named.
            Image? visibleCover = preview?.Cover ?? _lastCover;
            // Same rollingIdentity rule for the cover: a rolled-over daylist's stale store row names the PREVIOUS
            // edition's (generic editorial) art, which is different-art from the correct preview cover and would
            // otherwise win PreferVisible's "different art ⇒ take incoming" branch below — backwards for this case.
            Image? incomingCover = DetailHeaderMergeRules.ResolveIncomingCover(rollingIdentity, loaded.Cover, preview?.Cover);
            loaded = loaded with { Cover = ImageSource.PreferVisible(incomingCover, visibleCover) };
            LogCoverLatch(preview?.Title, loaded.Title, incomingCover, visibleCover, loaded.Cover);
            _lastCover = loaded.Cover;
            // DIAGNOSTIC ONLY (see DetailCoverTrace): the handoff the whole "flash" question turns on — the nav-preview
            // cover the page opened with vs the cover the full load brought. `same=false` with two ids that share their
            // last 24 chars is H1 exactly: identical art, a different CDN size hash, hence a different ImageCache key.
            if (DetailCoverTrace.On)
                WaveeLog.Instance.Debug("detail", "cover", "loaded",
                    WaveeLogField.Of("route", route.Name),
                    WaveeLogField.Of("kind", kind.ToString()),
                    WaveeLogField.Of("preview", DetailCoverTrace.Id(preview?.Cover)),
                    WaveeLogField.Of("loaded", DetailCoverTrace.Id(loaded.Cover)),
                    WaveeLogField.Of("same", ImageSource.SameSource(preview?.Cover, loaded.Cover)),
                    WaveeLogField.Of("sameArt", ImageSource.SameArt(preview?.Cover, loaded.Cover)),
                    WaveeLogField.Of("previewLargest", DetailCoverTrace.Id(preview?.Cover, preferLargest: true)),
                    WaveeLogField.Of("loadedLargest", DetailCoverTrace.Id(loaded.Cover, preferLargest: true)));
            return WithOwners(kind, id, loaded);   // the page's owner identities, for the store-change predicate below
        }, preview ?? PendingSeed(kind), route.Name).Loadable;

        // LIVE in-place refresh: an active page re-projects resident store data into the SAME loadable, never Pending.
        // This path is deliberately separate from the initial hydrated load. A store notification must not schedule
        // the hydration/revalidation that can write the same playlist and close a refresh loop. KeepAlive parking and
        // window suspension tear down the subscription, pump, and open-context ownership; reactivation catches up once.
        var post = Context.UsePost();
        var realStore = svc.RealStore;
        var realSync = svc.RealSync;
        var active = UseIsActive();
        var activationSeen = UseRef(false);
        var wasActive = UseRef(false);
        Context.UseSignalEffect(() =>
        {
            bool nowActive = active.Value;
            var activeRoute = _route.Value;   // route swaps also release the old subscription/context before re-arming
            bool reactivated = nowActive && activationSeen.Value && !wasActive.Value;
            wasActive.Value = nowActive;
            if (nowActive) activationSeen.Value = true;
            if (realStore is null || !nowActive) return;

            var (openKind, openId) = ParseDetail(activeRoute);
            if (openKind == DetailKind.Playlist && openId is not null) realSync?.SetOpenContext(openId);
            var pump = new DetailLiveRefresh(async ct =>
            {
                var (k, pid) = ParseDetail(_route.Peek());
                var fresh = await RefreshAsync(svc, k, pid, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    // Nav-away race: the load may land after the user routed to a DIFFERENT detail page, which now
                    // reuses this same loadable cell. Re-resolve the LIVE route and drop the write unless it still
                    // points at the page this pass loaded — otherwise the old model flashes into the new page.
                    var (k2, pid2) = ParseDetail(_route.Peek());
                    if (k2 != k || pid2 != pid) return;
                    // Re-publish the owner snapshot on the UI thread, from the model that is about to be committed:
                    // a collaborator added while the page was open widens the set this page reacts to.
                    var next = WithOwners(k, pid, WithNotice(k, model, fresh, lib, pid));
                    // Same latch on the live path: a bulk refresh (music-video detection, hydration) re-maps the
                    // model and can name the cover by a different size hash again; a genuine cover change (an
                    // edit, a daylist rollover) is DIFFERENT art and still wins.
                    var current = model.Value.Peek();
                    var incomingCover = next.Cover;
                    next = next with { Cover = ImageSource.PreferVisible(next.Cover, current.Cover) };
                    LogCoverLatch(current.Title, next.Title, incomingCover, current.Cover, next.Cover);
                    // Reorder-in-flight (§P1.11): while a SAME-LIST drag session is live over THIS playlist the
                    // rows under the pointer are the ones being aimed with. Committing a re-projection now
                    // yanks the insertion geometry out from under the gesture (the list re-keys, the gap moves,
                    // the drop lands somewhere else), so the model is HELD and applied the moment the session
                    // ends. A foreign session (a drag from another list, a file drag) is not deferred — it has
                    // no stake in this list's order.
                    if (k == DetailKind.Playlist && PlaylistReorderDefer.TryHold(model, next, pid)) return;
                    // A pass that landed nothing for THIS list republishes the SAME Tracks instance: hydration bulks
                    // arrive several per chunk and per trait kind, and every consumer keyed on the list reference (the
                    // facts rail and its cards, the track list's props) would otherwise re-render for a no-op.
                    if (LikedFactsRules.TracksEquivalent(current.Tracks, next.Tracks)) next = next with { Tracks = current.Tracks };
                    model.SetReady(next);
                    _lastCover = next.Cover;   // the no-preview latch's fallback (see the initial load above)
                });
            }, onStorm: passes =>
            {
                var (_, stormId) = ParseDetail(_route.Peek());
                WaveeLog.Instance.Event(WaveeLogLevel.Warning, "detail", "detail.refresh.storm",
                    "detail refresh exceeded the bounded steady-state rate",
                    fields: [WaveeLogField.Of("contextUri", stormId ?? "liked"), WaveeLogField.Of("passes", passes)]);
            });
            var sub = realStore.Changes.Subscribe(Wavee.Backend.Observers.From<Wavee.Backend.StoreChange>(c =>
            {
                var (k, pid) = ParseDetail(_route.Peek());
                // Live kinds: an open PLAYLIST refreshes on its own uri (membership/diff writes bump it); the LIKED page
                // refreshes on any Liked-kind change (an unlike bumps the track uri with Kind=Liked — the list must drop
                // the row) — both also on a Bulk (hydrate/delta bursts coalesce into one).
                bool relevant = k switch
                {
                    // ...plus a USER-kind change for an owner THIS page renders: a playlist byline / added-by cell is
                    // an Owner ROW now (P4-C), so a profile landing after the page mapped is exactly the "the model is
                    // stale" edge this reload exists for. Matched against the page's own owner-id snapshot (see
                    // OwnerScope) rather than on the KIND alone — every resolved stranger in the process used to re-map
                    // and re-project the open page.
                    DetailKind.Playlist when pid is not null =>
                        c.IsBulk || c.Uri == pid
                        || RendersOwner(pid, c.Uri),
                    DetailKind.Liked => c.IsBulk || c.Kind == Wavee.Core.CollectionKind.Liked,
                    // An open ALBUM refreshes on a Bulk only: the async music-video detection folds its per-track
                    // HasVideo flips into one bulk change, which would otherwise stay invisible until re-navigation.
                    DetailKind.Album when pid is not null => c.IsBulk || c.Uri == pid,
                    DetailKind.Show when pid is not null => c.IsBulk || c.Uri == pid,
                    _ => false,
                };
                if (!relevant) return;
                pump.Request();
            }));
            if (reactivated) pump.Request();
            Reactive.OnCleanup(() =>
            {
                sub.Dispose();
                pump.Dispose();
                if (openKind == DetailKind.Playlist && openId is not null) realSync?.ClearOpenContext(openId);
            });
        });

        // Pre-loaded: render the shell straight away from the preview (header live), tracks stream in via Skel.Region.
        // Thread the preview's cover as the fallback so a loaded null cover never drops the flown-in art to a placeholder.
        if (preview is not null)
            return Embed.Comp(() => new DetailShell(_route, model, svc.Settings));

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
                    content: _ => new BoxEl
                    {
                        Grow = 1f, Direction = 0,
                        Children =
                        [
                            Embed.Comp(() => new DetailShell(_route, model, settings: svc.Settings))
                                with { DeriveRenderedOutput = true },
                        ],
                    },
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
        if (kind == DetailKind.Album) return fresh with { Notice = PlaylistPageNoticeRules.ForAlbum(fresh.Tracks) };
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

    internal static async Task<DetailModel> LoadAsync(Services svc, DetailKind kind, string? id, CancellationToken ct) => kind switch
    {
        DetailKind.Playlist => await LoadPlaylistWithSaveCountAsync(svc, id ?? "", HydrationLevel.Open, ct),
        DetailKind.Liked => MapLiked(await svc.Library.GetLikedSongsAsync(ct: ct)),
        DetailKind.Show => MapShow(await svc.Library.GetShowAsync(id ?? "", ct: ct)),
        _ => await LoadAlbumDetailAsync(svc, id ?? "", HydrationLevel.Rich, ct),
    };

    /// <summary>Re-project resident data after a store signal. None is load-bearing: this path must never schedule the
    /// hydration/revalidation that can produce another store signal and close a refresh loop.</summary>
    internal static async Task<DetailModel> RefreshAsync(Services svc, DetailKind kind, string? id, CancellationToken ct) => kind switch
    {
        DetailKind.Playlist => await LoadPlaylistWithSaveCountAsync(svc, id ?? "", HydrationLevel.None, ct),
        DetailKind.Liked => MapLiked(await svc.Library.GetLikedSongsAsync(HydrationLevel.None, ct)),
        DetailKind.Show => MapShow(await svc.Library.GetShowAsync(id ?? "", HydrationLevel.None, ct)),
        _ => await LoadAlbumDetailAsync(svc, id ?? "", HydrationLevel.None, ct),
    };

    /// <summary>The album detail load, with the ONE extra hop an upcoming release needs.
    ///
    /// A prerelease route must resolve before it can read anything — the two ids are unrelated (Wavee.Core/PreReleaseUris).
    /// The REVERSE hop (album → prerelease link, for the pre-save heart) is deliberately gated: kind 138 404s for almost
    /// every album, so it is only asked when the album already looks upcoming. A normal album open costs exactly what it
    /// costs today.</summary>
    static async Task<DetailModel> LoadAlbumDetailAsync(Services svc, string id, HydrationLevel level, CancellationToken ct)
    {
        string albumUri = id;
        PreReleaseLink? link = null;
        if (PreReleaseUris.IsPreRelease(id))
        {
            link = await svc.PreRelease.ResolveAsync(id, ct).ConfigureAwait(false);
            if (link is null) return DetailModel.Empty;   // unresolvable (offline / 404 / dead entity) → the existing empty state
            albumUri = link.AlbumUri;
        }
        // Initial navigation asks for Rich: the ©/℗ line and Plays star ride the same catalogue POST as the tracklist.
        // Store-triggered refresh passes None and only re-projects the already-resident album.
        var album = await svc.Library.GetAlbumAsync(albumUri, level, ct).ConfigureAwait(false);
        if (link is null
            && (album.IsPreRelease || PreReleaseDerivation.UpcomingAt(album, DateTimeOffset.UtcNow) is not null))
            link = await svc.PreRelease.ResolveAsync(albumUri, ct).ConfigureAwait(false);
        return MapAlbum(album, link);
    }

    /// <summary>The playlist read. The owner-only permission GET that used to hang off this call is GONE: the store
    /// header is now the canonical permission state (<c>LibrarySync.SetOpenContext</c> seeds <c>IsPublic</c> /
    /// <c>BasePermissionRevision</c> / <c>Capabilities.IsCollaborative</c> on open, and a dealer permission push
    /// updates it and bumps the uri), so the page reads it instead of paying a request per open and racing the push.</summary>
    static async Task<Playlist?> LoadPlaylistAsync(Services svc, string uri, HydrationLevel level, CancellationToken ct)
        => await svc.Library.GetPlaylistAsync(uri, level, ct).ConfigureAwait(false);

    internal static async Task<DetailModel?> ReloadPlaylistDetailAsync(Services svc, string uri, CancellationToken ct = default)
    {
        var p = await LoadPlaylistAsync(svc, uri, HydrationLevel.Open, ct).ConfigureAwait(false);
        return p is null ? null : MapPlaylist(p, membershipLoaded: MembershipLoaded(svc, p));
    }

    /// <summary>Has the store adopted a membership baseline for this playlist? Read from the store's own baseline
    /// flag, never from the track count: an empty membership and a missing one both compose to zero rows.
    /// <para>Read AFTER the load (the rows were composed from the store a moment ago), and only for Spotify playlists
    /// the real store owns — a local / on-device / fake-backend list carries its rows with the record and has no
    /// store baseline to consult, so asking would pin it in the loading state forever.</para></summary>
    static bool MembershipLoaded(Services svc, Playlist p)
        => svc.RealStore is not { } store
           || EntityUri.KindOf(p.Uri) != EntityKind.Playlist
           || store.HasMembership(p.Uri);

    // A podcast show folds onto the shared detail surface: rail = cover + PODCAST pill + publisher/episode-count meta +
    // description + Play/Follow; the right column renders Episodes (DetailConfig.Show.Content == Episodes → EpisodeList).
    static DetailModel MapShow(Show? s)
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

    /// <summary>How long the header will wait on the save count before rendering without it. The popcount body is
    /// 6-11 bytes and it runs CONCURRENTLY with the (far heavier) playlist load, so in practice this never elapses —
    /// it exists so a hung spclient connection can never hold a painted header hostage to a decorative number.</summary>
    static readonly TimeSpan SaveCountGrace = TimeSpan.FromMilliseconds(250);

    static async Task<DetailModel> LoadPlaylistWithSaveCountAsync(Services svc, string id, HydrationLevel level, CancellationToken ct)
    {
        // Started FIRST and awaited last: the count rides along inside the playlist load's own latency instead of
        // adding to it. Never awaited without a grace window — see SaveCountGrace.
        var saves = svc.PlaylistPopcount.GetSaveCountAsync(PlaylistUri(id), ct);
        var playlist = await LoadPlaylistAsync(svc, id, level, ct).ConfigureAwait(false);

        long? count = null;
        try { count = await saves.WaitAsync(SaveCountGrace, ct).ConfigureAwait(false); }
        catch (TimeoutException) { }              // slow counter → header renders without the segment
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        if (playlist is null) return DetailModel.Empty;
        return MapPlaylist(playlist, count, MembershipLoaded(svc, playlist));
    }

    /// <summary>The playlist route id as a uri. Ids arrive bare from the route but a full uri also flows through some
    /// call paths, so accept both rather than producing `spotify:playlist:spotify:playlist:…`.</summary>
    static string PlaylistUri(string id)
        => EntityUri.Parse(id).IsSpotify ? id : "spotify:playlist:" + id;   // "is it already a uri?" via the ONE parser

    static DetailModel MapPlaylist(Playlist p, long? saveCount = null, bool membershipLoaded = true)
    {
        var tracks = p.Tracks ?? Array.Empty<Track>();
        // Data-drive the optional columns: show Date-added if any track has one, and Added-by only when the playlist is
        // collaborative (≥2 distinct contributors) — matching the reference app's "hide unless it carries signal" rule.
        bool hasDate = false, hasVideo = false;
        int episodes = 0;
        var contributors = new HashSet<string>();
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].AddedAt is not null) hasDate = true;
            if (VideoPresence.HasVideo(tracks[i])) hasVideo = true;   // a user-attached mp4 also earns the Video column
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
        string total = DetailFormat.TotalTime(DetailFormat.TotalMs(tracks));
        // No membership yet ⇒ no meta line at all: "0 songs · 1 min" is a count we do not have. The rail renders a
        // shimmer bar in the row's place while MembershipLoaded is false (DetailRail), so the slot is still held.
        string meta = !membershipLoaded ? ""
            : saveCount is > 0 and var n
            ? Strings.Detail.MetaLineSaved(songs, Strings.Detail.SaveCount(n), total)
            : Strings.Detail.MetaLine(songs, total);
        LogVideoSweep("playlist", p.Uri, tracks);
        return new DetailModel(
            Title: p.Name, Cover: p.Cover, ContextUri: p.Uri,
            BadgeType: null, Year: null, OwnerName: p.OwnerName, OwnerImage: p.Owner?.Avatar,
            Artists: Array.Empty<ArtistRef>(), Description: p.Description, MetaLine: meta,
            Tracks: tracks, AboutArtist: null,
            HasDateAdded: hasDate, HasAddedBy: contributors.Count >= 2, HasVideo: hasVideo,
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

    static DetailModel MapLiked(IReadOnlyList<Track> tracks)
    {
        string meta = Strings.Detail.MetaLine(Strings.Detail.SongCount(tracks.Count), DetailFormat.TotalTime(DetailFormat.TotalMs(tracks)));
        LogVideoSweep("liked", "spotify:collection:tracks", tracks);
        return new DetailModel(
            Title: Loc.Get(Strings.Detail.LikedSongs), Cover: null, ContextUri: "spotify:collection:tracks",
            BadgeType: null, Year: null, OwnerName: null, OwnerImage: null,
            Artists: Array.Empty<ArtistRef>(), Description: null, MetaLine: meta,
            Tracks: tracks, AboutArtist: null,
            HasDateAdded: tracks.Any(t => t.AddedAt is not null),   // liked rows carry the collection add time → Date-added column + sort
            HasVideo: tracks.Any(VideoPresence.HasVideo));
    }

    // The album model: hero + tracklist + the "More by" shelf the getAlbum payload carries. The below-the-fold
    // enrichment (About-the-artist / Fans-also-like / Featured-on / Merch / Similar) is deliberately NOT awaited here —
    // AlbumTrailing loads each section independently so the hero and track list render immediately and no slow or failed
    // enrichment can block (or sink) them.
    // `link` is the resolved kind-138 pre-release identity, when the loader had reason to ask for one (a full
    // prerelease route, or an album that already looks upcoming). Optional + null by default: every ordinary album open
    // keeps its single request.
    static DetailModel MapAlbum(Album a, PreReleaseLink? link = null)
    {
        var tracks = a.Tracks ?? Array.Empty<Track>();
        string badge = a.Kind switch
        {
            AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
            AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
            AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
            _ => Loc.Get(Strings.Detail.Badge.Album),
        };
        string meta = Strings.Detail.MetaLineYear(
            Strings.Detail.SongCount(a.TrackCount), DetailFormat.TotalTime(DetailFormat.TotalMs(tracks)), a.Year);
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
            HasVideo: tracks.Any(VideoPresence.HasVideo), ReleaseKind: a.Kind, MoreByArtist: a.MoreByArtist,
            Label: a.Label, Copyright: a.Copyright, ReleaseDate: AlbumReleaseFactsRules.FormatReleaseDate(a.ReleaseDate, a.ReleaseDatePrecision), AlbumArtists: a.ArtistsDetailed,
            OtherVersions: a.OtherVersions, CourtesyLine: a.CourtesyLine, ReleaseDatePrecision: a.ReleaseDatePrecision,
            DiscCount: a.DiscCount, ShareUrl: a.ShareUrl, IsPreRelease: a.IsPreRelease, PreReleaseEnd: a.PreReleaseEnd)
        {
            // The album path's cold-open notice, the exact counterpart of MapPlaylist's Cold(...) stamp below: an album
            // whose disc rows are still gid-only (a failed TrackV4 repair sealed it thin) must say so on the FIRST
            // paint, not only after a store change routes the live pump through WithNotice.
            Notice = PlaylistPageNoticeRules.ForAlbum(tracks),
            ReleaseInstant = releaseInstant,
            UpcomingAt = PreReleaseDerivation.UpcomingAt(a, now),
            // Only while genuinely ahead of us: a kind-138 link is cached for up to 30 days and must not turn the heart
            // into a "Pre-save" for a record that shipped last week.
            PreReleaseUri = link is { IsUpcoming: true } l ? l.PreReleaseUri : null,
            // "About this release" as DATA (AlbumReleaseFactsRules): computed ONCE here from the raw album fields, so
            // AlbumTrailing's grid composition (Songs/Length row + a full-width Released row) never depends on WHICH
            // hydration rung (Open/Rich/Full) last landed — only the strings inside an already-placed tile refine.
            ReleaseFacts = AlbumReleaseFactsRules.For(tracks, a.ReleaseDate, a.ReleaseDatePrecision,
                a.Year > 0 ? a.Year : null, releaseInstant, a.Label, a.CourtesyLine, a.Copyright, now),
        };
    }

    // Delegates to the ONE consolidated converter (Actions/SpotifyLink.cs); keeps this surface's bare-id fallback
    // (a caller passing a raw playlist id — no spotify: prefix — still gets a playlist url).
    internal static string SpotifyPlaylistWebUrl(string uri)
        => SpotifyLink.WebUrl(uri) ?? $"https://open.spotify.com/playlist/{uri}";
}
