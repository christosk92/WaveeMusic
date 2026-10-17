// ── Themes/Classic/Structure/Views/Queue/ClassicQueue.cs — Classic's `Queue.Panel` (themes plan 6.1, T7d) ─────────────────────────
//
// From `Entities/Queue.UI.cs`: the rail's queue panel. The core host (`Queue.RailPanelHost`) keeps what reads and decides: the
// subscriptions, the section split (`QueueProps`), the page counters, the realized slots, the reorder lane (the drag, the keyboard
// reorder, the foreign-drop rule), the row's drag payload and context menu, and the verbs. It hands this view the queue, the verbs, the lane
// and the setting it read (the row style); the view draws, and reads the hide-track-art option itself (M4).
//
//   panel  Pills · ScrollEl[ PlayingFrom · NowPlayingCard · lane[ headers · rows · Show more ] · vacancy ]
//
// A QUEUE ROW IS NOT `Track.Row`: a builder over (row, entry) with a heart, a ✕, a drag payload that no playlist may copy, the autoplay dim and
// a section-named menu. The Modern rows and the now-playing card are the shared media surface (`Controls.Surface` over `Track.RowData` /
// `Episode.RowData`: the plate, the hand, the play FAB, the pill and the right-click menu are the surface's — no "…" button,
// `QueueRowRules.ShowsMenuButton`; a row whose title is not known yet is its seed face) with the heart and ✕ as its trailing cluster. The
// hairline skin (square rows, no art, one folded line) stays its own tree — the surface has no hairline plate. Every slot is a plain keyed
// child; rows Enter / Exit / Slide.
//
// The panel is a component (as `RailPanel` was): the swipe group, the scroll handle, the chip accent and its hold are the picture's state.
// The stage's column is `ClassicQueueStagePane` (its own file).
//
// TIERS (TC3). The rows are fluid, so the panel lays out at whatever width the frame gives it (200-500). On a Compact window the panel is a
// sheet (C-Queue-Compact) and every target is at least 40 x 40: the hearts, the remove buttons, the mode toggles, "Clear" (its header grows
// to hold it, and the lane's slot maths with it) and "Playing from" (`ClassicStageTiers.PanelOf`).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's queue panel (the rail's queue body).</summary>
internal static class ClassicQueue
{
    /// <summary>The registered template of <see cref="ViewKeys.QueuePanel"/>.</summary>
    public static Element PanelTemplate(QueuePanelProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicQueuePanel());

    /// <summary>The registered template of <see cref="ViewKeys.QueueStagePane"/>.</summary>
    public static Element StagePaneTemplate(QueueStagePaneProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicQueueStagePane());

    // ── what the panel and the stage share ──────────────────────────────────────────────────────────────────────────────

    internal static string SectionTitle(QueueSection section) => Loc.Get(section switch
    {
        QueueSection.Queue => Strings.Player.NextInQueue,
        QueueSection.NextUp => Strings.Player.NextUp,
        _ => Strings.Player.Autoplay,
    });

    /// <summary>The queue entry's row/ghost EnterExit: rows rise in and leave upward.</summary>
    internal static EnterExit RowEnter => new(Dy: 6f, Opacity: 0f, Active: true);

    internal static EnterExit RowExit => new(Dy: -4f, Opacity: 0f, Active: true);
}

/// <summary>The rail panel's picture.</summary>
internal sealed class ClassicQueuePanel : Component
{
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

    // The verbs are the host's cached instances: one delegate each, never a fresh one per render.
    Action? _toggleShuffle, _cycleRepeat, _toggleAutoplay, _clear, _togglePlay;
    Func<QueueSlot, float>? _extentOf;
    float _rowExtent;
    ClassicPanelContentLayout _tier = ClassicStageTiers.Panel(WidthTier.Standard);   // this render's: the window tier's targets

    public ClassicQueuePanel()
    {
        _pushChip = () => _chipAccent.Value = _chipPending;
        _closeSwipeOnScroll = () =>
        {
            _ = _scroll.Offset.Value;   // re-runs on every offset the handle publishes
            if (_swipe.AnyOpen) _swipe.Close();
        };
    }

