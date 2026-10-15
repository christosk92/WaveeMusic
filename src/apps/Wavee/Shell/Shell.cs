// ── Shell/Shell.cs ─────────────────────────────────────────────────────────────────────────────────────────────────
// nav, routes, deep links, responsive layout, page-nav recipes, tint ownership, the first-run composite;
// the Shell.History section and the play log (ch 16); the in-app update card's arms (ch 14 → ch
// 19); the player bar's tier + picker rules and the Shell.Ui rail-state section the bar's four toggles write —
// RailOpen, Mode, RailWidth, DockedVideoHeight(+Pinned), ActiveStagePlayable, RailFits, ImmersiveLyrics, Toggle,
// CanFitRail (App/ShellUi.cs 91; ch 20 asks for a home and §2 had none) (ch 20, +230)
//
// Role: CORE
// Owner: I
// Wave: 4
// Budget: 2100 lines
// Spec: ch 18 §9 (1,150 pure) + ch 20 + ch 16 + ch 14 + ch 29
//
// The chrome-row allocator, the whole-shell responsive breakpoints and the tab workspace are the NAMED PARTIAL
// `Shell/Shell.Chrome.cs` (named on day one of the wave, §5 / §8 G4) — one file cannot hold ch 18's 1,150 pure lines
// plus ch 20's +230 plus ch 16's +180 plus the route table inside 2,100.

