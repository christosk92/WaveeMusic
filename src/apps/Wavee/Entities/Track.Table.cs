// ── Entities/Track.Table.cs ───────────────────────────────────────────────────────────────────────────────────────
// the detail track TABLE: the source, the view state, tier + relief plumbing, the row shape + snapshot memos, the bound
// list and its three arms (flat / recommendations / vertical-hero), the three-component row stack (slot → skin →
// content), the reveal ramp, membership choreography, selection, drag insertion, the drawer's mount/keying/reveal and
// the trailing-body arm. Owner O configures it through TableProfile and never edits it. The chrome (command bar,
// search, header, flyouts) is the named partial Track.Table.Chrome.cs
//
// Role: UI
// Owner: M
// Wave: 4.5
// Budget: 2600 lines (this file + Track.Table.Chrome.cs)
// Spec: ch 01 §9 / ch 04 §9
//
// ── THE SHAPE, STATED ONCE ───────────────────────────────────────────────────────────────────────────────────────────
//
// ONE host component (TableHost) owns every live input as a SIGNAL and publishes two equality-gated memos the rows read
// for themselves: the SNAPSHOT (membership version + view + every appearance flag a row observes — ch 30 §1.4: "a flag
// read outside the snapshot never reaches the rows") and the SHAPE (column set + the ONE TrackSize[] + art + row height,
// after the tier ladder and then the relief ladder). The tier is NOT in the list Key: a breakpoint cross re-skins the
// realized rows in place and the viewport keeps its scroll offset (ch 04 §0.3). The list Key carries exactly density ·
// skin · (two-column only) query+filter · reset epoch · recs, plus `vh:` for the vertical arm (ch 30 §5.3).
//
// The row stack is three pieces, and the split is load-bearing (ch 04 §9.4): TableSlot (owns the drawer; re-renders only
// on shape / flow / open flips — never on a recycle), the SKIN (a shape-stable BoxEl whose zebra/hover/press/pill/drop cue
// are bound Prop closures over the slot index, so selection is compositor-only) and TableRowContent (the bound grid,
// Track.UI.Bound.cs: built once per shape, every per-item value a Prop over its equality-gated presentation memo, so a
// recycle rebinds and allocates no element).
//
// TableArgs / TableProfile gate on DATA: their delegates are behaviour, read through `_latest` (a plain field, the
// PagedShelfCore pattern), so a page re-render with fresh closures re-renders nothing.

