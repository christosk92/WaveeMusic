using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Wavee.UI.Services.DragDrop;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// One-call wiring for "I am a drag source that produces payload P" on any
/// <see cref="UIElement"/>. Caller supplies a factory that builds the payload
/// on demand (so per-row VM state is fresh at drag time).
/// </summary>
public static class DragSourceBehavior
{
    public static void AttachSource(
        UIElement element,
        Func<IDragPayload?> payloadFactory,
        Action<DragStartingEventArgs>? onDragStarting = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(payloadFactory);

        var dragState = Ioc.Default.GetService<DragStateService>();
        element.CanDrag = true;

        element.DragStarting += (sender, args) =>
        {
            var payload = payloadFactory();
            if (payload is null)
            {
                args.Cancel = true;
                return;
            }
            DragPackageWriter.Write(args.Data, payload);
            args.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Link;
            dragState?.StartDrag(payload);
            onDragStarting?.Invoke(args);
        };

        element.DropCompleted += (sender, args) => dragState?.EndDrag();
    }
}
