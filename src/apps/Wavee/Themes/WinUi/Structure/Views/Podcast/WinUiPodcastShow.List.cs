// ── Themes/WinUi/Structure/Views/Podcast/WinUiPodcastShow.List.cs — the show page as ONE virtualised list (podcast pages list, 2026-11-18) ───────────
//
// The owner: "why is scrolling on the show page so low fps.." (a show of 459 episodes). Every row was mounted, so each scroll frame drew all of them. The
// page is now one ItemsView over the core's flattened list model (`PodcastShowView.Items`): only the items on screen are mounted, so a 500-episode show
// scrolls and filters at the cost of 14.
//
//   ZStack (WinUiPodcastKit.ListPage)
//   ├─ ItemsView (the page's scroller)   items = 3 prefix + Items
//   │  ├─ [0] the hero (the wash behind it; cut under the 48 bar while the bar shows)       persistent prefix
//   │  ├─ [1] About the show as an Expander (641-1007, ≤640; nothing at ≥1008)              persistent prefix
//   │  ├─ [2] the toolbar: a RAW sticky band under the bar (a sticky root from a component never pins)   persistent prefix
//   │  └─ [3..] Items: a group header · an episode row · the "Load more" foot · the empty line
//   ├─ the pinned group header: an overlay under the toolbar showing the group the list is in at that line (the list's own measured layout says which
//   │  item sits there), backed only while it shows (the ItemsView's items are wrapped, so a header item cannot pin itself)
//   ├─ the details pane (≥1008): an overlay over the list's right column, placed by a bound transform from the scroll offset: under the toolbar at
//   │  rest, pinned under the bar, and on a short window riding the scroll both ways (no relayout per frame)
//   └─ the 48 bar
//
// Every slot reads the tier record from `_lay` and the gutter from `_inset` (signals the page writes on render), so a crossing re-pads the slots in place.
//
// LOADING is this same list page over the placeholder view (a `ShowList` that is not live): the SkelRegion derives it with its components (the slots mount
// over the placeholder and their output turns into flat fills; the list's bound slots derive at fixed indices), so the shimmer IS the loaded layout.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using K = Wavee.Themes.WinUi.WinUiPodcastKitRules;
using S = Wavee.Themes.WinUi.WinUiPodcastShowRules;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiPodcastShow
{
    /// <summary>One slot's item: a prefix part (0 hero, 1 about, 2 toolbar) or a list item resolved against the snapshot. Records compare by value, so a
    /// slot whose episode did not change does not re-render when the list around it does.</summary>
    sealed record ShowSlot(int Prefix, PodcastItemKind Kind, PodcastGroup? Group, PodcastEpisodeVm? Vm, bool First)
    {
        public static readonly ShowSlot None = new(-1, PodcastItemKind.Empty, null, null, false);
    }

    sealed partial class PageHost
    {
        /// <summary>One list over one view: the live page's (the snapshot; it scrolls, measures and drives the bar, the pinned header and the pane) or the
        /// loading shimmer's (the placeholder view, derived flat by the SkelRegion: never scrolled, never wired to the page's scroll handle).
        /// Every slot reads its view from here, so the shimmer's slots read the placeholder, never the live snapshot.</summary>
        sealed class ShowList
        {
            public readonly PageHost Host;
            public readonly IReadSignal<PodcastShowView> View;
            public readonly bool Live;
            public readonly Signal<float> HeroH = new(0f);
            public readonly Action<RectF> MeasureHero;
            public readonly BoundItemsSource<ShowSlot> Items;
            public readonly MeasuredStackVirtualLayout Measured;
            public readonly RepeatLayout Layout;
            public readonly Func<BoundItemScope<ShowSlot>, Element> Template;
            public readonly SlotProps Slot;
            ListOptions<ShowSlot>? _options;

            public ShowList(PageHost host, IReadSignal<PodcastShowView> view, bool live)
            {
                Host = host;
                View = view;
                Live = live;
                MeasureHero = r => { if (r.H > 1f && MathF.Abs(r.H - HeroH.Peek()) > 0.5f) HeroH.Value = r.H; };
                Items = BoundItems.Project(view, static v => PrefixCount + v.Items.Length, SlotAt, ShowSlot.None);
                Measured = new MeasuredStackVirtualLayout(S.RowEstimate, false, ExtentOf);
                Layout = RepeatLayout.Measured(Measured);
                Template = TemplateOf;
                Slot = new SlotProps(this);
            }

            PodcastItemKind KindAt(int i)
            {
                if (i < PrefixCount) return PodcastItemKind.Empty;
                var v = View.Peek();
                int k = i - PrefixCount;
                return (uint)k < (uint)v.Items.Length ? v.Items[k].Kind : PodcastItemKind.Empty;
            }

            /// <summary>Recycle pools per kind: a row never rebinds as a header; each prefix part is its own.</summary>
            int ContentTypeOf(int i) => i < PrefixCount ? S.PrefixType + i : (int)KindAt(i);

            /// <summary>An item's seeded extent (the measured one replaces it on realize).</summary>
            float ExtentOf(int i)
            {
                var lay = Host._lay.Peek();
                if (i == 0) return HeroH.Peek() > 0f ? HeroH.Peek() : S.HeroEstimate;
                if (i == 1) return lay.Pane ? 0f : S.AboutEstimate;
                if (i == 2) return S.ToolbarH;
                // EXACT where it can be (a jump far down the list lands on its year): a header is its tier's height, a row at rest its one-line height (with
                // a line of description where the tier shows one and the episode has one) plus the gap above every row but a group's first. An expanded row,
                // a two-line Compact title and the foot correct on realize.
                var v = View.Peek();
                int k = i - PrefixCount;
                if ((uint)k >= (uint)v.Items.Length) return S.FootEstimate;
                var it = v.Items[k];
                switch (it.Kind)
                {
                    case PodcastItemKind.Group: return lay.GroupH;
                    case PodcastItemKind.Row:
                        var vm = (uint)it.Episode < (uint)v.Episodes.Length ? v.Episodes[it.Episode] : null;
                        bool desc = lay.RowDescription && vm is not null && (vm.Description.Length > 0 || vm.DescriptionHtml.Length > 0);
                        return (it.First ? 0f : K.RowGap) + (desc ? S.RowHeightDescribed : S.RowHeight);
                    default: return S.FootEstimate;
                }
            }

            Element TemplateOf(BoundItemScope<ShowSlot> scope)
            {
                int i = scope.Row.Index.Peek();
                var list = this;
                switch (i)
                {
                    case 0: return Embed.Comp(Slot, static () => new HeroSlot());
                    case 1: return Embed.Comp(Slot, static () => new AboutSlot());
                    case 2:
                        // RAW: the sticky root must be the slot itself (a sticky root returned from a component never pins).
                        return WinUiPodcastKit.PinnedBand("show-toolbar", Embed.Comp(Slot, static () => new ToolbarSlot()), S.ToolbarH, K.BarH, Host._toolbarStuck);
                }
                if (scope.Item.Peek().Kind == PodcastItemKind.Group) return Embed.Comp(() => new HeadSlot(list, scope.Item));
                return Embed.Comp(() => new BodySlot(list, scope.Item));
            }

            /// <summary>The list element: the live one scrolls under the page's handle (its offset remembered per route); the shimmer's is a
            /// plain list the region derives.</summary>
            public Element Create()
            {
                var host = Host;
                _options ??= new ListOptions<ShowSlot>
                {
                    SelectionMode = ItemsSelectionMode.None,
                    Selector = SelectorVisual.None,
                    Grow = 1f,
                    ContentType = ContentTypeOf,
                    PersistentPrefixCount = PrefixCount,
                    Scroll = new ScrollOptions
                    {
                        ScrollKey = Live ? "podcast-scroll:" + host._p.RouteKey : null, Handle = Live ? host._scroll : null,
                        AutoEdgeFade = false, EdgeCues = ScrollEdgeCues.None,
                        // The rows cut themselves at the pinned toolbar's lower edge (the bar and the toolbar paint no band at rest).
                        ItemClipTopInset = K.BarH + S.ToolbarH,
                    },
                };
                Element list = ItemsView.CreateBound<ShowSlot>(Items, Template, Layout, _options);
                return Live ? list with { Key = "podcast-list:" + host._p.RouteKey } : list;
            }
        }

        const int PrefixCount = 3;

        readonly Signal<WinUiPodcastShowLayout> _lay = new(WinUiPodcastShowTiers.For(WidthTier.Standard));
        readonly Signal<float> _inset = new(0f), _listW = new(0f), _pageH = new(0f);
        Action<RectF>? _measurePage;
        ShowList? _live, _shim;
        Func<int>? _pinnedOf;
        Memo<int>? _pinned;
        Func<bool>? _barOnOf;
        Memo<bool>? _barOnList;
        Func<Affine2D>? _paneRideOf, _paneRestOf;
        uint _tone;

        /// <summary>Writes what every slot reads beside its view: the tier record, the gutter, the list column's width.</summary>
        void PublishLayout(WinUiPodcastShowLayout lay, float inset, float listW)
        {
            _lay.SetIfChanged(lay);
            _inset.SetIfChanged(inset);
            _listW.SetIfChanged(listW);
        }

        /// <summary>The live page: the list, the pinned group header and the pane over it, the bar.</summary>
        Element LivePage(WinUiPodcastShowLayout lay, float inset, Element bar, uint tone)
        {
            _tone = tone;
            _measurePage ??= r => { if (r.H > 1f && MathF.Abs(r.H - _pageH.Peek()) > 0.5f) _pageH.Value = r.H; };
            var live = _live!;
            _paneRideOf ??= PaneRide;
            var layers = new List<Element>(2)
            {
                // The pinned group header: under the toolbar, over the list's column.
                new BoxEl
                {
                    Key = "show-pinned-head-layer", Direction = 1, HitTestPassThrough = true, MinWidth = 0f, IsolateLayout = true,
                    Padding = Gutters(lay, inset) with { Top = K.BarH + S.ToolbarH },
                    Children = [Embed.Comp(live.Slot, static () => new PinnedHead()) with { Key = "show-pinned-head" }],
                },
            };
            if (lay.Pane) layers.Add(PaneLayer(live, inset, _paneRideOf));
            return WinUiPodcastKit.ListPage(_p.RouteKey, live.Create(), bar, BarOn, layers.ToArray());
        }

        /// <summary>The loading page: the same list and pane over the placeholder view, derived flat by the region (no bar and no pinned header: neither
        /// shows at rest). The pane rests at the hero's foot.</summary>
        Element ShimmerPage(WinUiPodcastShowLayout lay, float inset)
        {
            var shim = _shim!;
            _paneRestOf ??= () => Affine2D.Translation(0f, MathF.Max(_shim!.HeroH.Value, S.PaneTop));
            Element list = shim.Create();
            return new BoxEl
            {
                ZStack = true, Direction = 1, Grow = 1f, MinWidth = 0f,
                Children = lay.Pane ? [list, PaneLayer(shim, inset, _paneRestOf)] : [list],
            };
        }

        /// <summary>The details pane's layer over the list's right column (>=1008), placed by <paramref name="place"/>.</summary>
        Element PaneLayer(ShowList list, float inset, Func<Affine2D> place) => new BoxEl
        {
            Key = "show-pane-layer", Direction = 1, AlignItems = FlexAlign.End, HitTestPassThrough = true, MinWidth = 0f, IsolateLayout = true,
            OnBoundsChanged = list.Live ? _measurePage : null,
            Padding = new Edges4(0f, 0f, inset, 0f),
            // The pane hangs from a 0-tall holder: the layer (and the page) keep the list's height however tall the pane is.
            Children =
            [
                new BoxEl { Key = "show-pane-holder", Direction = 1, Width = S.PaneW, Height = 0f, Shrink = 0f, Children = [
                new BoxEl
                {
                    Key = "show-pane-ride", Enter = WinUiDiscoveryMotion.SwapFade, Exit = WinUiDiscoveryMotion.SwapFade, Direction = 1, Width = S.PaneW,
                    Shrink = 0f, MinWidth = 0f, Transform = Prop.Of(place),
                    Children = [Embed.Comp(new PaneProps(list), static () => new PaneHost()) with { Key = "show-pane-host" }],
                }] },
            ],
        };

        /// <summary>The bar shows once the hero has gone under it (a memo over the offset: it flips, it does not re-run the page).</summary>
        IReadSignal<bool> BarOn => _barOnList!;

        static ShowSlot SlotAt(PodcastShowView v, int i)
        {
            if (i < PrefixCount) return new ShowSlot(i, PodcastItemKind.Empty, null, null, false);
            int k = i - PrefixCount;
            if ((uint)k >= (uint)v.Items.Length) return ShowSlot.None;
            var it = v.Items[k];
            var g = (uint)it.Group < (uint)v.Groups.Length ? v.Groups[it.Group] : null;
            var vm = it.Kind == PodcastItemKind.Row && (uint)it.Episode < (uint)v.Episodes.Length ? v.Episodes[it.Episode] : null;
            return new ShowSlot(-1, it.Kind, it.Kind == PodcastItemKind.Group ? g : null, vm, it.First);
        }

        /// <summary>The pane's place: under the toolbar at rest (the hero's foot), pinned 16 under the bar, and taller than the viewport it rides the scroll
        /// both ways (the owner, 2026-11-18): its shift (0 .. its overhang) follows each scroll step only while it is pinned.</summary>
        Affine2D PaneRide()
        {
            double o = _scroll.Offset.Value;
            // The page's own height (the pane's layer fills it): the list's visible height.
            float view = _pageH.Value;
            float hero = _live!.HeroH.Value, h = _paneH.Value;
            float natural = hero - (float)o;
            bool pinned = natural <= S.PaneTop;
            float overhang = MathF.Max(0f, h + S.PaneTop + S.PaneFoot - view);
            double d = double.IsNaN(_paneLastOffset) ? 0.0 : o - _paneLastOffset;
            _paneLastOffset = o;
            if (pinned) _paneShift = Math.Clamp(_paneShift + (float)d, 0f, overhang);
            else _paneShift = 0f;
            return Affine2D.Translation(0f, MathF.Max(natural, S.PaneTop) - _paneShift);
        }

        /// <summary>A list item between its gutters: spacers that EASE their width across a crossing (a padding cannot ease): the page inset, and on the
        /// right the pane's column too where the pane shows.</summary>
        Element Guttered(string key, WinUiPodcastShowLayout lay, float inset, Element body, Edges4 air = default, bool reflow = false) => new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Padding = air,
            Layout = reflow ? WinUiDiscoveryMotion.SizeReflow : null,
            Children =
            [
                WinUiDiscoveryMotion.AirH(key + ":lead", inset),
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [body] },
                WinUiDiscoveryMotion.AirH(key + ":end", inset + (lay.Pane ? S.PaneW + S.ColGap : 0f)),
            ],
        };

        /// <summary>The gutters of a list item: the page inset, and on the right the pane's column too where the pane shows.</summary>
        Edges4 Gutters(WinUiPodcastShowLayout lay, float inset) => new(inset, 0f, inset + (lay.Pane ? S.PaneW + S.ColGap : 0f), 0f);

        /// <summary>Jump to a year: its header to the top of the list, under the bar and the toolbar.</summary>
        void JumpToGroup(string groupKey)
        {
            var live = _live!;
            var v = live.View.Peek();
            for (int k = 0; k < v.Items.Length; k++)
            {
                var it = v.Items[k];
                if (it.Kind != PodcastItemKind.Group || (uint)it.Group >= (uint)v.Groups.Length || v.Groups[it.Group].Key != groupKey) continue;
                // The header's own place in the list's measured layout, parked under the bar and the toolbar.
                _scroll.ScrollTo(Math.Max(0.0, live.Measured.OffsetOf(PrefixCount + k, 0f) - (K.BarH + S.ToolbarH)), ScrollMove.Immediate);
                return;
            }
        }

        /// <summary>The group the list is in at the line under the toolbar (-1: the toolbar is not pinned, or the line is above the first header). A memo
        /// over the offset: the pinned header re-renders only when the group changes.</summary>
        int PinnedGroup()
        {
            if (!_toolbarStuck.Value || _live is not { Measured: var m }) return -1;
            double o = _scroll.Offset.Value;
            var v = _live.View.Value;
            int i = m.IndexAt((float)o + K.BarH + S.ToolbarH, 0f) - PrefixCount;
            if (i < 0 || v.Items.Length == 0) return -1;
            if (i >= v.Items.Length) i = v.Items.Length - 1;
            for (; i >= 0; i--) if (v.Items[i].Group >= 0) return v.Items[i].Group;
            return -1;
        }

        void UsePinnedHead()
        {
            _pinnedOf ??= PinnedGroup;
            _pinned = UseComputed(_pinnedOf);
        }

        sealed record SlotProps(ShowList List);

        /// <summary>The pinned group header over the list: the year (or "New since your last visit") the list is in under the toolbar, on the page's layer
        /// with the hairline; nothing while the toolbar has not pinned.</summary>
        sealed class PinnedHead : Component
        {
            public override Element Render()
            {
                var list = UseProps<SlotProps>().List;
                var host = list.Host;
                int gi = host._pinned?.Value ?? -1;
                var v = list.View.Peek();
                if ((uint)gi >= (uint)v.Groups.Length) return new BoxEl { Height = 0f };
                var lay = host._lay.Value;
                return new BoxEl
                {
                    Key = "show-pinned-head:" + v.Groups[gi].Key, Direction = 1, MinWidth = 0f, Fill = Prop.Of(static () => R.Layer.ContentSurface),
                    Children =
                    [
                        host.GroupHeadRow(v.Groups[gi], v.Years, lay, live: true),
                        new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                    ],
                };
            }
        }

        /// <summary>[0] the hero over the wash, cut under the bar while the bar shows.</summary>
        sealed class HeroSlot : Component
        {
            public override Element Render()
            {
                var list = UseProps<SlotProps>().List;
                var host = list.Host;
                var v = list.View.Value;
                var lay = host._lay.Value;
                float inset = host._inset.Value;
                bool on = list.Live && host.BarOn.Value;
                var body = new BoxEl
                {
                    Key = "show-hero-body", ZStack = true, Direction = 1, MinWidth = 0f, OnBoundsChanged = list.MeasureHero,
                    Children =
                    [
                        new BoxEl { Key = "podcast-wash", Height = K.WashH, AlignSelf = FlexAlign.Start, Gradient = K.Wash(list.Live ? host._tone : 0u), HitTestVisible = false },
                        new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(inset, 0f, inset, 0f), Children = [host.Hero(v, lay, list.Live)] },
                    ],
                };
                // A crossing steps the hero's height (the cover, the title's size): it eases through real layout, so the list's items after it ride it.
                return new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Layout = WinUiDiscoveryMotion.HeightReflow,
                    Children = [on ? body.StickyClip(K.BarH) : body],
                };
            }
        }

        /// <summary>[1] About the show as an Expander where the pane is not (641-1007, ≤640); nothing at ≥1008.</summary>
        sealed class AboutSlot : Component
        {
            public override Element Render()
            {
                var list = UseProps<SlotProps>().List;
                var host = list.Host;
                var lay = host._lay.Value;
                float inset = host._inset.Value;
                if (lay.Pane) return new BoxEl { Grow = 1f, Basis = 0f, Height = 0f, Layout = WinUiDiscoveryMotion.HeightReflow };
                var v = list.View.Value;
                return new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Layout = WinUiDiscoveryMotion.HeightReflow,
                    Children = [host.Guttered("about", lay with { Pane = false }, inset, host.AboutExpander(ViewOf(HeadOf(v))))],
                };
            }
        }

        /// <summary>[2] the toolbar's content (the sticky band around it is the slot itself).</summary>
        sealed class ToolbarSlot : Component
        {
            public override Element Render()
            {
                var list = UseProps<SlotProps>().List;
                var host = list.Host;
                var v = list.View.Value;
                var lay = host._lay.Value;
                float inset = host._inset.Value, listW = host._listW.Value;
                return host.Guttered("toolbar", lay, inset,
                    host.Toolbar(v, lay, WinUiPodcastShowTiers.SearchCollapsed(lay, listW), WinUiPodcastShowTiers.FilterCombo(lay, listW)));
            }
        }

        /// <summary>A group header's content: the year (or "New since your last visit", "In order") with its count, the jump list, Mark all as played.</summary>
        sealed class HeadSlot(ShowList list, IReadSignal<ShowSlot> item) : Component
        {
            public override Element Render()
            {
                var host = list.Host;
                var slot = item.Value;
                var lay = host._lay.Value;
                float inset = host._inset.Value;
                if (slot.Group is not { } g) return new BoxEl { Height = lay.GroupH };
                var years = list.View.Peek().Years;
                return host.Guttered("gh", lay, inset, host.GroupHeadRow(g, years, lay, list.Live), reflow: true);
            }
        }

        /// <summary>An episode row, the "Load more" foot or the empty line.</summary>
        /// <summary>An episode row, the "Load more" foot or the empty line, between its gutters. It re-renders only when its slot's SHAPE changes (the
        /// kind, whether the row opens its group): a recycled slot that rebinds to another episode re-renders the row alone (<see cref="RowItem"/>), never
        /// its gutters, so a fast scroll realising many rows a frame pays for each row's content only.</summary>
        sealed class BodySlot(ShowList list, IReadSignal<ShowSlot> item) : Component
        {
            Func<(PodcastItemKind Kind, bool First)>? _shapeOf;
            RowItemProps? _row;

            public override Element Render()
            {
                var host = list.Host;
                _shapeOf ??= () => (item.Value.Kind, item.Value.First);
                var shape = UseComputed(_shapeOf).Value;
                var lay = host._lay.Value;
                float inset = host._inset.Value;
                Element body = shape.Kind switch
                {
                    PodcastItemKind.Row => Embed.Comp(_row ??= new RowItemProps(list, item), static () => new RowItem()),
                    PodcastItemKind.More => new BoxEl
                    {
                        Key = "show-more", Direction = 0, Justify = FlexJustify.Center, Padding = new Edges4(0f, K.SectionBottom, 0f, K.SectionBottom),
                        Children = [Button.Create(Loc.Get(Strings.Podcast.Pages.LoadMore), host._p.LoadMore) with { Key = "load-more" }],
                    },
                    PodcastItemKind.Empty => new BoxEl
                    {
                        Key = "show-empty", Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, Wrap = true,
                        Padding = new Edges4(0f, S.EmptyPad, 0f, S.EmptyPad),
                        Children = [Body(Loc.Get(Strings.Podcast.Pages.NoMatch)) with { Color = Tok.TextSecondary },
                            HyperlinkButton.Create(Loc.Get(Strings.Podcast.Pages.ClearFilter), host._p.ClearFilter)],
                    },
                    _ => new BoxEl(),
                };
                return host.Guttered("row", lay, inset, body, new Edges4(0f, shape.Kind == PodcastItemKind.Row && !shape.First ? K.RowGap : 0f, 0f, 0f), reflow: true);
            }
        }

        sealed record RowItemProps(ShowList List, IReadSignal<ShowSlot> Item);

        /// <summary>The episode row of a row slot: ONE row per slot, so a recycled slot re-renders its row for the new episode instead of mounting a new
        /// one (a filter switch that brings rows back costs their props, not their mount).</summary>
        sealed class RowItem : Component
        {
            public override Element Render()
            {
                var p = UseProps<RowItemProps>();
                var host = p.List.Host;
                var lay = host._lay.Value;
                return p.Item.Value.Vm is { } vm ? WinUiPodcastKit.EpisodeRow(vm, lay.RowMode, lay.RowDescription, lay.Stacked, host._verbs!) : new BoxEl();
            }
        }
    }
}