    /// <summary>The now-playing cover's graded chrome accent when the plane has it, the album's wire accent until then,
    /// the last remembered colour while a cover is still grading, the semantic accent otherwise. With "Now playing colours" off (CT9 U3): the
    /// semantic accent, nothing remembered.</summary>
    void ResolveChipAccent(EntityRef current, string cover, bool fromCover)
    {
        ColorF? graded = fromCover && Design.ChromeSchemeFor(cover) is { } scheme ? Design.Palette.ChromeAccent(scheme) : null;
        uint payload = fromCover && current.Kind == EntityKind.Track && new Track(current.Slot) is { IsValid: true } t && t.Album is { IsValid: true } album
            ? album.Accent : 0u;
        var r = AccentLadder.Resolve(new AccentLadder.Input(graded, payload, Definite: !fromCover || cover.Length == 0), fromCover ? _queueHold : null, Tok.AccentDefault,
                                     static a => Design.Palette.ChromeFromPayload(a));
        if (r.Remember) _queueHold = r.Color;
        _chipPending = r.Color;
        UseEffect(_pushChip, DepKey.From(r.Color.GetHashCode()));
    }

    float ExtentOf(QueueSlot slot) => slot.Kind switch
    {
        QueueSlotKind.Header => slot.Section == QueueSection.Autoplay ? ClassicQueueRules.HeaderSubExtentFor(_tier.Target) : ClassicQueueRules.HeaderExtentFor(_tier.Target),
        QueueSlotKind.More => ClassicQueueRules.MoreExtent,
        _ => _rowExtent,                 // the live skin's: SetMetrics, each render
    };

    public override Element Render()
    {
        var props = UseProps<QueuePanelProps>();
        var facts = UseContext(ThemeFactsContext.Signal);
        _tier = ClassicStageTiers.PanelOf(facts is null ? TierFacts.Unknown : facts.Value.Tiers);
        var queue = props.Queue;
        var lane = props.Lane;
        _toggleShuffle ??= props.Player.ToggleShuffle;
        _cycleRepeat ??= props.Player.CycleRepeat;
        _toggleAutoplay ??= props.Verbs.ToggleAutoplay;
        _clear ??= props.Verbs.Clear;
        UseSignalEffect(_closeSwipeOnScroll);
        bool hairline = ClassicOptions.ClassicRows();   // the structure's row-style option (M2); the hairline skin is the Classic one
        bool art = !hairline && !ClassicOptions.HideTrackArt();
        _rowExtent = ClassicQueueRules.Extent(hairline, ClassicQueueRules.QueueArt);
        lane.SetMetrics(_rowExtent, _extentOf ??= ExtentOf);
        string cover = queue.NowPlaying.IsNone ? "" : lane.TextOf(queue.NowPlaying).Art ?? "";
        bool fromCover = ClassicOptions.NowPlayingColors();   // the chip's colour follows the cover (CT9 U3)
        if (fromCover && cover.Length > 0) _ = Wavee.Palette.Watch(cover).Value;
        ResolveChipAccent(queue.NowPlaying, cover, fromCover);

        var content = new List<Element>(4);
        if (queue.PlayingFrom is { } playingFrom) content.Add(PlayingFrom(playingFrom.Name, props.OpenPlayingFrom, _tier));
        if (!queue.NowPlaying.IsNone) content.Add(NowPlayingCard(props, hairline));
        if (lane.SlotCount > 0) content.Add(lane.List(Upcoming(props, art, hairline), "lane:upcoming"));
        if (queue.NothingPlaying)
            content.Add(Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                                         title: Loc.Get(Strings.Player.NothingPlaying), subtitle: ""));

