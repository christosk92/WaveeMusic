// ── Platform/Overlay.Fit.cs — the structure's per-tier fit of the overlays the core composes (themes plan 3.6, TC9) ─────────────────────
//
// The ContentDialog's card, the toast lane's dock and the standalone selection bar are the engine's controls, opened or placed by core code. How they
// SIZE and WHERE they sit per window band is the structure's (`ThemePolicies.Overlays`, one `OverlayLayout` per band: Compact, the bands between, Huge).
// This is the one place the core reads it: it never compares a width, it asks the policy for the band the window is in and applies the record — through
// the engine's own knobs (the dialog's `CommandSize` / `CommandMinWidth` / `UniformCommandWidths`, the toast lane's `Toast.Lane`), never a hand-built copy.
//
// WHICH WINDOW. A dialog opens from an event handler, outside any render, so it cannot read the layout viewport as a context. The overlay lane
// (`Shell.Overlays()`, mounted for the life of the root) binds the live viewport signal here every render; before it binds (and in a process that never mounts
// a frame) the tier facts' own viewport answers.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee.Theming;

namespace Wavee;

/// <summary>Applies the active structure's overlay layout for the band the window is in.</summary>
public static class OverlayFit
{
    // ROOT-lifetime (RootScope.cs): the live viewport belongs to the mounted root; a remounted root binds its own and a process that never mounted one has none.
    static readonly RootState<IReadSignal<Size2>?> s_viewport = new("Overlay.Viewport");

    /// <summary>Hand over the live layout viewport (the overlay lane does, every render: idempotent).</summary>
    public static void Bind(IReadSignal<Size2> viewport) => s_viewport.Value = viewport;

    /// <summary>The window's layout size now: the bound live viewport, else the tier facts' own.</summary>
    public static Size2 Viewport
    {
        get
        {
            if (s_viewport.Value is { } live && live.Peek() is { Width: > 0f } size) return size;
            var tiers = ThemeHost.Facts.Peek().Tiers;
            return new Size2(tiers.ViewportWidth, tiers.ViewportHeight);
        }
    }

    /// <summary>The structure's overlay layout for the band the window is in.</summary>
    public static OverlayLayout Layout
    {
        get
        {
            var facts = ThemeHost.Facts.Peek();
            return facts.Policies.Overlays.For(facts.Tiers.Window);
        }
    }

    /// <summary>The anchored menus' row size in the band the window is in (pass it to <c>MenuFlyout.Create(…, size:)</c> when the menu opens).</summary>
    public static ControlSize MenuSize => Layout.MenuSize;

    /// <summary>Fit <paramref name="dialog"/> to the window: the card's width (the opener's rung <paramref name="requestedWidth"/>, or the window less the
    /// structure's inset where it fills), its tallest height, and the structure's paddings. Call it from the opener's configure callback, AFTER the content
    /// and the buttons are set. The engine clamps <c>DialogWidth</c> to its own 320-548 rungs; the plate part re-states the width after that clamp, so a
    /// window narrower than 320 + 2 x inset still holds the card inside it.</summary>
    public static void Dialog(ContentDialog dialog, float? requestedWidth)
    {
        var fit = Layout.Dialog;
        Fit(dialog, fit, fit.WidthFor(requestedWidth ?? DefaultWidth(dialog), Viewport.Width));
    }

    /// <summary>Open a FORM dialog (the report and the crash prompt: a body with its own fields and, where the band has the room, a second pane for the live preview).
    /// <paramref name="configure"/> sets the title, the body and the commands as for <c>ContentDialog.Show</c>; this then fits the card as <see cref="Dialog"/> does,
    /// at the band's form width (<see cref="DialogFit.FormWidth"/>, else <paramref name="requestedWidth"/>); the report form (<paramref name="report"/>) takes the band's
    /// <see cref="DialogFit.ReportWidth"/> first where it names one. Returns the open dialog's handle.</summary>
    public static OverlayHandle ShowForm(IOverlayService overlay, float requestedWidth, Action<ContentDialog> configure, bool report = false)
        => ContentDialog.Show(overlay, d =>
        {
            configure(d);
            var fit = Layout.Dialog;
            Fit(d, fit, fit.FormCardWidthFor(requestedWidth, Viewport.Width, report));
        });

