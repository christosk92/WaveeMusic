// ── Spotify/Spotify.Audio.cs ───────────────────────────────────────────────────────────────────────────────────────
// fileId/format/key/CDN + AES-CTR stream
//
// Role: SHELL
// Owner: F
// Wave: 2
// Budget: 1200 lines
// Spec: plan
//
// WHAT THIS FILE DELIVERS, AND WHERE IT STOPS. A track uri goes in; an OPENED, DECRYPTED, SEEKABLE stream of one
// audio container plus that container's format comes out. The pump, the decoders (Vorbis / MP3 / AAC / FLAC), the
// gapless join and the output device are Wave 3's `Playback.Audio.cs` — `Playback.Audio` calls `Spotify.Audio.Open`
// and never learns what a file id, a CDN mirror or a key is.
//
// THE FIVE STEPS, in the order they run (0.2.9's `LiveTrackResolver` + `FastTrackPlayback`, folded into functions):
//   1. METADATA. TRACK_V4 (kind 10) carries the Ogg/AAC `file[]`, the `alternative[]` ladder and `original_audio`;
//      AUDIO_FILES (kind 5) on the derived `spotify:audio:` entity is the ONLY place FLAC file ids are served. The
//      legacy `/metadata/4/track/` route returns a shell with an empty `file[]` and is not used.
//   2. THE LADDER. `Choose` aims at the rung the user's quality setting names (96 / 160 / 320 / lossless) and, when
//      that rung is missing, falls to the NEAREST available one preferring LOWER bitrates — never exceed a bandwidth
//      choice the user made. A track with no playable file falls through to the first `alternative[]` that has one,
//      and that alternative's gid is what the audio key is asked for.
//   3. THE CDN. `/storage-resolve/files/audio/interactive/{fileIdHex}` answers a mirror list and a TTL. Keyed by the
//      FILE id, so it is format-agnostic and shared by every quality of the same file.
//   4. THE KEY. The AP's 0x0c/0x0d exchange first (`Spotify.RequestAudioKey`, owner D). AP audio-key service is
//      ACCOUNT-WIDE: one refusal means it will not serve any track this session, so it is tried once and latched off,
//      and everything after that goes to the deriver seam below. FLAC IS NOT ELIGIBLE FOR THAT PATH AT ALL — see the
//      lossless note below.
//   5. THE STREAM. AES-128-CTR with a PUBLIC iv over ranged CDN GETs, offset 0 at the container's first byte. For Ogg
//      and MP3 that means skipping the 167-byte Spotify header; for FLAC it means offset 0, because a Spotify FLAC has
//      no such header. The STORES under that — the clear head, the read-ahead ring, the fetch thread, the disk cache —
//      are the named partial `Spotify.Audio.Stream.cs` (owner F, Vorbis plan §5); this file keeps the CTR math, the
//      mirrors, the key and the ladder, and `Open` is the seam between the two halves.
//
// THE TWO THINGS LOSSLESS DOES DIFFERENTLY, and they are both invisible until you ship them (FLAC plan §1.1):
//   · NO 0xa7 HEADER. A Spotify FLAC is a plain `fLaC` stream from byte 0 — "no additional headers or anything, just
//     raw FLAC files without any modifications" (librespot #1583), and go-librespot opens FLAC at offset 0. Skipping
//     167 bytes hands the decoder a stream that starts inside STREAMINFO and fails its magic check every time, so
//     `HeaderBytesFor` decides the skip from the FORMAT and `Open` asks it rather than assuming.
//   · NO AP KEY. The audio-key service does not serve lossless file ids ("the keys to lossless don't work via
//     shannon", same thread; go-librespot disabled FLAC outright without its key plugin). That matters more than it
//     looks: the AP refusal is ACCOUNT-WIDE and latches, so a single lossless open would silently downgrade every
//     later Ogg open to the fallback path for the rest of the session. `ApEligible` answers it from the format, the
//     AP is never asked for a FLAC id, and a FLAC refusal can never set the latch.
//
// THE PLAYPLAY SEAM. The second key path is a DERIVATION this repository does not contain — it lives in the private
// `wavee-playplay-private` checkout, reached through `src/apps/Wavee.PlayPlay` when the junction exists. This file
// carries the HOOK and nothing else: `Audio.KeyDeriver` is a delegate the private assembly sets at boot, and with no
// deriver installed a track whose AP key is refused is `Fault.NoDeriver` — an honest, named "this format is
// unavailable on this build", not a crash and not a silent stall. A public-only checkout compiles, runs, and plays
// whatever the AP will hand it a key for; that is the whole of the contract.
//
// THREADING. Everything here BLOCKS and runs on an api thread (`Api.Run`) or on Wave 3's audio pump thread. No table,
// no signal, no `Entities.Strings` (C1). The CDN and key caches are this file's own, guarded by one `Lock` each, and
// they are bounded (C8): 64 mirror sets, 256 keys, oldest evicted.

