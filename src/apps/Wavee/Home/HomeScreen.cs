// ── Home/HomeScreen.cs — HomeScreen: the Home page's CORE HOST (T10a1; home rebuild, fifth pass — stock controls) ──
//
// Role: CORE host (the page's picture is Classic's `Pages.Home` view: `Themes\Classic\Structure\Views\Home\ClassicHomePage.cs`)
// Spec: docs/plans/wavee/home-rebuild-implementation.md "Fifth pass — stock controls" (Page, Sticky headers),
//       "Seventh pass" (the facet words are the page title; no greeting headline; no pointer handler on the dim host).
//
// The DATA half is carried over from the previous passes unchanged (Props, PageFor, Compute, Verdict,
// LocalizedTitles/Subtitles, Seed, BuildWash, the verdict/content logs) — see git history for why each is shaped the way
// it is. The facet machine's state and the effects that drive it (the route-arg effect, Demand, the select path,
// RevertRoute, RememberFollowing, Prefetch, the landing edge, the facet logs) moved to `HomeHost` (C16): this component
// keeps the hooks and calls one host method per effect.
//
// THE RENDER HALF IS THE THEME'S (T10a1). `Render` runs every hook in the order it always did (the route-arg effect, the demand, the
// model and verdict computeds, the shown/words record, the selector sync, the landing edge, the following-chip prefetch, the wash claim)
// and mounts `Pages.Home` with the model's facts: the published facet, the scroll handle and key, the facet row's props, the switch signal,
// the pending / failed / content / seed Funcs the skeleton region reads, and the wash claim. Everything below (the one plain `ScrollView`
// column, the facet row, the sticky-clipped zone column, the skeleton region, the zone chapters) is `ClassicHomePage` / `ClassicHomeZones`
// now, with the same keys, the same sticky idiom and the same entrance rows.
//
// NO GREETING HEADLINE (owner, seventh pass): the greeting lives only in the daylist card's eyebrow.
//
// ── FACET HISTORY + DEEP LINK (owner decision, v1) ──
//   A facet switch calls `Shell.GoTo(new Shell.Route(Shell.RouteKind.Home, arg: FacetRoute.ArgOf(facet), Tab: tab))`.
//   Home's route row is `KeyedByArg = false` (a facet switch reuses the SAME mounted `HomeScreen`) but `PlaceByArg =
//   true` (`Shell.cs` §3.10) — History/Back/Forward and the tab's stored route all see a facet switch as a new PLACE.
//   `PageFor` re-pushes fresh `Props(route.Tab, route.Arg)` on every commit; an effect keyed on `Props.Arg` resolves it.
//
// ── THE SELECTOR INDEX (no render-time writes) ──
//   The stock `SelectorBar` OWNS the host's `FacetIndex` (writes it on click/roving, then fires OnChange → HomeHost.Select). An
//   effect re-syncs it from `FacetPivot.Resolve(words, switch.Target)` — so a Missing→Failed revert, an Unavailable
//   no-op, a deep link or a Following toggle all move the selection without a write during Render.

using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee.HomeUi;

[CoreHost]
public sealed class HomeScreen : Component
{
    /// <summary>F23/W0: the facet arg is a RE-PUSHED PROP, not a read off a Home-only Shell static — the Wave-0
    /// `gate.keepalive.same-key-view` spike found `Flow.KeepAlive` re-runs `view` (fresh props, same instance) on a
    /// same-key token change, so `PageFor` below simply hands the route's own `Arg` through every time.</summary>
    public sealed record Props(int Tab, StringId Arg);

    public static Element PageFor(in Shell.Route route)
        => Embed.Comp(new Props(route.Tab, route.Arg), static () => new HomeScreen())
            with { Key = "home:" + route.Tab.ToString(CultureInfo.InvariantCulture) };

    // ── per-tab state (props-freeze rule: every field below is a stable object held for the component's whole
    //    lifetime, never rebuilt per render). The facet machine's state and the effects that drive it live in the
    //    HomeHost (C16): this component keeps the hooks and calls one host method per effect. ──
    readonly ScrollHandle _scroll = new();
    readonly HomeHost _host;
    readonly object _tintOwner = new();
    LoadState? _lastVerdict;                              // edge-gated facet.verdict log
    string? _contentFacet;                                // the facet whose content root is mounted (facet.content log)

    public HomeScreen() => _host = new HomeHost(_scroll);

