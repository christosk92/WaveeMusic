using System;
using Wavee.UI.Services.DragDrop;

namespace Wavee.UI.WinUI.DragDrop;

/// <summary>
/// Singleton service that tracks global drag state across the app.
/// Drag sources call StartDrag/EndDrag; UI components subscribe to DragStateChanged.
/// </summary>
public sealed class DragStateService
{
    /// <summary>
    /// Whether a drag operation is currently in progress.
    /// </summary>
    public bool IsDragging { get; private set; }

    /// <summary>
    /// The current drag payload, or null if not dragging.
    /// </summary>
    public IDragPayload? CurrentPayload { get; private set; }

    /// <summary>
    /// Fired when drag state changes. Parameter is true when drag starts, false when it ends.
    /// </summary>
    public event Action<bool>? DragStateChanged;

    public void StartDrag(IDragPayload payload)
    {
        CurrentPayload = payload;
        IsDragging = true;
        DragStateChanged?.Invoke(true);
    }

    public void EndDrag()
    {
        if (!IsDragging) return;
        IsDragging = false;
        CurrentPayload = null;
        DragStateChanged?.Invoke(false);
    }
}
