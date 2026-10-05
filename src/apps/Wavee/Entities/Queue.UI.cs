// ── Entities/Queue.UI.cs ───────────────────────────────────────────────────────────────────────────────────────────
// the rail panel + the stage pane, one shared row builder, two skins
//
// Role: UI
// Owner: Q
// Wave: 5
// Budget: 900 lines
// Spec: ch 21 §9.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE QUEUE IS A RAIL ARM AND A STAGE PANE, NEVER A DESTINATION (plan §4, no route key). `Queue.InstallUi()` fills K's
// two seams — `Rail.QueueBody` (the whole panel: pills, "Playing from", the now-playing card, the lane) and
// `Stage.QueuePaneBody` (the rows under K's stage skin, which owns the header, the ∞ row and the scroller) — plus the
// `ActionServices` Play / PlayNext / AddToQueue verbs, `Shell.OnPlayContext`, and the queue's `AppActions`. A context
// LOAD is never built here: Play and `OnPlayContext` hand the uri to the playback host's one path
// (`Playback.PlayContext`); this file only edits the rows that path wrote, and posts `Input.QueueChanged` after an edit.
//
//   RailPanel  Pills · ScrollEl[ PlayingFrom · NowPlayingCard · lane[ headers · rows · Show more ] · vacancy ]
//   StagePane  lane[ rows · Show more ] | "Nothing up next"          (no captions, one page counter, grip + duration)
//
// A QUEUE ROW IS NOT `Track.Row` (ch 21 §9.3 #4): it is a builder over (row ref, QueueEdge) — heart, ✕, a drag payload that
// no playlist may copy, the autoplay dim, a section-named menu. The rail's Modern rows and its now-playing card are the
// shared media surface (`Controls.Surface` over `Track.RowData` / `Episode.RowData`: the plate, the hand, the play FAB,
// the pill and the right-click menu are the surface's — no "…" button, `QueueRowRules.ShowsMenuButton`; a row whose title
// is not known yet is its seed face) with the heart and ✕ as its trailing cluster; `QueueRowRules` holds the decisions.
// The Classic skin (hairline rows, no art, one folded line) and the stage pane's glass rows stay their own trees — the
// surface has no hairline or on-media plate.
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
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

