// ── Entities/Queue.UI.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the queue's two core hosts (the rail panel's and the stage column's), the shared lane, the play verbs and the queue's menu verbs
//
// Role: UI (core hosts; the pictures are the structure's `Queue.Panel` and `Queue.StagePane`, themes plan T7d)
// Owner: Q
// Wave: 5
// Budget: 900 lines
// Spec: ch 21 §9.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE QUEUE IS A RAIL ARM AND A STAGE PANE, NEVER A DESTINATION (plan §4, no route key). `Queue.InstallUi()` fills the rail's
// seam — `Rail.QueueBody` (the whole panel) — plus the `ActionServices` Play / PlayNext / AddToQueue verbs, `Shell.OnPlayContext`,
// and the queue's `AppActions`; the stage's pane region mounts `Queue.StagePane()` itself. A context
// LOAD is never built here: Play and `OnPlayContext` hand the uri to the playback host's one path
// (`Playback.PlayContext`); this file only edits the rows that path wrote, and posts `Input.QueueChanged` after an edit.
//
//   RailPanelHost  reads → QueueProps · slots · lane  →  `Queue.Panel`      (pills · "Playing from" · now-playing card · lane · vacancy)
//   StagePaneHost  reads → QueueProps · slots · lane  →  `Queue.StagePane`  (title + Autoplay switch · scroller · lane | "Nothing up next")
//
// WHAT STAYS HERE is what decides and holds state: the reads and their subscriptions, the section split (`Shell.QueueModel`), the page
// counters, the realized slots, the reorder lane (`Reorderable`: the drag, the keyboard reorder, the foreign-drop rule, `QueueMovePlan`),
// the row's drag payload and context menu, and the verbs (`Shell.QueueVerbs`). WHAT MOVED to Classic (`Themes\Classic\Structure\Views\Queue`):
// every element — the rows (the surface row, the hairline row, the stage's glass rows and Next row), the headers, the pills, the
// now-playing card, "Show more", the empty states. A QUEUE ROW IS NOT `Track.Row` (ch 21 §9.3 #4): a builder over (row, QueueEdge) — heart,
// ✕, a drag payload that no playlist may copy, the autoplay dim, a section-named menu.
//
// EVERY SLOT IS A PLAIN KEYED CHILD (ch 21 §0 #6): no virtualization, rows Enter/Exit/Slide, visual pagination at 100
// rows per page. ONE `Reorderable` spans the lane, headers included; `QueueMovePlan` decides a drop and a cross-section
// drop is told, not silent. A row captures its item id and flat index, never a span (the span rule), and every verb
// re-locates it at invoke time.
//
// THE DIVIDER: rows after the reducer's cursor while playback is ours, after the NowPlaying row for a remote device's
// mirror (`Queue.Divider`). Each render subscribes the queue edge, the two row tables, the deck's current row/phase/owner
// and the settings epoch; the demand for row text is ONE `Entities.Ensure` over the whole queue per publication.