        Element body = new BoxEl { Direction = 1, MinHeight = 0f, Padding = new Edges4(0f, 0f, 0f, 14f), Children = content.ToArray() };
        // Nothing upcoming: the same lane wraps the body, so a foreign drop still has a target (slot 0 = play next).
        if (lane.SlotCount == 0) body = lane.List(body, "lane:empty");

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Padding = new Edges4(14f, 4f, 14f, 0f),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Wrap = true, Gap = ClassicQueueRules.PillGap, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, 4f, 0f, 10f),
                    Children =
                    [
                        Pill(Icons.Shuffle, Loc.Get(Strings.Player.Shuffle), queue.Shuffle, _toggleShuffle),
                        Pill(props.RepeatTrack ? Icons.RepeatOne : Icons.RepeatAll, Loc.Get(Strings.Player.Repeat), props.RepeatOn, _cycleRepeat),
                        Pill("∞", Loc.Get(Strings.Player.Autoplay), queue.Autoplay.Enabled, _toggleAutoplay, glyphIsText: true),
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

    Element Upcoming(QueuePanelProps props, bool art, bool hairline)
    {
        var lane = props.Lane;
        var kids = new Element[lane.SlotCount];
        for (int i = 0; i < kids.Length; i++)
        {
            int item = lane.ItemAt(i);
            var slot = lane.SlotAt(item);
            if (slot.Kind == QueueSlotKind.Header) { kids[i] = SectionHeader(props, slot.Section); continue; }
            if (slot.Kind == QueueSlotKind.More) { kids[i] = ShowMore(props, slot.Section); continue; }
            kids[i] = props.Queue.TryRow(slot.Section, slot.Pos, out var row)
                ? lane.Lane(item, Row(props, row, art, hairline), lane.RowKey("", row.ItemId, row.Index))
                : new BoxEl();
        }
        return new BoxEl { Key = "upcoming", Direction = 1, Children = kids };
    }

    Element Row(QueuePanelProps props, in QueueRowProps row, bool art, bool hairline)
    {
        var verbs = props.Verbs;
        EntityRef r = row.Ref;
        QueueRowText text = props.Lane.TextOf(r);
        ulong itemId = row.ItemId;
        int index = row.Index;
        bool removable = row.Removable;
        Element el = hairline ? HairlineRow(props, text, row)
                              : SurfaceRow(props, text, row, art);
        if (!removable || !Controls.SwipeArmed) return el;
        var remove = new SwipeAction(ActionIcons.Resolve(ActionIcons.Remove), Loc.Get(Strings.Menu.RemoveFromQueue))
        {
            OnInvoked = () => verbs.Remove(itemId, index, r),
        };
        SwipeSide? like = r.Kind == EntityKind.Track && AppActions.Find(ActionId.ToggleLike) is { } toggle
            ? SwipeSide.Of(toggle.ToSwipeAction(new ActionContext(ActionTarget.ForTracks([new Track(r.Slot)]), Actions.Services)))
            : null;
        return SwipeControl.Create(el, leading: like, trailing: SwipeSide.Of(remove), group: _swipe, touchOnly: true);
    }

    /// <summary>The Modern row: THE shared media surface (<c>Shape.Row</c>) over the row's own adapter — the plate, the
    /// hand, the play FAB, the pill and the right-click menu are the surface's (no "…" button,
    /// <see cref="QueueRowRules.ShowsMenuButton"/>); the heart and ✕ are its trailing cluster. A click (and the FAB) is
    /// a cursor move inside the live session (<see cref="IQueueVerbs.Play"/>), and the lane's reorder gesture rides the wrapper
    /// above the row, so the surface carries a drag of its own only for a viewer, who has no lane. The wrapper here carries what the surface has no dial for: the key (a skin flip REMOUNTS), the
    /// autoplay dim and the Enter/Exit/Slide motion.</summary>
    Element SurfaceRow(QueuePanelProps props, QueueRowText text, in QueueRowProps row, bool art)
    {
        var verbs = props.Verbs;
        EntityRef r = row.Ref;
        ulong itemId = row.ItemId;
        int index = row.Index;
        bool viewer = props.Queue.Viewer;
        Action skip = () => verbs.Play(itemId, index, r);
        var data = SurfaceData(r, text, art, click: skip, play: skip, draggable: row.Draggable, queueItem: itemId,
                               menu: () => verbs.RowMenu(itemId, index, r, viewer),
                               trailing: RowTrailing(text, row.Removable, () => verbs.Remove(itemId, index, r), _tier));
        var shape = Shape.Row(TrackRowRules.ArtEdge(ClassicQueueRules.QueueArt, art)) with { MinHeight = _rowExtent };
        return new BoxEl
        {
            Key = props.Lane.RowKey("", itemId, index) + ":art=" + art,
            Direction = 1, Opacity = ClassicQueueRules.Dim(row.Section),
            Enter = ClassicQueue.RowEnter,
            Exit = ClassicQueue.RowExit,
            Layout = LayoutTransition.Slide,
            Children = [Controls.Surface(data, shape)],
        };
    }

    /// <summary>The hairline row: square, no art, title and artists folded into one line — a look the surface
    /// has no plate for, so it stays its own tree. Like the Modern row it has no "…" button
    /// (<see cref="QueueRowRules.ShowsMenuButton"/>): its menu is the right-click / Menu key (<c>Attach</c>).</summary>
    Element HairlineRow(QueuePanelProps props, QueueRowText text, in QueueRowProps row)
    {
        var verbs = props.Verbs;
        EntityRef r = row.Ref;
        ulong itemId = row.ItemId;
        int index = row.Index;
        var body = new BoxEl
        {
            Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = ClassicQueueRules.HairlineExtent,
            Padding = new Edges4(Spacing.S, 0f, Spacing.XS, 0f),
            Children =
            [
                HeartLane(text, _tier, ClassicQueueRules.HairlineExtent),
                Identity(text, nowPlaying: false),
                row.Removable ? CloseGlyph(() => verbs.Remove(itemId, index, r), hairline: true, _tier) : new BoxEl { Width = _tier.CloseBox, Shrink = 0f },
            ],
        };
        var el = new BoxEl
        {
            Key = props.Lane.RowKey("", itemId, index) + ":classic",
            Draggable = props.Lane.OwnDrag(itemId, index, r),
            ZStack = true, MinHeight = ClassicQueueRules.HairlineExtent, ClipToBounds = true,
            Corners = CornerRadius4.All(0f),
            Fill = ColorF.Transparent, HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            PressScale = R.Motion.ScaleSubtle.Press,
            Opacity = ClassicQueueRules.Dim(row.Section),
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => verbs.Play(itemId, index, r),
            Enter = ClassicQueue.RowEnter,
            Exit = ClassicQueue.RowExit,
            Layout = LayoutTransition.Slide,
            Children = [body, Hairline()],
        };
        return props.Lane.Attach(el, itemId, index, r);
    }

    /// <summary>The playing card: Modern is the surface on the TILE plate (the opaque card fill and a hairline that turns
    /// accent while it plays) at a 44 art, its click the playing context's page — the context when it has one, else the
    /// track's album — and, with nowhere to go, NO click: the body is display-only and the FAB still toggles play/pause.
    /// The heart is its trailing; no "…" button (<see cref="QueueRowRules.ShowsMenuButton"/> — the right-click track
    /// menu stays), and it drags as the one track it is. The hairline skin stays its hairline row.</summary>
    Element NowPlayingCard(QueuePanelProps props, bool hairline)
    {
        EntityRef current = props.Queue.NowPlaying;
        QueueRowText text = props.Lane.TextOf(current);
        if (hairline) return HairlineNowPlaying(text, props.OpenNowPlaying);

        var player = props.Player;
        Action toggle = _togglePlay ??= () => player.TogglePlay("queue.nowplaying");
        Action? open = ClassicQueueRules.NowPlayingClickOf(props.OpenNowPlaying is not null) == ClassicQueueRules.NowPlayingClick.OpenContext
            ? props.OpenNowPlaying : null;
        var data = SurfaceData(current, text, art: true, click: open, play: toggle, draggable: true, queueItem: null, menu: null,
                               trailing: HeartLane(text, _tier));
        var shape = Shape.RowTile with { ArtEdge = ClassicQueueRules.NowArt };
        return new BoxEl
        {
            // Keyed by the track: a track change remounts the card with an Enter fade (the cross-fade a row cannot do).
            Key = "np:" + text.Uri + ":classic=False",
            Direction = 1, Margin = new Edges4(0f, 0f, 0f, 10f),
            Enter = ClassicQueue.RowEnter,
            Layout = LayoutTransition.Slide,
            Children = [Controls.Surface(data, shape)],
        };
    }

    Element HairlineNowPlaying(QueueRowText text, Action? open)
    {
        Element identity = Identity(text, nowPlaying: true);
        // The title is a LINK to where the track plays from (0.2.9 parity, user report 2026-09-16): the context's page
        // when it has one (a playlist, an album, an artist, the liked songs), else the track's album — a station has no
        // page of its own, and the album is the nearest thing to "where this came from".
        if (open is not null)
        {
            identity = new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1, Justify = FlexJustify.Center,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                Corners = Radii.ControlAll, HoverFill = R.Layer.RowHover,
                Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), Margin = new Edges4(-Spacing.XS, 0f, -Spacing.XS, 0f),
                OnClick = open,
                Children = [identity],
            };
        }
        var body = new BoxEl
        {
            Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = ClassicQueueRules.HairlineExtent,
            Padding = new Edges4(Spacing.S, 0f, Spacing.XS, 0f),
            Children =
            [
                identity,
                new BoxEl
                {
                    Width = _tier.AtLeast(ClassicQueueRules.NowHeartLaneW), Height = ClassicQueueRules.HairlineExtent, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Heart(text, _tier)],
                },
            ],
        };
        return new BoxEl
        {
            Key = "np:" + text.Uri + ":classic=True",
            ZStack = true, MinHeight = ClassicQueueRules.HairlineExtent, ClipToBounds = true, Corners = CornerRadius4.All(0f),
            Fill = ColorF.Transparent,
            Enter = ClassicQueue.RowEnter,
            Layout = LayoutTransition.Slide,
            Children = [body, Hairline()],
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
    static Controls.CardData SurfaceData(EntityRef r, QueueRowText text, bool art, Action? click, Action play, bool draggable,
                                         ulong? queueItem, Func<ContextMenuModel?>? menu, Element trailing)
    {
        switch (ClassicQueueRules.FaceOf(r.Kind, text.Thin))
        {
            case ClassicQueueRules.Face.Seed:
                return Controls.CardData.Seed;
            case ClassicQueueRules.Face.Episode:
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
    static Element RowTrailing(QueueRowText text, bool removable, Action remove, ClassicPanelContentLayout tier) => new BoxEl
    {
        Direction = 0, Gap = Spacing.XXS, AlignItems = FlexAlign.Center, Shrink = 0f,
        Children =
        [
            HeartLane(text, tier),
            removable ? CloseGlyph(remove, hairline: false, tier) : new BoxEl { Width = tier.CloseBox, Shrink = 0f },
        ],
    };

    Element SectionHeader(QueuePanelProps props, QueueSection section)
    {
        string title = ClassicQueue.SectionTitle(section);
        var top = new List<Element>(4)
        {
            R.Type.Eyebrow(title) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl(props.Queue.Counts.Of(section).ToString()) { Size = ClassicQueueRules.SmallSize, LineHeight = ClassicQueueRules.SmallLine, Weight = 600, Color = Tok.TextTertiary },
            new BoxEl { Grow = 1f, MinWidth = 0f },
        };
        if (section == QueueSection.Queue && !props.Queue.Viewer)
            top.Add(new BoxEl
            {
                MinHeight = _tier.MinTarget, MinWidth = _tier.MinTarget, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Corners = Radii.ControlAll,
                HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = _clear,
                Children = [new TextEl(Loc.Get(Strings.Player.Clear)) { Size = ClassicQueueRules.SmallSize, LineHeight = ClassicQueueRules.SmallLine, Weight = 600, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
            });
        bool hint = section == QueueSection.Autoplay;
        Element eyebrowRow = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = _tier.AtLeast(ClassicQueueRules.HeaderRowH), Children = top.ToArray() };
        return new BoxEl
        {
            Key = "hdr:" + QueueSections.Tag(section),
            Direction = 1, Gap = Spacing.XXS, Height = hint ? ClassicQueueRules.HeaderSubExtentFor(_tier.Target) : ClassicQueueRules.HeaderExtentFor(_tier.Target),
            Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.XS),
            Layout = LayoutTransition.Slide,
            BlocksDragArm = true,                                              // a slot, never a handle
            Children = hint
                ? [eyebrowRow, new TextEl(Loc.Get(Strings.Player.AutoplayHint)) { Size = ClassicQueueRules.SmallSize, LineHeight = ClassicQueueRules.SmallLine, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                : [eyebrowRow],
        };
    }

    Element ShowMore(QueuePanelProps props, QueueSection section)
    {
        int remaining = props.Lane.Remaining(section);
        var verbs = props.Verbs;
        return new BoxEl
        {
            Key = "more:" + QueueSections.Tag(section),
            Direction = 0, Height = ClassicQueueRules.MoreRowH, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            BlocksDragArm = true, Margin = new Edges4(0f, Spacing.XS, 0f, Spacing.XXS), Corners = Radii.ControlAll,
            Fill = Tok.FillCardSecondary, HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = () => verbs.ShowMore(section),
            Layout = LayoutTransition.Slide,
            Children =
            [
                new TextEl(Icons.ChevronDown) { Size = ClassicQueueRules.GlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextSecondary },
                new TextEl(Strings.Player.ShowNext(Math.Min(props.Lane.PageSize, remaining))) { Size = ClassicQueueRules.SmallSize, LineHeight = ClassicQueueRules.SmallLine, Weight = 600, Color = Tok.TextPrimary },
                new TextEl("·  " + Strings.Player.MoreCount(remaining)) { Size = ClassicQueueRules.SmallSize, LineHeight = ClassicQueueRules.SmallLine, Color = Tok.TextTertiary },
            ],
        };
    }

    static Element PlayingFrom(string source, Action? open, ClassicPanelContentLayout tier)
    {
        bool nav = open is not null;
        return new BoxEl
        {
            Key = "qp:ctx",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = tier.AtLeast(ClassicQueueRules.PlayingFromMinH),
            Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.S), Corners = Radii.ControlAll,
            HoverFill = nav ? R.Layer.RowHover : ColorF.Transparent, Cursor = nav ? CursorId.Hand : CursorId.Arrow,
            OnClick = open,
            Children =
            [
                new SpanTextEl(new[] { new TextSpan(Strings.Player.PlayingFrom(""), Color: Tok.TextSecondary), new TextSpan(source, Weight: 700, Color: Tok.TextPrimary) })
                {
                    Size = ClassicQueueRules.SmallSize, LineHeight = ClassicQueueRules.SmallLine, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, MinWidth = 0f,
                },
                new TextEl(Icons.ChevronRightMed) { Size = ClassicQueueRules.GlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextSecondary },
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
        // A Compact window's toggles are 40 tall (the Compact target rule): their root takes the tier's least height.
        float target = _tier.Target;
        TemplateParts parts = Controls.RootNoShrink;
        if (glyphIsText || target > 0f)
        {
            parts = new TemplateParts { [ToggleButton.PartRoot] = b => target > 0f ? b with { Shrink = 0f, MinHeight = target, Height = float.NaN } : b with { Shrink = 0f } };
            if (glyphIsText) parts.Set<TextEl>(ToggleButton.PartGlyph, t => t with { FontFamily = null, Weight = 600 });
        }
        return ToggleButton.Controlled(label, on, _ => click(), glyph: glyph,
            style: Controls.AccentToggleStyle(_chipAccent.Value), parts: parts);
    }

    /// <summary>The heart's 26-wide lane (40 on a Compact window) — the hairline row's leading cell (<paramref name="height"/> its row) and
    /// the surface's trailing one (NaN: as tall as its cluster).</summary>
    static Element HeartLane(in QueueRowText text, ClassicPanelContentLayout tier, float height = float.NaN) => new BoxEl
    {
        Width = tier.AtLeast(ClassicQueueRules.HeartLaneW), Height = height, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        BlocksDragArm = true, Children = [Heart(text, tier)],
    };

    static Element Heart(in QueueRowText text, ClassicPanelContentLayout tier)
    {
        if (text.Uri.Length == 0) return new BoxEl();
        string uri = text.Uri, title = text.Title;
        float box = tier.HeartBox;
        return Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = 14f, Box = box }) with { Key = "save:" + uri };
    }

    /// <summary>The hairline skin: no artwork, title and artists folded into ONE 14/20 run, the explicit mark pinned after the
    /// ellipsis (the only place the queue shows one). Thin: a single 160×14 bar.</summary>
    static Element Identity(in QueueRowText text, bool nowPlaying)
    {
        ColorF primary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextPrimary, secondary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextSecondary;
        Element identity;
        if (text.Thin) identity = new BoxEl { Width = ClassicQueueRules.ThinBarW, Height = ClassicQueueRules.ThinBarH, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary };
        else
        {
            TextSpan[] spans = text.Artists.Length > 0
                ? new[] { new TextSpan(text.Title, Weight: 600, Color: primary), new TextSpan("  ·  " + text.Artists, Color: secondary) }
                : new[] { new TextSpan(text.Title, Weight: 600, Color: primary) };
            identity = new SpanTextEl(spans)
            {
                Size = ClassicQueueRules.IdentitySize, LineHeight = ClassicQueueRules.IdentityLine, Color = primary, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f,
            };
        }
        Element clip = new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true, Children = [identity] };
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, ClipToBounds = true,
            Children = text.Explicit && !text.Thin ? [clip, Controls.ExplicitBadge(16f, nowPlaying ? Tok.AccentTextPrimary : null)] : [clip],
        };
    }

    static Element Hairline() => new BoxEl
    {
        Key = "classic-hairline", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = 1f,
        Fill = Prop.Of(static () => Tok.StrokeDividerDefault), HitTestVisible = false,
    };

    /// <summary>The rail's ✕ is painted AT REST (no hover-opacity — that is the stage's row, ch 21 §11 #1).</summary>
    static Element CloseGlyph(Action remove, bool hairline, ClassicPanelContentLayout tier) => new BoxEl
    {
        Width = tier.CloseBox, Height = tier.CloseBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = hairline ? CornerRadius4.All(0f) : Radii.ControlAll, HoverFill = R.Layer.RowPressed,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, BlocksDragArm = true, OnClick = remove,
        Children = [new TextEl(Icons.ChromeClose) { Size = ClassicQueueRules.GlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary }],
    };
}
