using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Features.Browse;
using Wavee.Features.Concerts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The content-card body opts routes into FluentGpu keep-alive caching. Route changes swap inside the boundary, scoped
// by the active browser tab, so same-route tabs never share page state. Slot identity + the direction→recipe map live in
// PageNavMotion (PageSlot / SlotKey / RecipeFor).
//
// Pages receive Route VALUES, never Signal<Route> — the KeepAlive boundary is the only route subscriber. A live route
// signal handed into a retained page re-renders it against foreign destinations during an animated exit.
sealed class ContentHost : Component
{
    readonly Signal<Route> _route;
    readonly Signal<NavTransitionKind> _motion;
    readonly Func<int> _activeTabId;
    readonly IAppSettings? _settings;   // seeds LibraryPage's persisted per-kind state (widths/sort/view/selection)
    // The shell's Go, refreshed from context on every render. PageFor runs inside the keep-alive boundary's own
    // computation, where a hook (UseContext) cannot be called — so the value is parked here by Render instead.
    Action<string, string?> _go = static (_, _) => { };
    public ContentHost(Signal<Route> route, Signal<NavTransitionKind> motion, Func<int> activeTabId, IAppSettings? settings = null)
    { _route = route; _motion = motion; _activeTabId = activeTabId; _settings = settings; }

