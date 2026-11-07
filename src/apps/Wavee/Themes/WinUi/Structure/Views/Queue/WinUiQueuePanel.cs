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
    // The body is ONE virtualized list (only the rows the viewport shows, and its overscan, are built): the lead (Playing from, the
    // now-playing card), the lane's slots, then the tail (the vacancy or the Autoplay row). Every item's extent is stated EXACTLY
    // (the tail's is measured), so the list's geometry matches the lane's drag maths and nothing jumps as rows realize.
    readonly FluentGpu.Scene.ExtentTableVirtualLayout _layout = new();
    float[] _extents = new float[64];
    int[] _kinds = new int[64];   // per item: a lane slot position (>= 0), or one of the Item* kinds
    const int ItemPlayingFrom = -1, ItemNow = -2, ItemVacancy = -3, ItemAutoplay = -4;
    /// <summary>The tail's estimate until its first measure (the Autoplay row: the divider, the title and a one-line hint).</summary>
    const float TailEstimate = 72f;
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

        // The items, in list order: the lead, the lane's slots (under the live projection while a row is lifted, so each position states the
        // extent of the slot shown there), the tail.
        int count = lane.SlotCount + 3;
        if (_extents.Length < count) { _extents = new float[count * 2]; _kinds = new int[count * 2]; }
        int n = 0;
        float lead = Q.BodyPadding.Top;
        if (queue.PlayingFrom is not null) { _kinds[n] = ItemPlayingFrom; _extents[n++] = Q.PlayingFromH; lead += Q.PlayingFromH; }
        if (!queue.NowPlaying.IsNone)
        {
            float card = Q.NowH + Q.NowMargin.Top + Q.NowMargin.Bottom;
            _kinds[n] = ItemNow; _extents[n++] = card; lead += card;
        }
        for (int i = 0; i < lane.SlotCount; i++) { _kinds[n] = i; _extents[n++] = ExtentOf(lane.SlotAt(lane.ItemAt(i))); }
        if (queue.NothingPlaying) { _kinds[n] = ItemVacancy; _extents[n++] = -TailEstimate; }
        else if (!queue.Viewer) { _kinds[n] = ItemAutoplay; _extents[n++] = -TailEstimate; }
        _layout.SetExtents(_extents.AsSpan(0, n), Q.BodyPadding.Top, Q.BodyPadding.Bottom);

        var body = Virtual.Custom(n, _layout, i => Item(props, autoplay, i)) with
        {
            Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "queuepanel", Handle = _scroll,
        };
        // The lane wraps the whole list (a foreign drop anywhere inserts at the slot under it; above the slots, at slot 0).
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
            Children = [lane.List(body, "lane:upcoming", lead).As<BoxEl>() with { Grow = 1f, MinHeight = 0f }],
        };
    }

    /// <summary>The list's item <paramref name="i"/> (<see cref="_kinds"/>), inset by the body's side padding.</summary>
    Element Item(QueuePanelProps props, Signal<bool> autoplay, int i)
    {
        var lane = props.Lane;
        int kind = (uint)i < (uint)_kinds.Length ? _kinds[i] : ItemVacancy;
        switch (kind)
        {
            case ItemPlayingFrom: return Inset(PlayingFrom(props.Queue.PlayingFrom.GetValueOrDefault().Name, props.OpenPlayingFrom));
            case ItemNow: return Inset(NowCard(props));
            case ItemAutoplay: return Inset(AutoplayRow(autoplay, _setAutoplay!));
            case ItemVacancy:
                return new BoxEl
                {
                    Key = "qp:empty", Direction = 1, Margin = new Edges4(Q.BodyPadding.Left, 0f, Q.BodyPadding.Right, 0f),
                    Children = [Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.NothingPlaying), subtitle: "")],
                };
        }
        int item = lane.ItemAt(kind);
        var slot = lane.SlotAt(item);
        if (slot.Kind == QueueSlotKind.Header) return Inset(Header(props, slot.Section));
        if (slot.Kind == QueueSlotKind.More) return Inset(ShowMore(props, slot.Section));
        return props.Queue.TryRow(slot.Section, slot.Pos, out var row)
            ? Inset(lane.Lane(item, Row(props, row), lane.RowKey("", row.ItemId, row.Index)))
            : Inset(new BoxEl { Key = "qp:gap:" + kind });
    }

    /// <summary>An item's root inset by the body's side padding (its own margins kept: they lie inside its stated extent).</summary>
    static Element Inset(Element e)
    {
        var b = e.As<BoxEl>();
        return b with { Margin = new Edges4(b.Margin.Left + Q.BodyPadding.Left, b.Margin.Top, b.Margin.Right + Q.BodyPadding.Right, b.Margin.Bottom) };
    }

    /// <summary>A queue row: the shared media surface (the plate, the hand, the play button, the right-click menu are the surface's) at the 40
    /// cover with the ✕ as its trailing cell (or its width, so every row's text ends at one line). The surface is built from the row's DATA
    /// (<see cref="RowFace"/>, with the lane's digest of what it shows) by a static builder, so an unchanged row is an equal push and skips its
    /// render when the panel re-renders (a new deck row, a section shift); only the rows that changed rebuild.</summary>
    Element Row(QueuePanelProps props, in QueueRowProps row)
    {
        var face = new RowFace(props.Verbs, props.Lane, row.Ref, row.ItemId, row.Index, row.Removable, row.Draggable, props.Queue.Viewer, _window,
                               props.Lane.RowStamp(row.Ref));
        return new BoxEl
        {
            Key = props.Lane.RowKey("", row.ItemId, row.Index),
            Direction = 1, Opacity = Q.Dim(row.Section),
            Enter = WinUiQueue.RowEnter, Exit = WinUiQueue.RowExit, Layout = LayoutTransition.Slide,
            Children = [Controls.FromData(face, s_rowSurface)],
        };
    }

    /// <summary>What a row's surface is built from (<see cref="Row"/>).</summary>
    readonly record struct RowFace(IQueueVerbs Verbs, IQueueLane Lane, EntityRef R, ulong ItemId, int Index, bool Removable, bool Draggable,
                                   bool Viewer, WidthTier Window, ulong Stamp);

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
                WinUiPlayerBar.Button(Icons.ChromeClose, () => verbs.Remove(itemId, index, r), true, false, Q.CloseOf(d.Window), Q.CloseOf(d.Window), Q.CloseGlyph)
                    with { BlocksDragArm = true }, Loc.Get(Strings.Menu.RemoveFromQueue))
            : new BoxEl { Width = Q.CloseOf(d.Window), Shrink = 0f };
        var data = SurfaceData(r, text, click: skip, play: skip, draggable: d.Draggable, queueItem: itemId,
            menu: () => verbs.RowMenu(itemId, index, r, viewer), trailing: trailing);
        var shape = Shape.Row(Q.RowArt) with { MinHeight = Q.RowExtent };
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
