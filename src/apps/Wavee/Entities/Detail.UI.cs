// ── Entities/Detail.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// THE SHARED DETAIL FRAME (ch 03 §1.2), CORE HALF: Identity (+ Identity.For(Album)), FrameActions, FrameSlots, FrameSpec and
// Frame — the 4-mode responsive ladder with both anti-flicker fail-safes, the Hero page-layout override, the per-scope rail
// prefs + the Splitter grip + resist fade, the page tone plane and shell tint leaves, the accent and the page-body drop
// target, and `NoticeBar` (the mount of the notice view). The satellite, the More button, the artist line and the save / share targets are Classic's (T10c2:
// `ClassicDetailKit.Helpers.cs`).
//
// T9b (themes plan 6.1): the notice strip (`NoticeBar` is now only the mount of `Detail.Notice`), the vertical arm's trailing body (`Detail.VerticalTrailing`),
// the keyed rail rows (`Row` / `LateRow` / `ReleaseRow`, their FLIP and fade-up), the eyebrow run and the billed-artist line box are Classic's: they moved to
// `Themes\Classic\Structure\Views\Detail\ClassicDetailKit.cs` and `ClassicDetailSeam.cs`, with the numbers in `Rules\ClassicDetailKitLayout.cs`.
// T9c moved the rail satellites (`RailSatellite*`) to `ClassicDetailKit.Satellites.cs` and dropped `PlayButton` (a forward to `Controls.PlayButton`).
//
// T9a (themes plan 6.1): the PICTURE of the frame is Classic's. `FrameHost.Render` decides and builds ONE `DetailFrameProps`, and the
// registered `Detail.Frame` view (`Themes\Classic\Structure\Views\Detail\ClassicDetailFrame.cs`) arranges the notice strip, the content
// column, the rail | grip | content row, the 96-DIP strip, the vertical show header and the insights sheet; the rail, the strip, the show
// header, the hero and the sheet are their own keys (`Theming\Contracts\ViewKeys.Detail.cs`). What the structure draws moved with its
// code: the rail family (`ClassicDetailRail.cs`), the vertical hero and its loading band (`ClassicDetailHero.cs`), the insights sheet
// (`ClassicInsights.cs`). The vertical hero, the context band's helpers and the presence rules are the partial Detail.UI.Hero.cs.
//
// Podcast rework wave P2 (podcast-show-rework-implementation.md §5.4): six APPENDED FrameSlots (Badges, Rating, Ledger,
// Primary, Satellites, Topics) with their insertion points in the rail and in the vertical show header, an episode's rail
// led by its show link (the Attribution slot), and the skeleton twin reserving each declared slot — both columns walk the
// ONE row model in Detail.cs §8b. The satellite builders (`ClassicDetailKit.RailSatellite*`, T9c) and the labeled
// `Controls.PlayButton` are what a page's slots are made of.
//
// Role: UI (the host is core; the picture is the structure's)
// Owner: M
// Wave: 4.5
// Spec: ch 03 §9
//
// ── HOW DATA REACHES THE FRAME (props freeze at mount) ───────────────────────────────────────────────────────────────
//
// A page builds a FrameSpec VALUE in its own render and hands it to Detail.Frame, which is ONE component keyed
// "detail:" + subject. That component is an IPropsHost: every re-push lands in a plain `_latest` field (so every delegate
// and slot is always the NEWEST the page pushed) and in an equality-gated signal whose record Equals compares DATA only
// (delegates by presence). A page that re-renders with an equal model therefore costs the frame nothing, and a real
// change re-renders exactly the frame. Node handlers never capture the page's closures: they call cached trampolines
// that read `_latest` at invocation, so a data-equal re-push with fresh closures is still honoured.
//
// The frame OWNS: the page mode and its measurement, the five rail pairs, the resist fade, the measured hero height the
// table publishes, the accent signal, the play-all cell and the shell-tint owner token. The TABLE owns the view state.
//
// ── THE COMPONENTS IN THIS FILE, AND WHY EACH IS ONE ─────────────────────────────────────────────────────────────────
//
//  FrameHost      the scaffold (above).
//  MoreHost       the hero/rail "More" button: it needs the overlay service (a hook) to attach the lazily-built menu.
// Everything else is a static function over values, re-run by its host.
//
// NAME NOTE: inside `Detail`, the nested `Text`, `Skeleton` and `Config` classes shadow `Ui.Text`, the engine's skeleton
// types and anything else of those names — text runs here are `new TextEl(...)` / `Ui.Caption(...)`, never a bare Text(.).

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using static FluentGpu.Dsl.Ui;
using Contracts = Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Detail
{
    // ══ 1. THE IDENTITY SNAPSHOT ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What every arm (rail, hero, compact rail, band, skeleton) renders — a value snapshot the page builds in its
    /// render. Equality is VALUE equality over every field (a face pile compares names/urls/flags, never its click
    /// delegates), so a page that re-publishes an identical model does not re-render the frame.</summary>
    public sealed record Identity
    {
        public required EntityUri Subject { get; init; }
        public required DetailKind Kind { get; init; }
        public string Title { get; init; } = "";
        /// <summary><c>Controls.ArtUrl(imageId)</c>; null ⇒ the art slot's neutral placeholder.</summary>
        public string? CoverUrl { get; init; }
        /// <summary>Override for the tone/tint/accent source (liked cover-wall anchor); null ⇒ <see cref="CoverUrl"/>.
        /// A mosaic is art only — never a wash source.</summary>
        public string? PaletteUrl { get; init; }
        /// <summary>Payload accent when no grading exists (the daylist card's extracted colour).</summary>
        public uint CardAccent { get; init; }
        /// <summary><c>Detail.Text.Eyebrow(...)</c> — answered for every kind (the hero shows it unconditionally).</summary>
        public string Eyebrow { get; init; } = "";
        /// <summary>Null + <see cref="MetaLoading"/> ⇒ a shimmer bar shaped "00 songs · 0 hr 00 min".</summary>
        public string? Meta { get; init; }
        public bool MetaLoading { get; init; }
        /// <summary>The subject's header (title, cover, owner/artists) has not answered yet. The two-column rail and the
        /// hero band shimmer AS A WHOLE on this flag and cross-dissolve once when it drops — never a real layout with
        /// empty cells. Each page derives it from its own row (<c>Knows(…Identity)</c>); Liked is never pending.</summary>
        public bool HeaderPending { get; init; }
        public string? OwnerName { get; init; }
        public string? OwnerImageUrl { get; init; }
        public EntityUri Owner { get; init; }
        /// <summary>Billed artists (album).</summary>
        public IReadOnlyList<Controls.Face>? Artists { get; init; }
        public IReadOnlyList<Controls.Face>? Collaborators { get; init; }
        public bool Collaborative { get; init; }
        public string? DescriptionHtml { get; init; }
        public int ChartNewEntries { get; init; }
        public long ChartUpdatedAtMs { get; init; }
        public long DaylistExpiresAtMs { get; init; }
        public long DaylistCreatedAtMs { get; init; }
        /// <summary>Prerelease countdown target, unix seconds (0 = none).</summary>
        public int UpcomingAtUnixSeconds { get; init; }
        /// <summary>The heart target (pre-release uri ?? subject). Default ⇒ the subject.</summary>
        public EntityUri SaveTarget { get; init; }
        public string? ShareUrl { get; init; }
        public DetailNotice Notice { get; init; }
        public bool EditableMetadata { get; init; }

        public bool Equals(Identity? o)
        {
            if (ReferenceEquals(this, o)) return true;
            if (o is null) return false;
            return Subject.Equals(o.Subject) && Kind == o.Kind
                && string.Equals(Title, o.Title, StringComparison.Ordinal)
                && string.Equals(CoverUrl, o.CoverUrl, StringComparison.Ordinal)
                && string.Equals(PaletteUrl, o.PaletteUrl, StringComparison.Ordinal)
                && CardAccent == o.CardAccent
                && string.Equals(Eyebrow, o.Eyebrow, StringComparison.Ordinal)
                && string.Equals(Meta, o.Meta, StringComparison.Ordinal) && MetaLoading == o.MetaLoading
                && HeaderPending == o.HeaderPending
                && string.Equals(OwnerName, o.OwnerName, StringComparison.Ordinal)
                && string.Equals(OwnerImageUrl, o.OwnerImageUrl, StringComparison.Ordinal)
                && Owner.Equals(o.Owner)
                && FacesEqual(Artists, o.Artists) && FacesEqual(Collaborators, o.Collaborators)
                && Collaborative == o.Collaborative
                && string.Equals(DescriptionHtml, o.DescriptionHtml, StringComparison.Ordinal)
                && ChartNewEntries == o.ChartNewEntries && ChartUpdatedAtMs == o.ChartUpdatedAtMs
                && DaylistExpiresAtMs == o.DaylistExpiresAtMs && DaylistCreatedAtMs == o.DaylistCreatedAtMs
                && UpcomingAtUnixSeconds == o.UpcomingAtUnixSeconds
                && SaveTarget.Equals(o.SaveTarget)
                && string.Equals(ShareUrl, o.ShareUrl, StringComparison.Ordinal)
                && Notice == o.Notice && EditableMetadata == o.EditableMetadata;
        }

        public override int GetHashCode() => HashCode.Combine(Subject, Kind, Title, CoverUrl, Meta, Notice);

        /// <summary>The album page's identity, read off the album columns and edges. The CALLER subscribes first
        /// (<c>Albums.Changed</c>, <c>Tracks.Changed</c>, <c>Artists.Changed</c>, <c>AlbumTracks.Changed</c>,
        /// <c>AlbumArtists.Changed</c>) — this reads, it does not subscribe. Cold: one pass over the tracklist per page
        /// render, never per frame.</summary>
        public static Identity For(Album a)
        {
            var scope = Entities.Current;
            var slots = a.TrackSlots;
            var state = scope.Edges.AlbumTracks.State(a.Slot);

            // The duration segment is dropped while any row is thin or the membership is incomplete — a sum over
            // zero-duration disc rows is a lie (ch 03 §7).
            long totalMs = 0;
            bool durationsKnown = state == EdgeState.Complete;
            for (int i = 0; i < slots.Length; i++)
            {
                var t = new Track(slots[i]);
                totalMs += t.DurationMs;
                if (!t.Knows(TrackFields.Duration)) durationsKnown = false;
            }

            // A known count of 0 is "not known yet" (Album.PageRules.SongCount): it neither prints "0 songs" nor ends
            // the loading state on its own — a row restored from a cache written before the commit guard can still
            // carry Known + 0 — so the realised members answer once the tracklist lands.
            bool countKnown = a.Knows(AlbumFields.TrackCount) && a.TrackCount > 0;
            bool metaLoading = !countKnown && state == EdgeState.Unknown;
            int trackCount = Album.PageRules.SongCount(countKnown ? a.TrackCount : 0, slots.Length);
            // No year here: the eyebrow carries it, and the release panel spells the full date.
            string? meta = metaLoading ? null : Text.AlbumMeta(trackCount, totalMs, durationsKnown);

            // Billed artists. An album with no artist edge (the fake seed's al3) has no attribution row at all.
            List<Controls.Face>? faces = null;
            var artistSlots = a.ArtistSlots;
            for (int i = 0; i < artistSlots.Length; i++)
            {
                var artist = new Artist(artistSlots[i]);
                if (!artist.Knows(ArtistFields.Name)) continue;
                faces ??= new List<Controls.Face>(artistSlots.Length);
                string name = artist.Name;
                var uri = artist.Uri;
                faces.Add(new Controls.Face(name, Controls.ArtUrl(artist.ImageId), () => Shell.GoTo(Shell.For(uri, name))));
            }

            // Entities.Now is APP seconds; the countdown instant is unix seconds.
            bool upcoming = a.IsPreRelease && a.PreReleaseEnd > Store.ToUnix(Entities.Now);
            var saveTarget = upcoming && !a.PreReleaseUriId.IsEmpty ? new EntityUri(a.PreReleaseId) : a.Uri;

            return new Identity
            {
                Subject = a.Uri,
                Kind = DetailKind.Album,
                HeaderPending = !a.Knows(AlbumFields.Title),
                Title = a.Knows(AlbumFields.Title) ? a.Title : "",
                CoverUrl = Controls.ArtUrl(a.ImageId),
                CardAccent = a.Accent,
                // A release whose KIND is unknown has no badge yet — the eyebrow cannot guess "SINGLE" off a zeroed cell.
                Eyebrow = a.Knows(AlbumFields.Kind)
                    ? Text.Eyebrow(DetailKind.Album, BadgeStyle.TypeYear, a.Kind, a.Year,
                                   collaborative: false, isPublic: true, visibilityKnown: true)
                    : "",
                Meta = meta,
                MetaLoading = metaLoading,
                Artists = faces,
                UpcomingAtUnixSeconds = upcoming ? a.PreReleaseEnd : 0,
                SaveTarget = saveTarget,
                ShareUrl = !a.ShareUrlId.IsEmpty ? Entities.Strings.Resolve(a.ShareUrlId) : WebUrlOf(a.Uri),
                Notice = state == EdgeState.Unknown
                    ? DetailNotice.None
                    : NoticeRules.ForAlbum(MemoryMarshal.Cast<int, Track>(slots)),
            };
        }
    }

    static bool FacesEqual(IReadOnlyList<Controls.Face>? a, IReadOnlyList<Controls.Face>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal)
                || !string.Equals(x.ImageUrl, y.ImageUrl, StringComparison.Ordinal)
                || x.Selected != y.Selected
                || !string.Equals(x.Tip, y.Tip, StringComparison.Ordinal)
                || (x.OnClick is null) != (y.OnClick is null)) return false;
        }
        return true;
    }

    // ══ 2. THE SPEC ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page-level verbs the frame wires. Equality is PRESENCE only (a null verb ⇒ its affordance is absent);
    /// the frame always invokes the newest delegate the page pushed.</summary>
    public sealed record FrameActions
    {
        /// <summary>Null ⇒ the shuffle satellite is absent.</summary>
        public Action? Shuffle { get; init; }
        /// <summary>The hero/rail More flyout content; null ⇒ absent.</summary>
        public Func<ContextMenuModel?>? More { get; init; }
        /// <summary>Editable playlist picker / liked style picker.</summary>
        public Action? CoverClick { get; init; }
        /// <summary>The cover's drag payload (<c>Drag.Source</c>).</summary>
        public Func<object?>? CoverDrag { get; init; }
        /// <summary>Page-body append target (playlist); null ⇒ the body target sits the gesture out.</summary>
        public Func<DragPayload, int?, bool>? DepositOnPage { get; init; }
        /// <summary>The notice strip's action (the "albums" route); null ⇒ no button.</summary>
        public Action? GoLibrary { get; init; }

        internal int Mask()
            => (Shuffle is null ? 0 : 1) | (More is null ? 0 : 2) | (CoverClick is null ? 0 : 4)
             | (CoverDrag is null ? 0 : 8) | (DepositOnPage is null ? 0 : 16) | (GoLibrary is null ? 0 : 32);

        public bool Equals(FrameActions? o) => o is not null && Mask() == o.Mask();
        public override int GetHashCode() => Mask();
    }

    /// <summary>Per-kind rows the frame hosts but does not own (Wave 5 pages supply them). Every slot is optional;
    /// equality is PRESENCE only, and the frame invokes the newest builder at render.
    /// <para>PRESENCE EQUALITY IS THE CONTRACT. A page re-pushes a new <c>FrameSlots</c> only when a slot APPEARS or
    /// DISAPPEARS (its facts became known, plan §6.1 — a slot is absent until then, never reserved); a builder closes over
    /// the page's own snapshot, and a slot body whose VALUES change reads them through signals (a bound <c>Prop</c>, a
    /// component's <c>UseComputed</c>) — the frame does not re-render for a body that changed behind an equal slot set.
    /// Builders run on every frame render (and the skeleton calls <see cref="Satellites"/> to count them), so they build
    /// elements and nothing else: no hooks, no signal writes, keyed components for anything stateful.</para>
    /// <para>The six podcast slots (plan §5.4) are APPENDED: <see cref="Badges"/> 4096 · <see cref="Rating"/> 8192 ·
    /// <see cref="Ledger"/> 16384 · <see cref="Primary"/> 32768 · <see cref="Satellites"/> 65536 · <see cref="Topics"/>
    /// 131072 — the twelve older bits keep their values.</para></summary>
    public sealed record FrameSlots
    {
        /// <summary>Custom cover at an edge (liked dynamic, editable playlist).</summary>
        public Func<float, Element>? Cover { get; init; }
        /// <summary>The 96-DIP strip's cover (liked: no picker).</summary>
        public Func<float, Element>? CompactCover { get; init; }
        /// <summary>Inline-edit title (size, lineHeight).</summary>
        public Func<float, float, Element>? Title { get; init; }
        /// <summary>Owner block / collaborator pile / billed artists at a width.</summary>
        public Func<float, Element>? Attribution { get; init; }
        /// <summary>Inline-edit description at a width.</summary>
        public Func<float, Element>? Description { get; init; }
        /// <summary>The daylist countdown row (reserved 28).</summary>
        public Func<Element>? Pulse { get; init; }
        /// <summary>The chart caption (reserved 16, W28).</summary>
        public Func<Element>? Chart { get; init; }
        /// <summary>The prerelease countdown card.</summary>
        public Func<Element>? PreRelease { get; init; }
        /// <summary>About this release (outerPadding).</summary>
        public Func<bool, Element>? ReleasePanel { get; init; }
        /// <summary>The liked facts bento (rail).</summary>
        public Func<float, Element>? LikedFacts { get; init; }
        /// <summary>The album trailing body (HasTrailing).</summary>
        public Func<Element>? Trailing { get; init; }
        /// <summary>The show's episode list (Content == Episodes), called with the frame's VERTICAL flag (ch 09 W4: no toolbar in the vertical arm).</summary>
        public Func<bool, Element>? Episodes { get; init; }

        // ── the podcast seams (plan §5.4) — appended; the WIDTH a Func<float, …> slot below receives is the rail's content
        //    measure (the cover edge, exact) in the two-column rail, and NaN (no measure: fill the column) in the vertical
        //    show header — treat it as a Width / MaxWidth only when it is finite. (Attribution keeps its old contract: the
        //    cover edge in the rail, the page-estimate's info column in the vertical header.) The frame re-invokes these builders only when it
        //    re-renders for its own reasons, so a body whose content changes — a label, a count, a satellite that comes and
        //    goes — is a keyed component or binds its values / its Visible; the Satellites ARRAY is fixed per slot set.

        /// <summary>Badge chips (<c>Controls.Chip</c> × n): on the eyebrow's line for a show ("Podcast [exclusive] [E]"),
        /// under the meta line for an episode. One line of 16 is reserved (<see cref="RailLayout.BadgeHeight"/>).</summary>
        public Func<Element>? Badges { get; init; }
        /// <summary>The rating row, under the title / attribution (a show's ★ 4.8 · 12,431 ratings). Reserved 20.</summary>
        public Func<float, Element>? Rating { get; init; }
        /// <summary>The played ledger (<c>Controls.LedgerBar</c> + its counts line), under the meta line. Reserved 26.</summary>
        public Func<float, Element>? Ledger { get; init; }
        /// <summary>The CTA's primary, called with the page ACCENT thunk; REPLACES the Play button when present (Follow,
        /// Resume · 17 min left, Play latest, Play preview — build it with <c>Controls.PlayButton</c>'s label/glyph).</summary>
        public Func<Func<ColorF>, Element>? Primary { get; init; }
        /// <summary>The CTA's satellites, KEYED; REPLACES the fixed [heart][Share][⋯] group when present, and they wrap one
        /// by one after the primary, 8 apart (<c>ClassicDetailKit.RailSatellite</c>, <c>RailSatelliteSave</c>,
        /// <c>RailSatelliteMore</c> build them at <see cref="RailLayout.SatelliteSize"/>).</summary>
        public Func<Element[]>? Satellites { get; init; }
        /// <summary>The topic words (<c>Controls.Words.Links</c>), under the CTA. Two lines reserved; the rail only (the
        /// vertical header has no topics).</summary>
        public Func<float, Element>? Topics { get; init; }
        /// <summary>The rail's SHORT about block — a small label over the description clamped to a few lines, with the
        /// inline overflow affordance the clamp itself decides on (an episode's; podcast-episode-peek §1). Natural
        /// height, arrives with the description, and the full text / chapters / links stay in the page body. Rail only
        /// (the vertical header puts its body under the rows, where the About section already is).</summary>
        public Func<float, Element>? About { get; init; }
        /// <summary>The page's right ASIDE body (contract 1.21): mounted only where the window tier's record docks an aside
        /// (<c>DetailTierLayout.AsideDocked</c>) in the two-column arm. What it holds must leave the rail, the band under the rows and the
        /// list's tail while it is docked: those read <see cref="AsideDocked"/>. The album's release facts, artist, fans and "More by"; a
        /// playlist's recommended songs.</summary>
        public Func<Element>? Aside { get; init; }

        internal int Mask()
            => (Cover is null ? 0 : 1) | (CompactCover is null ? 0 : 2) | (Title is null ? 0 : 4)
             | (Attribution is null ? 0 : 8) | (Description is null ? 0 : 16) | (Pulse is null ? 0 : 32)
             | (Chart is null ? 0 : 64) | (PreRelease is null ? 0 : 128) | (ReleasePanel is null ? 0 : 256)
             | (LikedFacts is null ? 0 : 512) | (Trailing is null ? 0 : 1024) | (Episodes is null ? 0 : 2048)
             | (Badges is null ? 0 : 4096) | (Rating is null ? 0 : 8192) | (Ledger is null ? 0 : 16384)
             | (Primary is null ? 0 : 32768) | (Satellites is null ? 0 : 65536) | (Topics is null ? 0 : 131072)
             | (About is null ? 0 : 262144) | (Aside is null ? 0 : 524288);

        public bool Equals(FrameSlots? o) => o is not null && Mask() == o.Mask();
        public override int GetHashCode() => Mask();
    }

    /// <summary>True under a frame whose aside is docked (<see cref="FrameSlots.Aside"/>, contract 1.21): what the aside now holds leaves the
    /// places it lives otherwise (the rail's release row, the album band's About / Fans / "More by", the playlist list's recommendations), so
    /// nothing is shown twice. Provided by <c>FrameHost</c> around the frame view; false everywhere else.</summary>
    public static readonly Context<bool> AsideDocked = new(false);

    /// <summary>Everything a page hands the frame. Equality gates on DATA: the identity by value, the config, the table
    /// source by (type, context, version, count, total, state), the profile by its own data equality, the verbs and slots
    /// by presence, the route key.</summary>
    public sealed record FrameSpec
    {
        public required Identity Identity { get; init; }
        public required Config Config { get; init; }
        /// <summary>Required when <c>Config.Content == Tracks</c>.</summary>
        public Track.TableSource? Source { get; init; }
        /// <summary>Null ⇒ <c>Track.TableProfile.From(Config)</c>.</summary>
        public Track.TableProfile? Profile { get; init; }
        public FrameActions Actions { get; init; } = new();
        public FrameSlots Slots { get; init; } = new();
        /// <summary><c>Shell.NameOf(route)</c>: keys the tone/tint leaves and the list.</summary>
        public string RouteKey { get; init; } = "";

        public bool Equals(FrameSpec? o)
        {
            if (ReferenceEquals(this, o)) return true;
            if (o is null) return false;
            return Identity.Equals(o.Identity) && Config == o.Config && SourceEquals(Source, o.Source)
                && Equals(Profile, o.Profile) && Actions.Equals(o.Actions) && Slots.Equals(o.Slots)
                && string.Equals(RouteKey, o.RouteKey, StringComparison.Ordinal);
        }

        public override int GetHashCode() => HashCode.Combine(Identity, Config, RouteKey);

        static bool SourceEquals(Track.TableSource? a, Track.TableSource? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null || a.GetType() != b.GetType()) return false;
            return a.Context.Equals(b.Context) && a.Version == b.Version && a.Count == b.Count
                && a.Total == b.Total && a.State == b.State;
        }
    }

    // ══ 3. THE FRAME ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>THE detail scaffold (ch 03 §1). Resolves the 4-mode ladder (the structure's, through UseTier), the per-scope rail prefs +
    /// grip + collapse, the notice strip, the tone plane, the shell tint, the page drop target and the accent; mounts
    /// <c>Track.Table</c> for tracks.</summary>
    public static Element Frame(FrameSpec spec)
        => Embed.Comp(spec, static () => new FrameHost()) with { Key = "detail:" + spec.Identity.Subject.Text };

    // Frame arithmetic 0.2.9 kept inside DetailShell / DetailRail (not in the CORE contract; see the report).
    const float RailMidW = RailPolicy.MidRestWidth, RailNarrowW = RailPolicy.NarrowRestWidth;  // RailW(mode 1 / mode 2)
    const float RailForcePush = 44f, RailReExpand = 220f;            // the collapse detent
    const float RailCompactW = RailPolicy.CompactStripW;               // the collapsed identity strip
    const float GripStripCollapsedW = RailPolicy.CollapsedGripW;       // the grip while collapsed (also a re-open gesture)
    const float TwoColumnHeroBandFraction = 0.55f;                    // the tone plane's synthetic band (hero-only is dead)

    /// <summary><c>DetailShell.RailW</c>: the breakpoint rail of modes 1/2, the config's own width at mode 0. The
    /// NON-RESIZABLE arm only (<c>RailResizable: false</c>) — a resizable rail rests through
    /// <see cref="RailPolicy.RestingWidth"/>, which carries the same two breakpoint widths.</summary>
    static float RailWidthForMode(int mode, in Config cfg) => mode switch { 0 => cfg.RailWidth, 1 => RailMidW, _ => RailNarrowW };


    /// <summary>One persisted rail pair (width + collapsed), live. TWO widths, deliberately: <see cref="Width"/> is the
    /// LIVE column (what the row lays out and the grip drags — always inside the page-aware bounds), <c>_stored</c> is
    /// the REMEMBERED one (what the store holds). A page too narrow for the remembered width only holds it back
    /// (<see cref="Rest"/>); widening the window restores it, because the clamp is never written back. RELEASE commits
    /// both to THIS scope's keys with ONE epoch bump.</summary>
    sealed class RailCell
    {
        public readonly RailScope Scope;
        public readonly Signal<float> Width;
        public readonly Signal<bool> Collapsed;
        public readonly Action Commit;
        public readonly Action Expand;
        public readonly string GripKey;
        readonly Signal<float> _fade;
        Splitter.SplitterOptions _options;
        float _stored;                                  // the REMEMBERED width — a page-width clamp never lowers it
        float _max = RailPolicy.MaxWidth;
        int _mode = RailPolicy.WideMode;
        float _wideRest;                                // the window tier's resting width of an undragged wide arm (0: the kind's own)

        public RailCell(RailScope scope, Signal<float> fade)
        {
            Scope = scope;
            _fade = fade;
            _stored = DetailRailPrefs.StoredWidth(scope);
            Width = new Signal<float>(RailPolicy.RestingWidth(_stored, scope, RailPolicy.WideMode, RailPolicy.MaxWidth));
            Collapsed = new Signal<bool>(DetailRailPrefs.Collapsed(scope));
            Commit = CommitNow;
            Expand = ExpandNow;
            _options = new Splitter.SplitterOptions
            {
                Min = RailPolicy.MinWidthFor(scope), Max = RailPolicy.MaxWidth,
                ForcePush = RailForcePush, ReExpand = RailReExpand,
            };
            GripKey = scope switch
            {
                RailScope.Album => "detail-rail-grip:album",
                RailScope.Playlist => "detail-rail-grip:playlist",
                RailScope.Liked => "detail-rail-grip:liked",
                RailScope.Show => "detail-rail-grip:show",
                _ => "detail-rail-grip:uniform",
            };
        }

        /// <summary>The seam's knobs for the page's CURRENT maximum. One record per distinct max (never per render), so
        /// the re-pushed <c>Splitter.Props</c> only differs when the cap really moved.</summary>
        public Splitter.SplitterOptions OptionsFor(float max)
        {
            if (max != _options.Max) _options = _options with { Max = max };
            return _options;
        }

        /// <summary>The page reported a new (mode, maximum): re-derive the LIVE width from the REMEMBERED one. Value-gated,
        /// so a resize that changes nothing costs no render. Runs from an effect, never from a render.</summary>
        public void Rest(int mode, float max, float wideRest)
        {
            _mode = mode;
            _max = max;
            _wideRest = wideRest;
            float w = RailPolicy.RestingWidth(_stored, Scope, mode, max, wideRest);
            if (w != Width.Peek()) Width.Value = w;
        }

        /// <summary>The width the frame COMPOSES this render for (mode, max): what <see cref="Rest"/> will write until it
        /// has run for exactly that pair, the live (dragged) width after (<see cref="RailPolicy.ComposedWidth"/>). Reads
        /// <see cref="Width"/> unconditionally, so a drag always re-renders. Pure read — a render never writes the cell.</summary>
        public float ComposedWidth(int mode, float max, float wideRest)
        {
            float live = Width.Value;
            return RailPolicy.ComposedWidth(live, _stored, Scope, _mode, _max, mode, max, _wideRest, wideRest);
        }

        void CommitNow()
        {
            // A drag that merely parked against a page-imposed cap keeps the wider remembered width.
            _stored = RailPolicy.CommitWidth(Width.Peek(), _stored, _max);
            // The ONE bump (inside the pref): every mounted (and parked) frame re-syncs its rails, so "Keep left-rail same size" moves them all.
            DetailRailPrefs.Commit(Scope, _stored, Collapsed.Peek());
        }

        void ExpandNow()
        {
            Collapsed.Value = false;
            _fade.Value = 1f;
            DetailRailPrefs.SetCollapsed(Scope, false);
        }

        /// <summary>Re-seed from the store, then re-rest at the page's live bounds (the epoch effect, ch 03 §6).</summary>
        public void Resync()
        {
            _stored = DetailRailPrefs.StoredWidth(Scope);
            Rest(_mode, _max, _wideRest);
            Collapsed.Value = DetailRailPrefs.Collapsed(Scope);
        }
    }

    sealed class FrameHost : Component, IPropsHost
    {
        // ── the spec: newest pushed (delegates) + the data gate (renders) ──
        FrameSpec? _latest;
        readonly Signal<FrameSpec?> _spec = new(null);

        // ── the page mode: the structure's mode ladder over the page's measured width (UseTier: a first mount resolves in its own frame from
        //    the seed, a crossing after it commits on the first non-resized frame and animates, themes plan 3.3), and the height rung ──
        readonly Signal<float> _pageW = new(0f);
        IReadSignal<int>? _modeTier;
        IReadSignal<int>? _heightRung;
        readonly Func<float> _pageSeed;
        float _measuredW, _measuredH;

        // ── rails, fade, hero height, accent, play cell, tint owner ──
        readonly RailCell[] _rails;
        readonly Signal<float> _railFade = new(1f);
        /// <summary>The PAGE-AWARE rail maximum, quantized to 8 DIP by <see cref="RailPolicy.MaxWidthForPage"/> and
        /// value-gated in <see cref="Measure"/> — the wide arm never writes it (its answer is always 480), so only the
        /// narrow arms re-render as the window resizes.</summary>
        readonly Signal<float> _railMax = new(RailPolicy.MaxWidth);
        readonly Signal<float> _heroHeight = new(0f);
        readonly Signal<ColorF> _accent = new(ColorF.Transparent);
        readonly Signal<Design.PageAccent> _pageAccent = new(new Design.PageAccent(Tok.AccentTextPrimary, Tok.AccentDefault, ""));
        readonly Action?[] _playAllCell = new Action?[1];
        readonly object _tintOwner = new();
        IReadSignal<Size2>? _viewport;
        bool _accentSeeded;

        // ── INSIGHTS SHEET (additive; Detail.Insights.cs) ── the host of the facts bento on every facts page (I1). One toggle
        //    per mounted frame: the open state is a signal, so the toolbar button and the pane cannot disagree and
        //    neither costs the page a render. `_sheetRoute` closes it on any route swap the host survives.
        readonly InsightsToggle _insights = new();
        readonly Action _returnInsightsFocus;
        readonly Action _syncInsightsArm;
        readonly Action _restInsights;
        bool _restOpenNow;
        InputHooks? _inputHooks;
        string _sheetRoute = "\0";
        bool _sheetHosts;
        //    The FACTS LATCH (InsightsSheet.FactsSettled): the page's bento slot is derived from a scan of the live row
        //    source, which reads empty while the list's open holds its reveal — so an absent slot is "not answered yet",
        //    not "no facts". Latched per ROUTE and dropped with it, beside the open state.
        Func<float, Element>? _factsLatched;
        // What the list shows against what it has: the table publishes it, the rail's count line reads it (tuned Classic D4).
        readonly Signal<ListCount> _listCount = new(default);

        // ── cached delegates (zero per-render allocation for the handlers the tree carries) ──
        readonly Action<RectF> _measure;
        readonly Func<ColorF> _accentFn;
        readonly Action _playAll;
        readonly Action _resyncRails;
        readonly Action _restRails;
        readonly Action _publishAccentTracked;
        readonly Action _republishAccent;
        readonly Func<bool> _railPending;

        // trampolines into _latest
        readonly Action _tShuffle, _tCoverClick, _tGoLibrary;
        readonly Func<ContextMenuModel?> _tMore;
        readonly Func<object?> _tCoverDrag;
        readonly Func<DragPayload, int?, bool> _tDeposit;
        readonly Func<float, Element> _sCover, _sCompactCover, _sAttribution, _sDescription,
                                      _sRating, _sLedger, _sTopics;
        readonly Func<float, float, Element> _sTitle;
        readonly Func<Element> _sPulse, _sChart, _sPreRelease, _sTrailing, _sTrailingVertical, _sBadges;
        readonly Func<bool, Element> _sReleasePanel, _sEpisodes;
        readonly Func<Func<ColorF>, Element> _sPrimary;
        readonly Func<Element[]> _sSatellites;
        readonly Func<Element> _sAside;
        FrameActions? _trampActions;
        int _trampActionsMask = -1;
        FrameSlots? _trampSlots;
        int _trampSlotsMask = -1;

        // the page-body drop target (built once; its callbacks read _latest)
        DropTargetSpec? _dropTarget;
        string _captionFor = "\0";
        string _captionText = "";
        EntityUri _subjectFor;
        string _subjectText = "";

        // the profile fallback, cached per config
        Track.TableProfile? _profileFallback;
        Config _profileFor;

        public FrameHost()
        {
            _rails = new RailCell[5];
            for (int i = 0; i < _rails.Length; i++) _rails[i] = new RailCell((RailScope)i, _railFade);

            _measure = Measure;
            _accentFn = () => _accent.Value;
            _playAll = PlayAll;
            _resyncRails = ResyncRails;
            _restRails = RestRails;
            _publishAccentTracked = PublishAccentTracked;
            _republishAccent = RepublishAccent;
            _pageSeed = PageSeed;
            _railPending = () => _spec.Value?.Identity.HeaderPending ?? true;

            _tShuffle = () => _latest?.Actions.Shuffle?.Invoke();
            _tCoverClick = () => _latest?.Actions.CoverClick?.Invoke();
            _tGoLibrary = () => _latest?.Actions.GoLibrary?.Invoke();
            _tMore = () => _latest?.Actions.More?.Invoke();
            _tCoverDrag = () => _latest?.Actions.CoverDrag?.Invoke();
            _tDeposit = (p, i) => _latest?.Actions.DepositOnPage?.Invoke(p, i) ?? false;

            _sCover = e => _latest?.Slots.Cover?.Invoke(e) ?? new BoxEl();
            _sCompactCover = e => _latest?.Slots.CompactCover?.Invoke(e) ?? new BoxEl();
            _sAttribution = w => _latest?.Slots.Attribution?.Invoke(w) ?? new BoxEl();
            _sDescription = w => _latest?.Slots.Description?.Invoke(w) ?? new BoxEl();
            _sTitle = (s, lh) => _latest?.Slots.Title?.Invoke(s, lh) ?? new BoxEl();
            _sPulse = () => _latest?.Slots.Pulse?.Invoke() ?? new BoxEl();
            _sChart = () => _latest?.Slots.Chart?.Invoke() ?? new BoxEl();
            _sPreRelease = () => _latest?.Slots.PreRelease?.Invoke() ?? new BoxEl();
            _sTrailing = () => _latest?.Slots.Trailing?.Invoke() ?? new BoxEl();
            _sEpisodes = vertical => _latest?.Slots.Episodes?.Invoke(vertical) ?? new BoxEl();
            _sReleasePanel = outer => _latest?.Slots.ReleasePanel?.Invoke(outer) ?? new BoxEl();
            _sTrailingVertical = VerticalTrailing;
            _sBadges = () => _latest?.Slots.Badges?.Invoke() ?? new BoxEl();
            _sRating = w => _latest?.Slots.Rating?.Invoke(w) ?? new BoxEl();
            _sLedger = w => _latest?.Slots.Ledger?.Invoke(w) ?? new BoxEl();
            _sTopics = w => _latest?.Slots.Topics?.Invoke(w) ?? new BoxEl();
            _sPrimary = a => _latest?.Slots.Primary?.Invoke(a) ?? new BoxEl();
            _sSatellites = () => _latest?.Slots.Satellites?.Invoke() ?? Array.Empty<Element>();
            _sAside = () => _latest?.Slots.Aside?.Invoke() ?? new BoxEl();

            // ── INSIGHTS SHEET (additive) ──
            _returnInsightsFocus = ReturnInsightsFocus;
            _syncInsightsArm = () => { if (!_sheetHosts) _insights.Close(); };
            _restInsights = () => _insights.Rest(_restOpenNow);
        }

        /// <summary>The insights pane's resting state: open only where the tier's record rests it open (<c>DetailTierLayout.InsightsRestOpen</c>), on a
        /// page that hosts facts, outside the vertical arm (where it is a sheet over the page, which never opens by itself: tuned Classic I1).</summary>
        static bool RestsOpen(DetailTierLayout layout, DetailPageLayout pageLayout, bool hostsFacts, bool vertical)
            => layout.InsightsRestOpen && hostsFacts && !vertical && pageLayout != DetailPageLayout.Hero;

        /// <summary>── INSIGHTS SHEET (additive) ── Focus returns to the toolbar toggle when the sheet closes. The
        /// control has already popped its focus scope and restored whatever held focus when the sheet opened; this is
        /// the belt-and-braces leg for the case where that node is gone (the hero re-realized while the sheet was
        /// open), so the user is never dropped back at the top of the page.</summary>
        void ReturnInsightsFocus()
        {
            var node = _insights.ButtonNode[0];
            if (node.IsNull || _inputHooks is not { } hooks) return;
            var scene = Context.Scene;
            if (scene is null || !scene.IsLive(node)) return;
            hooks.FocusNode?.Invoke(node, false);
        }

        /// <summary>Every re-push lands here (the reconciler calls it at mount and on every parent render).</summary>
        public void ApplyProps(object props)
        {
            var spec = (FrameSpec)props;
            _latest = spec;
            // ── INSIGHTS SHEET (additive) ── the sheet never survives a route change. The frame is keyed by subject, so
            //    a different subject remounts this host outright; a SAME-subject route swap reuses it, and this is the
            //    edge that closes the sheet there.
            if (!string.Equals(_sheetRoute, spec.RouteKey, StringComparison.Ordinal))
            {
                _sheetRoute = spec.RouteKey;
                _insights.Reset();
                _factsLatched = null;        // the facts latch is the ROUTE's, exactly like the open state
                // The tier's RESTING state from the route's first frame (a Huge dock or aside is open before it is laid out, so it never
                // eases in under the reader); later crossings and the facts latch re-rest it in `_restInsights`.
                _insights.Rest(RestsOpen(ShellPolicy.Now.DetailTiers.For(ThemeHost.Facts.Peek().Tiers), ShellPolicy.Now.Detail.PageLayout,
                    spec.Slots.LikedFacts is not null && spec.Config.Content == DetailContent.Tracks, vertical: false));
            }
            if (!_accentSeeded)
            {
                // The first paint already carries a cached grading's accent instead of the default for one frame.
                _accentSeeded = true;
                PublishAccent(spec);
                _accentEpoch = Tok.Epoch;
            }
            _spec.Value = spec;
        }

        public override Element Render()
        {
            _ = _spec.Value;                                    // the data gate
            var spec = _latest!;                                // …and the newest delegates
            var id = spec.Identity;
            var cfg = spec.Config;

            // ── the structure's decision (the page layout, the rails' uniformity: a tracked read of the published policy) and the rail epoch
            //    (each write to a rail's remembered size subscribes it, then re-reads the store) ──
            var detailPolicy = ShellPolicy.Tracked.Detail;
            DetailPageLayout pageLayout = detailPolicy.PageLayout;
            bool uniform = detailPolicy.RailUniform;
            int railEpoch = DetailRailPrefs.Epoch.Value;
            UseEffect(_resyncRails, DepKey.From(railEpoch));
            // Auto-tracked on the mode tier, _railMax and the window tier: a breakpoint cross or a narrower page re-rests every rail from its
            // REMEMBERED width. A signal write belongs in an effect, never in a render (rule 6).
            UseEffect(_restRails);

            var shellSlot = UseContext(ShellMaterial.Slot);
            _inputHooks = UseContext(InputHooks.Current);       // ── INSIGHTS SHEET (additive): the focus return ──
            var viewportSig = UseContextSignal(Viewport.Size);
            _viewport = viewportSig;
            // The structure's sub-ladders (themes plan 3.6; Classic: Tiers.Detail.cs). They freeze at mount like every hook's inputs: a theme
            // switch mounts a new root, and with it a new frame.
            var tierPolicy = ShellPolicy.Now.Tiers;
            _modeTier = UseTier(DetailLadders.Of(tierPolicy, DetailLadders.Mode), TierSource.Measured(_pageW, _pageSeed));
            _heightRung = UseTier(DetailLadders.Of(tierPolicy, DetailLadders.Height), TierSource.ViewportHeight);
            // The window tier's layout (tracked: a crossing re-renders the frame on the frame it commits).
            var tierLayouts = ShellPolicy.Tracked.DetailTiers;
            var tierFacts = ThemeHost.Facts.Value.Tiers;
            UseEffect(_publishAccentTracked);                                   // grading / spec
            UseEffect(_republishAccent, DepKey.From(Tok.Epoch));                // a theme flip or a definition swap re-derives the accent (X44)

            string? paletteUrl = id.PaletteUrl ?? id.CoverUrl;
            string routeKey = spec.RouteKey;
            RefreshCaches(id);

            // ── the mode: the committed tier of the mode ladder. Seeded before the first measure from the content region the frame resolved the
            //    window tiers on (synchronous), so the first frame is already in its final arm; a crossing after it lands on the first non-resized
            //    frame (the resized frame lays the old arm out at the new size, clipped) and the arms cross-fade (the structure's frame view).
            float pageWidthEstimate = Breakpoints.EstimatePageWidth(in tierFacts, viewportSig.Peek().Width);
            int mode = DetailLadders.ModeOf(_modeTier.Value);
            // The Hero page layout forces the vertical SYSTEM for track pages only; the mode tier keeps tracking the real width.
            if (cfg.Content == DetailContent.Tracks && pageLayout == DetailPageLayout.Hero)
                mode = Breakpoints.VerticalMode;
            bool vertical = mode == Breakpoints.VerticalMode;
            bool verticalTracks = vertical && cfg.Content == DetailContent.Tracks;

            // ── INSIGHTS SHEET (additive; Detail.Insights.cs) ── tuned Classic I1: the facts bento is a sheet over the list on every
            //    facts page, in every arm, toggled from the list bar — never a rail row, never appended to the page body.
            //
            //    The slot is LATCHED for the route first (InsightsSheet.FactsSettled): a page derives its bento slot
            //    from a scan of the live row source, which reads empty while the list's open holds its reveal, so an
            //    absent slot on a page that plainly has facts means "not answered yet", not "no facts" — and a toggle
            //    that appears only if that scan happens to have run at the right moment is the bug this cures. A page
            //    that has never offered facts still shows nothing and hints at nothing.
            bool factsNow = spec.Slots.LikedFacts is not null;
            bool factsSettled = InsightsSheet.FactsSettled(_factsLatched is not null, factsNow);
            if (factsNow) _factsLatched = spec.Slots.LikedFacts;      // a plain field, not a signal: no render write
            _sheetHosts = InsightsSheet.ShowsToggle(id.Kind, factsSettled, cfg.Content);
            // Losing the facts CLOSES the sheet — a signal write belongs in an effect, never in a render (rule 6).
            UseEffect(_syncInsightsArm, DepKey.From(_sheetHosts ? 1 : 0));
            // …and the tier's RESTING state (contract 1.21): open where the pane is a column of the page (the Huge dock, the aside) until the user
            // moves it, closed again when a crossing leaves that tier. Re-run on a crossing (the tracked reads above re-render this host) and when
            // the facts latch settles.
            var tierLayout = tierLayouts.For(tierFacts);
            _restOpenNow = RestsOpen(tierLayout, pageLayout, _sheetHosts && cfg.Content == DetailContent.Tracks, vertical);
            UseEffect(_restInsights, DepKey.From(_restOpenNow ? 1 : 0));

            // ── the leaves: shell tint (a hand-over, never a clear) and the page tone plane (the ONE ground) ──
            Element tint = Palette.ShellTint(paletteUrl, ready: true, apply: cfg.TwoColumn,
                owner: _tintOwner, slot: shellSlot, key: "detail-tint:" + routeKey, payloadAccent: id.CardAccent);
            float pageH = _measuredH > 1f ? _measuredH : viewportSig.Peek().Height;
            // heroOnly is dead (the setting is gone): the band/height props feed only that arm, so they are PEEKED — a
            // hero measurement must not re-render the page for a value nothing paints.
            float heroBand = verticalTracks ? _heroHeight.Peek() : pageH * TwoColumnHeroBandFraction;
            Element tone = Palette.PageTonePlane(paletteUrl, fallbackUrl: null, backdropBand: heroBand,
                pageHeight: pageH, heroOnly: false,
                key: Palette.ToneKey(routeKey),
                payloadAccent: id.CardAccent);

            // ── the notice strip: about the PAGE, read before the content it qualifies; never an error page ──
            Element notice = NoticeBar(id.Notice, showMinifiedAlbum: false,
                goLibrary: spec.Actions.GoLibrary is null ? null : _tGoLibrary);

            // ── the content column: the table (or the episodes slot); the structure's frame view places it under its key ──
            float rightMin = Breakpoints.ContentMinWidthForMode(mode);
            bool episodes = cfg.Content == DetailContent.Episodes;
            Element content = episodes
                ? (spec.Slots.Episodes is null ? new BoxEl { Grow = 1f } : _sEpisodes(vertical)) with { Key = "episodes:" + routeKey }
                : TableFor(spec, vertical, verticalTracks, ListSeed(cfg, vertical, mode, uniform, pageWidthEstimate, tierLayouts.For(tierFacts).RailRest), routeKey);
            string contentKey = episodes ? "right:eps" : "right:tracks";

            var acts = TrampolineActions(spec.Actions);
            DropTargetSpec? drop = PageDropTarget(cfg);

            // ── mode 3: the vertical / hero system ──
            if (vertical)
            {
                return Ctx.Provide(AsideDocked, false, ViewSlots.View(Contracts.ViewKeys.DetailFrame, new Contracts.DetailFrameProps
                {
                    Vertical = true, VerticalTracks = verticalTracks, PageAccent = _pageAccent, OnMeasured = _measure,
                    Tint = tint, Tone = tone, Notice = notice,
                    ContentKey = contentKey, Content = content, ContentMinWidth = rightMin, Drop = drop,
                    // A show in the vertical arm is the fixed ShowHeader above its episodes — never the hero system.
                    ShowHeader = verticalTracks ? null : new Contracts.DetailShowHeaderProps
                    {
                        Spec = spec, Actions = acts, Accent = _accentFn, Play = _playAll, PageWidth = pageWidthEstimate,
                    },
                    // ── INSIGHTS SHEET (additive) ── a TRACK page's vertical arm ALWAYS composes the overlay, open or not
                    //    and facts or not, so the page's tree shape never changes underneath the list: the facts arriving
                    //    (or the sheet opening) must not remount the table and lose its scroll position. An episode list's
                    //    vertical arm is left exactly as it was. The LATCHED builder, not the live slot: the toggle and the
                    //    pane must never disagree, and the builder closes over the page's own live source, so a latched
                    //    one is never stale data.
                    Sheet = verticalTracks ? SheetProps(routeKey) : null,
                }));
            }

            // ── modes 0/1/2 of a page with NO rail (Liked Songs, tuned Classic D16): the page head over the content column ──
            if (!cfg.Rail)
            {
                return Ctx.Provide(AsideDocked, false, ViewSlots.View(Contracts.ViewKeys.DetailFrame, new Contracts.DetailFrameProps
                {
                    Vertical = false, VerticalTracks = false, PageAccent = _pageAccent, OnMeasured = _measure,
                    Tint = tint, Tone = tone, Notice = notice,
                    ContentKey = contentKey, Content = content, ContentMinWidth = rightMin, Drop = drop,
                    Head = new Contracts.DetailHeadProps { Spec = spec, Shown = _listCount },
                    Sheet = episodes ? null : SheetProps(routeKey),
                }));
            }

            // ── modes 0/1/2: the centred two-column row [rail | grip | right] ──
            var rail =_rails[(int)RailPolicy.ScopeFor(cfg.RailScope, uniform)];
            bool resizable = RailPolicy.ResizableFor(cfg.RailResizable, mode);
            // Read unconditionally (a stable subscription) but honoured only where the grip that can undo it exists.
            bool collapsed = rail.Collapsed.Value && resizable;
            // The page-aware cap: the rail may never squeeze the content column below its 300-DIP floor. Before the first
            // page measure it is the viewport ESTIMATE's cap (the same estimate the mode seed above reads), not the absolute
            // ceiling. The width is the one the rail will REST at for this (mode, cap) until the re-resting effect has run
            // (RailPolicy.ComposedWidth): composing the live width instead painted the pre-measure frame(s) at the WIDE
            // arm's width and snapped the rail narrower a frame or two later — the title re-wrapping under it (the
            // "full title for 1-2 frames" flicker, 2026-09-30).
            float railMaxLive = _railMax.Value;
            float railMax = _measuredW > 0f ? railMaxLive : RailPolicy.MaxWidthForPage(pageWidthEstimate, mode);
            float wideRest = tierLayouts.For(tierFacts).RailRest;
            float railW = resizable ? rail.ComposedWidth(mode, railMax, wideRest) : RailWidthForMode(mode, cfg);

            Contracts.DetailTwoColumnProps twoColumn;
            if (collapsed)
            {
                twoColumn = new Contracts.DetailTwoColumnProps
                {
                    Collapsed = true, Resizable = resizable, RailWidth = railW,
                    CompactWidth = RailCompactW, CollapsedGripWidth = GripStripCollapsedW,
                    Grip = GripControl(rail, railMax), RailOpacity = _railFade,
                    CompactRail = new Contracts.DetailCompactRailProps
                    {
                        Spec = spec, StripWidth = RailCompactW, Expand = rail.Expand, Pending = _railPending,
                    },
                };
            }
            else
            {
                int rung = _heightRung.Value;                                     // re-renders only on a rung cross
                // The window rung is the title's size CAP (the structure's, per rung); the size drawn and its natural line box are the title
                // rule's (VerticalLayout.RailTitleTypeFor), never an authored pair.
                float titleSize = tierLayouts.RailTitleFor(rung);
                int descLines = tierLayouts.DescriptionFor(rung);
                twoColumn = new Contracts.DetailTwoColumnProps
                {
                    Collapsed = false, Resizable = resizable, RailWidth = railW,
                    CompactWidth = RailCompactW, CollapsedGripWidth = GripStripCollapsedW,
                    Grip = resizable ? GripControl(rail, railMax) : null, RailOpacity = _railFade,
                    Rail = new Contracts.DetailRailProps
                    {
                        Spec = spec, Actions = acts, RailWidth = railW, TitleSize = titleSize, DescriptionLines = descLines,
                        Accent = _accentFn, Play = _playAll, Pending = _railPending, Shown = _listCount,
                    },
                };
            }
            // ── the ASIDE (contract 1.21): docked where the window tier's record docks one (Classic Huge, C-Album-Huge / C-Playlist-Huge) and the page has
            //    an aside body or facts to show in it. Everything it holds leaves the rail, the band and the list's tail under `AsideDocked`. ──
            bool asideOn = tierLayout.AsideDocked && !episodes && (spec.Slots.Aside is not null || _sheetHosts);
            Contracts.DetailAsideProps? aside = asideOn
                ? new Contracts.DetailAsideProps { Kind = id.Kind, Body = spec.Slots.Aside is null ? null : _sAside(), RouteKey = routeKey }
                : null;
            return Ctx.Provide(AsideDocked, asideOn, ViewSlots.View(Contracts.ViewKeys.DetailFrame, new Contracts.DetailFrameProps
            {
                Vertical = false, VerticalTracks = false, PageAccent = _pageAccent, OnMeasured = _measure,
                Tint = tint, Tone = tone, Notice = notice,
                ContentKey = contentKey, Content = content, ContentMinWidth = rightMin, Drop = drop,
                TwoColumn = twoColumn,
                // ── INSIGHTS SHEET ── a TRACK page's two-column arm lays the sheet over its list column (I1), composed open or not; with an aside the
                //    aside shows the insights instead.
                Sheet = episodes ? null : SheetProps(routeKey),
                Aside = aside,
            }));
        }

        /// <summary>The sheet's props for this render: the LATCHED facts builder (null while this page hosts no facts, so the pane renders
        /// nothing and the toggle can never open it).</summary>
        Contracts.DetailInsightsProps SheetProps(string routeKey) => new()
        {
            Toggle = _insights, Facts = _sheetHosts ? _factsLatched : null,
            PageWidth = _measuredW, RouteKey = routeKey, OnClosed = _returnInsightsFocus,
        };

        /// <summary>The engine splitter wired to the scope's cell: width writes are direct during the drag (bounded by the
        /// page-aware Max, so a drag can never squeeze the content column out); RELEASE commits width + collapsed to THIS
        /// scope's pair. The structure's frame view places it in the seam layer (it is a behaviour control, built by the host).</summary>
        Element GripControl(RailCell rail, float max)
            => Splitter.Create(rail.Width, rail.Commit, rail.OptionsFor(max), collapsed: rail.Collapsed, fade: _railFade)
                with { Key = rail.GripKey };

        // ── the table ──

        /// <summary>The list column's pre-measure width, the table tier's seed: the page estimate, less the rail the two-column arm will compose
        /// (its width for this mode and cap), so a launch into the wide arm seeds the list's own tier, not the page's.</summary>
        float ListSeed(in Config cfg, bool vertical, int mode, bool uniform, float pageWidthEstimate, float wideRest)
        {
            if (vertical || !cfg.Rail || pageWidthEstimate <= 0f) return pageWidthEstimate;
            var rail = _rails[(int)RailPolicy.ScopeFor(cfg.RailScope, uniform)];
            float max = RailPolicy.MaxWidthForPage(pageWidthEstimate, mode);
            float railW = RailPolicy.ResizableFor(cfg.RailResizable, mode) ? rail.ComposedWidth(mode, max, wideRest) : RailWidthForMode(mode, cfg);
            return MathF.Max(0f, MathF.Min(pageWidthEstimate, R.Space.PageMaxW) - railW);
        }

        Element TableFor(FrameSpec spec, bool vertical, bool verticalTracks, float widthSeed, string routeKey)
        {
            var cfg = spec.Config;
            if (spec.Source is not { } source)
                return new BoxEl { Key = "tracks:none:" + routeKey, Grow = 1f };   // a page that forgot its source

            Func<Element>? trailing = verticalTracks
                ? (cfg.HasTrailing || spec.Slots.PreRelease is not null || spec.Slots.ReleasePanel is not null
                    ? _sTrailingVertical : null)
                : (cfg.HasTrailing && spec.Slots.Trailing is not null ? _sTrailing : null);

            var args = new Track.TableArgs
            {
                Source = source,
                Profile = spec.Profile ?? ProfileFor(cfg),
                Accent = _accentFn,
                Vertical = verticalTracks
                    ? new VerticalSpec(spec.Identity, cfg, TrampolineActions(spec.Actions), TrampolineSlots(spec.Slots), _accentFn)
                    { PlayAll = _playAll, Insights = _sheetHosts ? _insights : null }
                    : null,
                ShowToolbar = cfg.Content == DetailContent.Tracks || !vertical,
                Insights = !verticalTracks && _sheetHosts ? _insights : null,
                ListCount = _listCount,
                Trailing = trailing,
                PlayAllCell = _playAllCell,
                HeroHeight = _heroHeight,
                WidthSeed = widthSeed,
                ScrollKey = routeKey,
            };
            // A route is a new scroll/hero identity; the arm is in the key (a hero-system list must never reconcile
            // against a standard one). The tier is NOT (ch 03 §9 traps).
            return Track.Table(args) with { Key = (verticalTracks ? "tracks:vertical:" : "tracks:standard:") + routeKey };
        }

        Track.TableProfile ProfileFor(in Config cfg)
        {
            if (_profileFallback is null || _profileFor != cfg)
            {
                _profileFor = cfg;
                _profileFallback = Track.TableProfile.From(cfg);
            }
            return _profileFallback;
        }

        /// <summary>The hero arm's body under the rows (W12): the prerelease card and About-this-release move BELOW the
        /// rows because the hero system has no rail — they are not dropped — then the album trailing. The slots are built
        /// here (the page's); the `Detail.VerticalTrailing` view places them in the rail's keyed rows.</summary>
        Element VerticalTrailing()
        {
            var spec = _latest;
            Element? preRelease = null, releasePanel = null, trailingBand = null;
            if (spec is not null)
            {
                if (spec.Identity.UpcomingAtUnixSeconds > 0 && spec.Slots.PreRelease is { } pre) preRelease = pre();
                if (spec.Config.Badges == BadgeStyle.TypeYear && spec.Slots.ReleasePanel is { } release) releasePanel = release(true);
                if (spec.Config.HasTrailing && spec.Slots.Trailing is { } trailing) trailingBand = trailing();
            }
            return ViewSlots.View(Contracts.ViewKeys.DetailVerticalTrailing, new Contracts.DetailVerticalTrailingProps(preRelease, releasePanel, trailingBand));
        }

        // ── trampolines ──

        FrameActions TrampolineActions(FrameActions a)
        {
            int mask = a.Mask();
            if (_trampActions is null || mask != _trampActionsMask)
            {
                _trampActionsMask = mask;
                _trampActions = new FrameActions
                {
                    Shuffle = a.Shuffle is null ? null : _tShuffle,
                    More = a.More is null ? null : _tMore,
                    CoverClick = a.CoverClick is null ? null : _tCoverClick,
                    CoverDrag = a.CoverDrag is null ? null : _tCoverDrag,
                    DepositOnPage = a.DepositOnPage is null ? null : _tDeposit,
                    GoLibrary = a.GoLibrary is null ? null : _tGoLibrary,
                };
            }
            return _trampActions;
        }

        /// <summary>The VERTICAL arm's view of the page's slots. <c>LikedFacts</c> is not carried: the bento is the insights sheet's (tuned
        /// Classic I1), which the frame hands the page's own builder.</summary>
        FrameSlots TrampolineSlots(FrameSlots s)
        {
            int mask = s.Mask();
            if (_trampSlots is null || mask != _trampSlotsMask)
            {
                _trampSlotsMask = mask;
                _trampSlots = new FrameSlots
                {
                    Cover = s.Cover is null ? null : _sCover,
                    CompactCover = s.CompactCover is null ? null : _sCompactCover,
                    Title = s.Title is null ? null : _sTitle,
                    Attribution = s.Attribution is null ? null : _sAttribution,
                    Description = s.Description is null ? null : _sDescription,
                    Pulse = s.Pulse is null ? null : _sPulse,
                    Chart = s.Chart is null ? null : _sChart,
                    PreRelease = s.PreRelease is null ? null : _sPreRelease,
                    ReleasePanel = s.ReleasePanel is null ? null : _sReleasePanel,
                    Trailing = s.Trailing is null ? null : _sTrailing,
                    Episodes = s.Episodes is null ? null : _sEpisodes,
                    Badges = s.Badges is null ? null : _sBadges,
                    Rating = s.Rating is null ? null : _sRating,
                    Ledger = s.Ledger is null ? null : _sLedger,
                    Primary = s.Primary is null ? null : _sPrimary,
                    Satellites = s.Satellites is null ? null : _sSatellites,
                    Topics = s.Topics is null ? null : _sTopics,
                };
            }
            return _trampSlots;
        }

        // ── layout callbacks, effects, memos ──

        void Measure(RectF r)
        {
            if (r.W <= 0f) return;
            _measuredW = r.W;
            if (r.H > 0f) _measuredH = r.H;
            // The mode ladder's source (value-gated). Its tier crosses only through the host: a crossing found here is PENDING until the first
            // frame that is not resized, then commits before FLIP capture and animates (themes plan 3.3); a plain write of the mode here snapped.
            if (r.W != _pageW.Peek()) _pageW.Value = r.W;
            // …and the rail's page-aware ceiling, quantized to 8 DIP and value-gated. Every two-column arm shares one formula (the vertical arm
            // composes no rail, so its cap is never read), so it does not wait for the mode's commit.
            float mx = RailPolicy.MaxWidthForPage(r.W, RailPolicy.WideMode);
            if (mx != _railMax.Peek()) _railMax.Value = mx;
        }

        void ResyncRails()
        {
            for (int i = 0; i < _rails.Length; i++) _rails[i].Resync();
        }

        /// <summary>Every rail re-rests from its REMEMBERED width for the page's live (mode, maximum): the breakpoint rail
        /// in a narrow arm the scope has never been dragged in, the persisted width once it has — clamped, never written back.</summary>
        void RestRails()
        {
            int mode = _modeTier is { } tier ? DetailLadders.ModeOf(tier.Value) : RailPolicy.WideMode;
            float max = _railMax.Value;
            // The window tier's resting width (Classic Huge's 320): tracked, so a crossing re-rests every undragged rail on the frame it commits.
            float wideRest = ShellPolicy.Tracked.DetailTiers.For(ThemeHost.Facts.Value.Tiers).RailRest;
            for (int i = 0; i < _rails.Length; i++) _rails[i].Rest(mode, max, wideRest);
        }

        /// <summary>The mode ladder's seed before the first measure: the content region the frame resolved the window tiers on, else the viewport
        /// less a sidebar; unknown (no viewport yet) seeds the widest arm, as the shell always did.</summary>
        float PageSeed()
        {
            float vw = _viewport?.Peek().Width ?? 0f;
            float seed = Breakpoints.EstimatePageWidth(ThemeHost.Facts.Peek().Tiers, vw);
            return seed > 0f ? seed : float.PositiveInfinity;
        }

        void PlayAll()
        {
            // The table fills the cell with "play the VISIBLE order from the top"; before it mounts, play the context.
            // The hero ▶ pauses/resumes the context it is already playing instead of restarting it.
            if (_latest is { } cur && ContextPlayRules.For(cur.Identity.Subject.Id, Playback.ContextUri.Peek(), Playback.CurrentId.Peek(), Playback.Error.Peek()) == ContextPlayAction.Toggle)
            { Playback.PlayOrToggleContext(cur.Identity.Subject.Id); return; }
            if (_playAllCell[0] is { } visible) { visible(); return; }
            if (_latest is { } spec) Playback.PlayOrToggleContext(spec.Identity.Subject.Id);
        }

        /// <summary>Auto-tracked: the spec and the cover palette watch. A grading landing re-derives the accent without
        /// re-rendering the page.</summary>
        void PublishAccentTracked()
        {
            var spec = _spec.Value;
            if (spec is null) return;
            string? paletteUrl = spec.Identity.PaletteUrl ?? spec.Identity.CoverUrl;
            if (paletteUrl is { Length: > 0 } p) _ = Palette.Watch(p).Value;
            PublishAccent(spec);
        }

        /// <summary>Themes plan X44: the accent follows <see cref="Tok.Epoch"/> (the light/dark kind AND the definition: the graded half and the
        /// contrast solves read both) instead of a <c>Signal&lt;ThemeKind&gt;</c> the page republished on every flip. The first publish
        /// (<see cref="ApplyProps"/>) already ran at the current epoch, so the effect's mount run does nothing.</summary>
        void RepublishAccent()
        {
            if (_accentEpoch == Tok.Epoch) return;
            _accentEpoch = Tok.Epoch;
            if (_latest is { } spec) PublishAccent(spec);
        }

        /// <summary>The epoch the accent was last published at.</summary>
        int _accentEpoch = -1;

        /// <summary>The accent and the page-scoped context pair are written together, so a consumer reading either
        /// never sees them disagree.</summary>
        void PublishAccent(FrameSpec spec)
        {
            var accent = ComputeAccent(spec);
            _accent.Value = accent;
            _pageAccent.SetIfChanged(new Design.PageAccent(accent, accent, spec.RouteKey));
        }

        void RefreshCaches(Identity id)
        {
            if (!string.Equals(_captionFor, id.Title, StringComparison.Ordinal))
            {
                _captionFor = id.Title;
                _captionText = Drag.AddTo(id.Title);          // computed once, never inside a live drag's frame region
            }
            if (!_subjectFor.Equals(id.Subject))
            {
                _subjectFor = id.Subject;
                _subjectText = id.Subject.Text;
            }
        }

        // ── the page-body drop target (W19) ──

        DropTargetSpec? PageDropTarget(in Config cfg)
        {
            if (cfg.Content != DetailContent.Tracks) return null;
            return _dropTarget ??= Drop.Target<DragPayload>(Drag.Resource,
                accepts: DropAccepts,
                onDrop: (p, _) => DepositAppend(p),
                caption: _ => _captionText,
                refusalCaption: DropRefusalCaption,
                transparent: DropTransparent);
        }

        void DepositAppend(DragPayload p)
        {
            if (_latest?.Actions.DepositOnPage is { } deposit) _ = deposit(p, null);
        }

        bool DropAccepts(DragPayload p)
            => _latest?.Actions.DepositOnPage is not null && !SameList(p) && Verdict(p) == DropRefusal.None;

        /// <summary>A same-list row drag is the LIST's gesture (the hero is scenery it crosses); an album / show / liked
        /// page with no deposit verb has nothing to say about a resource drag. A read-only PLAYLIST stays a target so it
        /// can explain itself.</summary>
        bool DropTransparent(DragPayload p)
            => SameList(p) || (_latest is { } s && s.Actions.DepositOnPage is null && s.Identity.Kind != DetailKind.Playlist);

        DropRefusal Verdict(DragPayload p)
            => Drag.Evaluate(editable: _latest?.Actions.DepositOnPage is not null, loading: false,
                             payloadHasTracks: p.CanCopyTracks, sameList: false, naturalOrder: true, filtered: false,
                             rowsKeyed: true);

        string? DropRefusalCaption(DragPayload p) => Verdict(p) switch
        {
            DropRefusal.NotEditable => Loc.Get(Strings.Drag.CantEditPlaylist),
            // An artist has no single obvious track set — refuse rather than guess.
            DropRefusal.NoTracks => p.Kind == DragKind.Artist ? Loc.Get(Strings.Drag.CantAddArtist) : Loc.Get(Strings.Drag.NothingToAdd),
            _ => null,
        };

        bool SameList(DragPayload p)
            => p.SourceRows is { Length: > 0 } && string.Equals(p.SourcePlaylistUri, _subjectText, StringComparison.Ordinal);
    }

    static readonly Func<uint, ColorF> s_liftPayload = static a => Design.Palette.ChromeFromPayload(a);

    /// <summary>The one accent ladder (<see cref="AccentLadder"/>): the cover's chrome grading (else the fallback
    /// url's), else the payload hex, else the last remembered page accent while this page's grading is still pending,
    /// else the system accent. Never the now-playing track. Cached probes only — a miss enqueues elsewhere and the
    /// caller's tracked effect re-runs when it lands. The answer carries the theme's contrast floor
    /// (<see cref="Design.Palette.Floor"/>, D13): the hold remembers the graded colour, the floor is re-applied per theme.
    /// <para>With the structure's "Accent from artwork" off (<see cref="ArtworkColorPolicy.AccentFromArtwork"/>, CT9 U2) it is the system
    /// accent whatever the cover, and nothing is remembered. A tracked read: flipping the option re-runs the caller's effect.</para></summary>
    public static ColorF AccentFor(string? paletteUrl, uint payloadAccent, string? fallbackUrl = null)
    {
        if (!ShellPolicy.Tracked.Artwork.AccentFromArtwork) return Tok.AccentDefault;
        var scheme = Design.ChromeSchemeFor(paletteUrl) ?? Design.ChromeSchemeFor(fallbackUrl);
        ColorF? graded = scheme is { } cp ? Design.Palette.ChromeAccent(cp) : null;
        // Nothing better can arrive: no payload and a url the endpoint cannot grade (or has already refused).
        bool definite = payloadAccent == 0
            && (paletteUrl is null || !Palette.CanGrade(paletteUrl) || Palette.HasFreshNegative(paletteUrl));
        var result = AccentLadder.Resolve(new AccentLadder.Input(graded, payloadAccent, definite),
            AccentHold.Last, Tok.AccentDefault, s_liftPayload);
        AccentHold.Remember(result);
        return Design.Palette.Floor(result.Color);
    }

    /// <summary>The last remembered page accent a page seeds its first frame with, or null when the accent does not follow the artwork (a held
    /// cover colour would paint one frame of artwork accent with the option off). An untracked read: it seeds a page, the effect then follows.</summary>
    public static ColorF? HeldAccent => ShellPolicy.Now.Artwork.AccentFromArtwork ? AccentHold.Last : null;

    static ColorF ComputeAccent(FrameSpec spec)
    {
        var id = spec.Identity;
        return AccentFor(id.PaletteUrl ?? id.CoverUrl, id.CardAccent);
    }

    // ══ 4. THE WEB LINK ═════════════════════════════════════════════════════════════════════════════════════
    //
    // The rail's keyed rows (`Row` / `LateRow` / `ReleaseRow`), the eyebrow run and the billed-artist line box are Classic's (T9b: `ClassicDetailKit`); so are the
    // save target and the share action the views draw from an `Identity` (T10c2: `ClassicDetailKit.SaveTargetOf` / `ShareActionFor`). What stays is the web link.

    /// <summary>The open.spotify.com link a Spotify catalogue uri shares as (the ShareUrl column's fallback).</summary>
    static string? WebUrlOf(EntityUri uri)
    {
        if (uri.Provider != EntityProvider.Spotify) return null;
        string? kind = uri.Kind switch
        {
            EntityKind.Album => "album", EntityKind.Playlist => "playlist", EntityKind.Artist => "artist",
            EntityKind.Show => "show", EntityKind.Track => "track", EntityKind.Episode => "episode",
            _ => null,
        };
        if (kind is null) return null;
        string text = uri.Text;
        var bare = EntityUri.IdOf(text);
        return bare.IsEmpty ? null : "https://open.spotify.com/" + kind + "/" + new string(bare);
    }

    // ══ 5. THE NOTICE ════════════════════════════════════════════════════════════════════════

    /// <summary>W15: bad news is a strip, never an error page (the `Detail.Notice` view: one Informational bar; zero-height
    /// and hit-test free while there is nothing to say). <paramref name="showMinifiedAlbum"/> is false on the full page, which
    /// self-heals a thin tracklist; the minified verdict carries no action; the one action is a way OUT, never a retry. The
    /// frame host and the library album pane mount it.</summary>
    public static Element NoticeBar(DetailNotice notice, bool showMinifiedAlbum, Action? goLibrary)
        => ViewSlots.View(Contracts.ViewKeys.DetailNotice, new Contracts.DetailNoticeProps(notice, showMinifiedAlbum, goLibrary))
            with { Key = "detail-notice" };

    /// <summary>The brush budget every accent consumer shares — a VALUE under reduced motion (0 ⇒ an instant swap).</summary>
    static float AccentTransitionMs => Design.Reduced ? 0f : Design.Motion.Standard;
}