    public override Element Render()
    {
        // A floating surface (today: the video mini player) RESERVES bottom space while it sits at its default anchor,
        // so the page content simply ends above it instead of being covered. Dragging the surface releases the
        // reservation. The wrapper is UNCONDITIONAL — padding 0 when nothing is reserved — because appearing and
        // disappearing from the tree would remount the KeepAlive subtree and cold-restart every cached page.
        var bridge = UseContext(PlaybackBridge.Slot);
        var ui = UseContext(ShellUi.Slot);
        _go = UseContext(HistoryStore.NavCtx);
        float reserve = bridge?.FloatingSurfaceReserve.Value ?? 0f;   // subscribe → re-inset as the surface comes and goes

        // The CLEARING half of ShellUi.ActiveStagePlayable (ModulePage writes the claim; its doc-comment states the
        // whole contract). This boundary is the only thing that knows a navigation happened AT ALL when the
        // destination is a page that will never write the signal — Home, a playlist, settings — so without this a
        // watch page's claim would outlive the watch page and the rail would keep yielding to a stage that is no
        // longer on screen.
        //
        // Deliberately NOT an unconditional clear on every route change. Two module pages in a row are handed over by
        // the PAGES: the incoming one writes its own playable from an effect, and this effect and that one are two
        // subscribers of the same flush with no guaranteed order — an unconditional clear could therefore land AFTER
        // the new page's claim and silently erase it. Restricting the clear to routes that no ModulePage will mount
        // for makes it order-independent: the two writers touch disjoint sets of navigations.
        //
        // TryParseRoute, not IsRoute: a malformed module route mounts a page that returns before its hooks run, so
        // nothing would write the signal and a stale claim would survive. That is a clear case too.
        UseSignalEffect(() =>
        {
            if (Wavee.Backend.Modules.ModulePages.TryParseRoute(_route.Value.Name, out _, out _)) return;
            if (ui is null || ui.ActiveStagePlayable.Peek().Length == 0) return;   // value-gated: no idle wake-ups
            ui.ActiveStagePlayable.Value = "";
        });
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, ZStack = true,
            Padding = new Edges4(0f, 0f, 0f, reserve),
            Children =
            [
                // Clip exiting pages so they cannot paint through the incoming body. Grow+MinHeight give the keep-alive
                // a definite height so ClipToBounds has a box to clip against. This layer FILLS the card — the masthead
                // overlays it and must never steal column height (browse → playlist used to snap the band to 0 and
                // yank this box up ~84 DIP in the same frame the fade-through started).
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                    Children =
                    [
                        // The token reads the tab + the route ONLY. `_motion` is read untracked inside PageTransition
                        // (Peek), so a direction write can never re-run this thunk and re-activate the page that is
                        // already active.
                        Flow.KeepAlive(
                            () => new PageSlot(_activeTabId(), _route.Value),
                            PageNavMotion.SlotKey,
                            s => PageFor(s.Route),
                            new KeepAliveOptions(
                                MaxEntries: 8,
                                TransitionFor: PageTransition,
                                SuppressLayoutTransitionsOnActivation: true)),
                    ],
                },
                // THE one masthead band (G2c), ALWAYS mounted as an overlay — never an in-flow sibling of KeepAlive.
                // HitTestPassThrough yields empty overlay area to the page beneath; the band's own children still hit.
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                    HitTestPassThrough = true,
                    Children = [ Embed.Comp(() => new ShellMastheadBand(_route)) ],
                },
            ],
        };
    }

    // The whole recipe — Enter AND Exit. The two pages OVERLAP on the boundary's ZStack for the length of the swap: the
    // reconciler keeps the outgoing root drawing (hit-test invisible) and parks it the moment its tracks settle, so the
    // card cross-fades/slides between two real pages instead of cutting to empty and then fading only the new one in.
    // Direction comes from the motion signal by PEEK: the shell writes it before the route in the same flush, so at
    // reconcile time it already IS the direction of the route being activated — and an untracked read keeps a
    // motion-only write from re-running the keep-alive thunk.
    //
    // G2c: this used to special-case a "masthead family" (search's directory, a browse category, the two section
    // drills) with a fixed dissolve instead of the directional slide, because each of those pages rendered its OWN
    // copy of the shared masthead and sliding the page root double-exposed "Browse" and "Browse ›" at nearly the same
    // spot. The masthead now lives in ShellMastheadBand, mounted ONCE as an overlay on this boundary (ContentHost.Render)
    // — bodies underneath slide like every other page swap, the band never consumes KeepAlive height, and it fades
    // opacity on family↔non-family instead of snapping Height 0.
    //
    // VIDEO SAFETY (the watch page). A module page can host a live composited video, which is a DestOut hole punched
    // into the real back buffer — an ancestor opacity washes it out and an opacity GROUP erases it entirely. The
    // fade-through recipes above are opacity recipes, so a swap touching a module page takes the translate-only pair
    // instead. BOTH sides are classified because the outgoing page's root is still attached and DRAWING for the whole
    // length of its exit: navigating away from a watch page is exactly as exposed as navigating to one. The
    // classification lives HERE and not in PageNavMotion because that file is source-included into Wavee.Tests, which
    // cannot compile ModulePages — the motion stays pure, the route knowledge stays in the shell.
    LayoutTransition? PageTransition(object oldToken, object newToken)
    {
        if (newToken is not PageSlot slot) return null;
        bool videoSafe = Wavee.Backend.Modules.ModulePages.IsRoute(slot.Route.Name)
            || (oldToken is PageSlot prev && Wavee.Backend.Modules.ModulePages.IsRoute(prev.Route.Name));
        return videoSafe ? PageNavMotion.RecipeForVideoSafe(_motion.Peek()) : PageNavMotion.RecipeFor(_motion.Peek());
    }

    // Detail/artist pages still use their existing signal-based internals, but each route owns its signal and cached
    // subtree. Returning via Back reactivates that destination's preserved page; opening another entity activates a new
    // slot and therefore receives the same PageTransition as every other page.
    static Element DetailHost(Route route) => new BoxEl
    {
        Key = "page:detail", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = [ Embed.Comp(() => new DetailPage(new Signal<Route>(route))) ],
    };

    static Element ArtistHost(Route route) => new BoxEl
    {
        Key = "page:artist", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = [ Embed.Comp(() => new ArtistPage(new Signal<Route>(route))) ],
    };

    // album / playlist / liked / local / SHOW all flow through the one shared detail surface (DetailPage → DetailShell);
    // a show just renders Episodes instead of Tracks on the right (DetailConfig.Show.Content == Episodes).
    // A `prerelease:` route IS the album detail surface: the prerelease uri is resolved to its album INSIDE DetailPage's
    // load (kind 138 — the ids differ, so nothing can map them earlier), so it needs no page class of its own, only its
    // own keep-alive slot.
    static bool IsDetail(Route r) =>
        r.Name.StartsWith("album:", StringComparison.Ordinal) || r.Name.StartsWith("pl:", StringComparison.Ordinal)
        || r.Name.StartsWith("prerelease:", StringComparison.Ordinal)
        || r.Name.StartsWith("show:", StringComparison.Ordinal) || r.Name == "liked" || r.Name == "local";

    static bool IsArtist(Route r) => r.Name.StartsWith("artist:", StringComparison.Ordinal);

    Element PageFor(Route r)
    {
        // Older Home documents and persisted navigation history can still carry the synthetic section route. It was
        // never page-able (spotify:list:recents:main is not a home-section resource); render the canonical playlist4
        // Recents destination before the generic home-section arm can claim it.
        if (string.Equals(r.Name, NavRouteNormalizer.LegacyRecentsRoute, StringComparison.Ordinal))
            r = new Route("recents", r.Arg);

        if (r.Name == "home")
            return new BoxEl { Key = "page:home", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new HomePage()) ] };

        if (r.Name == HomeCustomizerPage.Route)
            return new BoxEl { Key = "page:home-customize", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => HomeCustomizerPage.Create()) ] };

        // One page class, two prefixes: HomeSectionPage itself switches on the PREFIX (home-section: vs
        // browse-section:) to pick homeSection vs browseSection, never on the section's own uri — see its class
        // doc-comment for why a uri-shaped discriminator is the bug this split replaced.
        if (HomeSectionRoutes.Is(r.Name) || BrowseSectionRoutes.Is(r.Name))
            return new BoxEl { Key = "page:home-section", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new HomeSectionPage(r)) ] };

        if (r.Name == "history")
            return new BoxEl { Key = "page:history", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new HistoryPage()) ] };

        // The full recently-PLAYED surface. Deliberately its own destination rather than a `home-section:` drill-in:
        // it is backed by `/playlist/v2/list/recents/page` (the whole grouped snapshot), not by the home document's
        // section paging, so nothing about the Home shelf's counts or URIs decides whether it can be reached.
        if (r.Name == "recents")
            return new BoxEl { Key = "page:recents", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new RecentsPage()) ] };

        if (r.Name == "settings")
            return new BoxEl { Key = "page:settings", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new SettingsPage()) ] };

        // Developer-only. Gated HERE, at the one place a route becomes a page, so every way in — the palette, a tab
        // restored from the last session, a deep link — obeys the same switch. With developer mode off the route falls
        // through to the not-found page below rather than being special-cased into a second "you can't see this" state.
        if (r.Name == DeveloperMode.ApiConsoleRoute && DeveloperMode.Enabled.Value)
            return new BoxEl { Key = "page:api-console", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new ApiConsolePage()) ] };

        // What's new. Keyed by the ARG as well as the name, so opening 0.2.1 from the rail is a NEW keep-alive slot
        // rather than the same page re-pointed at a foreign release (the sidebar-customizer precedent above).
        if (r.Name == "whatsnew")
            return new BoxEl { Key = "page:whatsnew:" + (r.Arg ?? ""), Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new ReleaseNotesPage(r.Arg)) ] };

        if (r.Name == PlaybackRuntimeDiagnosticsPage.Route)
            return new BoxEl { Key = "page:" + PlaybackRuntimeDiagnosticsPage.Route, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new PlaybackRuntimeDiagnosticsPage()) ] };

        // The full-page sidebar customizer (§C4.1). An ordinary destination — tabs, back/forward, history and KeepAlive
        // all behave — because it edits the LIVE preference document instead of owning any state of its own.
        if (r.Name == SidebarLayoutMenu.CustomizeRoute)
            return new BoxEl { Key = "page:sidebar-customize:" + (r.Arg ?? ""), Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new SidebarCustomizerPage(r.Arg)) with { Key = "sidebar-customizer:" + (r.Arg ?? "") } ] };

        if (BrowseRoutes.IsHome(r.Name))
            return new BoxEl { Key = "page:browse-home", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new BrowseDirectoryPage()) ] };

        if (r.Name == "search")
            return new BoxEl { Key = "page:search", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new SearchPage(r)) ] };

        if (r.Name == "albums" || r.Name == "artists" || r.Name == "podcasts")
            return new BoxEl { Key = "page:" + r.Name, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new LibraryPage(r.Name, _settings)) ] };

        if (DiscographyRoute.Is(r.Name))
            return new BoxEl { Key = "page:disco", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new DiscographyPage(new Signal<Route>(r))) ] };

        // A browse CATEGORY page (prefix browse:). The directory is BrowseRoutes.Home, handled above.
        if (BrowseRoutes.Is(r.Name))
            return new BoxEl { Key = "page:browse", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new BrowsePageHost(r)) ] };

        // A page a MODULE describes (Part 9). One page class for every module and every entity kind: the document is
        // declarative and the app renders it, so there is nothing per-module to switch on here. Keyed by the whole
        // route (the sidebar-customizer precedent above) so two module pages are two keep-alive slots rather than one
        // page being re-pointed at a foreign entity.
        if (Wavee.Backend.Modules.ModulePages.IsRoute(r.Name))
            return new BoxEl { Key = "page:" + r.Name, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new ModulePage(r)) with { Key = "module-page:" + r.Name } ] };

        if (ConcertRoutes.Is(r.Name))
            return new BoxEl { Key = "page:concert-route", Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
                Children = [ Embed.Comp(() => new ConcertRoutePage(r)) ] };

        if (IsArtist(r)) return ArtistHost(r);
        if (IsDetail(r)) return DetailHost(r);

        // ── Not found ────────────────────────────────────────────────────────────────────────────────────────────
        // Nothing above claimed the route. It is NOT "coming soon" — that copy promised a page that in every real
        // case does not and will not exist (a retired destination, a stale restored tab, a hand-typed deep link, a
        // developer route with developer mode off). Say so, keep the destination's own glyph so the page still reads
        // as the thing the user asked for, and give the one action that always works.
        var (_, glyph) = ShellNav.Dest(r);
        var go = _go;
        WarnUnknownRouteOnce(r.Name);
        return new BoxEl
        {
            Key = "page:" + r.Name,
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Gap = Spacing.M,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                Icon(glyph, 40f, Tok.TextTertiary),
                WaveeType.PageHero(Loc.Get(Strings.Nav.PageNotFound)),
                Button.Standard(Loc.Get(Strings.Nav.GoHome), () => go("home", null)),
            ],
        };
    }

    // One warning per route key per process. PageFor runs on every activation of a keep-alive slot, so an unguarded
    // log here would write a line every time the user tabbed back to the same dead tab — the noise would bury the
    // first, informative occurrence.
    static readonly HashSet<string> s_warnedUnknown = new(StringComparer.Ordinal);

    static void WarnUnknownRouteOnce(string routeName)
    {
        if (!s_warnedUnknown.Add(routeName)) return;
        WaveeLog.Instance.Warn("nav", "route.unknown: " + routeName);
    }
}