    public override Element Render()
    {
        var p = UseProps<Props>();
        int tab = p.Tab;
        _host.Tab = tab;
        var layout = UseRequiredContext(HomeLayout.Slot);
        string scope = UseContext(Shell.PageScrollScope);
        var overlay = UseContext(Overlay.Service);

        // ── route arg → facet (deep link, Back/Forward, a palette "Home: …" command, a tab restore). Re-runs
        // whenever the route's own Arg changes (a DepKey, not auto-tracking — Props are not a signal) OR the chip
        // strip's word count changes (a cold-start deep link named a facet before its words landed).
        UseEffect(() => _host.ResolveRouteArg(p.Arg, tab), DepKey.From(p.Arg.Value, _host.Words.Peek().Length));

        // ── demand (§3.12: the Artist/Album/Show idiom — Demand() in an effect, never in the model memo) ──
        UseEffect(() => _host.DemandTarget());   // re-ask in a new scope

        // ── the screen model: DERIVED over the table signals. No publish, no stamps, no writes. ──
        var facetSig = UseComputed(() => _host.Switch.Value.Published);
        var model = UseComputed(() => Compute(facetSig.Value, layout.Doc.Value));
        var verdict = UseComputed(() => Verdict(facetSig.Value, model.Value));

        // ── "shown this session" + words + the shimmer shape memo: written by an effect on the Ready edge, never
        // in the model memo itself. ──
        UseEffect(() => _host.RecordShown(model.Value));

        // ── the SelectorBar index follows the switch machine (see the header): reads its own signal too, so a click
        // the machine refused (Unavailable / no-op) snaps the selection back on the next turn. ──
        UseEffect(() => _host.SyncFacetIndex());

        // ── the landing edge: Loading/Refreshing → Idle once the target's document is known (F13: no timer). ──
        UseEffect(() => _host.Land(tab));

        // ── D-plan §6.3: the "From artists you follow" block on All needs the `music-following-chip` document.
        // Prefetched once per All-entry edge; `Home.EnsureFeed` dedupes on Known/Asked, so re-entering All while it's
        // still in flight (or already known) costs nothing extra. ──
        UseEffect(() =>
        {
            if (facetSig.Value.Length == 0) HomeHost.EnsureFollowingChipFeed();
        }, DepKey.From(facetSig.Value.Length == 0));

        // The row's props: the host's ONE record (its signals are this tab's own stable fields and its delegates read the
        // host's Tab), so it serves the component's whole life — a fresh record per render would re-push on every parent
        // render for nothing.
        var rowProps = _host.RowProps;

        // The page's picture is the theme's: the scroll column, the facet row, the sticky-clipped zone column and the skeleton region
        // (Reveal None: the zones own their entrance; the shimmer is the SAME tree rendered against the seed, so the skeleton is derived
        // from the real page, never authored by hand). The host hands over what it decided; every Func is read inside the region, as before.
        string facet = facetSig.Value;
        return Slots.View(ViewKeys.HomePage, new HomePageProps
        {
            Facet = facet,
            ScrollKey = scope + "home:" + facet,
            Scroll = _scroll,
            Group = this,
            FacetRow = rowProps,
            Switch = _host.Switch,
            Pending = () => verdict.Value == LoadState.Pending,
            Failed = () => verdict.Value == LoadState.Failed,
            Content = () => model.Value ?? Seed(facetSig.Value, layout.Doc.Value),
            Seed = () => Seed(facetSig.Value, layout.Doc.Value),
            OnContent = NoteContent,
            RetryFirstLoad = _host.RetryFirstLoad,
            Overlay = overlay,
            Wash = BuildWash(() => model.Value),
            Greeting = DaylistCard.Greeting,
        });
    }

    // ══ THE SCREEN MODEL (derived, never published by hand) ═══════════════════════════════════════════════════════

    ScreenModel? Compute(string facet, LayoutDoc layoutDoc)
    {
        // A sign-in re-boots the entity scope: every table (and its Changed signal) is replaced, so subscribe to the
        // scope epoch first or the model stays bound to the dead scope's signals and never sees the feed land.
        _ = Entities.ScopeEpoch.Value;
        var h = Entities.HomeFeed(facet);
        _ = Home.Changed.Value;
        _ = Entities.Current.Edges.SectionCards.Changed.Value;
        _ = Entities.Current.Edges.SectionPreviewTracks.Changed.Value;
        _ = h.Version; _ = h.SectionVersion;
        if (!h.Knows(HomeFields.Sections)) return null;

        var inputs = SectionReader.Read(h);
        var titles = LocalizedTitles();
        // §6.3: All folds the music-following-chip document's releases into "From artists you follow" once known.
        IReadOnlyList<SectionInput>? following = null;
        if (facet.Length == 0)
        {
            var fh = Entities.HomeFeed("music-following-chip");
            _ = fh.Version; _ = fh.SectionVersion;
            if (fh.Knows(HomeFields.Sections)) following = SectionReader.Read(fh);
        }
        IReadOnlyList<Zone> zones = IsPodcastsFacet(facet)
            ? PodcastPlanner.Plan(inputs, titles, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : ZonePlanner.Plan(inputs, facet, titles, facet.Length == 0 ? kind => LayoutZoneOf(kind) is { } lz && layoutDoc.IsHidden(lz) : null,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), following, LocalizedSubtitles());
        if (zones.Count == 0) zones = [new Zone(ZoneKind.EmptyFacet, "home:empty:" + facet, null, null, Array.Empty<HomeCard>())];
        return new ScreenModel(facet, zones, SectionReader.Greeting(h), SectionReader.Chips(h));
    }

