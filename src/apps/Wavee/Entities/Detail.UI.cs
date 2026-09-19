// ── Entities/Detail.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// THE SHARED DETAIL FRAME (ch 03 §1.2): Identity (+ Identity.For(Album)), FrameActions, FrameSlots, FrameSpec and Frame —
// the 4-mode responsive ladder with both anti-flicker fail-safes, the Hero page-layout override, the centred two-column
// row (MaxWidth 1600), the per-scope rail prefs + Splitter grip + resist fade + the 96-DIP compact strip, the notice
// strip, the page tone plane and shell tint leaves, the accent and the page-body drop target — plus the rail family
// (Rail, CompactRail, ShowHeader), NoticeBar, PlayPill, Satellite, the More button and the billed-artist ATTRIBUTION
// LINE every detail-frame hero and library pane heads its meta with (ArtistLine).
// The vertical hero, the context band, the skeleton band and the band helpers are the named partial Detail.UI.Hero.cs.
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
    /// equality is PRESENCE only, and the frame invokes the newest builder at render.</summary>
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

        internal int Mask()
            => (Cover is null ? 0 : 1) | (CompactCover is null ? 0 : 2) | (Title is null ? 0 : 4)
             | (Attribution is null ? 0 : 8) | (Description is null ? 0 : 16) | (Pulse is null ? 0 : 32)
             | (Chart is null ? 0 : 64) | (PreRelease is null ? 0 : 128) | (ReleasePanel is null ? 0 : 256)
             | (LikedFacts is null ? 0 : 512) | (Trailing is null ? 0 : 1024) | (Episodes is null ? 0 : 2048);

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
    const float RailMidW = 224f, RailNarrowW = 188f;                 // RailW(mode 1 / mode 2)
    const float RailForcePush = 44f, RailReExpand = 220f;            // the collapse detent
    const float RailCompactW = 96f;                                   // the collapsed identity strip
    const float GripStripCollapsedW = 20f;                            // the grip while collapsed (also a re-open gesture)
    const float TwoColumnHeroBandFraction = 0.55f;                    // the tone plane's synthetic band (hero-only is dead)
    const float TallWindowH = Design.Size.DesignH;                    // the 40/52 rail title at ≥ 900
    const float ShortWindowH = 760f;                                  // the 3-line description below 760
    const float RailSidePadL = Spacing.L, RailSidePadR = Spacing.S;   // 16 / 8
    const float RailGap = 14f;
    const float RailFabSize = 40f;
    const int RailCoverDecodePx = 256;                                // the shelf card's bucket — a warm texture on arrival
    const string MetaShimmerText = "00 songs · 0 hr 00 min";          // the shimmer SHAPE only; never painted as text

    /// <summary><c>DetailShell.RailW</c>: the breakpoint rail of modes 1/2, the config's own width at mode 0.</summary>
    static float RailWidthForMode(int mode, in Config cfg) => mode switch { 0 => cfg.RailWidth, 1 => RailMidW, _ => RailNarrowW };

    /// <summary><c>DetailRail.CoverEdge</c>: the rail cover fills the column less its side padding, floored at 80.</summary>
    static float RailCoverEdge(float railW) => MathF.Max(80f, railW - RailSidePadL - RailSidePadR);

    /// <summary>One persisted rail pair (width + collapsed), live. Seeded CLAMPED (ch 30 N4); the drag writes the width
    /// signal directly; RELEASE commits both to THIS scope's keys with ONE epoch bump.</summary>
    sealed class RailCell
    {
        public readonly RailScope Scope;
        public readonly Signal<float> Width;
        public readonly Signal<bool> Collapsed;
        public readonly Action Commit;
        public readonly Action Expand;
        public readonly Splitter.SplitterOptions Options;
        public readonly string GripKey;
        readonly Signal<float> _fade;

        public RailCell(RailScope scope, Signal<float> fade)
        {
            Scope = scope;
            _fade = fade;
            var keys = RailPolicy.KeysFor(scope);
            Width = new Signal<float>(RailPolicy.ClampStored(Platform.Settings.Get(keys.Width), scope));
            Collapsed = new Signal<bool>(Platform.Settings.Get(keys.Collapsed));
            Commit = CommitNow;
            Expand = ExpandNow;
            Options = new Splitter.SplitterOptions
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

        void CommitNow()
        {
            var keys = RailPolicy.KeysFor(Scope);
            Platform.Settings.Set(keys.Width, Width.Peek());
            // The ONE bump: every mounted (and parked) frame re-syncs its rails, so "Keep left-rail same size" moves them all.
            Prefs.DetailHero.Set(keys.Collapsed, Collapsed.Peek());
        }

        void ExpandNow()
        {
            Collapsed.Value = false;
            _fade.Value = 1f;
            Prefs.DetailHero.Set(RailPolicy.KeysFor(Scope).Collapsed, false);
        }

        /// <summary>Re-seed from the store, clamped exactly as at mount (the epoch effect, ch 03 §6).</summary>
        public void Resync()
        {
            var keys = RailPolicy.KeysFor(Scope);
            Width.Value = RailPolicy.ClampStored(Platform.Settings.Get(keys.Width), Scope);
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
        readonly Signal<float> _heroHeight = new(0f);
        readonly Signal<ColorF> _accent = new(ColorF.Transparent);
        readonly Signal<Design.PageAccent> _pageAccent = new(new Design.PageAccent(Tok.AccentTextPrimary, Tok.AccentDefault, ""));
        readonly Signal<ThemeKind> _theme = new(ThemeKind.Dark);
        readonly Action?[] _playAllCell = new Action?[1];
        readonly object _tintOwner = new();
        IReadSignal<Size2>? _viewport;
        bool _accentSeeded;

        // ── cached delegates (zero per-render allocation for the handlers the tree carries) ──
        readonly Action<RectF> _measure;
        readonly Func<ColorF> _accentFn;
        readonly Action _playAll;
        readonly Action _resyncRails;
        readonly Action _publishAccentTracked;
        readonly Action _publishTheme;
        readonly Func<int> _heightRungsCompute;
        readonly Func<bool> _railPending;

        // trampolines into _latest
        readonly Action _tShuffle, _tCoverClick, _tGoLibrary;
        readonly Func<ContextMenuModel?> _tMore;
        readonly Func<object?> _tCoverDrag;
        readonly Func<DragPayload, int?, bool> _tDeposit;
        readonly Func<float, Element> _sCover, _sCompactCover, _sAttribution, _sDescription, _sLikedFacts;
        readonly Func<float, float, Element> _sTitle;
        readonly Func<Element> _sPulse, _sChart, _sPreRelease, _sTrailing, _sTrailingVertical;
        readonly Func<bool, Element> _sReleasePanel, _sEpisodes;
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
        }

        /// <summary>Every re-push lands here (the reconciler calls it at mount and on every parent render).</summary>
        public void ApplyProps(object props)
        {
            var spec = (FrameSpec)props;
            _latest = spec;
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

            var shellSlot = UseContext(ShellMaterial.Slot);
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
                return Ctx.Provide(Design.AccentCtx.Slot, (IReadSignal<Design.PageAccent>?)_pageAccent, new BoxEl
                {
                    ZStack = true, Grow = 1f, OnBoundsChanged = _measure, ClipToBounds = true,
                    Children = [tint, tone, verticalPage],
                });
            }

            // ── modes 0/1/2: the centred two-column row [rail | grip | right] ──
            var rail = _rails[(int)RailPolicy.ScopeFor(cfg.RailScope, uniform)];
            bool resizable = RailPolicy.ResizableFor(cfg.RailResizable, mode);
            // Read unconditionally (a stable subscription) but honoured only where the grip that can undo it exists.
            bool collapsed = rail.Collapsed.Value && resizable;
            float railW = resizable ? rail.Width.Value : RailWidthForMode(mode, cfg);

            Element[] rowKids;
            if (collapsed)
            {
                // `right` keeps its Key across the collapse, so the table reconciles in place and keeps its scroll.
                rowKids = [CompactRailRegion(spec, RailCompactW, rail.Expand), Grip(rail, collapsedNow: true), right];
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
                rowKids = resizable ? [railFaded, Grip(rail, collapsedNow: false), right] : [railFaded, right];
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
                    { PlayAll = _playAll }
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
                    LikedFacts = s.LikedFacts is null ? null : _sLikedFacts,
                    Trailing = s.Trailing is null ? null : _sTrailing,
                    Episodes = s.Episodes is null ? null : _sEpisodes,
                };
            }
            return _trampSlots;
        }

        // ── the grip ──

        Element Grip(RailCell rail, bool collapsedNow) => new BoxEl
        {
            Key = "detail-rail-grip-strip",
            Width = collapsedNow ? GripStripCollapsedW : Splitter.StripW,
            Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Stretch,
            Children =
            [
                // Width writes are direct during the drag; RELEASE commits width + collapsed to THIS scope's pair.
                Splitter.Create(rail.Width, rail.Commit, rail.Options, collapsed: rail.Collapsed, fade: _railFade)
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
        }

        void ResyncRails()
        {
            for (int i = 0; i < _rails.Length; i++) _rails[i].Resync();
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

    /// <summary>The rail's loaded column: cover · eyebrow/owner · title · artists · meta · daylist · chart · CTA ·
    /// prerelease · release panel · description · liked facts. <see cref="RailSkeletonColumn"/> reserves the same rows in
    /// the same order at the same widths, so the swap is a dissolve and never a reflow.</summary>
    static Element RailColumn(FrameSpec spec, FrameActions acts, float railW, float titleSize, float titleLineHeight,
                              int descMaxLines, Func<ColorF> accent, Action play)
    {
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        float cover = RailCoverEdge(railW);
        ColorF accentColor = accent();
        var kids = new List<Element>(12);

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

        // The eyebrow asymmetry (W27): the rail shows it for TypeYear only; OwnerRow gets the owner block instead.
        if (cfg.Badges == BadgeStyle.TypeYear)
        {
            if (id.Eyebrow.Length > 0) kids.Add(LateRow("rail:eyebrow", EyebrowRun(id.Eyebrow) with { Width = cover }));
        }
        else if (cfg.Badges == BadgeStyle.OwnerRow && (slots.Attribution is not null || id.OwnerName is { Length: > 0 }))
        {
            kids.Add(LateRow("rail:owner", slots.Attribution?.Invoke(cover) ?? OwnerBlock(id, cover)));
        }

        kids.Add(Row("rail:title", slots.Title?.Invoke(titleSize, titleLineHeight)
            ?? Design.Type.DetailHero(id.Title) with
            {
                Size = titleSize, MinSize = 18f, Weight = 600, Width = cover, LineHeight = titleLineHeight,
                Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
            }));

        if (cfg.Badges == BadgeStyle.TypeYear && id.Artists is { Count: > 0 } artists)
            kids.Add(LateRow("rail:artists", slots.Attribution?.Invoke(cover) ?? BilledArtists(artists, cover)));

        // Meta: playlist / Liked / SHOW (a show has no facts panel; 0.2.9 built its publisher line and never drew it, ch 09 §9.4).
        if ((cfg.Badges != BadgeStyle.TypeYear || id.Kind == DetailKind.Show) && (id.Meta is { Length: > 0 } || id.MetaLoading))
            kids.Add(LateRow("rail:meta", MetaRow(id, cover, maxLines: 2)));

        if (id.DaylistExpiresAtMs > 0 && slots.Pulse is { } pulse) kids.Add(LateRow("rail:daylist", pulse()));
        if (id.ChartNewEntries > 0) kids.Add(LateRow("rail:chart", ChartCaption(id, slots)));

        // CTA cluster: Play + a FAB GROUP that wraps as a unit (no Shuffle here — it lives in the command bar, W27).
        var fabs = new List<Element>(3);
        if (cfg.Heart != HeartMode.None)
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
        kids.Add(new BoxEl
        {
            Key = "rail:cta", Layout = Shove,
            Direction = 0, Wrap = true, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
            Children =
            [
                PlayPill(accent, play),
                new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = fabs.ToArray() },
            ],
        });

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
        Padding = new Edges4(RailSidePadL, Spacing.XXL, RailSidePadR, Spacing.XXL),
        Children = rows,
    };

    /// <summary>The rail's LOADING column — the shimmer source of <c>FrameHost.RailRegion</c>, derived by the engine into
    /// bars: the same rows as <see cref="RailColumn"/> (<see cref="Skeleton.RailPlanFor"/> decides which, per kind and
    /// badge style) at the same widths, gap and padding. Unfilled boxes; a cover already known (a shelf card's 256 bucket)
    /// is painted for real and exempted from the deriver, as the hero's skeleton does.</summary>
    static Element RailSkeletonColumn(FrameSpec spec, float railW, float titleLineHeight, int descMaxLines)
    {
        var id = spec.Identity;
        var cfg = spec.Config;
        float cover = RailCoverEdge(railW);
        var plan = Skeleton.RailPlanFor(id.Kind, cfg.Badges, cfg.Heart != HeartMode.None, descMaxLines);
        var kids = new List<Element>(8) { SkeletonCover(id.CoverUrl, cover) };

        if (plan.Eyebrow) kids.Add(Bar(Skeleton.BarWidth(cover, Skeleton.EyebrowFraction), VerticalLayout.EyebrowRowHeight));
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
        kids.Add(Lines(cover, titleLineHeight, plan.TitleLines, Skeleton.TitleLastLineFraction));
        if (plan.Artists) kids.Add(Bar(Skeleton.BarWidth(cover, Skeleton.AttributionFraction), VerticalLayout.AttributionRowHeight));
        if (plan.Meta) kids.Add(Bar(Skeleton.BarWidth(cover, Skeleton.MetaFraction), VerticalLayout.MetaRowHeight));

        var fabs = new Element[plan.Fabs];
        for (int i = 0; i < fabs.Length; i++)
            fabs[i] = new BoxEl { Width = RailFabSize, Height = RailFabSize, Shrink = 0f, Corners = CornerRadius4.All(RailFabSize / 2f) };
        kids.Add(new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
            Children =
            [
                new BoxEl { Width = Skeleton.PlayPillWidth, Height = Controls.PillHeight, Shrink = 0f, Corners = CornerRadius4.All(Controls.PillHeight / 2f) },
                new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = fabs },
            ],
        });
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
    /// 1.0 (the one arm without the 1.18 boost), the PageHero title, Play + heart + a bare Share FAB, no toolbar.</summary>
    public static Element ShowHeader(FrameSpec spec, Func<ColorF> accent)
        => ShowHeaderCore(spec, spec.Actions, accent, DefaultPlay(spec.Identity.Subject));

    static Element ShowHeaderCore(FrameSpec spec, FrameActions acts, Func<ColorF> accent, Action play)
    {
        const float coverSize = 140f;
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        var info = new List<Element>(4);

        if (cfg.Badges == BadgeStyle.TypeYear)
        {
            // The info column already clamps (Grow / Basis 0), so the eyebrow run needs no explicit width here.
            if (id.Eyebrow.Length > 0) info.Add(LateRow("hdr:eyebrow", EyebrowRun(id.Eyebrow)));
        }
        else if (cfg.Badges == BadgeStyle.OwnerRow && (slots.Attribution is not null || id.OwnerName is { Length: > 0 }))
        {
            info.Add(LateRow("hdr:owner", slots.Attribution?.Invoke(600f) ?? OwnerBlock(id, 600f)));
        }

        info.Add(Row("hdr:title", slots.Title?.Invoke(28f, 36f)
            ?? Design.Type.PageHero(id.Title) with
            {
                Size = 28f, LineHeight = 36f, Weight = 600,
                Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
            }));
        if (cfg.Badges == BadgeStyle.TypeYear && id.Artists is { Count: > 0 } artists)
            info.Add(LateRow("hdr:artists", slots.Attribution?.Invoke(600f) ?? BilledArtists(artists, 600f)));
        if ((cfg.Badges != BadgeStyle.TypeYear || id.Kind == DetailKind.Show) && (id.Meta is { Length: > 0 } || id.MetaLoading))
            info.Add(LateRow("hdr:meta", MetaRow(id, float.NaN, maxLines: 1)));

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

        // The one CTA cluster that differs: no More, a bare Share FAB.
        var cta = new List<Element>(3) { PlayPill(accent, play) };
        if (cfg.Heart != HeartMode.None)
        {
            string saveUri = SaveTargetOf(id).Text;
            string saveName = id.Title;
            cta.Add(Embed.Comp(() => new Controls.SaveButton { Uri = saveUri, Name = saveName, Glyph = 16f, Box = RailFabSize, Accent = accent })
                with { Key = "save:" + saveUri });
        }
        if (ShareActionFor(id) is { } share) cta.Add(Controls.Named(Fab(Icons.Share, share), Loc.Get(Strings.Menu.Share)));

        var headerKids = new List<Element>(4)
        {
            coverRow,
            new BoxEl
            {
                Key = "hdr:play", Layout = Shove,
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Wrap = true,
                Children = cta.ToArray(),
            },
        };
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
    /// snapping it; hover/pressed shades and the ink re-push with the parent render.</summary>
    public static Element PlayPill(Func<ColorF> accent, Action onClick)
        => Controls.Play(accent(), onClick) with { Fill = accent, BrushTransitionMs = AccentTransitionMs };

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
