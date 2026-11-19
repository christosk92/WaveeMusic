// ── Platform/Controls.StripPager.cs ────────────────────────────────────────────────────────────────────────────────
// THE STRIP PAGER: a sideways strip's ‹ › page buttons on hover (Classic's and WinUI's Settings category strip)
//
// Role: UI
// Owner: O
// Wave: settings rework (owner report 2026-11-19: "without trackpad this is not scrollable")
// Budget: 160 lines
// Spec: DERIVED (the shelves' pager button, `PagedShelf`'s hover-edge affordance)
//
// ── WHY IT EXISTS ────────────────────────────────────────────────────────────────────────────────────────────────────
//
// A strip of words that runs past its column (the Settings categories at a narrow window, beside an open pane, at Compact) scrolls sideways and fades
// its end, but a mouse has no sideways wheel: without a trackpad the clipped words could not be reached. The shelves answer the same question with a
// pager, so the strip pages the way they do: hovering it shows a ‹ at the start when words wait before the view and a › at the end when more remain,
// and a click glides the strip a page on (the engine's own scroll glide, `ScrollHandle.ScrollBy`).
//
// ── WHAT IS REUSED ───────────────────────────────────────────────────────────────────────────────────────────────────
//
// The button is the shelves' pager button: the stock `IconButton` with the chevron glyphs, exactly what Classic's and WinUI's Home shelves put in their
// header tools (`ClassicHomeZones` / `WinUiHomeZones.*ShelfTools`), so each theme's own IconButton size, glyph, hover and press apply. Over the strip it
// rests on the solid fill the engine's own overflowing tab strip gives its edge buttons (`TabStrip`'s scroll pips: `FillSolidSecondary`, hover
// `FillSolidTertiary`), because a shelf header's transparent rest let the faded word under the chevron read through it ("‹me"). `PagedShelf`'s own
// hover-edge buttons cannot carry a strip: they live inside its virtualized card strip (equal fitted cards), and the Settings strip is ONE SelectorBar
// whose roving focus and pill must stay one control. The paging is the scroller's handle: `AtStart` / `AtEnd` say which side hides words, `ScrollBy`
// glides.
//
// ── THE RULES IT KEEPS ───────────────────────────────────────────────────────────────────────────────────────────────
//
// · No layout jump: the buttons are layers of a ZStack over the strip's ends; they never reflow the words, and the strip keeps its edge fade.
// · Mouse and pen only: the hover comes from `OnPointerMoveWithin` (never delivered for touch), so a touch or trackpad user pans as before and a tap at
//   the strip's end always reaches the word under it. The pointer leaving the strip (`OnPointerExit`, its subtree's edge) hides them.
// · A page is the view less the two fade bands (`ScrollEdgeCueResolver.DefaultBandDip`, the feather the strip draws), so the word fading under the ›
//   lands whole beside the ‹.
// · Keyboard is the strip's own (Left/Right move through the items and bring each into view): the buttons take no tab stop and never take focus.
// · A button comes and goes with a fade (`MotionTok.ControlFast`, the shared ~160 ms fade); reduced motion cuts the glide.
// · Zero allocation after mount: the handle, the hover cell, the handlers and the two Show thunks are built once; a hover edge flips one cell.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Controls
{
    /// <summary>A sideways strip's hover pagers. See the file header.</summary>
    public static class StripPager
    {
        /// <summary>The strip <paramref name="strip"/> (a horizontal <see cref="ScrollEl"/>; its <c>Handle</c> is the pager's) under the ‹ › page buttons,
        /// keyed <paramref name="key"/><c>:prev</c> and <c>:next</c>. <paramref name="target"/> is the buttons' square size where the theme asks for a hit
        /// target (Compact's 40); NaN keeps the stock IconButton.</summary>
        public static Element Create(ScrollEl strip, string key, float target = float.NaN)
            => Embed.Comp(new Props(strip, key, target), static () => new Host());

        /// <summary>A button's arrival and leaving: a fade in place.</summary>
        static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

        sealed record Props(ScrollEl Strip, string Key, float Target);

        sealed class Host : Component
        {
            readonly ScrollHandle _scroll = new();
            readonly Signal<bool> _pointer = new(false);
            readonly Action<Point2> _moved;
            readonly Action _left, _prev, _next;
            readonly Func<bool> _showPrev, _showNext;

            public Host()
            {
                _moved = _ => { if (!_pointer.Peek()) _pointer.Value = true; };
                _left = () => { if (_pointer.Peek()) _pointer.Value = false; };
                _showPrev = () => _pointer.Value && !_scroll.AtStart.Value;
                _showNext = () => _pointer.Value && !_scroll.AtEnd.Value;
                _prev = () => Page(-1f);
                _next = () => Page(1f);
            }

            /// <summary>Glide the strip a page toward <paramref name="direction"/> (the handle clamps at either end).</summary>
            void Page(float direction)
            {
                if (!_scroll.IsBound) return;
                double step = Math.Max(0.0, _scroll.Viewport - 2.0 * ScrollEdgeCueResolver.DefaultBandDip);
                _scroll.ScrollBy(direction * step, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide);
            }

            public override Element Render()
            {
                var p = UseProps<Props>();
                return new BoxEl
                {
                    ZStack = true, MinWidth = 0f, Shrink = 0f,
                    OnPointerMoveWithin = _moved, OnPointerExit = _left,
                    Children =
                    [
                        p.Strip with { Handle = _scroll },
                        Flow.Show(_showPrev, Button(p, back: true)),
                        Flow.Show(_showNext, Button(p, back: false)),
                    ],
                };
            }

            Element Button(Props p, bool back)
            {
                var style = IconButton.DefaultStyle with
                {
                    Fill = Tok.FillSolidSecondary, HoverFill = Tok.FillSolidTertiary, PressedFill = Tok.FillSubtleTertiary,
                };
                if (!float.IsNaN(p.Target)) style = style with { Size = p.Target };
                return IconButton.Create(back ? Icons.ChevronLeft : Icons.ChevronRight, back ? _prev : _next, style) with
                {
                    Key = p.Key + (back ? ":prev" : ":next"),
                    JustifySelf = back ? FlexAlign.Start : FlexAlign.End, AlignSelf = FlexAlign.Center,
                    Focusable = false,
                    Enter = s_fade, Exit = s_fade, Transition = MotionTok.ControlFast,
                };
            }
        }
    }
}
