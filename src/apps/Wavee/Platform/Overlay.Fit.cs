// ── Platform/Overlay.Fit.cs — the structure's per-tier fit of the overlays the core composes (themes plan 3.6, TC9) ─────────────────────
//
// The ContentDialog's card, the toast lane's dock and the standalone selection bar are the engine's controls, opened or placed by core code. How they
// SIZE and WHERE they sit per window band is the structure's (`ThemePolicies.Overlays`, one `OverlayLayout` per band: Compact, the bands between, Huge).
// This is the one place the core reads it: it never compares a width, it asks the policy for the band the window is in and applies the record.
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
    /// <paramref name="configure"/> sets the title, the body and the commands as for <c>ContentDialog.Show</c>; this then fits the card: the band's form width
    /// (<see cref="DialogFit.FormWidth"/>, else <paramref name="requestedWidth"/> as <see cref="Dialog"/> does) and, where the band lifts the commands
    /// (<see cref="DialogFit.CommandHeight"/>), a command row of buttons that tall in place of the engine's 32-DIP ones. Returns the open dialog's handle.</summary>
    public static OverlayHandle ShowForm(IOverlayService overlay, float requestedWidth, Action<ContentDialog> configure)
    {
        OverlayHandle? handle = null;
        handle = ContentDialog.Show(overlay, d =>
        {
            configure(d);
            var fit = Layout.Dialog;
            if (fit.CommandHeight > 0f && d.Footer is null) d.Footer = CommandRow(d, fit.CommandHeight, () => handle);
            Fit(d, fit, fit.FormCardWidthFor(requestedWidth, Viewport.Width));
        });
        return handle;
    }

    /// <summary>The card's part modifiers and width: the shared half of <see cref="Dialog"/> and <see cref="ShowForm"/>.</summary>
    static void Fit(ContentDialog dialog, DialogFit fit, float width)
    {
        var viewport = Viewport;
        float height = fit.MaxHeightFor(viewport.Height);
        dialog.DialogWidth = width;

        var parts = dialog.Parts ?? new TemplateParts();
        parts[ContentDialog.PartPlate] = plate => plate with
        {
            Width = width, MinWidth = 0f, MaxWidth = width,
            MinHeight = MathF.Min(plate.MinHeight, height), MaxHeight = MathF.Min(plate.MaxHeight, height),
        };
        // A body taller than the card must never push the command row out of it: the text region gives way (and clips), the buttons keep their place.
        parts[ContentDialog.PartContent] = content => content with
        {
            Padding = fit.ContentPadding ?? content.Padding, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
        };
        // The buttons stack in a column where two or three of them cannot sit side by side in the card (the engine's own 130 floor): never a button past the card's edge.
        bool stack = fit.StacksCommands(width, ShownButtons(dialog));
        if (fit.CommandPadding is { } command || stack)
            parts[ContentDialog.PartCommandRow] = row => row with { Padding = fit.CommandPadding ?? row.Padding, Direction = stack ? (byte)1 : row.Direction };
        dialog.Parts = parts;
    }

    /// <summary>The lifted command row: the dialog's own commands (the primary, the secondary and the close, in the engine's order) as buttons <paramref name="height"/> tall
    /// sharing the row equally. Each runs what the engine's button runs: the cancelable click handler first (a veto keeps the dialog open), then the plain click, then the
    /// close. The engine still routes Enter to the default command (the plate's key handler), so the commands' texts stay set on the dialog.</summary>
    static Element CommandRow(ContentDialog d, float height, Func<OverlayHandle?> handle)
    {
        void Run(Action<ContentDialogButtonClickArgs>? veto, Action? click)
        {
            if (veto is not null)
            {
                var args = new ContentDialogButtonClickArgs();
                veto(args);
                if (args.Cancel) return;
            }
            click?.Invoke();
            handle()?.Close();
        }

        BoxEl Command(string text, bool accent, bool enabled, Action onClick) => Button.Create(text, onClick,
            accent ? ButtonAppearance.Accent : ButtonAppearance.Standard, ControlSize.Large, isEnabled: enabled) with
        {
            MinWidth = 0f, MinHeight = height, Height = height, Grow = 1f, Basis = 0f, Justify = FlexJustify.Center, TabIndex = accent ? 1 : 2,
        };

        var kids = new List<Element>(3);
        bool primaryAccent = d.DefaultButton == ContentDialog.DefaultBtn.Primary;
        if (d.PrimaryText != "")
            kids.Add(Command(d.PrimaryText ?? "", primaryAccent, d.IsPrimaryButtonEnabled, () => { if (d.IsPrimaryButtonEnabled) Run(d.PrimaryButtonClick, d.PrimaryClick); }));
        if (d.SecondaryText.Length > 0)
            kids.Add(Command(d.SecondaryText, d.DefaultButton == ContentDialog.DefaultBtn.Secondary, d.IsSecondaryButtonEnabled, () => { if (d.IsSecondaryButtonEnabled) Run(d.SecondaryButtonClick, d.SecondaryClick); }));
        if (d.CloseText.Length > 0)
            kids.Add(Command(d.CloseText, d.DefaultButton == ContentDialog.DefaultBtn.Close, true, () => Run(d.CloseButtonClick, d.CloseClick)));
        return new BoxEl
        {
            Direction = 0, Gap = CommandGap, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch, Children = kids.ToArray(),
        };
    }

    /// <summary>The engine's own gap between command buttons (<c>ContentDialog.BtnGap</c>).</summary>
    const float CommandGap = 8f;

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
}
