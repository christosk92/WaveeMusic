// ── Themes/Classic/Structure/Views/Pages/ClassicRecentsList.cs — Classic's Recents list surface (themes plan 6.1, T10c2) ───────────────────────────
//
// From `Entities\Recents.Page.cs` (`SemanticSurface`, `ListSurface`, `RowSlot`, `StickyDayHeader`, `RailView`) and `Entities\Recents.UI.cs` (`DayHeader`): both views stay
// mounted (the zoom's keep-alive); the overview enters from 1.08 while the list recedes to 0.94. The slot components read the core's live state through `IRecentsHost`
// (one stable object per page), so a read inside a slot's render subscribes THAT slot, never the page.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Both views stay mounted (the zoom's keep-alive); the overview enters from 1.08 while the list recedes to 0.94. Keyed on (token, shapeEpoch) by its
/// parent — a deliberate remount on a re-cut.</summary>
internal sealed class ClassicRecentsSurface(IRecentsHost host, string? token, int shapeEpoch) : Component
{
    /// <summary>The day rail entering or leaving with a tier crossing (the list takes its 44 back as the rail goes), 160 ms on opacity; and, while it stays, gliding to its new
    /// place when the layout moves it (the flush rail of Small <-> the gapped one of Standard: its left edge and its margin change in one commit), over the same 160 ms.</summary>
    static readonly LayoutTransition s_rail = new(
        TransitionChannels.Opacity | TransitionChannels.Position, TransitionDynamics.Tween(ClassicRecentsRules.RailFadeMs, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(ClassicRecentsRules.RailFadeMs, Easing.SmoothOut));

    public override Element Render()
    {
        string suffix = (token ?? "all") + ":" + FormatCache.Int(shapeEpoch);
        Element detail = Embed.Comp(() => new ClassicRecentsListSurface(host, token)) with { Key = "recents-list:" + suffix };
        Element rail = Embed.Comp(() => new ClassicRecentsRail(host)) with { Key = "recents-rail:" + suffix };
        // The day rail is the record's (Compact has none): read here, so a crossing re-renders the surface in place and the rail fades in or out as an Enter / Exit.
        // The tiers of the facts this page is drawn in (the same signal the page's ctx.Tier() reads), never the process's last-published window.
        var facts = UseContext(ThemeFactsContext.Signal);
        var layout = ClassicRecentsTiers.Of(facts is null ? TierFacts.Unknown : facts.Value.Tiers);
        Element zoomedIn = new BoxEl
        {
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = layout.Rail && !layout.FlushRail ? Spacing.M : 0f, AlignItems = FlexAlign.Stretch,
            Children = layout.Rail
                ?
                [
                    // The list's width follows the body's right pad and the rail's gap (Small is flush, Standard keeps 36 and 12), so a crossing resizes it: it glides, its size revealed.
                    new BoxEl { Key = "recents-list-col", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, Layout = ClassicTierMotion.Reflow, Children = [detail] },
                    // The HStack item is a stretch COLUMN around the rail: Grow on the rail's own root would steal width.
                    new BoxEl
                    {
                        Key = "recents-rail-col", Animate = s_rail, Direction = 1, AlignSelf = FlexAlign.Stretch, MinHeight = 0f,
                        Margin = new Edges4(0f, 0f, layout.FlushRail ? ClassicRecentsRules.RailMarginRight : 0f, 0f), Children = [rail],
                    },
                ]
                : [new BoxEl { Key = "recents-list-col", Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, Layout = ClassicTierMotion.Reflow, Children = [detail] }],
        };
        Element zoomedOut = Embed.Comp(() => new ClassicRecentsCalendar(host, shapeEpoch)) with { Key = "recents-calendar:" + FormatCache.Int(shapeEpoch) };
        return SemanticZoom.Create(
            new SemanticZoomSlots(new SemanticZoomView(zoomedIn, host.ListController), new SemanticZoomView(zoomedOut, host.CalendarController)),
            new SemanticZoomOptions
            {
                IsZoomedOut = host.IsZoomedOut, Controller = host.ZoomController,
                MapInToOut = host.MapInToOutFunc, MapOutToIn = host.MapOutToInFunc,
            });
    }
}

/// <summary>ONE measured virtual list over the grouped projection with the pinned day band over it (§0 #4).</summary>
internal sealed class ClassicRecentsListSurface : Component
{
    static readonly RecentsFlatItem EmptyFlat = new(RecentsFlatItemKind.Row, -1, -1, -1);

