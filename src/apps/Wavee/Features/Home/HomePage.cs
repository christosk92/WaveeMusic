using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Home;
using Wavee.Features.Browse;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// One reveal group covers both the page shell and the artist disclosure nested inside it. A shared token makes the
// deriver coordinate those regions as one Home transition instead of staggering the inner disclosure a second time.
static class HomeSkeleton
{
    internal static readonly object Group = new();
}

// The section directory Home renders is HomeLanding.Sections (HomeLandingProjection.SectionDirectory) — the ONE
// producer, consumed by the single HomeRow.Sections emission. A second local selector used to live here; it had no
// caller left and was deleted rather than kept as a divergent shadow of the projection's rules.

// The Home landing page — a vertically scrolling composition driven by the source-agnostic, lossless HomeFeed ledger.
// Every server section survives either as its own typed module or as an entry in the two-row section deck; the deck
// drills into a dedicated page with a Home > section breadcrumb, following the Zune/Fluent master-detail model.
// Async skeletons are derived from FakeData.HomeSeed through Skel.Region.
sealed class HomePage : Component
{
    /// <summary>The card ▶ path, awaited. The controller's PlayAsync swallows "context resolved to 0 tracks" (it logs
    /// at Info and returns), so the only thing that ties a dead press to that line is one of our own naming the card:
    /// <c>card.play uri=… kind=… outcome=…</c> lands right beside the controller's resolve line. A thrown failure is
    /// the one outcome the user can act on, so it also reaches them as a toast — posted back to the UI thread, since
    /// the continuation may land on the player's own thread.</summary>
    static async Task PlayCardAsync(Services svc, Action<Action> post, string uri, HomeCardKind kind)
    {
        string outcome;
        Exception? failure = null;
        try
        {
            // Item vs context is HomeCardPlayRouting's one rule (shared with the section grid): a track or episode
            // plays itself, everything else is a CONTEXT the player starts from the top of.
            if (Wavee.Features.Home.HomeCardPlayRouting.PlaysAsItem(kind))
                await svc.Player.PlayTrackAsync(uri).ConfigureAwait(false);
            else
                await svc.Player.PlayAsync(uri, 0).ConfigureAwait(false);
            outcome = "completed";
        }
        catch (OperationCanceledException) { outcome = "canceled"; }
        catch (Exception ex) { outcome = "failed"; failure = ex; }

        WaveeLog.Instance.Event(failure is null ? WaveeLogLevel.Info : WaveeLogLevel.Warning, "ui", "card.play",
            "home card play " + outcome, ex: failure,
            fields: new[]
            {
                WaveeLogField.Of("uri", uri),
                WaveeLogField.Of("kind", kind.ToString()),
                WaveeLogField.Of("outcome", outcome),
            });
        if (failure is not null)
            post(() => Toast.Show(Loc.Get(Strings.Home.PlayFailed), new ToastOptions { Severity = InfoBarSeverity.Error }));
    }

