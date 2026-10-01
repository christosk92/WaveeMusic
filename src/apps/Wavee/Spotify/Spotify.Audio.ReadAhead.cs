// Spotify.Audio.ReadAhead.cs — how many seconds ahead of the playhead a track's ring reads, as one pure rule.
//
// The measured defect: every lossless open pulled the WHOLE file back-to-back in 2-5 s (the 600 s tier, capped only by
// the ring's 16 MiB), so a FLAC skipped after 1.6 s had already cost 16.8 MB. A lossless file at ~1000 kbit/s only needs
// a window of a minute or so for gapless playback on any link the 3x-throughput tier applies to, and a track that has
// not yet earned its keep (the playhead has not passed the probation) needs less still.

namespace Wavee;

public static partial class Spotify
{
    public static partial class Audio
    {
        public static class ReadAhead
        {
            /// <summary>A lossless track reads only <see cref="LosslessProbationSeconds"/> ahead until its playhead has
            /// passed this long — a skip inside it leaves at most that window downloaded.</summary>
            public const int ProbationMs = 10_000;

            /// <summary>The lossless window before the probation is over.</summary>
            public const int LosslessProbationSeconds = 30;

            /// <summary>The lossless window once it is. Far above a gapless hand-off's need, far below the whole file.</summary>
            public const int LosslessSeconds = 90;

            /// <summary>Seconds of audio the ring holds ahead of the playhead. Lossless (FLAC) is bounded as above on a link
            /// that has earned the big tier (a slow or metered link already has its own small windows); every other format
            /// keeps <see cref="Ring.ReadAheadSeconds"/>'s tiers. PURE.</summary>
            public static int For(Format format, bool metered, long measuredBytesPerSec, int fileBytesPerSec, long playheadMs)
            {
                int tier = Ring.ReadAheadSeconds(metered, measuredBytesPerSec, fileBytesPerSec);
                if (format is not (Format.Flac or Format.Flac24)) return tier;
                return Math.Min(tier, playheadMs < ProbationMs ? LosslessProbationSeconds : LosslessSeconds);
            }
        }
    }
}
