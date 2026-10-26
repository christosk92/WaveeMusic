// ── Themes/Zune/Structure/Views/Recents/ZuneRecentsList.cs — Zune's Recents list surface (themes plan 6.3; P5 Z7) ───────────────────────────
//
// Board Z-Recents. The semantic zoom keeps both views mounted (the zoom's keep-alive); the list is ONE measured virtual list over the grouped
// projection with the pinned day band over it. Every shape, the sticky arithmetic, the controllers and the verbs are the host's (`IRecentsHost`);
// the slots read the facts they are drawn in and re-render in place on a tier crossing. Zune has no day rail: the rows scroll under the pinned
// band, which stands on the page's gutter with nothing behind it (the rows are clipped at the band's foot with the core's feather).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Z = Wavee.Themes.Zune.ZuneRecentsRules;

namespace Wavee.Themes.Zune;

/// <summary>Both views stay mounted (the zoom's keep-alive). Keyed on (token, shapeEpoch) by its parent: a deliberate remount on a re-cut.</summary>
internal sealed class ZuneRecentsSurface(IRecentsHost host, string? token, int shapeEpoch) : Component
{
    public override Element Render()
    {
        string suffix = (token ?? "all") + ":" + FormatCache.Int(shapeEpoch);
        // Both views are COMPONENTS: the zoom builds a view's element once, so a plain element carrying the tier's numbers would stay at the tier
        // the page mounted in. Each reads the facts it is drawn in inside its own render.
        Element zoomedIn = Embed.Comp(() => new ZuneRecentsListSurface(host, token)) with { Key = "zune-recents-list:" + suffix };
        Element zoomedOut = Embed.Comp(() => new ZuneRecentsCalendar(host, shapeEpoch)) with { Key = "zune-recents-calendar:" + FormatCache.Int(shapeEpoch) };
        return SemanticZoom.Create(
            new SemanticZoomSlots(new SemanticZoomView(zoomedIn, host.ListController), new SemanticZoomView(zoomedOut, host.CalendarController)),
            new SemanticZoomOptions
            {
                IsZoomedOut = host.IsZoomedOut, Controller = host.ZoomController,
                MapInToOut = host.MapInToOutFunc, MapOutToIn = host.MapOutToInFunc,
            });
    }
}

/// <summary>ONE measured virtual list over the grouped projection with the pinned day band over it.</summary>
internal sealed class ZuneRecentsListSurface : Component
{
    static readonly RecentsFlatItem EmptyFlat = new(RecentsFlatItemKind.Row, -1, -1, -1);

    readonly IRecentsHost _host;
    readonly string? _token;
    readonly Func<BoundItemScope<RecentsFlatItem>, Element> _slotFactory;

    public ZuneRecentsListSurface(IRecentsHost host, string? token)
    {
        _host = host;
        _token = token;
        _slotFactory = scope => Embed.Comp(() => new ZuneRecentsRowSlot(host, scope));
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
            // The rows are plates of their own (the surface's wash); a list selector would be a second, competing cue.
            SelectionMode = ItemsSelectionMode.None, Selector = SelectorVisual.None,
            IsItemInvokedEnabled = true, OnInvokedTyped = host.InvokeFlatAction, ItemTextTyped = host.TextForFunc,
            Controller = host.ListController, Grow = 1f,
            // One recycle pool per ARM, so a card slot never rebinds into another shape.
            ContentType = host.ContentTypeFunc,
            Scroll = new ScrollOptions
            {
                // Each pivot remembers its OWN offset.
                ScrollKey = host.ScrollScope + "recents:" + (_token ?? "all"), AutoEdgeFade = true,
                Handle = host.ScrollController.Handle,
                // Rows scroll UNDER the pinned band: clipped at exactly the band's foot with the core's feather.
                ItemClipTopInset = RecentsLayout.DateHeaderHeight, ItemClipTopFadeBand = Detail.VerticalLayout.StickyFadeBand,
            },
        });
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
            Children = [list, Embed.Comp(() => new ZuneRecentsStickyHeader(host))],
        };
    }
}

/// <summary>A BOUND slot: the template runs once per recycled slot and rebinds through the scope's item signal. It subscribes to exactly what its
/// row paints: the target's table (identity landing), the accordion, the epoch, and the tier record it is drawn in.</summary>
internal sealed class ZuneRecentsRowSlot : Component
{
    readonly IRecentsHost _host;
    readonly BoundItemScope<RecentsFlatItem> _scope;
    readonly ZuneRecentsRowActions _actions;
    readonly Action _onHeader;

