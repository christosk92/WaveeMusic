// ── Themes/WinUi/Structure/Views/Queue/WinUiQueuePanel.cs — WinUI's `Queue.Panel` (P6 W4a) ────────────────────────────────────────────
//
// Boards W-Queue (Std), W-Queue-Small / -Compact / -Huge and the Queue tab of the panes in W-Shell-*: the pane's queue body under its tabs.
//
//   "Playing from <context>"   Caption 12, the name an accent link that opens the context
//   the now-playing card       the subtle card (r8): the 48 cover, the title in the accent (Body Strong) over the artists, the accent level
//                              bars at its end; it opens where the track plays from
//   the lane                   each section's header (Body Strong 14): "Next in queue" with "Clear" (an accent text button), "Next up" with
//                              "<n> more" (Caption, secondary), "Autoplay"; the rows (the shared media surface at the 40 cover, the ✕ as its
//                              trailing cell, the autoplay rows dimmed); "Show next n · m more"
//   Autoplay                   a divider, then the Autoplay row (Body Strong over "Similar music will keep playing") with its ToggleSwitch
//
// The core host (`Queue.RailPanelHost`) keeps what reads and decides (the sections, the page counters, the realized slots, the reorder lane,
// the rows' drag and menus, the verbs). The lane's drag maths needs every slot's height EXACT, so the template states them before it builds
// the lane (`WinUiQueueRules`): a header and "show more" are fixed boxes, a row is the surface's own height. At Compact the header and
// "show more" rows grow to 40 (the touch floor: "Clear" is a 40 target).

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

using PL = Wavee.Themes.WinUi.WinUiPlayerLayout;
using Q = Wavee.Themes.WinUi.WinUiQueueRules;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's queue panel.</summary>
internal static class WinUiQueue
{
    /// <summary>The node keys the mounted tests find: the now-playing card's prefix, the autoplay row.</summary>
    public const string NowKeyPrefix = "np:", AutoplayKey = "winui-queue-autoplay";

    /// <summary>The registered template of <see cref="ViewKeys.QueuePanel"/>.</summary>
    public static Element PanelTemplate(QueuePanelProps props, in ViewContext ctx) => Embed.Comp(props, static () => new WinUiQueuePanelView());

    internal static EnterExit RowEnter => new(Dy: Q.EnterDy, Opacity: 0f, Active: true);

    internal static EnterExit RowExit => new(Dy: -Q.ExitDy, Opacity: 0f, Active: true);
}

/// <summary>The queue body's picture.</summary>
internal sealed class WinUiQueuePanelView : Component
{
    readonly ScrollHandle _scroll = new();
    Func<QueueSlot, float>? _extentOf;
    Action? _clear;
    Action<bool>? _setAutoplay;
    WidthTier _window = WidthTier.Standard;

    float ExtentOf(QueueSlot slot) => slot.Kind switch
    {
        QueueSlotKind.Header => Q.Header(_window),
        QueueSlotKind.More => Q.MoreExtent(_window),
        _ => Q.RowExtent,
    };

