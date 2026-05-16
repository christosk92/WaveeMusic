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

    /// <summary>
    /// Fetches similar-album recommendations seeded from a track URI. Backed by
    /// the <c>similarAlbumsBasedOnThisTrack</c> persisted query. Used to populate
    /// the AlbumPage "For this mood" / "Similar albums" shelf — seed with the
    /// album's most-played track (fall back to track[0] if play counts aren't yet
    /// loaded).
    /// </summary>
    Task<List<AlbumSimilarResult>> GetSimilarAlbumsAsync(
        string trackUri, int limit = 24, CancellationToken ct = default);

    /// <summary>
    /// Fetches a track-level music-video association. Returns the video-track URI
    /// when the source track has at least one entry in <c>videoAssociations</c>;
    /// returns null otherwise. Drives the "Watch the official video" promotion on
    /// the AlbumPage for 1-track singles.
    /// </summary>
    Task<string?> GetMusicVideoUriAsync(string trackUri, CancellationToken ct = default);

    /// <summary>
    /// Fetches an artist's bio excerpt + related artists for the AlbumPage's
    /// mini "About the artist" card and "Fans also like" pill list. Reuses the
    /// <c>queryArtistOverview</c> response — same query that backs ArtistPage.
    /// </summary>
    Task<AlbumArtistContextResult> GetArtistContextAsync(
        string artistUri, CancellationToken ct = default);

    /// <summary>
    /// Combined music-video signal + related-artists fetch for a single track.
    /// Backed by one <c>getTrack</c> persisted query — cheaper than calling
    /// <see cref="GetMusicVideoUriAsync"/> + <see cref="GetArtistContextAsync"/>
    /// separately when both are needed. Used by short releases (≤ 2 tracks)
    /// where the artist-overview bio isn't surfaced and the only thing we need
    /// from the artist is the related-artists list. Returns null when the
    /// request fails outright; partial results (empty related-artists, no
    /// video) return a populated record with empty/null fields.
    /// </summary>
    Task<AlbumSingleTrackContextResult?> GetSingleTrackContextAsync(
        string trackUri, CancellationToken ct = default);

    /// <summary>
    /// Fetches the "Now Playing View" artist data via the <c>queryNpvArtist</c>
    /// Pathfinder query — the same call the Spotify desktop client makes for
    /// its right-side sidebar. Returns the artist's bio excerpt, avatar URL,
    /// verified flag, and monthly listeners count. Drives the AlbumPage
    /// "About the artist" card. Requires both the artist URI and a track URI
    /// (Spotify scopes the query through a track context). Returns null when
    /// the request fails — caller should render the card on best-effort data.
    /// </summary>
    Task<AlbumArtistNpvResult?> GetArtistNpvAsync(
        string artistUri, string leadTrackUri, CancellationToken ct = default);

    /// <summary>
    /// Fetches the curated playlist recommendations for an album via
    /// <c>RECOMMENDED_PLAYLISTS</c> extended-metadata, then resolves each
    /// playlist's hero metadata via batched <c>LIST_METADATA_V2</c>. Returns
    /// partial <see cref="PlaylistDetailDto"/> entries (Id = full URI, Name +
    /// ImageUrl + HeaderImageUrl populated when present, IsPartial = true).
    /// Empty list when the album has no recommendations or the fetch fails.
    /// </summary>
    Task<IReadOnlyList<PlaylistDetailDto>> GetRecommendedPlaylistsAsync(
        string albumUri, CancellationToken ct = default);
}

public sealed record AlbumSimilarResult
{
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? ImageUrl { get; init; }
    public string? ArtistName { get; init; }
    public string? ArtistUri { get; init; }
    /// <summary>"ALBUM" | "SINGLE" | "EP" | "COMPILATION" — drives the tile type chip.</summary>
    public string? Type { get; init; }
    public int Year { get; init; }
}