    LoadState Verdict(string facet, ScreenModel? m)
    {
        _ = Entities.ScopeEpoch.Value;   // see Compute: the tables are per scope
        _ = Home.Changed.Value;
        var h = Entities.HomeFeed(facet);
        var phase = Spotify.Status.Value;
        int zones = m?.Zones.Count ?? 0;
        bool knows = h.Knows(HomeFields.Sections), concluded = h.LiveAttemptConcluded;
        var v = ScreenLoad.Of(zones, knows, concluded,
            phase == Spotify.SessionPhase.Online, phase is >= Spotify.SessionPhase.Offline and <= Spotify.SessionPhase.Minting);
        if (v != _lastVerdict)
        {
            _lastVerdict = v;
            Log.Event(WaveeLogLevel.Info, "home", "facet.verdict", "facet verdict facet=" + facet + " verdict=" + v + " zones=" + zones
                + " knows=" + knows + " concluded=" + concluded + " phase=" + phase + " sections=" + h.SectionCount);
        }
        return v;
    }

    static ZoneTitles LocalizedTitles() => new(
        MadeForYou: Loc.Get(Strings.Home.MadeForYou),
        BecauseYouLike: Loc.Get(Strings.Home.Zone.BecauseYouLike),
        MoreForYou: Loc.Get(Strings.Home.Zone.MoreForYou),
        RadioAndMixes: Loc.Get(Strings.Home.Zone.RadioAndMixes),
        Browse: Loc.Get(Strings.Home.Zone.Browse),
        JumpBackIn: Loc.Get(Strings.Home.JumpBackIn),
        RecentlyPlayed: Loc.Get(Strings.Sidebar.Section.RecentlyPlayed),
        NewEpisodes: Loc.Get(Strings.Home.Zone.NewEpisodes),
        ContinueListening: Loc.Get(Strings.Home.Zone.ContinueListening),
        VideosYouMightLike: Loc.Get(Strings.Home.Zone.VideosYouMightLike),
        EpisodesYouMightLike: Loc.Get(Strings.Home.Zone.EpisodesYouMightLike),
        BecauseYouListenTo: Loc.Get(Strings.Home.Zone.BecauseYouListenTo),
        FromArtistsYouFollow: Loc.Get(Strings.Home.Zone.FromArtistsYouFollow),
        YourShows: Loc.Get(Strings.Home.Zone.YourShows),
        ShowsYouMightLike: Loc.Get(Strings.Home.Zone.ShowsYouMightLike));

    static ZoneSubtitles LocalizedSubtitles() => new(
        MadeForYou: Loc.Get(Strings.Home.ZoneSub.MadeForYou),
        NewMusic: Loc.Get(Strings.Home.ZoneSub.NewMusic),
        BecauseYouLike: Loc.Get(Strings.Home.ZoneSub.BecauseYouLike),
        JumpBackIn: Loc.Get(Strings.Home.ZoneSub.JumpBackIn),
        Radio: Loc.Get(Strings.Home.ZoneSub.Radio),
        Browse: Loc.Get(Strings.Home.ZoneSub.Browse),
        NewEpisodes: Loc.Get(Strings.Home.ZoneSub.NewEpisodes));

    static bool IsPodcastsFacet(string facet) => facet is "podcasts-chip" or "podcasts-following-chip";

    static LayoutZone? LayoutZoneOf(ZoneKind k) => k switch
    {
        ZoneKind.Daylist => LayoutZone.Daylist,
        ZoneKind.RecentGrid => LayoutZone.Recents,
        ZoneKind.CoverShelf => LayoutZone.MadeForYou,
        ZoneKind.WideTiles => LayoutZone.NewMusic,
        ZoneKind.ReleaseList => LayoutZone.Releases,
        ZoneKind.ClusterCards => LayoutZone.BecauseYouLike,
        ZoneKind.MixedCovers => LayoutZone.JumpBackIn,
        ZoneKind.RadioShelf => LayoutZone.Radio,
        ZoneKind.BrowseTiles => LayoutZone.Browse,
        _ => null,
    };