using Ink = Wavee.Design.StageInk;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Queue
{
    // ══ 0. THE INSTALL ════════════════════════════════════════════════════════════════════════════════════════════════

    // MOUNT POINT (stage B contract)
    /// <summary>Fill the rail and stage seams, the playback verbs, the play-a-context seam and the queue's menu verbs.
    /// Called once by the composition root, after <c>Actions.Registry.Build</c>. <c>ActionServices.StartRadio</c> is the
    /// host's radio path (G-251: the seed resolves to its radio playlist, which plays now or parks behind the current
    /// track) with its outcome raised as the toast through <see cref="RadioToast"/>.</summary>
    public static void InstallUi()
    {
        Rail.QueueBody = static () => PanelSlot();
        Stage.QueuePaneBody = static () => Embed.Comp(static () => new StagePane());
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
    static bool Viewing => Playback.OwnerSignal.Peek() == Playback.Owner.Foreign;

    /// <summary><see cref="Unavailable"/>: a ruled-unavailable track — the row says so (no artists, no cover) instead of
    /// painting what the table still holds of it.</summary>
    readonly record struct RowText(string Uri, string Title, string Artists, string? Art, int DurationMs, bool Thin, bool Explicit,
                                   bool Unavailable = false);

    /// <summary>What a row paints. A row whose title is not known is THIN (its seed face, or two bars), never a bare uri
    /// (ch 21 §7).</summary>
    static RowText TextOf(EntityRef r)
    {
        if (r.Kind == EntityKind.Episode && new Episode(r.Slot) is { IsValid: true } e)
            return new RowText(e.Uri.Text, e.Title, e.Show.IsValid ? e.Show.Title : "", Controls.ArtUrl(e.ImageId), e.DurationMs,
                               !e.Knows(EpisodeFields.Title), false);
        if (r.Kind != EntityKind.Track || new Track(r.Slot) is not { IsValid: true } t) return new RowText("", "", "", null, 0, true, false);
        // A ruled-unavailable track (Spotify no longer resolves it for this account) is NOT a thin row: it says so.
        if (t.Unplayable())
            return new RowText(t.Uri.Text, Loc.Get(Strings.Detail.TrackFacts.Unavailable), "", null, 0, false, false, Unavailable: true);
        // The FACTS come off the display row (a relinked id reads its canonical track); the uri stays this row's.
        Track d = t.ForDisplay;
        bool thin = !d.Knows(TrackFields.Title);
        return new RowText(t.Uri.Text, thin ? "" : d.Title, Entities.Strings.Resolve(d.ArtistLineId), Controls.ArtUrl(d.ImageId),
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

    static string Tag(QueueSection section) => section switch { QueueSection.Queue => "q", QueueSection.NextUp => "u", _ => "a" };

    static string RowKey(string prefix, ulong itemId, int index) => itemId != 0 ? prefix + "i" + itemId : prefix + "e" + index;

    /// <summary>One render's split: flat indices per section and the reorder slots, in arrays the component keeps.</summary>
    sealed class View
    {
        public int[] Index = new int[64];
        public QueueSlot[] Slots = new QueueSlot[64];
        public int User, Next, Auto, SlotCount;

        public void Read(ReadOnlySpan<QueueEdge> rows, int divider)
        {
            if (Index.Length < rows.Length) Index = new int[Math.Max(rows.Length, Index.Length * 2)];
            Split(rows, divider, Index, out User, out Next, out Auto);
        }

        public void Build(int shownUser, int shownNext, int shownAuto, bool autoplay, bool headers)
        {
            int cap = QueueSlots.Capacity(User, Next, Auto);
            if (Slots.Length < cap) Slots = new QueueSlot[Math.Max(cap, Slots.Length * 2)];
            SlotCount = QueueSlots.Build(User, shownUser, Next, shownNext, Auto, shownAuto, autoplay, headers, Slots);
        }

        public ReadOnlySpan<QueueSlot> Live => Slots.AsSpan(0, SlotCount);

        public int CountOf(QueueSection s) => s switch { QueueSection.Queue => User, QueueSection.NextUp => Next, _ => Auto };

        public int FlatIndex(QueueSection s, int pos)
        {
            int start = s switch { QueueSection.Queue => 0, QueueSection.NextUp => User, _ => User + Next };
            return (uint)pos < (uint)CountOf(s) ? Index[start + pos] : -1;
        }
    }

    // ══ 2. THE SURFACE BOTH SKINS SHARE: the reads, the lane, the drop ════════════════════════════════════════════════

    abstract class QueueSurface : Component
    {
        protected const int PageSize = 100;
        protected readonly View View = new();
        protected readonly Reorderable Reorder;
        protected IOverlayService? MenuHost;
        protected bool Viewer, Autoplay;
        protected EntityRef Current;
        protected EntityId ContextId;
        protected ContextWire Wire;

        /// <summary>The resting height of one row, which the lane's drag maths needs to be EXACT: a skin that changes the
        /// row's height says so through <see cref="SetRowExtent"/> before it configures the lane.</summary>
        protected float RowExtent { get; private set; }

        protected QueueSurface(float rowExtent)
        {
            RowExtent = rowExtent;
            Reorder = new Reorderable(Drag.Resource)
            {
                ItemExtent = rowExtent,
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

        protected abstract float ExtentOf(QueueSlot slot);

        protected void SetRowExtent(float extent)
        {
            RowExtent = extent;
            Reorder.ItemExtent = extent;
        }

        /// <summary>Every hook and signal a queue surface reads, in one fixed order, and this render's split.</summary>
        protected ReadOnlySpan<QueueEdge> Prologue()
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
            Viewer = Playback.OwnerSignal.Value == Playback.Owner.Foreign;
            _ = Platform.SettingsChanged.Value;
            Autoplay = Platform.Settings.Get(Platform.Keys.AutoplayEnabled);
            var rows = Queue.Rows;
            View.Read(rows, Divider(rows, Viewer ? -1 : Playback.Snap().Cursor.Index));
            return rows;
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
            Reorder.ItemCount = View.SlotCount;
        }

        /// <summary>The slot under the live projection at column position <paramref name="i"/>.</summary>
        protected int ItemAt(int i)
        {
            int item = Viewer ? i : Reorder.ItemAt(i);
            return (uint)item < (uint)View.SlotCount ? item : i;
        }

        /// <summary>Wrap a row as a lane item (the wrapper must be a column, or the hover plate collapses to the text).</summary>
        protected Element Lane(int item, Element row, string key)
            => Viewer ? row : (BoxEl)Reorder.Item(item, row, key: key) with { Direction = 1 };

        protected static DragSource? OwnDrag(bool viewer, ulong itemId, int index, EntityRef row)
            => QueueRowRules.RowDrags(viewer) ? Drag.Source(() => PayloadFor(itemId, index, row)) : null;

        protected Element Attach(BoxEl row, ulong itemId, int index, EntityRef r)
        {
            bool viewer = Viewer;
            return MenuHost is { } host ? row.WithContextMenu(host, () => RowMenu(itemId, index, r, viewer)) : row;
        }

        float ExtentAt(int i) => (uint)i < (uint)View.SlotCount ? ExtentOf(View.Slots[i]) : RowExtent;

        object? PayloadAt(int i)
        {
            if ((uint)i >= (uint)View.SlotCount || !View.Slots[i].IsRow) return null;
            int index = View.FlatIndex(View.Slots[i].Section, View.Slots[i].Pos);
            return index < 0 ? null : PayloadFor(Queue.Rows[index].ItemId, index, RefAt(index));
        }

        void CommitMove(int from, int to)
        {
            var plan = QueueMovePlan.For(View.Live, from, to);
            if (plan.Kind == QueueMoveKind.Move) MoveRow(plan.Section, plan.FromPos, plan.ToPos);
            else if (plan.Kind == QueueMoveKind.Refused) Notify.Say(Loc.Get(Strings.Drag.CantMoveAcrossSections));
        }

        void CommitForeign(object? payload, int fromIndex, Reorderable? from, Reorderable to, int slot)
            => InsertPayload(payload, QueueMovePlan.InsertIndex(View.Live, slot));
    }

    /// <summary>The queue row as a drag: its track travels for the chip, and <c>SourceQueueItemId</c> marks it a reorder
    /// that no playlist, tab or player-bar surface may deposit.</summary>
    static DragPayload? PayloadFor(ulong itemId, int index, EntityRef row)
    {
        if (row.IsNone) return null;
        var text = TextOf(row);
        Track[]? tracks = row.Kind == EntityKind.Track ? [new Track(row.Slot)] : null;
        return new DragPayload(Drag.KindOf(row.Kind), text.Uri, text.Uri, text.Title, row, Tracks: tracks, ArtUrl: text.Art,
                               SourceQueueItemId: itemId);
    }

    // ══ 3. THE RAIL PANEL (ch 21 W3-W5) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>The panel as the rail mounts it. The rail's body slot (<c>Rail.GrowSlot</c>) is a ROW, so a bare component
    /// anchor in it keeps its CONTENT's natural width and cannot shrink (Shrink 0): the pills row — three toggles that
    /// neither wrap nor shrink — is wider than a narrow rail's inner width, the panel root measured to it, and the whole
    /// panel (the now-playing card, every row, their hearts and ✕) was laid out that much wider than the rail and cut off
    /// at its right edge. A ZStack that grows AND shrinks to the slot and hands its one child the whole slot pins the panel
    /// to the rail's width whatever its content wants; only the pills row still overflows, clipped by the panel root.</summary>
    static Element PanelSlot() => new BoxEl
    {
        ZStack = true, Grow = 1f, Shrink = 1f, MinWidth = 0f,
        Children = [Embed.Comp(static () => new RailPanel())],
    };

    sealed class RailPanel : QueueSurface
    {
        const float QueueArt = 34f, NowArt = 44f, ClassicExtent = QueueRowRules.ClassicExtent, HeaderRowH = 20f;
        const float HeaderExtent = Spacing.M + HeaderRowH + Spacing.XS;           // 36
        const float HeaderSubExtent = HeaderExtent + Spacing.XXS + 16f;           // 54, the Autoplay header's hint line
        const float MoreRowH = 40f, MoreExtent = MoreRowH + Spacing.XS + Spacing.XXS;

        readonly Signal<int> _userPages = new(1), _nextPages = new(1), _autoPages = new(1);
        readonly SwipeGroup _swipe = new();
        /// <summary>The panel viewport's scroll handle: any scroll while a row's swipe is open closes it.</summary>
        readonly ScrollHandle _scroll = new();
        readonly Action _closeSwipeOnScroll;

        // The chip accent: resolved per render through the shared ladder, written to a signal by an effect (never during
        // render) — the mode toggles (Workstream B: ToggleButton.Controlled on Controls.AccentToggleStyle) read it
        // directly each render.
        ColorF? _queueHold;
        ColorF _chipPending = Tok.AccentDefault;
        readonly Signal<ColorF> _chipAccent = new(Tok.AccentDefault);
        readonly Action _pushChip;

        public RailPanel() : base(QueueRowRules.Extent(classic: false, QueueArt))
        {
            _pushChip = () => _chipAccent.Value = _chipPending;
            _closeSwipeOnScroll = () =>
            {
                _ = _scroll.Offset.Value;   // re-runs on every offset the handle publishes
                if (_swipe.AnyOpen) _swipe.Close();
            };
        }

        /// <summary>The now-playing cover's graded chrome accent when the plane has it, the album's wire accent until then,
        /// the last remembered colour while a cover is still grading, the semantic accent otherwise.</summary>
        void ResolveChipAccent(string cover)
        {
            ColorF? graded = Design.ChromeSchemeFor(cover) is { } scheme ? Design.Palette.ChromeAccent(scheme) : null;
            uint payload = Current.Kind == EntityKind.Track && new Track(Current.Slot) is { IsValid: true } t && t.Album is { IsValid: true } album
                ? album.Accent : 0u;
            var r = AccentLadder.Resolve(new AccentLadder.Input(graded, payload, Definite: cover.Length == 0), _queueHold, Tok.AccentDefault,
                                         static a => Design.Palette.ChromeFromPayload(a));
            if (r.Remember) _queueHold = r.Color;
            _chipPending = r.Color;
            UseEffect(_pushChip, DepKey.From(r.Color.GetHashCode()));
        }

        protected override float ExtentOf(QueueSlot slot) => slot.Kind switch
        {
            QueueSlotKind.Header => slot.Section == QueueSection.Autoplay ? HeaderSubExtent : HeaderExtent,
            QueueSlotKind.More => MoreExtent,
            _ => RowExtent,                 // the live skin's: SetRowExtent, each render
        };

        Signal<int> PagesOf(QueueSection s) => s switch { QueueSection.Queue => _userPages, QueueSection.NextUp => _nextPages, _ => _autoPages };

        public override Element Render()
        {
            var rows = Prologue();
            UseEffect(() => { _userPages.Value = 1; _nextPages.Value = 1; _autoPages.Value = 1; }, DepKey.From(ContextId.GetHashCode()));
            UseSignalEffect(_closeSwipeOnScroll);
            bool classic = Prefs.Appearance.TrackRowStyle() == 1;
            bool art = !classic && !Prefs.Appearance.TrackArtworkHidden();
            SetRowExtent(QueueRowRules.Extent(classic, QueueArt));
            bool shuffle = Playback.Shuffle.Value;
            RepeatMode repeat = Playback.Repeat.Value;
            string cover = Current.IsNone ? "" : TextOf(Current).Art ?? "";
            if (cover.Length > 0) _ = Palette.Watch(cover).Value;
            ResolveChipAccent(cover);

            View.Build(QueueSlots.Realized(View.User, _userPages.Value, PageSize), QueueSlots.Realized(View.Next, _nextPages.Value, PageSize),
                       QueueSlots.Realized(View.Auto, _autoPages.Value, PageSize), Autoplay, headers: true);
            ConfigureReorder();
            DumpRows(rows);

            var content = new List<Element>(4);
            if (ContextName(ContextId, in Wire) is { Length: > 0 } source) content.Add(PlayingFrom(source, ContextTarget(ContextId, in Wire)));
            if (!Current.IsNone) content.Add(NowPlayingCard(Current, classic));
            if (View.SlotCount > 0) content.Add((BoxEl)Reorder.List(Upcoming(rows, art, classic)) with { Grow = 0f, Key = "lane:upcoming" });
            if (Current.IsNone && View.User == 0 && View.Next == 0)
                content.Add(Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                                             title: Loc.Get(Strings.Player.NothingPlaying), subtitle: ""));

            Element body = new BoxEl { Direction = 1, MinHeight = 0f, Padding = new Edges4(0f, 0f, 0f, 14f), Children = content.ToArray() };
            // Nothing upcoming: the same lane wraps the body, so a foreign drop still has a target (slot 0 = play next).
            if (View.SlotCount == 0) body = (BoxEl)Reorder.List(body) with { Grow = 0f, Key = "lane:empty" };

            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Padding = new Edges4(14f, 4f, 14f, 0f),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Wrap = true, Gap = 8f, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, 4f, 0f, 10f),
                        Children =
                        [
                            Pill(Icons.Shuffle, Loc.Get(Strings.Player.Shuffle), shuffle, static () => Shell.ToggleShuffle()),
                            Pill(repeat == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll, Loc.Get(Strings.Player.Repeat),
                                 repeat != RepeatMode.Off, static () => Shell.CycleRepeat()),
                            Pill("∞", Loc.Get(Strings.Player.Autoplay), Autoplay, static () => ToggleAutoplay(), glyphIsText: true),
                        ],
                    },
                    new ScrollEl
                    {
                        Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "queuepanel", Handle = _scroll,
                        Content = body,
                    },
                ],
            };
        }

        string _lastDump = "";

        /// <summary>The always-on <c>queue.panel.rows</c> line (ch 21 §9.1 #21): what the panel ACTUALLY shows per section,
        /// with row keys, written only when it changes — diff it against the host's queue log to split a bad queue from a
        /// bad render.</summary>
        void DumpRows(ReadOnlySpan<QueueEdge> rows)
        {
            var sb = new System.Text.StringBuilder(96 + View.SlotCount * 24);
            sb.Append("card=").Append(Current.IsNone ? "-" : TextOf(Current).Uri)
              .Append(" queue=").Append(View.User).Append(" nextUp=").Append(View.Next)
              .Append(" autoplay=").Append(Autoplay ? View.Auto.ToString() : "off").Append(" rows=[");
            for (int i = 0; i < View.SlotCount; i++)
            {
                var slot = View.Slots[i];
                if (!slot.IsRow) continue;
                int index = View.FlatIndex(slot.Section, slot.Pos);
                if (index < 0) continue;
                if (sb[^1] != '[') sb.Append("; ");
                sb.Append(Tag(slot.Section)).Append(slot.Pos).Append(" key=").Append(RowKey("", rows[index].ItemId, index));
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

        Element Upcoming(ReadOnlySpan<QueueEdge> rows, bool art, bool classic)
        {
            var kids = new Element[View.SlotCount];
            for (int i = 0; i < kids.Length; i++)
            {
                int item = ItemAt(i);
                var slot = View.Slots[item];
                if (slot.Kind == QueueSlotKind.Header) { kids[i] = SectionHeader(slot.Section); continue; }
                if (slot.Kind == QueueSlotKind.More) { kids[i] = ShowMore(slot.Section); continue; }
                int index = View.FlatIndex(slot.Section, slot.Pos);
                kids[i] = index < 0 ? new BoxEl() : Lane(item, Row(rows[index], index, slot.Section, art, classic), RowKey("", rows[index].ItemId, index));
            }
            return new BoxEl { Key = "upcoming", Direction = 1, Children = kids };
        }

        Element Row(QueueEdge edge, int index, QueueSection section, bool art, bool classic)
        {
            EntityRef r = RefAt(index);
            RowText text = TextOf(r);
            ulong itemId = edge.ItemId;
            bool removable = QueueRowRules.Removable(Viewer, itemId);
            Element el = classic ? ClassicRow(text, r, itemId, index, section, removable)
                                 : SurfaceRow(text, r, itemId, index, section, art, removable);
            if (!removable || !Controls.SwipeArmed) return el;
            var remove = new SwipeAction(ActionIcons.Resolve(ActionIcons.Remove), Loc.Get(Strings.Menu.RemoveFromQueue))
            {
                OnInvoked = () => RemoveRow(itemId, index, r),
            };
            SwipeSide? like = r.Kind == EntityKind.Track && AppActions.Find(ActionId.ToggleLike) is { } toggle
                ? SwipeSide.Of(toggle.ToSwipeAction(new ActionContext(ActionTarget.ForTracks([new Track(r.Slot)]), Actions.Services)))
                : null;
            return SwipeControl.Create(el, leading: like, trailing: SwipeSide.Of(remove), group: _swipe, touchOnly: true);
        }

        /// <summary>The Modern row: THE shared media surface (<c>Shape.Row</c>) over the row's own adapter — the plate, the
        /// hand, the play FAB, the pill and the right-click menu are the surface's (no "…" button,
        /// <see cref="QueueRowRules.ShowsMenuButton"/>); the heart and ✕ are its trailing cluster. A click (and the FAB) is
        /// a cursor move inside the live session (<see cref="SkipToRow"/>), and the lane's reorder gesture rides the wrapper
        /// above the row, so the surface carries a drag of its own only for a viewer, who has no lane. The wrapper here carries what the surface has no dial for: the key (a skin flip REMOUNTS), the
        /// autoplay dim and the Enter/Exit/Slide motion.</summary>
        Element SurfaceRow(RowText text, EntityRef r, ulong itemId, int index, QueueSection section, bool art, bool removable)
        {
            bool viewer = Viewer;
            Action skip = () => SkipToRow(itemId, index, r);
            var data = SurfaceData(r, text, art, click: skip, play: skip, draggable: QueueRowRules.RowDrags(viewer), queueItem: itemId,
                                   menu: () => RowMenu(itemId, index, r, viewer),
                                   trailing: RowTrailing(text, removable, () => RemoveRow(itemId, index, r)));
            var shape = Shape.Row(TrackRowRules.ArtEdge(QueueArt, art)) with { MinHeight = RowExtent };
            return new BoxEl
            {
                Key = RowKey("", itemId, index) + ":art=" + art,
                Direction = 1, Opacity = QueueRowRules.Dim(section),
                Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
                Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
                Layout = LayoutTransition.Slide,
                Children = [Controls.Surface(data, shape)],
            };
        }

        /// <summary>The Classic row: hairline, square, no art, title and artists folded into one line — a look the surface
        /// has no plate for, so it stays its own tree. Like the Modern row it has no "…" button
        /// (<see cref="QueueRowRules.ShowsMenuButton"/>): its menu is the right-click / Menu key (<c>Attach</c>).</summary>
        Element ClassicRow(RowText text, EntityRef r, ulong itemId, int index, QueueSection section, bool removable)
        {
            var body = new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = ClassicExtent,
                Padding = new Edges4(Spacing.S, 0f, Spacing.XS, 0f),
                Children =
                [
                    HeartLane(text, ClassicExtent),
                    ClassicIdentity(text, nowPlaying: false),
                    removable ? CloseGlyph(() => RemoveRow(itemId, index, r), classic: true) : new BoxEl { Width = Controls.IconButtonSize, Shrink = 0f },
                ],
            };
            var row = new BoxEl
            {
                Key = RowKey("", itemId, index) + ":classic",
                Draggable = OwnDrag(Viewer, itemId, index, r),
                ZStack = true, MinHeight = ClassicExtent, ClipToBounds = true,
                Corners = CornerRadius4.All(0f),
                Fill = ColorF.Transparent, HoverFill = Design.Colors.RowHover, PressedFill = Design.Colors.RowPressed,
                PressScale = Design.Motion.ScaleSubtle.Press,
                Opacity = QueueRowRules.Dim(section),
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                OnClick = () => SkipToRow(itemId, index, r),
                Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
                Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
                Layout = LayoutTransition.Slide,
                Children = [body, ClassicHairline()],
            };
            return Attach(row, itemId, index, r);
        }

        /// <summary>The playing card: Modern is the surface on the TILE plate (the opaque card fill and a hairline that turns
        /// accent while it plays) at a 44 art, its click the playing context's page — the context when it has one, else the
        /// track's album — and, with nowhere to go, NO click: the body is display-only and the FAB still toggles play/pause.
        /// The heart is its trailing; no "…" button (<see cref="QueueRowRules.ShowsMenuButton"/> — the right-click track
        /// menu stays), and it drags as the one track it is. Classic stays its hairline row.</summary>
        Element NowPlayingCard(EntityRef current, bool classic)
        {
            RowText text = TextOf(current);
            var target = NowPlayingTarget(current);
            if (classic) return ClassicNowPlaying(text, target);

            Action toggle = static () => Playback.TogglePlay("queue.nowplaying");
            Action? open = QueueRowRules.NowPlayingClickOf(!target.IsNone) == QueueRowRules.NowPlayingClick.OpenContext
                ? () => Shell.GoTo(target) : null;
            var data = SurfaceData(current, text, art: true, click: open, play: toggle, draggable: true, queueItem: null, menu: null,
                                   trailing: HeartLane(text));
            var shape = Shape.RowTile with { ArtEdge = NowArt };
            return new BoxEl
            {
                // Keyed by the track: a track change remounts the card with an Enter fade (the cross-fade a row cannot do).
                Key = "np:" + text.Uri + ":classic=False",
                Direction = 1, Margin = new Edges4(0f, 0f, 0f, 10f),
                Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
                Layout = LayoutTransition.Slide,
                Children = [Controls.Surface(data, shape)],
            };
        }

        Element ClassicNowPlaying(RowText text, Shell.Route target)
        {
            Element identity = ClassicIdentity(text, nowPlaying: true);
            // The title is a LINK to where the track plays from (0.2.9 parity, user report 2026-09-16): the context's page
            // when it has one (a playlist, an album, an artist, the liked songs), else the track's album — a station has no
            // page of its own, and the album is the nearest thing to "where this came from".
            if (!target.IsNone)
            {
                var route = target;
                identity = new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1, Justify = FlexJustify.Center,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                    Corners = Radii.ControlAll, HoverFill = Design.Colors.RowHover,
                    Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), Margin = new Edges4(-Spacing.XS, 0f, -Spacing.XS, 0f),
                    OnClick = () => Shell.GoTo(route),
                    Children = [identity],
                };
            }
            var body = new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = ClassicExtent,
                Padding = new Edges4(Spacing.S, 0f, Spacing.XS, 0f),
                Children =
                [
                    identity,
                    new BoxEl
                    {
                        Width = 30f, Height = ClassicExtent, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [Heart(text)],
                    },
                ],
            };
            return new BoxEl
            {
                Key = "np:" + text.Uri + ":classic=True",
                ZStack = true, MinHeight = ClassicExtent, ClipToBounds = true, Corners = CornerRadius4.All(0f),
                Fill = ColorF.Transparent,
                Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
                Layout = LayoutTransition.Slide,
                Children = [body, ClassicHairline()],
            };
        }

        /// <summary>The surface's data for one queue or now-playing row, through the entity's own adapter: a seed face while
        /// the row's title is unknown, <c>Episode.RowData</c> for an episode (the compact title + show row), else
        /// <c>Track.RowData</c> over the row the track DISPLAYS (a relink reads its canonical facts; the uri stays the
        /// queue's own, which the playback relation matches). A ruled-unavailable track keeps the row's own "Unavailable" in
        /// place of its title, artists and cover. <paramref name="menu"/> replaces a track's single-track menu with the
        /// queue-entry menu; <paramref name="queueItem"/> marks the drag a queue reorder no playlist may deposit.
        /// No row grows the surface's hover "…" (<see cref="QueueRowRules.ShowsMenuButton"/>): its overlay would land on
        /// the ✕ (or, on the card, the heart) and hide it. The menu stays on the right-click, the Menu key and the swipe.</summary>
        static Controls.CardData SurfaceData(EntityRef r, RowText text, bool art, Action? click, Action play, bool draggable,
                                             ulong? queueItem, Func<ContextMenuModel?>? menu, Element trailing)
        {
            switch (QueueRowRules.FaceOf(r.Kind, text.Thin))
            {
                case QueueRowRules.Face.Seed:
                    return Controls.CardData.Seed;
                case QueueRowRules.Face.Episode:
                {
                    var episode = new Episode(r.Slot);
                    var facts = Episode.RowFactsOf(episode);
                    if (!art) facts = facts with { Cover = null };
                    var data = Episode.RowData(episode, in facts,
                        new Episode.RowOptions(OnClick: click, OnPlay: play, ShowMeta: false, ShowGoToShow: true, Trailing: trailing))
                        with { ShowMenu = QueueRowRules.ShowsMenuButton };
                    // The episode adapter reads a null click as "open the episode page"; the caller's null is display-only.
                    return click is null ? data with { OnClick = null } : data;
                }
                default:
                {
                    var options = new Track.RowDataOptions(OnClick: click, OnPlay: play, ShowArtwork: art, ShowExplicit: false,
                                                           ShowVideo: true, Draggable: draggable,
                                                           ShowMenu: QueueRowRules.ShowsMenuButton,
                                                           QueueItemId: queueItem, Trailing: trailing);
                    var data = Track.RowData(new Track(r.Slot).ForDisplay, in options) with { Uri = text.Uri };
                    if (menu is not null) data = data with { Menu = menu };
                    return text.Unavailable ? data with { Title = text.Title, Subtitle = null, CoverUrl = null } : data;
                }
            }
        }

        /// <summary>The surface's trailing cluster: the heart, then the ✕ — or its width, so the hearts of removable and
        /// viewed rows line up.</summary>
        static Element RowTrailing(RowText text, bool removable, Action remove) => new BoxEl
        {
            Direction = 0, Gap = Spacing.XXS, AlignItems = FlexAlign.Center, Shrink = 0f,
            Children =
            [
                HeartLane(text),
                removable ? CloseGlyph(remove, classic: false) : new BoxEl { Width = Controls.IconButtonSize, Shrink = 0f },
            ],
        };

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

        Element SectionHeader(QueueSection section)
        {
            string title = Loc.Get(section switch
            {
                QueueSection.Queue => Strings.Player.NextInQueue,
                QueueSection.NextUp => Strings.Player.NextUp,
                _ => Strings.Player.Autoplay,
            });
            var top = new List<Element>(4)
            {
                Design.Type.Eyebrow(title) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new TextEl(View.CountOf(section).ToString()) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextTertiary },
                new BoxEl { Grow = 1f, MinWidth = 0f },
            };
            if (section == QueueSection.Queue && !Viewer)
                top.Add(new BoxEl
                {
                    Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Corners = Radii.ControlAll,
                    HoverFill = Design.Colors.RowHover, PressedFill = Design.Colors.RowPressed,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = static () => ClearRows(),
                    Children = [new TextEl(Loc.Get(Strings.Player.Clear)) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
                });
            bool hint = section == QueueSection.Autoplay;
            Element eyebrowRow = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = HeaderRowH, Children = top.ToArray() };
            return new BoxEl
            {
                Key = "hdr:" + Tag(section),
                Direction = 1, Gap = Spacing.XXS, Height = hint ? HeaderSubExtent : HeaderExtent,
                Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.XS),
                Layout = LayoutTransition.Slide,
                BlocksDragArm = true,                                              // a slot, never a handle
                Children = hint
                    ? [eyebrowRow, new TextEl(Loc.Get(Strings.Player.AutoplayHint)) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                    : [eyebrowRow],
            };
        }

        Element ShowMore(QueueSection section)
        {
            var pages = PagesOf(section);
            int total = View.CountOf(section), remaining = total - QueueSlots.Realized(total, pages.Peek(), PageSize);
            return new BoxEl
            {
                Key = "more:" + Tag(section),
                Direction = 0, Height = MoreRowH, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                BlocksDragArm = true, Margin = new Edges4(0f, Spacing.XS, 0f, Spacing.XXS), Corners = Radii.ControlAll,
                Fill = Tok.FillCardSecondary, HoverFill = Design.Colors.RowHover, PressedFill = Design.Colors.RowPressed,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = () => pages.Value = pages.Peek() + 1,
                Layout = LayoutTransition.Slide,
                Children =
                [
                    new TextEl(Icons.ChevronDown) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary },
                    new TextEl(Strings.Player.ShowNext(Math.Min(PageSize, remaining))) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary },
                    new TextEl("·  " + Strings.Player.MoreCount(remaining)) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
                ],
            };
        }

        static Element PlayingFrom(string source, EntityId context)
        {
            var route = Shell.For(new EntityUri(context), source);
            bool nav = !route.IsNone;
            return new BoxEl
            {
                Key = "qp:ctx",
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = 28f,
                Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.S), Corners = Radii.ControlAll,
                HoverFill = nav ? Design.Colors.RowHover : ColorF.Transparent, Cursor = nav ? CursorId.Hand : CursorId.Arrow,
                OnClick = nav ? () => Shell.GoTo(route) : null,
                Children =
                [
                    new SpanTextEl(new[] { new TextSpan(Strings.Player.PlayingFrom(""), Color: Tok.TextSecondary), new TextSpan(source, Weight: 700, Color: Tok.TextPrimary) })
                    {
                        Size = 12f, LineHeight = 16f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, MinWidth = 0f,
                    },
                    new TextEl(Icons.ChevronRightMed) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary },
                ],
            };
        }

        /// <summary>A queue mode toggle: <see cref="ToggleButton.Controlled"/> on <see cref="Controls.AccentToggleStyle"/>
        /// (Workstream B's "Filter/mode toggles" grammar row) — stock checked = the chip accent, solid, never the raw
        /// grading. <paramref name="glyphIsText"/> is the autoplay "∞": a literal glyph, not an icon-font codepoint, so
        /// its <c>PartGlyph</c> node is overridden off the icon font.</summary>
        Element Pill(string glyph, string label, bool on, Action click, bool glyphIsText = false)
        {
            // The common (non-text-glyph) case reuses the shared cached RootNoShrink instance (no per-render
            // allocation); the text-glyph arm (autoplay's "∞") needs its own PartGlyph modifier too, so it builds a
            // local TemplateParts carrying both.
            TemplateParts parts = Controls.RootNoShrink;
            if (glyphIsText)
            {
                parts = new TemplateParts { [ToggleButton.PartRoot] = b => b with { Shrink = 0f } };
                parts.Set<TextEl>(ToggleButton.PartGlyph, t => t with { FontFamily = null, Weight = 600 });
            }
            return ToggleButton.Controlled(label, on, _ => click(), glyph: glyph,
                style: Controls.AccentToggleStyle(_chipAccent.Value), parts: parts);
        }

        /// <summary>The heart's 26-wide lane — the Classic row's leading cell (<paramref name="height"/> its row) and the
        /// surface's trailing one (NaN: as tall as its cluster).</summary>
        static Element HeartLane(in RowText text, float height = float.NaN) => new BoxEl
        {
            Width = 26f, Height = height, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            BlocksDragArm = true, Children = [Heart(text)],
        };

        static Element Heart(in RowText text)
        {
            if (text.Uri.Length == 0) return new BoxEl();
            string uri = text.Uri, title = text.Title;
            return Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = 14f, Box = 26f }) with { Key = "save:" + uri };
        }

        /// <summary>Classic: no artwork, title and artists folded into ONE 14/20 run, the explicit mark pinned after the
        /// ellipsis (the only place the queue shows one). Thin: a single 160×14 bar.</summary>
        static Element ClassicIdentity(in RowText text, bool nowPlaying)
        {
            ColorF primary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextPrimary, secondary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextSecondary;
            Element identity;
            if (text.Thin) identity = new BoxEl { Width = 160f, Height = 14f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary };
            else
            {
                TextSpan[] spans = text.Artists.Length > 0
                    ? new[] { new TextSpan(text.Title, Weight: 600, Color: primary), new TextSpan("  ·  " + text.Artists, Color: secondary) }
                    : new[] { new TextSpan(text.Title, Weight: 600, Color: primary) };
                identity = new SpanTextEl(spans)
                {
                    Size = 14f, LineHeight = 20f, Color = primary, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f,
                };
            }
            Element clip = new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true, Children = [identity] };
            return new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, ClipToBounds = true,
                Children = text.Explicit && !text.Thin ? [clip, Controls.ExplicitBadge(16f, nowPlaying ? Tok.AccentTextPrimary : null)] : [clip],
            };
        }

        static Element ClassicHairline() => new BoxEl
        {
            Key = "classic-hairline", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = 1f,
            Fill = Prop.Of(static () => Tok.StrokeDividerDefault), HitTestVisible = false,
        };

        /// <summary>The rail's ✕ is painted AT REST (no hover-opacity — that is the stage's row, ch 21 §11 #1).</summary>
        static Element CloseGlyph(Action remove, bool classic) => new BoxEl
        {
            Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = classic ? CornerRadius4.All(0f) : Radii.ControlAll, HoverFill = Design.Colors.RowPressed,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, BlocksDragArm = true, OnClick = remove,
            Children = [new TextEl(Icons.ChromeClose) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary }],
        };
    }

    /// <summary>A cover with the now-playing overlay's play FAB centred on it.</summary>
    static Element ArtTile(string? url, string uri, Action play, float edge, float fab, int decode) => new BoxEl
    {
        Width = edge, Height = edge, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = Radii.ControlAll,
        Children = [Controls.Artwork(url, edge, edge, Radii.Control, decodePx: decode), Controls.NowPlayingOverlay(uri, play, fab, centred: true)],
    };

    static void ToggleAutoplay() => Platform.Settings.Set(Platform.Keys.AutoplayEnabled, !Platform.Settings.Get(Platform.Keys.AutoplayEnabled));

    // ══ 4. THE STAGE QUEUE PANE (ch 21 W12) ═══════════════════════════════════════════════════════════════════════════

    /// <summary>The rows under K's stage skin, sized for the stage: every metric scales by <c>Stage.Layout.QueueScale</c> (1 on
    /// the board, 1.25 on a 2560×1080 ultrawide). The first upcoming song is the NEXT row (larger art, an eyebrow, display
    /// type); each section opens with a caption and its count; a row shows its position, which turns into the grip on hover
    /// while the list owns the drag; an end-aligned duration, a hover-revealed ✕, glass instead of the row-hover plate,
    /// autoplay at 0.68.</summary>
    sealed class StagePane : QueueSurface
    {
        const float RowH = 56f, RowArt = 38f, TimeW = 44f, GripW = 28f;
        const float NextH = 104f, NextArt = 80f, NextGap = Spacing.M;
        const float HeaderH = 40f;
        const float MoreRowH = 40f;

        readonly Signal<int> _pages = new(1);
        float _s = 1f;

        public StagePane() : base(RowH) { }

        /// <summary>The first row of the whole upcoming list is the Next row: its extent includes the gap under it.</summary>
        bool IsNextSlot(QueueSlot slot) => slot.IsRow && View.FlatIndex(slot.Section, slot.Pos) == 0;

        protected override float ExtentOf(QueueSlot slot) => slot.Kind switch
        {
            QueueSlotKind.More => Px(MoreRowH) + Px(Spacing.XS) + Px(Spacing.XXS),   // = ShowMore's rendered height + margins (each term rounded as rendered)
            QueueSlotKind.Header => HeaderH * _s,
            _ => IsNextSlot(slot) ? (NextH + NextGap) * _s : RowH * _s,
        };

        public override Element Render()
        {
            var rows = Prologue();
            _s = UseContext(Stage.StageContext)?.Layout.Value.QueueScale ?? 1f;
            SetRowExtent(RowH * _s);
            UseEffect(() => { _pages.Value = 1; }, DepKey.From(ContextId.GetHashCode()));
            bool art = !Prefs.Appearance.TrackArtworkHidden();
            int pages = _pages.Value;
            View.Build(QueueSlots.Realized(View.User, pages, PageSize), QueueSlots.Realized(View.Next, pages, PageSize),
                       QueueSlots.Realized(View.Auto, pages, PageSize), Autoplay, headers: true);
            ConfigureReorder();

            var kids = new List<Element>(2);
            if (View.SlotCount > 0)
            {
                var slots = new Element[View.SlotCount];
                for (int i = 0; i < slots.Length; i++)
                {
                    int item = ItemAt(i);
                    var slot = View.Slots[item];
                    if (slot.Kind == QueueSlotKind.More) { slots[i] = ShowMore(slot.Section, pages); continue; }
                    if (slot.Kind == QueueSlotKind.Header) { slots[i] = StageHeader(slot.Section); continue; }
                    int index = View.FlatIndex(slot.Section, slot.Pos);
                    slots[i] = index < 0 ? new BoxEl() : Lane(item, index == 0 ? NextRow(rows[index], index, slot.Section, art) : Row(rows[index], index, slot.Section, art),
                                                              RowKey("s", rows[index].ItemId, index));
                }
                kids.Add((BoxEl)Reorder.List(new BoxEl { Key = "stageupcoming", Direction = 1, Children = slots }) with { Grow = 0f, Key = "stagelane:upcoming" });
            }
            if (View.User == 0 && View.Next == 0 && View.Auto == 0)
                kids.Add(new BoxEl
                {
                    Padding = new Edges4(0f, Spacing.XXL, 0f, 0f),
                    Children = [new TextEl(Loc.Get(Strings.Player.QueueEmpty)) { Size = 14f, LineHeight = 20f, Color = Ink.InkTertiary }],
                });
            Element body = new BoxEl { Direction = 1, MinHeight = 0f, Children = kids.ToArray() };
            return View.SlotCount == 0 ? (BoxEl)Reorder.List(body) with { Grow = 0f, Key = "stagelane:empty" } : body;
        }

        float Px(float v) => MathF.Round(v * _s);

        Element Row(QueueEdge edge, int index, QueueSection section, bool art)
        {
            EntityRef r = RefAt(index);
            RowText text = TextOf(r);
            ulong itemId = edge.ItemId;
            bool removable = QueueRowRules.Removable(Viewer, itemId);
            var kids = new List<Element>(5)
            {
                // The row's position; on hover it gives way to the grip (an affordance, not a control: the whole row is the
                // drag source, and a viewer never sees the grip).
                new BoxEl
                {
                    Width = Px(GripW), Shrink = 0f, ZStack = true, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                            HoverOpacity = Viewer ? 1f : 0f, HitTestVisible = false,
                            Children = [new TextEl(FormatCache.Int(index + 1)) { Size = Px(12f), LineHeight = Px(16f), Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap }],
                        },
                        new BoxEl
                        {
                            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                            Opacity = 0f, HoverOpacity = Viewer ? 0f : 1f, HitTestVisible = false,
                            Children = [new TextEl(Icons.GripperBar) { Size = Px(14f), FontFamily = Theme.IconFont, Color = Ink.InkTertiary }],
                        },
                    ],
                },
            };
            if (art) kids.Add(ArtTile(text.Art, text.Uri, () => SkipToRow(itemId, index, r), Px(RowArt), Px(26f), (int)Px(96f)));
            kids.Add(TitleBlock(text, Px(14f), Px(20f), 600, null, Px(12f), Px(16f)));
            // Tabular by geometry: a fixed end-aligned slot, so every colon lands on the same x.
            kids.Add(new BoxEl
            {
                Width = Px(TimeW), Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                Children = [new TextEl(text.DurationMs > 0 ? Playback.TimeFormat.Clock(text.DurationMs) : "") { Size = Px(12f), LineHeight = Px(16f), Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap }],
            });
            kids.Add(RemoveSlot(removable, itemId, index, r));

            var row = new BoxEl
            {
                Key = RowKey("s", itemId, index) + ":art=" + art + ":s=" + Px(100f),
                Draggable = OwnDrag(Viewer, itemId, index, r),
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(Spacing.M), MinHeight = Px(RowH), Height = Px(RowH),
                Padding = new Edges4(Px(Spacing.S), 0f, Px(Spacing.S), 0f), Corners = Radii.ControlAll,
                Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
                PressScale = Design.Motion.ScaleSubtle.Press,
                Opacity = section == QueueSection.Autoplay ? 0.68f : 1f,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
                OnClick = () => SkipToRow(itemId, index, r),
                Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
                Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
                Layout = LayoutTransition.Slide,
                Children = kids.ToArray(),
            };
            return Attach(row, itemId, index, r);
        }

        /// <summary>The first upcoming song, set apart: larger art, a "Next" eyebrow, the title in display type, on a quiet
        /// plate. Same verbs as any row (click skips to it, drag reorders it, ✕ removes it).</summary>
        Element NextRow(QueueEdge edge, int index, QueueSection section, bool art)
        {
            EntityRef r = RefAt(index);
            RowText text = TextOf(r);
            ulong itemId = edge.ItemId;
            bool removable = QueueRowRules.Removable(Viewer, itemId);
            var kids = new List<Element>(4);
            if (art) kids.Add(ArtTile(text.Art, text.Uri, () => SkipToRow(itemId, index, r), Px(NextArt), Px(32f), (int)Px(192f)));
            kids.Add(TitleBlock(text, Px(22f), Px(28f), 600, Loc.Get(Strings.Stage.NextTrack), Px(14f), Px(20f)));
            kids.Add(new BoxEl
            {
                Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                Children = [new TextEl(text.DurationMs > 0 ? Playback.TimeFormat.Clock(text.DurationMs) : "") { Size = Px(14f), LineHeight = Px(20f), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap }],
            });
            kids.Add(RemoveSlot(removable, itemId, index, r));

            var row = new BoxEl
            {
                Key = RowKey("s", itemId, index) + ":next:art=" + art + ":s=" + Px(100f),
                Draggable = OwnDrag(Viewer, itemId, index, r),
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Px(Spacing.L), Height = Px(NextH),
                Margin = new Edges4(0f, 0f, 0f, Px(NextGap)),
                Padding = new Edges4(Px(Spacing.M), 0f, Px(Spacing.S), 0f), Corners = Radii.CardAll,
                Fill = Ink.Plate, HoverFill = Ink.PlateHover, PressedFill = Ink.GlassPressed, BrushTransitionMs = Design.Motion.Faster,
                BorderWidth = 1f, BorderColor = Ink.Stroke,
                PressScale = Design.Motion.ScaleSubtle.Press,
                Opacity = section == QueueSection.Autoplay ? 0.68f : 1f,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
                OnClick = () => SkipToRow(itemId, index, r),
                Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
                Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
                Layout = LayoutTransition.Slide,
                Children = kids.ToArray(),
            };
            return Attach(row, itemId, index, r);
        }

        /// <summary>Title over artists (an explicit badge before the artists when the track is explicit), with an optional
        /// eyebrow above. A THIN row (title unknown) keeps the slot empty.</summary>
        static Element TitleBlock(RowText text, float titleSize, float titleLine, ushort titleWeight, string? eyebrow, float subSize, float subLine)
        {
            if (text.Thin) return new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f };
            var lines = new List<Element>(3);
            if (eyebrow is not null)
                lines.Add(Design.Type.Eyebrow(eyebrow) with { Size = MathF.Round(subSize * 0.86f), Color = Ink.InkSecondary, MaxLines = 1 });
            lines.Add(new TextEl(text.Title)
            {
                Size = titleSize, LineHeight = titleLine, Weight = titleWeight, FontFamily = eyebrow is null ? null : Design.Type.DisplayFace,
                Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            });
            Element artists = new TextEl(text.Artists) { Size = subSize, LineHeight = subLine, Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
            lines.Add(text.Explicit
                ? new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
                              Children = [Controls.ExplicitBadge(MathF.Round(subLine * 0.8f), Ink.InkTertiary), artists] }
                : artists);
            return new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f, Children = lines.ToArray() };
        }

        Element RemoveSlot(bool removable, ulong itemId, int index, EntityRef r) => removable
            ? new BoxEl { Opacity = 0f, HoverOpacity = 1f, Shrink = 0f, BlocksDragArm = true, Children = [StageGlyph(Icons.ChromeClose, () => RemoveRow(itemId, index, r))] }
            : new BoxEl { Width = Controls.IconButtonSize, Shrink = 0f };

        /// <summary>A section caption in the stage's ink: the section's name and its count (the rail's grammar, on media).</summary>
        Element StageHeader(QueueSection section)
        {
            string title = Loc.Get(section switch
            {
                QueueSection.Queue => Strings.Player.NextInQueue,
                QueueSection.NextUp => Strings.Player.NextUp,
                _ => Strings.Player.Autoplay,
            });
            return new BoxEl
            {
                Key = "stagehdr:" + Tag(section),
                Direction = 0, AlignItems = FlexAlign.End, Gap = Spacing.S, Height = Px(HeaderH),
                Padding = new Edges4(Px(Spacing.S), 0f, Px(Spacing.S), Px(Spacing.XS)),
                Layout = LayoutTransition.Slide,
                BlocksDragArm = true,                                              // a slot, never a handle
                Children =
                [
                    Design.Type.Eyebrow(title) with { Size = Px(12f), Color = Ink.InkTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(FormatCache.Int(View.CountOf(section))) { Size = Px(12f), LineHeight = Px(16f), Weight = 600, Color = Ink.InkTertiary },
                ],
            };
        }

        Element ShowMore(QueueSection section, int pages)
        {
            int total = View.CountOf(section), remaining = total - QueueSlots.Realized(total, pages, PageSize);
            return new BoxEl
            {
                Key = "stagemore:" + Tag(section),
                // scaled like every other stage slot: height + margins sum to ExtentOf's More extent (the virtualizer's offsets)
                Direction = 0, Height = Px(MoreRowH), Gap = Px(Spacing.S), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                BlocksDragArm = true, Margin = new Edges4(0f, Px(Spacing.XS), 0f, Px(Spacing.XXS)), Corners = Radii.ControlAll,
                Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = () => _pages.Value = _pages.Peek() + 1,
                Layout = LayoutTransition.Slide,
                Children =
                [
                    new TextEl(Icons.ChevronDown) { Size = Px(12f), FontFamily = Theme.IconFont, Color = Ink.InkSecondary },
                    new TextEl("·  " + remaining) { Size = Px(12f), LineHeight = Px(16f), Color = Ink.InkTertiary },
                ],
            };
        }

        static BoxEl StageGlyph(string glyph, Action onClick) => new()
        {
            Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed,
            BrushTransitionMs = Design.Motion.Faster,
            HoverScale = Design.Motion.ScaleEmphatic.Hover, PressScale = Design.Motion.ScaleEmphatic.Press,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
            Children = [new TextEl(glyph) { Size = 12f, FontFamily = Theme.IconFont, Color = Ink.InkSecondary, HoverColor = Ink.Ink }],
        };
    }

    // ══ 5. THE ROW VERBS — resolved at INVOKE time ════════════════════════════════════════════════════════════════════

    /// <summary>Where a row a render captured lives NOW: by its item id, else by the captured index while it still holds
    /// the same ref.</summary>
    static int Locate(ulong itemId, int index, EntityRef row)
        => itemId != 0 ? IndexOfItem(itemId) : RefAt(index) == row && !row.IsNone ? index : -1;

    /// <summary>A click on a row: a cursor move inside the live session, never a rebuild (ch 21 §6.4).</summary>
    static void SkipToRow(ulong itemId, int index, EntityRef row)
    {
        int at = Locate(itemId, index, row);
        if (at < 0) return;
        EntityId context = Playback.ContextUri.Peek();
        if (Viewing) { Playback.PlayNow(RefAt(at), context, CursorOf(at)); return; }      // the reducer forwards it
        if (!SkipTo(at, Playback.Snap().Cursor, out QueueCursor cursor)) return;
        Entities.Publish();
        Playback.PlayNow(RefAt(cursor), context, cursor);
    }

    /// <summary>Play a queue row from OUTSIDE the panel (the rail's "Next up" rows): the very cursor move a click on the
    /// panel's own row makes — <see cref="SkipTo"/> trims the rows the skip passes over, then the deck plays the row — and
    /// a viewed (remote) queue forwards it. The row is found by <paramref name="itemId"/> in the LIVE queue, so a queue
    /// that shifted since the caller rendered still plays the row the user clicked; <paramref name="index"/> and
    /// <paramref name="row"/> are only the fallback for an entry that has no item id (0): the index the caller saw and
    /// the ref it saw there, which must still agree. A row that is gone, or not upcoming, does nothing.</summary>
    internal static void PlayItem(ulong itemId, int index = -1, EntityRef row = default) => SkipToRow(itemId, index, row);

    static void RemoveRow(ulong itemId, int index, EntityRef row)
    {
        int at = Locate(itemId, index, row);
        if (at >= 0 && !Viewing && RemoveUpcoming(at, Playback.Snap().Cursor)) Changed();
    }

    static void MoveRow(QueueSection section, int from, int to)
    {
        if (!Viewing && MoveInSection(section, from, to, Playback.Snap().Cursor)) Changed();
    }

    static void ClearRows()
    {
        if (!Viewing && ClearUserQueue(Playback.Snap().Cursor) > 0) Changed();
    }

    /// <summary>A queue edit landed: publish it now (a click is a drain boundary) and let the deck re-arm what plays next.</summary>
    static void Changed()
    {
        Entities.Publish();
        Playback.Post(Playback.Input.QueueChanged(Playback.FrameNowMs()));
    }

    /// <summary>A foreign drop on the lane: resolve the payload's tracks cold, insert them at the user-queue slot the
    /// insertion line marked, capped like the player bar's drop, and say how many landed.</summary>
    static void InsertPayload(object? payload, int userIndex)
    {
        if (Drag.Unwrap(payload) is { CanCopyTracks: true } p) _ = InsertPayloadAsync(p, userIndex);
    }

    static async Task InsertPayloadAsync(DragPayload payload, int userIndex)
    {
        Track[] tracks;
        try { tracks = await payload.ResolveTracksAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("queue", "drop could not resolve its tracks", ex); return; }
        if (tracks.Length == 0) return;
        Playback.ToUi(() =>
        {
            int cap = Shell.PlayerBarRules.DropInsertCount(tracks.Length), n = 0;
            var refs = new EntityRef[cap];
            for (int i = 0; i < cap; i++) if (tracks[i].IsValid) refs[n++] = new EntityRef(EntityKind.Track, tracks[i].Slot);
            if (Viewing)
            {
                // G-248: another device plays — the rows go to its queue through Connect, not into a queue we do not own.
                if (!Playback.QueueToOwner(refs.AsSpan(0, n), next: userIndex == 0))
                { Notify.Say(Loc.Get(Strings.Detail.QueueUnavailable), InfoBarSeverity.Warning); return; }
                Notify.Say(Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)), InfoBarSeverity.Success);
                return;
            }
            int added = InsertUser(refs.AsSpan(0, n), Playback.Snap().Cursor, userIndex);
            if (added == 0) return;
            Changed();
            Notify.Say(Shell.PlayerBarRules.DropWasTruncated(tracks.Length)
                ? Strings.Detail.AddedFirstToQueue(Strings.Detail.SongCount(added))
                : Strings.Detail.AddedToQueue(Strings.Detail.SongCount(added)), InfoBarSeverity.Success);
        });
    }

    /// <summary>The queue-entry menu (ch 21 §6.4, 0.2.9 <c>Menus.QueueEntry</c>): header "{artists} · {section}", the strip
    /// Play now · Play next · Add to queue · Like, the track rows, Move up / Move down where legal, Remove last.</summary>
    static ContextMenuModel? RowMenu(ulong itemId, int index, EntityRef row, bool viewer)
    {
        int at = Locate(itemId, index, row);
        if (at < 0 || row.Kind != EntityKind.Track || new Track(row.Slot) is not { IsValid: true } track) return null;
        var rows = Rows;
        int pos = PositionInSection(rows, Divider(rows, viewer ? -1 : Playback.Snap().Cursor.Index), at, out int count);
        QueueSection section = SectionOf(rows[at]);
        ulong id = rows[at].ItemId;
        var ctx = new ActionContext(ActionTarget.ForQueueEntry(track, (long)id), Actions.Services);

        var strip = new List<AppBarCommand>(4) { new AppBarCommand(Icons.Play, Loc.Get(Strings.Menu.PlayNow), () => SkipToRow(itemId, index, row)) };
        ReadOnlySpan<ActionId> verbs = [ActionId.PlayNext, ActionId.AddToQueue, ActionId.ToggleLike];
        for (int v = 0; v < verbs.Length; v++)
            if (Actions.Menu.Command(verbs[v], in ctx) is { } command) strip.Add(command);

        var list = new List<MenuFlyoutItem>(10);
        Actions.Menu.AddRows(list, in ctx, [ActionId.AddToPlaylist, ActionId.GoToAlbum, ActionId.GoToArtist]);
        if (Actions.Menu.Share(in ctx) is { } share) list.Add(share);
        Actions.Menu.AddRows(list, in ctx, [ActionId.ViewCredits, ActionId.GoToSongRadio]);
        bool editable = !viewer && id != 0 && pos >= 0;
        if (editable && (pos > 0 || pos + 1 < count))
        {
            Actions.Menu.OpenGroup(list);
            Actions.Menu.AddMoveRows(list, pos > 0 ? () => MoveRow(section, pos, pos - 1) : null,
                                     pos + 1 < count ? () => MoveRow(section, pos, pos + 1) : null);
        }
        if (editable)
        {
            Actions.Menu.OpenGroup(list);
            list.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.RemoveFromQueue), ActionIcons.Resolve(ActionIcons.Remove), true,
                                        () => RemoveRow(itemId, index, row)));
        }
        string name = Loc.Get(section switch
        {
            QueueSection.Queue => Strings.Player.QueueSectionNext,
            QueueSection.Autoplay => Strings.Player.QueueSectionAutoplay,
            _ => Strings.Player.QueueSectionNextUp,
        });
        string artists = Entities.Strings.Resolve(track.ArtistLineId);
        return new ContextMenuModel(strip, list,
            Actions.Menu.Header(Controls.ArtUrl(track.ImageId), track.Title, artists.Length > 0 ? artists + " · " + name : name));
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
        Changed();
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
            Execute = static c => RemoveRow((ulong)c.Target.QueueItemId, -1, default),
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