using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Track
{
    // ══ 1. THE SOURCE ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The rows a table shows, in ORIGINAL membership order, zero-allocation reads. <see cref="Subscribe"/> reads
    /// the Changed signals that matter (the edge table + the owning entity table) — call it inside a render or memo.</summary>
    public abstract class TableSource
    {
        public abstract EntityUri Context { get; }
        /// <summary>Resident rows.</summary>
        public abstract int Count { get; }
        /// <summary>Declared total (≥ Count).</summary>
        public abstract int Total { get; }
        /// <summary>Unknown ⇒ shimmer, never "Nothing here yet"; Failed only while nothing is resident (G-050).</summary>
        public abstract EdgeState State { get; }
        /// <summary>The edge version for this parent.</summary>
        public abstract uint Version { get; }
        public abstract Track At(int index);
        /// <summary>Unix seconds, 0 = none.</summary>
        public virtual int AddedAt(int index) => 0;
        public virtual User AddedBy(int index) => default;
        public virtual StringId ItemId(int index) => default;
        public virtual byte ChartStatus(int index) => 0;
        public virtual Track TopTrack => default;
        public virtual bool HasDateAdded => false;
        public virtual bool HasAddedBy => false;
        public virtual bool HasVideo => false;
        public abstract void Subscribe();

        // ── episodes as Track.Table rows (playlists only: a mixed playlist, or Your Episodes) ──────────────────────
        /// <summary>What kind of member sits at this ORIGINAL index — <see cref="EntityKind.Track"/> for every source
        /// that predates the podcast rework (the default), <see cref="EntityKind.Episode"/> where a playlist actually
        /// mixes the two.</summary>
        public virtual EntityKind KindAt(int index) => EntityKind.Track;
        /// <summary>The episode at this index, or <c>default</c> when <see cref="KindAt"/> says Track. Never both this
        /// and <see cref="At"/> valid for the same index.</summary>
        public virtual Episode EpisodeAt(int index) => default;
        /// <summary>Does this source carry ANY episode row? Gates the table's variable-height layout and its content-type
        /// pools — false for every source that predates the podcast rework, so a plain track table's layout/selection/
        /// choreography paths are BYTE-IDENTICAL to before this wave.</summary>
        public virtual bool HasEpisodes => false;

        public static TableSource ForAlbum(Album a) => new AlbumTableSource(a);
        public static TableSource ForPlaylist(Playlist p) => new PlaylistTableSource(p);
        public static TableSource ForLiked(User me) => new LikedTableSource(me);

        /// <summary>Re-ask the membership after a failed fetch (the Failed panel's Retry).</summary>
        internal virtual void Retry() { }
        /// <summary>The playlist whose membership this is — the move/remove host. Default elsewhere.</summary>
        internal virtual Playlist HostPlaylist => default;

        // The "any row has a video" scan, cached per (edge version, track publication) so a 5,000-row list pays it once
        // per landing rather than once per shape recompute. Reading Tracks.Changed subscribes the calling memo: kind 99
        // lands after the rows paint, and the film lane must appear when it does.
        uint _videoVersion, _videoPublication;
        bool _videoSeeded, _videoValue;

        protected bool CachedVideo(ReadOnlySpan<int> slots, uint version)
        {
            uint publication = Entities.Current.Tracks.Changed.Value;
            if (_videoSeeded && _videoVersion == version && _videoPublication == publication) return _videoValue;
            bool any = false;
            // `KindAt` because a MIXED list's episode member shares this relation but its slot indexes
            // `Entities.Episodes` (see the playlist source's own note) - reading it as a Track indexes the wrong
            // flags array. An episode never carries a music-video counterpart, so it simply does not vote.
            for (int i = 0; i < slots.Length && !any; i++)
                any = KindAt(i) == EntityKind.Track && new Track(slots[i]).HasVideo;
            _videoSeeded = true; _videoVersion = version; _videoPublication = publication; _videoValue = any;
            return any;
        }
    }

    sealed class AlbumTableSource(Album album) : TableSource
    {
        readonly Album _album = album;
        static EdgeTable<AlbumTrackEdge> Edge => Entities.Current.Edges.AlbumTracks;

        public override EntityUri Context => _album.Uri;
        public override int Count => Edge.Count(_album.Slot);
        public override int Total => Edge.Total(_album.Slot);
        public override EdgeState State => Edge.Readiness(_album.Slot);
        public override uint Version => Edge.Version(_album.Slot);
        public override Track At(int index)
        {
            var slots = Edge.Targets(_album.Slot);
            return (uint)index < (uint)slots.Length ? new Track(slots[index]) : default;
        }
        public override Track TopTrack => _album.TopTrack;
        public override bool HasVideo => CachedVideo(Edge.Targets(_album.Slot), Version);
        public override void Subscribe()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Edge.Changed.Value;
            _ = Entities.Current.Albums.Changed.Value;   // the top-track star is derived on the album row
        }
        internal override void Retry() => Entities.RefreshEdge(FetchEdge.AlbumTracks, _album.Slot);
        public override bool Equals(object? obj) => obj is AlbumTableSource o && o._album.Slot == _album.Slot;
        public override int GetHashCode() => HashCode.Combine(1, _album.Slot);
    }

    sealed class PlaylistTableSource(Playlist playlist) : TableSource
    {
        readonly Playlist _playlist = playlist;
        static EdgeTable<PlaylistTrackEdge> Edge => Entities.Current.Edges.PlaylistTracks;

        public override EntityUri Context => _playlist.Uri;
        public override int Count => Edge.Count(_playlist.Slot);
        public override int Total => Edge.Total(_playlist.Slot);
        public override EdgeState State => Edge.Readiness(_playlist.Slot);
        public override uint Version => Edge.Version(_playlist.Slot);
        public override Track At(int index)
        {
            if (KindAt(index) == EntityKind.Episode) return default;
            var slots = Edge.Targets(_playlist.Slot);
            return (uint)index < (uint)slots.Length ? new Track(slots[index]) : default;
        }
        /// <summary>B2 (plan §3.1 contract): <see cref="PlaylistTrackEdge.Kind"/> names an episode member — the same
        /// edge relation, so its target slot indexes <see cref="Entities.Episodes"/> instead of <see cref="Entities.Tracks"/>.</summary>
        public override EntityKind KindAt(int index)
        {
            var payload = Edge.Payload(_playlist.Slot);
            return (uint)index < (uint)payload.Length && payload[index].Kind == PlaylistItemKind.Episode
                ? EntityKind.Episode : EntityKind.Track;
        }
        public override Episode EpisodeAt(int index)
        {
            if (KindAt(index) != EntityKind.Episode) return default;
            var slots = Edge.Targets(_playlist.Slot);
            return (uint)index < (uint)slots.Length ? new Episode(slots[index]) : default;
        }
        uint _episodesVersion; bool _episodesSeeded, _episodesValue;
        public override bool HasEpisodes
        {
            get
            {
                uint v = Version;
                if (_episodesSeeded && _episodesVersion == v) return _episodesValue;
                var payload = Edge.Payload(_playlist.Slot);
                bool any = false;
                for (int i = 0; i < payload.Length && !any; i++) any = payload[i].Kind == PlaylistItemKind.Episode;
                _episodesSeeded = true; _episodesVersion = v; _episodesValue = any;
                return any;
            }
        }
        public override int AddedAt(int index)
        {
            var p = Edge.Payload(_playlist.Slot);
            return (uint)index < (uint)p.Length ? p[index].AddedAt : 0;
        }
        public override User AddedBy(int index)
        {
            var p = Edge.Payload(_playlist.Slot);
            return (uint)index < (uint)p.Length ? new User(p[index].AddedBy) : default;
        }
        public override StringId ItemId(int index)
        {
            var p = Edge.Payload(_playlist.Slot);
            return (uint)index < (uint)p.Length ? p[index].ItemId : default;
        }
        public override byte ChartStatus(int index)
        {
            var p = Edge.Payload(_playlist.Slot);
            return (uint)index < (uint)p.Length ? p[index].ChartStatus : (byte)0;
        }
        // The three column-existence facts are DERIVED AT COMMIT on the playlist row (Playlist.cs item 3) — no scan here.
        public override bool HasDateAdded => _playlist.HasDateAddedColumn;
        public override bool HasAddedBy => _playlist.HasAddedByColumn;
        // NOT the commit-time flag. `HasVideoColumn` is folded while the MEMBERSHIP lands, and kind 99 (the video
        // association) is asked for the rows only AFTER that - so the fold always saw hasVideo:false and the filter's
        // video row was decided "no" forever. The live rows are read through `CachedVideo`, as the album table does.
        public override bool HasVideo => CachedVideo(Edge.Targets(_playlist.Slot), Version);
        public override void Subscribe()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Edge.Changed.Value;
            _ = Entities.Current.Playlists.Changed.Value;
        }
        internal override void Retry() => Entities.RefreshEdge(FetchEdge.PlaylistTracks, _playlist.Slot);
        internal override Playlist HostPlaylist => _playlist;
        public override bool Equals(object? obj) => obj is PlaylistTableSource o && o._playlist.Slot == _playlist.Slot;
        public override int GetHashCode() => HashCode.Combine(2, _playlist.Slot);
    }

    sealed class LikedTableSource(User me) : TableSource
    {
        readonly User _me = me;
        EntityUri _context;
        bool _contextSeeded, _dateSeeded, _dateValue;
        uint _dateVersion;
        static EdgeTable<LibraryEdge> Edge => Entities.Current.Edges.Liked;

        public override EntityUri Context
        {
            get
            {
                if (_contextSeeded) return _context;
                _context = EntityUri.Parse(EntityUri.LikedCollection);   // interns once (UI thread, C1)
                _contextSeeded = true;
                return _context;
            }
        }
        public override int Count => Edge.Count(_me.Slot);
        public override int Total => Edge.Total(_me.Slot);
        public override EdgeState State => Edge.Readiness(_me.Slot);
        public override uint Version => Edge.Version(_me.Slot);
        public override Track At(int index)
        {
            var slots = Edge.Targets(_me.Slot);
            return (uint)index < (uint)slots.Length ? new Track(slots[index]) : default;
        }
        public override int AddedAt(int index)
        {
            var p = Edge.Payload(_me.Slot);
            return (uint)index < (uint)p.Length ? p[index].AddedAt : 0;
        }
        public override bool HasDateAdded
        {
            get
            {
                uint v = Version;
                if (_dateSeeded && _dateVersion == v) return _dateValue;
                var p = Edge.Payload(_me.Slot);
                bool any = false;
                for (int i = 0; i < p.Length && !any; i++) any = p[i].AddedAt > 0;
                _dateSeeded = true; _dateVersion = v; _dateValue = any;
                return any;
            }
        }
        public override bool HasVideo => CachedVideo(Edge.Targets(_me.Slot), Version);
        public override void Subscribe()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = Edge.Changed.Value;
        }
        internal override void Retry() => Entities.RefreshEdge(FetchEdge.Liked, _me.Slot);
        public override bool Equals(object? obj) => obj is LikedTableSource o && o._me.Slot == _me.Slot;
        public override int GetHashCode() => HashCode.Combine(3, _me.Slot);
    }

    // ══ 2. VIEW · PROFILE · ARGS ═════════════════════════════════════════════════════════════════════════════════════

    public readonly record struct TableView(SortSpec Sort, string Query, FilterState Filters, bool MultiSelect)
    {
        public static TableView Default => new(SortSpec.Default, "", default, false);
    }

    /// <summary>Everything a page configures (A2: owner O configures, never edits this file). Equals over DATA only —
    /// a delegate counts by PRESENCE (a null seam changes structure: no chevron lane, no recs arm), never by identity.</summary>
    public sealed record TableProfile
    {
        public required Detail.Config Config { get; init; }
        /// <summary>Liked: (DateAdded, true).</summary>
        public SortSpec DefaultSort { get; init; } = SortSpec.Default;
        /// <summary>Playlist contents write gate.</summary>
        public Func<bool>? Editable { get; init; }
        /// <summary>Liked chip rail (User.UI.cs); reads/writes the view through <see cref="TableLiveSlot"/>.</summary>
        public Func<Element?>? ContentFilterBar { get; init; }
        /// <summary>48 when present.</summary>
        public float ContentFilterExtent { get; init; }
        /// <summary>Liked lens header.</summary>
        public Func<Element?>? LensHeader { get; init; }
        /// <summary>36 when present.</summary>
        public float LensExtent { get; init; }
        /// <summary>Display index → play; default: <c>Playback.PlayRows</c> over the visible (sorted, filtered) order.</summary>
        public Action<int>? PlayFrom { get; init; }
        /// <summary>Insertion drop (index null = append).</summary>
        public Func<DragPayload, int?, bool>? Deposit { get; init; }
        /// <summary>Same-list move, PRE-move index.</summary>
        public Func<ReadOnlySpan<RowRef>, int, bool>? MoveRows { get; init; }
        /// <summary>Membership indices.</summary>
        public Action<IReadOnlyList<int>>? RemoveRows { get; init; }
        /// <summary>Appended section (O, Wave 5); null = absent.</summary>
        public Func<Element?>? Recommendations { get; init; }
        /// <summary>Playlist tuning button; null = absent.</summary>
        public Action? Tune { get; init; }
        /// <summary>Expanded-row BODY (Track.Drawer.cs, M Wave 5), handed the row's track and DISPLAY index; the host pads it
        /// by <see cref="RowMetrics.DrawerIndent"/>. Null = no chevron lane.</summary>
        public Func<Track, int, Element?>? Drawer { get; init; }

        public static TableProfile From(Detail.Config config) => new()
        {
            Config = config,
            DefaultSort = config.Kind == DetailKind.Liked ? new SortSpec(SortColumn.DateAdded, true) : SortSpec.Default,
        };

        int Seams => (Editable is null ? 0 : 1) | (ContentFilterBar is null ? 0 : 2) | (LensHeader is null ? 0 : 4)
                     | (PlayFrom is null ? 0 : 8) | (Deposit is null ? 0 : 16) | (MoveRows is null ? 0 : 32)
                     | (RemoveRows is null ? 0 : 64) | (Recommendations is null ? 0 : 128) | (Tune is null ? 0 : 256)
                     | (Drawer is null ? 0 : 512);

        public bool Equals(TableProfile? other)
            => other is not null && (ReferenceEquals(this, other)
               || (Config.Equals(other.Config) && DefaultSort.Equals(other.DefaultSort)
                   && ContentFilterExtent == other.ContentFilterExtent && LensExtent == other.LensExtent
                   && Seams == other.Seams));

        public override int GetHashCode() => HashCode.Combine(Config, DefaultSort, ContentFilterExtent, LensExtent, Seams);
    }

    public sealed record TableArgs
    {
        public required TableSource Source { get; init; }
        public required TableProfile Profile { get; init; }
        /// <summary>Page accent (selection pill, band Play word); null ⇒ Tok.AccentDefault.</summary>
        public Func<ColorF>? Accent { get; init; }
        /// <summary>Non-null ⇒ the vertical/hero arm.</summary>
        public Detail.VerticalSpec? Vertical { get; init; }
        public bool ShowToolbar { get; init; } = true;
        /// <summary>Library master-detail pane (no hero, no recs).</summary>
        public bool Embedded { get; init; }
        /// <summary>HasTrailing body under the rows (album trailing, M Wave 5). Non-null on an <see cref="Embedded"/>
        /// table is also the opt-IN to the trailing geometry: the host keeps the profile's <c>HasTrailing</c> instead of
        /// dropping it, so the rows and this band share ONE scroller (<c>TableHost.ConfigOf</c>).</summary>
        public Func<Element>? Trailing { get; init; }
        /// <summary>The table writes "play the VISIBLE order from the top" into [0].</summary>
        public Action?[]? PlayAllCell { get; init; }
        /// <summary>Vertical arm: the table publishes the measured hero band height (from an effect).</summary>
        public Signal<float>? HeroHeight { get; init; }
        /// <summary>The two-column arm's facts-sheet toggle (tuned Classic I1): non-null ⇒ the list bar carries the Insights button. The
        /// vertical arm's rides its <see cref="Vertical"/> spec.</summary>
        public Detail.InsightsToggle? Insights { get; init; }
        /// <summary>The table publishes what it shows against what it has (from an effect), for the rail's count line (tuned Classic D4).</summary>
        public Signal<Detail.ListCount>? ListCount { get; init; }
        /// <summary>Pre-measure list-column width estimate (the frame's: the page estimate less the rail it composes), the table tier's seed.</summary>
        public float WidthSeed { get; init; }
        /// <summary>Route identity for ScrollMemory / list keys.</summary>
        public string ScrollKey { get; init; } = "";
        /// <summary>The ONE-TREE page's arrangement (audit B9): the hero list hosts the structure's hero at every width, and this live value says
        /// whether it is the collapsing hero or the columns beside the rows. Never part of a key.</summary>
        public global::Wavee.Theming.Contracts.DetailForm Form { get; init; }
        /// <summary>The columns form's parts (the rail, the head, the aside, the sheet), handed to the hero; null where the page is not one tree.</summary>
        public global::Wavee.Theming.Contracts.DetailColumnsProps? Columns { get; init; }

        public bool Equals(TableArgs? other)
            => other is not null && (ReferenceEquals(this, other)
               || (Source.Equals(other.Source) && Profile.Equals(other.Profile) && Equals(Vertical, other.Vertical)
                   && (Accent is null) == (other.Accent is null) && ShowToolbar == other.ShowToolbar
                   && Embedded == other.Embedded && (Trailing is null) == (other.Trailing is null)
                   && ReferenceEquals(PlayAllCell, other.PlayAllCell) && ReferenceEquals(HeroHeight, other.HeroHeight)
                   && ReferenceEquals(Insights, other.Insights) && ReferenceEquals(ListCount, other.ListCount)
                   && WidthSeed == other.WidthSeed && string.Equals(ScrollKey, other.ScrollKey, StringComparison.Ordinal)
                   && Form == other.Form && ReferenceEquals(Columns, other.Columns)));

        public override int GetHashCode()
            => HashCode.Combine(Source, Profile, Vertical is null, ShowToolbar, Embedded, WidthSeed, ScrollKey, Form);
    }

    /// <summary>The table's live view, for the page-supplied rows it hosts (the Liked chip rail and lens header set and
    /// read the filters; the lens header states the visible count). Provided around the table; null elsewhere.
    /// <paramref name="InlineChips"/>: the chips ride the page bar (a page with no rail, tuned Classic D8), not a row of their own.</summary>
    public sealed record TableLive(IReadSignal<TableView> View, Action<FilterState> SetFilters, IReadSignal<int> Visible,
                                   bool InlineChips = false);

    /// <inheritdoc cref="TableLive"/>
    public static readonly Context<TableLive?> TableLiveSlot = new(null);

    /// <summary>Embed.Comp(args, () => new TableHost()) — TableArgs/TableProfile gate on data, delegates late-bound.</summary>
    public static Element Table(TableArgs args) => Embed.Comp(args, static () => new TableHost());

    // ══ 3. THE HOST ══════════════════════════════════════════════════════════════════════════════════════════════════

    sealed partial class TableHost : Component, IPropsHost
    {
        /// <summary>The ceiling for "Play next" / "Add to queue" on the whole context (0.2.9 DetailQueueActions.MaxBatch).</summary>
        const int MaxQueueBatch = 50;
        const int ReDealReversalMs = 200;
        /// <summary>How long after the hero list's form changes (audit B9) a table-tier change is the re-arrangement's own (its list column narrowed or widened
        /// with the form): no re-deal then, the rows ride the re-arrangement (they glide with the list's insets) instead of fading out and back in.</summary>
        const int ReDealFormGraceMs = 400;
        const int ReDealRows = 24;

        // ── props: data gates the render, behaviour is read through _latest ─────────────────────────────────────────
        readonly Signal<TableArgs?> _args = new(null);
        TableArgs _latest = null!;

        public void ApplyProps(object props)
        {
            bool mount = _latest is null;
            _latest = (TableArgs)props;
            _args.SetIfChanged(_latest);
        }

        TableProfile P => _latest.Profile;

        /// <summary>The facts this table is drawn in (`TierContext.DrawnIn`, set at the top of every render): its root's, never the process's last window.</summary>
        IReadSignal<global::Wavee.Theming.ThemeFacts>? _facts;

        /// <summary>The window tiers this table is drawn in, read TRACKED.</summary>
        global::Wavee.Theming.TierFacts TiersNow() => (_facts ?? global::Wavee.Theming.ThemeHost.Facts).Value.Tiers;

        /// <summary>The structure's pinned band the hero collapses into (`DetailTierLayout.BandHeight`: the shell's 56 where it names none). A tracked read, so a window
        /// crossing re-renders the table on the frame it commits.</summary>
        float BandNow() => ShellPolicy.Tracked.DetailTiers.For(TiersNow()).BandOf;

        /// <summary>The structure's page inset for the list under the hero (`DetailTierLayout.PageInsetX`; 0: none, the table's own gutter).</summary>
        float PageInsetNow() => ShellPolicy.Tracked.DetailTiers.For(TiersNow()).PageInsetX;

        /// <summary>The hero list's COLUMNS form (audit B9): the hero's side columns stand beside the rows (a plain read: the host re-renders with its args).</summary>
        bool ColumnsForm => VerticalArm && _latest.Form == global::Wavee.Theming.Contracts.DetailForm.Columns;

        /// <summary>The hero list in its collapsing HERO form (the vertical arm's own look: the stacked rows, the hero's list bar, no column header where
        /// the form drops it), read TRACKED: a component that draws by it re-renders when a crossing flips the form (audit B9).</summary>
        internal bool HeroFormLive => _args.Value is { Vertical: not null, Embedded: false } a && a.Form != global::Wavee.Theming.Contracts.DetailForm.Columns;

        /// <summary>The list column's insets in the hero list, TRACKED (the args, the tiers, and whatever the structure reads), so a bound spacer follows a
        /// crossing on the frame it commits: the structure's page inset (<see cref="Theming.ListPolicy.InsetHeroList"/>, or a trailing band's, which pads
        /// the rows and the band alike), and the width its hero's side columns take in the columns form (<c>DetailHeroStyle.ListInsets</c>, audit B9).</summary>
        internal global::Wavee.Theming.Contracts.DetailListInsets InsetsNow()
        {
            var a = _args.Value!;
            if (a.Vertical is null || a.Embedded) return default;
            float page = ConfigOf(a).HasTrailing || ShellPolicy.Tracked.Lists.InsetHeroList ? PageInsetNow() : 0f;
            var side = _heroStyle?.ListInsets is { } insets
                ? insets(new global::Wavee.Theming.Contracts.DetailInsetQuery(a.Form, TiersNow(), a.Columns, _tableW.Value))
                : default;
            return new global::Wavee.Theming.Contracts.DetailListInsets(page + side.Lead, page + side.Trail);
        }

        /// <summary>A lead / trail spacer of the list column: its width BOUND to <see cref="InsetsNow"/> (a crossing re-binds it, nothing re-renders) and
        /// eased with the structure's column motion, so the rows move in step with the hero's identity column.</summary>
        BoxEl InsetSpacer(bool lead) => new()
        {
            Width = Prop.Of(lead ? _leadInset : _trailInset), Shrink = 0f, HitTestVisible = false, Animate = _heroStyle?.InsetMotion,
        };

        /// <summary><paramref name="e"/> in the list column: between the lead and trail spacers (always the same three children, whatever the insets, so an
        /// inset that comes or goes moves the item instead of re-parenting it). The box yields the pointer where its own children take none
        /// (`HitTestPassThrough`): in the columns form the hero's identity column hangs over the lead, and a wheel there scrolls that column's own scroller,
        /// never the list behind it.</summary>
        internal Element InsetList(Element e, string? key = null) => new BoxEl
        {
            Key = key, Direction = 0, MinWidth = 0f, HitTestPassThrough = true,
            // The list column hosts the slot root's keyboard-focus ring (the slot root spans the whole viewport, its lead under the hero's identity column).
            Children = [InsetSpacer(true), new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, FocusRingHost = true, Children = [e] }, InsetSpacer(false)],
        };
        Detail.Config Cfg => ConfigOf(_latest);

        /// <summary>The config the host actually lays out with. An EMBEDDED table has no trailing band by default — the
        /// library pane mounts no About/Fans/More-by — so the album family's <c>HasTrailing</c> is dropped rather than
        /// left to grow a slot nothing fills. It is dropped only when the caller asked for NOTHING, though: a pane that
        /// does hand over a <see cref="TableArgs.Trailing"/> thunk means it, and forcing the flag off there is what put
        /// the album pane's rows in a nested scroller with the "Also by" strip pinned below it (a 5-track EP showed 3½
        /// rows in a 550-DIP pane). With the band honoured, <see cref="TrailingBody"/>'s ONE outer scroller carries the
        /// rows and the band together and the column header stays fixed above it.</summary>
        static Detail.Config ConfigOf(TableArgs args)
            => args.Embedded && args.Trailing is null ? args.Profile.Config with { HasTrailing = false } : args.Profile.Config;

        bool VerticalArm => _latest.Vertical is not null && !_latest.Embedded;
        int TrackStart => VerticalArm && !Cfg.HasTrailing ? Detail.VerticalLayout.PrefixCount : 0;
        bool Editable => _latest.Profile.Editable?.Invoke() ?? false;
        ColorF AccentNow() => _latest.Accent?.Invoke() ?? Tok.AccentDefault;

        // ── the view state (sort persisted per context; query/filters/multi-select reset on a context change) ──────
        readonly Signal<SortSpec> _sort = new(SortSpec.Default);
        readonly Signal<string> _query = new("");
        readonly Signal<FilterState> _filters = new(default);
        readonly Signal<bool> _multi = new(false);

        // ── tier + relief (ch 04 §0.2) ──────────────────────────────────────────────────────────────────────────────
        // The tier is the structure's table ladder over the list column's measured width (`_listW`, value-gated), through UseTier: the first
        // mount resolves in its own frame from the frame's seed (the list column's estimate), a crossing after it commits on the first frame
        // that is not resized and re-deals the rows animated (themes plan 3.3).
        IReadSignal<int>? _tierSig;
        readonly Signal<float> _listW = new(0f);
        readonly Func<float> _listSeed;
        readonly Signal<int> _relief = new(0);
        readonly Signal<bool> _tierMeasured = new(false);
        readonly Signal<float> _colW = new(0f);
        /// <summary>The table's laid-out width, exact: what the structure's list insets yield against (<c>DetailInsetQuery.TableWidth</c>).</summary>
        readonly Signal<float> _tableW = new(0f);
        readonly Signal<bool> _rowFlow = new(false);
        float _lastW;
        bool _flowInit;
        Lanes _reliefLanes;
        float _reliefGap = RowMetrics.ColGap, _reliefPad = RowMetrics.PadX;

        // ── the vertical arm ───────────────────────────────────────────────────────────────────────────────────────
        readonly Signal<float> _heroH = new(0f);
        /// <summary>The COLUMNS form's pinned head, as the hero reports it (audit B9): the line its chrome pins at and its rows clip under. Its own value,
        /// never the hero form's: a crossing lands each form on the head it last measured (the head's own report lands at once, its body eases).</summary>
        readonly Signal<float> _headH = new(0f);
        /// <summary>The hero's measured height while it is SHORTER than the band it collapses to (else 0), equality-gated: the hero form's chrome pins at
        /// the hero's foot rather than below it while a crossing from the columns form (a 52 list head) eases the hero up through the band's height
        /// (audit B9), so the chrome never steps down to the band's line on the commit frame.</summary>
        readonly Signal<float> _heroShort = new(0f);
        float _heroShortNext;
        bool _heroShortPosted;
        Action? _raiseStickLine;
        // The chrome sticky's ENGAGED edge (`Sticky(… engaged:)` — the engine writes it on its flip, before the frame
        // publishes): the compact band's input hand-off. (The body clip's feather is the clip's own composite-time
        // parameter, EdgeFadeSpec.WhileStuck — no engaged signal, no re-render; RCA 2026-09-25 F(ii).)
        readonly Signal<bool> _compactInteractive = new(false);
        // The hero arm's list count and its recommendations section (C-Playlist-Compact, C-Empty-Compact), DERIVED from the same snapshot
        // and row count the slots resolve their roles from (TableVerticalItem): a count written by a layout effect reached the list a
        // frame after the slots had already turned the skeleton's slot into row 1 (the skeleton gone, then row 1 alone, then the rest).
        IReadSignal<int> _verticalItemCount = null!;
        IReadSignal<bool> _verticalRecs = null!;
        /// <summary>Can the hero arm's list carry the section at all (the render's config, the aside, the page's builder)? Written by the
        /// layout effect; <see cref="_verticalRecs"/> folds it with the list's state.</summary>
        readonly Signal<bool> _recsCapable = new(false);
        /// <summary>The list is empty and shows its recommendations lead (an owned, empty playlist): a theme that sets <c>HidesChromeOnEmptyLead</c> draws no table chrome over it.</summary>
        readonly Signal<bool> _emptyLead = new(false);
        bool EmptyLeadHides => _listStyle?.Chrome.HidesChromeOnEmptyLead == true && _emptyLead.Value;

        // ── selection · list controller · counts ───────────────────────────────────────────────────────────────────
        readonly SelectionModel _selection = new();
        readonly ItemsViewController _listCtl = new();
        /// <summary>The list viewport's scroll handle — also the target the column header names in its <c>WheelTarget</c>,
        /// so a wheel over the header (laid out ABOVE the list, outside its viewport) glides the rows exactly as a wheel
        /// over them would.</summary>
        readonly ScrollHandle _listScroll = new();
        /// <summary>The album list's ONE outer scroller (the trailing body): its viewport's height is what the columns form's side columns fill.</summary>
        readonly ScrollHandle _trailScroll = new();
        readonly Signal<int> _visible = new(0);
        readonly Signal<int> _listCount = new(0);
        readonly Signal<string> _expanded = new("");   // MembershipDiff.RowKey of the ONE open drawer, "" = none
        // The rows whose drawer is CLOSING (still presented under them while their exit reveal runs) — their bottom corners
        // stay square until the engine reports the orphan at rest. A set, not one key: a rapid A→B→close-B sequence holds A
        // and B at once. `_tailEpoch` bumps on every change so the row computeds that read the set re-run. Plus the open
        // drawer's node (captured at realize).
        readonly Signal<int> _tailEpoch = new(0);
        readonly HashSet<string> _tails = new(StringComparer.Ordinal);
        NodeHandle _drawerNode;
        Func<NodeHandle>? _benchDrawerNode;
        string _drawerNodeKey = "";
        readonly Signal<int> _videoDropRow = new(-1);
        InsertionOptions? _insertion;
        IOverlayService? _overlay;

        // ── reveal ramp (Design.RevealRamp) ────────────────────────────────────────────────────────────────────────
        readonly Signal<int> _reveal = new(Design.RevealRamp.Done);
        readonly Signal<bool> _rampActive = new(false);
        bool _rampPending, _rampDecided, _sawUnknown;
        int _rampEpoch;

        // ── membership choreography + re-deal (ch 04 §5) ───────────────────────────────────────────────────────────
        readonly Signal<int> _dispVer = new(0);
        readonly Dictionary<int, (float dx, float dy)> _flip = new();
        readonly Dictionary<int, (float from, float delayMs)> _fade = new();
        readonly Dictionary<string, int> _keyIndex = new(StringComparer.Ordinal);
        EntityId _contextSeen;
        string _contextText = "";
        bool _contextSeeded, _settled, _dealtThisFrame, _orderSeeded;
        int _resetEpoch, _dealEpoch, _lastDealtTier = -1;
        long _lastDealtAtMs;
        global::Wavee.Theming.Contracts.DetailForm _formSeen;
        long _formChangedAtMs;
        float _rowHSeen;

        /// <summary>A row whose height a crossing changes (audit B9: WinUI's Compact two-line rows, 40 ↔ 56): its presented height eases while its content is
        /// laid out at the final size (a reveal), so a row never steps; its place glides on the seeds <see cref="ReRow"/> writes.</summary>
        static readonly LayoutTransition s_rowResize = new(TransitionChannels.Size, TransitionDynamics.Tween(280f, Easing.SmoothOut), SizeMode.Reveal);

        /// <summary>An album's list (its rows in the outer scroller) across a row-height change: its height eases through real layout, the trailing band
        /// under it riding it (audit B9).</summary>
        static readonly LayoutTransition s_listResize = new(TransitionChannels.Size, TransitionDynamics.Tween(280f, Easing.SmoothOut),
            Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

        /// <summary>The row skin's own transition (<see cref="s_rowResize"/>), none under reduced motion.</summary>
        internal LayoutTransition? RowResize => Design.Reduced ? null : s_rowResize;

        /// <summary>A chrome slot whose height a crossing changes (the chips coming into the pinned chrome from the page bar): it eases through real layout,
        /// the rows under it riding it (audit B9). None under reduced motion.</summary>
        internal LayoutTransition? ChromeResize => Design.Reduced ? null : s_listResize;
        Snapshot _orderSeen;
        Track[] _curTracks = [], _prevTracks = [];
        StringId[] _curIds = [], _prevIds = [];
        int _curLen, _prevLen;
        EdgeState _curState;

        // ── memos (hook-owned; assigned every render, read by the rows) ────────────────────────────────────────────
        Memo<Snapshot>? _snapshot;
        Memo<Shape>? _shape;
        Memo<TableView>? _viewMemo;
        Memo<int>? _selectedCount;
        Memo<bool>? _checksVisible;
        Memo<bool>? _selectionVisible;
        /// <summary>The frame's docked selection bar (contract 1.47), or null where the frame provides none: the table then keeps its in-flow bar.</summary>
        global::Wavee.Theming.Contracts.SelectionDockHost? _dockHost;
        global::Wavee.Theming.Contracts.SelectionDockProps? _dockPublished;
        static readonly Signal<bool> s_neverSelecting = new(false);
        Memo<bool>? _hotSettled;
        BoundItemsSource<Track>? _rowItems;
        /// <summary>B2 (plan §3.2): the parallel episode-row projection over the SAME view/index space as
        /// <see cref="_rowItems"/> — invalid (<c>default</c>) at every index that isn't an episode row.</summary>
        BoundItemsSource<Episode.RowItem>? _episodeItems;

        // ── cached delegates (mount-stable; list options freeze at mount and must read live state) ─────────────────
        readonly Action<RectF> _onBounds;
        readonly Action<float> _onHeroMeasured;
        readonly Action _playAll, _shuffle;
        Detail.VerticalSpec? _heroSpecFrom, _heroSpec;
        readonly Func<bool> _checksRead, _rampActiveRead, _false = static () => false;
        readonly Func<ColorF> _accent;
        readonly Func<int, (float dx, float dy)?> _flipFrom, _verticalFlipFrom;
        readonly Func<int, (float from, float delayMs)?> _fadeFrom, _verticalFadeFrom;
        readonly Func<float> _leadInset, _trailInset;

        public TableHost()
        {
            _onBounds = OnColumnBounds;
            // Unknown (no seed) is the widest tier, as the table always opened.
            _listSeed = () => _latest is { WidthSeed: > 0f } a ? a.WidthSeed : float.PositiveInfinity;
            _onHeroMeasured = h =>
            {
                // The columns form reports its pinned head (the hero's list head's foot), the hero form its whole height.
                if (ColumnsForm)
                {
                    if (h > 1f && MathF.Abs(_headH.Peek() - h) > 1f) _headH.Value = h;
                    return;
                }
                if (h > 1f && MathF.Abs(_heroH.Peek() - h) > 1f) _heroH.Value = h;
                // Lowered at once, raised a frame later: a reflow's first layout probes the hero at its FINAL height before it lays it out again at the
                // presented one, and a line raised on that probe would step the chrome down to the band on the commit frame.
                float shortH = h > 1f && h < BandNow() ? h : 0f;
                _heroShortNext = shortH;
                if (shortH > 1f)
                {
                    if (MathF.Abs(_heroShort.Peek() - shortH) > 0.5f) _heroShort.Value = shortH;
                }
                else if (_heroShort.Peek() > 0f && !_heroShortPosted && _post is { } post)
                {
                    _heroShortPosted = true;
                    post(_raiseStickLine ??= () =>
                    {
                        _heroShortPosted = false;
                        if (_heroShortNext <= 1f && _heroShort.Peek() > 0f) _heroShort.Value = 0f;
                    });
                }
            };
            _playAll = () => StartVisible(0);
            _shuffle = Shuffle;
            _checksRead = () => _checksVisible?.Value ?? false;
            _rampActiveRead = () => _rampActive.Value;
            _accent = AccentNow;
            _flipFrom = display => _flip.TryGetValue(display, out var f) ? f : null;
            _fadeFrom = display => _fade.TryGetValue(display, out var f) ? f : null;
            _verticalFlipFrom = item => IsVerticalRow(item) ? _flipFrom(item - Detail.VerticalLayout.PrefixCount) : null;
            _verticalFadeFrom = item => IsVerticalRow(item) ? _fadeFrom(item - Detail.VerticalLayout.PrefixCount) : null;
            _leadInset = () => InsetsNow().Lead;
            _trailInset = () => InsetsNow().Trail;
            InitChrome();
        }

        /// <summary>ROW slots only: the seeds are keyed by DISPLAY index, and the placeholder slot sits past the live
        /// row count, where a stale seed would make it rise like a track that moved.</summary>
        bool IsVerticalRow(int itemIndex)
        {
            int display = itemIndex - Detail.VerticalLayout.PrefixCount;
            return display >= 0 && display < (_rowItems?.Count.Peek() ?? 0);
        }

        // ── the two memos' value types ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Every non-slot input a persistent row can observe, as ONE equality-gated value (ch 04 §9 trap 1).</summary>
        internal readonly record struct Snapshot(
            EntityId Context, uint Version, int Count, EdgeState State,
            SortSpec Sort, string Query, FilterState Filters, uint DataPublication, uint LikesPublication,
            bool Classic, bool ArtHidden, bool Marquee, bool TempoColumn, bool PlaysColumn, Detail.Config Config);

        /// <summary>The ACTIVE geometry: one column set, one TrackSize[] (the row's per-(set, art) cached instance — header
        /// and rows read the same array), art, row height, density. Tracks compare by VALUE so a cache miss that rebuilt an
        /// identical array does not re-render every row.</summary>
        internal readonly record struct Shape(ColumnSet Set, TrackSize[] Tracks, float Art, float RowH, int Density, float EpisodeRowH = 0f, bool NarrowEpisode = false)
        {
            public bool Equals(Shape other)
            {
                if (!Set.Equals(other.Set) || Art != other.Art || RowH != other.RowH || Density != other.Density
                    || EpisodeRowH != other.EpisodeRowH || NarrowEpisode != other.NarrowEpisode) return false;
                var a = Tracks; var b = other.Tracks;
                if (ReferenceEquals(a, b)) return true;
                if (a is null || b is null || a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }

            public override int GetHashCode() => HashCode.Combine(Set, Art, RowH, Density, Tracks?.Length ?? 0);
        }

        Snapshot ComputeSnapshot()
        {
            var args = _args.Value!;
            var src = args.Source;
            src.Subscribe();
            var sort = _sort.Value;
            string query = _query.Value;
            var filters = _filters.Value;
            // Track data only changes the ORDER when something reads it — a natural-order unfiltered list is a pure
            // function of the membership, so an unrelated commit anywhere in the app must not re-sort 5,000 rows.
            bool dataDependent = sort != SortSpec.Default || query.Length > 0 || !filters.IsDefault;
            uint data = dataDependent
                ? Entities.Current.Tracks.Changed.Value + (sort.Column == SortColumn.Album ? Entities.Current.Albums.Changed.Value : 0u)
                : 0u;
            uint likes = filters.LikedOnly ? Entities.Current.Edges.Liked.Changed.Value : 0u;
            var cfg = ConfigOf(args);
            return new Snapshot(src.Context.Id, src.Version, src.Count, src.State, sort, query.Trim(), filters, data, likes,
                ShellPolicy.Tracked.Lists.ClassicRows, !ShellPolicy.Tracked.Lists.TrackArtwork, Prefs.Appearance.Marquee(),
                TableColumnsPref.Tempo(), TableColumnsPref.Plays(), cfg);
        }

        /// <summary>Have the HOT rows (the first page, source order) settled — each knows its face, or an answer has been
        /// given and nothing is out for it (<see cref="TableRules.RowUnsettled"/>)? Re-evaluated on the membership, on every
        /// track publication (a row landing) and on every settled batch (<see cref="Fetch.Settled"/>: a failure, or an
        /// answer that did not name a row, publishes nothing else). Equality-gated, so the host re-renders on the flip
        /// only. Twelve rows at most, never per frame.</summary>
        bool ComputeHotSettled()
        {
            var src = _args.Value!.Source;
            src.Subscribe();
            _ = Entities.Current.Tracks.Changed.Value;
            _ = global::Wavee.Fetch.Settled.Value;
            int hot = TableRules.HotRows(src.Count);
            bool credits = RowsTogether();
            for (int i = 0; i < hot; i++)
            {
                var t = src.At(i);
                bool paints = TableRules.RowPaints(t.Knows(TrackFields.Face), t.Knows(TrackFields.Artists), t.InFlight, credits);
                if (t.IsValid && TableRules.RowUnsettled(paints, t.InFlight, t.Answered)) return false;
            }
            return true;
        }

        Shape ComputeShape()
        {
            var snap = _snapshot!.Value;
            var args = _args.Value!;
            // Two ladders, in order: the tier admits the lanes this WIDTH allows, then relief takes back the ones the
            // identity lanes cannot pay for. Relief only subtracts, so this can never widen the table.
            // The window tier's table form (the structure's record: the Compact row, the lanes a band never admits, the row floors). Tracked, so
            // a crossing re-shapes on the frame it commits. An embedded table (a library pane) keeps the density's rows and every lane (Track.EmbeddedForm).
            var form = args.Embedded
                ? Track.EmbeddedForm()
                : ShellPolicy.Tracked.DetailTiers.For(TiersNow()).Table;
            var admitted = SetFor(in snap, args, TierNow(), form);
            var set = TableRules.ApplyRelief(in admitted, ReliefStepFor(in admitted));
            var rows = RowMetrics.Rows();   // the style's density (M3)
            float art = form.ArtFor(set.Classic ? rows.ClassicArt : rows.ModernArt);
            float rowH = form.RowFor(set.Classic, set.Classic ? rows.ClassicRow : rows.ModernRow);
            // An album's numbered rows are one line with no art lane: a structure may stand them under the density's two-line row.
            if (form.OneLineTrim > 0f && !set.Compact && !set.Thumb && snap.Config.Kind == DetailKind.Album) rowH = MathF.Max(0f, rowH - form.OneLineTrim);
            // A list's two-line rows may add air of their own (DetailTableTier.ListRowAir: WinUI's 2, a 50 pitch at the standard density); albums and the Classic skin keep theirs.
            if (form.ListRowAir > 0f && !set.Classic && !set.Compact && snap.Config.Kind != DetailKind.Album) rowH += form.ListRowAir;
            return new Shape(set, Track.TracksFor(in set, art), art, rowH, rows.Rung, rows.EpisodeRow, rows.NarrowEpisode);
        }

        /// <summary>The committed table tier (0 the widest); the widest before the hook has run.</summary>
        int TierNow() => _tierSig is { } t ? global::Wavee.Theming.DetailLadders.TierOf(t.Value) : 0;

        static ColumnSet SetFor(in Snapshot s, TableArgs args, int tier, global::Wavee.Theming.DetailTableTier form)
        {
            var cfg = s.Config;
            var src = args.Source;
            var identity = TableRules.IdentityColumns(s.Classic, cfg.ShowArtThumb, s.ArtHidden, cfg.ShowTrackArtist, tier);
            // A null Drawer seam is "no chevron lane" (the contract), so it folds into ShowVersions before the tier gate.
            var trailing = TableRules.TrailingColumns(cfg.ShowVersions && args.Profile.Drawer is not null, tier);
            // The Compact row (boards C-TrackList compact, C-RowStates-Compact): no column header, the heart trailing before the duration,
            // no versions chevron lane (the drawer stays reachable through the menu's "Track details"), no Artist / Added-by / tempo lane,
            // and the 40 more lane at every width.
            bool compact = form.CompactRows;
            bool hover = form.HoverActions && !compact && !s.Classic;
            return new ColumnSet(
                Album: cfg.ShowAlbumColumn && tier < 2 && form.AlbumColumn,
                By: src.HasAddedBy && tier < 1 && !compact,
                // A quiet list (Liked) keeps its date wherever the width does (C-TrackList small: "Added" on Liked only).
                Date: src.HasDateAdded && tier < 3 && (form.DateColumn || cfg.QuietRows) && !compact,
                Plays: TableColumnsPref.PlaysVisible(cfg.ShowPlays, cfg.PlaysColumnOptIn, s.PlaysColumn) && tier < 3 && form.PlaysColumn,
                // A quiet list (Liked, D16) states its heart in the trailing lane, before the duration (board C-TrackList, liked).
                Heart: tier < 5 && !cfg.QuietRows && !compact && !hover && !form.HeartTrailing,
                Thumb: identity.Thumb,
                Actions: (trailing.Actions && !hover) || compact,
                Tier: tier,
                Tempo: cfg.ShowTempo && s.TempoColumn && !compact,
                Expand: trailing.Expand && !compact && !hover && form.VersionsLane && (form.ListVersionsLane || cfg.Kind == DetailKind.Album),
                Artist: identity.Artist && !compact,
                Classic: s.Classic,
                HeartTrailing: compact || (tier < 5 && (cfg.QuietRows || form.HeartTrailing) && !hover),
                Quiet: cfg.QuietRows,
                Compact: compact,
                // A list's Modern row drops its "#" where the tier says so (C-TrackList compact); an album's track number and the Classic row keep it.
                IndexHidden: !form.ListIndex && !s.Classic && cfg.Kind != DetailKind.Album,
                HoverActions: hover,
                DurationKept: compact && form.KeepDuration,
                DurationWidth: compact ? 0f : form.DurationWidth,
                HeartWidth: form.HeartWidth);
        }

        int ReliefStepFor(in ColumnSet s)
        {
            var lanes = TableRules.LanesOf(in s);
            float gap = RowMetrics.ColGapFor(s.Tier), pad = RowMetrics.PadXFor(s.Tier);
            _reliefLanes = lanes; _reliefGap = gap; _reliefPad = pad;
            return TableRules.ReliefFor(in lanes, _lastW, gap, pad, _relief.Value, _tierMeasured.Value);
        }

        /// <summary>Measure → tier → relief, value-gated so a drag re-renders only where a lane changes hands. The first
        /// real width takes the NOMINAL tier; every later one crosses the 24-DIP band.</summary>
        void OnColumnBounds(RectF r)
        {
            if (r.W <= 0f) return;
            _lastW = r.W;
            if (MathF.Abs(_colW.Peek() - r.W) > 4f) _colW.Value = r.W;
            if (r.W != _tableW.Peek()) _tableW.Value = r.W;
            if (VerticalArm)
            {
                bool flow = Detail.VerticalLayout.RowFlow(r.W, _rowFlow.Peek(), _flowInit);
                // A stacked ↔ row restructure changes the natural hero height a lot; clear the measure so the collapse
                // binds cannot keep the previous flow's height (DetailTracks.cs:1144-1150).
                // The columns form's head (audit B9) has no such restructure: its measure stands.
                if (_flowInit && flow != _rowFlow.Peek() && !ColumnsForm) _heroH.Value = 0f;
                if (flow != _rowFlow.Peek()) _rowFlow.Value = flow;
                _flowInit = true;
            }
            bool measured = _tierMeasured.Peek();
            // The tier ladder's source (value-gated). Its crossing is the host's: pending until the first non-resized frame, then animated.
            // The list column's own width: the table's, less the structure's page inset on both sides (the hero arm's list stands inside it).
            // In the hero list the list column stands between its insets (the page inset, the hero's side columns: their TARGET widths, so the tier
            // follows the form's commit and never the insets' ease).
            float listW = VerticalArm ? MathF.Max(1f, r.W - Reactive.Untrack(_leadInset) - Reactive.Untrack(_trailInset)) : r.W;
            if (listW != _listW.Peek()) _listW.Value = listW;
            int relief = TableRules.ReliefFor(in _reliefLanes, listW, _reliefGap, _reliefPad, _relief.Peek(), measured);
            if (relief != _relief.Peek()) _relief.Value = relief;
            if (!measured) _tierMeasured.Value = true;
        }

        float ColumnWidth()
        {
            float w = _colW.Value;
            return w > 0f ? w : _latest.WidthSeed > 0f ? _latest.WidthSeed : Detail.VerticalLayout.FallbackW;
        }

        internal bool RowFlowValue
        {
            get
            {
                bool flow = _rowFlow.Value;
                return _flowInit ? flow : Detail.VerticalLayout.RowFlow(_latest.WidthSeed);
            }
        }

        // ── the view map: filtered + sorted display order → original membership index ────────────────────────────

        int[] _view = [];
        int _viewLen;
        Snapshot _viewKey;
        bool _viewValid;
        readonly ViewComparer _comparer = new();

        ReadOnlySpan<int> ViewOf(scoped in Snapshot s)
        {
            if (!_viewValid || !_viewKey.Equals(s)) RebuildView(in s);
            return new ReadOnlySpan<int>(_view, 0, _viewLen);
        }

        ReadOnlySpan<int> ViewNow() => _snapshot is { } memo ? ViewOf(memo.Peek()) : default;

        /// <summary>Rebuilt only when the snapshot VALUE moves (membership version, view, the data publication a sort or
        /// filter reads), into a reused buffer; a steady list never rebuilds.</summary>
        void RebuildView(in Snapshot s)
        {
            var src = _latest.Source;
            int n = src.Count;
            if (_view.Length < n) _view = new int[Math.Max(n, _view.Length * 2)];
            bool unfiltered = s.Query.Length == 0 && s.Filters.IsDefault;
            long now = Store.ToUnix(Entities.Now);   // the wire's AddedAt is unix seconds; Entities.Now is APP seconds
            var me = User.Me;
            int len = 0;
            for (int i = 0; i < n; i++)
            {
                if (!unfiltered && src.KindAt(i) != EntityKind.Episode)
                {
                    // episode rows bypass the (track-shaped) filter model until a later wave teaches it their fields —
                    // never dropped silently, always shown, which is the safe default for a filter it cannot evaluate.
                    var t = src.At(i);
                    bool saved = s.Filters.LikedOnly && me.IsValid && me.Likes(t);
                    var row = FilterRow.Of(t, src.AddedAt(i), saved, now);
                    if (!FilterModel.Matches(in row, s.Query, s.Filters, now)) continue;
                }
                _view[len++] = i;
            }
            _viewLen = len;
            if (s.Sort != SortSpec.Default)
            {
                _comparer.Arm(src, s.Sort);
                Array.Sort(_view, 0, len, _comparer);
                _comparer.Arm(null, default);
            }
            _viewKey = s;
            _viewValid = true;
        }

        /// <summary>Stable by construction: ties break by original index, so Descending flips only the primary key — and
        /// Index compares by position, so (Index, Descending) is "invert this list".</summary>
        sealed class ViewComparer : IComparer<int>
        {
            TableSource? _src;
            SortSpec _sort;

            public void Arm(TableSource? src, SortSpec sort) { _src = src; _sort = sort; }

            public int Compare(int a, int b)
            {
                var src = _src!;
                int c = _sort.Column switch
                {
                    SortColumn.Title => string.Compare(src.At(a).Title, src.At(b).Title, StringComparison.OrdinalIgnoreCase),
                    SortColumn.Album => string.Compare(src.At(a).Album.Title, src.At(b).Album.Title, StringComparison.OrdinalIgnoreCase),
                    SortColumn.Duration => src.At(a).DurationMs.CompareTo(src.At(b).DurationMs),
                    SortColumn.Artist => string.Compare(Entities.Strings.Resolve(src.At(a).ArtistLineId),
                                                        Entities.Strings.Resolve(src.At(b).ArtistLineId), StringComparison.OrdinalIgnoreCase),
                    SortColumn.DateAdded => src.AddedAt(a).CompareTo(src.AddedAt(b)),
                    SortColumn.Plays => src.At(a).PlayCount.CompareTo(src.At(b).PlayCount),
                    _ => a.CompareTo(b),
                };
                if (_sort.Descending) c = -c;
                return c != 0 ? c : a.CompareTo(b);
            }
        }

        int OriginalOf(int display)
        {
            var v = ViewNow();
            return (uint)display < (uint)v.Length ? v[display] : -1;
        }

        // ── the date groups (tuned Classic D16): per display row of the sorted view, its group key and, where a group starts, its run ──

        int[] _groupKeys = [], _groupRuns = [];
        Snapshot _groupsKey;
        bool _groupsValid;

        readonly Dictionary<(int Key, int Run), DateGroupHead> _groupHeads = new();

        /// <summary>The list CAN group by date: a <see cref="Detail.Config.DateGroups"/> page's own table in its wide arm (never the library
        /// pane's embedded one). Fixed for the host (the arm is in the table's key), so a slot decides once whether it carries a head.</summary>
        internal bool GroupsCapable => Cfg.DateGroups && (!VerticalArm || ColumnsForm) && !_latest.Embedded;

        /// <summary>The list groups by date now: it can, and it is sorted by when its songs were added.</summary>
        bool GroupsOn(in Snapshot s) => GroupsCapable && s.Sort.Column == SortColumn.DateAdded && _latest.Source.HasDateAdded;

        /// <summary>The head of the date group that STARTS at this display index, or null (no groups, or a row inside a group). Reads the
        /// snapshot, so a slot's memo over it follows the sort, the filter and the membership.</summary>
        internal DateGroupHead? GroupHeadAt(int display)
        {
            if (_snapshot is not { } memo) return null;
            var s = memo.Value;
            if (!GroupsOn(in s)) return null;
            var v = ViewOf(in s);
            if ((uint)display >= (uint)v.Length) return null;
            if (!_groupsValid || !_groupsKey.Equals(s)) RebuildGroups(in s, v);
            int run = _groupRuns[display];
            if (run == 0) return null;
            // Cached per (group, run): a recycle onto a head row allocates nothing once the head has been worded.
            int key = _groupKeys[display];
            if (_groupHeads.TryGetValue((key, run), out var cached)) return cached;
            var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
            int year = DateTimeOffset.FromUnixTimeSeconds(Store.ToUnix(Entities.Now)).ToOffset(offset).Year;
            string label = DateGroupRules.LabelOf(key, year, System.Globalization.CultureInfo.CurrentCulture,
                Loc.Get(Strings.Detail.DateGroup.ThisWeek), Loc.Get(Strings.Detail.DateGroup.Earlier));
            var head = new DateGroupHead(label, Strings.Detail.SongCount(run));
            _groupHeads[(key, run)] = head;
            return head;
        }

        /// <summary>The structure's head for a date group, at the rows' gutter (an empty box where no registry provides the chrome). BOUND:
        /// <paramref name="head"/> is read inside the template's props, so a recycle onto or off a head row re-binds, never re-renders.</summary>
        internal Element GroupHeader(Func<DateGroupHead?> head, in Shape shape)
            => (_listStyle?.Chrome.GroupHeader(head, RowMetrics.PadXFor(shape.Set.Tier)) ?? new BoxEl()) with { Key = "group-head" };

        /// <summary>The first group's head while the rows shimmer: no words yet (the deriver sizes its bars), present.</summary>
        static readonly DateGroupHead s_pendingHead = new("", "");
        static readonly Func<DateGroupHead?> s_pendingGroupHead = static () => s_pendingHead;

        void RebuildGroups(in Snapshot s, ReadOnlySpan<int> view)
        {
            int n = view.Length;
            if (_groupKeys.Length < n) { _groupKeys = new int[Math.Max(n, _groupKeys.Length * 2)]; _groupRuns = new int[_groupKeys.Length]; }
            var src = _latest.Source;
            long now = Store.ToUnix(Entities.Now);   // AddedAt is unix seconds; Entities.Now is APP seconds
            var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
            for (int i = 0; i < n; i++) _groupKeys[i] = DateGroupRules.KeyOf(src.AddedAt(view[i]), now, offset);
            DateGroupRules.RunsOf(new ReadOnlySpan<int>(_groupKeys, 0, n), new Span<int>(_groupRuns, 0, n));
            _groupHeads.Clear();   // the words follow the clock's year and the language: a rebuilt view re-words its heads
            _groupsKey = s;
            _groupsValid = true;
        }

        Action<int>? _benchToggle;

        /// <summary>The frame bench's drawer handle (Diagnostics.BenchHooks): the last mounted table's, while it is mounted.</summary>
        Action? InstallBenchHook()
        {
            var toggle = _benchToggle ??= display =>
            {
                var t = DisplayTrack(display);
                if (t.IsValid) ToggleExpanded(MembershipDiff.RowKey(t, ItemIdAt(display), display), t);
            };
            var node = _benchDrawerNode ??= () => _drawerNode;
            Diagnostics.BenchHooks.ToggleTrackDrawer = toggle;
            Diagnostics.BenchHooks.TrackDrawerNode = node;
            return () =>
            {
                if (ReferenceEquals(Diagnostics.BenchHooks.ToggleTrackDrawer, toggle)) Diagnostics.BenchHooks.ToggleTrackDrawer = null;
                if (ReferenceEquals(Diagnostics.BenchHooks.TrackDrawerNode, node)) Diagnostics.BenchHooks.TrackDrawerNode = null;
            };
        }

        Track DisplayTrack(int display)
        {
            int orig = OriginalOf(display);
            return orig >= 0 ? _latest.Source.At(orig) : default;
        }

        internal StringId ItemIdAt(int display)
        {
            int orig = OriginalOf(display);
            return orig >= 0 ? _latest.Source.ItemId(orig) : default;
        }

        internal IReadSignal<Track> BindItemFor(RowScope scope, int start)
            => scope.Runtime is { } runtime ? _rowItems!.BindItem(scope.Index, runtime, start) : _rowItems!.BindItem(scope.Index, start);

        /// <inheritdoc cref="BindItemFor"/>
        internal IReadSignal<Episode.RowItem> BindEpisodeItemFor(RowScope scope, int start)
            => scope.Runtime is { } runtime ? _episodeItems!.BindItem(scope.Index, runtime, start) : _episodeItems!.BindItem(scope.Index, start);

        /// <summary>What <see cref="TableSlot"/> renders at this DISPLAY index: a track row or an episode row (a mixed
        /// playlist, or Your Episodes). Track for every source that predates the podcast rework.</summary>
        internal RowTemplate RowKindAt(int display)
        {
            int v = OriginalOf(display);
            return v >= 0 && _latest.Source.KindAt(v) == EntityKind.Episode ? RowTemplate.Episode : RowTemplate.Track;
        }

        // ── selection · view-state writers ────────────────────────────────────────────────────────────────────────────

        /// <summary>Selected TRACK rows only (never a recs section or a hero prefix) — over the RANGES, so Select-all on 5,000
        /// rows is one range, not 5,000 probes.</summary>
        int ComputeSelectedCount()
        {
            _ = _selection.Version.Value;
            int start = TrackStart;
            int end = start + (_rowItems?.Count.Value ?? 0) - 1;
            int n = 0;
            for (int r = 0; r < _selection.RangeCount; r++)
            {
                var (s, e) = _selection.GetRange(r);
                int a = Math.Max(s, start), b = Math.Min(e, end);
                if (b < a) continue;
                n += b - a + 1;
            }
            return n;
        }

        void SetSort(SortSpec sort)
        {
            _sort.Value = sort;
            var ctx = _latest.Source.Context;
            if (!ctx.IsValid) return;
            Platform.Settings.Set(Detail.SortKeys.Column(ctx), (int)sort.Column);
            Platform.Settings.Set(Detail.SortKeys.Descending(ctx), sort.Descending);
        }

        void SetFilters(FilterState filters) => _filters.Value = filters;

        void SetMultiSelect(bool on)
        {
            if (!on) _selection.ClearSelection();
            _multi.Value = on;
        }

        /// <summary>A context's own view: the persisted sort (−1 sentinel ⇒ the profile default) and a fresh query/filter/
        /// multi-select. From a layout effect keyed on the context — never from Render.</summary>
        void LoadView(EntityUri context)
        {
            // A context change starts fresh (the one-tree page keeps one table across every crossing, audit B9: nothing to hand over).
            _query.Value = "";
            _filters.Value = default;
            _multi.Value = false;
            _searchExpanded.Value = false;
            _searchFocused.Value = false;
            _restoreSearchFocus = false;
            _compactInteractive.Value = false;
            _expanded.Value = "";
            _selection.ClearSelection();
            int col = context.IsValid ? Platform.Settings.Get(Detail.SortKeys.Column(context)) : -1;
            _sort.Value = col < 0
                ? _latest.Profile.DefaultSort
                : new SortSpec((SortColumn)col, Platform.Settings.Get(Detail.SortKeys.Descending(context)));
        }

        // ── Render ──────────────────────────────────────────────────────────────────────────────────────────────────

        public override Element Render()
        {
            var args = _args.Value!;
            _facts = global::Wavee.Theming.TierContext.DrawnIn(UseContext(global::Wavee.Theming.ThemeFactsContext.Signal));
            _overlay = UseContext(Overlay.Service);
            _hooks = UseContext(InputHooks.Current);
            _scrollScope = UseContext(Shell.PageScrollScope);
            // The frame's docked aside holds the recommendations then (contract 1.21): the list does not append them a second time.
            bool asideDocked = UseContext(Detail.AsideDocked);
            _post = UsePost();
            _dockHost = UseContext(global::Wavee.Theming.Contracts.SelectionDockHost.Context);
            // The structure's row templates (T8a): resolved once per host render; the slots read `_listStyle` on their rare renders.
            var registry = UseContext(global::FluentGpu.Theming.ViewRegistry.Context);
            _listStyle = registry is not null && registry.TryGetStyle(global::Wavee.Theming.Contracts.StyleKeys.TrackTable, out var listStyle)
                ? listStyle : null;
            // The structure's hero loading band (T9a): a pure builder, because the skeleton deriver cannot run a view slot.
            _heroStyle = registry is not null && registry.TryGetStyle(global::Wavee.Theming.Contracts.StyleKeys.DetailHero, out var heroStyle)
                ? heroStyle : null;
            // The eager row's grid (T8c): the counted shimmer draws the REAL row's shape from it.
            _registry = registry;
            _eagerStyle ??= global::Wavee.Theming.Contracts.ListStyleLookup.Of(registry, global::Wavee.Theming.Contracts.StyleKeys.EagerTrackRow);
            _drawerStyle ??= global::Wavee.Theming.Contracts.ListStyleLookup.Of(registry, global::Wavee.Theming.Contracts.StyleKeys.TrackDrawer);

            _snapshot = UseComputed(ComputeSnapshot);
            // The structure's table ladder (themes plan 3.6; Classic: Tiers.Detail.cs); it freezes at mount like every hook's inputs.
            _tierSig = UseTier(global::Wavee.Theming.DetailLadders.Of(ShellPolicy.Now.Tiers, global::Wavee.Theming.DetailLadders.Table),
                TierSource.Measured(_listW, _listSeed));
            _shape = UseComputed(ComputeShape);
            _hotSettled = UseComputed(ComputeHotSettled);
            _rowItems = UseMemo(() => BoundItems.Project(_snapshot!,
                snap => ViewOf(in snap).Length,
                (snap, display) =>
                {
                    var v = ViewOf(in snap);
                    if ((uint)display >= (uint)v.Length) return default;
                    int o = v[display];
                    return o >= 0 ? _latest.Source.At(o) : default;
                },
                default(Track)), DepKey.Empty);
            _verticalRecs = UseComputed(VerticalRecsNow);
            _verticalItemCount = UseComputed(VerticalItemCountNow);
            _episodeItems = UseMemo(() => BoundItems.Project(_snapshot!,
                snap => ViewOf(in snap).Length,
                (snap, display) =>
                {
                    var v = ViewOf(in snap);
                    if ((uint)display >= (uint)v.Length) return default;
                    int o = v[display];
                    return o >= 0 ? Episode.RowItem.Of(_latest.Source.EpisodeAt(o)) : default;
                },
                default(Episode.RowItem)), DepKey.Empty);
            _viewMemo = UseComputed(() => new TableView(_sort.Value, _query.Value, _filters.Value, _multi.Value));
            _selectedCount = UseComputed(ComputeSelectedCount);
            _checksVisible = UseComputed(() => SelectionBarRules.ChecksVisible(_selectedCount!.Value, _multi.Value));
            _selectionVisible = UseComputed(() => SelectionBarRules.Visible(_selectedCount!.Value, _multi.Value));
            // A frame that docks the selection bar (Zune): this table publishes the bar's model while the selection shows it, and withdraws it when the table goes.
            UseEffect(PublishSelectionDock);
            UseEffect(() => WithdrawSelectionDock, DepKey.Empty);
            var live = UseMemo(() => new TableLive(_viewMemo!, SetFilters, _visible, PageBar), DepKey.Empty);

            var context = args.Source.Context;
            UseLayoutEffect(() => LoadView(context), DepKey.From(context.Id.GetHashCode()));

            var snap = _snapshot.Value;
            var shape = _shape.Value;
            var cfg = snap.Config;
            bool vertical = VerticalArm;
            bool trailing = cfg.HasTrailing;
            int visible = _rowItems.Count.Value;

            // ── a context change: navigation is not an edit, and a fresh identity may ramp ──
            if (!_contextSeeded || !snap.Context.Equals(_contextSeen))
            {
                _contextSeeded = true;
                _contextSeen = snap.Context;
                _contextText = context.IsValid ? context.Text : "";
                _settled = false;
                _orderSeeded = false;
                _curLen = _prevLen = 0;
                _flip.Clear(); _fade.Clear();
                _resetEpoch++;
                _rampDecided = false;
                _sawUnknown = false;
            }
            // "This context shimmered": the edge was unanswered, or it answered before its first page knew its face (the
            // hot-row half of TableRules.RowsPending). Either way the release is a cold reveal and the ramp must run.
            if (snap.State == EdgeState.Unknown || !_hotSettled!.Value) _sawUnknown = true;
            // The ramp arms once per context, on the frame the shimmer releases (TableRules.RowsPending — the first page is in
            // AND its rows know their face, or the edge is Complete and they do): a cold (shimmered) open always, and a
            // HasTrailing page on warm opens too — that list is one unwindowed mandatory band (DetailTracks.cs:812-817). The
            // ramp therefore never runs over rows that have no data. A Failed edge leaves the ramp undecided, so a retry
            // that then lands rows still gets its reveal. Total is read live off the source: the snapshot carries
            // Count/State, and Total is ≥ Count by contract so the same Changed signal moves them together.
            int total = _latest.Source.Total;
            if (!_rampDecided && snap.State is EdgeState.Partial or EdgeState.Complete
                && !TableRules.RowsPending(snap.State, snap.Count, total, _hotSettled!.Value))
            {
                _rampDecided = true;
                // Where the structure reveals the hero arm's rows together (ListPolicy.RowsRevealTogether), they are already mounted, each holding its own
                // shimmer on the gate (TableRowContent), so a ramp there would only turn rows past the first chunk back into blank placeholders for a frame:
                // the arm releases every row at once instead.
                bool together = vertical && !trailing && ShellPolicy.Tracked.Lists.RowsRevealTogether;
                if ((_sawUnknown || trailing) && !together) { _rampPending = true; _rampEpoch++; }
            }
            UseLayoutEffect(ArmRamp, DepKey.From(_rampEpoch));

            // ── membership choreography (§4.6) + the breakpoint re-deal, seeded in the render that commits the order ──
            int resetBefore = _resetEpoch;
            int dealBefore = _dealEpoch;
            TrackOrder(in snap, shape.RowH);
            if (_latest.Form != _formSeen) { _formSeen = _latest.Form; _formChangedAtMs = Environment.TickCount64; }
            ReDeal(shape.Set.Tier, shape.RowH);
            // A row-height change no deal narrates glides the rows to their new places (a deal owns the rows when one ran).
            if (_dealEpoch == dealBefore) ReRow(shape.RowH, trailing);
            else _rowHSeen = shape.RowH;
            _dealtThisFrame = false;
            UseLayoutEffect(BumpDisplacement, DepKey.From(_dealEpoch));
            bool narrate = _resetEpoch != resetBefore;

            if (args.PlayAllCell is { Length: > 0 } cell) cell[0] = _playAll;

            Element? chips = P.ContentFilterBar?.Invoke();
            Element? lens = P.LensHeader?.Invoke();
            // The unavailable-songs bar rides the pinned chrome while it is on (contract 1.22): the rows clip under it too.
            // What the pinned chrome holds: the chips ride the page bar instead where the page has one (the hero list's columns form of a page with no rail).
            float extent = (chips is not null && !PageBar ? P.ContentFilterExtent : 0f) + (lens is not null ? P.LensExtent : 0f) + UnavailableBarExtent();
            float stickyInset = Detail.VerticalLayout.StickyClipInset(extent, TableRules.HeaderHeightFor(shape.Set), StickLine());
            // The hero list's item band is LIVE (`ItemClipTopInsetSource`): the list re-reads it on its every render, so the band a later measure moves (the
            // columns form's pinned head, audit B9; the Liked chips arriving) is never overwritten by the inset the list froze at mount.
            _itemBandSig ??= new Signal<float>(stickyInset);
            UseLayoutEffect(() => ApplyItemBand(stickyInset), DepKey.From(stickyInset, (float)_resetEpoch));

            // The hero arm appends the section too (C-Playlist-Compact, C-Empty-Compact): after its rows, as one more slot of its viewport.
            bool recsCapable = cfg.Recommendations && !args.Embedded && !asideDocked && P.Recommendations is not null;
            var listState = Playlist.RowsStateOf(snap.State is EdgeState.Partial or EdgeState.Complete, snap.Count, visible);
            bool recsLive = recsCapable && listState is PlaylistRowsState.Rows or PlaylistRowsState.Empty;
            int listTotal = visible + (recsLive ? 1 : 0);
            UseLayoutEffect(() =>
            {
                _visible.Value = visible;
                _listCount.Value = listTotal;
                _recsCapable.Value = recsCapable;
                _emptyLead.Value = recsLive && listState == PlaylistRowsState.Empty;
            }, DepKey.From(visible, listTotal, (recsLive ? 1 : 0) | (recsCapable ? 2 : 0), listState == PlaylistRowsState.Empty ? 1 : 0));
            UseEffect(PublishHeroHeight);
            var listCount = CountNow(in snap, visible) with { EmptyLead = recsLive && listState == PlaylistRowsState.Empty };
            UseEffect(() => { if (_latest.ListCount is { } target) target.Value = listCount; },
                      DepKey.From(listCount.Shown, listCount.Total, (int)(listCount.ShownMs / 1000L),
                                  (listCount.Filtered ? 1 : 0) | (listCount.DurationsKnown ? 2 : 0) | (listCount.EmptyLead ? 4 : 0)));
            UseEffect(InstallBenchHook, DepKey.Empty);

            float rowH = shape.RowH;
            // An episode-capable source has rows of more than one height, so the fixed `MeasuredStackVirtualLayout`
            // (every row = `rowH`) gives way to `RepeatLayout.VariableList` (an estimate, then measured per realized
            // slot); a pure track list keeps the fixed layout untouched.
            bool variableRows = _latest.Source.HasEpisodes;
            var layout = UseMemo(() => variableRows
                    ? RepeatLayout.VariableList(shape.EpisodeRowH > 0f ? shape.EpisodeRowH : rowH)
                    : RepeatLayout.Measured(new MeasuredStackVirtualLayout(rowH)),
                DepKey.From(variableRows ? 1f : 0f, rowH, shape.EpisodeRowH, 0f));

            Element realList =
                vertical && !trailing ? VerticalList(visible, recsLive, layout, stickyInset, narrate)
                : recsLive ? RecsList(listTotal, layout, trailing, narrate)
                : listState == PlaylistRowsState.Rows ? FlatList(layout, trailing, narrate)
                : Placeholder(listState, in shape);

            Element region = new SkelRegionEl(
                Pending: vertical && !trailing ? VerticalPending : RowsPending,
                Failed: vertical && !trailing ? VerticalFailed : RowsFailed,
                Content: () => realList,
                ShimmerSource: vertical && !trailing ? () => VerticalShimmer(in shape, chips, lens) : () => RowsShimmer(in shape),
                OnFailed: FailedPanel,
                Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);

            // Density · skin · (two-column only) query+filter · reset epoch · recs, and `vh:` for the vertical arm. NOT the
            // tier and NOT the sort (ch 04 §0.3/§0.5, ch 30 §5.3).
            // The hero list counts its recommendations live (`_verticalItemCount`): they never re-key it (a tier that docks them in the aside must not
            // remount the list, audit B9).
            string filterKey = vertical ? "" : ":q" + HashCode.Combine(snap.Query, snap.Filters).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string listKey = "list:" + args.ScrollKey + ":" + (vertical ? "vh:" : "") + "d" + shape.Density
                             + (shape.Set.Classic ? ":classic" : ":modern") + filterKey + ":r" + _resetEpoch + (recsCapable && !vertical ? ":rec" : "");
            // The list's box names the hero arm's sticky SCOPE: the prefix items pin inside the list's viewport, and a scope
            // that encloses the whole scroller releases only at the content's end (see VerticalList).
            Element listKeyed = new BoxEl
            {
                Key = listKey, Grow = trailing ? 0f : 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, Children = [region],
                // An album's rows stand in the outer scroller: across a row-height change the list's height eases through real layout (the rows glide
                // inside it), so the trailing band under it rides it frame by frame (audit B9).
                Layout = trailing && !Design.Reduced ? s_listResize : null,
                ScrollScope = vertical && !trailing ? TableScope : null,
            };

            Element body = trailing ? TrailingBody(listKeyed, vertical, stickyInset, in shape, chips, lens) : listKeyed;
            var column = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                OnBoundsChanged = _onBounds,
                // The two-column chrome (toolbar · chips · lens · column header) sits ABOVE the list, outside its
                // viewport, so a wheel over it reached nothing; it names the list's scroll handle as its wheel target and
                // the notch glides the rows. In the album (trailing) arm the OUTER ScrollView owns scrolling, not the
                // list, so the header names nothing. The hero arm's chrome is item 1 of the list itself and needs none.
                Children = vertical
                    ? [body]
                    : [Chrome(in shape, chips, lens) with { WheelTarget = trailing ? null : _listScroll }, body],
            };

            // The per-frame reveal clock, mounted ONLY while a ramp is in flight so the frame loop quiesces after it; a
            // 0×0 hit-invisible box so the sibling never captures the wheel.
            Element clock = new BoxEl
            {
                HitTestVisible = false, Width = 0f, Height = 0f,
                Children = [Flow.Show(_rampActiveRead, Embed.Comp(() => new TableTicker(this)))],
            };
            return ZStack(Ctx.Provide(TableLiveSlot, live, column), clock) with { Grow = 1f, Shrink = 1f, MinHeight = 0f };
        }

        bool RowsPending()
        {
            var src = _latest.Source;
            src.Subscribe();
            return TableRules.RowsPending(src.State, src.Count, src.Total, _hotSettled!.Value);
        }

        bool RowsFailed()
        {
            var src = _latest.Source;
            src.Subscribe();
            return src.State == EdgeState.Failed;
        }

        // The vertical arm's hero and chrome are PREFIX ITEMS inside the boundary (D49). They shimmer as a band only while
        // the page's header has not answered (Identity.HeaderPending — the same flag the two-column rail pends on); once
        // it has, the list mounts and its empty slot shimmers the rows instead.
        bool VerticalPending() => RowsPending() && (_latest.Vertical?.Identity.HeaderPending ?? true);
        bool VerticalFailed() => RowsFailed() && (_latest.Vertical?.Identity.HeaderPending ?? true);

        void ArmRamp()
        {
            if (!_rampPending) return;
            _reveal.Value = Design.RevealRamp.Chunk;
            _rampActive.Value = true;
            _rampPending = false;
        }

        /// <summary>Advance the ramp one chunk; once per frame from the ticker, never from its mount run (see
        /// <see cref="TableTicker"/>). Peek/arithmetic/signal writes only. A step at Done adds nothing, and a step that ENDS
        /// at Done always drops the gate, so the ticker unmounts and the loop idles (G-259).</summary>
        internal void AdvanceReveal()
        {
            int reveal = _reveal.Peek();
            if (reveal != Design.RevealRamp.Done)
            {
                reveal = Design.RevealRamp.Next(reveal, _visible.Peek());
                _reveal.Value = reveal;
            }
            if (reveal == Design.RevealRamp.Done) _rampActive.Value = false;
        }

        /// <summary>Is this display row REAL yet? Reads the signal (subscribing the caller's per-row bool memo) and, on the
        /// render that arms a ramp, the pending chunk — so rows mounting before the arming layout effect already agree.</summary>
        internal bool RowRevealed(int displayIndex)
        {
            int reveal = _reveal.Value;
            if (_rampPending) reveal = Design.RevealRamp.Chunk;
            return Design.RevealRamp.Revealed(displayIndex, reveal);
        }

        /// <summary>What the list shows against what it has (tuned Classic D4): the shown rows' length is summed only while a find or a
        /// filter narrows the list (the page's own meta speaks otherwise), once per snapshot.</summary>
        Detail.ListCount CountNow(in Snapshot snap, int shown)
        {
            var src = _latest.Source;
            int total = Math.Max(src.Total, src.Count);
            bool filtered = snap.Query.Length > 0 || snap.Filters.ActiveCountFor(snap.Query.Length > 0) > 0;
            if (!filtered || Cfg.Content == DetailContent.Episodes) return new Detail.ListCount(shown, total, 0L, false, false);
            if (!_countValid || !_countKey.Equals(snap))
            {
                long ms = 0L;
                bool known = true;
                foreach (int i in ViewOf(in snap))
                {
                    var t = src.At(i);
                    if (t.DurationMs <= 0) known = false;
                    ms += t.DurationMs;
                }
                _countMs = ms; _countKnown = known; _countKey = snap; _countValid = true;
            }
            return new Detail.ListCount(shown, total, _countMs, _countKnown, true);
        }

        Snapshot _countKey;
        bool _countValid, _countKnown;
        long _countMs;

        void PublishHeroHeight()
        {
            float h = _heroH.Value;
            if (_latest.HeroHeight is { } target && h > 1f && MathF.Abs(target.Peek() - h) > 0.5f) target.Value = h;
        }

        void BumpDisplacement()
        {
            if (_flip.Count == 0 && _fade.Count == 0) return;
            _dispVer.Value = _dispVer.Peek() + 1;
        }

        /// <summary>The hero list's live item-band inset (<see cref="ApplyItemBand"/> writes it; the list reads it on its renders).</summary>
        Signal<float>? _itemBandSig;

        /// <summary>The item band moved after mount (the Liked chip rail / lens arriving after enrichment, 93 → 141 DIP; the columns form's pinned head
        /// measuring): the live source the list re-reads on its renders, and the viewport's shared band patched in place for this very frame, so no
        /// remount (DetailTracks.cs:1721-1735).</summary>

        void ApplyItemBand(float inset)
        {
            if (_itemBandSig is { } band) band.Value = inset;
            if (!VerticalArm || Cfg.HasTrailing || Context.Scene is not { } scene) return;
            var viewport = _listCtl.Viewport;
            if (viewport.IsNull || !scene.IsLive(viewport) || !scene.HasScroll(viewport)) return;
            ref ScrollState sc = ref scene.ScrollRef(viewport);
            if (MathF.Abs(sc.ItemClipTopInset - inset) <= 0.01f
                && MathF.Abs(sc.ItemClipTopFadeBand - Detail.VerticalLayout.StickyFadeBand) <= 0.01f) return;
            sc.ItemClipTopInset = inset;
            sc.ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand;
            scene.Mark(viewport, NodeFlags.PaintDirty);
            if (!sc.ContentNode.IsNull && scene.IsLive(sc.ContentNode)) scene.Mark(sc.ContentNode, NodeFlags.PaintDirty);
        }

        // ══ 4. THE LIST ARMS ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The page's scroll scope (<see cref="Shell.PageScrollScope"/> — the tab), composed onto every restore key.</summary>
        string _scrollScope = "";

        string ListScrollKey => _scrollScope + _latest.ScrollKey + ":r" + _resetEpoch;

        /// <summary>The album arm's outer scroller's restore key: <see cref="ListScrollKey"/>'s identity, its own lane.</summary>
        string TrailScrollKey => _scrollScope + _latest.ScrollKey + ":trail:r" + _resetEpoch;

        /// <summary>The slot ROOT every list path returns. The list's roving focus lands on the slot root, and a component's
        /// node (its anchor) cannot carry a handler, so the root is this box: it hears the focus edge and feeds the slot's
        /// focus fact (<c>RowScope.IsFocused</c>: the keyboard-current tracking and the row's focus reveals, tuned Classic L2).
        /// Without it the skin's own edge never fired — the focus sits on its ancestor.</summary>
        static Element FocusRoot(Element slot, Action<bool> focusEdge)
            => new BoxEl { Direction = 1, MinWidth = 0f, OnFocusChanged = focusEdge, FocusVisualMargin = s_rowFocusRing, HitTestPassThrough = true, Children = [slot] };

        /// <summary>The keyboard-focus ring of a row, drawn INSIDE the slot's bounds. The slot root (this box) is the focus node (the roving tab stop), not
        /// the row's skin; the engine's default margin (WinUI's -3) pushed the ring 3 DIP outside it, where the list's viewport clipped its left and
        /// right edges. A positive margin never shrinks the ring, so 0 puts the 2 DIP outer and 1 DIP inner strokes on the row's own edge.</summary>
        static readonly Edges4 s_rowFocusRing = Edges4.All(0f);

        Element FlatList(RepeatLayout layout, bool trailing, bool narrate)
            => ItemsView.CreateBound(_rowItems!,
                scope => FocusRoot(Embed.Comp(() => new TableSlot(this, scope.Row, 0, narrate)), scope.Row.OnFocusChanged),
                layout,
                new ListOptions<Track>
                {
                    SelectionMode = Cfg.Selection,
                    Selection = _selection,
                    IsItemInvokedEnabled = true,
                    OnInvokedTyped = (i, _) => PlayRow(i),
                    ContentType = i => (int)RowKindAt(i),
                    Grow = trailing ? 0f : 1f,
                    Controller = _listCtl,
                    // Alpha-mask edge fade: the page floats over a tone plane with no opaque plate. Nested in the album
                    // trailing scroller, the OUTER ScrollView owns scrolling and its cue.
                    Scroll = new ScrollOptions { AutoEdgeFade = !trailing, ScrollKey = ListScrollKey, Handle = _listScroll },
                    Reorder = new ReorderOptions { DisplacementVersion = _dispVer },
                    Insertion = Insertion(),
                    Entrance = new EntranceOptions { ItemFlipFrom = _flipFrom, ItemFadeFrom = _fadeFrom },
                });

        /// <summary>Track rows, then ONE appended item: the recommendations section (owner O's element, header and states
        /// included). The COUNT carries the section only while the page has rows or is empty — never over a shimmer.</summary>
        Element RecsList(int total, RepeatLayout layout, bool trailing, bool narrate)
            => ItemsView.CreateBound(total,
                scope => FocusRoot(Embed.Comp(() => new TableRecItem(this, scope, narrate)), scope.OnFocusChanged),
                layout,
                new ListOptions
                {
                    SelectionMode = Cfg.Selection,
                    Selection = _selection,
                    IsItemInvokedEnabled = true,
                    OnInvoked = i => { if (_rowItems!.TryPeek(i, out _)) PlayRow(i); },
                    IsItemEnabled = i => _rowItems!.TryPeek(i, out _),
                    Grow = trailing ? 0f : 1f,
                    Controller = _listCtl,
                    CountSignal = _listCount,
                    Scroll = new ScrollOptions { AutoEdgeFade = !trailing, ScrollKey = ListScrollKey, Handle = _listScroll },
                    Reorder = new ReorderOptions { DisplacementVersion = _dispVer },
                    Insertion = Insertion(),
                    Entrance = new EntranceOptions { ItemFlipFrom = _flipFrom, ItemFadeFrom = _fadeFrom },
                });

        /// <summary>The sticky scope of the hero arm's list (named on the list's own box, which encloses the scroller: a
        /// pin clamped to it releases only at the content's end).</summary>
        const string TableScope = "track-table";

        /// <summary>The album arm's sticky scopes: the scroller's content column (the hero pins across all of it) and the
        /// table block (chrome + rows — the chrome releases at the last row, never over the trailing shelves).</summary>
        const string TrailScope = "track-table:trail", TableBlockScope = "track-table:block";

        /// <summary>Playlist/Liked hero system: a HARD viewport whose items 0 and 1 are the hero (pinned under the 56-DIP
        /// band) and the chrome (sticky at 56), kept mounted; the recyclable suffix is clipped by ONE shared band at the sticky
        /// inset with a 24-DIP feather, and no stock scroll-edge cue (ch 03 §0.16). The two prefix items pin from their
        /// component's own rendered root (<see cref="HeroItem"/>, <see cref="ChromeItem"/>) against the declared
        /// <see cref="TableScope"/> — a named scope, so no raw wrapper is needed around the item component.</summary>
        Element VerticalList(int visible, bool recs, RepeatLayout layout, float stickyInset, bool narrate)
        {
            int prefix = Detail.VerticalLayout.PrefixCount;
            return ItemsView.CreateBound(Detail.VerticalLayout.ItemCount(visible, recs),
                scope => FocusRoot(Embed.Comp(() => new TableVerticalItem(this, scope, narrate)), scope.OnFocusChanged),
                layout,
                new ListOptions
                {
                    // NEVER data-dependent. `ItemsView.Create` is an `Embed.Comp` factory, so SelectionMode
                    // FREEZES at mount (component-props contract): a list that mounted while its rows were
                    // still landing used to freeze at `None` for its whole life, and WinUI's CanRaiseItemInvoked
                    // matrix invokes on a single TAP in that mode. Same page, same click, play-or-select decided
                    // by whether the rows happened to arrive first — the owner's "coinflip", 2026-09-20. An empty
                    // list has nothing to select, so the guard bought nothing.
                    SelectionMode = Cfg.Selection,
                    Selection = _selection,
                    IsItemInvokedEnabled = true,
                    OnInvoked = i => { if (_rowItems!.TryPeek(i, out _, prefix)) PlayRow(i - prefix); },
                    ItemText = i => _rowItems!.TryPeek(i, out var t, prefix) ? t.Title : "",
                    IsItemEnabled = i => _rowItems!.TryPeek(i, out _, prefix),
                    // The recommendations section recycles in a pool of its own (it is never a row).
                    ContentType = i => i < prefix ? -1 - i
                        : Detail.VerticalLayout.ItemRole(i, _rowItems!.Count.Peek(), _verticalRecs.Peek()) == VerticalItemRole.Recommendations ? -1 - prefix
                        : (int)RowKindAt(i - prefix),
                    PersistentPrefixCount = prefix,
                    Grow = 1f,
                    Controller = _listCtl,
                    CountSignal = _verticalItemCount,
                    Scroll = new ScrollOptions
                    {
                        ScrollKey = ListScrollKey,
                        Handle = _listScroll,
                        AutoEdgeFade = false,
                        EdgeCues = ScrollEdgeCues.None,
                        ItemClipTopInset = stickyInset,
                        ItemClipTopInsetSource = _itemBandSig,
                        ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand,
                    },
                    Reorder = new ReorderOptions { DisplacementVersion = _dispVer },
                    Insertion = Insertion(),
                    Entrance = new EntranceOptions { ItemFlipFrom = _verticalFlipFrom, ItemFadeFrom = _verticalFadeFrom },
                });
        }

        /// <summary>Album/single (HasTrailing): one outer scroller. In the hero arm the hero is the first child and pins
        /// across the whole content column (<see cref="TrailScope"/>); the chrome's sticky scope is
        /// <see cref="TableBlockScope"/> — <c>tableBlock</c> holds the chrome and the rows as ONE box whose height ends
        /// with the last row, so the chrome releases there instead of pinning over the trailing shelves. The trailing
        /// shelves sit AFTER it, in their own block, clipped at the same compact-band line so nothing shows through it
        /// while still riding under the band.</summary>
        Element TrailingBody(Element listKeyed, bool vertical, float stickyInset, in Shape shape, Element? chips, Element? lens)
        {
            var spec = _latest.Vertical;
            var kids = new List<Element>(4);
            // The frame's trailing thunk is the whole body under the rows — in the hero arm it already carries the
            // countdown and About-this-release (no rail, so they move BELOW the rows, ch 03 §1.1). Only a caller that
            // passed none gets the slots composed here.
            if (_latest.Trailing is { } frameTrailing) kids.Add(frameTrailing());
            else if (vertical && spec is not null)
            {
                if (spec.Slots.PreRelease?.Invoke() is { } countdown) kids.Add(countdown);
                if (spec.Slots.ReleasePanel?.Invoke(true) is { } release) kids.Add(release);
                if (spec.Slots.Trailing?.Invoke() is { } trail) kids.Add(trail);
            }

            Element[] children;
            if (vertical && spec is not null)
            {
                Element listBlock = new BoxEl
                {
                    Direction = 1,
                    // Feathered exactly while the body clip is engaged, on the render turn that poses it (WhileStuck —
                    // RCA 2026-09-25 F(ii): a re-render off an engaged signal lagged the cut by two presents).
                    EdgeFade = new EdgeFadeSpec(EdgeMask.Top, Detail.VerticalLayout.StickyFadeBand) { WhileStuck = true },
                    Children = [listKeyed],
                }.StickyClip(stickyInset);
                // The pinned line: the band the collapsing hero leaves, or the pinned head of the columns form (audit B9).
                float line = StickLine();
                // The list column's insets (the structure's page inset, WinUI: the list under the hero inside the page's gutter while the scroller and
                // its bar keep the card's edge; the hero's side columns in the columns form): bound spacers, never a padding, so they ease.
                Element tableBlock = InsetList(new BoxEl
                {
                    Direction = 1, ScrollScope = TableBlockScope,
                    Children =
                    [
                        Chrome(in shape, chips, lens).Sticky(line, TableBlockScope, engaged: _compactInteractive),
                        listBlock,
                    ],
                });
                Element trailBlock = InsetList(new BoxEl { Direction = 1, Children = kids.ToArray() })
                    .StickyClip(line);
                // A structure with its own band (WinUI's opaque 48 bar) also clips the block at the band's line: the chrome that the block's end pushes up must
                // not paint over the bar it passes under. The shell's transparent 56 band leaves it as it was.
                if (ShellPolicy.Tracked.DetailTiers.For(TiersNow()).BandHeight > 0f) tableBlock = tableBlock.StickyClip(line);
                children = [TrailHero(spec), tableBlock, trailBlock];
            }
            else
            {
                Element content = new BoxEl
                {
                    Direction = 1,
                    EdgeFade = vertical
                        ? new EdgeFadeSpec(EdgeMask.Top, Detail.VerticalLayout.StickyFadeBand) { WhileStuck = true }
                        : null,
                    Children = [listKeyed, .. kids],
                };
                children = [vertical ? content.StickyClip(stickyInset) : content];
            }
            // Keyed by the route/pane identity: a host that PERSISTS across contents (the library pane re-skins in place)
            // must not hand the next album the previous one's offset. A page mounts one per route anyway, so this only ever
            // costs the pane a fresh viewport. The restore key is the same identity in the page's scroll scope, with the
            // content's reset epoch (exactly like the flat list's, ListScrollKey): an album evicted from KeepAlive and revisited,
            // or remounted by a theme switch, opens at the offset it was left at, in its first layout (E8/F12), and a
            // different album or a new content load starts at the top.
            return ScrollView(new BoxEl { Direction = 1, Grow = 1f, AlignSelf = FlexAlign.Stretch, ScrollScope = TrailScope, Children = children }) with
            {
                Key = "trail:" + _latest.ScrollKey,
                ScrollKey = TrailScrollKey,
                Handle = _trailScroll,
                Grow = 1f,
                // WinUI's per-notch distance (15 % of the viewport, floored at 48 DIP) snapped to a whole number of
                // rows at the live density, so a notch lands on a row boundary instead of slicing one in half. Dynamic
                // on purpose — the density control changes RowH under the same viewport.
                ScrollLineDip = shape.RowH,
                EdgeCues = vertical ? ScrollEdgeCues.None : ScrollEdgeCues.Auto,
            };
        }

        /// <summary>The album arm's hero, pinned at the viewport top across the whole content column and COLLAPSING into
        /// the compact band: over the collapse distance its presented height shrinks from the hero height to the band, and
        /// the clipping root cuts the expanded presentation (paint and input) at that edge — below it the chrome and rows
        /// take the input. Leading-anchored: the presentation carries its own slide-away (Detail.Hero's parallax).</summary>
        Element TrailHero(Detail.VerticalSpec spec)
        {
            float colW = ColumnWidth();
            float heroH = HeroHeightFor(spec, colW);
            bool columns = ColumnsForm;
            var root = new BoxEl
            {
                // The columns form (audit B9) pins the head whole and its side columns hang below it, beside the rows: no clip there.
                Key = "vertical:hero-root", Direction = 1, ClipToBounds = !columns,
                Children = [new BoxEl { Key = "vhero:header", Direction = 1, Children = [Detail.Hero(HeroSpec(spec), HeroPartsFor(spec, colW, heroH))] }],
            }
            .Sticky(0f, TrailScope);
            return columns ? root : root.Collapse(Detail.VerticalLayout.CollapseDistance(heroH, BandNow()), BandNow(), CollapseAnchor.Leading);
        }

        /// <summary>The hero's spec with its shuffle satellite pointed at THIS table's <see cref="Shuffle"/> (G-264): the
        /// page's <c>FrameActions.Shuffle</c> still decides whether the satellite exists, and the command bar, its "…" item and
        /// the satellite all run the one shuffle. Cached per spec instance, so a steady page allocates nothing.</summary>
        Detail.VerticalSpec HeroSpec(Detail.VerticalSpec spec)
        {
            if (spec.Actions.Shuffle is null) return spec;
            if (!ReferenceEquals(_heroSpecFrom, spec) || _heroSpec is not { } cached)
            {
                cached = spec with { Actions = spec.Actions with { Shuffle = _shuffle } };
                _heroSpecFrom = spec;
                _heroSpec = cached;
            }
            return cached;
        }

        float HeroHeightFor(Detail.VerticalSpec spec, float colW)
        {
            if (ColumnsForm) return _headH.Value;
            float measured = _heroH.Value;
            // Pre-measure: the SAME title plan the skeleton reserves — the known title's, else the pessimistic null-title
            // one (ch 03 §9.3).
            return measured > 1f ? measured : Detail.HeroBandHeight(spec, colW);
        }

        Detail.HeroParts HeroPartsFor(Detail.VerticalSpec spec, float colW, float heroH)
        {
            float left = RowMetrics.PadXFor(_shape!.Value.Set.Tier);
            bool toolbar = _latest.ShowToolbar && spec.Config.Content == DetailContent.Tracks && !EmptyLeadHides;
            // ── INSIGHTS SHEET (additive) ── the frame's own answer to Detail.InsightsSheet.ShowsToggle, threaded down
            //    as the presence of the toggle object: the band does not re-derive it, and the pinned band and the hero
            //    toolbar therefore cannot disagree about whether the sheet has an entry point. The BAND is the one that
            //    survives the collapse (Detail.Insights.cs §7); the hero keeps its own for the pre-stuck range, where
            //    the band is transparent and owns no input.
            var insights = spec.Insights;
            return new Detail.HeroParts(
                Toolbar: toolbar ? Toolbar() : null,
                BandActions: BandActions(insights),
                SelectionBar: Cfg.Selection == ItemsSelectionMode.None || _dockHost is not null ? null : SelectionSurface("compact-selection"),
                SelectionVisible: _dockHost is not null ? s_neverSelecting : _selectionVisible!,
                CompactInteractive: _compactInteractive,
                SearchField: CompactSearch(colW, left, insights is not null),
                SearchExpanded: _searchExpanded,
                ColumnWidth: colW,
                CompactLeft: left,
                HeroHeight: heroH,
                OnHeroMeasured: _onHeroMeasured,
                Form: _latest.Form,
                Columns: _latest.Columns,
                ViewportHeight: Cfg.HasTrailing ? _trailScroll.ViewportSignal : _listScroll.ViewportSignal);
        }

        // ── THE RULE, STATED ONCE ───────────────────────────────────────────────────────────────────────────────────
        // A sticky with NO named scope clamps to its IMMEDIATE parent (CSS containing block), and a component anchor
        // MIRRORS its rendered child's size — so a `.Sticky` on what a component returns, with no scope, never pins. The
        // two prefix items below therefore name `TableScope` (declared on the list's own box, see VerticalList) and pin
        // from the component's rendered root directly.

        /// <summary>Item 0 of the vertical list: the hero, pinned at the viewport top against <see cref="TableScope"/> and
        /// collapsing into the compact band — its presented height shrinks from the hero height to the band over the collapse
        /// distance (hit-testing follows it, so the band below hands its input to the chrome and rows).
        /// <para>The spec is read through <c>_args</c>, never <c>_latest</c>: this runs inside a RECYCLED bound item, so a
        /// plain-field read subscribes it to nothing. A spec change that keeps the hero's height (the meta line going
        /// shimmer → "15 songs · 52 min" at the same 16 DIP) then never reached the hero, which shimmered until an
        /// unrelated relayout. <c>_args</c> is written with <c>SetIfChanged</c> under <see cref="TableArgs"/>' data
        /// equality (which covers <c>Vertical</c>), so this item re-renders exactly when its spec changes.</para></summary>
        internal Element HeroItem()
        {
            var args = _args.Value!;
            var spec = args.Vertical!;
            float colW = ColumnWidth();
            float heroH = HeroHeightFor(spec, colW);
            var hero = Detail.Hero(HeroSpec(spec), HeroPartsFor(spec, colW, heroH)).Sticky(0f, TableScope);
            // The columns form (audit B9) pins the hero whole: it is the page head above the rows, its side columns hanging beside them.
            return args.Form == global::Wavee.Theming.Contracts.DetailForm.Columns
                ? hero
                : hero.Collapse(Detail.VerticalLayout.CollapseDistance(heroH, BandNow()), BandNow(), CollapseAnchor.Leading);
        }

        /// <summary>The line the hero list's chrome pins at and its rows clip under: the band the collapsing hero leaves (the hero form; the hero's own foot
        /// while it is shorter), or the pinned head the hero reports (the columns form, audit B9: the head stands whole above the chrome; its side columns
        /// hang beside the rows). Tracked.</summary>
        float StickLine()
        {
            if (!ColumnsForm)
            {
                float band = BandNow(), shortH = _heroShort.Value;
                return shortH > 1f ? MathF.Min(band, shortH) : band;
            }
            float h = _headH.Value;
            return h > 1f ? h : 0f;
        }

        /// <summary>Item 1: chips · lens · column header, sticky at the compact band (56) against <see cref="TableScope"/>
        /// — see THE RULE above. Its engaged edge writes <c>_compactInteractive</c>: the input hand-off the hero reads.</summary>
        internal Element ChromeItem()
        {
            _ = _args.Value;   // a recycled bound item: subscribe to the props its chips/lens builders read (see HeroItem)
            var shape = _shape!.Value;
            Element? chips = P.ContentFilterBar?.Invoke();
            Element? lens = P.LensHeader?.Invoke();
            return InsetList(Chrome(in shape, chips, lens)).Sticky(StickLine(), TableScope, engaged: _compactInteractive);
        }

        internal Element EmptyItem()
        {
            var snap = _snapshot!.Value;
            var shape = _shape!.Value;
            int visible = _rowItems!.Count.Value;
            var state = Playlist.RowsStateOf(snap.State is EdgeState.Partial or EdgeState.Complete, snap.Count, visible);
            return new BoxEl { Key = "vitem:empty", MinHeight = 160f, Direction = 1, Children = [InsetList(Placeholder(state, in shape))] };
        }

        internal int VisibleCountValue => _rowItems!.Count.Value;
        /// <summary>The hero arm's list carries the recommendations section after its rows (a tracked read: the slot map follows it).</summary>
        internal bool VerticalRecsValue => _verticalRecs.Value;

        /// <summary>The section rides the hero arm's list while the list has rows or is empty (never over a shimmer): the render's rule
        /// (<c>recsLive</c>), read off the same snapshot and row count the slots read.</summary>
        bool VerticalRecsNow()
        {
            var snap = _snapshot!.Value;
            int visible = _rowItems!.Count.Value;
            var state = Playlist.RowsStateOf(snap.State is EdgeState.Partial or EdgeState.Complete, snap.Count, visible);
            return _recsCapable.Value && state is PlaylistRowsState.Rows or PlaylistRowsState.Empty;
        }

        int VerticalItemCountNow() => Detail.VerticalLayout.ItemCount(_rowItems!.Count.Value, _verticalRecs.Value);
        internal Shape ShapeValue => _shape!.Value;
        internal Func<ColorF> AccentRead => _accent;
        internal Element? RecommendationsElement() => P.Recommendations?.Invoke();
        /// <summary>What an episode row (<see cref="EpisodeRowContent"/>) builds its <see cref="Episode.RowContext"/>
        /// from — the same overlay this host attaches a track row's own context menu through.</summary>
        internal IOverlayService? OverlayService => _overlay;
        /// <summary>The table's own multi-select arm/clear, adapted to <see cref="Episode.RowSelectionContext"/> so an
        /// episode row's check lane and click/keyboard wiring read the SAME selection this host owns for a track row.</summary>
        internal Episode.RowSelectionContext EpisodeSelection => new() { Selecting = _multi, Clear = () => SetMultiSelect(false) };
        internal ContextMenuModel? EpisodeMenuFor(Episode e) => e.IsValid ? Episode.Menu(e, new Episode.MenuOptions()) : null;

        /// <summary>What stands in for rows: shimmer while the membership is unknown (and the Failed panel when that ask
        /// died), else the empty / no-match sentence. There is no error/offline arm inside the table (ch 04 W9).</summary>
        Element Placeholder(PlaylistRowsState state, in Shape shape)
        {
            if (state == PlaylistRowsState.Loading)
            {
                var geometry = shape;
                return new SkelRegionEl(
                    Pending: RowsPending, Failed: RowsFailed, Content: static () => new BoxEl(),
                    ShimmerSource: () => RowsShimmer(in geometry), OnFailed: FailedPanel,
                    Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false)
                    with { Key = "rows:loading" };
            }
            return Message(Loc.Get(state == PlaylistRowsState.Empty ? Strings.Detail.Empty.NoTracks : Strings.Detail.Empty.NoMatch));
        }

        static Element Message(string text) => new BoxEl
        {
            Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(Spacing.L, Spacing.XXL, Spacing.L, Spacing.XXL),
            Children = [new TextEl(text) { Size = 14f, Color = Tok.TextTertiary }],
        };

        /// <summary>A membership we could not READ is not an empty playlist: its own sentence, and a Retry that re-asks.</summary>
        Element FailedPanel() => new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.L, Spacing.XXL, Spacing.L, Spacing.XXL),
            Children =
            [
                new TextEl(Loc.Get(Strings.Detail.Empty.LoadFailed)) { Size = 14f, Color = Tok.TextTertiary },
                Button.Standard(Loc.Get(Strings.Common.Retry), () => _latest.Source.Retry()),
            ],
        };

        /// <summary>12 copies of the REAL row grid over an empty track — the deriver shimmers the row's own shape (one
        /// source; the shimmer can never drift from the rows). A two-column Modern row sits inside its skin's plate inset
        /// (<see cref="RowMetrics.RowInset"/> either side), so its copy does too (tuned Classic X2: the bars used to start 8 DIP
        /// left of the cells and run 16 wider). A list that groups by date (Liked Songs, D16, sorted by when its songs were added — its
        /// default order) always opens on a group's head, so the first copy carries the head's band too (the rows used to land a head's
        /// 40 DIP below the bars); the heads further down depend on the dates, which have not landed.</summary>
        Element RowsShimmer(in Shape shape)
        {
            Edges4 inset = !shape.Set.Classic && (!VerticalArm || ColumnsForm) ? new Edges4(RowMetrics.RowInset, 0f, RowMetrics.RowInset, 0f) : default;
            bool groups = _snapshot is { } memo && GroupsOn(memo.Peek());
            var rows = new Element[Design.RevealRamp.Chunk];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = ShimmerGrid(in shape, i);
                if (inset != default) rows[i] = new BoxEl { Direction = 1, Margin = inset, Children = [rows[i]] };
            }
            if (groups) rows[0] = new BoxEl { Direction = 1, Children = [GroupHeader(s_pendingGroupHead, in shape), rows[0]] };
            return new BoxEl { Direction = 1, Children = rows };
        }

        /// <summary>One copy of the REAL row grid over an empty track: what the deriver shimmers.</summary>
        Element ShimmerGrid(in Shape shape, int display)
        {
            var set = shape.Set;
            var state = default(RowState);
            var options = new GridOptions(Art: shape.Art, MoreEnabled: false);
            Element title = new TextEl("") { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
            return _eagerStyle is { } eager ? eager.Grid(default, display, in state, in set, shape.Tracks, shape.RowH, title, in options) : new BoxEl();
        }

        /// <summary>A mounted row's own shimmer (<see cref="TableRowContent"/>): the row grid over an empty track, inside the row's skin.</summary>
        internal Element ShimmerRow(in Shape shape) => ShimmerGrid(in shape, 0);

        /// <summary>Is the table's reveal gate still closed for the rows the HERO ARM has already mounted? The two-column arms hold the whole region on it (their
        /// rows mount only once it opens); the hero arm mounts its rows with the membership's count, so each row holds on it itself. Tracked (the source and the
        /// hot rows), so every row hears the release on the same flush.</summary>
        internal bool RowGateClosed() => VerticalArm && !ConfigOf(_latest).HasTrailing && RowsPending();

        /// <summary>Does the structure reveal the hero arm's rows together (<see cref="Theming.ListPolicy.RowsRevealTogether"/>)? Tracked.</summary>
        internal static bool RowsTogether() => ShellPolicy.Tracked.Lists.RowsRevealTogether;

        /// <summary>The hero arm's shimmer IS its item sequence: the reserved hero band, the REAL chrome, then the rows — so
        /// content replaces each in place instead of appearing above them (D49).</summary>
        Element VerticalShimmer(in Shape shape, Element? chips, Element? lens)
        {
            var spec = _latest.Vertical!;
            return new BoxEl
            {
                Direction = 1,
                Children =
                [
                    _heroStyle?.Skeleton(spec, ColumnWidth(), RowMetrics.PadXFor(shape.Set.Tier), TiersNow(), _latest.Form, _latest.Columns) ?? new BoxEl(),
                    InsetList(Chrome(in shape, chips, lens)),
                    InsetList(RowsShimmer(in shape)),
                ],
            };
        }

        // ══ 5. PLAYBACK ══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The ONE activation funnel (the # transport, double-click, Enter): a not-yet-out row is refused here, so
        /// every path agrees with the dimmed row that withholds its play button (ch 04 §0.8).
        /// <para>B2 (plan §3.2): dispatches on the row's kind first — an episode row plays through
        /// <see cref="Episode.Invoke"/> (the disc's toggle-current-else-start semantics), never <see cref="Track.Invoke"/>,
        /// which reads an invalid <see cref="Track"/> for that row.</para></summary>
        internal void PlayRow(int display)
        {
            if (RowKindAt(display) == RowTemplate.Episode)
            {
                int orig = OriginalOf(display);
                var e = orig >= 0 ? _latest.Source.EpisodeAt(orig) : default;
                if (!e.IsValid) return;
                Episode.Invoke(e, () => StartVisible(display));
                return;
            }
            var t = DisplayTrack(display);
            if (!t.IsValid || t.NotYetOut(Store.ToUnix(Entities.Now))) return;
            Track.Invoke(t, () => StartVisible(display));
        }

        /// <summary>Play this context at a display row over the VISIBLE (sorted/filtered) order, so the queue mirrors the
        /// screen, through the ONE held-rows funnel (G-253): <c>Playback.PlayRows</c> lays the rows above the start out as
        /// history (Previous walks back up the list), keeps the user's still-waiting queued rows after the new deck row,
        /// supersedes a context resolve still in flight (the header's Play), drops the saved unshuffle order, caps the
        /// history and the rows, and publishes before its one Play.
        /// <para>B2: for an episode-capable source, every row is tagged by its own kind (<see cref="EntityKind.Track"/>
        /// or <see cref="EntityKind.Episode"/>) rather than assumed Track.</para></summary>
        void StartVisible(int display)
        {
            if (_latest.Profile.PlayFrom is { } custom) { custom(display); return; }
            var src = _latest.Source;
            var view = ViewNow();
            if (view.Length == 0) return;
            if (!src.HasEpisodes)
            {
                if ((uint)display >= (uint)view.Length) display = 0;
                EntityRef[] refs = System.Buffers.ArrayPool<EntityRef>.Shared.Rent(view.Length);
                try
                {
                    for (int i = 0; i < view.Length; i++) refs[i] = new EntityRef(EntityKind.Track, src.At(view[i]).Slot);
                    Playback.PlayRows(refs.AsSpan(0, view.Length), display, src.Context.Id);
                }
                finally { System.Buffers.ArrayPool<EntityRef>.Shared.Return(refs); }
                return;
            }
            var refList = new List<EntityRef>(view.Length);
            int target = 0;
            for (int i = 0; i < view.Length; i++)
            {
                int o = view[i];
                if (i == display) target = refList.Count;
                var kind = src.KindAt(o);
                int slot = kind == EntityKind.Episode ? src.EpisodeAt(o).Slot : src.At(o).Slot;
                refList.Add(new EntityRef(kind == EntityKind.Episode ? EntityKind.Episode : EntityKind.Track, slot));
            }
            if (refList.Count == 0) return;
            Playback.PlayRows(refList.ToArray(), target, src.Context.Id);
        }

        /// <summary>The page's ONE shuffle (G-264; 0.2.9 `DetailShell.Shuffle`): the command bar, its "…" item and the vertical
        /// hero's satellite (<see cref="HeroSpec"/>) all land here. Shuffle on, then the visible order from its first row
        /// through the play funnel — the posted shuffle reorders the next-up run under the new deck in the same drain.</summary>
        internal void Shuffle()
        {
            Playback.SetShuffle(true);
            StartVisible(0);
        }

        /// <summary>"Play next" / "Add to queue" on the whole context through the registered track verb (capped at 50, 0.2.9
        /// DetailQueueActions). An unregistered or disabled verb does nothing — the verb owns its toast.</summary>
        internal void RunOnContext(ActionId id)
        {
            if (AppActions.Find(id) is not { } action) return;
            var src = _latest.Source;
            int n = Math.Min(src.Count, MaxQueueBatch);
            if (n == 0) return;
            var tracks = new Track[n];
            for (int i = 0; i < n; i++) tracks[i] = src.At(i);
            var ctx = new ActionContext(ActionTarget.ForTracks(tracks), Actions.Services);
            if (action.EnabledFor(in ctx)) action.Execute(ctx);
        }

        internal static void ToggleLike(Track t)
        {
            var me = User.Me;
            if (!me.IsValid || !t.IsValid) return;
            if (me.Likes(t)) me.Unlike(t);
            else me.Like(t);
        }

        // ══ 6. SELECTION TARGETS · MENU · BLOCK MOVE ════════════════════════════════════════════════════════════════

        List<Track> SelectedTracks()
        {
            int start = TrackStart;
            var list = new List<Track>();
            for (int r = 0; r < _selection.RangeCount; r++)
            {
                var (s, e) = _selection.GetRange(r);
                for (int i = s; i <= e; i++)
                {
                    var t = DisplayTrack(i - start);
                    if (t.IsValid) list.Add(t);
                }
            }
            return list;
        }

        /// <summary>B2 (plan §3.3): the episode twin of <see cref="SelectedTracks"/>, for Track.Table's batch bar over
        /// an episode-capable source.</summary>
        internal List<Episode> SelectedEpisodes()
        {
            int start = TrackStart;
            var list = new List<Episode>();
            for (int r = 0; r < _selection.RangeCount; r++)
            {
                var (s, e) = _selection.GetRange(r);
                for (int i = s; i <= e; i++)
                {
                    int orig = OriginalOf(i - start);
                    if (orig < 0) continue;
                    var ep = _latest.Source.EpisodeAt(orig);
                    if (ep.IsValid) list.Add(ep);
                }
            }
            return list;
        }

        /// <summary>Which kinds the current selection carries — the batch bar's verb-set decider
        /// (<see cref="SelectionVerbs.For"/>), computed over ranges the same way <see cref="ComputeSelectedCount"/> is.</summary>
        internal (bool AnyTrack, bool AnyEpisode) SelectedKindMix()
        {
            int start = TrackStart;
            bool anyTrack = false, anyEpisode = false;
            for (int r = 0; r < _selection.RangeCount && !(anyTrack && anyEpisode); r++)
            {
                var (s, e) = _selection.GetRange(r);
                for (int i = s; i <= e && !(anyTrack && anyEpisode); i++)
                {
                    var kind = RowKindAt(i - start);
                    if (kind == RowTemplate.Track) anyTrack = true;
                    else if (kind == RowTemplate.Episode) anyEpisode = true;
                }
            }
            return (anyTrack, anyEpisode);
        }

        /// <summary>The hosting playlist for the menu / batch bar: original membership indices of the selected rows, in
        /// display order — only on an editable playlist.</summary>
        PlaylistHost HostFor()
        {
            if (!Editable) return default;
            var playlist = _latest.Source.HostPlaylist;
            if (!playlist.IsValid) return default;
            int start = TrackStart;
            var rows = new List<int>();
            for (int r = 0; r < _selection.RangeCount; r++)
            {
                var (s, e) = _selection.GetRange(r);
                for (int i = s; i <= e; i++)
                {
                    int orig = OriginalOf(i - start);
                    if (orig >= 0) rows.Add(orig);
                }
            }
            return rows.Count == 0 ? default : new PlaylistHost(playlist.Uri, playlist.Caps, rows);
        }

        /// <summary>Explorer semantics, settled BEFORE the host rows are read: a right-click outside the selection collapses it
        /// to the clicked row. "Track details" exists only where the row shows no chevron: no chevron lane, or a row with no
        /// versions to put in it (ShowVersionsMenuItem).</summary>
        /// <summary>Track rows only — an episode row is never wrapped by <see cref="Skin"/> (<see cref="TableSlot"/>
        /// hands it straight to <see cref="Episode.ReaderRow"/>, which dispatches its own "…" to
        /// <see cref="Episode.Menu"/> through the <see cref="Episode.RowContext"/> the table builds).</summary>
        ContextMenuModel? RowMenuFor(int slotIndex, int start, bool expandLane)
        {
            int display = slotIndex - start;
            var t = DisplayTrack(display);
            if (!t.IsValid) return null;
            bool selectable = Cfg.Selection != ItemsSelectionMode.None;
            if (selectable && !_selection.IsSelected(slotIndex))
            {
                _selection.ClearSelection();
                _selection.Select(slotIndex);
            }
            Action? details = null;
            if (TableRules.ShowVersionsMenuItem(Cfg.ShowVersions && P.Drawer is not null, expandLane && t.HasVersions(),
                                                t.Uri.Kind == EntityKind.Track))
            {
                string key = MembershipDiff.RowKey(t, ItemIdAt(display), display);
                details = () => ToggleExpanded(key, t);
            }
            var options = new MenuOptions(Host: selectable ? HostFor() : default, ShowGoToAlbum: Cfg.ShowAlbumColumn, TrackDetails: details);
            if (!selectable) return Track.Menu([t], in options);
            var model = Track.RowMenu(_selection, slotIndex, i => DisplayTrack(i - start), i => OriginalOf(i - start), in options);
            // A touch long-press (or a right-click) is how a row enters selection without a modifier key: the menu's last row arms select mode with
            // this row selected (the "..." menu's Select toggle stays for the mouse). Offered only while select mode is off.
            if (model is { } m && !_multi.Peek())
                model = m with
                {
                    Rows = [.. m.Rows, MenuFlyoutItem.Separator, new MenuFlyoutItem(Loc.Get(Strings.Detail.Select), Icons.MultiSelect, true, () => SetMultiSelect(true))],
                };
            return model;
        }

        internal void ToggleExpanded(string rowKey, Track t)
        {
            string previous = _expanded.Peek();
            bool opening = !string.Equals(previous, rowKey, StringComparison.Ordinal);
            _expanded.Value = opening ? rowKey : "";
            if (opening && t.IsValid) Entities.Ensure(t, TrackFields.All);   // the drawer states EVERY fact
            if (previous.Length > 0) HoldDrawerTail(previous);
            CorrectDrawerExtents(previous, opening ? rowKey : "");
        }

        // A closing drawer is still PRESENTED under its row while its exit reveal runs, so the row keeps square bottom
        // corners until the engine reports the orphan at rest (WhenSettled on the node captured at realize). The engine
        // also fires it when that node dies (a recycle onto another track, a reopen reclaiming the orphan), so the tail
        // never sticks and the registration never leaks.
        void HoldDrawerTail(string key)
        {
            var node = _drawerNode;
            if (Context.Anim is not { } anim || Context.Scene is not { } scene || node.IsNull || !scene.IsLive(node)
                || !string.Equals(_drawerNodeKey, key, StringComparison.Ordinal)) return;
            if (_tails.Add(key)) _tailEpoch.Value = _tailEpoch.Peek() + 1;
            anim.WhenSettled(node, AnimChannel.RevealExtent, () =>
            {
                anim.WhenSettled(node, AnimChannel.RevealExtent, null);
                if (!_tails.Remove(key)) return;
                _tailEpoch.Value = _tailEpoch.Peek() + 1;
            });
        }

        // Item 9 (DrawerExtentRule): a toggle changes its row's real extent, but the measured table only learns a REALIZED
        // row's new height from layout — a closing row scrolled out of the window would keep its open height. Write the
        // closing leg here; the opening row is realized (the user just clicked it) and measures itself.
        void CorrectDrawerExtents(string previous, string next)
        {
            Span<DrawerExtentWrite> writes = stackalloc DrawerExtentWrite[2];
            int n = DrawerExtentRule.For(previous, next, DisplayOfKey, ShapeValue.RowH, 0f, writes);
            for (int i = 0; i < n; i++)
            {
                int index = TrackStart + writes[i].Index;
                if (!_listCtl.IsItemRealized(index)) _listCtl.CorrectMeasuredExtent(index, writes[i].Extent);
            }
        }

        int DisplayOfKey(string key)
        {
            int count = _visible.Peek();
            for (int d = 0; d < count; d++)
                if (MembershipDiff.RowKeyMatches(key, DisplayTrack(d), ItemIdAt(d), d)) return d;
            return -1;
        }

        internal bool DrawerTail(Track t, int display)
        {
            _ = _tailEpoch.Value;   // subscribe: a hold or settle re-runs the row
            if (_tails.Count == 0 || !t.IsValid) return false;
            StringId itemId = ItemIdAt(display);
            foreach (string key in _tails)
                if (MembershipDiff.RowKeyMatches(key, t, itemId, display)) return true;
            return false;
        }

        internal bool IsOpen(Track t, int display)
        {
            string key = _expanded.Value;
            return key.Length > 0 && t.IsValid && MembershipDiff.RowKeyMatches(key, t, ItemIdAt(display), display);
        }

        StringId[] MembershipItemIds()
        {
            var src = _latest.Source;
            var ids = new StringId[src.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = src.ItemId(i);
            return ids;
        }

        /// <summary>Alt+Up / Alt+Down: move the selected contiguous block one row through the SAME seam and PRE-move index
        /// convention the drag commits with. A block the wire cannot name is refused out loud (Notify.Say announces a warning
        /// assertively and toasts it), never a silent no-op (ch 04 §0.15).</summary>
        bool TryBlockMove(int delta)
        {
            // Block move addresses rows by their position among Track membership indices — a mixed Track/Episode view
            // has no such single membership order to move within, so it opts out entirely (episode reordering is
            // out of scope).
            if (_latest.Source.HasEpisodes) return false;
            var sort = _sort.Peek();
            if (!ReorderRules.AllowsBlockMove(Editable, sort.Column == SortColumn.Index && !sort.Descending, _query.Peek(), _filters.Peek()))
                return false;
            if (P.MoveRows is not { } move) return false;
            var src = _latest.Source;
            int start = TrackStart, n = src.Count;
            var refs = new List<RowRef>();
            for (int r = 0; r < _selection.RangeCount; r++)
            {
                var (s, e) = _selection.GetRange(r);
                for (int i = s; i <= e; i++)
                {
                    int orig = i - start;   // natural order: display IS membership order (the gate above)
                    if ((uint)orig < (uint)n) refs.Add(new RowRef(src.ItemId(orig), orig));
                }
            }
            if (refs.Count == 0) return false;
            var moved = refs.ToArray();
            Span<int> indices = moved.Length <= 64 ? stackalloc int[moved.Length] : new int[moved.Length];
            int first = int.MaxValue;
            for (int i = 0; i < moved.Length; i++)
            {
                indices[i] = moved[i].Index;
                if (moved[i].Index < first) first = moved[i].Index;
            }
            int to = ReorderRules.BlockMoveTarget(indices, n, delta);
            if (to < 0) return false;
            if (!Drag.RowsAreKeyed(moved) || !ReorderRules.AnchorRowIsKeyedAt(MembershipItemIds(), moved, to))
            {
                Notify.Say(Loc.Get(Strings.Drag.StillSyncing), InfoBarSeverity.Warning);
                return false;
            }
            bool ok = move(moved, to);
            // Re-point the selection at the landed rows so a held Alt+Down keeps walking the SAME block; a refusal puts it
            // back where it was.
            int landed = start + first + (ok ? delta : 0);
            _selection.ClearSelection();
            _selection.SelectRange(landed, landed + moved.Length - 1);
            _selection.AnchorIndex = landed;
            return true;
        }

        // ══ 7. DRAG INSERTION ═══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The declarative destination, created ONCE and shared by all three arms; every delegate reads LIVE state
        /// because the record freezes at mount. The engine owns every coordinate.</summary>
        InsertionOptions Insertion() => _insertion ??= new InsertionOptions
        {
            AcceptKinds = [Drag.Resource],
            CanAccept = payload => Verdict(payload) == DropRefusal.None,
            // An album/show table sits the gesture out silently; a read-only PLAYLIST says why (ch 04 §9.9).
            Transparent = _ => Cfg.Kind is DetailKind.Album or DetailKind.Show && !Editable,
            IsSameList = IsSameList,
            // A same-list reorder never dims the app; a cross-list deposit keeps the scrim.
            SpotlightWhen = session => !IsSameList(session.Payload),
            SourceIndices = SourceDisplayRows,
            DraggedCount = payload => Drag.Unwrap(payload) is { Tracks.Length: > 0 } d ? d.Tracks!.Length : 1,
            // The insertable sub-range: TRACK rows only — never the hero/chrome prefix or the recs section.
            Range = () => (TrackStart, _visible.Peek()),
            OnDeposit = DepositAt,
            Caption = Caption,
            RefusalCaption = RefusalCaptionFor,
            GapPreview = (payload, _) =>
            {
                if (Drag.Unwrap(payload) is not { } d) return new BoxEl { Height = 0f };
                var shape = _shape!.Peek();
                return Drag.InsertionCards(d, shape.RowH, shape.Art, RowMetrics.RowInset, RowMetrics.PadXFor(shape.Set.Tier),
                    showArtwork: !_snapshot!.Peek().ArtHidden);
            },
            PreviewCap = Drag.PreviewCap,
        };

        bool IsSameList(object? payload)
            => Drag.Unwrap(payload) is { SourceRows.Length: > 0 } d
               && string.Equals(d.SourcePlaylistUri, _contextText, StringComparison.Ordinal);

        /// <summary>ONE verdict behind the accept test AND the caption (Drag.Evaluate's fixed order).</summary>
        DropRefusal Verdict(object? payload)
        {
            var d = Drag.Unwrap(payload);
            var sort = _sort.Peek();
            var rows = d?.SourceRows;
            return Drag.Evaluate(
                editable: Editable,
                loading: _latest.Source.State == EdgeState.Unknown,
                payloadHasTracks: d is { CanCopyTracks: true },
                sameList: IsSameList(payload),
                naturalOrder: sort.Column == SortColumn.Index && !sort.Descending,
                filtered: !ReorderRules.AllowsSameListMove(true, _query.Peek(), _filters.Peek()),
                rowsKeyed: rows is not { Length: > 0 } || Drag.RowsAreKeyed(rows));
        }

        string? RefusalCaptionFor(object? payload)
        {
            var verdict = Verdict(payload);
            // An artist has no single obvious track set: refused with its own sentence rather than a guess.
            if (verdict == DropRefusal.NoTracks && Drag.Unwrap(payload) is { Kind: DragKind.Artist })
                return Loc.Get(Strings.Drag.CantAddArtist);
            return Drag.RefusalCaption(verdict);
        }

        string? Caption(object? payload, int _)
        {
            if (Drag.Unwrap(payload) is not { } d) return null;
            int rows = d.SourceRows?.Length ?? 0;
            int tracks = d.Tracks?.Length ?? 0;
            return ReorderRules.VerbFor(IsSameList(payload), rows, tracks) switch
            {
                DropVerb.MoveRows => Drag.MoveTracks(rows),
                DropVerb.AddTracks => Drag.AddTracks(tracks),
                DropVerb.AddContainer => Drag.AddTo(ContextTitle()),
                _ => null,
            };
        }

        string ContextTitle()
        {
            if (_latest.Vertical is { } spec && spec.Identity.Title.Length > 0) return spec.Identity.Title;
            var playlist = _latest.Source.HostPlaylist;
            return playlist.IsValid ? Entities.Strings.Resolve(playlist.TitleId) : "";
        }

        IReadOnlyList<int>? SourceDisplayRows(object? payload)
        {
            if (Drag.Unwrap(payload) is not { SourceRows: { Length: > 0 } rows }) return null;
            var view = ViewNow();
            var display = new List<int>(rows.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                int at = ReorderRules.DisplayRowOf(rows[i].Index, view);
                if (at >= 0) display.Add(at);
            }
            return display;
        }

        /// <summary>The commit. The slot is the RAW slot the user aimed at — the move convention already discounts rows
        /// removed above it, so correcting here would move the block twice.</summary>
        Task<bool> DepositAt(object? payload, int displaySlot)
        {
            if (Drag.Unwrap(payload) is not { } d) return Task.FromResult(false);
            var src = _latest.Source;
            int at = ReorderRules.OriginalInsertionIndex(ViewNow(), src.Count, displaySlot);
            if (IsSameList(payload) && d.SourceRows is { Length: > 0 } rows)
            {
                // The ANCHOR half of the keyed-reorder gate: the first moment a slot exists.
                if (!ReorderRules.AnchorRowIsKeyedAt(MembershipItemIds(), rows, at))
                {
                    Notify.Say(Loc.Get(Strings.Drag.StillSyncing), InfoBarSeverity.Warning);
                    return Task.FromResult(false);
                }
                return Task.FromResult(P.MoveRows is { } move && move(rows, at));
            }
            return Task.FromResult(P.Deposit is { } deposit && deposit(d, at >= src.Count ? null : at));
        }

        /// <summary>The row's drag payload: the whole selection when the dragged row is inside it, else that row. A page that
        /// is not an editable playlist drags as a COPY (no source rows).</summary>
        object? DragPayloadFor(int slotIndex, int start)
        {
            var dragged = DisplayTrack(slotIndex - start);
            if (!dragged.IsValid) return null;
            var src = _latest.Source;
            var tracks = new List<Track>();
            var rows = new List<RowRef>();
            void Add(int display)
            {
                int orig = OriginalOf(display);
                // A mixed-selection drag carries its TRACK members only — an episode row within it is silently
                // excluded rather than dragged as an invalid handle.
                if (orig < 0 || src.KindAt(orig) != EntityKind.Track) return;
                tracks.Add(src.At(orig));
                rows.Add(new RowRef(src.ItemId(orig), orig));
            }
            if (_selection.IsSelected(slotIndex))
            {
                for (int r = 0; r < _selection.RangeCount; r++)
                {
                    var (s, e) = _selection.GetRange(r);
                    for (int i = s; i <= e; i++) Add(i - start);
                }
            }
            else Add(slotIndex - start);
            if (tracks.Count == 0) return null;
            string? source = Editable && _contextText.Length > 0 ? _contextText : null;
            string uri = dragged.Uri.Text;
            string name = tracks.Count == 1 ? dragged.Title : Strings.Sidebar.SongCount(tracks.Count);
            var kind = tracks.Count == 1 ? Drag.PlayableKind(uri) : DragKind.Track;
            return new DragPayload(kind, uri, uri, name, new EntityRef(EntityKind.Track, dragged.Slot), tracks.ToArray(),
                source, source is null ? null : rows.ToArray());
        }

        /// <summary>An OS file drag over a row attaches a local video override. The target only ACCEPTS a drop carrying an
        /// .mp4, so any other file falls through to the shell's play-this-file target instead of being swallowed (ch 04 §6).</summary>
        DropTargetSpec VideoDrop(IReadSignal<int> index, int start) => new(
            [DropKinds.Files],
            OnEnter: _ => _videoDropRow.Value = index.Peek(),
            OnLeave: _ => { if (_videoDropRow.Peek() == index.Peek()) _videoDropRow.Value = -1; },
            OnDrop: session =>
            {
                _videoDropRow.Value = -1;
                if (session.Payload is not FileDropData files || Video.OverrideUx.FirstMp4(files.Paths) is not { } mp4) return;
                var t = DisplayTrack(index.Peek() - start);
                if (!t.IsValid) return;
                var result = Video.Overrides.Attach(t.Uri.Text, mp4, mp4, Store.ToUnix(Entities.Now));
                Notify.Say(Loc.Get(result is null ? Strings.VideoOverride.RejectedNotFound : Strings.VideoOverride.Attached),
                    result is null ? InfoBarSeverity.Error : InfoBarSeverity.Success);
            })
        {
            CanAccept = static session => session.Payload is FileDropData f && Video.OverrideUx.FirstMp4(f.Paths) is not null,
        };

        // ══ 8. THE ROW SKIN, THE DRAWER, THE PRESENTATION ═══════════════════════════════════════════════════════════

        /// <summary>The bound, shape-stable row container for a TRACK row: the CORE half. This wires the behaviour (the drag
        /// source and the .mp4 drop target, the tap / double-tap / Enter / Space / Alt+arrow gestures, the focus and hover edges
        /// the equalizer's pause reads, the check lane) and hands it to the structure's skin template
        /// (<see cref="Wavee.Theming.Contracts.TrackListStyle"/>), which draws it: zebra is DISPLAY-index parity; selection never
        /// changes the fill except in Classic (RowHover); the 3×16 pill is the highlight cue and hands over to the check lane;
        /// an .mp4 drag over the row rides the same fill closure instead of an overlay node (ch 04 §0.6). Built by the slot on
        /// its rare re-renders (shape / flow / open), so a recycle only moves the closures' index signal and allocates nothing.
        /// The context menu is attached here, once, around whatever the template returned.
        /// <para>An episode row is never wrapped here — <see cref="TableSlot"/> hands it straight to
        /// <see cref="Episode.ReaderRow"/>, which is fully self-contained (its own click, hover, check lane, "…").</para></summary>
        internal BoxEl Skin(RowScope scope, Element content, in Shape shape, bool rowFlow, bool squared, int start,
                            Signal<bool> hovered, bool entrance)
        {
            var index = scope.Index;
            var isSel = scope.IsSelected;
            var interact = scope.OnInteraction;
            bool classic = shape.Set.Classic;
            bool plain = !classic && VerticalArm && !ColumnsForm && !rowFlow;   // the hero form's STACKED flow: plain full-bleed rows
            var drop = Video.Overrides.Present ? VideoDrop(index, start) : null;
            Func<bool> cue = drop is null ? _false : () => _videoDropRow.Value == index.Value;

            var templates = RowTemplatesFor(classic);
            if (templates is null) return new BoxEl { MinHeight = shape.RowH, Children = [content] };

            var wiring = new global::Wavee.Theming.Contracts.TrackRowSkin
            {
                Content = content,
                CheckLane = SelectorVisualsBound.BoundCheckLane(_checksRead, isSel, interact, leftMargin: 4f),
                RowHeight = shape.RowH,
                Plain = plain,
                Quiet = shape.Set.Quiet,
                Squared = squared,
                Entrance = entrance,
                IsSelected = isSel,
                Cue = cue,
                Odd = () => Math.Max(0, index.Value - start) % 2 != 0,
                ChecksVisible = _checksRead,
                Accent = _accent,
                DropTarget = drop,
                Draggable = Drag.Source(() => DragPayloadFor(index.Peek(), start)),
                OnPointerReleased = args =>
                {
                    if (RowClickPolicy.TriggerOf(args.ClickCount, args.Mods) == ItemContainerTrigger.DoubleTap) interact(ItemContainerTrigger.DoubleTap, args.Mods);
                    else interact(ItemContainerTrigger.Tap, SelectorVisualsBound.MultiSelectMods(_checksVisible!.Peek(), args.Mods));
                },
                OnKeyDown = args =>
                {
                    if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                    else if (args.KeyCode == Keys.Space && !args.IsRepeat)
                    {
                        interact(ItemContainerTrigger.SpaceKey, SelectorVisualsBound.MultiSelectMods(_checksVisible!.Peek(), args.Mods));
                        args.Handled = true;
                    }
                    else if (args.Alt && (args.KeyCode == Keys.Up || args.KeyCode == Keys.Down)
                             && TryBlockMove(args.KeyCode == Keys.Up ? -1 : 1))
                        args.Handled = true;
                },
                Dimmed = () => Track.DimmedTrack(DisplayTrack(index.Value - start), Track.NowUnix),
                OnFocusChanged = scope.OnFocusChanged,
                // Row hover → the equalizer's pause (it sits under a hover-opacity reveal and must stop ticking).
                OnHoverMove = _ => { if (!hovered.Peek()) hovered.Value = true; },
                OnPointerExit = () => { if (hovered.Peek()) hovered.Value = false; },
            };
            BoxEl skin = templates.Skin(wiring);
            var overlay = _overlay;
            if (Controls.IsNullOverlay(overlay)) return skin;
            bool expandLane = shape.Set.Expand;
            // Attached ONCE per slot render; the factory runs at OPEN and reads the slot's live index + selection.
            return skin.WithContextMenu(overlay, () => RowMenuFor(index.Peek(), start, expandLane));
        }

        // ── the structure's row templates ──────────────────────────────────────────────────────────────────────────

        /// <summary>The active structure's track-list style, resolved once per host render (the registry is ambient; a theme
        /// switch remounts the root, so a mounted host never sees it change). Null when no registry provides one.</summary>
        global::Wavee.Theming.Contracts.TrackListStyle? _listStyle;
        bool _listStyleReported;

        /// <summary>The structure's hero loading band (<c>StyleKeys.DetailHero</c>), resolved with <see cref="_listStyle"/>. Null when no registry
        /// provides one: the vertical shimmer then reserves nothing for the hero band.</summary>
        global::Wavee.Theming.Contracts.DetailHeroStyle? _heroStyle;
        /// <summary>The eager row's style (T8c), for the counted shimmer that draws the real row's grid over an empty track.</summary>
        global::Wavee.Theming.Contracts.EagerRowStyle? _eagerStyle;

        /// <summary>The drawer's style (T8c): the frame the open row mounts under itself.</summary>
        global::Wavee.Theming.Contracts.TrackDrawerStyle? _drawerStyle;

        global::FluentGpu.Theming.ViewRegistry? _registry;
        global::Wavee.Theming.Contracts.EpisodeRowStyle? _episodeStyle;

        /// <summary>The episode row's style (T8c), resolved when the first episode-kind row renders (a table of tracks never asks).</summary>
        internal global::Wavee.Theming.Contracts.EpisodeRowStyle? EpisodeStyle
            => _episodeStyle ??= global::Wavee.Theming.Contracts.ListStyleLookup.Of(_registry, global::Wavee.Theming.Contracts.StyleKeys.EpisodeRow);

        /// <summary>The Modern or the Classic row templates (the row style is the structure's option, read as <see cref="ColumnSet.Classic"/>; the
        /// structure supplies both). Read by the slot components on their rare renders, never per bind. Null when the registry
        /// has no <c>StyleKeys.TrackTable</c>: the row then renders its bare container, and the first miss is logged.</summary>
        internal global::Wavee.Theming.Contracts.TrackRowTemplates? RowTemplatesFor(bool classic)
        {
            var style = _listStyle;
            if (style is not null) return classic ? style.Classic : style.Modern;
            if (!_listStyleReported)
            {
                _listStyleReported = true;
                Log.Warn("ui", "track table: no TrackListStyle is registered (StyleKeys.TrackTable); the rows render nothing");
            }
            return null;
        }

        /// <summary>The blank placeholder row of the structure's chrome (T8b): the SAME tracks, gap, height and padding as the live row, so
        /// the crossing is a cross-fade and never a reflow. A host with no registry has no style at all (its rows render their bare container, and the first miss is logged), so its placeholder is
        /// the same bare box.</summary>
        internal Element PlaceholderRow(in ColumnSet set, TrackSize[] tracks, float rowH, float padX)
            => _listStyle?.Chrome.Placeholder is { } placeholder ? placeholder(in set, tracks, rowH, padX) : new BoxEl();

        /// <summary>The drawer's mount, keying and reveal — its BODY is the profile's. One drawer at a time, keyed by ROW
        /// identity (item id, else uri#@display), continuing the row's zebra parity with square top corners. ONE spec on the
        /// clip box (Design.Reveal.Drawer): the presented height springs, the rows below ride it; the presence box is plain
        /// padding.</summary>
        internal Element DrawerBox(Track t, int display, in Shape shape, IReadSignal<int> index, int start)
        {
            string rowKey = MembershipDiff.RowKey(t, ItemIdAt(display), display);
            bool classic = shape.Set.Classic;
            Element body = P.Drawer?.Invoke(t, display) ?? new BoxEl();
            // The structure's frame (T8c): the clip box with the drawer's open / close motion, the zebra continuation and the indent. The
            // core keeps the reveal wiring (the node the keyboard and the scroll anchor read) and the decisions it draws from.
            if (_drawerStyle is null) return new BoxEl();
            return _drawerStyle.Frame(new global::Wavee.Theming.Contracts.TrackDrawerFrameArgs
            {
                RowKey = rowKey, Body = body, Indent = RowMetrics.DrawerIndent(shape.Set, shape.Art), Classic = classic,
                Odd = () => Math.Max(0, index.Value - start) % 2 != 0,
                OnRealized = h => { _drawerNode = h; _drawerNodeKey = rowKey; },
            });
        }

        internal RowPresentation Present(Track t, int display)
        {
            var snap = _snapshot!.Value;
            _ = Entities.Current.Tracks.Changed.Value;
            _ = Entities.Current.Albums.Changed.Value;
            _ = Entities.Current.Artists.Changed.Value;
            // `t.ArtistSlots` below reads `Edges.TrackArtists` directly (a CSR span, not a `Known` bit), and the row's
            // own `Version` does not bump when the edge lands in a LATER drain than the row itself — which a disk-cached
            // row now does by construction (`TrackShape.Load` no longer claims `Artists` known, so it re-asks and the
            // edge arrives after the row). Without this the credit line silently stays blank until an unrelated
            // invalidation happens to repaint the table (bug C; see `Album.UI.cs`, `User.Page.Liked.cs`, `Sidebar.UI.cs`
            // for the same idiom).
            _ = Entities.Current.Edges.TrackArtists.Changed.Value;
            var src = _latest.Source;
            int orig = OriginalOf(display);
            bool isTop = snap.Config.ShowPlays && t.IsValid && src.TopTrack.Slot == t.Slot;
            var state = Track.StateOf(t, isTop);
            // The credit line's discriminator is what the row PRINTS per artist (slot · interned name id · uri known), not
            // the artist's whole Version: that summed every field landing on every credited artist (a follower count, a
            // header image on the artist page), so any commit touching any of them re-rendered thirteen rows a frame.
            ulong artists = Track.CreditStamp.Seed;
            foreach (int a in t.ArtistSlots)
            {
                var artist = new Artist(a);
                artists = Track.CreditStamp.Add(artists, a, artist.NameId.Value, artist.Uri.IsValid);
            }
            // The adder's own data (name, avatar) is NOT an input here: the Added-by lane subscribes to it itself
            // (Track.AddedByLabel(BoundRow)), so a user landing re-runs that lane, not every cell of every row.
            var by = orig >= 0 ? src.AddedBy(orig) : default;
            return new RowPresentation(t, display, state,
                t.IsValid ? t.Version : 0u, t.Album.IsValid ? t.Album.Version : 0u, artists,
                orig >= 0 ? src.AddedAt(orig) : 0, by,
                orig >= 0 ? src.ChartStatus(orig) : (byte)0,
                snap.Marquee, snap.Config.ShowTrackArtist, snap.Config.ShowAlbumColumn, IsOpen(t, display));
        }

        // ══ 9. CHOREOGRAPHY ══════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Track the displayed order and narrate a LIVE membership change. The FIRST complete landing of a context is
        /// a load and never choreographs (`_settled`); paging (Partial) never narrates either — a 300-row page would read as
        /// a re-cut and remount at the top.</summary>
        void TrackOrder(in Snapshot snap, float rowH)
        {
            if (_orderSeeded && _orderSeen.Equals(snap)) return;
            bool membershipChanged = _orderSeeded && snap.Version != _orderSeen.Version;
            var previousState = _curState;
            // swap the buffers: the order the user was looking at becomes the baseline (no allocation per change)
            (_prevTracks, _curTracks) = (_curTracks, _prevTracks);
            (_prevIds, _curIds) = (_curIds, _prevIds);
            _prevLen = _curLen;
            var view = ViewOf(in snap);
            if (_curTracks.Length < view.Length) { _curTracks = new Track[view.Length]; _curIds = new StringId[view.Length]; }
            var src = _latest.Source;
            for (int i = 0; i < view.Length; i++)
            {
                int o = view[i];
                _curTracks[i] = o >= 0 ? src.At(o) : default;
                _curIds[i] = o >= 0 ? src.ItemId(o) : default;
            }
            _curLen = view.Length;
            _curState = snap.State;
            _orderSeen = snap;
            _orderSeeded = true;
            if (!membershipChanged || previousState != EdgeState.Complete || snap.State != EdgeState.Complete) return;
            // The FLIP/fade reorder animation keys rows by Track identity (`MembershipDiff.Keys`) — an episode-
            // capable source (mixed Track/Episode) skips the choreography rather than mis-keying it; its rows still
            // reflow (Track.Table's plain layout pass), just without the fancy displacement narration.
            if (src.HasEpisodes) { _settled = true; return; }
            if (_prevLen > 0 && _settled)
                Choreograph(new ReadOnlySpan<Track>(_prevTracks, 0, _prevLen), new ReadOnlySpan<StringId>(_prevIds, 0, _prevLen),
                            new ReadOnlySpan<Track>(_curTracks, 0, _curLen), new ReadOnlySpan<StringId>(_curIds, 0, _curLen), rowH);
            _settled = true;
            _dealtThisFrame = true;   // a membership narration outranks a breakpoint re-deal
        }

        void Choreograph(ReadOnlySpan<Track> oldTracks, ReadOnlySpan<StringId> oldIds,
                         ReadOnlySpan<Track> newTracks, ReadOnlySpan<StringId> newIds, float rowH)
        {
            var oldKeys = MembershipDiff.Keys(oldTracks, oldIds);
            var newKeys = MembershipDiff.Keys(newTracks, newIds);
            var delta = MembershipDiff.Diff(oldKeys, newKeys);
            if (delta.IsEmpty) return;
            if (delta.IsReset)
            {
                // A curated re-cut: ONE crossfade — the keyed remount replays the slots' mount entrance at a fresh scroll.
                _flip.Clear(); _fade.Clear();
                _resetEpoch++;
                return;
            }
            float offset = _listCtl.ScrollOffset;
            int firstVisible = rowH > 0f ? Math.Max(0, (int)(offset / rowH)) : 0;

            // (1) Anchor: the first visible SURVIVOR keeps its screen Y; at the start edge the engine declines.
            _keyIndex.Clear();
            for (int i = 0; i < newKeys.Length; i++) _keyIndex[newKeys[i]] = i;
            int shift = 0;
            for (int i = Math.Clamp(firstVisible, 0, Math.Max(0, oldKeys.Length - 1)); i < oldKeys.Length; i++)
                if (_keyIndex.TryGetValue(oldKeys[i], out int ni)) { shift = ni - i; break; }
            if (shift != 0 && !_listCtl.PreserveAnchor(shift * rowH)) shift = 0;

            // (2) FLIP residuals for every survivor, and a rise + fade for adds, 20 ms/row capped at 8.
            _flip.Clear(); _fade.Clear();
            _keyIndex.Clear();
            for (int i = 0; i < oldKeys.Length; i++) _keyIndex[oldKeys[i]] = i;
            for (int n = 0; n < newKeys.Length; n++)
                if (_keyIndex.TryGetValue(newKeys[n], out int o))
                {
                    float residual = (o - n + shift) * rowH;
                    if (MathF.Abs(residual) > 0.5f) _flip[n] = (0f, residual);
                }
            int ord = 0;
            foreach (var add in delta.Adds)
            {
                if (add.NewIndex is not { } n) continue;
                _flip[n] = (0f, -6f);
                _fade[n] = (0f, Math.Min(ord++, 8) * 20f);
            }
            _dealEpoch++;
        }

        /// <summary>A breakpoint cross narrated as ONE deal: realized rows rise 6 DIP and fade in top-down behind a capped
        /// stagger. Never the first tier a list measures, never within 200 ms of the last cross (the rapid-reversal gate —
        /// the one sanctioned TickCount64 read, a gesture-rate debounce), never under reduced motion.</summary>
        /// <summary>The rows across a row-height change (audit B9: a crossing to or from WinUI's Compact two-line rows): every realized row keeps its node and
        /// glides from where it stood to where the new height puts it (a FLIP seed per row, the same channel a reorder uses), while its own height eases
        /// (<see cref="RowResize"/>). The hero list re-anchors its scroll on its first visible row, so the residual counts from there; an album's rows sit in
        /// the outer scroller and count from the first row. Never the first height a list lays out, never under reduced motion.</summary>
        void ReRow(float rowH, bool trailing)
        {
            float old = _rowHSeen;
            _rowHSeen = rowH;
            if (old <= 0f || rowH <= 0f || MathF.Abs(old - rowH) < 0.5f || Design.Reduced) return;
            int visible = _visible.Peek();
            if (visible <= 0) return;
            float d = old - rowH;
            int anchor = trailing ? 0 : Math.Clamp((int)((_listCtl.ScrollOffset - _heroH.Peek()) / old), 0, visible - 1);
            int first = trailing ? 0 : Math.Max(0, anchor - ReDealRows);
            int last = Math.Min(visible, (trailing ? 0 : anchor) + 2 * ReDealRows);
            _flip.Clear(); _fade.Clear();
            for (int i = first; i < last; i++)
            {
                float residual = (i - anchor) * d;
                if (MathF.Abs(residual) > 0.5f) _flip[i] = (0f, residual);
            }
            _dealEpoch++;
        }

        void ReDeal(int tier, float rowH)
        {
            if (_lastDealtTier == tier) return;
            int previous = _lastDealtTier;
            _lastDealtTier = tier;
            long now = Environment.TickCount64;
            bool reversal = now - _lastDealtAtMs < ReDealReversalMs;
            bool rearranged = VerticalArm && now - _formChangedAtMs < ReDealFormGraceMs;
            _lastDealtAtMs = now;
            if (_dealtThisFrame) return;
            if (previous < 0 || reversal || rearranged || Design.Reduced || rowH <= 0f)
            {
                _flip.Clear(); _fade.Clear();   // never let a stale deal replay on an unrelated displacement bump
                return;
            }
            bool narrowing = tier > previous;
            int cap = narrowing ? 6 : 4;
            float step = narrowing ? 24f : 16f;
            _flip.Clear(); _fade.Clear();
            int visible = _visible.Peek();
            int first = Math.Max(0, (int)(_listCtl.ScrollOffset / rowH));
            int last = Math.Min(visible, first + ReDealRows);
            for (int i = first; i < last; i++)
            {
                _flip[i] = (0f, 6f);
                _fade[i] = (0f, Math.Min(i - first, cap) * step);
            }
            _dealEpoch++;
        }
    }

    // ══ 10. THE ROW COMPONENTS ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The expandable slot: the skin, and — when THIS row is the open one — the drawer beneath it. Re-renders on its
    /// own subscriptions only (shape, flow, open), never on a recycle; one root shape in every state so the bound skin stays
    /// wired while the keyed drawer child enters and exits.
    /// <para>THE ROOT SWITCH: the one place a display row's kind decides what it renders — an episode row goes straight to
    /// <see cref="Episode.ReaderRow"/> (self-contained; no <see cref="TableHost.Skin"/>, no drawer), a track row keeps
    /// the skin/check-lane/menu/drawer path unchanged. It self-binds whichever item type it needs (rather than receiving
    /// a bound item at construction) so all three arms (<see cref="TableHost.FlatList"/>/<see cref="TableRecItem"/>/
    /// <see cref="TableVerticalItem"/>) share one dispatch instead of duplicating the kind switch.</para></summary>
    sealed class TableSlot(TableHost host, RowScope scope, int start, bool narrate) : Component
    {
        readonly TableHost _host = host;
        readonly RowScope _scope = scope;
        readonly int _start = start;
        readonly bool _narrate = narrate;
        IReadSignal<Track>? _trackItem;
        IReadSignal<Episode.RowItem>? _episodeItem;

        public override Element Render()
        {
            // A RECYCLE (a write to `_scope.Index`) must not re-render the slot: the skin's closures, the check lane and the
            // row content all read the index through bound props / memos, so rebuilding them per recycle only allocated —
            // the whole Skin tree, ~17 KB per recycled row, every row of a fast thumb drag (EventPipe allocation ticks,
            // 2026-09-25: TableSlot.Render 35 MB of a 4-leg drag, Skin alone 17 MB). The slot subscribes to the row's KIND
            // (track / episode), which is all its shape depends on; the display index is peeked where it is needed.
            var kind = UseComputed(() => _host.RowKindAt(_scope.Index.Value - _start)).Value;

            // An episode row is never wrapped by Skin — it IS the show reader's own row (Episode.ReaderRow), self-
            // contained (click, hover, check lane, "…"), built through Episode.RowContext from this host's own
            // tone/play/overlay/menu/selection seams (Track.Table.EpisodeRow.cs). Looks and behaves identically to a
            // row in the show reader, whether it's hosted in a mixed playlist or Your Episodes.
            if (kind == RowTemplate.Episode)
            {
                _episodeItem ??= _host.BindEpisodeItemFor(_scope, _start);
                var host = _host;
                var scope = _scope;
                var episodeItem = _episodeItem!;
                int start = _start;
                Element erow = Embed.Comp(() => new EpisodeRowContent(host, scope, episodeItem, start)) with { Key = "row" };
                return new BoxEl { Direction = 1, MinWidth = 0f, Children = [erow] };
            }

            var hovered = UseSignal(false);
            var shape = _host.ShapeValue;
            bool flow = _host.RowFlowValue;

            _trackItem ??= _host.BindItemFor(_scope, _start);
            // The DISPLAY index is resolved INSIDE the computation, never captured. `UseComputed` keeps the delegate from
            // this slot's FIRST mount and a slot is recycled onto other positions by a write to `_scope.Index`, so a
            // captured `display` freezes at the mount position and answers "am I the open row" for it forever (the
            // drawer opening under the wrong row after a scroll). Same rule the sibling sites below already follow.
            var open = UseComputed(() => _host.IsOpen(_trackItem!.Value, _scope.Index.Value - _start));
            bool isOpen = open.Value;
            var tail = UseComputed(() => _host.DrawerTail(_trackItem!.Value, _scope.Index.Value - _start));
            var thost = _host;
            var tscope = _scope;
            var titem = _trackItem!;
            int tstart = _start;
            Element content = Embed.Comp(() => new TableRowContent(thost, tscope, titem, tstart, hovered));
            // Its height eases across a row-height change (a skin's own entrance, where it has one, wins: a fresh row has no height to ease from).
            Element row = _host.Skin(_scope, content, in shape, flow, isOpen || tail.Value, _start, hovered, _narrate) with { Key = "row", Layout = _host.RowResize };
            // A row that starts a date group (tuned Classic D16) carries the group's head above its plate, inside its slot: the list stays
            // one row per song, so selection, keys, drag and the drawer never see the heads. The head is BOUND (its presence and words read
            // the slot's memo inside props), so scrolling recycles the slot without a render.
            Element? head = null;
            if (_host.GroupsCapable)
            {
                var group = UseComputed(() => _host.GroupHeadAt(_scope.Index.Value - _start));
                head = _host.GroupHeader(() => group.Value, in shape);
            }
            if (!isOpen) return new BoxEl { Direction = 1, MinWidth = 0f, Children = head is null ? [row] : [head, row] };
            var t = _trackItem!.Peek();
            int display = _scope.Index.Peek() - _start;
            Element drawer = _host.DrawerBox(t, display, in shape, _scope.Index, _start);
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children = head is null ? [row, drawer] : [head, row, drawer],
            };
        }
    }

    /// <summary>The live grid of one row, built ONCE per slot per shape (the structure's grid template): every per-item value is a
    /// bound Prop over this slot's equality-gated presentation memo, so a recycle rebinds and allocates no element. Re-renders
    /// only on the shape and the reveal edge. Before the ramp reaches it: a blank placeholder row of the identical extent (a GridEl —
    /// the TYPE change to the BoxEl-wrapped real row is what forces the crossing remount and its 280 ms fade; a Key change
    /// alone cannot). Only a slot whose FIRST render was a placeholder fades.</summary>
    sealed class TableRowContent(TableHost host, RowScope scope, IReadSignal<Track> item, int start, IReadSignal<bool> hovered)
        : Component
    {
        readonly TableHost _host = host;
        readonly RowScope _scope = scope;
        readonly IReadSignal<Track> _item = item;
        readonly int _start = start;
        readonly IReadSignal<bool> _hovered = hovered;
        int _likeSlot;
        bool _likeSaved;
        byte _latch;   // 0 = unrendered · 1 = first render was a placeholder (the crossing fades) · 2 = mounted real
        Track.BoundRow? _row;

        static readonly LayoutTransition s_rampReveal = new(TransitionChannels.Opacity,
            TransitionDynamics.Tween(280f, Easing.FluentDecelerate), Enter: new EnterExit(Opacity: 0f, Active: true));

        // The three handlers resolve the CURRENT item / index at click time (`Peek`): the host, the scope's index signal,
        // the item signal and `start` are all mount-stable for the life of the slot.
        void PlayCurrent() => _host.PlayRow(_scope.Index.Peek() - _start);

        void LikeCurrent() => TableHost.ToggleLike(_item.Peek());

        void ToggleExpandCurrent()
        {
            var current = _item.Peek();
            int at = _scope.Index.Peek() - _start;
            _host.ToggleExpanded(MembershipDiff.RowKey(current, _host.ItemIdAt(at), at), current);
        }

        public override Element Render()
        {
            var revealed = UseComputed(() => _host.RowRevealed(_scope.Index.Value - _start));
            var presentation = UseComputed(() => _host.Present(_item.Value, _scope.Index.Value - _start));
            // The like EDGE is per-slot state over successive presentations: recomputed once per presentation change, never
            // on a read, so a recycle onto an already-saved row reports no edge (ch 01 §9, parity 22).
            var likePop = UseComputed(() =>
            {
                var p = presentation.Value;
                return Track.LikeEdge(ref _likeSlot, ref _likeSaved, p.Track, p.State.Saved);
            });
            var shape = _host.ShapeValue;
            var set = shape.Set;
            bool real = revealed.Value;
            if (_latch == 0) _latch = real ? (byte)2 : (byte)1;
            if (!real) return _host.PlaceholderRow(in set, shape.Tracks, shape.RowH, RowMetrics.PadXFor(set.Tier)) with { Key = "row:shim" };

            _row ??= new Track.BoundRow(presentation, likePop, _hovered, _scope.IsFocused, Playback.IsPlaying, _host.AccentRead, PlayCurrent, LikeCurrent, ToggleExpandCurrent);
            var row = _row;
            // The structure draws the grid (Modern or Classic template); built ONCE per slot per shape, like it always was.
            var templates = _host.RowTemplatesFor(set.Classic);
            Element grid = templates is null ? new BoxEl() : templates.Grid(row, in set, shape.Tracks, shape.RowH, shape.Art);
            // The tracklist EDGE answers with row ids first and the rows' own fields land in a later fetch, so a slot can be
            // valid while its row has no data. The table's reveal gate holds the whole region until the first page knows its
            // face (TableRules.RowsPending), so this branch is for the rows PAST that band — a later batch's rows, a row
            // scrolled to before its page arrived: they keep the placeholder of the same extent until their own data lands
            // (a real mount/unmount, rare). The rule is TableRules.RowHasData over TrackFields.Face, NOT the whole Identity
            // group: a disk-restored row withholds Artists until the credits edge re-lands, and a warm open must not shimmer
            // for a credit line that fills in place.
            // THE ROW'S OWN GATE, where the structure reveals the hero arm's rows together (ListPolicy.RowsRevealTogether; elsewhere the shipped per-row
            // placeholder above stays). The owner's recording: rows 1 and 4 alone, a hole where 2-3 belong, then single rows popping in. A row with no data, or
            // any row of the hero arm while the table's reveal gate is still closed (TableRules.RowsPending: the hero arm mounts its rows the moment the
            // membership has a count, so the region-wide gate never covered them), shimmers the row's own shape in place and cross-dissolves to the real
            // grid. The gate opens for every row on the same frame, so the list swaps in as one block; covers then fade in inside rows already laid out.
            var host = _host;
            var geometry = shape;
            Element body = !TableHost.RowsTogether()
                ? Flow.Show(() => { var t = row.Presentation.Value.Track; return TableRules.RowHasData(t.IsValid, t.Knows(TrackFields.Face)); }, grid,
                    _host.PlaceholderRow(in set, shape.Tracks, shape.RowH, RowMetrics.PadXFor(set.Tier)))
                : new SkelRegionEl(
                Pending: () =>
                {
                    var t = row.Presentation.Value.Track;
                    _ = global::Wavee.Fetch.Settled.Value;   // an answer that names no field still ends the wait for the credits
                    bool paints = TableRules.RowPaints(t.Knows(TrackFields.Face), t.Knows(TrackFields.Artists), t.InFlight, withCredits: true);
                    return !TableRules.RowHasData(t.IsValid, paints) || host.RowGateClosed();
                },
                Failed: static () => false,
                Content: () => grid,
                ShimmerSource: () => host.ShimmerRow(in geometry),
                OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
            return new BoxEl
            {
                Key = "row:real", Direction = 0, Grow = 1f, Animate = _latch == 1 ? s_rampReveal : null, Children = [body],
            };
        }
    }

    /// <summary>One slot of the hero-system viewport: hero · chrome (persistent prefix) · rows · the
    /// empty/loading placeholder. Constructor values are shape-only.</summary>
    sealed class TableVerticalItem(TableHost host, RowScope scope, bool narrate) : Component
    {
        readonly TableHost _host = host;
        readonly RowScope _scope = scope;
        readonly bool _narrate = narrate;

        /// <summary>What this slot renders, as one value: its role and, for the empty/blank role, whether it is the first
        /// suffix slot (the empty-state item). A recycle between two track rows changes neither, so it re-renders nothing.</summary>
        readonly record struct Slot(VerticalItemRole Role, bool First);

        public override Element Render()
        {
            int prefix = Detail.VerticalLayout.PrefixCount;
            // Subscribes to the ROLE, never to the raw index (see TableSlot.Render: a per-recycle re-render here rebuilt this
            // wrapper and its component factory for every recycled row of a drag).
            var slot = UseComputed(() =>
            {
                int i = _scope.Index.Value;
                return new Slot(Detail.VerticalLayout.ItemRole(i, _host.VisibleCountValue, _host.VerticalRecsValue), i == prefix);
            }).Value;
            switch (slot.Role)
            {
                case VerticalItemRole.Hero:
                    return _host.HeroItem();
                case VerticalItemRole.Chrome:
                    return _host.ChromeItem();
                case VerticalItemRole.ExpandableTrack:
                {
                    var host = _host;
                    var scope = _scope;
                    bool narrate = _narrate;
                    // The list column's insets (the page inset, the hero's side columns in the columns form): the row and its hover plate in the column.
                    return host.InsetList(Embed.Comp(() => new TableSlot(host, scope, prefix, narrate)), "vitem:row");
                }
                case VerticalItemRole.Recommendations:
                {
                    // The playlist's "Recommended songs" after the rows (C-Playlist-Compact; an empty list shows it alone, C-Empty-Compact).
                    Element? section = _host.RecommendationsElement();
                    return new BoxEl
                    {
                        Key = section is null ? "vitem:blank" : "vitem:recs", Direction = 1,
                        Children = section is null ? [] : [_host.InsetList(section)],
                    };
                }
                default:
                    return slot.First ? _host.EmptyItem() : new BoxEl { Key = "vitem:blank" };
            }
        }
    }

    /// <summary>A slot of the recommendations-capable list: a track row, or the ONE appended section.</summary>
    sealed class TableRecItem(TableHost host, RowScope scope, bool narrate) : Component
    {
        readonly TableHost _host = host;
        readonly RowScope _scope = scope;
        readonly bool _narrate = narrate;

        public override Element Render()
        {
            // The slot's ROLE (−1 track row, 0 the section, 1 empty), not the raw index: a recycle between two track rows
            // re-renders nothing (see TableSlot.Render).
            int role = UseComputed(() =>
            {
                int at = _scope.Index.Value, n = _host.VisibleCountValue;
                return at < n ? -1 : at == n ? 0 : 1;
            }).Value;
            if (role < 0)
            {
                var host = _host;
                var scope = _scope;
                bool narrate = _narrate;
                return new BoxEl
                {
                    Key = "rec:track", Direction = 1,
                    Children = [Embed.Comp(() => new TableSlot(host, scope, 0, narrate))],
                };
            }
            Element? section = role == 0 ? _host.RecommendationsElement() : null;
            return new BoxEl
            {
                Key = section is null ? "rec:empty" : "rec:section", Direction = 1,
                Children = section is null ? [] : [section],
            };
        }
    }

    /// <summary>The reveal clock: one step per produced frame for as long as it is mounted; unmounting lets the loop idle.
    /// <para>NEVER a step in the mount run (G-259). A signal effect's body runs EAGERLY when the hook is created, and this
    /// component mounts INSIDE the <c>Flow.Show</c> boundary's own run: the boundary has read <c>_rampActive</c> and is still
    /// Dirty while it mounts us. A step there that finishes a short ramp (≤ 24 rows — the armed 12 plus one chunk covers the
    /// band) writes <c>_rampActive = false</c> into that running computation, the engine drops the wakeup
    /// (<c>Computation.MarkDirty</c> returns early while Dirty), the boundary never unmounts us, and every later tick stepped
    /// past Done: blank rows and a loop that never idles. So the mount run only SUBSCRIBES to the tick; every step runs from
    /// a later frame's flush, where the boundary is Clean and hears the write in that same flush. The engine half is D42.</para></summary>
    sealed class TableTicker(TableHost host) : Component
    {
        readonly TableHost _host = host;
        bool _subscribed;

        public override Element Render()
        {
            var tick = UseContextSignal(FluentGpu.Hooks.FrameClock.Tick);
            UseSignalEffect(() =>
            {
                _ = tick.Value;                                              // the subscription is the request for frames
                if (!_subscribed) { _subscribed = true; return; }            // the eager mount run: subscribe, never step
                _host.AdvanceReveal();
            });
            return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
        }
    }
}
