// ── Entities/Track.Table.cs ───────────────────────────────────────────────────────────────────────────────────────
// the detail track TABLE: the source, the view state, tier + relief plumbing, the row shape + snapshot memos, the bound
// list and its three arms (flat / recommendations / vertical-hero), the three-component row stack (slot → skin →
// content), the reveal ramp, membership choreography, selection, drag insertion, the drawer's mount/keying/reflow and
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
        // association) is asked for the rows only AFTER that - so the fold always saw hasVideo:false, the column was
        // decided "no" forever, and a playlist full of music videos showed not one film glyph. The album table has
        // read the live rows through `CachedVideo` all along ("kind 99 lands after the rows paint, and the film lane
        // must appear when it does"); the playlist table now reads them the same way.
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
        /// <summary>Pre-measure column width estimate (Detail.Breakpoints.EstimatePageWidthFromViewport).</summary>
        public float WidthSeed { get; init; }
        /// <summary>Route identity for ScrollMemory / list keys.</summary>
        public string ScrollKey { get; init; } = "";
        /// <summary>Item 5: handed the table's OWN <see cref="ItemsViewController"/> once, at mount, so a caller can
        /// drive <c>StartBringItemIntoView</c> without the table exposing the controller as a stored field of its own
        /// record (which would break <see cref="TableArgs"/>'s data-only equality). Never invoked again after the
        /// first successful call — the controller instance is mount-stable for the table's life.</summary>
        public Action<ItemsViewController>? OnController { get; init; }

        public bool Equals(TableArgs? other)
            => other is not null && (ReferenceEquals(this, other)
               || (Source.Equals(other.Source) && Profile.Equals(other.Profile) && Equals(Vertical, other.Vertical)
                   && (Accent is null) == (other.Accent is null) && ShowToolbar == other.ShowToolbar
                   && Embedded == other.Embedded && (Trailing is null) == (other.Trailing is null)
                   && ReferenceEquals(PlayAllCell, other.PlayAllCell) && ReferenceEquals(HeroHeight, other.HeroHeight)
                   && WidthSeed == other.WidthSeed && string.Equals(ScrollKey, other.ScrollKey, StringComparison.Ordinal)));

        public override int GetHashCode()
            => HashCode.Combine(Source, Profile, Vertical is null, ShowToolbar, Embedded, WidthSeed, ScrollKey);
    }

    /// <summary>The table's live view, for the page-supplied rows it hosts (the Liked chip rail and lens header set and
    /// read the filters; the lens header states the visible count). Provided around the table; null elsewhere.</summary>
    public sealed record TableLive(IReadSignal<TableView> View, Action<FilterState> SetFilters, IReadSignal<int> Visible);

    /// <inheritdoc cref="TableLive"/>
    public static readonly Context<TableLive?> TableLiveSlot = new(null);

    /// <summary>Embed.Comp(args, () => new TableHost()) — TableArgs/TableProfile gate on data, delegates late-bound.</summary>
    public static Element Table(TableArgs args) => Embed.Comp(args, static () => new TableHost());

    // ══ 3. THE HOST ══════════════════════════════════════════════════════════════════════════════════════════════════

    sealed partial class TableHost : Component, IPropsHost
    {
        /// <summary>The ceiling for "Play next" / "Add to queue" on the whole context (0.2.9 DetailQueueActions.MaxBatch).</summary>
        const int MaxQueueBatch = 50;
        const int Overscan = 8;
        const int ReDealReversalMs = 200;
        const int ReDealRows = 24;

        // ── props: data gates the render, behaviour is read through _latest ─────────────────────────────────────────
        readonly Signal<TableArgs?> _args = new(null);
        TableArgs _latest = null!;

        public void ApplyProps(object props)
        {
            _latest = (TableArgs)props;
            _args.SetIfChanged(_latest);
        }

        TableProfile P => _latest.Profile;
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
        readonly Signal<int> _tier = new(0);
        readonly Signal<int> _relief = new(0);
        readonly Signal<bool> _tierMeasured = new(false);
        readonly Signal<float> _colW = new(0f);
        readonly Signal<bool> _rowFlow = new(false);
        float _lastW;
        int _seedTier;
        bool _flowInit;
        Lanes _reliefLanes;
        float _reliefGap = RowMetrics.ColGap, _reliefPad = RowMetrics.PadX;

        // ── the vertical arm ───────────────────────────────────────────────────────────────────────────────────────
        readonly Signal<float> _heroH = new(0f);
        readonly Signal<bool> _compactInteractive = new(false);
        readonly Signal<bool> _bodyClipEngaged = new(false);
        readonly Signal<bool> _verticalFacts = new(false);
        readonly Signal<int> _verticalItemCount = new(Detail.VerticalLayout.ItemCount(0, false));

        // ── selection · list controller · counts ───────────────────────────────────────────────────────────────────
        readonly SelectionModel _selection = new();
        readonly ItemsViewController _listCtl = new();
        /// <summary>The list viewport's <see cref="IScrollController"/> seam — the target the column header names in its
        /// <c>WheelTarget</c>, so a wheel over the header (laid out ABOVE the list, outside its viewport) glides the rows
        /// exactly as a wheel over them would.</summary>
        readonly AnnotatedScrollBarController _wheelCtl = new();
        readonly Signal<int> _visible = new(0);
        readonly Signal<int> _listCount = new(0);
        readonly Signal<string> _expanded = new("");   // MembershipDiff.RowKey of the ONE open drawer, "" = none
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
        Memo<bool>? _hotSettled;
        BoundItemsSource<Track>? _rowItems;
        /// <summary>B2 (plan §3.2): the parallel episode-row projection over the SAME view/index space as
        /// <see cref="_rowItems"/> — invalid (<c>default</c>) at every index that isn't an episode row.</summary>
        BoundItemsSource<Episode.RowItem>? _episodeItems;

        // ── cached delegates (mount-stable; list options freeze at mount and must read live state) ─────────────────
        readonly Action<RectF> _onBounds;
        readonly Action<float> _onHeroMeasured;
        readonly Action<bool> _onStuck, _onBodyClip;
        readonly Action _playAll, _shuffle;
        Detail.VerticalSpec? _heroSpecFrom, _heroSpec;
        readonly Func<bool> _checksRead, _rampActiveRead, _false = static () => false;
        readonly Func<ColorF> _accent;
        readonly Func<int, (float dx, float dy)?> _flipFrom, _verticalFlipFrom;
        readonly Func<int, (float from, float delayMs)?> _fadeFrom, _verticalFadeFrom;

        public TableHost()
        {
            _onBounds = OnColumnBounds;
            _onHeroMeasured = h => { if (h > 1f && MathF.Abs(_heroH.Peek() - h) > 1f) _heroH.Value = h; };
            _onStuck = pinned => { if (_compactInteractive.Peek() != pinned) _compactInteractive.Value = pinned; };
            _onBodyClip = engaged => { if (_bodyClipEngaged.Peek() != engaged) _bodyClipEngaged.Value = engaged; };
            _playAll = () => StartVisible(0);
            _shuffle = Shuffle;
            _checksRead = () => _checksVisible?.Value ?? false;
            _rampActiveRead = () => _rampActive.Value;
            _accent = AccentNow;
            _flipFrom = display => _flip.TryGetValue(display, out var f) ? f : null;
            _fadeFrom = display => _fade.TryGetValue(display, out var f) ? f : null;
            _verticalFlipFrom = item => IsVerticalRow(item) ? _flipFrom(item - Detail.VerticalLayout.PrefixCount) : null;
            _verticalFadeFrom = item => IsVerticalRow(item) ? _fadeFrom(item - Detail.VerticalLayout.PrefixCount) : null;
            InitChrome();
        }

        /// <summary>ROW slots only: the seeds are keyed by DISPLAY index, and the placeholder/footer slots sit past the live
        /// row count, where a stale seed would make the footer rise like a track that moved.</summary>
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
        internal readonly record struct Shape(ColumnSet Set, TrackSize[] Tracks, float Art, float RowH, int Density, float EpisodeRowH = 0f)
        {
            public bool Equals(Shape other)
            {
                if (!Set.Equals(other.Set) || Art != other.Art || RowH != other.RowH || Density != other.Density
                    || EpisodeRowH != other.EpisodeRowH) return false;
                var a = Tracks; var b = other.Tracks;
                if (ReferenceEquals(a, b)) return true;
                if (a is null || b is null || a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }

            public override int GetHashCode() => HashCode.Combine(Set, Art, RowH, Density, Tracks?.Length ?? 0);
        }

        static bool TempoColumnPref()
        {
            _ = Prefs.Appearance.Epoch.Value;
            return Platform.Settings.Get(Platform.Keys.TempoColumn);
        }

        static bool PlaysColumnPref()
        {
            _ = Prefs.Appearance.Epoch.Value;
            return Platform.Settings.Get(Platform.Keys.PlaysColumn);
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
                Prefs.Appearance.TrackRowStyle() == 1, Prefs.Appearance.TrackArtworkHidden(), Prefs.Appearance.Marquee(),
                TempoColumnPref(), PlaysColumnPref(), cfg);
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
            for (int i = 0; i < hot; i++)
            {
                var t = src.At(i);
                if (t.IsValid && TableRules.RowUnsettled(t.Knows(TrackFields.Face), t.InFlight, t.Answered)) return false;
            }
            return true;
        }

        Shape ComputeShape()
        {
            var snap = _snapshot!.Value;
            var args = _args.Value!;
            // Two ladders, in order: the tier admits the lanes this WIDTH allows, then relief takes back the ones the
            // identity lanes cannot pay for. Relief only subtracts, so this can never widen the table.
            var admitted = SetFor(in snap, args, ClampTier(_tier.Value));
            var set = TableRules.ApplyRelief(in admitted, ReliefStepFor(in admitted));
            int density = Prefs.Appearance.RowDensity();
            float art = TableRules.ArtSizeFor(density, set.Classic);
            return new Shape(set, Track.TracksFor(in set, art), art, TableRules.RowHeightFor(density, set.Classic), density,
                RowMetrics.EpisodeRowHeightFor(density));
        }

        static ColumnSet SetFor(in Snapshot s, TableArgs args, int tier)
        {
            var cfg = s.Config;
            var src = args.Source;
            var identity = TableRules.IdentityColumns(s.Classic, cfg.ShowArtThumb, s.ArtHidden, cfg.ShowTrackArtist, tier);
            // A null Drawer seam is "no chevron lane" (the contract), so it folds into ShowVersions before the tier gate.
            var trailing = TableRules.TrailingColumns(s.Classic, src.HasVideo, cfg.ShowVersions && args.Profile.Drawer is not null, tier);
            return new ColumnSet(
                Album: cfg.ShowAlbumColumn && tier < 2,
                By: src.HasAddedBy && tier < 1,
                Date: src.HasDateAdded && tier < 3,
                Video: trailing.Video,
                Plays: (cfg.ShowPlays || (cfg.PlaysColumnOptIn && s.PlaysColumn)) && tier < 3,
                Heart: tier < 5,
                Thumb: identity.Thumb,
                Actions: trailing.Actions,
                Tier: tier,
                Tempo: cfg.ShowTempo && s.TempoColumn,
                Expand: trailing.Expand,
                Artist: identity.Artist,
                Classic: s.Classic);
        }

        /// <summary>Self-heal: never RENDER a tier wider than the last measured width supports; before the first measure the
        /// width seed governs and the one-shot `_tierMeasured` flip retires it (DetailTracks.cs:565-581).</summary>
        int ClampTier(int tier)
        {
            if (_lastW <= 0f)
            {
                _ = _tierMeasured.Value;
                return Math.Max(tier, _seedTier);
            }
            int fit = Detail.Breakpoints.TierFor(_lastW, tier, initialized: true);
            return fit > tier ? fit : tier;
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
            if (VerticalArm)
            {
                bool flow = Detail.VerticalLayout.RowFlow(r.W, _rowFlow.Peek(), _flowInit);
                // A stacked ↔ row restructure changes the natural hero height a lot; clear the measure so the collapse
                // binds cannot keep the previous flow's height (DetailTracks.cs:1144-1150).
                if (_flowInit && flow != _rowFlow.Peek()) _heroH.Value = 0f;
                if (flow != _rowFlow.Peek()) _rowFlow.Value = flow;
                _flowInit = true;
            }
            bool measured = _tierMeasured.Peek();
            int t = Detail.Breakpoints.TierFor(r.W, _tier.Peek(), measured);
            if (t != _tier.Peek()) _tier.Value = t;
            int relief = TableRules.ReliefFor(in _reliefLanes, r.W, _reliefGap, _reliefPad, _relief.Peek(), measured);
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

        /// <summary>The ONE density writer: persist + bump, so every mounted page re-reads it (ch 30 N12).</summary>
        static void SetDensity(int density) => Prefs.Appearance.Set(Platform.Keys.RowDensity, Math.Clamp(density, 0, 3));

        /// <summary>A context's own view: the persisted sort (−1 sentinel ⇒ the profile default) and a fresh query/filter/
        /// multi-select. From a layout effect keyed on the context — never from Render.</summary>
        void LoadView(EntityUri context)
        {
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
            _overlay = UseContext(Overlay.Service);
            _hooks = UseContext(InputHooks.Current);
            _post = UsePost();

            _snapshot = UseComputed(ComputeSnapshot);
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
            _checksVisible = UseComputed(() => _multi.Value || _selectedCount!.Value >= 2);
            _selectionVisible = UseComputed(() => { int n = _selectedCount!.Value; return n > 0 && (_multi.Value || n >= 2); });
            var live = UseMemo(() => new TableLive(_viewMemo!, SetFilters, _visible), DepKey.Empty);

            var context = args.Source.Context;
            UseLayoutEffect(() => LoadView(context), DepKey.From(context.Id.GetHashCode()));

            if (_lastW <= 0f && args.WidthSeed > 0f) _seedTier = Detail.Breakpoints.InitialTierForViewport(args.WidthSeed);

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
                if (_sawUnknown || trailing) { _rampPending = true; _rampEpoch++; }
            }
            UseLayoutEffect(ArmRamp, DepKey.From(_rampEpoch));

            // ── membership choreography (§4.6) + the breakpoint re-deal, seeded in the render that commits the order ──
            int resetBefore = _resetEpoch;
            TrackOrder(in snap, shape.RowH);
            ReDeal(shape.Set.Tier, shape.RowH);
            _dealtThisFrame = false;
            UseLayoutEffect(BumpDisplacement, DepKey.From(_dealEpoch));
            bool narrate = _resetEpoch != resetBefore;

            if (args.PlayAllCell is { Length: > 0 } cell) cell[0] = _playAll;

            Element? chips = P.ContentFilterBar?.Invoke();
            Element? lens = P.LensHeader?.Invoke();
            float extent = (chips is not null ? P.ContentFilterExtent : 0f) + (lens is not null ? P.LensExtent : 0f);
            float stickyInset = Detail.VerticalLayout.StickyClipInset(extent, TableRules.HeaderHeightFor(shape.Set.Classic));
            UseLayoutEffect(() => ApplyItemBand(stickyInset), DepKey.From(stickyInset, (float)_resetEpoch));

            bool recsCapable = cfg.Recommendations && !args.Embedded && !vertical && P.Recommendations is not null;
            bool hasFacts = vertical && !trailing && args.Vertical!.Slots.LikedFacts is not null;
            var listState = Playlist.RowsStateOf(snap.State is EdgeState.Partial or EdgeState.Complete, snap.Count, visible);
            bool recsLive = recsCapable && listState is PlaylistRowsState.Rows or PlaylistRowsState.Empty;
            int listTotal = visible + (recsLive ? 1 : 0);
            UseLayoutEffect(() =>
            {
                _visible.Value = visible;
                _listCount.Value = listTotal;
                _verticalFacts.Value = hasFacts;
                _verticalItemCount.Value = Detail.VerticalLayout.ItemCount(visible, hasFacts);
            }, DepKey.From(visible, listTotal, hasFacts ? 1 : 0, 0));
            UseEffect(PublishHeroHeight);

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
                vertical && !trailing ? VerticalList(visible, layout, stickyInset, hasFacts, narrate)
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
            string filterKey = vertical ? "" : ":q" + HashCode.Combine(snap.Query, snap.Filters).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string listKey = "list:" + args.ScrollKey + ":" + (vertical ? "vh:" : "") + "d" + shape.Density
                             + (shape.Set.Classic ? ":classic" : ":modern") + filterKey + ":r" + _resetEpoch + (recsCapable ? ":rec" : "");
            Element listKeyed = new BoxEl
            {
                Key = listKey, Grow = trailing ? 0f : 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, Children = [region],
            };

            Element body = trailing ? TrailingBody(listKeyed, vertical, stickyInset, in shape, chips, lens) : listKeyed;
            var column = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                OnBoundsChanged = _onBounds,
                // The two-column chrome (toolbar · chips · lens · column header) sits ABOVE the list, outside its
                // viewport, so a wheel over it reached nothing; it names the list as its wheel target and the notch
                // glides the rows. In the album (trailing) arm the OUTER ScrollView owns scrolling and exposes no
                // IScrollController seam, so there is no target to name yet. The hero arm's chrome is item 1 of the
                // list itself and needs none.
                Children = vertical
                    ? [body]
                    : [Chrome(in shape, chips, lens) with { WheelTarget = trailing ? null : _wheelCtl }, body],
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

        /// <summary>ItemsView options freeze at mount, but the Liked chip rail / lens can arrive after enrichment: patch the
        /// live viewport's shared item-band clip in place so 93 → 141 DIP needs no remount (DetailTracks.cs:1721-1735).</summary>
        void ApplyItemBand(float inset)
        {
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

        string ListScrollKey => _latest.ScrollKey + ":r" + _resetEpoch;

        Element FlatList(RepeatLayout layout, bool trailing, bool narrate)
            => ItemsView.CreateBound(_rowItems!,
                scope => Embed.Comp(() => new TableSlot(this, scope.Row, 0, narrate)),
                layout,
                new ListOptions<Track>
                {
                    SelectionMode = Cfg.Selection,
                    Selection = _selection,
                    IsItemInvokedEnabled = true,
                    OnInvokedTyped = (i, _) => PlayRow(i),
                    ContentType = i => (int)RowKindAt(i),
                    Overscan = Overscan,
                    Grow = trailing ? 0f : 1f,
                    Controller = _listCtl,
                    // Alpha-mask edge fade: the page floats over a tone plane with no opaque plate. Nested in the album
                    // trailing scroller, the OUTER ScrollView owns scrolling and its cue.
                    Scroll = new ScrollOptions { AutoEdgeFade = !trailing, ScrollKey = ListScrollKey, VerticalScrollController = _wheelCtl },
                    Reorder = new ReorderOptions { DisplacementVersion = _dispVer },
                    Insertion = Insertion(),
                    Entrance = new EntranceOptions { ItemFlipFrom = _flipFrom, ItemFadeFrom = _fadeFrom },
                });

        /// <summary>Track rows, then ONE appended item: the recommendations section (owner O's element, header and states
        /// included). The COUNT carries the section only while the page has rows or is empty — never over a shimmer.</summary>
        Element RecsList(int total, RepeatLayout layout, bool trailing, bool narrate)
            => ItemsView.CreateBound(total,
                scope => Embed.Comp(() => new TableRecItem(this, scope, narrate)),
                layout,
                new ListOptions
                {
                    SelectionMode = Cfg.Selection,
                    Selection = _selection,
                    IsItemInvokedEnabled = true,
                    OnInvoked = i => { if (_rowItems!.TryPeek(i, out _)) PlayRow(i); },
                    IsItemEnabled = i => _rowItems!.TryPeek(i, out _),
                    Overscan = Overscan,
                    Grow = trailing ? 0f : 1f,
                    Controller = _listCtl,
                    CountSignal = _listCount,
                    Scroll = new ScrollOptions { AutoEdgeFade = !trailing, ScrollKey = ListScrollKey, VerticalScrollController = _wheelCtl },
                    Reorder = new ReorderOptions { DisplacementVersion = _dispVer },
                    Insertion = Insertion(),
                    Entrance = new EntranceOptions { ItemFlipFrom = _flipFrom, ItemFadeFrom = _fadeFrom },
                });

        /// <summary>Playlist/Liked hero system: a HARD viewport whose items 0 and 1 are the hero (collapsing into the 56-DIP
        /// band) and the chrome (sticky at 56), kept mounted; the recyclable suffix is clipped by ONE shared band at the sticky
        /// inset with a 24-DIP feather, and no stock scroll-edge cue (ch 03 §0.16).
        /// <para>THE PIN LIVES ON A RAW WRAPPER, NOT ON THE ITEM COMPONENT. Every slot's content is a
        /// <see cref="TableVerticalItem"/>, and a component anchor MIRRORS its rendered child's size
        /// (<c>Reconciler.MirrorParticipation</c>) while <c>ScrollBindEval.ApplyPin</c> clamps a pin to its IMMEDIATE
        /// parent (<c>limit = parent.H − node.H</c>) — a bind on the component's rendered root therefore gets
        /// <c>limit == 0</c> and never pins. So the two persistent prefix slots get a RAW <see cref="BoxEl"/> wrapper
        /// that carries the bind, chosen ONCE per slot from <c>scope.Index.Peek()</c> (slots 0/1 never recycle —
        /// <see cref="ListOptions.PersistentPrefixCount"/>); every other slot stays the bare component.</para></summary>
        Element VerticalList(int visible, RepeatLayout layout, float stickyInset, bool facts, bool narrate)
        {
            int prefix = Detail.VerticalLayout.PrefixCount;
            return ItemsView.CreateBound(Detail.VerticalLayout.ItemCount(visible, facts),
                scope =>
                {
                    Element content = Embed.Comp(() => new TableVerticalItem(this, scope, narrate));
                    int initial = scope.Index.Peek();
                    if (initial > 1) return content;
                    // Item 0 — the hero's PIN. Its companion PresentedH row stays on the item's own root
                    // (TableVerticalItem → HeroItem), which re-bakes when the measured hero height settles; a child clip
                    // rides with this wrapper's pin translation, so the two halves of the old `.Collapse` read identically
                    // split across wrapper and inner. Item 1 — the chrome's sticky at the compact band, whose `_onStuck`
                    // edge is the input handoff the hero reads.
                    return new BoxEl
                    {
                        Key = initial == 0 ? "vitem:hero" : "vitem:chrome",
                        Direction = 1, MinWidth = 0f, Children = [content],
                        ScrollBinds = initial == 0
                            ? [new ScrollBindDsl { PinTop = 0f }]
                            : [new ScrollBindDsl { PinTop = Detail.VerticalLayout.CompactIdentityHeight, OnFlag = _onStuck }],
                    };
                },
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
                    ContentType = i => i < prefix ? -1 - i : (int)RowKindAt(i - prefix),
                    Overscan = Overscan,
                    PersistentPrefixCount = prefix,
                    Grow = 1f,
                    Controller = _listCtl,
                    CountSignal = _verticalItemCount,
                    Scroll = new ScrollOptions
                    {
                        ScrollKey = ListScrollKey,
                        AutoEdgeFade = false,
                        EdgeCues = ScrollEdgeCues.None,
                        ItemClipTopInset = stickyInset,
                        ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand,
                    },
                    Reorder = new ReorderOptions { DisplacementVersion = _dispVer },
                    Insertion = Insertion(),
                    Entrance = new EntranceOptions { ItemFlipFrom = _verticalFlipFrom, ItemFadeFrom = _verticalFadeFrom },
                });
        }

        /// <summary>Album/single (HasTrailing): one outer scroller. In the hero arm the hero is the first child; the
        /// chrome no longer shares the WHOLE page column as its sticky containing block (that pinned it past the last
        /// row, over the trailing shelves — the engine's sticky/clip binds clamp to the IMMEDIATE PARENT's bounds,
        /// <c>ScrollBindEval.cs:214-233</c>, and the DSL has no range-end, so the containing block IS the release rule).
        /// <c>tableBlock</c> wraps the chrome and the rows as ONE box whose height ends with the last row, so
        /// <see cref="ChromeRoot"/>'s <c>.Sticky</c> releases there; the trailing shelves sit AFTER it, in their own
        /// block, clipped at the same compact-band line so nothing shows through it while still riding under the band.
        /// Both wrappers stay RAW <see cref="BoxEl"/>s — a component anchor mirrors its child's height (limit 0, never
        /// pins; see Artist.Reader.cs:364-372 for the same rule).</summary>
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
                    EdgeFade = _bodyClipEngaged.Value
                        ? new EdgeFadeSpec(EdgeMask.Top, Detail.VerticalLayout.StickyFadeBand)
                        : null,
                    ScrollBinds = [new ScrollBindDsl { ClipTopAtViewport = stickyInset, OnFlag = _onBodyClip }],
                    Children = [listKeyed],
                };
                Element tableBlock = new BoxEl { Direction = 1, Children = [ChromeRoot(in shape, chips, lens), listBlock] };
                Element trailBlock = new BoxEl { Direction = 1, Children = kids.ToArray() }
                    .ClipBelow(Detail.VerticalLayout.TrailingClipInset);
                children = [HeroRoot(spec), tableBlock, trailBlock];
            }
            else
            {
                Element content = new BoxEl
                {
                    Direction = 1,
                    EdgeFade = vertical && _bodyClipEngaged.Value
                        ? new EdgeFadeSpec(EdgeMask.Top, Detail.VerticalLayout.StickyFadeBand)
                        : null,
                    ScrollBinds = vertical ? [new ScrollBindDsl { ClipTopAtViewport = stickyInset, OnFlag = _onBodyClip }] : [],
                    Children = [listKeyed, .. kids],
                };
                children = [content];
            }
            // Keyed by the route/pane identity: a host that PERSISTS across contents (the library pane re-skins in place)
            // must not hand the next album the previous one's offset, and a ScrollEl restores by node, not by a key of
            // its own. A page mounts one per route anyway, so this only ever costs the pane a fresh viewport.
            return ScrollView(new BoxEl { Direction = 1, Grow = 1f, AlignSelf = FlexAlign.Stretch, Children = children }) with
            {
                Key = "trail:" + _latest.ScrollKey,
                Grow = 1f,
                EdgeCues = vertical ? ScrollEdgeCues.None : ScrollEdgeCues.Auto,
            };
        }

        Element HeroRoot(Detail.VerticalSpec spec)
        {
            float colW = ColumnWidth();
            float heroH = HeroHeightFor(spec, colW);
            return new BoxEl
            {
                Key = "vertical:hero-root", Direction = 1, ClipToBounds = true,
                Children = [new BoxEl { Key = "vhero:header", Direction = 1, Children = [Detail.Hero(HeroSpec(spec), HeroPartsFor(spec, colW, heroH))] }],
            }.Collapse(heroH, Detail.VerticalLayout.CompactIdentityHeight, Detail.VerticalLayout.CollapseDistance(heroH));
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

        Element ChromeRoot(in Shape shape, Element? chips, Element? lens)
            => new BoxEl { Key = "vertical:chrome-root", Direction = 1, Children = [Chrome(in shape, chips, lens)] }
                .Sticky(Detail.VerticalLayout.CompactIdentityHeight, _onStuck);

        float HeroHeightFor(Detail.VerticalSpec spec, float colW)
        {
            float measured = _heroH.Value;
            // Pre-measure: the SAME pessimistic null-title plan the skeleton reserves (ch 03 §9.3).
            return measured > 1f ? measured : Detail.HeroBandHeight(spec, colW);
        }

        Detail.HeroParts HeroPartsFor(Detail.VerticalSpec spec, float colW, float heroH)
        {
            float left = RowMetrics.PadXFor(_shape!.Value.Set.Tier);
            bool toolbar = _latest.ShowToolbar && spec.Config.Content == DetailContent.Tracks;
            // ── INSIGHTS SHEET (additive) ── the frame's own answer to Detail.InsightsSheet.ShowsToggle, threaded down
            //    as the presence of the toggle object: the band does not re-derive it, and the pinned band and the hero
            //    toolbar therefore cannot disagree about whether the sheet has an entry point. The BAND is the one that
            //    survives the collapse (Detail.Insights.cs §7); the hero keeps its own for the pre-stuck range, where
            //    the band is transparent and owns no input.
            var insights = spec.Insights;
            return new Detail.HeroParts(
                Toolbar: toolbar ? Toolbar() : null,
                BandActions: BandActions(insights),
                SelectionBar: Cfg.Selection == ItemsSelectionMode.None ? null : SelectionSurface("compact-selection"),
                SelectionVisible: _selectionVisible!,
                CompactInteractive: _compactInteractive,
                SearchField: CompactSearch(colW, left, insights is not null),
                SearchExpanded: _searchExpanded,
                ColumnWidth: colW,
                CompactLeft: left,
                HeroHeight: heroH,
                OnHeroMeasured: _onHeroMeasured);
        }

        // ── THE RULE, STATED ONCE (three authors have now tripped over it) ──────────────────────────────────────────
        // A SCROLL BIND MUST SIT ON A RAW ELEMENT, NEVER ON A COMPONENT'S RENDERED ROOT.
        // `ScrollBindEval.ApplyPin` clamps a pin to its IMMEDIATE parent (`limit = parent.H − node.H`), and a component
        // anchor MIRRORS its rendered child's size (`Reconciler.MirrorParticipation`). Put `.Collapse`/`.Sticky` on what
        // a component returns and the parent IS that mirror: `limit == 0`, the node never pins, `StickyPinned` is never
        // set and the `onStuck` callback never fires — while the item band still reserves `StickyClipInset` for a pinned
        // hero + chrome (the empty 93–177 DIP header band) and the hero's inner presentation still translates by −offset
        // to cancel a pin that is not happening (the 2× hero scroll). So: `TableHost.VerticalList` wraps these two items
        // in a RAW `BoxEl` and puts the pin/sticky THERE; the album arm goes through `TrailingBody`, whose `HeroRoot`/
        // `ChromeRoot` are already raw `BoxEl`s, which is why it was never affected.
        // What may stay here: a PAINT-only row (`PresentedH`, clip) — it has no containing-block clamp and rides the
        // wrapper's pin translation, so it belongs with the measured height that re-bakes it.

        /// <summary>Item 0 of the vertical list: the hero collapsing into the band — its PAINT half only. The
        /// <c>PresentedH</c> row re-bakes here when the measured height settles (this item component re-renders on
        /// `_heroH`); the PIN half of the old `.Collapse` lives on the raw wrapper <see cref="VerticalList"/> mounts
        /// around this component — see THE RULE above.</summary>
        internal Element HeroItem()
        {
            var spec = _latest.Vertical!;
            float colW = ColumnWidth();
            float heroH = HeroHeightFor(spec, colW);
            return new BoxEl
            {
                Direction = 1,
                Children = [Detail.Hero(HeroSpec(spec), HeroPartsFor(spec, colW, heroH))],
                ScrollBinds =
                [
                    new ScrollBindDsl
                    {
                        From = ScrollChannel.Offset, To = BindSink.PresentedH,
                        Range = ScrollRange.Px(0f, Detail.VerticalLayout.CollapseDistance(heroH)),
                        OutStart = heroH, OutEnd = Detail.VerticalLayout.CompactIdentityHeight,
                    },
                ],
            };
        }

        /// <summary>Item 1: chips · lens · column header. The sticky pin at 56 and its onStuck flag — the input handoff
        /// the hero reads (ch 03 §0.18) — live on the raw wrapper <see cref="VerticalList"/> mounts around this
        /// component, NOT here: see THE RULE above.</summary>
        internal Element ChromeItem()
        {
            var shape = _shape!.Value;
            Element? chips = P.ContentFilterBar?.Invoke();
            Element? lens = P.LensHeader?.Invoke();
            return Chrome(in shape, chips, lens);
        }

        internal Element FooterItem()
        {
            var shape = _shape!.Value;
            float pad = RowMetrics.PadXFor(shape.Set.Tier);
            float colW = ColumnWidth();
            Element? facts = _latest.Vertical?.Slots.LikedFacts?.Invoke(MathF.Max(0f, colW - pad * 2f));
            return new BoxEl
            {
                Key = "vitem:facts", Direction = 1,
                Padding = new Edges4(pad, Spacing.XXL, pad, Spacing.XXL),
                Children = facts is null ? [] : [facts],
            };
        }

        internal Element EmptyItem()
        {
            var snap = _snapshot!.Value;
            var shape = _shape!.Value;
            int visible = _rowItems!.Count.Value;
            var state = Playlist.RowsStateOf(snap.State is EdgeState.Partial or EdgeState.Complete, snap.Count, visible);
            return new BoxEl { Key = "vitem:empty", MinHeight = 160f, Direction = 1, Children = [Placeholder(state, in shape)] };
        }

        internal int VisibleCountValue => _rowItems!.Count.Value;
        internal bool FactsValue => _verticalFacts.Value;
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
        /// source; the shimmer can never drift from the rows).</summary>
        Element RowsShimmer(in Shape shape)
        {
            var rows = new Element[Design.RevealRamp.Chunk];
            var set = shape.Set;
            var state = default(RowState);
            var options = new GridOptions(Art: shape.Art, MoreEnabled: false);
            for (int i = 0; i < rows.Length; i++)
            {
                Element title = new TextEl("") { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
                rows[i] = Track.Grid(default, i, in state, in set, shape.Tracks, shape.RowH, title, in options);
            }
            return new BoxEl { Direction = 1, Children = rows };
        }

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
                    Detail.HeroSkeleton(spec, ColumnWidth(), RowMetrics.PadXFor(shape.Set.Tier)),
                    Chrome(in shape, chips, lens),
                    RowsShimmer(in shape),
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
        /// to the clicked row. "Track details" exists only where the chevron lane does not (ShowVersionsMenuItem).</summary>
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
            if (TableRules.ShowVersionsMenuItem(Cfg.ShowVersions && P.Drawer is not null, expandLane, t.Uri.Kind == EntityKind.Track))
            {
                string key = MembershipDiff.RowKey(t, ItemIdAt(display), display);
                details = () => ToggleExpanded(key, t);
            }
            var options = new MenuOptions(Host: selectable ? HostFor() : default, ShowGoToAlbum: Cfg.ShowAlbumColumn, TrackDetails: details);
            if (!selectable) return Track.Menu([t], in options);
            return Track.RowMenu(_selection, slotIndex, i => DisplayTrack(i - start), i => OriginalOf(i - start), in options);
        }

        internal void ToggleExpanded(string rowKey, Track t)
        {
            bool opening = !string.Equals(_expanded.Peek(), rowKey, StringComparison.Ordinal);
            _expanded.Value = opening ? rowKey : "";
            if (opening && t.IsValid) Entities.Ensure(t, TrackFields.All);   // the drawer states EVERY fact
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
            // The insertable sub-range: TRACK rows only — never the hero/chrome prefix, the recs section or the footer.
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

        static readonly LayoutTransition s_mountEntrance = new(TransitionChannels.Opacity,
            TransitionDynamics.Tween(280f, Easing.FluentDecelerate), Enter: new EnterExit(Opacity: 0f, Active: true));

        static readonly LayoutTransition s_checkShift = new(TransitionChannels.Position,
            TransitionDynamics.Tween(MotionTok.DisclosureExpand.DurationMs, Easing.FluentDecelerate));

        // TWO specs on TWO nodes (DetailTracks.cs:3279-3294): the clip box eases its HEIGHT only (a position terminal there
        // would move the window itself); the presence box inside it fades and drops the content under a stationary window.
        // Enter/Exit Active + Reflow is the mount-reflow opt-in.
        static readonly LayoutTransition s_drawerReveal = new(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(),
            Enter: new EnterExit(Active: true), Exit: new EnterExit(Active: true), ExitDynamics: MotionTok.ControlFast.ToDynamics(),
            Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, SuppressDescendantTransitions: true);

        static readonly LayoutTransition s_drawerPresence = new(TransitionChannels.Opacity | TransitionChannels.Position,
            MotionTok.ControlNormal.ToDynamics(),
            Enter: new EnterExit(Dy: -Spacing.S, Opacity: 0f, Active: true),
            Exit: new EnterExit(Dy: -Spacing.XS, Opacity: 0f, Active: true),
            ExitDynamics: MotionTok.ControlFast.ToDynamics());

        /// <summary>The bound, shape-stable row container for a TRACK row. Zebra is DISPLAY-index parity; selection never
        /// changes the fill except in Classic (RowHover); the 3×16 pill is the highlight cue and hands over to the check
        /// lane; an .mp4 drag over the row rides the same fill closure instead of an overlay node (ch 04 §0.6). Built by
        /// the slot on its rare re-renders (shape / flow / open), so a recycle only moves the closures' index signal and
        /// allocates nothing.
        /// <para>An episode row is never wrapped here — <see cref="TableSlot"/> hands it straight to
        /// <see cref="Episode.ReaderRow"/>, which is fully self-contained (its own click, hover, check lane, "…").</para></summary>
        internal BoxEl Skin(RowScope scope, Element content, in Shape shape, bool rowFlow, bool open, int start,
                            Signal<bool> hovered, bool entrance)
        {
            var index = scope.Index;
            var isSel = scope.IsSelected;
            var interact = scope.OnInteraction;
            bool classic = shape.Set.Classic;
            bool plain = !classic && VerticalArm && !rowFlow;   // the hero system's STACKED flow: plain full-bleed rows
            float pad = RowMetrics.PadXFor(shape.Set.Tier);
            var drop = Video.Overrides.Present ? VideoDrop(index, start) : null;
            Func<bool> cue = drop is null ? _false : () => _videoDropRow.Value == index.Value;
            bool Odd() => Math.Max(0, index.Value - start) % 2 != 0;

            var skin = new BoxEl
            {
                ZStack = true, MinHeight = shape.RowH, ClipToBounds = true,
                Margin = classic || plain ? default : new Edges4(RowMetrics.RowInset, 0f, RowMetrics.RowInset, 0f),
                // Bottom corners square while THIS row's drawer is open, so the row and its drawer read as one plate.
                Corners = classic || plain ? CornerRadius4.All(0f)
                    : open ? new CornerRadius4(6f, 6f, 0f, 0f) : CornerRadius4.All(6f),
                Animate = entrance ? s_mountEntrance : null,
                DropTarget = drop,
                Fill = classic ? Prop.Of(() => cue() || isSel() ? Design.Colors.RowHover : ColorF.Transparent)
                    : plain ? Prop.Of(() => cue() ? Design.Colors.RowHover : ColorF.Transparent)
                    : Prop.Of(() => cue() ? Design.Colors.RowHover : Odd() ? Design.Colors.RowZebra : ColorF.Transparent),
                HoverFill = classic || plain ? Prop.Of(static () => Design.Colors.RowHover)
                    : Prop.Of(() => Odd() ? Design.Colors.RowHoverZebra : Design.Colors.RowHover),
                PressedFill = classic || plain ? Prop.Of(static () => Design.Colors.RowPressed)
                    : Prop.Of(() => Odd() ? Design.Colors.RowPressedZebra : Design.Colors.RowPressed),
                // No PressScale: a ~1000-DIP row that shrinks 2 % reads as the whole row springing back (ch 01 §0.11).
                // Stationary lift: the row stays at 0.4 while the chip follows the pointer.
                Draggable = Drag.Source(() => DragPayloadFor(index.Peek(), start)),
                BorderWidth = classic || plain ? 0f : 1f,
                BorderColor = classic || plain ? ColorF.Transparent
                    : Prop.Of(() => Odd() ? Tok.StrokeCardDefault : ColorF.Transparent),
                HoverBorderColor = classic || plain ? ColorF.Transparent : Tok.StrokeCardDefault,
                FocusVisualMargin = Design.FocusInsetRow,
                Focusable = false,                   // the ItemsView roving effect owns the one tab stop
                Role = AutomationRole.Button,
                OnPointerReleased = args =>
                {
                    if (args.ClickCount >= 2) interact(ItemContainerTrigger.DoubleTap, args.Mods);
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
                OnFocusChanged = scope.OnFocusChanged,
                // Row hover → the equalizer's pause (it sits under a hover-opacity reveal and must stop ticking).
                OnHoverMove = _ => { if (!hovered.Peek()) hovered.Value = true; },
                OnPointerExit = () => { if (hovered.Peek()) hovered.Value = false; },
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Animate = s_checkShift,
                        Children = [SelectorVisualsBound.BoundCheckLane(_checksRead, isSel, interact, leftMargin: 4f), content],
                    },
                    new BoxEl
                    {
                        Key = "row-pill", Width = 3f, Height = 16f, Margin = new Edges4(2f, 0f, 0f, 0f),
                        Corners = classic ? CornerRadius4.All(0f) : CornerRadius4.All(1.5f),
                        Fill = Prop.Of(_accent), AlignSelf = FlexAlign.Center,
                        HitTestVisible = false, PressScale = 10f / 16f,
                        Opacity = classic ? (Prop<float>)0f : Prop.Of(() => isSel() && !_checksRead() ? 1f : 0f),
                    },
                    new BoxEl
                    {
                        Key = "row-divider", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = 1f,
                        Fill = classic ? Prop.Of(static () => Tok.StrokeDividerDefault) : (Prop<ColorF>)ColorF.Transparent,
                        Margin = classic ? new Edges4(pad, 0f, pad, 0f) : default,
                        HitTestVisible = false,
                    },
                ],
            };
            var overlay = _overlay;
            if (Controls.IsNullOverlay(overlay)) return skin;
            bool expandLane = shape.Set.Expand;
            // Attached ONCE per slot render; the factory runs at OPEN and reads the slot's live index + selection.
            return skin.WithContextMenu(overlay, () => RowMenuFor(index.Peek(), start, expandLane));
        }

        /// <summary>The drawer's mount, keying and reflow — its BODY is the profile's. One drawer at a time, keyed by ROW
        /// identity (item id, else uri#@display), continuing the row's zebra parity with square top corners.</summary>
        internal Element DrawerBox(Track t, int display, in Shape shape, IReadSignal<int> index, int start)
        {
            string rowKey = MembershipDiff.RowKey(t, ItemIdAt(display), display);
            bool classic = shape.Set.Classic;
            Element body = P.Drawer?.Invoke(t, display) ?? new BoxEl();
            return new BoxEl
            {
                Key = "drawer:" + rowKey, Direction = 1, MinWidth = 0f, ClipToBounds = true, Animate = s_drawerReveal,
                Fill = classic ? (Prop<ColorF>)ColorF.Transparent
                    : Prop.Of(() => Math.Max(0, index.Value - start) % 2 != 0 ? Design.Colors.RowZebra : ColorF.Transparent),
                Margin = classic ? default : new Edges4(RowMetrics.RowInset, 0f, RowMetrics.RowInset, 0f),
                Corners = classic ? CornerRadius4.All(0f) : new CornerRadius4(0f, 0f, 6f, 6f),
                Children =
                [
                    new BoxEl
                    {
                        Key = "drawer-presence:" + rowKey, Direction = 1, MinWidth = 0f, Animate = s_drawerPresence,
                        // The rail lands on the ROW'S ARTWORK CENTRE: indent = ArtCentreIndent − the rail's own offset.
                        Padding = new Edges4(RowMetrics.DrawerIndent(shape.Set, shape.Art), 0f, Spacing.L, Spacing.S),
                        Children = [body with { Key = "drawer-body:" + rowKey }],
                    },
                ],
            };
        }

        /// <summary>What one row paints, as one equality-gated value: a recycle recomputes it once and re-renders the row
        /// only when its visual state really changed (a title landing is the row's Version; a like, the Liked edge).</summary>
        internal readonly record struct RowPresentation(
            Track Track, int Display, RowState State, uint TrackVersion, uint AlbumVersion, ulong ArtistStamp,
            int AddedAt, User AddedBy, uint UserVersion, byte Chart, bool Marquee, bool ShowTrackArtist,
            bool ShowAlbumColumn, bool Open);

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
            var by = orig >= 0 ? src.AddedBy(orig) : default;
            if (by.IsValid) _ = Entities.Current.Users.Changed.Value;
            return new RowPresentation(t, display, state,
                t.IsValid ? t.Version : 0u, t.Album.IsValid ? t.Album.Version : 0u, artists,
                orig >= 0 ? src.AddedAt(orig) : 0, by, by.IsValid ? by.Version : 0u,
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
        void ReDeal(int tier, float rowH)
        {
            if (_lastDealtTier == tier) return;
            int previous = _lastDealtTier;
            _lastDealtTier = tier;
            long now = Environment.TickCount64;
            bool reversal = now - _lastDealtAtMs < ReDealReversalMs;
            _lastDealtAtMs = now;
            if (_dealtThisFrame) return;
            if (previous < 0 || reversal || Design.Reduced || rowH <= 0f)
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
            int display = _scope.Index.Value - _start;
            var kind = _host.RowKindAt(display);

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
            var thost = _host;
            var tscope = _scope;
            var titem = _trackItem!;
            int tstart = _start;
            Element content = Embed.Comp(() => new TableRowContent(thost, tscope, titem, tstart, hovered));
            Element row = _host.Skin(_scope, content, in shape, flow, isOpen, _start, hovered, _narrate) with { Key = "row" };
            if (!isOpen) return new BoxEl { Direction = 1, MinWidth = 0f, Children = [row] };
            var t = _trackItem!.Peek();
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children = [row, _host.DrawerBox(t, display, in shape, _scope.Index, _start)],
            };
        }
    }

    /// <summary>The live grid of one row, built ONCE per slot per shape (<c>Track.BoundGrid</c>): every per-item value is a
    /// bound Prop over this slot's equality-gated presentation memo, so a recycle rebinds and allocates no element. Re-renders
    /// only on the shape and the reveal edge. Before the ramp reaches it: a blank ShimmerRow of the identical extent (a GridEl —
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
            if (!real) return Track.ShimmerRow(in set, shape.Tracks, shape.RowH, RowMetrics.PadXFor(set.Tier)) with { Key = "row:shim" };

            _row ??= new Track.BoundRow(presentation, likePop, _hovered, _host.AccentRead, PlayCurrent, LikeCurrent, ToggleExpandCurrent);
            var row = _row;
            Element grid = Track.BoundGrid(row, in set, shape.Tracks, shape.RowH, shape.Art);
            // The tracklist EDGE answers with row ids first and the rows' own fields land in a later fetch, so a slot can be
            // valid while its row has no data. The table's reveal gate holds the whole region until the first page knows its
            // face (TableRules.RowsPending), so this branch is for the rows PAST that band — a later batch's rows, a row
            // scrolled to before its page arrived: they keep the placeholder of the same extent until their own data lands
            // (a real mount/unmount, rare). The rule is TableRules.RowHasData over TrackFields.Face, NOT the whole Identity
            // group: a disk-restored row withholds Artists until the credits edge re-lands, and a warm open must not shimmer
            // for a credit line that fills in place.
            Element body = Flow.Show(() => { var t = row.Presentation.Value.Track; return TableRules.RowHasData(t.IsValid, t.Knows(TrackFields.Face)); }, grid,
                Track.ShimmerRow(in set, shape.Tracks, shape.RowH, RowMetrics.PadXFor(set.Tier)));
            return new BoxEl
            {
                Key = "row:real", Direction = 0, Grow = 1f, Animate = _latch == 1 ? s_rampReveal : null, Children = [body],
            };
        }
    }

    /// <summary>One slot of the hero-system viewport: hero · chrome (persistent prefix) · rows · the facts footer · the
    /// empty/loading placeholder. Constructor values are shape-only.</summary>
    sealed class TableVerticalItem(TableHost host, RowScope scope, bool narrate) : Component
    {
        readonly TableHost _host = host;
        readonly RowScope _scope = scope;
        readonly bool _narrate = narrate;

        public override Element Render()
        {
            int i = _scope.Index.Value;
            int visible = _host.VisibleCountValue;
            bool facts = _host.FactsValue;
            int prefix = Detail.VerticalLayout.PrefixCount;
            switch (Detail.VerticalLayout.ItemRole(i, visible, facts))
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
                    return new BoxEl
                    {
                        Key = "vitem:row", Direction = 1,
                        Children = [Embed.Comp(() => new TableSlot(host, scope, prefix, narrate))],
                    };
                }
                case VerticalItemRole.Footer:
                    return _host.FooterItem();
                default:
                    return i == prefix ? _host.EmptyItem() : new BoxEl { Key = "vitem:blank" };
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
            int i = _scope.Index.Value;
            int visible = _host.VisibleCountValue;
            if (i < visible)
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
            Element? section = i == visible ? _host.RecommendationsElement() : null;
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
