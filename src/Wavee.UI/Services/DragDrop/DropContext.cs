namespace Wavee.UI.Services.DragDrop;

/// <summary>
/// Everything a drop handler needs to act. Constructed by the WinUI adapter
/// at drop time and passed straight to the registry. Carries no WinUI / ViewModel
/// types — only primitives and the payload — so handlers stay testable.
/// </summary>
/// <param name="Payload">The deserialized drag payload.</param>
/// <param name="TargetKind">Which kind of surface received the drop.</param>
/// <param name="TargetId">
/// Identifier for the specific target instance — a Spotify URI for content rows
/// (e.g. <c>spotify:playlist:xxx</c>, <c>spotify:start-group:xxx</c>) or
/// <c>null</c> for global targets (Queue, NowPlaying).
/// </param>
/// <param name="Position">Where on the target row the drop landed.</param>
/// <param name="TargetIndex">
/// Optional ordered-target index (e.g. row index inside a playlist track list).
/// </param>
/// <param name="Modifiers">Keyboard modifiers held when the drop completed.</param>
public sealed record DropContext(
    IDragPayload Payload,
    DropTargetKind TargetKind,
    string? TargetId,
    DropPosition Position,
    int? TargetIndex,
    DropModifiers Modifiers);
