// ── Entities/Album.Page.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the album page and the whole prerelease: surface over the shared detail frame (ch 05; ch 03 items 55-56): the route and
// its kind-138 resolve, the frame composition (the identity with the upcoming instant and the pre-save target swap, the
// album table profile with the drawer seam, Shuffle · the hero ⋯ flyout · the cover drag), the whole-model demand, the
// "About this release" panel (+ Other versions), the trailing band (ONE reserved skeleton region, seven fail-soft
// sections in a fixed order) and InstallPages — the owner-M composition entry the orchestrator calls from App.cs
//
// Role: UI
// Owner: M
// Wave: 5
// Budget: 1500 lines
// Spec: ch 05 §9
//
// T9b (themes plan 6.1): the PICTURES of the page's slots are Classic's — the billed-artist line and flyout, the countdown, About this release, the trailing
// band (region, sections, stack, rows, music videos, skeletons) and the unresolvable line (`Themes\Classic\Structure\Views\Detail\ClassicAlbum*.cs`; the
// keys and their props `Theming\Contracts\ViewKeys.DetailAlbumPlaylist.cs`, `DetailAlbumProps.cs`). What stays here is the model's side of each: the demands,
// the page / trailing / sections / stack gates (the stamps), the one-swap latch and its deadline, which sections are present and the verbs a row runs
// (open, play, drag, menu, watch). Each host builds the plain data its view paints and mounts the key.
//
// ── WHAT THE FRAME ALREADY DRAWS (do not duplicate) ──────────────────────────────────────────────────────────────────
//
// Detail.Frame owns the cover, eyebrow, title, the Play pill, the heart (keyed `save:<SaveTarget>`), share, the rail and
// hero scaffolds, the tone plane, the table. This page hands it a FrameSpec VALUE and four SLOTS:
//   Attribution  → the face pile (Album.UI.cs)            rail row `rail:artists` / the hero attribution block
//   PreRelease   → the countdown card, keyed on its instant rail row `rail:prerelease` / the hero arm's trailing body
//   ReleasePanel → About this release + Other versions      rail row `rail:release`  / the hero arm's trailing body
//   Trailing     → the reserved trailing band               the table's trailing body (both arms)
// The frame invokes a slot only when its spec changes, and the table invokes the trailing thunk only when its args
// change, so every slot body is a component that reads the tables it paints.
//
// ── ROW 2 (P5 Z6a1, contract 1.26) ───────────────────────────────────────────────────────────────────────────────────
//
// A band-style structure (Zune) draws the page's title, its views and its actions in its band's row 2, so the page host publishes them through
// the `PageChromeSink` the content host provides around the page: the views "Songs" (current: the page itself) and "More by <artist>" (the lead
// artist's page) once the billing is known, the actions Play, Shuffle and Save / Saved (the word follows the saved set). The inputs are folded
// into a value the render computes; a keyed effect hands it to a signal, and an auto-tracked effect (it also reads the saved set) publishes the
// row, so a save elsewhere re-words the action without re-rendering the page. Classic draws its own head and reads none of it.
//
// ── DEMAND (the GateAlbumPage pattern, Detail.UI.cs §6) ──────────────────────────────────────────────────────────────
//
// The page asks for its WHOLE model from effects: the album's Detail groups + the tracklist edge once per (album slot,
// scope epoch); the member rows, the next tracklist page while Partial, the kind-138 link while upcoming and the billed
// artists as the membership lands. The trailing band asks its own relations (0.2.9's AlbumTrailing asked the Full rung
// the same way) and holds its shimmer on the tracklist plus — for at most PageRules.TrailingDeadlineMs — the sections
// directly under the rows (About the artist, Featured on, the watch-video verdict; all asked at Visible) — then swaps ONCE and latches.

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>The render-cost fix's one shared shape (Album.Page.cs's page render, its trailing band's pending gate and
/// demand, Artist.Page.cs's row demand): a table-wide generation ("did the table drain at all" — the ONLY thing a
/// <c>Signal&lt;uint&gt; Changed</c> read establishes; see <see cref="Table.Version"/> and
/// <see cref="EdgeTableBase.Version(int)"/>) paired with one row's — or one edge parent's — own version, plus the
/// slot/parent itself so two stamps for DIFFERENT rows never compare equal by coincidence (a component that is reused
/// across a slot change, e.g. an artist page surviving keep-alive across a different artist, must not alias the new
/// row against the old one's cached stamp).
/// <para>A stamp only lets the render/demand BODY early-out once the table has already woken this component — reading
/// the <c>Changed</c> signal itself still must happen in a tracked scope for that wake (the engine's <c>Signal&lt;T&gt;</c>
/// has no pull cascade — only <c>Memo&lt;T&gt;</c> does, per <c>Signal.NotifySubscribers</c>'s own comment — so nothing
/// here removes the wake, only the redundant recompute after it).</para></summary>
/// <para><paramref name="Source"/> disambiguates a gate whose stamp can be built from DIFFERENT TABLES on different
/// calls — the fans row, which reads the lead artist's related edge on one render and a seed track's on the next. A
/// slot is a per-table index, so artist slot 5 and track slot 5 both exist; without a source tag a flip between the
/// two could produce an identical <c>(Slot, Generation, Version)</c> triple, read as "not moved", and silently skip
/// the fetch. Sites with one fixed table leave it at 0.</para>
public readonly record struct RowStamp(int Slot, uint Generation, uint Version, byte Source = 0)
{
    /// <summary>True when the slot, the source table, the table's generation or the row's own version differs from
    /// <paramref name="previous"/> — the one comparison every render-cost gate below shares instead of hand-rolling
    /// its own field-by-field equality.</summary>
    public bool Moved(in RowStamp previous)
        => Slot != previous.Slot || Source != previous.Source
        || Generation != previous.Generation || Version != previous.Version;
}

/// <summary>The stamp for a ROW SET (W2-A2, the value gates): one 64-bit FNV-1a fold over the (slot, version) pairs of
/// every row a host paints, in order — the members of a tracklist, the billed artists, the rows a trailing stack
/// shows. A host's <c>UseComputed</c> value carries the fold instead of the rows themselves, so the record stays a
/// handful of words whatever the list's length and the memo's equality cut-off (<c>Memo&lt;T&gt;</c>'s push-pull
/// compare) decides the re-render with no allocation. Order-sensitive on purpose: a reordered list paints
/// differently. A row that is <see cref="Table.None"/> or past the table folds as version 0, so a placeholder slot is
/// still part of the value (its ARRIVAL moves the fold) without an out-of-range read. The collision odds of a 64-bit
/// fold are the accepted price — a missed re-render needs two distinct row sets to hash identically in the same
/// component's lifetime.</summary>
public static class RowFold
{
    /// <summary>FNV-1a's 64-bit offset basis: the fold of an empty row set.</summary>
    public const ulong Seed = 0xCBF29CE484222325UL;
    const ulong Prime = 0x100000001B3UL;

    /// <summary>One 32-bit word into the fold.</summary>
    public static ulong Add(ulong fold, uint word) => (fold ^ word) * Prime;
    /// <inheritdoc cref="Add(ulong, uint)"/>
    public static ulong Add(ulong fold, int word) => Add(fold, (uint)word);

    /// <summary>A row's own version, 0 for <see cref="Table.None"/> or a slot the table has not allocated.</summary>
    public static uint Version(Table table, int slot)
        => slot > Table.None && slot < table.Count ? table.Version[slot] : 0u;

    /// <summary>One row — its slot and its version — into the fold.</summary>
    public static ulong Row(ulong fold, Table table, int slot) => Add(Add(fold, slot), Version(table, slot));

    /// <summary>Every row of <paramref name="slots"/>, in order, from <see cref="Seed"/>.</summary>
    public static ulong Rows(Table table, ReadOnlySpan<int> slots)
    {
        ulong fold = Seed;
        for (int i = 0; i < slots.Length; i++) fold = Row(fold, table, slots[i]);
        return fold;
    }
}

