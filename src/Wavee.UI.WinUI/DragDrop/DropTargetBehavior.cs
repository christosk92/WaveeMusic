using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// One-call wiring for "I'm a drop target of kind X" on any
/// <see cref="UIElement"/>. Hooks <c>DragOver</c> + <c>Drop</c>, sets
/// <c>AllowDrop=true</c>, and routes every drop through
/// <see cref="IDragDropService.DropAsync"/>. The caller's only responsibility
/// is converting the dragged-over visual into a target id when one exists.
///
/// Both handlers must be <c>async void</c> to satisfy the WinUI event
/// signatures. They wrap the entire payload-read + service dispatch in
/// try/catch so a thrown <see cref="Exception"/> never escapes into the
/// unhandled-exception path (which would tear down the process). On a
/// Drop failure the user sees a generic "Couldn't complete drop" toast;
/// a DragOver failure is silently logged because the operation hasn't
/// committed yet and bothering the user mid-drag is worse than the bug.
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
            try
            {
                var payload = await DragPackageReader.ReadAsync(e.DataView, service);
                if (payload is null) return;
                var targetId = targetIdResolver?.Invoke(e);
                if (!service.CanDrop(payload, kind, targetId)) return;
                e.AcceptedOperation = DataPackageOperation.Copy;
                onDragOver?.Invoke(e, payload);
            }
            catch (Exception ex)
            {
                // Drag-over fires many times per second. Failures here usually
                // mean a malformed clipboard payload (cross-process drag from
                // a foreign app). Don't bother the user; just log and let the
                // operation default to no-accept.
                GetLogger()?.LogDebug(ex, "DropTargetBehavior.OnDragOver swallowed (kind={Kind})", kind);
            }
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

                // Surface a toast for results that carry a message but where
                // the caller didn't wire an onDropped — otherwise successful
                // adds and reorders happen completely silently from the
                // user's perspective.
                if (onDropped is null && !string.IsNullOrEmpty(result.UserMessage))
                {
                    var severity = result.Success
                        ? NotificationSeverity.Success
                        : NotificationSeverity.Error;
                    GetNotifications()?.Show(result.UserMessage!, severity, TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception ex)
            {
                GetLogger()?.LogWarning(ex, "DropTargetBehavior.OnDrop failed (kind={Kind})", kind);
                GetNotifications()?.Show(
                    "Couldn't complete drop",
                    NotificationSeverity.Error,
                    TimeSpan.FromSeconds(3));
            }
            finally
            {
                deferral.Complete();
            }
        }

        element.DragOver += OnDragOver;
        element.Drop += OnDrop;
    }

    private static ILogger? GetLogger()
    {
        try { return Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger("DropTargetBehavior"); }
        catch { return null; }
    }

    private static INotificationService? GetNotifications()
    {
        try { return Ioc.Default.GetService<INotificationService>(); }
        catch { return null; }
    }
}
