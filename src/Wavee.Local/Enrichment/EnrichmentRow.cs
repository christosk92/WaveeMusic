using Wavee.Local.Classification;

namespace Wavee.Local.Enrichment;

/// <summary>
/// Projection of one indexed local_files row joined with entities, carrying
/// every column an enricher needs to decide what to look up and where to
/// write the result. Returned by
/// <see cref="LocalLibraryService.GetEnrichmentRowAsync"/>.
/// </summary>
public sealed record EnrichmentRow(
    string TrackUri,
    string FilePath,
    LocalContentKind AutoKind,
    string? Title,
    string? Artist,
    string? Album,
    int? MovieYear,
    string? SeriesId,
    string? SeriesName,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? TmdbId,
    string? MusicBrainzId,
    string EnrichmentState,
    // Duration in milliseconds from the entities table — used by the
    // Spotify-music match heuristic as a tiebreaker (track-title +
    // artist-name fuzzy matching ± duration within MaxDurationSkewMs).
    // Defaulted so callers that pre-date the column keep compiling.
    long DurationMs = 0);