public readonly partial struct Album
{
    // ══ 1. INSTALL AND THE ROUTES ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Owner M's ONE install: the album, prerelease and show pages, the pre-save resolve seam and the
    /// View-credits seam. Called once by the composition root after <c>Track.InstallActions()</c>; replaces the Wave 4.5
    /// gate (<c>Detail.GateAlbum</c>) on <see cref="Shell.RouteKind.Album"/>.</summary>
    public static void InstallPages()
    {
        Shell.SetPage(Shell.RouteKind.Album, Page);
        Shell.SetPage(Shell.RouteKind.Prerelease, PreReleasePage);
        Shell.SetPage(Shell.RouteKind.Show, Show.Page);
        // Podcast rework wave P2 (owner S): a PLACEHOLDER only — wave P5 (owner Q) replaces this with the real
        // Episode.Page/Reader. Registered here because Episode joins the shared-detail-surface family this group
        // already installs (gate P2: the route resolves to a named surface, never a play or a not-found).
        Shell.SetPage(Shell.RouteKind.Episode, Episode.Page);
        Controls.PreSave = Spotify.Api.ResolvePreRelease;
        Track.MenuSeams.ViewCredits = static t => Track.OpenCredits(t);
    }

    /// <summary><see cref="Shell.RouteKind.Album"/>. A <c>spotify:prerelease:</c> subject resolves exactly as the
    /// prerelease route does.</summary>
    // MOUNT POINT (stage B contract)
    public static Element Page(in Shell.Route route) => PageFor(in route);

    /// <summary><see cref="Shell.RouteKind.Prerelease"/>: the kind-138 pairing resolves to the ordinary album page; an
    /// unresolvable one paints the shell with an empty title and "Nothing here yet" — never an error page (W18).</summary>
    // MOUNT POINT (stage B contract)
    public static Element PreReleasePage(in Shell.Route route) => PageFor(in route);

    static Element PageFor(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new PageProps(route.Subject, routeKey), static () => new PageHost())
            with { Key = "album-page:" + routeKey };
    }

    sealed record PageProps(EntityUri Subject, string RouteKey);

    sealed class PageHost : Component
    {
        Scope? _scope;
        EntityUri _subject;
        Album _row, _album, _display;
        uint _resolvedAt = uint.MaxValue;
        int _upcomingAt;
        IOverlayService? _overlay;

        // value caches, so an equal re-render costs the frame and the table nothing
        Track.TableSource? _source;
        int _sourceSlot = -1;
        Track.TableProfile? _profile;
        Detail.Config _profileFor;
        // The frame's identity VALUE, rebuilt only when the inputs it reads moved (IdentityInputs, below):
        // Detail.Identity.For walks the tracklist and allocates the face list, and the frame's own _spec.SetIfChanged
        // already short-circuits an equal spec — so an unmoved identity must cost nothing above the compare.
        Detail.Identity? _identity;
        IdentityInputs _identityFor;

        readonly Detail.FrameActions _actions;
        readonly Detail.FrameSlots _slots, _slotsUnresolved;
        readonly Func<ColorF> _accent;
        readonly Action _demandAlbum, _demandTracked;
        readonly Func<PageStamp> _stamp;

        // ── row 2 (P5 Z6a1): what the band draws for this page (see the file header) ──
        PageChromeSink _sink = PageChromeSink.None;
        ChromeInputs _chromeNext;
        readonly Signal<ChromeInputs> _chromeIn = new(default);
        readonly Action _syncChrome, _publishChrome, _playChrome, _noView;

        /// <summary>What row 2 says: the album, its title once known, the heart's target and the lead artist (the "More by" view).</summary>
        readonly record struct ChromeInputs(EntityUri Subject, EntityUri SaveTarget, string? Title, EntityUri Artist, string? ArtistName);

        /// <summary>Exactly what <see cref="Detail.Identity.For"/> and the page's own FrameSpec fields read off the
        /// tables: the album row (title, cover, kind, year, count, the prerelease columns), the tracklist edge and every
        /// member's version (durations, availability, the notice rules), the billing edge and every billed artist's
        /// version (the faces), and the two clock verdicts (the countdown instant, the heart's target).</summary>
        readonly record struct IdentityInputs(int Slot, uint Album, EdgeState Tracks, uint TracksEdge, ulong Members,
                                              uint ArtistsEdge, ulong Billed, int UpcomingAt, EntityId SaveTarget);

        /// <summary>The page render's gate value (W2-A2). The five table counters are read INSIDE the memo that
        /// computes this, so a publication that leaves it equal never re-renders the page; <see cref="Readiness"/> is
        /// the W18 arm's input, and <see cref="Resolving"/> carries the album table's generation only while a
        /// <c>prerelease:</c> subject's kind-138 pairing is still unresolved — the one case where "the table drained"
        /// is itself the news (Render re-looks the pairing up).</summary>
        readonly record struct PageStamp(uint Epoch, IdentityInputs Identity, EdgeState Readiness, uint Resolving);

        public PageHost()
        {
            _accent = AccentNow;
            _demandAlbum = DemandAlbum;
            _demandTracked = DemandTracked;
            _stamp = Stamp;
            _syncChrome = () => _chromeIn.SetIfChanged(_chromeNext);
            _publishChrome = PublishChrome;
            _playChrome = () => { var a = _display; if (a.IsValid) DetailVerbs.PlayContext(a.Uri)(); };
            _noView = static () => { };
            _actions = new Detail.FrameActions { Shuffle = ShuffleAlbum, More = MoreMenu, CoverDrag = CoverPayload };
            Func<float, Element> attribution = w => FacePile(_display, w);
            Func<Element> preRelease = () => Countdown(_display.Uri, _upcomingAt, _accent);
            Func<bool, Element> release = outer => ReleasePanel(_display, outer);
            Func<Element> trailing = () => Trailing(_display);
            // The Huge aside (C-Album-Huge, contract 1.21): the release facts, the artist, Fans also like and "More by" leave the rail and the band for it.
            Func<Element> aside = () => Aside(_display);
            _slots = new Detail.FrameSlots { Attribution = attribution, PreRelease = preRelease, ReleasePanel = release, Trailing = trailing, Aside = aside };
            // Without a panel the rail must not hold an empty `rail:release` row — it would still take a 14-DIP gap.
            _slotsUnresolved = new Detail.FrameSlots { Episodes = static _ => ViewSlots.View(ViewKeys.AlbumUnresolved, AlbumUnresolvedProps.Instance) };   // Func<bool, Element> (Detail.UI.cs patch P-B1)
        }

        public override Element Render()
        {
            var p = UseProps<PageProps>();
            uint scopeEpoch = Entities.ScopeEpoch.Value;           // FIRST: a scope switch re-points every table below
            var scope = Entities.Current;
            var overlay = UseContext(Overlay.Service);
            _overlay = overlay;
            Track.DrawerOverlay = overlay;                          // the drawer's credits sheet (idempotent)

            if (!ReferenceEquals(_scope, scope) || !_subject.Equals(p.Subject))
            {
                _scope = scope;
                _subject = p.Subject;
                // The factory allocates an empty row for an unseen uri: the page binds it at once and Ensure fills it.
                _row = p.Subject.IsValid ? Entities.Album(p.Subject) : default;
                _album = default;
                _resolvedAt = uint.MaxValue;
                _source = null;
                _identity = null;
            }
            // The kind-138 pairing is re-looked-up only when the album table published and nothing resolved yet — a
            // resolved pairing never un-resolves (the cold scan is Upcoming.ResolvePreRelease's). The generation is
            // PEEKED: the wake for it is PageStamp.Resolving inside the gate below, which carries the generation only
            // while unresolved — after that an album publication no longer re-renders the page by itself.
            uint albumsPublished = scope.Albums.Changed.Peek();
            if (!_album.IsValid && _row.IsValid && _resolvedAt != albumsPublished)
            {
                _resolvedAt = albumsPublished;
                _album = IsPreReleaseSubject(p.Subject) ? Upcoming.ResolvePreRelease(p.Subject) : _row;
            }
            var a = _album.IsValid ? _album : _row;
            _display = a;

            // THE gate (W2-A2): the table counters are subscribed inside this memo, and the memo's equality cut-off
            // means an equal stamp resolves this render CLEAN without running the body below — in real mode the tables
            // publish nearly every frame while covers stream in, and only a stamp that moved rebuilds the frame spec.
            var stamp = UseComputed(_stamp).Value;

            UseEffect(_demandAlbum, DepKey.From(a.Slot, (int)scopeEpoch));
            UseEffect(_demandTracked);

            // Row 2 (P5 Z6a1): the inputs this render knows, handed to the publishing effect. Hooks before any early return.
            _sink = UseContext(PageChromeSink.Context);
            _chromeNext = ChromeOf(a, in stamp);
            UseEffect(_syncChrome, DepKey.From(_chromeNext.GetHashCode()));
            UseEffect(_publishChrome);

            if (!a.IsValid) return Controls.Vacancy(Controls.VacancyVoice.Error);

            // W18: an unresolvable prerelease route — the tracklist ask for the prerelease row failed and nothing named it.
            if (PageRules.IsUnresolvedPreRelease(_album.IsValid, stamp.Readiness, a.Knows(AlbumFields.Title)))
                return Detail.Frame(new Detail.FrameSpec
                {
                    Identity = new Detail.Identity { Subject = a.Uri, Kind = DetailKind.Album },
                    Config = Detail.Config.Album with { Content = DetailContent.Episodes, HasTrailing = false },
                    Slots = _slotsUnresolved,
                    RouteKey = p.RouteKey,
                });

            var cfg = Detail.Config.For(DetailKind.Album, a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album);
            var inputs = stamp.Identity;
            _upcomingAt = inputs.UpcomingAt;
            var identity = _identity;
            if (identity is null || inputs != _identityFor)
            {
                _identityFor = inputs;
                identity = Detail.Identity.For(a) with
                {
                    UpcomingAtUnixSeconds = _upcomingAt,
                    SaveTarget = new EntityUri(inputs.SaveTarget),
                };
                _identity = identity;
            }

            if (_source is null || _sourceSlot != a.Slot)
            {
                _source = Track.TableSource.ForAlbum(a);
                _sourceSlot = a.Slot;
            }
            if (_profile is null || _profileFor != cfg)
            {
                _profileFor = cfg;
                _profile = Track.TableProfile.From(cfg) with { Drawer = Track.DrawerSeam };
            }

            return Detail.Frame(new Detail.FrameSpec
            {
                Identity = identity,
                Config = cfg,
                Source = _source,
                Profile = _profile,
                Actions = _actions,
                // Keep the release slot in the frame from the first paint. ReleasePanelHost returns a zero-size
                // element until its facts exist, then fills the same keyed row; changing FrameSlots here used to
                // insert a new rail child several seconds into navigation and made the album jump/flicker.
                Slots = _slots,
                RouteKey = p.RouteKey,
            });
        }

        /// <summary>Row 2's inputs for <paramref name="a"/> (default while there is no album).</summary>
        static ChromeInputs ChromeOf(Album a, in PageStamp stamp)
        {
            if (!a.IsValid) return default;
            var billed = a.ArtistSlots;
            var lead = billed.Length > 0 ? new Artist(billed[0]) : default;
            bool named = lead.IsValid && lead.Knows(ArtistFields.Name);
            var save = stamp.Identity.SaveTarget.IsValid ? new EntityUri(stamp.Identity.SaveTarget) : a.Uri;
            return new ChromeInputs(a.Uri, save, a.Knows(AlbumFields.Title) ? a.Title : null,
                                    named ? lead.Uri : default, named ? lead.Name : null);
        }

        /// <summary>Publishes row 2 (an auto-tracked effect: it reads the inputs signal and the saved set, so either moving re-publishes).</summary>
        void PublishChrome()
        {
            var c = _chromeIn.Value;
            if (!c.Subject.IsValid) return;
            var views = new List<PageView>(2) { new("songs", Loc.Get(Strings.Search.Songs), true, _noView) };
            if (c.ArtistName is { } name)
            {
                var artist = c.Artist;
                views.Add(new PageView("moreby", Strings.Detail.MoreBy(name), false, () => Shell.GoTo(Shell.For(artist, name))));
            }
            var actions = new List<PageAction>(3)
            {
                new("play", Loc.Get(Strings.Detail.Play), _playChrome, PageActionKind.Play),
                new("shuffle", Loc.Get(Strings.Detail.Shuffle), ShuffleAlbum, PageActionKind.Shuffle),
            };
            if (Controls.Library is { } lib)
            {
                string target = c.SaveTarget.Text, title = c.Title ?? "";
                bool saved = lib.IsSaved(target);
                actions.Add(new PageAction("save", Loc.Get(saved ? Strings.Menu.Saved : Strings.Menu.Save), () => lib.ToggleSaved(target, title),
                    Reserve: Loc.Get(saved ? Strings.Menu.Save : Strings.Menu.Saved)));
            }
            _sink.Publish(new PageChromeRow(views, actions, new PageBackdrop(Bleed: null, TallWash: true), c.Title));
        }

        static bool IsPreReleaseSubject(EntityUri subject)
            => subject.Id.IsPrerelease || EntityUri.IsPrerelease(subject.Text.AsSpan());

        /// <summary>The gate's compute (the <c>UseComputed</c> body): reads the five counters this page depends on —
        /// tracked, so a drain of any of them wakes the memo — and folds what the render reads into a
        /// <see cref="PageStamp"/>. <c>_display</c>, <c>_row</c> and <c>_album</c> are Render's fields, written before the
        /// memo is read; a value computed against the previous slot differs in <see cref="IdentityInputs.Slot"/> on the
        /// next publication, so a pairing that just resolved re-renders once more and then gates on the new row.
        /// Runs a member-count loop and one ISO-date parse (<see cref="Upcoming.Of"/>) per publication; allocates
        /// nothing.</summary>
        PageStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            uint albums = scope.Albums.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = e.AlbumTracks.Changed.Value;
            _ = e.AlbumArtists.Changed.Value;
            uint resolving = _album.IsValid || !_row.IsValid ? 0u : albums;
            var a = _display;
            if (!a.IsValid) return new PageStamp(epoch, default, EdgeState.Unknown, resolving);

            long now = Store.ToUnix(Entities.Now);
            int slot = a.Slot;
            var identity = new IdentityInputs(slot, a.Version, e.AlbumTracks.State(slot), e.AlbumTracks.Version(slot),
                                              RowFold.Rows(scope.Tracks, a.TrackSlots),
                                              e.AlbumArtists.Version(slot), RowFold.Rows(scope.Artists, a.ArtistSlots),
                                              Upcoming.Of(a, now), Upcoming.SaveTarget(a, now).Id);
            return new PageStamp(epoch, identity, e.AlbumTracks.Readiness(slot), resolving);
        }

        /// <summary>The page accent for the slot bodies the frame does not hand one (the countdown's ring and eyebrow):
        /// the cover's chrome grading, else the system accent — read in the consumer's render, so a late grading
        /// re-tints it. Delegates the ladder itself to <see cref="Detail.AccentFor"/> (the single ladder every accent
        /// site now shares), with the row's own <see cref="Album.Accent"/> as the payload rung. The
        /// <see cref="Palette.Watch"/> read is a per-IMAGE signal (bumped when THIS cover grades, never per batch), and
        /// it lands wherever this thunk runs: the countdown card binds it into the eyebrow's <c>Color</c> channel
        /// (<c>Prop.Of(p.Accent)</c>, a mount-time effect that writes one scene column — no render) and peeks it,
        /// untracked, for the ring's scalar foreground (W2-A2). Never called from this page's own render.</summary>
        ColorF AccentNow()
        {
            string? url = Controls.ArtUrl(_display.ImageId);
            if (url is { Length: > 0 }) _ = Palette.Watch(url).Value;
            return Detail.AccentFor(url, _display.Accent);
        }

        void DemandAlbum()
        {
            var a = _display;
            if (!a.IsValid) return;
            Entities.Ensure(a, AlbumFields.Detail);
            // Only an unanswered list is asked: a complete one needs nothing, and an ask that fails must not re-arm itself.
            if (Entities.Current.Edges.AlbumTracks.State(a.Slot) == EdgeState.Unknown)
                Entities.EnsureEdge(FetchEdge.AlbumTracks, a.Slot);
        }

        void DemandTracked()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Albums.Changed.Value;
            _ = scope.Edges.AlbumTracks.Changed.Value;
            _ = scope.Edges.AlbumArtists.Changed.Value;
            var a = _display;
            if (!a.IsValid) return;
            var edge = scope.Edges.AlbumTracks;
            var slots = a.TrackSlots;
            if (slots.Length > 0) Entities.Ensure(MemoryMarshal.Cast<int, Track>(slots), TrackFields.Row | TrackFields.Video);
            if (edge.State(a.Slot) == EdgeState.Partial) Entities.EnsureEdge(FetchEdge.AlbumTracks, a.Slot, edge.Count(a.Slot));
            if (Upcoming.NeedsLink(a, Store.ToUnix(Entities.Now))) Entities.Ensure(a, AlbumFields.PreReleaseLink);
            var billed = a.ArtistSlots;
            if (billed.Length > 0) Entities.Ensure(scope.Artists, billed, (uint)ArtistFields.Identity);
        }

        /// <summary>The hero's Shuffle satellite: shuffle on, then this album's OUT rows as the context (the table's own
        /// command-bar shuffle is the same shape over its visible order — G-264).</summary>
        void ShuffleAlbum()
        {
            var a = _display;
            if (!a.IsValid) return;
            var slots = a.TrackSlots;
            long now = Store.ToUnix(Entities.Now);
            var rows = new EntityRef[slots.Length];
            int n = 0;
            for (int i = 0; i < slots.Length; i++)
                if (!new Track(slots[i]).NotYetOut(now)) rows[n++] = new EntityRef(EntityKind.Track, slots[i]);
            Playback.SetShuffle(true);
            if (n > 0) Playback.PlayRows(rows.AsSpan(0, n), 0, a.Id);
            else Actions.Services.Play?.Invoke(a.Uri);
        }

        /// <summary>The hero ⋯ (W20): the one container menu, on-page — see <see cref="Album.HeroMenu"/>.</summary>
        ContextMenuModel? MoreMenu() => HeroMenu(_display, _overlay);

        /// <summary>The cover drags the whole album: the resident rows when they are in hand, else the library's resolver.</summary>
        object? CoverPayload()
        {
            var a = _display;
            if (!a.IsValid) return null;
            string uri = a.Uri.Text;
            var slots = a.TrackSlots;
            Track[]? tracks = null;
            if (slots.Length > 0)
            {
                tracks = new Track[slots.Length];
                for (int i = 0; i < tracks.Length; i++) tracks[i] = new Track(slots[i]);
            }
            Func<CancellationToken, Task<Track[]>>? resolver = null;
            if (tracks is null && Sidebar.LibraryWrites?.ResolveTracks is { } resolve) resolver = ct => resolve(uri, ct);
            return new DragPayload(DragKind.Album, uri, uri, a.Title, new EntityRef(EntityKind.Album, a.Slot),
                                   Tracks: tracks, TrackResolver: resolver, ArtUrl: Controls.ArtUrl(a.ImageId));
        }
    }

    /// <summary>The album's hero ⋯ — the page's and the library pane's ONE menu, built at OPEN from the live model:
    /// <c>Menus.Container</c> on the page (Play next · Add to queue · Add to playlist ▸ · Pin · Share). When the tracks are
    /// resident, Add to playlist is the track menu's own deposit submenu over them (no resolve round-trip); otherwise the
    /// registered container verb resolves them. Null when nothing is offerable.</summary>
    internal static ContextMenuModel? HeroMenu(Album a, IOverlayService? overlay)
    {
        if (!a.IsValid) return null;
        MenuFlyoutItem? deposit = null;
        var slots = a.TrackSlots;
        if (slots.Length > 0)
        {
            var tracks = new Track[slots.Length];
            for (int i = 0; i < tracks.Length; i++) tracks[i] = new Track(slots[i]);
            if (Track.Menu(tracks, new Track.MenuOptions(ShowGoToAlbum: false, PickerOverlay: overlay)) is { } model)
            {
                string add = Loc.Get(Strings.Detail.AddToPlaylist);
                for (int i = 0; i < model.Rows.Count; i++)
                    if (string.Equals(model.Rows[i].Label, add, StringComparison.Ordinal)) { deposit = model.Rows[i]; break; }
            }
        }
        var artists = a.ArtistSlots;
        var artist = artists.Length > 0 ? new Artist(artists[0]) : default;
        string subtitle = PageRules.Subtitle(artist.IsValid && artist.Knows(ArtistFields.Name) ? artist.Name : null, a.Year, a.Kind);
        var target = ActionTarget.ForAlbum(a.Uri, a.Title);
        return Menus.Container(in target, Controls.ArtUrl(a.ImageId), subtitle,
            new ContainerExtras { OnPage = true, Deposit = deposit });
    }

    // ══ 2. ABOUT THIS RELEASE (ch 05 W11, W15; ch 03 item 56) ════════════════════════════════════════════════════════

    static Element ReleasePanel(Album a, bool outerPadding)
        => Embed.Comp(new ReleaseProps(a.Slot, outerPadding), static () => new ReleasePanelHost());

    sealed record ReleaseProps(int AlbumSlot, bool OuterPadding);

    /// <summary>ONE shape from its first paint (the `Album.ReleasePanel` view): an "About this release" title, a label/value facts list
    /// (Released, Label), then the ℗/© lines as notes and the Other versions picker. Song count and length are not painted — the hero meta
    /// line already states both. The whole record is gated on the publishing group, so the panel appears once, complete.</summary>
    [CoreHost]
    sealed class ReleasePanelHost : Component
    {
        int _slot;
        readonly Func<ReleaseStamp> _stamp;

        public ReleasePanelHost() => _stamp = Stamp;

        /// <summary>The facts this panel prints, as the rows they are read from (W2-A2): the album row (label, ℗/©,
        /// the release date and its precision), the tracklist edge and every member's version (the facts record still
        /// counts what is out) and the versions edge with every edition's own
        /// version (the Other-versions labels: title · year · kind). "Out" is a clock verdict and is re-read when any of
        /// these move, not on its own — a row crossing its release instant with nothing else publishing keeps its old
        /// count until the next publication, exactly as the countdown card owns the visible instant.</summary>
        readonly record struct ReleaseStamp(uint Epoch, int Slot, uint Album, uint TracksEdge, ulong Members,
                                            uint VersionsEdge, ulong Versions);

        ReleaseStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Albums.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            _ = e.AlbumTracks.Changed.Value;
            _ = e.AlbumVersions.Changed.Value;
            int slot = _slot;
            var a = new Album(slot);
            if (!a.IsValid) return new ReleaseStamp(epoch, slot, 0, 0, 0, 0, 0);
            return new ReleaseStamp(epoch, slot, a.Version, e.AlbumTracks.Version(slot), RowFold.Rows(scope.Tracks, a.TrackSlots),
                                    e.AlbumVersions.Version(slot), RowFold.Rows(scope.Albums, a.VersionSlots));
        }

        public override Element Render()
        {
            var p = UseProps<ReleaseProps>();
            _slot = p.AlbumSlot;
            // The gate (W2-A2): the four counters are subscribed inside the memo; this body — the facts record, the notes, the versions — runs only when
            // the stamp moved.
            _ = UseComputed(_stamp).Value;
            var a = new Album(p.AlbumSlot);
            if (!a.IsValid) return Mount(p.OuterPadding, null, "", null, [], []);

            long now = Store.ToUnix(Entities.Now);
            var facts = a.Knows(AlbumFields.Publishing | AlbumFields.Release) ? ReleaseFactsRules.Of(a, now) : ReleaseFacts.Empty;
            var versions = a.VersionSlots;
            // Songs and length never open the panel: the hero already prints both. A date, a label, the courtesy lines, or another edition do (the view
            // draws nothing otherwise).
            string? released = ReleaseFactsRules.ReleasedText(facts);
            var choices = new AlbumVersionChoice[versions.Length];
            for (int i = 0; i < choices.Length; i++)
            {
                var v = new Album(versions[i]);
                choices[i] = new AlbumVersionChoice(PageRules.VersionLabel(v.Knows(AlbumFields.Title) ? v.Title : "", v.Year, v.Kind),
                                                    () => Track.GoToAlbum(v));
            }
            return Mount(p.OuterPadding, released, released is not null ? ReleaseFactsRules.ReleasedCaption(facts) : "", facts.Label, facts.Notes, choices);
        }

        static Element Mount(bool outerPadding, string? released, string releasedCaption, string? label, IReadOnlyList<string> notes,
                             IReadOnlyList<AlbumVersionChoice> versions)
            => ViewSlots.View(ViewKeys.AlbumReleasePanel, new AlbumReleasePanelProps(outerPadding, released, releasedCaption, label, notes, versions));
    }

    // ══ 3. THE TRAILING BAND (ch 05 §0.10-§0.11, W7, W12, W18; ch 03 item 55) ════════════════════════════════════════

    /// <summary>The band under the rows: reserved from mount, keyed per album so its one-swap latch is per album.</summary>
    static Element Trailing(Album a)
        => Embed.Comp(new TrailingProps(a.Slot), static () => new TrailingHost())
            with { Key = "album-trailing:" + a.Slot.ToString(CultureInfo.InvariantCulture) };

    /// <summary>The band's (or, <paramref name="Aside"/>, the Huge aside's) album.</summary>
    sealed record TrailingProps(int AlbumSlot, bool Aside = false);

    /// <summary>The Huge aside's body (C-Album-Huge / -Huge4K): About this release, the artist, Fans also like and "More by", from the band's own host and
    /// gates (<see cref="SectionsHost"/> in its aside arm), keyed per album. Mounted by the frame only while the aside is docked; the rail and the band
    /// drop what it holds then (<see cref="Detail.AsideDocked"/>).</summary>
    static Element Aside(Album a)
        => Embed.Comp(new TrailingProps(a.Slot, Aside: true), static () => new SectionsHost())
            with { Key = "album-aside:" + a.Slot.ToString(CultureInfo.InvariantCulture) };

    /// <summary>ONE skeleton region (the `Album.Trailing` view), shimmering from the first frame and revealing once into whatever it resolves
    /// to — the sections, or a collapsed empty box. The shimmer is shaped by what the row already knows
    /// (<see cref="PageRules.SkeletonShape"/>) and the region is keyed on that shape, so a single never reserves an
    /// album's rows block and the reveal does not collapse the page under the cursor.</summary>
    [CoreHost]
    sealed class TrailingHost : Component
    {
        int _slot;
        bool _swapped;
        // The shimmer's shape, decided at mount and never re-keyed.
        PageRules.TrailingShape _shape;
        string _skelKey = "";
        readonly Signal<bool> _demanded = new(false);
        // The one deadline per album (PageRules.TrailingDeadlineMs): armed by UseTimeout once the page has demanded,
        // flipped by the preallocated _deadlineHit. It is a Signal so the pending thunk's tracked read wakes the region.
        readonly Signal<bool> _deadline = new(false);
        readonly Func<bool> _pending;
        readonly Func<Element> _content;
        readonly Action _demand;
        readonly Action _deadlineHit;

        // The pending gate's cached stamp + inputs (ch 08 render-cost fix — see RowStamp in Album.Page.cs).
        RowStamp _pendingTracks;
        bool _pendingDemanded, _pendingAbout, _pendingFeatured, _pendingFans, _pendingVideo, _pendingDeadline, _pendingCached;

        // The demand block's cached stamps, one per Prefetch relation whose EnsureRows call actually scans a growing
        // target list (the Ask() calls below stay unconditional — each is already an O(1) no-op once its edge leaves
        // Unknown, so gating them saves nothing; see RowStamp).
        RowStamp _moreByStamp, _similarStamp, _versionsStamp, _recsStamp, _fansStamp;

        public TrailingHost()
        {
            _pending = PendingNow;
            _content = () => Embed.Comp(new TrailingProps(_slot), static () => new SectionsHost());
            _demand = Demand;
            _deadlineHit = DeadlineHit;
        }

        public override Element Render()
        {
            var p = UseProps<TrailingProps>();
            _slot = p.AlbumSlot;
            // The Huge aside holds About and Fans also like while it is docked (contract 1.21): the band's reserve does not shimmer for them.
            bool asideDocked = UseContext(Detail.AsideDocked);
            UseEffect(_demand);
            // The deadline is keyed on (album, demanded): UseTimeout arms from mount and RE-ARMS when its deps change,
            // so the fire that counts is the one 400 ms after the page demanded — DeadlineHit ignores the pre-demand
            // fire. Reading the signal here (tracked) is what re-renders this host once, when the demand lands.
            bool demanded = _demanded.Value;
            UseTimeout(_deadlineHit, PageRules.TrailingDeadlineMs, DepKey.From(_slot, demanded ? 1 : 0));
            // The shape is decided ONCE, from what the row knows at mount. A later kind or billing answer must not
            // re-key the region: a remount restarts the shimmer's reveal and the band reads as blank for those frames.
            // SmoothResize eases whatever the guess got wrong.
            if (_skelKey.Length == 0)
            {
                var a = new Album(_slot);
                // A video is reserved only when the rows already say so: every member's verdict is in AND one has a video.
                int videoCount = 0;
                if (a.IsValid)
                {
                    var known = MemoryMarshal.Cast<int, Track>(a.TrackSlots);
                    Span<PageRules.AlbumVideo> probe = stackalloc PageRules.AlbumVideo[PageRules.VideoCap];
                    videoCount = PageRules.VideoDecided(known) ? PageRules.SelectVideos(known, probe) : 0;
                }
                _shape = a.IsValid
                    ? PageRules.SkeletonShape(videoKnown: videoCount > 0, videoShelf: PageRules.ArmFor(videoCount) == PageRules.VideoArm.Shelf)
                    : PageRules.SkeletonShape();
                if (asideDocked) _shape = _shape with { About = false, Fans = false };
                int hash = (_shape.About ? 1 : 0) | (_shape.Fans ? 2 : 0) | (_shape.Video ? 4 : 0) | (_shape.VideoShelf ? 8 : 0)
                           | (_shape.RowBlocks << 4);
                _skelKey = "trailing-skel:" + hash.ToString(CultureInfo.InvariantCulture);
            }
            // The `Album.Trailing` view: ONE skeleton region over these thunks (its shimmer is the section skeletons `_shape` names).
            return ViewSlots.View(ViewKeys.AlbumTrailing, new AlbumTrailingProps(_skelKey, _shape, _pending, _content));
        }

        bool PendingNow()
        {
            if (_swapped) return false;
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            uint tracksPublished = e.AlbumTracks.Changed.Value;    // read unconditionally: these ARE the wake
            _ = scope.Tracks.Changed.Value;                        // a member's video group landing
            _ = scope.Artists.Changed.Value;                       // the lead artist's name landing
            _ = e.AlbumArtists.Changed.Value;                      // the billing landing (who the lead artist is)
            _ = e.AlbumRecommendations.Changed.Value;              // Featured on answering
            _ = e.ArtistRelated.Changed.Value;                     // Fans also like answering (full release)
            _ = e.TrackRelatedArtists.Changed.Value;               // Fans also like answering (short release)
            bool demanded = _demanded.Value;                       // same-component signals — always tracked too
            bool deadline = _deadline.Value;
            int slot = _slot;
            var a = new Album(slot);
            var billed = a.IsValid ? a.ArtistSlots : default;
            // The two sections directly under the rows: About the artist (the lead's name is known, or nobody is billed
            // and there is no card) and Featured on (the recommendations edge answered or failed — an EMPTY answer is
            // ready too, the section simply does not mount).
            bool aboutReady = billed.Length == 0 || new Artist(billed[0]).Knows(ArtistFields.Name);
            bool featuredReady = e.AlbumRecommendations.Readiness(slot) != EdgeState.Unknown;
            // The music-video section: every member's video verdict (asked with the rows in DemandTracked), at ANY
            // release length, so the section reveals with the band instead of landing a beat after it and shoving
            // About-the-artist down — it is the FIRST section in the band.
            var members = MemoryMarshal.Cast<int, Track>(a.IsValid ? a.TrackSlots : default);
            bool shortRelease = PageRules.IsShortRelease(a.IsValid && a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album, members.Length);
            bool videoReady = PageRules.VideoDecided(members);
            // Fans also like sits right under About: its edge has answered and every fan drawn knows its name, so the
            // chips land with the band instead of the header first and the faces a frame later.
            bool fansReady;
            ReadOnlySpan<int> fans;
            if (shortRelease)
            {
                int seed = PageRules.SeedTrackIndex(members);
                fansReady = seed < 0 || e.TrackRelatedArtists.Readiness(a.TrackSlots[seed]) != EdgeState.Unknown;
                fans = seed >= 0 ? members[seed].RelatedArtistSlots : default;
            }
            else
            {
                fansReady = billed.Length == 0 || e.ArtistRelated.Readiness(billed[0]) != EdgeState.Unknown;
                fans = billed.Length > 0 ? new Artist(billed[0]).RelatedSlots : default;
            }
            int fanCount = Math.Min(fans.Length, PageRules.FansCap);
            for (int i = 0; i < fanCount && fansReady; i++) fansReady = new Artist(fans[i]).Knows(ArtistFields.Name);
            var stamp = new RowStamp(slot, tracksPublished, e.AlbumTracks.Version(slot));
            if (!stamp.Moved(_pendingTracks) && demanded == _pendingDemanded && aboutReady == _pendingAbout
                && featuredReady == _pendingFeatured && fansReady == _pendingFans && videoReady == _pendingVideo
                && deadline == _pendingDeadline)
                return _pendingCached;
            _pendingTracks = stamp;
            _pendingDemanded = demanded;
            _pendingAbout = aboutReady;
            _pendingFeatured = featuredReady;
            _pendingFans = fansReady;
            _pendingVideo = videoReady;
            _pendingDeadline = deadline;
            // The tracklist and the demand are unconditional; About, Featured on, Fans also like and the video verdict
            // hold the reserve for at most PageRules.TrailingDeadlineMs (all asked at Visible priority, so they normally
            // land inside it). Merch, more-by and similar stay Prefetch and are NOT part of this gate — they resolve
            // later and the sections update in place (see PageRules.TrailingReserved).
            bool pending = PageRules.TrailingReserved(demanded, e.AlbumTracks.Readiness(slot), aboutReady, featuredReady, fansReady, videoReady, deadline);
            if (!pending) _swapped = true;                       // ONE swap per album: later landings update in place
            return _pendingCached = pending;
        }

        /// <summary>The deadline timer's fire. The timer arms from mount too (UseTimeout has no disabled arm), so a fire
        /// before the page has demanded is ignored — the re-arm on the (album, demanded) dep change is the one that
        /// counts. Idempotent: the signal flips once per album.</summary>
        void DeadlineHit()
        {
            if (_demanded.Peek() && !_deadline.Peek()) _deadline.Value = true;
        }

        /// <summary>The band's relations and the rows its sections paint. The two sections directly under the rows —
        /// About the artist (the lead's Identity|Stats|Bio) and Featured on (the recommendations edge and its playlist
        /// identities) — are asked at Visible priority: the reserve holds for them (PendingNow), so they must ride the
        /// same planner lane as the tracklist. Merch, more-by, similar and the related artists stay Prefetch (below the
        /// fold; not part of the gate). The
        /// <c>Ask</c> calls stay unconditional (each is a single state check, a no-op once its edge leaves Unknown);
        /// the <c>EnsureRows</c> calls — the ones that actually walk a growing target span — are each gated behind a
        /// <see cref="RowStamp"/> for the relation they read, so an unrelated drain elsewhere (another page's
        /// prefetch, a sidebar sync) that merely wakes this effect via one of the eight subscriptions below does not
        /// also re-walk every relation's targets (ch 08 render-cost fix).</summary>
        void Demand()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = e.AlbumTracks.Changed.Value;
            _ = e.AlbumArtists.Changed.Value;
            uint moreByPublished = e.AlbumMoreBy.Changed.Value;
            uint similarPublished = e.AlbumSimilar.Changed.Value;
            uint versionsPublished = e.AlbumVersions.Changed.Value;
            uint recsPublished = e.AlbumRecommendations.Changed.Value;
            uint relatedPublished = e.ArtistRelated.Changed.Value;
            uint trackRelatedPublished = e.TrackRelatedArtists.Changed.Value;
            int slot = _slot;
            var a = new Album(slot);
            if (!a.IsValid) return;

            Ask(FetchEdge.AlbumRecommendations, e.AlbumRecommendations, slot, FetchPriority.Visible);
            Ask(FetchEdge.AlbumMerch, e.AlbumMerch, slot, FetchPriority.Prefetch);
            Ask(FetchEdge.AlbumMoreBy, e.AlbumMoreBy, slot, FetchPriority.Prefetch);
            var members = a.TrackSlots;
            // Similar albums are SEEDED by a track (Album.SimilarSeedUri): ask once the tracklist is in hand.
            if (members.Length > 0) Ask(FetchEdge.AlbumSimilar, e.AlbumSimilar, slot, FetchPriority.Prefetch);

            bool shortRelease = PageRules.IsShortRelease(a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album, members.Length);
            var billed = a.ArtistSlots;
            ReadOnlySpan<int> fans = default;
            int fansParent = Table.None;
            bool fansFromTrack = false;
            if (billed.Length > 0)
            {
                var lead = new Artist(billed[0]);
                if (!lead.Knows(ArtistFields.Stats | ArtistFields.Bio))
                    Entities.Ensure(lead, ArtistFields.Identity | ArtistFields.Stats | ArtistFields.Bio, FetchPriority.Visible);
                if (!shortRelease)
                {
                    Ask(FetchEdge.ArtistRelated, e.ArtistRelated, lead.Slot, FetchPriority.Visible);
                    fans = lead.RelatedSlots;
                    fansParent = lead.Slot;
                }
            }
            if (shortRelease)
            {
                int seed = PageRules.SeedTrackIndex(MemoryMarshal.Cast<int, Track>(members));
                if (seed >= 0)
                {
                    fansParent = members[seed];
                    fansFromTrack = true;
                    fans = new Track(fansParent).RelatedArtistSlots;
                }
            }

            var moreByStamp = new RowStamp(slot, moreByPublished, e.AlbumMoreBy.Version(slot));
            if (moreByStamp.Moved(_moreByStamp))
            {
                _moreByStamp = moreByStamp;
                EnsureRows(scope.Albums, e.AlbumMoreBy.Targets(slot), (uint)AlbumFields.Card, FetchPriority.Prefetch);
            }
            var similarStamp = new RowStamp(slot, similarPublished, e.AlbumSimilar.Version(slot));
            if (similarStamp.Moved(_similarStamp))
            {
                _similarStamp = similarStamp;
                EnsureRows(scope.Albums, e.AlbumSimilar.Targets(slot), (uint)AlbumFields.Card, FetchPriority.Prefetch);
            }
            var versionsStamp = new RowStamp(slot, versionsPublished, e.AlbumVersions.Version(slot));
            if (versionsStamp.Moved(_versionsStamp))
            {
                _versionsStamp = versionsStamp;
                EnsureRows(scope.Albums, e.AlbumVersions.Targets(slot), (uint)AlbumFields.Card, FetchPriority.Prefetch);
            }
            var recsStamp = new RowStamp(slot, recsPublished, e.AlbumRecommendations.Version(slot));
            if (recsStamp.Moved(_recsStamp))
            {
                _recsStamp = recsStamp;
                // Featured on paints these playlists' names the moment the band reveals: same lane as the edge itself.
                EnsureRows(scope.Playlists, e.AlbumRecommendations.Targets(slot), (uint)PlaylistFields.Identity, FetchPriority.Visible);
            }
            // The fans source flips between the lead artist's related edge and a seed track's. The SOURCE TAG is what
            // makes that safe: a slot is a per-table index, so the artist parent and the track parent can carry the
            // same slot number, and without the tag a flip could match the other source's cached generation/version
            // by coincidence, read as "not moved", and skip the fetch — leaving the fans row silently empty.
            var fansStamp = fansFromTrack
                ? new RowStamp(fansParent, trackRelatedPublished, e.TrackRelatedArtists.Version(fansParent), Source: 1)
                : new RowStamp(fansParent, relatedPublished, e.ArtistRelated.Version(fansParent), Source: 2);
            if (fansStamp.Moved(_fansStamp))
            {
                _fansStamp = fansStamp;
                EnsureRows(scope.Artists, fans.Length > PageRules.FansCap ? fans[..PageRules.FansCap] : fans, (uint)ArtistFields.Identity, FetchPriority.Visible);
            }

            if (!_demanded.Peek() && e.AlbumTracks.Readiness(slot) != EdgeState.Unknown) _demanded.Value = true;
        }

        static void Ask(FetchEdge edge, EdgeTableBase table, int parent, FetchPriority priority)
        {
            if (parent > 0 && table.State(parent) == EdgeState.Unknown) Entities.EnsureEdge(edge, parent, 0, priority);
        }

        static void EnsureRows(Table table, ReadOnlySpan<int> slots, uint groups, FetchPriority priority)
        {
            if (!slots.IsEmpty) Entities.Ensure(table, slots, groups, priority);
        }
    }

    /// <summary>The resolved band: the seven sections in their fixed order, each present or absent on its own gate. The host decides which are present and
    /// reads what each paints; the `Album.Sections` view arranges them.</summary>
    [CoreHost]
    sealed class SectionsHost : Component
    {
        static readonly Func<Track, bool> s_hasOverride = static t => (t.FlagBits & (uint)TrackFlags.VideoOverride) != 0;

        int _slot;
        readonly Func<SectionsStamp> _stamp;
        readonly Action _demandVideos;

        public SectionsHost()
        {
            _stamp = Stamp;
            _demandVideos = DemandVideos;
        }

        /// <summary>Which sections are present and what each one paints from (W2-A2): the album row and the members
        /// (the video section's flags and stills and the seed track's play counts), the video COUNTERPART rows (their
        /// own titles and durations, which land after the members do — they are not members, so the member fold cannot
        /// see them), the billing edge and the lead artist's version (About the artist; the "More by" title), the fans'
        /// count and rows (the chips), and each list relation's count plus edge version (its section header, its
        /// stack's key signature). The rows INSIDE a list section are the stack's own gate (<c>StackHost</c>), not this
        /// one's.</summary>
        readonly record struct SectionsStamp(uint Epoch, int Slot, uint Album, uint TracksEdge, EdgeState Tracks, ulong Members,
                                             ulong Videos,
                                             uint ArtistsEdge, int Lead, uint LeadVersion, int Fans, ulong FanRows,
                                             int MoreBy, uint MoreByEdge, int Featured, uint FeaturedEdge,
                                             int Merch, uint MerchEdge, int Similar, uint SimilarEdge);

        /// <summary>The fold over what the video section paints that the MEMBER fold does not already carry: each
        /// selected video's still id and its counterpart row's version. A counterpart's identity lands later (see
        /// <see cref="DemandVideos"/>), and without this the card would keep the song's title for the rest of the
        /// session.</summary>
        static ulong VideoFold(Table tracks, ReadOnlySpan<PageRules.AlbumVideo> videos)
        {
            ulong fold = RowFold.Seed;
            for (int i = 0; i < videos.Length; i++)
            {
                fold = RowFold.Add(fold, videos[i].Thumb.Value);
                fold = RowFold.Row(fold, tracks, videos[i].CounterpartSlot);
            }
            return fold;
        }

        /// <summary>The counterpart rows' identities. Kind 99 hands the page a video's uri and its still and NOTHING
        /// else (<c>Spotify.Decode.cs</c>), so the video's own title and duration need one Prefetch ask — the same one
        /// the versions drawer makes for the row it expands (<c>Track.Drawer.cs</c> <c>DemandVersions</c>). Until it
        /// lands the card states the SONG's title and length, which is why a late answer is an upgrade and never a
        /// blank.</summary>
        void DemandVideos()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Tracks.Changed.Value;                    // a member learning its counterpart is the wake
            var a = new Album(_slot);
            if (!a.IsValid) return;
            Span<PageRules.AlbumVideo> buffer = stackalloc PageRules.AlbumVideo[PageRules.VideoCap];
            int count = PageRules.SelectVideos(MemoryMarshal.Cast<int, Track>(a.TrackSlots), buffer);
            Span<int> wanted = stackalloc int[PageRules.VideoCap];
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                int slot = buffer[i].CounterpartSlot;
                if (slot > Table.None && !new Track(slot).Knows(TrackFields.Identity)) wanted[n++] = slot;
            }
            if (n > 0) Entities.Ensure(scope.Tracks, wanted[..n], (uint)TrackFields.Identity, FetchPriority.Prefetch);
        }

        SectionsStamp Stamp()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            var e = scope.Edges;
            _ = scope.Albums.Changed.Value;
            _ = scope.Artists.Changed.Value;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Playlists.Changed.Value;
            _ = e.AlbumTracks.Changed.Value;
            _ = e.AlbumArtists.Changed.Value;
            _ = e.AlbumMoreBy.Changed.Value;
            _ = e.AlbumSimilar.Changed.Value;
            _ = e.AlbumMerch.Changed.Value;
            _ = e.AlbumRecommendations.Changed.Value;
            _ = e.ArtistRelated.Changed.Value;
            _ = e.TrackRelatedArtists.Changed.Value;

            int slot = _slot;
            var a = new Album(slot);
            if (!a.IsValid) return default(SectionsStamp) with { Epoch = epoch, Slot = slot };
            var memberSlots = a.TrackSlots;
            var members = MemoryMarshal.Cast<int, Track>(memberSlots);
            var billed = a.ArtistSlots;
            int lead = billed.Length > 0 ? billed[0] : Table.None;
            bool shortRelease = PageRules.IsShortRelease(a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album, members.Length);
            int seed = PageRules.SeedTrackIndex(members);
            ReadOnlySpan<int> fans = shortRelease
                ? (seed >= 0 ? members[seed].RelatedArtistSlots : default)
                : (lead > Table.None ? new Artist(lead).RelatedSlots : default);
            int fanCount = Math.Min(fans.Length, PageRules.FansCap);
            Span<PageRules.AlbumVideo> videos = stackalloc PageRules.AlbumVideo[PageRules.VideoCap];
            int videoCount = PageRules.SelectVideos(members, videos);
            return new SectionsStamp(epoch, slot, a.Version, e.AlbumTracks.Version(slot), e.AlbumTracks.State(slot),
                                     RowFold.Rows(scope.Tracks, memberSlots),
                                     VideoFold(scope.Tracks, videos[..videoCount]),
                                     e.AlbumArtists.Version(slot), lead, RowFold.Version(scope.Artists, lead),
                                     fanCount, RowFold.Rows(scope.Artists, fans[..fanCount]),
                                     e.AlbumMoreBy.Count(slot), e.AlbumMoreBy.Version(slot),
                                     e.AlbumRecommendations.Count(slot), e.AlbumRecommendations.Version(slot),
                                     e.AlbumMerch.Count(slot), e.AlbumMerch.Version(slot),
                                     e.AlbumSimilar.Count(slot), e.AlbumSimilar.Version(slot));
        }

        public override Element Render()
        {
            var p = UseProps<TrailingProps>();
            _slot = p.AlbumSlot;
            var overlay = UseContext(Overlay.Service);               // the video hero's menu host (its shelf cards read their own)
            // The Huge aside (contract 1.21): its arm draws About, Fans also like and "More by" beside the release facts; while it is docked the band
            // under the rows leaves those three out, so nothing is shown twice.
            bool asideDocked = UseContext(Detail.AsideDocked);
            bool asideArm = p.Aside;
            bool leaveOut = asideDocked && !asideArm;
            // WinUI keeps "Fans also like" under the list while the rest of the sections dock in the aside (`DetailTierLayout.FansInBand`).
            bool fansStay = asideDocked && ShellPolicy.Tracked.DetailTiers.For(Wavee.Theming.ThemeHost.Facts.Value.Tiers).FansInBand;
            // The gate (W2-A2): the twelve counters are subscribed inside the memo; the section data below — seven sections, each a card or a keyed stack — is
            // rebuilt only when the stamp moved.
            _ = UseComputed(_stamp).Value;
            UseEffect(_demandVideos);
            var e = Entities.Current.Edges;

            var a = new Album(p.AlbumSlot);
            if (!a.IsValid) return asideArm ? MountAside(a, null, [], null, []) : Mount(false, [], false, null, null, [], []);
            var memberSlots = a.TrackSlots;
            var members = MemoryMarshal.Cast<int, Track>(memberSlots);
            bool shortRelease = PageRules.IsShortRelease(a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album, members.Length);
            // ONE entry per video-bearing row, in track order — never the boolean `any` that drew a single card for
            // three videos, and never gated on `shortRelease`, which hid every video on anything longer than an EP.
            Span<PageRules.AlbumVideo> videoBuffer = stackalloc PageRules.AlbumVideo[PageRules.VideoCap];
            var videos = videoBuffer[..PageRules.SelectVideos(members, videoBuffer)];

            var billed = a.ArtistSlots;
            var lead = billed.Length > 0 ? new Artist(billed[0]) : default;
            bool about = lead.IsValid && lead.Knows(ArtistFields.Name);
            int seed = PageRules.SeedTrackIndex(members);
            // A short release reads the SEED track's related artists; a full album reads the lead artist's.
            ReadOnlySpan<int> fans = shortRelease
                ? (seed >= 0 ? members[seed].RelatedArtistSlots : default)
                : (lead.IsValid ? lead.RelatedSlots : default);
            int fanCount = Math.Min(fans.Length, PageRules.FansCap);
            int slot = a.Slot;
            int featured = e.AlbumRecommendations.Count(slot), merch = e.AlbumMerch.Count(slot);
            int similar = e.AlbumSimilar.Count(slot), moreBy = e.AlbumMoreBy.Count(slot);
            if (asideArm)
            {
                var asideFans = new TrailCardData[fansStay ? 0 : fanCount];
                for (int i = 0; i < asideFans.Length; i++) asideFans[i] = FanCard(new Artist(fans[i]));
                Element? moreByStack = moreBy > 0 && billed.Length > 0
                    ? Stack(Strings.Detail.MoreBy(lead.Name), moreBy, TrailKind.MoreBy, slot, Signature("moreby:", moreBy, e.AlbumMoreBy.Targets(slot)), name: lead.Name,
                            artist: lead.Slot)
                    : null;
                var billedArtists = new List<Artist>(billed.Length);
                for (int i = 0; i < billed.Length; i++)
                {
                    var billedArtist = new Artist(billed[i]);
                    if (billedArtist.IsValid && billedArtist.Knows(ArtistFields.Name)) billedArtists.Add(billedArtist);
                }
                return MountAside(a, about ? lead : null, asideFans, moreByStack, billedArtists);
            }
            if (leaveOut) { about = false; if (!fansStay) fanCount = 0; moreBy = 0; }
            if (!PageRules.HasTrailingSections(videos.Length > 0, about, fanCount, featured, merch, similar, moreBy, billed.Length))
                return Mount(false, [], false, null, null, [], []);   // nothing to show: the region eases to zero

            var cards = new AlbumVideoCard[videos.Length];
            for (int i = 0; i < cards.Length; i++) cards[i] = VideoCard(in videos[i]);
            var fanCards = new TrailCardData[fanCount];
            for (int i = 0; i < fanCards.Length; i++) fanCards[i] = FanCard(new Artist(fans[i]));
            var lists = new List<Element>(4);
            if (moreBy > 0 && billed.Length > 0)
                lists.Add(Stack(Strings.Detail.MoreBy(lead.Name), moreBy, TrailKind.MoreBy, slot,
                                Signature("moreby:", moreBy, e.AlbumMoreBy.Targets(slot)), name: lead.Name, artist: lead.Slot));
            if (featured > 0)
                lists.Add(Stack(Loc.Get(Strings.Detail.FeaturedOn), featured, TrailKind.FeaturedOn, slot,
                                Signature("featured:", featured, e.AlbumRecommendations.Targets(slot))));
            if (merch > 0)
                lists.Add(Stack(Loc.Get(Strings.Artist.Merch), merch, TrailKind.Merch, slot,
                                Signature("merch:", merch, e.AlbumMerch.Targets(slot))));
            if (similar > 0)
                lists.Add(Stack(Loc.Get(Strings.Detail.SimilarAlbums), similar, TrailKind.Similar, slot,
                                Signature("similar:", similar, e.AlbumSimilar.Targets(slot))));
            return Mount(true, cards, videos.Length > 0 && PageRules.HasCustomVideo(members, s_hasOverride), overlay, about ? lead : null, fanCards, lists);
        }

        static Element Mount(bool any, IReadOnlyList<AlbumVideoCard> videos, bool customVideo, IOverlayService? overlay, Artist? about,
                             IReadOnlyList<TrailCardData> fans, IReadOnlyList<Element> lists)
            => ViewSlots.View(ViewKeys.AlbumSections, new AlbumSectionsProps
            {
                Any = any, Videos = videos, CustomVideo = customVideo, Overlay = overlay, About = about, Fans = fans, Lists = lists,
            });

        /// <summary>The aside arm's view: the release facts (always: the rail no longer carries them), then whichever of the artist, the fans and "More by"
        /// are present.</summary>
        static Element MountAside(Album a, Artist? about, IReadOnlyList<TrailCardData> fans, Element? moreBy, IReadOnlyList<Artist> artists)
            => ViewSlots.View(ViewKeys.AlbumAside, new AlbumAsideProps
            {
                Release = a.IsValid ? ReleasePanel(a, false) : null, About = about, Fans = fans, MoreBy = moreBy,
                Artists = artists, AlbumTitle = a.IsValid && a.Knows(AlbumFields.Title) ? a.Title : "",
                // The credits paragraph (contract 1.82): null today (PageRules.CreditsOf: no album-level credits source yet), so no section draws.
                Credits = a.IsValid ? PageRules.CreditsOf(a) : null,
            });

        static string Signature(string prefix, int count, ReadOnlySpan<int> targets)
            => prefix + count.ToString(CultureInfo.InvariantCulture) + ":"
             + (targets.Length > 0 ? targets[0].ToString(CultureInfo.InvariantCulture) : "");
    }

    /// <summary>One row of a list section, read live off its relation: the data its view paints.</summary>
    static TrailRowData TrailRow(TrailKind kind, int parent, int index)
    {
        var e = Entities.Current.Edges;
        return kind switch
        {
            TrailKind.MoreBy => AlbumRow(At(e.AlbumMoreBy.Targets(parent), index)),
            TrailKind.Similar => AlbumRow(At(e.AlbumSimilar.Targets(parent), index)),
            TrailKind.FeaturedOn => PlaylistRow(At(e.AlbumRecommendations.Targets(parent), index)),
            _ => MerchRow(At(e.AlbumMerch.Targets(parent), index)),
        };
    }

    static int At(ReadOnlySpan<int> slots, int index) => (uint)index < (uint)slots.Length ? slots[index] : 0;

    /// <summary>A related album: the shared media surface (its row, 48 cover, hover play FAB, the hot-revealed "…", drag source) on the opaque card tile under
    /// its hairline. Its menu is the home card grammar (<see cref="HomeCardNav.MenuOf"/>: Play · Play next · Add to queue, Save · Add to playlist · Open ·
    /// Pin · Share), built at open from the card handle — the row has no tracklist of its own to compose a track menu from.</summary>
    static TrailRowData AlbumRow(int slot)
    {
        var x = new Album(slot);
        if (!x.IsValid) return new TrailRowData(TrailRowKind.Missing);
        var artists = x.ArtistSlots;
        var artist = artists.Length > 0 ? new Artist(artists[0]) : default;
        string subtitle = PageRules.Subtitle(artist.IsValid && artist.Knows(ArtistFields.Name) ? artist.Name : null, x.Year, x.Kind);
        string title = x.Knows(AlbumFields.Title) ? x.Title : "";
        string uri = x.Uri.Text;
        string? cover = Controls.ArtUrl(x.ImageId);
        var id = x.Id;
        var card = new HomeCard(new EntityRef(EntityKind.Album, slot));
        return new TrailRowData(TrailRowKind.Card, new TrailCardData("album:" + slot.ToString(CultureInfo.InvariantCulture), uri, title, subtitle, cover,
            OnClick: () => Track.GoToAlbum(x),
            OnPlay: () => Playback.PlayOrToggleContext(id),
            Circular: false,
            Drag: () => ResourcePayload(DragKind.Album, EntityKind.Album, slot, uri, title, cover),
            Menu: HomeCardNav.MenuOf(in card))
        {
            // The release's own kind and year (the TypeYear eyebrow's words), for a theme that captions an artist's releases by them (contract 1.81).
            TypeYear = Detail.Text.Eyebrow(BadgeStyle.TypeYear, x.Kind, x.Year, collaborative: false, isPublic: false, visibilityKnown: false),
        });
    }

    /// <summary>A playlist this album appears on: the same plated row, the owner as its subtitle, the same card menu.</summary>
    static TrailRowData PlaylistRow(int slot)
    {
        var x = new Playlist(slot);
        if (!x.IsValid) return new TrailRowData(TrailRowKind.Missing);
        string title = Entities.Strings.Resolve(x.TitleId);
        string owner = x.Owner.IsValid ? Entities.Strings.Resolve(x.Owner.NameId) : "";
        string uri = x.Uri.Text;
        string? cover = Controls.ArtUrl(x.ImageId);
        var playlistUri = x.Uri;
        var id = x.Id;
        var card = new HomeCard(new EntityRef(EntityKind.Playlist, slot));
        return new TrailRowData(TrailRowKind.Card, new TrailCardData("playlist:" + slot.ToString(CultureInfo.InvariantCulture), uri, title,
            owner.Length > 0 ? owner : null, cover,
            OnClick: () => Shell.GoTo(Shell.For(playlistUri, title)),
            OnPlay: () => Playback.PlayOrToggleContext(id),
            Circular: false,
            Drag: () => ResourcePayload(DragKind.Playlist, EntityKind.Playlist, slot, uri, title, cover),
            Menu: HomeCardNav.MenuOf(in card)));
    }

    /// <summary>A navigable resource as a drag payload: tracks resolve through the library seam, lazily, after a drop.</summary>
    static DragPayload ResourcePayload(DragKind kind, EntityKind entity, int slot, string uri, string title, string? cover)
    {
        Func<CancellationToken, Task<Track[]>>? resolver = null;
        if (Sidebar.LibraryWrites?.ResolveTracks is { } resolve) resolver = ct => resolve(uri, ct);
        return new DragPayload(kind, uri, uri, title, new EntityRef(entity, slot), TrackResolver: resolver, ArtUrl: cover);
    }

    /// <summary>A merch listing: not a media row — nothing to play, and its call to action is the PRICE ("Buy" when the wire gave none). With no shop url it is
    /// a listing, not a dead button: the view gives it no scale, no role, no focus stop, no cursor, no click (<see cref="MerchRowData.Open"/> null).</summary>
    static TrailRowData MerchRow(int merchSlot)
    {
        if (merchSlot <= 0) return new TrailRowData(TrailRowKind.Missing);
        var m = MerchAt(merchSlot);
        string name = Entities.Strings.Resolve(m.Name);
        string price = m.Price.IsEmpty ? "" : Entities.Strings.Resolve(m.Price);
        string? url = m.ShopUrl.IsEmpty ? null : Entities.Strings.Resolve(m.ShopUrl);
        Action? open = url is { Length: > 0 } ? () => Actions.Services.OpenExternal?.Invoke(url) : null;
        return new TrailRowData(TrailRowKind.Merch,
            Merch: new MerchRowData("merch:" + merchSlot.ToString(CultureInfo.InvariantCulture), name, price, Controls.ArtUrl(m.ImageId), open));
    }

    /// <summary>"Fans also like": one circular artist card, the same as the artist page's (click opens, hover play toggles the artist context, container
    /// menu, drag source).</summary>
    static TrailCardData FanCard(Artist artist)
    {
        string uri = artist.Uri.Text;
        string name = artist.Name;
        string? image = Controls.ArtUrl(artist.ImageId);
        string sub = Loc.Get(Strings.Search.TypeArtist);
        var id = artist.Id;
        var ar = artist;
        return new TrailCardData("fan:" + artist.Slot.ToString(CultureInfo.InvariantCulture), uri, name, sub, image,
            OnClick: () => Track.GoToArtist(ar),
            OnPlay: () => Playback.PlayOrToggleContext(id),
            Circular: true,
            Drag: () => new DragPayload(DragKind.Artist, uri, uri, name, new EntityRef(EntityKind.Artist, ar.Slot), ArtUrl: image),
            Menu: () => Menus.Container(ActionTarget.ForArtist(ar.Uri, name), image, sub));
    }

    // ── the music-video section (ch 05 W12; the 2026-09-17 rewrite) ──────────────────────────────────────────────────
    //
    // The section's pictures (the hero card, the shelf, the skeleton) are Classic's (`ClassicAlbumTrailing.cs`); what a video IS stays here. ONE thumbnail rule
    // for both arms: EVERY still is the video's own (PageRules.SelectVideos → Track.VideoImageId, the counterpart's art, the song's art — in that order). ONE
    // click rule for both arms: the card plays the VIDEO — that is, the SONG that owns it, because kind 99 keys a video on its song (Track.Drawer.cs
    // PlayVersion does exactly this).

    /// <summary>What a video's card shows and the verbs it runs. A music video is a TRACK (kind 99 keys it on its song): its identity for the now-playing
    /// relation is the song's uri, its menu and its drag are the track's, and a click or the badge WATCHES it (<see cref="WatchVideo"/> — the surface first,
    /// then the song). The duration rides the shelf card's inline caption.</summary>
    static AlbumVideoCard VideoCard(in PageRules.AlbumVideo v)
    {
        int slot = v.MemberSlot;
        string uri = VideoUri(slot);
        string title = VideoTitle(in v);
        string? thumb = Controls.ArtUrl(v.Thumb);
        return new AlbumVideoCard(VideoKey(in v), uri, title, thumb, VideoMeta(in v),
            v.DurationMs > 0 ? Track.Format.TrackTime(v.DurationMs) : null,
            WatchVideo(slot), () => VideoPayload(slot, uri, title, thumb), () => VideoMenu(slot));
    }

    /// <summary>The video's identity for the playback relation: its member song's uri ("" for a stale handle).</summary>
    static string VideoUri(int memberSlot)
    {
        var member = new Track(memberSlot);
        return member.IsValid ? member.Uri.Text : "";
    }

    /// <summary>A video drags as the track it is — depositable on a playlist, queueable.</summary>
    static DragPayload VideoPayload(int memberSlot, string uri, string title, string? cover)
        => new(DragKind.Track, uri, uri, title, new EntityRef(EntityKind.Track, memberSlot),
               Tracks: [new Track(memberSlot)], ArtUrl: cover);

    /// <summary>The track menu for the video's song, built at OPEN. No "Go to album" (this IS the album's page); the
    /// playlist picker opens on the ambient overlay the page published (<see cref="Track.DrawerOverlay"/>), which is how
    /// a static seam with no component context reaches one.</summary>
    static ContextMenuModel? VideoMenu(int memberSlot)
    {
        var member = new Track(memberSlot);
        return member.IsValid
            ? Track.Menu([member], new Track.MenuOptions(ShowGoToAlbum: false, PickerOverlay: Track.DrawerOverlay))
            : null;
    }

    /// <summary>The VIDEO's title: the counterpart row's once its identity has landed (<c>SectionsHost.DemandVideos</c>),
    /// the song's until then — an upgrade, never a blank.</summary>
    static string VideoTitle(in PageRules.AlbumVideo v)
    {
        var counterpart = new Track(v.CounterpartSlot);
        if (counterpart.IsValid && !counterpart.TitleId.IsEmpty) return counterpart.Title;
        var member = new Track(v.MemberSlot);
        return member.IsValid ? member.Title : "";
    }

    /// <summary>The hero arm's subtitle: what this card IS, then how long it runs. Never the album's "N songs · M min ·
    /// year" — that line described the RELEASE and was the third half of this bug.</summary>
    static string VideoMeta(in PageRules.AlbumVideo v)
    {
        string kind = Loc.Get(Strings.Detail.Versions.MusicVideo);
        return v.DurationMs > 0 ? kind + " · " + Track.Format.TrackTime(v.DurationMs) : kind;
    }

    /// <summary>WATCH this video. Playing the song is NOT enough: the reducer decides a row's media kind from
    /// <c>videoWanted &amp;&amp; (flags &amp; VideoMask)</c> (<c>Playback.Transitions</c> <c>KindOfRow</c>), and
    /// <c>videoWanted</c> comes from the placement state, whose <c>Requested</c> starts at
    /// <c>SurfacePlacement.None</c> — so starting the song with the surface off gives exactly what it says: audio.
    /// <para>The card therefore REQUESTS THE SURFACE FIRST and plays second (<see cref="PageRules.WatchAction"/>). It
    /// requests through <c>State.FoldAvailability</c> + <c>State.OpenAt</c> — the rail's own <c>ShowVideoAt</c> pair,
    /// which <c>Commit</c>s directly — and not through <c>FoldForTrack</c>, so <c>UpgradeGate.DeferUpgrade</c> (which
    /// exists to withhold a MID-TRACK upgrade nobody asked for) can never swallow an explicit click. The commit posts
    /// <c>Playback.SetVideoPlacement(true)</c>, and because the reducer's inbox is FIFO and folded in one batch, that
    /// input lands before the load.</para></summary>
    static Action WatchVideo(int memberSlot)
    {
        var member = new Track(memberSlot);
        return () => Watch(member);
    }

    static void Watch(Track member)
    {
        if (!member.IsValid) return;
        // ONE `hasVideo` for both halves of the decision. The gate below asks "can anything host this row's video right
        // now"; RequestVideoSurface then STAMPS that same answer. Asking the gate a hardcoded `true` while the fold
        // stamped the row's real bit is how a disagreement between them would reopen the original defect — the gate
        // says "go", the fold stamps None, OpenAt resolves to nothing, and the song plays under a play badge with no
        // word said.
        bool hasVideo = member.HasVideo;
        bool canHost = Video.UpgradeGate.AvailabilityFor(hasVideo, Video.State.HostCapability.Peek()) != Video.PlacementSet.None;
        var playing = Playback.CurrentId.Peek();
        bool deckRow = !playing.IsEmpty && playing == member.Id;
        switch (PageRules.WatchFor(canHost, deckRow))
        {
            case PageRules.WatchAction.AudioOnly:
                // Say it. A play badge over a video still that silently starts the song is the defect, not the fix.
                Notify.Say(Loc.Get(Strings.Player.VideoUnavailable), InfoBarSeverity.Warning, dedupeKey: "album.video.nohost");
                Playback.PlayOrToggleContext(member.Id, "album.watch");
                return;

            case PageRules.WatchAction.SwitchInPlace:
                // Already on the deck: the placement input alone re-decides the row's kind and reloads it on the video
                // host at the carried position (DoVideoPlacement). Never PlayContext — that would restart it.
                RequestVideoSurface(hasVideo);
                if (!Playback.IsPlaying.Peek()) Playback.TogglePlay("album.watch");
                return;

            default:
                RequestVideoSurface(hasVideo);        // FIRST — see the summary; order is the contract
                Playback.PlayOrToggleContext(member.Id, "album.watch");
                return;
        }
    }

    /// <summary>Stamp the availability this row really has, then open at where the user likes to watch (G-150's
    /// persisted <c>Preferred</c>, docked when nothing is remembered) — the player bar's own
    /// <c>PlacementCore.TogglePrimary</c> opens at exactly that same <c>Preferred</c>, so a card click and the badge
    /// land the surface in the same place.
    /// <para>Two commits, in this order, and NOT <c>State.TogglePrimary</c>: that one is a TOGGLE (a second card click
    /// would turn the surface off) and it runs the intent through <c>UpgradeGate.PrimaryClick</c>, whose
    /// <c>DeferUpgrade</c> arm exists to withhold a mid-track upgrade nobody asked for. A click on a video card IS the
    /// ask, so it must not be routed past a gate built to ignore un-asked-for ones.</para></summary>
    static void RequestVideoSurface(bool hasVideo)
    {
        Video.State.FoldAvailability(hasVideo);
        var preferred = Video.State.Surface.Peek().Preferred;
        Video.State.OpenAt(preferred == Video.SurfacePlacement.None ? Video.SurfacePlacement.Docked : preferred);
    }

    static string VideoKey(in PageRules.AlbumVideo v)
        => "video:" + v.MemberSlot.ToString(CultureInfo.InvariantCulture);

}
