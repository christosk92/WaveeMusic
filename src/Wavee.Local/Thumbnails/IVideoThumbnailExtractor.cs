namespace Wavee.Local;

/// <summary>
/// Synchronous frame-thumbnail extraction for video files. The scanner
/// (<see cref="LocalFolderScanner"/>) calls this when a video file has no
/// embedded cover art so the resulting JPEG bytes can be persisted alongside
/// the rest of the entity row in the same transaction.
/// </summary>
/// <remarks>
/// <para>
/// The Wavee core project targets non-Windows TFM (<c>net10.0</c>), so it
/// can't depend on <c>Windows.Storage</c>. The host app supplies a
/// platform-specific implementation via DI; the scanner accepts an optional
/// dependency and falls back to "no thumbnail" when none is registered.
/// </para>
/// <para>
/// The contract is synchronous because the scanner pipeline is sync inside a
/// background <c>Task.Run</c>. Implementations may block on async work — they
/// already run off the UI thread.
/// </para>
/// </remarks>
public interface IVideoThumbnailExtractor
{
    /// <summary>
    /// Returns JPEG bytes for a representative frame of the file at <paramref name="path"/>,
    /// or <c>null</c> if extraction failed or the platform has no thumbnail to offer.
    /// </summary>
    byte[]? Extract(string path);
}