    // ══ THE FACET CONTENT LOG ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page view built the facet content for <paramref name="m"/>: log it on the edge (one line per published facet).
    /// The content itself is one KEYED subtree per published facet — the swap IS the reconciler's keyed Exit/Enter reconcile (F13), not a
    /// timer — over ONE dim host for the whole zone column (F17/F18): opacity and hit-testing are compositor binds on the switch phase,
    /// and NOTHING ELSE. The host carries no pointer handler: any OnPointerDown/OnHoverMove/… sets the node's PointerBit
    /// (Reconciler.cs "hit-testable so it receives press/drag AND bare-hover/exit"), which made the whole zone column an interactive
    /// hover scope — every card → gap → card move flipped it and re-walked hover over the column (seventh pass: the late hover). F35's
    /// "interacted" mark went with it; a Refreshing landing swaps on at-top + within-window alone (FacetSwitch.Landed).</summary>
    void NoteContent(ScreenModel m)
    {
        if (m.Facet == _contentFacet) return;
        _contentFacet = m.Facet;
        Log.Event(WaveeLogLevel.Info, "home", "facet.content", "facet content facet=" + m.Facet + " zones=" + m.Zones.Count
            + " first=" + (m.Zones.Count > 0 ? m.Zones[0].Kind.ToString() : "-"));
    }

    /// <summary>The Pending/shimmer document: the facet's OWN last-known zone shape when this tab has shown it
    /// before this session (F30), else a generic first-paint shape per facet family. All opens on the daylist card
    /// (ZonePlanner.PlanAll is the only planner that emits a Daylist zone) unless the Home layout hides it; the other
    /// music facets open on the Recents grid; Podcasts on its own lead. Rendered through the SAME zone column (the page view's `ClassicHomeZones.Column`) as the
    /// live page, so the skeleton is derived from the real tree (the daylist card's SkeletonProxy draws its silhouette
    /// for the blank seed card).</summary>
    ScreenModel Seed(string facet, LayoutDoc layoutDoc)
    {
        ZoneKind[] kinds = _host.TryLastShape(facet, out var known) ? known
            : IsPodcastsFacet(facet)
                ? [ZoneKind.EpisodeLead, ZoneKind.ContinueEpisodes, ZoneKind.ShowGrid]
                : facet.Length == 0 && !layoutDoc.IsHidden(LayoutZone.Daylist)
                    ? [ZoneKind.Daylist, ZoneKind.RecentGrid, ZoneKind.CoverShelf, ZoneKind.CoverShelf]
                    : [ZoneKind.RecentGrid, ZoneKind.CoverShelf, ZoneKind.CoverShelf];

        var zones = new Zone[kinds.Length];
        for (int i = 0; i < kinds.Length; i++)
        {
            var kind = kinds[i];
            int count = kind switch { ZoneKind.RecentGrid => 8, ZoneKind.ShowGrid => 8, ZoneKind.EpisodeLead or ZoneKind.Daylist => 1, _ => 6 };
            var cardKind = kind switch
            {
                ZoneKind.EpisodeLead or ZoneKind.ContinueEpisodes or ZoneKind.EpisodeRows => HomeCardKind.Episode,
                ZoneKind.ShowGrid => HomeCardKind.Podcast,
                ZoneKind.ReleaseList or ZoneKind.WideTiles => HomeCardKind.Album,
                _ => HomeCardKind.Playlist,
            };
            var items = new HomeCard[count];
            string? format = kind == ZoneKind.Daylist ? "daylist" : null;   // a blank of the daylist format (Home.cs s_blankFormats)
            for (int j = 0; j < count; j++) items[j] = HomeCard.Blank(cardKind, j, format);
            zones[i] = new Zone(kind, "seed:" + i, kind == ZoneKind.Daylist ? null : " ", null, items);
        }
        return new ScreenModel(facet, zones, "", Array.Empty<ChipInput>());
    }

    // ── the shell wash (WashPick.Pick + Palette.ShellTint) ──────────────────────────────────────────────────────────

    Element BuildWash(Func<ScreenModel?> model)
    {
        var m = model();
        string? url = null;
        uint accent = 0;
        if (m is { Zones.Count: > 0 })
        {
            var first = m.Zones[0];
            if (first.Items.Count > 0)
            {
                url = first.Items[0].ImageUrl;
                if (first.Kind == ZoneKind.Daylist) accent = first.Items[0].Accent;
            }
        }
        var shellSlot = UseContext(ShellMaterial.Slot);
        uint payload = WashPick.Pick(accent, 0, 0);
        // Every detail-shaped page must mount ONE of these or the window chrome stays neutral while the page is
        // coloured (Palette.Host.cs's own doc comment) — the 0-size leaf lives in the tree, never discarded.
        return Palette.ShellTint(url, ready: url is { Length: > 0 }, apply: true,
            owner: _tintOwner, slot: shellSlot, key: "home-tint", payloadAccent: payload);
    }
}