public sealed record AlbumArtistContextResult
{
    public string? BioExcerpt { get; init; }
    public required List<RelatedArtistResult> SimilarArtists { get; init; }
}

/// <summary>
/// Lead-artist data sourced from the <c>queryNpvArtist</c> Pathfinder query,
/// used to render the AlbumPage "About the artist" card. All fields nullable
/// because Spotify ships partial responses; the card hides individual
/// sub-elements when their backing field is empty.
/// </summary>
public sealed record AlbumArtistNpvResult
{
    /// <summary>First-sentence or ~200-char snippet of the artist biography.</summary>
    public string? BioExcerpt { get; init; }
    /// <summary>Largest-source avatar URL. Falls back to album-page's existing
    /// <c>ArtistImageUrl</c> when null.</summary>
    public string? AvatarImageUrl { get; init; }
    /// <summary>Verified-artist flag — drives the ★ glyph next to the name.</summary>
    public bool IsVerified { get; init; }
    /// <summary>Spotify monthly listeners count. Surfaced as a future enhancement.</summary>
    public long MonthlyListeners { get; init; }
}

/// <summary>
/// Combined music-video signal + related-artists for a single track. Hydrated
/// in one <c>getTrack</c> Pathfinder call for short releases (≤ 2 tracks). The
/// list type reuses <see cref="RelatedArtistResult"/> from <see cref="IArtistService"/>
/// so the AlbumPage's "Fans also like" pills consume the same shape whether
/// the source is <c>getTrack</c> (singles/EPs) or <c>queryArtistOverview</c>
/// (full albums).
/// </summary>
public sealed record AlbumSingleTrackContextResult
{
    /// <summary>Sentinel video URI (equals the source track URI when the track
    /// has at least one <c>videoAssociations</c> entry; null otherwise). Same
    /// shape as <see cref="IAlbumService.GetMusicVideoUriAsync"/>.</summary>
    public string? MusicVideoUri { get; init; }

    public required List<RelatedArtistResult> RelatedArtists { get; init; }
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

    /// <summary>
    /// True when this result is a viewport-prefetched <c>ALBUM_V4</c> extended-
    /// metadata snapshot: hero fields populated (Name / Type / CoverArtUrl /
    /// ReleaseDate / Artists / TotalTracks / DiscCount), but <see cref="Tracks"/>
    /// is empty (AlbumV4 only carries track GIDs, not names/durations) and
    /// secondary sections (<see cref="Copyrights"/>, <see cref="MoreByArtist"/>,
    /// <see cref="AlternateReleases"/>, <see cref="Palette"/>, <see cref="Label"/>,
    /// <see cref="ShareUrl"/>) are empty/null until the authoritative Pathfinder
    /// fetch lands. <c>AlbumViewModel.ApplyDetailAsync</c> branches on this flag.
    /// </summary>
    public bool IsPartial { get; init; }
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
    public List<TrackArtistRef> Artists { get; init; } = [];
    public bool IsExplicit { get; init; }
    public bool IsPlayable { get; init; }
    public bool IsSaved { get; init; }
    public bool HasVideo { get; init; }
    public int TrackNumber { get; init; }
    public int DiscNumber { get; init; }
}

/// <summary>
/// Lightweight artist reference carried by track-level data. Preserves the
/// per-track artist list with URIs so multi-artist tracks (collabs, soundtracks)
/// render every contributor as an independently-clickable hyperlink.
/// </summary>
public sealed record TrackArtistRef
{
    public required string Id { get; init; }
    public required string Uri { get; init; }
    public required string Name { get; init; }
}

/// <summary>
/// Projection of an album-billed artist for the header names line. Carries
/// <see cref="IsFirst"/> so the comma separator preceding each entry can be
/// hidden on the first item without a converter that walks the parent list.
/// </summary>
public sealed record HeaderArtistLink
{
    public required string Name { get; init; }
    public required string Uri { get; init; }
    public required bool IsFirst { get; init; }
}