using System.Buffers;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Queue
{
    // ══ 0. THE INSTALL ════════════════════════════════════════════════════════════════════════════════════════════════

    // MOUNT POINT (stage B contract)
    /// <summary>Fill the rail's seam, the playback verbs, the play-a-context seam and the queue's menu verbs.
    /// Called once by the composition root, after <c>Actions.Registry.Build</c>. <c>ActionServices.StartRadio</c> is the
    /// host's radio path (G-251: the seed resolves to its radio playlist, which plays now or parks behind the current
    /// track) with its outcome raised as the toast through <see cref="RadioToast"/>.</summary>
    public static void InstallUi()
    {
        Rail.QueueBody = static () => PanelSlot();
        ActionServices s = Actions.Services;
        s.Play = static uri => Playback.PlayContext(uri.Id);
        s.PlayNext = static uri => QueueUri(uri, next: true, announce: false);
        s.AddToQueue = static uri => QueueUri(uri, next: false, announce: false);
        s.StartRadio = static uri => Playback.StartRadio(uri, RadioToast);
        Shell.OnPlayContext = static uri => Playback.PlayContext(uri.Id);
        RegisterActions();
    }

    /// <summary>The radio verb's outcome as 0.2.9's toast (G-251, <c>RadioLaunch</c>): "Radio started" with an "Open
    /// playlist" action that opens the radio playlist's page, or "Couldn't start radio" when the seed has no radio or the
    /// resolve was refused. Raised HERE, in the UI layer, off <see cref="Playback.RadioOutcome"/> — the host owns no
    /// navigation. Shared by the song/artist-radio menu rows and the artist hero's radio pill; a caller who knows a display
    /// name fills <see cref="Playback.RadioOutcome.Name"/>, else the page opens titled "Radio". UI thread.</summary>
    public static void RadioToast(Playback.RadioOutcome outcome)
    {
        if (!outcome.Started)
        {
            Notify.Say(Loc.Get(Strings.Menu.RadioUnavailable), InfoBarSeverity.Warning);
            return;
        }
        EntityId playlist = outcome.Playlist;
        string name = string.IsNullOrEmpty(outcome.Name) ? Loc.Get(Strings.Menu.Radio) : outcome.Name;
        Notify.Say(Loc.Get(Strings.Menu.RadioStarted), InfoBarSeverity.Success,
            actionLabel: playlist.IsEmpty ? null : Loc.Get(Strings.Menu.OpenRadioPlaylist),
            onAction: playlist.IsEmpty ? null : () => Shell.GoTo(Shell.For(new EntityUri(playlist), name)));
    }

    // ══ 1. SHARED READS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A remote device owns playback: the list is its mirror, and local edits would be overwritten.</summary>
    static bool Viewing => Shell.QueueModel.IsViewer();

    /// <summary>What a row paints (<see cref="QueueRowText"/>). A row whose title is not known is THIN (its seed face, or two bars),
    /// never a bare uri (ch 21 §7).</summary>
    static QueueRowText TextOf(EntityRef r)
    {
        if (r.Kind == EntityKind.Episode && new Episode(r.Slot) is { IsValid: true } e)
            return new QueueRowText(e.Uri.Text, e.Title, e.Show.IsValid ? e.Show.Title : "", Controls.ArtUrl(e.ImageId), e.DurationMs,
                               !e.Knows(EpisodeFields.Title), false);
        if (r.Kind != EntityKind.Track || new Track(r.Slot) is not { IsValid: true } t) return new QueueRowText("", "", "", null, 0, true, false);
        // A ruled-unavailable track (Spotify no longer resolves it for this account) is NOT a thin row: it says so.
        if (t.Unplayable())
            return new QueueRowText(t.Uri.Text, Loc.Get(Strings.Detail.TrackFacts.Unavailable), "", null, 0, false, false, Unavailable: true);
        // The FACTS come off the display row (a relinked id reads its canonical track); the uri stays this row's.
        Track d = t.ForDisplay;
        bool thin = !d.Knows(TrackFields.Title);
        return new QueueRowText(t.Uri.Text, thin ? "" : d.Title, Entities.Strings.Resolve(d.ArtistLineId), Controls.ArtUrl(d.ImageId),
                           d.DurationMs, thin, d.IsExplicit);
    }

    /// <summary>Ask the catalog for the context entity's name, so <see cref="ContextName"/> has one to answer with — as ONE
    /// span ask over (table, row, groups) (plan §3.5: the per-row callers became span asks; the tick's drain batches it
    /// with everything else the tick asked) — and open a playlist context's LIST through the one open rule at the queue's
    /// urgency (<see cref="ListOpenPolicy.Surface.Queue"/>: Prefetch, never holds — the queue's rows are the playback
    /// host's, and the list behind "Playing from" only has to be current by the time its page opens). Nothing else
    /// fetches the name: a restored or remote context is only ever named here.</summary>
    internal static void EnsureContext(EntityId context)
    {
        var scope = Entities.Current;
        if (context.IsEmpty || scope is null) return;
        Table table;
        uint groups;
        switch (context.Kind)
        {
            case EntityKind.Playlist: table = scope.Playlists; groups = (uint)PlaylistFields.Identity; break;
            case EntityKind.Album: table = scope.Albums; groups = (uint)AlbumFields.Title; break;
            case EntityKind.Artist: table = scope.Artists; groups = (uint)ArtistFields.Name; break;
            case EntityKind.Show: table = scope.Shows; groups = (uint)ShowFields.Title; break;
            default: return;
        }
        int slot = table.Slot(context);        // the factory's row for an unseen context, as before
        Entities.Ensure(table, new ReadOnlySpan<int>(in slot), groups);
        if (context.Kind == EntityKind.Playlist) ListOpen.Open(new Playlist(slot), ListOpenPolicy.Surface.Queue);
    }

    /// <summary>The queue's half of a playlist context's open revalidation (<see cref="ListOpen.Observe"/>): the queue never
    /// holds, but the record still settles — and a rolling context (a daylist) re-asks its header — when the model says how
    /// the revalidation ended, and the baseline is captured when the disk leg lands. Auto-tracked: the context signal,
    /// then the membership and playlist tables <see cref="ListOpen.Observe"/> subscribes (a playlist context only).</summary>
    static readonly Action s_observeContext = static () =>
    {
        EntityId context = Playback.ContextUri.Value;
        var scope = Entities.Current;
        if (scope is null || context.Kind != EntityKind.Playlist) return;
        if (scope.Playlists.TryGetSlot(context, out int slot)) ListOpen.Observe(slot);
    };

    /// <summary>The context's name, from the entity it names — never a "Playing from" with no name. Liked Songs answers
    /// at once. A <c>spotify:list:</c> context is named by what the owner's cluster called it (<paramref name="wire"/>:
    /// "FLEMMING Popular"), else by the artist it belongs to. Shared with the stage's queue skin (<c>Stage.UI.cs</c>).</summary>
    public static string? ContextName(EntityId context, in ContextWire wire)
    {
        if (context.Kind != EntityKind.List) return NameOf(context);
        return ListName(context, in wire) ?? NameOf(ContextTarget(context, in wire));
    }

    /// <summary>The noun for a context's KIND ("album", "playlist", "artist", "podcast", "search") — the fullscreen stage's
    /// "Playing from {kind}" caption. Anything that is not one of the four named kinds (a Liked Songs collection, a
    /// <c>spotify:list:</c> context, a search context) reads as "search". UI, not <c>Queue.cs</c>: it reads loc.</summary>
    public static string ContextKindLabel(EntityId context) => Loc.Get(context.Kind switch
    {
        EntityKind.Album => Strings.Stage.Kind.Album,
        EntityKind.Playlist => Strings.Stage.Kind.Playlist,
        EntityKind.Artist => Strings.Stage.Kind.Artist,
        EntityKind.Show => Strings.Stage.Kind.Show,
        _ => Strings.Stage.Kind.Search,
    });

    static string? NameOf(EntityId context)
    {
        if (context.Kind == EntityKind.Collection) return Loc.Get(Strings.Player.LikedSongs);
        if (Entities.Current is not { } scope) return null;
        switch (context.Kind)
        {
            case EntityKind.Playlist:
                _ = scope.Playlists.Changed.Value;
                return scope.Playlists.TryGetSlot(context, out int p) && new Playlist(p).Knows(PlaylistFields.Identity)
                    ? Entities.Strings.Resolve(new Playlist(p).TitleId) : null;
            case EntityKind.Album:
                _ = scope.Albums.Changed.Value;
                return scope.Albums.TryGetSlot(context, out int a) && new Album(a).Knows(AlbumFields.Title) ? new Album(a).Title : null;
            case EntityKind.Artist:
                _ = scope.Artists.Changed.Value;
                return scope.Artists.TryGetSlot(context, out int r) && new Artist(r).Knows(ArtistFields.Name) ? new Artist(r).Name : null;
            case EntityKind.Show:
                _ = scope.Shows.Changed.Value;
                return scope.Shows.TryGetSlot(context, out int s) && new Show(s).Knows(ShowFields.Title) ? new Show(s).Title : null;
            default: return null;
        }
    }

    /// <summary>The page's demand, once per queue publication: every row's list fields, in two batches.</summary>
    static void EnsureRows()
    {
        var packed = PackedRefs;
        if (packed.IsEmpty) return;
        int[] tracks = ArrayPool<int>.Shared.Rent(packed.Length), episodes = ArrayPool<int>.Shared.Rent(packed.Length);
        try
        {
            int nt = 0, ne = 0;
            for (int i = 0; i < packed.Length; i++)
            {
                var r = Unpack(packed[i]);
                if (r.Kind == EntityKind.Track) tracks[nt++] = r.Slot;
                else if (r.Kind == EntityKind.Episode) episodes[ne++] = r.Slot;
            }
            if (nt > 0) Entities.Ensure(Entities.Current.Tracks, tracks.AsSpan(0, nt), (uint)(TrackFields.Identity | TrackFields.Availability), FetchPriority.Visible);
            if (ne > 0) Entities.Ensure(Entities.Current.Episodes, episodes.AsSpan(0, ne), (uint)EpisodeFields.Row, FetchPriority.Visible);
        }
        finally { ArrayPool<int>.Shared.Return(tracks); ArrayPool<int>.Shared.Return(episodes); }
    }

    static string RowKey(string prefix, ulong itemId, int index) => itemId != 0 ? prefix + "i" + itemId : prefix + "e" + index;

    // ══ 2. THE SURFACE BOTH HOSTS SHARE: the reads, the lane, the drop ════════════════════════════════════════════════
    //
    // A host reads the live queue into ONE `QueueProps` per render (`Shell.QueueModel.Project`), realizes the slots (headers, rows and "Show
    // more" rows, by the page counters), points the reorder lane at them and mounts the structure's view with the props, the verbs and this
    // object as the lane (`IQueueLane`). The view draws; every rule of the drag, the drop, the page and the row's menu is here or in the verbs.

    abstract class QueueSurface : Component, IQueueLane
    {
        protected const int PageSize = 100;
        protected readonly Reorderable Reorder;
        /// <summary>This surface's page counters ("Show more") and the core queue verbs bound to them.</summary>
        protected readonly Shell.QueuePages Pages;
        protected readonly Shell.QueueVerbs Verbs;
        protected IOverlayService? MenuHost;
        protected bool Autoplay;
        protected EntityRef Current;
        protected EntityId ContextId;
        protected ContextWire Wire;
        /// <summary>This render's queue: the split, the counts, the listed rows.</summary>
        protected QueueProps Model = null!;

        QueueSlot[] _slots = new QueueSlot[64];
        int _slotCount;
        Func<QueueSlot, float>? _extentOf;

        /// <summary>The resting height of one row, which the lane's drag maths needs to be EXACT: the view says it through
        /// <see cref="SetMetrics"/> before it builds the lane.</summary>
        float _rowExtent;

        protected QueueSurface(Shell.QueuePages pages)
        {
            Pages = pages;
            Verbs = new Shell.QueueVerbs(pages);
            Reorder = new Reorderable(Drag.Resource)
            {
                ItemExtent = 0f,
                Spacing = 0f,
                DragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = FluentGpu.Controls.Drag.SourceDimOpacity },
                // Released outside the list commits nothing: no surface takes a queue row as a copy.
                RequireDropOnList = true,
                CanAcceptForeign = static p => Drag.Unwrap(p) is { CanCopyTracks: true },
                ForeignRefusalCaption = static p => Drag.Unwrap(p) is { } r
                    ? Loc.Get(r.FromQueue ? Strings.Drag.ReorderHint : r.Kind == DragKind.Artist ? Strings.Drag.CantAddArtist : Strings.Drag.NothingToAdd)
                    : null,
                ForeignCaption = static (_, _) => Loc.Get(Strings.Drag.AddToQueue),
            };
            Reorder.ExtentOf = ExtentAt;
            Reorder.ItemOf = PayloadAt;
            Reorder.OnReorder = CommitMove;
            Reorder.OnCrossCommit = CommitForeign;
        }

        bool Viewer => Model.Viewer;

        public int SlotCount => _slotCount;

        public QueueSlot SlotAt(int item) => _slots[item];

        int IQueueLane.PageSize => PageSize;

        public int Remaining(QueueSection section)
        {
            int total = Model.Counts.Of(section);
            return total - QueueSlots.Realized(total, Pages.Of(section).Peek(), PageSize);
        }

        public void SetMetrics(float rowExtent, Func<QueueSlot, float> extentOf)
        {
            _rowExtent = rowExtent;
            _extentOf = extentOf;
            Reorder.ItemExtent = rowExtent;
        }

        /// <summary>Every hook and signal a queue surface reads, in one fixed order, and this render's queue. The rail also tracks
        /// shuffle and repeat (its pills) and names the context ("Playing from"); the stage's column draws neither.</summary>
        protected void Prologue(bool rail)
        {
            var overlay = UseContext(Overlay.Service);
            MenuHost = Controls.IsNullOverlay(overlay) ? null : overlay;
            // The version restarts per scope, so the key carries the scope epoch too: a rebind re-asks for the rows.
            UseEffect(static () => EnsureRows(), DepKey.From(((long)Entities.Current.Epoch << 32) | Queue.Version));
            _ = Entities.Current.Edges.Queue.Changed.Value;
            _ = Entities.Current.Tracks.Changed.Value;
            _ = Entities.Current.Episodes.Changed.Value;
            _ = Playback.PhaseSignal.Value;
            Current = Playback.Current.Value;
            ContextId = Playback.ContextUri.Value;
            Wire = Playback.ContextLabel.Value;
            UseEffect(static () => EnsureContext(ContextTarget(Playback.ContextUri.Peek(), Playback.ContextLabel.Peek())),
                      DepKey.From(ContextId.GetHashCode() ^ (Wire.Referrer.GetHashCode() * 31)));
            UseEffect(s_observeContext);
            bool viewer = Playback.OwnerSignal.Value == Playback.Owner.Foreign;
            _ = Platform.SettingsChanged.Value;
            Autoplay = Shell.QueueModel.AutoplayEnabled();
            bool shuffle = rail ? Playback.Shuffle.Value : Playback.Shuffle.Peek();
            RepeatMode repeat = rail ? Playback.Repeat.Value : Playback.Repeat.Peek();
            QueuePlayingFrom? playingFrom = rail ? Shell.QueueModel.PlayingFromOf(ContextId, in Wire) : null;
            Model = Shell.QueueModel.Project(Queue.Rows, static i => Queue.RefAt(i), viewer ? -1 : Playback.Snap().Cursor.Index, viewer,
                                             Autoplay, shuffle, repeat, Current, playingFrom);
        }

        /// <summary>Realize this render's slots: the headers, the rows of each section up to its page, and the "Show more" rows.</summary>
        protected void BuildSlots(int shownQueue, int shownNextUp, int shownAutoplay)
        {
            var counts = Model.Counts;
            int cap = QueueSlots.Capacity(counts.Queue, counts.NextUp, counts.Autoplay);
            if (_slots.Length < cap) _slots = new QueueSlot[Math.Max(cap, _slots.Length * 2)];
            _slotCount = QueueSlots.Build(counts.Queue, shownQueue, counts.NextUp, shownNextUp, counts.Autoplay, shownAutoplay, Autoplay, headers: true, _slots);
        }

        /// <summary>Point the lane at THIS render's slots. A pointer lift whose gesture ended without telling the list (a
        /// push re-keyed the lifted row and freed its node) is dropped here, or the lane paints a row nobody put there.</summary>
        protected void ConfigureReorder()
        {
            if (Reorder.IsLifted && !Reorder.IsKeyboardLifted
                && !(FluentGpu.Hooks.InputHooks.Current.Default.GetDragState?.Invoke() ?? default).Active)
                Reorder.Core.Cancel();
            Reorder.Scene = Context.Scene;
            Reorder.RequestRender = Context.RequestRerender;
            Reorder.ItemCount = _slotCount;
        }

        /// <summary>The slot under the live projection at column position <paramref name="i"/>.</summary>
        public int ItemAt(int i)
        {
            int item = Viewer ? i : Reorder.ItemAt(i);
            return (uint)item < (uint)_slotCount ? item : i;
        }

        /// <summary>Wrap a row as a lane item (the wrapper must be a column, or the hover plate collapses to the text).</summary>
        public Element Lane(int item, Element row, string key)
            => Viewer ? row : (BoxEl)Reorder.Item(item, row, key: key) with { Direction = 1 };

        public Element List(Element body, string key) => (BoxEl)Reorder.List(body) with { Grow = 0f, Key = key };

        public DragSource? OwnDrag(ulong itemId, int index, EntityRef row)
            => QueueRowRules.RowDrags(Viewer) ? Drag.Source(() => PayloadFor(itemId, row)) : null;

        public Element Attach(BoxEl row, ulong itemId, int index, EntityRef rowRef)
        {
            bool viewer = Viewer;
            return MenuHost is { } host ? row.WithContextMenu(host, () => Verbs.RowMenu(itemId, index, rowRef, viewer)) : row;
        }

        public QueueRowText TextOf(EntityRef row) => Queue.TextOf(row);

        public string Clock(int durationMs) => Playback.TimeFormat.Clock(durationMs);

        public string RowKey(string prefix, ulong itemId, int index) => Queue.RowKey(prefix, itemId, index);

        float ExtentAt(int i) => (uint)i < (uint)_slotCount && _extentOf is { } extentOf ? extentOf(_slots[i]) : _rowExtent;

        object? PayloadAt(int i)
        {
            if ((uint)i >= (uint)_slotCount || !_slots[i].IsRow) return null;
            return Model.TryRow(_slots[i].Section, _slots[i].Pos, out var row) ? PayloadFor(row.ItemId, row.Ref) : null;
        }

        void CommitMove(int from, int to)
        {
            var plan = QueueMovePlan.For(new ReadOnlySpan<QueueSlot>(_slots, 0, _slotCount), from, to);
            if (plan.Kind == QueueMoveKind.Move) Verbs.Move(plan.Section, plan.FromPos, plan.ToPos);
            else if (plan.Kind == QueueMoveKind.Refused) Notify.Say(Loc.Get(Strings.Drag.CantMoveAcrossSections));
        }

        void CommitForeign(object? payload, int fromIndex, Reorderable? from, Reorderable to, int slot)
            => Verbs.DropInsert(payload, QueueMovePlan.InsertIndex(new ReadOnlySpan<QueueSlot>(_slots, 0, _slotCount), slot));
    }

    /// <summary>The queue row as a drag: its track travels for the chip, and <c>SourceQueueItemId</c> marks it a reorder
    /// that no playlist, tab or player-bar surface may deposit.</summary>
    static DragPayload? PayloadFor(ulong itemId, EntityRef row)
    {
        if (row.IsNone) return null;
        var text = TextOf(row);
        Track[]? tracks = row.Kind == EntityKind.Track ? [new Track(row.Slot)] : null;
        return new DragPayload(Drag.KindOf(row.Kind), text.Uri, text.Uri, text.Title, row, Tracks: tracks, ArtUrl: text.Art,
                               SourceQueueItemId: itemId);
    }

    // ══ 3. THE RAIL PANEL'S HOST (ch 21 W3-W5) ════════════════════════════════════════════════════════════════════════

    /// <summary>The panel as the rail mounts it (<c>Rail.QueueBody</c>). The rail's body slot (<c>Rail.GrowSlot</c>) is a ROW, so a bare component
    /// anchor in it keeps its CONTENT's natural width and cannot shrink (Shrink 0): the pills row — three toggles that
    /// neither wrap nor shrink — is wider than a narrow rail's inner width, the panel root measured to it, and the whole
    /// panel (the now-playing card, every row, their hearts and ✕) was laid out that much wider than the rail and cut off
    /// at its right edge. A ZStack that grows AND shrinks to the slot and hands its one child the whole slot pins the panel
    /// to the rail's width whatever its content wants; only the pills row still overflows, clipped by the panel root.</summary>
    public static Element PanelSlot() => new BoxEl
    {
        ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f,
        Children = [Embed.Comp(static () => new RailPanelHost())],
    };

    /// <summary>The Up next column as the stage's pane region mounts it (the <c>Queue.StagePane</c> view inside the stage's pane frame).</summary>
    public static Element StagePane() => Embed.Comp(static () => new StagePaneHost());

    /// <summary>The rail panel's host: this render's queue, the three sections' page counters, the lane, and the facts the panel
    /// reads for it ("Playing from" and where the now-playing row plays from). The picture is the structure's <c>Queue.Panel</c>.</summary>
    sealed class RailPanelHost : QueueSurface
    {
        public RailPanelHost() : base(new Shell.QueuePages()) { }

        public override Element Render()
        {
            Prologue(rail: true);
            UseEffect(() => Pages.Reset(), DepKey.From(ContextId.GetHashCode()));
            var counts = Model.Counts;
            BuildSlots(QueueSlots.Realized(counts.Queue, Pages.Of(QueueSection.Queue).Value, PageSize),
                       QueueSlots.Realized(counts.NextUp, Pages.Of(QueueSection.NextUp).Value, PageSize),
                       QueueSlots.Realized(counts.Autoplay, Pages.Of(QueueSection.Autoplay).Value, PageSize));
            ConfigureReorder();
            DumpRows();

            Action? openPlayingFrom = null;
            if (Model.PlayingFrom is { } playingFrom)
            {
                var route = Shell.For(new EntityUri(playingFrom.Target), playingFrom.Name);
                if (!route.IsNone) openPlayingFrom = () => Shell.GoTo(route);
            }
            Action? openNowPlaying = null;
            if (!Current.IsNone)
            {
                var target = NowPlayingTarget(Current);
                if (!target.IsNone) openNowPlaying = () => Shell.GoTo(target);
            }
            return ViewSlots.View(ViewKeys.QueuePanel, new QueuePanelProps
            {
                Queue = Model,
                Lane = this,
                Verbs = Verbs,
                Player = PlayerVerbs.Default,
                HairlineRows = Prefs.Appearance.TrackRowStyle() == 1,
                RepeatOn = Model.Repeat != RepeatMode.Off,
                RepeatTrack = Model.Repeat == RepeatMode.Track,
                OpenPlayingFrom = openPlayingFrom,
                OpenNowPlaying = openNowPlaying,
            });
        }

        string _lastDump = "";

        /// <summary>The always-on <c>queue.panel.rows</c> line (ch 21 §9.1 #21): what the panel ACTUALLY shows per section,
        /// with row keys, written only when it changes — diff it against the host's queue log to split a bad queue from a
        /// bad render.</summary>
        void DumpRows()
        {
            var sb = new System.Text.StringBuilder(96 + SlotCount * 24);
            sb.Append("card=").Append(Current.IsNone ? "-" : TextOf(Current).Uri)
              .Append(" queue=").Append(Model.Counts.Queue).Append(" nextUp=").Append(Model.Counts.NextUp)
              .Append(" autoplay=").Append(Autoplay ? Model.Counts.Autoplay.ToString() : "off").Append(" rows=[");
            for (int i = 0; i < SlotCount; i++)
            {
                var slot = SlotAt(i);
                if (!slot.IsRow) continue;
                if (!Model.TryRow(slot.Section, slot.Pos, out var row)) continue;
                if (sb[^1] != '[') sb.Append("; ");
                sb.Append(QueueSections.Tag(slot.Section)).Append(slot.Pos).Append(" key=").Append(RowKey("", row.ItemId, row.Index));
            }
            string dump = sb.Append(']').ToString();
            if (dump == _lastDump) return;
            _lastDump = dump;
            Log.Info("queue", "queue.panel.rows " + dump);
            // §2.5 step 4: tap the EXISTING "what changed" dump rather than inventing a render-observation layer.
            // The ambient UI-thread cause (§2.2) is the best cause id available at this seam without threading one
            // through the whole render path — an optimistic mutation and its later echo both re-render through here,
            // each tagged with whichever cause was ambient at the time, exactly the "captured TWICE per cause" shape
            // §2.5 describes.
            Capture.Point(CaptureKind.UiRerender, Capture.AmbientUiCauseId, a: dump);
        }

        /// <summary>Where the now-playing title navigates: the playing context's page when the context has one, else the
        /// track's album page, else nowhere (<see cref="Shell.Route.IsNone"/>).</summary>
        Shell.Route NowPlayingTarget(EntityRef current)
        {
            if (!ContextId.IsEmpty)
            {
                EntityId target = ContextTarget(ContextId, in Wire);
                var ctx = target.IsEmpty ? new Shell.Route(Shell.RouteKind.NotFound) : Shell.For(new EntityUri(target), ContextName(ContextId, in Wire) ?? "");
                if (!ctx.IsNone) return ctx;
            }
            if (current.Kind == EntityKind.Track)
            {
                var album = new Track(current.Slot).Album;
                if (album.IsValid && album.Uri.IsValid) return Shell.For(album.Uri, album.Title);
            }
            return new Shell.Route(Shell.RouteKind.NotFound);   // RouteKind's zero is Home: `default` would be a link home
        }
    }

    // ══ 4. THE STAGE QUEUE PANE'S HOST (ch 21 W12) ════════════════════════════════════════════════════════════════════

    /// <summary>The stage column's host: this render's queue, ONE page counter for the continuous list (<see cref="Shell.QueuePages"/>
    /// unified) and the lane. The title, the Autoplay switch, the rows and every metric (they scale by <c>Stage.Layout.QueueScale</c>) are
    /// the structure's <c>Queue.StagePane</c>.</summary>
    sealed class StagePaneHost : QueueSurface
    {
        public StagePaneHost() : base(new Shell.QueuePages(unified: true)) { }

        public override Element Render()
        {
            Prologue(rail: false);
            UseEffect(() => Pages.Reset(), DepKey.From(ContextId.GetHashCode()));
            int pages = Pages.Of(QueueSection.Queue).Value;
            var counts = Model.Counts;
            BuildSlots(QueueSlots.Realized(counts.Queue, pages, PageSize), QueueSlots.Realized(counts.NextUp, pages, PageSize),
                       QueueSlots.Realized(counts.Autoplay, pages, PageSize));
            ConfigureReorder();
            return ViewSlots.View(ViewKeys.QueueStagePane, new QueueStagePaneProps
            {
                Queue = Model,
                Lane = this,
                Verbs = Verbs,
            });
        }
    }

    // ══ 6. QUEUEING (G-027) — a context LOAD is the playback host's (`Playback.PlayContext`) ═══════════════════════════

    /// <summary>A selection of tracks played from a menu: the first through the host's one play path, the rest queued
    /// behind it (0.2.9 <c>TrackActions.Play</c>).</summary>
    static void PlayTracks(IReadOnlyList<Track> tracks)
    {
        EntityRef[] refs = RefsOf(tracks);
        if (refs.Length == 0) return;
        Playback.PlayContext(refs[0].Id);
        if (refs.Length > 1) QueueRefs(refs.AsSpan(1), next: false, announce: false);
    }

    static EntityRef[]? One(EntityId id) => Entities.Ref(id) is { IsNone: false } r ? [r] : null;

    /// <summary>A container's members from the tables, or null when the relation has not been answered.</summary>
    static EntityRef[]? LocalMembers(EntityId id)
    {
        var scope = Entities.Current;
        var e = scope.Edges;
        return id.Kind switch
        {
            EntityKind.Album => scope.Albums.TryGetSlot(id, out int a) ? Members(e.AlbumTracks, a, EntityKind.Track) : null,
            EntityKind.Playlist => scope.Playlists.TryGetSlot(id, out int p) ? Members(e.PlaylistTracks, p, EntityKind.Track) : null,
            EntityKind.Artist => scope.Artists.TryGetSlot(id, out int r) ? Members(e.ArtistPopular, r, EntityKind.Track) : null,
            EntityKind.Show => scope.Shows.TryGetSlot(id, out int s) ? Members(e.ShowEpisodes, s, EntityKind.Episode) : null,
            EntityKind.Collection => Members(e.Liked, scope.MeSlot, EntityKind.Track),
            _ => null,
        };
    }

    static EntityRef[]? Members<TEdge>(EdgeTable<TEdge> edge, int parent, EntityKind kind) where TEdge : unmanaged
    {
        if (parent <= Table.None || edge.State(parent) == EdgeState.Unknown) return null;
        var targets = edge.Targets(parent);
        var refs = new EntityRef[targets.Length];
        int n = 0;
        for (int i = 0; i < targets.Length; i++) if (targets[i] > Table.None) refs[n++] = new EntityRef(kind, targets[i]);
        return n == 0 ? null : n == refs.Length ? refs : refs[..n];
    }

    /// <summary><c>ActionServices.PlayNext</c> / <c>AddToQueue</c>: a playable goes in at once; a container's members when
    /// the tables have them. The player bar's drop toasts its own batch, so the seam is quiet.</summary>
    static void QueueUri(EntityUri uri, bool next, bool announce)
    {
        if (!uri.IsValid) return;
        EntityRef[]? refs = uri.IsPlayable ? One(uri.Id) : LocalMembers(uri.Id);
        if (refs is null) { Notify.Say(Loc.Get(Strings.Detail.QueueUnavailable), InfoBarSeverity.Warning); return; }
        QueueRefs(refs, next, announce);
    }

    static void QueueRefs(ReadOnlySpan<EntityRef> refs, bool next, bool announce)
    {
        if (Viewing)
        {
            // G-248: another device plays — forward to its queue through Connect.
            int forwarded = Shell.PlayerBarRules.DropInsertCount(refs.Length);
            if (!Playback.QueueToOwner(refs[..forwarded], next))
            { Notify.Say(Loc.Get(Strings.Detail.QueueUnavailable), InfoBarSeverity.Warning); return; }
            if (announce) Notify.Say(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(forwarded)), InfoBarSeverity.Success);
            return;
        }
        int cap = Shell.PlayerBarRules.DropInsertCount(refs.Length);
        int n = InsertUser(refs[..cap], Playback.Snap().Cursor, next ? 0 : int.MaxValue);
        if (n == 0) return;
        Shell.QueueVerbs.Changed();
        if (announce)
            Notify.Say(refs.Length > cap ? Strings.Detail.AddedFirstToQueue(Strings.Detail.SongCount(n))
                                         : Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), InfoBarSeverity.Success);
    }

    // ══ 7. THE QUEUE'S MENU VERBS (G-027) ═════════════════════════════════════════════════════════════════════════════
    //
    // First registration per ActionId wins (`AppActions.Register`), so a later owner registering the same id is a no-op,
    // never a duplicate row. The radio verbs are NOT registered: `ActionServices.StartRadio` has no host path yet.

    static void RegisterActions()
    {
        AppActions.Register(new AppAction
        {
            Id = ActionId.Play, IconKey = ActionIcons.Play,
            Label = static _ => Loc.Get(Strings.Detail.Play),
            IsEnabled = static c => c.Target.Count > 0,
            Execute = static c => PlayTracks(c.Target.Tracks),
        });
        AppActions.Register(new AppAction
        {
            Id = ActionId.PlayNext, IconKey = ActionIcons.PlayNext,
            Label = static c => c.Target.Count > 1 ? Strings.Menu.PlayNextN(c.Target.Count) : Loc.Get(Strings.Detail.PlayNext),
            IsEnabled = static c => c.Target.Count > 0,
            Execute = static c => QueueRefs(RefsOf(c.Target.Tracks), next: true, announce: true),
        });
        AppActions.Register(new AppAction
        {
            Id = ActionId.AddToQueue, IconKey = ActionIcons.Queue,
            Label = static c => c.Target.Count > 1 ? Strings.Menu.AddToQueueN(c.Target.Count) : Loc.Get(Strings.Detail.PlayAfter),
            IsEnabled = static c => c.Target.Count > 0,
            Execute = static c => QueueRefs(RefsOf(c.Target.Tracks), next: false, announce: true),
        });
        AppActions.Register(new AppAction
        {
            Id = ActionId.RemoveFromQueue, IconKey = ActionIcons.Remove, Destructive = true,
            Label = static _ => Loc.Get(Strings.Menu.RemoveFromQueue),
            IsEnabled = static c => c.Target.Kind == TargetKind.QueueEntry && c.Target.QueueItemId != 0 && !Viewing,
            Execute = static c => Shell.QueueVerbs.Default.Remove((ulong)c.Target.QueueItemId, -1, default),
        });
        AppActions.Register(new AppAction
        {
            Id = ActionId.PlayContext, IconKey = ActionIcons.Play,
            Label = static _ => Loc.Get(Strings.Detail.Play),
            IsEnabled = static c => c.Target.Uri.IsValid,
            Execute = static c => Playback.PlayContext(c.Target.Uri.Id),
        });
        AppActions.Register(new AppAction
        {
            Id = ActionId.PlayContextNext, IconKey = ActionIcons.PlayNext,
            Label = static _ => Loc.Get(Strings.Detail.PlayNext),
            IsEnabled = static c => c.Target.Uri.IsValid && LocalMembers(c.Target.Uri.Id) is not null,
            Execute = static c => QueueContext(c.Target.Uri, next: true),
        });
        AppActions.Register(new AppAction
        {
            Id = ActionId.AddContextToQueue, IconKey = ActionIcons.Queue,
            Label = static _ => Loc.Get(Strings.Detail.PlayAfter),
            IsEnabled = static c => c.Target.Uri.IsValid && LocalMembers(c.Target.Uri.Id) is not null,
            Execute = static c => QueueContext(c.Target.Uri, next: false),
        });
    }

    static void QueueContext(EntityUri uri, bool next)
    {
        if (LocalMembers(uri.Id) is { } refs) QueueRefs(refs, next, announce: true);
        else Notify.Say(Loc.Get(Strings.Drag.NothingToAdd), InfoBarSeverity.Warning);
    }

    static EntityRef[] RefsOf(IReadOnlyList<Track> tracks)
    {
        var refs = new EntityRef[tracks.Count];
        int n = 0;
        for (int i = 0; i < tracks.Count; i++) if (tracks[i].IsValid) refs[n++] = new EntityRef(EntityKind.Track, tracks[i].Slot);
        return n == refs.Length ? refs : refs[..n];
    }
}

