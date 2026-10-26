// ── Shell/Shell.PageChrome.cs — the page-chrome channel's core half: row 2, the current page's chrome, the page scroll, the band ──
//
// Role: CORE (themes plan 6.3, P5 ZK; contract 1.26)
//
// `PageChrome` (Theming\Contracts\PageChrome.cs) is what the frame draws of a page's head. T4c built it for the masthead band only
// (`Shell.Masthead.UI.cs`: the Browse family's title and trail). A band-style structure (Zune) draws a head for EVERY page, row 2
// included, so the core publishes one more thing here: THE CURRENT PAGE'S CHROME, as a signal, for whatever route is on screen.
//
//   · ROUTE-LEVEL ROW 2 (`RouteViews`): a family whose row 2 is a set of sibling routes answers it from the route alone, so the band
//     shows it on the navigation's first frame, before the page mounts. The Library kinds are the first (`User.LibraryChromeViews`), Browse's bands the second (`BrowseBands.ChromeViews`), Home's lone "all" a seed (`HomeSeedViews`, ZT4: `HomeHost` publishes the real words).
//   · IN-PAGE ROW 2 (`Publish`, through the `PageChromeSink` the content host provides around every page): a page's core host or its
//     theme template publishes its in-page tabs, its actions and its backdrop under its route. LRU 16, as the masthead publications.
//   · THE MERGE (`ChromeOf`): the title and trail are the masthead's for its family and the destination's title elsewhere (a detail page's
//     published entity name replaces the title, contract 1.30); the views are the page's when it published any, else the route's; the
//     actions and the backdrop are the page's.
//   · THE PAGE SCROLL (`Scroll`): the current page's main scroller, named through the sink, followed by an effect: its offset and the
//     direction it last travelled past a dead zone (`PageScrollRules`), what a band collapses on.
//   · THE BAND (`Band`): the one `BandProps` instance `Sidebar.DockedPaneProps` / `DrawerPaneProps` carry (`NavPaneProps.Band`).
//
// Classic reads none of it: its masthead keeps `MastheadBand` and its pane is the tree. The effects run in the content host
// (`ContentHost`, `Shell.UI.cs`), so the channel lives with the frame's root.