    public override Element Render()
    {
        var svc    = UseContext(Services.Slot);
        var go     = UseContext(HistoryStore.NavCtx);
        var goOrigin = UseContext(HistoryStore.GoWithOrigin);
        var homePrefs = UseContext(HomePreferences.Slot);
        int layoutVersion = homePrefs?.LayoutVersion.Value ?? 0;
        _homePrefs = homePrefs;
        _renderLayoutVersion = layoutVersion;
        var bridge = UseContext(PlaybackBridge.Slot);
        var preview = UseContext(NavPreviewStore.Slot);    // pre-load: stash the card's known cover/title for the detail page
        var sectionPreview = UseContext(HomeSectionPreviewStore.Slot);
        var acts = UseContext(ActionServices.Slot);        // card context menus (Menus.Card via CardAttach)
        var menuOverlay = UseContext(Overlay.Service);
        var lib = UseContext(LibraryBridge.Slot);          // the hero's heart
        var shellMaterial = UseContext(ShellMaterial.Slot);   // Home owns the shell material while it is the active page
        if (svc is null) return new BoxEl { Grow = 1f };

        var home = UseLoadable(Loadable<HomeFeed>.Pending(FakeData.HomeSeed));   // seed renders the loading shape; later refreshes swap Ready->Ready in place

        // ── the two auth vantages this page reasons from (bridge.AuthState, PlaybackBridge.ProjectAuthState) ──────
        // CONCLUDED: has the live-catalog attempt had its say? Everything but Connecting — Live, Offline (a resume
        // that failed against a retained credential), and SignInRequired (nothing will ever be attempted: no stored
        // credential, or the fake backend, which reports Authenticated anyway). This is HomeFeedReadiness.Classify's
        // `liveCatalogConcluded`: not "did THIS fetch get a live answer" (that can never tell "not tried yet" apart from
        // "tried and failed"), but "is there anything still worth waiting for". ProjectAuthState already folds the
        // login phase against the stored-credential check for precisely this Connecting-vs-Offline call, so it is
        // reused rather than re-derived inside StoreLibrarySource. Peek() in the helpers: they are read from background
        // poll continuations, never during Render (Render reads `.Value` once below, to subscribe).
        // AVAILABLE: is there a live catalog to fail loud against right now? Live only. Charts' Featured-null rule
        // used to be keyed on CONCLUDED, which made an Offline session throw its fail-loud ErrorState for a browse
        // that legitimately does not exist offline; the two questions are different and get different helpers.
        bool LiveCatalogConcluded() => bridge is null || bridge.AuthState.Peek() != ShellAuthState.Connecting;
        bool LiveCatalogAvailable() => bridge is not null && bridge.AuthState.Peek() == ShellAuthState.Live;
        bool catalogConcludedNow = bridge is null || bridge.AuthState.Value != ShellAuthState.Connecting;   // subscribe: the flip re-renders

        // Charts is chrome, not a home-document module — it rides its OWN resource. Five browseSection reads
        // (ChartSections.All), one Fold tile each: Featured null throws (fail-loud) UNLESS there is no live catalog,
        // in which case it degrades to an empty deck (see HomeBrowseCards.LoadChartDeckAsync) — an offline session
        // legitimately has no browse (IBrowseService's own contract for GetCategoriesAsync says the same). Later
        // shelves that come back empty are omitted. No browsePage fallback, no homeSection: see BrowseSectionRoutes.
        //
        // The fetch ARMS the moment the live-catalog attempt concludes — the same vantage the landing feed settles on
        // — and never before: a pre-GoLive read used to fire on mount, race the still-connecting session, and paint
        // either a fail-loud ErrorState or a lone "No charts right now" row the instant before GoLive filled it in.
        // Armed once per mount (monotonic): a later Offline→Connecting→Live reconnect must not flip the resource back
        // to Pending and re-skeletonize one row inside an already-revealed page. Until armed, the resource re-serves
        // its own seed (a Fold-shaped shimmer under the page skeleton, not a real read). Whether the deck has
        // CONCLUDED is one of the two chrome inputs the first reveal waits on (ChromeConcluded below), so the deck
        // paints WITH the reveal instead of shimmering inside it.
        if (catalogConcludedNow) _chartsArmed = true;
        bool chartsArmed = _chartsArmed;
        var browseSvc = svc.Browse;
        var charts = UseResource<IReadOnlyList<HomeSection>>(
            async ct => chartsArmed
                ? await HomeBrowseCards.LoadChartDeckAsync(browseSvc, ct, LiveCatalogAvailable()).ConfigureAwait(false)
                : HomeBrowseCards.ChartDeckSeed,
            seed: HomeBrowseCards.ChartDeckSeed, deps: DepKey.From(chartsArmed));

        // The notification timeline (HomeRow.Timeline → HomeTimeline) renders NotificationCenterBridge.Items, which
        // go-live primes; the row is empty until the what's-new + social feeds land and then appears inside the page.
        // "Concluded" here is "nothing in flight": Idle (never fetched — offline, the fake backend) counts as concluded
        // because nothing is coming, Loading is the one state worth a bounded wait.
        var nc = UseContext(NotificationCenterBridge.Slot);
        bool ChromeConcluded()
            => charts.Loadable.State.Peek() != (byte)LoadState.Pending
            && (nc is null
                || (nc.WhatsNewState.Peek() != NotificationFeedState.Loading
                    && nc.SocialState.Peek() != NotificationFeedState.Loading));

        var post = UsePost();
        // Home groups have substantially different heights (quick grid / hero / compact grid / shelf / editorial).
        // Hoist one measured extent table so the viewport can correct and anchor rows while recycling offscreen groups.
        var homeLayout = UseMemo(static () => new HomeFeedVirtualLayout(), DepKey.Empty);
        // The FACET page is a different DOCUMENT with a different row table (the server's sections, in server order),
        // so it keeps its own extent table rather than sharing the landing's: one table describing two shapes would
        // reseed on every All<->facet swap and throw away every measured correction belonging to both.
        var facetLayout = UseMemo(static () => new HomeFacetVirtualLayout(), DepKey.Empty);

        // The background home-refresh loop, tied to this component's lifetime. Its Reactive.OnCleanup fires on unmount
        // (KeepAlive eviction / a page whose cache entry was evicted) and before each re-run. Without it, each cold
        // remount of Home leaked an orphaned 60s PeriodicTimer loop that COMPOUNDED over a long session. Mirrors the
        // LyricsTicker lifecycle pattern (Features/Player/LyricsView.cs).
        //
        // The two signals it reads are the cache-published FEED EPOCH (Services.HomeFeedEpoch) and bridge.AuthState. A
        // bump — OR an auth-state flip — re-runs this effect, which cancels the old loop and starts a new one, i.e. one
        // immediate re-read plus a fresh 60 s cadence, never a poll in render and never a subscription to anything hot.
        // This is what reaches a KeepAlive-PARKED page: park runs no cleanups, so the effect is still live, re-reads on
        // the bump, and the page's deferred render replays the fresh feed the instant it is activated. Reading epoch
        // HERE rather than in Render is deliberate — it is a refresh trigger, not a rendered value.
        //
        // AuthState is the fix for the "Home never leaves the skeleton" hang: go-live publishes no epoch bump for the
        // session's OPENING read (LiveHomeCache in SpotifyOnlineCatalog.cs deliberately withholds the bump on the very
        // first post-login identity — "a bump nobody needed"), so without tracking AuthState too a withheld Placeholder
        // answer could sit until the next ordinary 60 s tick happened to land after go-live — sometimes effectively
        // never, if that tick's own read raced ahead of the session. Reading `.Value` here makes the flip itself the
        // trigger: the loop restarts and re-reads the instant the session goes live, without waiting on the clock.
        //
        // The vantage is captured at the loop's START, not re-read per answer: a loop started while Connecting can
        // only ever have read the resident shelves (its online catalog was still the offline stub), so its answers are
        // provisional however late they land — a read that was in flight across the flip must not be mistaken for the
        // settled feed just because the flip beat its continuation. The flip restarts the loop, and THAT loop's read
        // is the one that settles the page.
        var feedEpoch = svc.HomeFeedEpoch;
        Context.UseSignalEffect(() =>
        {
            int epoch = feedEpoch.Value;
            bool concludedAtStart = bridge is null || bridge.AuthState.Value != ShellAuthState.Connecting;   // tracked
            var cts = new CancellationTokenSource();
            StartHomeRefreshLoop(svc, home, post, epoch,
                (e, feed) => ApplyFeed(svc, home, e, feed, concludedAtStart, ChromeConcluded), cts.Token);
            Reactive.OnCleanup(() => { cts.Cancel(); cts.Dispose(); });
        });

        // The HARD FALLBACK: Home must never sit on the skeleton indefinitely, no matter what upstream timing bug
        // might withhold every ordinary read. 8 s after MOUNT (DepKey.Empty — armed once, not on every epoch/auth
        // re-run) — if the region is still Pending — force-publish whatever feed the refresh loop has most recently
        // seen (even a withheld Placeholder), or HomeFeed.Empty if nothing has landed at all yet. `home` is captured
        // by reference in the loadable, and UseTimeout always invokes the LATEST render's closure, so this reads
        // current state at fire time regardless of how many renders happened in between.
        UseTimeout(() => ForceReleaseIfStillPending(svc, home, ChromeConcluded), (float)HomeFeedReadiness.ForceReleaseMs, DepKey.Empty);

        // ── the FIRST reveal waits for the chrome (HomeRevealGate) ──────────────────────────────────────────────
        // A settled feed that landed before the Charts deck / the notification feeds concluded is HELD (the skeleton
        // stays up) and published from here — the instant the last of them concludes (this effect tracks their
        // signals), or when the cap elapses (the timeout, re-armed on every hold via _heldVersion), whichever is
        // first. Both funnel through the one gate, which is idempotent, so a chrome flip and the cap firing in the
        // same tick cannot double-publish. After the reveal neither path does anything: HomeRevealGate.Tick returns
        // null once revealed, and every later publish is a Ready→Ready swap straight from ApplyFeed.
        int heldVersion = _heldVersion.Value;   // subscribe: a hold re-renders, which re-arms the cap below
        Context.UseSignalEffect(() =>
        {
            _ = charts.Loadable.State.Value;
            if (nc is not null) { _ = nc.WhatsNewState.Value; _ = nc.SocialState.Value; }
            _ = _heldVersion.Value;
            PublishHeld(home, ChromeConcluded, force: false);
        });
        UseTimeout(() => PublishHeld(home, ChromeConcluded, force: false),
            (float)HomeFeedReadiness.ChromeSettleMs, DepKey.From(heldVersion));

        // ── the shell MATERIAL: Home's three-wash composition (ShellMaterial / ShellMaterialLayer) ────────────────
        // Home publishes the WASH arm (Tint: null); detail pages publish the flat tint arm. The SEED and the loaded feed
        // go through this ONE path: the seed's cards carry no accent and no artwork, so it resolves to an empty wash and
        // the shell keeps its bare deterministic ground while Home loads — no placeholder colour, ever.
        _ = AppearancePrefs.Epoch.Value;   // the Settings toggle applies LIVE (the ColorWashesEnabled idiom)
        bool colorWashesDisabled = !svc.Settings.Get(WaveeSettings.ColorWashesEnabled);
        var feedNow = home.Value.Value;    // subscribe → re-publish when the feed lands, and on every refresh swap
        var washCards = HomeWashSource.Sources(feedNow);
        // LATE GRADING: watch only the (at most three) selected artworks that are still waiting on the plane, so a
        // landed grading re-renders this page and re-publishes the wash. Never CoverColorPlane.Epoch — every scrolling
        // grid batch bumps that, and Home is the page with the most grids. A null/empty url is NOT passed to Watch:
        // the plane answers an unkeyable url with its global epoch, which is exactly the subscription being avoided.
        WatchArtwork(HomeWashSource.PlaneUrl(washCards.Hero));
        WatchArtwork(HomeWashSource.PlaneUrl(washCards.Weekly));
        WatchArtwork(HomeWashSource.PlaneUrl(washCards.Mix));
        var picks = colorWashesDisabled ? default : HomeWashSource.Select(washCards, Surfaces.ChromeSchemeFor);
        // Disabled ⇒ no material at all (Wash null, Tint null): Home still CLAIMS ownership, so the previous page's
        // tint is cleared and only the deterministic ground remains.
        HomeWash? wash = colorWashesDisabled
            ? null
            : new HomeWash(Layer(picks.Hero), Layer(picks.Weekly), Layer(picks.Mix));

        // Owner-gated exactly like DetailShell: a page clears the material only while it is still the owner, so a
        // "park Home + activate the destination" nav lands on the destination's material whichever effect fires first.
        void SetWash(HomeWash? w)
        {
            if (shellMaterial is not null) shellMaterial.Value = new ShellMaterialState(_washOwner, null, w);
        }
        void ClearWash()
        {
            if (shellMaterial is not null && ReferenceEquals(shellMaterial.Peek().Owner, _washOwner))
                shellMaterial.Value = default;
        }
        // SET on mount + on any real colour/artwork change (UseEffect, keyed on the resolved legs) and on REACTIVATION
        // (a KeepAlive-cached page does not re-run its mount effect); CLEAR on park…
        UseEffect(() => SetWash(wash),
            DepKey.From(HashCode.Combine(colorWashesDisabled, HomeWashSource.Fingerprint(picks))));
        UseActivation(
            onActivated: () =>
            {
                SetWash(wash);
                // The epoch COMPARE, not a refetch. An epoch this page has not applied means the cache superseded the
                // feed on screen, so re-read once; an epoch it HAS applied means nothing is known to have moved, and
                // the only thing worth spending is the cheap head probe — which resolves nothing itself: if the
                // daylist's revision has advanced it publishes the epoch, and the ordinary refresh effect below does
                // the read. One mechanism, one call, and none of it on a cadence or in a render.
                int at = feedEpoch.Peek();
                if (at != _gate.AppliedEpoch)
                    _ = RefreshHomeOnce(svc, post, failIfInitial: false, at,
                        (e, feed) => ApplyFeed(svc, home, e, feed, LiveCatalogConcluded(), ChromeConcluded), home, default);
                else if (svc.HomeFeedRevalidate is { } revalidate)
                    _ = revalidate(default);
            },
            onDeactivated: ClearWash);
        // …and on UNMOUNT too, because onDeactivated fires only on PARK: a nav that evicts Home without parking it would
        // otherwise leave a wash owned by a gone page. Owner-gated, so it can never clobber the next page's material.
        UseEffect(() => (Action?)ClearWash, DepKey.Empty);
        // The in-flight facet read is page state for the same reason: an unmount must not leave a request racing to
        // publish into a loadable whose page is gone, nor a live CancellationTokenSource behind it.
        UseEffect(() => (Action?)(() =>
        {
            _facetCts?.Cancel();
            _facetCts?.Dispose();
            _facetCts = null;
        }), DepKey.Empty);

        string? name = bridge?.User.Value?.DisplayName;     // subscribe → greeting refreshes on login

        // Home is a PLACE the Browse-family pages it opens are reached from, so every drill out of it hands over this
        // origin (HistoryStore.GoWithOrigin, the BrowsePage precedent): the masthead then reads Home › Browse › X
        // instead of claiming a Browse visit that never happened (DrillTrail.Compose). NavCtx would write a NULL
        // origin — and NavOriginStore treats null as a delete, so a Home visit used to erase an earlier Browse origin.
        var homeOrigin = new NavOrigin(Loc.Get(Strings.Nav.Home), "home", null);
        void GoFromHome(string key, string? arg) => goOrigin(key, arg, homeOrigin);

        // The card-open decision lives in HomeCardNav (shared with HomeSectionPage — the two drifted apart once already,
        // over the Liked branch).
        void NavCard(HomeCard c) => HomeCardNav.Open(c, preview, go, uri => _ = svc.Player.PlayTrackAsync(uri));

        // Awaited, not fire-and-forget (finding #8): a card ▶ whose context resolved to nothing used to vanish without
        // a trace — the controller logs the empty resolve at Info, and nothing tied it to the press. PlayCardAsync
        // writes one `card.play` line per press naming the card and how the request ended, and a thrown failure
        // reaches the user as a toast instead of dying in a discarded Task.
        void PlayCard(HomeCard c) => _ = PlayCardAsync(svc, post, c.Uri, c.Kind);

        // Every home card is a drag SOURCE for the entity it stands for — drop it on a sidebar playlist to add its
        // tracks, on a folder to file it, on the pin band to pin it. The payload factory is gesture-COLD (it runs once,
        // at promotion), so it reads `acts` live rather than snapshotting anything here.
        // TRACK and EPISODE cards are deliberately excluded: the feed carries only a uri for either — no Track object, and
        // no by-uri track read exists — so the payload could be neither pinned nor deposited. A drag every surface
        // refuses is worse than no drag at all. (An audiobook/podcast IS draggable: it maps to a Show, which the sidebar
        // and pin band both accept.)
        DragSource? CardDrag(HomeCard c)
            => c.Kind is HomeCardKind.Track or HomeCardKind.Episode
                ? null
                : Drag.Source(WaveeDragKinds.Resource,
                    () => WaveeResourceDragPayload.ForEntity(WaveeDragKindMap.Of(c.Kind), c.Uri, c.Title, c.Image, acts));

        // Per-card callbacks as factories, so every module shell stays a pure function of (group, callbacks) and never
        // needs the page's services threaded through it.
        Action NavOf(HomeCard c) => () => NavCard(c);
        Action PlayOf(HomeCard c) => () => PlayCard(c);

        // Drag + the context menu, applied once per card by the shells. Both belong to the ENTITY, not the skin, so they
        // survive the whole card vocabulary being re-authored: every module gets right-click and drag-out for free.
        HomeCardChrome ChromeOf(HomeCard c) => new(
            CardDrag(c),
            Menus.CardAttach(acts, menuOverlay, c.Uri, c.Title, c.Image, PlainSubtitle(c),
                circular: c.Kind == HomeCardKind.Artist));

        // "{n} songs · by {owner}" — the meta line the editorial feature and the feed cards close with.
        // The owner comes from Meta.OwnerName, NOT from Subtitle: Subtitle is `description ?? ownerName`, so a playlist
        // with a description made this read "50 songs · by <the entire description, tags and all>".
        string CardMeta(HomeCard c)
        {
            int n = c.Meta?.TrackCount ?? 0;
            string count = c.Kind == HomeCardKind.Episode
                ? HomeCards.Duration(c.Meta?.DurationMs ?? 0)
                : n > 0 ? Strings.Detail.SongCount(n) : "";
            string? owner = c.Meta?.OwnerName;
            if (count.Length == 0) return owner ?? "";
            return owner is { Length: > 0 } o && c.Kind != HomeCardKind.Episode
                ? Strings.Home.SongsBy(count, o)
                : count;
        }

        // The recents rail's caption names the entity TYPE. It used to fall through to Subtitle, which for a playlist is
        // its owner — so every tile in the rail said "Spotify" and the rail explained nothing.
        string KindLabel(HomeCard c) => c.Kind switch
        {
            HomeCardKind.Artist => Loc.Get(Strings.Home.Artist),
            HomeCardKind.Album => Loc.Get(Strings.Home.Album),
            HomeCardKind.Podcast or HomeCardKind.Audiobook => Loc.Get(Strings.Podcast.Show),
            HomeCardKind.Episode => Loc.Get(Strings.Podcast.Episodes),
            HomeCardKind.Track => Loc.Get(Strings.Detail.Column.Song),
            // Liked deliberately falls through to "Playlist": this caption names the entity TYPE under a title that
            // already reads "Liked Songs", and the arm that answered LikedSongs here made the tile say its own name
            // twice. (It was unreachable until the recents mapper started classifying the collection correctly.)
            _ => Loc.Get(Strings.Nav.Playlist),
        };

        // A description flattened for the string-typed consumers — a context-menu subtitle, a tooltip. RichText handles
        // the rendered cases; these hold a string and would otherwise show the raw markup.
        static string? PlainSubtitle(HomeCard c) => SpotifyExportMapper.ToPlainText(c.Subtitle);

        void OpenSection(HomeSection section)
        {
            string identity = section.Uri is { Length: > 0 } uri
                ? uri
                : HomeSectionRoutes.LocalPrefix + HomeModuleLayout.SectionSetKey([section]);
            string route = HomeSectionRoutes.Page(identity);
            sectionPreview?.Set(route, section);
            GoFromHome(route, section.Title);
        }

        void OpenBrowseSection(HomeSection s) =>
            HomeCardNav.OpenBrowseSection(s, preview, sectionPreview, go, uri => _ = svc.Player.PlayTrackAsync(uri),
                origin: homeOrigin, goOrigin: goOrigin);

        void OpenLanding(HomeLandingModule module)
        {
            var g = module.Group;
            OpenSection(module.PrimarySection ?? new HomeSection(
                g.Uri, g.Title, g.Subtitle, g.Cards,
                Math.Max(g.TotalCount, g.Cards.Count), g.Cards.Count));
        }

        void NavUri(HomeFeed feed, string key)
        {
            if (HomeSectionRoutes.Is(key))
            {
                string uri = HomeSectionRoutes.UriOf(key);
                var sections = feed.Sections;
                if (sections is not null)
                    for (int i = 0; i < sections.Count; i++)
                        if (string.Equals(sections[i].Uri, uri, StringComparison.Ordinal))
                        {
                            OpenSection(sections[i]);
                            return;
                        }
            }
            go(key, null);
        }

        // The Concert Hub destination is the final virtual row. It is mounted only when the measured list realizes the
        // tail of the feed instead of living permanently below every Spotify module.
        Element concerts = ConcertUi.WideEditorialDestination(
            artwork: null,
            style: EditorialArtStyle.Concert,
            eyebrow: Loc.Get(Strings.Concerts.LiveMusic),
            title: Loc.Get(Strings.Concerts.HomeTitle),
            subtitle: Loc.Get(Strings.Concerts.HomeSubtitle),
            actionLabel: Loc.Get(Strings.Concerts.Explore),
            onClick: () => GoFromHome(Wavee.Features.Concerts.ConcertRoutes.Hub, Loc.Get(Strings.Concerts.Title)))
            with { Key = "home-concerts-editorial" };

        // The Browse destination, in the SAME editorial voice as the concert card directly above it — two calm
        // full-width destinations closing the feed, rather than one and an abrupt end.
        Element browse = ConcertUi.WideEditorialDestination(
            artwork: null,
            style: EditorialArtStyle.Browse,
            eyebrow: Loc.Get(Strings.Browse.Eyebrow),
            title: Loc.Get(Strings.Browse.HomeTitle),
            subtitle: Loc.Get(Strings.Browse.HomeSubtitle),
            actionLabel: Loc.Get(Strings.Browse.ExploreAll),
            onClick: () => GoFromHome(BrowseRoutes.Home, null))
            with { Key = "home-browse-editorial" };

        // Both destinations ride the final virtual row together, so the tail mounts once.
        Element tail = new BoxEl
        {
            Direction = 1, Gap = Spacing.XL, MinWidth = 0f,
            Children = [ concerts, browse ],
        };

        void WarmGroup(HomeGroup g)
        {
            // Preview lookup and image decode follow the realized window. The old eager whole-feed pass enqueued every
            // cover before the first content frame, largely defeating the benefit of recycling the group trees.
            // The hover peek is primed for DiscoverFeed, not Featured: feedBaselineLookup only answers for the
            // single-item baseline recommendations, which now coalesce into the discover feed. Featured is editorial
            // playlists, which that batch has nothing to say about.
            if (g.Kind == HomeGroupKind.DiscoverFeed)
                Wavee.SpotifyLive.HomeBaselinePreviews.Prime(g.Cards.Select(c => c.Uri));
            // The decode target per module — a cover decoded for a 32px station row must not be fetched at 512.
            int px = g.Kind switch
            {
                HomeGroupKind.RadioDial or HomeGroupKind.QueueList => 64,
                HomeGroupKind.QuickGrid => 64,
                HomeGroupKind.RatedShelf or HomeGroupKind.ChipCards or HomeGroupKind.WeeklyPair
                    or HomeGroupKind.DiscoverFeed => 128,
                HomeGroupKind.Hero => 256,
                HomeGroupKind.MixBand => 64,
                HomeGroupKind.Featured => 512,
                _ => 256,
            };
            var cards = g.Cards;
            for (int i = 0; i < cards.Count; i++)
                if (cards[i].Image?.Url is { Length: > 0 } url) PrefetchImage(url, px);
        }

        Element VirtualHome(HomeFeed feed)
        {
            HomeImageDiagnostics.LogFeed(feed);
            HomeFeedDiagnostics.LogModules(feed);
            var landing = Landing(feed);
            homeLayout.Configure(landing);
            // The layout object can't see the loadable itself (it is hoisted OUTSIDE Render, per-page state that
            // survives a refresh), so mirror it in as a byte + an empty bit — the same pattern _sectionDeckCount uses
            // for the section directory. `.Value` on both reads SUBSCRIBE (not Peek), so the row re-estimates the
            // instant the fetch resolves, not on the next unrelated re-render.
            homeLayout.SetChartsState((byte)charts.Loadable.State.Value, charts.Loadable.Value.Value.Count == 0);

            // Rows come from the landing AFTER hide+reorder. A hidden Hero is omitted (no empty slot) and a
            // user reorder is the order RowAt / KeyAt / Estimate all switch on.
            var rows = homeLayout.Rows;

            // This viewport is the UNFILTERED document, and only that: a facet is not a filtered view of the landing
            // but a different document with a different row table (VirtualFacet), reached through the content arm
            // below. So every identity on this page is the bare "home" tag it has always been.

            // EVERY row is a component (Responsive.Of → ResponsiveBox), and a component's build closure FREEZES at mount
            // (the component-props contract): a parent re-render that re-runs RenderItem over an unchanged window finds
            // the same type + key at the slot and diffs it in place — the closure it mounted with, over the feed it
            // mounted with, stays. That is what made every Ready→Ready swap (a facet, the 60 s poll, a daylist
            // rollover) invisible: the shelves kept describing the FIRST feed while the chip row, which reads a signal,
            // moved on. Two things keep a row honest now:
            //   1. its KEY — module rows carry the group's content fingerprint, so a module that changed REMOUNTS and
            //      one that did not is left alone; chrome rows (Chips / Artists / Timeline / Charts / Sections / Tail)
            //      carry the row NAME alone, which is what lets the chip row keep ONE key across the whole page family
            //      — the facet viewport describes its chip row under the very same "home:row:Chips", so switching
            //      All<->facet re-describes it in place and never replays the fused pill's animation;
            //   2. its build closure reads the LIVE feed off the loadable (RowAt), so a same-key row re-describes from
            //      the current landing — the chip row's greeting/has-hero, the section deck — without a remount.
            string KeyAt(int index)
            {
                var row = rows[index];
                var g = FeedGroup(landing, row);
                return g is null
                    ? "home:row:" + row
                    : "home:row:" + row + ":" + HomeModuleLayout.SourceGroupKey(g);
            }

            Element RowAt(int index)
            {
                var row = rows[index];
                string key = KeyAt(index);
                float tailBottom = PlayerDock.Reserve + Spacing.XXL;
                return Responsive.Of(width =>
                {
                    // `.Value` SUBSCRIBES this row's ResponsiveBox to the feed: a swap re-renders the realized rows from
                    // the landing that is actually on the page, not the one the closure was born with.
                    var liveFeed = home.Value.Value;
                    var liveLanding = Landing(liveFeed);
                    return HomeRowShell(RenderRow(liveFeed, liveLanding, row), key,
                        // The first row opens on the page gutter, not on half of it: 24 top matches the 36 sides closely
                        // enough to read as one inset, where 12 read as "the page starts before it starts".
                        row == HomeRow.Chips ? Spacing.XXL : 0f,
                        row == HomeRow.Tail ? tailBottom : RowHasContent(liveLanding, row) ? HomeModuleLayout.Gap(width) : 0f);
                }, fallback: HomeModuleLayout.FallbackWidth) with { Key = key };
            }

            return Virtual.Measured(rows.Length, homeLayout, RowAt, KeyAt, overscan: 1) with
            {
                Grow = 1f,
                Shrink = 1f,
                MinHeight = 0f,
                ScrollKey = "home",
                OnVisibleRange = (first, end) =>
                {
                    for (int i = first; i < end && i < rows.Length; i++)
                        if (FeedGroup(landing, rows[i]) is { } g) WarmGroup(g);
                },
            };
        }

        // -- the FACET viewport -------------------------------------------------------------------------------
        // A facet chip does not filter the landing page - it asks the server a different question and gets a different
        // DOCUMENT back, which Spotify renders as a plain ordered list of that document's sections. The authored
        // landing is the wrong composition for it, and demonstrably lies about it: "Podcasts" answers four separately
        // titled show shelves ("Your shows", "Podcasts for you", ...) plus ~20 single-card baseline sections, and the
        // landing merged the four shelves into one "Podcasts" module, the baselines into one feed, and still offered a
        // "Your top artists" chrome row that the facet never asked for.
        //
        // So this viewport walks HomeFeed.Sections in server order (HomeFacetProjection) and renders one module per
        // section, each wearing the SERVER's title and drilling into its own section page. The only thing coalesced is
        // a run of consecutive baseline sections, which is twenty one-card shelves otherwise.
        Element VirtualFacet(HomeFeed feed)
        {
            HomeFeedDiagnostics.LogModules(feed);
            var facetRows = FacetRows(feed);
            facetLayout.Configure(facetRows);
            int count = facetRows.Count + 2;                     // chips + one row per section + tail

            // The CHIPS key is deliberately byte-identical to VirtualHome's: the chip row is the one element on the
            // page that survives an All<->facet swap, and a remount a second after the tap would replay the fused
            // pill's morph. Section rows carry the facet, their ORDINAL (two sections may share a title) and the
            // group's content fingerprint, so a section that changed remounts and one that did not is left alone.
            string facetTag = "home:" + feed.Facet;
            string KeyAt(int index)
            {
                if (index == 0) return "home:row:Chips";
                if (index > facetRows.Count) return "home:row:Tail";
                var row = facetRows[index - 1];
                return facetTag + ":sec:" + (index - 1) + ":" + HomeModuleLayout.SourceGroupKey(row.Group);
            }

            Element RowAt(int index)
            {
                string key = KeyAt(index);
                bool chips = index == 0;
                bool isTail = index > facetRows.Count;
                var mounted = chips || isTail ? null : facetRows[index - 1];
                float tailBottom = PlayerDock.Reserve + Spacing.XXL;
                return Responsive.Of(width =>
                {
                    // `.Value` SUBSCRIBES this row to the feed, so a Ready->Ready swap (the 60 s poll, a re-read of the
                    // same facet) re-describes the realized rows from the feed that is actually on the page instead of
                    // leaving them frozen on the one their closure was born with. The row's ROLE stays as mounted: a
                    // feed whose shape moved produces a different key above and remounts the row outright.
                    var liveFeed = home.Value.Value;
                    if (chips)
                        // No hero band on a facet, so the greeting has nowhere else to go: a null landing is what makes
                        // GreetingBlock render its standalone form above the chip strip.
                        return HomeRowShell(GreetingBlock(name, liveFeed, home, svc, post, LiveHasHero(liveFeed), go), key,
                            Spacing.XXL, HomeModuleLayout.Gap(width));
                    if (isTail) return HomeRowShell(tail, key, 0f, tailBottom);
                    var liveRows = FacetRows(liveFeed);
                    var row = index - 1 < liveRows.Count ? liveRows[index - 1] : mounted!;
                    return HomeRowShell(RenderFacetRow(liveFeed, row), key, 0f, HomeModuleLayout.Gap(width));
                }, fallback: HomeModuleLayout.FallbackWidth) with { Key = key };
            }

            return Virtual.Measured(count, facetLayout, RowAt, KeyAt, overscan: 1) with
            {
                Grow = 1f,
                Shrink = 1f,
                MinHeight = 0f,
                // Per facet: the previous facet's offset belongs to a document that no longer exists, so it is dropped
                // rather than landing 3000px down this one.
                ScrollKey = facetTag,
                OnVisibleRange = (first, end) =>
                {
                    for (int i = Math.Max(1, first); i < end && i - 1 < facetRows.Count; i++)
                        WarmGroup(facetRows[i - 1].Group);
                },
            };
        }

        // One server section, in the shape the projection decided its cards name. The dispatcher for the facet page,
        // exactly as RenderRow is for the landing. `openSection` drills into the section the row came FROM, so a shelf's
        // chevron opens the server's own "show all" page for it; the coalesced discover feed is the one row that is not
        // a single section, so it has none.
        Element RenderFacetRow(HomeFeed feed, HomeFacetRow row)
        {
            var g = row.Group;
            void Navigate(string key) => NavUri(feed, key);
            Action<HomeGroup>? open = row.Section is { } section ? _ => OpenSection(section) : null;
            switch (row.Kind)
            {
                case HomeFacetRowKind.Hero:
                {
                    var card = g.Cards[0];
                    return HomeModules.SourceModule(g,
                        Responsive.Of(w => HomeCards.HeroBand(card, HeroEyebrow(card, feed), CardMeta(card),
                            () => PlayCard(card), () => ShuffleCard(card), () => NavCard(card),
                            () => lib?.ToggleSaved(card.Uri, card.Title),
                            ChromeOf(card).Menu,
                            w),
                            fallback: 900f),
                        open);
                }
                case HomeFacetRowKind.Recents:
                    // Recents drills to its OWN page (the "recents" destination, backed by a different endpoint), never
                    // through OpenSection - the same rule the landing's Recents row follows.
                    return HomeModules.Recents(g, NavOf, PlayOf, KindLabel, ChromeOf, () => go("recents", null));
                case HomeFacetRowKind.Podcasts:
                    return HomeModules.Podcasts(g, NavOf, PlayOf, ChromeOf, open);
                case HomeFacetRowKind.Audiobooks:
                    return HomeModules.SplitSingle(HomeModules.Audiobooks(g, NavOf, ChromeOf, open));
                case HomeFacetRowKind.Episodes:
                    return HomeModules.SplitSingle(HomeModules.UpNext(g, NavOf, ChromeOf, open));
                case HomeFacetRowKind.Feed:
                    // The coalesced baseline run: one paged browse module wearing the app's own copy. It is not one
                    // section, so there is nothing honest to drill into.
                    return HomeModules.Feed(g, NavOf, PlayOf, ChromeOf, Navigate, null);
                default:
                    return HomeModules.Shelf(g, NavOf, PlayOf, ChromeOf, open);
            }
        }

        // Which feed group (if any) a row renders. The Chips / Artists / Timeline / Tail rows are service- or
        // chrome-driven and have none.
        HomeGroup? FeedGroup(HomeLanding landing, HomeRow row) => row switch
        {
            HomeRow.Hero => landing.Get(HomeGroupKind.Hero)?.Group,
            HomeRow.Weekly => landing.Get(HomeGroupKind.WeeklyPair)?.Group,
            HomeRow.Quick => landing.Get(HomeGroupKind.QuickGrid)?.Group,
            HomeRow.Recents => landing.Get(HomeGroupKind.Recents)?.Group,
            HomeRow.MixBand => landing.Get(HomeGroupKind.MixBand)?.Group,
            HomeRow.ChipCards => landing.Get(HomeGroupKind.ChipCards)?.Group,
            HomeRow.Radio => landing.Get(HomeGroupKind.RadioDial)?.Group,
            HomeRow.Podcasts => landing.Get(HomeGroupKind.PodcastShelf)?.Group,
            HomeRow.Editorial => landing.Get(HomeGroupKind.Featured)?.Group,
            HomeRow.Feed => landing.Get(HomeGroupKind.DiscoverFeed)?.Group,
            // The split row is sized by whichever of its two modules is taller; the estimator asks for both.
            HomeRow.EpisodesAndBooks => landing.Get(HomeGroupKind.QueueList)?.Group
                ?? landing.Get(HomeGroupKind.RatedShelf)?.Group,
            HomeRow.Queue => landing.Get(HomeGroupKind.QueueList)?.Group,
            HomeRow.Books => landing.Get(HomeGroupKind.RatedShelf)?.Group,
            _ => null,
        };

        bool RowHasContent(HomeLanding landing, HomeRow row) => row switch
        {
            // Service rows add their gap only after their async data is non-empty; an empty component contributes 0.
            HomeRow.Artists or HomeRow.Timeline => false,
            HomeRow.Chips or HomeRow.Tail => true,
            // Charts is present in EVERY state (Pending/Ready/Failed all paint something — that's the point of
            // Skel.Region's onFailed/onEmpty arms), so it always contributes its module gap.
            HomeRow.Charts => true,
            HomeRow.Sections => landing.Sections.Count > 0,
            _ => FeedGroup(landing, row) is not null,
        };

        Element RenderRow(HomeFeed feed, HomeLanding landing, HomeRow row)
        {
            void Navigate(string key) => NavUri(feed, key);
            Action<HomeGroup>? Drill(HomeGroupKind kind)
                => landing.Get(kind) is { } m ? _ => OpenLanding(m) : null;
            switch (row)
            {
                case HomeRow.Chips:
                    return GreetingBlock(name, feed, home, svc, post, LiveHasHero(feed), go);
                case HomeRow.Hero:
                    return landing.Get(HomeGroupKind.Hero) is { Group: { } h }
                        ? HomeModules.SourceModule(h,
                            Responsive.Of(w => HomeCards.HeroBand(h.Cards[0], HeroEyebrow(h.Cards[0], feed), CardMeta(h.Cards[0]),
                                () => PlayCard(h.Cards[0]), () => ShuffleCard(h.Cards[0]), () => NavCard(h.Cards[0]),
                                () => lib?.ToggleSaved(h.Cards[0].Uri, h.Cards[0].Title),
                                ChromeOf(h.Cards[0]).Menu,
                                w),
                                fallback: 900f))
                        : new BoxEl();
                case HomeRow.Weekly:
                    return FeedGroup(landing, row) is { } weekly
                        ? HomeModules.WeeklyPair(weekly, NavOf, ChromeOf, Drill(HomeGroupKind.WeeklyPair)) : new BoxEl();
                case HomeRow.Quick:
                    return FeedGroup(landing, row) is { } quick
                        ? HomeModules.Quick(quick, NavOf, PlayOf, ChromeOf, Drill(HomeGroupKind.QuickGrid)) : new BoxEl();
                case HomeRow.Recents:
                    // Recents has a page of its own (ContentHost's "recents" arm), backed by
                    // /playlist/v2/list/recents/page rather than by the home document, so it navigates to that route
                    // and never through OpenSection. Armed UNCONDITIONALLY: the landing projection's Recents group
                    // carries a null Uri, and the destination's availability has nothing to do with this shelf's payload.
                    // The strip still pages in place — pager is reserved for horizontal shelves.
                    return FeedGroup(landing, row) is { } recents
                        ? HomeModules.Recents(recents, NavOf, PlayOf, KindLabel, ChromeOf, () => go("recents", null))
                        : new BoxEl();
                case HomeRow.MixBand:
                    return FeedGroup(landing, row) is { } mixes
                        ? HomeModules.MixBand(mixes, NavOf, ChromeOf, Drill(HomeGroupKind.MixBand)) : new BoxEl();
                case HomeRow.Artists:
                    return Embed.Comp(() => new HomeArtistRow());
                case HomeRow.ChipCards:
                    return FeedGroup(landing, row) is { } chips
                        ? HomeModules.ChipCards(chips, NavOf, ChromeOf, Navigate, Drill(HomeGroupKind.ChipCards)) : new BoxEl();
                case HomeRow.Radio:
                    return FeedGroup(landing, row) is { } radio
                        ? HomeModules.Radio(radio, NavOf, PlayOf, ChromeOf, Drill(HomeGroupKind.RadioDial)) : new BoxEl();
                case HomeRow.EpisodesAndBooks:
                {
                    var episodes = landing.Get(HomeGroupKind.QueueList)?.Group;
                    var books = landing.Get(HomeGroupKind.RatedShelf)?.Group;
                    if (episodes is null && books is null) return new BoxEl();
                    Element left = episodes is null ? new BoxEl()
                        : HomeModules.UpNext(episodes, NavOf, ChromeOf, Drill(HomeGroupKind.QueueList));
                    Element right = books is null ? new BoxEl()
                        : HomeModules.Audiobooks(books, NavOf, ChromeOf, Drill(HomeGroupKind.RatedShelf));
                    if (episodes is null) return HomeModules.SplitSingle(right);
                    if (books is null) return HomeModules.SplitSingle(left);
                    return HomeModules.SplitEven(left, right);
                }
                case HomeRow.Queue:
                    return landing.Get(HomeGroupKind.QueueList)?.Group is { } queueOnly
                        ? HomeModules.SplitSingle(HomeModules.UpNext(queueOnly, NavOf, ChromeOf, Drill(HomeGroupKind.QueueList)))
                        : new BoxEl();
                case HomeRow.Books:
                    return landing.Get(HomeGroupKind.RatedShelf)?.Group is { } booksOnly
                        ? HomeModules.SplitSingle(HomeModules.Audiobooks(booksOnly, NavOf, ChromeOf, Drill(HomeGroupKind.RatedShelf)))
                        : new BoxEl();
                case HomeRow.Timeline:
                    return Embed.Comp(() => new HomeTimeline());
                case HomeRow.Podcasts:
                    return FeedGroup(landing, row) is { } podcasts
                        ? HomeModules.Podcasts(podcasts, NavOf, PlayOf, ChromeOf, Drill(HomeGroupKind.PodcastShelf)) : new BoxEl();
                case HomeRow.Charts:
                    return Skel.Region(
                        charts.Loadable,
                        content: list => HomeModules.FoldDeck(list, Loc.Get(Strings.Home.Charts), OpenBrowseSection,
                            openHeader: () => GoFromHome(Wavee.Features.Browse.BrowseRoutes.Page(Wavee.Features.Browse.ChartPages.Charts), Loc.Get(Strings.Home.Charts))),
                        isEmpty: list => list.Count == 0,
                        onEmpty: () => EmptyState.Compact(Loc.Get(Strings.Home.ChartsEmpty)),
                        onFailed: () => ErrorState.Build(charts.Loadable.Error, onRetry: charts.Refresh),
                        reveal: SkelReveal.None, smoothResize: false);
                case HomeRow.Sections:
                    return landing.Sections.Count == 0 ? new BoxEl()
                        : HomeModules.FoldDeck(landing.Sections, Loc.Get(Strings.Home.Sections), OpenSection);
                case HomeRow.Editorial:
                    return FeedGroup(landing, row) is { } editorial
                        ? HomeModules.Editorial(editorial, NavOf, PlayOf, CardMeta, ChromeOf, Navigate,
                            Drill(HomeGroupKind.Featured)) : new BoxEl();
                case HomeRow.Feed:
                    return FeedGroup(landing, row) is { } discover
                        ? HomeModules.Feed(discover, NavOf, PlayOf, ChromeOf, Navigate, Drill(HomeGroupKind.DiscoverFeed)) : new BoxEl();
                default:
                    return tail;
            }
        }

        // The page gutter is Spacing.PageWide (36) — the WinUI NavigationView content margin every other page in the app
        // already uses — not the 24 this page had; and the row stops growing at WaveeSize.PageMaxW and centres, the same
        // measure DetailShell and ArtistPage cap their two-column row at. The cap lives on the ROW rather than on the
        // virtual list on purpose: the list keeps measuring at the full cross size (so the scrollbar stays at the window
        // edge and the extent table is unaffected) while the content column stops chasing an ultra-wide display.
        Element HomeRowShell(Element child, string contentKey, float top, float bottom) => new BoxEl
        {
            Direction = 0, Justify = FlexJustify.Center, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = WaveeSize.PageMaxW,
                    Padding = new Edges4(Spacing.PageWide, top, Spacing.PageWide, bottom),
                    // Home is a heterogeneous virtual list: no two module shapes share a recyclable subtree. Keep this
                    // cheap row shell recyclable, but key its content so a shell reused for another row replaces the old
                    // subtree instead of positionally rebinding incompatible element trees.
                    Children = [ child with { Key = contentKey } ],
                },
            ],
        };

        // "Good morning, Christos · your daylist" — the greeting lives HERE, as the hero's eyebrow, because the prototype
        // has no standalone greeting block: a page that opens with two stacked text blocks before any content wastes its
        // best row. GreetingBlock keeps the standalone form for the no-hero case.
        //
        // The "· your daylist" tail belongs to an ACTUAL daylist and nothing else. The composer fills the hero slot with
        // a Spotlight card (album / artist / editorial playlist) whenever the feed carries one and only falls back to the
        // daylist, so appending the tail unconditionally captioned somebody's new album "your daylist". Same
        // `Meta.Format` discriminator the composer routes on (SpotifyHomeComposer.ModuleForFormat).
        string HeroEyebrow(HomeCard card, HomeFeed feed)
        {
            string part = GreetingPart(feed.Greeting);
            string? who = name is { Length: > 0 } n && !LooksLikeHandle(n) ? n : null;
            if (card.Meta?.Format is not "daylist")
                return who is null ? part : Strings.Home.Greeting(part, who);
            string daylist = Loc.Get(Strings.Home.YourDaylist);
            return who is null ? part + " · " + daylist : Strings.Home.HeroEyebrow(part, who, daylist);
        }

        // Shuffle ARMS the mode before starting the context — the same two fire-and-forget calls, in the same order, as
        // every other shuffle site (ArtistPage, LibraryPage, DetailShell). Without SetShuffleAsync this was a verbatim
        // copy of Play and the hero's two buttons did the identical thing. Routed through PlayCard so a single-item hero
        // (track/episode) still plays itself rather than being started as a context.
        void ShuffleCard(HomeCard c)
        {
            _ = svc.Player.SetShuffleAsync(true);
            PlayCard(c);
        }

        // The empty / failed viewport reads the SAME gutter and first-row inset as the live feed, so a page that fails
        // to load is not laid out to a different measure than the page that succeeds.
        Element StateHome(Element state) => ScrollView(new BoxEl
        {
            Direction = 1,
            Gap = Spacing.XL,
            Padding = new Edges4(Spacing.PageWide, Spacing.XXL, Spacing.PageWide, PlayerDock.Reserve + Spacing.XXL),
            Children = [ GreetingBlock(name, null, home, svc, post, false, go), state, tail ],
        }) with { Grow = 1f, ScrollKey = "home" };   // empty/failed states are facet-agnostic - one offset for all of them

        // Swap one viewport for another. There is deliberately no outer ScrollView around VirtualHome: doing that would
        // measure the virtual list at its complete content extent and silently realize every group again.
        //
        // `isEmpty`/`onEmpty` IS back (HomeFeedReadiness fix): both VirtualHome and VirtualFacet ALWAYS render the
        // Tail row (`tail`, above — the Concert Hub + Browse editorial destinations) unconditionally, so a 0-group
        // feed painted through VirtualHome directly is not a blank page — it is Timeline's inline notification rows,
        // Charts, and Tail with nothing else, which reads as broken chrome rather than as "nothing here yet". That
        // shape used to reach `home` in TWO cases: a genuinely empty account, and the pre-GoLive placeholder (0
        // groups because the live session had not landed). Removing isEmpty/onEmpty fixed the second case by
        // accident and broke the first — it let a truly empty account render the same chrome-only soup this row's
        // predecessor comment was written to avoid.
        //
        // ApplyFeed (above) now owns the actual fix: a 0-group UNFACETED feed only ever reaches `home` as Ready once
        // HomeFeedReadiness.Classify says Empty (the live-catalog attempt has concluded) — the pre-GoLive Placeholder
        // case is withheld and `home` stays Pending (skeleton) instead. So by the time this predicate can see
        // `Groups.Count == 0`, it is never the placeholder — it is a real answer — and the page state below is
        // finally safe to show without misfiring on every cold launch. `onFailed` is untouched — a genuine load
        // failure is not "empty" and still needs its own explicit state.
        return Skel.Region(
            home,
            group: HomeSkeleton.Group,
            reveal: SkelReveal.StaggerRows,
            // VirtualHome is a Grow=1 fill-list. Easing the region's height 0 → feed clips the first shelf of covers
            // into a strip while the pane still fills (empty mica under the shear). Search's facet body is the same.
            smoothResize: false,
            // Facet-agnostic guard: a facet is the server's own ordered document (VirtualFacet renders whatever it
            // says, including empty), so this only ever fires for the unfiltered landing.
            isEmpty: feed => feed.Facet.Length == 0 && feed.Groups.Count == 0,
            onEmpty: () => StateHome(EmptyState.Default()),
            onFailed: () => StateHome(ErrorState.Build(home.Error)),
            // A facet renders the server's ORDERED SECTIONS, not the authored landing rhythm — see VirtualFacet.
            content: feed => feed.Facet.Length == 0 ? VirtualHome(feed) : VirtualFacet(feed));
    }

    // The reveal state machine (pure, HomeFeedReadiness.cs): which read settles the page, when the FIRST reveal may
    // fire, and that nothing after it ever reveals again. Instance state, like _facetCts: two mounted HomePages (tabs)
    // each track what THEY have consumed. It also carries the applied epoch (-1 until the first read lands, so a fresh
    // mount never skips one) and the last read SEEN — withheld or not — for the 8 s hard fallback.
    readonly HomeRevealGate<HomeFeed> _gate = new();

    // Bumped whenever the gate HOLDS a settled feed for the chrome rows: Render subscribes, so the hold re-arms the
    // ChromeSettleMs cap timer against the moment this feed settled rather than against mount.
    readonly Signal<int> _heldVersion = new(0);

    // Charts' resource arms (monotonically, per mount) the first time Render sees the live-catalog attempt concluded.
    bool _chartsArmed;

    // The gate's clock. Wall-clock-independent ticks: only differences matter (the chrome cap), never a date.
    static double NowMs() => Environment.TickCount64;

    /// <summary>Publish a read's feed through the reveal gate. Its rules, in order:
    /// <para>The FACET: the 60 s poll reads whatever facet was current when it left; a chip tapped while it was in
    /// flight makes that answer a different document, and painting it would repaint Home as "All" under a lit "Music"
    /// tab. A dropped answer leaves the epoch UNADVANCED — nothing was applied, so a later reactivation must still be
    /// free to re-read it.</para>
    /// <para>The EPOCH, monotonic rather than arrival-ordered: a read superseded mid-flight must not land on top of a
    /// newer one — but the read that PRODUCED a bump (the cache publishes the epoch from inside the very read that
    /// observed the rollover) is itself the freshest answer, so gating on the loop's cancellation instead would throw
    /// away exactly the feed the bump exists to deliver.</para>
    /// <para>READINESS (<see cref="HomeFeedReadiness"/>), for the UNFACETED landing only: a read from a vantage where
    /// the live-catalog attempt had not concluded — whatever it holds, including the resident library shelves — is
    /// withheld as <see cref="HomeFeedState.Placeholder"/>: <c>home</c> stays Pending and the skeleton stays up. A
    /// withheld read leaves the applied epoch UNCHANGED, so the AuthState-triggered re-read for the SAME epoch is still
    /// free to land. Publishing the shelves early is the whole "why does Home open like this" recording: cached grid
    /// revealed, then the live feed replaced it 1.5 s later and every row jumped. A faceted read is the server's own
    /// document and can only be tapped on a revealed page, so it always passes.</para>
    /// <para>The CHROME, for the first reveal only: a settled feed is HELD until the Charts deck and the notification
    /// feeds have concluded (or <see cref="HomeFeedReadiness.ChromeSettleMs"/> elapses), so those rows paint WITH the
    /// reveal — never a lone "No charts right now" or a timeline popping into a page already on screen. The hold is
    /// released from <see cref="PublishHeld"/>. Once revealed, every later publish is a Ready→Ready swap in place:
    /// the engine reveals only on the Pending→Ready edge, so a poll, an epoch bump or a facet can never replay the
    /// stagger or re-skeletonize a row.</para>
    /// <para><paramref name="force"/> is the 8 s hard-fallback escape hatch (<see cref="ForceReleaseIfStillPending"/>):
    /// it publishes a Placeholder as-is and skips the chrome hold — the epoch and facet gates still apply, so a forced
    /// publish can never regress behind a real answer that already landed.</para></summary>
    void ApplyFeed(Services svc, Loadable<HomeFeed> home, int epoch, HomeFeed feed, bool liveCatalogConcluded,
        Func<bool> chromeConcluded, bool force = false)
    {
        if (!FacetMatches(svc, feed)) return;
        var verdict = _gate.Offer(epoch, feed, feed.Groups.Count, faceted: feed.Facet.Length > 0, liveCatalogConcluded,
            force, alreadyResolved: home.State.Peek() != (byte)LoadState.Pending, chromeConcluded(), NowMs());
        switch (verdict)
        {
            case HomeRevealVerdict.Reveal:
            case HomeRevealVerdict.Swap:
                home.SetReady(feed);
                break;
            case HomeRevealVerdict.Held:
                _heldVersion.Value++;   // re-arm the cap against THIS settle; the chrome effect does the rest
                break;
        }
    }

    /// <summary>Release a held first reveal if the chrome has concluded or the cap elapsed — called from the chrome
    /// signal effect and the cap timeout; a no-op whenever nothing is held or the page is already revealed.</summary>
    void PublishHeld(Loadable<HomeFeed> home, Func<bool> chromeConcluded, bool force)
    {
        if (_gate.Tick(chromeConcluded(), NowMs(), force) is { } feed) home.SetReady(feed);
    }

    /// <summary>The hard fallback (HomePage's mount-time <c>UseTimeout</c>, 8 s): Home must never sit on the skeleton
    /// indefinitely no matter what upstream timing withheld every ordinary read (see the comment on the refresh
    /// effect). If the region is still Pending 8 s after mount, force through the best UNFACETED feed this page has
    /// actually seen — a held feed still waiting on a slow chart read, else the last read even if it was withheld (a
    /// real device with cached shelves virtually always has SOMETHING by then) — or <see cref="HomeFeed.Empty"/> if
    /// literally nothing has landed yet. A facet in progress is left alone: it is the server's own document and the
    /// gate never held it in the first place.</summary>
    void ForceReleaseIfStillPending(Services svc, Loadable<HomeFeed> home, Func<bool> chromeConcluded)
    {
        if (home.State.Peek() != (byte)LoadState.Pending) return;   // already resolved (Ready/Failed) — nothing to force
        var (epoch, feed) = _gate.ForceRelease();
        ApplyFeed(svc, home, epoch, feed ?? HomeFeed.Empty, liveCatalogConcluded: true, chromeConcluded, force: true);
    }

    static void StartHomeRefreshLoop(Services svc, Loadable<HomeFeed> home, Action<Action> post, int epoch,
        Action<int, HomeFeed> apply, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshHomeOnce(svc, post, failIfInitial: true, epoch, apply, home, ct).ConfigureAwait(false);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    await RefreshHomeOnce(svc, post, failIfInitial: false, epoch, apply, home, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* Home unmounted / a newer epoch superseded this loop → stop cleanly */ }
        }, ct);
    }

    static async Task RefreshHomeOnce(Services svc, Action<Action> post, bool failIfInitial,
        int epoch, Action<int, HomeFeed> apply, Loadable<HomeFeed> home, CancellationToken ct)
    {
        try
        {
            // The facet is a request parameter, and the poll refreshes whatever the user is LOOKING at: re-reading
            // unfiltered here would have every 60 s tick quietly replace the faceted feed with "All".
            var feed = await svc.Library.GetHomeAsync(svc.HomeFacet.Peek(), ct).ConfigureAwait(false);
            post(() => apply(epoch, feed));
        }
        catch (OperationCanceledException) { /* superseded / unmounted — never a page failure */ }
        catch (Exception ex)
        {
            if (!failIfInitial) return;
            post(() =>
            {
                if (home.State.Peek() != (byte)LoadState.Ready) home.SetFailed(ex);
            });
        }
    }


    // Greeting + the home facet chip row. The chips come from the SAME home response the shelves do, so they cost no
    // extra request; selecting one writes Services.HomeFacet (the UI selection) and hands (previous, next) back here,
    // which re-reads home with the facet as an explicit REQUEST parameter. The answer publishes into the loadable this
    // page is bound to — passed in rather than stashed in a field — and only if the row still says that facet.
    // The greeting appears here ONLY as a fallback. Normally it is the hero band's eyebrow ("Good morning, Christos ·
    // your daylist") — the prototype has no standalone greeting block, because a page that opens with two stacked text
    // blocks before any content wastes its best row. With no hero on the page there is nowhere else for it to go, so it
    // comes back rather than being lost.
    Element GreetingBlock(string? name, HomeFeed? feed, Loadable<HomeFeed> home, Services? svc, Action<Action> post,
        bool hasHero, Action<string, string?>? go)
    {
        // `hasHero` is the caller's answer for the page it is composing (LiveHasHero): a hidden Hero must not steal
        // the greeting, and a facet page decides its hero by its own rule, not the landing's.
        Element? hero = hasHero ? null : GreetingHero(name, feed?.Greeting);

        Element? chipRow = null;
        if (feed?.Chips is { Count: > 0 } chips && svc is not null)
            chipRow = Ctx.Provide(HomeFacetChips.Props,
                new HomeFacetChips.Model(chips, (prev, next) => RefreshForFacet(svc, home, post, prev, next)),
                Embed.Comp(() => new HomeFacetChips()));

        Element? customize = go is null ? null : HomeCustomizeAffordance.Button(go);
        Element? body = hero is null ? chipRow : chipRow is null ? hero : new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f,
            Children = [ hero, chipRow ],
        };

        if (customize is null) return body ?? new BoxEl();
        if (body is null)
            return new BoxEl
            {
                Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children = [ customize ],
            };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Start, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Children = [ body ] },
                customize,
            ],
        };
    }

    /// <summary>The greeting WORD — the server's own when it sent one, else a local-clock guess. Spotify's
    /// <c>home.greeting.transformedLabel</c> is already localized for the ACCOUNT and bucketed against the timezone the
    /// home request itself carried, so it wins: the two disagree for anyone travelling, anyone on a differently-localized
    /// OS, and anyone whose account language is not the system one. The clock fallback is for offline/fake sources, which
    /// publish no greeting at all.</summary>
    static string GreetingPart(string? serverGreeting)
    {
        if (serverGreeting is { Length: > 0 } fromServer) return fromServer;
        int h = DateTime.Now.Hour;
        return h < 5 ? Loc.Get(Strings.Home.GoodEvening)
             : h < 12 ? Loc.Get(Strings.Home.GoodMorning)
             : h < 18 ? Loc.Get(Strings.Home.GoodAfternoon)
             : Loc.Get(Strings.Home.GoodEvening);
    }

    /// <summary>A Spotify user-id handle is a long space-less hash. Greeting someone by it is worse than not greeting them
    /// by name at all.</summary>
    static bool LooksLikeHandle(string name) => name.Length >= 20 && !name.Contains(' ');

    // A facet change is a new home REQUEST, not a client-side filter: Spotify returns a different document per facet
    // (Music drops the personal quick matrix, Podcasts is shows), so the facet travels as a request PARAMETER and the
    // answer carries the facet it was read for. PathfinderResource keys its cache on the request body, so each facet is
    // its own entry rather than a stale hit on the unfiltered feed.
    //
    // Exactly ONE facet read is in flight: a second tap cancels the first, so a slow "Podcasts" can never land on top
    // of the "Music" the user has since chosen. The answer publishes only if the chip row still says this facet, and a
    // failure is LOUD — the row is put back where it was and the user is told, because a tab left underlined over the
    // previous facet's feed (what the old swallow-everything version did) is a page lying about what it is showing.
    void RefreshForFacet(Services svc, Loadable<HomeFeed> home, Action<Action> post, string? previous, string? facet)
    {
        _facetCts?.Cancel();
        _facetCts?.Dispose();
        var cts = _facetCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var feed = await svc.Library.GetHomeAsync(facet, cts.Token).ConfigureAwait(false);
                post(() => { if (FacetMatches(svc, feed)) home.SetReady(feed); });
            }
            catch (OperationCanceledException) { /* a newer tap superseded this read — ITS answer is the one that lands */ }
            catch (Exception ex)
            {
                WaveeLog.Instance.Warn("ui", "home.facet.failed",
                    "home facet '" + (facet ?? "") + "' failed; keeping the previous feed: " + ex.Message);
                post(() =>
                {
                    // The user moved on while this was failing — whatever they picked owns the row now, and reverting
                    // would yank the strip out from under them.
                    if (!string.Equals(svc.HomeFacet.Peek(), facet, StringComparison.Ordinal)) return;
                    svc.HomeFacet.Value = previous;
                    Toast.Show(Loc.Get(Strings.Home.FacetFailed), new ToastOptions { Severity = InfoBarSeverity.Error });
                });
            }
        }, cts.Token);
    }

    /// <summary>Does this answer still belong on screen? A feed knows the facet it was READ for
    /// (<c>HomeFeed.Facet</c>), so a superseded facet read and a stale poll answer are both measured against the ONE
    /// thing that decides what the page is showing — the chip row's selection. A null selection is the unfiltered
    /// feed, which the model spells "".</summary>
    static bool FacetMatches(Services svc, HomeFeed feed)
        => string.Equals(svc.HomeFacet.Peek() ?? "", feed.Facet, StringComparison.Ordinal);

    // The shell-material ownership token (see ShellMaterialState): identity for race-free last-writer-wins across a
    // navigation. Per instance, never static — two mounted HomePages must not clear each other's wash.
    readonly object _washOwner = new();

    // A resolved leg → the shell's layer record. Alpha stays 1 here: ShellMaterialLayer stamps the theme wash strength
    // onto both gradient stops itself (ShellWashGeometry.HeroAlpha / ShelfAlpha).
    static WashLayer? Layer(HomeWashPick? pick) => pick is { } p ? new WashLayer(p.Color, p.Key) : null;

    // Subscribe this page to ONE artwork's grading. Guarded: CoverColorPlane.Watch(null/unkeyable) returns the plane's
    // GLOBAL epoch, and subscribing Home to that would re-render the page on every grid batch it scrolls past.
    static void WatchArtwork(string? url)
    {
        if (url is { Length: > 0 }) _ = SpotifyLive.CoverColorPlane.Current.Watch(url).Value;
    }

    // The in-flight facet read. Cancelled by the next chip tap and by unmount, so two racing reads can never publish
    // in arrival order and leave the loser's feed on screen. Instance state, not static: two mounted HomePages (tabs)
    // must not cancel each other's reads.
    CancellationTokenSource? _facetCts;

    // ── the landing projection, memoized on the feed ───────────────────────────────────────────────────────────────
    // Project() walks every group and every card of the feed (per-kind aggregation, a URI dedupe set per module, the
    // section directory) and is a PURE function of (feed, titles). It used to run inside VirtualHome, which is re-entered
    // on every re-render of the page — a hover fade, a chip selection, a landed cover grading — so
    // the whole projection was rebuilt many times per second while nothing about the feed had changed.
    //
    // The feed is an immutable snapshot published by the refresh loop, so a REFERENCE hit is a content hit. Titles are
    // compared by value because Loc is live (HomeModuleCopy deliberately re-resolves per read): a language change must
    // re-title the modules, and it is the only other input Project reads. Instance state, like _facetCts.
    HomeFeed? _landingFeed;
    HomeModuleTitles? _landingTitles;
    HomeLanding? _landing;
    HomeLayoutDoc? _landingLayout;
    int _landingLayoutVersion = -1;
    HomePreferences? _homePrefs;
    int _renderLayoutVersion;

    HomeLanding Landing(HomeFeed feed)
    {
        var titles = HomeModuleCopy.Titles;
        var layout = _homePrefs?.Layout ?? HomeLayoutDoc.Default;
        int version = _renderLayoutVersion;
        if (_landing is { } cached && ReferenceEquals(_landingFeed, feed) && titles.Equals(_landingTitles)
            && version == _landingLayoutVersion && ReferenceEquals(_landingLayout, layout))
            return cached;
        var landing = HomeLandingProjection.Project(feed, titles, layout);
        _landingFeed = feed;
        _landingTitles = titles;
        _landing = landing;
        _landingLayout = layout;
        _landingLayoutVersion = version;
        return landing;
    }

    // ── the facet projection, memoized on the feed (the Landing(feed) contract, verbatim) ──────────────────────────
    // Rows() walks every section and every card and is a PURE function of (feed, titles), and EVERY realized facet row
    // re-projects inside its own Responsive closure so it re-describes from the live feed. Without the memo that is one
    // full walk per realized row per render — a hover fade would rebuild the projection a dozen times over a feed that
    // had not moved. The feed is an immutable snapshot, so a REFERENCE hit is a content hit; titles compare by value
    // because Loc is live and a language change must re-title the coalesced discover feed.
    HomeFeed? _facetFeed;
    HomeModuleTitles? _facetTitles;
    IReadOnlyList<HomeFacetRow>? _facetRows;

    IReadOnlyList<HomeFacetRow> FacetRows(HomeFeed feed)
    {
        var titles = HomeModuleCopy.Titles;
        if (_facetRows is { } cached && ReferenceEquals(_facetFeed, feed) && titles.Equals(_facetTitles))
            return cached;
        var rows = HomeFacetProjection.Rows(feed, titles);
        _facetFeed = feed;
        _facetTitles = titles;
        _facetRows = rows;
        return rows;
    }

    /// <summary>Whether the page composed for <paramref name="feed"/> opens with a hero band — which is what decides
    /// whether the chip row carries the standalone greeting. ONE answer for both lists: the chip row is the element
    /// that survives an All<->facet swap under a shared key, so whichever list's closure happens to be live must reach
    /// the same verdict for the same feed. The landing asks its projection (a hidden Hero is a hidden Hero); a facet
    /// asks its own rows, where a daylist inside a five-card "Jump back in" section is a shelf, not a hero.</summary>
    bool LiveHasHero(HomeFeed feed)
    {
        if (feed.Facet.Length == 0) return Landing(feed).Get(HomeGroupKind.Hero) is not null;
        var rows = FacetRows(feed);
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Kind == HomeFacetRowKind.Hero) return true;
        return false;
    }

    // ── greeting hero (the no-hero fallback only) ────────────────────────────────────────────────────
    static Element GreetingHero(string? name, string? serverGreeting)
    {
        string part = GreetingPart(serverGreeting);
        string greet = name is { Length: > 0 } n && !LooksLikeHandle(n) ? Strings.Home.Greeting(part, n) : part;
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Padding = new Edges4(0f, Spacing.S, 0f, 0f),
            Children = [ WaveeType.PageHero(greet), WaveeType.TrackMeta(Loc.Get(Strings.Home.OnRotation)) ],
        };
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────
}