    readonly IRecentsHost _host;
    readonly string? _token;
    readonly Func<BoundItemScope<RecentsFlatItem>, Element> _slotFactory;

    public ClassicRecentsListSurface(IRecentsHost host, string? token)
    {
        _host = host;
        _token = token;
        _slotFactory = scope => Embed.Comp(() => new ClassicRecentsRowSlot(host, scope));
    }

    public override Element Render()
    {
        var host = _host;
        var layout = host.ShapeNow.Layout;
        var items = UseMemo(() => BoundItems.Project(host.ShapeSignal, static s => s.Sections.Items.Length,
            static (s, i) => s.Sections.Items[i], EmptyFlat), DepKey.Empty);
        UseSignalEffect(host.WatchStickyAction);
        Element list = ItemsView.CreateBound(items, _slotFactory, RepeatLayout.Measured(layout), new ListOptions<RecentsFlatItem>
        {
            // The rows are cards with their own chrome; a list selector would be a second, competing cue.
            SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None,
            IsItemInvokedEnabled = true, OnInvokedTyped = host.InvokeFlatAction, ItemTextTyped = host.TextForFunc,
            Controller = host.ListController, Grow = 1f,
            // One recycle pool per ARM, so a card slot never rebinds into the track-grid shape (§9.1 #13).
            ContentType = host.ContentTypeFunc,
            Scroll = new ScrollOptions
            {
                // Each pivot remembers its OWN offset (§9.1 #16).
                ScrollKey = host.ScrollScope + "recents:" + (_token ?? "all"), AutoEdgeFade = true,
                Handle = host.ScrollController.Handle, SuppressScrollBar = true,
                // Rows scroll UNDER the pinned band: clipped at exactly 48 with a 24-DIP feather (W4).
                ItemClipTopInset = RecentsLayout.DateHeaderHeight, ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand,
            },
        });
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
            Children = [list, Embed.Comp(() => new ClassicRecentsStickyHeader(host))],
        };
    }
}

/// <summary>A BOUND slot: the template runs once per recycled slot and rebinds through the scope's item signal. It
/// subscribes to exactly what its row paints — the target's table (identity landing), the accordion, the epoch.</summary>
internal sealed class ClassicRecentsRowSlot : Component
{
    readonly IRecentsHost _host;
    readonly BoundItemScope<RecentsFlatItem> _scope;
    readonly ClassicRecentsRowActions _actions;
    readonly Action _onHeader;

    public ClassicRecentsRowSlot(IRecentsHost host, BoundItemScope<RecentsFlatItem> scope)
    {
        _host = host;
        _scope = scope;
        _actions = new ClassicRecentsRowActions(host, scope.Item);
        _onHeader = () => host.InvokeDay(scope.Item.Peek().DayIndex);
    }

    public override Element Render()
    {
        var flat = _scope.Item.Value;
        _ = _scope.Index.Value;
        _ = _host.Epoch.Value;
        var shape = _host.ShapeNow;
        var facts = UseContext(ThemeFactsContext.Signal);
        var layout = ClassicRecentsTiers.Of(facts is null ? TierFacts.Unknown : facts.Value.Tiers);   // read here: a tier crossing re-renders the realized slots in place (no remount)
        if (flat.Kind == RecentsFlatItemKind.DateHeader) return ClassicRecentsList.DayHeader(_host, shape, flat.DayIndex, overlay: false, _onHeader);
        int r = flat.OriginalRowIndex;
        if ((uint)r >= (uint)shape.Rows.Count) return new BoxEl { Height = RecentsLayout.RowHeight };

        _host.SubscribeRow(shape, r);
        if (RecentsLayout.UsesTrackArm(shape.Rows, r)) return ClassicRecentsRows.SingleRow(_host, shape, r, _actions);
        var id = shape.Rows.Rows[r].ItemId;
        bool expanded = !id.IsEmpty && _host.ExpandedRow.Value == id;
        return ClassicRecentsRows.Card(_host, shape, r, expanded, _actions, layout);
    }
}

/// <summary>The pinned day band: ALWAYS mounted, 48 tall; before the first resolve it is present but invisible and
/// non-hit-testable (W24). The push is a bound transform — a scroll frame never re-renders it.</summary>
internal sealed class ClassicRecentsStickyHeader : Component
{
    readonly IRecentsHost _host;
    readonly Action _onClick;

