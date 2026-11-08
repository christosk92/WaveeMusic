// ── Themes/Zune/Structure/Views/Queue/ZuneQueuePanel.cs — Zune's `Queue.Panel` (P5 Z4a) ──────────────────────────────────────────────
//
// Boards Z-Part-Panel (pivot "queue") and Queue: the panel's queue body under the pivot head, on the ground, no plates.
//
//   modes        shuffle · repeat · autoplay as words (13/18), the accent while on; 28 tall (40 at Compact, the touch floor)
//   scroll       "playing from …" (the label cased, the name its own; it opens the context) · the now-playing row (72: the 56 art, the
//                title 17 and the artists 13 in the accent; it opens where the track plays from) · the lane: each section's header at the
//                foot of its 32 (40 at Compact): "next in queue" with "clear", "next up from …", "autoplay"; the rows (the shared media
//                surface at the 36 art, the ✕ as its trailing cell, the autoplay rows at 0.6); "show next n · m more"
//
// The core host (`Queue.RailPanelHost`) keeps what reads and decides (the sections, the page counters, the realized slots, the reorder lane,
// the rows' drag and menus, the verbs). The lane's drag maths needs every slot's height EXACT, so the template states them before it builds
// the lane (`ZuneQueueRules`): a header and "show more" are fixed boxes, a row is the surface's own height.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using Q = Wavee.Themes.Zune.ZuneQueueRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's queue panel.</summary>
internal static class ZuneQueue
{
    /// <summary>The node keys the mounted tests find: the mode row and the now-playing row's prefix.</summary>
    public const string ModesKey = ZunePlayerLayout.QueueModesKey, NowKeyPrefix = "np:";

    /// <summary>The registered template of <see cref="ViewKeys.QueuePanel"/>.</summary>
    public static Element PanelTemplate(QueuePanelProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZuneQueuePanelView());

    internal static EnterExit RowEnter => new(Dy: Q.EnterDy, Opacity: 0f, Active: true);

    internal static EnterExit RowExit => new(Dy: -Q.ExitDy, Opacity: 0f, Active: true);
}

/// <summary>The queue body's picture.</summary>
internal sealed class ZuneQueuePanelView : Component
{
    readonly ScrollHandle _scroll = new();
    // The body is ONE virtualized list (only the rows the viewport shows, and its overscan, are built): the lead ("playing from", the
    // now-playing row), the lane's slots, then the vacancy. Every item's extent is stated EXACTLY (the vacancy's is measured), so the
    // list's geometry matches the lane's drag maths and nothing jumps as rows realize.
    readonly FluentGpu.Scene.ExtentTableVirtualLayout _layout = new();
    float[] _extents = new float[64];
    int[] _kinds = new int[64];   // per item: a lane slot position (>= 0), or one of the Item* kinds
    const int ItemPlayingFrom = -1, ItemNow = -2, ItemVacancy = -3;
    /// <summary>The vacancy's estimate until its first measure.</summary>
    const float VacancyEstimate = 120f;
    Func<QueueSlot, float>? _extentOf;
    Action? _toggleShuffle, _cycleRepeat, _toggleAutoplay, _clear;
    WidthTier _window = WidthTier.Standard;
    ZunePanelInk? _ink;   // the panel's ink while its cover wash is drawn (null: the roles' own)

    QueueProps? _queue;   // this render's queue: a row's extent depends on whether it carries the ✕

    float ExtentOf(QueueSlot slot) => slot.Kind switch
    {
        QueueSlotKind.Header => Q.Header(_window),
        QueueSlotKind.More => Q.MoreExtent,
        _ => RowExtentOf(slot),
    };

