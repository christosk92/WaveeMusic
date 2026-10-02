// ── Platform/Surface.Host.cs ───────────────────────────────────────────────────────────────────────────────────────
// THE media surface: `Controls.Surface(CardData, SurfaceShape, width)` and its ONE host — every card, tile and row.
//
// Role: UI
// Owner: L
// Wave: 0 (shared media surface)
// Budget: 250 lines
// Spec: docs/plans/wavee/shared-media-surface-implementation.md §2.2–§2.6
//
// ── ONE HOST, TWO MODES ──────────────────────────────────────────────────────────────────────────────────────────────
//
// The host discovers its OWN hosting mode from context. Inside a bound `ItemsView` slot (`Controls.BoundSurface`) or a
// `PagedShelf` card slot, the slot root provides `ItemsView.SlotRow` and owns invoke and the roving tab stop (ONE
// RELEASE, ONE OWNER — input-a11y.md §6.5): the shell renders with no click, no focus stop and no role, keeps the hand
// cursor (the slot is invokable), and reads the slot's focus bit (`RowScope.IsFocused`) as part of "hot". Anywhere else
// it owns them itself: the shell is the tab stop and Enter/Space are the engine's click. Everything else — plate, chrome,
// "…", menu, drag, drop, tooltip, selected skin, seed face — is identical in both, because it is the same tree.
//
// Hover, press and focus are FIELD writes folded into ONE equality-gated signal (`_hot`): a pointer sweeping across a
// hot surface schedules nothing, and the hot edge re-renders exactly this surface to mount its lazy chrome.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Controls
{
    /// <summary>THE media surface: what it shows is <paramref name="d"/> (an adapter's data, compared by data), how it is
    /// laid out is <paramref name="shape"/> (a <see cref="Shape"/> preset). <paramref name="width"/> is the shelf's card
    /// width — NaN for every fluid (grid) and row shape. A caller that needs a reconciler KEY puts it on the returned
    /// element (<c>Surface(d, s) with { Key = … }</c>).
    /// <para>The skeleton face of a pending region is THIS tree, derived (<c>DeriveRenderedOutput</c>: text → bars, images
    /// keep their aspect, chrome and handlers stripped) — one geometry, one description. A blank item is
    /// <see cref="CardData.Seed"/>, which the host renders as its own bone face; inside a pending region that face IS the
    /// skeleton, verbatim (<c>SkeletonOverride</c>): re-deriving it would collapse the fluid grid cover (an
    /// <c>AspectRatio</c>-only box derives to a 0×0 spacer) and drop the title bar's width cap, and would mount a host per
    /// placeholder during load.</para></summary>
    public static Element Surface(CardData d, SurfaceShape shape, float width = float.NaN)
    {
        var el = Embed.Comp(new SurfaceProps(d, shape, width), static () => new SurfaceHost());
        return d.IsSeed
            ? el with { SkeletonOverride = SurfaceParts.Seed(shape, width, d.Height, CoverShape.IsCircular(d.Circular, d.CoverAspect)) }
            : el with
            {
                SkeletonProxy = () => Embed.Comp(new SurfaceProps(d, shape, width), static () => new SurfaceHost())
                    with { DeriveRenderedOutput = true },
            };
    }

    /// <summary>The surface's re-pushed props: the data (<see cref="CardData"/>'s data-only equality), the shape (a value)
    /// and the shelf's card width (NaN for every fluid/row shape — <c>float.Equals</c>, so NaN equals NaN). No focus
    /// signal: the host learns slot mode from context. Public only so the equality rule can be pinned by a fact.</summary>
    public sealed record SurfaceProps(CardData Data, SurfaceShape Shape, float Width = float.NaN)
    {
        public bool Equals(SurfaceProps? o)
            => o is not null && (ReferenceEquals(this, o)
               || (Shape.Equals(o.Shape) && Width.Equals(o.Width) && Data.Equals(o.Data)));

        public override int GetHashCode() => HashCode.Combine(Data, Shape, Width);
    }

    /// <summary>The ONE host (the <c>NowPlayingOverlayHost</c> pattern): an <see cref="IPropsHost"/> whose every re-push
    /// lands in <see cref="_latest"/> while renders gate on the data-equal <see cref="_props"/> signal, and every delegate
    /// the tree carries is a per-host TRAMPOLINE into <see cref="_latest"/> — so a data-equal re-push with fresh closures
    /// is honoured on the next click, play, drag or menu without a render.
    /// <para>HOT folds three inputs by <see cref="CardChromeRules.Hot"/>: the pointer (<c>OnHoverMove</c>/
    /// <c>OnPointerExit</c>, delivered on the shell's SUBTREE edges, so moving onto the FAB is not an exit and the node
    /// being pressed is never unmounted under the press), focus WITHIN (<see cref="CardChromeRules.FocusWithin"/> of the
    /// shell's own routed edge and the inner wrapper's), and — in slot mode — the slot root's focus bit.</para></summary>
    sealed class SurfaceHost : Component, IPropsHost
    {
        SurfaceProps? _latest;
        readonly Signal<SurfaceProps?> _props = new(null);
        readonly Signal<bool> _hot = new(false);
        bool _pointerIn, _selfFocus, _innerFocus;
        // The label block / text column, built once per GATED props instance: the hot edge re-renders this host to mount
        // its chrome, and handing the title's trim tooltip the SAME element keeps it (and its ToolTip) from re-rendering.
        SurfaceProps? _textFor;
        Element? _text;
        // Trampolines and handlers are built ONCE per host: reference-stable across renders, reading `_latest` at
        // invocation time.
        readonly Action _onClick, _onPlay, _exit;
        readonly Action<Point2> _enter;
        readonly Action<bool> _selfFocusChanged, _innerFocusChanged;
        readonly Func<object?> _dragPayload;
        readonly Func<ContextMenuModel?> _menu;

        public SurfaceHost()
        {
            _onClick = () => _latest?.Data.OnClick?.Invoke();
            _onPlay = () => _latest?.Data.OnPlay?.Invoke();
            _dragPayload = () => _latest?.Data.Drag?.PayloadFactory();
            _menu = () => _latest?.Data.Menu?.Invoke();
            _enter = _ => { _pointerIn = true; Fold(); };
            _exit = () => { _pointerIn = false; Fold(); };
            _selfFocusChanged = got => { _selfFocus = got; Fold(); };
            _innerFocusChanged = got => { _innerFocus = got; Fold(); };
        }

        // Field writes + ONE equality-gated signal write: a pointer sweeping across a hot surface schedules nothing.
        void Fold() => _hot.Value = CardChromeRules.Hot(_pointerIn, CardChromeRules.FocusWithin(_selfFocus, _innerFocus));

        public void ApplyProps(object props)
        {
            _latest = (SurfaceProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            // The context reads come BEFORE the early return, so the hook order never depends on the props being seeded.
            var overlay = UseContext(Overlay.Service);
            RowScope? row = UseContext(ItemsView.SlotRow);
            var p = _props.Value;
            if (p is null) return new BoxEl();
            var d = p.Data;
            var s = p.Shape;

            // A seed is the shape's bone face: disabled, no plate, no chrome, no handlers. In a bound slot its owner also
            // disables the row, so the slot root dims and refuses invoke.
            if (d.IsSeed) return SurfaceParts.Seed(in s, p.Width, d.Height, CoverShape.IsCircular(d.Circular, d.CoverAspect));

            bool slot = row is not null;
            // HOT first, then the relation COARSE-first (the NowPlayingOverlayHost discipline): a hot surface never asks
            // `RelatesTo`, so it does not join the hot identity fan-out; an idle one reads one app-wide bool and stops. In
            // slot mode the keyboard focus is the SLOT ROOT's: reading its bit subscribes this host, so a Tab onto the
            // slot re-renders exactly this surface hot.
            bool hot = _hot.Value || (slot && row!.Value.IsFocused?.Value == true);
            bool relates = false;
            if (!hot && NowPlaying is { } pb && pb.HasActiveContext.Value) relates = pb.RelatesTo(d.Uri);
            bool chrome = CardChromeRules.Mounted(hot, relates);
            bool overlayOn = SurfaceRules.ChromeMounted(hot, relates, s.Play, d.OnPlay is not null);
            // A "…" re-enters the context funnel, so it exists only where a menu is attached — a menu AND a host for it.
            bool hasMenu = d.Menu is not null && !IsNullOverlay(overlay);
            // Presence is the adapter's (a surface with nothing to play grows no dead FAB); the handler is the trampoline.
            Action? play = d.OnPlay is null ? null : _onPlay;

            if (!ReferenceEquals(_textFor, p))
            {
                _textFor = p;
                _text = s.IsRow ? SurfaceParts.RowText(d, in s) : SurfaceParts.Labels(d, in s, SurfaceParts.InnerOf(p.Width));
            }
            Element body = s.IsRow
                ? SurfaceParts.RowBody(d, in s, overlayOn, chrome, hasMenu, play, _text)
                : SurfaceParts.StackBody(d, in s, p.Width, overlayOn, chrome, hasMenu, play, _text);
            // FOCUS WITHIN, with no scene walk: the dispatcher fires OnFocusChanged on an ancestor as a ROUTED boundary
            // event (focus entering/leaving its SUBTREE), so this NON-focusable wrapper hears a Tab from the shell onto
            // the surface's own FAB as an ENTRY — the very move the shell itself reads as a loss. Layout-transparent (a
            // column growing into the shell) and hover-scope transparent.
            Element within = new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, HoverScopeTransparent = true,
                OnFocusChanged = _innerFocusChanged,
                Children = [body],
            };

            var mode = SurfaceRules.Ownership(slot, hasClick: d.OnClick is not null);
            // The adapter's DATA (Kind, Style) with the trampoline payload factory — rebuilt only when this host renders.
            DragSource? drag = d.Drag is { } ds ? new DragSource(ds.Kind, _dragPayload) { Style = ds.Style } : null;
            // The shell is the surface's outermost hit-testable node, so the hover/exit/focus handlers belong exactly
            // here — the subtree edges the dispatcher delivers them on are this node's.
            BoxEl shell = SurfaceParts.Shell(within, in s, in mode, d.Uri) with
            {
                OnClick = mode.OwnsClick ? _onClick : null,
                OnHoverMove = _enter, OnPointerExit = _exit,
                OnFocusChanged = mode.OwnsFocus ? _selfFocusChanged : null,
                Draggable = drag, DropTarget = d.Drop,
            };
            // A PINNED height zeroes Grow/Shrink with it: Height is only the flex basis, and the shell's Grow = 1 would
            // otherwise stretch the hover plate into a row taller than the card (Controls.Art.cs header, rule 5).
            if (!float.IsNaN(d.Height)) shell = shell with { Height = d.Height, Grow = 0f, Shrink = 0f };
            // The opened surface wears the accent border + the brighter fill. The accent is read HERE, in this host's
            // render, so a palette landing repaints the one selected surface and no other; the NEWEST pushed thunk is
            // honoured through `_latest`, like every other delegate.
            if (d.Selected) shell = SurfaceParts.Selected(shell, _latest?.Data.SelectedAccent);
            // The menu rides on the shell in BOTH modes: the right-click funnel is not a gesture owner.
            if (hasMenu) shell = ContextMenu.Attach(shell, overlay!, _menu);
            Element root = s.IsRow || float.IsNaN(p.Width) ? shell : SurfaceParts.Gutter(shell, p.Width);
            return SurfaceRules.TitleTip(trimmed: false, hasLabels: s.Labels) ? LabelTip(root, d.Title) : root;
        }

        // A label-less shape's tooltip IS its label (the rail tile). The ToolTip is a component boundary the skeleton
        // deriver cannot see into, so it is handed the surface it wraps.
        static Element LabelTip(Element root, string title)
        {
            Element tip = ToolTip.Wrap(root, title);
            return tip is ComponentEl c ? c with { SkeletonProxy = () => root } : tip;
        }
    }
}
