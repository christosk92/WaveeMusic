using System;
using System.IO;
using FlacBox;
using NLayer;
using NVorbis;

namespace Wavee.SpotifyLive.Audio;

// ── "How long is this file?" — the one thing a local playable cannot learn from a catalog ────────────────────────────
// Every other source hands us a duration with its metadata. A file on disk does not, and a duration of 0 is not
// harmless: NowPlayingProjection only folds a duration when it is > 0, so a 0 would leave the PREVIOUS track's length
// on the seek bar (mis-scaling every scrub) until something else republished it.
//
// So we read it from the container header, using the SAME three codecs the audio host decodes with — no new dependency,
// and no tag library (tag reading stays out of scope; this is a header read, not a metadata read). It runs at human
// rate (once per pick / drop, or once on a resolve whose Track carried no duration), never per frame, and it is
// fail-soft in every branch: an unreadable header returns 0 and playback proceeds exactly as it would have.

/// <summary>Container-header duration probe for the three locally-decodable audio formats. Wired into
/// <c>LocalFileMediaProvider</c>/<c>GenericMediaProvider</c> and the synthetic-track builder at composition; the
/// providers themselves stay codec-free (and therefore headlessly testable) because they take it as a delegate.</summary>
public static class LocalAudioDurationProbe
{
    /// <summary>The file's duration in milliseconds, or 0 when it cannot be determined. Never throws.</summary>
    public static long Probe(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        try
        {
            if (path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) return Flac(path);
            if (path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) return Vorbis(path);
            if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) return Mp3(path);
        }
        catch (Exception ex)
        {
            WaveeLog.Instance.Event(WaveeLogLevel.Debug, "playback", "local.duration.failed",
                "couldn't read a local file's duration header",
                fields: [WaveeLogField.Of("path", path), WaveeLogField.Of("error", ex.GetType().Name)]);
        }
        return 0;
    }

    // STREAMINFO is the first metadata block: total samples / sample rate, no decoding at all.
    static long Flac(string path)
    {
        using var stream = OpenRead(path);
        var reader = new FlacReader(stream, false);
        while (reader.Streaminfo is null)
            if (!reader.Read()) return 0;
        var si = reader.Streaminfo;
        if (si.SampleRate <= 0 || si.TotalSampleCount <= 0) return 0;
        return (long)(si.TotalSampleCount * 1000.0 / si.SampleRate);
    }

    // NVorbis reads the identification header and the last page's granule position — two seeks, no decode.
    static long Vorbis(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new VorbisReader(stream, false);
        return (long)reader.TotalTime.TotalMilliseconds;
    }

    // NLayer: a VBR (Xing/VBRI) header answers immediately; a header-less CBR file is measured from its frame size.
    static long Mp3(string path)
    {
        using var stream = OpenRead(path);
        using var file = new MpegFile(stream);
        return (long)file.Duration.TotalMilliseconds;
    }

    // Share-everything, like the playback stream: probing must never stop the user moving or replacing their own file.
    static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        BufferSize = 32 * 1024,
    });
}
