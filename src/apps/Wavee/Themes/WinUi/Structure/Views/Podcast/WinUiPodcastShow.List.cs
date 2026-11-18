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
        const int PrefixCount = 3;

        readonly ItemsViewController _ctl = new();
        readonly Signal<WinUiPodcastShowLayout> _lay = new(WinUiPodcastShowTiers.For(WidthTier.Standard));
        readonly Signal<float> _inset = new(0f), _heroH = new(0f), _listW = new(0f);
        BoundItemsSource<ShowSlot>? _items;
        ListOptions<ShowSlot>? _listOptions;
        RepeatLayout? _listLayout;
        MeasuredStackVirtualLayout? _measured;
        Func<int>? _pinnedOf;
        Memo<int>? _pinned;
        Func<BoundItemScope<ShowSlot>, Element>? _template;
        Func<bool>? _barOnOf;
        Memo<bool>? _barOnList;
        Action<RectF>? _measureHero;
        Func<Affine2D>? _paneRideOf;

        /// <summary>The live page: the list, the pane over its right column, the bar.</summary>
        Element LivePage(WinUiPodcastShowLayout lay, float inset, float listW, Element bar, uint tone)
        {
            _lay.SetIfChanged(lay);
            _inset.SetIfChanged(inset);
            _listW.SetIfChanged(listW);
            _tone = tone;
            _measureHero ??= r => { if (r.H > 1f && MathF.Abs(r.H - _heroH.Peek()) > 0.5f) _heroH.Value = r.H; };
            _items ??= BoundItems.Project(_p.View, static v => PrefixCount + v.Items.Length, SlotAt, ShowSlot.None);
            _measured ??= new MeasuredStackVirtualLayout(S.RowEstimate, false, ExtentOf);
            _listLayout ??= RepeatLayout.Measured(_measured);
            _template ??= Template;
            _listOptions ??= new ListOptions<ShowSlot>
            {
                SelectionMode = ItemsSelectionMode.None,
                Selector = SelectorVisual.None,
                Controller = _ctl,
                Grow = 1f,
                ContentType = ContentTypeOf,
                PersistentPrefixCount = PrefixCount,
                Scroll = new ScrollOptions
                {
                    ScrollKey = "podcast-scroll:" + _p.RouteKey, Handle = _scroll, AutoEdgeFade = false, EdgeCues = ScrollEdgeCues.None,
                    // The rows cut themselves at the pinned toolbar's lower edge (the bar and the toolbar paint no band at rest).
                    ItemClipTopInset = K.BarH + S.ToolbarH,
                },
            };
            Element list = ItemsView.CreateBound<ShowSlot>(_items!, _template!, _listLayout!.Value, _listOptions) with { Key = "podcast-list:" + _p.RouteKey };
            _paneRideOf ??= PaneRide;
            var layers = new List<Element>(2)
            {
                // The pinned group header: under the toolbar, over the list's column.
                new BoxEl
                {
                    Key = "show-pinned-head-layer", Direction = 1, HitTestPassThrough = true, MinWidth = 0f, IsolateLayout = true,
                    Padding = Gutters(lay, inset) with { Top = K.BarH + S.ToolbarH },
                    Children = [Embed.Comp(new SlotProps(this), static () => new PinnedHead()) with { Key = "show-pinned-head" }],
                },
            };
            if (lay.Pane)
                layers.Add(new BoxEl
                {
                    Key = "show-pane-layer", Direction = 1, AlignItems = FlexAlign.End, HitTestPassThrough = true, MinWidth = 0f, IsolateLayout = true,
                    Padding = new Edges4(0f, 0f, inset, 0f),
                    Children =
                    [
                        new BoxEl
                        {
                            Key = "show-pane-ride", Enter = WinUiDiscoveryMotion.SwapFade, Exit = WinUiDiscoveryMotion.SwapFade, Direction = 1, Width = S.PaneW,
                            Shrink = 0f, MinWidth = 0f, Transform = Prop.Of(_paneRideOf),
                            Children = [Embed.Comp(new PaneProps(this), static () => new PaneHost()) with { Key = "show-pane-host" }],
                        },
                    ],
                });
            return WinUiPodcastKit.ListPage(_p.RouteKey, list, bar, BarOn, layers.ToArray());
        }

        uint _tone;

        /// <summary>The bar shows once the hero has gone under it (a memo over the offset: it flips, it does not re-run the page).</summary>
        IReadSignal<bool> BarOn => _barOnList ?? (IReadSignal<bool>)_barOn;

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

        PodcastItemKind KindAt(int i)
        {
            if (i < PrefixCount) return PodcastItemKind.Empty;
            var v = _p.View.Peek();
            int k = i - PrefixCount;
            return (uint)k < (uint)v.Items.Length ? v.Items[k].Kind : PodcastItemKind.Empty;
        }

        /// <summary>Recycle pools per kind: a row never rebinds as a header; each prefix part is its own.</summary>
        int ContentTypeOf(int i) => i < PrefixCount ? S.PrefixType + i : (int)KindAt(i);

        /// <summary>An item's seeded extent (the measured one replaces it on realize).</summary>
        float ExtentOf(int i)
        {
            var lay = _lay.Peek();
            if (i == 0) return _heroH.Peek() > 0f ? _heroH.Peek() : S.HeroEstimate;
            if (i == 1) return lay.Pane ? 0f : S.AboutEstimate;
            if (i == 2) return S.ToolbarH;
            return KindAt(i) switch
            {
                PodcastItemKind.Group => lay.GroupH,
                PodcastItemKind.Row => lay.RowDescription ? S.RowEstimateTall : S.RowEstimate,
                _ => S.FootEstimate,
            };
        }

        Element Template(BoundItemScope<ShowSlot> scope)
        {
            int i = scope.Row.Index.Peek();
            var host = this;
            switch (i)
            {
                case 0: return Embed.Comp(new SlotProps(host), static () => new HeroSlot());
                case 1: return Embed.Comp(new SlotProps(host), static () => new AboutSlot());
                case 2:
                    // RAW: the sticky root must be the slot itself (a sticky root returned from a component never pins).
                    return WinUiPodcastKit.PinnedBand("show-toolbar", Embed.Comp(new SlotProps(host), static () => new ToolbarSlot()), S.ToolbarH, K.BarH, _toolbarStuck);
            }
            if (scope.Item.Peek().Kind == PodcastItemKind.Group) return Embed.Comp(() => new HeadSlot(host, scope.Item));
            return Embed.Comp(() => new BodySlot(host, scope.Item));
        }

        /// <summary>The pane's place: under the toolbar at rest (the hero's foot), pinned 16 under the bar, and taller than the viewport it rides the scroll
        /// both ways (the owner, 2026-11-18): its shift (0 .. its overhang) follows each scroll step only while it is pinned.</summary>
        Affine2D PaneRide()
        {
            double o = _scroll.Offset.Value;
            float view = (float)_scroll.ViewportSignal.Value;
            float hero = _heroH.Value, h = _paneH.Value;
            float natural = hero - (float)o;
            bool pinned = natural <= S.PaneTop;
            float overhang = MathF.Max(0f, h + S.PaneTop + S.PaneFoot - view);
            double d = double.IsNaN(_paneLastOffset) ? 0.0 : o - _paneLastOffset;
            _paneLastOffset = o;
            if (pinned) _paneShift = Math.Clamp(_paneShift + (float)d, 0f, overhang);
            else _paneShift = 0f;
            return Affine2D.Translation(0f, MathF.Max(natural, S.PaneTop) - _paneShift);
        }

        /// <summary>The gutters of a list item: the page inset, and on the right the pane's column too where the pane shows.</summary>
        Edges4 Gutters(WinUiPodcastShowLayout lay, float inset) => new(inset, 0f, inset + (lay.Pane ? S.PaneW + S.ColGap : 0f), 0f);

        /// <summary>Jump to a year: its header to the top of the list, under the bar and the toolbar.</summary>
        void JumpToGroup(string groupKey)
        {
            var v = _p.View.Peek();
            for (int k = 0; k < v.Items.Length; k++)
            {
                var it = v.Items[k];
                if (it.Kind != PodcastItemKind.Group || (uint)it.Group >= (uint)v.Groups.Length || v.Groups[it.Group].Key != groupKey) continue;
                _ctl.StartBringItemIntoView(PrefixCount + k, alignmentRatio: 0f, animate: false);
                _scroll.ScrollBy(-(K.BarH + S.ToolbarH), ScrollMove.Immediate);
                return;
            }
        }

        /// <summary>The group the list is in at the line under the toolbar (-1: the toolbar is not pinned, or the line is above the first header). A memo
        /// over the offset: the pinned header re-renders only when the group changes.</summary>
        int PinnedGroup()
        {
            if (!_toolbarStuck.Value || _measured is not { } m) return -1;
            double o = _scroll.Offset.Value;
            var v = _p.View.Value;
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

        sealed record SlotProps(PageHost Host);

        /// <summary>The pinned group header over the list: the year (or "New since your last visit") the list is in under the toolbar, on the page's layer
        /// with the hairline; nothing while the toolbar has not pinned.</summary>
        sealed class PinnedHead : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                int gi = host._pinned?.Value ?? -1;
                var v = host._p.View.Peek();
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
                var host = UseProps<SlotProps>().Host;
                var full = host._p.View.Value;
                var v = full.Load == PodcastLoad.Pending ? s_placeholder : full;
                var lay = host._lay.Value;
                float inset = host._inset.Value;
                bool on = host.BarOn.Value;
                var body = new BoxEl
                {
                    Key = "show-hero-body", ZStack = true, Direction = 1, MinWidth = 0f, OnBoundsChanged = host._measureHero,
                    Children =
                    [
                        new BoxEl { Key = "podcast-wash", Height = K.WashH, AlignSelf = FlexAlign.Start, Gradient = K.Wash(host._tone), HitTestVisible = false },
                        new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(inset, 0f, inset, 0f), Children = [host.Hero(v, lay, live: true)] },
                    ],
                };
                return new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [on ? body.StickyClip(K.BarH) : body] };
            }
        }

        /// <summary>[1] About the show as an Expander where the pane is not (641-1007, ≤640); nothing at ≥1008.</summary>
        sealed class AboutSlot : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                var lay = host._lay.Value;
                float inset = host._inset.Value;
                if (lay.Pane) return new BoxEl { Grow = 1f, Basis = 0f, Height = 0f };
                var full = host._p.View.Value;
                var v = full.Load == PodcastLoad.Pending ? s_placeholder : full;
                return new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Padding = new Edges4(inset, 0f, inset, 0f),
                    Children = [host.AboutExpander(ViewOf(HeadOf(v)))],
                };
            }
        }

        /// <summary>[2] the toolbar's content (the sticky band around it is the slot itself).</summary>
        sealed class ToolbarSlot : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                var full = host._p.View.Value;
                var v = full.Load == PodcastLoad.Pending ? s_placeholder : full;
                var lay = host._lay.Value;
                float inset = host._inset.Value, listW = host._listW.Value;
                return new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Padding = host.Gutters(lay, inset),
                    Children = [host.Toolbar(v, lay, WinUiPodcastShowTiers.SearchCollapsed(lay, listW), WinUiPodcastShowTiers.FilterCombo(lay, listW))],
                };
            }
        }

        /// <summary>A group header's content: the year (or "New since your last visit", "In order") with its count, the jump list, Mark all as played.</summary>
        sealed class HeadSlot(PageHost host, IReadSignal<ShowSlot> item) : Component
        {
            public override Element Render()
            {
                var slot = item.Value;
                var lay = host._lay.Value;
                float inset = host._inset.Value;
                if (slot.Group is not { } g) return new BoxEl { Height = lay.GroupH };
                var years = host._p.View.Peek().Years;
                return new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Padding = host.Gutters(lay, inset),
                    Children = [host.GroupHeadRow(g, years, lay, live: true)],
                };
            }
        }

        /// <summary>An episode row, the "Load more" foot or the empty line.</summary>
        sealed class BodySlot(PageHost host, IReadSignal<ShowSlot> item) : Component
        {
            public override Element Render()
            {
                var slot = item.Value;
                var lay = host._lay.Value;
                float inset = host._inset.Value;
                Element body = slot.Kind switch
                {
                    // ONE row per slot (a constant key): a recycled slot re-renders its row for the new episode instead of mounting a new one, so a filter
                    // switch that brings rows back costs their props, not their mount.
                    PodcastItemKind.Row when slot.Vm is { } vm => WinUiPodcastKit.EpisodeRow(vm, lay.RowMode, lay.RowDescription, lay.Stacked, host._verbs!) with { Key = "ep" },
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
                var pad = host.Gutters(lay, inset);
                return new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Padding = pad with { Top = slot.Kind == PodcastItemKind.Row && !slot.First ? K.RowGap : 0f },
                    Children = [body],
                };
            }
        }
    }
}