    public ZuneRecentsRowSlot(IRecentsHost host, BoundItemScope<RecentsFlatItem> scope)
    {
        _host = host;
        _scope = scope;
        _actions = new ZuneRecentsRowActions(host, scope.Item);
        _onHeader = () => host.InvokeDay(scope.Item.Peek().DayIndex);
    }

    public override Element Render()
    {
        var flat = _scope.Item.Value;
        _ = _scope.Index.Value;
        _ = _host.Epoch.Value;
        var shape = _host.ShapeNow;
        var facts = UseContext(ThemeFactsContext.Signal);
        // Read here: a tier crossing re-renders the realized slots in place (no remount).
        var layout = ZuneRecentsTiers.Of(facts is null ? TierFacts.Unknown : facts.Value.Tiers);
        if (flat.Kind == RecentsFlatItemKind.DateHeader) return ZuneRecentsDays.Header(_host, shape, flat.DayIndex, layout, _onHeader);
        int r = flat.OriginalRowIndex;
        if ((uint)r >= (uint)shape.Rows.Count) return new BoxEl { Height = RecentsLayout.RowHeight };

        _host.SubscribeRow(shape, r);
        var id = shape.Rows.Rows[r].ItemId;
        bool expanded = !id.IsEmpty && _host.ExpandedRow.Value == id;
        return ZuneRecentsRows.Row(_host, shape, r, expanded, _actions, layout);
    }
}

/// <summary>The pinned day band: ALWAYS mounted, the core's 48 tall; before the first resolve it is present but invisible and non-hit-testable.
/// The push is a bound transform: a scroll frame never re-renders it.</summary>
internal sealed class ZuneRecentsStickyHeader : Component
{
    readonly IRecentsHost _host;
    readonly Action _onClick;

    public ZuneRecentsStickyHeader(IRecentsHost host)
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
        var facts = UseContext(ThemeFactsContext.Signal);
        var layout = ZuneRecentsTiers.Of(facts is null ? TierFacts.Unknown : facts.Value.Tiers);
        int day = (uint)flat < (uint)shape.Sections.Items.Length ? shape.Sections.Items[flat].DayIndex : -1;
        return new BoxEl
        {
            Direction = 0, Grow = 1f, MinWidth = 0f, Height = RecentsLayout.DateHeaderHeight,
            HitTestVisible = day >= 0, HitTestPassThrough = true, Opacity = day >= 0 ? 1f : 0f,
            Transform = _host.StickyTransform,
            OnPointerWheel = _host.ForwardWheelAction,
            Children = [ZuneRecentsDays.Header(_host, shape, day, layout, _onClick, overlay: true)],
        };
    }
}

/// <summary>The day heading.</summary>
internal static class ZuneRecentsDays
{
    /// <summary><c>label ——— N items</c> on the core's 48 band: the day in the heading voice, a hairline, the count at the end. The copy is the day's
    /// one tab stop; a header with no rows keeps its rule and an empty caption (the lane never drops).</summary>
    internal static Element Header(IRecentsHost host, RecentsShape shape, int dayIndex, ZuneRecentsLayout layout, Action onClick, bool overlay = false)
    {
        var sections = shape.Sections;
        string label = (uint)dayIndex < (uint)sections.HeaderLabels.Length ? sections.HeaderLabels[dayIndex] : "";
        int n = RecentsView.CountForDay(sections, dayIndex);
        TextEl title = layout.DayHead == ZuneRecentsHead.Standard ? R.Type.Heading(label) : R.Type.QuickplayHead(label);
        // It fills its host: the list's row, and the pinned band's row (a header that hugged there drew the rule at its 16 minimum, the count at x165).
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Height = RecentsLayout.DateHeaderHeight, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Z.DayGap,
            // The count's right edge is the rows' chevron glyph's, not the list's edge (the audit's 1044 vs 985).
            Padding = new Edges4(0f, 0f, Z.CountInset(layout.Stacked, layout.ChevronLane, Controls.IconButtonSize, Spacing.S), 0f),
            Role = overlay ? AutomationRole.None : AutomationRole.Button, Focusable = !overlay, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                title with
                {
                    Shrink = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, Case = R.Case.For(LabelKind.Heading),
                },
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = Z.DayRuleMin, Height = Z.DayRule, AlignSelf = FlexAlign.Center,
                    Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
                },
                R.Type.DenseMeta(n > 0 ? host.CountLabel(n) : "") with
                {
                    Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Case = R.Case.For(LabelKind.Label),
                },
            ],
        };
    }
}
