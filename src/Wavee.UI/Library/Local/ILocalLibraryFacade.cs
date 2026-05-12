using Wavee.Local;
using Wavee.Local.Classification;
using Wavee.Local.Models;

namespace Wavee.UI.Library.Local;

/// <summary>
/// Single UI-facing surface for the local-files library. Composes the
/// underlying Wavee.Local services (LocalLibraryService, ILocalLikeService,
/// ILocalEnrichmentService, LocalGroupService) into one cohesive API the
/// view-models bind against.
///
/// <para>Why a facade: avoids every ViewModel taking 4 service deps; gives
/// us a single place to derive "effective metadata" by merging ATL tags +
/// filename hints + enrichment + user overrides; centralizes change
/// notifications so all surfaces refresh in lockstep.</para>
/// </summary>
public interface ILocalLibraryFacade
{
    // -------- Reads --------
    Task<IReadOnlyList<LocalShow>>          GetShowsAsync(CancellationToken ct = default);
    Task<LocalShow?>                        GetShowAsync(string showId, CancellationToken ct = default);
    Task<IReadOnlyList<LocalSeason>>        GetShowSeasonsAsync(string showId, CancellationToken ct = default);
    Task<IReadOnlyList<LocalMovie>>         GetMoviesAsync(CancellationToken ct = default);
    Task<LocalMovie?>                       GetMovieAsync(string trackUri, CancellationToken ct = default);
    Task<IReadOnlyList<LocalMusicVideo>>    GetMusicVideosAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LocalOtherItem>>     GetOthersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LocalContinueItem>>  GetContinueWatchingAsync(int limit = 20, CancellationToken ct = default);
    Task<IReadOnlyList<LocalTrackRow>>      GetRecentlyAddedAsync(int limit = 30, CancellationToken ct = default);
    Task<IReadOnlyList<LocalTrackRow>>      GetMusicTracksAsync(int limit = 500, CancellationToken ct = default);
    Task<IReadOnlyList<LocalTrackRow>>      GetRecentlyPlayedAsync(int limit = 30, CancellationToken ct = default);
    Task<IReadOnlyList<LocalTrackRow>>      GetLikedTracksAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LocalCollection>>    GetCollectionsAsync(CancellationToken ct = default);
    Task<LocalCollection?>                  GetCollectionAsync(string id, CancellationToken ct = default);
    /// <summary>Resolves a collection's members to their LocalTrackRow shape in sort_order.</summary>
    Task<IReadOnlyList<LocalTrackRow>>      GetCollectionMembersAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<LocalSubtitle>>      GetSubtitlesForAsync(string filePath, CancellationToken ct = default);
    Task<IReadOnlyList<LocalEmbeddedTrack>> GetAudioTracksForAsync(string filePath, CancellationToken ct = default);
    /// <summary>v20 — top-N principal cast for a matched movie, ordered by TMDB billing.</summary>
    Task<IReadOnlyList<LocalCastMember>>    GetMovieCastAsync(string trackUri, CancellationToken ct = default);
    /// <summary>v21 — top-N principal cast for a matched TV show, ordered by TMDB billing.</summary>
    Task<IReadOnlyList<LocalCastMember>>    GetShowCastAsync(string seriesId, CancellationToken ct = default);
    /// <summary>TV shows in the library where the given TMDB person appears in the cast.</summary>
    Task<IReadOnlyList<LocalShow>>          GetShowsByPersonIdAsync(int personId, CancellationToken ct = default);
    /// <summary>Movies in the library where the given TMDB person appears in the cast.</summary>
    Task<IReadOnlyList<LocalMovie>>         GetMoviesByPersonIdAsync(int personId, CancellationToken ct = default);
    /// <summary>TMDB person biography + profile image for the cast detail page.</summary>
    Task<LocalPersonInfo?>                  GetTmdbPersonAsync(int personId, CancellationToken ct = default);
    Task<LocalLyrics?>                      GetLyricsAsync(string filePath, CancellationToken ct = default);

    // -------- Writes --------
    Task SetKindAsync(string filePath, LocalContentKind? kind, CancellationToken ct = default);
    Task PatchMetadataAsync(string filePath, MetadataPatch patch, CancellationToken ct = default);
    Task SetLikedAsync(string trackUri, bool liked, CancellationToken ct = default);
    Task<string> CreateCollectionAsync(string name, CancellationToken ct = default);
    Task AddToCollectionAsync(string collectionId, string filePath, CancellationToken ct = default);
    Task RemoveFromCollectionAsync(string collectionId, string filePath, CancellationToken ct = default);
    Task<bool> DeleteFromDiskAsync(string filePath, CancellationToken ct = default);
    /// <summary>
    /// Replaces the cover artwork for one entity (track / album / show etc.) by
    /// hashing user-supplied bytes and linking them in the local-artwork cache.
    /// Returns the new <c>wavee-artwork://hash</c> URI.
    /// </summary>
    Task<string> SetArtworkOverrideAsync(string entityUri, byte[] bytes, string? mimeType, CancellationToken ct = default);
    Task RemoveFromLibraryAsync(string filePath, CancellationToken ct = default);
    Task MarkWatchedAsync(string trackUri, bool watched, CancellationToken ct = default);
    Task SetLastPositionAsync(string trackUri, long positionMs, CancellationToken ct = default);
    Task RecordPlayAsync(string trackUri, long positionMs, long durationMs, CancellationToken ct = default);
    Task RefreshMetadataAsync(string trackUri, CancellationToken ct = default);

    // -------- Observables --------
    /// <summary>Fires when any row's metadata, kind, watched-state, or
    /// group-membership changes. Subscribers should re-read.</summary>
    IObservable<LocalLibraryChange> Changes { get; }
    /// <summary>Live enrichment progress (matches the ribbon on Local landing).</summary>
    IObservable<EnrichmentProgress> EnrichmentProgress { get; }
}

/// <summary>Coarse-grained change notification — view-models re-fetch on receipt.</summary>
public sealed record LocalLibraryChange(LocalLibraryChangeKind Kind, string? KeyUri = null);

public enum LocalLibraryChangeKind
{
    UnspecifiedReload = 0,
    KindOverrideChanged,
    MetadataOverrideChanged,
    LikeChanged,
    WatchedStateChanged,
    CollectionChanged,
    EnrichmentResult,
    FileRemoved,
    ScanCompleted,
}
