using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Service for fetching album data with caching and resilience.
/// Reusable from artist page, album detail page, search results, etc.
/// </summary>
public interface IAlbumService
{
    Task<List<AlbumTrackDto>> GetTracksAsync(string albumUri, CancellationToken ct = default);
    Task<AlbumDetailResult> GetDetailAsync(string albumUri, CancellationToken ct = default);
    Task<List<AlbumMerchItemResult>> GetMerchAsync(string albumUri, CancellationToken ct = default);
}

public sealed record AlbumDetailResult
{
    public string? Name { get; init; }
    public string? Uri { get; init; }
    public string? Type { get; init; }
    public string? Label { get; init; }
    public string? CoverArtUrl { get; init; }
    public string? ColorDarkHex { get; init; }
    public string? ColorLightHex { get; init; }
    public string? ColorRawHex { get; init; }
    public DateTimeOffset ReleaseDate { get; init; }
    public bool IsSaved { get; init; }
    public bool IsPreRelease { get; init; }
    /// <summary>
    /// When IsPreRelease is true, the moment the album becomes available. Used by
    /// the pre-release banner to format "Coming {weekday} at {time}".
    /// </summary>
    public DateTimeOffset? PreReleaseEndDateTime { get; init; }
    public required List<AlbumCopyrightResult> Copyrights { get; init; }
    public required List<AlbumArtistResult> Artists { get; init; }
    public required List<AlbumTrackDto> Tracks { get; init; }
    public required List<AlbumRelatedResult> MoreByArtist { get; init; }
    /// <summary>
    /// Alternate editions of THIS album (deluxe / remaster / anniversary / instrumental).
    /// Distinct from MoreByArtist (which is the artist's other albums). Empty when the
    /// API doesn't return any.
    /// </summary>
    public required List<AlbumAlternateReleaseResult> AlternateReleases { get; init; }
    public int TotalTracks { get; init; }
    public int DiscCount { get; init; }
    public string? ShareUrl { get; init; }
    /// <summary>
    /// Spotify-extracted palette for the album cover (3 contrast tiers). Null when
    /// the API doesn't return a visualIdentity block. Same shape as ArtistPalette /
    /// ConcertArtistPalette so the page can use the concert's theme-aware logic.
    /// </summary>
    public AlbumPalette? Palette { get; init; }
}

public sealed record AlbumAlternateReleaseResult
{
    public string? Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? CoverArtUrl { get; init; }
    public int Year { get; init; }
    public string? Type { get; init; } // "ALBUM" | "SINGLE" | "COMPILATION" | …
}

public sealed record AlbumPalette
{
    public AlbumPaletteTier? HighContrast { get; init; }    // saturated dark bg
    public AlbumPaletteTier? HigherContrast { get; init; }  // darkest bg
    public AlbumPaletteTier? MinContrast { get; init; }     // light / pastel bg
}

public sealed record AlbumPaletteTier
{
    public byte BackgroundR { get; init; }
    public byte BackgroundG { get; init; }
    public byte BackgroundB { get; init; }
    public byte BackgroundTintedR { get; init; }
    public byte BackgroundTintedG { get; init; }
    public byte BackgroundTintedB { get; init; }
    public byte TextAccentR { get; init; }
    public byte TextAccentG { get; init; }
    public byte TextAccentB { get; init; }
}

public sealed record AlbumCopyrightResult
{
    public string? Text { get; init; }
    public string? Type { get; init; }
}

public sealed record AlbumArtistResult
{
    public string? Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed record AlbumRelatedResult
{
    public string? Id { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? ImageUrl { get; init; }
    public int Year { get; init; }
}

public sealed record AlbumMerchItemResult
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public string? Price { get; init; }
    public string? ShopUrl { get; init; }
}

public sealed record AlbumTrackResult
{
    public required string Id { get; init; }
    public string? Uid { get; init; }
    public string? Title { get; init; }
    public string? Uri { get; init; }
    public TimeSpan Duration { get; init; }
    public long PlayCount { get; init; }
    public string? ArtistNames { get; init; }
    public bool IsExplicit { get; init; }
    public bool IsPlayable { get; init; }
    public bool IsSaved { get; init; }
    public bool HasVideo { get; init; }
    public int TrackNumber { get; init; }
    public int DiscNumber { get; init; }
}
