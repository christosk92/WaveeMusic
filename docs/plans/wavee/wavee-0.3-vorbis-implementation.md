# Wavee 0.3 — Ogg Vorbis: a from-scratch unsafe/SIMD decoder, one-request seeks, and the CDN stream layer, implementation plan

Status: DRAFT for approval, 2026-09-13. A named partial beside the master plan
(`docs/plans/wavee/wavee-0.3-implementation.md`, §2 `Playback/`, §4.9, §5 Wave 3) and the sibling FLAC plan
(`docs/plans/wavee/wavee-0.3-flac-implementation.md`, whose §1.2 engine contract, §3.3 bit-reader shape, §3.9 seek
planner and §4.3 adapter this plan mirrors so the two decoders read alike). Rules P1-P16 / C1-C10
(`C:\Users\ChristosKarapasias\Documents\relationships_wavee.md` §5.11-5.12) apply: CORE allocates nothing after
`Open`, no LINQ / closures / async / boxing in CORE, SIMD with a `Vector128` (NEON) path always present because
arm64 ships, NativeAOT, Windows only.

Christos's brief, verbatim: "a small tiny Vorbis/Ogg decoder that is super fast, with seeking specifically ultra
fast and optimized for our CDN stuff"; "the decoder should use heavy optimized low level code, unsafe, span, simd
instructions and other heavily optimized code"; and "are we also heavily optimizing/reworking our Spotify CDN
buffering? fast playback is a priority (play from head file) but then seeking should be very fast like YouTube
Music". §3 is written to the first two: `unsafe` pointer loops over buffers pinned once at open, `Unsafe.Add` /
`Unsafe.ReadUnaligned`, `[SkipLocalsInit]`, `Vector256` when accelerated with a `Vector128` path and a scalar
tail, bounds checks hoisted to one guard per loop where the bound is proven by construction — the code in §3 is
the code, not a clean draft to be optimized later. §5 is written to the third: the decoder and the stream are one
problem, and the stream layer is redesigned around the head file, a seconds-sized read-ahead ring, a sparse disk
cache and next-track prefetch.

The PlayPlay derivation is a private repository; nothing here reads, names or depends on it. The key reaches this
plan as `Spotify.Audio.CtrStream`'s key argument; AES-CTR is position-addressable, which is the whole reason a seek
can start anywhere.

---

## 0. The decision, in three sentences

1. **Write the Vorbis decoder and the Ogg layer ourselves, in CORE, as two named partials** —
   `Playback/Playback.Audio.Ogg.cs` (page parser, packet assembly, the page index, the seek planner) and
   `Playback/Playback.Audio.Vorbis.cs` (headers, codebooks with a 10-bit fast-Huffman table, floor 1, residue
   0/1/2, an stb-structured inverse MDCT with `Vector128`/`Vector256` kernels, fused window + overlap-add +
   interleave + gain, channel coupling) — zero allocations after `Open`, every buffer sized from the setup header,
   the hot loops `unsafe` over arrays pinned once. The vendored NVorbis fork (a per-packet allocator with a virtual
   call per Huffman symbol, §1.4) is deleted in Wave 6 with `src/apps/_old` (CLAUDE.md: no legacy paths; §9 Q1).