    public ClassicRecentsStickyHeader(IRecentsHost host)
    {
        _host = host;
        _onClick = () =>
        {
            var s = host.ShapeNow;
            int flat = host.StickyHeader.Peek();
            if ((uint)flat < (uint)s.Sections.Items.Length) host.InvokeDay(s.Sections.Items[flat].DayIndex);
        };
    }

    public override Element Render()
    {
        _ = _host.Epoch.Value;
        int flat = _host.StickyHeader.Value;
        var shape = _host.ShapeNow;
        int day = (uint)flat < (uint)shape.Sections.Items.Length ? shape.Sections.Items[flat].DayIndex : -1;
        return new BoxEl
        {
            Direction = 0, Grow = 1f, MinWidth = 0f, Height = RecentsLayout.DateHeaderHeight,
            HitTestVisible = day >= 0, HitTestPassThrough = true, Opacity = day >= 0 ? 1f : 0f,
            Transform = _host.StickyTransform,
            OnPointerWheel = _host.ForwardWheelAction,
            Children = [ClassicRecentsList.DayHeader(_host, shape, day, overlay: true, _onClick)],
        };
    }
}

/// <summary>The 44-DIP annotated rail — the list's scrollbar. Labels and ticks are memoized on (shapeEpoch, the
/// layout's measured-extent version), which bumps only on a REAL extent delta (§9.1 #11).</summary>
internal sealed class ClassicRecentsRail(IRecentsHost host) : Component
{
    int _logged;

    public override Element Render()
    {
        float slotH = UseMeasuredBounds("recents:rail").Value.H;
        int measured = host.RailMeasuredVersion.Value;
        int epoch = host.ShapeEpoch.Peek();
        var labels = UseMemo(host.RailLabelsFunc, DepKey.From(epoch, measured));
        var ticks = UseMemo(host.RailTicksFunc, DepKey.From(epoch, measured));
        // The one diagnostic the parity walk cross-checks (item 39): the last label against the scroll maximum.
        float max = (float)host.ScrollController.MaximumOffset.Peek();
        float lastOff = labels.Length > 0 ? labels[^1].ScrollOffset : 0f;
        int key = HashCode.Combine((int)slotH, (int)max, (int)lastOff, labels.Length);
        if (key != _logged)
        {
            _logged = key;
            Log.Info("recents.rail", "slotH=" + (int)slotH + " max=" + (int)max + " lastOff=" + (int)lastOff
                + " lastMinusMax=" + (int)(lastOff - max) + " labels=" + labels.Length + " ticks=" + ticks.Length);
        }
        return new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f,
            Children =
            [
                AnnotatedScrollBar.Create(host.ScrollController, new AnnotatedScrollBarOptions
                {
                    Labels = labels, TickOffsets = ticks,
                    // The slot's real height, so the last date is the bottom of the track (no dead band).
                    Height = slotH > 0f ? slotH : float.NaN,
                    DetailLabelAtOffset = host.RailDetailFunc,
                }),
            ],
        };
    }
}

/// <summary>The day header.</summary>
internal static class ClassicRecentsList
{
    /// <summary><c>label ——— N items</c>, 48 tall. The inline copy is the day's one tab stop; the pinned copy
    /// (<paramref name="overlay"/>) wears <c>FillLayerDefault</c> and keeps only the click. The hover ink is the page's
    /// accent — a plain <c>ColorF</c> (TextEl.HoverColor takes no Prop), so the realized header re-renders on a crossing.</summary>
    internal static Element DayHeader(IRecentsHost host, RecentsShape shape, int dayIndex, bool overlay, Action onClick)
    {
        var sections = shape.Sections;
        string label = (uint)dayIndex < (uint)sections.HeaderLabels.Length ? sections.HeaderLabels[dayIndex] : "";
        int n = RecentsView.CountForDay(sections, dayIndex);
        return new BoxEl
        {
            Direction = 0, Height = RecentsLayout.DateHeaderHeight, Grow = overlay ? 1f : 0f, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Fill = overlay ? Tok.FillLayerDefault : ColorF.Transparent,
            Role = overlay ? AutomationRole.None : AutomationRole.Button,
            Focusable = !overlay, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                R.Type.ModuleHeader(label) with
                {
                    Shrink = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    HoverColor = host.Accent.Value.Ink, BrushTransitionMs = Design.Motion.Faster,
                },
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Height = 1f, AlignSelf = FlexAlign.Center,
                    Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
                },
                // n == 0 keeps the node with an empty caption: the lane never drops (§0 #17).
                Caption(n > 0 ? host.CountLabel(n) : "") with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 },
            ],
        };
    }
}