using System.Security.Cryptography;
using Google.Protobuf;
using Af = Wavee.Protocol.Audiofiles;
using Md = Wavee.Protocol.Metadata;
using St = Wavee.Protocol.Storage;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>File ids, the format ladder, the CDN, the key and the decrypted stream. SHELL: everything blocks.
    ///
    /// <para>The BYTES live in the named partial <c>Spotify.Audio.Stream.cs</c>: the clear head, the seconds-sized
    /// read-ahead ring, the one <c>Wavee.AudioFetch</c> thread with its cancellable in-flight range, and the
    /// <c>ChunkDiskCache</c> write-through. This file still owns everything ABOVE the bytes — the ladder, the mirrors,
    /// the key and the CTR math.</para></summary>
    public static partial class Audio
    {
        // ── 1. the values ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>What the bytes are. The rung is implied by the name for Ogg; MP3 and FLAC are one each because the
        /// decoder does not care about their bitrate and nothing else asks.</summary>
        public enum Format : byte { Unknown = 0, OggVorbis96, OggVorbis160, OggVorbis320, Mp3, Flac, Flac24 }

        /// <summary>What the user asked for. The persisted setting is an int (`Platform.Keys.PlaybackQuality`), so the
        /// VALUES are the wire and a rename here is a preference the user loses.</summary>
        public enum Quality : byte { Normal96 = 0, High160 = 1, VeryHigh320 = 2, Lossless = 3 }

        /// <summary>Why a track did not open. Every one of these is renderable: the detail notice pipeline turns a
        /// fault into the sentence the user sees, which is why there is no "unknown error" member.</summary>
        public enum Fault : byte
        {
            None = 0,
            /// <summary>The metadata answered, and there is no playable file on the track or any alternative.</summary>
            NoFile,
            /// <summary>The catalogue says not in this market / not for this account.</summary>
            Restricted,
            /// <summary>The AP refused the key and no deriver is installed — a public-only build, honestly named.</summary>
            NoDeriver,
            /// <summary>A deriver IS installed and it could not produce a key for this file.</summary>
            NoKey,
            /// <summary>A socket, a DNS answer or a 5xx. Retryable.</summary>
            Network,
            /// <summary>The session is not online. Nothing was asked.</summary>
            Offline,
        }

        /// <summary>Which file the ladder picked, and everything the next three steps need. A VALUE.</summary>
        public readonly record struct FileChoice(
            byte[] FileId, string FileIdHex, byte[] TrackGid, Format Fmt, long DurationMs, float GainDb,
            string? ExternalUrl, Fault Fault)
        {
            public bool Ok => Fault == Audio.Fault.None && (FileId.Length > 0 || ExternalUrl is { Length: > 0 });
            public static FileChoice Failed(Audio.Fault fault) => new([], "", [], Format.Unknown, 0, 0f, null, fault);
        }

        /// <summary>An opened track. <see cref="Stream"/> is positioned at the container's first byte and seeks in
        /// container coordinates; the caller disposes it (which disposes the <see cref="Body"/> under it).
        ///
        /// <para><see cref="Body"/> is the same bytes without the `Stream` shape: random access by offset, the epoch a
        /// seek bumps, the head/ring/disk stores. Wave 3's decoders read THAT (`RingSource` wraps it); `Stream` stays
        /// so the module and local paths, and anything that only speaks `Stream`, keep working through one class.</para></summary>
        public readonly record struct Opened(
            Stream? Stream, Format Fmt, long Length, long DurationMs, float GainDb, string FileIdHex, Fault Fault,
            Body? Body = null)
        {
            public bool Ok => Stream is not null && Fault == Audio.Fault.None;
            public static Opened Failed(Audio.Fault fault) => new(null, Format.Unknown, 0, 0, 0f, "", fault);
        }

        /// <summary>What a deriver is asked for. A VALUE with no Spotify types on it, so the private assembly binds to
        /// this file and to nothing else in the tree.</summary>
        public readonly record struct KeyRequest(string FileIdHex, ReadOnlyMemory<byte> FileId, ReadOnlyMemory<byte> TrackGid);

        /// <summary>THE PLAYPLAY SEAM. Set once at boot by the private assembly when the junction is present; null on
        /// a public-only build. Returns the 16-byte AES key, or null when it cannot derive one. It is called on an api
        /// thread, may block, and must never throw — a throw is caught here and reported as <see cref="Fault.NoKey"/>,
        /// but a deriver that throws per track is a deriver that is wrong.</summary>
        public static Func<KeyRequest, CancellationToken, byte[]?>? KeyDeriver { get; set; }

        /// <summary>True when this build can answer for a file the AP refuses. The player bar reads it to say
        /// "unavailable on this build" once, rather than failing track by track.</summary>
        public static bool CanDerive => KeyDeriver is not null;

        // ── 2. the ladder (PURE) ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The wire format as ours. Anything the decoder stack cannot open is <see cref="Format.Unknown"/> —
        /// the AAC and xHE-AAC rungs are in the proto and are deliberately NOT carried (see the report).</summary>
        public static Format FormatOf(Md.AudioFile.Types.Format wire) => wire switch
        {
            Md.AudioFile.Types.Format.OggVorbis96 => Format.OggVorbis96,
            Md.AudioFile.Types.Format.OggVorbis160 => Format.OggVorbis160,
            Md.AudioFile.Types.Format.OggVorbis320 => Format.OggVorbis320,
            Md.AudioFile.Types.Format.Mp396 or Md.AudioFile.Types.Format.Mp3160
                or Md.AudioFile.Types.Format.Mp3256 or Md.AudioFile.Types.Format.Mp3320 => Format.Mp3,
            Md.AudioFile.Types.Format.FlacFlac => Format.Flac,
            Md.AudioFile.Types.Format.FlacFlac24Bit => Format.Flac24,
            _ => Format.Unknown,
        };

        /// <summary>How many bytes of the DECRYPTED file sit in front of the container, per format. Ogg and MP3 carry
        /// Spotify's 167-byte header; a Spotify FLAC carries nothing and starts at `fLaC` (FLAC plan §1.1). This is the
        /// whole of the difference, and it is a function of the format rather than a constant because getting it wrong
        /// is not a crash — it is a decoder handed 167 bytes of STREAMINFO as if it were a file magic.</summary>
        public static int HeaderBytesFor(Format format)
            => format is Format.Flac or Format.Flac24 ? 0 : Ctr.HeaderBytes;

        /// <summary>Can the AP's audio-key service answer for this format? NO for lossless: it does not serve FLAC
        /// file ids, and because its refusals are account-wide and latching, asking it once for a FLAC would cost every
        /// later Ogg open its fast key path for the rest of the session.</summary>
        public static bool ApEligible(Format format)
            => format is not (Format.Flac or Format.Flac24);

        /// <summary>Which BANDWIDTH rung a format sits on: 0 ≈ 96 kbps, 1 ≈ 160, 2 ≈ 320, 3 lossless. −1 means the
        /// format is not on the ladder at all and can never be picked.</summary>
        public static int Rung(Format format) => format switch
        {
            Format.OggVorbis96 => 0,
            Format.OggVorbis160 => 1,
            Format.OggVorbis320 => 2,
            Format.Mp3 => 1,
            Format.Flac or Format.Flac24 => 3,
            _ => -1,
        };

        /// <summary>The rung a quality setting aims at. Lossless aims at 3; everything else at its own number.</summary>
        public static int TargetRung(Quality quality) => (int)quality;

        /// <summary>THE LADDER, pure and therefore tested: given what the catalogue offered, which index does the user's
        /// setting pick? Exact rung wins; below-target beats above-target (never exceed the bandwidth the user chose);
        /// within a tier the nearest wins. −1 when nothing on the list is playable at all.
        ///
        /// <para>Lossless is asked for separately (FLAC lives on a different extension), so a <see cref="Quality"/> of
        /// <see cref="Quality.Lossless"/> handed a list of Ogg rungs behaves exactly like VeryHigh320 — which is what
        /// an account without lossless must do rather than refuse to play.</para></summary>
        public static int PickRung(ReadOnlySpan<Format> available, Quality quality)
        {
            int target = Math.Min(TargetRung(quality), 2);
            int best = -1, bestScore = 0;
            for (int i = 0; i < available.Length; i++)
            {
                int rung = Rung(available[i]);
                if (rung < 0) continue;
                if (rung > 2) rung = 2;                       // a FLAC in an Ogg list scores as the top bandwidth rung
                int score = rung == target ? 100 : rung < target ? 50 - (target - rung) : 10 - (rung - target);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            return best;
        }

        /// <summary>Pick one file out of a `file[]` list. The list is walked twice — once to project the formats, once
        /// to take the winner — so <see cref="PickRung"/> stays a span fold with no protobuf on it.</summary>
        static (byte[] FileId, Format Fmt) PickFile(IReadOnlyList<Md.AudioFile> files, Quality quality)
        {
            int n = files.Count;
            if (n == 0) return ([], Format.Unknown);
            Span<Format> formats = n <= 32 ? stackalloc Format[n] : new Format[n];
            for (int i = 0; i < n; i++)
                formats[i] = files[i].FileId.Length == 0 ? Format.Unknown : FormatOf(files[i].Format);
            int pick = PickRung(formats, quality);
            return pick < 0 ? ([], Format.Unknown) : (files[pick].FileId.ToByteArray(), formats[pick]);
        }

        /// <summary>FLAC out of an AUDIO_FILES payload — 24-bit preferred over 16-bit. The audio KEY still uses the
        /// track's own gid: the FLAC is an alternative ENCODING of the same track, not an alternative track.</summary>
        static (byte[] FileId, Format Fmt) PickFlac(Af.AudioFilesExtensionResponse response)
        {
            byte[] best = [];
            Format bestFormat = Format.Unknown;
            int bestRank = 0;
            foreach (Af.ExtendedAudioFile extended in response.Files)
            {
                Md.AudioFile? file = extended.File;
                if (file is null || file.FileId.Length == 0) continue;
                int rank = file.Format switch
                {
                    Md.AudioFile.Types.Format.FlacFlac24Bit => 2,
                    Md.AudioFile.Types.Format.FlacFlac => 1,
                    _ => 0,
                };
                if (rank <= bestRank) continue;
                bestRank = rank;
                best = file.FileId.ToByteArray();
                bestFormat = rank == 2 ? Format.Flac24 : Format.Flac;
            }
            return (best, bestFormat);
        }

        /// <summary>Spotify loudness normalization: gain = target (−14 LUFS) − the track's loudness, capped so the true
        /// peak stays at or below −1 dBFS. PURE.</summary>
        public static float NormalizationGain(float loudnessDb, float truePeakDb)
        {
            const float Target = -14f;
            float gain = Target - loudnessDb;
            float headroom = -1f - truePeakDb;
            return gain > headroom ? headroom : gain;
        }

        /// <summary>The whole ladder over the two payloads, pure: TRACK_V4 plus an optional AUDIO_FILES. FLAC wins when
        /// the account returned it AND the setting asked for it; otherwise the Ogg rungs; otherwise the first
        /// alternative that has any file, carrying ITS gid.</summary>
        public static FileChoice Choose(Md.Track track, Af.AudioFilesExtensionResponse? lossless, Quality quality,
            long fallbackDurationMs)
        {
            if (quality == Quality.Lossless && lossless is not null)
            {
                (byte[] flacId, Format flacFormat) = PickFlac(lossless);
                if (flacId.Length > 0)
                {
                    float gain = lossless.DefaultFileNormalizationParams is { } np
                        ? NormalizationGain(np.LoudnessDb, np.TruePeakDb) : 0f;
                    return new FileChoice(flacId, Hexed(flacId), track.Gid.ToByteArray(), flacFormat,
                        track.HasDuration ? track.Duration : fallbackDurationMs, gain, null, Fault.None);
                }
            }

            (byte[] fileId, Format format) = PickFile(track.File, quality);
            if (fileId.Length > 0)
                return new FileChoice(fileId, Hexed(fileId), track.Gid.ToByteArray(), format,
                    track.HasDuration ? track.Duration : fallbackDurationMs, 0f, null, Fault.None);

            foreach (Md.Track alternative in track.Alternative)
            {
                (byte[] altId, Format altFormat) = PickFile(alternative.File, quality);
                if (altId.Length == 0) continue;
                long duration = alternative.HasDuration ? alternative.Duration
                    : track.HasDuration ? track.Duration : fallbackDurationMs;
                // The alternative's OWN gid: the audio key is bound to the (file, track) pair and the alternative is a
                // different track row on the wire even though the user thinks it is the same song.
                return new FileChoice(altId, Hexed(altId), alternative.Gid.ToByteArray(), altFormat,
                    duration, 0f, null, Fault.None);
            }

            return FileChoice.Failed(Fault.NoFile);
        }

        /// <summary>The episode ladder. An episode may carry an EXTERNAL url instead of a file id — a plain,
        /// unencrypted MP3 on somebody else's host, which skips the key and the CDN entirely.</summary>
        public static FileChoice Choose(Md.Episode episode, Quality quality, long fallbackDurationMs)
        {
            long duration = episode.HasDuration ? episode.Duration : fallbackDurationMs;
            byte[] gid = episode.Gid.ToByteArray();
            if (episode.HasExternalUrl && episode.ExternalUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return new FileChoice(gid, Hexed(gid), gid, Format.Mp3, duration, 0f, episode.ExternalUrl, Fault.None);

            (byte[] fileId, Format format) = PickFile(episode.Audio, quality);
            return fileId.Length == 0
                ? FileChoice.Failed(Fault.NoFile)
                : new FileChoice(fileId, Hexed(fileId), gid, format, duration, 0f, null, Fault.None);
        }

        static string Hexed(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return "";
            Span<char> hex = stackalloc char[bytes.Length * 2];
            return new string(hex[..Hex.Encode(bytes, hex)]);
        }

        // ── 3. the metadata read ─────────────────────────────────────────────────────────────────────────────────────
        //
        // `Spotify.Decode` stages catalogue COLUMNS; the file list is not one of them and never will be (a file id is
        // not something a page renders). So this reads the extension payload straight off the envelope with the
        // generated parser, which is the same wire and no second copy of anybody's decoder.

        /// <summary>Pull one extension payload for one uri out of a `BatchedExtensionResponse`. Empty when the service
        /// answered for a different kind, or answered an error status for this one.</summary>
        public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> response, Xm.ExtensionKind kind)
        {
            if (response.Length == 0) return default;
            Xm.BatchedExtensionResponse parsed;
            try { parsed = Xm.BatchedExtensionResponse.Parser.ParseFrom(response); }
            catch (InvalidProtocolBufferException) { return default; }
            foreach (Xm.EntityExtensionDataArray array in parsed.ExtendedMetadata)
            {
                if (array.ExtensionKind != kind) continue;
                foreach (var data in array.ExtensionData)
                {
                    int status = data.Header is { } header && header.HasStatusCode ? header.StatusCode : 200;
                    if (status is < 200 or >= 300) continue;
                    if (data.ExtensionData is null) continue;
                    return data.ExtensionData.Value.Span;
                }
            }
            return default;
        }

        static Md.Track? TrackMetadata(string trackUri, CancellationToken ct)
        {
            Xm.ExtensionKind[] kinds = [Xm.ExtensionKind.TrackV4];
            Api.Result result = Api.MetadataPost(Api.MetadataBody(trackUri, kinds, Api.Market, Api.Catalogue), ct);
            if (!result.Ok) return null;
            ReadOnlySpan<byte> payload = Payload(result.Bytes, Xm.ExtensionKind.TrackV4);
            if (payload.IsEmpty) return null;
            try { return Md.Track.Parser.ParseFrom(payload); }
            catch (InvalidProtocolBufferException) { return null; }
        }

        static Md.Episode? EpisodeMetadata(string episodeUri, CancellationToken ct)
        {
            Xm.ExtensionKind[] kinds = [Xm.ExtensionKind.EpisodeV4];
            Api.Result result = Api.MetadataPost(Api.MetadataBody(episodeUri, kinds, Api.Market, Api.Catalogue), ct);
            if (!result.Ok) return null;
            ReadOnlySpan<byte> payload = Payload(result.Bytes, Xm.ExtensionKind.EpisodeV4);
            if (payload.IsEmpty) return null;
            try { return Md.Episode.Parser.ParseFrom(payload); }
            catch (InvalidProtocolBufferException) { return null; }
        }

        /// <summary>The FLAC half, on the `spotify:audio:` entity derived from `original_audio.uuid`. Null whenever the
        /// account does not get lossless, which is the ordinary case and not a failure.</summary>
        static Af.AudioFilesExtensionResponse? LosslessMetadata(Md.Track track, CancellationToken ct)
        {
            if (track.OriginalAudio is not { Uuid.Length: 16 } original) return null;
            Span<char> id = stackalloc char[32];
            int written = Base62.Encode(ToUInt128(original.Uuid.Span), id);
            if (written == 0) return null;
            string audioUri = "spotify:audio:" + new string(id[..written]);
            Xm.ExtensionKind[] kinds = [Xm.ExtensionKind.AudioFiles];
            Api.Result result = Api.MetadataPost(Api.MetadataBody(audioUri, kinds, Api.Market, Api.Catalogue), ct);
            if (!result.Ok) return null;
            ReadOnlySpan<byte> payload = Payload(result.Bytes, Xm.ExtensionKind.AudioFiles);
            if (payload.IsEmpty) return null;
            try { return Af.AudioFilesExtensionResponse.Parser.ParseFrom(payload); }
            catch (InvalidProtocolBufferException) { return null; }
        }

        static UInt128 ToUInt128(ReadOnlySpan<byte> gid16)
        {
            UInt128 value = UInt128.Zero;
            for (int i = 0; i < gid16.Length && i < 16; i++) value = (value << 8) | gid16[i];
            return value;
        }

        // ── 4. the CDN (bounded cache, C8) ───────────────────────────────────────────────────────────────────────────

        /// <summary>Where one file's bytes live, and for how long the answer is good for.</summary>
        public readonly record struct Mirrors(string[] Urls, long ExpiresAtMs, Fault Fault)
        {
            public bool Ok => Fault == Audio.Fault.None && Urls.Length > 0;
        }

        const int MirrorCacheMax = 64;
        static readonly Lock MirrorGate = new();
        static readonly Dictionary<string, Mirrors> MirrorCache = new(MirrorCacheMax, StringComparer.OrdinalIgnoreCase);
        static readonly Queue<string> MirrorOrder = new(MirrorCacheMax);

        /// <summary>Resolve (and cache) the mirror list. The TTL the service sends is honoured at 80 % — the last fifth
        /// is the window a long track needs to finish streaming on a url it already opened.</summary>
        public static Mirrors Resolve(string fileIdHex, CancellationToken ct)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (MirrorGate)
            {
                if (MirrorCache.TryGetValue(fileIdHex, out Mirrors cached) && cached.ExpiresAtMs > now) return cached;
            }

            Api.Result result = Api.StorageResolve(fileIdHex, ct);
            if (!result.Ok) return new Mirrors([], 0, Fault.Network);

            St.StorageResolveResponse parsed;
            try { parsed = St.StorageResolveResponse.Parser.ParseFrom(result.Bytes); }
            catch (InvalidProtocolBufferException) { return new Mirrors([], 0, Fault.Network); }

            if (parsed.Result == St.StorageResolveResponse.Types.Result.Restricted)
                return new Mirrors([], 0, Fault.Restricted);
            if (parsed.Cdnurl.Count == 0) return new Mirrors([], 0, Fault.Network);

            var urls = new string[parsed.Cdnurl.Count];
            for (int i = 0; i < urls.Length; i++) urls[i] = parsed.Cdnurl[i];
            long ttlSeconds = parsed.HasTtlSeconds && parsed.TtlSeconds > 0 ? parsed.TtlSeconds : 1800;
            var mirrors = new Mirrors(urls, now + (long)(ttlSeconds * 800), Fault.None);

            lock (MirrorGate)
            {
                if (!MirrorCache.ContainsKey(fileIdHex)) MirrorOrder.Enqueue(fileIdHex);
                MirrorCache[fileIdHex] = mirrors;
                while (MirrorOrder.Count > MirrorCacheMax && MirrorOrder.TryDequeue(out string? oldest))
                    MirrorCache.Remove(oldest);
            }
            return mirrors;
        }

        /// <summary>Forget one file's mirrors — called when a body fetch fails, because a dead mirror stays dead for
        /// the rest of its TTL and every retry against it is another wasted round trip.</summary>
        public static void InvalidateMirrors(string fileIdHex)
        {
            lock (MirrorGate) MirrorCache.Remove(fileIdHex);
        }

        // ── 5. the key: the AP first, then the seam ──────────────────────────────────────────────────────────────────

        const int KeyCacheMax = 256;
        static readonly Lock KeyGate = new();
        static readonly Dictionary<string, byte[]> KeyCache = new(KeyCacheMax, StringComparer.OrdinalIgnoreCase);
        static readonly Queue<string> KeyOrder = new(KeyCacheMax);
        static volatile bool s_apKeysDisabled;

        /// <summary>True once the AP has refused a key. The AP audio-key service is account-wide: one refusal means it
        /// will not serve ANY track for this account this session, so re-probing it per track is 250 ms of latency per
        /// play in exchange for nothing.</summary>
        public static bool ApKeysDisabled => s_apKeysDisabled;

        /// <summary>Reset the AP latch. `Spotify.Session` calls it on a fresh login — a new session is a new answer.</summary>
        public static void ResetKeyLatch()
        {
            s_apKeysDisabled = false;
            lock (KeyGate) { KeyCache.Clear(); KeyOrder.Clear(); }
        }

        /// <summary>The 16-byte AES key for one (file, track) pair. Cached for the session — a key is immutable.
        ///
        /// <para><paramref name="apEligible"/> is <see cref="ApEligible"/> over the format, and it is an ARGUMENT
        /// rather than something this method works out because the caller is the only one holding the format. False
        /// means the AP is not asked at all and — the part that matters — its account-wide latch is never touched, so
        /// one lossless track cannot cost every later Ogg track its fast key path.</para></summary>
        public static Fault Key(string fileIdHex, ReadOnlySpan<byte> fileId, ReadOnlySpan<byte> trackGid,
            Span<byte> key16, bool apEligible, CancellationToken ct)
        {
            lock (KeyGate)
            {
                if (KeyCache.TryGetValue(fileIdHex, out byte[]? cached)) { cached.CopyTo(key16); return Fault.None; }
            }

            if (apEligible && !s_apKeysDisabled)
            {
                AudioKeyResult outcome = RequestAudioKey(fileId, trackGid, key16);
                if (outcome == AudioKeyResult.Ok) { Remember(fileIdHex, key16); return Fault.None; }
                if (outcome == AudioKeyResult.Offline) return Fault.Offline;
                if (outcome == AudioKeyResult.Rejected)
                {
                    s_apKeysDisabled = true;
                    Log.Warn("spotify", "AP audio-key refused — disabled for this session, falling back to the deriver");
                }
                // Timeout and Busy are NOT a latch: they are this request being unlucky, not the account being refused.
                else if (outcome is AudioKeyResult.Timeout or AudioKeyResult.Busy)
                    Log.Warn("spotify", "AP audio-key " + outcome + " for " + fileIdHex);
            }

            Func<KeyRequest, CancellationToken, byte[]?>? deriver = KeyDeriver;
            if (deriver is null) return Fault.NoDeriver;

            byte[]? derived;
            try { derived = deriver(new KeyRequest(fileIdHex, fileId.ToArray(), trackGid.ToArray()), ct); }
            catch (OperationCanceledException) { return Fault.Network; }
            catch (Exception ex) { Log.Error("spotify", "audio key deriver faulted", ex); return Fault.NoKey; }

            if (derived is not { Length: AudioKey.KeyLength }) return Fault.NoKey;
            derived.CopyTo(key16);
            Remember(fileIdHex, key16);
            return Fault.None;
        }

        static void Remember(string fileIdHex, ReadOnlySpan<byte> key16)
        {
            byte[] copy = key16.ToArray();
            lock (KeyGate)
            {
                if (!KeyCache.ContainsKey(fileIdHex)) KeyOrder.Enqueue(fileIdHex);
                KeyCache[fileIdHex] = copy;
                while (KeyOrder.Count > KeyCacheMax && KeyOrder.TryDequeue(out string? oldest)) KeyCache.Remove(oldest);
            }
        }

        // ── 6. AES-128-CTR (PURE, and the reason this file has a vector test) ────────────────────────────────────────

        /// <summary>Spotify's AES-128-CTR primitives. The iv is PUBLIC and fixed; the keystream for block <c>n</c> is
        /// <c>AES-ECB(key, iv + n)</c> with the counter added big-endian across the whole 16 bytes.</summary>
        public static class Ctr
        {
            public static ReadOnlySpan<byte> PublicIv =>
            [
                0x72, 0xe0, 0x67, 0xfb, 0xdd, 0xcb, 0xcf, 0x77,
                0xeb, 0xe8, 0xbc, 0x64, 0x3f, 0x63, 0x0d, 0x93,
            ];

            /// <summary>The bytes in front of the container in a Spotify-hosted file. `OggS` is at exactly this offset
            /// once the file is decrypted, which is also the runtime proof that the key is the right one.</summary>
            public const int HeaderBytes = 0xa7;
            public const int BlockBytes = 16;

            public static ReadOnlySpan<byte> OggMagic => "OggS"u8;

            /// <summary>…and the other container's, at offset 0, because a Spotify FLAC has no header in front of it.</summary>
            public static ReadOnlySpan<byte> FlacMagic => "fLaC"u8;

            /// <summary>Decrypt in place. <paramref name="streamOffset"/> is the byte offset of
            /// <paramref name="buffer"/>[0] in the whole file — a range request decrypts correctly only because this
            /// is passed, which is what makes the stream seekable at all.</summary>
            public static void DecryptInPlace(Span<byte> buffer, ReadOnlySpan<byte> key16, long streamOffset)
            {
                if (key16.Length != BlockBytes) throw new ArgumentException("AES-128 key must be 16 bytes", nameof(key16));
                using Aes aes = Aes.Create();
                aes.KeySize = 128;
                aes.Key = key16.ToArray();
                aes.Padding = PaddingMode.None;

                Span<byte> keystream = stackalloc byte[BlockBytes];
                Span<byte> counter = stackalloc byte[BlockBytes];
                long position = streamOffset;
                int offset = 0;
                while (offset < buffer.Length)
                {
                    long block = position / BlockBytes;
                    int inBlock = (int)(position % BlockBytes);
                    PublicIv.CopyTo(counter);
                    AddBigEndian(counter, block);
                    aes.EncryptEcb(counter, keystream, PaddingMode.None);
                    int n = Math.Min(buffer.Length - offset, BlockBytes - inBlock);
                    Xor(buffer.Slice(offset, n), keystream.Slice(inBlock, n));
                    offset += n;
                    position += n;
                }
            }

            /// <summary>Decrypt a copy. Convenience for a test and for the key check below; the stream decrypts in
            /// place and never allocates a second buffer.</summary>
            public static byte[] Decrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> key16, long streamOffset)
            {
                byte[] plain = cipher.ToArray();
                DecryptInPlace(plain, key16, streamOffset);
                return plain;
            }

            /// <summary>Is this key the right one? Decrypt the first 0xc0 bytes and look for a container magic where
            /// that container puts it: `fLaC` at 0 for lossless, `OggS` at 0xa7 for everything with the Spotify header
            /// in front of it. A wrong key otherwise produces noise the decoder rejects a second later with a much
            /// worse message, and for FLAC the clear head file is the proof vector — it is unencrypted, so a correctly
            /// decrypted first chunk must equal it byte for byte.
            ///
            /// <para>FALSE IS NOT "WRONG KEY" FOR MP3. A raw MP3 has no unambiguous magic — its frame sync is eleven
            /// set bits, which random noise hits about once every two kilobytes — so a check for it would answer true
            /// for a wrong key often enough to be worse than no check. This gate covers the two containers that can be
            /// recognised honestly; a caller holding an MP3 must not read a false as a verdict.</para></summary>
            public static bool Validates(ReadOnlySpan<byte> encryptedPrefix, ReadOnlySpan<byte> key16)
            {
                if (encryptedPrefix.Length < FlacMagic.Length) return false;
                byte[] plain = Decrypt(encryptedPrefix[..Math.Min(encryptedPrefix.Length, 0xc0)], key16, 0);
                if (plain.AsSpan(0, FlacMagic.Length).SequenceEqual(FlacMagic)) return true;
                return plain.Length >= HeaderBytes + OggMagic.Length
                    && plain.AsSpan(HeaderBytes, OggMagic.Length).SequenceEqual(OggMagic);
            }

            static void AddBigEndian(Span<byte> counter, long value)
            {
                ulong carry = (ulong)value;
                for (int i = BlockBytes - 1; i >= 0 && carry > 0; i--)
                {
                    ulong sum = counter[i] + carry;
                    counter[i] = (byte)sum;
                    carry = sum >> 8;
                }
            }

            static void Xor(Span<byte> destination, ReadOnlySpan<byte> keystream)
            {
                for (int i = 0; i < destination.Length; i++) destination[i] ^= keystream[i];
            }
        }

        // ── 7. the stream: see Spotify.Audio.Stream.cs ──────────────────────────────────────────
        //
        // `CtrStream` lived here: ONE 128 KiB chunk, one synchronous HTTP range request per refill on the DECODE
        // thread, no cache, no read-ahead and no way to cancel. It is deleted rather than patched — its shape cannot
        // express a ring, a disk cache or a seek probe (Vorbis plan §1.2). What replaced it is `Body` / `Ring` /
        // `Fetcher` / `BodyStream` in the named partial `Spotify.Audio.Stream.cs`; the CTR math above is unchanged and
        // is still the only thing that makes a ranged read seekable.

        // ── 8. opening a track ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The one CDN client for the process. HTTP/2 is preferred per request and a single connection carries
        /// every range a file has in flight (`EnableMultipleHttp2Connections = false`), which is what makes a cancelled
        /// range an `RST_STREAM` rather than a dropped TCP connection. No `Timeout`: a range's deadline is its own
        /// `CancellationToken` on the fetch thread, and a client-wide timeout would also kill a legitimately long
        /// 512 KiB body on a slow link.</summary>
        static readonly HttpClient Cdn = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 4,
            EnableMultipleHttp2Connections = false,
        })
        { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>The quality the user chose, capped where the plan says it must be. Read PER OPEN, so a settings
        /// change applies from the very next track rather than from the next launch.</summary>
        public static Quality PreferredQuality()
        {
            int stored = Platform.Settings.Get(Platform.Keys.PlaybackQuality);
            return (Quality)Math.Clamp(stored, 0, 3);
        }

        /// <summary>THE entry point Wave 3 calls. Blocks: metadata, ladder, CDN, key, then an opened stream.</summary>
        public static Opened Open(string uri, CancellationToken ct) => Open(uri, PreferredQuality(), 0, ct);

        /// <inheritdoc cref="Open(string, CancellationToken)"/>
        public static Opened Open(string uri, Quality quality, long fallbackDurationMs, CancellationToken ct)
        {
            if (!Current.IsOnline) return Opened.Failed(Fault.Offline);

            FileChoice choice = uri.StartsWith("spotify:episode:", StringComparison.Ordinal)
                ? ChooseEpisode(uri, quality, fallbackDurationMs, ct)
                : ChooseTrack(uri, quality, fallbackDurationMs, ct);
            if (!choice.Ok) return Opened.Failed(choice.Fault == Fault.None ? Fault.NoFile : choice.Fault);

            return Open(choice, ct);
        }

        /// <summary>The half after the ladder: the head, the CDN, the key and the byte stores. Split out so a caller
        /// that already resolved the file (a prefetch that warmed the metadata) does not resolve it twice.
        ///
        /// <para>The body of it is `Spotify.Audio.Stream.cs`'s <see cref="OpenBody"/>: head ‖ storage-resolve ‖ key in
        /// parallel, then the first body range ‖ the tail. Four HTTP requests cold, one round trip to the first
        /// decodable byte.</para></summary>
        public static Opened Open(in FileChoice choice, CancellationToken ct) => OpenBody(in choice, ct);

        static FileChoice ChooseTrack(string uri, Quality quality, long fallbackDurationMs, CancellationToken ct)
        {
            Md.Track? track = TrackMetadata(uri, ct);
            if (track is null) return FileChoice.Failed(Fault.Restricted);
            Af.AudioFilesExtensionResponse? lossless = quality == Quality.Lossless ? LosslessMetadata(track, ct) : null;
            return Choose(track, lossless, quality, fallbackDurationMs);
        }

        static FileChoice ChooseEpisode(string uri, Quality quality, long fallbackDurationMs, CancellationToken ct)
        {
            Md.Episode? episode = EpisodeMetadata(uri, ct);
            return episode is null ? FileChoice.Failed(Fault.Restricted) : Choose(episode, quality, fallbackDurationMs);
        }

        /// <summary>A per-request deadline linked to the caller's token. The CDN client itself has no `Timeout` (a
        /// legitimately long range body on a slow link must not be killed by a client-wide clock), so every request
        /// that is NOT a ranged body carries its own.</summary>
        static CancellationTokenSource Deadline(CancellationToken ct, int seconds)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));
            return cts;
        }

        static long HeadLength(string url, CancellationToken ct)
        {
            try
            {
                using var deadline = Deadline(ct, 20);
                using var message = new HttpRequestMessage(HttpMethod.Head, url);
                using HttpResponseMessage response = Cdn.Send(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode) return 0;
                return response.Content.Headers.ContentLength ?? 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                return 0;
            }
        }

        // ── 9. the clear head file ───────────────────────────────────────────────────────────────────────────────────
        //
        // The head service serves the first ~128 KB of a file UNENCRYPTED and without auth. It is what hides the key
        // and CDN latency behind the first seconds of audio the player can start on immediately: `OpenBody` fetches it
        // IN PARALLEL with storage-resolve and the audio key, and `Body.ReadAt` serves [0, headLen) out of it while
        // the body is still landing. The splice is proven byte-exact against decrypted chunk 0 (`Body.ProveHead`).

        const string HeadHost = "https://heads-fa-tls13.spotifycdn.com/head/";
        /// <summary>80 KiB, deliberately under the large-object threshold: enough for the container headers and the
        /// first seconds of audio, and a buffer the pump can reuse without pinning an LOH block per track.</summary>
        public const int HeadMaxBytes = 80 * 1024;

        /// <summary>The clear head. Empty on any failure — a missing head is a slower start, never a failed play.</summary>
        public static byte[] Head(string fileIdHex, CancellationToken ct)
        {
            try
            {
                using var deadline = Deadline(ct, 20);
                using var message = new HttpRequestMessage(HttpMethod.Get, HeadHost + fileIdHex.ToLowerInvariant());
                using HttpResponseMessage response = Cdn.Send(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode) return [];
                using Stream body = response.Content.ReadAsStream(deadline.Token);
                var buffer = new byte[HeadMaxBytes];
                int total = 0;
                while (total < HeadMaxBytes)
                {
                    int n = body.Read(buffer, total, HeadMaxBytes - total);
                    if (n <= 0) break;
                    total += n;
                }
                return total == HeadMaxBytes ? buffer : buffer.AsSpan(0, total).ToArray();
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                return [];
            }
        }

        /// <summary>The normalization gain the head file carries at byte 144 — the one number a head is read for
        /// before the body lands. 0 when the head is too short to hold it.</summary>
        public static float HeadGainDb(ReadOnlySpan<byte> head)
            => head.Length > 148 ? BitConverter.ToSingle(head[144..148]) : 0f;
    }
}
