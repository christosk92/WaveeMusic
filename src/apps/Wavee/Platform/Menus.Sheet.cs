// ── Platform/Menus.Sheet.cs — a context menu is a sheet where the structure says so (themes plan 3.6, TC9) ────────────────────────
//
// Every context menu in the app is the engine's `ContextMenu.Attach(...)` / `row.WithContextMenu(...)`: a popup anchored to the pointer, the
// element or the invoking button. At the narrowest window band a structure draws the same menu as a bottom sheet instead (the Compact boards:
// a 44-DIP row, a four-column quick-action strip, the whole lower edge), because a 36-DIP menu row is under the 40-DIP touch floor and a popup
// at a thumb's contact point leaves the window.
//
// THIS FILE IS THE DECISION, NOT THE PICTURE. `ContextSheets.Attach` composes onto the element exactly as the engine's attach does (its own
// request handler first, then ours), and at the moment a request arrives it asks the structure's policy (`OverlayLayout.ContextSheet` for the
// band the window is in): a sheet opens `Overlay.ContextSheet` over the whole window (the structure draws it), anything else delegates to the engine's
// stock attach, unchanged. The model is the same one the stock menu would have shown (the factory runs at open time), so no verb, no row and
// no order changes: only what shows them.
//
// `WithContextMenu` here SHADOWS the engine's extension of the same shape for every file in `namespace Wavee.*` (an extension method declared in
// an enclosing namespace wins over one imported by a `using` at the top of the file), so the ~25 sites that spell `row.WithContextMenu(overlay, factory)`
// need no edit; the sites that call `ContextMenu.Attach(...)` directly call `ContextSheets.Attach(...)` instead.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>Context menus that open as a sheet in the bands the structure's policy names.</summary>
public static class ContextSheets
{
    /// <summary>Attach a context menu to <paramref name="element"/>: the engine's, or the structure's sheet where its policy says so.</summary>
    public static BoxEl Attach(BoxEl element, IOverlayService svc, Func<ContextMenuModel?> factory, ContextMenuOptions? options = null)
    {
        var stock = ContextMenu.Attach(element, svc, factory, options);
        Action<ContextRequestEventArgs>? stockHandler = stock.OnContextRequested;
        Action<ContextRequestEventArgs>? own = element.OnContextRequested;
        return element with
        {
            OnContextRequested = args =>
            {
                if (!SheetNow()) { stockHandler?.Invoke(args); return; }
                own?.Invoke(args);                        // the element's own handler runs first, as in the engine's chain
                Open(svc, factory, options);
            },
        };
    }

    /// <summary>Whether a context menu is a sheet right now: the structure's policy for the window band on screen.</summary>
    public static bool SheetNow()
    {
        var facts = ThemeHost.Facts.Peek();
        return facts.Policies.Overlays.For(facts.Tiers.Window).ContextSheet;
    }

    /// <summary>Open the sheet for the model <paramref name="factory"/> builds now (null, or no enabled row: nothing opens). The handle, or null.</summary>
    public static OverlayHandle? Open(IOverlayService svc, Func<ContextMenuModel?> factory, ContextMenuOptions? options = null)
    {
        if (factory() is not { } model || !ContextMenu.HasAnyEnabled(model)) return null;

        OverlayHandle? handle = null;
        var props = new ContextSheetProps
        {
            Header = model.Header,
            Primary = model.Primary,
            Rows = model.Rows,
            Close = () => handle?.Close(),
        };
        // The whole window, light-dismissed (Escape, and a press on the scrim which the view wires to Close). Rect-anchored, so no anchor node can die under it;
        // `Full` is the engine's "fill the container" placement and `Raw` its chrome-less surface: the structure's view draws the scrim and the card.
        handle = svc.OpenAt(
            () => new RectF(0f, 0f, 1f, 1f),
            () => ViewSlots.View(ViewKeys.OverlayContextSheet, props),
            FlyoutPlacement.Full,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Raw)
            {
                OpaqueSurface = options?.OpaqueSurface ?? false,
            });
        // The engine hands its own menus the close cause through an internal hook; the sheet reports a programmatic close (every way out goes through Close).
        if (options?.OnClosed is { } onClosed) handle.ClosedAction = () => onClosed(OverlayCloseCause.Programmatic);
        return handle;
    }
}

/// <summary>The shadow of the engine's <c>WithContextMenu</c> (see <see cref="ContextSheets"/>).</summary>
public static class ContextSheetExtensions
{
    /// <summary>Attach a context menu (or, where the structure's policy says so, a context sheet) to <paramref name="el"/>.</summary>
    public static BoxEl WithContextMenu(this BoxEl el, IOverlayService svc, Func<ContextMenuModel?> factory, ContextMenuOptions? options = null)
        => ContextSheets.Attach(el, svc, factory, options);
}
