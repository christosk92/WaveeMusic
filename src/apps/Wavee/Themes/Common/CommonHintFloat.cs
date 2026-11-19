// ── Themes/Common/CommonHintFloat.cs — a hint's anchored line: the overlay mechanics a theme's picture floats in (settings rework phase 6) ─────
//
// A hint that POINTS at a control (the core's `Hints.Pointer`: the play button, the volume, the queue's Crossfade switch, Classic's page head)
// is drawn by Classic and Zune as a line next to that control, never inside the surface's layout: it floats in the overlay layer, anchored to
// the hint's target, so nothing on the page moves when it comes or goes. This is only the mechanics, shared once: the picture (Classic's strip
// on its notice plate, Zune's lowercase line on the bar's band) is the theme's `Content`, entering with the theme's own motion. The overlay is
// a raw one (the tooltip chrome's 167 ms fade), never a focus trap and never light-dismissed: the hint's own words answer it (its action, "not
// now", "×"), it takes no focus, and the host closes it with its target. It opens again when the offer turns into its confirmation.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

/// <summary>The anchored line's overlay (see the file header).</summary>
internal static class CommonHintFloat
{
    static readonly BoxEl s_zero = new() { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };

    /// <summary>Floats <paramref name="content"/> (built from the hint) at the hint's target, on <paramref name="placement"/>'s side.</summary>
    public static Element Create(HintProps hint, FlyoutPlacement placement, Func<HintProps, Element> content)
        => Embed.Comp(new Props(hint, placement, content), static () => new Host());

    sealed record Props(HintProps Hint, FlyoutPlacement Placement, Func<HintProps, Element> Content);

    sealed class Host : Component
    {
        public override Element Render()
        {
            var p = UseProps<Props>();
            var overlay = UseContext(Overlay.Service);
            // Again for another hint on the same slot, and when the offer becomes its confirmation.
            UseEffect(() => Open(overlay, p), DepKey.From(p.Hint.Id.GetHashCode(), p.Hint.Done ? 1 : 0));
            return s_zero;
        }

        static Action? Open(IOverlayService overlay, Props p)
        {
            if (p.Hint.Target is not { } target) return null;
            var hint = p.Hint;
            var build = p.Content;
            OverlayHandle handle = overlay.Open(target, () => build(hint), p.Placement,
                new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw) { PinsAnchor = false });
            return () => handle.Close();
        }
    }
}