/// <summary>The rail queue row's pure decisions, engine-free so a fact pins each one: the row's height under each skin
/// (the lane's drag maths needs it EXACT), the autoplay dim, which row has a ✕, which row drags on its own, whether a row
/// has a "…" button, which adapter feeds a row, and where a click on the playing card goes. The builders in
/// <c>Queue.UI.cs</c> (and the rail's "Next up" rows) only feed them.</summary>
public static class QueueRowRules
{
    /// <summary>The Classic row's height: the hairline row, no art.</summary>
    public const float ClassicExtent = 44f;

    /// <summary>The Autoplay rows' dim — the radio's tail is not the user's own queue.</summary>
    public const float AutoplayDim = 0.72f;

    /// <summary>The surface row's text column: a title line, the label gap and a caption line — what a row with artists
    /// stacks beside its art.</summary>
    public const float TextColumn = SurfaceGeometry.CardTitleLineH + SurfaceGeometry.CardLabelGap + SurfaceGeometry.CardCaptionLineH;

    /// <summary>One row's resting height under a skin: Classic's own, else the surface's.</summary>
    public static float Extent(bool classic, float artEdge) => classic ? ClassicExtent : SurfaceExtent(artEdge);

    /// <summary>The shared surface's row height: its 8-DIP padding round the taller of the art and the text column. It is
    /// what the surface renders, so the lane's slot maths (<c>Reorderable.ExtentOf</c>) and the shape's floor both state it.</summary>
    public static float SurfaceExtent(float artEdge) => 2f * SurfaceGeometry.RowPad + MathF.Max(artEdge, TextColumn);

    /// <summary>The Autoplay section's rows are dimmed; the queue's own are not.</summary>
    public static float Dim(QueueSection section) => section == QueueSection.Autoplay ? AutoplayDim : 1f;

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

    /// <summary>Which adapter feeds a row: the seed face while its title is unknown, the episode adapter for an episode,
    /// the track adapter for everything else.</summary>
    public enum Face : byte { Seed, Track, Episode }

    public static Face FaceOf(EntityKind kind, bool thin)
        => thin ? Face.Seed : kind == EntityKind.Episode ? Face.Episode : Face.Track;

    /// <summary>Where a click on the playing card goes: the playing context's page when there is one, else nowhere — the
    /// card body is then DISPLAY-ONLY (<c>CardData.OnClick</c> null: no hand, no Button role, no tab stop), and the play/pause
    /// FAB is the one control that still toggles playback.</summary>
    public enum NowPlayingClick : byte { OpenContext, None }

    public static NowPlayingClick NowPlayingClickOf(bool hasPage) => hasPage ? NowPlayingClick.OpenContext : NowPlayingClick.None;
}
