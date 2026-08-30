using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;

namespace Wavee;

// A tiny debug HUD: live FPS + frame time, pinned top-right under the chrome. Mounted by WaveeApp when TWO SETTINGS
// are on — Settings › Diagnostics: Developer mode AND the FPS overlay toggle (`DeveloperMode.Enabled` &&
// `WaveeSettings.FpsOverlay`, read inside WaveeApp.Render so flipping either re-renders immediately). It is NOT gated on
// Diag.CompiledIn, so a Release build ships it too — a user can find and un-find it without a relaunch. Hit-test
// pass-through so it never steals input.
//
// IMPORTANT — this renders EXACTLY ONCE. The numbers are RETAINED dynamic-text slots (Element.DynamicText): the host
// refreshes the interned fps/ms strings in place each frame WITHOUT re-rendering this component (AppHost.UpdateDynamic-
// DiagnosticsText). The earlier version read FrameDiagnostics.Current in Render(), which re-rendered every frame — the
// HUD then forced a full reconcile + record on every frame and kept the loop from ever idling (an observer that
// depressed the very fps it displayed). With the retained slots, when nothing else animates the loop throttles the HUD
// readout to ~10 Hz at ~0% CPU; while something animates it reads the real display-rate fps. Tradeoff: the value colour
// is now static (a per-frame threshold recolour would require the per-frame re-render we are deleting) — honesty/idle
// beat aesthetics for a diagnostic. See [[derived-skeleton-pattern]]-style retained idiom: Gallery.FrameDiagnosticsHud.
sealed class FpsOverlay : Component
{
    // Returns ONLY the small HUD pill — NOT a full-bleed container. Critical: this is mounted via Embed.Comp, and the
    // reconciler MIRRORS the output's LAYOUT flags onto the component-wrapper node (MirrorParticipation). HitTestPassThrough
    // is NOT a layout flag, so it does NOT mirror — a full-bleed (Grow=1) output would leave the wrapper full-bleed AND
    // hittable AND non-passthrough, swallowing every hit-test (it silently killed scrolling + the auto-hide scrollbar hover
    // whenever the HUD was on). Keeping the output content-sized keeps the wrapper a small top-right box; WaveeApp owns the
    // full-bleed PASS-THROUGH positioner (a plain BoxEl, whose passthrough flag IS honoured).
    public override Element Render() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f,
        Padding = new Edges4(8f, 4f, 8f, 4f), Corners = CornerRadius4.All(6f),
        Fill = Tok.FillSolidBase with { A = 0.90f }, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            // Retained slots: initial "--" until the host writes the first reading; refreshed in place, no re-render.
            new TextEl("--") { Size = 12f, Weight = 700, Color = Tok.AccentTextPrimary, DynamicText = DynamicTextKind.FrameFps },
            new TextEl("fps") { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
            new TextEl("·") { Size = 12f, Weight = 600, Color = Tok.TextTertiary },
            new TextEl("--") { Size = 12f, Weight = 600, Color = Tok.TextSecondary, DynamicText = DynamicTextKind.FrameMs },
            new TextEl("ms") { Size = 12f, Weight = 600, Color = Tok.TextTertiary },
        ],
    };
}