/// <summary>Variable-height Home stack with row-aware first estimates. The engine still measures every realized row and
/// feeds the exact extent back through <see cref="IMeasuredVirtualLayout.SetMeasured"/>; these estimates only make the
/// cold window and content extent credible before those measurements exist. State is hoisted by HomePage and retained
/// across refreshes, so steady scrolling remains the normal Fenwick-table path.
///
/// <para>The row table is the landing's projected order (hide + reorder already applied). <see cref="Estimate"/>
/// switches on <see cref="HomeRow"/> and has no index arithmetic and no fallthrough arm.</para></summary>
sealed class HomeFeedVirtualLayout : IMeasuredVirtualLayout
{
    HomeRow[] _rows = HomeLandingProjection.DefaultRows;

    public HomeRow[] Rows => _rows;

    readonly ExtentTable _extents = new(0, 1f);
    readonly record struct GroupMetric(int Count, bool Titled);
    // Landing projection guarantees at most one authored module per kind. The lossless source ledger is represented by
    // the section directory and does not multiply module extents or vertical gaps here.
    readonly Dictionary<HomeGroupKind, List<GroupMetric>> _groups = new();
    int _sectionDeckCount;
    // Charts rides its OWN resource (HomePage.charts), invisible from here, so HomePage mirrors it in every render via
    // SetChartsState — same shape as _sectionDeckCount for the section directory. State is a byte (Pending/Ready/
    // Failed — FluentGpu.Signals.LoadState) rather than the enum itself, matching Loadable<T>.State's own wire type.
    // _chartsEmpty is folded in alongside it (one call, one dep) rather than a second setter: a Ready-with-zero-
    // sections read must estimate the STATE extent (the empty grammar), not the deck extent, and the two bits always
    // change together from this call site's point of view.
    byte _chartsState;
    bool _chartsEmpty;
    int _shapeVersion;
    int _seededVersion = -1;
    float _seededCross = float.NaN;

