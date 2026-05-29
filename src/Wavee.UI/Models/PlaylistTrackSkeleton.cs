using System;
using System.Collections.Generic;

namespace Wavee.UI.Models;

/// <summary>
/// The playlist-item-derived, metadata-free shape of a playlist row: everything
/// known from the playlist contents (URI, position, added-at/by, uid, format
/// attributes) <em>before</em> the per-track TrackV4 extended-metadata lands.
/// Used to paint clickable shimmer rows immediately on a cold open, then enrich
/// each into a full <see cref="PlaylistTrackDto"/> as metadata streams in.
/// </summary>
public sealed record PlaylistTrackSkeleton
{
    public required string Id { get; init; }
    public required string Uri { get; init; }

    /// <summary>1-based position among the playlist's track items.</summary>
    public int Index { get; init; }

    public DateTime? AddedAt { get; init; }
    public string? AddedBy { get; init; }

    /// <summary>Lower-case hex of the playlist item's binary <c>itemId</c>; the
    /// stable per-row uid published in PlayerState for skip-to-uid.</summary>
    public string? Uid { get; init; }

    public IReadOnlyDictionary<string, string>? FormatAttributes { get; init; }
}
