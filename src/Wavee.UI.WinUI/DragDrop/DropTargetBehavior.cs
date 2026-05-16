using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Wavee.UI.Services.DragDrop;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// One-call wiring for "I'm a drop target of kind X" on any
/// <see cref="UIElement"/>. Hooks <c>DragOver</c> + <c>Drop</c>, sets
/// <c>AllowDrop=true</c>, and routes every drop through
/// <see cref="IDragDropService.DropAsync"/>. The caller's only responsibility
/// is converting the dragged-over visual into a target id when one exists.
/// </summary>
public static class DropTargetBehavior
{
    public static void AttachDropTarget(
        UIElement element,
        DropTargetKind kind,
        Func<DragEventArgs, string?>? targetIdResolver = null,
        Func<DragEventArgs, (DropPosition pos, int? index)>? positionResolver = null,
        Action<DropResult>? onDropped = null,
        Action<DragEventArgs, IDragPayload>? onDragOver = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        var service = Ioc.Default.GetService<IDragDropService>();
        if (service is null) return;

        element.AllowDrop = true;

        async void OnDragOver(object sender, DragEventArgs e)
        {
            var payload = await DragPackageReader.ReadAsync(e.DataView, service);
            if (payload is null) return;
            var targetId = targetIdResolver?.Invoke(e);
            if (!service.CanDrop(payload, kind, targetId)) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            onDragOver?.Invoke(e, payload);
        }

        async void OnDrop(object sender, DragEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                var payload = await DragPackageReader.ReadAsync(e.DataView, service);
                if (payload is null) return;
                var (pos, index) = positionResolver?.Invoke(e) ?? (DropPosition.Inside, (int?)null);
                var modifiers = DragModifiersCapture.Current();
                var ctx = new DropContext(payload, kind, targetIdResolver?.Invoke(e), pos, index, modifiers);
                var result = await service.DropAsync(ctx, CancellationToken.None).ConfigureAwait(true);
                onDropped?.Invoke(result);
            }
            finally
            {
                deferral.Complete();
            }
        }

        element.DragOver += OnDragOver;
        element.Drop += OnDrop;
    }
}