    /// <summary>Mirror Charts' resource state in. Bumps <see cref="_shapeVersion"/> only on an actual change, so a
    /// steady Ready state re-renders (hover, hover fade, hero grading) cost nothing here.</summary>
    public void SetChartsState(byte state, bool empty)
    {
        if (state == _chartsState && empty == _chartsEmpty) return;
        _chartsState = state;
        _chartsEmpty = empty;
        _shapeVersion++;
    }

    public void Configure(HomeLanding landing)
    {
        var next = new Dictionary<HomeGroupKind, List<GroupMetric>>();
        // A cheap structural fingerprint: which kinds are present and how many cards each holds. Anything else about the
        // feed cannot change a row's height.
        foreach (var kind in Enum.GetValues<HomeGroupKind>())
        {
            var group = landing.Get(kind)?.Group;
            if (group is null) continue;
            next.Add(kind, [new GroupMetric(group.Cards.Count, group.Title is { Length: > 0 })]);
        }
        bool changed = !SameShape(_groups, next);
        if (changed)
        {
            // A kind disappeared between feeds — drop the stale entries rather than sizing a row that no longer renders.
            _groups.Clear();
            foreach (var pair in next) _groups.Add(pair.Key, pair.Value);
        }
        int deck = landing.Sections.Count;
        if (deck != _sectionDeckCount) { _sectionDeckCount = deck; changed = true; }
        if (!SameRows(_rows, landing.Rows))
        {
            _rows = CopyRows(landing.Rows);
            changed = true;
        }
        if (changed) _shapeVersion++;
    }