/// <summary>The queue row's pure decisions the core owns, engine-free so a fact pins each one: which row has a ✕, which row drags on
/// its own, and whether a row has a "…" button. The row's height under each skin, the autoplay dim, which adapter feeds a row and where
/// a click on the playing card goes are the structure's (`ClassicQueueRules`).</summary>
public static class QueueRowRules
{
    /// <summary>A row has a ✕ when the queue is ours to edit and the entry has an item id to name it by.</summary>
    public static bool Removable(bool viewer, ulong itemId) => !viewer && itemId != 0;

    /// <summary>A row carries a drag of its own only when no reorder lane does: the lane's wrapper is the drag source of
    /// every row of a queue we own, and a viewed queue has no lane.</summary>
    public static bool RowDrags(bool viewer) => viewer;

    /// <summary>Does a queue surface grow the hover "…" button? Never — the rail queue's rows (both skins), its
    /// now-playing card and the NPV's "Next up" rows are right-click only (the Menu key, Shift+F10 and the touch swipe open
    /// the same entry menu). The surface's "…" overlays the row's END, which here is the ✕ / the heart: on hover it hid and
    /// blocked them (owner, 2026-10-02 — reverses interaction-consistency D4). Fed to the surface as
    /// <c>CardData.ShowMenu</c>, which keeps the attached menu and drops only the button.</summary>
    public static bool ShowsMenuButton => false;
}
