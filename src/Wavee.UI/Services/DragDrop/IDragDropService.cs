using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Services.DragDrop;

/// <summary>
/// Registry-backed dispatcher for drag-drop operations. A single singleton is
/// registered at startup (<c>AppLifecycleHelper</c>); every WinUI drop target
/// resolves through this service so the routing table lives in one place.
/// </summary>
public interface IDragDropService
{
    /// <summary>True when at least one <c>(payload kind, target kind)</c> handler is registered AND its CanDrop predicate accepts the context.</summary>
    bool CanDrop(IDragPayload payload, DropTargetKind targetKind, string? targetId);

    /// <summary>Run the registered handler for <paramref name="ctx"/>. Returns <see cref="DropResult.NoHandler"/> when no handler matches.</summary>
    Task<DropResult> DropAsync(DropContext ctx, CancellationToken ct = default);

    /// <summary>Deserialize a payload by its <see cref="DragFormats"/> format string. Returns false when the format is unknown.</summary>
    bool TryDeserialize(string format, string raw, out IDragPayload? payload);
}
