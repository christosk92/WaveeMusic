// ── Themes/Zune/Structure/Views/Podcast/ZunePodcastShow.List.cs — Zune's show page as ONE virtualised list (podcast pages list, the shared approach) ──
//
// The owner's show has 459 episodes: every mounted row cost each scroll frame. The page is one ItemsView over the core's flattened list model
// (`PodcastShowView.Items`), so only the items on screen are mounted (the WinUI plan, in Zune's look):
//
//   ZStack
//   ├─ ItemsView (the page's scroller)   items = 5 prefix + (on the episodes pivot) Items
//   │  ├─ [0] the name (bleeding to the window's edge)                               persistent prefix
//   │  ├─ [1] the pivots                                                             persistent prefix
//   │  ├─ [2] the hero                                                               persistent prefix
//   │  ├─ [3] the toolbar: a RAW sticky band at the viewport's top (episodes pivot; empty otherwise)   persistent prefix
//   │  ├─ [4] the about / more like this body (empty on the episodes pivot)          persistent prefix
//   │  └─ [5..] Items: a group header · an episode row · the "load more" foot · the empty line
//   └─ the pinned group header: an overlay under the toolbar naming the group the list is in at that line (the list's measured layout says which),
//      backed by the page's layer only while it shows; the rows cut themselves at the toolbar's lower edge (the list's item clip), so nothing at rest
//      paints a band.
//
// Every slot reads the tier record, the column's lead and the pivot from signals the page writes on render, so a crossing re-pads the slots in place.

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
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using K = Wavee.Themes.Zune.ZunePodcastKit;
using Z = Wavee.Themes.Zune.ZunePodcastRules;

namespace Wavee.Themes.Zune;

internal static partial class ZunePodcastShow
{
    /// <summary>One slot's item: a prefix part or a list item resolved against the snapshot (records compare by value: an unchanged slot does not
    /// re-render when the list around it does). <see cref="Last"/>: the list's last item (it carries the page's foot).</summary>
    sealed record ShowSlot(int Prefix, PodcastItemKind Kind, PodcastGroup? Group, PodcastEpisodeVm? Vm, bool First, bool Last)
    {
        public static readonly ShowSlot None = new(-1, PodcastItemKind.Empty, null, null, false, false);
    }

    /// <summary>What the list projects: the snapshot and the pivot (the rows exist only on the episodes pivot).</summary>
    sealed record ListSnap(PodcastShowView View, int Pivot);

    sealed partial class PageHost
    {
        const int PrefixCount = 5;

        readonly ItemsViewController _ctl = new();
        readonly Signal<ZunePodcastLayout> _layS = new(ZunePodcastTiers.For(WidthTier.Standard));
        readonly Signal<float> _leadS = new(0f);
        readonly Signal<bool> _toolbarStuck = new(false);
        Func<ListSnap>? _snapOf;
        Memo<ListSnap>? _snap;
        BoundItemsSource<ShowSlot>? _items;
        ListOptions<ShowSlot>? _listOptions;
        RepeatLayout? _listLayout;
        MeasuredStackVirtualLayout? _measured;
        Func<int>? _pinnedOf;
        Memo<int>? _pinned;
        Func<BoundItemScope<ShowSlot>, Element>? _template;

        /// <summary>The list's memos (the snapshot with the pivot, the pinned group), made once per render in a stable order.</summary>
        void UseListMemos()
        {
            _snapOf ??= () =>
            {
                int pivot = _pivot.Value;
                var v = _p.View.Value;
                if (pivot == (int)Pivot.More && v.Similar.IsDefaultOrEmpty) pivot = (int)Pivot.Episodes;
                return new ListSnap(v, pivot);
            };
            _snap = UseComputed(_snapOf);
            _pinnedOf ??= PinnedGroup;
            _pinned = UseComputed(_pinnedOf);
        }