using System.Collections.Generic;
using System.Globalization;

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Shell
{
    // ══ 1. THE ROUTE SET ════════════════════════════════════════════════════════════════════════════════════════════
    //
    // ONE kind per destination `PageFor` can render. Read off 0.2.9's `ShellRoutes.s_exact` (16 names; 15 here —
    // `api-console` is DELETED, plan §9.6 Q7), `ShellRoutes.s_prefixes` (10 prefix families), `ConcertRoutes.TryParse`
    // (3) and `ContentHost.PageFor` (which renders one more key than `ShellRoutes` registers: `connect-diagnostics`).
    //
    // The 0.2.9 defect this closes by construction: `IsKnown`, `Dest` and `PageFor` were THREE lists that had to be
    // kept in step, and they were not — `connect-diagnostics` was renderable but not deep-linkable, and `disco:`,
    // `whatsnew` and `connect-diagnostics` all fell through `Dest`'s default and read "Your Library" in the tab strip,
    // the back/forward flyout, the sidebar's pinned rows and the not-found glyph. Here they are three READS of ONE
    // table (`Row(kind)`), so a new kind cannot be added with three of the four columns filled in.

    /// <summary>One kind per destination <see cref="PageFor"/> can render. 32 registered routes (28 in 0.2.9's own
    /// count, +1 for the podcast rework's <see cref="RouteKind.Episode"/>, wave P2, +1 for
    /// <see cref="RouteKind.LibraryAudiobooks"/>, A2 plan §3.6, +2 for the profile pages' <see cref="RouteKind.User"/>
    /// and <see cref="RouteKind.ProfileList"/>) + connect-diagnostics + capture-diagnostics + the full-page log viewer
    /// <see cref="RouteKind.Logs"/> (privacy-diagnostics-tab-implementation.md D6) +
    /// <see cref="RouteKind.NotFound"/> (29 in 0.2.9, less ApiConsole — deleted, plan §9.6 Q7).</summary>
    public enum RouteKind : byte
    {
        // s_exact (15) — ApiConsole DELETED, plan §9.6 Q7, 2026-09-12; SidebarCustomize deleted with the customizer (sidebar rework P3)
        Home, Browse, Search, LibraryAlbums, LibraryArtists, LibraryPodcasts, LibraryAudiobooks, Liked, Local,
        History, Recents, Settings, PlaybackDiagnostics, WhatsNew, HomeCustomize,
        // s_prefixes (10) — "<prefix><entity uri>"; a bare prefix addresses nothing and is NOT a route
        Album, Playlist, Artist, Show, Prerelease, Discography, Module, BrowseCategory, HomeSection, BrowseSection,
        // ConcertRoutes (3)
        Concerts, ArtistConcerts, Concert,
        // Podcast rework wave P2 (owner S; podcast-show-rework-implementation.md §5.11), appended the same way the
        // concert family was: an episode renders the shared detail surface like show/artist, so it needs its own
        // label + glyph rather than falling through to "Your Library".
        Episode,
        // Profile pages (#161): a person's profile, and its two whole lists (Following, Followers). Both are
        // installed by one lazy group (`Profile.InstallPages`).
        User, ProfileList,
        // Renderable by PageFor, NOT in 0.2.9's s_exact — the defect the table closes.
        //
        // 0.2.9: `ContentHost.PageFor` renders `ConnectDiagnosticsPage.Route` but the key is absent from
        // `ShellRoutes.s_exact`, so `IsKnown` is false: the deep link is refused with `deeplink.route.unknown`, its
        // history row is inert and its tab is labelled "Your Library". Giving it a kind here fixes it BY
        // CONSTRUCTION. Per CLAUDE.md ("every fix references its issue") the fix needs a GitHub issue first and NONE
        // EXISTS TODAY — that is an action for Christos (plan §9.6 item 6); do not file one from here.
        ConnectDiagnostics,
        // realtime-capture-implementation.md unit 6 (§5.5): the causal flight recorder's own Diagnostics page,
        // routed exactly like the two rows above it (developer-only, no material) so it is deep-linkable and
        // history-aware from day one rather than repeating the ConnectDiagnostics defect this table exists to close.
        CaptureDiagnostics,
        // privacy-diagnostics-tab-implementation.md D6: the full-page log viewer, Privacy & diagnostics › Logs › Log
        // viewer; `wavee://open?route=logs`. Unlike the three diagnostics rows above it is NOT developer-only — a user
        // reading their own log (and attaching it to a report) is the point of the page.
        Logs,
        // the fall-through `PageFor` paints when nothing above claims the key
        NotFound,
    }

    /// <summary>The route's identity. <paramref name="Subject"/> is the entity a prefix family addresses (default for
    /// the exact kinds). <paramref name="Arg"/> is 0.2.9's <c>Route.Arg</c> verbatim: the DISPLAY NAME for the entity
    /// families, and a DISCRIMINATOR for the kinds whose keep-alive slot depends on it
    /// (<see cref="RouteKind.Search"/>'s query, <see cref="RouteKind.WhatsNew"/>'s version,
    /// <see cref="RouteKind.Module"/>'s id, both customizers, <see cref="RouteKind.Discography"/>'s facet).
    /// <paramref name="Tab"/> is the workspace tab the slot belongs to (0 = none).</summary>
    public readonly record struct Route(RouteKind Kind, EntityUri Subject = default, StringId Arg = default, int Tab = 0)
    {
        /// <summary>The route that addresses NOTHING. Spelled out because <c>default(Route)</c> is NOT it: §4.11
        /// declares <see cref="RouteKind.Home"/> first, so a defaulted route is Home — and a "no destination" that
        /// silently means Home is how a dead span navigates somewhere.</summary>
        public static Route None => new(RouteKind.NotFound);

        public bool IsNone => Kind == RouteKind.NotFound;
    }

    /// <summary>The page a route renders. Filled once, at boot, by the page owners through <see cref="SetPage"/> —
    /// the UI half of the ONE table, kept out of the row itself so the row stays a POD a test can compare.</summary>
    public delegate Element PageFactory(in Route route);

    /// <summary>One row of THE route table. <c>IsKnown</c> is not a column: it is <c>Kind != NotFound</c>, which is
    /// what makes the two lists impossible to drift apart (ch 29 §9.3 rule (a)).</summary>
    /// <param name="Kind">The kind this row describes.</param>
    /// <param name="Key">The exact route name, or the family prefix (ending in <c>':'</c>). "" for
    /// <see cref="RouteKind.NotFound"/>, which has no key of its own.</param>
    /// <param name="IsPrefix">True when <paramref name="Key"/> is a family prefix rather than an exact name.</param>
    /// <param name="TitleLocKey">The loc KEY of the kind's own label — resolved at the edge by <see cref="Dest"/>.
    /// NEVER a fall-through that names another surface (ch 18 §7): a kind with nothing to say says its own kind.</param>
    /// <param name="Glyph">The kind's glyph (a Segoe Fluent code point, already a string).</param>
    /// <param name="DeveloperOnly">Reachable only while <c>diag.developerMode</c> is on. A deep link to one is
    /// REFUSED rather than being the thing that turns developer mode on.</param>
    /// <param name="ClaimsMaterial">The page publishes its own shell tint/wash. The complement is claimed NEUTRAL by
    /// the content host, and the two sets must stay disjoint or the order of two effects in one flush decides the
    /// colour. Note the asymmetry that is easy to lose: <c>browse-section:</c> CLAIMS, <c>browse:</c> does not.</param>
    /// <param name="KeyedByArg">The keep-alive slot (and the tab) depends on <see cref="Route.Arg"/> as well as the
    /// kind — two whatsnew versions are two slots.</param>
    /// <param name="PlaceByArg">Whether <see cref="SameSlot"/> ("is this a new PLACE for history/tab bookkeeping")
    /// discriminates on <see cref="Route.Arg"/>. Matches <paramref name="KeyedByArg"/> for every row except
    /// <see cref="RouteKind.Home"/> (remediation §3.10): Home's keep-alive SLOT ignores Arg (a facet switch reuses
    /// the mounted <c>HomeScreen</c>), but Arg is still a real destination (the facet), so its PLACE must track it —
    /// the one row where "same keep-alive instance" and "same place" disagree.</param>
    public readonly record struct RouteRow(
        RouteKind Kind,
        string Key,
        bool IsPrefix,
        string TitleLocKey,
        string Glyph,
        bool DeveloperOnly,
        bool ClaimsMaterial,
        bool KeyedByArg,
        bool PlaceByArg = false)
    {
        /// <summary>Is there a page behind this kind at all? The deep-link intake, the history rows and the not-found
        /// page each ask this, and it can no longer disagree with <see cref="PageFor"/>.</summary>
        public bool IsKnown => Kind != RouteKind.NotFound;
    }

    // The ONE table. Declaration order IS `RouteKind`'s order — `Row` indexes straight into it, and `ShellRoutesTests`
    // pins that alignment so a reorder cannot silently mislabel every destination.
    static readonly RouteRow[] s_routes =
    [
        new(RouteKind.Home,                "home",                 false, Strings.Nav.Home,             Icons.Home,        false, true,  false, PlaceByArg: true),
        new(RouteKind.Browse,              "browse",               false, Strings.Browse.HomeTitle,     Icons.Globe,       false, false, false),
        new(RouteKind.Search,              "search",               false, Strings.Nav.Search,           Icons.Search,      false, false, true, PlaceByArg: true),
        new(RouteKind.LibraryAlbums,       "albums",               false, Strings.Nav.Albums,           Icons.Album,       false, false, false),
        new(RouteKind.LibraryArtists,      "artists",              false, Strings.Nav.Artists,          Icons.Contact,     false, false, false),
        new(RouteKind.LibraryPodcasts,     "podcasts",             false, Strings.Nav.Podcasts,         Icons.RadioTower,  false, false, false),
        // Audiobooks (A2 plan §3.6): its own library route beside Podcasts — same SavedShows edge, split client-side
        // (User.Page.Library.cs's LibraryAudiobookFilter), so it needs its own row for exactly the ch 18 §7 reason
        // every other kind does (a fall-through here would read "Your Library" in the tab strip and history).
        new(RouteKind.LibraryAudiobooks,   "audiobooks",           false, Strings.Nav.Audiobooks,       Icons.Microphone,  false, false, false),
        new(RouteKind.Liked,               "liked",                false, Strings.Nav.LikedSongs,       Icons.Heart,       false, true,  false),
        new(RouteKind.Local,               "local",                false, Strings.Nav.LocalFiles,       Icons.Folder,      false, true,  false),
        new(RouteKind.History,             "history",              false, Strings.Nav.History.Title,    Icons.Clock,       false, false, false),
        // Recently PLAYED — a different destination from `history`, which is the app's NAVIGATION log. Headphones,
        // not Clock, for exactly that reason: two surfaces wearing one glyph read as the same place.
        new(RouteKind.Recents,             "recents",              false, Strings.Nav.Recents,          Icons.Headphones,  false, true,  false),
        new(RouteKind.Settings,            "settings",             false, Strings.Nav.Settings,         Icons.Settings,    false, false, false),
        new(RouteKind.PlaybackDiagnostics, "playback-diagnostics", false, Strings.Nav.PlaybackRuntime,  Icons.MusicNote,   true,  false, false),
        new(RouteKind.WhatsNew,            "whatsnew",             false, Strings.WhatsNew.Title,       Icons.RefineSparkle, false, false, true, PlaceByArg: true),
        new(RouteKind.HomeCustomize,       "home-customize",       false, Strings.Home.Customizer.Title, Icons.Edit,       false, false, true, PlaceByArg: true),

        new(RouteKind.Album,               "album:",               true,  Strings.Nav.Album,            Icons.Album,       false, true,  false),
        new(RouteKind.Playlist,            "pl:",                  true,  Strings.Nav.Playlist,         Icons.MusicNote,   false, true,  false),
        new(RouteKind.Artist,              "artist:",              true,  Strings.Nav.Artist,           Icons.Contact,     false, true,  false),
        // A show renders the shared detail surface exactly like album/artist, so it needs its own label + glyph —
        // without this arm 0.2.9 fell through to "Your Library" in the tab strip, the history rows and the pinned rows.
        new(RouteKind.Show,                "show:",                true,  Strings.Nav.Show,             Icons.RadioTower,  false, true,  false),
        // An upcoming release renders the ordinary album page, so it wears the album's label and glyph.
        new(RouteKind.Prerelease,          "prerelease:",          true,  Strings.Nav.Album,            Icons.Album,       false, true,  false),
        // `disco:` had NO Dest arm in 0.2.9 — an artist's discography tab was labelled "Your Library". Its own label now.
        new(RouteKind.Discography,         "disco:",               true,  Strings.Nav.Discography,      Icons.Album,       false, false, true, PlaceByArg: true),
        new(RouteKind.Module,              "module:",              true,  Strings.ModulePage.Title,     Icons.Globe,       false, false, true, PlaceByArg: true),
        new(RouteKind.BrowseCategory,      "browse:",              true,  Strings.Browse.HomeTitle,     Icons.Globe,       false, false, false),
        new(RouteKind.HomeSection,         "home-section:",        true,  Strings.Nav.Home,             Icons.MusicNote,   false, true,  false),
        // The asymmetry: a browse SECTION carries a colour, a browse CATEGORY eases to the neutral ground.
        new(RouteKind.BrowseSection,       "browse-section:",      true,  Strings.Browse.HomeTitle,     Icons.Globe,       false, true,  false),

        new(RouteKind.Concerts,            "concerts",             false, Strings.Nav.Concerts,         Icons.Calendar,    false, false, false),
        new(RouteKind.ArtistConcerts,      "artist-concerts:",     true,  Strings.Nav.ArtistConcertsGeneric, Icons.Calendar, false, false, false),
        new(RouteKind.Concert,             "concert:",             true,  Strings.Nav.ConcertDetails,   Icons.Calendar,    false, false, false),

        // Podcast rework wave P2: a shared `spotify:episode:` link OPENS this route (decision D-2) rather than
        // playing — the same shared-detail-surface treatment as `show:`, so it wears its own label + glyph too.
        new(RouteKind.Episode,             "episode:",             true,  Strings.Nav.Episode,          Icons.RadioTower,  false, true,  false),

        // Profile pages (#161): `user:<uri>` is the profile (claims its own tint like every detail page; Arg = the
        // display name); `people:<facet>:<uri>` is one of its two whole lists, Discography-shaped — the facet rides in
        // the key, so Arg is the place discriminator and the tab/history title is the facet word (`Dest`).
        new(RouteKind.User,                "user:",                true,  Strings.Person.List.Nav.Profile, Icons.Contact, false, true,  false),
        new(RouteKind.ProfileList,         "people:",              true,  Strings.Person.List.Nav.Lists, Icons.Friends,    false, false, true, PlaceByArg: true),

        new(RouteKind.ConnectDiagnostics,  "connect-diagnostics",  false, Strings.Nav.ConnectDiagnostics, Icons.MusicNote, true,  false, false),
        // realtime-capture-implementation.md unit 6: same developer-only, non-material shape as the two rows above.
        new(RouteKind.CaptureDiagnostics,  "capture-diagnostics",  false, Strings.Nav.CaptureDiagnostics, Icons.Document, true,  false, false),
        // The log viewer page (D6): a normal user route — NOT developer-only, claims no material, not keyed by arg.
        new(RouteKind.Logs,                "logs",                 false, Strings.Nav.Logs,             Icons.List,        false, false, false),
        new(RouteKind.NotFound,           "",                     false, Strings.Nav.PageNotFound,     Icons.MusicNote,   false, false, false),
    ];

    /// <summary>The page factories, parallel to <see cref="s_routes"/> and indexed by the SAME kind — the third read
    /// of one table, not a third list. Filled once at boot by <see cref="SetPage"/>.</summary>
    static readonly PageFactory?[] s_pages = new PageFactory?[s_routes.Length];

    /// <summary>ONE table, keyed by kind (ch 27 §9.6): (page, isKnown, title, glyph, developerOnly).
    /// <see cref="RouteRow.IsKnown"/>, <see cref="Dest"/> and <see cref="PageFor"/> are three READS of this, never
    /// three lists that must stay in step.</summary>
    public static ref readonly RouteRow Row(RouteKind k) => ref s_routes[(int)k];

    /// <summary>Total kinds: the route table's length, which the route tests walk (and pin against the enum) rather than a
    /// hand-kept list.</summary>
    public static int RouteKindCount => s_routes.Length;

    /// <summary>Register the page a kind renders. Called once per kind — at boot by the owner of that page, or on the
    /// first miss below for a lazily-installed group; a second registration WINS, so a skeleton stub can be replaced by
    /// the real page.</summary>
    public static void SetPage(RouteKind kind, PageFactory page) => s_pages[(int)kind] = page;

    /// <summary>The page for a route, or null when nothing is registered yet (the frame paints the not-found arm). The
    /// SAME index as <see cref="Row"/>, so a kind can never be renderable-but-unknown again.
    /// <para>LAZY PAGE-FACTORY REGISTRATION (perf: the measured <c>boot.pages</c> mark). A miss runs
    /// <see cref="InstallLazyGroupFor"/> once for that kind's group, then resolves again — most page-factory
    /// registration is work nothing needs until the user first navigates to that page. Cheap on the (common) hit
    /// path: one array read, one null check, zero allocation.</para></summary>
    public static PageFactory? PageFor(in Route route)
    {
        var page = s_pages[(int)route.Kind];
        if (page is not null) return page;
        InstallLazyGroupFor(route.Kind);
        return s_pages[(int)route.Kind];
    }

    // ── 1.3 lazy page-factory groups ────────────────────────────────────────────────────────────────────────────────
    //
    // Three of the nine App.cs page-owner installs moved off the boot path and onto this miss arm: Home.InstallPages
    // (8 kinds: Home/Search/Browse/Recents + the two section prefixes + HomeCustomize), Album.InstallPages (4: Album/
    // Prerelease/Show/Episode — Episode joined in the podcast rework's wave P2, plan §5.11) and Playlist.InstallPages
    // (6: Playlist/Local/Liked + the three library kinds) — 18 of the 30 renderable kinds, and most of the boot.pages
    // cost. Each group installs at most ONCE (the bool below), then every kind in it resolves the ordinary way.
    //
    // NOT moved here — each stays eager in App.cs, called exactly where it always was:
    //   • Artist.InstallPages (Artist/Discography + Concert.InstallPages's three concert kinds): kept eager for its
    //     ConcertHost seam (nothing in the sidebar reads it since P3).
    //   • Modules.InstallUi, Settings.InstallScreens, Diagnostics.Install: each also wires a seam nothing routes past —
    //     PlayLinkRouter.Modules/Match answer ANY pasted link, the setup wizard and the crash-report dialog must be on
    //     screen at first launch, and the crash-prompt latch / network-cost host are process-lifetime, not per-page.
    //   • Shell.InstallUi's own SetPage call (History): RootFactory must exist before Run.
    //   • Queue.InstallUi / Track.InstallActions: neither owns a route (Queue is a rail/stage arm, Track a menu's
    //     verbs) — Queue's Play/PlayNext/AddToQueue registrations must still win the first-wins race, so Track keeps
    //     running immediately after it, exactly as before.
    //
    // Navigation is UI-thread only (C1): a plain bool is the whole guard, no lock and no Interlocked — PageFor never
    // re-enters itself for the same kind mid-install.
    static bool s_homeGroupInstalled, s_albumGroupInstalled, s_playlistGroupInstalled, s_profileGroupInstalled;

    static void InstallLazyGroupFor(RouteKind kind)
    {
        switch (kind)
        {
            case RouteKind.Home or RouteKind.HomeSection or RouteKind.BrowseSection or RouteKind.HomeCustomize
                or RouteKind.Search or RouteKind.Browse or RouteKind.BrowseCategory or RouteKind.Recents:
                if (s_homeGroupInstalled) return;
                s_homeGroupInstalled = true;
                Home.InstallPages();
                break;
            // RouteKind.Episode joined this group in the podcast rework's wave P2 (plan §5.11): Album.InstallPages()
            // registers its placeholder page the same way it registers Show's real one, so a miss on EITHER must
            // install the SAME group or the episode route's page factory would never resolve.
            case RouteKind.Album or RouteKind.Prerelease or RouteKind.Show or RouteKind.Episode:
                if (s_albumGroupInstalled) return;
                s_albumGroupInstalled = true;
                Album.InstallPages();
                break;
            case RouteKind.Playlist or RouteKind.Local or RouteKind.Liked or RouteKind.LibraryAlbums
                or RouteKind.LibraryArtists or RouteKind.LibraryPodcasts or RouteKind.LibraryAudiobooks:
                if (s_playlistGroupInstalled) return;
                s_playlistGroupInstalled = true;
                Playlist.InstallPages();
                break;
            // The profile pages (#161): Profile.InstallPages registers BOTH kinds (the profile + its Following/Followers
            // lists), so a miss on either installs the one group.
            case RouteKind.User or RouteKind.ProfileList:
                if (s_profileGroupInstalled) return;
                s_profileGroupInstalled = true;
                Profile.InstallPages();
                break;
        }
    }

    // ── test-only seam (BootOrderingTests) ──────────────────────────────────────────────────────────────────────────
    //
    // Wavee.Tests has no InternalsVisibleTo (see Controls.cs / Playlist.UI.cs), so this has to be public. It exists
    // solely so a guard test that boots every page factory — including the three lazy groups above — can put the
    // table back the way it found it, rather than leaking a process-wide registration into whichever test runs next.
    // Never called from production code.
    public static PageFactory?[] SnapshotPagesForTests() => (PageFactory?[])s_pages.Clone();

    public static void RestorePagesForTests(PageFactory?[] snapshot)
    {
        Array.Copy(snapshot, s_pages, s_pages.Length);
        s_homeGroupInstalled = s_albumGroupInstalled = s_playlistGroupInstalled = s_profileGroupInstalled = false;
    }

    /// <summary>Is there a page behind this route? Developer-gated kinds answer false unless
    /// <paramref name="developerMode"/> is on — those surfaces are reachable from Settings once the user turns
    /// developer mode on, and a link from outside the app must not be what turns them on.</summary>
    public static bool IsKnown(in Route route, bool developerMode = true)
    {
        ref readonly var row = ref Row(route.Kind);
        if (!row.IsKnown) return false;
        if (row.DeveloperOnly && !developerMode) return false;
        // A prefix family with nothing after the prefix addresses no entity, so it is not a route. "Something after
        // the prefix" is a uri with TEXT, not a VALID uri: `browse:`, `home-section:`, `browse-section:` and `module:`
        // carry ids (a genre, a section, a module) that are not catalogue entity kinds, and 0.2.9's rule was only
        // `key.Length > prefix.Length`.
        // A profile list is STRICT: a bad facet digit or a missing user is not a route (`people:9:…`, `people:0:`).
        if (route.Kind == RouteKind.ProfileList) return ProfileListRoute.TryParse(route, out _, out _);
        return !row.IsPrefix || HasSubject(route.Subject) || (route.Kind == RouteKind.Discography && !route.Arg.IsEmpty);
    }

    /// <summary>The route addresses SOMETHING: the uri carries text or a gid, whether or not the catalogue has a kind
    /// for it.</summary>
    static bool HasSubject(EntityUri uri) => uri.Id.Form != EntityForm.None;

    /// <summary>Does this route publish its own shell material? The content host owns the COMPLEMENT, and the two
    /// sets must stay disjoint — this is the one predicate, so they are.</summary>
    public static bool ClaimsMaterial(in Route route) => Row(route.Kind).ClaimsMaterial;

    // ── 1.1 route → (title, glyph) ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The label + glyph for a destination — the tab strip, the back/forward flyout, the sidebar's pinned
    /// rows and the breadcrumb all read this. <paramref name="liveTitle"/> (the page's own resolved title) wins, then
    /// the route's <c>Arg</c> when the kind carries a display name there, then the KIND'S OWN loc default.
    /// <para>There is deliberately NO fall-through that names a real destination: 0.2.9's
    /// <c>_ =&gt; (Loc.Get(Strings.Nav.YourLibrary), Icons.MusicNote)</c> is what labelled a discography tab, a
    /// what's-new tab and the Connect diagnostics page "Your Library" (ch 18 §7).</para></summary>
    public static (string Title, string Glyph) Dest(in Route route, ReadOnlySpan<char> liveTitle = default)
    {
        ref readonly var row = ref Row(route.Kind);
        if (!liveTitle.IsWhiteSpace()) return (liveTitle.Trim().ToString(), row.Glyph);
        if (CarriesDisplayName(route.Kind) && !route.Arg.IsEmpty)
        {
            string arg = Entities.Strings.Resolve(route.Arg);
            if (!string.IsNullOrWhiteSpace(arg))
                return (route.Kind == RouteKind.ArtistConcerts ? Strings.Nav.ArtistConcerts(arg) : arg, row.Glyph);
        }
        // A profile list's tab and history rows read its FACET ("Following"), not the generic "People" row title.
        if (route.Kind == RouteKind.ProfileList && ProfileListRoute.TryParse(route, out var facet, out _))
            return (Loc.Get(ProfileListRoute.FacetLabelKey(facet)), row.Glyph);
        return (Loc.Get(row.TitleLocKey), row.Glyph);
    }

    /// <summary>Kinds whose <c>Arg</c> is a DISPLAY NAME rather than a slot discriminator. <c>search</c> is in both
    /// camps on purpose: the query is the label AND the keep-alive discriminator, exactly as 0.2.9 has it.</summary>
    static bool CarriesDisplayName(RouteKind k) => k is not (RouteKind.Discography or RouteKind.ProfileList or RouteKind.WhatsNew
        or RouteKind.HomeCustomize or RouteKind.Home   // Home's arg is a facet id
        or RouteKind.Settings);   // Settings' arg, when present, is a tab slug (`route=settings&arg=privacy`), never a display name

    // ── 1.2 the key codec: (name, arg) ⇄ Route ──────────────────────────────────────────────────────────────────────
    //
    // Every persisted document in the app still speaks 0.2.9's opaque `(name, arg)` pair: `history.json`,
    // `session.json`, `workspace.tabs.pinned`, `sidebar.acct-*.json`'s pin ids and the `wavee://` verb map. The codec
    // is therefore part of the CORE, not a compatibility shim: a pin id IS a route key.

    /// <summary>The ONE back-compat rewrite for committed navigation (0.2.9's `NavRouteNormalizer`, ported verbatim)
    /// plus the kind resolution. Called from every nav verb and from both restore paths (pinned workspace + session
    /// snapshot), so a page never sees the pre-normalized form.
    /// <list type="bullet">
    /// <item>empty/whitespace <c>search</c> → the Browse directory;</item>
    /// <item><see cref="LegacyRecentsRoute"/> → <see cref="RouteKind.Recents"/>. It is matched by the
    /// <c>home-section:</c> prefix, so <c>IsKnown</c> accepted it, but 0.2.9's `ContentHost` rewrote it to `recents`
    /// before any arm saw it. Persisted history documents and old Home layout documents still carry it.</item>
    /// </list></summary>
    public static Route Parse(ReadOnlySpan<char> name, ReadOnlySpan<char> arg = default, int tab = 0)
    {
        name = name.Trim();
        if (name.IsEmpty) return new Route(RouteKind.NotFound, default, default, tab);

        // NavRouteNormalizer, verbatim, and BEFORE the table walk so the legacy key never resolves as a home-section.
        if (name.SequenceEqual("search") && arg.IsWhiteSpace()) return new Route(RouteKind.Browse, default, default, tab);
        if (name.SequenceEqual(LegacyRecentsRoute)) return new Route(RouteKind.Recents, default, Intern(arg), tab);

        for (int i = 0; i < s_routes.Length; i++)
        {
            ref readonly var row = ref s_routes[i];
            if (row.Key.Length == 0) continue;
            if (!row.IsPrefix)
            {
                if (name.SequenceEqual(row.Key)) return new Route(row.Kind, default, Intern(arg), tab);
                continue;
            }
            if (name.Length <= row.Key.Length || !name.StartsWith(row.Key, StringComparison.Ordinal)) continue;
            var suffix = name[row.Key.Length..];
            return row.Kind switch
            {
                // `disco:<kindInt>:<uri>` — the FACET rides in Arg (0.2.9 has no `disco:` Dest arm, so no display
                // name is lost by the choice), the artist uri in Subject.
                RouteKind.Discography => new Route(RouteKind.Discography, FacetSubject(suffix), Intern(suffix), tab),
                // `people:<facetDigit>:<user uri>` — the same shape: the facet rides in Arg, the user uri in Subject.
                RouteKind.ProfileList => new Route(RouteKind.ProfileList, FacetSubject(suffix), Intern(suffix), tab),
                // The concert family carries a BARE id, not a uri. Rebuild the uri so the page addresses a handle.
                RouteKind.ArtistConcerts => new Route(RouteKind.ArtistConcerts, ConcertSubject("spotify:artist:", suffix), Intern(arg), tab),
                RouteKind.Concert => new Route(RouteKind.Concert, ConcertSubject("spotify:concert:", suffix), Intern(arg), tab),
                _ => new Route(row.Kind, EntityUri.Parse(suffix), Intern(arg), tab),
            };
        }
        return new Route(RouteKind.NotFound, default, Intern(arg), tab);
    }

    /// <summary>Older Home documents and persisted history can still carry this synthetic section route. It was never
    /// page-able (<c>spotify:list:recents:main</c> is not a home-section resource) and it gets no kind of its own.</summary>
    public const string LegacyRecentsRoute = "home-section:spotify:list:recents:main";

    /// <summary>The opaque route KEY a document persists (0.2.9's <c>Route.Name</c>) — and, because a pin id IS a
    /// route key, the id the sidebar stores for a pinned destination.</summary>
    public static string NameOf(in Route route)
    {
        ref readonly var row = ref Row(route.Kind);
        if (!row.IsPrefix) return row.Key;
        return route.Kind switch
        {
            RouteKind.Discography or RouteKind.ProfileList => row.Key + Entities.Strings.Resolve(route.Arg),
            RouteKind.ArtistConcerts or RouteKind.Concert => row.Key + BareIdOf(route.Subject),
            _ => row.Key + route.Subject.Text,
        };
    }

    /// <summary>The display arg a document persists (0.2.9's <c>Route.Arg</c>), or null when the kind carries its
    /// discriminator inside the key instead.</summary>
    public static string? ArgOf(in Route route)
    {
        if (route.Arg.IsEmpty) return null;
        if (route.Kind is RouteKind.Discography or RouteKind.ProfileList) return null;   // the facet is in the key
        string s = Entities.Strings.Resolve(route.Arg);
        return s.Length == 0 ? null : s;
    }

    /// <summary>A route for a destination entity — the one composer every "go to this thing" call site uses, so a key
    /// minted by a menu and a key minted by a sidebar row are the same string.</summary>
    public static Route For(EntityUri uri, ReadOnlySpan<char> displayName = default, int tab = 0)
    {
        if (HasSubject(uri) && EntityUri.IsLikedCollection(uri.Text))
            return new Route(RouteKind.Liked, default, Intern(displayName), tab);
        var kind = uri.Kind switch
        {
            EntityKind.Album => RouteKind.Album,
            EntityKind.Playlist => RouteKind.Playlist,
            EntityKind.Artist => RouteKind.Artist,
            EntityKind.Show => RouteKind.Show,
            EntityKind.Concert => RouteKind.Concert,
            EntityKind.Episode => RouteKind.Episode,
            EntityKind.User => RouteKind.User,
            _ => RouteKind.NotFound,
        };
        return kind == RouteKind.NotFound
            ? new Route(RouteKind.NotFound, default, default, tab)
            : new Route(kind, uri, Intern(displayName), tab);
    }

    /// <summary>The entity a route addresses, for the reverse direction (a pin id back to a uri).</summary>
    public static EntityUri UriOf(in Route route) => route.Subject;

    /// <summary>The uri after the first <c>':'</c> of a facet-keyed suffix (<c>1:spotify:artist:…</c>) — shared by
    /// <c>disco:</c> and <c>people:</c>, which both ride a facet digit in the key.</summary>
    static EntityUri FacetSubject(ReadOnlySpan<char> suffix)
    {
        int colon = suffix.IndexOf(':');
        return colon < 0 || colon + 1 >= suffix.Length ? default : EntityUri.Parse(suffix[(colon + 1)..]);
    }

    static EntityUri ConcertSubject(string prefix, ReadOnlySpan<char> id)
    {
        if (id.IsWhiteSpace()) return default;
        Span<char> buf = stackalloc char[prefix.Length + id.Length];
        prefix.CopyTo(buf);
        id.CopyTo(buf[prefix.Length..]);
        return EntityUri.Parse(buf);
    }

    static string BareIdOf(EntityUri uri) => HasSubject(uri) ? EntityUri.IdOf(uri.Text).ToString() : "";

    static StringId Intern(ReadOnlySpan<char> s)
    {
        var t = s.Trim();
        return t.IsEmpty ? StringId.Empty : Entities.Strings.Intern(t);
    }

    // ══ 2. NAVIGATION ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The browser-style nav model. Both stacks are capped at <see cref="MaxBackStack"/>: an unbounded back
    /// stack is a slow leak over a long listening session, and nobody walks back 200 pages.</summary>
    public struct Nav
    {
        public Route Current;
        public readonly List<Route> Back, Forward;
        public int Tab;

        public Nav(Route home)
        {
            Current = home;
            Back = new List<Route>(16);
            Forward = new List<Route>(16);
            Tab = 0;
        }

        public readonly bool CanBack => Back.Count > 0;
        public readonly bool CanForward => Forward.Count > 0;
    }

    /// <summary>The in-memory back/forward cap (0.2.9's <c>WaveeShell.MaxBackStack</c>).</summary>
    public const int MaxBackStack = 200;

    /// <summary>Commit a forward navigation. Returns false when the route is the one already showing — a re-entrant
    /// Go to the same place must not push a duplicate onto the back stack.
    /// <para>0.2.9's <c>Go</c> is a 19-line verb with SIX side effects beside the stack push (origin write, history
    /// record, recent-surface pin, active-tab sync, omnibar sync, session capture). Those are the SHELL's —
    /// <c>Shell.Host</c> drives them off this reducer's answer — but the stack arithmetic and the cap live here, where
    /// they are testable (ch 18 §9.4: "port the verb, not just the stack").</para></summary>
    public static bool Go(ref Nav n, in Route r)
    {
        if (SameSlot(r, n.Current)) return false;
        n.Back.Add(n.Current);
        if (n.Back.Count > MaxBackStack) n.Back.RemoveAt(0);
        n.Forward.Clear();
        n.Current = r;
        return true;
    }

    /// <summary>Step back. The current route moves onto the forward stack (capped the same way).</summary>
    public static bool BackStep(ref Nav n)
    {
        if (n.Back.Count == 0) return false;
        n.Forward.Add(n.Current);
        if (n.Forward.Count > MaxBackStack) n.Forward.RemoveAt(0);
        n.Current = n.Back[^1];
        n.Back.RemoveAt(n.Back.Count - 1);
        return true;
    }

    /// <summary>Step forward.</summary>
    public static bool ForwardStep(ref Nav n)
    {
        if (n.Forward.Count == 0) return false;
        n.Back.Add(n.Current);
        if (n.Back.Count > MaxBackStack) n.Back.RemoveAt(0);
        n.Current = n.Forward[^1];
        n.Forward.RemoveAt(n.Forward.Count - 1);
        return true;
    }

    /// <summary>A tab SWITCH: show the tab's page as it was. Deliberately NOT a <see cref="Go"/> — no back-stack push
    /// (the tab is not a new place; Back must still leave the page, not undo the switch), no origin write (the
    /// arrival's crumb stands — a Go would erase "Home › Browse › X" back to the IA answer), no history record and no
    /// recent-surface pin (nothing was newly opened). Neutral motion: a switch is neither forward nor back.</summary>
    public static void Restore(ref Nav n, in Route r) => n.Current = r;

    /// <summary>Two routes address the SAME keep-alive slot (kind + subject + the arg discriminator, never the tab:
    /// the same page in two tabs is two slots by <see cref="SlotKey"/>, but Go compares within one tab).</summary>
    public static bool SameSlot(in Route a, in Route b)
    {
        if (a.Kind != b.Kind || a.Subject != b.Subject) return false;
        // PlaceByArg (remediation §3.10, F23): matches KeyedByArg for every row except Home, where the two questions
        // ("is this a new place for history/tab bookkeeping" vs "does it get a new keep-alive instance") disagree —
        // Home's KeyedByArg is false (SlotKey ignores Arg, a facet switch reuses the mounted HomeScreen) but its
        // PlaceByArg is true (Arg IS a real destination, the facet — Go()/TabWorkspace.SetActiveRoute must see a
        // facet switch as a new place or the tab's own stored route would never pick up the new facet).
        return !Row(a.Kind).PlaceByArg || a.Arg == b.Arg;
    }

    // ══ 3. DEEP LINKS ═══════════════════════════════════════════════════════════════════════════════════════════════
    //
    // The pure half of the `wavee://` surface (0.2.9's `App/DeepLinkParse.cs`), plus the composition + gate half of
    // `GoDeepLinkOpen` that 0.2.9 left untested. A deep link is UNTRUSTED input — it arrives from the OS, a browser,
    // another app — so the composed key is checked against the route table before it can become a tab.

    /// <summary>The <c>wavee://</c> verbs. <see cref="Quit"/> (<c>wavee://quit</c>) closes the app even when closing
    /// hides it to the notification area (tray plan §4.7); it never raises the window (<c>Tray.WakeFor</c>).
    /// <see cref="Diag"/> (<c>wavee://diag?cmd=…</c>, the evidence harness — evidence-diagnostics §B) is developer-only,
    /// refused unless developer mode, and never raises the window either.</summary>
    public enum DeepLinkKind : byte { None, Open, Play, Resume, Pause, Report, Quit, Diag }

    /// <summary>A parsed verb. Unknown or garbage input yields <see cref="DeepLinkKind.None"/>; the parser never
    /// throws. Exactly one of <see cref="Context"/> (a Spotify context uri) and <see cref="Link"/> (a pasted module
    /// url) is set on a <see cref="DeepLinkKind.Play"/>.</summary>
    public readonly record struct DeepLinkVerb(DeepLinkKind Kind, Route Route, string Context = "", string Link = "",
        string Arg = "", DiagVerb Diag = default);

    /// <summary>Parse an activation payload into a verb, composing and GATING the route in one step: the six entity
    /// verbs (<c>route=album&amp;arg=spotify:album:…</c>) become <c>album:&lt;uri&gt;</c>, the composed key resolves
    /// through the route table, and a developer-only kind is refused unless <paramref name="developerMode"/> is on.
    /// An unknown or refused key yields <see cref="DeepLinkKind.None"/> and the caller logs it — 0.2.9 opened a real
    /// tab on the not-found page and wrote it into the persisted history log.</summary>
    public static DeepLinkVerb DeepLink(ReadOnlySpan<char> uriOrUrl, bool developerMode = false)
    {
        // A WM_COPYDATA sender that counts its C terminator in cbData delivers `wavee://open?route=home\0`, and the
        // engine rebuilds the string from cbData / 2 chars — the NUL used to ride into the route id (an unknown route)
        // and from there into every nav/log line that carried it. The parser is the one door every intake reaches, so
        // it cuts at the first NUL (privacy-diagnostics audit #16, G10).
        int nul = uriOrUrl.IndexOf('\0');
        if (nul >= 0) uriOrUrl = uriOrUrl[..nul];
        if (!TryParseVerb(uriOrUrl, out string name, out string route, out string arg, out string ctx, out string link, out string query))
            return default;

        // `wavee://diag?cmd=bundle|pixel|scroll|vps|probe&…` — the evidence harness's verbs (EvidenceReport.TryParseDiag).
        // Developer-only, like the developer routes: a link from outside must not drive the app's diagnostics.
        if (name.Equals("diag", StringComparison.OrdinalIgnoreCase))
            return developerMode && EvidenceReport.TryParseDiag(query, out DiagVerb diag)
                ? new DeepLinkVerb(DeepLinkKind.Diag, default, Diag: diag) : default;

        if (name.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            if (route.Length == 0) return default;
            // `wavee://open?route=report&arg=bug|feature|crash|question|idea` — a report is a DIALOG, never a tab or a
            // history entry, so it short-circuits before the entity-route / IsKnown machinery below.
            if (route.Equals("report", StringComparison.OrdinalIgnoreCase))
                return new DeepLinkVerb(DeepLinkKind.Report, default, Arg: arg);

            string key = route;
            string? display = arg.Length == 0 ? null : arg;
            if (display is not null && route.IndexOf(':') < 0 && IsEntityVerb(route))
            {
                key = route + ":" + display;
                display = null;   // the uri lives in the key; Arg is the display name, which a deep link does not carry
            }
            var r = Parse(key, display);
            return IsKnown(r, developerMode) ? new DeepLinkVerb(DeepLinkKind.Open, r) : default;
        }
        if (name.Equals("play", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.Length > 0) return new DeepLinkVerb(DeepLinkKind.Play, default, Context: ctx);
            // `link=` is the module path: the same intake the Play ▸ Link… dialog feeds (YouTube, Twitch, radio…).
            if (link.Length > 0 && LooksLikeUrl(link)) return new DeepLinkVerb(DeepLinkKind.Play, default, Link: link);
            return default;
        }
        if (name.Equals("resume", StringComparison.OrdinalIgnoreCase)) return new DeepLinkVerb(DeepLinkKind.Resume, default);
        if (name.Equals("pause", StringComparison.OrdinalIgnoreCase)) return new DeepLinkVerb(DeepLinkKind.Pause, default);
        if (name.Equals("quit", StringComparison.OrdinalIgnoreCase)) return new DeepLinkVerb(DeepLinkKind.Quit, default);
        return default;
    }

    /// <summary>The route verbs whose <c>arg</c> composes into the key. Spelled out rather than derived from the
    /// prefix table: a deep link names the FAMILY (<c>album</c>), the table names the PREFIX (<c>album:</c>), and
    /// letting the gap close itself would make <c>home-section</c> deep-linkable as a bare word.</summary>
    static bool IsEntityVerb(string route)
        => route is "album" or "pl" or "artist" or "show" or "prerelease" or "module" or "episode" or "user";

    static bool TryParseVerb(ReadOnlySpan<char> raw, out string name, out string route, out string arg,
        out string ctx, out string link, out string query)
    {
        name = route = arg = ctx = link = query = "";
        if (raw.IsWhiteSpace()) return false;

        // A bare `spotify:` entity uri, so the opt-in `spotify:` handler shares ONE activation path with `wavee://`.
        if (TryParseSpotifyUri(raw, out name, out route, out arg, out ctx)) return true;

        if (!TryExtractUri(raw, out string text)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, "wavee", StringComparison.OrdinalIgnoreCase)) return false;

        name = uri.Host;
        if (name.Length == 0)
        {
            string path = uri.AbsolutePath.Trim('/');
            int slash = path.IndexOf('/');
            name = slash < 0 ? path : path[..slash];
        }
        if (name.Length == 0) return false;
        query = uri.Query;
        ReadQuery(uri.Query, out route, out arg, out ctx, out link);
        return true;
    }

    /// <summary>A bare Spotify entity uri. Pages become an OPEN on the shell's own route names; a PLAYABLE track
    /// becomes a PLAY — which is what clicking a shared link to one means, and gating it on Track alone is why a
    /// shared track link fell through to "route is null ⇒ refuse" and did nothing at all. An episode is now an OPEN
    /// too (decision D-2, podcast plan §12): a shared episode link lands on the episode page, whose primary is one
    /// click from playing, rather than starting playback the instant the link is followed. A <c>spotify:user:&lt;id&gt;</c>
    /// opens that person's profile page (#161). Everything else (concerts, search links,
    /// <c>https://open.spotify.com/…</c> web links) is refused, not guessed at.</summary>
    static bool TryParseSpotifyUri(ReadOnlySpan<char> raw, out string name, out string route, out string arg, out string ctx)
    {
        name = route = arg = ctx = "";
        ReadOnlySpan<char> s = raw.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
        if (!s.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) return false;

        // spotify:<kind>:<id> — reject the nested/extra-segment forms rather than guessing.
        ReadOnlySpan<char> rest = s["spotify:".Length..];
        int colon = rest.IndexOf(':');
        if (colon <= 0 || colon + 1 >= rest.Length) return false;
        ReadOnlySpan<char> kind = rest[..colon];
        ReadOnlySpan<char> id = rest[(colon + 1)..];
        if (id.IndexOf(':') >= 0 || id.IndexOfAny(' ', '\t', '"') >= 0) return false;

        // The SHAPE is this parser's business; WHICH entity it addresses is EntityUri's. Lower-case the scheme and the
        // kind (the id stays verbatim — base62 IS case-sensitive).
        string uriText = string.Concat("spotify:", kind.ToString().ToLowerInvariant(), ":", id.ToString());
        var entityKind = EntityUri.KindOf(uriText);
        if (entityKind is EntityKind.Track)
        {
            name = "play";
            ctx = uriText;
            return true;
        }
        route = entityKind switch
        {
            EntityKind.Album => "album",
            EntityKind.Playlist => "pl",
            EntityKind.Artist => "artist",
            EntityKind.Show => "show",
            EntityKind.Episode => "episode",
            EntityKind.User => "user",
            _ => "",
        };
        if (route.Length == 0) return false;
        name = "open";
        arg = uriText;
        return true;
    }

    static bool TryExtractUri(ReadOnlySpan<char> raw, out string uri)
    {
        uri = "";
        ReadOnlySpan<char> s = raw.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
        if (s.StartsWith("wavee:", StringComparison.OrdinalIgnoreCase)) { uri = s.ToString(); return true; }
        int i = raw.IndexOf("wavee://", StringComparison.OrdinalIgnoreCase);
        if (i < 0) i = raw.IndexOf("wavee:", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        int end = i + 1;
        while (end < raw.Length && !char.IsWhiteSpace(raw[end]) && raw[end] != '"') end++;
        uri = raw[i..end].ToString();
        return uri.Length > 0;
    }

    static void ReadQuery(string query, out string route, out string arg, out string ctx, out string link)
    {
        route = arg = ctx = link = "";
        if (query.Length == 0) return;
        ReadOnlySpan<char> q = query;
        if (q[0] == '?') q = q[1..];
        while (q.Length > 0)
        {
            int amp = q.IndexOf('&');
            ReadOnlySpan<char> pair = amp < 0 ? q : q[..amp];
            q = amp < 0 ? default : q[(amp + 1)..];
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            // TRIM AFTER UNESCAPING, not before. `arg=spotify:playlist:<id>%20` unescapes to a trailing space that
            // otherwise rides all the way into a route key: `IsKnown` accepted it on the `pl:` prefix and Spotify
            // rejected the malformed uri with HTTP 400, leaving the playlist in placeholder rows forever.
            string key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).ToString()).Trim();
            string val = eq < 0 || eq + 1 >= pair.Length ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].ToString()).Trim();
            if (key.Equals("route", StringComparison.OrdinalIgnoreCase)) route = val;
            else if (key.Equals("arg", StringComparison.OrdinalIgnoreCase)) arg = val;
            else if (key.Equals("ctx", StringComparison.OrdinalIgnoreCase)) ctx = val;
            else if (key.Equals("link", StringComparison.OrdinalIgnoreCase)) link = val;
        }
    }

    /// <summary>Does this look like a pasted url a playback module could claim? (The module router decides WHICH
    /// module; this only refuses a bare word.)</summary>
    public static bool LooksLikeUrl(ReadOnlySpan<char> s)
    {
        s = s.Trim();
        return s.Length >= 8
            && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    // ══ 4. DRILL TRAIL + NAV ORIGINS + THE MASTHEAD REGISTRY ════════════════════════════════════════════════════════

    /// <summary>One crumb of a drill trail. <see cref="Crumb.Route"/> null marks the CURRENT page — the last crumb,
    /// never clickable.</summary>
    public readonly record struct Crumb(string Label, Route? Route);

    /// <summary>Parent crumb captured at Go time — the JOURNEY answer, keyed by destination route identity.
    /// Deliberately not part of <see cref="Shell.Route"/>: folding it in would fork keep-alive slot identity.</summary>
    public readonly record struct NavOrigin(string Label, Route Route);

    /// <summary>Session-local origins, LRU 32, keyed by destination slot. Back/Forward do not write — the arrival's
    /// origin stands.</summary>
    public static class Origins
    {
        public const int Capacity = 32;

        /// <summary>Bumped on every write so a mounted breadcrumb re-reads.</summary>
        public static readonly Signal<int> Version = new(0);

        static readonly Dictionary<string, NavOrigin> s_map = new(StringComparer.Ordinal);
        static readonly List<string> s_lru = [];

        static string KeyOf(in Route r) => NameOf(r) + "" + (ArgOf(r) ?? "");

        /// <summary>Latest arrival wins, INCLUDING a null that clears the entry (deterministic overwrite — a Go that
        /// carries no origin must erase the previous journey answer, not inherit it).</summary>
        public static void Write(in Route destination, NavOrigin? origin)
        {
            string k = KeyOf(destination);
            if (origin is null)
            {
                if (s_map.Remove(k)) s_lru.Remove(k);
            }
            else
            {
                Touch(k);
                s_map[k] = origin.Value;
                while (s_map.Count > Capacity && s_lru.Count > 0)
                {
                    string old = s_lru[0];
                    s_lru.RemoveAt(0);
                    s_map.Remove(old);
                }
            }
            Version.Value++;
        }

        /// <summary>Subscribing read (a rendering breadcrumb).</summary>
        public static NavOrigin? For(in Route r)
        {
            _ = Version.Value;
            return s_map.TryGetValue(KeyOf(r), out var o) ? o : null;
        }

        /// <summary>Non-subscribing read — session serialize / restore.</summary>
        public static NavOrigin? Peek(in Route r) => s_map.TryGetValue(KeyOf(r), out var o) ? o : null;

        /// <summary>Restore without bumping the version (cold start, before anything is mounted).</summary>
        public static void Restore(in Route r, NavOrigin? origin)
        {
            if (origin is null) return;
            string k = KeyOf(r);
            Touch(k);
            s_map[k] = origin.Value;
        }

        internal static void Clear()
        {
            s_map.Clear();
            s_lru.Clear();
        }

        static void Touch(string k)
        {
            s_lru.Remove(k);
            s_lru.Add(k);
        }
    }

    /// <summary>The breadcrumb as a pure function of the route plus the optional origin captured at Go time.
    /// <para>Without an origin this is the IA answer (same route ⇒ same trail). With one, ONE extra parent crumb is
    /// composed — the journey's answer, never more than one level: an origin that IS the IA root adds nothing; a
    /// same-family origin inserts between root and current (Browse › Netflix › New on Netflix); a foreign-family
    /// origin on a Browse-family page is PREPENDED to the whole IA trail (Home › Browse › Weekly Song Charts — Home is
    /// where the user came from, Browse is where the page lives, and both are true) — except a search LOOKUP, which
    /// jumped straight to the page and would only invent a Browse visit; a foreign-family origin anywhere else
    /// REPLACES the root.</para>
    /// <para>The floor: no label ⇒ an EMPTY trail.</para></summary>
    public static IReadOnlyList<Crumb> Trail(in Route route, ReadOnlySpan<char> liveTitle, NavOrigin? origin = null)
    {
        string? label = !liveTitle.IsWhiteSpace() ? liveTitle.Trim().ToString()
                      : ArgOf(route) is { Length: > 0 } a && !string.IsNullOrWhiteSpace(a) ? a.Trim()
                      : null;

        if (route.Kind is RouteKind.Concerts or RouteKind.ArtistConcerts or RouteKind.Concert)
            return ConcertTrail(route, label, origin);

        if (label is null) return [];
        return Compose(IaArms(route, label), origin, label, route);
    }

    static IReadOnlyList<Crumb> IaArms(in Route route, string label)
    {
        if (route.Kind == RouteKind.HomeSection)
            return [new Crumb(Loc.Get(Strings.Nav.Home), new Route(RouteKind.Home)), new Crumb(label, null)];

        // A Home-minted section drills to Home; a BROWSE section/category lives under Browse in the IA even when the
        // tile that opened it sat on the Home page (a Home Charts Fold). The IA answer says Browse; the JOURNEY answer
        // (Home › Browse › X) is Compose's job, once the opener hands over its origin.
        if (route.Kind is RouteKind.BrowseSection or RouteKind.BrowseCategory or RouteKind.Browse)
            return [new Crumb(Loc.Get(Strings.Browse.HomeTitle), new Route(RouteKind.Browse)), new Crumb(label, null)];

        return [];
    }

    static IReadOnlyList<Crumb> ConcertTrail(in Route route, string? label, NavOrigin? origin)
    {
        var browse = new Crumb(Loc.Get(Strings.Browse.HomeTitle), new Route(RouteKind.Browse));
        string concerts = Loc.Get(Strings.Concerts.Title);
        if (route.Kind == RouteKind.Concerts)
            return Compose([browse, new Crumb(concerts, null)], origin, concerts, route);

        string current = label ?? concerts;
        return Compose(
            [browse, new Crumb(concerts, new Route(RouteKind.Concerts)), new Crumb(current, null)],
            origin, current, route);
    }

    internal static IReadOnlyList<Crumb> Compose(IReadOnlyList<Crumb> ia, NavOrigin? origin, string currentLabel,
        in Route currentRoute)
    {
        if (origin is not { } o || string.IsNullOrWhiteSpace(o.Label)) return ia;
        if (ia.Count > 0 && IsRoot(ia[0], o)) return ia;
        var originCrumb = new Crumb(o.Label.Trim(), o.Route);
        var current = new Crumb(currentLabel, null);
        if (SameFamily(o.Route.Kind, currentRoute.Kind))
        {
            if (ia.Count >= 2) return [ia[0], originCrumb, current];
            return [originCrumb, current];
        }
        if (BrowseFamily(currentRoute.Kind) && ia.Count > 0 && o.Route.Kind != RouteKind.Search)
        {
            // The origin is an ANCESTOR of the IA here, not a replacement for it: the user came from Home, the page
            // lives under Browse, and the Browse crumb stays clickable so the IA parent is one tap away too.
            var arms = new Crumb[ia.Count + 1];
            arms[0] = originCrumb;
            for (int i = 0; i < ia.Count; i++) arms[i + 1] = ia[i];
            return arms;
        }
        // A search result is a LOOKUP, not a place: the query jumped straight to the page, so its crumb stands alone
        // ("pop" › Pop) — a Browse crumb between them would claim a visit that never happened.
        return [originCrumb, current];
    }

    static bool IsRoot(in Crumb root, in NavOrigin origin)
        => root.Route is { } r && r.Kind == origin.Route.Kind && r.Subject == origin.Route.Subject;

    internal static bool SameFamily(RouteKind a, RouteKind b)
        => (BrowseFamily(a) && BrowseFamily(b))
        || (a == RouteKind.HomeSection && b == RouteKind.HomeSection);

    static bool BrowseFamily(RouteKind k)
        => k is RouteKind.Browse or RouteKind.BrowseCategory or RouteKind.BrowseSection
             or RouteKind.Concerts or RouteKind.ArtistConcerts or RouteKind.Concert;

    /// <summary>Route family → the masthead band's title + trail. The band's ONE predicate: an unknown family
    /// collapses (its title is null), which is what keeps the overlay from double-exposing during a swap. Concerts
    /// resolve here with zero page code.</summary>
    public static bool TryMasthead(in Route route, ReadOnlySpan<char> liveTitle, NavOrigin? origin,
        out string? title, out IReadOnlyList<Crumb> trail)
    {
        title = null;
        trail = [];
        if (route.Kind is not (RouteKind.Browse or RouteKind.BrowseCategory or RouteKind.BrowseSection
            or RouteKind.HomeSection or RouteKind.Concerts or RouteKind.ArtistConcerts or RouteKind.Concert))
            return false;

        string? live = liveTitle.IsWhiteSpace() ? null : liveTitle.Trim().ToString();
        trail = Trail(route, live, origin);
        title = live ?? (trail.Count > 0 ? trail[^1].Label : MastheadStaticTitle(route));
        return !string.IsNullOrWhiteSpace(title);
    }

    /// <summary>What a masthead route's head holds (<see cref="PageHeadForm"/>), by the route's kind alone, so the head keeps one height
    /// on its route whatever its trail turns out to be: Browse is a root (its title only); the Concerts family's pages carry their
    /// title in their own hero (the head holds the trail only); every other masthead route is a drill-in page (the trail over the
    /// title).</summary>
    public static PageHeadForm HeadFormOf(in Route route) => route.Kind switch
    {
        RouteKind.Browse => PageHeadForm.Title,
        RouteKind.Concerts or RouteKind.ArtistConcerts or RouteKind.Concert => PageHeadForm.Hero,
        _ => PageHeadForm.Drill,
    };

    static string? MastheadStaticTitle(in Route route)
    {
        if (route.Kind == RouteKind.Browse) return Loc.Get(Strings.Browse.Title);
        if (route.Kind is RouteKind.Concerts or RouteKind.ArtistConcerts or RouteKind.Concert)
            return ArgOf(route) is { Length: > 0 } a ? a : Loc.Get(Strings.Concerts.Title);
        if (ArgOf(route) is { Length: > 0 } arg) return arg.Trim();
        if (route.Kind is RouteKind.BrowseCategory or RouteKind.BrowseSection) return Loc.Get(Strings.Browse.HomeTitle);
        if (route.Kind == RouteKind.HomeSection) return Loc.Get(Strings.Nav.Home);
        return null;
    }

    // ══ 5. THE PAGE-SWAP POLICY ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Which direction the NEXT page swap travels. The nav verbs write this BEFORE the route signal in the
    /// same flush, so the reconciler can <c>Peek</c> it (an untracked read — a motion-only write must never re-run the
    /// keep-alive boundary) and get the direction that belongs to the route it is about to activate.</summary>
    public enum NavTransitionKind : byte { Forward, Back, Neutral }

    /// <summary>The IDENTITY of a keep-alive page slot: which tab, which route. The nav DIRECTION is deliberately NOT
    /// part of it — direction decides how a swap animates, not which page is cached. Folding it in made a motion-only
    /// write on the already-active key look like an activation change, which re-seeded the entrance and re-faded the
    /// whole page with no content change at all.</summary>
    public static string SlotKey(in Route r)
        => r.Tab.ToString(CultureInfo.InvariantCulture) + "" + NameOf(r) + ""
         + (Row(r.Kind).KeyedByArg ? Entities.Strings.Resolve(r.Arg) : "");

    /// <summary>The page's keyed-measurement SCOPE (<c>MeasureMemory.Scope</c>, E17c): the keep-alive slot's identity (tab,
    /// destination and the argument a keyed destination carries), so a region keyed by its role inside a page finds its size
    /// again when the same page instance remounts (a theme switch's new root, a revisit after a keep-alive eviction), and the
    /// same page open in two tabs keeps two sizes. Provided per keep-alive slot by the content host (<c>PageBody</c>).</summary>
    public static string MeasureScopeOf(in Route r) => SlotKey(r) + "/";

    /// <summary>How many pages the content host keeps alive. Back to a page shows it exactly as it was, including
    /// scroll and selection; the fourth oldest is evicted.</summary>
    public const int KeepAliveSlots = 3;

    /// <summary>The page's scroll-restoration SCOPE. The engine keys a saved scroll offset by the <c>ScrollKey</c> alone
    /// (it no longer namespaces by the enclosing keep-alive slot), so every CONTENT page's <c>ScrollKey</c> (album,
    /// playlist, artist, show, episode, library, home, browse, search, recents, concerts, module pages) is composed under
    /// the tab its page lives in: the same content open in two tabs keeps two offsets. Provided per keep-alive slot by
    /// the content host (<c>PageBody</c>); "" outside a page. The rails, panels, the sidebar and dialogs are one per
    /// window, and the tool pages (settings, what's new, diagnostics, customizers, feedback) have one logical instance,
    /// so their keys stay bare.</summary>
    public static readonly Context<string> PageScrollScope = new("");

    /// <summary>The scroll scope of a page in tab <paramref name="tab"/> (a prefix composed onto its scroll keys).</summary>
    public static string ScrollScopeOf(int tab) => "tab" + tab.ToString(CultureInfo.InvariantCulture) + "/";

    /// <summary>The masthead band's fade window — the page's REAL exit window (<see cref="Design.Nav.ExitDurationMs"/>),
    /// so a drill-in's two halves read as one gesture rather than an independent constant that can drift from it.</summary>
    internal const float FadeThroughExitMs = Design.Nav.FadeThroughExitMs;

    /// <summary>The recipe for a page swap (<see cref="Design.Nav.RecipeFor"/>): the one fade-through for every pair of
    /// pages, a plain fade for Neutral.</summary>
    public static LayoutTransition RecipeFor(NavTransitionKind motion)
        => Design.Nav.RecipeFor(MapMotion(motion));

    public static LayoutTransition? RecipeForVideoSafe(NavTransitionKind motion)
        => Design.Nav.RecipeForVideoSafe(MapMotion(motion));

    public static LayoutTransition PageSlideSafeForward => Design.Nav.PageSlideSafeForward;
    public static LayoutTransition PageSlideSafeBack => Design.Nav.PageSlideSafeBack;

    /// <summary>Does a swap between these two routes need the video-safe pair? BOTH sides are classified, because the
    /// outgoing root is still drawing for the whole exit.</summary>
    public static bool NeedsVideoSafe(in Route from, in Route to)
        => from.Kind == RouteKind.Module || to.Kind == RouteKind.Module;

    static Design.NavTransitionKind MapMotion(NavTransitionKind motion) => motion switch
    {
        NavTransitionKind.Back => Design.NavTransitionKind.Back,
        NavTransitionKind.Neutral => Design.NavTransitionKind.Neutral,
        _ => Design.NavTransitionKind.Forward,
    };

    // ══ 6. SHELL.UI — the rail state the player bar's four toggles write ════════════════════════════════════════════
    //
    // 0.2.9's `App/ShellUi.cs` (91 lines). Ch 20 asks for a home and §2 had none; this is it. UI-only chrome state,
    // deliberately NOT on the playback model: the model stays about playback, not chrome.

    /// <summary>Which panel the rail shows when open. Owner K's `Shell/Rail.cs` renders them; the vocabulary lives
    /// here because the player bar's toggles write it and the session document persists it.</summary>
    public enum RailMode : byte { Lyrics, Queue, NowPlaying, Friends, Video }

    public static class Ui
    {
        /// <summary>The rail is open. When false the rail slot animates its width to 0.</summary>
        public static readonly Signal<bool> RailOpen = new(false);

        /// <summary>Which panel the rail shows when open.</summary>
        public static readonly Signal<RailMode> Mode = new(RailMode.Lyrics);

        /// <summary>The rail's expanded width in DIP.</summary>
        public static readonly FloatSignal RailWidth = new(RailDefaultW);

        /// <summary>F243: the rail splitter's PREVIEW width. A drag writes THIS, never <see cref="RailWidth"/>: the content
        /// column, the rail card and the docked video all lay out against the committed <see cref="RailWidth"/>, so a drag
        /// reflows nothing (and the PlayReady stream is resized once, on release) while the seam and its guide follow the
        /// pointer. Equal to <see cref="RailWidth"/> at rest; <c>FrameRules.RailDragCommit</c> is the one commit rule.</summary>
        public static readonly FloatSignal RailDragWidth = new(RailDefaultW);

        /// <summary>Docked music-video cap height in DIP. Floor is 16:9 of <see cref="RailWidth"/>; the vertical
        /// splitter only grows from there.</summary>
        public static readonly FloatSignal DockedVideoHeight = new(DockedVideoNaturalH(RailDefaultW));

        /// <summary>Has the user DELIBERATELY sized the docked cap for what is playing right now? False (the default)
        /// means <see cref="DockedVideoHeight"/> follows the content's own aspect
        /// (<see cref="FitDockedVideoHeight"/>), so a 16:9 stream fills the card edge to edge instead of sitting in
        /// letterbox bars. The vertical splitter sets it on a committed drag; the docked surface CLEARS it when the
        /// source changes.</summary>
        public static readonly Signal<bool> DockedVideoHeightPinned = new(false);

        /// <summary>The rail column's laid-out height. The docked cap clamps against the remainder so a 560 DIP video
        /// cannot shove lyrics off the window.</summary>
        public static readonly FloatSignal RailBodyHeight = new(0f);

        /// <summary>The PLAYABLE uri the attached page's own stage would host — a module watch page's video — or
        /// <c>""</c> when no attached page stages anything. NAVIGATION state, not rail state, which is why it lives
        /// beside the chrome the arbitration reads; and the EMPTY STRING (not null) is the resting value, so a reader
        /// can treat empty as "nothing staged" and never accidentally match an empty playing uri.
        /// <para>A PLAYABLE uri, and that is the whole point: a module's entity id space and its playable id space are
        /// deliberately different, so comparing the page's own ENTITY uri against the playing uri could never be equal
        /// and the page stage never mounted — on the one module the feature exists for.</para></summary>
        public static readonly Signal<string> ActiveStagePlayable = new("");

        /// <summary>Whether the rail can currently reserve inline layout width alongside the sidebar and the content
        /// region. When false the shell FLOATS the rail over content instead of allocating row width for it. A DERIVED
        /// FACT published by one effect — the shell must never probe three widths at a call site.</summary>
        public static readonly Signal<bool> RailFits = new(true);

        /// <summary>The immersive fullscreen lyrics surface is open. Deliberately NOT a <see cref="RailMode"/>: the
        /// surface covers the whole shell rather than replacing the rail's panel, so the rail's own state is
        /// untouched.</summary>
        public static readonly Signal<bool> ImmersiveLyrics = new(false);

        // ── the frame's derived presentation cells (stage 2, `Shell.UI.cs` writes them from ONE effect each) ───────────
        //
        // DERIVED FACTS LIVE ON THE MODEL (ch 18 §7): the sidebar mounts, the player bar and the rail read these instead
        // of each re-deriving "is the window narrow" from three widths at a call site.

        // The floating surface's bottom reservation is owner K's `Video.FloatingSurfaceReserve` (the PiP writes it); the
        // content host reads that one cell rather than a second copy here.

        /// <summary>Clicking the already-showing mode CLOSES the rail; otherwise switch to that mode and open.</summary>
        public static void Toggle(RailMode mode) => PanelHost.Shared.Toggle(mode);

        /// <summary>Viewport-fit test for sidebar + rail + a minimum usable content region.</summary>
        public static bool CanFitRail(float viewportW, float sidebarW, float railW = RailDefaultW,
            float minContentW = MinContentW)
            => Shell.CanFitRail(viewportW, sidebarW, railW, minContentW);
    }

    // ── 6.1 the rail's clamps (one pair, every writer) ──────────────────────────────────────────────────────────────
    // One clamp for every writer (the left-seam splitter, the settings seed, CanFitRail). A second literal pair is how
    // the sidebar drag and the probe drifted apart in 0.2.9; do not reintroduce that here.

    /// <summary>The shell's baseline panel bounds and content floor: <c>PanelPolicy.Default</c> (what Wavee Classic declares) is
    /// built from them; the clamp reads the policy.</summary>
    public const float RailMinW = 200f, RailMaxW = 500f, RailDefaultW = 340f;
    public const float MinContentW = 480f;

    /// <summary>The one panel-width clamp: the active panel policy's bounds (Classic 200-500), or <paramref name="policy"/>'s.</summary>
    public static float ClampRailWidth(float w, Theming.PanelPolicy? policy = null) => ShellPolicy.PanelOr(policy).ClampWidth(w);

    /// <summary>16:9 height of a full-bleed rail-width video cap (no inset). The splitter's floor — drag only grows
    /// from here.</summary>
    public static float DockedVideoNaturalH(float railW) => railW * 9f / 16f;

    /// <summary>How tall the docked cap may grow. Leaves the lyrics/queue body as the remaining Grow=1 column.</summary>
    public const float DockedVideoMaxH = 560f;

    public static float ClampDockedVideoHeight(float h, float railW, float remainingRailH = DockedVideoMaxH)
    {
        float min = DockedVideoNaturalH(railW);
        float max = Math.Max(min, DockedCapCeiling(remainingRailH));
        return h <= 0f ? min : Math.Clamp(h, min, max);
    }

    /// <summary>Hard floor for a CONTENT-fitted cap (not the splitter's 16:9 floor): tall enough that the transport
    /// chrome stays usable, short enough that an ultra-wide (2.35:1+) stream fits edge to edge without bars.</summary>
    public const float DockedVideoFitMinH = 120f;

    /// <summary>The cap height that matches the CONTENT'S OWN aspect at the current rail width. Deliberately NOT
    /// routed through <see cref="ClampDockedVideoHeight"/>: that clamp's minimum is the 16:9 splitter floor, and
    /// flooring a CONTENT fit at 16:9 forces every wider-than-16:9 stream (a 2.35:1 music video) 32%+ taller than its
    /// own aspect — guaranteed black bars, the exact thing this fit exists to remove.</summary>
    /// <param name="railW">The rail's current width — the card is full-bleed, so this IS the video's width.</param>
    /// <param name="naturalW">The media's natural pixel width; ≤ 0 = not reported yet (answers 16:9).</param>
    /// <param name="naturalH">The media's natural pixel height; ≤ 0 = not reported yet.</param>
    public static float DockedCapCeiling(float remainingRailH)
    {
        float cap = remainingRailH > 0f && float.IsFinite(remainingRailH) ? remainingRailH : DockedVideoMaxH;
        return Math.Clamp(cap, DockedVideoFitMinH, DockedVideoMaxH);
    }

    public static float FitDockedVideoHeight(float railW, int naturalW, int naturalH, float remainingRailH = DockedVideoMaxH)
    {
        float ratio = naturalW > 0 && naturalH > 0 ? (float)naturalH / naturalW : 9f / 16f;
        return Math.Clamp(railW * ratio, DockedVideoFitMinH, DockedCapCeiling(remainingRailH));
    }

    /// <summary>Viewport-fit test for sidebar + rail + a minimum usable content region.</summary>
    public static bool CanFitRail(float viewportW, float sidebarW, float railW = RailDefaultW,
        float minContentW = MinContentW)
        => sidebarW + railW + minContentW <= viewportW;

    // ══ 7. THE PLAYER BAR'S TIERS AND ITS DEVICE PICKER (ch 20, +230 CORE) ══════════════════════════════════════════

    /// <summary>The identity-first player-dock pressure tiers, ordered from the 300-DIP floor to the full desktop
    /// bar.</summary>
    public enum PlayerBarTier : byte { Minimal, Compact, Medium, Comfortable, Wide, Full }

    /// <summary>The player bar's pressure machine: which of the six levels a width belongs to, and the narrowing hold. The
    /// level starts and the hold are the active <see cref="Theming.PlayerBarPolicy"/>'s (Classic 440 / 760 / 900 / 1100 / 1240 and
    /// 24); every method takes it as a trailing argument (null = the active one).</summary>
    public static class PlayerBar
    {
        /// <summary>The level a width belongs to before any narrowing hold.</summary>
        public static PlayerBarTier Nominal(float width, Theming.PlayerBarPolicy? policy = null)
            => (PlayerBarTier)ShellPolicy.PlayerBarOr(policy).LevelOf(width);

        /// <summary>Widening commits immediately; narrowing holds the current presentation through the policy's dip (24 DIP in
        /// Classic) so a pointer resize cannot chatter structural controls at a threshold.</summary>
        public static PlayerBarTier Resolve(float width, PlayerBarTier current, bool initialized, Theming.PlayerBarPolicy? policy = null)
        {
            if (width <= 0f) return current;
            policy = ShellPolicy.PlayerBarOr(policy);
            var nominal = Nominal(width, policy);
            if (!initialized || nominal >= current) return nominal;
            var dipped = Nominal(width + policy.Hysteresis, policy);
            return dipped < current ? dipped : current;
        }
    }

    /// <summary>The complete structural/metric policy for one player-bar tier.
    /// <para>HIT-TARGET FLOORS (WinUI minimums, not a tuning preference). A transport button is a pointer AND a touch
    /// target, and WinUI's control ladder bottoms out at 32×32 with a 16-DIP glyph; the primary transport takes the
    /// 40/20 rung wherever the row has room. 0.2.9's tier table ramped the secondaries 32/30/28/26 and the primary
    /// 36/36/34/30, so <b>three of six tiers shipped sub-minimum targets</b> — the narrowest window, i.e. exactly
    /// where a mis-click costs most. The metrics below are therefore FLAT across the ladder: pressure is absorbed by
    /// what the row DROPS and by the spacing rungs, never by the buttons.</para>
    /// <para>The 300-DIP floor still balances: 12 row pad + 132 identity block + 6 row gaps + 36 primary + 2 cluster
    /// gap + 32 devices + 32 overflow = 252, leaving the seek bar the ~48 DIP it grows into. That is the honest trade
    /// — a short seek line at the absolute floor, never a 26-DIP Next button.</para></summary>
    public readonly record struct PlayerBarLayout(
        PlayerBarTier Tier,
        bool ShowExpand, bool ShowDevices, bool ShowQueue, bool ShowVolumeSlider, bool ShowShuffleRepeat,
        bool ShowLikeSlot, bool ShowVolumeButton, bool ShowLyrics, bool ShowRemoteDeviceLine,
        bool ShowTimesElapsed, bool ShowTimesRemaining, bool ShowPrevNext, bool ShowSubtitle,
        float ButtonBox, float ButtonGlyph, float PrimaryBox, float PrimaryGlyph,
        float LeftW, float ArtSize, float RowGap, float RowPad, float ClusterGap, float LeftGap, float SeekGap,
        float RightGap, float TopEdgeWidth, float RightWMax,
        bool ShowTitleBlock = true)
    {
        static bool Has(Theming.PlayerBarSlots set, Theming.PlayerBarSlots slot) => (set & slot) != 0;

        public const float MinButtonBox = 32f;
        public const float MinButtonGlyph = 16f;
        public const float MinPrimaryBox = 36f;
        public const float MinPrimaryGlyph = 18f;

        /// <summary>The inline volume rail's length (the stock 22-DIP thumb ring rides it).</summary>
        public const float VolumeSliderW = 96f;

        /// <summary>The video split button's disclosure chevron: narrow, full button height.</summary>
        public const float SplitChevronW = 20f;

        /// <summary>The indeterminate seek sweep's nominal track width. Used to be a hard 2400 literal — correct for
        /// the windows the ladder was authored against, but on a 3440-DIP ultrawide the sweep stopped 1040+ DIP short
        /// of the bar itself. Derived from the bar's OWN measured width, floored at the old nominal so nothing
        /// narrower regresses.</summary>
        public const float TopEdgeWidthFloor = 2400f;

        public static PlayerBarLayout Initial(float width, Theming.PlayerBarPolicy? policy = null)
            => ForTier(Shell.PlayerBar.Nominal(width, policy), width, policy);

        public static PlayerBarLayout Resolve(float width, in PlayerBarLayout current, bool initialized, Theming.PlayerBarPolicy? policy = null)
            => ForTier(Shell.PlayerBar.Resolve(width, current.Tier, initialized, policy), width, policy);

        /// <param name="tier">The resolved pressure tier.</param>
        /// <param name="width">The bar's measured width in DIP; 0 keeps the pre-fix 2400 behaviour via the floor.</param>
        /// <param name="policy">The player-bar policy whose level <paramref name="tier"/> picks; null is the active one.</param>
        public static PlayerBarLayout ForTier(PlayerBarTier tier, float width = 0f, Theming.PlayerBarPolicy? policy = null)
        {
            policy = ShellPolicy.PlayerBarOr(policy);
            var level = policy.Tiers[(int)tier];
            var slots = level.VisibleSlots;

            var layout = new PlayerBarLayout(
                Tier: tier,
                ShowExpand: Has(slots, Theming.PlayerBarSlots.Expand),
                ShowDevices: Has(slots, Theming.PlayerBarSlots.Devices),   // the device picker is the only route when local playback is unavailable
                ShowQueue: Has(slots, Theming.PlayerBarSlots.Queue),
                ShowVolumeSlider: Has(slots, Theming.PlayerBarSlots.VolumeSlider),
                ShowShuffleRepeat: Has(slots, Theming.PlayerBarSlots.ShuffleRepeat),
                ShowLikeSlot: Has(slots, Theming.PlayerBarSlots.LikeSlot),         // identity-first: the heart survives down to the 300-DIP floor
                ShowVolumeButton: Has(slots, Theming.PlayerBarSlots.VolumeButton),
                ShowLyrics: Has(slots, Theming.PlayerBarSlots.Lyrics),
                ShowRemoteDeviceLine: Has(slots, Theming.PlayerBarSlots.RemoteDeviceLine),
                ShowTimesElapsed: Has(slots, Theming.PlayerBarSlots.TimesElapsed),
                ShowTimesRemaining: Has(slots, Theming.PlayerBarSlots.TimesRemaining),
                ShowPrevNext: Has(slots, Theming.PlayerBarSlots.PrevNext),
                ShowSubtitle: Has(slots, Theming.PlayerBarSlots.Subtitle),         // title + artist are one indivisible identity block
                ShowTitleBlock: Has(slots, Theming.PlayerBarSlots.Title),
                ButtonBox: level.ButtonBox,
                ButtonGlyph: level.ButtonGlyph,
                PrimaryBox: level.PrimaryBox,
                PrimaryGlyph: level.PrimaryGlyph,
                LeftW: level.LeftW,
                ArtSize: level.ArtSize,
                RowGap: level.RowGap,
                RowPad: level.RowPad,
                ClusterGap: level.ClusterGap,
                LeftGap: level.LeftGap,
                SeekGap: level.SeekGap,
                RightGap: level.RightGap,
                TopEdgeWidth: MathF.Max(TopEdgeWidthFloor, width),
                RightWMax: 0f);
            // The right cluster's WIDEST width at this tier — the slot sum WITH the video split (PlayerBarRules.RightWidth,
            // hasVideo: true). The live cluster is that or the same less the split (the one state input, user decision
            // 2026-09-16); the 300-DIP seek-bar floor and any arithmetic that must hold in the widest case use this.
            return layout with { RightWMax = PlayerBarRules.RightWidth(in layout, hasVideo: true) };
        }
    }

    // ── 7.1 the device picker's rows ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The role of a picker row (engine-free so the composition is unit-tested without a flyout type).</summary>
    public enum DevicePickerRowKind : byte
    {
        Header,          // a disabled section-header row ("This computer" / "Spotify Connect")
        Separator,       // divider between sections
        LocalDefault,    // the "System default" radio (DeviceId == "")
        LocalDevice,     // a specific this-computer output radio
        Quality,         // disabled: the ACTUAL playing quality, echoed from the player — never a setting loopback
        ConnectDevice,   // a Spotify Connect device radio (click = transfer)
        Empty,           // a disabled placeholder row (no-devices / hint)
    }

    /// <summary>One row in the two-section device picker — a POD the UI maps to a menu item.</summary>
    public readonly record struct DevicePickerRow(
        DevicePickerRowKind Kind,
        string Label,
        bool IsChecked = false,
        bool Enabled = true,
        string DeviceId = "",
        string? Accelerator = null,
        byte LocalKind = 0,
        Spotify.Decode.DeviceKind ConnectKind = Spotify.Decode.DeviceKind.Computer);

    /// <summary>Hard cap on device labels — endpoint names are short (DeviceDesc-first) but Connect names and
    /// duplicate-name fallbacks can be arbitrarily long, and the flyout sizes to its widest row.</summary>
    public const int DevicePickerMaxLabelChars = 48;

    internal static string CapLabel(string label)
        => label.Length <= DevicePickerMaxLabelChars
            ? label
            : label[..(DevicePickerMaxLabelChars - 1)].TrimEnd() + "…";

    /// <summary>The two-section device picker: "This computer" (System default + the local render endpoints), then a
    /// separator + "Spotify Connect" (the roster, with THIS device filtered out — this PC is section 1).
    /// <para><b>The ownership contract, applied (ch 20 §9.11).</b> 0.2.9 checked a Connect row when its id matched the
    /// active id <b>OR</b> when the cluster's own <c>IsActive</c> bit was set. That fallback contradicts the ownership
    /// authority — the same fix the remote-device line already took after the 2026-09-11 Connect incident — so 0.3
    /// checks against the ACTIVE ID ALONE. A deliberate divergence from 0.2.9, recorded here so a side-by-side
    /// reviewer does not file it as a regression.</para></summary>
    public static List<DevicePickerRow> DevicePickerRows(
        ReadOnlySpan<Playback.Audio.LocalAudioDevice> local,
        string? selectedLocalId,
        bool localSupported,
        bool weAreActiveOutput,
        ReadOnlySpan<Playback.Devices.Row> connect,
        string? activeConnectId,
        Playback.Audio.Opened observed,
        Spotify.Audio.Quality askedQuality)
    {
        var rows = new List<DevicePickerRow>(local.Length + connect.Length + 6);
        // stale-truthful "Unavailable" only when local playback is genuinely unsupported
        string? acc = localSupported ? null : Loc.Get(Strings.Player.Unavailable);

        rows.Add(new DevicePickerRow(DevicePickerRowKind.Header, Loc.Get(Strings.Player.ThisComputer), Enabled: false));
        rows.Add(new DevicePickerRow(DevicePickerRowKind.LocalDefault, Loc.Get(Strings.Player.SystemDefault),
            IsChecked: localSupported && weAreActiveOutput && string.IsNullOrEmpty(selectedLocalId),
            Enabled: localSupported, DeviceId: "", Accelerator: acc));
        for (int i = 0; i < local.Length; i++)
        {
            ref readonly var d = ref local[i];
            rows.Add(new DevicePickerRow(DevicePickerRowKind.LocalDevice, CapLabel(d.Name),
                IsChecked: localSupported && weAreActiveOutput && !string.IsNullOrEmpty(selectedLocalId)
                           && string.Equals(d.Id, selectedLocalId, StringComparison.OrdinalIgnoreCase),
                Enabled: localSupported, DeviceId: d.Id, Accelerator: acc, LocalKind: d.Kind));
        }

        rows.Add(QualityRow(observed, askedQuality));

        rows.Add(new DevicePickerRow(DevicePickerRowKind.Separator, ""));
        rows.Add(new DevicePickerRow(DevicePickerRowKind.Header, Loc.Get(Strings.Player.SpotifyConnect), Enabled: false));
        int connectCount = 0;
        for (int i = 0; i < connect.Length; i++)
        {
            ref readonly var d = ref connect[i];
            if (d.Kind == Spotify.Decode.DeviceKind.ThisDevice) continue;
            connectCount++;
            rows.Add(new DevicePickerRow(DevicePickerRowKind.ConnectDevice, CapLabel(d.Name ?? ""),
                IsChecked: !string.IsNullOrEmpty(activeConnectId)
                           && string.Equals(d.Id, activeConnectId, StringComparison.OrdinalIgnoreCase),
                DeviceId: d.Id ?? "", ConnectKind: d.Kind));
        }
        if (connectCount == 0)
        {
            rows.Add(new DevicePickerRow(DevicePickerRowKind.Empty, Loc.Get(Strings.Player.NoDevices), Enabled: false));
            rows.Add(new DevicePickerRow(DevicePickerRowKind.Empty, Loc.Get(Strings.Player.NoDevicesHint), Enabled: false));
        }
        return rows;
    }

    /// <summary>The verification row: what the player is ACTUALLY echoing, never a loopback of the setting (the
    /// user's own wording — "so we can verify the quality is being selected and played"). <paramref name="observed"/>
    /// is the player's own echo (<c>Playback.Audio.PlayingOpened</c>); its <c>Label</c> is empty exactly when
    /// nothing has been opened yet (<see cref="Playback.Audio.Opened"/> default / the silent-endpoint open). The
    /// "below setting" wording fires ONLY when the observed format sits on the quality ladder at all — an AAC/ICY
    /// radio stream (<see cref="Spotify.Audio.Rung"/> = −1) is not a claim about the Spotify quality setting, so it
    /// never disagrees with it.</summary>
    static DevicePickerRow QualityRow(Playback.Audio.Opened observed, Spotify.Audio.Quality askedQuality)
    {
        if (string.IsNullOrEmpty(observed.Label))
            return new DevicePickerRow(DevicePickerRowKind.Quality, Loc.Get(Strings.Player.QualityIdle), Enabled: false);

        int observedRung = Spotify.Audio.Rung(observed.Format);
        int askedRung = Spotify.Audio.TargetRung(askedQuality);
        string text = observedRung >= 0 && observedRung < askedRung
            ? Strings.Player.QualityBelowSetting(observed.Label, Loc.Get(AskedQualityLabel(askedQuality)))
            : Strings.Player.QualityPlaying(observed.Label);
        return new DevicePickerRow(DevicePickerRowKind.Quality, text, Enabled: false);
    }

    /// <summary>The setting's own name for <paramref name="quality"/> — the same four labels the Settings page's
    /// quality combo offers (<c>ClassicSettings.QualityLabels</c>), so "Lossless unavailable" reads exactly
    /// like the row the user picked it from.</summary>
    static string AskedQualityLabel(Spotify.Audio.Quality quality) => quality switch
    {
        Spotify.Audio.Quality.Normal96 => Strings.Settings.Playback.QualityNormal,
        Spotify.Audio.Quality.High160 => Strings.Settings.Playback.QualityHigh,
        Spotify.Audio.Quality.VeryHigh320 => Strings.Settings.Playback.QualityVeryHigh,
        _ => Strings.Settings.Playback.QualityLossless,
    };

    // ── 7.2 where a now-playing span goes ───────────────────────────────────────────────────────────────────────────

    /// <summary>Which part of a now-playing identity cluster was clicked. The three slots are the ONE vocabulary the
    /// player bar, the immersive stage and any future identity surface share, so "where does the art go?" is answered
    /// in one table rather than re-decided per surface.</summary>
    public enum LinkSlot : byte
    {
        /// <summary>The track TITLE. Spotify: its album. A module playable: its own page.</summary>
        Title,
        /// <summary>The SUBTITLE (the artist row). Spotify: the primary artist. A module playable: whoever published
        /// it — a YouTube channel, a Twitch channel, a radio station.</summary>
        Artist,
        /// <summary>The ART tile. A module playable: its own page. Spotify: nothing here — the bar's art tile opens
        /// the playback CONTEXT, which is not derivable from the track, so the caller keeps that answer.</summary>
        Art,
    }

    /// <summary>The route a track's identity slot navigates to, or a <see cref="RouteKind.NotFound"/> route when the
    /// slot must stay INERT.
    /// <para>0.2.9's gate was <c>ArtistRef.Uri.Length &gt; 0</c> — a SPOTIFY question wearing a general name. A module
    /// playable carries artist NAMES with no uri, so every module subtitle was styled-but-inert and every module
    /// title/art tile was dead, even once the module had told us exactly which page it wanted. A module playable
    /// therefore never falls through to the Spotify arms.</para></summary>
    /// <param name="track">The playing track handle.</param>
    /// <param name="slot">Which part of the identity cluster was clicked.</param>
    /// <param name="isModulePlayable">The uri belongs to a playback module (owner T's <c>Modules.cs</c> answers it —
    /// passed in rather than probed, so this file does not depend on a Wave-6 module registry).</param>
    /// <param name="moduleRoute">The route the module named for this slot, or null when it named none. Nullable
    /// rather than defaulted: <c>default(Route)</c> is Home (see <see cref="Route.None"/>).</param>
    public static Route LinkFor(Track track, LinkSlot slot, bool isModulePlayable = false, Route? moduleRoute = null)
    {
        if (!isModulePlayable && track.IsValid) isModulePlayable = Modules.IsModulePlayable(track.Id);
        if (isModulePlayable) moduleRoute ??= Modules.LinkRouteFor(track, slot);
        if (moduleRoute is { IsNone: false } named) return named;
        if (isModulePlayable) return new Route(RouteKind.NotFound);
        switch (slot)
        {
            case LinkSlot.Title:
            {
                var album = track.Album;
                return album.IsValid && album.Uri.IsValid ? For(album.Uri) : new Route(RouteKind.NotFound);
            }
            case LinkSlot.Artist:
            {
                var artists = track.ArtistSlots;
                if (artists.Length == 0) return new Route(RouteKind.NotFound);
                var primary = new Artist(artists[0]);
                return primary.IsValid && primary.Uri.IsValid ? For(primary.Uri) : new Route(RouteKind.NotFound);
            }
            default:
                return new Route(RouteKind.NotFound);
        }
    }

    // ══ 8. TINT OWNERSHIP — the material hand-over ══════════════════════════════════════════════════════════════════
    //
    // The window's material carries the PAGE's colour, and the one rule that matters is that it NEVER dips to neutral
    // between two coloured pages. A page CLAIMS the plane the moment it navigates (with whatever colour it already
    // has, which may be none); an ungraded claim HOLDS the previous colour rather than painting grey; a DEFINITE
    // grading replaces it; and a grading that arrives for a page the user has already left is REFUSED.

    /// <summary>Who owns the shell material right now, and whether its colour is settled. <see cref="Argb"/> 0 means
    /// "no colour of its own yet"; neutral is a real value (the ground at A = 0.03), never <c>Transparent</c>.</summary>
    public readonly record struct TintOwner(string Claimant, uint Argb, bool Definite)
    {
        public static TintOwner Neutral => new("", 0u, true);
        public bool IsNeutral => Claimant.Length == 0;
    }

    /// <summary>The material hand-over, as a pure fold. Returns the new owner.
    /// <list type="bullet">
    /// <item><b>claim</b> — a route takes the plane. An ungraded claim keeps the CURRENT colour so the chrome never
    /// flashes neutral on the way in.</item>
    /// <item><b>refresh</b> — a grading lands for the claimant that already owns the plane. A definite grading always
    /// wins; an indefinite one never demotes a definite.</item>
    /// <item><b>neutral</b> — an empty claimant, the value a route that publishes no material of its own writes.</item>
    /// </list></summary>
    public static TintOwner Claim(in TintOwner current, string claimant, uint argb, bool definite)
    {
        if (claimant.Length == 0) return TintOwner.Neutral;
        bool sameClaimant = string.Equals(current.Claimant, claimant, StringComparison.Ordinal);
        if (!sameClaimant)
            // A new page: it owns the plane from this instant. With no colour yet it HOLDS the outgoing colour.
            return new TintOwner(claimant, definite || argb != 0u ? argb : current.Argb, definite);
        if (!definite && current.Definite) return current;
        return new TintOwner(claimant, argb != 0u ? argb : current.Argb, definite || current.Definite);
    }

    /// <summary>The claimant key a route publishes under. Two different pages must never collide, and the same page
    /// re-entered must reclaim its own plane — so it is the slot key WITHOUT the tab.</summary>
    public static string TintClaimantOf(in Route route)
        => NameOf(route) + (Row(route.Kind).KeyedByArg ? "" + Entities.Strings.Resolve(route.Arg) : "");

    // ══ 9. THE AUTH FOLD (ch 18 §7 DATA GAP) ════════════════════════════════════════════════════════════════════════

    /// <summary>What the profile chip says. A FOLD, not four <c>if</c>s at the chip: the four states are mutually
    /// exclusive and the chip renders one verb per state.</summary>
    public enum AuthState : byte
    {
        /// <summary>Signed in and online.</summary>
        Live,
        /// <summary>A login or a silent resume is in flight.</summary>
        Connecting,
        /// <summary>A credential is stored but the session is down — the shell stays up on the cached library and the
        /// chip offers Reconnect.</summary>
        Offline,
        /// <summary>Nothing to resume: the sign-in surface owns the window.</summary>
        SignInRequired,
    }

    /// <summary>Fold the session's phase + fault + whether a credential is stored into the chip's one answer.
    /// <para>Only <see cref="Spotify.SessionFault.CredentialRejected"/> / <see cref="Spotify.SessionFault.NoCredential"/>
    /// and "no credential at all" reach <see cref="AuthState.SignInRequired"/> — a connectivity blip must never push a
    /// signed-in user at a login screen, which is 0.2.9's rule and its reason.</para></summary>
    public static AuthState FoldAuth(Spotify.SessionPhase phase, Spotify.SessionFault fault, bool hasStoredCredential)
    {
        if (!hasStoredCredential) return AuthState.SignInRequired;
        return phase switch
        {
            Spotify.SessionPhase.Online => AuthState.Live,
            Spotify.SessionPhase.Failed => fault is Spotify.SessionFault.CredentialRejected or Spotify.SessionFault.NoCredential
                ? AuthState.SignInRequired
                : AuthState.Offline,
            Spotify.SessionPhase.Offline => AuthState.Offline,
            _ => AuthState.Connecting,
        };
    }

    // ══ 11. SHELL.HISTORY — the navigation log's decisions (ch 16 §8, +180) ═════════════════════════════════════════
    //
    // "Recently VISITED" (places you navigated to), as distinct from the play log's "recently PLAYED". The STORE is
    // `Shell.Host.cs` (history.json); every decision the page makes is here, because 0.2.9 had them inline in a
    // 616-line page component where nothing could pin them.

    /// <summary>One navigation event: its destination and when it happened.</summary>
    public readonly record struct HistoryEntry(Route Route, DateTime VisitedAt);

    /// <summary>The navigation log's pure rules. The cap, the filter chips, the search, the date grouping and the
    /// most-visited fold — every one of them untested in 0.2.9. The STORE half (history.json) is the partial in
    /// <c>Shell.Host.cs</c>.</summary>
    public static partial class History
    {
        /// <summary>Ring cap. FIFO: the oldest entry is evicted on add, and the store writes only the newest
        /// <c>min(count, 500)</c>.</summary>
        public const int MaxEntries = 500;

        /// <summary>Route prefix of the removed fake-history seed. Nothing legitimate mints it — a real playlist route
        /// is <c>pl:spotify:playlist:…</c> — so every such row is a dead destination the page would still offer. The
        /// load path drops them AND rewrites the file, so the log stops carrying them at all.</summary>
        public const string DeadSeedPrefix = "pl:local:";

        /// <summary>The filter chips, in the order the page lists them.</summary>
        public enum Filter : byte { All, Playlists, Shows, Library, Search, Pages }

        /// <summary>The two sorts. <see cref="Sort.MostVisited"/> counts over the FULL UNFILTERED log.</summary>
        public enum Sort : byte { MostRecent, MostVisited }

        /// <summary>The row's family — the filter chips and the row's kind label both read it.</summary>
        public static string KindOf(in Route r) => r.Kind switch
        {
            RouteKind.Playlist => "playlist",
            // A show opens the shared detail surface (like album/artist), so it is an ENTITY visit, not a generic
            // page — its own kind, so the chips and the row label stay truthful.
            RouteKind.Show => "show",
            RouteKind.LibraryAlbums or RouteKind.LibraryArtists or RouteKind.Liked
                or RouteKind.LibraryPodcasts or RouteKind.LibraryAudiobooks or RouteKind.Local => "library",
            RouteKind.Search => "search",
            RouteKind.Browse or RouteKind.BrowseCategory or RouteKind.BrowseSection => "browse",
            _ => "page",
        };

        public static bool PassesFilter(in HistoryEntry e, Filter f) => f switch
        {
            Filter.Playlists => KindOf(e.Route) == "playlist",
            Filter.Shows => KindOf(e.Route) == "show",
            Filter.Library => KindOf(e.Route) == "library",
            Filter.Search => KindOf(e.Route) == "search",
            Filter.Pages => KindOf(e.Route) is "page" or "browse",
            _ => true,
        };

        public static bool PassesSearch(in HistoryEntry e, string q)
        {
            if (q.Length == 0) return true;
            var (title, _) = Dest(e.Route);
            return title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (ArgOf(e.Route)?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || NameOf(e.Route).Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        public static int CountUniqueRoutes(IReadOnlyList<HistoryEntry> entries)
        {
            var seen = new HashSet<string>(entries.Count, StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++) seen.Add(NameOf(entries[i].Route));
            return seen.Count;
        }

        /// <summary>The visible list: ONE pass, reversed so most-recent is first, then filter, then search.</summary>
        public static List<HistoryEntry> Visible(IReadOnlyList<HistoryEntry> entries, Filter filter, string search)
        {
            var visible = new List<HistoryEntry>(entries.Count);
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (!PassesFilter(e, filter)) continue;
                if (!PassesSearch(e, search)) continue;
                visible.Add(e);
            }
            return visible;
        }

        /// <summary>The visit-count map, over the FULL UNFILTERED log, keyed on the route key.</summary>
        public static Dictionary<string, int> VisitCounts(IReadOnlyList<HistoryEntry> entries)
        {
            var counts = new Dictionary<string, int>(entries.Count, StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                string k = NameOf(entries[i].Route);
                counts[k] = counts.TryGetValue(k, out int c) ? c + 1 : 1;
            }
            return counts;
        }

        /// <summary>The Most-visited fold: a STABLE sort by count descending over the already-newest-first visible
        /// list, then dedup keeping the FIRST — which is therefore the most recent visit within a count. "Stable" IS
        /// the whole rule and nothing pinned it in 0.2.9; <c>List.Sort</c> is NOT stable, so this sorts on
        /// (count desc, original index asc) explicitly rather than trusting the comparer.</summary>
        public static List<HistoryEntry> MostVisited(List<HistoryEntry> visible, Dictionary<string, int> counts)
        {
            int n = visible.Count;
            var order = new int[n];
            var keys = new long[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                int c = counts.TryGetValue(NameOf(visible[i].Route), out int v) ? v : 1;
                keys[i] = ((long)(int.MaxValue - c) << 32) | (uint)i;   // count desc, then original index asc
            }
            Array.Sort(keys, order);

            var seen = new HashSet<string>(n, StringComparer.Ordinal);
            var rows = new List<HistoryEntry>(n);
            for (int i = 0; i < n; i++)
            {
                var e = visible[order[i]];
                if (!seen.Add(NameOf(e.Route))) continue;
                rows.Add(e);
            }
            return rows;
        }

        /// <summary>The date-section label a run of entries sits under.</summary>
        public static string DateGroupLabel(DateTime dt, DateTime now)
        {
            int days = (now.Date - dt.Date).Days;
            return days switch
            {
                0 => Loc.Get(Strings.Nav.History.Group.Today),
                1 => Loc.Get(Strings.Nav.History.Group.Yesterday),
                < 7 => Loc.Get(Strings.Nav.History.Group.ThisWeek),
                < 30 => Loc.Get(Strings.Nav.History.Group.ThisMonth),
                _ => Loc.Get(Strings.Nav.History.Group.Earlier),
            };
        }

        /// <summary>The timestamp a row states.</summary>
        public static string FormatTimestamp(DateTime dt, DateTime now)
        {
            int days = (now.Date - dt.Date).Days;
            string t = dt.ToString("HH:mm", CultureInfo.CurrentCulture);
            return days switch
            {
                0 => t,
                1 => Strings.Nav.History.Ts.Yesterday(t),
                < 7 => Strings.Nav.History.Ts.Weekday(dt.ToString("dddd", CultureInfo.CurrentCulture), t),
                _ => Strings.Nav.History.Ts.Date(dt.ToString("MMM d yyyy", CultureInfo.CurrentCulture), t),
            };
        }

        /// <summary>The taskbar jump list's history half (ch 14). Walks BACKWARD because the log is oldest-first —
        /// forward is a jump list of the user's OLDEST six visits — and it composes the SAME route key the play-log
        /// half does, so the two share one <c>seen</c> key space and a surface that was both played and visited
        /// appears once.</summary>
        public static Playback.Os.JumpRow[] RecentSurfaces(IReadOnlyList<HistoryEntry> entries, int max)
        {
            if (max <= 0 || entries.Count == 0) return [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rows = new List<Playback.Os.JumpRow>(Math.Min(max, entries.Count));
            for (int i = entries.Count - 1; i >= 0 && rows.Count < max; i--)
            {
                var r = entries[i].Route;
                // Only a real DESTINATION earns a taskbar row: never `liked`, never a settings page, never not-found.
                if (r.Kind is not (RouteKind.Album or RouteKind.Playlist or RouteKind.Artist or RouteKind.Show or RouteKind.User)) continue;
                string key = NameOf(r);
                if (!seen.Add(key)) continue;
                var (title, _) = Dest(r);
                rows.Add(new Playback.Os.JumpRow(key, title, (byte)r.Subject.Kind));   // the ENTITY kind: `KindLabel`'s fallback reads it
            }
            return rows.ToArray();
        }
    }

    // ══ 12. THE FRAME'S RULES (stage 2 — every decision `Shell.UI.cs` would otherwise make inline) ═══════════════════
    //
    // A component body lays out and binds; it does not decide. Each rule below is one the 0.2.9 shell carried inline in
    // a 2,348-line component where nothing could pin it — and two of them (the drag-peek width, the auto-zoom control
    // loop, now `ZoomService`) are exactly the ones ch 18 §9 records as regressions-in-waiting.

    /// <summary>The frame's geometry, precedence and gating decisions. Pure.</summary>
    public static class FrameRules
    {
        /// <summary>Every seam grip in the shell: the engine's 16-DIP strip (<c>Splitter.StripW</c>), restated so this
        /// file stays free of a control's constant.</summary>
        public const float SeamStripW = 16f;

        /// <summary>The rail's breathing gap while it is INLINE (<c>Spacing.S</c>).</summary>
        public const float RailGapW = 8f;

        /// <summary>The sidebar column's bound width — THE W12 TRAP. A drag that starts on a COLLAPSED rail presents the
        /// pane expanded for the whole drag (drag peek); the column is <c>ClipToBounds</c>, so deriving its width from
        /// <paramref name="presented"/> alone clips a rail-wide strip of art and tree connectors with every label cut
        /// off. The peek term must stay in the same expression.</summary>
        public static float SidebarPaneWidth(bool dragPeek, float expanded, float presented)
            => dragPeek ? expanded : presented;

        /// <summary>The seam strip exists only where the pane is docked beside the content (the Wide band). A forced band
        /// (Narrow, Tiny) has no seam: the overlay pane owns the width.</summary>
        public static float SidebarSeamWidth(bool seamVisible) => seamVisible ? SeamStripW : 0f;

        /// <summary>The rail's 8-DIP gap exists ONLY while the rail is inline (open AND fits). A closed or floating rail
        /// leaves the page flush to the window edge.</summary>
        public static float RailGapWidth(bool open, bool fits) => open && fits ? RailGapW : 0f;

        /// <summary>The rail's row RESERVATION. It SNAPS between 0 and the rail width at commit; the content card's FLIP
        /// absorbs the shift while the rail panel slide-reveals into the band.</summary>
        public static float RailReservedWidth(bool open, bool fits, float railWidth) => open && fits ? railWidth : 0f;

        /// <summary>The floating rail's opaque backing shows only while the rail is open and does NOT fit (it overlays the
        /// page rather than resizing it).</summary>
        public static bool RailFloats(bool open, bool fits) => open && !fits;

        /// <summary>The rail seam grip: translated to the rail's left edge, on the content side.</summary>
        public static float RailSeamX(float viewportWidth, float railWidth) => viewportWidth - railWidth - SeamStripW;

        /// <summary>The rail seam grip only exists while the rail is open.</summary>
        public static float RailSeamWidth(bool open) => open ? SeamStripW : 0f;

        /// <summary>F243: the width a rail splitter drag COMMITS to <c>Ui.RailWidth</c> (and the settings) on release: the
        /// preview width through the one rail clamp, or null when there is nothing to commit (a non-finite preview, or the
        /// pointer ended where the rail already was). Nothing reaches the layout, the docked video or the settings until
        /// this answers a width.</summary>
        public static float? RailDragCommit(float dragWidth, float committedWidth)
        {
            if (!float.IsFinite(dragWidth)) return null;
            float w = ClampRailWidth(dragWidth);
            return MathF.Abs(w - committedWidth) < 0.01f ? null : w;
        }

        /// <summary>What a bare Escape reaching the shell column does, in precedence order.</summary>
        public enum EscapeAction : byte { None, CloseImmersiveLyrics, ExitVideoFullscreen }

        /// <summary>Escape's precedence at the shell. Everything that should beat the shell already has (an in-flight drag,
        /// the overlay host's pre-focus Escape, every deeper focused owner) — except the command palette, which is a
        /// SIBLING layer rather than an overlay entry, hence the explicit guard. Immersive lyrics closes before video
        /// fullscreen.</summary>
        public static EscapeAction Escape(bool handled, bool paletteOpen, bool immersiveLyrics, bool videoFullscreen)
        {
            if (handled || paletteOpen) return EscapeAction.None;
            if (immersiveLyrics) return EscapeAction.CloseImmersiveLyrics;
            return videoFullscreen ? EscapeAction.ExitVideoFullscreen : EscapeAction.None;
        }

        /// <summary>Bare Space toggles playback only after focused routing declined it and never while a text editor has
        /// focus (Space is not an accelerator — the dispatcher only matches Ctrl/Alt or F-keys).</summary>
        public static bool SpaceTogglesPlayback(bool handled, bool textEditorFocused) => !handled && !textEditorFocused;

        /// <summary>What F11 does. The fullscreen stage closes first if it is up; otherwise video fullscreen exits / enters
        /// as before; and with no video fullscreen and none to enter, F11 OPENS the stage (an empty stage is allowed — the
        /// rail's ⛶ does the same). <see cref="None"/> is the neutral zero value; <see cref="F11"/> never returns it.</summary>
        public enum FullscreenToggle : byte { None, Enter, Exit, CloseStage, OpenStage }

        /// <summary>F11, in priority order: the stage closes if it is up; else fullscreen video exits; else an active video
        /// goes fullscreen; else the fullscreen stage opens.</summary>
        public static FullscreenToggle F11(bool isFullscreen, bool videoActive, bool stageUp)
            => stageUp ? FullscreenToggle.CloseStage
             : isFullscreen ? FullscreenToggle.Exit
             : videoActive ? FullscreenToggle.Enter
             : FullscreenToggle.OpenStage;

        /// <summary>A search-mode flip while the user was IN the search must hand the caret to the new form: field → icon
        /// while the field had focus, or icon → field while the flyout was open (ch 18 W4).</summary>
        public static bool ReissueSearchFocus(MergedSearchMode old, MergedSearchMode next, bool fieldFocused, bool flyoutOpen)
            => old != next && (old == MergedSearchMode.Field ? fieldFocused : flyoutOpen);

        /// <summary>The back/forward history flyout shows at most this many rows (0.2.9 <c>HistoryMenuMax</c>).</summary>
        public const int HistoryMenuMax = 8;

        /// <summary>The history flyout's shape for a stack of <paramref name="stackCount"/> routes: how many rows, and
        /// whether "View all history" follows. An EMPTY stack opens nothing at all (no empty menu).</summary>
        public static (int Rows, bool HasMore) HistoryMenu(int stackCount)
            => (Math.Clamp(stackCount, 0, HistoryMenuMax), stackCount > HistoryMenuMax);

        /// <summary>Row <paramref name="row"/> of the flyout addresses this stack index — MOST RECENT FIRST.</summary>
        public static int HistoryMenuIndex(int stackCount, int row) => stackCount - 1 - row;

        /// <summary>The pin id of the destination a route shows, or null when the surface is deliberately non-pinnable. A
        /// pin id IS a route key, so this is the route key screened through the sidebar's one recogniser.</summary>
        public static string? PinIdFor(in Route route)
            => route.IsNone ? null : SidebarPinId.Canonical(NameOf(route));

        /// <summary>The content host's body for a route.</summary>
        public enum BodyKind : byte
        {
            /// <summary>A page is registered for the kind — render it.</summary>
            Page,
            /// <summary>A real destination whose page has not been registered (a Wave-5 page in a Wave-4 build) — an
            /// EMPTY body, never the not-found page: the destination exists, it just has nothing to draw yet.</summary>
            Empty,
            /// <summary>Nothing claims the route (a retired key, a stale tab, a developer route with developer mode off).</summary>
            NotFound,
        }

        public static BodyKind BodyFor(in Route route, bool hasPage, bool developerMode)
            => !IsKnown(route, developerMode) ? BodyKind.NotFound : hasPage ? BodyKind.Page : BodyKind.Empty;

        /// <summary>The route's body as the contract's kind: what the theme's <c>Shell.ContentState</c> view draws in the content
        /// region. The same decision as <see cref="BodyFor"/>, named for the contract (T4a).</summary>
        public static Theming.Contracts.ContentStateKind ContentBodyFor(in Route route, bool hasPage, bool developerMode)
            => BodyFor(route, hasPage, developerMode) switch
            {
                BodyKind.Page => Theming.Contracts.ContentStateKind.Page,
                BodyKind.Empty => Theming.Contracts.ContentStateKind.EmptyBody,
                _ => Theming.Contracts.ContentStateKind.NotFound,
            };

        /// <summary>The node key of a content state's root box, from the route's key (<see cref="NameOf"/>) and arg (<see cref="ArgOf"/>):
        /// <c>page:&lt;route&gt;</c>, <c>page-empty:&lt;route&gt;</c> and <c>page-notfound:&lt;route&gt;|&lt;arg&gt;</c>; the lane has none.
        /// These are the keys the frame mounted before the theme drew them, so a probe and a test find the same nodes.</summary>
        public static string ContentKey(Theming.Contracts.ContentStateKind kind, string routeName, string? routeArg) => kind switch
        {
            Theming.Contracts.ContentStateKind.Page => "page:" + routeName,
            Theming.Contracts.ContentStateKind.EmptyBody => "page-empty:" + routeName,
            Theming.Contracts.ContentStateKind.NotFound => "page-notfound:" + routeName + "|" + (routeArg ?? ""),
            _ => "",
        };

        /// <summary>The CLEARING half of <see cref="Ui.ActiveStagePlayable"/>: a navigation to a route no module watch page
        /// will mount for clears a stale claim — value-gated, so an idle navigation writes nothing. Module routes are the
        /// PAGES' to hand over (an unconditional clear could land after the incoming page's claim and erase it).</summary>
        public static bool ClearsStagePlayable(in Route route, string currentPlayable)
            => route.Kind != RouteKind.Module && currentPlayable.Length > 0;

        /// <summary>The trailing island's identity form, from the auth fold (ch 18 W17). Never raw session status.</summary>
        public enum ChipForm : byte { Profile, Connecting, Reconnect, SignIn }

        public static ChipForm ChipFor(AuthState auth) => auth switch
        {
            AuthState.Live => ChipForm.Profile,
            AuthState.Connecting => ChipForm.Connecting,
            AuthState.Offline => ChipForm.Reconnect,
            _ => ChipForm.SignIn,
        };

        /// <summary>The page-scale offline strip (ch 29 W9 C / W10): shown above the kept content while a stored credential
        /// exists but the session is down. AT MOST ONE LAYER SHOUTS — the chrome's accent Reconnect is the loud one, so
        /// the strip is caution-tinted with a standard action. A deliberate divergence: 0.2.9's banner had no call site
        /// (ch 29 parity 46).</summary>
        public static bool ShowsOfflineStrip(AuthState auth) => auth == AuthState.Offline;
    }

    // ══ 13. THE MASTHEAD PUBLICATIONS (ch 18 W13) ═══════════════════════════════════════════════════════════════════
    //
    // The ONE band ("Browse › Category") is mounted once, above the keep-alive boundary; a page never renders its own
    // copy. What a page CAN do is publish its live title and its "Show all" tool under its route. UI state, not entity
    // data — an LRU like the nav origins.

    /// <summary>What a masthead-family page publishes for itself. <see cref="ToolsAction"/> is BEHAVIOUR, not data: a
    /// re-publish that changes only the delegate updates it silently and never re-renders the band.</summary>
    public readonly record struct MastheadPublication(string? Title, bool ToolsVisible = false, bool ToolsLoading = false,
        Action? ToolsAction = null);

    public static class Mastheads
    {
        public const int Capacity = 16;

        /// <summary>Bumped only when a publication's DATA changes, so the band re-renders at navigation rate.</summary>
        public static readonly Signal<int> Version = new(0);

        static readonly Dictionary<string, MastheadPublication> s_map = new(StringComparer.Ordinal);
        static readonly List<string> s_lru = [];

        static string KeyOf(in Route r) => NameOf(r) + "" + (ArgOf(r) ?? "");

        /// <summary>Publish (or re-publish) a route's masthead. Returns whether the band must re-render.</summary>
        public static bool Publish(in Route route, in MastheadPublication publication)
        {
            string k = KeyOf(route);
            bool changed = !s_map.TryGetValue(k, out var old)
                || !string.Equals(old.Title, publication.Title, StringComparison.Ordinal)
                || old.ToolsVisible != publication.ToolsVisible
                || old.ToolsLoading != publication.ToolsLoading;
            s_lru.Remove(k);
            s_lru.Add(k);
            s_map[k] = publication;
            while (s_map.Count > Capacity && s_lru.Count > 0)
            {
                s_map.Remove(s_lru[0]);
                s_lru.RemoveAt(0);
            }
            if (changed) Version.Value = Version.Peek() + 1;
            return changed;
        }

        /// <summary>Subscribing read (the band).</summary>
        public static MastheadPublication? For(in Route route)
        {
            _ = Version.Value;
            return s_map.TryGetValue(KeyOf(route), out var p) ? p : null;
        }

        /// <summary>Non-subscribing read — the "Show all" click resolves the LATEST delegate here.</summary>
        public static MastheadPublication? Peek(in Route route) => s_map.TryGetValue(KeyOf(route), out var p) ? p : null;

        internal static void Clear()
        {
            s_map.Clear();
            s_lru.Clear();
        }
    }
}
