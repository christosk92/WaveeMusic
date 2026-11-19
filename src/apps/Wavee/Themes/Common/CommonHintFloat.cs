// ── Themes/Common/CommonHintFloat.cs — a hint's anchored line: the overlay mechanics a theme's picture floats in (settings rework phase 6) ─────
//
// A hint that POINTS at a control (the core's `Hints.Pointer`: the play button, the volume, the queue's Crossfade switch, Classic's page head)
// is drawn by Classic and Zune as a line next to that control, never inside the surface's layout: it floats in the overlay layer, anchored to
// the hint's target, so nothing on the page moves when it comes or goes. This is only the mechanics, shared once: the picture (Classic's strip
// on its notice plate, Zune's lowercase line on the bar's band) is the theme's `Content`, entering with the theme's own motion.
//
// IT STAYS IN ITS SURFACE. The line never hangs over the page beside the surface its control lives in (`HintProps.Bounds`: the queue panel,
// the player bar, the page head's band; the window where none is given): its width is the theme's reading width at most, never more than the
// surface less the theme's inset on both sides (measured when it opens, again when the window's tier changes), so a long line wraps to more
// rows instead of widening; and it is placed on the side the theme names (below or above the control: the side that covers least), aligned
// to the control's start, centre or end and then held inside the surface's inset column, following the control while it is up.
//
// The overlay is a raw one (the tooltip chrome's 167 ms fade), never a focus trap and never light-dismissed: the hint's own words answer it
// (its action, "not now", "×"), it takes no focus, and the host closes it with its target. It opens again when the offer turns into its
// confirmation.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

/// <summary>Where a floating line lines up with its control, along the surface (see the file header).</summary>
internal enum HintFloatAlign : byte { Start, Center, End }

/// <summary>Where a floating line sits: below or above its control, its alignment, the inset it keeps from its surface's edges and the widest it
/// grows (all the theme's Rules numbers).</summary>
internal readonly record struct HintFloatPlace(bool Below, HintFloatAlign Align, float Inset, float MaxWidth);

/// <summary>The anchored line's overlay (see the file header).</summary>
internal static class CommonHintFloat
{
    static readonly BoxEl s_zero = new() { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };

    /// <summary>Floats <paramref name="content"/> (built from the hint at the width it may take) at the hint's target, inside its surface.
    /// <paramref name="stamp"/> reopens it when the layout it was measured for changes (the window's tier).</summary>
    public static Element Create(HintProps hint, HintFloatPlace place, int stamp, Func<HintProps, float, Element> content)
        => Embed.Comp(new Props(hint, place, stamp, content), static () => new Host());

    sealed record Props(HintProps Hint, HintFloatPlace Place, int Stamp, Func<HintProps, float, Element> Content);

    sealed class Host : Component
    {
        public override Element Render()
        {
            var p = UseProps<Props>();
            var overlay = UseContext(Overlay.Service);
            Size2 window = UseContextSignal(Viewport.Size).Peek();   // the surface where none is given; the stamp reopens on a tier change
            // Again for another hint on the same slot, when the offer becomes its confirmation, and when the window's tier changes.
            UseEffect(() => Open(overlay, p, window), DepKey.From(p.Hint.Id.GetHashCode(), p.Hint.Done ? 1 : 0, p.Stamp, 0));
            return s_zero;
        }

        Action? Open(IOverlayService overlay, Props p, Size2 window)
        {
            if (p.Hint.Target is not { } target || Context.Scene is not { } scene) return null;
            var hint = p.Hint;
            var place = p.Place;
            var bounds = hint.Bounds;
            var build = p.Content;
            RectF Surface()
            {
                var node = bounds?.Invoke() ?? default;
                return !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : new RectF(0f, 0f, window.Width, window.Height);
            }
            float width = MathF.Max(0f, MathF.Min(place.MaxWidth, Surface().W - 2f * place.Inset));
            // The rect the line hangs from: the control's own edge, as wide as the line, slid along the surface's inset column so the line never
            // leaves it. Read on every placement, so it follows the control and its surface while it is up.
            RectF Anchor()
            {
                var node = target();
                RectF a = !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : default;
                RectF s = Surface();
                float x = place.Align switch
                {
                    HintFloatAlign.Center => a.X + (a.W - width) * 0.5f,
                    HintFloatAlign.End => a.X + a.W - width,
                    _ => a.X,
                };
                float left = s.X + place.Inset, right = MathF.Max(left, s.X + s.W - place.Inset - width);
                return new RectF(Math.Clamp(x, left, right), a.Y, width, a.H);
            }
            OverlayHandle handle = overlay.OpenAt(Anchor, () => build(hint, width),
                place.Below ? FlyoutPlacement.BottomEdgeAlignedLeft : FlyoutPlacement.TopEdgeAlignedLeft,
                new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw) { PinsAnchor = false }, owner: target);
            return () => handle.Close();
        }
    }
}
