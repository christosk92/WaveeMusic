namespace Wavee.UI.Services.DragDrop;

/// <summary>
/// Outcome of a drop. <see cref="UserMessage"/> is a ready-to-display toast
/// string when the caller wants to surface success / failure; <c>null</c> means
/// no toast.
/// </summary>
public readonly record struct DropResult(bool Success, string? UserMessage, int ItemsAffected)
{
    public static DropResult NoHandler { get; } = new(false, null, 0);
    public static DropResult Ok(int itemsAffected, string? message = null) => new(true, message, itemsAffected);
    public static DropResult Failed(string message) => new(false, message, 0);
}
