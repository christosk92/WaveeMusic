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
//   3. THE CDN. `/storage-resolve/v2/files/audio/interactive/{wireFormat}/{fileIdHex}?product=0` answers a mirror list
//      and a TTL. The FORMAT segment is not decoration: the service signs a url per (format, file id) pair, and the v1
//      route without it signs into the Ogg object namespace whatever it is handed — which is why a FLAC id used to
//      resolve to mirrors that 404 every range while the id-keyed head service served 80 KiB of the same id. The
//      answers are still CACHED by file id alone, because a file id has exactly one format.
//   4. THE KEY. The AP's 0x0c/0x0d exchange first (`Spotify.RequestAudioKey`, owner D). AP audio-key service is
//      ACCOUNT-WIDE: one refusal means it will not serve any track this session, so it is tried once and latched off,
//      and everything after that goes to the deriver seam below. FLAC IS NOT ELIGIBLE FOR THAT PATH AT ALL — see the
//      lossless note below.
//   5. THE STREAM. AES-128-CTR with a PUBLIC iv over ranged CDN GETs, offset 0 at the container's first byte. For Ogg
//      and MP3 that means skipping the 167-byte Spotify header; for FLAC it means offset 0, because a Spotify FLAC has
//      no such header. The STORES under that — the clear head, the read-ahead ring, the fetch task, the disk cache —
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
// they are bounded (C8): 64 mirror sets, 256 keys, 4 ladder answers, oldest evicted.
//
// WHAT A FAILURE MEANS (G-038, D7). A metadata read that never reached a server, a 5xx or a 429 is `Fault.Network` (the bar
// retries); only an answer that says "nothing here" is `Fault.Restricted`. A lossless file whose key is refused plays its
// Ogg 320 rung instead of failing, and a build that cannot derive a lossless key never asks for the lossless rung at all.
//
// …AND A REFUSED BODY IS THE THIRD WAY (`Fault.Refused`, `LosslessFallback.DemoteForNoBody`). A lossless open can succeed
// in every step above — file id, mirrors, key, 80 KiB of clear head — and STILL never be handed a body byte, because the
// mirrors answer a non-2xx to every range. The head then plays for about half a second and the track is silence: observed
// once as 57 s of nothing followed by a skip. So a lossless `Open` waits, briefly and interruptibly, for the first body
// byte, and a refusal (or a deadline with zero bytes landed) demotes the whole open to the Ogg 320 rung — a different file
// id on a different mirror set — rather than starving on the rung that said no. That demotion is NOT remembered the way a
// refused key is: a key that cannot be had is a fact about the build, a refused body is a server saying no this minute, so
// the 320 answer is kept for seconds rather than half an hour and the next attempt asks for lossless again.

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
        public enum Format : byte { Unknown = 0, OggVorbis96, OggVorbis160, OggVorbis320, Mp3, Flac, Flac24, Aac }

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
            /// <summary>The file id resolved, the key was had, and every CDN mirror still refused the BODY — either at
            /// once, or by never landing a byte inside the open's first-body deadline. Distinct from
            /// <see cref="Network"/> on purpose: a refusal is an ANSWER, so the one thing that cannot help is waiting for
            /// it to change (D5's 90 s of patience is for a link that is slow, not for one that said no), and for a
            /// lossless file it is what the Ogg 320 demotion exists for (D7, <see cref="LosslessFallback.Decide"/>).</summary>
            Refused,
        }

        /// <summary>A file id and the format its bytes are in — everything the CDN needs to NAME the object, which is
        /// more than the id. Storage-resolve signs a url per (format, file id) pair, so a resolve that knows only the
        /// id is a resolve that guesses the format, and the guess is Ogg.</summary>
        public readonly record struct FileRef(string Hex, Md.AudioFile.Types.Format Wire);

        /// <summary>Which file the ladder picked, and everything the next three steps need. A VALUE.
        /// <see cref="Peak"/> is the catalogue's LINEAR true peak when the catalogue carried the gain (lossless), else 0 —
        /// an Ogg body's peak is read from its own header at open (byte 148).
        ///
        /// <para><see cref="Wire"/> is the catalogue's OWN format for this file id, carried beside <see cref="Fmt"/>
        /// rather than derived from it because <see cref="Fmt"/> is lossy on purpose (<see cref="FormatOf"/> collapses
        /// the four MP3 rungs into one, since no decoder cares). Storage-resolve does care: the number goes in the url
        /// it signs. It is read by exactly that one route, so a choice that never reaches it — a failed one, an
        /// external episode url — leaves it at its default rather than claiming a format it does not have.</para></summary>
        public readonly record struct FileChoice(
            byte[] FileId, string FileIdHex, byte[] TrackGid, Format Fmt, long DurationMs, float GainDb,
            string? ExternalUrl, Fault Fault, float Peak = 0f,
            Md.AudioFile.Types.Format Wire = Md.AudioFile.Types.Format.OggVorbis96)
        {
            public bool Ok => Fault == Audio.Fault.None && (FileId.Length > 0 || ExternalUrl is { Length: > 0 });

            /// <summary>What <see cref="Resolve"/> is asked with: the id and the format under which the CDN filed it.</summary>
            public FileRef Ref => new(FileIdHex, Wire);

            public static FileChoice Failed(Audio.Fault fault) => new([], "", [], Format.Unknown, 0, 0f, null, fault);
        }

        /// <summary>An opened track. <see cref="Stream"/> is positioned at the container's first byte and seeks in
        /// container coordinates; the caller disposes it (which disposes the <see cref="Body"/> under it).
        ///
        /// <para><see cref="Body"/> is the same bytes without the `Stream` shape: random access by offset, the epoch a
        /// seek bumps, the head/ring/disk stores. Wave 3's decoders read THAT (`RingSource` wraps it); `Stream` stays
        /// so the module and local paths, and anything that only speaks `Stream`, keep working through one class.</para>
        ///
        /// <para><see cref="Peak"/> is the LINEAR true peak the normalization gain is capped by (librespot's
        /// <c>get_factor</c>), 0 when unknown — carried so the adapters' <c>NormalizationFactor</c> can apply the cap.</para></summary>
        public readonly record struct Opened(
            System.IO.Stream? Stream, Format Fmt, long Length, long DurationMs, float GainDb, string FileIdHex, Fault Fault,
            Body? Body = null, float Peak = 0f)
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

        /// <summary>Decrypts one landed piece of a Spotify-hosted body IN PLACE. <paramref name="streamOffset"/> is
        /// <paramref name="buffer"/>[0]'s byte offset in the whole file — the contract of <see cref="Ctr.DecryptInPlace"/>,
        /// which is what a body without one runs. It is called from the fetch task and the disk-cache reads, so an
        /// implementation serializes whatever state it keeps.</summary>
        public delegate void BodyDecrypt(Span<byte> buffer, long streamOffset);

        /// <summary>THE PLAYPLAY SEAM, second half. A key the deriver produced can come with a NATIVE body decryptor that
        /// replaces the AES-CTR keystream for that file. Asked once per body open, after <see cref="Key"/> answered for
        /// the file; null, or a null answer, decrypts with the key. Set once at boot by the private assembly, beside
        /// <see cref="KeyDeriver"/>.</summary>
        public static Func<string, BodyDecrypt?>? BodyDecryptorFor { get; set; }

        /// <summary>Is the active connection metered? A SEAM the platform's network-cost probe installs (WP-6.S
        /// <c>NetworkPolicy</c>); null reads as unmetered. Read per open: a metered link gets the 10 s read-ahead tier.</summary>
        public static Func<bool>? MeteredConnection { get; set; }

        /// <summary>D7, the lossless rung and its fallback, as pure rules. The AP never serves a FLAC key and a
        /// public-only build has no deriver, so a Lossless setting there is VeryHigh320 from the start (no AUDIO_FILES
        /// request, no refused key); and when a deriver IS installed but refuses one file, that file plays its Ogg 320
        /// rung instead of failing — the plan's gate: "the public-only build plays the 320 rung".
        ///
        /// <para>A refused BODY joins the same fallback but not the same memory, which is the distinction the rest of
        /// this class turns on: a key that cannot be had is a fact about the build, a mirror set that refuses is a fact
        /// about one minute on one server. <see cref="Decide"/> and <see cref="DemoteForNoBody"/> say WHEN to fall back;
        /// <see cref="RememberFor"/> says for how long that answer is allowed to outlive the failure.</para></summary>
        public static class LosslessFallback
        {
            /// <summary>Should an open that failed with <paramref name="fault"/> on <paramref name="fmt"/> re-run the
            /// ladder at VeryHigh320? A lossless file whose key could not be had — and a lossless file whose BODY was
            /// refused: the 320 rung is a DIFFERENT file id on a different mirror set, which is the only thing left that
            /// has not said no. A network fault is still retried rather than downgraded; it is the link, not the file.</summary>
            public static bool Decide(Format fmt, Fault fault)
                => fmt is Format.Flac or Format.Flac24 && fault is Fault.NoDeriver or Fault.NoKey or Fault.Refused;

            /// <summary>The quality the ladder is asked for: Lossless only when this build can derive a lossless key.</summary>
            public static Quality Effective(Quality asked, bool canDerive)
                => asked == Quality.Lossless && !canDerive ? Quality.VeryHigh320 : asked;

            /// <summary>How long a lossless open waits for its FIRST body byte before it gives the rung up. The clear
            /// head is 80 KiB — about 0.6 s of a 1000 kbit/s FLAC — so an open whose body has not started by now is
            /// half a second of audio followed by silence for the rest of the track. The wait ENDS EARLY the moment the
            /// mirrors refuse (measured: every mirror refused and the re-resolve answered at 1.4 s), so this bound only
            /// governs a link that is merely too slow to carry lossless at all; and it is far under the ring's 8 s read
            /// bound, so the demotion lands before the first underrun rather than after the seventh.</summary>
            public const int FirstBodyMs = 3_000;

            /// <summary>Must a lossless open that has served only its clear head be demoted to the Ogg 320 rung? Yes
            /// once its mirror set is refusing, or once <see cref="FirstBodyMs"/> has passed with not one body byte
            /// landed. <paramref name="bodyBytes"/> deliberately excludes the head: the head is what makes this failure
            /// invisible — the track starts, plays for half a second and then starves. PURE.</summary>
            public static bool DemoteForNoBody(Format fmt, long bodyBytes, bool refusing, long waitedMs)
                => fmt is Format.Flac or Format.Flac24 && bodyBytes == 0 && (refusing || waitedMs >= FirstBodyMs);

            /// <summary>How long the demoted (Ogg 320) answer is remembered for, given what demoted it — the one place
            /// the two kinds of lossless failure part company.
            ///
            /// <para>A key that cannot be had is DETERMINISTIC: this build has no deriver, or this account has no
            /// entitlement, and asking again this session gets the same answer. Remembering it for the full
            /// <see cref="ChoiceTtlMs"/> is what stops every track paying for the same verdict twice.</para>
            ///
            /// <para>A REFUSED body is not a fact about the track at all. The entitlement is there, the ladder picked a
            /// real FLAC, the key was had — and a mirror set answered no, this minute, for reasons on the server's side.
            /// Sticking that to the uri for half an hour would quietly cost the user lossless on a track that is fine,
            /// and — worse for anyone trying to diagnose it — would make the bug unreproducible: a retry would be served
            /// the cached 320 rung and never reach a mirror, so the `audio.mirror` and `audio.resolve` lines the retry
            /// was FOR would never be printed. It is therefore suppression, not memory: just long enough
            /// (<see cref="RefusedChoiceTtlMs"/>) that a retry loop cannot hammer a dead url set. PURE.</para></summary>
            public static long RememberFor(Fault fault) => fault == Fault.Refused ? RefusedChoiceTtlMs : ChoiceTtlMs;
        }

        static int s_fallbackLogged;

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
        /// to take the winner — so <see cref="PickRung"/> stays a span fold with no protobuf on it. The WIRE format of
        /// the winner comes back beside ours: this is the only place that still holds it, and the CDN needs it.</summary>
        static (byte[] FileId, Format Fmt, Md.AudioFile.Types.Format Wire) PickFile(
            IReadOnlyList<Md.AudioFile> files, Quality quality)
        {
            int n = files.Count;
            if (n == 0) return ([], Format.Unknown, default);
            Span<Format> formats = n <= 32 ? stackalloc Format[n] : new Format[n];
            for (int i = 0; i < n; i++)
                formats[i] = files[i].FileId.Length == 0 ? Format.Unknown : FormatOf(files[i].Format);
            int pick = PickRung(formats, quality);
            return pick < 0
                ? ([], Format.Unknown, default)
                : (files[pick].FileId.ToByteArray(), formats[pick], files[pick].Format);
        }

        /// <summary>FLAC out of an AUDIO_FILES payload — 24-bit preferred over 16-bit. The audio KEY still uses the
        /// track's own gid: the FLAC is an alternative ENCODING of the same track, not an alternative track.</summary>
        static (byte[] FileId, Format Fmt, Md.AudioFile.Types.Format Wire) PickFlac(Af.AudioFilesExtensionResponse response)
        {
            byte[] best = [];
            Format bestFormat = Format.Unknown;
            Md.AudioFile.Types.Format bestWire = default;
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
                bestWire = file.Format;
            }
            return (best, bestFormat, bestWire);
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

        /// <summary>A true peak in dBFS as the LINEAR amplitude the gain cap compares against; 0 (= unknown) for a figure
        /// that is not a believable peak. PURE.</summary>
        public static float PeakLinear(float truePeakDb)
            => float.IsFinite(truePeakDb) ? SanePeak(MathF.Pow(10f, truePeakDb / 20f)) : 0f;

        /// <summary>A linear peak is a positive amplitude no louder than +12 dBFS; anything else (a garbage header float,
        /// a NaN, a zero) is "unknown" — a wrong peak would silence a track through the cap. PURE.</summary>
        public static float SanePeak(float peak) => float.IsFinite(peak) && peak > 0f && peak <= 4f ? peak : 0f;

        /// <summary>A header track gain beyond ±30 dB, or not finite, is garbage bytes (a wrong key, a file that has no
        /// such header), never a gain. PURE.</summary>
        public static float SaneGain(float gainDb) => float.IsFinite(gainDb) && Math.Abs(gainDb) <= 30f ? gainDb : 0f;

        /// <summary>The Ogg rungs — the only files whose Spotify header carries normalization data.</summary>
        public static bool IsOggFormat(Format format)
            => format is Format.OggVorbis96 or Format.OggVorbis160 or Format.OggVorbis320;

        /// <summary>How much decrypted file a header gain and peak need: the gain at 144, the peak at 148.</summary>
        public const int HeaderGainBytes = 152;

        /// <summary>The normalization a body opens with (D6; G-105). The catalogue's figure when it carried one (the
        /// lossless AUDIO_FILES normalization params); otherwise, for an OGG body ONLY, the Spotify header's track gain at
        /// byte 144 and its linear peak at 148 (librespot <c>NormalisationData::parse_from_ogg</c>). A FLAC has no such header
        /// — byte 144 of a FLAC is STREAMINFO/SEEKTABLE data, which read as a float was up to +30 dB — and an MP3 or an
        /// external body carries none, so both answer (0, 0). <paramref name="clearHeader"/> is the decrypted file from byte
        /// 0 (the clear head, or the cached chunk 0); empty when neither is at hand, and then the body learns the figure
        /// when chunk 0 lands (G-107). PURE.</summary>
        public static (float GainDb, float Peak) GainFor(Format fmt, float catalogueGainDb, float cataloguePeak,
            ReadOnlySpan<byte> clearHeader)
        {
            if (catalogueGainDb != 0f && float.IsFinite(catalogueGainDb)) return (catalogueGainDb, SanePeak(cataloguePeak));
            if (!IsOggFormat(fmt) || clearHeader.Length < HeaderGainBytes) return (0f, 0f);
            return (SaneGain(HeadGainDb(clearHeader)), HeadPeak(clearHeader));
        }

        /// <summary>Does a country list admit <paramref name="market"/>? The wire spells a list as 2-char country codes
        /// run together (<c>"SEGBUS"</c>, `metadata.proto` Restriction). An EMPTY <c>countries_allowed</c> is an empty
        /// WHITELIST — no country gate at all, not "allowed nowhere" (`lean_metadata.proto`'s corpus note) — and an empty
        /// forbidden list forbids nobody. A market that is not a 2-char code (the session has not learned it yet) is no
        /// verdict either: a gate nobody can evaluate admits. PURE.</summary>
        /// <param name="forbiddenList">true = <paramref name="list"/> is <c>countries_forbidden</c> (a member is OUT);
        /// false = <c>countries_allowed</c> (a non-member is out).</param>
        public static bool CountryAllowed(string list, string market, bool forbiddenList)
        {
            if (list.Length < 2 || market.Length != 2) return true;
            bool listed = false;
            for (int i = 0; i + 1 < list.Length; i += 2)
            {
                if (string.Compare(list, i, market, 0, 2, StringComparison.OrdinalIgnoreCase) != 0) continue;
                listed = true;
                break;
            }
            return forbiddenList ? !listed : listed;
        }

        /// <summary>May this track's OWN file[] be played in <paramref name="market"/>? False only when a restriction
        /// rules the market out — not in a non-empty <c>countries_allowed</c>, or in <c>countries_forbidden</c>. The
        /// catalogue and type fields are NOT a verdict (`lean_metadata.proto`: the restriction appears on the relinked
        /// class and the dead class alike). A relinked id keeps its old file[] listed but restricted; playing it hands
        /// the key service the OLD gid, which refuses (NoKey → Unavailable) where the alternative would have played. PURE.</summary>
        public static bool Allowed(Md.Track t, string market)
        {
            foreach (Md.Restriction r in t.Restriction)
            {
                if (r.HasCountriesAllowed && !CountryAllowed(r.CountriesAllowed, market, forbiddenList: false)) return false;
                if (r.HasCountriesForbidden && !CountryAllowed(r.CountriesForbidden, market, forbiddenList: true)) return false;
            }
            return true;
        }

        /// <summary>The whole ladder over the two payloads, pure: TRACK_V4 plus an optional AUDIO_FILES. FLAC wins when
        /// the account returned it AND the setting asked for it; otherwise the Ogg rungs of the track's own file[] — but
        /// only when <see cref="Allowed"/> admits the track in <paramref name="market"/>; otherwise (and when the list is
        /// empty) the first alternative that is admitted AND has a file, carrying ITS gid.</summary>
        /// <param name="market">The session's 2-char market (<c>Api.Market</c>), passed in so the ladder stays pure.</param>
        public static FileChoice Choose(Md.Track track, Af.AudioFilesExtensionResponse? lossless, Quality quality,
            long fallbackDurationMs, string market)
        {
            if (quality == Quality.Lossless && lossless is not null)
            {
                (byte[] flacId, Format flacFormat, Md.AudioFile.Types.Format flacWire) = PickFlac(lossless);
                if (flacId.Length > 0)
                {
                    Af.NormalizationParams? np = lossless.DefaultFileNormalizationParams;
                    float gain = np is not null ? NormalizationGain(np.LoudnessDb, np.TruePeakDb) : 0f;
                    float peak = np is not null ? PeakLinear(np.TruePeakDb) : 0f;
                    return new FileChoice(flacId, Hexed(flacId), track.Gid.ToByteArray(), flacFormat,
                        track.HasDuration ? track.Duration : fallbackDurationMs, gain, null, Fault.None, peak, flacWire);
                }
            }

            if (Allowed(track, market))
            {
                (byte[] fileId, Format format, Md.AudioFile.Types.Format wire) = PickFile(track.File, quality);
                if (fileId.Length > 0)
                    return new FileChoice(fileId, Hexed(fileId), track.Gid.ToByteArray(), format,
                        track.HasDuration ? track.Duration : fallbackDurationMs, 0f, null, Fault.None, 0f, wire);
            }

            foreach (Md.Track alternative in track.Alternative)
            {
                if (!Allowed(alternative, market)) continue;
                (byte[] altId, Format altFormat, Md.AudioFile.Types.Format altWire) = PickFile(alternative.File, quality);
                if (altId.Length == 0) continue;
                long duration = alternative.HasDuration ? alternative.Duration
                    : track.HasDuration ? track.Duration : fallbackDurationMs;
                // The alternative's OWN gid: the audio key is bound to the (file, track) pair and the alternative is a
                // different track row on the wire even though the user thinks it is the same song.
                return new FileChoice(altId, Hexed(altId), alternative.Gid.ToByteArray(), altFormat,
                    duration, 0f, null, Fault.None, 0f, altWire);
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

            (byte[] fileId, Format format, Md.AudioFile.Types.Format wire) = PickFile(episode.Audio, quality);
            return fileId.Length == 0
                ? FileChoice.Failed(Fault.NoFile)
                : new FileChoice(fileId, Hexed(fileId), gid, format, duration, 0f, null, Fault.None, 0f, wire);
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
        /// answered for a different kind, or answered an error status for this one. <paramref name="entityStatus"/> is
        /// that entry's own status (its header's, else 200), or 0 when the response held no entry of the kind at all.</summary>
        public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> response, Xm.ExtensionKind kind, out int entityStatus)
        {
            entityStatus = 0;
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
                    if (entityStatus == 0) entityStatus = status;
                    if (status is < 200 or >= 300) continue;
                    if (data.ExtensionData is null) continue;
                    entityStatus = status;
                    return data.ExtensionData.Value.Span;
                }
            }
            return default;
        }

        /// <summary>What a failed TRACK_V4 / EPISODE_V4 read MEANS (G-038). A request that never reached a server
        /// (status 0), a 401 that survived the token refresh, a timeout, a 429 or a 5xx — at the HTTP level or on the entry
        /// itself — is the network being unlucky: <see cref="Fault.Network"/>, which the bar offers to retry. Only an
        /// answer that genuinely says "nothing here" (a 2xx with no payload, a 404, any other 4xx) is
        /// <see cref="Fault.Restricted"/>, the terminal "unavailable". PURE.</summary>
        public static Fault MetadataFault(int httpStatus, int entityStatus, bool hasPayload)
        {
            if (hasPayload) return Fault.None;
            if (httpStatus is 0 or 401 or 408 or 429 or >= 500) return Fault.Network;
            if (httpStatus is < 200 or >= 300) return Fault.Restricted;
            return entityStatus is 408 or 429 or >= 500 ? Fault.Network : Fault.Restricted;
        }

        static Md.Track? TrackMetadata(string trackUri, CancellationToken ct, out Fault fault)
        {
            Xm.ExtensionKind[] kinds = [Xm.ExtensionKind.TrackV4];
            Api.Result result = Api.MetadataPost(Api.MetadataBody(trackUri, kinds, Api.Market, Api.Catalogue), ct);
            int entity = 0;
            ReadOnlySpan<byte> payload = result.Ok ? Payload(result.Bytes, Xm.ExtensionKind.TrackV4, out entity) : default;
            fault = MetadataFault(result.Status, entity, !payload.IsEmpty);
            if (payload.IsEmpty) return null;
            try { return Md.Track.Parser.ParseFrom(payload); }
            catch (InvalidProtocolBufferException) { fault = Fault.Restricted; return null; }
        }

        static Md.Episode? EpisodeMetadata(string episodeUri, CancellationToken ct, out Fault fault)
        {
            Xm.ExtensionKind[] kinds = [Xm.ExtensionKind.EpisodeV4];
            Api.Result result = Api.MetadataPost(Api.MetadataBody(episodeUri, kinds, Api.Market, Api.Catalogue), ct);
            int entity = 0;
            ReadOnlySpan<byte> payload = result.Ok ? Payload(result.Bytes, Xm.ExtensionKind.EpisodeV4, out entity) : default;
            fault = MetadataFault(result.Status, entity, !payload.IsEmpty);
            if (payload.IsEmpty) return null;
            try { return Md.Episode.Parser.ParseFrom(payload); }
            catch (InvalidProtocolBufferException) { fault = Fault.Restricted; return null; }
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
            ReadOnlySpan<byte> payload = Payload(result.Bytes, Xm.ExtensionKind.AudioFiles, out _);
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

        /// <summary>WHAT storage-resolve answered for one file, kept so the line that REPORTS a refusal can name it. A
        /// re-resolve reaches the log through a urls-only seam, so without this the `audio.resolve` line reads exactly
        /// the same whether the service 403'd us, answered 200 with RESTRICTED (this account may not stream this file),
        /// or answered a perfectly good url set the CDN then refused — three different bugs, one line.</summary>
        /// <param name="Status">The HTTP status of the storage-resolve request itself.</param>
        /// <param name="Verdict">The service's own `StorageResolveResponse.Result`, or why there was none.</param>
        /// <param name="Mirrors">How many urls came back with it.</param>
        public readonly record struct ResolveAnswer(int Status, string Verdict, int Mirrors)
        {
            /// <summary>Nothing has been asked for this file yet (or it fell out of the four slots).</summary>
            public static ResolveAnswer Unasked => new(0, "unasked", 0);
        }

        const int MirrorCacheMax = 64;
        static readonly Lock MirrorGate = new();
        static readonly Dictionary<string, Mirrors> MirrorCache = new(MirrorCacheMax, StringComparer.OrdinalIgnoreCase);
        static readonly Queue<string> MirrorOrder = new(MirrorCacheMax);

        /// <summary>Four files' worth of last answer (C8): the playing one, the prepared one and the two prefetched.</summary>
        const int ResolveAnswerSlots = 4;
        static readonly Lock AnswerGate = new();
        static readonly string[] s_answerKeys = ["", "", "", ""];
        static readonly ResolveAnswer[] s_answers = new ResolveAnswer[ResolveAnswerSlots];
        static int s_answerNext;

        /// <summary>The last storage-resolve answer for <paramref name="fileIdHex"/>, or
        /// <see cref="ResolveAnswer.Unasked"/>. Read by the body's re-resolve log line.</summary>
        public static ResolveAnswer LastResolve(string fileIdHex)
        {
            lock (AnswerGate)
            {
                for (int i = 0; i < ResolveAnswerSlots; i++)
                    if (string.Equals(s_answerKeys[i], fileIdHex, StringComparison.OrdinalIgnoreCase)) return s_answers[i];
            }
            return ResolveAnswer.Unasked;
        }

        /// <summary>Remember what the service said, and hand the mirrors straight back — every `return` of
        /// <see cref="Resolve"/> goes through here, so there is no arm that answers without recording why.</summary>
        static Mirrors Answered(string fileIdHex, Mirrors mirrors, int status, string verdict)
        {
            var answer = new ResolveAnswer(status, verdict, mirrors.Urls.Length);
            lock (AnswerGate)
            {
                int slot = -1;
                for (int i = 0; i < ResolveAnswerSlots; i++)
                    if (string.Equals(s_answerKeys[i], fileIdHex, StringComparison.OrdinalIgnoreCase)) { slot = i; break; }
                if (slot < 0) { slot = s_answerNext; s_answerNext = (s_answerNext + 1) % ResolveAnswerSlots; }
                s_answerKeys[slot] = fileIdHex;
                s_answers[slot] = answer;
            }
            return mirrors;
        }

        /// <summary>The service's verdict as the log spells it. Literals, not `ToString()`: an enum name costs a
        /// reflection lookup and an allocation on a line that runs on every refused range.</summary>
        static string VerdictOf(St.StorageResolveResponse.Types.Result result) => result switch
        {
            St.StorageResolveResponse.Types.Result.Cdn => "cdn",
            St.StorageResolveResponse.Types.Result.Storage => "storage",
            St.StorageResolveResponse.Types.Result.Restricted => "restricted",
            _ => "unknown",
        };

        /// <summary>Resolve (and cache) the mirror list. The TTL the service sends is honoured at 80 % — the last fifth
        /// is the window a long track needs to finish streaming on a url it already opened.
        ///
        /// <para>Asked with a <see cref="FileRef"/> rather than a hex id because the ROUTE needs the format
        /// (<c>…/interactive/{wireFormat}/{fileId}</c>) — the url the service signs is per (format, id) pair. The CACHE
        /// is still keyed by the id alone, and correctly so: a file id names one object in one format, so the two keys
        /// are the same key.</para></summary>
        public static Mirrors Resolve(FileRef file, CancellationToken ct)
        {
            string fileIdHex = file.Hex;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (MirrorGate)
            {
                if (MirrorCache.TryGetValue(fileIdHex, out Mirrors cached) && cached.ExpiresAtMs > now) return cached;
            }

            Interlocked.Increment(ref s_statResolves);
            Api.Result result = Api.StorageResolve(fileIdHex, file.Wire, ct);
            if (!result.Ok) return Answered(fileIdHex, new Mirrors([], 0, Fault.Network), result.Status, "http");

            St.StorageResolveResponse parsed;
            try { parsed = St.StorageResolveResponse.Parser.ParseFrom(result.Bytes); }
            catch (InvalidProtocolBufferException)
            { return Answered(fileIdHex, new Mirrors([], 0, Fault.Network), result.Status, "unparsable"); }

            if (parsed.Result == St.StorageResolveResponse.Types.Result.Restricted)
                return Answered(fileIdHex, new Mirrors([], 0, Fault.Restricted), result.Status, "restricted");
            if (parsed.Cdnurl.Count == 0)
                return Answered(fileIdHex, new Mirrors([], 0, Fault.Network), result.Status, "no-cdnurl");

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
            return Answered(fileIdHex, mirrors, result.Status, VerdictOf(parsed.Result));
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

        /// <summary>Reset the AP latch. `Spotify.Session.AdoptWelcome` calls it on EVERY AP welcome — a fresh login and
        /// the reconnect after an `ap channel failed` alike (the Welcome effect fires per successful `ConnectAndLogin`,
        /// not per account) — so one refused key window never outlives the AP session that produced it.</summary>
        public static void ResetKeyLatch()
        {
            s_apKeysDisabled = false;
            lock (KeyGate) { KeyCache.Clear(); KeyOrder.Clear(); }
            lock (ChoiceGate) Array.Clear(s_choices);           // a new session may be a new market: its ladder answers anew
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
        /// `CancellationToken` on the fetch task, and a client-wide timeout would also kill a legitimately long
        /// 512 KiB body on a slow link.
        ///
        /// <para>It sends the session's `User-Agent` (`Identity.UserAgent`, the same string every spclient request
        /// carries). librespot, go-librespot and the desktop client all name themselves to the CDN; we sent nothing at
        /// all, which is a difference no honest client has and the audio edge is free to treat as it likes.</para></summary>
        static readonly HttpClient Cdn = NewCdnClient();

        static HttpClient NewCdnClient()
        {
            var client = new HttpClient(Wire.Handler("cdn", new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                MaxConnectionsPerServer = 4,
                EnableMultipleHttp2Connections = false,
            }, storms: false))
            { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Identity.UserAgent);
            return client;
        }

        /// <summary>The quality the user chose, capped where the plan says it must be: a metered connection first
        /// (<see cref="Platform.Network.EffectiveQuality()"/> — the metered cap applies to the lossless rung exactly as
        /// it does to 320), then Lossless only on a build that can derive a lossless key (D7,
        /// <see cref="LosslessFallback.Effective"/>). Read PER OPEN, so a settings or connection-cost change applies from
        /// the very next track rather than from the next launch.</summary>
        public static Quality PreferredQuality()
        {
            int effective = Platform.Network.EffectiveQuality();
            return LosslessFallback.Effective((Quality)Math.Clamp(effective, 0, 3), CanDerive);
        }

        /// <summary>THE entry point Wave 3 calls. Blocks: metadata, ladder, CDN, key, then an opened stream.</summary>
        public static Opened Open(string uri, CancellationToken ct) => Open(uri, PreferredQuality(), 0, ct);

        /// <inheritdoc cref="Open(string, CancellationToken)"/>
        /// <param name="prepared">The NEXT track, opened ahead of a hand-off: its ring comes out of the shared read-ahead
        /// budget at no more than <see cref="ReadAheadBudget.PreparedSeconds"/> (Vorbis plan §5.3).</param>
        /// <remarks>A lossless file whose key is refused re-runs the ladder at VeryHigh320 over the SAME metadata (D7), and
        /// that answer is remembered for the uri at this setting, so the next open does not ask for the FLAC again.</remarks>
        public static Opened Open(string uri, Quality quality, long fallbackDurationMs, CancellationToken ct, bool prepared = false)
        {
            if (!Current.IsOnline) return Opened.Failed(Fault.Offline);

            CachedChoice resolved = ChooseFor(uri, quality, fallbackDurationMs, ct);
            FileChoice choice = resolved.Choice;
            if (!choice.Ok) return Opened.Failed(choice.Fault == Fault.None ? Fault.NoFile : choice.Fault);

            Opened opened = Open(choice, ct, prepared);
            if (!LosslessFallback.Decide(choice.Fmt, opened.Fault)) return opened;

            // A refused body is a per-TRACK event and always worth a line; a key that cannot be had is a property of the
            // BUILD, so it is latched to one line rather than repeated for every track of the session.
            if (opened.Fault == Fault.Refused)
                Log.Warn("audio", $"audio.demote file={choice.FileIdHex} fmt={choice.Fmt} why=refused rung=OggVorbis320");
            else if (Interlocked.Exchange(ref s_fallbackLogged, 1) == 0)
                Log.Warn("audio", $"audio.demote file={choice.FileIdHex} fmt={choice.Fmt} why={opened.Fault} "
                                  + "rung=OggVorbis320 (logged once)");
            Md.Track? track = resolved.Track ?? TrackMetadata(uri, ct, out _);
            if (track is null) return opened;
            FileChoice ogg = Choose(track, null, Quality.VeryHigh320, fallbackDurationMs, Api.Market);
            if (!ogg.Ok) return opened;
            // The demotion decides its own shelf life (`LosslessFallback.RememberFor`): sticky for a key that cannot be
            // had, seconds for a refused body — otherwise one bad mirror set costs the user lossless on this track for
            // half an hour and swallows the very retry that was meant to diagnose it.
            RememberChoice(new CachedChoice(uri, quality, ogg, track, Environment.TickCount64,
                                            LosslessFallback.RememberFor(opened.Fault)));
            return Open(ogg, ct, prepared);
        }

        /// <summary>The next track's metadata, ladder, head, mirrors and key, warmed without opening it (the reducer's
        /// "prefetch at load", G-112). Non-blocking: the ladder runs on an api thread and lands in the choice cache, the rest
        /// in <see cref="Prefetch(in FileChoice)"/>'s caches, so the boundary's <see cref="Open(string, Quality, long,
        /// CancellationToken, bool)"/> costs the first range and the tail.</summary>
        public static void Prefetch(string uri)
        {
            if (uri.Length == 0 || !Current.IsOnline) return;
            Quality quality = PreferredQuality();
            Api.Run(() =>
            {
                FileChoice warmed = ChooseFor(uri, quality, 0, CancellationToken.None).Choice;
                if (warmed.Ok) Prefetch(in warmed);
            });
        }

        /// <summary>A plain body on somebody else's host (a podcast enclosure, G-109): no key, no CDN resolve, format MP3.
        /// The file-id slot carries a short key of the url for the log lines and the live-body diagnostics.</summary>
        public static FileChoice ExternalChoice(string url, long durationMs)
            => new([], "ext" + ((uint)StringComparer.Ordinal.GetHashCode(url)).ToString("x8"), [], Format.Mp3,
                Math.Max(0, durationMs), 0f, url, Fault.None);

        // ── 8a. the choice cache (C8: four answers) ─────────────────────────────────────────────────────────────────
        //
        // The ladder's answer for a uri at a quality: what a prefetch warms and the boundary's open reads, and what the
        // lossless fallback re-chooses over. File ids do not move; the mirrors and keys under them have their own caches.

        const int ChoiceCacheMax = 4;

        /// <summary>How long a ladder answer is good for. A file id does not move, so this is only a bound on how stale
        /// a catalogue read may be.</summary>
        public const long ChoiceTtlMs = 30 * 60_000;

        /// <summary>…and how long a REFUSED lossless demotion is good for, which is a different question entirely. A
        /// refused body is not a fact about the track — the account has the entitlement, the file id resolved, the key
        /// was had, and then some mirror set said no THIS MINUTE. Remembering the 320 rung for half an hour would cost
        /// the user lossless on that track for half an hour, and it would hide the refusal itself: the retry meant to
        /// gather evidence would be served the cached rung and never reach a mirror, so not one `audio.mirror` or
        /// `audio.resolve` line would be printed. So it is kept only long enough to stop a retry loop hammering a dead
        /// url set — the same scale as the pump's refused starve budget (`Playback.Audio.StarvePolicy.RefusedFailMs`).</summary>
        public const long RefusedChoiceTtlMs = 6_000;

        /// <summary>One remembered ladder answer, with the track it came from (null for an episode). <paramref name="TtlMs"/>
        /// is per ENTRY rather than per cache because the lossless demotion's answer is worth remembering for a very
        /// different length of time depending on what demoted it (<see cref="LosslessFallback.RememberFor"/>).</summary>
        readonly record struct CachedChoice(string Uri, Quality Quality, FileChoice Choice, Md.Track? Track, long AtMs,
            long TtlMs = ChoiceTtlMs);

        static readonly Lock ChoiceGate = new();
        static readonly CachedChoice[] s_choices = new CachedChoice[ChoiceCacheMax];
        static int s_choiceNext;

        static CachedChoice ChooseFor(string uri, Quality quality, long fallbackDurationMs, CancellationToken ct)
        {
            long now = Environment.TickCount64;
            lock (ChoiceGate)
            {
                foreach (CachedChoice c in s_choices)
                {
                    if (c.Uri is not null && c.Quality == quality && now - c.AtMs < c.TtlMs
                        && string.Equals(c.Uri, uri, StringComparison.Ordinal)) return c;
                }
            }

            CachedChoice made;
            if (uri.StartsWith("spotify:episode:", StringComparison.Ordinal))
            {
                Md.Episode? episode = EpisodeMetadata(uri, ct, out Fault fault);
                made = new CachedChoice(uri, quality,
                    episode is null ? FileChoice.Failed(fault) : Choose(episode, quality, fallbackDurationMs), null, now);
            }
            else
            {
                Md.Track? track = TrackMetadata(uri, ct, out Fault fault);
                if (track is null) made = new CachedChoice(uri, quality, FileChoice.Failed(fault), null, now);
                else
                {
                    Af.AudioFilesExtensionResponse? lossless = quality == Quality.Lossless ? LosslessMetadata(track, ct) : null;
                    made = new CachedChoice(uri, quality, Choose(track, lossless, quality, fallbackDurationMs, Api.Market),
                                            track, now);
                }
            }
            if (made.Choice.Ok) RememberChoice(in made);
            return made;
        }

        static void RememberChoice(in CachedChoice choice)
        {
            lock (ChoiceGate)
            {
                for (int i = 0; i < s_choices.Length; i++)
                {
                    if (s_choices[i].Quality == choice.Quality && string.Equals(s_choices[i].Uri, choice.Uri, StringComparison.Ordinal))
                    {
                        s_choices[i] = choice;
                        return;
                    }
                }
                s_choices[s_choiceNext] = choice;
                s_choiceNext = (s_choiceNext + 1) % s_choices.Length;
            }
        }

        /// <summary>The half after the ladder: the head, the CDN, the key and the byte stores. Split out so a caller
        /// that already resolved the file (a prefetch that warmed the metadata) does not resolve it twice.
        ///
        /// <para>The body of it is `Spotify.Audio.Stream.cs`'s <see cref="OpenBody"/>: head ‖ storage-resolve ‖ key in
        /// parallel, then the first body range ‖ the tail. Four HTTP requests cold, one round trip to the first
        /// decodable byte.</para></summary>
        public static Opened Open(in FileChoice choice, CancellationToken ct, bool prepared = false)
            => OpenBody(in choice, ct, prepared);

        /// <summary>A per-request deadline linked to the caller's token. The CDN client itself has no `Timeout` (a
        /// legitimately long range body on a slow link must not be killed by a client-wide clock), so every request
        /// that is NOT a ranged body carries its own.</summary>
        static CancellationTokenSource Deadline(CancellationToken ct, int seconds)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));
            return cts;
        }

        /// <summary>An external body's length (the <see cref="OpenSeams.ExternalLength"/> seam, formerly the two-step
        /// `HeadLength` + `ProbeLength`): HEAD's `Content-Length`, else a `Range: bytes=0-0` GET's `Content-Range` total
        /// (a host that refuses HEAD, or answers it with no length). &gt;0 the length · 0 reachable but unnamed (G-119: the
        /// body opens on the duration's estimate and the first range names the truth) · −1 unreachable. Blocks on the
        /// open's thread (an api thread or the pump's blocking `Open`, C9), HTTP/1.1 — the only synchronous sends left on
        /// <see cref="Cdn"/> (the ranged body itself is async end to end, `Spotify.Audio.Stream.cs`'s
        /// `HttpRangeSource.OpenAsync`).</summary>
        static long ExternalLength(string url, CancellationToken ct)
        {
            using var deadline = Deadline(ct, 20);
            try
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, url);
                using HttpResponseMessage r = Cdn.Send(head, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (r.IsSuccessStatusCode && r.Content.Headers.ContentLength is > 0 and { } n) return n;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { }
            try
            {
                using var probe = new HttpRequestMessage(HttpMethod.Get, url);
                probe.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using HttpResponseMessage r = Cdn.Send(probe, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if ((int)r.StatusCode is not (200 or 206)) return -1;
                return r.Content.Headers.ContentRange is { HasLength: true, Length: { } total } ? total
                     : (int)r.StatusCode == 200 ? r.Content.Headers.ContentLength ?? 0 : 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                return -1;
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
                Interlocked.Increment(ref s_statHeads);
                using var deadline = Deadline(ct, 20);
                using var message = new HttpRequestMessage(HttpMethod.Get, HeadHost + fileIdHex.ToLowerInvariant());
                using HttpResponseMessage response = Cdn.Send(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode) return [];
                using System.IO.Stream body = response.Content.ReadAsStream(deadline.Token);
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

        /// <summary>The track's LINEAR true peak the header carries at byte 148, right after the gain (librespot's
        /// <c>NormalisationData</c>: track gain, track peak, album gain, album peak from 144). 0 when the head is too short
        /// or the figure is not a believable peak (<see cref="SanePeak"/>).</summary>
        public static float HeadPeak(ReadOnlySpan<byte> head)
            => head.Length >= 152 ? SanePeak(BitConverter.ToSingle(head[148..152])) : 0f;
    }
}