    /// <summary>The card's part modifiers, width and command buttons: the shared half of <see cref="Dialog"/> and <see cref="ShowForm"/>.</summary>
    static void Fit(ContentDialog dialog, DialogFit fit, float width)
    {
        var viewport = Viewport;
        float height = fit.MaxHeightFor(viewport.Height);
        dialog.DialogWidth = width;
        // The band's command buttons: the engine's own, at the band's size (Compact: 40 tall, sharing the row equally from a lower floor).
        dialog.CommandSize = fit.CommandSize;
        dialog.CommandMinWidth = fit.CommandMinWidth;
        dialog.UniformCommandWidths = fit.UniformCommands;

        var parts = dialog.Parts ?? new TemplateParts();
        if (fit.TitleSize is { } titleSize)
            parts.Set<TextEl>(ContentDialog.PartTitle, t => t with { Size = titleSize, LineHeight = titleSize * 1.2f, MaxLines = 2 });
        parts[ContentDialog.PartPlate] = plate => plate with
        {
            Width = width, MinWidth = 0f, MaxWidth = width,
            MinHeight = MathF.Min(plate.MinHeight, height), MaxHeight = MathF.Min(plate.MaxHeight, height),
            Corners = fit.PlateRadius is { } radius ? CornerRadius4.All(radius) : plate.Corners,
        };
        // A body taller than the card must never push the command row out of it: the text region gives way (and clips), the buttons keep their place.
        parts[ContentDialog.PartContent] = content => content with
        {
            Padding = fit.ContentPadding ?? content.Padding, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
        };
        // The buttons stack in a column where two or three of them cannot sit side by side in the card (at the band's floor): never a button past the card's edge.
        bool stack = fit.StacksCommands(width, ShownButtons(dialog));
        if (fit.CommandPadding is { } command || stack)
            parts[ContentDialog.PartCommandRow] = row => row with { Padding = fit.CommandPadding ?? row.Padding, Direction = stack ? (byte)1 : row.Direction };
        dialog.Parts = parts;
    }

    /// <summary>The engine's own default rung for a dialog its opener gave none: the wide rung for three buttons, else the compact one.</summary>
    static float DefaultWidth(ContentDialog dialog) => ShownButtons(dialog) >= 3 ? Controls.DialogWidthWide : Controls.DialogWidthCompact;

    /// <summary>The command buttons the dialog shows (a null primary is the localized OK, shown; a custom footer replaces them).</summary>
    static int ShownButtons(ContentDialog dialog)
        => dialog.Footer is not null ? 0 : (dialog.PrimaryText != "" ? 1 : 0) + (dialog.SecondaryText.Length > 0 ? 1 : 0) + (dialog.CloseText.Length > 0 ? 1 : 0);

    /// <summary>Dock the toast lane for the band the window is in, above the player bar (<paramref name="policyBarHeight"/> is the policy's own height; a band
    /// may name another). Idempotent: the overlay lane calls it every render.</summary>
    public static void Toasts(float policyBarHeight)
    {
        var dock = Layout.Toast;
        Toast.Placement = dock.Placement;
        Toast.EdgeInset = dock.EdgeInsetFor(policyBarHeight);
    }

    /// <summary>The lane geometry of the band the window is in (the engine's <c>Toast.Lane</c> context, provided above the overlay host). It READS the facts, so the
    /// engine's lane, which calls it in its render, re-renders when a tier crosses.</summary>
    public static readonly Func<ToastLaneStyle> ToastLane = static () =>
    {
        var facts = ThemeHost.Facts.Value;
        return facts.Policies.Overlays.For(facts.Tiers.Window).Toast.Lane ?? ToastLaneStyle.Default;
    };
}
