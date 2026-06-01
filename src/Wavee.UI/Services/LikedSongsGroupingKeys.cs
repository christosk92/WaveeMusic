using System;

namespace Wavee.UI.Services;

/// <summary>
/// Shared normalization helpers for the "From Liked Songs" grouping path —
/// used by <see cref="LikedSongsByAlbumGrouper"/>, <see cref="LikedSongsByArtistGrouper"/>
/// and the incremental grouping cache. Defined once so the artist-name key and the
/// AddedAt→offset conversion can't drift between call sites.
/// </summary>
public static class LikedSongsGroupingKeys
{
    /// <summary>Normalized artist-name key for name-based grouping / dedup.</summary>
    public static string NormalizeNameKey(string name) => name.Trim().ToLowerInvariant();

    /// <summary>
    /// Converts a stored <c>AddedAt</c> <see cref="DateTime"/> to a
    /// <see cref="DateTimeOffset"/>, treating <see cref="DateTimeKind.Unspecified"/>
    /// as local (LibraryDataService stores <c>LocalDateTime</c>, which is Unspecified).
    /// </summary>
    public static DateTimeOffset ToOffsetRespectingKind(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(dt, TimeSpan.Zero),
        DateTimeKind.Local => new DateTimeOffset(dt),
        _ => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local))
    };
}