    static bool SameRows(HomeRow[] a, IReadOnlyList<HomeRow> b)
    {
        if (a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    static HomeRow[] CopyRows(IReadOnlyList<HomeRow> src)
    {
        var copy = new HomeRow[src.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = src[i];
        return copy;
    }

    static bool SameShape(Dictionary<HomeGroupKind, List<GroupMetric>> a,
                          Dictionary<HomeGroupKind, List<GroupMetric>> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var pair in a)
        {
            if (!b.TryGetValue(pair.Key, out var other) || pair.Value.Count != other.Count) return false;
            for (int i = 0; i < pair.Value.Count; i++) if (pair.Value[i] != other[i]) return false;
        }
        return true;
    }

    int Count(HomeGroupKind kind)
    {
        if (!_groups.TryGetValue(kind, out var groups)) return 0;
        int total = 0;
        for (int i = 0; i < groups.Count; i++) total += groups[i].Count;
        return total;
    }

    void Ensure(int itemCount, float crossSize)
    {
        // Measure can ask for an estimate before arrange publishes a finite cross size. Reuse the last real width when
        // available so a 0-width prepass cannot reset a corrected table every frame.
        float cross = crossSize > 1f ? crossSize : !float.IsNaN(_seededCross) ? _seededCross : 1100f;
        if (_extents.Count == itemCount && _seededVersion == _shapeVersion
            && !float.IsNaN(_seededCross) && MathF.Abs(_seededCross - cross) <= 0.5f)
            return;

        // Trace the reseed trigger (code 110): f0=incoming cross, f1=previously seeded cross, i1=itemCount vs i2=seeded
        // count — a reseed mid-scroll wipes every measured correction and flaps the anchor re-pin.
        if (FluentGpu.Foundation.ScrollTrace.CompiledIn && FluentGpu.Foundation.ScrollTrace.Enabled)
            FluentGpu.Foundation.ScrollTrace.Note(110, cross, itemCount, (_extents.Count << 8) | (_seededVersion == _shapeVersion ? 1 : 0), _seededCross);

        _extents.Reset(itemCount, 240f);
        for (int i = 0; i < itemCount; i++) _extents.SetExtent(i, Estimate(i, cross));
        _seededCross = cross;
        _seededVersion = _shapeVersion;
    }

    // A module head is Subtitle 20/28 plus the module head gap.
    const float Head = 28f + HomeModuleLayout.HeadGap;

    float Estimate(int index, float cross)
    {
        // The SAME arithmetic HomeRowShell performs: cap the row at the app page measure, then take the page gutter off
        // both sides. If these two ever disagree the estimator sizes a module for a width the renderer never uses, and
        // the measured list re-pins its scroll anchor mid-scroll.
        float available = MathF.Max(1f, MathF.Min(cross, WaveeSize.PageMaxW) - 2f * Spacing.PageWide);
        float gap = HomeModuleLayout.Gap(available);
        var row = (uint)index < (uint)_rows.Length ? _rows[index] : HomeRow.Tail;

        float Stack(HomeGroupKind kind, bool shelfOwnsHeader = false)
        {
            if (!_groups.TryGetValue(kind, out var groups) || groups.Count == 0) return 0f;
            float extent = 0f;
            int rendered = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (group.Count == 0) continue;
                if (rendered++ > 0) extent += HomeModuleLayout.Gap(available);
                if (!shelfOwnsHeader && group.Titled) extent += Head;
                extent += HomeModuleLayout.ContentExtent(kind, available, group.Count);
            }
            return extent;
        }

        float First(HomeGroupKind kind)
        {
            if (!_groups.TryGetValue(kind, out var groups)) return 0f;
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].Count > 0)
                    return (groups[i].Titled ? Head : 0f)
                        + HomeModuleLayout.ContentExtent(kind, available, groups[i].Count);
            return 0f;
        }

