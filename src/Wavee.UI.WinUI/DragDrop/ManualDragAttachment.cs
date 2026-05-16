using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
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
    /// </summary>
    public static void AttachWithPackageWriter(UIElement element, Func<IDragPayload?> payloadFactory)
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
        };
    }
}