    public override Element Render()
    {
        var props = UseProps<QueuePanelProps>();
        var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
        var tiers = facts.Value.Tiers;
        _window = tiers.Policy is null ? WidthTier.Standard : tiers.Window;
        var queue = props.Queue;
        var lane = props.Lane;
        _clear ??= props.Verbs.Clear;
        _setAutoplay ??= props.Verbs.SetAutoplay;
        bool autoplayOn = queue.Autoplay.Enabled;
        var autoplay = UseSignal(autoplayOn);
        UseLayoutEffect(() => { if (autoplay.Peek() != autoplayOn) autoplay.Value = autoplayOn; }, DepKey.From(autoplayOn ? 1 : 0));
        lane.SetMetrics(Q.RowExtent, _extentOf ??= ExtentOf);

        var content = new List<Element>(5);
        if (queue.PlayingFrom is { } src) content.Add(PlayingFrom(src.Name, props.OpenPlayingFrom));
        if (!queue.NowPlaying.IsNone) content.Add(NowCard(props));
        if (lane.SlotCount > 0) content.Add(lane.List(Upcoming(props), "lane:upcoming"));
        if (queue.NothingPlaying)
            content.Add(Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.NothingPlaying), subtitle: ""));
        else if (!queue.Viewer)
            content.Add(AutoplayRow(autoplay, _setAutoplay));

        Element body = new BoxEl
        {
            Direction = 1, MinHeight = 0f, Padding = Q.BodyPadding,
            Children = content.ToArray(),
        };
        // Nothing upcoming: the same lane wraps the body, so a foreign drop still has a target (slot 0 = play next).
        if (lane.SlotCount == 0) body = lane.List(body, "lane:empty");

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
            Children = [new ScrollEl { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "queuepanel", Handle = _scroll, Content = body }],
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
            if (slot.Kind == QueueSlotKind.More) { kids[i] = ShowMore(props, slot.Section); continue; }
            kids[i] = props.Queue.TryRow(slot.Section, slot.Pos, out var row)
                ? lane.Lane(item, Row(props, row), lane.RowKey("", row.ItemId, row.Index))
                : new BoxEl();
        }
        return new BoxEl { Key = "upcoming", Direction = 1, Children = kids };
    }

    /// <summary>A queue row: the shared media surface (the plate, the hand, the play button, the right-click menu are the surface's) at the 40
    /// cover with the ✕ as its trailing cell (or its width, so every row's text ends at one line).</summary>
    Element Row(QueuePanelProps props, in QueueRowProps row)
    {
        var verbs = props.Verbs;
        EntityRef r = row.Ref;
        ulong itemId = row.ItemId;
        int index = row.Index;
        bool viewer = props.Queue.Viewer;
        var text = props.Lane.TextOf(r);
        Action skip = () => verbs.Play(itemId, index, r);
        // The ✕ is built from its data (the verbs, the entry and the window tier) by a static builder, so an unchanged row is an equal push
        // and its surface does not re-render when the panel does (Controls.FromData).
        Element trailing = row.Removable
            ? Controls.FromData((verbs, itemId, index, r, _window), static d => ToolTip.Wrap(
                WinUiPlayerBar.Button(Icons.ChromeClose, () => d.verbs.Remove(d.itemId, d.index, d.r), true, false, Q.CloseOf(d._window), Q.CloseOf(d._window), Q.CloseGlyph)
                    with { BlocksDragArm = true }, Loc.Get(Strings.Menu.RemoveFromQueue)))
            : new BoxEl { Width = Q.CloseOf(_window), Shrink = 0f };
        var data = SurfaceData(r, text, click: skip, play: skip, draggable: row.Draggable, queueItem: itemId,
            menu: () => verbs.RowMenu(itemId, index, r, viewer), trailing: trailing);
        var shape = Shape.Row(Q.RowArt) with { MinHeight = Q.RowExtent };
        return new BoxEl
        {
            Key = props.Lane.RowKey("", itemId, index),
            Direction = 1, Opacity = Q.Dim(row.Section),
            Enter = WinUiQueue.RowEnter, Exit = WinUiQueue.RowExit, Layout = LayoutTransition.Slide,
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

    /// <summary>"Playing from" and the context's name as an accent link (when it has a page).</summary>
    static Element PlayingFrom(string source, Action? open) => new BoxEl
    {
        Key = "qp:ctx", Height = Q.PlayingFromH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Q.FromGap,
        Children =
        [
            new TextEl(Loc.Get(Strings.Theme.Winui.Player.PlayingFrom)) { Size = Q.SmallSize, LineHeight = Q.SmallLine, Color = Tok.TextSecondary, Shrink = 0f },
            new BoxEl
            {
                Shrink = 1f, MinWidth = 0f, ClipToBounds = true,
                Cursor = open is null ? null : CursorId.Hand, Role = open is null ? AutomationRole.Text : AutomationRole.Hyperlink,
                Focusable = open is not null, OnClick = open,
                Children =
                [
                    new TextEl(source)
                    {
                        Size = Q.SmallSize, LineHeight = Q.SmallLine, Color = open is null ? Tok.TextSecondary : Tok.AccentTextPrimary,
                        HoverColor = open is null ? Tok.TextSecondary : Tok.AccentTextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            },
        ],
    };

    /// <summary>The now-playing card: keyed by the track, so a track change enters with a fade.</summary>
    static Element NowCard(QueuePanelProps props)
    {
        var current = props.Queue.NowPlaying;
        var text = props.Lane.TextOf(current);
        var open = props.OpenNowPlaying;
        Element lines = text.Thin
            ? new BoxEl { Width = Q.SkeletonW, Height = Q.SkeletonH, Corners = CornerRadius4.All(Q.SkeletonH / 2f), Fill = Tok.FillSubtleSecondary }
            : new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    new TextEl(text.Title) { Size = Q.TitleSize, LineHeight = Q.TitleLine, Weight = Q.TitleWeight, Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    new TextEl(text.Artists) { Size = Q.SmallSize, LineHeight = Q.SmallLine, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                ],
            };
        return new BoxEl
        {
            Key = WinUiQueue.NowKeyPrefix + text.Uri, Height = Q.NowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Q.NowGap,
            Padding = Q.NowPadding, Margin = Q.NowMargin, Corners = CornerRadius4.All(Q.CardRadius), Fill = Tok.FillCardDefault,
            HoverFill = open is null ? Tok.FillCardDefault : Tok.FillCardSecondary,
            Cursor = open is null ? null : CursorId.Hand, Role = open is null ? AutomationRole.None : AutomationRole.Button, Focusable = open is not null, OnClick = open,
            Enter = WinUiQueue.RowEnter, Layout = LayoutTransition.Slide,
            Children =
            [
                Controls.Artwork(text.Art, Q.NowArt, Q.NowArt, Q.ArtCorner),
                lines,
                new TextEl(Q.LevelGlyph) { Size = Q.LevelGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.AccentTextPrimary, Shrink = 0f },
            ],
        };
    }

    /// <summary>A section's header: the name (Body Strong), and at its end "Clear" (the queue) or "<n> more" (next up).</summary>
    Element Header(QueuePanelProps props, QueueSection section)
    {
        var queue = props.Queue;
        float h = Q.Header(_window);
        string title = Loc.Get(section switch
        {
            QueueSection.Queue => Strings.Player.NextInQueue,
            QueueSection.NextUp => Strings.Player.NextUp,
            _ => Strings.Player.Autoplay,
        });
        var kids = new List<Element>(2)
        {
            new TextEl(title) { Size = Q.HeaderSize, LineHeight = Q.HeaderLine, Weight = Q.TitleWeight, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f, Grow = 1f },
        };
        if (section == QueueSection.Queue && !queue.Viewer)
            kids.Add(new BoxEl
            {
                Height = h, MinWidth = Q.ClearMinW, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = _clear,
                Children = [new TextEl(Loc.Get(Strings.Player.Clear)) { Size = Q.SmallSize, LineHeight = Q.SmallLine, Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary }],
            });
        else if (section == QueueSection.NextUp && props.Lane.Remaining(section) > 0)
            kids.Add(new TextEl(Strings.Player.MoreCount(props.Lane.Remaining(section))) { Size = Q.SmallSize, LineHeight = Q.SmallLine, Color = Tok.TextSecondary, Shrink = 0f });
        return new BoxEl
        {
            Key = "hdr:" + QueueSections.Tag(section), Height = h, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
            Layout = LayoutTransition.Slide, BlocksDragArm = true,
            Children = kids.ToArray(),
        };
    }

    Element ShowMore(QueuePanelProps props, QueueSection section)
    {
        int remaining = props.Lane.Remaining(section);
        var verbs = props.Verbs;
        return new BoxEl
        {
            Key = "more:" + QueueSections.Tag(section), Height = Q.MoreH(_window), Shrink = 0f, Margin = new Edges4(0f, Q.MoreTop, 0f, 0f),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Q.FromGap, BlocksDragArm = true, Padding = Q.MorePadding,
            Corners = CornerRadius4.All(PL.ButtonCorner), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = () => verbs.ShowMore(section), Layout = LayoutTransition.Slide,
            Children =
            [
                new TextEl(Strings.Player.ShowNext(Math.Min(props.Lane.PageSize, remaining))) { Size = Q.HeaderSize, LineHeight = Q.HeaderLine, Color = Tok.TextPrimary },
                new TextEl("· " + Strings.Player.MoreCount(remaining)) { Size = Q.SmallSize, LineHeight = Q.SmallLine, Color = Tok.TextSecondary },
            ],
        };
    }

    /// <summary>The divider, then "Autoplay" over its hint with the switch at the row's end.</summary>
    static Element AutoplayRow(Signal<bool> on, Action<bool> set) => new BoxEl
    {
        Key = WinUiQueue.AutoplayKey, Direction = 1, Shrink = 0f, Margin = Q.AutoplayMargin,
        Children =
        [
            new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault },
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Padding = Q.AutoplayPadding, Gap = Q.FromGap,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Player.Autoplay)) { Size = Q.HeaderSize, LineHeight = Q.HeaderLine, Weight = Q.TitleWeight, Color = Tok.TextPrimary },
                            new TextEl(Loc.Get(Strings.Player.AutoplayHint)) { Size = Q.SmallSize, LineHeight = Q.SmallLine, Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap, MinWidth = 0f },
                        ],
                    },
                    // The bare switch (no 154 content column), so it ends at the pane's content edge (W-Queue).
                    ToggleSwitch.Create(on, set, onContent: "", offContent: "", style: ToggleSwitch.DefaultStyle with { MinWidth = 0f }),
                ],
            },
        ],
    };
}
