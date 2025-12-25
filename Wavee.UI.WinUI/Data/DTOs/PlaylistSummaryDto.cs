namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Summary of a playlist for display in sidebar/lists.
/// </summary>
public sealed record PlaylistSummaryDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ImageUrl { get; init; }
    public int TrackCount { get; init; }
    public bool IsOwner { get; init; }
}
