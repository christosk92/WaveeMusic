namespace Wavee.Local.Subtitles;

/// <summary>
/// Probes a video container (.mkv / .mp4 / .mov) for embedded audio,
/// video, and subtitle tracks. Returns one <see cref="EmbeddedTrackInfo"/>
/// per stream so the player UI can expose track pickers and the scanner
/// can persist embedded-track metadata alongside indexed files.
///
/// <para>Implementation is host-supplied. On Windows (WinUI), the
/// implementation uses <c>Windows.Media.Core.MediaSource</c> to enumerate
/// tracks without decoding. The interface keeps Wavee.Local OS-agnostic;
/// non-Windows / headless surfaces can supply a stub that returns
/// <c>Array.Empty&lt;EmbeddedTrackInfo&gt;()</c>.</para>
///
/// <para>Synchronous because the scanner pipeline runs sync inside a
/// background <c>Task.Run</c>. Implementations may block on async
/// internals.</para>
/// </summary>
public interface IEmbeddedTrackProber
{
    /// <summary>
    /// Returns one entry per embedded stream in the container. Returns
    /// empty if the file is not a multi-track container, the OS has no
    /// probe support, or the operation fails. Never throws.
    /// </summary>
    IReadOnlyList<EmbeddedTrackInfo> Probe(string filePath);
}

/// <summary>One embedded stream in a video container.</summary>
public sealed record EmbeddedTrackInfo(
    EmbeddedTrackKind Kind,
    int StreamIndex,
    string? Language,
    string? Label,
    string? Codec,
    bool IsDefault);

public enum EmbeddedTrackKind
{
    Audio = 0,
    Video = 1,
    Subtitle = 2,
}
