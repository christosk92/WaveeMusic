using System;

namespace Wavee.UI.Models;

/// <summary>
/// A library item (track, album, artist, etc.) for display in the UI.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record LibraryItemDto
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? ImageUrl { get; init; }
    public TimeSpan Duration { get; init; }
    public int PlayCount { get; init; }
    public DateTimeOffset? LastPlayedAt { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}