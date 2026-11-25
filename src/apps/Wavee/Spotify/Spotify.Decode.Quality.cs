// ── Spotify/Spotify.Decode.Quality.cs ────────────────────────────────────────────────────────────────────────────
// connect-state's PlaybackQuality (player_state field 32), both ways: what Wavee says it plays, and what another
// device says it plays
//
// Role: CORE
// Owner: E
// Budget: 220 lines
// Spec: handoff connect-audio-quality.md (2026-11-25): the owner's capture (an iPhone playing lossless, Wavee playing
//       FLAC), the reference desktop captures, `Protos/player.proto` PlaybackQuality, `Protos/media.proto` AudioQuality
//       — a named partial of Spotify.Decode.cs
//
// What the official clients put in `playback_quality` (ids redacted, capture-20261125 and the .saz references):
//
//   playing                      bitrate_level  strategy        target    available  hifi_status  6: highest  7: origin
//   iPhone, setting Lossless     hifi (5)       best_matching   hifi      true       on (2)       5 or 6      1 internal
//   iPhone, a 24-bit file        hifi24 (6)     cached_file     hifi      true       on           6           1
//   desktop, setting Very high   veryhigh (4)   best_matching   veryhigh  true       off (1)      5 or 6      1
//   desktop, a local file        —              local_file (5)  veryhigh  —          off          —           3 local
//
// Wavee wrote `high · cached_file · high · available · off` for every stream, so a phone showed "High" while Wavee
// played FLAC. The level names are media.proto's AudioQuality ordinals (the JSON view spells them "hifi", "hifi_24").

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary><c>BitrateLevel</c>: the same ordinals as <c>media.proto</c>'s AudioQuality (5 hifi, 6 hifi24).</summary>
        public enum BitrateLevel : byte { Unknown = 0, Low = 1, Normal = 2, High = 3, VeryHigh = 4, Hifi = 5, Hifi24 = 6 }

        public enum BitrateStrategy : byte { Unknown = 0, BestMatching = 1, BackendAdvised = 2, OfflinedFile = 3, CachedFile = 4, LocalFile = 5 }

        public enum HiFiStatus : byte { None = 0, Off = 1, On = 2 }

        /// <summary><c>player_state.playback_quality</c> as a value. <see cref="Present"/> is false when the field was absent.
        /// <see cref="Highest"/> is field 6 (the row's best rendition), <see cref="FileOrigin"/> field 7 (1 internal, 3 local).</summary>
        public readonly record struct PlaybackQuality(
            BitrateLevel Level, BitrateStrategy Strategy, BitrateLevel Target, bool TargetAvailable, HiFiStatus Hifi,
            BitrateLevel Highest, byte FileOrigin, bool Present = true)
        {
            /// <summary>The device says it plays lossless: hi-fi on, or a hifi level stated.</summary>
            public bool Lossless => Present && (Hifi == HiFiStatus.On || Level >= BitrateLevel.Hifi);

            /// <summary>The level a reader names: the playing one, else (a lossless claim with no level) hifi, else the target.</summary>
            public BitrateLevel Shown => Level != BitrateLevel.Unknown ? Level
                : Hifi == HiFiStatus.On ? BitrateLevel.Hifi : Target;
        }

        /// <summary>The level an opened Spotify file plays at. Ogg rungs by name, FLAC by depth; an MP3/AAC stream by its
        /// stated rate (0 = unknown). PURE.</summary>
        public static BitrateLevel LevelOf(Audio.Format format, int kbps = 0) => format switch
        {
            Audio.Format.OggVorbis96 => BitrateLevel.Normal,
            Audio.Format.OggVorbis160 => BitrateLevel.High,
            Audio.Format.OggVorbis320 => BitrateLevel.VeryHigh,
            Audio.Format.Flac => BitrateLevel.Hifi,
            Audio.Format.Flac24 => BitrateLevel.Hifi24,
            Audio.Format.Mp3 or Audio.Format.Aac when kbps > 0 =>
                kbps <= 32 ? BitrateLevel.Low : kbps <= 96 ? BitrateLevel.Normal : kbps <= 160 ? BitrateLevel.High : BitrateLevel.VeryHigh,
            _ => BitrateLevel.Unknown,
        };

        /// <summary>The level a quality SETTING aims at (the target): Lossless aims at hifi, as the iPhone states it even while a
        /// 24-bit file plays. PURE.</summary>
        public static BitrateLevel LevelOf(Audio.Quality quality) => quality switch
        {
            Audio.Quality.Normal96 => BitrateLevel.Normal,
            Audio.Quality.High160 => BitrateLevel.High,
            Audio.Quality.VeryHigh320 => BitrateLevel.VeryHigh,
            _ => BitrateLevel.Hifi,
        };

        /// <summary>What Wavee says it plays (the table in the file header): the opened file's level (unstated until the open
        /// lands), the rung the open asked for as the target (hifi while a FLAC plays, which only a lossless ask opens), hi-fi on exactly while a FLAC plays (or, before the open, while
        /// lossless is asked for), best_matching, internal. A lossless open states its own level as the highest available; a
        /// lossy one states none (Wavee does not know the row's best rendition there). PURE.</summary>
        /// <param name="playing">The format the pump opened for the row on the deck; <see cref="Audio.Format.Unknown"/> before it opened.</param>
        /// <param name="asked">The rung the open asks for (<c>Playback.QualityFor</c>: a pinned format, else the preferred quality).</param>
        public static PlaybackQuality Outgoing(Audio.Format playing, int kbps, Audio.Quality asked)
        {
            BitrateLevel level = LevelOf(playing, kbps);
            BitrateLevel target = LevelOf(asked);
            if (level >= BitrateLevel.Hifi && target < BitrateLevel.Hifi) target = BitrateLevel.Hifi;   // a FLAC opens only when lossless was asked
            bool lossless = level >= BitrateLevel.Hifi || (level == BitrateLevel.Unknown && target >= BitrateLevel.Hifi);
            return new PlaybackQuality(level, BitrateStrategy.BestMatching, target, true, lossless ? HiFiStatus.On : HiFiStatus.Off,
                level >= BitrateLevel.Hifi ? level : BitrateLevel.Unknown, 1);
        }

        /// <summary>A local file, as the official client states one (capture report 3.4): local_file, target veryhigh, hi-fi off,
        /// file_origin local; no level, no target_bitrate_available.</summary>
        public static readonly PlaybackQuality LocalFileQuality =
            new(BitrateLevel.Unknown, BitrateStrategy.LocalFile, BitrateLevel.VeryHigh, false, HiFiStatus.Off, BitrateLevel.Unknown, 3);

        /// <summary>Write <paramref name="q"/> as <c>playback_quality</c> (field 32), in field order; zeros are proto3 defaults.</summary>
        public static void WriteQuality(ref ProtoWriter w, in PlaybackQuality q)
        {
            int m = w.Open(32);
            w.U(1, (ulong)q.Level);
            w.U(2, (ulong)q.Strategy);
            w.U(3, (ulong)q.Target);
            w.B(4, q.TargetAvailable);
            w.U(5, (ulong)q.Hifi);
            w.U(6, (ulong)q.Highest);
            w.U(7, q.FileOrigin);
            w.Close(m);
        }

        /// <summary><c>playback_quality</c>'s bytes → the value. An ordinal past the known enum is kept (a reader names it
        /// unknown), never thrown on. PURE, allocation-free.</summary>
        public static PlaybackQuality ReadQuality(ReadOnlySpan<byte> proto)
        {
            var r = new ProtoReader(proto);
            BitrateLevel level = 0, target = 0, highest = 0;
            BitrateStrategy strategy = 0;
            HiFiStatus hifi = 0;
            bool available = false;
            byte origin = 0;
            while (r.Next())
            {
                if (r.Wire != 0) { r.Skip(); continue; }
                switch (r.Field)
                {
                    case 1: level = (BitrateLevel)Small(r.Varint()); break;
                    case 2: strategy = (BitrateStrategy)Small(r.Varint()); break;
                    case 3: target = (BitrateLevel)Small(r.Varint()); break;
                    case 4: available = r.Bool(); break;
                    case 5: hifi = (HiFiStatus)Small(r.Varint()); break;
                    case 6: highest = (BitrateLevel)Small(r.Varint()); break;
                    case 7: origin = Small(r.Varint()); break;
                    default: r.Skip(); break;
                }
            }
            return new PlaybackQuality(level, strategy, target, available, hifi, highest, origin);

            static byte Small(ulong v) => v > 255 ? (byte)255 : (byte)v;
        }

        /// <summary>The level's wire name, the JSON view's spelling ("hifi", "hifi_24", "very_high"); diagnostics only. PURE.</summary>
        public static string LevelName(BitrateLevel level) => level switch
        {
            BitrateLevel.Low => "low",
            BitrateLevel.Normal => "normal",
            BitrateLevel.High => "high",
            BitrateLevel.VeryHigh => "very_high",
            BitrateLevel.Hifi => "hifi",
            BitrateLevel.Hifi24 => "hifi_24",
            BitrateLevel.Unknown => "unknown",
            _ => "level_" + ((int)level).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
