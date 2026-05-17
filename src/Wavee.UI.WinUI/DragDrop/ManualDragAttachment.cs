using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Wavee.UI.Services.DragDrop;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Manual drag detection that works around <see cref="UIElement.CanDrag"/> not
/// firing on elements wrapping a Button (or any child that captures pointer in
/// <c>PointerPressed</c> for tap detection — ItemContainer, MediaCard's Button,
/// SidebarItem's ElementBorder selection pipeline).
///
/// Tracks pointer position from press; once movement exceeds
/// <see cref="DragThresholdPx"/>, calls <see cref="UIElement.StartDragAsync"/>
/// — which raises <c>DragStarting</c> on the element regardless of CanDrag.
/// The factory builds the payload at drag-start so per-row VM state stays fresh.
/// </summary>
public static class ManualDragAttachment
{
    /// <summary>Squared pixel threshold past press point that promotes a hold into a drag.</summary>
    private const double DragThresholdPxSquared = 6 * 6;

    public static void Attach(UIElement element, Func<IDragPayload?> payloadFactory)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(payloadFactory);

        var state = new DragState(payloadFactory);
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) => OnPointerPressed(element, state, e)), true);
        element.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((s, e) => OnPointerMoved(element, state, e)), true);
        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, e) => state.Reset()), true);
        element.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((s, e) => state.Reset()), true);
        element.PointerCaptureLost += (s, e) => state.Reset();
    }

    private sealed class DragState
    {
        public Point? Origin;
        public uint   PointerId;
        public bool   DragInFlight;
        public readonly Func<IDragPayload?> PayloadFactory;

        public DragState(Func<IDragPayload?> factory) { PayloadFactory = factory; }

        public void Reset()
        {
            Origin = null;
            PointerId = 0;
            DragInFlight = false;
        }
    }

    private static void OnPointerPressed(UIElement element, DragState state, PointerRoutedEventArgs e)
    {
        // Only mouse-left / pen / touch initiate a drag. Right-click, middle-click
        // and the keyboard "context menu" key should still bubble through to the
        // owning control's existing handlers (right-click context menus etc.).
        var pp = e.GetCurrentPoint(element);
        if (pp.Properties.IsRightButtonPressed || pp.Properties.IsMiddleButtonPressed) return;

        state.Origin = pp.Position;
        state.PointerId = pp.PointerId;
        state.DragInFlight = false;
    }

    private static async void OnPointerMoved(UIElement element, DragState state, PointerRoutedEventArgs e)
    {
        if (state.DragInFlight) return;
        if (state.Origin is not { } origin) return;

        var pp = e.GetCurrentPoint(element);
        if (pp.PointerId != state.PointerId) return;

        var dx = pp.Position.X - origin.X;
        var dy = pp.Position.Y - origin.Y;
        if (dx * dx + dy * dy < DragThresholdPxSquared) return;

        var payload = state.PayloadFactory();
        if (payload is null) { state.Reset(); return; }

        state.DragInFlight = true;
        var dragState = Ioc.Default.GetService<DragStateService>();
        dragState?.StartDrag(payload);
        try
        {
            // StartDragAsync raises DragStarting on the element; the lambda below
            // is the equivalent of an XAML DragStarting handler — populate the
            // DataPackage there.
            var op = await element.StartDragAsync(pp);
            // op is the resulting DataPackageOperation — we don't need it; the
            // drop target handles the side effect through the registry.
        }
        finally
        {
            dragState?.EndDrag();
            state.Reset();
        }
    }

    /// <summary>
    /// Convenience: <see cref="Attach"/> + register a <c>DragStarting</c> handler
    /// that writes the payload through <see cref="DragPackageWriter"/>.
    /// When <paramref name="useSmallPreview"/> is true (default), the framework's
    /// default drag visual (which captures the source element at its actual size
    /// — way too big for content cards / track rows, and obscures sidebar drop
    /// regions) is replaced by a downscaled bitmap of the same element capped at
    /// <see cref="SmallPreviewMaxWidth"/> × <see cref="SmallPreviewMaxHeight"/>.
    /// </summary>
    public static void AttachWithPackageWriter(
        UIElement element,
        Func<IDragPayload?> payloadFactory,
        bool useSmallPreview = true)
    {
        IDragPayload? captured = null;

        // The payload is built once by the threshold-detection logic above and
        // captured here so the DragStarting handler writes the SAME instance
        // (not a fresh one — selection might have shifted in the meantime).
        Attach(element, () =>
        {
            captured = payloadFactory();
            return captured;
        });

        element.DragStarting += (sender, args) =>
        {
            if (captured is null)
            {
                args.Cancel = true;
                return;
            }
            DragPackageWriter.Write(args.Data, captured);
            args.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Link;

            if (useSmallPreview && sender is FrameworkElement fe)
                _ = ApplySmallDragPreviewAsync(args, fe);
        };
    }

    /// <summary>
    /// Outer bounding box for the rendered drag preview. Both axes are capped
    /// independently and the aspect-preserving scale is the harsher of the two,
    /// so any source — portrait content card (~200×280), landscape track row
    /// (~800×56), wide media card — folds down to something the user can read
    /// without obscuring drop regions.
    /// </summary>
    private const double SmallPreviewMaxWidth = 260;
    private const double SmallPreviewMaxHeight = 96;

    /// <summary>
    /// Renders <paramref name="source"/> off-screen at a capped size using
    /// <see cref="RenderTargetBitmap"/> and feeds the result back into
    /// <see cref="DragStartingEventArgs.DragUI"/>. Held under a deferral so
    /// WinUI waits for the bitmap before showing the cursor preview.
    /// </summary>
    private static async System.Threading.Tasks.Task ApplySmallDragPreviewAsync(
        DragStartingEventArgs args,
        FrameworkElement source)
    {
        var deferral = args.GetDeferral();
        try
        {
            var actualW = source.ActualWidth;
            var actualH = source.ActualHeight;
            if (actualW <= 0 || actualH <= 0) return;

            var scale = Math.Min(1.0, Math.Min(SmallPreviewMaxWidth / actualW, SmallPreviewMaxHeight / actualH));
            // Source already fits — skip the round-trip and let WinUI use its
            // default capture, which is identical pixel-for-pixel.
            if (scale >= 1.0) return;

            var w = Math.Max(1, (int)Math.Round(actualW * scale));
            var h = Math.Max(1, (int)Math.Round(actualH * scale));

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(source, w, h);
            var pixels = await rtb.GetPixelsAsync();
            var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                pixels,
                BitmapPixelFormat.Bgra8,
                rtb.PixelWidth,
                rtb.PixelHeight,
                BitmapAlphaMode.Premultiplied);

            // Anchor at (0, 0): bitmap's top-left sits at the cursor, so the
            // chip floats below-right of the pointer (Explorer-style) and
            // leaves the area the user is reaching toward unobscured. A
            // centered anchor (PixelWidth/2, PixelHeight/2) was tried first
            // and made the chip swallow the row directly under the cursor.
            args.DragUI.SetContentFromSoftwareBitmap(softwareBitmap, new Point(0, 0));
        }
        catch
        {
            // Best-effort. If RenderAsync fails (element not in tree, GPU
            // device lost, etc.) WinUI just falls back to the default visual.
        }
        finally
        {
            deferral.Complete();
        }
    }
}