        /// <summary>The live page: the list and the pinned group header over it.</summary>
        Element LivePage()
        {
            _items ??= BoundItems.Project<ListSnap, ShowSlot>(_snap!, static s => PrefixCount + (s.Pivot == (int)Pivot.Episodes ? s.View.Items.Length : 0), SlotAt, ShowSlot.None);
            _measured ??= new MeasuredStackVirtualLayout(Z.RowEstimate, false, ExtentOf);
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
                    // The rows cut themselves at the pinned toolbar's lower edge: no band is painted at rest.
                    ItemClipTopInset = Z.ToolbarH,
                },
            };
            Element list = ItemsView.CreateBound<ShowSlot>(_items, _template, _listLayout.Value, _listOptions) with { Key = "podcast-list:" + _p.RouteKey };
            Element pinned = Embed.Comp(new SlotProps(this), static () => new PinnedHead()) with { Key = "show-pinned-head" };
            return new BoxEl
            {
                Key = "podcast-page:" + _p.RouteKey, ZStack = true, Direction = 1, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    list,
                    new BoxEl
                    {
                        Key = "show-pinned-head-layer", Direction = 1, HitTestPassThrough = true, MinWidth = 0f, IsolateLayout = true, AlignSelf = FlexAlign.Start,
                        Padding = new Edges4(0f, Z.ToolbarH, 0f, 0f), Children = [pinned],
                    },
                ],
            };
        }

        static ShowSlot SlotAt(ListSnap s, int i)
        {
            var v = s.View;
            bool episodes = s.Pivot == (int)Pivot.Episodes;
            int count = PrefixCount + (episodes ? v.Items.Length : 0);
            if (i < PrefixCount) return new ShowSlot(i, PodcastItemKind.Empty, null, null, false, i == count - 1);
            int k = i - PrefixCount;
            if (!episodes || (uint)k >= (uint)v.Items.Length) return ShowSlot.None;
            var it = v.Items[k];
            var g = (uint)it.Group < (uint)v.Groups.Length ? v.Groups[it.Group] : null;
            var vm = it.Kind == PodcastItemKind.Row && (uint)it.Episode < (uint)v.Episodes.Length ? v.Episodes[it.Episode] : null;
            return new ShowSlot(-1, it.Kind, it.Kind == PodcastItemKind.Group ? g : null, vm, it.First, i == count - 1);
        }

        PodcastItemKind KindAt(int i)
        {
            if (i < PrefixCount) return PodcastItemKind.Empty;
            var v = _p.View.Peek();
            int k = i - PrefixCount;
            return (uint)k < (uint)v.Items.Length ? v.Items[k].Kind : PodcastItemKind.Empty;
        }

        /// <summary>Recycle pools per kind: a row never rebinds as a header; each prefix part is its own.</summary>
        int ContentTypeOf(int i) => i < PrefixCount ? Z.PrefixType + i : (int)KindAt(i);

        /// <summary>An item's seeded extent (the measured one replaces it on realize).</summary>
        float ExtentOf(int i)
        {
            var lay = _layS.Peek();
            return i switch
            {
                0 => lay.TitleLine * (lay.TitleWraps ? 2f : 1f) + Z.TitleDescent,
                1 => Z.PivotH + Z.PivotBottom,
                2 => lay.Art + Z.HeroBottom,
                3 => Z.ToolbarH,
                4 => 0f,
                _ => KindAt(i) switch
                {
                    PodcastItemKind.Group => Z.GroupH,
                    PodcastItemKind.Row => lay.RowDescription ? Z.RowEstimateTall : Z.RowEstimate,
                    _ => Z.FootEstimate,
                },
            };
        }

        Element Template(BoundItemScope<ShowSlot> scope)
        {
            int i = scope.Row.Index.Peek();
            var host = this;
            switch (i)
            {
                case 0: return Embed.Comp(new SlotProps(host), static () => new TitleSlot());
                case 1: return Embed.Comp(new SlotProps(host), static () => new PivotSlot());
                case 2: return Embed.Comp(new SlotProps(host), static () => new HeroSlot());
                case 3:
                    // RAW: the sticky root must be the slot itself (a sticky root returned from a component never pins).
                    return new BoxEl
                    {
                        Key = "show-toolbar", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children = [Embed.Comp(new SlotProps(host), static () => new ToolbarSlot())],
                    }.Sticky(0f, null, _toolbarStuck);
                case 4: return Embed.Comp(new SlotProps(host), static () => new OtherSlot());
            }
            if (scope.Item.Peek().Kind == PodcastItemKind.Group) return Embed.Comp(() => new HeadSlot(host, scope.Item));
            return Embed.Comp(() => new BodySlot(host, scope.Item));
        }

        /// <summary>A slot on the page column: the lead each side (the frame's gutter, or more past the 2400 cap) as the house's easing spacers
        /// (`ZuneFrameRules.Gutter`), so a crossing that changes the gutter glides the slot's content instead of snapping it; <paramref name="bleed"/>
        /// keeps no right spacer (the name and the pivots run to the window's edge). <paramref name="foot"/>: the page's foot under it.</summary>
        BoxEl Column(Element content, bool bleed = false, float foot = 0f, Prop<ColorF>? fill = null)
        {
            float lead = _leadS.Value;
            var body = new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [content] };
            return new BoxEl
            {
                // A crossing steps an item's height (the name wraps at Compact, the hero stacks, a row's title takes two lines): it eases through real
                // layout, so the list's items after it ride it instead of jumping.
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, foot), Fill = fill ?? (Prop<ColorF>)ColorF.Transparent,
                Layout = ZuneMotion.HeightResize,
                Children = bleed ? [ZuneFrameRules.Gutter(lead), body] : [ZuneFrameRules.Gutter(lead), body, ZuneFrameRules.Gutter(lead)],
            };
        }

        /// <summary>The page's foot past the last item (the dock's reserve).</summary>
        static float Foot => Z.PageFoot + R.Space.DockReserve;

        /// <summary>Jump to a year: its header to the top of the list, under the toolbar.</summary>
        internal void JumpToGroup(string groupKey)
        {
            var v = _p.View.Peek();
            for (int k = 0; k < v.Items.Length; k++)
            {
                var it = v.Items[k];
                if (it.Kind != PodcastItemKind.Group || (uint)it.Group >= (uint)v.Groups.Length || v.Groups[it.Group].Key != groupKey) continue;
                if (_measured is { } m) _scroll.ScrollTo(Math.Max(0.0, m.OffsetOf(PrefixCount + k, 0f) - Z.ToolbarH), Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide);
                else _ctl.StartBringItemIntoView(PrefixCount + k, alignmentRatio: 0f, animate: false);
                return;
            }
        }

        /// <summary>The group the list is in at the line under the toolbar (-1: the toolbar is not pinned, or the line is above the first header). A memo
        /// over the offset: the pinned header re-renders only when the group changes.</summary>
        int PinnedGroup()
        {
            if (!_toolbarStuck.Value || _measured is not { } m || _snap is not { } snap) return -1;
            var s = snap.Value;
            if (s.Pivot != (int)Pivot.Episodes) return -1;
            double o = _scroll.Offset.Value;
            var v = s.View;
            int i = m.IndexAt((float)o + Z.ToolbarH, 0f) - PrefixCount;
            if (i < 0 || v.Items.Length == 0) return -1;
            if (i >= v.Items.Length) i = v.Items.Length - 1;
            // The header's own slot still at the line: it shows itself.
            if (v.Items[i].Kind == PodcastItemKind.Group && m.OffsetOf(PrefixCount + i, 0f) >= o + Z.ToolbarH - 1f) return -1;
            for (; i >= 0; i--) if (v.Items[i].Group >= 0) return v.Items[i].Group;
            return -1;
        }

        sealed record SlotProps(PageHost Host);

        /// <summary>The pinned group header over the list (the page's layer behind it while it shows; nothing otherwise).</summary>
        sealed class PinnedHead : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                int gi = host._pinned?.Value ?? -1;
                var v = host._p.View.Peek();
                if ((uint)gi >= (uint)v.Groups.Length) return new BoxEl { Height = 0f };
                return host.Column(GroupHeader(host, host.State, v.Years, v.Groups[gi], live: true), fill: Prop.Of(static () => R.Layer.ContentSurface))
                    with { Key = "show-pinned-head:" + v.Groups[gi].Key };
            }
        }

        /// <summary>[0] the name, from the column's lead to the window's edge.</summary>
        sealed class TitleSlot : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                var h = host._head!.Value;
                var lay = host._layS.Value;
                string title = h.Load == PodcastLoad.Pending ? s_placeholder.Head.Title : h.Head.Title;
                return host.Column(K.Title(title, lay, 0f), bleed: true);
            }
        }

        /// <summary>[1] the pivots, from the column's lead to the window's edge.</summary>
        sealed class PivotSlot : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                _ = host._head!.Value;
                _ = host._pivot.Value;
                var lay = host._layS.Value;
                return host.Column(K.Pivots("zp-pivots", host._pivotItems, host._pivotNow, host._select!, lay, 0f), bleed: true);
            }
        }

        /// <summary>[2] the hero.</summary>
        sealed class HeroSlot : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                var h = host._head!.Value;
                var lay = host._layS.Value;
                return host.Column(host.Hero(h, lay, live: true));
            }
        }

        /// <summary>[3] the toolbar's content on the episodes pivot (the sticky band around it is the slot itself); nothing on the others.</summary>
        sealed class ToolbarSlot : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                var s = host._snap!.Value;
                if (s.Pivot != (int)Pivot.Episodes) return new BoxEl { Height = 0f };
                var v = s.View.Load == PodcastLoad.Pending ? s_placeholder : s.View;
                var lay = host._layS.Value;
                return host.Column(new BoxEl
                {
                    Key = "show-toolbar:row", Direction = 0, Height = Z.ToolbarH, Gap = Z.CommandGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children = Toolbar(host, host.State, v, lay, live: true),
                });
            }
        }

        /// <summary>[4] the about / more like this body; nothing on the episodes pivot.</summary>
        sealed class OtherSlot : Component
        {
            public override Element Render()
            {
                var host = UseProps<SlotProps>().Host;
                var s = host._snap!.Value;
                var h = host._head!.Value;
                var lay = host._layS.Value;
                var p = host._p;
                Element? body = (Pivot)s.Pivot switch
                {
                    Pivot.About => K.Body("zp-body:about", host.About(h, lay)),
                    Pivot.More => K.Body("zp-body:more", K.Cards("zp-more", h.Similar, p.OpenUri, lay.Stacked, p.CardMenu, p.PlayCard)),
                    _ => null,
                };
                if (body is null) return new BoxEl { Height = 0f };
                return host.Column(body.Value, foot: Foot);
            }
        }

        /// <summary>A group header (the year, "new since your last visit" with mark all as played, "in order").</summary>
        sealed class HeadSlot(PageHost host, IReadSignal<ShowSlot> item) : Component
        {
            public override Element Render()
            {
                var slot = item.Value;
                if (slot.Group is not { } g) return new BoxEl { Height = Z.GroupH };
                return host.Column(GroupHeader(host, host.State, host._p.View.Peek().Years, g, live: true), foot: slot.Last ? Foot : 0f);
            }
        }

        /// <summary>An episode row, the "load more" foot or the empty line.</summary>
        sealed class BodySlot(PageHost host, IReadSignal<ShowSlot> item) : Component
        {
            public override Element Render()
            {
                var slot = item.Value;
                var lay = host._layS.Value;
                var p = host._p;
                Element body = slot.Kind switch
                {
                    // ONE row per slot (a constant key): a recycled slot re-renders its row for the new episode instead of mounting a new one.
                    PodcastItemKind.Row when slot.Vm is { } vm => K.EpisodeRow(vm, lay.RowDescription, lay.Stacked, host.Verbs) with { Key = "ep" },
                    PodcastItemKind.More => new BoxEl
                    {
                        Key = "show-more", Direction = 0, Padding = new Edges4(0f, Z.MoreTop, 0f, 0f),
                        Children = [K.Button("load-more", Loc.Get(Strings.Podcast.Pages.LoadMore), null, p.LoadMore, lay.Target)],
                    },
                    PodcastItemKind.Empty => new BoxEl
                    {
                        Key = "show-empty", Direction = 0, Gap = Z.CommandGap, AlignItems = FlexAlign.Center, Wrap = true, Padding = new Edges4(0f, Z.EmptyPad, 0f, Z.EmptyPad),
                        Children =
                        [
                            R.Type.TrackTitle(Loc.Get(Strings.Podcast.Pages.NoMatch)) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label) },
                            K.Link("show-clear", Loc.Get(Strings.Podcast.Pages.ClearFilter), p.ClearFilter, lay.Target),
                        ],
                    },
                    _ => new BoxEl(),
                };
                return host.Column(body, foot: slot.Last ? Foot : 0f);
            }
        }
    }
}