        float RowStack(HomeGroupKind kind, bool shelfOwnsHeader = false)
        {
            float extent = Stack(kind, shelfOwnsHeader);
            return extent > 0f ? extent + gap : 0f;
        }

        static float SplitExtent(float left, float right, float width, float outerGap)
        {
            if (left <= 0f && right <= 0f) return 0f;
            if (width >= HomeModuleLayout.SplitEvenMin || left <= 0f || right <= 0f)
                return MathF.Max(left, right) + outerGap;
            return left + HomeModuleLayout.Gap(width) + right + outerGap;
        }

        return row switch
        {
            // Greeting fallback (only when there is no hero) + the chip row.
            HomeRow.Chips => (Count(HomeGroupKind.Hero) > 0 ? 0f : 84f) + 40f + Spacing.XXL + gap,
            // Through ContentExtent, not a second literal: the hero band's authored text/action allocation and the
            // estimator's prediction are the same arithmetic.
            HomeRow.Hero => Count(HomeGroupKind.Hero) == 0 ? 0f
                : First(HomeGroupKind.Hero) + gap,
            HomeRow.Weekly => RowStack(HomeGroupKind.WeeklyPair),
            HomeRow.Quick => RowStack(HomeGroupKind.QuickGrid),
            // PagedShelf owns the recents header, chevrons, lift clearance, and shared MediaCard height.
            HomeRow.Recents => RowStack(HomeGroupKind.Recents, shelfOwnsHeader: true),
            HomeRow.MixBand => RowStack(HomeGroupKind.MixBand),
            // The podium sizes itself: head + the tallest avatar + its 8-DIP pod gap + a two-line Caption 12/16 label +
            // the podium's own 16-a-side padding. (The label leg used to say 30 while the renderer set 15/line — the
            // convergence onto Caption 12/16 is what made the two agree.)
            HomeRow.Artists => Head + 2f * Spacing.L + 2f * Spacing.S + 76f + Spacing.S + 2f * 16f + gap,
            HomeRow.ChipCards => RowStack(HomeGroupKind.ChipCards),
            HomeRow.Radio => RowStack(HomeGroupKind.RadioDial),
            // Side by side above the split threshold, stacked below — so the estimate is the max of the two, or the sum.
            HomeRow.EpisodesAndBooks => SplitExtent(Stack(HomeGroupKind.QueueList), Stack(HomeGroupKind.RatedShelf), available, gap),
            HomeRow.Queue => RowStack(HomeGroupKind.QueueList),
            HomeRow.Books => RowStack(HomeGroupKind.RatedShelf),
            HomeRow.Podcasts => RowStack(HomeGroupKind.PodcastShelf, shelfOwnsHeader: true),
            // Up to 8 rows in day groups (a 40 cover with 8 of padding a side); it hides itself when the feed is empty
            // and the measured pass corrects it.
            HomeRow.Timeline => Head + 8f * (WaveeSize.Thumb40 + 2f * Spacing.S) + gap,
            // Pending estimates the LOADED shape (FoldExtent) — the seed IS Fold-shaped, so the shimmer never needs the
            // compact empty grammar. Only a settled Failed, or a settled Ready with zero sections, switches to it.
            HomeRow.Charts => ((_chartsState == (byte)LoadState.Failed
                    || (_chartsState == (byte)LoadState.Ready && _chartsEmpty))
                ? HomeModuleLayout.FoldStateExtent : HomeModuleLayout.FoldExtent) + gap,
            HomeRow.Sections => _sectionDeckCount == 0 ? 0f : HomeModuleLayout.FoldExtent + gap,   // was the two-row SectionDeckExtent
            HomeRow.Editorial => RowStack(HomeGroupKind.Featured),
            HomeRow.Feed => Count(HomeGroupKind.DiscoverFeed) == 0 ? 0f
                : HomeModuleLayout.ContentExtent(HomeGroupKind.DiscoverFeed, available, Count(HomeGroupKind.DiscoverFeed)) + gap,
            _ => Wavee.Features.Concerts.ConcertLayout.WideEditorial(available).Height * 2f
                 + Spacing.XL + PlayerDock.Reserve + Spacing.XXL,
        };
    }

    public float ContentExtent(int itemCount, float crossSize)
    {
        Ensure(itemCount, crossSize);
        return (float)_extents.Total;
    }

    public void Window(int itemCount, float crossSize, float viewportExtent, float scrollOffset, int overscan,
        out int first, out int last)
    {
        Ensure(itemCount, crossSize);
        first = Math.Max(0, _extents.IndexAt(scrollOffset) - overscan);
        last = Math.Min(itemCount, _extents.IndexAt(scrollOffset + viewportExtent) + 1 + overscan);
        if (last < first) last = first;
    }

    public RectF ItemRect(int index, float crossSize)
    {
        Ensure(_rows.Length, crossSize);
        return new RectF(0f, _extents.OffsetOf(index), crossSize, _extents.ExtentAt(index));
    }

    public void SetMeasured(int index, float mainExtent, float crossSize)
    {
        Ensure(_rows.Length, crossSize);
        _extents.SetExtent(index, mainExtent);
    }

    public float OffsetOf(int index, float crossSize)
    {
        Ensure(_rows.Length, crossSize);
        return _extents.OffsetOf(index);
    }

    public int IndexAt(float offset, float crossSize)
    {
        Ensure(_rows.Length, crossSize);
        return _extents.IndexAt(offset);
    }
}

