namespace Wavee.AudioHost.Diagnostics;

/// <summary>
/// Cross-component correlation for [seek-trace] log lines emitted from
/// AudioEngine, ProgressiveDownloader, etc. AudioEngine bumps and sets
/// <see cref="CurrentSeq"/> at the start of each seek; downstream
/// components read it so all logs from one seek share the same seq id.
/// Vendored NVorbis can't reference this (it's a separate project) so
/// it logs with its own counter and is correlated by timestamp.
/// </summary>
internal static class SeekTrace
{
    public static int CurrentSeq;
}
