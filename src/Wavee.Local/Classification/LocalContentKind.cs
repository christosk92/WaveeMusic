namespace Wavee.Local.Classification;

/// <summary>
/// Content type assigned to a scanned file by <see cref="LocalContentClassifier"/>.
/// Stored as a TEXT discriminator in <c>local_files.auto_kind</c>; an optional
/// override lives in <c>local_files.kind_override</c>. The effective kind seen by
/// the UI is <c>kind_override ?? auto_kind</c>.
/// </summary>
public enum LocalContentKind
{
    /// <summary>Unclassified / catch-all (broken files, unknown formats, fallback).</summary>
    Other = 0,

    /// <summary>Plain music track (mp3/flac/m4a/opus/...).</summary>
    Music = 1,

    /// <summary>Music video — visual content with one song.</summary>
    MusicVideo = 2,

    /// <summary>One episode of a TV series.</summary>
    TvEpisode = 3,

    /// <summary>A movie.</summary>
    Movie = 4,
}

/// <summary>Helpers for converting <see cref="LocalContentKind"/> to/from the
/// short TEXT codes stored in the database.</summary>
public static class LocalContentKindExtensions
{
    public static string ToWireValue(this LocalContentKind kind) => kind switch
    {
        LocalContentKind.Music      => "Music",
        LocalContentKind.MusicVideo => "MusicVideo",
        LocalContentKind.TvEpisode  => "TvEpisode",
        LocalContentKind.Movie      => "Movie",
        _                           => "Other",
    };

    public static LocalContentKind ParseWireValue(string? s) => s switch
    {
        "Music"      => LocalContentKind.Music,
        "MusicVideo" => LocalContentKind.MusicVideo,
        "TvEpisode"  => LocalContentKind.TvEpisode,
        "Movie"      => LocalContentKind.Movie,
        _            => LocalContentKind.Other,
    };

    public static bool IsVideo(this LocalContentKind kind) => kind is
        LocalContentKind.TvEpisode or LocalContentKind.Movie or LocalContentKind.MusicVideo;

    public static bool IsAudio(this LocalContentKind kind) => kind == LocalContentKind.Music;
}