/// <summary>Variable-height FACET stack: the chip/greeting block, one row per SERVER SECTION in server order, then the
/// tail. The same mechanics as <see cref="HomeFeedVirtualLayout"/> - a Fenwick extent table seeded from per-row
/// estimates and corrected by the engine's measured pass - over a different row table.
///
/// <para>Two objects rather than one on purpose: a facet and the landing are different documents, and a single table
/// asked to describe both would reseed on every swap and discard every measured correction belonging to both.</para>
///
/// <para>The fingerprint is (kind, card count, titled) per row - nothing else about a section can change its height.
/// <see cref="Estimate"/> has exactly three index cases (chips at 0, the tail past the rows, a section between) and no
/// other arithmetic.</para></summary>
sealed class HomeFacetVirtualLayout : IMeasuredVirtualLayout
{
    readonly record struct RowMetric(HomeFacetRowKind Kind, int Count, bool Titled);

    static readonly RowMetric[] NoRows = [];
    RowMetric[] _rows = NoRows;

    readonly ExtentTable _extents = new(0, 1f);
    int _shapeVersion;
    int _seededVersion = -1;
    float _seededCross = float.NaN;

    /// <summary>The section rows plus the two chrome rows the page always has: the chip block at 0 and the tail last.</summary>
    public int ItemCount => _rows.Length + 2;

