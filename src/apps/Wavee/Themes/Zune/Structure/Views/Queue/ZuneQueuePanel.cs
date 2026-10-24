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
    Func<QueueSlot, float>? _extentOf;
    Action? _toggleShuffle, _cycleRepeat, _toggleAutoplay, _clear;
    WidthTier _window = WidthTier.Standard;
    ZunePanelInk? _ink;   // the panel's ink while its cover wash is drawn (null: the roles' own)

    float ExtentOf(QueueSlot slot) => slot.Kind switch
    {
        QueueSlotKind.Header => Q.Header(_window),
        QueueSlotKind.More => Q.MoreExtent,
        _ => Q.RowExtent,
    };

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
        lane.SetMetrics(Q.RowExtent, _extentOf ??= ExtentOf);

        var content = new List<Element>(4);
        if (queue.PlayingFrom is { } src) content.Add(PlayingFrom(src.Name, props.OpenPlayingFrom, _ink));
        if (!queue.NowPlaying.IsNone) content.Add(NowRow(props, _ink));
        if (lane.SlotCount > 0) content.Add(lane.List(Upcoming(props), "lane:upcoming"));
        if (queue.NothingPlaying)
            content.Add(Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.NothingPlaying), subtitle: ""));

        Element body = new BoxEl
        {
            Direction = 1, MinHeight = 0f, Padding = new Edges4(ZunePanelBodies.Inset.Left, 0f, ZunePanelBodies.Inset.Right, Q.BottomRoom),
            Children = content.ToArray(),
        };
        // Nothing upcoming: the same lane wraps the body, so a foreign drop still has a target (slot 0 = play next).
        if (lane.SlotCount == 0) body = lane.List(body, "lane:empty");

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
                new ScrollEl { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "queuepanel", Handle = _scroll, Content = body },
            ],
        };
    }

    Element Upcoming(QueuePanelProps props)
    {
        var lane = props.Lane;
        var kids = new Element[lane.SlotCount];
        for (int i = 0; i < kids.Length; i++)
        {
            int item = lane.ItemAt(i);
            var slot = lane.SlotAt(item);
            if (slot.Kind == QueueSlotKind.Header) { kids[i] = Header(props, slot.Section); continue; }
            if (slot.Kind == QueueSlotKind.More) { kids[i] = ShowMore(props, slot.Section, _ink); continue; }
            kids[i] = props.Queue.TryRow(slot.Section, slot.Pos, out var row)
                ? lane.Lane(item, Row(props, row), lane.RowKey("", row.ItemId, row.Index))
                : new BoxEl();
        }
        return new BoxEl { Key = "upcoming", Direction = 1, Children = kids };
    }

    /// <summary>A queue row: the shared media surface (the plate, the hand, the play FAB, the right-click menu are the surface's) at the 36 art
    /// with the ✕ as its trailing cell (or its width, so every row's text ends at one line).</summary>
    Element Row(QueuePanelProps props, in QueueRowProps row)
    {
        var verbs = props.Verbs;
        EntityRef r = row.Ref;
        ulong itemId = row.ItemId;
        int index = row.Index;
        bool viewer = props.Queue.Viewer;
        var text = props.Lane.TextOf(r);
        Action skip = () => verbs.Play(itemId, index, r);
        // The ✕ is built from its data (the verbs and the entry) by a static builder, so an unchanged row is an equal push and its surface
        // does not re-render when the panel does (Controls.FromData).
        Element trailing = row.Removable
            ? Controls.FromData((verbs, itemId, index, r), static d => ToolTip.Wrap(
                ZunePlayerBar.Glyph(Icons.ChromeClose, () => d.verbs.Remove(d.itemId, d.index, d.r), true, Tok.TextTertiary, Q.CloseBox, Q.CloseGlyph)
                    with { BlocksDragArm = true }, Loc.Get(Strings.Menu.RemoveFromQueue)))
            : new BoxEl { Width = Q.CloseBox, Shrink = 0f };
        var data = SurfaceData(r, text, click: skip, play: skip, draggable: row.Draggable, queueItem: itemId,
            menu: () => verbs.RowMenu(itemId, index, r, viewer), trailing: trailing);
        var shape = Shape.Row(Q.RowArt) with { MinHeight = Q.RowExtent, PadX = ZunePanelBodies.RowPadX };
        return new BoxEl
        {
            Key = props.Lane.RowKey("", itemId, index),
            Direction = 1, Opacity = Q.Dim(row.Section),
            Enter = ZuneQueue.RowEnter, Exit = ZuneQueue.RowExit, Layout = LayoutTransition.Slide,
            Children = [Controls.Surface(data, shape)],
        };
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
                var options = new Track.RowDataOptions(OnClick: click, OnPlay: play, ShowArtwork: true, ShowExplicit: false, ShowVideo: true,
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