    /// <summary>A row's EXACT extent: the surface's padding round the art and text column (<see cref="Q.RowExtent"/>), or round the ✕'s 40 box
    /// where the row carries one (it is the row's tallest cell: 56, where a row without it is 54).</summary>
    float RowExtentOf(QueueSlot slot)
        => _queue is { } queue && queue.TryRow(slot.Section, slot.Pos, out var row) && row.Removable
            ? MathF.Max(Q.RowExtent, 2f * SurfaceGeometry.RowPad + Q.CloseBox)
            : Q.RowExtent;

    public override Element Render()
    {
        var props = UseProps<QueuePanelProps>();
        var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
        var tiers = facts.Value.Tiers;
        _window = tiers.Policy is null ? WidthTier.Standard : tiers.Window;
        _ink = UseContext(ZunePanelInk.Current);
        var queue = props.Queue;
        var lane = props.Lane;
        _toggleShuffle ??= props.Player.ToggleShuffle;
        _cycleRepeat ??= props.Player.CycleRepeat;
        _toggleAutoplay ??= props.Verbs.ToggleAutoplay;
        _clear ??= props.Verbs.Clear;
        _queue = queue;
        lane.SetMetrics(Q.RowExtent, _extentOf ??= ExtentOf);

        // The items, in list order: the lead, the lane's slots (under the live projection while a row is lifted, so each position states the
        // extent of the slot shown there), the vacancy.
        int count = lane.SlotCount + 3;
        if (_extents.Length < count) { _extents = new float[count * 2]; _kinds = new int[count * 2]; }
        int n = 0;
        float lead = 0f;
        if (queue.PlayingFrom is not null) { _kinds[n] = ItemPlayingFrom; _extents[n++] = Q.PlayingFromH; lead += Q.PlayingFromH; }
        if (!queue.NowPlaying.IsNone) { _kinds[n] = ItemNow; _extents[n++] = Q.NowH; lead += Q.NowH; }
        for (int i = 0; i < lane.SlotCount; i++) { _kinds[n] = i; _extents[n++] = ExtentOf(lane.SlotAt(lane.ItemAt(i))); }
        if (queue.NothingPlaying) { _kinds[n] = ItemVacancy; _extents[n++] = -VacancyEstimate; }
        _layout.SetExtents(_extents.AsSpan(0, n), 0f, Q.BottomRoom);

        var list = Virtual.Custom(n, _layout, i => Item(props, i)) with
        {
            Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "queuepanel", Handle = _scroll,
        };
        // The lane wraps the whole list (a foreign drop anywhere inserts at the slot under it; above the slots, at slot 0).
        var body = lane.List(list, "lane:upcoming", lead).As<BoxEl>() with { Grow = 1f, MinHeight = 0f };

        float modeH = Q.ModeRow(_window);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Key = ZuneQueue.ModesKey, Height = modeH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = Q.ModeRowPadding,
                    Children =
                    [
                        ZunePlayerBar.Word(Loc.Get(Strings.Player.Shuffle), _toggleShuffle, true, queue.Shuffle, modeH, Q.WordSize, latchedInk: _ink?.Accent) with { Key = "mode:shuffle" },
                        ZunePlayerBar.Word(Loc.Get(Strings.Player.Repeat), _cycleRepeat, true, props.RepeatOn, modeH, Q.WordSize, latchedInk: _ink?.Accent) with { Key = "mode:repeat" },
                        ZunePlayerBar.Word(Loc.Get(Strings.Player.Autoplay), _toggleAutoplay, true, queue.Autoplay.Enabled, modeH, Q.WordSize, latchedInk: _ink?.Accent) with { Key = "mode:autoplay" },
                    ],
                },
                body,
            ],
        };
    }

    /// <summary>The list's item <paramref name="i"/> (<see cref="_kinds"/>), inset by the body's side padding.</summary>
    Element Item(QueuePanelProps props, int i)
    {
        var lane = props.Lane;
        int kind = (uint)i < (uint)_kinds.Length ? _kinds[i] : ItemVacancy;
        switch (kind)
        {
            case ItemPlayingFrom: return Inset(PlayingFrom(props.Queue.PlayingFrom.GetValueOrDefault().Name, props.OpenPlayingFrom, _ink));
            case ItemNow: return Inset(NowRow(props, _ink));
            case ItemVacancy:
                return Inset(new BoxEl
                {
                    Key = "qp:empty", Direction = 1,
                    Children = [Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.NothingPlaying), subtitle: "")],
                });
        }
        int item = lane.ItemAt(kind);
        var slot = lane.SlotAt(item);
        if (slot.Kind == QueueSlotKind.Header) return Inset(Header(props, slot.Section));
        if (slot.Kind == QueueSlotKind.More) return Inset(ShowMore(props, slot.Section, _ink));
        return props.Queue.TryRow(slot.Section, slot.Pos, out var row)
            ? Inset(lane.Lane(item, Row(props, row), lane.RowKey("", row.ItemId, row.Index)))
            : Inset(new BoxEl { Key = "qp:gap:" + kind });
    }

    /// <summary>An item's root inset by the body's side padding (its own margins kept: they lie inside its stated extent).</summary>
    static Element Inset(Element e)
    {
        var b = e.As<BoxEl>();
        return b with
        {
            Margin = new Edges4(b.Margin.Left + ZunePanelBodies.Inset.Left, b.Margin.Top, b.Margin.Right + ZunePanelBodies.Inset.Right, b.Margin.Bottom),
        };
    }

    /// <summary>A queue row: the shared media surface (the plate, the hand, the play FAB, the right-click menu are the surface's) at the 36 art
    /// with the ✕ as its trailing cell (or its width, so every row's text ends at one line). The surface is built from the row's DATA
    /// (<see cref="RowFace"/>, with the lane's digest of what it shows) by a static builder, so an unchanged row is an equal push and skips its
    /// render when the panel re-renders; only the rows that changed rebuild.</summary>
    Element Row(QueuePanelProps props, in QueueRowProps row)
    {
        var face = new RowFace(props.Verbs, props.Lane, row.Ref, row.ItemId, row.Index, row.Removable, row.Draggable, props.Queue.Viewer,
                               props.Lane.RowStamp(row.Ref));
        return new BoxEl
        {
            Key = props.Lane.RowKey("", row.ItemId, row.Index),
            Direction = 1, Opacity = Q.Dim(row.Section),
            Enter = ZuneQueue.RowEnter, Exit = ZuneQueue.RowExit, Layout = LayoutTransition.Slide,
            Children = [Controls.FromData(face, s_rowSurface)],
        };
    }

    /// <summary>What a row's surface is built from (<see cref="Row"/>).</summary>
    readonly record struct RowFace(IQueueVerbs Verbs, IQueueLane Lane, EntityRef R, ulong ItemId, int Index, bool Removable, bool Draggable,
                                   bool Viewer, ulong Stamp);

    static readonly Func<RowFace, Element> s_rowSurface = RowSurface;

    static Element RowSurface(RowFace d)
    {
        var verbs = d.Verbs;
        EntityRef r = d.R;
        ulong itemId = d.ItemId;
        int index = d.Index;
        bool viewer = d.Viewer;
        var text = d.Lane.TextOf(r);
        Action skip = () => verbs.Play(itemId, index, r);
        Element trailing = d.Removable
            ? ToolTip.Wrap(
                ZunePlayerBar.Glyph(Icons.ChromeClose, () => verbs.Remove(itemId, index, r), true, Tok.TextTertiary, Q.CloseBox, Q.CloseGlyph)
                    with { BlocksDragArm = true }, Loc.Get(Strings.Menu.RemoveFromQueue))
            : new BoxEl { Width = Q.CloseBox, Shrink = 0f };
        var data = SurfaceData(r, text, click: skip, play: skip, draggable: d.Draggable, queueItem: itemId,
            menu: () => verbs.RowMenu(itemId, index, r, viewer), trailing: trailing);
        var shape = Shape.Row(Q.RowArt) with { MinHeight = Q.RowExtent, PadX = ZunePanelBodies.RowPadX };
        return Controls.Surface(data, shape);
    }

    /// <summary>The surface's data through the entity's own adapter: the seed face while the title is unknown, the episode adapter, else the
    /// track adapter over the row the track displays (the uri stays the queue's own); a ruled-unavailable track keeps its "unavailable".</summary>
    static Controls.CardData SurfaceData(EntityRef r, QueueRowText text, Action? click, Action play, bool draggable, ulong? queueItem,
        Func<ContextMenuModel?>? menu, Element trailing)
    {
        switch (Q.FaceOf(r.Kind, text.Thin))
        {
            case Q.Face.Seed:
                return Controls.CardData.Seed;
            case Q.Face.Episode:
            {
                var episode = new Episode(r.Slot);
                var facts = Episode.RowFactsOf(episode);
                var data = Episode.RowData(episode, in facts,
                    new Episode.RowOptions(OnClick: click, OnPlay: play, ShowMeta: false, ShowGoToShow: true, Trailing: trailing))
                    with { ShowMenu = QueueRowRules.ShowsMenuButton };
                return click is null ? data with { OnClick = null } : data;
            }
            default:
            {
                var options = new Track.RowDataOptions(OnClick: click, OnPlay: play, ShowArtwork: true, ShowExplicit: false,
                    Draggable: draggable, ShowMenu: QueueRowRules.ShowsMenuButton, QueueItemId: queueItem, Trailing: trailing,
                    Tracked: false);   // the host re-pushes when a shown row changes (its rows digest)
                var data = Track.RowData(new Track(r.Slot).ForDisplay, in options) with { Uri = text.Uri };
                if (menu is not null) data = data with { Menu = menu };
                return text.Unavailable ? data with { Title = text.Title, Subtitle = null, CoverUrl = null } : data;
            }
        }
    }

    /// <summary>"playing from" (cased) and the context's name (its own casing), at the foot of its 28; it opens the context when it has a page.</summary>
    static Element PlayingFrom(string source, Action? open, ZunePanelInk? ink) => new BoxEl
    {
        Key = "qp:ctx", Height = Q.PlayingFromH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.End, Gap = ZunePlayerLayout.FromGap,
        Cursor = open is null ? null : CursorId.Hand, Role = open is null ? AutomationRole.None : AutomationRole.Hyperlink, Focusable = open is not null, OnClick = open,
        Children =
        [
            new TextEl(Loc.Get(Strings.Theme.Zune.Player.PlayingFrom)) { Size = Q.WordSize, LineHeight = Q.WordLine, Color = ink?.Tertiary ?? Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Shrink = 0f },
            new TextEl(source) { Size = Q.WordSize, LineHeight = Q.WordLine, Color = ink?.Tertiary ?? Tok.TextTertiary, HoverColor = open is null ? ink?.Tertiary ?? Tok.TextTertiary : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
        ],
    };

    /// <summary>The now-playing row: keyed by the track, so a track change enters with a fade (the cross-fade a row cannot do).</summary>
    static Element NowRow(QueuePanelProps props, ZunePanelInk? ink)
    {
        var current = props.Queue.NowPlaying;
        var text = props.Lane.TextOf(current);
        var open = props.OpenNowPlaying;
        Element lines = text.Thin
            ? new BoxEl { Width = ZunePanelBodies.SkeletonDetailW, Height = ZunePanelBodies.SkeletonBarH, Fill = Tok.FillSubtleSecondary }
            : new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Q.NowLinesGap,
                Children =
                [
                    new TextEl(text.Title) { Size = Q.NowTitleSize, LineHeight = Q.NowTitleLine, Color = ink?.Accent ?? Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    new TextEl(text.Artists)
                    {
                        Size = Q.WordSize, LineHeight = Q.WordLine, Color = ink?.Accent ?? Tok.AccentTextPrimary with { A = Q.NowSubA }, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        return new BoxEl
        {
            Key = ZuneQueue.NowKeyPrefix + text.Uri, Height = Q.NowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
            Cursor = open is null ? null : CursorId.Hand, Role = open is null ? AutomationRole.None : AutomationRole.Button, Focusable = open is not null, OnClick = open,
            Enter = ZuneQueue.RowEnter, Layout = LayoutTransition.Slide,
            Children =
            [
                new BoxEl
                {
                    Width = Q.NowArtCol, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                    Children = [Controls.Artwork(text.Art, Q.NowArt, Q.NowArt, R.Shape.For(ShapeRole.Cover))],
                },
                lines,
            ],
        };
    }

    /// <summary>A section's header at the foot of its slot: the section's name (cased), the source's name after "next up from", and "clear".</summary>
    Element Header(QueuePanelProps props, QueueSection section)
    {
        var queue = props.Queue;
        var kids = new List<Element>(3);
        var @case = R.Case.For(LabelKind.Label);
        float h = Q.Header(_window);
        if (section == QueueSection.NextUp && queue.PlayingFrom is { Name.Length: > 0 } src)
        {
            kids.Add(new TextEl(Loc.Get(Strings.Theme.Zune.Player.NextUpFrom)) { Size = Q.WordSize, LineHeight = Q.WordLine, Color = _ink?.Tertiary ?? Tok.TextTertiary, Case = @case, Shrink = 0f });
            kids.Add(new TextEl(src.Name) { Size = Q.WordSize, LineHeight = Q.WordLine, Color = _ink?.Tertiary ?? Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f });
        }
        else
        {
            string title = Loc.Get(section switch
            {
                QueueSection.Queue => Strings.Player.NextInQueue,
                QueueSection.NextUp => Strings.Player.NextUp,
                _ => Strings.Player.Autoplay,
            });
            kids.Add(new TextEl(title) { Size = Q.WordSize, LineHeight = Q.WordLine, Color = _ink?.Tertiary ?? Tok.TextTertiary, Case = @case, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f });
        }
        if (section == QueueSection.Queue && !queue.Viewer)
            kids.Add(new BoxEl
            {
                Height = h, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.End, Margin = new Edges4(Q.HeaderGap, 0f, 0f, 0f),
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = _clear,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Player.Clear))
                    {
                        Size = Q.ClearSize, LineHeight = Q.ClearLine, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command),
                    },
                ],
            });
        return new BoxEl
        {
            Key = "hdr:" + QueueSections.Tag(section), Height = h, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.End, Gap = ZunePlayerLayout.FromGap,
            Padding = new Edges4(0f, 0f, 0f, Q.HeaderPadBottom), Layout = LayoutTransition.Slide, BlocksDragArm = true,
            Children = kids.ToArray(),
        };
    }

    static Element ShowMore(QueuePanelProps props, QueueSection section, ZunePanelInk? ink)
    {
        int remaining = props.Lane.Remaining(section);
        var verbs = props.Verbs;
        var @case = R.Case.For(LabelKind.Command);
        return new BoxEl
        {
            Key = "more:" + QueueSections.Tag(section), Height = Q.MoreH, Shrink = 0f, Margin = new Edges4(0f, Q.MoreTop, 0f, 0f),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = ZunePlayerLayout.FromGap, BlocksDragArm = true,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = () => verbs.ShowMore(section), Layout = LayoutTransition.Slide,
            Children =
            [
                new TextEl(Strings.Player.ShowNext(Math.Min(props.Lane.PageSize, remaining))) { Size = Q.WordSize, LineHeight = Q.WordLine, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, Case = @case },
                new TextEl("· " + Strings.Player.MoreCount(remaining)) { Size = Q.WordSize, LineHeight = Q.WordLine, Color = ink?.Tertiary ?? Tok.TextTertiary, Case = @case },
            ],
        };
    }
}
