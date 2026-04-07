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
    public required List<AlbumCopyrightResult> Copyrights { get; init; }
    public required List<AlbumArtistResult> Artists { get; init; }
    public required List<AlbumTrackDto> Tracks { get; init; }
    public required List<AlbumRelatedResult> MoreByArtist { get; init; }
    public int TotalTracks { get; init; }
    public int DiscCount { get; init; }
    public string? ShareUrl { get; init; }
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
