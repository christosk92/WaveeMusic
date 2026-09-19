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
// A QUEUE ROW IS NOT `Track.Row` (ch 21 §9.3 #4): it is its own builder over (row ref, QueueEdge) — heart lane, ✕, a
// drag payload that no playlist may copy, the autoplay dim, the thin-row skeleton, a section-named menu. `Track.UI.cs`'s
// `TrackRow` controls do not exist yet (WP-4.5 in flight), so the heart is `Controls.SaveButton` and the artist line is
// plain text.
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
        Rail.QueueBody = static () => Embed.Comp(static () => new RailPanel());
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

    readonly record struct RowText(string Uri, string Title, string Artists, string? Art, int DurationMs, bool Thin, bool Explicit);

    /// <summary>What a row paints. A row whose title is not known is THIN (two bars), never a bare uri (ch 21 §7).</summary>
    static RowText TextOf(EntityRef r)
    {
        if (r.Kind == EntityKind.Episode && new Episode(r.Slot) is { IsValid: true } e)
            return new RowText(e.Uri.Text, e.Title, e.Show.IsValid ? e.Show.Title : "", Controls.ArtUrl(e.ImageId), e.DurationMs,
                               !e.Knows(EpisodeFields.Title), false);
        if (r.Kind != EntityKind.Track || new Track(r.Slot) is not { IsValid: true } t) return new RowText("", "", "", null, 0, true, false);
        // A ruled-unavailable track (Spotify no longer resolves it for this account) is NOT a thin row: it says so.
        if (t.Unplayable())
            return new RowText(t.Uri.Text, Loc.Get(Strings.Detail.TrackFacts.Unavailable), "", null, 0, false, false);
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
    /// at once. Shared with the stage's queue skin (<c>Stage.UI.cs</c>).</summary>
    internal static string? ContextName(EntityId context)
    {
        var scope = Entities.Current;
        switch (context.Kind)
        {
            case EntityKind.Collection: return Loc.Get(Strings.Player.LikedSongs);
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
            if (nt > 0) Entities.Ensure(Entities.Current.Tracks, tracks.AsSpan(0, nt), (uint)TrackFields.Row, FetchPriority.Visible);
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
        readonly float _rowExtent;

        protected QueueSurface(float rowExtent)
        {
            _rowExtent = rowExtent;
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
            UseEffect(static () => EnsureContext(Playback.ContextUri.Peek()), DepKey.From(ContextId.GetHashCode()));
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
            => viewer ? Drag.Source(() => PayloadFor(itemId, index, row)) : null;

        protected Element Attach(BoxEl row, ulong itemId, int index, EntityRef r)
        {
            bool viewer = Viewer;
            return MenuHost is { } host ? row.WithContextMenu(host, () => RowMenu(itemId, index, r, viewer)) : row;
        }

        float ExtentAt(int i) => (uint)i < (uint)View.SlotCount ? ExtentOf(View.Slots[i]) : _rowExtent;

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

    sealed class RailPanel : QueueSurface
    {
        const float QueueArt = 34f, RowExtent = 44f, HeaderRowH = 20f;
        const float HeaderExtent = Spacing.M + HeaderRowH + Spacing.XS;           // 36
        const float HeaderSubExtent = HeaderExtent + Spacing.XXS + 16f;           // 54, the Autoplay header's hint line
        const float MoreRowH = 40f, MoreExtent = MoreRowH + Spacing.XS + Spacing.XXS;

        readonly Signal<int> _userPages = new(1), _nextPages = new(1), _autoPages = new(1);
        readonly SwipeGroup _swipe = new();

        // The chip accent: resolved per render through the shared ladder, written to a signal by an effect (never during
        // render), read by three Prop thunks the pills bind once.
        ColorF? _queueHold;
        ColorF _chipPending = Tok.AccentDefault;
        readonly Signal<ColorF> _chipAccent = new(Tok.AccentDefault);
        readonly Action _pushChip;
        readonly Prop<ColorF> _chipFill, _chipHover, _chipPressed;

        public RailPanel() : base(RowExtent)
        {
            _pushChip = () => _chipAccent.Value = _chipPending;
            _chipFill = Prop.Of(() => _chipAccent.Value);
            _chipHover = Prop.Of(() => _chipAccent.Value with { A = 0.88f });
            _chipPressed = Prop.Of(() => _chipAccent.Value with { A = 0.78f });
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
            _ => RowExtent,
        };

        Signal<int> PagesOf(QueueSection s) => s switch { QueueSection.Queue => _userPages, QueueSection.NextUp => _nextPages, _ => _autoPages };

        public override Element Render()
        {
            var rows = Prologue();
            UseEffect(() => { _userPages.Value = 1; _nextPages.Value = 1; _autoPages.Value = 1; }, DepKey.From(ContextId.GetHashCode()));
            bool classic = Prefs.Appearance.TrackRowStyle() == 1;
            bool art = !classic && !Prefs.Appearance.TrackArtworkHidden();
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
            if (ContextName(ContextId) is { Length: > 0 } source) content.Add(PlayingFrom(source, ContextId));
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
                        Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, 4f, 0f, 10f),
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
                        Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "queuepanel",
                        OnScrollGeometryChanged = (g => _swipe.AnyOpen ? BitConverter.SingleToInt32Bits(g.OffsetY) : 0L, _ => _swipe.Close()),
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
            bool removable = !Viewer && itemId != 0;
            var kids = new List<Element>(5) { HeartLane(text, RowExtent) };
            if (art) kids.Add(ArtTile(text.Art, text.Uri, () => SkipToRow(itemId, index, r), QueueArt, 26f, 72));
            kids.Add(classic ? ClassicIdentity(text, nowPlaying: false) : Identity(text, Tok.TextPrimary, Tok.TextSecondary));
            kids.Add(MenuHost is null ? new BoxEl { Width = 0f, Shrink = 0f } : Overflow(classic));
            kids.Add(removable ? CloseGlyph(() => RemoveRow(itemId, index, r), classic) : new BoxEl { Width = Controls.IconButtonSize, Shrink = 0f });

            var body = new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = RowExtent,
                Padding = new Edges4(Spacing.S, 0f, Spacing.XS, 0f), Children = kids.ToArray(),
            };
            var row = new BoxEl
            {
                Key = RowKey("", itemId, index) + ":art=" + art + ":classic=" + classic,
                Draggable = OwnDrag(Viewer, itemId, index, r),
                ZStack = true, MinHeight = RowExtent, ClipToBounds = classic,
                Corners = classic ? CornerRadius4.All(0f) : Radii.ControlAll,
                Fill = ColorF.Transparent, HoverFill = Design.Colors.RowHover, PressedFill = Design.Colors.RowPressed,
                PressScale = Design.Motion.ScaleSubtle.Press,
                Opacity = section == QueueSection.Autoplay ? 0.72f : 1f,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                OnClick = () => SkipToRow(itemId, index, r),
                Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
                Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
                Layout = LayoutTransition.Slide,
                Children = classic ? [body, ClassicHairline()] : [body],
            };
            Element el = Attach(row, itemId, index, r);
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

        Element NowPlayingCard(EntityRef current, bool classic)
        {
            RowText text = TextOf(current);
            var kids = new List<Element>(3);
            if (!classic) kids.Add(ArtTile(text.Art, text.Uri, static () => Playback.TogglePlay(), 44f, 28f, 96));
            Element identity = classic ? ClassicIdentity(text, nowPlaying: true) : Identity(text, Tok.AccentTextPrimary, Tok.TextSecondary);
            // The title is a LINK to where the track plays from (0.2.9 parity, user report 2026-09-16): the context's page
            // when it has one (a playlist, an album, an artist, the liked songs), else the track's album — a station has no
            // page of its own, and the album is the nearest thing to "where this came from".
            var target = NowPlayingTarget(current);
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
            kids.Add(identity);
            kids.Add(new BoxEl
            {
                Width = 30f, Height = classic ? RowExtent : 44f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Heart(text)],
            });
            var body = new BoxEl
            {
                Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = classic ? Spacing.S : Spacing.L,
                MinHeight = classic ? RowExtent : 64f, Padding = classic ? new Edges4(Spacing.S, 0f, Spacing.XS, 0f) : Edges4.All(10f),
                Children = kids.ToArray(),
            };
            return new BoxEl
            {
                // Keyed by the track: a track change remounts the card with an Enter fade (the cross-fade a row cannot do).
                Key = "np:" + text.Uri + ":classic=" + classic,
                ZStack = true, MinHeight = classic ? RowExtent : 64f, ClipToBounds = classic,
                Margin = classic ? default : new Edges4(0f, 0f, 0f, 10f),
                Corners = classic ? CornerRadius4.All(0f) : Radii.CardAll,
                Fill = classic ? ColorF.Transparent : Tok.FillCardDefault,
                BorderWidth = classic ? 0f : 1f, BorderColor = classic ? ColorF.Transparent : Tok.StrokeCardDefault,
                Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
                Layout = LayoutTransition.Slide,
                Children = classic ? [body, ClassicHairline()] : [body],
            };
        }

        /// <summary>Where the now-playing title navigates: the playing context's page when the context has one, else the
        /// track's album page, else nowhere (<see cref="Shell.Route.IsNone"/>).</summary>
        Shell.Route NowPlayingTarget(EntityRef current)
        {
            if (!ContextId.IsEmpty)
            {
                var ctx = Shell.For(new EntityUri(ContextId), ContextName(ContextId) ?? "");
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

        /// <summary>A queue pill: accent-FILLED when on (the lifted cover role, never the raw grading), an alpha step on
        /// hover/press, on-accent ink. The fills are bound to the chip accent signal, so a colour change is a brush
        /// transition, not a re-render.</summary>
        Element Pill(string glyph, string label, bool on, Action click, bool glyphIsText = false) => new BoxEl
        {
            Direction = 0, Height = 32f, Gap = 6f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(12f, 0f, 12f, 0f), Corners = Radii.PillAll,
            Fill = on ? _chipFill : (Prop<ColorF>)Tok.FillCardSecondary,
            HoverFill = on ? _chipHover : (Prop<ColorF>)Tok.FillSubtleSecondary,
            PressedFill = on ? _chipPressed : (Prop<ColorF>)Tok.FillSubtleTertiary,
            BrushTransitionMs = Design.Reduced ? 0f : Design.Motion.Standard,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = click,
            Children =
            [
                glyphIsText
                    ? new TextEl(glyph) { Size = 14f, LineHeight = 20f, Weight = 600, Color = on ? Tok.TextOnAccentPrimary : Tok.TextSecondary }
                    : new TextEl(glyph) { Size = 12f, FontFamily = Theme.IconFont, Color = on ? Tok.TextOnAccentPrimary : Tok.TextSecondary },
                new TextEl(label) { Size = 12f, LineHeight = 16f, Weight = 600, Color = on ? Tok.TextOnAccentPrimary : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };

        static Element HeartLane(in RowText text, float height) => new BoxEl
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

        static Element Identity(in RowText text, ColorF title, ColorF artists)
        {
            if (text.Thin)
                return new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 4f,
                    Children =
                    [
                        new BoxEl { Width = 120f, Height = 14f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                        new BoxEl { Width = 80f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                    ],
                };
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f,
                Children =
                [
                    new TextEl(text.Title) { Size = 14f, LineHeight = 20f, Weight = 600, Color = title, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    new TextEl(text.Artists) { Size = 12f, LineHeight = 16f, Color = artists, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                ],
            };
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

        /// <summary>The hover-revealed "…": re-enters the context funnel, so the row's own menu opens anchored here.</summary>
        static Element Overflow(bool classic) => new BoxEl
        {
            Opacity = 0f, HoverOpacity = 1f, Shrink = 0f, BlocksDragArm = true,
            Children =
            [
                new BoxEl
                {
                    Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = classic ? CornerRadius4.All(0f) : Radii.ControlAll, HoverFill = Design.Colors.RowPressed,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand, ClickRequestsContext = true,
                    Children = [new TextEl(Icons.More) { Size = 14f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary }],
                },
            ],
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

    /// <summary>The rows under K's stage skin: one continuous 56-DIP list on the on-media ladder — no captions, one shared
    /// page counter, a grip revealed on hover while the list owns the drag, an end-aligned duration, a hover-revealed ✕,
    /// glass instead of the row-hover plate, autoplay at 0.68.</summary>
    sealed class StagePane : QueueSurface
    {
        const float RowH = 56f, RowArt = 38f, TimeW = 44f, GripW = 24f;
        const float MoreRowH = 40f, MoreExtent = MoreRowH + Spacing.XS + Spacing.XXS;

        readonly Signal<int> _pages = new(1);

        public StagePane() : base(RowH) { }

        protected override float ExtentOf(QueueSlot slot) => slot.Kind == QueueSlotKind.More ? MoreExtent : RowH;

        public override Element Render()
        {
            var rows = Prologue();
            UseEffect(() => { _pages.Value = 1; }, DepKey.From(ContextId.GetHashCode()));
            bool art = !Prefs.Appearance.TrackArtworkHidden();
            int pages = _pages.Value;
            View.Build(QueueSlots.Realized(View.User, pages, PageSize), QueueSlots.Realized(View.Next, pages, PageSize),
                       QueueSlots.Realized(View.Auto, pages, PageSize), Autoplay, headers: false);
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
                    int index = View.FlatIndex(slot.Section, slot.Pos);
                    slots[i] = index < 0 ? new BoxEl() : Lane(item, Row(rows[index], index, slot.Section, art), RowKey("s", rows[index].ItemId, index));
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

        Element Row(QueueEdge edge, int index, QueueSection section, bool art)
        {
            EntityRef r = RefAt(index);
            RowText text = TextOf(r);
            ulong itemId = edge.ItemId;
            bool removable = !Viewer && itemId != 0;
            var kids = new List<Element>(5)
            {
                // An affordance, not a control: the whole row is the drag source, and a viewer never sees it.
                new BoxEl
                {
                    Width = GripW, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Opacity = 0f, HoverOpacity = Viewer ? 0f : 1f, HitTestVisible = false,
                    Children = [new TextEl(Icons.GripperBar) { Size = 14f, FontFamily = Theme.IconFont, Color = Ink.InkTertiary }],
                },
            };
            if (art) kids.Add(ArtTile(text.Art, text.Uri, () => SkipToRow(itemId, index, r), RowArt, 26f, 96));
            kids.Add(text.Thin
                ? new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f }
                : new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f,
                    Children =
                    [
                        new TextEl(text.Title) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        new TextEl(text.Artists) { Size = 12f, LineHeight = 16f, Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    ],
                });
            // Tabular by geometry: a fixed end-aligned slot, so every colon lands on the same x.
            kids.Add(new BoxEl
            {
                Width = TimeW, Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                Children = [new TextEl(text.DurationMs > 0 ? Playback.TimeFormat.Clock(text.DurationMs) : "") { Size = 12f, LineHeight = 16f, Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap }],
            });
            kids.Add(removable
                ? new BoxEl { Opacity = 0f, HoverOpacity = 1f, Shrink = 0f, BlocksDragArm = true, Children = [StageGlyph(Icons.ChromeClose, () => RemoveRow(itemId, index, r))] }
                : new BoxEl { Width = Controls.IconButtonSize, Shrink = 0f });

            var row = new BoxEl
            {
                Key = RowKey("s", itemId, index) + ":art=" + art,
                Draggable = OwnDrag(Viewer, itemId, index, r),
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = RowH,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
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

        Element ShowMore(QueueSection section, int pages)
        {
            int total = View.CountOf(section), remaining = total - QueueSlots.Realized(total, pages, PageSize);
            return new BoxEl
            {
                Key = "stagemore:" + Tag(section),
                Direction = 0, Height = MoreRowH, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                BlocksDragArm = true, Margin = new Edges4(0f, Spacing.XS, 0f, Spacing.XXS), Corners = Radii.ControlAll,
                Fill = Ink.GlassRest, HoverFill = Ink.GlassHover, PressedFill = Ink.GlassPressed,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = () => _pages.Value = _pages.Peek() + 1,
                Layout = LayoutTransition.Slide,
                Children =
                [
                    new TextEl(Icons.ChevronDown) { Size = 12f, FontFamily = Theme.IconFont, Color = Ink.InkSecondary },
                    new TextEl("·  " + remaining) { Size = 12f, LineHeight = 16f, Color = Ink.InkTertiary },
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
