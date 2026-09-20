// ── Entities/Detail.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// THE SHARED DETAIL FRAME (ch 03 §1.2): Identity (+ Identity.For(Album)), FrameActions, FrameSlots, FrameSpec and Frame —
// the 4-mode responsive ladder with both anti-flicker fail-safes, the Hero page-layout override, the centred two-column
// row (MaxWidth 1600), the per-scope rail prefs + Splitter grip + resist fade + the 96-DIP compact strip, the notice
// strip, the page tone plane and shell tint leaves, the accent and the page-body drop target — plus the rail family
// (Rail, CompactRail, ShowHeader), NoticeBar, PlayPill, Satellite, the More button and the billed-artist ATTRIBUTION
// LINE every detail-frame hero and library pane heads its meta with (ArtistLine).
// The vertical hero, the context band, the skeleton band and the band helpers are the named partial Detail.UI.Hero.cs.
//
// Podcast rework wave P2 (podcast-show-rework-implementation.md §5.4): six APPENDED FrameSlots (Badges, Rating, Ledger,
// Primary, Satellites, Topics) with their insertion points in the rail and in the vertical show header, an episode's rail
// led by its show link (the Attribution slot), and the skeleton twin reserving each declared slot — both columns walk the
// ONE row model in Detail.cs §8b. The satellite builders (RailSatellite / RailSatelliteSave / RailSatelliteMore) and the
// labeled PlayPill are what a page's slots are made of.
//
// Role: UI
// Owner: M
// Wave: 4.5
// Budget: 2600 lines
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
using static FluentGpu.Dsl.Ui;

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
            string? meta = metaLoading ? null : Text.AlbumMeta(trackCount, totalMs, durationsKnown, a.Year);

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
        //    cover edge in the rail, a 600 cap in the vertical header.) The frame re-invokes these builders only when it
        //    re-renders for its own reasons, so a body whose content changes — a label, a count, a satellite that comes and
        //    goes — is a keyed component or binds its values / its Visible; the Satellites ARRAY is fixed per slot set.

        /// <summary>Badge chips (<c>Controls.Chip</c> × n): on the eyebrow's line for a show ("Podcast [exclusive] [E]"),
        /// under the meta line for an episode. One line of 16 is reserved (<see cref="RailLayout.BadgeHeight"/>).</summary>
        public Func<Element>? Badges { get; init; }
        /// <summary>The rating row, under the title / attribution (a show's ★ 4.8 · 12,431 ratings). Reserved 20.</summary>
        public Func<float, Element>? Rating { get; init; }
        /// <summary>The played ledger (<c>Controls.LedgerBar</c> + its counts line), under the meta line. Reserved 26.</summary>
        public Func<float, Element>? Ledger { get; init; }
        /// <summary>The CTA's primary, called with the page ACCENT thunk; REPLACES the Play pill when present (Follow,
        /// Resume · 17 min left, Play latest, Play preview — build it with <see cref="PlayPill"/>'s label/glyph).</summary>
        public Func<Func<ColorF>, Element>? Primary { get; init; }
        /// <summary>The CTA's satellites, KEYED; REPLACES the fixed [heart][Share][⋯] group when present, and they wrap one
        /// by one after the primary, 8 apart (<see cref="RailSatellite"/>, <see cref="RailSatelliteSave"/>,
        /// <see cref="RailSatelliteMore"/> build them at <see cref="RailLayout.SatelliteSize"/>).</summary>
        public Func<Element[]>? Satellites { get; init; }
        /// <summary>The topic words (<c>Controls.Words.Links</c>), under the CTA. Two lines reserved; the rail only (the
        /// vertical header has no topics).</summary>
        public Func<float, Element>? Topics { get; init; }
        /// <summary>The rail's SHORT about block — a small label over the description clamped to a few lines, with the
        /// inline overflow affordance the clamp itself decides on (an episode's; podcast-episode-peek §1). Natural
        /// height, arrives with the description, and the full text / chapters / links stay in the page body. Rail only
        /// (the vertical header puts its body under the rows, where the About section already is).</summary>
        public Func<float, Element>? About { get; init; }

        internal int Mask()
            => (Cover is null ? 0 : 1) | (CompactCover is null ? 0 : 2) | (Title is null ? 0 : 4)
             | (Attribution is null ? 0 : 8) | (Description is null ? 0 : 16) | (Pulse is null ? 0 : 32)
             | (Chart is null ? 0 : 64) | (PreRelease is null ? 0 : 128) | (ReleasePanel is null ? 0 : 256)
             | (LikedFacts is null ? 0 : 512) | (Trailing is null ? 0 : 1024) | (Episodes is null ? 0 : 2048)
             | (Badges is null ? 0 : 4096) | (Rating is null ? 0 : 8192) | (Ledger is null ? 0 : 16384)
             | (Primary is null ? 0 : 32768) | (Satellites is null ? 0 : 65536) | (Topics is null ? 0 : 131072)
             | (About is null ? 0 : 262144);

        public bool Equals(FrameSlots? o) => o is not null && Mask() == o.Mask();
        public override int GetHashCode() => Mask();
    }

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

    /// <summary>THE detail scaffold (ch 03 §1). Owns the 4-mode ladder and both fail-safes, the per-scope rail prefs +
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
    const float TallWindowH = Design.Size.DesignH;                    // the 40/52 rail title at ≥ 900
    const float ShortWindowH = 760f;                                  // the 3-line description below 760
    const float RailSidePadL = Spacing.L, RailSidePadR = Spacing.S;   // 16 / 8
    const float RailGap = RailLayout.Gap;                             // 14 — the row model's (Detail.cs §8b)
    const float RailFabSize = RailLayout.FabSize;                     // 40
    const int RailCoverDecodePx = 256;                                // the shelf card's bucket — a warm texture on arrival
    const string MetaShimmerText = "00 songs · 0 hr 00 min";          // the shimmer SHAPE only; never painted as text

    /// <summary><c>DetailShell.RailW</c>: the breakpoint rail of modes 1/2, the config's own width at mode 0. The
    /// NON-RESIZABLE arm only (<c>RailResizable: false</c>) — a resizable rail rests through
    /// <see cref="RailPolicy.RestingWidth"/>, which carries the same two breakpoint widths.</summary>
    static float RailWidthForMode(int mode, in Config cfg) => mode switch { 0 => cfg.RailWidth, 1 => RailMidW, _ => RailNarrowW };

    /// <summary><c>DetailRail.CoverEdge</c>: the rail cover fills the column less its side padding, floored at 80.</summary>
    static float RailCoverEdge(float railW) => MathF.Max(80f, railW - RailSidePadL - RailSidePadR);

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

        public RailCell(RailScope scope, Signal<float> fade)
        {
            Scope = scope;
            _fade = fade;
            var keys = RailPolicy.KeysFor(scope);
            _stored = RailPolicy.ClampStored(Platform.Settings.Get(keys.Width), scope);
            Width = new Signal<float>(RailPolicy.RestingWidth(_stored, scope, RailPolicy.WideMode, RailPolicy.MaxWidth));
            Collapsed = new Signal<bool>(Platform.Settings.Get(keys.Collapsed));
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
        public void Rest(int mode, float max)
        {
            _mode = mode;
            _max = max;
            float w = RailPolicy.RestingWidth(_stored, Scope, mode, max);
            if (w != Width.Peek()) Width.Value = w;
        }

        void CommitNow()
        {
            var keys = RailPolicy.KeysFor(Scope);
            // A drag that merely parked against a page-imposed cap keeps the wider remembered width.
            _stored = RailPolicy.CommitWidth(Width.Peek(), _stored, _max);
            Platform.Settings.Set(keys.Width, _stored);
            // The ONE bump: every mounted (and parked) frame re-syncs its rails, so "Keep left-rail same size" moves them all.
            Prefs.DetailHero.Set(keys.Collapsed, Collapsed.Peek());
        }

        void ExpandNow()
        {
            Collapsed.Value = false;
            _fade.Value = 1f;
            Prefs.DetailHero.Set(RailPolicy.KeysFor(Scope).Collapsed, false);
        }

        /// <summary>Re-seed from the store, then re-rest at the page's live bounds (the epoch effect, ch 03 §6).</summary>
        public void Resync()
        {
            var keys = RailPolicy.KeysFor(Scope);
            _stored = RailPolicy.ClampStored(Platform.Settings.Get(keys.Width), Scope);
            Rest(_mode, _max);
            Collapsed.Value = Platform.Settings.Get(keys.Collapsed);
        }
    }

    sealed class FrameHost : Component, IPropsHost
    {
        // ── the spec: newest pushed (delegates) + the data gate (renders) ──
        FrameSpec? _latest;
        readonly Signal<FrameSpec?> _spec = new(null);

        // ── the page mode ──
        readonly Signal<int> _mode = new(0);
        float _measuredW, _measuredH;
        bool _modeInitialized;

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
        readonly Signal<ThemeKind> _theme = new(ThemeKind.Dark);
        readonly Action?[] _playAllCell = new Action?[1];
        readonly object _tintOwner = new();
        IReadSignal<Size2>? _viewport;
        bool _accentSeeded;

        // ── INSIGHTS SHEET (additive; Detail.Insights.cs) ── the vertical arm's host for the facts bento. One toggle
        //    per mounted frame: the open state is a signal, so the toolbar button and the pane cannot disagree and
        //    neither costs the page a render. `_sheetRoute` closes it on any route swap the host survives.
        readonly InsightsToggle _insights = new();
        readonly Action _closeInsights;
        readonly Action _returnInsightsFocus;
        readonly Action _syncInsightsArm;
        InputHooks? _inputHooks;
        string _sheetRoute = "\0";
        bool _sheetHosts;
        //    The FACTS LATCH (InsightsSheet.FactsSettled): the page's bento slot is derived from a scan of the live row
        //    source, which reads empty while the list's open holds its reveal — so an absent slot is "not answered yet",
        //    not "no facts". Latched per ROUTE and dropped with it, beside the open state.
        Func<float, Element>? _factsLatched;

        // ── cached delegates (zero per-render allocation for the handlers the tree carries) ──
        readonly Action<RectF> _measure;
        readonly Func<ColorF> _accentFn;
        readonly Action _playAll;
        readonly Action _resyncRails;
        readonly Action _restRails;
        readonly Action _publishAccentTracked;
        readonly Action _publishTheme;
        readonly Func<int> _heightRungsCompute;
        readonly Func<bool> _railPending;

        // trampolines into _latest
        readonly Action _tShuffle, _tCoverClick, _tGoLibrary;
        readonly Func<ContextMenuModel?> _tMore;
        readonly Func<object?> _tCoverDrag;
        readonly Func<DragPayload, int?, bool> _tDeposit;
        readonly Func<float, Element> _sCover, _sCompactCover, _sAttribution, _sDescription, _sLikedFacts,
                                      _sRating, _sLedger, _sTopics;
        readonly Func<float, float, Element> _sTitle;
        readonly Func<Element> _sPulse, _sChart, _sPreRelease, _sTrailing, _sTrailingVertical, _sBadges;
        readonly Func<bool, Element> _sReleasePanel, _sEpisodes;
        readonly Func<Func<ColorF>, Element> _sPrimary;
        readonly Func<Element[]> _sSatellites;
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

        const int RungTall = 1, RungShort = 2;

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
            _publishTheme = () => { _theme.Value = Tok.Theme; };
            _heightRungsCompute = ComputeHeightRungs;
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
            _sLikedFacts = w => _latest?.Slots.LikedFacts?.Invoke(w) ?? new BoxEl();
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

            // ── INSIGHTS SHEET (additive) ──
            _closeInsights = _insights.Close;
            _returnInsightsFocus = ReturnInsightsFocus;
            _syncInsightsArm = () => { if (!_sheetHosts) _insights.Close(); };
        }

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
            //    edge that closes the sheet there (Detail.InsightsSheet.SurvivesRouteChange).
            if (!string.Equals(_sheetRoute, spec.RouteKey, StringComparison.Ordinal))
            {
                _sheetRoute = spec.RouteKey;
                _insights.Close();
                _factsLatched = null;        // the facts latch is the ROUTE's, exactly like the open state
            }
            if (!_accentSeeded)
            {
                // The first paint already carries a cached grading's accent instead of the default for one frame.
                _accentSeeded = true;
                PublishAccent(spec);
                _theme.Value = Tok.Theme;
            }
            _spec.Value = spec;
        }

        public override Element Render()
        {
            _ = _spec.Value;                                    // the data gate
            var spec = _latest!;                                // …and the newest delegates
            var id = spec.Identity;
            var cfg = spec.Config;

            // ── preferences (each subscribes its epoch, then re-reads the store) ──
            bool washes = Prefs.Appearance.ColorWashes();
            int pageLayout = Prefs.DetailHero.PageLayout();
            bool uniform = Prefs.DetailHero.RailUniform();
            int railEpoch = Prefs.DetailHero.Epoch.Value;
            UseEffect(_resyncRails, DepKey.From(railEpoch));
            // Auto-tracked on _mode + _railMax: a breakpoint cross or a narrower page re-rests every rail from its
            // REMEMBERED width. A signal write belongs in an effect, never in a render (rule 6).
            UseEffect(_restRails);

            var shellSlot = UseContext(ShellMaterial.Slot);
            _inputHooks = UseContext(InputHooks.Current);       // ── INSIGHTS SHEET (additive): the focus return ──
            var viewportSig = UseContextSignal(Viewport.Size);
            _viewport = viewportSig;
            var rungsMemo = UseComputed(_heightRungsCompute);
            UseEffect(_publishAccentTracked);                                   // grading / spec
            UseEffect(_publishTheme, DepKey.From((int)Tok.Theme));              // a theme flip re-derives the accent

            string? paletteUrl = id.PaletteUrl ?? id.CoverUrl;
            string routeKey = spec.RouteKey;
            RefreshCaches(id);

            // ── the mode, with both anti-flicker fail-safes (ch 03 §6) ──
            float pageWidthEstimate = Breakpoints.EstimatePageWidthFromViewport(viewportSig.Peek().Width);
            int mode = _mode.Value;
            // (a) pre-measure seed: a NARROWER arm is always safe to seed; a wider one is the flicker.
            if (!_modeInitialized && _measuredW <= 0f)
                mode = Math.Max(mode, Breakpoints.InitialModeForViewport(pageWidthEstimate));
            // (b) self-heal: never RENDER a mode wider than the last measured width supports (0 = widest).
            if (_measuredW > 0f)
            {
                int fit = Breakpoints.ModeFor(_measuredW, mode, _modeInitialized);
                if (fit > mode) mode = fit;
            }
            // The Hero page layout forces the vertical SYSTEM for track pages only; _mode keeps tracking the real width.
            if (cfg.Content == DetailContent.Tracks && pageLayout == Prefs.DetailHero.Hero)
                mode = Breakpoints.VerticalMode;
            bool vertical = mode == Breakpoints.VerticalMode;
            bool verticalTracks = vertical && cfg.Content == DetailContent.Tracks;

            // ── INSIGHTS SHEET (additive; Detail.Insights.cs) ── which arm HOSTS the facts bento. A two-column arm is
            //    unchanged (the rail row, below). The vertical arm has no rail, so the bento is a sheet over the
            //    content and the toolbar carries its toggle — and, because the toggle is gated on the same slot
            //    presence, the bento is never appended to the page body again (Detail.InsightsSheet).
            //
            //    The slot is LATCHED for the route first (InsightsSheet.FactsSettled): a page derives its bento slot
            //    from a scan of the live row source, which reads empty while the list's open holds its reveal, so an
            //    absent slot on a page that plainly has facts means "not answered yet", not "no facts" — and a toggle
            //    that appears only if that scan happens to have run at the right moment is the bug this cures. A page
            //    that has never offered facts still shows nothing and hints at nothing.
            bool factsNow = spec.Slots.LikedFacts is not null;
            bool factsSettled = InsightsSheet.FactsSettled(_factsLatched is not null, factsNow);
            if (factsNow) _factsLatched = spec.Slots.LikedFacts;      // a plain field, not a signal: no render write
            _sheetHosts = InsightsSheet.ShowsToggle(mode, id.Kind, factsSettled, cfg.Content);
            // Widening back into a two-column arm (or losing the facts) CLOSES the sheet — a signal write belongs in an
            // effect, never in a render (rule 6).
            UseEffect(_syncInsightsArm, DepKey.From(_sheetHosts ? 1 : 0));

            // ── the leaves: shell tint (a hand-over, never a clear) and the page tone plane (the ONE ground) ──
            Element tint = Palette.ShellTint(paletteUrl, ready: true, disabled: !washes, apply: cfg.TwoColumn,
                owner: _tintOwner, slot: shellSlot, key: "detail-tint:" + routeKey, payloadAccent: id.CardAccent);
            float pageH = _measuredH > 1f ? _measuredH : viewportSig.Peek().Height;
            // heroOnly is dead (the setting is gone): the band/height props feed only that arm, so they are PEEKED — a
            // hero measurement must not re-render the page for a value nothing paints.
            float heroBand = verticalTracks ? _heroHeight.Peek() : pageH * TwoColumnHeroBandFraction;
            Element tone = Palette.PageTonePlane(paletteUrl, fallbackUrl: null, disabled: !washes, backdropBand: heroBand,
                pageHeight: pageH, heroOnly: false,
                key: "detail-tone:" + routeKey + (Tok.Theme == ThemeKind.Light ? ":light" : ":dark"),
                payloadAccent: id.CardAccent);

            // ── the notice strip: about the PAGE, read before the content it qualifies; never an error page ──
            Element notice = NoticeBar(id.Notice, showMinifiedAlbum: false,
                goLibrary: spec.Actions.GoLibrary is null ? null : _tGoLibrary);

            // ── the right column ──
            float rightMin = Breakpoints.ContentMinWidthForMode(mode);
            Element right = cfg.Content == DetailContent.Episodes
                ? new BoxEl
                {
                    Key = "right:eps", Grow = 1f, Shrink = 1f, MinWidth = rightMin, MinHeight = 0f, Direction = 1,
                    Children = [(spec.Slots.Episodes is null ? new BoxEl { Grow = 1f } : _sEpisodes(vertical)) with { Key = "episodes:" + routeKey }],
                }
                : new BoxEl
                {
                    Key = "right:tracks", Grow = 1f, Shrink = 1f, MinWidth = rightMin, MinHeight = 0f, Direction = 1,
                    Children = [TableFor(spec, vertical, verticalTracks, pageWidthEstimate, routeKey)],
                };

            var acts = TrampolineActions(spec.Actions);
            DropTargetSpec? drop = PageDropTarget(cfg);

            // ── mode 3: the vertical / hero system ──
            if (vertical)
            {
                Element content = new BoxEl
                {
                    Direction = 1, Grow = 1f, ClipToBounds = true,
                    DropTarget = drop,
                    // A show in the vertical arm is the fixed ShowHeader above its episodes — never the hero system.
                    Children = verticalTracks ? [right] : [ShowHeaderCore(spec, acts, _accentFn, _playAll), right],
                };
                Element verticalPage = new BoxEl
                {
                    Key = "detail:vertical", Direction = 1, Grow = 1f, ClipToBounds = true,
                    Children = [notice, content],
                };
                // ── INSIGHTS SHEET (additive) ── a TRACK page's vertical arm ALWAYS composes the overlay, open or not
                //    and facts or not, so the page's tree shape never changes underneath the list: the facts arriving
                //    (or the sheet opening) must not remount the table and lose its scroll position. An episode list's
                //    vertical arm is left exactly as it was.
                Element verticalRoot = verticalTracks
                    //    The LATCHED builder, not the live slot: the toggle and the pane must never disagree, and the
                    //    builder closes over the page's own live source, so a latched one is never stale data.
                    ? InsightsOverlay(verticalPage, _insights, _sheetHosts ? _factsLatched : null,
                                      _measuredW, routeKey, _returnInsightsFocus)
                    : verticalPage;
                return Ctx.Provide(Design.AccentCtx.Slot, (IReadSignal<Design.PageAccent>?)_pageAccent, new BoxEl
                {
                    ZStack = true, Grow = 1f, OnBoundsChanged = _measure, ClipToBounds = true,
                    Children = [tint, tone, verticalRoot],
                });
            }

            // ── modes 0/1/2: the centred two-column row [rail | grip | right] ──
            var rail = _rails[(int)RailPolicy.ScopeFor(cfg.RailScope, uniform)];
            bool resizable = RailPolicy.ResizableFor(cfg.RailResizable, mode);
            // Read unconditionally (a stable subscription) but honoured only where the grip that can undo it exists.
            bool collapsed = rail.Collapsed.Value && resizable;
            // The page-aware cap: the rail may never squeeze the content column below its 300-DIP floor. Clamped again
            // HERE (on read) so the one frame between a resize and the re-resting effect still lays out inside the page.
            float railMax = _railMax.Value;
            float railW = resizable ? RailPolicy.ClampLive(rail.Width.Value, rail.Scope, railMax) : RailWidthForMode(mode, cfg);

            Element[] rowKids;
            if (collapsed)
            {
                // `right` keeps its Key across the collapse, so the table reconciles in place and keeps its scroll.
                rowKids = [CompactRailRegion(spec, RailCompactW, rail.Expand), Grip(rail, railMax, collapsedNow: true), right];
            }
            else
            {
                int rungs = rungsMemo.Value;                                      // re-renders only on a rung cross
                float titleSize = (rungs & RungTall) != 0 ? 40f : 28f;
                float titleLineHeight = VerticalLayout.TitleLineHeightFor(titleSize);
                int descLines = (rungs & RungShort) != 0 ? 3 : 6;
                // The fade wrapper is present in every non-collapsed mode so a breakpoint cross never changes the row's
                // child shape at index 0. PAINT-BOUND: the resist cue never re-renders the rail.
                Element railFaded = new BoxEl
                {
                    Key = "detail-rail-fade",
                    Direction = 0, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch,
                    // CLIPS (bug B). `rail:cover` below takes an EXPLICIT square of `RailCoverEdge(railW)` and, like
                    // every flex item, defaults to `Shrink = 0` — so while a two-column split is still settling, a
                    // `railW` that has not caught up mints a cover wider than the column it sits in, and with nothing
                    // bounding it the square paints straight over the table's `#` lane and first rows. This wrapper is
                    // the one box that always knows the column's REAL width, so it is the right place to contain it.
                    //
                    // Deliberately NOT `Shrink = 1` on the cover itself: the rail is a COLUMN, so flex shrink there
                    // works on the VERTICAL axis and would squash the cover out of square on a short rail — a worse
                    // bug than the one being fixed. A clip cannot change layout, only what escapes it.
                    ClipToBounds = true,
                    MinHeight = 0f, Shrink = 0f, Width = railW,
                    Opacity = _railFade,
                    Children = [RailRegion(spec, acts, railW, titleSize, titleLineHeight, descLines)],
                };
                rowKids = resizable ? [railFaded, Grip(rail, railMax, collapsedNow: false), right] : [railFaded, right];
            }

            var row = new BoxEl
            {
                // flex:1 1 0 — the row's width is the AVAILABLE region, so the right column yields to a tighter tier
                // instead of overflowing the clipped content card.
                Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Basis = 0f,
                MaxWidth = Design.Size.PageMaxW,
                AlignItems = FlexAlign.Stretch,
                DropTarget = drop,
                Children = rowKids,
            };
            var twoColumnPage = new BoxEl
            {
                Key = "detail:two-column",
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
                Children =
                [
                    notice,
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.Center,
                        Children = [row],
                    },
                ],
            };
            // The page PROVIDES its accent: the heart and every other ambient consumer read it through the context.
            return Ctx.Provide(Design.AccentCtx.Slot, (IReadSignal<Design.PageAccent>?)_pageAccent, new BoxEl
            {
                ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                OnBoundsChanged = _measure, ClipToBounds = true,
                Children = [tint, tone, twoColumnPage],
            });
        }

        // ── the rail behind ONE skeleton boundary ──

        /// <summary>The expanded rail: one <c>SkelRegionEl</c> pending on the header (<see cref="Identity.HeaderPending"/>),
        /// whose shimmer is derived from a column of the SAME rows the loaded rail lays out (<see cref="RailSkeletonColumn"/>
        /// mirrors <see cref="RailColumn"/> row for row), cross-dissolving once — the table's contract (FadeOnly, no resize
        /// tween). The frame (layer fill + scroller) sits OUTSIDE the boundary so the fill never blinks. The content thunk
        /// closes over this render's spec; a later push re-renders the frame and the region refreshes its Ready branch in
        /// place, so the rail keeps following the header (meta, description, a rename) after the reveal.</summary>
        Element RailRegion(FrameSpec spec, FrameActions acts, float railW, float titleSize, float titleLineHeight, int descLines)
            => RailFrame(spec.Identity.Kind, railW, new SkelRegionEl(
                Pending: _railPending, Failed: s_false,
                Content: () => RailColumn(spec, acts, railW, titleSize, titleLineHeight, descLines, _accentFn, _playAll),
                ShimmerSource: () => RailSkeletonColumn(spec, railW, titleLineHeight, descLines),
                OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false));

        /// <summary>The collapsed strip's identity (cover + title) behind the same boundary; the expand chevron stays real.</summary>
        Element CompactRailRegion(FrameSpec spec, float stripW, Action expand)
        {
            float cover = CompactCover(stripW);
            return CompactRailStrip(spec, stripW, expand, new SkelRegionEl(
                Pending: _railPending, Failed: s_false,
                Content: () => CompactRailIdentity(spec, cover, expand),
                ShimmerSource: () => CompactRailSkeleton(spec, cover),
                OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false));
        }

        // ── the table ──

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
        /// rows because the hero system has no rail — they are not dropped — then the album trailing.</summary>
        Element VerticalTrailing()
        {
            var spec = _latest;
            if (spec is null) return new BoxEl();
            var kids = new List<Element>(3);
            if (spec.Identity.UpcomingAtUnixSeconds > 0 && spec.Slots.PreRelease is { } pre)
                kids.Add(LateRow("vtrail:prerelease", pre()));
            if (spec.Config.Badges == BadgeStyle.TypeYear && spec.Slots.ReleasePanel is { } release)
                kids.Add(Row("vtrail:release", release(true)));
            if (spec.Config.HasTrailing && spec.Slots.Trailing is { } trailing)
                kids.Add(Row("vtrail:trailing", trailing()));
            return new BoxEl { Direction = 1, Children = kids.ToArray() };
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

        /// <summary>The VERTICAL arm's view of the page's slots. ── INSIGHTS SHEET (additive) ── <c>LikedFacts</c> is
        /// deliberately DROPPED here: in this arm the bento belongs to the sheet, and the table's own facts FOOTER
        /// (its <c>hasFacts</c> reads exactly this slot) is what used to push it to the bottom of the page. The frame
        /// still hands the page's own builder to the sheet, so nothing is lost — only the host changed.</summary>
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
                    LikedFacts = null,   // ── INSIGHTS SHEET ── the sheet hosts it in this arm; never the list footer.
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

        // ── the grip ──

        Element Grip(RailCell rail, float max, bool collapsedNow) => new BoxEl
        {
            Key = "detail-rail-grip-strip",
            Width = collapsedNow ? GripStripCollapsedW : Splitter.StripW,
            Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Stretch,
            Children =
            [
                // Width writes are direct during the drag (bounded by the page-aware Max, so a drag can never squeeze the
                // content column out); RELEASE commits width + collapsed to THIS scope's pair.
                Splitter.Create(rail.Width, rail.Commit, rail.OptionsFor(max), collapsed: rail.Collapsed, fade: _railFade)
                    with { Key = rail.GripKey },
            ],
        };

        // ── layout callbacks, effects, memos ──

        void Measure(RectF r)
        {
            if (r.W <= 0f) return;
            _measuredW = r.W;
            if (r.H > 0f) _measuredH = r.H;
            int md = Breakpoints.ModeFor(r.W, _mode.Peek(), _modeInitialized);
            _modeInitialized = true;
            if (md != _mode.Peek()) _mode.Value = md;     // value-gated: a re-render only on a breakpoint cross
            // …and the rail's page-aware ceiling, quantized to 8 DIP and value-gated for the same reason.
            float mx = RailPolicy.MaxWidthForPage(r.W, md);
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
            int mode = _mode.Value;
            float max = _railMax.Value;
            for (int i = 0; i < _rails.Length; i++) _rails[i].Rest(mode, max);
        }

        void PlayAll()
        {
            // The table fills the cell with "play the VISIBLE order from the top"; before it mounts, play the context.
            if (_playAllCell[0] is { } visible) { visible(); return; }
            if (_latest is { } spec) Actions.Services.Play?.Invoke(spec.Identity.Subject);
        }

        /// <summary>Auto-tracked: the spec, the theme tick and the cover palette watch. A grading landing re-derives
        /// the accent without re-rendering the page.</summary>
        void PublishAccentTracked()
        {
            var spec = _spec.Value;
            if (spec is null) return;
            _ = _theme.Value;
            string? paletteUrl = spec.Identity.PaletteUrl ?? spec.Identity.CoverUrl;
            if (paletteUrl is { Length: > 0 } p) _ = Palette.Watch(p).Value;
            PublishAccent(spec);
        }

        /// <summary>The accent and the page-scoped context pair are written together, so a consumer reading either
        /// never sees them disagree.</summary>
        void PublishAccent(FrameSpec spec)
        {
            var accent = ComputeAccent(spec);
            _accent.Value = accent;
            _pageAccent.SetIfChanged(new Design.PageAccent(accent, accent, spec.RouteKey));
        }

        int ComputeHeightRungs()
        {
            float h = _viewport is { } v ? v.Value.Height : 0f;
            return (h >= TallWindowH ? RungTall : 0) | (h < ShortWindowH ? RungShort : 0);
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
    /// caller's tracked effect re-runs when it lands.</summary>
    public static ColorF AccentFor(string? paletteUrl, uint payloadAccent, string? fallbackUrl = null)
    {
        var scheme = Design.ChromeSchemeFor(paletteUrl) ?? Design.ChromeSchemeFor(fallbackUrl);
        ColorF? graded = scheme is { } cp ? Design.Palette.ChromeAccent(cp) : null;
        // Nothing better can arrive: no payload and a url the endpoint cannot grade (or has already refused).
        bool definite = payloadAccent == 0
            && (paletteUrl is null || !Palette.CanGrade(paletteUrl) || Palette.HasFreshNegative(paletteUrl));
        var result = AccentLadder.Resolve(new AccentLadder.Input(graded, payloadAccent, definite),
            AccentHold.Last, Tok.AccentDefault, s_liftPayload);
        AccentHold.Remember(result);
        return result.Color;
    }

    static ColorF ComputeAccent(FrameSpec spec)
    {
        var id = spec.Identity;
        return AccentFor(id.PaletteUrl ?? id.CoverUrl, id.CardAccent);
    }

    // ══ 4. THE RAIL FAMILY ═══════════════════════════════════════════════════════════════════════════════════════════

    static readonly EnterExit FadeUp = new(Opacity: 0f, Active: true);
    static readonly LayoutTransition Shove = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut));

    /// <summary>A keyed row that exists in BOTH models: it only ever gets pushed, so it takes the position-only FLIP.</summary>
    static BoxEl Row(string key, Element child) => new() { Key = key, Direction = 1, Layout = Shove, Children = [child] };
    // Release facts arrive after the album frame. Keep that late panel from participating in the rail's position
    // transition; its own content fades in while the rest of the page keeps its settled geometry.
    static BoxEl ReleaseRow(string key, Element child) => new() { Key = key, Direction = 1, Children = [child] };

    /// <summary>A keyed row that can arrive LATE: fades up on insert, FLIPs on later shoves (ch 03 §0.11).</summary>
    static BoxEl LateRow(string key, Element child) => new()
    {
        Key = key, Direction = 1, Layout = Shove, Enter = FadeUp, Children = [child],
    };

    static TextEl EyebrowRun(string text) => Design.Type.Eyebrow(text) with
    {
        Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    static Action DefaultPlay(EntityUri subject) => () => Actions.Services.Play?.Invoke(subject);

    /// <summary>The fixed-width metadata rail (two-column arm): cover · eyebrow/owner · title · artists · meta · daylist ·
    /// chart · CTA · prerelease · release panel · description · liked facts, in its own scroller.</summary>
    public static Element Rail(FrameSpec spec, float railWidth, float titleSize, float titleLineHeight, int descriptionLines,
                               Func<ColorF> accent)
        => RailCore(spec, spec.Actions, railWidth, titleSize, titleLineHeight, descriptionLines, accent,
                    DefaultPlay(spec.Identity.Subject));

    static Element RailCore(FrameSpec spec, FrameActions acts, float railW, float titleSize, float titleLineHeight,
                            int descMaxLines, Func<ColorF> accent, Action play)
        => RailFrame(spec.Identity.Kind, railW, RailColumn(spec, acts, railW, titleSize, titleLineHeight, descMaxLines, accent, play));

    /// <summary>The rail's frame: the layer fill and, the LAST resort once the text has given, the rail's own scroller.
    /// The Liked arm differs in exactly ONE property — no layer fill (ch 03 §9). Holds the loaded column, or the skeleton
    /// boundary that swaps to it (<c>FrameHost.RailRegion</c>) — outside the boundary, so the fill never blinks.</summary>
    static Element RailFrame(DetailKind kind, float railW, Element body) => new BoxEl
    {
        Direction = 1, Width = railW, Shrink = 0f, ClipToBounds = true,
        Fill = kind == DetailKind.Liked ? ColorF.Transparent : Tok.FillLayerDefault,
        Children = [ScrollView(body) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, Width = railW }],
    };

    /// <summary>The rail's loaded column: cover · eyebrow (+ a show's badges) / owner / an episode's show link · title ·
    /// attribution · rating · meta · an episode's badges · ledger · daylist · chart · CTA (primary + the fixed group or the
    /// page's satellites) · topics · prerelease · release panel · description · liked facts. The identity rows are
    /// <see cref="RailLayout.RowsFor"/>'s decisions; <see cref="RailSkeletonColumn"/> reserves the same rows
    /// (<see cref="Skeleton.RailPlanFor"/>) in the same order at the same widths, so the swap is a dissolve and never a
    /// reflow. With none of the podcast slots declared the column is exactly the pre-podcast one.</summary>
    static Element RailColumn(FrameSpec spec, FrameActions acts, float railW, float titleSize, float titleLineHeight,
                              int descMaxLines, Func<ColorF> accent, Action play)
    {
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        float cover = RailCoverEdge(railW);
        ColorF accentColor = accent();
        var kids = new List<Element>(16);

        // The CTA's pieces first: how many FABs it carries is a row decision (it sizes the CTA's lines) as well as its
        // children. A page's Satellites replace the fixed group; no Shuffle either way (the command bar's, W27).
        Element[]? satellites = slots.Satellites?.Invoke();
        List<Element>? fabs = satellites is null ? RailFabs(id, cfg.Heart != HeartMode.None, acts, accent) : null;
        var presence = PresenceOf(slots, satellites?.Length ?? 0);
        bool lead = RailLayout.LeadsWithAttribution(id.Kind, presence);
        bool blurb = (id.EditableMetadata && slots.Description is not null) || id.DescriptionHtml is { Length: > 0 };
        var rows = RailLayout.RowsFor(id.Kind, cfg.Badges, id.Eyebrow.Length > 0, id.OwnerName is { Length: > 0 },
            id.Artists is { Count: > 0 }, id.Meta is { Length: > 0 } || id.MetaLoading, fabs?.Count ?? 0, blurb, descMaxLines,
            presence);

        // Cover: the column's ANCHOR — keyed, never animated. The drag source sits on the FRAMING box so an editable
        // cover's own file-drop target inside it is untouched.
        kids.Add(new BoxEl
        {
            Key = "rail:cover",
            Width = cover, Height = cover, Corners = CornerRadius4.All(Radii.Card),
            Shadow = Elevation.Card, ClipToBounds = true,
            Draggable = acts.CoverDrag is { } drag ? Drag.Source(drag) : null,
            OnClick = acts.CoverClick,
            Cursor = acts.CoverClick is null ? (CursorId?)null : CursorId.Hand,
            Children = [slots.Cover?.Invoke(cover)
                ?? Controls.Artwork(id.CoverUrl, cover, cover, Radii.Card, decodePx: RailCoverDecodePx, saturation: 1.18f)],
        });

        // The lead. The eyebrow asymmetry (W27): the rail shows it for TypeYear only (a show's badges share its line);
        // OwnerRow gets the owner block instead; an EPISODE is led by its show link (its Attribution slot, W4).
        if (rows.Eyebrow)
            kids.Add(LateRow("rail:eyebrow", rows.Badges == RailBadgeRow.BesideEyebrow && slots.Badges is { } badgesBeside
                ? EyebrowWithBadges(id.Eyebrow, badgesBeside(), cover)
                : EyebrowRun(id.Eyebrow) with { Width = cover }));
        if (rows.Owner)
            kids.Add(lead && slots.Attribution is { } showLink
                ? LateRow("rail:lead", showLink(cover))
                : LateRow("rail:owner", slots.Attribution?.Invoke(cover) ?? OwnerBlock(id, cover)));
        if (rows.Badges == RailBadgeRow.OwnRow && slots.Badges is { } badgesOwn)
            kids.Add(SlotRow("rail:badges", badgesOwn(), RailLayout.BadgeHeight));

        kids.Add(Row("rail:title", slots.Title?.Invoke(titleSize, titleLineHeight)
            ?? Design.Type.DetailHero(id.Title) with
            {
                Size = titleSize, MinSize = 18f, Weight = 600, Width = cover, LineHeight = titleLineHeight,
                Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
            }));

        // Attribution: an album's billed artists; a podcast's Attribution slot (a show's publisher line).
        if (rows.Artists)
            kids.Add(LateRow("rail:artists", slots.Attribution?.Invoke(cover)
                ?? (id.Artists is { Count: > 0 } billed ? BilledArtists(billed, cover) : new BoxEl())));
        if (rows.Rating && slots.Rating is { } rating) kids.Add(SlotRow("rail:rating", rating(cover), RailLayout.RatingHeight));

        // Meta: playlist / Liked / the PODCAST family (a show has no facts panel; 0.2.9 built its publisher line and never
        // drew it, ch 09 §9.4; an episode states its date · length).
        if (rows.Meta) kids.Add(LateRow("rail:meta", MetaRow(id, cover, maxLines: 2)));
        if (rows.Badges == RailBadgeRow.AfterMeta && slots.Badges is { } badgesLate)
            kids.Add(SlotRow("rail:badges", badgesLate(), RailLayout.BadgeHeight));
        if (rows.Ledger && slots.Ledger is { } ledger) kids.Add(SlotRow("rail:ledger", ledger(cover), RailLayout.LedgerHeight));

        if (id.DaylistExpiresAtMs > 0 && slots.Pulse is { } pulse) kids.Add(LateRow("rail:daylist", pulse()));
        if (id.ChartNewEntries > 0) kids.Add(LateRow("rail:chart", ChartCaption(id, slots)));

        // CTA cluster: the primary (the page's, else Play), then the fixed FAB GROUP that wraps as a unit — or the page's
        // satellites, wrapping one by one.
        Element primary = slots.Primary is { } primaryOf ? primaryOf(accent) : PlayPill(accent, play);
        kids.Add(satellites is not null
            ? SatelliteCta("rail:cta", primary, satellites, RailLayout.CtaTopMargin)
            : new BoxEl
            {
                Key = "rail:cta", Layout = Shove,
                Direction = 0, Wrap = true, Gap = RailLayout.CtaGap, AlignItems = FlexAlign.Center,
                Margin = new Edges4(0f, RailLayout.CtaTopMargin, 0f, 0f),
                Children =
                [
                    primary,
                    new BoxEl { Direction = 0, Gap = RailLayout.FabGap, AlignItems = FlexAlign.Center, Children = fabs!.ToArray() },
                ],
            });

        if (rows.Topics && slots.Topics is { } topics) kids.Add(LateRow("rail:topics", topics(cover)));

        // The short About block (podcast-episode-peek §1): its own late row, because the description lands after the
        // header facts do — an empty body renders an empty box and reserves nothing.
        if (slots.About is { } about) kids.Add(LateRow("rail:about", about(cover)));

        if (id.UpcomingAtUnixSeconds > 0 && slots.PreRelease is { } pre) kids.Add(LateRow("rail:prerelease", pre()));

        if (cfg.Badges == BadgeStyle.TypeYear && slots.ReleasePanel is { } release)
            kids.Add(ReleaseRow("rail:release", release(false)));

        if (descMaxLines > 0)
        {
            if (id.EditableMetadata && slots.Description is { } editDescription)
                kids.Add(LateRow("rail:desc", editDescription(cover)));
            else if (id.DescriptionHtml is { Length: > 0 } html)
                kids.Add(LateRow("rail:desc", Controls.RichText(html, 12f, Tok.TextSecondary, accentColor, cover, descMaxLines, s_navRoute)));
        }

        // Row, not LateRow: the facts panel owns its own entrance.
        if (slots.LikedFacts is { } facts) kids.Add(Row("rail:likedfacts", facts(cover)));

        return RailColumnBox(railW, kids.ToArray());
    }

    static Element RailColumnBox(float railW, Element[] rows) => new BoxEl
    {
        Direction = 1, Gap = RailGap, Width = railW, Shrink = 0f,
        Padding = new Edges4(RailSidePadL, RailLayout.PadTop, RailSidePadR, RailLayout.PadBottom),
        Children = rows,
    };

    /// <summary>The rail's FIXED FAB group — the heart (a Save / Follow kind), Share, and the ⋯ every kind but the album
    /// carries. What a page's <see cref="FrameSlots.Satellites"/> replace.</summary>
    static List<Element> RailFabs(Identity id, bool heart, FrameActions acts, Func<ColorF> accent)
    {
        var fabs = new List<Element>(3);
        if (heart)
        {
            string saveUri = SaveTargetOf(id).Text;
            string saveName = id.Title;
            fabs.Add(Embed.Comp(() => new Controls.SaveButton { Uri = saveUri, Name = saveName, Glyph = 16f, Box = RailFabSize, Accent = accent })
                with { Key = "save:" + saveUri });
        }
        if (ShareActionFor(id) is { } share)
            fabs.Add(Controls.Named(Fab(Icons.Share, share), Loc.Get(Strings.Menu.Share)));
        // ch 05 parity 15: an album's rail has no ⋯ — its overflow is the vertical hero's alone.
        if (acts.More is { } more && id.Kind != DetailKind.Album)
            fabs.Add(MoreButton(more, RailFabSize, 16f, round: true) with { Key = "rail:more" });
        return fabs;
    }

    /// <summary>The SATELLITES arm of a CTA cluster (rail and vertical header): the primary, then each satellite, in ONE
    /// wrap 8 apart both ways — the prototype's <c>.cta</c> (W1, W4).</summary>
    static Element SatelliteCta(string key, Element primary, Element[] satellites, float topMargin)
    {
        var kids = new Element[1 + satellites.Length];
        kids[0] = primary;
        Array.Copy(satellites, 0, kids, 1, satellites.Length);
        return new BoxEl
        {
            Key = key, Layout = Shove,
            Direction = 0, Wrap = true, Gap = RailLayout.SatelliteGap, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, topMargin, 0f, 0f),
            Children = kids,
        };
    }

    /// <summary>A single-line podcast slot row: a <see cref="LateRow"/> held at least at its nominal height
    /// (<see cref="RailLayout"/>), so a body shorter than its reservation never pulls the rows under it up on reveal.</summary>
    static BoxEl SlotRow(string key, Element child, float nominal) => LateRow(key, child) with { MinHeight = nominal };

    /// <summary>A show's eyebrow line: the eyebrow run, then the page's badge row in the width the run leaves (a wrapping
    /// chip row wraps inside it, never under the eyebrow). NaN <paramref name="width"/> = the vertical header's column.</summary>
    static Element EyebrowWithBadges(string eyebrow, Element badges, float width) => new BoxEl
    {
        Direction = 0, Gap = RailLayout.BadgeGap, AlignItems = FlexAlign.Start, Width = width, MinWidth = 0f,
        Children =
        [
            EyebrowRun(eyebrow) with { Shrink = 0f },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Children = [badges] },
        ],
    };

    /// <summary>The rail-row presence a page's slots declare — the input <see cref="RailLayout.RowsFor"/> and
    /// <see cref="Skeleton.RailPlanFor"/> share. <paramref name="satelliteCount"/> is the length the Satellites builder
    /// returned (0 when the slot is absent).</summary>
    static RailSlotSet PresenceOf(FrameSlots s, int satelliteCount) => new(
        Attribution: s.Attribution is not null, Badges: s.Badges is not null, Rating: s.Rating is not null,
        Ledger: s.Ledger is not null, Topics: s.Topics is not null, Satellites: s.Satellites is not null,
        SatelliteCount: s.Satellites is null ? 0 : satelliteCount);

    /// <summary>The rail's LOADING column — the shimmer source of <c>FrameHost.RailRegion</c>, derived by the engine into
    /// bars: the same rows as <see cref="RailColumn"/> (<see cref="Skeleton.RailPlanFor"/> decides which, per kind and
    /// badge style and the rail slots the page declared) at the same widths, gap and padding — its nominal height is
    /// <see cref="RailLayout.HeightOf"/> of that plan. Unfilled boxes; a cover already known (a shelf card's 256 bucket)
    /// is painted for real and exempted from the deriver, as the hero's skeleton does.</summary>
    static Element RailSkeletonColumn(FrameSpec spec, float railW, float titleLineHeight, int descMaxLines)
    {
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        float cover = RailCoverEdge(railW);
        // Satellites are COUNTED (the builder only makes elements; they are dropped here) so the CTA wraps as the reveal will.
        int satelliteCount = slots.Satellites is { } satellitesOf ? satellitesOf().Length : 0;
        var plan = Skeleton.RailPlanFor(id.Kind, cfg.Badges, cfg.Heart != HeartMode.None, descMaxLines,
                                        PresenceOf(slots, satelliteCount));
        var kids = new List<Element>(12) { SkeletonCover(id.CoverUrl, cover) };

        if (plan.Eyebrow)
        {
            Element eyebrow = Bar(Skeleton.BarWidth(cover, Skeleton.EyebrowFraction), VerticalLayout.EyebrowRowHeight);
            kids.Add(plan.Badges == RailBadgeRow.BesideEyebrow
                ? new BoxEl
                {
                    Direction = 0, Gap = RailLayout.BadgeGap, AlignItems = FlexAlign.Start, MaxWidth = cover,
                    Children = [eyebrow, Bar(Skeleton.BadgeBarWidth, RailLayout.BadgeHeight)],
                }
                : eyebrow);
        }
        if (plan.Owner)
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MaxWidth = cover,
                Children =
                [
                    new BoxEl { Width = Skeleton.OwnerAvatar, Height = Skeleton.OwnerAvatar, Shrink = 0f, Corners = CornerRadius4.All(Skeleton.OwnerAvatar / 2f) },
                    Bar(Skeleton.BarWidth(cover - Skeleton.OwnerAvatar - Spacing.S, Skeleton.AttributionFraction), VerticalLayout.AttributionRowHeight),
                ],
            });
        if (plan.Badges == RailBadgeRow.OwnRow) kids.Add(Bar(Skeleton.BadgeBarWidth, RailLayout.BadgeHeight));
        kids.Add(Lines(cover, titleLineHeight, plan.TitleLines, Skeleton.TitleLastLineFraction));
        if (plan.Artists) kids.Add(Bar(Skeleton.BarWidth(cover, Skeleton.AttributionFraction), VerticalLayout.AttributionRowHeight));
        if (plan.Rating) kids.Add(Bar(Skeleton.BarWidth(cover, Skeleton.RatingFraction), RailLayout.RatingHeight));
        if (plan.Meta) kids.Add(Bar(Skeleton.BarWidth(cover, Skeleton.MetaFraction), VerticalLayout.MetaRowHeight));
        if (plan.Badges == RailBadgeRow.AfterMeta) kids.Add(Bar(Skeleton.BadgeBarWidth, RailLayout.BadgeHeight));
        if (plan.Ledger)
            kids.Add(new BoxEl
            {
                Direction = 1, Gap = RailLayout.LedgerGap, Width = cover,
                Children =
                [
                    Bar(cover, RailLayout.LedgerBarHeight),
                    Bar(Skeleton.BarWidth(cover, Skeleton.LedgerLineFraction), RailLayout.LedgerLineHeight),
                ],
            });

        float fabEdge = plan.Satellites ? RailLayout.SatelliteSize : RailFabSize;
        var fabs = new Element[Math.Max(0, plan.Fabs)];
        for (int i = 0; i < fabs.Length; i++)
            fabs[i] = new BoxEl { Width = fabEdge, Height = fabEdge, Shrink = 0f, Corners = CornerRadius4.All(fabEdge / 2f) };
        Element pill = new BoxEl { Width = Skeleton.PlayPillWidth, Height = Controls.PillHeight, Shrink = 0f, Corners = CornerRadius4.All(Controls.PillHeight / 2f) };
        kids.Add(plan.Satellites
            ? SatelliteCta("skel:cta", pill, fabs, RailLayout.CtaTopMargin)
            : new BoxEl
            {
                Direction = 0, Wrap = true, Gap = RailLayout.CtaGap, AlignItems = FlexAlign.Center,
                Margin = new Edges4(0f, RailLayout.CtaTopMargin, 0f, 0f),
                Children =
                [
                    pill,
                    new BoxEl { Direction = 0, Gap = RailLayout.FabGap, AlignItems = FlexAlign.Center, Children = fabs },
                ],
            });
        if (plan.Topics)
        {
            var topicLines = new Element[RailLayout.TopicLines];
            for (int i = 0; i < topicLines.Length; i++)
                topicLines[i] = Bar(Skeleton.LineWidth(cover, i, RailLayout.TopicLines, Skeleton.DescriptionLastLineFraction),
                                    RailLayout.TopicLineHeight);
            kids.Add(new BoxEl { Direction = 1, Gap = RailLayout.TopicLineGap, Width = cover, Children = topicLines });
        }
        if (plan.DescriptionLines > 0)
            kids.Add(Lines(cover, VerticalLayout.DescriptionLineHeight, plan.DescriptionLines, Skeleton.DescriptionLastLineFraction));

        return RailColumnBox(railW, kids.ToArray());
    }

    /// <summary>The skeleton's cover slot: the REAL preview when a url is already known, exempted from the deriver with a
    /// self-override; else the reserved card-cornered square the deriver paints as a bar.</summary>
    static Element SkeletonCover(string? coverUrl, float edge)
    {
        if (coverUrl is { Length: > 0 } url)
        {
            Element preview = Controls.Artwork(url, edge, edge, Radii.Card, decodePx: RailCoverDecodePx, saturation: 1.18f);
            return preview.Skel(preview);
        }
        return new BoxEl { Width = edge, Height = edge, Shrink = 0f, Corners = CornerRadius4.All(Radii.Card) };
    }

    /// <summary>The collapsed rail (WP-κ): a 96-DIP identity strip — cover, two-line title, chevron. The WHOLE cover and
    /// the chevron both expand (no second hit target inside 48-80 DIP).</summary>
    public static Element CompactRail(FrameSpec spec, float width, Action expand) => CompactRailCore(spec, width, expand);

    static Element CompactRailCore(FrameSpec spec, float stripW, Action expand)
        => CompactRailStrip(spec, stripW, expand, CompactRailIdentity(spec, CompactCover(stripW), expand));

    static float CompactCover(float stripW) => MathF.Max(48f, stripW - Spacing.S - Spacing.S);

    /// <summary>The strip's loading identity: the reserved cover square (or the known preview) over two short title
    /// runs — what <see cref="CompactRailIdentity"/> lays out, unfilled.</summary>
    static Element CompactRailSkeleton(FrameSpec spec, float cover) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Width = cover,
        Children = [SkeletonCover(spec.Identity.CoverUrl, cover), Lines(cover, 16f, Skeleton.RailTitleLines, Skeleton.TitleLastLineFraction)],
    };

    /// <summary>The strip's identity: the cover (a hit target that expands) and the two-line title.</summary>
    static Element CompactRailIdentity(FrameSpec spec, float cover, Action expand)
    {
        var id = spec.Identity;
        Element coverHit = new BoxEl
        {
            Key = "compact:cover",
            Width = cover, Height = cover, Corners = CornerRadius4.All(Radii.Card),
            Shadow = Elevation.Card, ClipToBounds = true, Shrink = 0f,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = expand,
            Children = [spec.Slots.CompactCover?.Invoke(cover)
                ?? Controls.Artwork(id.CoverUrl, cover, cover, Radii.Card, decodePx: RailCoverDecodePx, saturation: 1.18f)],
        };
        Element title = new BoxEl
        {
            Key = "compact:title", Width = cover, Direction = 1,
            Children =
            [
                new TextEl(id.Title)
                {
                    Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary,
                    Width = cover, MinWidth = 0f, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                    Wrap = TextWrap.WrapWholeWords,
                },
            ],
        };
        return new BoxEl { Key = "compact:identity", Direction = 1, Gap = Spacing.S, Width = cover, Children = [coverHit, title] };
    }

    /// <summary>The 96-DIP strip around its identity (loaded, or the skeleton boundary that swaps to it): the layer fill,
    /// the spacer and the expand chevron — the chevron is chrome and stays real while the identity loads.</summary>
    static Element CompactRailStrip(FrameSpec spec, float stripW, Action expand, Element identity)
    {
        const float pad = Spacing.S;
        float cover = CompactCover(stripW);
        Element expandHit = new BoxEl
        {
            Key = "compact:expand",
            Width = cover, Height = 28f, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = expand,
            Children = [Icon(Icons.ChevronRight, 14f, Tok.TextSecondary)],
        };
        Element strip = new BoxEl
        {
            Key = "detail-rail-compact",
            Direction = 1, Width = stripW, Shrink = 0f, Gap = Spacing.S,
            Padding = new Edges4(pad, Spacing.L, pad, Spacing.L),
            Fill = Tok.FillLayerDefault, ClipToBounds = true,
            Children =
            [
                identity,
                new BoxEl { Key = "compact:spacer", Grow = 1f, MinHeight = 0f },
                expandHit,
            ],
        };
        return ToolTip.Wrap(strip, spec.Identity.Title);
    }

    /// <summary>W25: the VERTICAL show header — the third composition, never the hero system. Cover 140 at saturation
    /// 1.0 (the one arm without the 1.18 boost), the PageHero title, Play + heart + a bare Share FAB, no toolbar. The
    /// podcast slots land as the prototype's narrow pane does: badges / rating beside the cover in the info column, the
    /// ledger and the CTA (a page's primary + satellites) full width under it, no topics and no blurb. The rows are the
    /// rail's own decision (<see cref="RailLayout.RowsFor"/>); every slot width here is NaN (fill the column).</summary>
    public static Element ShowHeader(FrameSpec spec, Func<ColorF> accent)
        => ShowHeaderCore(spec, spec.Actions, accent, DefaultPlay(spec.Identity.Subject));

    static Element ShowHeaderCore(FrameSpec spec, FrameActions acts, Func<ColorF> accent, Action play)
    {
        const float coverSize = 140f;
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        Element[]? satellites = slots.Satellites?.Invoke();
        var presence = PresenceOf(slots, satellites?.Length ?? 0);
        bool lead = RailLayout.LeadsWithAttribution(id.Kind, presence);
        var rows = RailLayout.RowsFor(id.Kind, cfg.Badges, id.Eyebrow.Length > 0, id.OwnerName is { Length: > 0 },
            id.Artists is { Count: > 0 }, id.Meta is { Length: > 0 } || id.MetaLoading, fabs: 0, description: false,
            descriptionMaxLines: 0, presence);
        var info = new List<Element>(6);

        // The info column already clamps (Grow / Basis 0), so the eyebrow run needs no explicit width here.
        if (rows.Eyebrow)
            info.Add(LateRow("hdr:eyebrow", rows.Badges == RailBadgeRow.BesideEyebrow && slots.Badges is { } badgesBeside
                ? EyebrowWithBadges(id.Eyebrow, badgesBeside(), float.NaN)
                : EyebrowRun(id.Eyebrow)));
        if (rows.Owner)
            info.Add(lead && slots.Attribution is { } showLink
                ? LateRow("hdr:lead", showLink(600f))
                : LateRow("hdr:owner", slots.Attribution?.Invoke(600f) ?? OwnerBlock(id, 600f)));
        if (rows.Badges == RailBadgeRow.OwnRow && slots.Badges is { } badgesOwn)
            info.Add(SlotRow("hdr:badges", badgesOwn(), RailLayout.BadgeHeight));

        info.Add(Row("hdr:title", slots.Title?.Invoke(28f, 36f)
            ?? Design.Type.PageHero(id.Title) with
            {
                Size = 28f, LineHeight = 36f, Weight = 600,
                Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
            }));
        if (rows.Artists)
            info.Add(LateRow("hdr:artists", slots.Attribution?.Invoke(600f)
                ?? (id.Artists is { Count: > 0 } billed ? BilledArtists(billed, 600f) : new BoxEl())));
        if (rows.Rating && slots.Rating is { } rating) info.Add(SlotRow("hdr:rating", rating(float.NaN), RailLayout.RatingHeight));
        if (rows.Meta) info.Add(LateRow("hdr:meta", MetaRow(id, float.NaN, maxLines: 1)));
        if (rows.Badges == RailBadgeRow.AfterMeta && slots.Badges is { } badgesLate)
            info.Add(SlotRow("hdr:badges", badgesLate(), RailLayout.BadgeHeight));

        var coverRow = new BoxEl
        {
            Key = "hdr:cover",
            Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,   // centred: balanced, never a wedge
            Children =
            [
                new BoxEl
                {
                    Width = coverSize, Height = coverSize, Corners = CornerRadius4.All(Radii.Card),
                    Shadow = Elevation.Card, ClipToBounds = true, Shrink = 0f,
                    Draggable = acts.CoverDrag is { } drag ? Drag.Source(drag) : null,
                    OnClick = acts.CoverClick,
                    Cursor = acts.CoverClick is null ? (CursorId?)null : CursorId.Hand,
                    Children = [slots.Cover?.Invoke(coverSize)
                        ?? Controls.Artwork(id.CoverUrl, coverSize, coverSize, Radii.Card, decodePx: RailCoverDecodePx)],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS, Children = info.ToArray() },
            ],
        };

        // The one fixed CTA cluster that differs: no More, a bare Share FAB. A page's primary replaces Play; its
        // satellites replace the heart + Share and wrap one by one.
        Element primary = slots.Primary is { } primaryOf ? primaryOf(accent) : PlayPill(accent, play);
        Element ctaRow;
        if (satellites is not null)
            ctaRow = SatelliteCta("hdr:play", primary, satellites, topMargin: 0f);
        else
        {
            var cta = new List<Element>(3) { primary };
            if (cfg.Heart != HeartMode.None)
            {
                string saveUri = SaveTargetOf(id).Text;
                string saveName = id.Title;
                cta.Add(Embed.Comp(() => new Controls.SaveButton { Uri = saveUri, Name = saveName, Glyph = 16f, Box = RailFabSize, Accent = accent })
                    with { Key = "save:" + saveUri });
            }
            if (ShareActionFor(id) is { } share) cta.Add(Controls.Named(Fab(Icons.Share, share), Loc.Get(Strings.Menu.Share)));
            ctaRow = new BoxEl
            {
                Key = "hdr:play", Layout = Shove,
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Wrap = true,
                Children = cta.ToArray(),
            };
        }

        var headerKids = new List<Element>(5) { coverRow };
        // The ledger spans the header (the narrow pane's full-basis row), above the CTA.
        if (rows.Ledger && slots.Ledger is { } ledger) headerKids.Add(SlotRow("hdr:ledger", ledger(float.NaN), RailLayout.LedgerHeight));
        headerKids.Add(ctaRow);
        if (id.UpcomingAtUnixSeconds > 0 && slots.PreRelease is { } pre) headerKids.Add(LateRow("hdr:prerelease", pre()));
        if (cfg.Badges == BadgeStyle.TypeYear && slots.ReleasePanel is { } release) headerKids.Add(ReleaseRow("hdr:release", release(false)));

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Shrink = 0f,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.S),
            Children = headerKids.ToArray(),
        };
    }

    // ── shared row bodies (rail, show header, hero) ──

    /// <summary>The "N songs · 2 hr 59 min" caption — or, while the count is still loading, a shimmer bar of that
    /// caption's SHAPE in the same slot ("0 songs · 1 min" is a count the page does not have).</summary>
    static Element MetaRow(Identity id, float width, int maxLines)
    {
        if (!id.MetaLoading && id.Meta is { Length: > 0 } meta)
        {
            var line = Design.Type.TrackMeta(meta) with
            {
                MaxLines = maxLines, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis,
            };
            return float.IsNaN(width) ? line : line with { Width = width };
        }
        return new SkelRegionEl(
            Pending: s_true, Failed: s_false, Content: s_metaShimmer, ShimmerSource: null, OnFailed: null,
            Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
    }

    static readonly Func<bool> s_true = static () => true;
    static readonly Func<bool> s_false = static () => false;
    static readonly Func<Element> s_metaShimmer = static () => Design.Type.TrackMeta(MetaShimmerText) with { MaxLines = 1 };

    /// <summary>A rich-text anchor's route key → navigation.</summary>
    static readonly Action<string> s_navRoute = static key => Shell.GoTo(Shell.Parse(key));

    /// <summary>The chart playlist's "N new entries · date" caption (W28) — the page's slot, else the default caption.</summary>
    static Element ChartCaption(Identity id, FrameSlots slots)
    {
        if (slots.Chart is { } chart) return chart();
        string text = Strings.Detail.ChartNewEntries(id.ChartNewEntries,
            Track.Format.ChartUpdatedDateLabel(id.ChartUpdatedAtMs, Store.ToUnix(Entities.Now)));
        return Ui.Caption(text) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
    }

    /// <summary>The default owner row (a playlist page supplies its inline-edit owner block through the Attribution
    /// slot): the collaborator pile when it applies — WIDTH-keyed, the one geometry-keyed remount — else the owner.</summary>
    static Element OwnerBlock(Identity id, float width)
    {
        if (id.Collaborators is { Count: > 0 } collaborators && Text.ShowCollaborators(collaborators.Count, id.Collaborative))
            return new BoxEl
            {
                Key = "pl-collab:" + ((int)width).ToString(CultureInfo.InvariantCulture),
                Direction = 0, AlignItems = FlexAlign.Center, MaxWidth = width,
                Children = [Controls.FacePile(collaborators)],
            };
        string owner = id.OwnerName ?? "";
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MaxWidth = width,
            Children =
            [
                PersonPicture.Create("", 24f, displayName: owner, imageSourcePath: id.OwnerImageUrl),
                new TextEl(owner)
                {
                    Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary,
                    MinWidth = 0f, Shrink = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    /// <summary>The default billed-artist row: the face pile, then ONE ellipsised accent run to the lead artist.</summary>
    static Element BilledArtists(IReadOnlyList<Controls.Face> artists, float width)
    {
        var lead = artists[0];
        var names = new System.Text.StringBuilder(64);
        for (int i = 0; i < artists.Count; i++)
        {
            if (i > 0) names.Append(", ");
            names.Append(artists[i].Name);
        }
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MaxWidth = width,
            Children =
            [
                Controls.FacePile(artists, maxVisible: 3),
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f,
                    OnClick = lead.OnClick,
                    Cursor = lead.OnClick is null ? (CursorId?)null : CursorId.Hand,
                    Role = lead.OnClick is null ? AutomationRole.None : AutomationRole.Hyperlink,
                    Children =
                    [
                        new TextEl(names.ToString())
                        {
                            Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.AccentTextPrimary,
                            Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
            ],
        };
    }

    static EntityUri SaveTargetOf(Identity id) => id.SaveTarget.IsValid ? id.SaveTarget : id.Subject;

    /// <summary>Copy the page's web link with the confirmation toast — only when a clipboard seam exists.</summary>
    static Action? ShareActionFor(Identity id)
    {
        if (id.ShareUrl is not { Length: > 0 } url) return null;
        return () =>
        {
            if (Actions.Services.Clipboard is not { } copy) return;
            copy(url);
            _ = Notify.Say(Loc.Get(Strings.Menu.LinkCopied), InfoBarSeverity.Success);
        };
    }

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

    // ══ 5. NOTICE, PILL, SATELLITE, FAB, MORE ════════════════════════════════════════════════════════════════════════

    /// <summary>W15: bad news is a strip, never an error page. One Informational bar; zero-height (and hit-test free)
    /// while there is nothing to say. <paramref name="showMinifiedAlbum"/> is false on the full page, which self-heals a
    /// thin tracklist; the minified verdict carries no action.</summary>
    public static Element NoticeBar(DetailNotice notice, bool showMinifiedAlbum, Action? goLibrary)
    {
        if (notice == DetailNotice.None || (notice == DetailNotice.MinifiedAlbum && !showMinifiedAlbum))
            return new BoxEl { Key = "detail-notice", Height = 0f, HitTestVisible = false };

        string message = notice switch
        {
            DetailNotice.Deleted => Loc.Get(Strings.Detail.Notice.Deleted),
            DetailNotice.AccessRevoked => Loc.Get(Strings.Detail.Notice.AccessRevoked),
            DetailNotice.MinifiedAlbum => Loc.Get(Strings.Detail.Notice.MinifiedAlbum),
            _ => Loc.Get(Strings.Detail.Notice.CreateFailed),
        };
        // The one action is a way OUT, never a retry. There is no bare "library" destination — the albums section.
        Element? action = notice == DetailNotice.MinifiedAlbum || goLibrary is null
            ? null
            : Button.Create(Loc.Get(Strings.Detail.Notice.GoToLibrary), goLibrary, ButtonAppearance.Subtle, ControlSize.Small);
        return new BoxEl
        {
            Key = "detail-notice",
            Direction = 1, Padding = new Edges4(16f, 8f, 16f, 4f), Shrink = 0f,
            Children = [InfoBar.Create(InfoBarSeverity.Informational, message, "", isClosable: false, actionButton: action)],
        };
    }

    /// <summary>The brush budget every accent consumer shares — a VALUE under reduced motion (0 ⇒ an instant swap).</summary>
    static float AccentTransitionMs => Design.Reduced ? 0f : Design.Motion.Standard;

    /// <summary>The media Play capsule at the detail geometry (36 floor, 18/6/18/7, Bold, accent fill + picked ink).
    /// The rest fill is BOUND to the caller's accent thunk, so a grading landing cross-fades the pill instead of
    /// snapping it; hover/pressed shades and the ink re-push with the parent render. <paramref name="label"/> /
    /// <paramref name="glyph"/> (default "Play" / ▶) make it a page's <see cref="FrameSlots.Primary"/>: "Resume · 17 min
    /// left", "Play latest", Follow with <c>Icons.Add</c>.</summary>
    public static Element PlayPill(Func<ColorF> accent, Action onClick, string? label = null, string? glyph = null)
        => Controls.Accent(label ?? Loc.Get(Strings.Detail.Play), accent(), onClick, glyph)
            with { Fill = accent, BrushTransitionMs = AccentTransitionMs };

    /// <summary>A page's SATELLITE FAB (the <see cref="FrameSlots.Satellites"/> building block): the rail's round FAB at
    /// <see cref="RailLayout.SatelliteSize"/>, named for its tooltip. A null <paramref name="onClick"/> draws it DISABLED
    /// (plan D-10: a write with no captured endpoint ships visible, with its reason as the name). Key it at the call site.</summary>
    public static Element RailSatellite(string glyph, string name, Action? onClick)
    {
        BoxEl fab = Fab(glyph, onClick, RailLayout.SatelliteSize, 16f);
        return Controls.Named(onClick is null ? fab with { IsEnabled = false, Cursor = null } : fab, name);
    }

    /// <summary>The heart as a satellite: <c>Controls.SaveButton</c> at <see cref="RailLayout.SatelliteSize"/>, inked by the
    /// page's ambient accent (the frame provides it), keyed on the uri (props freeze at mount).</summary>
    public static Element RailSatelliteSave(string uri, string name)
        => Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = name, Glyph = 16f, Box = RailLayout.SatelliteSize })
            with { Key = "save:" + uri };

    /// <summary>The ⋯ as a satellite: the frame's lazily-built More menu at <see cref="RailLayout.SatelliteSize"/>.</summary>
    public static Element RailSatelliteMore(Func<ContextMenuModel?> menu)
        => MoreButton(menu, RailLayout.SatelliteSize, 16f, round: true) with { Key = "rail:more" };

    /// <summary>The hero's quiet secondary action: 32 × 32, transparent at rest, FillSubtleSecondary on hover, the 83 ms
    /// brush, Radii.Control, the Standard scale tier, and a tooltip naming it.</summary>
    public static Element Satellite(string glyph, string tooltip, Action? onClick, float size = 32f, float glyphSize = 14f)
        => ToolTip.Wrap(SatelliteBox(glyph, onClick, size, glyphSize), tooltip);

    static BoxEl SatelliteBox(string glyph, Action? onClick, float size, float glyphSize) => new()
    {
        Direction = 0, Width = size, Height = size, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        BrushTransitionMs = Design.Motion.Faster,
        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = onClick,
        HoverScale = Design.Motion.ScaleStandard.Hover, PressScale = Design.Motion.ScaleStandard.Press,
        Children = [Icon(glyph, glyphSize, Tok.TextSecondary)],
    };

    /// <summary>The rail's 40-DIP FAB: round, the Emphatic scale tier, the Subtle interaction fill. <paramref name="size"/>
    /// is the rail's edge by default; the library panes ask for their own 36 so the ⋯ matches the command circles beside it
    /// (<c>Album.CommandCircle</c>) instead of standing 4 DIP taller than every one of them.</summary>
    static BoxEl Fab(string glyph, Action? onClick, float size = RailFabSize, float glyphSize = 16f) => new BoxEl
    {
        Width = size, Height = size, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(size / 2f),
        HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [Icon(glyph, glyphSize, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle);

    /// <summary>The More button: its flyout is built lazily AT OPEN from the newest menu factory. <c>internal</c> because the
    /// library's panes (<c>Album.Pane</c>, <c>Artist.Reader</c>) head their command rows with the SAME ⋯ — one menu host, so
    /// a surface never grows a second one (library rework §5.0).</summary>
    internal static Element MoreButton(Func<ContextMenuModel?> menu, float size, float glyphSize, bool round)
        => Embed.Comp(new MoreProps(size, glyphSize, round, menu), static () => new MoreHost());

    sealed record MoreProps(float Size, float Glyph, bool Round, Func<ContextMenuModel?> Menu)
    {
        public bool Equals(MoreProps? o) => o is not null && Size == o.Size && Glyph == o.Glyph && Round == o.Round;
        public override int GetHashCode() => HashCode.Combine(Size, Glyph, Round);
    }

    static readonly ContextMenuOptions s_moreMenu = new()
    {
        PointerPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
        KeyboardPlacement = FlyoutPlacement.BottomEdgeAlignedRight,
    };

    sealed class MoreHost : Component, IPropsHost
    {
        MoreProps? _latest;
        readonly Signal<MoreProps?> _props = new(null);
        readonly Func<ContextMenuModel?> _menu;

        public MoreHost() => _menu = () => _latest?.Menu();

        public void ApplyProps(object props)
        {
            _latest = (MoreProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var p = _latest!;
            var overlay = UseContext(Overlay.Service);
            BoxEl button = (p.Round ? Fab(Icons.More, null, p.Size, p.Glyph) : SatelliteBox(Icons.More, null, p.Size, p.Glyph)) with
            {
                OnClick = null, ClickRequestsContext = true,
            };
            if (!Controls.IsNullOverlay(overlay)) button = ContextMenu.Attach(button, overlay, _menu, s_moreMenu);
            return ToolTip.Wrap(button, Loc.Get(Strings.Common.More));
        }
    }

    // ── the billed-artist attribution line ───────────────────────────────────────────────────────────────────────────

    /// <summary>The billed artists as one comma-joined run of 14/20/600 links (accent on hover), each one shrinkable and
    /// clipped so a long bill yields instead of pushing the meta line out. An empty bill is an EMPTY BOX, never a blank
    /// row of air: the header's gap must not open for an attribution that does not exist. A detail-frame piece, shared by
    /// the library's album pane, the show pane's publisher slot and the artist reader (library rework §5.5).</summary>
    internal static Element ArtistLine(IReadOnlyList<Controls.Face>? artists)
    {
        if (artists is not { Count: > 0 }) return new BoxEl();
        var kids = new Element[artists.Count * 2 - 1];
        for (int i = 0, k = 0; i < artists.Count; i++)
        {
            if (i > 0) kids[k++] = new TextEl(", ") { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextSecondary };
            var face = artists[i];
            var label = new TextEl(face.Name)
            {
                Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextSecondary, HoverColor = Tok.AccentTextPrimary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
            kids[k++] = face.OnClick is { } go
                ? new BoxEl { Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = go, Shrink = 1f, MinWidth = 0f, Children = [label] }
                : label;
        }
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true, Children = kids };
    }
}