    /// <summary>Mirror the projected rows in. Bumps <see cref="_shapeVersion"/> only on an ACTUAL shape change, so a
    /// steady re-render (a hover fade, a landed cover grading) costs nothing here and never reseeds mid-scroll.</summary>
    public void Configure(IReadOnlyList<HomeFacetRow> rows)
    {
        if (SameShape(_rows, rows)) return;
        var next = new RowMetric[rows.Count];
        for (int i = 0; i < next.Length; i++) next[i] = Metric(rows[i]);
        _rows = next;
        _shapeVersion++;
    }

    static RowMetric Metric(HomeFacetRow row)
        => new(row.Kind, row.Group.Cards.Count, row.Group.Title is { Length: > 0 });

    static bool SameShape(RowMetric[] a, IReadOnlyList<HomeFacetRow> b)
    {
        if (a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != Metric(b[i])) return false;
        return true;
    }

    void Ensure(int itemCount, float crossSize)
    {
        // Measure can ask for an estimate before arrange publishes a finite cross size. Reuse the last real width when
        // available so a 0-width prepass cannot reset a corrected table every frame.
        float cross = crossSize > 1f ? crossSize : !float.IsNaN(_seededCross) ? _seededCross : 1100f;
        if (_extents.Count == itemCount && _seededVersion == _shapeVersion
            && !float.IsNaN(_seededCross) && MathF.Abs(_seededCross - cross) <= 0.5f)
            return;

        // Trace the reseed trigger (code 111, the facet twin of the landing's 110): f0=incoming cross, f1=previously
        // seeded cross, i1=itemCount vs i2=seeded count - a reseed mid-scroll wipes every measured correction.
        if (FluentGpu.Foundation.ScrollTrace.CompiledIn && FluentGpu.Foundation.ScrollTrace.Enabled)
            FluentGpu.Foundation.ScrollTrace.Note(111, cross, itemCount, (_extents.Count << 8) | (_seededVersion == _shapeVersion ? 1 : 0), _seededCross);

        _extents.Reset(itemCount, 240f);
        for (int i = 0; i < itemCount; i++) _extents.SetExtent(i, Estimate(i, cross));
        _seededCross = cross;
        _seededVersion = _shapeVersion;
    }

    // A module head is Subtitle 20/28 plus the module head gap - the same constant the landing estimator uses.
    const float Head = 28f + HomeModuleLayout.HeadGap;

    float Estimate(int index, float cross)
    {
        // The SAME arithmetic HomeRowShell performs: cap the row at the app page measure, then take the page gutter off
        // both sides. If these two ever disagree the estimator sizes a module for a width the renderer never uses, and
        // the measured list re-pins its scroll anchor mid-scroll.
        float available = MathF.Max(1f, MathF.Min(cross, WaveeSize.PageMaxW) - 2f * Spacing.PageWide);
        float gap = HomeModuleLayout.Gap(available);

        // Row 0 is the greeting block + the chip strip. A facet page never has a hero band, so the greeting ALWAYS
        // shows: the landing's "hero ? 0 : 84" conditional has exactly one answer here.
        if (index == 0) return 84f + 40f + Spacing.XXL + gap;

        int row = index - 1;
        if ((uint)row >= (uint)_rows.Length)
            return Wavee.Features.Concerts.ConcertLayout.WideEditorial(available).Height * 2f
                   + Spacing.XL + PlayerDock.Reserve + Spacing.XXL;

        var metric = _rows[row];
        if (metric.Count == 0) return 0f;
        return metric.Kind switch
        {
            // Through ContentExtent, not a second literal: the hero band's authored text/action allocation and the
            // estimator's prediction stay the same arithmetic.
            HomeFacetRowKind.Hero => (metric.Titled ? Head : 0f)
                + HomeModuleLayout.ContentExtent(HomeGroupKind.Hero, available, 1) + gap,
            // Every PagedShelf owns its own header, chevrons and lift clearance, so ShelfExtent IS the whole row.
            HomeFacetRowKind.Podcasts or HomeFacetRowKind.Recents or HomeFacetRowKind.Feed or HomeFacetRowKind.Shelf
                => HomeModuleLayout.ShelfExtent(available) + gap,
            // The two tabular stacks wear a separate module head above their content (when the section is titled).
            HomeFacetRowKind.Audiobooks => (metric.Titled ? Head : 0f)
                + HomeModuleLayout.ContentExtent(HomeGroupKind.RatedShelf, available, metric.Count) + gap,
            HomeFacetRowKind.Episodes => (metric.Titled ? Head : 0f)
                + HomeModuleLayout.ContentExtent(HomeGroupKind.QueueList, available, metric.Count) + gap,
            _ => HomeModuleLayout.ShelfExtent(available) + gap,
        };
    }

    public float ContentExtent(int itemCount, float crossSize)
    {
        Ensure(itemCount, crossSize);
        return (float)_extents.Total;
    }

    public void Window(int itemCount, float crossSize, float viewportExtent, float scrollOffset, int overscan,
        out int first, out int last)
    {
        Ensure(itemCount, crossSize);
        first = Math.Max(0, _extents.IndexAt(scrollOffset) - overscan);
        last = Math.Min(itemCount, _extents.IndexAt(scrollOffset + viewportExtent) + 1 + overscan);
        if (last < first) last = first;
    }

    public RectF ItemRect(int index, float crossSize)
    {
        Ensure(ItemCount, crossSize);
        return new RectF(0f, _extents.OffsetOf(index), crossSize, _extents.ExtentAt(index));
    }

    public void SetMeasured(int index, float mainExtent, float crossSize)
    {
        Ensure(ItemCount, crossSize);
        _extents.SetExtent(index, mainExtent);
    }

    public float OffsetOf(int index, float crossSize)
    {
        Ensure(ItemCount, crossSize);
        return _extents.OffsetOf(index);
    }

    public int IndexAt(float offset, float crossSize)
    {
        Ensure(ItemCount, crossSize);
        return _extents.IndexAt(offset);
    }
}

sealed class HomeQuickImageProbe : Component
{
    readonly string _url;
    readonly string _uri;
    readonly string _title;
    readonly string _section;
    readonly int _index;

    public HomeQuickImageProbe(string url, string uri, string title, string section, int index)
    {
        _url = url;
        _uri = uri;
        _title = title;
        _section = section;
        _index = index;
    }

    public override Element Render()
    {
        // §C0 (large-display-scaling.md): route the decode budget through the ambient device scale (OS DPI x zoom)
        // rather than the bare QuickW/QuickH DIP literals — the Equalizer.cs precedent for reading Viewport.Scale to
        // size a device-pixel quantity.
        float scale = UseContext(Viewport.Scale);
        int decodeW = ImageDecodeScale.For(MediaCard.QuickW, scale);
        int decodeH = ImageDecodeScale.For(MediaCard.QuickH, scale);
        var binding = UseImage(_url, decodeW, decodeH);
        HomeImageDiagnostics.LogState(_uri, _title, _section, _index, _url, binding);
        return new BoxEl { Width = 0f, Height = 0f };
    }
}

static class HomeImageDiagnostics
{
    /// <summary>The home image tracer rides the app's ONE developer switch (Settings ▸ Diagnostics ▸ Developer mode)
    /// instead of the <c>WAVEE_HOME_IMAGE_DIAG</c> environment variable it used to read. An env var can only be set
    /// before launch, which is precisely the wrong moment: the tracer exists for "this card's art is missing RIGHT
    /// NOW", and the answer to that must be reachable from inside the running app. Peek, not Value — this is read from
    /// logging call sites, not from a render, so there is nothing to subscribe.</summary>
    public static bool Enabled => DeveloperMode.Enabled.Peek();
    static readonly object Gate = new();
    static readonly HashSet<string> Seen = new(StringComparer.Ordinal);

    public static string? NormalizedUrl(Image? image)
    {
        if (image?.MosaicTiles is { Count: > 0 } tiles)
            return tiles.Count >= 4 ? null : ImageSource.Normalize(tiles[0]);
        return image?.Url is { Length: > 0 } u ? ImageSource.Normalize(u) : null;
    }

    public static void LogFeed(HomeFeed feed)
    {
        if (!Enabled) return;
        for (int gi = 0; gi < feed.Groups.Count; gi++)
        {
            var group = feed.Groups[gi];
            if (group.Kind != HomeGroupKind.QuickGrid) continue;

            // The diagnostic samples what the jump-back-in grid actually SHOWS, which is the module's own display cap.
            int total = Math.Min(group.Cards.Count, HomeModuleLayout.QuickShown);
            int url = 0, mosaic = 0, missing = 0, emptyUrl = 0;
            for (int i = 0; i < total; i++)
            {
                var card = group.Cards[i];
                if (card.Image is null)
                {
                    if (card.Kind != HomeCardKind.Liked) { missing++; LogMissing(group, gi, card, i, "image-null"); }
                    continue;
                }
                if (card.Image.MosaicTiles is { Count: >= 4 }) { mosaic++; continue; }
                if (card.Image.Url is not { Length: > 0 }) { emptyUrl++; LogMissing(group, gi, card, i, "url-empty"); continue; }
                url++;
            }

            LogOnce("summary|" + gi + "|" + total + "|" + url + "|" + mosaic + "|" + missing + "|" + emptyUrl,
                () => WaveeLog.Instance.Event(WaveeLogLevel.Info, "ui", "home.image.quickgrid.summary",
                    "Home quick-grid image inventory",
                    fields:
                    [
                        WaveeLogField.Of("groupIndex", gi),
                        WaveeLogField.Of("title", group.Title ?? ""),
                        WaveeLogField.Of("cards", total),
                        WaveeLogField.Of("url", url),
                        WaveeLogField.Of("mosaic", mosaic),
                        WaveeLogField.Of("missing", missing),
                        WaveeLogField.Of("emptyUrl", emptyUrl),
                    ]));
        }
    }

    public static void LogState(string uri, string title, string section, int index, string url, ImageBinding binding)
    {
        if (!Enabled) return;
        string key = "state|" + uri + "|" + index + "|" + binding.State + "|" + binding.Failure + "|" + binding.Attempts;
        LogOnce(key, () =>
        {
            var level = binding.State == ImageState.Failed && binding.Failure != ImageFailureKind.Canceled
                ? WaveeLogLevel.Warning
                : WaveeLogLevel.Debug;
            WaveeLog.Instance.Event(level, "ui", "home.image.quickgrid.state",
                "Home quick-grid image cache state",
                fields:
                [
                    WaveeLogField.Of("uri", uri),
                    WaveeLogField.Of("title", title),
                    WaveeLogField.Of("section", section),
                    WaveeLogField.Of("index", index),
                    WaveeLogField.Of("state", binding.State.ToString()),
                    WaveeLogField.Of("failure", binding.Failure.ToString()),
                    WaveeLogField.Of("attempts", binding.Attempts),
                    WaveeLogField.Of("host", WaveeLogRedaction.UrlHost(url)),
                    WaveeLogField.Of("url", ShortUrl(url)),
                ]);
        });
    }

    static void LogMissing(HomeGroup group, int groupIndex, HomeCard card, int index, string reason)
    {
        LogOnce("missing|" + groupIndex + "|" + index + "|" + card.Uri + "|" + reason,
            () => WaveeLog.Instance.Event(WaveeLogLevel.Warning, "ui", "home.image.quickgrid.missing",
                "Home quick-grid card has no renderable image URL",
                fields:
                [
                    WaveeLogField.Of("reason", reason),
                    WaveeLogField.Of("groupIndex", groupIndex),
                    WaveeLogField.Of("section", group.Title ?? ""),
                    WaveeLogField.Of("index", index),
                    WaveeLogField.Of("kind", card.Kind.ToString()),
                    WaveeLogField.Of("uri", card.Uri),
                    WaveeLogField.Of("title", card.Title),
                    WaveeLogField.Of("mosaicTiles", card.Image?.MosaicTiles?.Count ?? 0),
                ]));
    }

    static void LogOnce(string key, Action log)
    {
        lock (Gate)
        {
            if (!Seen.Add(key)) return;
        }
        log();
    }

    static string ShortUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return url.Length <= 96 ? url : url[..96];
        var tail = u.AbsolutePath;
        int slash = tail.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < tail.Length) tail = tail[(slash + 1)..];
        return u.Host + "/" + tail;
    }
}