using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Shell
{
    /// <summary>Which of the band's row-1 destinations a route lights (P5 ZK). Pure: the route's kind alone, so a detail page reached
    /// from anywhere lights nothing (-1) and its parent pivot is the band's own decision from the trail.</summary>
    public static class BandRules
    {
        /// <summary>The row-1 destinations, in order.</summary>
        public const int Home = 0, Browse = 1, Library = 2, Recents = 3;

        /// <summary>The destination index <paramref name="kind"/> lights, or -1.</summary>
        public static int IndexOf(RouteKind kind) => kind switch
        {
            RouteKind.Home or RouteKind.HomeSection or RouteKind.HomeCustomize => Home,
            // Concerts sit under Browse (ZC1: the concerts view of Browse's row 2, "browse" lit).
            RouteKind.Browse or RouteKind.BrowseCategory or RouteKind.BrowseSection
                or RouteKind.Concerts or RouteKind.ArtistConcerts or RouteKind.Concert => Browse,
            RouteKind.Liked or RouteKind.LibraryAlbums or RouteKind.LibraryArtists or RouteKind.LibraryPodcasts
                or RouteKind.LibraryAudiobooks or RouteKind.LibraryPlaylists or RouteKind.Local => Library,
            RouteKind.Recents => Recents,
            _ => -1,
        };

        /// <summary>The destination index the page on <paramref name="route"/> lives under (<see cref="BandProps.Section"/>): its own where it lights
        /// one (<see cref="IndexOf"/>); a search's, browse; a detail page's, the destination its trail starts from: the journey's origin (through the
        /// origin's own origin while that is another detail page, a few hops at most), else the information architecture's root (library for an
        /// album, playlist, artist, show or episode, as <see cref="DetailTrail"/> roots them). -1 when none (settings, profile). Pure but for
        /// <paramref name="originOf"/>.</summary>
        public static int SectionOf(in Route route, Func<Route, NavOrigin?> originOf)
        {
            var at = route;
            for (int hop = 0; hop < 4; hop++)
            {
                if (IndexOf(at.Kind) is >= 0 and var own) return own;
                if (at.Kind == RouteKind.Search) return Browse;
                if (originOf(at) is not { } origin || origin.Route.IsNone) break;
                at = origin.Route;
            }
            return RootOf(at.Kind);
        }

        // The information architecture's root of a detail kind (DetailTrail's first crumb).
        static int RootOf(RouteKind kind) => kind switch
        {
            RouteKind.Album or RouteKind.Playlist or RouteKind.Artist or RouteKind.Show or RouteKind.Episode => Library,
            RouteKind.Search => Browse,
            _ => -1,
        };
    }

    /// <summary>How the page-scroll signal derives a direction from a stream of offsets (P5 ZK). The direction flips only after the
    /// offset has travelled <see cref="DeadZone"/> back from the furthest point reached in the current direction, so a trackpad's jitter
    /// or an inertia tail never flaps a band. At (or above) the top it is <see cref="PageScroll.Top"/>.</summary>
    public static class PageScrollRules
    {
        /// <summary>The travel (DIP) that commits a direction.</summary>
        public const float DeadZone = 8f;

        /// <summary>The follower's state: the published scroll and the furthest offset reached in its direction.</summary>
        public readonly record struct State(PageScroll Scroll, float Extreme)
        {
            /// <summary>At the top.</summary>
            public static State Top => default;
        }

        /// <summary>The next state for <paramref name="offset"/>.</summary>
        public static State Next(in State state, float offset)
        {
            if (!float.IsFinite(offset) || offset <= 0f) return State.Top;
            var dir = state.Scroll.Direction;
            float ext = state.Extreme;
            if (dir != PageScrollDirection.Up)
            {
                if (offset >= ext)
                {
                    ext = offset;
                    if (dir == PageScrollDirection.None && offset >= DeadZone) dir = PageScrollDirection.Down;
                }
                else if (ext - offset >= DeadZone)
                {
                    dir = PageScrollDirection.Up;
                    ext = offset;
                }
            }
            else if (offset <= ext)
            {
                ext = offset;
            }
            else if (offset - ext >= DeadZone)
            {
                dir = PageScrollDirection.Down;
                ext = offset;
            }
            return new State(new PageScroll(offset, dir), ext);
        }
    }

    /// <summary>The page-chrome channel (P5 ZK). See the file header.</summary>
    public static class PageChromeChannel
    {
        public const int Capacity = 16;

        // ══ ROW 2: the route's and the page's ══════════════════════════════════════════════════════════════════════════

        /// <summary>The views a route family answers from the route alone (empty for every other route). Pure but for the verbs.</summary>
        public static IReadOnlyList<PageView> RouteViews(in Route route)
            => User.IsLibraryChromeKind(route.Kind) ? User.LibraryChromeViews(route.Kind)
             : BrowseBands.HasBands(route) ? BrowseBands.ChromeViews(route)
             : route.Kind == RouteKind.Home ? HomeSeedViews(route) : [];

        /// <summary>Home's row-2 SEED (P5 ZT4): the "all" word alone, lit, so a band that draws row 2 reserves it on the navigation's first frame instead
        /// of easing 52 to 84 when <c>HomeHost</c> publishes the real words from its effect (those replace it under the same "all" id). Only for the
        /// route that names no facet: a facet switch changes the route's argument inside the mounted page and the host publishes under the new route
        /// in the same turn, so the real words never give way to a lone "all" there.</summary>
        static IReadOnlyList<PageView> HomeSeedViews(in Route route)
            => Entities.Strings.Resolve(route.Arg).Length == 0
                ? [new PageView(HomeUi.HomeHost.AllViewId, Loc.Get(Strings.Home.FacetAll), Current: true, s_goHome)]
                : [];

        /// <summary>Bumped when a page's publication changes.</summary>
        public static readonly Signal<int> Version = new(0);

        static readonly Dictionary<string, PageChromeRow> s_rows = new(StringComparer.Ordinal);

        /// <summary>The routes whose page has published at all (a row, or an empty one that withdrew it); bounded, cleared when full.</summary>
        static readonly HashSet<string> s_spoken = new(StringComparer.Ordinal);
        const int SpokenCapacity = 256;

        /// <summary>The page kinds that publish their row 2 (views, actions, the entity's name) from their first frame on: a route of one whose page has
        /// not spoken yet has its row coming. The page commits a frame before its effect publishes, so without this a band folded row 2 away for
        /// that frame and brought it back (album to album, artist to artist: audit B7, B5b).</summary>
        static bool PublishesRow(RouteKind kind) => kind is RouteKind.Album or RouteKind.Playlist or RouteKind.Artist or RouteKind.Show
            or RouteKind.Episode or RouteKind.User or RouteKind.ProfileList;
        static readonly List<string> s_lru = [];

        static string KeyOf(in Route r) => NameOf(r) + "\u001F" + (ArgOf(r) ?? "") + "\u001F" + r.Tab.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Publish (or withdraw, with null or an empty row) a page's row 2 under its route. Returns whether it changed.</summary>
        public static bool Publish(in Route route, PageChromeRow? row)
        {
            string k = KeyOf(route);
            if (s_spoken.Count >= SpokenCapacity) s_spoken.Clear();
            s_spoken.Add(k);
            if (row is null || row.IsEmpty)
            {
                if (!s_rows.Remove(k)) return false;
                s_lru.Remove(k);
                Version.Value = Version.Peek() + 1;
                return true;
            }
            if (s_rows.TryGetValue(k, out var old) && ReferenceEquals(old, row)) return false;
            s_lru.Remove(k);
            s_lru.Add(k);
            s_rows[k] = row;
            while (s_rows.Count > Capacity && s_lru.Count > 0)
            {
                s_rows.Remove(s_lru[0]);
                s_lru.RemoveAt(0);
            }
            Version.Value = Version.Peek() + 1;
            return true;
        }

        /// <summary>The name a page published under <paramref name="route"/> (a detail page's entity: "Late Night", "Coffee & Code"), or null.
        /// UNTRACKED: a reader that must follow it folds <see cref="Version"/> itself (the tab strip's items version does).</summary>
        public static string? PublishedTitle(in Route route)
            => s_rows.TryGetValue(KeyOf(route), out var row) && row.Title is { Length: > 0 } title ? title : null;

        /// <summary>Subscribing read of a page's publication.</summary>
        public static PageChromeRow? RowFor(in Route route)
        {
            _ = Version.Value;
            return s_rows.TryGetValue(KeyOf(route), out var row) ? row : null;
        }

        // ══ THE SINK the content host provides around a page ══════════════════════════════════════════════════════════

        /// <summary>A sink for the page on <paramref name="route"/>: what it publishes lands under its route.</summary>
        public static PageChromeSink SinkFor(Route route)
            => new(row => Publish(route, row), offset => ReportScroll(route, offset));

        // ══ THE CURRENT PAGE'S CHROME ═════════════════════════════════════════════════════════════════════════════════

        static readonly Signal<PageChrome?> s_current = new(null);

        /// <summary>The chrome of the page on screen: its title and trail, and row 2 (null before the content host first published).</summary>
        public static IReadSignal<PageChrome?> Current => s_current;

        /// <summary>The chrome of <paramref name="route"/>: the masthead's title and trail for its family (the same value the masthead band
        /// holds), the destination's own title elsewhere (one crumb, the page itself); the page's published views, else the route's; the
        /// page's actions and backdrop. Subscribing (the masthead publications, the origins, the row-2 publications).</summary>
        public static PageChrome ChromeOf(in Route route)
        {
            var published = Mastheads.For(route);
            var row = RowFor(route);
            PageChrome head;
            if (TryMasthead(route, published?.Title, Origins.For(route), out var title, out var trail))
            {
                head = ChromeFor(title!, trail, published, HeadFormOf(route));
            }
            else
            {
                string own = Dest(route).Title;
                // A detail page's trail (Z-Part-TitleBar: "library › playlists › late night drive", "search › the weeknd"): the journey's origin, else the
                // information architecture's parents, ending at the entity's own name once the page has published it.
                string entity = row?.Title is { Length: > 0 } published2 ? published2 : ArgOf(route) is { Length: > 0 } arg && !string.IsNullOrWhiteSpace(arg) ? arg.Trim() : "";
                var detail = entity.Length > 0 ? DetailTrail(route, entity, Origins.For(route)) : [];
                if (detail.Count >= 2) head = new PageChrome(own, CrumbsOf(detail));
                else head = new PageChrome(own, [new PageCrumb(own, null)]);
            }
            var views = row is { Views.Count: > 0 } ? row.Views : RouteViews(route);
            return head with
            {
                // Contract 1.30 (A10): a detail page's entity name replaces the kind ("Album") as the title; while it loads the kind stands.
                // The crumbs keep the destination's own words. Only the band reads this title: Classic's masthead is the masthead's.
                Title = row?.Title is { Length: > 0 } name ? name : head.Title,
                Views = views,
                // Contract 1.59 (audit B7): the page's views are coming; a band keeps row 2's place until they land.
                // A page of a kind that publishes its row and has not spoken yet (its first frame) has its row coming too.
                ViewsPending = views.Count == 0
                    && (row is { ViewsPending: true } || (row is null && PublishesRow(route.Kind) && !s_spoken.Contains(KeyOf(route)))),
                Actions = row?.Actions ?? [],
                ActionsPending = row is { ActionsPending: true },
                Backdrop = row?.Backdrop ?? PageBackdrop.None,
            };
        }

        /// <summary>The route whose chrome the band shows: the committed route's once the page swap has LANDED, the leaving page's until then
        /// (see <see cref="LandIfDue"/>). <c>None</c> before the first commit: the first frame is final.</summary>
        static readonly Signal<Route> s_landed = new(Route.None);

        static bool s_landDue;

        /// <summary>The route whose swap has landed (None before the first): the deferred page mount and the band's chrome follow it together.</summary>
        public static IReadSignal<Route> Landed => s_landed;

        /// <summary>The page swap's landing edge, armed by the content host's staged timer (<see cref="Shell.Shown"/>: the exit leg has run, the
        /// incoming page's enter starts). It does not write the signal itself: a timer's write is reconciled AFTER the frame's FLIP capture, so
        /// the band's height (row 2 coming or going) would SNAP and take the page with it. <see cref="LandIfDue"/> writes it from the host's
        /// after-animations phase of the SAME frame instead, which leaves it pending into the next frame's capture, and the band eases.</summary>
        internal static void ArmLanding() => s_landDue = true;

        /// <summary>The host's after-animations hook: land the swap if it is due.</summary>
        internal static void LandIfDue()
        {
            if (!s_landDue) return;
            s_landDue = false;
            s_landed.Value = Shell.Current.Peek();
        }

        /// <summary>The content host's effect: republish the page chrome of the page ON SCREEN (it re-runs on a navigation, on the landing and on
        /// any publication).</summary>
        internal static void PublishCurrent()
        {
            var route = OnScreenRoute();
            s_current.Value = route.IsNone ? null : ChromeOf(route);
        }

        /// <summary>The route of the page ON SCREEN as the band sees it: the committed route once the swap has landed (or inside one keep-alive
        /// slot, a facet or tab switch or album to album, where there is no swap), the leaving page's until then. The band's lit pivot reads it
        /// too, so a detail page's title does not flash up over the leaving page's row 2 for the exit leg. Subscribing.</summary>
        public static Route OnScreenRoute()
        {
            var committed = Shell.Current.Value;
            // The content host holds the leaving page for its exit leg (Design.Nav.ExitDurationMs) and lands the swap when the incoming page's
            // enter starts, so row 2 swaps WITH the page (its words fade through in step with it) instead of a whole exit leg ahead of it.
            var landed = s_landed.Value;
            bool sameSlot = !landed.IsNone && !committed.IsNone && string.Equals(SlotKey(in landed), SlotKey(in committed), StringComparison.Ordinal);
            return sameSlot || FrameRules.SwapLanded(in landed, in committed) ? committed : landed;
        }

        // ══ THE PAGE SCROLL ═══════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The follower's state: the pages' scrollers by route, the scroll signal and its direction memory. ROOT-LIFETIME (<see cref="RootScope"/>):
        /// the scrollers are nodes of the mounted root's pages and the direction is what a band folds on, so a remounted root starts at the top with none of the
        /// frozen tree's scrollers.</summary>
        sealed class ScrollFollower
        {
            public readonly Dictionary<string, IReadSignal<double>> Scrollers = new(StringComparer.Ordinal);
            public readonly Signal<int> Version = new(0);
            public PageScrollRules.State State = PageScrollRules.State.Top;
            public string Key = "";
        }

        /// <summary>The scroll signal the one band record holds (a process constant): a new root's first follow writes the top back into it.</summary>
        static readonly Signal<PageScroll> s_scroll = new(PageScroll.Top);

        static readonly RootState<ScrollFollower> s_follow = new("Shell.PageScroll", static () => new ScrollFollower());

        /// <summary>The current page's scroll: offset and direction (a band reads it in a bound prop, never in a render body: it moves
        /// every scrolled frame).</summary>
        public static IReadSignal<PageScroll> Scroll => s_scroll;

        /// <summary>Name (or withdraw, with null) the page's main scroller.</summary>
        public static void ReportScroll(in Route route, IReadSignal<double>? offset)
        {
            var f = s_follow.Value;
            string k = KeyOf(route);
            if (offset is null)
            {
                if (!f.Scrollers.Remove(k)) return;
            }
            else
            {
                if (f.Scrollers.TryGetValue(k, out var old) && ReferenceEquals(old, offset)) return;
                f.Scrollers[k] = offset;
                while (f.Scrollers.Count > Capacity * 2) f.Scrollers.Remove(FirstKeyBut(f, k));
            }
            f.Version.Value = f.Version.Peek() + 1;
        }

        static string FirstKeyBut(ScrollFollower f, string keep)
        {
            foreach (var key in f.Scrollers.Keys) if (!string.Equals(key, keep, StringComparison.Ordinal)) return key;
            return keep;
        }

        /// <summary>The content host's effect: follow the current page's scroller (a navigation starts the follower over).</summary>
        internal static void FollowScroll()
        {
            var f = s_follow.Value;
            var route = Shell.Current.Value;
            _ = f.Version.Value;
            string k = KeyOf(route);
            float offset = f.Scrollers.TryGetValue(k, out var scroller) ? (float)scroller.Value : 0f;
            if (!string.Equals(k, f.Key, StringComparison.Ordinal))
            {
                f.Key = k;
                f.State = PageScrollRules.State.Top;
            }
            f.State = PageScrollRules.Next(in f.State, offset);
            s_scroll.SetIfChanged(f.State.Scroll);
        }

        // ══ THE BAND ══════════════════════════════════════════════════════════════════════════════════════════════════

        static readonly Action s_goHome = static () => GoTo(new Route(RouteKind.Home));
        static readonly Action s_goBrowse = static () => GoTo(new Route(RouteKind.Browse));
        // The library pivot lands where the palette's "Your Library" row does (Liked Songs, the library's first view).
        static readonly Action s_goLibrary = static () => GoTo(new Route(RouteKind.Liked));
        static readonly Action s_goRecents = static () => GoTo(new Route(RouteKind.Recents));
        static readonly Action s_goSettings = static () => GoTo(new Route(RouteKind.Settings));
        static readonly Action s_goProfile = static () => { if (User.Me.IsValid) GoTo(ProfileRoute.For(User.Me)); };

        static BandDestination Destination(string id, RouteKind kind, Action go, string? label = null)
        {
            var (title, glyph) = Dest(new Route(kind));
            return new BandDestination(id, label ?? title, glyph, go);
        }

        /// <summary>Row 1: home, browse, library, recents (labels localised at the read, so a language change is followed).</summary>
        public static IReadOnlyList<BandDestination> Destinations() =>
        [
            Destination("home", RouteKind.Home, s_goHome),
            Destination("browse", RouteKind.Browse, s_goBrowse),
            Destination("library", RouteKind.Liked, s_goLibrary, Loc.Get(Strings.Nav.YourLibrary)),
            Destination("recents", RouteKind.Recents, s_goRecents),
        ];

        /// <summary>The hub list: row 1, then the profile (signed in only), then settings.</summary>
        public static IReadOnlyList<BandDestination> HubList()
        {
            var list = new List<BandDestination>(6);
            list.AddRange(Destinations());
            if (User.Me.IsValid) list.Add(new BandDestination("profile", Loc.Get(Strings.Person.List.Menu.Profile), Icons.Contact, s_goProfile));
            list.Add(Destination("settings", RouteKind.Settings, s_goSettings));
            return list;
        }

        static IReadOnlyList<BandDestination>? s_destinations;
        static string s_destinationsLocale = "";

        /// <summary>The destinations, rebuilt only when the language moved (so the band's reads hand back one instance).</summary>
        static IReadOnlyList<BandDestination> CachedDestinations()
        {
            string locale = Loc.Get(Strings.Nav.YourLibrary);   // a cheap fingerprint of the language in force
            if (s_destinations is null || !string.Equals(locale, s_destinationsLocale, StringComparison.Ordinal))
            {
                s_destinations = Destinations();
                s_destinationsLocale = locale;
            }
            return s_destinations;
        }

        static readonly Func<Route, NavOrigin?> s_originOf = static r => Origins.For(r);

        /// <summary>The one band record (<c>NavPaneProps.Band</c>): equal across re-pushes, every live value a signal or a Func.</summary>
        public static readonly BandProps Band = new()
        {
            Destinations = CachedDestinations,
            // A destination lights the moment it is clicked (the click's feedback); a detail page, which lights none, takes the pivot off only when its
            // page lands, so the leaving page's row 2 does not flash up under a title.
            Current = static () => BandRules.IndexOf(Shell.Current.Value.Kind) is >= 0 and var lit ? lit : BandRules.IndexOf(OnScreenRoute().Kind),
            // A detail page lights the destination it lives under, from its trail, when its page lands (the leaving page's word holds until then).
            Section = static () => BandRules.IndexOf(Shell.Current.Value.Kind) is >= 0 and var lit ? lit : BandRules.SectionOf(OnScreenRoute(), s_originOf),
            Pins = static () => Sidebar.Model.BandPins(ShellCaps.PinCap(ShellCaps.Tracked)),
            Chrome = s_current,
            Scroll = s_scroll,
            Hub = HubList,
        };
    }
}