2. **Seeking is one request, by design, on both sides of the seam**: the target sample becomes a byte estimate
   (`bytes × target / total`, corrected by the page index of every page already seen and backed off by one max
   page); the stream layer fetches ONE small range (48 KiB — under one RTT at the CDN's observed throughput) straight
   at the estimate on a named network thread, cancelling any in-flight range (epoch); the page found there either
   holds the target (decode forward) or narrows a granulepos bisection bounded at 8 probes; the decoder resumes at
   the page boundary by decoding one packet whose output is discarded (the spec's priming rule) and lands
   sample-exact. Playback starts from the clear head file before the key round-trip finishes and splices to the
   body at the head's length, proven byte-exact by the key check; a seconds-sized read-ahead ring (never bytes: a
   24-bit FLAC makes 128 KiB less than one second) keeps the decoder from ever blocking; a sparse per-file disk
   cache with a range map serves replays and revisited regions; the next track's resolve + key + head are prefetched
   in the last seconds for gapless and instant skip.
3. **The engine-facing surface does not move.** `VorbisAudioDecoder : IAudioDecoder` in owner H's
   `Playback.Audio.cs` keeps its name and its factory arm and loses its NVorbis internals for a byte window +
   `Ogg.Reader` + `Vorbis.Decoder`, exactly `FlacAudioDecoder`'s shape (FLAC plan §4.3); the byte seam gains
   `ReadAt(offset, span)` so a probe never drains a forward stream; the normalization gain (`Opened.GainDb`, or the
   header's byte 144 when the catalogue is silent) is folded into the interleave multiply; `GaplessInfo` becomes
   the TRUTH from the granule positions instead of `None`. CORE decoder + Ogg layer + tests (owner V) and the stream
   layer (owner F's file, a named partial `+Spotify.Audio.Stream.cs`) start today; the adapter and the ring are a
   +260-line request to H in Wave 3.

---

## 1. Research record — what is true today

### 1.1 What Wave 3 has today (`src/apps/Wavee/Playback/Playback.Audio.cs`, owner H, 2,365 lines)

Read in full. The Vorbis-relevant facts, with line references:

| Fact | Where |
|---|---|
| The decoder factory is one switch: `Flac/Flac24 → FlacAudioDecoder`, `Mp3 → Mp3AudioDecoder`, `_ → VorbisAudioDecoder(opened.GainDb)`; the engine calls `TakePendingDecoder(mix)` once per open and takes the decoder the pump set in `s_pendingDecoder` | `:730-744`, `:308`, `:568` |
| `VorbisAudioDecoder` today: `new NVorbis.VorbisReader(new ByteSourceStream(src), closeOnDispose: false)`; `_src = new float[4096 × channels]`; `Read` → `PullConform(dst, …, _pull)` over `_reader.ReadSamples(Span<float>)`; `Seek(frame)` → `_reader.SeekTo(srcFrame)` then `_resampler?.Reset()`; **`Gapless => GaplessInfo.None`** with the comment that NVorbis already applies the priming packet and the EOS granule trim internally | `:753-816` |
| `ByteSourceStream : Stream` is the adapter "for the two third-party decoders that only speak `Stream`" — a `Read(Span<byte>)` and a `Seek` over `IMediaByteSource`; it exists only because NVorbis and NLayer want a `Stream` | `:1059-1110` |
| `Prefetching : IMediaByteSource` wraps the `CtrStream`: `Read` is the bounded wait (poll 4 ms, up to 8 s) that guarantees **a zero is only ever true EOF** ("NVorbis's page reader gives up after ten" zero reads — rule 2 of the file header); `Seek(offset)` is `_inner.Seek(_skip + offset)`; `SetSkip` re-bases logical zero past the 0xa7 header; `Caps = { Seekable, KnownLength, ExpensiveSeek = true }`. **It has no buffer of its own**: every `Read` is a synchronous `CtrStream.Read` on the decode-ahead thread | `:1744-1810`, `:24-27`, `:1601-1604` |
| `ApplySkip` reads the first `0xa7 + 8` bytes, sniffs, and calls `pf.SetSkip(SkipFor(format, head))` — so the decoder never sees the Spotify header; `SniffFormat` answers `OggVorbis320` for any `OggS` (the rung is a label, not a codec fact) | `:553-565`, `:1841` |
| `GainLinear(gainDb)` = `10^(dB/20)` gated by `Platform.Keys.NormalizationEnabled`, "folded into the conversion that happens anyway — one multiply per sample and no extra pass" | `:746-750` |
| `HandOffAt(positionMs, durationMs, fadeMs, prepared, overlapAllowed, handOffInFlight)` is the ONE selector between gapless (commit inside the last 1.5 s) and crossfade; `Prepare(row, id)` is the pre-open of the next track; `EndingSoonMs(fade, dur) = fade + 8 s` is the window in which the reducer is nudged to prepare; `GaplessJoinClock` (G's, CORE) owns the join arithmetic | `:116-122`, `:100-107`, `:370-375`, `:1416` |
| `SpotifySource` computes `kbps = Length × 8 / DurationMs` from the opened stream — the byte-rate the seek estimate wants is already logged at open | `:1665-1668` |
| The FLAC adapter is the shape to mirror: `WindowBytes = 64 KiB`, `Fill()` keeps the unread tail, `NextBlock()` resyncs one byte on a bad frame, `Seek` drives the CORE `SeekPlan` with `ReadWindowAt(offset)` = `_src.Seek` + `Fill`, drops `target − SampleNumber` samples inside the found frame | `:2139-2364` |
| The engine's `AudioTripwire` forbids managed allocation on the RT thread; the decoder runs on the decode-ahead thread (`FluentGpu.AudioProducer`) behind a 1 s ring — a decoder allocation is GC pressure, not a glitch, and P8 applies all the same | FLAC plan §1.2 |

**Consequences.** (a) The adapter surface (`TryOpen` / `Read` / `Seek` / `Gapless`) and the factory arm stay; only
the internals change (§6). (b) `ByteSourceStream` loses its Vorbis consumer and keeps its MP3 one until NLayer is
replaced (out of scope). (c) A seek today goes `IAudioDecoder.Seek` → `NVorbis.SeekTo` → `ByteSourceStream.Seek`
→ `Prefetching.Seek` → `CtrStream.Seek`, and every NVorbis page probe is a `Stream.Seek` + 2-4 `Stream.Read`
calls (§1.4), each outside the held chunk a synchronous range GET on the decode thread. (d) There is no read-ahead,
no head file, no disk cache and no next-track byte prefetch in Wave 3 today — `Prepare` opens the next track's
stream but nothing fetches its bytes until the engine's decode-ahead pulls them (§1.5 has what 0.2.9 did).

### 1.2 The byte seam under the decoder (`src/apps/Wavee/Spotify/Spotify.Audio.cs`, owner F, 886 lines)

| Fact | Where |
|---|---|
| `CtrStream : Stream`, AES-128-CTR with the public iv, **one HTTP range request per refill, `ChunkBytes = 128 KiB`** ("two seconds of 320 kbps and one range request per two seconds of listening"), decrypted in place at the TRUE file offset; **one chunk buffer, no cache, no read-ahead**: a read outside the held chunk is a new synchronous range request on the calling (decode) thread, even for a chunk fetched a moment ago | `:606-700`, `:620`, `:675-700` |
| `Seek` is a position update only; the next `Read` fetches | `:727-738` |
| `Position 0` is the CONTAINER's first byte; `skip = HeaderBytesFor(format)` = `0xa7` for Ogg, 0 for FLAC; `Length = fileLength − skip` | `:609-611`, `:150-151`, `:648` |
| `Opened(Stream, Fmt, Length, DurationMs, GainDb, FileIdHex, Fault)` — the total byte length and the catalogue duration are both known at open, which is what a byte-per-sample estimate needs | `:109-113`, `:786-813` |
| The gain: `NormalizationGain(loudnessDb, truePeakDb)` (−14 LUFS target, −1 dBTP headroom) is used ONLY on the FLAC branch of `Choose`; for Ogg `GainDb` is 0 today. The Spotify header carries the track gain at byte 144 (`HeadGainDb(head) = BitConverter.ToSingle(head[144..148])`, read from the clear head file) — the same offset librespot reads: `SPOTIFY_NORMALIZATION_HEADER_START_OFFSET = 144`, 16 bytes = `track_gain_db, track_peak, album_gain_db, album_peak`, four little-endian f32 (`C:\WAVEE\librespot\playback\src\player.rs:351-381`) | `:226-233`, `:245-258`, `:883-885` |
| `Ctr.Validates` checks `OggS` at `0xa7` (or `fLaC` at 0) on a self-decrypted prefix — the key check IS an Ogg sync check | `:579-585` |
| The head file: `Head(fileIdHex)` fetches up to `HeadMaxBytes` (80 KiB) of CLEAR bytes from `HeadHost`; today it exists "for Wave 3 to use or not" and nothing uses it | `:860-881`, FLAC plan §1.1 |
| The CDN client: one static `HttpClient` over `SocketsHttpHandler { PooledConnectionLifetime = 5 min, ConnectTimeout = 10 s, MaxConnectionsPerServer = 4 }`, 20 s timeout; mirrors are retried in order and invalidated as a set | `:745-752`, `:682-700` |

**Consequences.** (a) The 0xa7 header is invisible to the decoder (`Prefetching.SetSkip`), but the pump has the
clear head bytes and hands the decoder `GainDb` from byte 144 when the catalogue's `GainDb` is 0 (§6.3). (b) A
range request is the unit of cost: the stream layer decides its size (§5.2), not the codec. (c) `CtrStream` is
replaced, not patched: its one-chunk shape cannot express a ring, a cache or a probe (§5).

### 1.3 The engine contract, the parts Vorbis touches

FLAC plan §1.2 is the full table (`IAudioDecoder`, `IMediaByteSource`, `MixFormat`, the resampler at the decode
edge, WASAPI shared only, the tripwire); it is not repeated. The Vorbis-specific facts:

| Question | Answer | Where |
|---|---|---|
| Codec identity | `CodecId.Vorbis`, `Container.Ogg` exist; `Capabilities.IsSupported` lists Vorbis | `MediaTypes.cs:108-111`; `PcmAudioPlayer.cs:168-170` |
| Gapless | `GaplessInfo(LeadInFrames, TrailPadFrames, ExactFrames, TailKnown)` in MIX-domain frames, "populated from container side-metadata (… Vorbis granulepos) — NEVER a hardcoded constant"; `BuildTrimmedVoice` wraps the voice in `TrimmingSource` when any field is non-trivial and `totalFrames = ExactFrames` | `MediaSeams.cs:287-291`; `PcmAudioPlayer.cs:151-165` |
| Total length | `MixFrames` derives a pluggable decoder's length from the DECLARED duration unless `Gapless.ExactFrames` is set — the last page's granulepos is what makes the butt-join sample-exact | `PcmAudioPlayer.cs:141-143` |
| The byte seam's own words | `IMediaByteSource.Seek`: "re-opens a Range under the hood for HTTP; may FAIL"; `Read`: "&gt;0 bytes (short reads legal), 0 = EOF, &lt;0 = error — the decoder ALWAYS loops"; `Cancel()`: "cross-thread, NON-BLOCKING abort of an in-flight expensive read (keeps scrub/stop responsive)"; `Length` nullable | `MediaSeams.cs:144-160` |
| Where the decoder runs | `FluentGpu.AudioProducer` per voice, decode-ahead 500 ms, ring 1 s: a `Read` that blocks longer than ~1 s of audio is an xrun | FLAC plan §1.2 (`AudioFeedThread.cs:476-562`, `RingAudioSource.cs`) |

### 1.4 Why NVorbis is slow — measured from its source (`src/apps/vendor/NVorbis/NVorbis/**`, 8,270 lines)

The vendored copy is not stock NVorbis: `Ogg/StreamPageReader.cs` (982 lines) carries a byte-position bisection,
a sparse page index and three rounds of seek clamps ("Phase 3/4", "CDN", "LazyProgressiveDownloader" in its
comments) — it has already been patched toward a ranged stream twice. What remains, per packet and per seek, with
file:line into that directory:

**Per packet (one Vorbis packet ≈ 2,048 or 256 samples per channel):**

| What | Where | Cost |
|---|---|---|
| `IPacketProvider.GetNextPacket()`; `IMode.Decode` → `IMapping.DecodePacket` → `IFloor.Unpack` × ch → `IResidue.Decode` → `IMdct.Reverse` × ch | `StreamDecoder.cs:544, 580`; `Mode.cs:157`; `Mapping.cs:104, 133, 190` | 6+ interface dispatches per packet before any sample work |
| **Every Huffman symbol and every VQ value goes through `ICodebook`** (`DecodeScalar`, the indexer) — the innermost loop is virtual | `Residue0.cs:140, 155, 164, 188, 197`; `Codebook.cs:294-322` | interface call per symbol |
| The bit reader: `IPacket.ReadBits` = `TryPeekBits` + `SkipBits` (two interface calls), the 64-bit bucket refilled **one byte at a time** through `abstract ReadNextByte()` (`Ogg.Packet.ReadNextByte` → `Memory<byte>.Span[i]`, and `IPacketReader.GetPacketData` at every segment boundary) | `DataPacket.cs:150-205`; `Ogg/Packet.cs:32-53` | 2 interface calls per field + 1 virtual call per byte |
| `Mapping.DecodePacket`: `new IFloorData[ch]`, `new bool[ch]` | `Mapping.cs:100-101` | 2 arrays / packet |
| `Floor1.Unpack`: `new Data()` with an embedded `int[64] Posts`; `UnwrapPosts`: `new bool[64]`, `new int[64]` | `Floor1.cs:12, 135-137, 224-230` | 1 object + 3 arrays / channel / packet |
| `Residue0.Decode`: `new int[_channels, partitionWords][]`; `WriteVectors`: `new int[steps]` **per call**, once per (channel × partition × pass) | `Residue0.cs:130, 184` | dozens of arrays / packet |
| `Mdct.CalcReverse`: `var buf2 = new float[_n2]` per channel per packet; the twiddles cached in a `Dictionary<int, MdctImpl>` looked up per call; scalar throughout | `Mdct.cs:11-21, 69` | 1 array / channel / packet + a hash lookup |
| `_stats.AddPacket` takes `lock(_lock)` per packet | `StreamDecoder.cs:498, 527`; `StreamStats.cs:96` | a Monitor per packet |
| Window multiply, overlap-add, copy-out (`ClippingCopyBuffer`) are three per-sample × per-channel scalar loops with a `Utils.ClipValue` call and a patched-in bounds check per sample; channels are `float[][]` swapped between `_prevPacketBuf`/`_nextPacketBuf` | `Mode.cs:160-166`; `StreamDecoder.cs:452-489, 606-615, 529-535` | no SIMD anywhere in the tree |

**Per page:** `PageReaderBase.VerifyPage` allocates `pageBuf = new byte[dataLen + segCnt + 27]` **for every page
read** (`PageReaderBase.cs:98`), `PageReader.ReadPackets` a `new Memory<byte>[packetCount]` per page
(`PageReader.cs:66-93`), `ReadPageAt` a `new byte[282]` per call (`:173`); each page costs ≥ 1 `Stream.Seek` + ≥ 2
`Stream.Read` (header, then body; `EnsureRead` retries up to 10 zero reads) (`PageReaderBase.cs:229-347`); the CRC
is byte-at-a-time (`Ogg/Crc.cs:34-37`); every page touch takes `Monitor.Enter(_readLock)` (`PageReader.cs:95-113`).
The page index — `_pageOffsets`, `_pageGranulePositions`, `_pageOffsetToIndex` and three checkpoint lists — is
**append-only for the life of the reader and never trimmed** (`StreamPageReader.cs:17-24, 120-137`) and by its own
comment "neither granule- nor offset-monotonic" after bisected seeks (`:730-741`), which is why a backward seek does
"a linear scan over the WHOLE index on purpose" (`:752-766`).

**Per seek** (`VorbisReader.SeekTo` → `StreamDecoder.SeekTo:636-787` → `PacketProvider.SeekTo:68-124` →
`StreamPageReader.FindPage:169-240`): forward into unread bytes → `FindPageForwardByteBisection` (`:335-510`, up to
16 hops, bail-outs when no stream length or `stagnantHops >= 4`) then `WalkToPageContaining` (1-3 page reads), else
the fallback `FindPageForward` — **an O(pages) sequential walk** (`:255-326`). Then `PacketProvider.FindPacket`
reads the PREVIOUS page's last packet and parses every packet header on the target page (`:126-198, 284-322`), and
`StreamDecoder` fully decodes the pre-roll packet and the target packet (`:692, 716`) before skipping
`rollForward` samples with three defensive clamps (`:739-779`). Count: best case **3-6 page probes ≈ 6-24
`Stream.Read` + 3-6 `Stream.Seek`** on a 10 MB file, each `Stream.Seek` outside the held 128 KiB chunk a
synchronous range GET; worst case hundreds. And `ResetDecoder` nulls `_nextPacketBuf`, so the first packet after
every seek reallocates `float[ch][]` + `ch × float[block1]` (`:305-315, 572-579`).

**The expected wins, as ratios with the reasoning (§7 pins them in tests; nothing here is a measurement):**

| Path | Today | Plan | Ratio and why |
|---|---|---|---|
| Huffman symbol | 2 interface calls + table probe + `SkipBits` interface call | one `Peek(10)` on a 64-bit cache, one table load, one `Consume` — all inlined, `unsafe` | ≥ 5× fewer instructions per symbol; ~40-60 k symbols/s at 320 kbps, the decoder's inner loop |
| Bit refill | 1 virtual call per byte | 1 `ReadUInt64LittleEndian` per 7-8 bytes | ~8× fewer calls |
| Residue 2 | per-call `new int[steps]` + `ICodebook` indexer per value | flat `float[]` VQ table, pointer add into the interleaved spectrum | zero allocs; one load + one add per value |
| IMDCT | scalar, `new float[n/2]` per channel per packet | in place on two pinned scratch halves, `Vector128`/`Vector256` butterflies | 4-8 lanes in the butterfly stages; zero allocs |
| Window / overlap / copy-out | 3 scalar per-sample passes over `float[][]` | one fused vector pass: `prev_tail·w′ + cur_head·w`, interleave + gain | 3 passes → 1, 4-8 lanes |
| Per-packet GC | ~10-60 arrays / packet ≈ 300-2,000 allocations/s | 0 | no gen-0 GCs from the decoder |
| Seek, typical | 3-6 probes, each outside the chunk a synchronous range GET on the decode thread, + 2 full packet decodes | 1 range window on the network thread + 1-2 packet decodes (§4.5) | 3-6× fewer requests; no post-seek reallocation |
| Seek, worst | O(pages) forward walk | ≤ 8 probes then linear within one window | bounded |

### 1.5 How 0.2.9 streamed and seeked (`src/apps/_old/Wavee/**`, `src/apps/Wavee.Sdk/Streams/**`, reference only)

| Fact | Where |
|---|---|
| The chain: UI `Seek(ms)` → the serialized pump → `SeekGate.Decide` (defer while the body is not attached) → `PcmAudioSession.SeekAsync(…, Accurate)` (engine) → `SpotifyEngineAudioDecoder.Seek(frame)` → `VorbisSampleSource.SeekTo(TimeSpan)` → `NVorbis.VorbisReader.SeekTo` | `SpotifyLive/Audio/FluentMediaAudioHost.cs:590-632, 259-268`; `SampleSource.cs:21-30` |
| **The seek Christos felt**: "the 0.2.9 engine's `PcmAudioSession.SeekAsync` holds its replacement gate until the decode producer has PCM at the target … a target beyond [the clear head] makes the decoder block in `SpotifyAudioStream.WaitForBody`" — a seek waited for NVorbis's page probes, each a blocking range fetch, before the UI could move on; `SeekGate` / `_pendingSeekMs` exist to keep that from deadlocking the pump. No stopwatch ever measured it | `SpotifyLive/Audio/SeekGate.cs:14-21`; `FluentMediaAudioHost.cs:703-721` |
| The range layer: `RangedHttpSource` keeps every fetched 64 KiB chunk in a `Dictionary<int, byte[]>` **with no bound and no LRU** for the life of the stream (a scrub-and-return hits it), `MinFetchBytes = 64 KiB`, read-ahead `ReadAheadPolicy.Compute(measuredBytesPerSec, bitrate, metered, cap)` = 15 s metered / 30 s near-realtime / 600 s once throughput ≥ 3× bitrate, floor 256 KiB, under a 24 MiB cap shared with the prepared next track; `Seek` is a position write, the next `Read` fetches synchronously; a `TryRead(wouldBlock)` path for the decode thread requests an async prefetch instead of blocking | `Wavee.Sdk/Streams/RangedHttpSource.cs:14-61, 182-211, 488-498, 774-794`; `Backend/Audio/SpotifyAudioStream.cs:292-338, 465-477` |
| `PrefetchingReadStream`: no buffer of its own; the 4 ms / 8 s bounded wait and the never-return-zero rule Wave 3's `Prefetching` inherited verbatim; bitrate hints 96/160/320 kbit/s per rung, FLAC 1.0/1.8 Mbit/s | `Backend/Audio/PrefetchingReadStream.cs:16-73, 112-123` |
| The 0xa7 skip was decided by validating a REAL Vorbis identification page at 0 or at 0xa7 (`HasVorbisHeaderAt`: `OggS`, lacing sum ≥ 7, packet type 1, "vorbis") — the shape §4.1's sniff keeps | `FluentMediaAudioHost.cs:2043-2067` |
| The gain was never read from the header; it came from the catalogue as `NormalizationGainDb` and was applied per sample AFTER resampling in a scalar loop | `FluentMediaAudioHost.cs:229-233, 2041` |
| The head file, the body disk cache and the next-track prepare existed (`HeadFileClient`, `AudioBodyDiskCache`, `ChunkDiskCache`, `PrepareNextCoreAsync`) — §5.1 has their facts | `SpotifyLive/Audio/HeadFileClient.cs`; `Backend/Audio/AudioBodyDiskCache.cs`; `Wavee.Sdk/Streams/ChunkDiskCache.cs` |

Net: 0.2.9's seek cost was NVorbis's probe count × a blocking fetch each, felt as the UI waiting on the pump.
Wave 3 already removed the pump wait (the reducer's `Seek` is an effect; the position clock rebases at once);
this plan removes the probe count (§4) and takes the fetch off the decode thread (§5).

### 1.6 The formats and the Spotify subset

**RFC 3533 (Ogg encapsulation, https://www.rfc-editor.org/rfc/rfc3533.txt).** §6 page header: `OggS` (4) |
version 0 (1) | header type (1: `0x01` continued packet, `0x02` BOS, `0x04` EOS) | granule position (8, LE, signed;
**−1 = no packet ends on this page**) | serial (4) | page sequence (4) | CRC-32 (4) | segment count (1) | lacing
table (count bytes). §5 lacing: "a lacing value of 255 implies that a second lacing value follows in the packet,
and a value of less than 255 marks the end of the packet after that many additional bytes. A packet of 255 bytes
(or a multiple of 255 bytes) is terminated by a lacing value of 0." §6: "Pages are of variable size, usually 4-8
kB, maximum 65307 bytes" (27 + 255 + 255 × 255). CRC: generator `0x04c11db7`, direct (non-reflected), init 0, no
final xor, computed with the CRC field zeroed (libogg `framing.c:110-113, 237-253`: "unreflected alg and an
init/final of 0, not 0xffffffff"). §3-4: the granule position is the "position landmark for direct random access";
"Ogg does not have a concept of 'time' … an application can only get temporal information through … the codec" —
seeking is the application's bisection over granule positions, which is what libvorbisfile's `ov_pcm_seek_page`
and stb's `seek_to_sample_coarse` implement (§1.7).

**Vorbis I (https://xiph.org/vorbis/doc/Vorbis_I_spec.html).** §2 bitpacking is **LSB-first** ("the LSb of a
binary integer to the logical bitstream first") — the opposite of FLAC, so the bit reader is a right-shifting
cache (§3.3). §1.3.2 + §4.3.8: the decoder returns `blocksize(prev)/4 + blocksize(cur)/4` samples per packet after
overlap-add and **"data is not returned from the first frame; it must be used to 'prime' the decode engine"** — the
rule every seek resume obeys (§4.4). §4.2.2 identification header: version 0, channels, rate, three bitrates,
`blocksize_0 ≤ blocksize_1` as exponents 6..13, framing bit. §4.2.4 setup order: codebooks, time transforms
(all 0), floors, residues, mappings, modes, framing bit. §3.2 codebooks: sync `0x564342`, dimensions (16), entries
(24), ordered/unordered lengths (5 bits + 1; sparse flag), canonical assignment ("the lowest valued unused binary
Huffman codeword possible" in entry order), lookup 0/1/2 with `float32_unpack` and the lattice (`index_divisor`)
or explicit multiplicands. §4.3: mode number in `ilog(modes − 1)` bits, long windows carry
`previous_window_flag` / `next_window_flag`, window `y = sin(π/2 · sin²((x + 0.5)/n · π))` with the four
left/right boundaries in §4.3.1, floor then residue, "do not decode" for a channel whose floor is unused, coupling
inverse (§4.3.5: magnitude/angle → `M ± A` with the sign fold), floor × residue, IMDCT, overlap-add. §7.2 floor 1:
partitions/classes/subclasses/masterbooks/books, multiplier ∈ {1,2,3,4} → range {256,128,86,64}, `ilog(range−1)`
bits for Y[0..1], `low/high_neighbor`, `render_point`, the step-2 room/sign fold, integer `render_line`, the
256-entry inverse dB table (`1.0649863e-07 … 0.9389798, 1.0`: a 140 dB range in 0.5468 dB steps; libvorbis
`floor1.c:280`, stb `stb_vorbis.c:1946-2012`). §8.6.2 residue: begin/end/partition_size/classifications/
classbook/cascade; classifications decoded `classbook.dimensions` at a time; up to 8 passes; **type 2 interleaves all
channels into one vector** (`vector[offset + j·ch + c]`). §A.2 Ogg mapping: the identification header alone on the
BOS page; the comment + setup headers on their own page(s) before any audio; "the granule position of a page is
the PCM sample position of the last completed packet in the page"; **the last page's granule position may be less
than the decoded total to truncate the end**; a packet's first sample = the page's granule minus the samples of the
packets after it on that page.

**Spotify's Ogg subset.** The wire enum has `OGG_VORBIS_96/160/320 = 0/1/2`, MP3 rungs, `AAC_24/48`, `FLAC_FLAC =
16`, `XHE_AAC_*`, `FLAC_FLAC_24BIT = 22` — **and no Opus** (`C:\WAVEE\librespot\protocol\proto\metadata.proto:297-
315`; Wave 2's `Format` enum mirrors it, `Spotify.Audio.cs:74-79`). Opus exists at Spotify only for the web player
and Cast at 64 kbit/s (Wikipedia "Vorbis"; Spotify community "Audio Formats"), never for a desktop client, and no
consumer in Wavee asks for it. **Opus is out of scope** and stays out until a real consumer exists.
The files are libvorbis output at 44.1 kHz stereo (librespot hard-locks 44.1 kHz, FLAC plan §1.5); libvorbis's 44.1
kHz modes use **short 256 / long 2048** blocks at every quality above the lowest
(`C:\WAVEE\libvorbis\lib\modes\setup_44.h:30-35`, `blocksize_short_44 = {512,256,…}`, `blocksize_long_44 =
{4096,2048,…}`), **floor 1 only** (`modes/floor_all.h:165` declares `vorbis_info_floor1 _floor[11]`; no
`vorbis_info_floor0` in any mode header), and **residue 2** for coupled stereo (`modes/residue_44.h:179-252`, every
`{2,0,…}` entry is a residue type 2 setup). Floor 0 and residues 0/1 are implemented for completeness (a local
`.ogg` can be anything; stb refuses floor 0 and is the one reference that does) but the fast paths are floor 1 +
residue 2 with 256/2048 blocks.

**The 0xa7 header.** Bytes 0..0xa6 before page 0; the normalization block at 144 (four LE f32: track gain dB,
track peak, album gain dB, album peak — librespot `player.rs:351-381`; Wave 2 `HeadGainDb`). Nothing else in the
header is consumed by any open client.

**Measured page profile (this machine, ffmpeg 8.1.2 + libvorbis, scratchpad; the numbers §4.5 uses).** Four
30-60 s files, 44.1 kHz stereo: pink noise at 96/160/320 kbit/s, a −60 dB/0 dB alternating VBR file at q8, and a
100 ms-page variant. ffmpeg's muxer flushes a page per ~1 s (`-page_duration` default), oggenc/libvorbis per ~4-8
KB; both are legal and the design reads up to one max page (65,307 B) past any probe, so either works:

| File | Bytes | Pages | Page size min/med/max | Packets/page | Granules/page | Linear-estimate landing error (10..90 %, s; + = late) |
|---|--:|--:|---|--:|--:|---|
| pink 96 k | 271,824 | 32 | 3,429 / 9,113 / 9,203 | 44 | 45,056 | +0.45 … +1.41 (always inside one page) |
| pink 320 k | 861,711 | 32 | 10,944 / 29,167 / 29,364 | 44 | 45,056 | +0.45 … +1.41 |
| VBR q8 (−60/0 dB, 10 s cycles) | 1,626,008 | 61 | 15,288 / 26,640 / 34,714 | 44 | 45,056 | **−1.40 … +1.41** |
| pink 320 k, 100 ms pages | 867,894 | 261 | 1,700 / 3,338 / 3,427 | 5 | 5,120 | +0.04 … +0.17 |

The linear estimate lands within ±1.5 s even on the strongly VBR file, i.e. inside one 128 KiB chunk at 320 kbit/s
(≈ 3.3 s) and inside two at 96 kbit/s — which is why §4.5's typical seek is one request and the bisection is a
fallback, not the path. Real music is less extreme than a −60/0 dB square wave of noise but longer (3-5 min), so
the error scales: the planner reads the estimate with a back-off and uses the page index on the second seek.

### 1.7 The reference decoders on disk — what each settles

Cloned for this plan under `C:\WAVEE`: `stb` (nothings/stb, `stb_vorbis.c` v1.22, MIT/public domain — the
"small and fast" single file everyone ships), `lewton` (RustAudio, pure Rust, the safe reference whose `imdct.rs`
is "a very close translation of … stb_vorbis"), `libvorbis` (xiph reference: `mdct.c`, `block.c`, `floor1.c`,
`res0.c`, `codebook.c`, `sharedbook.c`, `window.c`, `vorbisfile.c`), `tremor` (gitlab.xiph.org, the integer
reference), `minivorbis` (edubart: libogg + libvorbis amalgamated into one header — useful only as a "this is how
big the reference really is" yardstick, 100 k lines), `libogg` (`framing.c`). `Symphonia` was already at
`C:\WAVEE\Symphonia` (`symphonia-codec-vorbis`, `symphonia-format-ogg`). Line ranges are into those checkouts; the
§3/§4 section that transliterates each is in the last column.

| Reference | What it settles | § |
|---|---|---|
| stb `compute_accelerated_huffman` `:1134-1154` — a `1 << FAST_HUFFMAN_LENGTH` (default 10, max 24) table filled by stepping `z += 1 << len` over every codeword of length ≤ 10, bit-reversed for LSB-first; `DECODE_RAW` `:1717-1729` — `acc & MASK` → table → if hit, `acc >>= len`, else `codebook_decode_scalar_raw` `:1656-1713` (binary search over bit-reversed sorted codewords) | the fast Huffman path: one mask, one load, one shift; the slow path a binary search, never a tree walk | 3.4 |
| libvorbis `codebook.c:310-372` `decode_packed_entry_number` — `oggpack_look(firsttablen)` then a sorted binary search; `sharedbook.c:79-163` `_make_words` — the canonical codeword synthesis with bit reversal; Symphonia `codebook.rs:113-210` `synthesize_codewords` (same algorithm), `:380-385` builds a VLC over 4-8 bit reads | the codeword builder; the "first table then binary search" shape is universal | 3.4 |
| stb `acc`/`valid_bits` `:884-885` is a **32-bit** accumulator refilled a byte at a time (`prep_huffman` `:1634-1647`); lewton `BitpackCursor` reads bit-by-bit; Symphonia's `BitReaderRtl` is a 64-bit cache | none of the references has a 64-bit, 8-bytes-per-refill LSB-first reader; §3.3 is Symphonia's cache width with stb's `DECODE_RAW` peek | 3.3 |
| stb `inverse_mdct` `:2629-2929` — "IMDCT algorithm from 'The use of multirate filter banks for coding of high quality digital audio'", eight stages, twiddles `A/B/C` + `bit_reverse` per block size (`:1254-1301`), `imdct_step3_iter0_loop` `:2408-2451`, `imdct_step3_inner_r_loop` `:2453-2501`, `imdct_step3_inner_s_loop` `:2503-2552` (twiddles cached in registers), `iter_54` / `_ld654` `:2554-2627` (the last three stages fused), the final step 8 writes the output from both ends by symmetry `:2867-2925`; lewton `imdct.rs:14-659` is the same, line for line | the IMDCT structure: the step-3 loops are 4-wide independent butterflies over contiguous floats — exactly a `Vector128` lane group — and the r/s loop swap is the reason each stage vectorizes | 3.7 |
| libvorbis `mdct.c:316-336` `mdct_butterflies` → `_first` / `_generic` / `_32` / `_16` / `_8`, `mdct_bitreverse` `:346-394`, `mdct_backward` `:396-490` (rotate → butterflies → bitreverse → rotate+window); Tremor `mdct.c` the same in Q31 with `MULT31`/`XNPROD31` | the alternative (split radix to hard-coded 8/16/32 kernels); rejected for the port because stb's stages are flat loops the JIT vectorizes as written, and Tremor's fixed point buys nothing on a machine with SIMD floats | 3.7 |
| Symphonia `symphonia-core/src/dsp/mdct.rs:16-147` — an N/2 complex FFT with pre/post twiddles (Duhamel); state `scratch` + `twiddle` per block size | the third shape; correct, more state, an FFT to write — not taken | 3.7 |
| libvorbis `window.c:2087-2135` — 8 baked `vwin` tables of `n/2` floats, `_vorbis_apply_window` zeroes the unwindowed middle; stb `compute_window` `:1271-1276` computes once at setup; Symphonia `window.rs:11-39` | the window is computed once per block size at open (`MathF.Sin` twice per sample, 1,152 sines total for 256 + 2048) — no runtime trig | 3.8 |
| libvorbis `block.c:731-944` `vorbis_synthesis_blockin` — `pcm_returned == −1` = "no output yet" (`:831-843`), the four `lW/W` overlap cases (`:781-823`), the EOS trim `extra = granulepos − expected` clamped to what is buffered (`:864-936`) | the first-block rule and the end trim | 3.8, 4.4 |
| stb `vorbis_finish_frame` `:3456-3507` — `if (!prev) return 0` (the primed frame yields nothing), the overlap `cur[left+j]·w[j] + prev[j]·w[n−1−j]`; `vorbis_pump_first_frame` `:3509-3516`; `first_decode` `:3382-3392` (`current_loc = −n2`) | the priming decode after every seek and at open | 3.8, 4.4 |
| stb `vorbis_decode_packet_rest` `:3330-3376` — coupling inverse from the last step backwards, the deferred floor multiply, the IMDCT dispatch; `decode_residue` `:2104-2284` with `codebook_decode_deinterleave_repeat` `:1865-1933` for type 2 (`ch != 1`), `part_classdata` per (channel, partition); `predict_point` `:1935-1943`, `draw_line` with `LINE_OP` `:2022-2081`, `do_floor` `:3072-3108` | the packet decode order and the residue-2 fast path: decode the interleaved vector straight into the channel spectra, no deinterleave pass | 3.5, 3.6, 3.9 |
| libvorbis `res0.c:780-782, 812-864` `res2_inverse` — "all the channels are interleaved into a single vector and encoded", `vorbis_book_decodevv_add` `codebook.c:472-495` adds VQ values round-robin into `ch` vectors; Symphonia `residue.rs:220-308` decodes into one `type2_buf` then `deinterleave_2` | the same; Symphonia's extra deinterleave pass is what §3.6 avoids | 3.6 |
| libvorbis `floor1.c:347-374` `render_line`, `:955-1080` `floor1_inverse1/2`, `FLOOR1_fromdB_LOOKUP` `:280`; Symphonia `floor.rs:568-653` `synthesis_step1/2` | floor 1 in two steps: unwrap posts (integer), render the curve (integer Bresenham) and multiply — and the curve multiply is where `do_not_decode` channels are skipped | 3.5 |
| stb `seek_to_sample_coarse` `:4700-4852` — probe 0 by `bytes_per_sample × (target − left.last_decoded_sample)`, probe 1 corrected by the observed error clamped to ±8,000 B, then plain bisection `left.page_end + delta/2 − 32768`, **stop bisecting at `delta ≤ 65536` and scan linearly** ("finding the synchronization pattern can be expensive", `:4634-4637`); `vorbis_find_page` `:4561-4629` (byte scan for `OggS` + CRC); `get_seek_page_info` `:4643-4671`; then discard packets to `start_seg_with_known_loc` and `vorbis_pump_first_frame` `:4803-4842`; `stb_vorbis_seek_frame` `:4880-4917` walks packets with `peek_decode_initial` | the bracket-and-scan structure and the two-probe interpolation | 4.2 |
| libvorbisfile `vorbisfile.c:63` `CHUNKSIZE 65536`; `ov_pcm_seek_page` `:1406-1663` — `bisect = begin + (target − begintime)·(end − begin)/(endtime − begintime) − CHUNKSIZE`, `if (end − begin < CHUNKSIZE) bisect = begin`, then the linear page scan; `ov_pcm_seek` `:1685-1781` — `vorbis_synthesis_trackonly` + `blockin` to advance without output, then discard the remainder | the interpolation with a one-chunk back-off, and "track only" priming | 4.2, 4.4 |
| Symphonia `demuxer.rs:163-303` `do_seek` — bisect by byte while `end − start > 2 × OGG_PAGE_MAX_SIZE`, `inspect_page` per probe (no decode), reset every logical stream, consume packets to the timestamp; `page.rs:15-17` `OGG_PAGE_MAX_SIZE = 65307`; `lib.rs:317-326` "the first packet after a decoder reset is silenced" via `prev_block_flag: None` | the page-inspect probe (a probe parses one header, decodes nothing) and the reset sentinel | 4.2, 4.4 |
| stb `stb_vorbis_decode_frame_pushdata` `:4444-4512` — a caller hands an arbitrary byte window; `is_whole_packet_present` false ⇒ "0 bytes used / 0 samples" = need more; `vorbis_search_for_page_pushdata` `:4353-4441` CRC-checks page candidates in the window; `stb_vorbis_flush_pushdata` `:4341-4351` resets `previous_length` after a seek | the pushdata contract IS §3.9's `DecodePacket(window, out consumed)` + §4.1's `NextPage(window)` — the SHELL owns the window, the CORE never blocks | 3.9, 4.1 |
| stb header `:96-122` — "malloc() at startup … alloca() … during a frame … maximal-size usage is ~150KB"; `start_decoder` `:4131-4141` per channel `channel_buffers[block1]`, `previous_window[block1/2]`, `finalY[longest_floorlist]`; `temp_memory_required = max(classify_mem, imdct_mem)` `:4155-4191` | the allocation plan: everything sized at setup, one scratch for the residue classification and the IMDCT half-buffer | 3.10 |
| libogg `framing.c:642-729` `ogg_sync_pageseek` (≥ 27 bytes, sum the lacing table, verify the CRC with the field zeroed, return −n/0/n), `:775-901` `ogg_stream_pagein` (continued pages, the `0x400` hole marker), `:944-996` `_packetout` (`while (size == 255)` gathers the packet across segments and pages) | the page parser and the spanning-packet rule | 4.1 |
| lewton `huffman_tree.rs:162-339` — an 8-bit unrolled table plus a bit-by-bit tree walk on a miss; `inside_ogg.rs:307-313` seeks at page granularity via the `ogg` crate and resets `PreviousWindowRight` | the safe-but-slow reference: a tree walk per long code and a `Vec` per packet — what not to do | 3.4 |

---

## 2. The decoder — three options, one recommendation

| | (a) from-scratch CORE decoder, `Playback.Audio.Vorbis.cs` + `Playback.Audio.Ogg.cs` | (b) keep the vendored NVorbis fork and patch it again | (c) P/Invoke `stb_vorbis` (or libvorbis) as a native library |
|---|---|---|---|
| Managed / AOT | Pure C#, `unsafe` over pinned arrays, no P/Invoke, no reflection; one assembly on both arches | Managed, AOT-safe, already builds | Two native binaries (x64, arm64) in the MSIX, a C toolchain in the release script, symbolication for a second language; the Trusted Signing step signs them too |
| Allocation per packet | **Zero after `Open`** (§3.10): every buffer sized from the setup header, POH-pinned once | 10-60 arrays per packet plus a `byte[]` per page, a `float[][]` reallocation after every seek (§1.4); the fork has been patched three times and the allocations are structural (`IFloorData`, `new Data()`, `WriteVectors`) | stb's own `alloca` per frame; zero managed allocation — but every `Read` marshals a buffer across the boundary |
| Inner loop | one 64-bit cache peek + one table load per symbol, `Vector128`/`Vector256` IMDCT and stream kernels | an interface call per Huffman symbol, per VQ value and per bit field; no SIMD anywhere | stb's `DECODE_RAW` — the model for (a); scalar IMDCT (no SIMD in stb) |
| Seek on a range stream | the planner is written for it: one probe window, an index, `ReadAt` (§4) | byte bisection exists but falls to an O(pages) walk whenever the bracket stalls; 6-24 `Stream.Read` best case | `stb_vorbis_seek` assumes a `FILE*`/memory; the pushdata API needs the caller to do the bisection anyway (§1.7) — i.e. (a)'s Ogg layer would still be written |
| Gapless truth | `GaplessInfo` from the granule positions (§4.4) | `None` today; the trim is internal and undocumented ("uncertain", `gapless-findings.md:174`) | stb ignores the leading truncation ("lossless sample-truncation at beginning ignored", `stb_vorbis.c:17`) |
| arm64 | Same code; `Vector128` = NEON | Same code | A second native build to verify |
| Testable (D17) | Pure over spans: fixtures + an s16 reference + the sine oracle + the allocation and request-count gates (§7) | Testable as a black box | Needs the native library on the test host |
| Cost | ≈ 2,200 lines CORE (Ogg 600, Vorbis 1,600) + 500 tests | 0 lines, permanent debt, and every seek defect so far was found in it | ≈ 400 lines app + the toolchain + the packaging |
| Precedent | stb_vorbis (5.6 k lines of C incl. comments), lewton (a from-scratch pure port), Symphonia (pure Rust) — the format is small: the spec's decode half is ~40 pages | — | go-librespot uses cgo libvorbis/libFLAC |

**Recommendation: (a).** The whole reason Christos names seeking is that the seek lives in the Ogg layer, and that
layer must be written for a range stream regardless of which Vorbis core sits under it; once the Ogg layer is ours
the Vorbis core is 1,600 lines that stb, lewton and Symphonia have each written from the same spec. (b) cannot
reach zero allocations without a rewrite of its object model. (c) adds a native toolchain to a release process
that has none, and would still need (a)'s Ogg layer.

---

## 3. The decoder — `Playback/Playback.Audio.Vorbis.cs` (CORE, new named partial)

Role CORE, owner **V** (new; §8), Wave **3-parallel** (can start today), budget **1,600**. `public static partial
class Playback { public static unsafe class Vorbis { … } }` — the same nesting as `Flac`. Everything is `static`
or a `struct`; the only class is `Vorbis.Decoder`, constructed once per open and reused across seeks and, by the
SHELL, across tracks of the same shape. No `Stream`, no `IMediaByteSource`, no engine type: the decoder is handed
one packet as a `ReadOnlySpan<byte>` and writes interleaved `float` samples into a buffer it owns. The Ogg layer
(§4) is the same shape one level down: it is handed a byte window and answers pages and packets.

**The rules this file is written under, beyond P8/P9/P15:**

- Every buffer the hot loops touch is allocated ONCE in `Open` with `GC.AllocateUninitializedArray<T>(n, pinned:
  true)` (the pinned object heap: no `GCHandle`, no `fixed` per call, addresses stable for the life of the decoder)
  and its `T*` is taken once into a `Tables` struct of pointers. The hot loops receive pointers, never arrays.
- `[SkipLocalsInit]` on every method with a `stackalloc`; `[MethodImpl(MethodImplOptions.AggressiveInlining)]` on
  the leaf readers (`Peek`, `Consume`, `Refill`, `DecodeScalar`); the codebook/residue/IMDCT/window loops are
  `unsafe` pointer loops with **no bounds checks**: each loop states, in a comment at its top, the invariant that
  bounds it (a table sized `1 << FastBits` indexed by `Peek(FastBits)`; a residue vector whose `begin/end` were
  clamped to `ch × n/2` at open; an IMDCT whose pointers are derived from `n` and the tables sized from `n`).
- SIMD: `Vector256` when `Vector256.IsHardwareAccelerated` (x64 with AVX2), a `Vector128` path always (NEON on
  arm64, SSE elsewhere), a scalar tail. Rule for where: the IMDCT butterflies use `Vector128` because the
  algorithm's unit is a group of four floats (stb unrolls every step-3 loop 4-wide for that reason — §1.7) and NEON
  has no 256-bit register; the four streaming kernels (window + overlap-add, coupling, floor multiply's constant
  tail, interleave + gain) use `Vector256` where accelerated because they are plain elementwise streams over
  ≥ 128 floats.
- No `double` anywhere on the packet path; `MathF.Sin`/`Cos`/`Exp` only in `Open` (stb: "called from setup only").
- No exceptions on the packet path: a malformed packet is a `PacketResult`; an overrun sets a flag and yields
  zeros (stb's `get_bits` past end-of-packet returns 0; the residue loop stops on the first symbol that cannot be
  read, the floor decodes what it has).

### 3.1 Stream → range window → page → packet → decoder → graph

```
   CDN (AES-128-CTR, 0xa7 header)          local .ogg                 module / external
   ─────────────────────────────          ──────────                 ─────────────────
   Spotify.Audio.Body (F, §5):             FileByteSource (engine)    Modules.Host stream (T)
   head file ∪ ring ∪ disk cache
             │                                   │                              │
             └───────────────┬───────────────────┴──────────────────────────────┘
                             ▼
                 IMediaByteSource  (engine seam)  ── the app's RingSource adds ReadAt(offset, span) (§5.5, §6.2)
                             │  blocking only in Read/ReadAt, on FluentGpu.AudioProducer (decode-ahead thread)
                             ▼
     ┌─────────────────────────────────────────────────────────────────────────────────────────────┐
     │  Playback.Audio.cs (SHELL, owner H)  —  VorbisAudioDecoder : IAudioDecoder                   │
     │                                                                                              │
     │   byte window  _win[]  (192 KiB; the tail kept across refills)   ← ReadAt / Read fills it    │
     │        │  ReadOnlySpan<byte> over [cursor .. filled)                                         │
     │        ▼                                                                                     │
     │   Playback.Ogg.Reader (CORE, §4)  ── NextPacket(window, ref cursor, out packet) ──▶ span     │
     │        pages parsed + CRC'd in place, packets zero-copy unless they span a page              │
     │        every page (offset, granule) → PageIndex                                              │
     │        │                                                                                     │
     │        ▼  packet span (≤ 64 KiB)                                                             │
     │   Playback.Vorbis.Decoder (CORE, §3)  ── DecodePacket(packet) ──▶ Output: float* interleaved │
     │        mode → floor posts → residue-2 into spectra → coupling → floor × → IMDCT → overlap    │
     │        gain folded into the interleave; the primed packet yields 0 samples                   │
     │        │                                                                                     │
     │        ▼  LinearResampler when 44.1 k ≠ device rate (engine, decode edge)                    │
     │   Read(Span<float> dst)  →  frames at MIX rate                                               │
     └─────────────────────────────────────────────────────────────────────────────────────────────┘
                             │
                             ▼
     DecoderAudioSource → [TrimmingSource: LeadIn / ExactFrames from granulepos] → RingAudioSource (1 s ring)
                             │
                             ▼   RT thread: FluentGpu.AudioRT (MMCSS Pro Audio), ~10 ms blocks
     CrossfadeMixer → master gain → master chain (EQ) → transport → TapBlock (level meter) → WASAPI shared
```

### 3.2 The stream, as the decoder sees it

```
 Ogg page (RFC 3533 §6):
 ┌──────┬───┬─────┬───────────────────┬────────┬────────┬────────┬─────┬──────────────┬─────────────────┐
 │"OggS"│ 0 │flags│ granule pos (i64) │ serial │ seq no │ CRC-32 │ nseg│ lacing[nseg] │ body (Σ lacing) │
 │  4   │ 1 │  1  │        8 LE       │  4 LE  │  4 LE  │  4 LE  │  1  │   nseg       │                 │
 └──────┴───┴─────┴───────────────────┴────────┴────────┴────────┴─────┴──────────────┴─────────────────┘
 flags: 0x01 body starts with the tail of a packet from the previous page, 0x02 BOS, 0x04 EOS
 lacing: 255 = the packet continues in the next segment (or the next page); < 255 = the packet ends here
 granule: sample position of the LAST packet that ENDS on this page; −1 when none ends

 Vorbis stream (spec §4, §A.2):   page 0 (BOS): [1 "vorbis" identification (30 B)]
                                  page 1..k:    [3 "vorbis" comment][5 "vorbis" setup (~4 KB, 40-60 codebooks)]
                                  page k+1..:   audio packets, ~4-8 KB (oggenc) or ~1 s (ffmpeg) per page

 audio packet (LSB-first bits):
 ┌─┬────────────┬──────┬──────┬─────────────────────────┬─────────────────────────────────────────────┐
 │0│ mode       │ prev │ next │ floor per channel        │ residue per submap (type 2: one vector for   │
 │ │ilog(m−1) b │ long │ long │ [nonzero][Y0][Y1][posts] │ all channels, up to 8 passes of VQ codewords)│
 └─┴────────────┴──────┴──────┴─────────────────────────┴─────────────────────────────────────────────┘
      │ blockflag → n = 256 or 2048        ──▶ spectra[ch][n/2] ──▶ coupling ──▶ × floor ──▶ IMDCT ──▶ n samples
      window boundaries (§4.3.1):  left  = prevShort ? [n/4 − n0/4, n/4 + n0/4) : [0, n/2)
                                   right = nextShort ? [3n/4 − n0/4, 3n/4 + n0/4) : [n/2, n)
      returned = right.start − left.start  (= prev/4 + cur/4);  the first packet after open/seek returns 0
```

### 3.3 The bit reader — LSB-first, a 64-bit cache, 8 bytes per refill

Vorbis packs LSB-first (§1.6), so the cache is right-aligned: the next bit to read is bit 0, `Peek(n)` is a mask,
`Consume(n)` a right shift. The refill is Fabian Giesen's "variant 4" (reading bits in far too many ways, part 2):
one unaligned 64-bit load OR'd in above the valid bits, the pointer advanced by `(63 − bits) >> 3` bytes, `bits |=
56` — branch-free, 7 bytes per refill, and always ≥ 56 valid bits afterwards so every field the spec allows
(≤ 32 bits) and every fast-Huffman peek (10 bits) is one `Peek` with no loop. The last 8 bytes of a packet take the
byte-at-a-time path once. Reading past the end yields zeros and sets `Overrun` (stb: `get_bits` returns 0 past
EOP; §3 rules).

```csharp
public static partial class Playback
{
    public static unsafe partial class Vorbis
    {
        /// <summary>LSB-first bit reader over a packet (Vorbis I §2). 64-bit right-aligned cache, refilled 7 bytes at a
        /// time with one unaligned load; ≥ 56 valid bits after every <see cref="Refill"/>, so any ≤ 32-bit field and
        /// any ≤ 24-bit Huffman peek is a mask. Never throws: past the end it yields zeros and sets <see cref="Overrun"/>.</summary>
        public ref struct BitReader
        {
            readonly byte* _end;      // one past the packet's last byte
            readonly byte* _end8;     // _end − 8: the last address a whole 64-bit load is legal at
            byte* _p;                 // next byte to load into the cache
            ulong _cache;             // bit 0 = the next bit to read
            int _bits;                // valid bits in _cache
            public bool Overrun;

            public BitReader(byte* packet, int length)
            {
                _p = packet; _end = packet + length; _end8 = _end - 8;
            }

            public readonly int Bits => _bits;

            /// <summary>Top the cache up to ≥ 56 bits. Giesen variant 4 while 8 bytes remain; byte-wise on the tail.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Refill()
            {
                if (_p <= _end8)
                {
                    _cache |= Unsafe.ReadUnaligned<ulong>(_p) << _bits;    // bits above 63 fall off; they are re-read next time
                    _p += (63 - _bits) >> 3;
                    _bits |= 56;
                    return;
                }
                while (_bits <= 56 && _p < _end) { _cache |= (ulong)*_p++ << _bits; _bits += 8; }
            }

            /// <summary>The next <paramref name="n"/> ≤ 24 bits without consuming them. The caller has refilled
            /// (<c>Bits ≥ n</c>) or accepts zeros past the end.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly uint Peek(int n) => (uint)_cache & ((1u << n) - 1);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Consume(int n)
            {
                _cache >>= n;
                _bits -= n;
                if (_bits < 0) { Overrun = true; _bits = 0; _cache = 0; }
            }

            /// <summary>Read 0..32 bits (§2 "read n bits"). n == 0 is legal and returns 0.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public uint Read(int n)
            {
                if (n == 0) return 0;
                if (_bits < n) Refill();
                uint v = n == 32 ? (uint)_cache : (uint)_cache & ((1u << n) - 1);
                Consume(n);
                return v;
            }

            public bool ReadBit() => Read(1) != 0;

            /// <summary>Signed 32-bit read for the identification header's bitrate fields.</summary>
            public int ReadInt32() => (int)Read(32);
        }

        /// <summary>§9.2.1 ilog: the number of bits needed to represent x (ilog(0) = 0, ilog(7) = 3).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ILog(int x) => x <= 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)x);

        /// <summary>§9.2.2 float32_unpack. Setup only.</summary>
        public static float Float32Unpack(uint x)
        {
            uint mantissa = x & 0x1FFFFF;
            bool sign = (x & 0x80000000) != 0;
            int exponent = (int)((x & 0x7FE00000) >> 21);
            float m = sign ? -(float)mantissa : mantissa;
            return (float)Math.ScaleB(m, exponent - 788);
        }
    }
}
```

`Peek` after `Consume` is legal without a refill only while `Bits ≥ n` — the decode loops call `Refill()` once at
the top of each symbol when `Bits < 24` (stb's `DECODE_RAW` condition, `:1718`), which bounds the fast path at one
compare per symbol. The `_end8` guard is the one bounds check in the reader.

### 3.4 Codebooks: the canonical builder, the 10-bit fast table, the sorted fallback, the VQ tables

**Storage is flat and shared.** One decoder owns three arenas allocated at `Open`: `uint[] _fast` (every book's
`1 << FastBits` table back to back; entry = `len << 24 | value`, `Miss = 0xFFFFFFFF`), `uint[] _sorted` +
`int[] _sortedEntry` (every book's left-aligned canonical codewords of length > `FastBits`, ascending, and the
entry each maps to), and `float[] _vq` (every VQ book's `Entries × Dims` values, expanded at open so a residue
value is one load). A `Book` is a struct of offsets into those arenas. No `Codebook` objects, no per-entry nodes,
no `List<>` — the whole codebook set of a Spotify setup header (≈ 40-60 books) is ≈ 250 KB of tables.

```csharp
public static partial class Playback
{
    public static unsafe partial class Vorbis
    {
        public const int FastBits = 10;                         // stb STB_VORBIS_FAST_HUFFMAN_LENGTH default (§1.7)
        public const int FastSize = 1 << FastBits;
        public const uint Miss = 0xFFFFFFFF;
        public const int MaxCodeLen = 32;

        /// <summary>One codebook (§3.2): offsets into the decoder's arenas. 32 bytes, kept in an array.</summary>
        public struct Book
        {
            public int Dims, Entries;
            public int FastOff;            // into _fast: FastSize entries
            public int SortedOff, SortedCount, MaxLen;   // into _sorted/_sortedEntry: codes longer than FastBits
            public int VqOff;              // into _vq: Entries × Dims floats, or −1 (lookup type 0)
        }

        /// <summary>Canonical Huffman assignment (§3.2.1; stb <c>compute_codewords</c>, stb_vorbis.c:1086-1130):
        /// each used entry, in order, takes the lowest unused codeword of its length. <paramref name="codes"/>[i] is
        /// the MSB-first (spec-order) codeword, right-aligned. False on an over-subscribed length set.
        /// Single-entry books get length 1 (errata 20150226; Symphonia codebook.rs:314-324, libvorbis
        /// sharedbook.c:523): both 1-bit codes decode to the one entry.</summary>
        public static bool BuildCodewords(ReadOnlySpan<sbyte> len, Span<uint> codes)
        {
            Span<uint> available = stackalloc uint[MaxCodeLen];
            available.Clear();
            int n = len.Length, k = 0;
            while (k < n && len[k] <= 0) k++;
            if (k == n) return true;                              // no used entries: a legal, unused book
            codes[k] = 0;
            for (int i = 1; i <= len[k]; i++) available[i] = 1u << (32 - i);
            for (int i = k + 1; i < n; i++)
            {
                int z = len[i];
                if (z <= 0) continue;
                while (z > 0 && available[z] == 0) z--;
                if (z == 0) return false;                         // over-subscribed
                uint res = available[z];
                available[z] = 0;
                codes[i] = res >> (32 - len[i]);
                for (int y = len[i]; y > z; y--) available[y] = res + (1u << (32 - y));
            }
            return true;
        }

        /// <summary>Reverse the low <paramref name="len"/> bits of <paramref name="x"/> (the codeword as it appears in
        /// the LSB-first cache).</summary>
        static uint ReverseBits(uint x, int len)
        {
            x = ((x & 0x55555555) << 1) | ((x >> 1) & 0x55555555);
            x = ((x & 0x33333333) << 2) | ((x >> 2) & 0x33333333);
            x = ((x & 0x0F0F0F0F) << 4) | ((x >> 4) & 0x0F0F0F0F);
            x = ((x & 0x00FF00FF) << 8) | ((x >> 8) & 0x00FF00FF);
            x = (x << 16) | (x >> 16);
            return x >> (32 - len);
        }

        /// <summary>Fill one book's fast table (stb <c>compute_accelerated_huffman</c>, :1134-1154): every codeword of
        /// length ≤ FastBits is bit-reversed (LSB-first order) and written at every index that has it as a prefix —
        /// stepping by <c>1 &lt;&lt; len</c>. Longer codewords go to the sorted table, left-aligned into MaxLen bits.</summary>
        static void BuildTables(ReadOnlySpan<sbyte> len, ReadOnlySpan<uint> codes, Span<uint> fast,
                                Span<uint> sorted, Span<int> sortedEntry, out int sortedCount, out int maxLen)
        {
            fast.Fill(Miss);
            sortedCount = 0; maxLen = 0;
            for (int i = 0; i < len.Length; i++) if (len[i] > maxLen) maxLen = len[i];
            for (int i = 0; i < len.Length; i++)
            {
                int l = len[i];
                if (l <= 0) continue;
                if (l <= FastBits)
                {
                    uint packed = ((uint)l << 24) | (uint)i;
                    for (uint z = ReverseBits(codes[i], l); z < FastSize; z += 1u << l) fast[(int)z] = packed;
                }
                else
                {
                    sorted[sortedCount] = codes[i] << (maxLen - l);      // left-aligned: prefix-free ⇒ unique order
                    sortedEntry[sortedCount++] = i;
                }
            }
            // insertion sort by key — sortedCount is small (codes longer than 10 bits are the tail of a book)
            for (int i = 1; i < sortedCount; i++)
            {
                uint key = sorted[i]; int e = sortedEntry[i]; int j = i - 1;
                while (j >= 0 && sorted[j] > key) { sorted[j + 1] = sorted[j]; sortedEntry[j + 1] = sortedEntry[j]; j--; }
                sorted[j + 1] = key; sortedEntry[j + 1] = e;
            }
        }

        /// <summary>Decode one codeword (stb <c>DECODE_RAW</c>, :1717-1729). Fast path: the next FastBits of the cache
        /// index the table, one load. Slow path (a codeword longer than FastBits — rare in libvorbis's books):
        /// reverse the next MaxLen bits into spec order and binary-search the left-aligned sorted codewords for the
        /// largest key ≤ it; prefix-freedom makes that the match (libvorbis <c>decode_packed_entry_number</c>,
        /// codebook.c:354-367). Returns −1 at end of packet. No bounds check: <c>fast</c> is FastSize long by
        /// construction and Peek(FastBits) &lt; FastSize.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DecodeScalar(ref BitReader r, in Book b, uint* fast, uint* sorted, int* sortedEntry)
        {
            if (r.Bits < 24) r.Refill();
            uint e = fast[b.FastOff + (int)r.Peek(FastBits)];
            if (e != Miss)
            {
                int l = (int)(e >> 24);
                if (l > r.Bits) { r.Overrun = true; return -1; }
                r.Consume(l);
                return (int)(e & 0xFFFFFF);
            }
            return DecodeSlow(ref r, in b, sorted, sortedEntry);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int DecodeSlow(ref BitReader r, in Book b, uint* sorted, int* sortedEntry)
        {
            if (r.Bits < b.MaxLen) r.Refill();
            int ml = b.MaxLen;
            uint key = ReverseBits(r.Peek(ml), ml);                                  // spec order, left-aligned in ml bits
            int lo = b.SortedOff, hi = b.SortedOff + b.SortedCount - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] <= key) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            if (found < 0) { r.Overrun = true; return -1; }
            int entry = sortedEntry[found];
            int len = b.MaxLen;                                                       // the entry's own length:
            // recovered from the key's leading bits: the code is sorted[found] >> (ml − len); lengths are kept in a
            // parallel sbyte* (b.LenOff) in the file — elided here for length; the call site is one load.
            r.Consume(len);
            return entry;
        }
    }
}
```

**Reading a codebook from the setup header** (§3.2, at `Open`, setup-only code, allocation allowed): sync
`0x564342`; `dims = Read(16)`, `entries = Read(24)`; `ordered = ReadBit()`; if unordered: `sparse = ReadBit()`,
each length `sparse ? (ReadBit() ? Read(5)+1 : 0) : Read(5)+1`; if ordered: `cur = Read(5)+1`, then
`num = Read(ilog(entries − i))` entries of length `cur`, `cur++`, until `i == entries`. `lookup = Read(4)`: 0
none; 1 lattice (`min = Float32Unpack(Read(32))`, `delta = …`, `valueBits = Read(4)+1`, `seq = ReadBit()`,
`lookupValues = ⌊entries^(1/dims)⌋` (the largest `v` with `v^dims ≤ entries`), `mult[lookupValues]` of
`Read(valueBits)`; expansion: `vq[e·dims + d] = mult[(e / v^d) mod v] × delta + min (+ last if seq)`); 2 explicit
(`entries × dims` multiplicands). The `_vq` arena is filled here; a lattice book's expansion is
`entries × dims` floats (Symphonia and stb do the same at open; stb keeps lattices unexpanded only under
`STB_VORBIS_DIVIDES_IN_CODEBOOK`).

### 3.5 Floor 1 — header, packet decode, and the curve rendered straight into the spectrum

The setup (§7.2.2) is parsed once into a `Floor1` struct: `Partitions ≤ 31`, `PartitionClass[31]`,
`ClassDims/Subclasses/Masterbook[16]`, `SubclassBooks[16 × 8]` (−1 = none), `Multiplier ∈ 1..4`, `RangeBits`,
`X[65]` (X[0] = 0, X[1] = 1 << RangeBits, then the partitions' values), `Values`. At open the file also computes
what every reference computes at open: the sort order of the X positions (`Sorted[65]`), and for each i ≥ 2 the
`LowNeighbor[i]` / `HighNeighbor[i]` (the nearest earlier-listed X below / above X[i]; stb `neighbors`,
`:4110-4122`). Range by multiplier: `{256, 128, 86, 64}`.

Packet decode (§7.2.3) fills `int* y` (65 posts) per channel and returns whether the floor is used:

```csharp
public static partial class Playback
{
    public static unsafe partial class Vorbis
    {
        static ReadOnlySpan<int> FloorRange => [256, 128, 86, 64];

        /// <summary>§7.2.3. Fills <paramref name="y"/>[0..Values). Returns false when the channel's floor is unused
        /// (the "nonzero" bit is 0) — the caller then skips its residue and zeros its spectrum.</summary>
        public static bool DecodeFloor1Posts(ref BitReader r, in Floor1 f, Book* books, uint* fast, uint* sorted,
                                             int* sortedEntry, int* y)
        {
            if (!r.ReadBit()) return false;
            int range = FloorRange[f.Multiplier - 1];
            int bits = ILog(range - 1);
            y[0] = (int)r.Read(bits);
            y[1] = (int)r.Read(bits);
            int offset = 2;
            for (int i = 0; i < f.Partitions; i++)
            {
                int cls = f.PartitionClass[i];
                int cdim = f.ClassDims[cls];
                int cbits = f.ClassSubclasses[cls];
                int csub = (1 << cbits) - 1;
                int cval = 0;
                if (cbits > 0)
                {
                    cval = DecodeScalar(ref r, in books[f.ClassMasterbook[cls]], fast, sorted, sortedEntry);
                    if (cval < 0) return true;                                   // EOP: what we have is the floor
                }
                for (int j = 0; j < cdim; j++)
                {
                    int book = f.SubclassBooks[cls * 8 + (cval & csub)];
                    cval >>= cbits;
                    y[offset + j] = book >= 0 ? DecodeScalar(ref r, in books[book], fast, sorted, sortedEntry) : 0;
                    if (y[offset + j] < 0) { y[offset + j] = 0; return true; }
                }
                offset += cdim;
            }
            return true;
        }

        /// <summary>§7.2.4 render_point: the predicted amplitude at x on the line (x0,y0)-(x1,y1), integer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int RenderPoint(int x0, int y0, int x1, int y1, int x)
        {
            int dy = y1 - y0, adx = x1 - x0, ady = Math.Abs(dy);
            int err = ady * (x - x0);
            int off = err / adx;
            return dy < 0 ? y0 - off : y0 + off;
        }

        /// <summary>§7.2.4 step 1: unwrap the posts into absolute amplitudes. <paramref name="final"/>[i] ≥ 0 when
        /// the post is "set" (step2 flag), −1 when it is interpolated and does not start a segment (stb encodes the
        /// flag in the sign, <c>do_floor</c> :3072-3108).</summary>
        static void UnwrapPosts(in Floor1 f, int* y, int* final)
        {
            int range = FloorRange[f.Multiplier - 1];
            final[0] = y[0]; final[1] = y[1];
            for (int i = 2; i < f.Values; i++)
            {
                int lo = f.LowNeighbor[i], hi = f.HighNeighbor[i];
                int pred = RenderPoint(f.X[lo], final[lo] & 0x7FFF, f.X[hi], final[hi] & 0x7FFF, f.X[i]);
                int val = y[i];
                int highroom = range - pred, lowroom = pred;
                int room = (highroom < lowroom ? highroom : lowroom) << 1;
                if (val != 0)
                {
                    final[lo] &= 0x7FFF; final[hi] &= 0x7FFF;                    // step2_flag[lo] = step2_flag[hi] = set
                    if (val >= room) final[i] = highroom > lowroom ? val - lowroom + pred : pred - val + highroom - 1;
                    else final[i] = (val & 1) != 0 ? pred - ((val + 1) >> 1) : pred + (val >> 1);
                }
                else final[i] = pred | 0x8000;                                   // interpolated, flag clear
            }
        }

        /// <summary>§7.2.4 step 2, fused with §4.3.6's dot product: walk the posts in X order, draw each segment with
        /// the integer line algorithm and MULTIPLY the dB-table value into the spectrum (stb <c>draw_line</c> with
        /// <c>LINE_OP = *=</c>, :2022-2081). One pass; no floor buffer. <paramref name="spec"/> is n/2 floats. No bounds
        /// check: X[·] ≤ 1 &lt;&lt; RangeBits and the caller clamps hx to n2.</summary>
        static void RenderFloor1(in Floor1 f, int* final, float* spec, int n2, float* db)
        {
            int lx = 0, ly = final[0] * f.Multiplier;
            for (int q = 1; q < f.Values; q++)
            {
                int j = f.Sorted[q];
                if ((final[j] & 0x8000) != 0) continue;                          // not a segment end
                int hy = (final[j] & 0x7FFF) * f.Multiplier;
                int hx = f.X[j];
                if (hx > n2) hx = n2;
                if (lx != hx) DrawLine(spec, lx, ly, hx, hy, db);
                lx = hx; ly = hy;
                if (hx == n2) return;
            }
            float last = db[ly > 255 ? 255 : ly];
            for (int x = lx; x < n2; x++) spec[x] *= last;                      // the constant tail (Vector256-able; short)
        }

        /// <summary>§7.2.4 render_line (Bresenham, integer). y is clamped to 0..255 before the table.</summary>
        static void DrawLine(float* spec, int x0, int y0, int x1, int y1, float* db)
        {
            int dy = y1 - y0, adx = x1 - x0, ady = Math.Abs(dy);
            int bas = dy / adx, sy = dy < 0 ? bas - 1 : bas + 1;
            ady -= Math.Abs(bas) * adx;
            int y = y0, err = 0;
            if (y0 < 0) y0 = 0; else if (y0 > 255) y0 = 255;
            spec[x0] *= db[y0];
            for (int x = x0 + 1; x < x1; x++)
            {
                err += ady;
                if (err >= adx) { err -= adx; y += sy; } else y += bas;
                int yc = y < 0 ? 0 : y > 255 ? 255 : y;
                spec[x] *= db[yc];
            }
        }

        /// <summary>§7.2.4 / spec §10.1 floor1_inverse_dB_table: the 256 literals, copied verbatim from the spec
        /// (stb :1946-2012 and libvorbis floor1.c:280 carry the same values). Each step is ×0.9389798
        /// (−0.546875 dB); 1.0649863e-07 … 0.9389798, 1.0. The file carries all 256; the two rows here are the ends.</summary>
        static readonly float[] s_floor1Db =
        [
            1.0649863e-07f, 1.1341951e-07f, 1.2079015e-07f, 1.2863978e-07f, 1.3699951e-07f, 1.4590251e-07f, 1.5538408e-07f, 1.6548181e-07f,
            /* … 240 values … */
            0.50028648f, 0.53279791f, 0.56742212f, 0.60429640f, 0.64356699f, 0.68538959f, 0.72993007f, 0.77736504f,
            0.82788260f, 0.88168307f, 0.9389798f, 1.0f,
        ];
    }
}
```

`RenderFloor1` is scalar on purpose: its cost is `n/2` lookups and multiplies per channel (1,024 for a long block
— under 5 % of the packet next to the IMDCT), the Bresenham step is branch-predictable, and a vector version would
need a per-lane gather. Floor 0 (§6) is implemented as the spec's LSP → curve (`floor0_inverse`), scalar, without
a fast path — it never occurs in a libvorbis file after 2002 (§1.6) and stb refuses it outright.

### 3.6 Residue 2 — the interleaved vector decoded straight into the channel spectra

Setup (§8.6.1): `Begin, End, PartitionSize, Classifications, Classbook, Cascade[64], Books[64 × 8]`. At open the
file also builds the residue's `ClassMap` (`classbook.Entries × classbook.Dims` bytes: the partition classes a
classification codeword unpacks to, so the packet loop never divides — stb `part_classdata`/`classdata`,
`STB_VORBIS_DIVIDES_IN_RESIDUE` comment `:512-516`), and clamps `End` to `ch × n/2` for every block size (the
"actual size" rule).

Residue 2 (`res2_inverse`, libvorbis `res0.c:812-864`): all channels' residues are ONE vector `v[0 .. ch·n/2)`,
interleaved `v[j·ch + c]` (§8.6.2). The reference decoders either write into that vector and deinterleave
afterwards (Symphonia `residue.rs:177-218`) or, as stb does (`codebook_decode_deinterleave_repeat`,
`:1865-1933`), keep a channel cursor and a bin cursor and ADD each VQ value straight into `spec[c][bin]`. That is
what this loop does; with `ch == 2` the cursor is a bit and a shift.

```csharp
public static partial class Playback
{
    public static unsafe partial class Vorbis
    {
        public struct Residue
        {
            public byte Type;                       // 0, 1, 2
            public int Begin, End, PartitionSize, Classifications, Classbook;
            public int ClassMapOff;                 // into _classMap: Classifications^... see Open
            public fixed byte Cascade[64];
            public fixed short Books[64 * 8];       // −1 = none
        }

        /// <summary>§8.6.2 for type 2, all channels at once. <paramref name="spec"/> = ch pointers to n/2 floats
        /// (already zeroed for this packet); the VQ values are ADDED. <paramref name="partClass"/> is the decoder's
        /// scratch (≥ partitions bytes). Stops at end-of-packet: what was decoded stands, the rest is zero (stb
        /// <c>decode_residue</c> :2129-2227 "goto done"). No bounds check: <c>end ≤ ch × n2</c> was clamped at
        /// open, and every VQ read is <c>b.Dims</c> floats inside a book sized <c>Entries × Dims</c>.</summary>
        public static void DecodeResidue2(ref BitReader r, in Residue res, Book* books, uint* fast, uint* sorted,
                                          int* sortedEntry, float* vq, byte* classMap, float** spec, int ch, int n2,
                                          byte* partClass)
        {
            int psize = res.PartitionSize;
            int end = res.End < ch * n2 ? res.End : ch * n2;
            int begin = res.Begin;
            if (begin >= end) return;
            int partitions = (end - begin) / psize;
            ref readonly Book classbook = ref books[res.Classbook];
            int cw = classbook.Dims;                                              // classwords per codeword
            byte* map = classMap + res.ClassMapOff;

            for (int pass = 0; pass < 8; pass++)
            {
                for (int p = 0; p < partitions;)
                {
                    if (pass == 0)
                    {
                        int e = DecodeScalar(ref r, in classbook, fast, sorted, sortedEntry);
                        if (e < 0) return;
                        byte* m = map + e * cw;
                        int take = partitions - p < cw ? partitions - p : cw;
                        for (int k = 0; k < take; k++) partClass[p + k] = m[k];
                    }
                    int stop = p + cw < partitions ? p + cw : partitions;
                    for (; p < stop; p++)
                    {
                        int cls = partClass[p];
                        if ((res.Cascade[cls] & (1 << pass)) == 0) continue;
                        int bi = res.Books[cls * 8 + pass];
                        if (bi < 0) continue;
                        ref readonly Book b = ref books[bi];
                        float* table = vq + b.VqOff;
                        int dims = b.Dims;
                        int off = begin + p * psize;                              // interleaved index
                        int words = psize / dims;
                        if (ch == 2)
                        {
                            float* l = spec[0], rr = spec[1];
                            for (int q = 0; q < words; q++)
                            {
                                int entry = DecodeScalar(ref r, in b, fast, sorted, sortedEntry);
                                if (entry < 0) return;
                                float* v = table + entry * dims;
                                for (int d = 0; d < dims; d++, off++)
                                    ((off & 1) == 0 ? l : rr)[off >> 1] += v[d];
                            }
                        }
                        else
                        {
                            for (int q = 0; q < words; q++)
                            {
                                int entry = DecodeScalar(ref r, in b, fast, sorted, sortedEntry);
                                if (entry < 0) return;
                                float* v = table + entry * dims;
                                for (int d = 0; d < dims; d++, off++)
                                    spec[off % ch][off / ch] += v[d];
                            }
                        }
                    }
                }
            }
        }
    }
}
```

The `ch == 2` arm's select compiles to a conditional move; libvorbis's residue-2 books for 44.1 kHz stereo all
have even `Dims` (2, 4, 8 — `modes/residue_44.h`), so a further specialization writes `v[d], v[d+1]` to `l, rr`
and advances `off` by two: the file carries that as the inner loop with the generic one as the odd-dims fallback.
Residue 0 and 1 (§8.6.2 formats) use the same partition/pass/classification loop with per-channel vectors:
type 1 adds `dims` consecutive values into one channel's `spec[c][off..]`; type 0 strides them (`spec[c][off +
d·step]`, `step = psize / dims`). They share the loop through a `switch` on `res.Type` outside the pass loop, not
inside.

### 3.7 The inverse MDCT — stb's eight stages with `Vector128` butterflies

Three shapes exist (§1.7): libvorbis's split radix down to hard-coded 8/16/32-point kernels, Symphonia's N/2
complex FFT, and stb's flat eight-stage form from "The use of multirate filter banks for coding of high quality
digital audio" with the step-3 loops unrolled four-wide (`imdct_step3_iter0_loop`, `_inner_r_loop`,
`_inner_s_loop`, `_ld654`, `stb_vorbis.c:2408-2627`) and the final stage writing the output from both ends by
symmetry (`:2867-2925`). This file transliterates stb `:2629-2929` line for line — lewton did exactly that
(`imdct.rs:14-659`, "a very close translation") — with two changes: the tables and both buffers are the decoder's
pinned arrays, and each four-wide step-3 group is one `Vector128<float>` butterfly.

**Tables per block size** (stb `compute_twiddle_factors` `:1254-1269`, `compute_bitreverse` `:1278-1284`; at
`Open`, `MathF` only here): `A[n/2]`, `B[n/2]`, `C[n/4]` floats and `BitRev[n/8]` ushorts:

```
A[2k] = cos(4kπ/n),   A[2k+1] = −sin(4kπ/n)                 k < n/4
B[2k] = cos((2k+1)π/(2n))/2,  B[2k+1] = sin((2k+1)π/(2n))/2   k < n/4
C[2k] = cos(2(2k+1)π/n), C[2k+1] = −sin(2(2k+1)π/n)          k < n/8
BitRev[i] = (bitreverse32(i) >> (32 − log2(n) + 3)) << 2      i < n/8
```

**The butterfly every step-3 stage runs.** Eight consecutive floats at `e0[−7..0]` and eight at `e2[−7..0]` are
four complex pairs `(im, re)` each; the pair-wise operation is `sum → e0`, `(diff × twiddle) → e2`. In one
`Vector128` over lanes `[k11b, k00b, k11a, k00a]` (addresses −3..0), with twiddle pairs `(A0, A1)` for the pair at
−1..0 and `(A2, A3)` for −3..−2: `e2' = k ⊙ [A2, A2, A0, A0] + swap(k) ⊙ [A3, −A3, A1, −A1]`, which is exactly
stb's `ee2[0] = k00·A0 − k11·A1; ee2[−1] = k11·A0 + k00·A1` for both pairs. `swap` is `Vector128.Shuffle(k, [1, 0,
3, 2])` — one `vpermilps` / NEON `rev64`. Two such vectors cover the eight floats.

```csharp
public static partial class Playback
{
    public static unsafe partial class Vorbis
    {
        /// <summary>One four-pair butterfly group: e0[−7..0], e2[−7..0]; twiddles for the four pairs, from the
        /// highest address down, as (re, im) = (t[0],t[1]) (t[2],t[3]) (t[4],t[5]) (t[6],t[7]).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Butterfly8(float* e0, float* e2,
                               Vector128<float> reHi, Vector128<float> imHi, Vector128<float> reLo, Vector128<float> imLo)
        {
            // lanes: address −3..0 → [k11b, k00b, k11a, k00a]
            var a0 = Vector128.Load(e0 - 3); var b0 = Vector128.Load(e2 - 3);
            var a1 = Vector128.Load(e0 - 7); var b1 = Vector128.Load(e2 - 7);
            var k0 = a0 - b0; var k1 = a1 - b1;
            Vector128.Store(a0 + b0, e0 - 3);
            Vector128.Store(a1 + b1, e0 - 7);
            var s0 = Vector128.Shuffle(k0, Vector128.Create(1, 0, 3, 2));
            var s1 = Vector128.Shuffle(k1, Vector128.Create(1, 0, 3, 2));
            Vector128.Store(k0 * reHi + s0 * imHi, e2 - 3);
            Vector128.Store(k1 * reLo + s1 * imLo, e2 - 7);
        }

        /// <summary>Build the two twiddle vectors for a pair of complex twiddles (a: the pair at the higher address,
        /// b: the lower): re = [b.re, b.re, a.re, a.re], im = [b.im, −b.im, a.im, −a.im].</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Twiddle(float* a, float* b, out Vector128<float> re, out Vector128<float> im)
        {
            re = Vector128.Create(b[0], b[0], a[0], a[0]);
            im = Vector128.Create(b[1], -b[1], a[1], -a[1]);
        }

        /// <summary>stb <c>imdct_step3_iter0_loop</c> (:2408-2451): n/4 groups, twiddles advancing by 8 per pair.</summary>
        static void Step3Iter0(int n, float* e, int iOff, int kOff, float* A)
        {
            float* ee0 = e + iOff, ee2 = ee0 + kOff;
            for (int i = n >> 2; i > 0; i--)
            {
                Twiddle(A, A + 8, out var reHi, out var imHi);
                Twiddle(A + 16, A + 24, out var reLo, out var imLo);
                Butterfly8(ee0, ee2, reHi, imHi, reLo, imLo);
                A += 32; ee0 -= 8; ee2 -= 8;
            }
        }

        /// <summary>stb <c>imdct_step3_inner_r_loop</c> (:2453-2501): twiddles advance by k1 per pair.</summary>
        static void Step3R(int lim, float* e, int d0, int kOff, float* A, int k1)
        {
            float* e0 = e + d0, e2 = e0 + kOff;
            for (int i = lim >> 2; i > 0; i--)
            {
                Twiddle(A, A + k1, out var reHi, out var imHi);
                Twiddle(A + 2 * k1, A + 3 * k1, out var reLo, out var imLo);
                Butterfly8(e0, e2, reHi, imHi, reLo, imLo);
                A += 4 * k1; e0 -= 8; e2 -= 8;
            }
        }

        /// <summary>stb <c>imdct_step3_inner_s_loop</c> (:2503-2552): the same four twiddles for every group (held in
        /// registers), groups k0 apart.</summary>
        static void Step3S(int n, float* e, int iOff, int kOff, float* A, int aOff, int k0)
        {
            Twiddle(A, A + aOff, out var reHi, out var imHi);
            Twiddle(A + 2 * aOff, A + 3 * aOff, out var reLo, out var imLo);
            float* ee0 = e + iOff, ee2 = ee0 + kOff;
            for (int i = n; i > 0; i--)
            {
                Butterfly8(ee0, ee2, reHi, imHi, reLo, imLo);
                ee0 -= k0; ee2 -= k0;
            }
        }

        /// <summary>stb <c>imdct_step3_inner_s_loop_ld654</c> + <c>iter_54</c> (:2554-2627): the last three stages
        /// fused, where the twiddles are 1, 0 and ±√½ (A2). Sixteen floats per group; kept scalar-shaped as in stb
        /// because every lane has a different sign pattern — the JIT vectorizes the adds, the multiplies are two.</summary>
        static void Step3Ld654(int n, float* e, int iOff, float* A, int baseN)
        {
            float A2 = A[baseN >> 3];
            float* z = e + iOff, bas = z - 16 * n;
            while (z > bas)
            {
                float k00 = z[0] - z[-8], k11 = z[-1] - z[-9], l00 = z[-2] - z[-10], l11 = z[-3] - z[-11];
                z[0] += z[-8]; z[-1] += z[-9]; z[-2] += z[-10]; z[-3] += z[-11];
                z[-8] = k00; z[-9] = k11; z[-10] = (l00 + l11) * A2; z[-11] = (l11 - l00) * A2;
                k00 = z[-4] - z[-12]; k11 = z[-5] - z[-13]; l00 = z[-6] - z[-14]; l11 = z[-7] - z[-15];
                z[-4] += z[-12]; z[-5] += z[-13]; z[-6] += z[-14]; z[-7] += z[-15];
                z[-12] = k11; z[-13] = -k00; z[-14] = (l11 - l00) * A2; z[-15] = (l00 + l11) * -A2;
                Iter54(z); Iter54(z - 8);
                z -= 16;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Iter54(float* z)
        {
            float k00 = z[0] - z[-4], y0 = z[0] + z[-4], y2 = z[-2] + z[-6], k22 = z[-2] - z[-6];
            z[0] = y0 + y2; z[-2] = y0 - y2;
            float k33 = z[-3] - z[-7];
            z[-4] = k00 + k33; z[-6] = k00 - k33;
            float k11 = z[-1] - z[-5], y1 = z[-1] + z[-5], y3 = z[-3] + z[-7];
            z[-1] = y1 + y3; z[-3] = y1 - y3; z[-5] = k11 - k22; z[-7] = k11 + k22;
        }

        /// <summary>The inverse MDCT (stb <c>inverse_mdct</c> :2629-2929). <paramref name="buf"/>: n floats — the n/2
        /// spectrum on entry, the n time-domain samples (unwindowed) on exit. <paramref name="buf2"/>: n/2 scratch.
        /// Tables per block size from Open. No bounds check: every pointer is derived from n and the tables are sized
        /// from n; the loop bounds are stb's.</summary>
        public static void Imdct(float* buf, float* buf2, int n, float* A, float* B, float* C, ushort* bitrev)
        {
            int n2 = n >> 1, n4 = n >> 2, n8 = n >> 3;
            int ld = ILog(n) - 1;

            // step 0+1: copy and reflect the spectrum into buf2 with A (the "missing ×2" is repaid in B)
            {
                float* d = buf2 + n2 - 2, AA = A, e = buf, eStop = buf + n2;
                while (e != eStop)
                {
                    d[1] = e[0] * AA[0] - e[2] * AA[1];
                    d[0] = e[0] * AA[1] + e[2] * AA[0];
                    d -= 2; AA += 2; e += 4;
                }
                e = buf + n2 - 3;
                while (d >= buf2)
                {
                    d[1] = -e[2] * AA[0] + e[0] * AA[1];
                    d[0] = -e[2] * AA[1] - e[0] * AA[0];
                    d -= 2; AA += 2; e -= 4;
                }
            }
            float* u = buf, v = buf2;

            // step 2: v → u with A from the top (stb :2695-2727)
            {
                float* AA = A + n2 - 8, e0 = v + n4, e1 = v, d0 = u + n4, d1 = u;
                while (AA >= A)
                {
                    float v41_21 = e0[1] - e1[1], v40_20 = e0[0] - e1[0];
                    d0[1] = e0[1] + e1[1]; d0[0] = e0[0] + e1[0];
                    d1[1] = v41_21 * AA[4] - v40_20 * AA[5]; d1[0] = v40_20 * AA[4] + v41_21 * AA[5];
                    v41_21 = e0[3] - e1[3]; v40_20 = e0[2] - e1[2];
                    d0[3] = e0[3] + e1[3]; d0[2] = e0[2] + e1[2];
                    d1[3] = v41_21 * AA[0] - v40_20 * AA[1]; d1[2] = v40_20 * AA[0] + v41_21 * AA[1];
                    AA -= 8; d0 += 4; d1 += 4; e0 += 4; e1 += 4;
                }
            }

            // step 3: iterations 0 and 1 with per-pair twiddles, then the r-inside-s stages, the s-inside-r stages,
            // then the fused last three (stb :2729-2790)
            Step3Iter0(n >> 4, u, n2 - 1 - n4 * 0, -(n >> 3), A);
            Step3Iter0(n >> 4, u, n2 - 1 - n4 * 1, -(n >> 3), A);
            Step3R(n >> 5, u, n2 - 1 - n8 * 0, -(n >> 4), A, 16);
            Step3R(n >> 5, u, n2 - 1 - n8 * 1, -(n >> 4), A, 16);
            Step3R(n >> 5, u, n2 - 1 - n8 * 2, -(n >> 4), A, 16);
            Step3R(n >> 5, u, n2 - 1 - n8 * 3, -(n >> 4), A, 16);
            int l = 2;
            for (; l < (ld - 3) >> 1; l++)
            {
                int k0 = n >> (l + 2), k0_2 = k0 >> 1, lim = 1 << (l + 1);
                for (int i = 0; i < lim; i++) Step3R(n >> (l + 4), u, n2 - 1 - k0 * i, -k0_2, A, 1 << (l + 3));
            }
            for (; l < ld - 6; l++)
            {
                int k0 = n >> (l + 2), k1 = 1 << (l + 3), k0_2 = k0 >> 1, rlim = n >> (l + 6), lim = 1 << (l + 1);
                float* A0 = A; int iOff = n2 - 1;
                for (int r = rlim; r > 0; r--) { Step3S(lim, u, iOff, -k0_2, A0, k1, k0); A0 += k1 * 4; iOff -= 8; }
            }
            Step3Ld654(n >> 5, u, n2 - 1, A, n);

            // steps 4-6: bit reversal u → v (stb :2796-2825)
            {
                float* d0 = v + n4 - 4, d1 = v + n2 - 4; ushort* br = bitrev;
                while (d0 >= v)
                {
                    int k4 = br[0];
                    d1[3] = u[k4 + 0]; d1[2] = u[k4 + 1]; d0[3] = u[k4 + 2]; d0[2] = u[k4 + 3];
                    k4 = br[1];
                    d1[1] = u[k4 + 0]; d1[0] = u[k4 + 1]; d0[1] = u[k4 + 2]; d0[0] = u[k4 + 3];
                    d0 -= 4; d1 -= 4; br += 2;
                }
            }

            // step 7: in place in v with C (stb :2833-2866) — two complex rotates per 4 floats from each end
            {
                float* d = v, e = v + n2 - 4, CC = C;
                while (d < e)
                {
                    float a02 = d[0] - e[2], a11 = d[1] + e[3];
                    float b0 = CC[1] * a02 + CC[0] * a11, b1 = CC[1] * a11 - CC[0] * a02;
                    float b2 = d[0] + e[2], b3 = d[1] - e[3];
                    d[0] = b2 + b0; d[1] = b3 + b1; e[2] = b2 - b0; e[3] = b1 - b3;
                    a02 = d[2] - e[0]; a11 = d[3] + e[1];
                    b0 = CC[3] * a02 + CC[2] * a11; b1 = CC[3] * a11 - CC[2] * a02;
                    b2 = d[2] + e[0]; b3 = d[3] - e[1];
                    d[2] = b2 + b0; d[3] = b3 + b1; e[0] = b2 - b0; e[1] = b1 - b3;
                    CC += 4; d += 4; e -= 4;
                }
            }

            // step 8 + decode: B twiddle, written to the four quarters of buf from both ends (stb :2867-2925)
            {
                float* BB = B + n2 - 8, e = v + n2 - 8;
                float* d0 = buf, d1 = buf + n2 - 4, d2 = buf + n2, d3 = buf + n - 4;
                while (e >= v)
                {
                    float p3 = e[6] * BB[7] - e[7] * BB[6], p2 = -e[6] * BB[6] - e[7] * BB[7];
                    d0[0] = p3; d1[3] = -p3; d2[0] = p2; d3[3] = p2;
                    float p1 = e[4] * BB[5] - e[5] * BB[4], p0 = -e[4] * BB[4] - e[5] * BB[5];
                    d0[1] = p1; d1[2] = -p1; d2[1] = p0; d3[2] = p0;
                    p3 = e[2] * BB[3] - e[3] * BB[2]; p2 = -e[2] * BB[2] - e[3] * BB[3];
                    d0[2] = p3; d1[1] = -p3; d2[2] = p2; d3[1] = p2;
                    p1 = e[0] * BB[1] - e[1] * BB[0]; p0 = -e[0] * BB[0] - e[1] * BB[1];
                    d0[3] = p1; d1[0] = -p1; d2[3] = p0; d3[0] = p0;
                    BB -= 8; e -= 8; d0 += 4; d3 -= 4; d1 -= 4; d2 += 4;
                }
            }
        }
    }
}
```

Steps 0-2 and 7-8 stay in stb's scalar shape in the first cut: they are one pass each over `n/2` floats with a
strided/reflected access pattern (the JIT already keeps them in registers), and the step-3 stages are where the
flops are (`log2(n) − 4` stages × `n/2` butterflies). `Step3Ld654`'s adds are lane-independent; its two multiplies
by `±A2` are the only twiddles — it is left as stb wrote it and is the first candidate for a `Vector128` rewrite
when §7's throughput fact is measured. Correctness of the port is pinned by §7's s16 reference and the sine SNR
fact, and by the property `Imdct(spectrum of a unit impulse) == the B/C-derived cosine` computed once in a test.

### 3.8 Window + overlap-add, coupling, and interleave + gain — the `Vector256` streams

**Window tables at open** (§4.3.1's `y = sin(π/2 · sin²((x + 0.5)/n · π))`): for each block size `m ∈ {n0, n1}`
the left slope `w_m[i]`, `i < m/2`, and — so the overlap-add is elementwise — the **reversed** right slope
`wr_m[i] = w_m[m/2 − 1 − i]`. 4 arrays, `n0 + n1` floats total (2,304 for 256/2048), 1,152 `MathF.Sin` calls once.

**Overlap-add** (stb `vorbis_finish_frame` `:3456-3507`, spec §1.3.2): the current block's `cur[left .. left+m/2)`
is multiplied by the left slope and added to the previous block's saved right half multiplied by the reversed
right slope, where `m = 2 × min(prev_n, cur_n)/2` — both blocks agree on `m` by construction (the previous block's
`next_window_flag` and this block's `previous_window_flag` describe the same neighbour). The block's samples
`[left, right)` are then the packet's output; `cur[right .. n)` becomes the next packet's `prev`. The first
packet after open or seek has no `prev`: it saves its right half and returns 0 samples.

```csharp
public static partial class Playback
{
    public static unsafe partial class Vorbis
    {
        /// <summary>cur[i] = cur[i]·w[i] + prev[i]·wr[i] for i &lt; m: the overlap region of one channel. Vector256 when
        /// accelerated, Vector128 always, scalar tail. Pointers are 32-byte aligned by construction (POH arrays of
        /// floats are 8-aligned; the file over-allocates and rounds the base up once at Open).</summary>
        public static void OverlapAdd(float* cur, float* prev, float* w, float* wr, int m)
        {
            int i = 0;
            if (Vector256.IsHardwareAccelerated && m >= 32)
            {
                for (; i <= m - 8; i += 8)
                    Vector256.Store(Vector256.Load(cur + i) * Vector256.Load(w + i)
                                  + Vector256.Load(prev + i) * Vector256.Load(wr + i), cur + i);
            }
            else if (Vector128.IsHardwareAccelerated && m >= 16)
            {
                for (; i <= m - 4; i += 4)
                    Vector128.Store(Vector128.Load(cur + i) * Vector128.Load(w + i)
                                  + Vector128.Load(prev + i) * Vector128.Load(wr + i), cur + i);
            }
            for (; i < m; i++) cur[i] = cur[i] * w[i] + prev[i] * wr[i];
        }

        /// <summary>§4.3.5 inverse coupling for one (magnitude, angle) pair over n2 bins, branch-free:
        /// d = m &gt; 0 ? a : −a;  a &gt; 0 ? (m, m − d) : (m + d, m)  — the four cases of stb :3330-3349 folded.</summary>
        public static void Uncouple(float* m, float* a, int n2)
        {
            int i = 0;
            if (Vector256.IsHardwareAccelerated && n2 >= 32)
            {
                var zero = Vector256<float>.Zero;
                for (; i <= n2 - 8; i += 8)
                {
                    var mv = Vector256.Load(m + i); var av = Vector256.Load(a + i);
                    var d = Vector256.ConditionalSelect(Vector256.GreaterThan(mv, zero), av, -av);
                    var aPos = Vector256.GreaterThan(av, zero);
                    Vector256.Store(Vector256.ConditionalSelect(aPos, mv, mv + d), m + i);
                    Vector256.Store(Vector256.ConditionalSelect(aPos, mv - d, mv), a + i);
                }
            }
            else if (Vector128.IsHardwareAccelerated && n2 >= 16)
            {
                var zero = Vector128<float>.Zero;
                for (; i <= n2 - 4; i += 4)
                {
                    var mv = Vector128.Load(m + i); var av = Vector128.Load(a + i);
                    var d = Vector128.ConditionalSelect(Vector128.GreaterThan(mv, zero), av, -av);
                    var aPos = Vector128.GreaterThan(av, zero);
                    Vector128.Store(Vector128.ConditionalSelect(aPos, mv, mv + d), m + i);
                    Vector128.Store(Vector128.ConditionalSelect(aPos, mv - d, mv), a + i);
                }
            }
            for (; i < n2; i++)
            {
                float mv = m[i], av = a[i], d = mv > 0 ? av : -av;
                if (av > 0) { a[i] = mv - d; } else { m[i] = mv + d; }
            }
        }

        /// <summary>Interleave two channels into <paramref name="dst"/> with the gain folded in (the one multiply per
        /// sample the adapter promised, Playback.Audio.cs:746-750). Vector128: two single-vector shuffles and a
        /// blend per four frames — one instruction each on x64 and NEON; a platform unpack (Sse.UnpackLow /
        /// AdvSimd.Arm64.ZipLow) is the drop-in if §7's throughput fact says the generic lowering is worse.</summary>
        public static void InterleaveStereo(float* l, float* r, float* dst, int frames, float gain)
        {
            int i = 0;
            if (Vector128.IsHardwareAccelerated && frames >= 16)
            {
                var g = Vector128.Create(gain);
                var even = Vector128.Create(-1, 0, -1, 0).AsSingle();          // lanes 0,2 from a; 1,3 from b
                for (; i <= frames - 4; i += 4)
                {
                    var lv = Vector128.Load(l + i) * g; var rv = Vector128.Load(r + i) * g;
                    var lo = Vector128.ConditionalSelect(even, Vector128.Shuffle(lv, Vector128.Create(0, 0, 1, 1)),
                                                               Vector128.Shuffle(rv, Vector128.Create(0, 0, 1, 1)));
                    var hi = Vector128.ConditionalSelect(even, Vector128.Shuffle(lv, Vector128.Create(2, 2, 3, 3)),
                                                               Vector128.Shuffle(rv, Vector128.Create(2, 2, 3, 3)));
                    Vector128.Store(lo, dst + 2 * i);
                    Vector128.Store(hi, dst + 2 * i + 4);
                }
            }
            for (; i < frames; i++) { dst[2 * i] = l[i] * gain; dst[2 * i + 1] = r[i] * gain; }
        }
    }
}
```

Mono is duplicated to L/R by the same kernel with `l == r`; more than two channels take the FLAC plan's
`ToFloatMulti` downmix shape (rare; scalar).

### 3.9 The packet decoder — modes, mappings, the order of operations

`Decoder.DecodePacket(ReadOnlySpan<byte> packet)` returns `PacketResult { Ok, NotAudio, BadMode, Truncated }` and
sets `Output` (interleaved floats, `Frames` frames, 0 for the primed packet). The order is the spec's (§4.3) and
stb's `vorbis_decode_packet_rest` (`:3186-3400`):

```csharp
public static partial class Playback
{
    public static unsafe partial class Vorbis
    {
        public sealed class Decoder
        {
            // setup tables (Open): Book[] _books; Floor1[] _floors; Residue[] _residues; Mapping[] _mappings; Mode[] _modes;
            // arenas (POH, pinned): uint[] _fast, _sorted; int[] _sortedEntry; float[] _vq; byte[] _classMap;
            // per block size: float[] A,B,C; ushort[] bitrev; float[] w, wr
            // per channel: float[] spec (n1/2), buf (n1), prev (n1/2); int[65] y, final; bool residueOff
            // scratch: float[] buf2 (n1/2); byte[] partClass; float[] output (n1 × ch)
            // state across packets: int _prevN (0 = no previous block); long _granule (§4.4)

            public int Frames { get; private set; }
            public float* Output => _outputPtr;

            [SkipLocalsInit]
            public PacketResult DecodePacket(ReadOnlySpan<byte> packet)
            {
                fixed (byte* p = packet)
                {
                    var r = new BitReader(p, packet.Length);
                    if (r.ReadBit()) return PacketResult.NotAudio;                       // §4.3.1 packet type 0
                    int modeIdx = (int)r.Read(_modeBits);
                    if (modeIdx >= _modes.Length) return PacketResult.BadMode;
                    ref readonly Mode mode = ref _modes[modeIdx];
                    int n = mode.BlockFlag ? _n1 : _n0, n2 = n >> 1;
                    bool prevShort = false, nextShort = false;
                    if (mode.BlockFlag) { prevShort = !r.ReadBit(); nextShort = !r.ReadBit(); }
                    ref readonly Mapping map = ref _mappings[mode.Mapping];

                    // 1. floors: posts per channel; unused floors mark the channel "no residue"
                    for (int c = 0; c < _ch; c++)
                    {
                        ref readonly Floor1 f = ref _floors[map.SubmapFloor[map.Mux[c]]];
                        _residueOff[c] = !DecodeFloor1Posts(ref r, in f, _bookPtr, _fastPtr, _sortedPtr, _sortedEntryPtr, _y[c]);
                    }
                    // 2. coupling propagates "has residue" to both halves of a pair (§4.3.3)
                    for (int s = 0; s < map.CouplingSteps; s++)
                    {
                        int mch = map.Magnitude[s], ach = map.Angle[s];
                        if (!_residueOff[mch] || !_residueOff[ach]) { _residueOff[mch] = false; _residueOff[ach] = false; }
                    }
                    // 3. residues: zero the spectra, then add (type 2: all channels of the submap at once)
                    for (int c = 0; c < _ch; c++) new Span<float>(_spec[c], n2).Clear();
                    for (int s = 0; s < map.Submaps; s++)
                    {
                        ref readonly Residue res = ref _residues[map.SubmapResidue[s]];
                        // channels of this submap; "do not decode" only when ALL are off (§8.6.2 type 2 rule)
                        int cnt = GatherSubmapChannels(in map, s, _submapSpec, out bool allOff);
                        if (allOff) continue;
                        if (res.Type == 2) DecodeResidue2(ref r, in res, _bookPtr, _fastPtr, _sortedPtr, _sortedEntryPtr, _vqPtr, _classMapPtr, _submapSpec, cnt, n2, _partClass);
                        else DecodeResidue01(ref r, in res, /* … */ _submapSpec, cnt, n2, _partClass, _residueOffForSubmap);
                    }
                    // 4. inverse coupling, last step first (§4.3.5)
                    for (int s = map.CouplingSteps - 1; s >= 0; s--) Uncouple(_spec[map.Magnitude[s]], _spec[map.Angle[s]], n2);
                    // 5. floor curve × spectrum, in place; unused floor ⇒ silence (§4.3.6)
                    for (int c = 0; c < _ch; c++)
                    {
                        if (_residueOff[c]) { new Span<float>(_spec[c], n2).Clear(); continue; }
                        ref readonly Floor1 f = ref _floors[map.SubmapFloor[map.Mux[c]]];
                        UnwrapPosts(in f, _y[c], _final[c]);
                        RenderFloor1(in f, _final[c], _spec[c], n2, _dbPtr);
                    }
                    // 6. IMDCT per channel: spec (n/2) → buf (n), then overlap with prev and stash the right half
                    int left = mode.BlockFlag && prevShort ? n / 4 - _n0 / 4 : 0;
                    int right = mode.BlockFlag && nextShort ? 3 * n / 4 - _n0 / 4 : n2;
                    int m = _prevN == 0 ? 0 : Math.Min(_prevN, n) >> 1;          // the overlap length
                    for (int c = 0; c < _ch; c++)
                    {
                        Buffer.MemoryCopy(_spec[c], _buf[c], n2 * 4, n2 * 4);
                        Imdct(_buf[c], _buf2, n, TablesA(n), TablesB(n), TablesC(n), TablesRev(n));
                        if (m > 0) OverlapAdd(_buf[c] + left, _prev[c], WinL(m << 1), WinR(m << 1), m);
                    }
                    int frames = _prevN == 0 ? 0 : right - left;
                    // 7. interleave + gain into the output; save the right halves for the next packet
                    if (frames > 0)
                    {
                        if (_ch == 2) InterleaveStereo(_buf[0] + left, _buf[1] + left, _outputPtr, frames, _gain);
                        else InterleaveMulti(/* … */);
                    }
                    int keep = n - right;
                    for (int c = 0; c < _ch; c++) Buffer.MemoryCopy(_buf[c] + right, _prev[c], keep * 4, keep * 4);
                    _prevN = n;
                    Frames = frames;
                    if (r.Overrun) _overruns++;                                          // diagnostics counter, not a fault
                    return PacketResult.Ok;
                }
            }

            /// <summary>Forget the previous block: the next packet primes and returns 0 frames (§4.4).</summary>
            public void ResetLapping() => _prevN = 0;

            /// <summary>What a packet WOULD return, from its first bytes only (stb <c>peek_decode_initial</c>
            /// :4855-4878, libvorbis <c>vorbis_packet_blocksize</c>): the samples the seek planner counts without
            /// decoding (§4.4). −1 for a non-audio packet.</summary>
            public int PeekFrames(ReadOnlySpan<byte> packet, ref int prevN)
            {
                if (packet.Length == 0 || (packet[0] & 1) != 0) return -1;
                int modeIdx = (int)((packet[0] >> 1) & ((1u << _modeBits) - 1));        // mode bits ≤ 6 fit byte 0
                if (modeIdx >= _modes.Length) return -1;
                int n = _modes[modeIdx].BlockFlag ? _n1 : _n0;
                int frames = prevN == 0 ? 0 : (prevN >> 2) + (n >> 2);
                prevN = n;
                return frames;
            }
        }
    }
}
```

`Open(identification, setup)` parses the three headers into the tables (allocation happens here and only here);
`ParseComments(span, ref Tags)` is the FLAC plan's `ParseVorbisComment` lifted to a shared
`Playback.VorbisComment` (a request to U, §8) so local `.ogg` files get the same title/artist/cover ranges.

### 3.10 The allocation plan (P8), stated as a table

Everything below is allocated in `Decoder.Open` from the setup header, on the pinned object heap, and reused
across seeks and across tracks whose setup is not larger; a second `Open` with equal or smaller sizes allocates
nothing. Sizes are for the Spotify shape (stereo, 256/2048, a libvorbis 44.1 kHz setup with ~45 codebooks).

| Buffer | Type / owner | Size | When |
|---|---|---|---|
| `_fast` fast-Huffman tables | `uint[]` arena, `Decoder` | `books × 4 KiB` ≈ 180 KB | `Open` |
| `_sorted` + `_sortedEntry` long codewords | `uint[]` + `int[]` | Σ codes longer than 10 bits ≈ 10-30 KB | `Open` |
| `_vq` expanded VQ values | `float[]` arena | Σ `Entries × Dims` × 4 B ≈ 100-300 KB (largest libvorbis residue books are 6,561 × 2) | `Open` |
| `_classMap` residue classification maps | `byte[]` | Σ `classbook.Entries × Dims` ≈ 5-20 KB | `Open` |
| `Book[]`, `Floor1[]`, `Residue[]`, `Mapping[]`, `Mode[]` | struct arrays | < 16 KB (`Residue` carries `fixed` cascade/books) | `Open` |
| IMDCT tables `A, B, C, BitRev` × 2 block sizes | `float[]`/`ushort[]` | `(n/2 + n/2 + n/4) × 4 + n/8 × 2` per size: 10.5 KB (2048) + 1.3 KB (256) | `Open` |
| Window slopes `w, wr` × 2 block sizes | `float[]` | `n0 + n1` floats = 9 KB | `Open` |
| `_spec[ch]` spectra | `float[]` per channel | `n1/2` floats = 4 KB × 2 | `Open` |
| `_buf[ch]` IMDCT output | `float[]` per channel | `n1` floats = 8 KB × 2 | `Open` |
| `_prev[ch]` saved right halves | `float[]` per channel | `n1/2` floats = 4 KB × 2 | `Open` |
| `_buf2` IMDCT scratch | `float[]` | `n1/2` floats = 4 KB | `Open` |
| `_y[ch]`, `_final[ch]` floor posts | `int[65]` × 2 per channel | 1 KB | `Open` |
| `_partClass` residue scratch | `byte[]` | `ch × n1/2 / min partition size` ≤ 4 KB | `Open` |
| `_output` interleaved frames | `float[]` | `n1 × ch` floats = 16 KB | `Open` |
| `s_floor1Db` | `float[256]` static | 1 KB | type init |
| `BitReader`, `Mode`, spans, `stackalloc uint[32]` in the builder | stack | — | per call / `Open` |
| **Per packet** | — | **0 bytes** | — |

Total ≈ 400-600 KB per decoder, all of it addressed through pointers taken once. The gate (§7.3) decodes 200
packets after two warm-up packets and asserts `GC.GetAllocatedBytesForCurrentThread()` moved by zero — the same
shape as `DecodeTests.cs:596-612` and the FLAC plan §3.11.

---

## 4. The Ogg layer and seeking — `Playback/Playback.Audio.Ogg.cs` (CORE, new named partial)

Role CORE, owner **V**, Wave **3-parallel**, budget **600**. `public static partial class Playback { public static
class Ogg { … } }`. Pure over spans: pages are parsed and CRC-checked in place in the SHELL's byte window, packets
are zero-copy slices of it unless they span a page (then they are assembled into one `_packet` buffer sized at
open), every page's `(offset, granule)` goes into a `PageIndex`, and the seek is a state machine with the FLAC
plan's exact shape — `BeginSeek` / `TryNextProbe` / `Observe` — so a probe count is a fact a unit test asserts
(FLAC plan §3.9's reason for putting the loop in CORE). FLAC-in-Ogg would sit on the same layer later; nothing
here knows the codec except through two callbacks the decoder provides (`PeekFrames`, §3.9, and the header packet
types).

### 4.1 Pages and packets

```csharp
public static partial class Playback
{
    public static class Ogg
    {
        public const int MaxPageBytes = 27 + 255 + 255 * 255;          // 65,307 (RFC 3533 §6; Symphonia page.rs:17)
        public const int HeaderBytes = 27;
        public const byte FlagContinued = 0x01, FlagBos = 0x02, FlagEos = 0x04;

        public enum PageResult : byte { Ok, NotSync, Truncated, BadCrc, BadVersion }

        /// <summary>One parsed page header. <see cref="Granule"/> is −1 when no packet ends on the page.</summary>
        public struct Page
        {
            public int At;                 // offset of "OggS" in the window
            public int HeaderLen;          // 27 + Segments
            public int BodyLen;            // Σ lacing
            public long Granule;
            public uint Serial, Sequence;
            public byte Flags, Segments;
            public readonly int Length => HeaderLen + BodyLen;
            public readonly bool Continued => (Flags & FlagContinued) != 0;
            public readonly bool Eos => (Flags & FlagEos) != 0;
        }

        /// <summary>Parse the page at <paramref name="win"/>[<paramref name="at"/>]. Truncated ⇒ the caller needs more
        /// bytes at the same offset; BadCrc/NotSync ⇒ the caller scans on by one byte. The CRC is over the whole page
        /// with the checksum field zeroed (libogg framing.c:642-729).</summary>
        public static PageResult TryParsePage(ReadOnlySpan<byte> win, int at, out Page p)
        {
            p = default;
            if (at + HeaderBytes > win.Length) return PageResult.Truncated;
            if (!win.Slice(at, 4).SequenceEqual("OggS"u8)) return PageResult.NotSync;
            if (win[at + 4] != 0) return PageResult.BadVersion;
            int segs = win[at + 26];
            int hdr = HeaderBytes + segs;
            if (at + hdr > win.Length) return PageResult.Truncated;
            int body = 0;
            for (int i = 0; i < segs; i++) body += win[at + HeaderBytes + i];
            if (at + hdr + body > win.Length) return PageResult.Truncated;
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(win.Slice(at + 22, 4));
            if (Crc32(win.Slice(at, hdr + body), crcFieldAt: 22) != crc) return PageResult.BadCrc;
            p.At = at; p.HeaderLen = hdr; p.BodyLen = body;
            p.Flags = win[at + 5];
            p.Granule = BinaryPrimitives.ReadInt64LittleEndian(win.Slice(at + 6, 8));
            p.Serial = BinaryPrimitives.ReadUInt32LittleEndian(win.Slice(at + 14, 4));
            p.Sequence = BinaryPrimitives.ReadUInt32LittleEndian(win.Slice(at + 18, 4));
            p.Segments = (byte)segs;
            return PageResult.Ok;
        }

        /// <summary>The next valid page at or after <paramref name="from"/>: a byte scan for "OggS" (a 4-byte compare
        /// per candidate; the CRC rejects audio data that happens to contain the pattern — stb <c>vorbis_find_page</c>
        /// :4561-4629). −1 when none; <paramref name="truncatedAt"/> ≥ 0 when a page STARTS in the window but does
        /// not end in it (the caller refills from there).</summary>
        public static int FindPage(ReadOnlySpan<byte> win, int from, out Page p, out int truncatedAt)
        {
            p = default; truncatedAt = -1;
            for (int i = from; i + 4 <= win.Length; i++)
            {
                if (win[i] != (byte)'O') { int j = win[(i + 1)..].IndexOf((byte)'O'); if (j < 0) return -1; i += j; if (i + 4 > win.Length) return -1; }
                PageResult r = TryParsePage(win, i, out p);
                if (r == PageResult.Ok) return i;
                if (r == PageResult.Truncated) { truncatedAt = i; return -1; }
            }
            return -1;
        }

        // CRC-32, poly 0x04c11db7, direct (non-reflected), init 0, no final xor (RFC 3533 §6; libogg framing.c:110-113).
        // Slicing-by-8 tables built at type init from the polynomial — the same construction as Flac.Crc16.
        static readonly uint[] s_crc = BuildCrc32Tables();
        public static uint Crc32(ReadOnlySpan<byte> page, int crcFieldAt) { /* slicing-by-8, the 4 CRC bytes read as 0 */ }
    }
}
```

**Packets.** `PacketCursor` walks a page's lacing table: a run of 255s ends with the first value < 255; a packet
that ends exactly at a 255 is closed by a 0 lacing value (RFC 3533 §5); a page whose last lacing value is 255
leaves an open packet that the next page's body (flag `0x01`) continues. Within a page a packet is
`win.Slice(bodyStart + sum, len)` — zero copy. A spanning packet is copied into `_packet` (64 KiB at open; the
setup header, the largest packet a Vorbis stream carries, is ~4 KB; a packet larger than the buffer is a
`PacketResult.TooLarge`, logged once) — libogg's `_packetout` `while (size == 255)` gather (`framing.c:966-978`).
A page with a sequence gap after the previous one ends any open packet (libogg's `0x400` hole marker,
`:826-838`) so a corrupt page costs one packet, not the stream.

```csharp
public static partial class Playback
{
    public static class Ogg
    {
        /// <summary>The reader the SHELL drives: it owns no bytes, only the cursor into the SHELL's window and the
        /// spanning-packet buffer. <c>NextPacket</c> answers a span (zero-copy or assembled), and records every page
        /// it passes in the index. Zero allocation after construction.</summary>
        public sealed class Reader
        {
            readonly byte[] _packet = new byte[MaxPageBytes];
            int _packetLen;                 // bytes of an open (spanning) packet held in _packet
            public readonly PageIndex Index = new(4096);
            public long WindowOffset;       // absolute offset of window[0]
            public int Cursor;              // next unread byte in the window
            Page _page; int _seg, _bodyPos; // the page being walked, the next lacing index, the byte after the last packet
            uint _serial; bool _haveSerial; uint _expectSeq;
            public long LastGranule = -1;   // granule of the last page whose last packet was returned
            public int MaxPageSeen = 4096;  // grows as pages are parsed; the probe window is sized from it (§4.2)

            public enum Next : byte { Packet, NeedMore, Eos, Corrupt }

            /// <summary>Advance to the next packet. <paramref name="packet"/> is valid until the next call or refill.
            /// NeedMore: refill the window from <c>WindowOffset + Cursor</c> (the reader keeps its own spanning bytes,
            /// so the SHELL may discard everything before Cursor).</summary>
            public Next NextPacket(ReadOnlySpan<byte> win, out ReadOnlySpan<byte> packet, out long granuleAtEnd)
            {
                packet = default; granuleAtEnd = -1;
                while (true)
                {
                    if (_seg >= _page.Segments)                                    // need a page
                    {
                        int at = FindPage(win, Cursor, out Page p, out int truncatedAt);
                        if (at < 0) { Cursor = truncatedAt >= 0 ? truncatedAt : Math.Max(Cursor, win.Length - 3); return Next.NeedMore; }
                        if (!_haveSerial) { _serial = p.Serial; _haveSerial = true; }
                        else if (p.Serial != _serial) { Cursor = at + p.Length; continue; }     // a second logical stream: skipped
                        if (p.Sequence != _expectSeq) _packetLen = 0;                          // a hole: drop the open packet
                        _expectSeq = p.Sequence + 1;
                        if (p.Granule >= 0) Index.Add(WindowOffset + at, p.Granule);
                        if (p.Length > MaxPageSeen) MaxPageSeen = p.Length;
                        _page = p; _seg = 0; _bodyPos = at + p.HeaderLen;
                        if (!p.Continued) _packetLen = 0;                                     // a fresh page cannot continue a packet
                        Cursor = at + p.Length;
                    }
                    // walk lacing values until a packet ends or the page runs out
                    int start = _bodyPos, len = 0;
                    bool ended = false;
                    while (_seg < _page.Segments)
                    {
                        int v = win[_page.At + HeaderBytes + _seg++];
                        len += v;
                        if (v < 255) { ended = true; break; }
                    }
                    ReadOnlySpan<byte> piece = win.Slice(start, len);
                    _bodyPos = start + len;
                    if (!ended)
                    {
                        if (_packetLen + len > _packet.Length) { _packetLen = 0; return Next.Corrupt; }
                        piece.CopyTo(_packet.AsSpan(_packetLen)); _packetLen += len;        // spans into the next page
                        if (_page.Eos) return Next.Eos;
                        continue;
                    }
                    bool lastOnPage = _seg >= _page.Segments;
                    granuleAtEnd = lastOnPage ? _page.Granule : -1;
                    if (lastOnPage && _page.Granule >= 0) LastGranule = _page.Granule;
                    if (_packetLen > 0)
                    {
                        if (_packetLen + len > _packet.Length) { _packetLen = 0; return Next.Corrupt; }
                        piece.CopyTo(_packet.AsSpan(_packetLen));
                        packet = _packet.AsSpan(0, _packetLen + len); _packetLen = 0;
                    }
                    else packet = piece;
                    return Next.Packet;
                }
            }

            /// <summary>Forget the page walk and any spanning bytes: the SHELL re-pointed the window (a seek).</summary>
            public void Reposition(long windowOffset) { WindowOffset = windowOffset; Cursor = 0; _seg = 0; _page = default; _packetLen = 0; _expectSeq = 0; }
        }
    }
}
```

The `_expectSeq = 0` in `Reposition` disables the hole check for the first page after a seek (any sequence is
accepted there); the next page must follow it.

### 4.2 The page index and the seek planner

**`PageIndex`**: `(long Offset, long Granule)` pairs (16 bytes), sorted by offset, capacity 4,096 (a 4-minute
song at oggenc's 4-8 KB pages is 1,500-3,000 pages; at ffmpeg's 1 s pages 240). `Add` is an append when the
offset is past the last entry (the common case: playing forward) and a binary-search insert otherwise (a probe
into unseen territory); a duplicate offset is ignored. When full, every other entry is dropped (`Decimate`) —
coverage stays uniform and the array never grows. `Bracket(granule, out lo, out hi)` answers the tightest known
pair around a target in O(log n). It is built from playing as much as from probing: after the first play-through
of a track every seek in it resolves with zero probes beyond the landing window; and it lives in the reader, so a
gapless-prepared next track already has its first pages indexed when it starts.

**The planner.** The same three-tier shape as `Flac.SeekPlan` (§1.1; FLAC plan §3.9) with granules instead of
sample numbers and pages instead of frames:

```csharp
public static partial class Playback
{
    public static class Ogg
    {
        public enum SeekTier : byte { Index, Estimate, Bisect, Linear }
        public enum ProbeResult : byte { Continue, Found, NoPage }

        /// <summary>The seek state machine. Drivable by anything that can read bytes at an offset; the loop lives here
        /// so the probe count is a unit-test fact. Buffers: none.</summary>
        public struct SeekPlan
        {
            public long Target;                     // the sample asked for (granule domain)
            public long Offset;                     // best known page start at or before the page holding Target
            public long OffsetGranule;              // that page's granule (−1 = unknown; the page before it ends the previous samples)
            public bool Resolved;                   // Offset is the page to resume from (§4.4)
            public int Probes;                      // windows the caller had to read
            public SeekTier Tier;
            public long Lo, LoGranule, Hi, HiGranule; // the bracket: Lo/Hi page starts, granules at those pages
            public int WindowBytes;                 // how much to read at each probe
            public int MaxProbes;
        }

        /// <summary>The probe window: enough to hold at least one whole page after the landing point plus the page
        /// straddling it. 48 KiB on oggenc-shaped files (4-8 KB pages); grows with the largest page seen; never
        /// more than 192 KiB. This is also the first range the stream layer fetches (§5.2).</summary>
        public static int ProbeWindow(int maxPageSeen) => Math.Clamp(4 * maxPageSeen, 48 * 1024, 192 * 1024);

        /// <summary>Plan a seek to <paramref name="target"/> samples. The index narrows first; a stream with no
        /// known length or total can only go linear.</summary>
        public static SeekPlan BeginSeek(PageIndex index, long firstAudioPage, long streamLength, long totalGranules,
                                         long target, int maxPageSeen, int maxProbes = 8)
        {
            SeekPlan p = default;
            p.Target = target < 0 ? 0 : target;
            p.Lo = firstAudioPage; p.LoGranule = 0;
            p.Hi = streamLength; p.HiGranule = totalGranules;                    // −1 when unknown
            p.WindowBytes = ProbeWindow(maxPageSeen);
            p.MaxProbes = maxProbes;
            if (index.Bracket(p.Target, out long lo, out long loG, out long hi, out long hiG))
            {
                if (lo >= p.Lo) { p.Lo = lo; p.LoGranule = loG; }
                if (hi < p.Hi) { p.Hi = hi; p.HiGranule = hiG; }
                p.Tier = SeekTier.Index;
            }
            else p.Tier = p.HiGranule > 0 ? SeekTier.Estimate : SeekTier.Linear;
            p.Offset = p.Lo; p.OffsetGranule = p.LoGranule;
            // Two adjacent indexed pages around the target: no probe at all — the page is known.
            p.Resolved = p.Tier == SeekTier.Index && index.Adjacent(p.Lo, p.Hi);
            return p;
        }

        /// <summary>The next window to read: [offset, offset + WindowBytes). False when done — resolved, out of
        /// probes, or a bracket small enough to scan linearly from Lo.</summary>
        public static bool TryNextProbe(ref SeekPlan plan, out long offset)
        {
            offset = plan.Offset;
            if (plan.Resolved || plan.HiGranule <= 0 || plan.Probes >= plan.MaxProbes) return false;
            if (plan.Hi - plan.Lo <= plan.WindowBytes) { plan.Tier = SeekTier.Linear; return false; }
            // Interpolate inside the CURRENT bracket: every Observe moved Lo or Hi to a real page with a real granule,
            // so the second estimate is already corrected by what the first one saw (libvorbisfile's bisect formula,
            // vorbisfile.c:1464-1468, with the bracket doing what stb's explicit probe-1 error term does, :4756-4761).
            long est = plan.Lo + (long)((double)(plan.Target - plan.LoGranule) / (plan.HiGranule - plan.LoGranule) * (plan.Hi - plan.Lo));
            long backoff = plan.WindowBytes / 4;                                 // land BEFORE the page: the scan runs forward
            if (plan.Probes >= 2) est = plan.Lo + (plan.Hi - plan.Lo) / 2;      // plain bisection after two estimates: bounded, not clever
            offset = Math.Clamp(est - backoff, plan.Lo, Math.Max(plan.Lo, plan.Hi - plan.WindowBytes));
            plan.Probes++;
            plan.Tier = plan.Probes > 2 ? SeekTier.Bisect : SeekTier.Estimate;
            return true;
        }

        /// <summary>Feed the window read at <paramref name="windowOffset"/> back. Every page in it goes into the index;
        /// the FIRST page whose granule ≥ Target and whose predecessor's granule &lt; Target resolves the plan (Symphonia
        /// <c>inspect_page</c>: a probe parses headers, decodes nothing). Otherwise the bracket narrows to the side the
        /// target is on and the byte error is remembered.</summary>
        public static ProbeResult Observe(ref SeekPlan plan, PageIndex index, ReadOnlySpan<byte> window, long windowOffset)
        {
            int from = 0; bool any = false; long prevAt = -1, prevG = -1;
            while (true)
            {
                int at = FindPage(window, from, out Page p, out _);
                if (at < 0) break;
                any = true;
                long abs = windowOffset + at;
                if (p.Granule >= 0)
                {
                    index.Add(abs, p.Granule);
                    if (p.Granule >= plan.Target)
                    {
                        // this page ends at/after the target; the previous page (in the window or the bracket's Lo) ends before it
                        long startG = prevG >= 0 ? prevG : plan.LoGranule;
                        if (startG <= plan.Target || prevAt < 0)
                        {
                            plan.Offset = prevAt >= 0 ? prevAt : abs;            // resume one page early so the primed packet precedes the target's page (§4.4)
                            plan.OffsetGranule = prevAt >= 0 ? prevG : -1;
                            plan.Hi = abs; plan.HiGranule = p.Granule;
                            plan.Resolved = true;
                            return ProbeResult.Found;
                        }
                        plan.Hi = abs; plan.HiGranule = p.Granule;
                        return ProbeResult.Continue;
                    }
                    plan.Lo = abs; plan.LoGranule = p.Granule;
                    plan.Offset = abs; plan.OffsetGranule = p.Granule;
                    prevAt = abs; prevG = p.Granule;
                }
                from = at + p.Length;
            }
            if (!any) return ProbeResult.NoPage;
            return ProbeResult.Continue;                       // every page in the window ended before the target: Lo moved up
        }
    }
}
```

`Adjacent(lo, hi)` is true when the index holds no page between the two and both came from a contiguous parse
(the reader marks contiguity when it adds consecutive pages) — the "seek back into what we played" case that
costs nothing. A `NoPage` window (no `OggS` in 48 KiB — only possible on a corrupt file, since a page is at most
65,307 bytes and the window grows to four times the largest page seen) makes the SHELL read the next window
forward once and then give up the seek with `−1`, never loop.

### 4.3 Resuming at a page boundary — prime one packet, count with `PeekFrames`, land sample-exact

After `Observe` resolves, the SHELL reads a window at `plan.Offset` (the page BEFORE the one that ends past the
target, when the window held one; else that page itself) and calls the decoder like this:

```
   window ──▶ Reader.Reposition(offset)
   1. NextPacket → the first packet that BEGINS in the window (a continued fragment at the start is skipped:
      Reposition dropped it, and the page flag says so)
   2. Decoder.ResetLapping(); DecodePacket(p1)  → 0 frames (the primed packet; its right half is now `prev`)
   3. From here every packet returns prev/4 + cur/4 frames. Their absolute position is fixed by the FIRST page
      granule the reader reaches: run PeekFrames over the packets of that page (one byte each, no decode) to sum
      the frames of the packets ending on it; first sample of the first output packet = pageGranule − Σframes.
      (libvorbis ov_pcm_seek's vorbis_synthesis_trackonly, :1694-1757; stb stb_vorbis_seek_frame's
      peek_decode_initial, :4880-4917.)
   4. Decode forward, dropping whole packets whose range ends before Target and `Target − start` samples inside
      the packet that holds it. Typically 1-3 packets are decoded and discarded (a page holds ~0.1-1 s).
```

The resume never reads before `plan.Offset`: priming with the packet BEFORE the first output packet is exactly
what the overlap needs, and any packet works as the primer as long as it immediately precedes the first one whose
output is used (§1.6, spec §1.3.2). When `Observe` could not include the previous page (the window started at
the target's page), the first output packet on that page is discarded as well — the position arithmetic is the
same, one packet (≤ 46 ms) later.

### 4.4 Gapless from the granule positions

| Field of `GaplessInfo` | Source | How |
|---|---|---|
| `ExactFrames` | the LAST page's granule (spec §A.2: it may be less than the decoded total to truncate the end; libvorbis `block.c:864-936` trims `extra = decoded − granule` at EOS) | the stream layer fetches the file's tail window (`max(WindowBytes, 64 KiB)`) at open, in parallel with the first body range (§5.2); `FindPage` backwards from the end (the last `OggS` whose page ends at `Length`) gives the granule. Missing at `TryOpen` ⇒ `−1` (the declared duration; the tail is retried lazily and the decoder still trims by the EOS granule when it arrives at the end) |
| `LeadInFrames` | the FIRST audio page: `Σ PeekFrames(packets ending on it) − granule` when positive (libvorbis "deal with initial packet state", `block.c:831-843`; stb does not implement it — "lossless sample-truncation at beginning ignored", `:17`) | computed once at open from the header window the decoder already holds |
| `TrailPadFrames` | 0 — the end trim is expressed through `ExactFrames` | — |
| `TailKnown` | `ExactFrames ≥ 0` | — |

All four in MIX-domain frames (the adapter converts, FLAC plan §4.3's `ToMix`). Today's `GaplessInfo.None`
(`Playback.Audio.cs:770`) becomes the truth, and `PcmAudioPlayer.BuildTrimmedVoice` wraps the voice in
`TrimmingSource` with the exact total so the butt-join is sample-exact — the thing `gapless-findings.md:174`
called "UNCERTAIN" for NVorbis.

### 4.5 "Seek to X": probes and requests under each strategy

Assumptions: a 4-minute 320 kbit/s Spotify track (≈ 9.6 MB), oggenc-shaped pages (4-8 KB), the stream layer of
§5 (one 48 KiB probe range, then a sequential fill), the measured estimate error of ±1.5 s worst case on the
synthetic VBR file (§1.6) — i.e. ±60 KB at 320 kbit/s — and `ProbeWindow = 48 KiB` with a 12 KiB back-off.

| Seek | Estimate only (no index) | With the index | Bisection (the fallback) | NVorbis today (§1.4) |
|---|---|---|---|---|
| Forward into unplayed bytes, first seek in the track | 1 probe: the window holds the target page when the error < 36 KiB (≈ 0.9 s at 320 k; the pink-noise files land within +1.4 s → 1-2 probes); 2 probes typical on a VBR song | same (nothing indexed there yet) | ≤ 8 probes, each halving; stops at one window | 3-6 page probes (6-24 `Stream.Read`), each outside the held chunk a range GET, + 2 full packet decodes |
| Back into what has played | — | **0 probes**: the bracket is two adjacent indexed pages; one window read at the known page (served from the ring or the disk cache, §5) | — | "a linear scan over the WHOLE index" + 1-3 page reads |
| Second seek into a region probed once | — | 0-1 probes: the earlier probe indexed every page in its window | — | as above |
| Repeated scrubbing (10 seeks in 3 s) | ≤ 2 each | converges to 0-1 each as the index fills | — | 3-6 each, and the index is append-only and non-monotonic |
| Pathological (a −60/0 dB VBR square wave, 60 s) | 2 probes (error 1.4 s = 56 KB > 36 KiB on one side) | 0-2 | ≤ 8 | 6-16 |
| Requests per seek (stream layer, §5.3) | 1-2 | 0-1 | ≤ 8 | 3-6 |

Every probe is one range request of `ProbeWindow` bytes issued straight at the estimate on the network thread with
the previous in-flight range cancelled (§5.2) — no 128 KiB chunk alignment, no forward drain, no decode. The
worst case is bounded by `MaxProbes = 8` and then a linear page scan inside one window; NVorbis's worst case is
`O(pages)`.

---

## 5. The CDN stream layer — the decoder and the stream are one problem

Christos: "fast playback is a priority (play from head file) but then seeking should be very fast like YouTube
Music". YouTube Music's client fetches a small range at the seek point first and widens after; the audio starts
before the buffer is "full". This section makes Wavee's stream layer do the same, on the facts below.

### 5.1 What exists, what 0.2.9 had, what the references do

| Layer | Wave 3 today (`Spotify.Audio.cs`, `Playback.Audio.cs`) | 0.2.9 (`_old`, `Wavee.Sdk/Streams`) | librespot (`audio/src/fetch`) | go-librespot (`audio/`) |
|---|---|---|---|---|
| Instant start | none — the first body range (128 KiB) is fetched synchronously on the decode thread after resolve + key | **the clear head file**: `GET https://heads-fa-tls13.spotifycdn.com/head/{fileIdHex}`, up to **80 KiB**, no auth, immutable, LRU 32 (`HeadFileClient.cs:16, 26-27, 44`); `SpotifyAudioStream.Read` copies `pos < _headLen` straight from the head with no decrypt (`:237-281`), the body attaches later (`IsBodyAttached`, `WaitForBodyOrSeekIntoHead`, `:180-183, 409-418`) | none: the first request is `stream_from_cdn(url, 0, 64 KiB)` and `open` returns when its `Content-Range` arrives (`mod.rs:437-506`) | none: chunk 0 (512 KiB) is fetched synchronously in the constructor (`chunked-reader.go:105-135`) |
| Range size | 128 KiB fixed (`CtrStream.ChunkBytes`) | `MinFetchBytes = 64 KiB`; the read-ahead window is fetched as ONE range (`RangedHttpSource.cs:182, 446-484`) | `minimum_download_size = 64 KiB` (`mod.rs:103`) | `DefaultChunkSize = 512 KiB` (`:22`) |
| Read-ahead | none | `ReadAheadPolicy.Compute`: **15 s metered / 30 s / 600 s when throughput ≥ 3× bitrate**, floor 256 KiB, per-stream cap 12 MiB of a 24 MiB total shared with the prepared next track; a `LongRunning` task, 100 ms ticks (`:14-61, 186-187, 312-323`) | `read_ahead_before_playback = 1 s`, `read_ahead_during_playback = 5 s` (`mod.rs:110-111`); `length_to_request = length + read_ahead_s × bytes_per_second` (`mod.rs:563-573`); prefetch when pending < `max(4 × ping_s × bytes_per_s, ping_s × throughput)` (`receive.rs:504-516`) | `PrefetchCount = 3` chunks (1.5 MB) ahead of every read (`:23, 252-262`) |
| Latency model | none | throughput over a 2 s window (`:353-370`) | ping = median of the last 3 request-to-headers times, capped 1.5 s; throughput `(old + new)/2` (`receive.rs:75-82, 279-339`) | per-chunk latency telemetry only (`latency.go`) |
| Seek | position write; next `Read` fetches synchronously | position write (`SpotifyAudioStream.cs:465-477`); the chunk dictionary is **never cleared** (a scrub-and-return is free); no cancel of an in-flight fetch; `_fetchGate = SemaphoreSlim(2)` | flips to "random access" mode when the byte is not downloaded; **no cancel**; the next `read` requests the range and waits (`mod.rs:626-669, 575-608`) | position arithmetic only (`:332-358`) |
| Parallel ranges | 1 (synchronous) | 2 | **1** (`Semaphore::new(1)`, `mod.rs:518`) | 3 (the prefetch goroutines) |
| Disk cache | none | `ChunkDiskCache`: per file id, sharded dir, `.enc` (raw encrypted bytes at `chunk × 64 KiB`) + `.map` (`WAC2` header + 33 bytes per chunk: committed flag + SHA-256), verified on every read; **reserve `max(5 GiB, 5 % of the volume)`**; budget modes fixed / drive-share (`clamp(total/10, 16 GiB, 128 GiB)`) / unlimited; LRU by `.map` access time, trim to 90 %; a `BelowNormal` writer thread (`ChunkDiskCache.cs:69-83, 342-351, 466-490, 605-625, 783-814`); CHANGELOG 0.2.5: "writing a streamed chunk … used to hash it, check free disk space and fsync — all synchronously on the audio decode thread" | a `NamedTempFile` pre-sized to the file, written at offset; the complete file saved to the cache only at 100 % (`mod.rs:524-527`; `receive.rs:356-419`) | complete files only, atomically renamed, LRU by size (`cache.go:101-140`); tried before storage-resolve (`player.go:845-850`) |
| Next track | `Prepare` opens the stream; no bytes fetched until the engine pulls | `PrepareNextCoreAsync` opened the next body and its read-ahead shared the 24 MiB cap | the player preloads `read_ahead_during_playback × bytes_per_second` before starting (`player.rs:2464-2484`) | — |
| HTTP client | one static `HttpClient`, `SocketsHttpHandler { MaxConnectionsPerServer = 4, PooledConnectionLifetime = 5 min }` (`Spotify.Audio.cs:745-752`) | one `HttpClient` per source | one `hyper` client per session ("configuring TLS is expensive and should be done once per process", `http_client.rs:148`) | one `*http.Client` for the player |

**What to keep verbatim:** `ChunkDiskCache` (968 lines, shipped, tested, with the free-space reserve and the
background writer — the SDK is out of scope for 0.3 and stays); the head host and the 80 KiB head; the
seconds-based window with the metered/near-realtime/fast tiers; the never-return-zero read. **What changes:** the
stream is no longer a `Stream` with one chunk; it is a ring of 64 KiB slots filled by one named network thread
with a cancellable in-flight request, addressed by `ReadAt`, backed by the head, the disk cache and the CDN in
that order.

### 5.2 The design

```
 t0 press play ─────────────────────────────────────────────────────────────────────────────────────▶ time
 │
 ├─ reducer: Load(epoch n)  ──▶  pump chain (H)
 │      │
 │      ├─ Spotify.Audio.Open (F), three requests IN PARALLEL on api threads:
 │      │     ├─ GET head/{fileId}     (80 KiB clear, no auth)            ~ 1 RTT   ─┐
 │      │     ├─ storage-resolve       (mirror list, TTL)                 ~ 1 RTT    ├─ Task.WhenAll
 │      │     └─ audio key (AP 0x0c/0x0d, or the deriver seam)            ~ 1 RTT   ─┘
 │      │     the head lands first or with the others; the decoder does NOT wait for the key
 │      │
 │      ├─ t0 + ~1 RTT: VorbisAudioDecoder.TryOpen over the head: id + comment + setup headers (~4 KB) parsed,
 │      │     first audio pages decoded from the head's clear bytes → first frames into the 1 s ring → SOUND
 │      │     (the head holds ~2 s at 320 kbit/s, ~6 s at 96)
 │      │
 │      ├─ t0 + ~1 RTT (key + resolve): the network thread starts:
 │      │     ├─ range [headLen − 64 KiB ∧ 64 KiB-aligned, +ring window)   the body, from where the head ends
 │      │     └─ range [Length − 64 KiB, Length)                            the tail: last page's granule → ExactFrames
 │      │     chunk 0 lands → SPLICE PROOF: decrypt(chunk0)[0..headLen) must equal head[0..headLen) byte for byte
 │      │     (also proves the key: OggS at 0xa7 — Ctr.Validates); a mismatch drops the head and restarts at 0
 │      │
 │      ├─ the decoder's ReadAt(offset) crosses headLen: served from the ring — no splice code in the decoder,
 │      │     the head IS the first headLen bytes of the same byte stream
 │      │
 │      └─ steady state: the ring holds `ReadAheadSeconds × bytesPerSecond` ahead of the decoder's cursor; every
 │            completed 64 KiB chunk is written through to the disk cache on its writer thread
 │
 ├─ seek to X (epoch n+1):  reducer Seek effect → H: RingSource.Retarget(estimate, window) → in-flight range
 │      CANCELLED (HttpRequestMessage's token; HTTP/2 RST_STREAM); the probe range fetched FIRST; the decoder's
 │      SeekPlan reads it; found → sequential fill resumes from the found page; the ring drops slots behind the
 │      new cursor except the last 256 KiB (a scrub-back is free)
 │
 └─ last EndingSoonMs (fade + 8 s): Prepare(next) → head + resolve + key + first range + tail for the NEXT track,
        its own RingSource, budget-shared; gapless hand-off at HandOffAt; instant "next" click
```

**The three backing stores, in lookup order, all behind one `ReadAt`:**

1. **Head** — `byte[] head` (≤ 80 KiB, clear). Serves `[0, headLen)` until chunk 0 proves it (then it is dropped;
   the ring's chunk 0 serves).
2. **Ring** — `RingSource` (H): `N` slots of 64 KiB (the disk cache's granularity, so a completed slot is one
   cache chunk), `N = ceil(ReadAheadSeconds × bytesPerSecond / 64 KiB)`, capacity in SECONDS: 30 s default, 10 s
   metered, whole-file (capped 16 MiB) when measured throughput ≥ 3× the byte rate — 0.2.9's tiers. For 320 kbit/s
   that is 1.2 MB / 19 slots; for a 24-bit FLAC (§1.5's 1.8 Mbit/s hint) 6.8 MB / 105 slots; **never 128 KiB**.
3. **Disk cache** — `ChunkDiskCache` unchanged: `TryReadChunk(fileId, chunkIndex)` before any network fetch;
   `WriteChunk` after every complete slot; a completed file (every chunk committed) is served with zero requests
   on replay and no storage-resolve or key is needed to READ it (the bytes are still encrypted; the key is still
   fetched for decryption — as go-librespot, `cache.go:1-8`).

**The fetcher** (F, `Spotify.Audio.Fetcher`): one consumer task on the pool (P10: one consumer, strict sequence —
no dedicated OS thread), a bounded
queue of `RangeRequest { long Start, End; uint Epoch; bool Probe }` (C8: capacity 8, a new probe replaces a queued
probe), at most **2 ranges in flight per file** (the sequential fill + one probe/tail) and **4 per host**
(`MaxConnectionsPerServer = 4` stays; HTTP/2 multiplexes them on one connection when the CDN offers it —
`SocketsHttpHandler.EnableMultipleHttp2Connections = false`, `VersionPolicy = RequestVersionOrLower` on
`HttpRequestMessage { Version = 2.0 }`). The sequential fill requests are `min(ring free, 512 KiB)` long (one
range per ~1.5 s of audio at 320 k — larger than 0.2.9's 64 KiB minimum, smaller than go-librespot's 512 KiB
chunk when the ring is nearly full); a probe is `ProbeWindow` (§4.2, 48 KiB) long. Throughput and ping are
measured as librespot does: ping = time to headers, median of 3; throughput = bytes / body time, `(old + new)/2`.

**Cancellation is the new thing.** `RangeRequest` carries the epoch; `Retarget` cancels the in-flight request's
`CancellationTokenSource` (the socket is dropped — on HTTP/2 a `RST_STREAM`, on HTTP/1.1 the pooled connection is
closed and a new one opened; both cheaper than draining the rest of a 512 KiB range at CDN speed) and empties the
queue. librespot and 0.2.9 never cancel (§5.1); a scrub of ten seeks in three seconds then queues ten fetches
behind each other. Here the tenth seek's probe is the only request in flight.

**`ReadAt` never blocks on the network thread's lock for long and never returns 0 except at EOF**: it copies from
whichever store holds `[offset, offset + n)`; on a miss it registers the want (`_wantOffset`, a volatile long the
fetcher reads to re-prioritize), then waits on a `ManualResetEventSlim` pulsed by every landed range, with the
same 8 s bound and the same degrade-to-blocking-fetch rule as today's `Prefetching.Read` (`:1780-1797`). A decoder
waiting in `ReadAt` is a buffer underrun — the log line says which store missed and by how much.

### 5.3 The code — `+Spotify.Audio.Stream.cs` (F) and `RingSource` (H)

```csharp
public static partial class Spotify
{
    public static partial class Audio
    {
        /// <summary>What Open hands the pump instead of a Stream: the stores and the fetcher for ONE file. Disposal
        /// cancels the fetcher's work for this file and returns the ring's slots.</summary>
        public sealed class Body : IDisposable
        {
            public readonly long Length;            // container bytes after the 0xa7 header
            public readonly long DurationMs;
            public readonly Format Fmt;
            public readonly float GainDb;
            public readonly string FileIdHex;
            public readonly int BytesPerSecond;     // Length × 1000 / DurationMs, the seek estimate's slope
            byte[]? _head; int _headLen;            // clear bytes [0, headLen), dropped once proven
            readonly byte[]? _key; readonly long _skip;
            readonly Fetcher _fetcher; readonly string[] _mirrors;
            readonly ChunkDiskCache? _disk;
            internal RingSource? Ring;              // H's ring, attached by the pump

            /// <summary>Copy [offset, offset+dst.Length) from the head, the ring or the disk cache. Returns bytes
            /// copied (short when a store ends), 0 only at EOF, −1 on a fault. Blocking only in the ring's wait.</summary>
            public int ReadAt(long offset, Span<byte> dst, uint epoch)
            {
                if (offset >= Length || dst.Length == 0) return 0;
                if (_head is not null && offset < _headLen)
                {
                    int n = (int)Math.Min(dst.Length, _headLen - offset);
                    _head.AsSpan((int)offset, n).CopyTo(dst);
                    return n;                                   // the ring serves the rest on the next call
                }
                return Ring!.ReadAt(offset, dst, epoch);        // ring → disk → network (§5.3 RingSource)
            }

            /// <summary>Chunk 0 landed: the head must be the same bytes. Proven ⇒ the head is dropped (the ring holds
            /// chunk 0); refuted ⇒ the head is dropped AND the decoder is told to restart at 0 (a stale head file for a
            /// re-encoded file id — never observed, cheap to guard).</summary>
            internal bool ProveHead(ReadOnlySpan<byte> chunk0Plain)
            {
                if (_head is null) return true;
                int n = Math.Min(_headLen, chunk0Plain.Length);
                bool same = chunk0Plain[..n].SequenceEqual(_head.AsSpan(0, n));
                _head = null;
                return same;
            }
        }

        /// <summary>One request on the wire. Epoch-stamped: a landed range for a stale epoch is still stored (bytes
        /// are bytes; the file id is the key) but never waited for.</summary>
        public readonly record struct RangeRequest(long Start, long End, uint Epoch, bool Probe);

        /// <summary>The ONE network thread for audio bytes ("Wavee.AudioFetch"). A bounded queue per file (C8), ≤ 2
        /// in flight per file, ≤ 4 per host. Cancels the in-flight request on Retarget. Measures ping and throughput
        /// the way librespot does (receive.rs:75-82, 279-339).</summary>
        public sealed class Fetcher
        {
            const int QueueDepth = 8;
            const int MaxInFlightPerFile = 2;
            readonly Thread _thread;
            readonly Channel<(Body body, RangeRequest req)> _queue = Channel.CreateBounded<(Body, RangeRequest)>(
                new BoundedChannelOptions(QueueDepth) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            CancellationTokenSource? _inFlight;     // the sequential fill; a probe has its own
            public int PingMs { get; private set; } = 500;            // librespot initial_ping_time_estimate
            public long BytesPerSecond { get; private set; } = 64 * 1024; // librespot minimum_throughput × 8

            public void Enqueue(Body body, in RangeRequest req) => _queue.Writer.TryWrite((body, req));

            /// <summary>A seek: cancel what is in flight for this body, drop its queued ranges, put the probe first.</summary>
            public void Retarget(Body body, in RangeRequest probe)
            {
                Interlocked.Exchange(ref _inFlight, null)?.Cancel();
                // drain queued entries for this body (bounded: ≤ QueueDepth)
                while (_queue.Reader.TryPeek(out var head) && head.body == body) _queue.Reader.TryRead(out _);
                _queue.Writer.TryWrite((body, probe));
            }

            void Loop()
            {
                while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (_queue.Reader.TryRead(out var item))
                    {
                        var (body, req) = item;
                        if (body.Ring is null || body.Ring.Epoch != req.Epoch && !req.Probe) continue;
                        // disk first: any committed chunks inside [Start, End) are copied, and the range shrinks to the gap
                        long start = req.Start, end = req.End;
                        body.Ring.FillFromDisk(ref start, ref end);
                        if (start >= end) continue;
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        _inFlight = cts;
                        long t0 = Stopwatch.GetTimestamp();
                        int got = body.FetchRange(start, end, cts.Token, out long tHeaders);   // mirrors, retry ladder, decrypt at the true offset (today's TryRange + Ctr.DecryptInPlace)
                        if (got > 0)
                        {
                            PingMs = MedianOf3(PingMs, (int)Stopwatch.GetElapsedTime(t0, tHeaders).TotalMilliseconds);
                            BytesPerSecond = (BytesPerSecond + got * 1000L / Math.Max(1, (long)Stopwatch.GetElapsedTime(tHeaders).TotalMilliseconds)) / 2;
                            body.Ring.Landed(start, got, req.Epoch);        // pulses ReadAt's waiters; writes complete slots through to disk
                        }
                        _inFlight = null;
                    }
                }
            }
        }
    }
}
```

```csharp
public static partial class Playback
{
    public static partial class Audio
    {
        /// <summary>The seconds-sized read-ahead ring, an <see cref="IMediaByteSource"/> for the engine and a
        /// <see cref="ReadAt"/> for the decoder. Slots are 64 KiB (= ChunkDiskCache.ChunkBytes), allocated once per
        /// open from the file's byte rate and the read-ahead policy; the decoder never blocks while the ring holds
        /// [cursor, cursor + demand). Retarget discards forward slots, keeps the last 256 KiB behind the cursor.</summary>
        public sealed class RingSource : IMediaByteSource
        {
            public const int SlotBytes = 64 * 1024;
            const int KeepBehindSlots = 4;                      // 256 KiB: a scrub-back into the last few seconds is free
            readonly Spotify.Audio.Body _body;
            readonly Spotify.Audio.Fetcher _fetch;
            readonly byte[][] _slots;                           // ring of SlotBytes arrays, POH-pinned
            readonly long[] _slotStart;                         // absolute chunk index held by each slot, −1 empty
            readonly int[] _slotFilled;                         // bytes valid in the slot
            readonly ManualResetEventSlim _landed = new(false);
            readonly Lock _gate = new();
            long _cursor;                                       // the decoder's sequential position
            public uint Epoch;                                  // the load/seek epoch this ring serves
            public long? Length => _body.Length;
            public SourceCaps Caps => new() { Seekable = true, KnownLength = true, ExpensiveSeek = false };

            /// <summary>How many seconds to keep ahead: 0.2.9's tiers (RangedHttpSource.ReadAheadPolicy, :43-61),
            /// sized in seconds and converted with the FILE's byte rate, never a byte constant.</summary>
            public static int ReadAheadSeconds(bool metered, long measuredBytesPerSec, int fileBytesPerSec)
                => metered ? 10 : measuredBytesPerSec >= 3L * fileBytesPerSec ? 600 : 30;

            public static int SlotCount(int seconds, int fileBytesPerSec, long fileLength)
            {
                long bytes = Math.Min((long)seconds * fileBytesPerSec, Math.Min(fileLength, 16L << 20));
                return (int)Math.Max(8, (bytes + SlotBytes - 1) / SlotBytes) + KeepBehindSlots;
            }

            /// <summary>The decoder's read. Head → ring → disk → (network wait). Never 0 except at EOF.</summary>
            public int ReadAt(long offset, Span<byte> dst, uint epoch)
            {
                if (offset >= _body.Length) return 0;
                long deadline = Environment.TickCount64 + 8_000;
                while (true)
                {
                    int n = TryCopy(offset, dst);
                    if (n > 0) { Advance(offset + n); return n; }
                    if (epoch != Epoch) return -1;                            // superseded: let the pump discard this decoder
                    Volatile.Write(ref _want, offset);                        // the fetcher reads this to re-prioritize
                    if (!_landed.Wait(4)) { if (Environment.TickCount64 >= deadline) return _body.Length > offset ? -1 : 0; }
                    _landed.Reset();
                }
            }

            /// <summary>A seek: the probe range first, the sequential fill from the found page later
            /// (<see cref="ResumeFrom"/>). Called on the decode thread inside VorbisAudioDecoder.Seek.</summary>
            public void Retarget(long probeOffset, int probeBytes, uint epoch)
            {
                lock (_gate)
                {
                    Epoch = epoch;
                    DropForwardSlots(keepBehind: probeOffset - KeepBehindSlots * SlotBytes);
                }
                _fetch.Retarget(_body, new Spotify.Audio.RangeRequest(probeOffset, Math.Min(_body.Length, probeOffset + probeBytes), epoch, Probe: true));
            }

            /// <summary>After the seek landed on a page: the sequential fill restarts there.</summary>
            public void ResumeFrom(long offset) { _cursor = offset; Advance(offset); }

            // Advance: move the cursor, free slots behind (cursor − KeepBehind), and enqueue the next sequential
            // range [firstMissing, firstMissing + min(free slots, 512 KiB)) when the ring holds less than its window.
            // Landed: copy into slots (a probe's bytes too — they are real bytes at a real offset), pulse waiters,
            // write complete slots through to ChunkDiskCache on its own writer thread.
            // FillFromDisk: for each 64 KiB chunk in [start, end) that ChunkDiskCache.TryReadChunk answers, fill the
            // slot and shrink the range from the ends (the middle stays one range: the CDN cost is per request).

            // IMediaByteSource for the engine's own consumers (the WAV decoder path, tests): sequential over ReadAt
            public bool TryOpen(in DataSpec spec) { _cursor = Math.Max(0, spec.Position); return true; }
            public int Read(Span<byte> dst) { int n = ReadAt(_cursor, dst, Epoch); if (n > 0) _cursor += n; return n; }
            public long Seek(long offset) { _cursor = Math.Clamp(offset, 0, _body.Length); return _cursor; }
            public void Cancel() { _fetch.Retarget(_body, default); }
            public void Close() { _body.Dispose(); }
        }
    }
}
```

**The next-track prefetch** (H, `Prepare`): today `PrepareCoreAsync` opens the next track's stream inside
`EndingSoonMs(fade, dur) = fade + 8 s` (`Playback.Audio.cs:100-107, 370-375, 1112`). With `Body`, `Open` already
issues head + resolve + key + first range + tail; `Prepare` additionally sizes the next ring at `min(30 s, the
remaining budget)` — the two rings share a 24 MiB total as 0.2.9's `TotalReadAheadMemoryCapBytes` did — and the
hand-off at `HandOffAt` finds the next decoder primed with its first pages indexed. A "next" click before the
window is a cold start with the head (≈ 1 RTT to sound).

### 5.4 Requests per scenario

| Scenario | HTTP requests | AP round trips | Time to first audio |
|---|---|---|---|
| **Cold start** (nothing cached) | head (1) ‖ storage-resolve (1) ‖ key; then body range 1 (1) ‖ tail (1) = **4**, the first three in parallel | 1 (key) | ≈ 1 RTT (the head): decode starts before resolve + key complete |
| Cold start, file id in the disk cache (complete) | 0 for bytes; key only | 1 | ≈ the key RTT (bytes local); with a cached key (`Spotify.Audio.cs`'s 256-entry key cache) 0 network at all |
| Cold start, partially cached (a track played to 40 % last week) | head ‖ resolve; the fill skips cached chunks; tail if not cached | 1 | ≈ 1 RTT |
| Seek inside the ring (≤ 30 s ahead, or ≤ 256 KiB back) | **0** | 0 | the decoder's own resume (1-3 packets) |
| Far seek, first time in the region | **1** probe (48 KiB) → typically found; 2 on a VBR miss; ≤ 8 bounded (§4.5); the sequential fill resumes as one more range after the page is found | 0 | ≈ 1 RTT + one probe body (48 KiB ≈ 15 ms at 25 Mbit/s) |
| Far seek into a region played earlier this session | 0-1 (index + ring/disk) | 0 | local |
| Far seek into the disk cache (a previous session) | 0 (the probe range is answered from disk) | 0 | local |
| Scrubbing (10 seeks / 3 s) | ≤ 1 in flight at any time; ≤ 10 total, each cancelling the last | 0 | per seek as above |
| Next track (prepared in the last 8 s) | head ‖ resolve ‖ key; range 1 ‖ tail — all before the boundary | 1 | 0 at the boundary (gapless); a click inside the window is instant |
| Next track, "next" clicked with no prepare | as cold start | 1 | ≈ 1 RTT |

Versus today: cold start = resolve + key + a synchronous 128 KiB range on the decode thread ≈ 3 RTT + 128 KiB
before sound; a far seek = 3-6 synchronous ranges (§1.4); no cache; a "next" = a cold start.

### 5.5 The seam between the two owners, and the `IMediaByteSource` contract

| Owner F — `+Spotify.Audio.Stream.cs` (new named partial, ~700 lines) | Owner H — `Playback.Audio.cs` (+260, a request) |
|---|---|
| `Body` (the stores' owner; `ReadAt` head → ring; `FetchRange` = today's `TryRange` + mirrors + `Ctr.DecryptInPlace` at the true offset; `ProveHead`; the tail granule request) | `RingSource : IMediaByteSource` (slots, `ReadAt` wait, `Retarget`, `ResumeFrom`, `Advance`, disk write-through calls) |
| `Fetcher` (the `Wavee.AudioFetch` thread, the bounded queue, in-flight cancel, ping/throughput) | `VorbisAudioDecoder` over `Ogg.Reader` + `Vorbis.Decoder` + `RingSource.ReadAt` (§6) |
| `Head(fileIdHex)` — exists (`:860-881`); becomes part of `Open`'s `Task.WhenAll` with resolve + key | `Prepare`: the next `Body` + ring under the shared budget; the hand-off unchanged (`HandOffAt`) |
| `Open(...)` returns `Opened` with `Body` instead of `Stream`; `CtrStream` is deleted (no legacy path) | `ApplySkip`/`SetSkip` go: `Body.Length` and offsets are already container-relative (the 0xa7 skip is inside `Body`) |
| `DiskCache` adapter: `ChunkDiskCache` constructed from settings as 0.2.9's `AudioBodyDiskCache.FromSettings` did (`AudioBodyDiskCache.cs:17-27`), keyed by file id hex | `Prefetching` stays for module streams and local files (their `Stream`s), loses its Spotify use |

**The `IMediaByteSource` contract does not change** — it is the engine's (`MediaSeams.cs:144-160`) and changing it
is engine work. `RingSource` implements it faithfully (sequential `Read`/`Seek`, `Cancel` non-blocking, `Length`
known) so the engine's own consumers and the tests keep working, and ADDS `ReadAt(offset, span, epoch)` +
`Retarget` + `ResumeFrom` as app-level members the Vorbis/FLAC adapters reach by type-testing `src is RingSource`
(the FLAC adapter's `ReadWindowAt`, `Playback.Audio.cs:2354-2361`, gets the same fast path in one line). A local
file (`FileByteSource`) keeps the engine's `Seek + Read` path. Stated exactly, the app's added surface:

```csharp
public interface IRandomAccessBytes   // Playback.Audio.cs; implemented by RingSource only
{
    int ReadAt(long offset, Span<byte> dst, uint epoch);       // >0 copied, 0 EOF, −1 superseded/fault; blocks bounded
    void Retarget(long probeOffset, int probeBytes, uint epoch); // cancel in-flight, probe first
    void ResumeFrom(long offset);                               // sequential fill from here
    uint Epoch { get; }
}
```

### 5.6 Tests (`Wavee.Tests/AudioStreamTests.cs`, owner F for `Body`/`Fetcher`, H for `RingSource`)

| Test | What it pins |
|---|---|
| `Cold_start_issues_head_resolve_key_in_parallel_then_two_ranges` | a `FakeCdn` (an `HttpMessageHandler` counting requests per URL and range) + a fake key: exactly 4 HTTP requests; the head request's start time precedes the key's completion; the first `ReadAt(0)` is served before the key lands |
| `Head_splice_is_byte_exact` | the fake CDN serves `bytes[]`; the head is `bytes[0..80 KiB]`; after chunk 0 lands `ProveHead` is true; a decoder reading `[0, 200 KiB)` through `ReadAt` gets exactly `bytes[0..200 KiB)`; a corrupted head (one flipped byte) is refused and the read still yields `bytes` (from the ring) |
| `Seek_is_one_request_typical_and_bounded` | over the §7 fixtures through `FakeCdn`: `Seek` to 70 % with an empty index ⇒ ≤ 2 range requests; a second seek to 72 % ⇒ 0-1; ten scrubs ⇒ ≤ 10 requests and ≤ 1 in flight (the fake handler asserts concurrency); never > 8 probes |
| `Retarget_cancels_the_in_flight_range` | the fake handler holds a response open; `Retarget` cancels it (the handler observes the token) before the probe is issued |
| `Ring_never_blocks_the_decoder_under_a_fake_clock` | a manual-scheduler `Fetcher` (ranges land only when the test says); the ring holds 30 s; a decoder consuming at 1× realtime under a fake `TickCount` never enters the wait while the fetcher delivers ≥ 1× — and DOES wait, bounded at 8 s, when delivery stops |
| `Ring_slots_size_from_seconds_not_bytes` | `SlotCount(30, 40_000 B/s, …) == 19 + 4`; `SlotCount(30, 225_000 B/s, …) == 104 + 4`; metered ⇒ 10 s |
| `Cache_range_map_round_trips` | write chunks 0, 3, 5 of a 7-chunk file through `ChunkDiskCache` in a temp dir; reopen; `TryReadChunk` answers those three and refuses 1, 2, 4, 6; a flipped byte in `.enc` is refused by the SHA-256 |
| `Cached_file_needs_no_request` | a complete cached file: `FakeCdn` sees 0 requests; the tail granule and the head come from disk |
| `Free_space_reserve_blocks_commits` | `ChunkDiskCache.CanCommit` false when `free − growth < max(5 GiB, 5 %)` (pure, with an injected capacity) — the 0.2.9 rule kept |

---

## 6. The pipeline — `Playback/Playback.Audio.cs` (SHELL, owner H, Wave 3)

### 6.1 What changes in H's file

| Today | Plan | Lines |
|---|---|---|
| `VorbisAudioDecoder` over `NVorbis.VorbisReader` + `ByteSourceStream` + `PullConform` (`:753-816`) | the same class name and factory arm; internals = a byte window + `Ogg.Reader` + `Vorbis.Decoder`, the FLAC adapter's shape (`:2139-2364`) | +120 net (the NVorbis glue goes) |
| `GaplessInfo.None` | `Gapless` from §4.4 | in the above |
| `Prefetching` over `CtrStream` for Spotify (`:1668`) | `RingSource` over `Body` (§5.3); `Prefetching` kept for module/local `Stream`s | +140 |
| `ApplySkip` / `SetSkip` (`:553-565`) | deleted: `Body` is container-relative | −15 |
| `Prepare` opens the next stream | `Prepare` also sizes the next ring under the shared budget | +30 |
| `SpotifySource` logs `kbps` | also logs `head=… tail=… ring=…s` and, per seek, `audio.seek target=… probes=… requests=… tier=… ms=…` | +10 |

### 6.2 `VorbisAudioDecoder : IAudioDecoder` — the adapter

```csharp
public static partial class Playback
{
    public static partial class Audio
    {
        /// <summary>Ogg Vorbis over the CORE Ogg reader and Vorbis decoder (Vorbis plan §3-§4). The same shape as
        /// <see cref="FlacAudioDecoder"/>: a byte window over the byte seam, the CORE over the window, the engine's
        /// resampler at the decode edge. Blocks in ReadAt/Read and nowhere else.</summary>
        public sealed class VorbisAudioDecoder : IAudioDecoder
        {
            const int WindowBytes = 192 * 1024;                  // ≥ ProbeWindow's ceiling; the tail is kept across refills
            readonly Vorbis.Decoder _dec = new();
            readonly Ogg.Reader _ogg = new();
            readonly float _gainLinear;
            IMediaByteSource? _src; IRandomAccessBytes? _ra;    // _ra when the source is a RingSource
            MixFormat _target;
            byte[] _win = GC.AllocateUninitializedArray<byte>(WindowBytes, pinned: true);
            long _winStart; int _winLen;
            int _hold;                                          // frames held in _dec.Output not yet handed out
            int _holdOffset;
            long _samplePos;                                    // source sample of the next frame to emit
            int _skipSamples;                                   // after a seek: samples to drop inside the landing packet
            long _firstAudioPage, _totalGranules = -1;
            bool _eof; uint _epoch;
            LinearResampler? _resampler;

            public VorbisAudioDecoder(float gainDb) => _gainLinear = GainLinear(gainDb);
            public GaplessInfo Gapless { get; private set; } = GaplessInfo.None;

            public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
            {
                info = default; _src = src; _ra = src as IRandomAccessBytes; _target = target;
                if (!src.TryOpen(new DataSpec { Position = 0, Length = -1 })) return false;
                _winStart = 0; _winLen = 0; _ogg.Reposition(0);
                if (!Fill()) return false;
                // the three header packets (spec §4.2): identification, comment, setup — all inside the 80 KiB head
                if (!ReadHeaderPacket(out var ident) || !ReadHeaderPacket(out var comment) || !ReadHeaderPacket(out var setup)) return false;
                if (!_dec.Open(ident, setup, _gainLinear)) return false;           // the ONLY allocation site of the CORE
                _firstAudioPage = _winStart + _ogg.Cursor;
                _resampler = _dec.SampleRate != target.SampleRate ? new LinearResampler(_dec.SampleRate, target.SampleRate, target.Channels) : null;
                // gapless: lead-in from the first audio page (§4.4), the exact total from the tail when the source has it
                int leadIn = LeadInFromFirstPage();
                _totalGranules = (_ra as RingSource)?.TailGranule ?? -1;
                Gapless = new GaplessInfo(ToMix(leadIn), 0, _totalGranules >= 0 ? ToMix(_totalGranules) : -1, _totalGranules >= 0);
                info = new DecodedInfo(new MediaContentType(Container.Ogg, CodecId.None, CodecId.Vorbis),
                    new MixFormat(_dec.SampleRate, _dec.Channels),
                    TimeSpan.FromSeconds(_totalGranules >= 0 ? (double)_totalGranules / _dec.SampleRate : 0), default);
                return true;
            }

            /// <summary>Fill the window from the source. Keeps the unread tail (the reader owns spanning bytes, so the
            /// window may drop everything before Cursor). Uses ReadAt when the source has it.</summary>
            bool Fill()
            {
                int keep = _winLen - _ogg.Cursor;
                if (keep > 0 && _ogg.Cursor > 0) _win.AsSpan(_ogg.Cursor, keep).CopyTo(_win);
                _winStart += _ogg.Cursor; _winLen = Math.Max(0, keep); _ogg.Reposition(_winStart); // Reposition keeps the index
                while (_winLen < _win.Length)
                {
                    int n = _ra is not null ? _ra.ReadAt(_winStart + _winLen, _win.AsSpan(_winLen), _epoch)
                                            : _src!.Read(_win.AsSpan(_winLen));
                    if (n < 0) return false;
                    if (n == 0) break;
                    _winLen += n;
                }
                return _winLen > 0;
            }

            /// <summary>Decode packets until one yields frames. A corrupt packet costs itself, never the track.</summary>
            bool NextFrames()
            {
                while (true)
                {
                    var next = _ogg.NextPacket(_win.AsSpan(0, _winLen), out var packet, out long granuleAtEnd);
                    if (next == Ogg.Reader.Next.NeedMore) { if (!Fill()) return false; continue; }
                    if (next == Ogg.Reader.Next.Eos) return false;
                    if (next == Ogg.Reader.Next.Corrupt) continue;
                    if (_dec.DecodePacket(packet) != Vorbis.PacketResult.Ok) continue;
                    int frames = _dec.Frames;
                    if (granuleAtEnd >= 0) _samplePos = granuleAtEnd - frames;    // the page's granule pins the position (§A.2)
                    if (frames == 0) continue;                                     // the primed packet
                    _hold = frames; _holdOffset = 0;
                    if (_skipSamples > 0)
                    {
                        int drop = Math.Min(_skipSamples, _hold);
                        _holdOffset = drop; _hold -= drop; _skipSamples -= drop; _samplePos += drop;
                        if (_hold == 0) continue;
                    }
                    return true;
                }
            }

            public int Read(Span<float> dst)
            {
                if (_src is null || _eof) return 0;
                int ch = _target.Channels, want = dst.Length / ch;
                if (want <= 0) return 0;
                if (_hold == 0 && !NextFrames()) { _eof = true; return 0; }
                var held = new ReadOnlySpan<float>(_dec.Output + _holdOffset * ch, _hold * ch);
                if (_resampler is { IsActive: true } rs)
                {
                    ResampleResult rr = rs.Process(held, _hold, dst);
                    _holdOffset += rr.Consumed; _hold -= rr.Consumed; _samplePos += rr.Consumed;
                    return rr.Produced;
                }
                int frames = Math.Min(want, _hold);
                held[..(frames * ch)].CopyTo(dst);
                _holdOffset += frames; _hold -= frames; _samplePos += frames;
                return frames;
            }

            /// <summary>MIX-domain frame in, MIX-domain frame reached out (or −1). The planner is the CORE's
            /// (Ogg.BeginSeek / TryNextProbe / Observe); this is the I/O around it: one ReadAt per probe, the ring
            /// re-targeted FIRST so the probe is the only request in flight (§5.2).</summary>
            public long Seek(long frame)
            {
                if (_src is null) return -1;
                long target = Math.Clamp(ToSrc(frame), 0, _totalGranules > 0 ? _totalGranules : long.MaxValue);
                _hold = 0; _eof = false; _skipSamples = 0; _resampler?.Reset();
                _epoch++;
                long t0 = Stopwatch.GetTimestamp();
                Ogg.SeekPlan plan = Ogg.BeginSeek(_ogg.Index, _firstAudioPage, _src.Length ?? 0, _totalGranules, target, _ogg.MaxPageSeen);
                int requests = 0;
                while (Ogg.TryNextProbe(ref plan, out long at))
                {
                    _ra?.Retarget(at, plan.WindowBytes, _epoch); requests++;
                    if (!ReadWindowAt(at, plan.WindowBytes)) break;
                    if (Ogg.Observe(ref plan, _ogg.Index, _win.AsSpan(0, _winLen), _winStart) == Ogg.ProbeResult.NoPage) break;
                }
                // land: the page before the target's page primes; PeekFrames fixes the position; decode forward
                if (!ReadWindowAt(plan.Offset, plan.WindowBytes)) return -1;
                _ra?.ResumeFrom(plan.Offset);
                _dec.ResetLapping();
                _samplePos = plan.OffsetGranule >= 0 ? plan.OffsetGranule : PositionFromPeek();
                while (true)
                {
                    if (!NextFrames()) { _eof = true; return -1; }
                    if (_samplePos + _hold > target) { int drop = (int)(target - _samplePos); _holdOffset += drop; _hold -= drop; _samplePos += drop; break; }
                    _hold = 0;
                }
                Log.Info("audio", $"audio.seek target={target} probes={plan.Probes} requests={requests} tier={plan.Tier} ms={Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0}");
                return ToMix(target);
            }

            bool ReadWindowAt(long offset, int bytes)
            {
                _winStart = offset; _winLen = 0; _ogg.Reposition(offset);
                int want = Math.Min(bytes, _win.Length);
                while (_winLen < want)
                {
                    int n = _ra is not null ? _ra.ReadAt(offset + _winLen, _win.AsSpan(_winLen, want - _winLen), _epoch)
                                            : (_src!.Seek(offset + _winLen) < 0 ? -1 : _src.Read(_win.AsSpan(_winLen, want - _winLen)));
                    if (n <= 0) break;
                    _winLen += n;
                }
                return _winLen > 0;
            }

            long ToMix(long src) => (long)Math.Round((double)src * _target.SampleRate / _dec.SampleRate);
            long ToSrc(long mix) => (long)Math.Round((double)mix * _dec.SampleRate / _target.SampleRate);
        }
    }
}
```

`PositionFromPeek()` runs `_dec.PeekFrames` over the packets of the first page the reader reaches after the
landing window and subtracts their sum from that page's granule (§4.3); `LeadInFromFirstPage()` is the same
arithmetic on the first audio page at open (§4.4). Both are ~20 lines over `Ogg.Reader` and `Vorbis.Decoder`
and live in the adapter because they need the window.

### 6.3 The gain, and the header's byte 144

`Opened.GainDb` is 0 for Ogg today (§1.2: `NormalizationGain` is used on the FLAC branch only). Wave 2 left
`HeadGainDb(head)` for exactly this; with the head in `Body`, `Open` sets `GainDb = catalogue gain if non-zero,
else HeadGainDb(head)` — librespot reads the same 16 bytes (`player.rs:351-381`; track gain, track peak, album
gain, album peak, LE f32) and applies `10^(gain/20)` capped so `peak × factor ≤ 1` (`get_factor`,
`player.rs:383-395`). The plan applies the track gain the same way, capped by the track peak, folded into
`InterleaveStereo`'s one multiply (§3.8) — `Platform.Keys.NormalizationEnabled` gates it as today (`:746-750`).
Album mode is §9 Q8.

### 6.4 Gapless and `HandOffAt`

Nothing in the hand-off changes: `HandOffAt` decides, `GaplessJoinClock` computes, the engine butt-joins. What
changes is the input: `Gapless.ExactFrames` is now exact (the tail's granule), so `PcmAudioPlayer.MixFrames` is
the true length and the join lands on the last real sample instead of the catalogue's millisecond duration; and
the prepared next decoder has already primed its first packet and indexed its first pages, so the first `Read`
after the join is a copy, not a decode-and-wait.

---

## 7. Measurement plan, tests and fixtures (`Wavee.Tests/VorbisTests.cs`, `OggTests.cs`, `Fixtures/ogg/`)

### 7.1 Fixtures — generated with ffmpeg 8.1.2 + libvorbis (present on this machine; `oggenc` is not)

`ffmpeg -f lavfi -i "<source>" -c:a libvorbis <rate/quality> out.ogg`, deterministic seeds, 44.1 kHz stereo unless
stated, 8-10 s each so the set stays under 3 MB. `Fixtures/ogg/README.md` records the exact command lines and
the ffmpeg version; the files are regenerable, not sacred.

| File | Source | Bytes (≈) | What it pins |
|---|---|---|---|
| `pink-320.ogg` | pink noise, `-b:a 320k`, 10 s | 290 KB | the Spotify VeryHigh shape: 256/2048 blocks, floor 1, residue 2, coupled stereo; the throughput fact's input |
| `pink-96.ogg` | pink noise, `-b:a 96k`, 10 s | 90 KB | the Normal rung: smaller books, more short blocks |
| `vbr-q8.ogg` | white noise gated −60/0 dB in 2 s cycles, `-q:a 8`, 10 s | 270 KB | strong VBR: the seek estimate's worst case (§1.6) |
| `sine-440.ogg` | 440 Hz at −6 dBFS, mono, `-q:a 5`, 8 s | 40 KB | mono duplication; the SNR oracle (no stored reference needed) |
| `sweep-48k.ogg` | a 20 Hz-20 kHz sweep at 48 kHz, `-q:a 6`, 8 s | 120 KB | the 48 kHz path (no resampler on a 48 k device) and long/short block switching |
| `pages-100ms.ogg` | pink 320 k with `-page_duration 100000` | 300 KB | small pages (oggenc-like), many packets per probe window |
| `pink-320.s16` | `ffmpeg -i pink-320.ogg -f s16le` (ffmpeg's own Vorbis decoder) | 1.7 MB → **first 3 s only, 530 KB** | the PCM reference: max |Δ| ≤ 2 LSB and RMS(Δ) < 1e-4 against our decode (two float decoders differ in the last bits; an MD5 over s16 would be brittle) |
| `vbr-q8.s16` (first 3 s) | as above | 530 KB | the reference on the VBR file |
| synthetic pages | built in the test by a 40-line `OggWriter` test helper (lacing, CRC, continued flags) | 0 | spanning packets (a 70 KB packet), a zero-length packet, a sequence hole, a second serial, a bad CRC, BOS/EOS |

Total ≈ 2.2 MB. `Wavee.Tests.csproj:29` already copies `Fixtures\**\*`. The 30 s files used for §1.6's page
profile stay in the scratchpad; the plan's numbers are reproducible from the README's commands.

### 7.2 The facts

| Test | What it pins |
|---|---|
| `Decodes_pink320_within_two_lsb_of_ffmpeg` | `pink-320.ogg` vs `pink-320.s16`: per sample |Δ| ≤ 2/32768, RMS < 1e-4, and the same sample count (the lead-in and the EOS trim applied) — the correctness oracle for codebooks, floor 1, residue 2, coupling, IMDCT, window |
| `Decodes_vbr_within_two_lsb_of_ffmpeg` | the same on `vbr-q8` |
| `Sine_decodes_with_snr_above_60_db` | `sine-440.ogg`: fit a 440 Hz sinusoid to the decoded output; SNR > 60 dB, L == R — an oracle with no stored data, and the one that catches a wrong window shape or a mis-scaled IMDCT (the "½ too small" note in stb :2655-2657) |
| `Imdct_of_a_unit_impulse_is_the_basis_function` | `Imdct` on `spec[k] = 1` yields `cos(π/n·(i + ½ + n/4)·(k + ½))`-shaped output within 1e-5 for n = 256 and 2048 — pins the port of stb's eight stages independently of any codec |
| `Codewords_are_canonical_and_the_fast_table_agrees_with_the_slow_path` | random valid length sets: `BuildCodewords` matches a reference canonical assignment; for every entry, decoding its bit-reversed codeword through `DecodeScalar` returns the entry via the fast table when `len ≤ 10` and via `DecodeSlow` otherwise (forced by a table filled with `Miss`) |
| `Single_entry_codebook_reads_one_bit` | the errata case |
| `Floor1_render_matches_the_spec_pseudocode` | a hand-built floor 1 config and posts against a straightforward `render_line` into a curve then multiply — the fused `RenderFloor1` equals it bit-exactly |
| `Residue2_interleave_writes_channels_round_robin` | a two-channel residue with a synthetic 2-dim book: values land at `spec[c][j]` per §8.6.2's `vector[offset + j·ch + c]` |
| `Uncouple_matches_the_four_cases` | the branchless kernel vs the spec's four `if`s, vector and scalar paths (a 15-element slice forces scalar) |
| `Interleave_matches_scalar` | `InterleaveStereo` vector == scalar, gain applied |
| `Decoding_two_hundred_packets_allocates_nothing` | after `Open` + two packets: `GC.GetAllocatedBytesForCurrentThread()` delta over 200 packets == 0 (P8; `DecodeTests.cs:596-612`'s shape) |
| `Throughput_is_at_least_fifty_times_realtime` | decode `pink-320.ogg` 20× in a loop, Stopwatch: ≥ 50× realtime (10 s of audio in ≤ 200 ms per pass) — a generous bound so it never flakes; the measured ratio is written to the test output, and the perf tour records it (stb reaches several hundred× on a modern core; the point is a floor under regressions, not a benchmark) |
| `Seek_lands_on_the_exact_sample` | for every fixture: `Seek(mix frame)` then `Read` == the linear decode's floats at the same position (first 4,096 samples identical, not ±LSB — same decoder, same bits); also a seek inside the last packet, to 0, and past the end (EOF, no exception) |
| `Seek_probe_count_is_bounded_and_typical_is_one` | a counting `IRandomAccessBytes` over each fixture: cold seek to 70 % ⇒ `plan.Probes ≤ 2` and `requests ≤ 2`; a second seek to 72 % ⇒ 0-1; on `vbr-q8` ≤ 3; never > 8 |
| `Page_index_brackets_and_decimates` | 5,000 adds stay ≤ 4,096 entries with uniform coverage; `Bracket` answers the tightest pair; `Adjacent` true only for contiguous parses |
| `Ogg_parser_handles_spanning_holes_and_serials` | the synthetic pages: a 70 KB packet spans three pages and comes back whole; a sequence hole drops the open packet only; a foreign serial is skipped; a bad CRC is skipped by one byte; `Truncated` asks for more at the page's own offset |
| `Crc32_matches_the_reference` | `Crc32("123456789")` under poly 0x04c11db7 / init 0 / no reflection / no xor = `0x89A1897F` (= CRC-32/CKSUM's catalogue check `0x765E7680` without its final xor — the Ogg variant; computed for this plan with a bit-serial reference and confirmed equal to the stored CRC of the first page of a libvorbis file, `0xB916ACC1`); the test also re-checks the first real page of every fixture |
| `Gapless_fields_come_from_the_granules` | `pink-320`: `ExactFrames == last page granule` (= 441,000 at 10 s), `LeadInFrames == 0` for a libvorbis file whose first page granule accounts for the primed packet; a synthetic first page with a smaller granule ⇒ a positive lead-in |
| `Peek_frames_agrees_with_decode` | for every packet of `pink-320`: `PeekFrames` == `DecodePacket(...).Frames` |
| `Header_gain_is_read_at_byte_144` (owner F) | a synthetic 0xa7 header with the four floats; `HeadGainDb` reads the track gain; the peak cap holds |
| `--vorbis-probe <file>` (owner S, `Screens/Diagnostics.Probe.cs`, 40 lines) | decodes any file, prints the setup summary (books, floors, residues, block sizes), packet count, the index size, throughput, and runs three seeks reporting probes/requests — the field tool for "this file does not play"; no env var |

### 7.3 The gate the numbers pin

- **Zero allocation**: the 200-packet fact, and the perf tour shows no gen-0 GC attributable to the decoder over
  a 5-minute listen (the memory sampler, 30 s).
- **Throughput**: ≥ 50× realtime as a floor; the measured figure logged.
- **Seek**: probes ≤ 2 typical / ≤ 8 bounded on every fixture; over the fake CDN, requests per seek ≤ 2 typical,
  0-1 after the index warms; the login smoke's `audio.seek` line shows `requests=1` on three of four seeks in a
  real track (orchestrator; network).
- **Start**: the login smoke's `audio.open` line shows `firstAudioMs` under 400 ms on a warm session (head-served).

---

## 8. The work split

### 8.1 Files, owners, waves, budgets — in §2's style, disjoint from in-flight owners

| File | Concern | Owner | Wave | Lines | Source |
|---|---|---|---|---|---|
| `+Playback/Playback.Audio.Vorbis.cs` | **CORE**: `BitReader`, codebooks (builder, fast/sorted tables, VQ arenas), floor 1 (+ floor 0 for completeness), residues 2/0/1, `Imdct` + tables, `OverlapAdd` / `Uncouple` / `InterleaveStereo`, `Decoder.Open/DecodePacket/PeekFrames/ResetLapping`, the header parsers | **V** (new) | starts today; gated before Wave 3's adapter lands | 1,600 | §3 |
| `+Playback/Playback.Audio.Ogg.cs` | **CORE**: `TryParsePage`, `FindPage`, `Crc32`, `Reader` (packets, spanning, holes, serials), `PageIndex`, `SeekPlan` / `BeginSeek` / `TryNextProbe` / `Observe` | V | with the above | 600 | §4 |
| `Wavee.Tests/VorbisTests.cs`, `OggTests.cs`, `Fixtures/ogg/**` (≈ 2.2 MB, generated) | §7 | V | with the above | 550 | §7 |
| `+Spotify/Spotify.Audio.Stream.cs` | **SHELL**: `Body`, `Fetcher` (the `Wavee.AudioFetch` thread, cancel, ping/throughput), the head in `Open`'s `WhenAll`, the tail granule, the `ChunkDiskCache` adapter, `FetchRange` (today's `TryRange` + decrypt); `CtrStream` deleted; `Opened.Body` | F | request, Wave 2 file — can start today (nothing above it depends on Wave 3) | +700 / −150 | §5.3, §5.5 |
| `Wavee.Tests/AudioStreamTests.cs` | §5.6 (`Body`/`Fetcher` rows) | F | with the above | 250 | §5.6 |
| `Playback/Playback.Audio.cs` | `VorbisAudioDecoder` internals (§6.2), `RingSource` + `IRandomAccessBytes` (§5.3), `Prepare`'s ring budget, the two log lines, `ApplySkip` removed, the FLAC adapter's `ReadWindowAt` fast path | H | 3 | +260 (2,470 → 2,730; a named partial `+Playback.Audio.Ring.cs` if H prefers) | §5.3, §6 |
| `Wavee.Tests/PlaybackAudioTests.cs` | §5.6 (`RingSource` rows), `Seek_probe_count` over the adapter | H | 3 | +120 | §5.6, §7.2 |
| `Playback/Playback.Audio.Flac.cs` | `ParseVorbisComment` lifted to a shared `Playback.VorbisComment` (both containers carry the same block) | U | request, +0 net | §3.9 |
| `Screens/Diagnostics.Probe.cs` | `--vorbis-probe` | S | 6 | +40 | §7.2 |
| `Wavee.csproj` | **remove** `<ProjectReference Include="..\vendor\NVorbis\NVorbis\NVorbis.csproj" />` (`:108`); `git rm -r src/apps/vendor/NVorbis` | orchestrator | Wave 6 (with `src/apps/_old`) | −1 | §2 |
| `docs/plans/wavee/wavee-0.3-implementation.md` §1 | the "`vendor/NVorbis` (unchanged, D3)" cell becomes "deleted in Wave 6 (Vorbis plan)" | orchestrator | Wave 6 | 1 | §1 |

**Owner V is one subagent** with two CORE files, two test files and the fixtures, sharing nothing with anyone in
flight. **Owner F's stream partial is the second start-today item**: it is F's own file (§2's `Spotify.Audio.cs`
row), Wave 4's L/I/J/K do not touch it, and H's Wave 3 work consumes it. H's lines are a request; U's is a
one-function lift. Nothing here edits `Playback.cs`/`Playback.Host.cs` (G), `Shell/**` (I/L), `Entities/**`.

### 8.2 What can start today, what waits

```
 today ─────────────────────────────────────────────────────────────────────────▶ Wave 3 ──────▶ Wave 6
 V:  Playback.Audio.Vorbis.cs + Playback.Audio.Ogg.cs + tests + fixtures    (pure over spans; no wave dependency)
 F:  +Spotify.Audio.Stream.cs (Body, Fetcher, head, tail, disk cache)       (F's own file; tests over a fake CDN)
                                                              H:  VorbisAudioDecoder internals + RingSource
                                                                  (needs V green and F's Body)
                                                              U:  VorbisComment lift (10 min)
                                                                              S:  --vorbis-probe
                                                                              orchestrator: NVorbis removed, plan §1
                                                                              cell, CHANGELOG (#n)
```

### 8.3 The gate

- **V's gate (before H opens the adapter):** `VorbisTests` + `OggTests` green — both s16 references within 2 LSB,
  the sine SNR, the impulse IMDCT, the allocation fact at 0, throughput ≥ 50×, seek exact on every fixture with
  probes ≤ 2 typical / ≤ 8 max, the synthetic Ogg cases; Debug and Release clean (`TreatWarningsAsErrors`); no
  source-text test.
- **F's gate:** `AudioStreamTests` green over the fake CDN — 4 requests cold, the splice byte-exact, cancel
  observed, the cache map round trip, the reserve rule.
- **Wave 3's gate gains three lines:** the login smoke (orchestrator, network) plays a track with `audio.open
  … head=81920 firstAudioMs<400`, seeks four times with `audio.seek … requests=1` on at least three, and
  hands off gapless with `Gapless.ExactFrames` set (the `[gapless] arm` line shows `exact=1`); a scrub of ten
  seeks leaves at most one range in flight (the fetcher's log counter).
- **Wave 6:** NVorbis gone from the tree and the build; `--fake` unchanged; the perf tour shows no gen-0 GC from
  decoding over a 5-minute listen and a working set that does not grow across 50 seeks (the ring and the index
  are bounded).

---

## 9. Open questions — only Christos can answer

1. **NVorbis: delete outright in Wave 6, or keep it one release as a fallback?** CLAUDE.md says no legacy paths and
   this plan deletes it; the tests do not depend on it (the s16 references and the sine oracle are the truth). A
   one-release fallback would need a settings switch, which the rules also forbid. The plan assumes delete.
2. **The same unsafe/SIMD pass over the FLAC decoder** (`Playback.Audio.Flac.cs`, owner U — safe code with two
   `Vector128` sites today)? What it would gain: the 64-bit/7-byte refill in its `BitReader` (today one byte per
   refill, FLAC plan §3.3), pointer loops in the Rice reader and the LPC restore, `Vector256` in the three
   existing vector sites. What it would not gain: the LPC recurrence is sequential per sample (Symphonia's is
   scalar for the same reason), so the ceiling is ~1.5-2× on a codec that already costs a tenth of Vorbis per
   sample. Proposed: yes, as a second pass by V after Vorbis lands, same kernels, same tests (the MD5 oracle
   makes it safe) — but it is U's file, so it is a hand-over, and only if you want the two decoders to read alike
   at the `unsafe` level too.
3. **`Vector256` at all?** It is used in the four streaming kernels only (§3.8) and is dead code on arm64. One
   `Vector128` path (NEON-identical) would be simpler and, on a 2,048-point block, within a few percent. Keep both
   as planned, or `Vector128` only?
4. **Disk cache defaults.** 0.2.9 shipped `AudioBodyCacheEnabled` with the drive-share auto budget
   (`clamp(total/10, 16 GiB, 128 GiB)`) and the `max(5 GiB, 5 %)` reserve. Keep those defaults and the three
   settings keys, or start with a fixed 8 GiB and no mode switch (fewer settings; ch 27 has no cache row)?
5. **The head host.** `heads-fa-tls13.spotifycdn.com/head/{fileId}` is unauthenticated and serves clear audio;
   0.2.9 used it and librespot does not. Any reason not to keep depending on it (it is the whole of "instant
   start")? If it ever stops answering, `Open` falls back to the body's first range with no other change.
6. **Read-ahead seconds.** 30 s default / 10 s metered / whole file (capped 16 MiB) when throughput ≥ 3× the
   byte rate — 0.2.9's tiers. librespot uses 5 s; YouTube Music buffers ~1-2 minutes on Wi-Fi. Keep 30/10/600?
7. **The tail request at open** (one extra 64 KiB range per track, for the exact gapless length and `Duration`)
   versus the catalogue duration with the EOS trim applied only when the end is reached. The plan fetches it
   (it is parallel, cached on disk, and it is what makes the butt-join exact); say if you would rather not.
8. **Normalization: track or album gain?** The header carries both (byte 144: track gain/peak, album gain/peak).
   0.2.9 and today apply the track gain; librespot defaults to album gain when playing an album context. Track
   only for 0.3, or album gain inside an album context (a reducer fact the pump already has)?
9. **Local `.ogg` tags** — the FLAC plan's Q5 applies unchanged (parse, keep behind `LocalFileTags`, default off).
10. **The issue numbers.** This is a feature (the decoder + the stream layer) that fixes live defects: the
    synchronous range fetch on the decode thread, the seek cost, the `GaplessInfo.None` for Vorbis. One issue
    "Vorbis decoder + CDN stream layer" with the defects as bullets, or three? The orchestrator drafts; nothing is
    filed by this plan.

---

## 10. Sources

Local, read for this plan: `src/apps/Wavee/Playback/Playback.Audio.cs` (`:1-140, 270-320, 370-400, 550-575,
725-820, 1055-1115, 1595-1612, 1655-1835, 2120-2365`), `Playback/Playback.Audio.Flac.cs` (`:1-60, 990-1125,
1140-1247`), `Spotify/Spotify.Audio.cs` (`:20-62, 596-765, 860-886` + the structure grep), `Wavee.csproj:103-108,
173-188`, `Wavee.Tests/Wavee.Tests.csproj:24-34`, `Wavee.Tests/DecodeTests.cs:590-620`, `docs/plans/wavee/
wavee-0.3-implementation.md` §1, §2 `Playback/`, §4.9, §5 Waves 0-4, `docs/plans/wavee/wavee-0.3-flac-implementation.md`
in full, `docs/plans/wavee/gapless-findings.md:15-174`, `relationships_wavee.md:598-712`; the vendored decoder
`src/apps/vendor/NVorbis/NVorbis/**` (every file cited in §1.4); 0.2.9: `src/apps/_old/Wavee/SpotifyLive/Audio/
{FluentMediaAudioHost,SampleSource,SeekGate,AudioFormatProbe,HeadFileClient}.cs`, `Backend/Audio/
{PrefetchingReadStream,SpotifyAudioStream,SpotifyAesCtr,AudioBodyDiskCache}.cs`, `src/apps/Wavee.Sdk/Streams/
{RangedHttpSource,ChunkDiskCache}.cs`, `CHANGELOG.md:572-582, 770`; the engine: `Media/Playback/MediaSeams.cs:
140-215, 275-310`, `MediaTypes.cs:108-111`, `Audio/PcmAudioPlayer.cs:45-53, 109-170`, `Audio/AudioDecode.cs:
200-224`, `Audio/LinearResampler.cs`, `Audio/DspStages.cs:55-100`.

Reference decoders, cloned for this plan: `C:\WAVEE\stb\stb_vorbis.c` (`:1-66, 96-122, 452-535, 884-885,
1086-1154, 1179-1230, 1254-1301, 1599-1647, 1656-1729, 1865-1933, 1935-2081, 2104-2284, 2408-2929, 3072-3108,
3186-3400, 3456-3516, 4131-4191, 4341-4512, 4561-4934`); `C:\WAVEE\lewton\src\{audio,imdct,huffman_tree,
bitpacking,inside_ogg,header_cached}.rs`; `C:\WAVEE\libvorbis\lib\{mdct,window,block,vorbisfile,codebook,
sharedbook,res0,floor1}.c` and `lib/modes/{setup_44,floor_all,residue_44}.h`; `C:\WAVEE\tremor\{mdct,misc}.*`;
`C:\WAVEE\minivorbis\minivorbis.h`; `C:\WAVEE\libogg\src\framing.c`; `C:\WAVEE\Symphonia\symphonia-codec-vorbis\
src\{codebook,dsp,floor,residue,lib}.rs`, `symphonia-core/src/dsp/mdct.rs`, `symphonia-format-ogg/src/
{demuxer,logical,physical,page}.rs`; `C:\WAVEE\librespot\{audio/src/fetch/{mod,receive}.rs, audio/src/range_set.rs,
playback/src/player.rs:51,330-395,1094-1229,2464-2484, core/src/http_client.rs:97-148, protocol/proto/
metadata.proto:292-320}`; `C:\WAVEE\go-librespot\{audio/chunked-reader.go, audio/latency.go, audio/decryptor.go,
cache/cache.go, player/player.go:600-880}`.

Specs and web: RFC 3533 https://www.rfc-editor.org/rfc/rfc3533.txt; Vorbis I https://xiph.org/vorbis/doc/
Vorbis_I_spec.html (§1.3.2, §2, §3.2, §4.2-4.3, §7.2, §8.6, §A.2, §10.1); Fabian Giesen, "Reading bits in far too
many ways (part 2)", https://fgiesen.wordpress.com/2018/02/20/reading-bits-in-far-too-many-ways-part-2/ (the
variant-4 refill); https://en.wikipedia.org/wiki/Vorbis and https://community.spotify.com/t5/Other-Podcasts-
Partners-etc/Audio-Formats/td-p/1937513 (Spotify's format lineup: Vorbis on desktop/mobile, AAC on web, Opus
only for web/Cast at 64 kbit/s); https://github.com/librespot-org/librespot/issues/1583 (head files "identical
down to the last bit"; via the FLAC plan §1.4); ffmpeg 8.1.2 (`C:\Users\…\WinGet\Links\ffmpeg.exe`, libvorbis
encoder present) and the page profile measured with it (§1.6).
