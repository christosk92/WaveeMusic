# Wavee 0.3 — FLAC: Spotify Lossless (16/24-bit) and local FLAC files, implementation plan

Status: DRAFT for approval, 2026-09-13. A named partial beside the master plan
(`docs/plans/wavee/wavee-0.3-implementation.md`, §2 `Playback/`, §4.9, §5 Waves 2-3 and 6, §9.6) — this plan adds
files, columns and one settings rung to that tree and does not re-argue it. Rules P1-P16 / C1-C10
(`C:\Users\ChristosKarapasias\Documents\relationships_wavee.md` §5.11-5.12) are the rules here too: CORE allocates
nothing after warm-up, no LINQ / closures / async / boxing in CORE, SIMD only above ~16 elements and always with a
`Vector128` fallback (arm64 ships), NativeAOT, Windows only.

The PlayPlay derivation is a private repository and is treated here as the seam Wave 2 already carries
(`Spotify.Audio.KeyDeriver`); nothing in this plan reads, names or depends on its internals.

---

## 0. The decision, in three sentences

1. **Write the FLAC decoder ourselves, in CORE**, as `Playback/Playback.Audio.Flac.cs`: span in, `int` samples out,
   zero allocations after `Open`, seek by seek table with a libFLAC-style bracketed search when the file carries
   none (Spotify's do not — §1.4), CRC-8/CRC-16 checked per frame, STREAMINFO MD5 as the test oracle. `FlacBox` is a
   2015 net35 binary with no source, no license, no seek and a per-frame enumerator allocation; the Windows FLAC MFT
   has no documented output types, needs a byte-stream handler and an `IMFSourceReader` path the engine does not have
   (its only MF audio code is a raw ADTS AAC `IMFTransform`), and would still hand back PCM we resample and
   float-convert ourselves.
2. **It plugs into the engine where Vorbis and MP3 plug in**: a `FlacAudioDecoder : IAudioDecoder` in Wave 3's
   `Playback.Audio.cs` decodes into the engine's fixed `f32 / device-rate / stereo` mix format at the decode edge
   (`LinearResampler` when the file's rate differs from the device's), reports `GaplessInfo.ExactFrames` from
   STREAMINFO's total-samples so the butt-join is sample-exact, and pre-applies Spotify's normalization gain from
   `AudioFilesExtensionResponse.default_file_normalization_params` (`Spotify.Audio.Opened.GainDb`). 24-bit reaches
   the device as float32 through WASAPI shared mode — the engine has no exclusive mode and this plan does not add one.
3. **The catalogue learns two things**: which formats a track has (a `TrackFormats` edge + a `Files` field group,
   filled from extension kind 5 on the `spotify:audio:` entity, the only place FLAC file ids are served) and a
   fourth **Lossless** rung in Settings ▸ Playback that the metered cap (`min(user, meteredCap)`, ch 29 §8) already
   keeps off a metered link unless the user raises the cap. The CORE decoder and its tests, and the catalogue
   columns, can start today; the pump adapter lands with Wave 3 (owner H), the settings rung with Wave 6 (owner R).

---

## 1. Research record — what is true today

### 1.1 What Wave 2 already landed (`src/apps/Wavee/Spotify/Spotify.Audio.cs`, owner F)

Read in full, 842 lines. The FLAC-relevant facts, with line references into that file:

| Fact | Where |
|---|---|
| `Format { Unknown, OggVorbis96, OggVorbis160, OggVorbis320, Mp3, Flac, Flac24 }` and `Quality { Normal96, High160, VeryHigh320, Lossless = 3 }`; the stored int IS the wire (`Platform.Keys.PlaybackQuality`, default 2) | `:74-79`, `Platform.cs:192` |
| `FormatOf` maps `FlacFlac → Flac`, `FlacFlac24Bit → Flac24`; AAC/xHE-AAC rungs deliberately `Unknown` | `:130-142` |
| `Rung(Flac | Flac24) = 3`; `PickRung` caps the target at 2 for an Ogg list, so `Lossless` on an account without FLAC behaves as VeryHigh320 | `:147-156`, `:169-183` |
| `PickFlac` walks `AudioFilesExtensionResponse.Files`, 24-bit ranked above 16-bit | `:200-222` |
| `NormalizationGain(loudnessDb, truePeakDb)` = −14 LUFS target, capped at −1 dBTP headroom; used ONLY on the FLAC branch of `Choose`, from `lossless.DefaultFileNormalizationParams` | `:226-233`, `:245-252` |
| `LosslessMetadata(track)` derives `spotify:audio:<base62(original_audio.uuid)>` and POSTs `AUDIO_FILES` (kind 5) for it — the only route that carries FLAC ids; the legacy `/metadata/4/track/` route is a shell with an empty `file[]` | `:330-346`, header lines 13-16; `docs/plans/wavee/spotify-audio-file-pipeline-guide.md` §3-§4 (observed in `exx.har`: `TRACK_V4` carries Ogg 96/160/320 + AAC_24 and **no FLAC**; `AUDIO_FILES` on the audio uri carries all of those **plus** `FLAC_FLAC = 16`) |
| `ChooseTrack` fetches kind 5 **only when** `quality == Quality.Lossless` — two round trips per lossless open, one otherwise | `:756-761` |
| The key is bound to (file id, **track** gid) — "the FLAC is an alternative ENCODING of the same track, not an alternative track" | `:200-201` |
| The key path: AP `0x0c/0x0d` first; **one refusal latches `s_apKeysDisabled` for the whole session**; then the `KeyDeriver` seam | `:438-486` |
| The stream is one class for every format: `CtrStream`, AES-128-CTR with the public iv, 128 KiB ranged chunks decrypted at the true file offset; `Open` **always** passes `skip = Ctr.HeaderBytes (0xa7)` | `:494-509`, `:568-700`, `:742` |
| **The key check is Ogg-only.** `Ctr.Validates` looks for `OggS` at `0xa7` and nothing else | `:539-544` |
| The 80 KiB clear head (`Head`) and `HeadGainDb` (byte 144 of the Ogg header) exist for Wave 3 to use or not | `:774-819` |
| The quality is read per open (`PreferredQuality()`), so a settings change applies from the next track | `:717-721` |

**Three things §1.4's evidence changes, and owner F must apply** (listed again in §8 as requests to F, not as
files the FLAC agents own):

1. **A Spotify FLAC has no `0xa7` header.** The bytes are a plain `fLaC` stream, and the clear head file is
   byte-identical to the file's first chunk (librespot #1583, SuisChan 2025-09-29: "no additional headers or
   anything, just raw FLAC files without any modifications"; go-librespot opens FLAC at offset 0 — §1.5). `Open`
   must pass `skip = 0` for `Format.Flac / Flac24`: `new CtrStream(Cdn, mirrors, key, choice.Fmt is Flac or Flac24
   ? 0 : Ctr.HeaderBytes, length, hex)`. Without this the decoder is handed a stream that starts 167 bytes into
   the STREAMINFO block and fails the `fLaC` check on every lossless open.
2. **The AP audio-key path does not serve FLAC file ids** ("the keys to lossless don't work via shannon", same
   thread; go-librespot disabled FLAC outright without its PlayPlay plugin, commit `ef213e5`). Today's `Key` tries
   the AP first and **latches AP keys off for the whole session on a `Rejected`** — so the first lossless open on a
   build with a deriver would silently downgrade every later Ogg open to the deriver as well. `Key` needs a
   `bool apEligible` argument (false for `Flac`/`Flac24`) that skips the AP attempt and goes straight to the seam;
   a public-only build then reports `Fault.NoDeriver` for FLAC honestly and keeps AP keys for Ogg.
3. `Ctr.Validates` must accept `fLaC` at offset 0 beside `OggS` at `0xa7` (the head file is the proof vector: it
   is clear, so a decrypted first chunk must equal it byte for byte — §7's key test uses exactly that).

No change to the ladder, `PickFlac`, `NormalizationGain` or `LosslessMetadata` is needed — Wave 2 already speaks
FLAC on the wire, and the three items above are ~15 lines.

### 1.2 The engine's audio graph (`C:\wavee\fluent-gpu\src\FluentGpu.Engine\Media\Playback\**`)

Established by reading the engine source (file:line cited); this is the contract the decoder is written against.

| Question | Answer | Where |
|---|---|---|
| What does the graph consume? | **`float32` interleaved at the device's mix rate, stereo.** `MixFormat(int SampleRate, int Channels)`; "f32 interleaved implied". No bit-depth field exists above the device leaf | `MediaSeams.cs:280` |
| The seam a codec implements | `IAudioDecoder { bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info); int Read(Span<float> dst); long Seek(long frame); GaplessInfo Gapless { get; } }` — `Read` returns FRAMES, short reads legal, 0 = EOF, <0 = error; `Seek` is in the **mix-rate** frame domain | `MediaSeams.cs:298-308`; spec `docs/plans/media-playback-api-spec.md` §5.5 |
| How the app injects it | `new PcmAudioPlayer(…, decoderFactory: fmt => …)`; "the app supplies a Vorbis/FLAC/MP3 factory to route real streaming content through the same graph" | `PcmAudioPlayer.cs:119-137` |
| The byte seam under the decoder | `IMediaByteSource { TryOpen(in DataSpec); int Read(Span<byte>); long Seek(long); long? Length; SourceCaps Caps; Cancel(); Close() }` — synchronous `read(2)` style on a firewalled worker thread; `StreamByteSource(Stream)` adapts any `Stream`; `FileByteSource(path)` exists; `DecryptingSource(inner, ICtrCipher)` is the engine's own AES-CTR decorator | `MediaSeams.cs:144-160`, `:165-212`; `AudioDecode.cs:51-76` |
| Resampling | **At the decode edge, by the decoder**, into the device rate: `LinearResampler(fromRate, toRate, channels)` — linear interpolation, block-continuous, alloc-free; "Linear is the M2 choice; a windowed-sinc SRC is a later quality refinement". The device opens once at its own rate (`Negotiate` → `MixFormat(device rate, 2)`) and is never reopened for a codec-rate change | `LinearResampler.cs:24-84`; `AudioDecode.cs:148`; `WasapiFormatNegotiation.cs:22-26`; `gapless-findings.md` §2 |
| Output mode | **WASAPI shared only.** `Initialize(AUDCLNT_SHAREMODE_SHARED, EVENTCALLBACK, 100 ms, 0, mix, null)`; the device's `GetMixFormat` is passed through untouched; no `IsFormatSupported`, no exclusive mode anywhere in the repo. Float fast path when the mix is 32-bit float; a 16/24-bit PCM mix is converted per block (`ToInt24(float) => (int)(s * 8388607f)`) | `WasapiAudioDevice.cs:426-463`; `WasapiFormatNegotiation.cs:30-135` |
| Media Foundation? | **No.** `AudioDecode.cs` is a hand-written managed WAV decoder; `IMFByteStream` and `IMFSourceReader` have zero hits in the engine. The only MF audio code is `FluentGpu.Windows/Media/Audio/MfAacDecoder.cs`, a raw `IMFTransform` over ADTS frames for live radio, and it does not implement `IAudioDecoder` | `AudioDecode.cs:116`; `MfAacDecoder.cs:30` |
| Gapless | A source property: `GaplessInfo(LeadInFrames, TrailPadFrames, ExactFrames, TailKnown)` in MIX-domain frames; `TrimmingSource` applies it; `PcmAudioPlayer.BuildTrimmedVoice` is the one seam; `VoiceScheduler` butt-joins (`TransitionOutcome.Gapless`, overlap 0, `GainEnvelope.Constant`) | `MediaSeams.cs:287-291`; `AudioSources.cs:142`; `PcmAudioPlayer.cs:151`; `VoiceScheduler.cs:268-281` |
| Total length | `PcmAudioPlayer.MixFrames`: the WAV decoder reports it exactly; a pluggable decoder's length is derived from the DECLARED duration unless `Gapless.ExactFrames` is set — which is why STREAMINFO total-samples matters | `PcmAudioPlayer.cs:141-143`; `AudioDecode.cs:380` |
| Where a meter / DSP taps in | `PcmAudioSession.RenderBlock`: mixer → master gain → master chain → transport → `TapBlock(buf, frames)` (peak + RMS into `AudioLevelMailbox`, demand-gated by the visualizer) → device write. A new `IDspStage` goes into `BuildGraphSpec`'s master chain; per-voice EQ is `PerVoiceChain` | `PcmAudioPlayer.cs:1400-1469`, `:1511-1529`; `AudioGraph.cs:204-211` |
| Allocation discipline | `AudioTripwire` asserts **0 managed bytes, 0 locks, 0 blocking calls** per RT block (erased in the shipping AOT binary); buffers are fixed scratch, never `ArrayPool`. The decoder runs on `FluentGpu.AudioProducer` (per-voice decode-ahead, `AboveNormal`), not on the RT thread — a decoder allocation is a GC-pressure bug, not a glitch, and P8 applies all the same | `AudioTripwire.cs:17-59`; `RingAudioSource.cs:71-83` |
| SIMD in the engine | One site: `Vector128<float>` in the constant-gain stage with identity/scalar fallbacks around it. No `Vector256`, no `Vector<T>` | `DspStages.cs:61-98` |
| Thread model | RT feed `FluentGpu.AudioRT` (Highest + MMCSS "Pro Audio"), decode worker `FluentGpu.AudioWorker`, clock `FluentGpu.AudioClock`; block ≈10 ms, decode-ahead 500 ms, ring 1 s | `AudioFeedThread.cs:137-155`, `:476-562`; `MmcssProAudio.cs:15-21` |

**Consequences.** (a) A FLAC decoder's output is `float`; 16-bit and 24-bit differ only in the scale constant.
(b) A 44.1 kHz file on a 48 kHz device is linearly resampled (the engine's existing floor for Vorbis too) — the one
place lossless is not lossless on the way out; §9 Q3. (c) There is no bit-perfect path: 24-bit → float32 → the
shared-mode mix (float32 on every default Windows endpoint) → the OS mixer. Spotify's own client only got one in
March 2026 ("Exclusive Mode for Windows", §1.4); for Wavee it would be an engine feature (exclusive mode +
`IsFormatSupported` + a 24-bit PCM sink) and is out of scope for 0.3 (§9 Q3).

### 1.3 How 0.2.9 did it (`src/apps/_old/Wavee/**`, reference only)

| Fact | Where |
|---|---|
| The whole FLAC path was **dark in shipped 0.2.9**: `AudioQualityPreference.Lossless` exists but Settings never offers it ("a permanently-disabled fourth row labelled 'Coming soon' is an advert", ch 27 §2 W7) and `AudioPlaybackStack.cs:89` wires `preferLossless: false`. The ladder's `FormatLabel` has no `FlacFlac24Bit` case (renders "Format 22") | `SettingsPage.Playback.cs:310-312`; `SpotifyTrackExpansionService.cs:344-357` |
| Decoder: `FlacSampleSource` over **FlacBox 1.0.0** `FlacReader`; samples pulled through `IEnumerable<int> GetValues()` — one heap enumerator per FLAC frame plus an interface call per sample | `SpotifyLive/Audio/SampleSource.cs:51-111` |
| `SeekTo` is a **silent no-op** ("FlacBox has no random seek — TODO"), and `SpotifyEngineAudioDecoder.Seek` returns the target as if it succeeded, so a scrub desyncs position from audio. Known defect (`ChangelogParserTests.cs:252` "FLAC seek is not implemented") | `SampleSource.cs:108`; `FluentMediaAudioHost.cs:259-268` |
| FlacBox itself: `lib/net35/FlacBox.dll`, 72,704 B, dated 2015-01-10, IL header v2.0.50727, author `notmasteryet`, project url `flacbox.codeplex.com` (dead), **no license expression, no source link**; managed-only, no P/Invoke, no functional reflection — AOT-safe in practice but restored under `NU1701` suppression (`Wavee.csproj:16`) | `%USERPROFILE%\.nuget\packages\flacbox\1.0.0\` |
| What FlacBox genuinely gave: STREAMINFO `TotalSampleCount` → `GaplessInfo(0, 0, ExactFrames, TailKnown: true)` — 15 lines of header parsing | `FluentMediaAudioHost.cs:281-301` |
| The decode edge is `float32` at source rate → channel conform → `LinearResampler` → engine mix; normalization gain is multiplied in the decoder so engine ReplayGain stays unity | `FluentMediaAudioHost.cs:119-121`, `:189-234` |
| Container skip: `fLaC` at 0 ⇒ raw file, else the 167-byte Spotify header (the 0.2.9 code guessed right for local files and would have guessed wrong for Spotify FLAC only because it looked for `fLaC` first) | `FluentMediaAudioHost.cs:2043-2050`; `AudioFormatProbe.cs:299-319` |
| Bitrate hints for read-ahead: FLAC 1,000,000 b/s, FLAC24 1,800,000 b/s | `PrefetchingReadStream.cs:114-122` |
| Local files: `.mp3 / .ogg / .flac` only; duration by a fail-soft STREAMINFO read; **no tag reading by design** ("a file name is what the user named the thing"); uri `wavee:local:file:<b64url(path)>` | `LocalFileMediaProvider.cs:81-97`; `LocalAudioDurationProbe.cs:44-53`; `LocalPlayables.cs:28-32, 120-137` |
| The drawer's format ladder reads kind 5 (`TrackExpansion.Formats`), sorted by bitrate DESC, radio items "`<label>   <n> kbps`", "Use my default quality" resets a session-scoped per-uri override | `FormatSplitButton.cs:84-123`; `SpotifyTrackExpansionService.cs:318-339` |
| Worth keeping verbatim in Wave 3: `GaplessJoinClock` (41 lines, tested), `PrefetchingReadStream`'s never-return-zero invariant, `ReadAheadPolicy`'s pure shape, the epoch-guarded serialized pump | owner H, plan §5 Wave 3 |

### 1.4 Spotify Lossless, as shipped (web, September 2025 →)

| Fact | Source |
|---|---|
| "With Lossless, you can now stream tracks in up to 24-bit/44.1 kHz FLAC"; Premium, no surcharge; launched 2025-09-10 in AU, AT, CZ, DK, DE, JP, NZ, NL, PT, SE, US, UK, "more than 50 markets through October"; mobile, desktop, tablet and Connect partners | https://newsroom.spotify.com/2025-09-10/lossless-listening-arrives-on-spotify-premium-with-a-richer-more-detailed-listening-experience/ |
| **Default is OFF**: "You'll need to enable Lossless manually on each device"; the rungs are "Low, Normal, High, Very High, and now Lossless", chosen separately for "Wi-Fi, cellular, and downloads"; "the Lossless indicator will appear in the Now Playing view" | same; https://newsroom.spotify.com/2025-10-08/new-spotify-features-to-use/ |
| Support page: profile → Settings and privacy → **Media quality** → Lossless for Wi-Fi / cellular / downloads; desktop ≥ 1.2.67; "1.5 to 2 Mbps" recommended; unavailable for music videos, podcasts, audiobooks | https://support.spotify.com/us/article/lossless-audio-quality/ |
| Rung equivalences: Low ≈ 24 kbit/s, Normal ≈ 96, High ≈ 160, Very high ≈ 320, "Lossless: Equivalent up to 24-bit/44.1kHz FLAC"; the default rung is "Automatic" | https://support.spotify.com/us/article/audio-quality/ |
| Observed on the wire: 44.1 kHz FLAC at ~700 kbit/s (16-bit) and ~1,400 kbit/s (24-bit); "bitrate is variable per track"; the `PlaybackService/GetFiles` response lists `flac/flac` entries up to 1,400,000 bps | community threads (403 to the crawler, search summaries); librespot #1583, Lustyn 2025-10-02 |
| Discovery: "You need to take the `original_audio.uuid` field, convert it to a `spotify:audio:...` id and send it with the `AUDIO_FILES` entity request"; "The current format values (16 and 22) for FLAC and FLAC_24 respectively are correct"; the classic `/metadata/4/track/` route lists AAC_24 and no FLAC | https://github.com/librespot-org/librespot/discussions/1578; https://github.com/librespot-org/librespot/issues/1583 |
| DRM: "The encryption mode is the same, AES-128-CTR, as is the IV" — but "the keys to lossless don't work via shannon"; the desktop app "doing requests to playplay for keys" | librespot #1583 (SuisChan, kingosticks, 2025-09/10) |
| No header: "just raw FLAC files without any modifications"; "`head-fa` files are just a chunk of the original, they are identical down to the last bit" | librespot #1583, SuisChan 2025-09-29 |
| go-librespot ships it: FLAC only when the PlayPlay plugin is present (commit ef213e5, v0.6.0); `flac.New(log, audioStream, normalisationFactor)` over the decrypted stream **at offset 0**; libFLAC via cgo; samples normalised by `2^(bps-1)` (v0.8.0 fix) | https://github.com/devgianlu/go-librespot/commit/ef213e5424c755a1a5dcfe2607e3997de5ea4eb6; `player/player.go`, `player/format.go`, `flac/decoder.go` |
| librespot itself: enumerates 16/22 (`metadata.proto:309/313`) and never requests them; locked all lossless threads 2025-11-07 after contact from Spotify | §1.5 |
| Windows was not bit-perfect until Spotify's own "Exclusive Mode for Windows" (March 2026); before it "your 44.1kHz FLAC gets converted to 48kHz before it reaches your DAC" — the identical situation as Wavee's shared-mode graph | https://www.audioendgame.com/articles/spotify-lossless-in-2026-is-it-actually-hi-fi-and-what-gear-do-you-need; https://www.notebookcheck.net/Spotify-lossless-is-apparently-not-lossless-on-Windows-but-a-fix-might-be-coming-soon.1128528.0.html |
| Third-party Connect endpoints (incl. librespot) still receive Ogg 320 — lossless does not transfer to a foreign device by itself | community, June 2026 (search summary) |
| Markets with a "Premium Platinum" tier (IN, ID, AE, SA, ZA): lossless only there, "up to 840 kbps" | https://techcrunch.com/2025/11/13/spotify-introduces-a-premium-platinum-plan-with-lossless-access-in-five-markets; https://support.spotify.com/in-en/article/premium-platinum/ |

Two consequences beyond §1.1's three fixes. **Entitlement is per account and per market, and the only way a client
learns it is to ask kind 5 and see whether a FLAC row comes back** — there is no capability flag on the session
(librespot's `connect.proto` even reserves `supports_lossless_audio = 24`). So "is lossless available" is a
per-track catalogue fact (§5), not a session fact, and the settings rung cannot be gated on entitlement (§9 Q1).
**Spotify defaults lossless OFF and per device**, which is the precedent this plan follows: the rung ships, the
default stays VeryHigh320 (`playback.quality` default 2 is untouched) and the metered cap stays High (§5.4).

### 1.5 The reference decoders and clients on disk

**Symphonia** (`C:\WAVEE\Symphonia`, Rust, `symphonia-bundle-flac` + `symphonia-common/src/xiph/audio/flac/mod.rs`
+ `symphonia-metadata/src/embedded/{flac,vorbis}.rs`) is the model for §3's decoder. What is taken from it,
by file:line, with the C# section that transliterates each:

| Symphonia | What it settles | §3 |
|---|---|---|
| `frame.rs:64-75` `sync_frame` — widen the 14-bit sync to a 16-bit window `(sync & 0xfffc) == 0xfff8`, blocking strategy = `sync & 1` | the sync scan | 3.5 |
| `frame.rs:77-222` `read_frame_header` — CRC-8 seeded 0 over the sync bytes + header; the coded number is read BEFORE the block-size/rate extension bytes; block-size / rate / channel / bps tables; reserved-bit checks | the header parser | 3.5 |
| `frame.rs:272-310` `utf8_decode_be_u64` — 7-byte / 36-bit coded number; invalid prefix → `None` so the resync scanner can reject | `ReadCodedNumber` | 3.5 |
| `frame.rs:225-267` `is_likely_frame_header` — the cheap plausibility gate before the CRC-8 parse; `parser.rs:586-647` `strict_frame_header_check` — rate/bps/channels must match STREAMINFO, block length bounded, sequence monotonic | the seek/resync probe | 3.9 |
| `decoder.rs:341-400` `read_subframe` — pad bit, 6-bit type, wasted-bits unary, the four decoders, `samples_shl` after | subframes | 3.6 |
| `decoder.rs:663-710` `fixed_predict` — i64 wrapping accumulate per order; `decoder.rs:716-752` `lpc_predict<const N>` — coefficients reversed into a fixed 32-slot array, i64 dot product, a "prefill" phase for the first `N − order` samples, no intrinsics — the fixed trip count is what lets the compiler unroll | FIXED / LPC restoration | 3.6 |
| `decoder.rs:513-615` `decode_residual` / `decode_rice_partition`; `decoder.rs:617-644` `rice_signed_to_i32` = `(u >> 1) ^ -(u & 1)` | the Rice reader | 3.7 |
| `bit.rs:865-938` `BitReaderLtr` — 64-bit MSB-aligned cache; `bit.rs:642-671` `read_unary_zeros` via `leading_zeros()`; `bit.rs:566-589` the two-step shift so `bit_width == 0` is not UB | `BitReader` | 3.3 |
| `decoder.rs:32-82` the three decorrelations; the side channel gets `bps + 1` (`decoder.rs:195-227`); side-first read order for side/right | stereo | 3.8 |
| `crc8.rs:24-66` (poly 0x07, table), `crc16.rs:288-339` (poly 0x8005, slicing-by-8) — the frame CRC-16 is checked by the **parser** over the frame bytes, not by the sample decoder | CRC | 3.10 |
| `demuxer.rs:249-393` `seek` — seek-table narrowing, then bisection by byte offset with `resync` at the midpoint while the bracket is wider than `2 × 8096` bytes, then linear; `reset` is a no-op because "no state is stored between packets" | seek | 3.9 |
| `mod.rs:100-193` STREAMINFO (34 bytes; min block ≥ 16; rate 1..655350; channels 1..8; bps 4..32; `n_samples 0 = unknown`; all-zero MD5 = absent); `vorbis.rs:369-416` VORBIS_COMMENT (LE lengths, vendor ignored, split on first `=`, keys lowercased: `title`, `artist`, `album`, `albumartist`, `tracknumber`, `date`); `flac.rs:62-134` PICTURE (all BE u32; 0 width/height = unknown) | metadata | 3.4 |
| `validate.rs:36-98` the MD5 packing: bytes per sample = ceil(bps/8), interleaved, little-endian, low bytes of the i32; fed BEFORE the output up-shift (`decoder.rs:231-242`) | the MD5 oracle | 3.10 |
| `decoder.rs:236-242` output is planar `i32` shifted to full 32-bit scale — 24-bit becomes `sample << 8` | our float scale is `1 / 2^(bps−1)` instead (go-librespot v0.8.0 landed the same constant) | 3.8 |

**librespot / librespot-java / fastpotify / SpotiLoad** (`C:\WAVEE\*`): none has a working lossless path.
librespot enumerates `FLAC_FLAC = 16` / `FLAC_FLAC_24BIT = 22` (`metadata/src/audio/file.rs:16-39`) and never
selects them (`playback/src/player.rs:1042-1087` lists Vorbis/MP3 only); it applies the `0xa7` offset **only to Ogg**
(`player.rs:51`, `:1121-1129`) and hard-locks 44.1 kHz (`playback/src/lib.rs:18`, `symphonia_decoder.rs:66-74`) —
the one thing not to copy. Its `AUDIO_FILES = 5` / `AudioFilesExtensionResponse { files, default_file_normalization_params,
default_album_normalization_params, audio_id }` protos exist and are unused (`spclient.rs:626-644` only wires the
`*_V4` kinds). librespot-java's `metadata.proto` mislabels 16 as `AAC_24_NORM`. fastpotify inherits librespot and
documents "librespot does not receive lossless streams". SpotiLoad is an in-process hook on the official client and
carries no format code. Net: Wave 2's `Spotify.Audio.cs` is already ahead of every open client on the wire side;
the decoder is the missing half.

### 1.6 The format (RFC 9639, https://www.rfc-editor.org/rfc/rfc9639.html)

Section numbers used in §3: §7 streamable subset (block ≤ 4608 at ≤ 48 kHz, ≤ 16384 above; LPC order ≤ 12 at
≤ 48 kHz; Rice partition order ≤ 8); §8.1 block header; §8.2 STREAMINFO (min/max block MUST be 16..65535; the MD5
is over interleaved, signed, little-endian samples widened to whole bytes); §8.5 SEEKTABLE; §8.6 VORBIS_COMMENT (LE
lengths); §8.8 PICTURE; §9.1 frame header (15-bit sync `0b111111111111100` + blocking bit, §9.1.1-9.1.4 the four
code tables, §9.1.5 the coded number, §9.1.8 CRC-8 poly `x^8+x^2+x+1`); §9.2.1 subframe header; §9.2.2 wasted bits
("the number of used wasted bits minus 1 appears in unary form"); §9.2.5 fixed predictors; §9.2.6 LPC (precision
`0b1111` forbidden, shift MUST NOT be negative); §9.2.7 residual (4/5-bit Rice, escape = all ones then 5 bits of
width, zigzag "positive → doubled, negative → ×−2 − 1"); §4.2 stereo decorrelation (mid `<< 1 | side & 1`, then
`(mid ± side) >> 1`; side has one extra bit); §9.3 frame footer CRC-16 poly `x^16+x^15+x^2+1`.

The Spotify subset, from §1.4: 44.1 kHz, 16- or 24-bit, stereo, fixed block size (the observed files are
libFLAC-encoded, block 4096, subset-conformant); no seek table can be assumed. A local file can be anything the
subset allows and the decoder handles the full subset plus 8..32-bit and 1..8 channels (downmixed, §4.3).

---

## 2. The decoder — three options, one recommendation

| | (a) from-scratch CORE decoder, `Playback/Playback.Audio.Flac.cs` | (b) `FlacBox` 1.0.0 (what 0.2.9 referenced) | (c) Windows FLAC MFT through the engine |
|---|---|---|---|
| Managed / AOT | Pure C#, no P/Invoke, no reflection; the same span discipline as `Spotify.Decode.cs` | Managed, no P/Invoke, no functional reflection (verified against the DLL's metadata, §1.3) — but a net35 IL 2.0 binary restored under `NU1701` suppression, no source, **no license** | COM interop (`IMFTransform` / `IMFSourceReader`) — AOT-viable via `ComWrappers` as the engine's `MfAacDecoder` proves, but the engine has **no** `IMFByteStream` over a managed stream and **no** `IMFSourceReader` path; both are new engine work in `..\fluent-gpu`, verified there |
| Allocation per frame | **Zero after `Open`**: every buffer sized from STREAMINFO's max block size at open; the bit reader is a `ref struct` over a byte window (§3.11) | One `IEnumerable<int>` enumerator per frame + an interface call per sample (`GetValues()`); internal `byte[]`/`int[]` churn with no pooling | The MFT allocates its own output samples (`IMFSample`/`IMFMediaBuffer`) per `ProcessOutput`; COM round trips per block; can be pooled with effort |
| 24-bit | Yes: `bps` from the frame header, scale `1 / 2^(bps−1)`; 8..32-bit, wasted bits, 20-bit all handled | Structurally yes (`int` samples scaled by `1 << (bps−1)`), overflows at 32 bps | Output types **undocumented** ("Supported Media Formats in Media Foundation" still omits FLAC — learn.microsoft.com); 24-bit/96 kHz decode unverified; a stock byte-stream handler rejects some valid FLACs (learn.microsoft.com Q&A) |
| Seek | Seek table → bracketed sync search → linear (§3.9); exact sample; bounded probes over `CtrStream`'s 128 KiB ranged chunks | **None** — 0.2.9's `SeekTo` was a silent no-op and the engine was told it succeeded (§1.3) | `IMFSourceReader::SetCurrentPosition` — approximate, resolved by the handler; over a ranged CDN stream every seek is a byte-stream `Seek` + re-parse the handler decides |
| arm64 | Same code; `Vector128` paths have scalar fallbacks (§3.8) | Same code | MSFlacDecoder.dll ships on arm64 Windows; same interop |
| Testable (D17, no engine) | Pure over spans: the xiph vectors + the STREAMINFO MD5 are a complete oracle (§7) | Testable, but the behaviour under test is a black box we cannot fix | Needs MF on the test host; not pure |
| Gapless | `ExactFrames` from STREAMINFO, exact sample position from the frame header | STREAMINFO only | MF reports duration, not sample-exact position |
| Cost | ≈ 1,100 lines CORE + 350 tests; the format is small (RFC 9639 is 100 pages, the decoder half of it ~30) | 0 lines, permanent debt | ≈ 600 lines engine + 200 app, plus the byte-stream handler research |
| Reference precedent | Symphonia (`symphonia-bundle-flac`, pure Rust, no intrinsics — §1.5), SimpleFlac (single-file MIT C#, .NET 8, "no `unsafe` code or native dependencies", https://github.com/jdpurcell/SimpleFlac), go-librespot uses libFLAC via cgo | — | Spotify's own Windows client uses its own decoder, not MF |

**Recommendation: (a).** The decoder is the one piece of the FLAC feature that is pure and small, and it is the piece
0.2.9 got wrong (no seek) with a dependency that cannot be fixed. It lands as CORE with the same tests-and-fixtures
discipline as `Spotify.Decode.cs`, can be written and gated today, and gives Wave 3 a decoder whose seek is exact.
(b) is rejected on the seek defect and the license alone. (c) is rejected because the engine would grow two MF
abstractions for one codec that Windows documents least, and the output would still be resampled and float-converted
by us — the MFT buys nothing the graph does not already require us to do.

---

## 3. The decoder — `Playback/Playback.Audio.Flac.cs` (CORE, new named partial)

Role CORE, owner **U** (new; §8), Wave **3-parallel** (can start today), budget **1,100**. `public static partial
class Playback { public static class Flac { … } }` — the same nesting as `Spotify.Audio`. Everything is `static`
or a `ref struct` / `struct`; the only class is `Flac.Decoder`, constructed once per open and reused across seeks.
No `Stream`, no `IMediaByteSource`, no engine type: the decoder reads from a `ReadOnlySpan<byte>` window that the
SHELL adapter (§4) fills, and writes `int` samples into buffers it owns. That is what makes every test in §7 a
pure fact over a byte array.

### 3.1 Stream → decoder → graph

```
   CDN (AES-128-CTR, no header)             local .flac                 module / external
   ───────────────────────────             ────────────                 ─────────────────
   Spotify.Audio.CtrStream (F)              FileByteSource (engine)      Modules.Host stream (T)
   skip = 0 for Flac/Flac24 (§1.1 fix 1)
             │                                   │                              │
             └───────────────┬───────────────────┴──────────────────────────────┘
                             ▼
                 IMediaByteSource  (engine seam; StreamByteSource wraps the CtrStream)
                             │  Read(Span<byte>) — blocking, on FluentGpu.AudioProducer (decode-ahead thread)
                             ▼
     ┌─────────────────────────────────────────────────────────────────────────────────────────┐
     │  Playback.Audio.cs (SHELL, owner H)  —  FlacAudioDecoder : IAudioDecoder                │
     │                                                                                          │
     │   byte window  _win[]  (64 KiB, grown once to STREAMINFO max-frame if larger)            │
     │        │  ReadOnlySpan<byte> over [cursor .. filled)                                     │
     │        ▼                                                                                 │
     │   Playback.Flac.Decoder (CORE)  ── DecodeFrame(span, out consumed) ──▶ int[] _pcm        │
     │        planar int samples, one run per channel, block ≤ MaxBlock                        │
     │        │                                                                                 │
     │        ▼  Flac.ToFloat: scale 1/2^(bps−1) × gainLinear, downmix >2ch, interleave        │
     │   float[] _conformed (target channels, SOURCE rate)                                      │
     │        │                                                                                 │
     │        ▼  LinearResampler when srcRate != mix rate (engine, decode edge)                 │
     │   Read(Span<float> dst)  →  frames at MIX rate                                           │
     └─────────────────────────────────────────────────────────────────────────────────────────┘
                             │
                             ▼
     DecoderAudioSource → [TrimmingSource when Gapless.ExactFrames ≥ 0] → RingAudioSource (1 s ring)
                             │
                             ▼   RT thread: FluentGpu.AudioRT (MMCSS Pro Audio), ~10 ms blocks
     CrossfadeMixer → master gain → master chain (EQ) → transport → TapBlock (level meter) → WASAPI shared
```

### 3.2 The frame, as the decoder sees it

```
 file:  "fLaC" | METADATA_BLOCK … (last=1) | FRAME | FRAME | FRAME | …            (RFC 9639 §8, §9)

 METADATA_BLOCK:  [1: last][7: type][24: length BE] [length bytes]
   type 0 STREAMINFO (34 B): u16 minBlock | u16 maxBlock | u24 minFrame | u24 maxFrame |
                             u20 rate | u3 channels−1 | u5 bps−1 | u36 totalSamples | u128 MD5
   type 3 SEEKTABLE: n × { u64 sample | u64 byteOffset (from first frame) | u16 samples }, placeholder sample = 2^64−1
   type 4 VORBIS_COMMENT (little-endian lengths!): u32 vendorLen, vendor, u32 count, count × { u32 len, "KEY=value" }
   type 6 PICTURE (big-endian): u32 kind | u32 mimeLen, mime | u32 descLen, desc | u32 w | u32 h | u32 depth | u32 colors | u32 dataLen | data

 FRAME:
   ┌ header ────────────────────────────────────────────────────────────────────────────────┐
   │ 11111111 111110bs │ bbbb rrrr │ cccc ppp 0 │ coded number (1..7 B) │ [block ext 1/2 B] │ [rate ext 1/2 B] │ CRC-8 │
   │  sync 14 + res + blocking  block/rate   chan/bps/res   frame# or sample#                                     │
   └────────────────────────────────────────────────────────────────────────────────────────┘
   ┌ subframe × channels ───────────────────────────────────────────────────────────────────┐
   │ 0 tttttt w [unary k−1]  ──  CONSTANT: s(bps)                                            │
   │                             VERBATIM: block × s(bps)                                    │
   │                             FIXED  n: n warm-up s(bps) │ residual                       │
   │                             LPC    n: n warm-up s(bps) │ u4 prec−1 │ s5 shift │ n × s(prec) │ residual │
   │   residual: u2 method (00: 4-bit rice, 01: 5-bit) │ u4 partitionOrder │ 2^order × partition │
   │   partition: u4|u5 param  (all ones ⇒ escape: u5 width, raw s(width) samples)           │
   │              else per sample: unary q │ u(param) r  →  zigzag((q << param) | r)          │
   └────────────────────────────────────────────────────────────────────────────────────────┘
   pad to byte │ CRC-16 (over everything from the sync byte)

 channel assignment 1000 L/S, 1001 S/R, 1010 M/S: the side subframe is decoded at bps+1, then §3.8 restores L/R.
```

### 3.3 The bit reader

A `ref struct` over the window, 64-bit MSB-aligned cache (Symphonia `BitReaderLtr`, `bit.rs:865-938`). An overrun
never throws: it sets `Overrun` and returns zeros, the frame decoder checks the flag once at the end, and the SHELL
refills the window and retries — a frame is decoded from a span that is guaranteed to hold it or the decode is
discarded (§3.11). That is what keeps the hot loop branch-light and the CORE free of exceptions.

```csharp
public static partial class Playback
{
    public static partial class Flac
    {
        /// <summary>MSB-first bit reader over a byte span. 64-bit cache, refilled a byte at a time so a window of any
        /// length is legal; reading past the end sets <see cref="Overrun"/> and yields zeros (never throws).</summary>
        public ref struct BitReader
        {
            readonly ReadOnlySpan<byte> _b;
            int _pos;          // next byte to load into the cache
            ulong _cache;      // MSB-aligned: the next bit to read is bit 63
            int _bits;         // valid bits in _cache
            public bool Overrun;

            public BitReader(ReadOnlySpan<byte> bytes, int start) { _b = bytes; _pos = start; }

            /// <summary>Byte offset of the next unread bit's byte. Exact only after <see cref="AlignToByte"/>.</summary>
            public readonly int BytePosition => _pos - (_bits >> 3);

            void Refill()
            {
                while (_bits <= 56)
                {
                    if (_pos >= _b.Length) { Overrun = _bits < 1; return; }
                    _cache |= (ulong)_b[_pos++] << (56 - _bits);
                    _bits += 8;
                }
            }

            /// <summary>Read 0..32 bits. n == 0 is legal and returns 0 (Rice parameter 0 is common).</summary>
            public uint Read(int n)
            {
                if (n == 0) return 0;
                if (_bits < n) { Refill(); if (_bits < n) { Overrun = true; _bits = 0; _cache = 0; return 0; } }
                uint v = (uint)(_cache >> (64 - n));
                _cache <<= n; _bits -= n;
                return v;
            }

            /// <summary>Read 33..64 bits (the 36-bit total-samples field, the 64-bit seek-point fields).</summary>
            public ulong ReadLong(int n) => n <= 32 ? Read(n) : ((ulong)Read(n - 32) << 32) | Read(32);

            /// <summary>Read n bits as a two's-complement signed value (n ≤ 32).</summary>
            public int ReadSigned(int n) => n == 0 ? 0 : (int)(Read(n) << (32 - n)) >> (32 - n);

            public bool ReadBit() => Read(1) != 0;

            /// <summary>Count zeros up to and including the terminating one (the Rice quotient; §9.2.7). One
            /// <c>LeadingZeroCount</c> per cache fill, not one branch per bit (Symphonia <c>read_unary_zeros</c>).</summary>
            public int ReadUnary()
            {
                int n = 0;
                while (true)
                {
                    if (_bits == 0) { Refill(); if (_bits == 0) { Overrun = true; return 0; } }
                    int z = System.Numerics.BitOperations.LeadingZeroCount(_cache);
                    if (z < _bits) { n += z; _cache <<= z + 1; _bits -= z + 1; return n; }
                    n += _bits; _cache = 0; _bits = 0;
                }
            }

            /// <summary>Drop the bits to the next byte boundary (the end of the subframes, §9.3).</summary>
            public void AlignToByte() { int drop = _bits & 7; _cache <<= drop; _bits -= drop; }
        }
    }
}
```

### 3.4 Metadata: STREAMINFO, SEEKTABLE, VORBIS_COMMENT, PICTURE

`ParseHeaders` walks the metadata blocks once at open. It is also the whole of the local-file duration/tag probe
(§6): the same function, called with the file's first 64 KiB, answers duration, title/artist/album and where the
cover bytes are. Text comes out as byte ranges into the span (P14: the caller interns on the UI thread; the
decoder never makes a `string`).

```csharp
public static partial class Playback
{
    public static partial class Flac
    {
        public const uint Magic = 0x664C6143;                      // "fLaC"
        public const int StreamInfoBytes = 34;
        public const int MaxChannels = 8;

        public enum BlockType : byte { StreamInfo = 0, Padding = 1, Application = 2, SeekTable = 3, VorbisComment = 4, CueSheet = 5, Picture = 6 }

        /// <summary>STREAMINFO (§8.2). <see cref="TotalSamples"/> 0 = unknown (a live encode); an all-zero
        /// <see cref="Md5"/> = absent. Plain fields, no methods: the decoder and the probe both read it.</summary>
        public struct StreamInfo
        {
            public ushort MinBlock, MaxBlock;
            public uint MinFrame, MaxFrame;         // 0 = unknown
            public int SampleRate;                  // 1..655350
            public byte Channels;                   // 1..8
            public byte Bps;                        // 4..32
            public long TotalSamples;               // 0 = unknown
            public Md5Digest Md5;
            public readonly bool HasMd5 => !Md5.IsZero;
            public readonly long DurationMs => SampleRate > 0 ? TotalSamples * 1000 / SampleRate : 0;
        }

        [System.Runtime.CompilerServices.InlineArray(16)]
        public struct Md5Digest { byte _e0; public readonly bool IsZero { get { foreach (byte b in this) if (b != 0) return false; return true; } } }

        /// <summary>A seek point (§8.5): sample number, byte offset from the FIRST FRAME, samples in that frame.</summary>
        public readonly record struct SeekPoint(long Sample, long Offset, ushort Samples);

        /// <summary>A byte range inside the span the parser was given — a tag value, the picture bytes.</summary>
        public readonly record struct ByteRange(int Offset, int Length) { public bool IsEmpty => Length == 0; }

        /// <summary>The row-facing tags (§6). Ranges into the header span; empty when the file has no comment block.</summary>
        public struct Tags
        {
            public ByteRange Title, Artist, Album, AlbumArtist, Date, TrackNumber;
            public ByteRange PictureMime, PictureData;
            public uint PictureKind;                // 3 = front cover (§8.8); the first picture wins, a front cover replaces it
        }

        /// <summary>What <see cref="ParseHeaders"/> answers. <see cref="FirstFrame"/> is the byte offset of the first
        /// frame's sync byte, which every seek-table offset is relative to.</summary>
        public struct Headers
        {
            public StreamInfo Info;
            public Tags Tags;
            public int FirstFrame;
            public int SeekPointCount;              // how many were written into the caller's seek span
            public bool Complete;                   // false ⇒ the span ended before the last metadata block
            public bool Valid;                      // magic + STREAMINFO present and sane
        }

        /// <summary>Parse "fLaC" + the metadata blocks at the start of <paramref name="head"/>. Pure. Seek points are
        /// written into <paramref name="seek"/> (the caller sizes it; 1,024 covers a 3-hour file at one point per 10 s —
        /// excess points are dropped, never allocated). A span that stops inside a block reports
        /// <c>Complete = false</c> so the caller can fetch more and call again.</summary>
        public static Headers ParseHeaders(ReadOnlySpan<byte> head, Span<SeekPoint> seek)
        {
            Headers h = default;
            if (head.Length < 8 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head) != Magic) return h;
            int pos = 4;
            bool last = false;
            bool sawInfo = false;
            while (!last)
            {
                if (pos + 4 > head.Length) return h;                              // Complete = false
                byte b0 = head[pos];
                last = (b0 & 0x80) != 0;
                var type = (BlockType)(b0 & 0x7F);
                int len = (head[pos + 1] << 16) | (head[pos + 2] << 8) | head[pos + 3];
                pos += 4;
                if ((b0 & 0x7F) == 127) return h;                                 // forbidden (§8.1)
                if (pos + len > head.Length) return h;                            // Complete = false
                ReadOnlySpan<byte> body = head.Slice(pos, len);
                switch (type)
                {
                    case BlockType.StreamInfo:
                        if (sawInfo || len != StreamInfoBytes || !ParseStreamInfo(body, out h.Info)) return h;
                        sawInfo = true;
                        break;
                    case BlockType.SeekTable:
                        h.SeekPointCount = ParseSeekTable(body, seek);
                        break;
                    case BlockType.VorbisComment:
                        ParseVorbisComment(body, pos, ref h.Tags);
                        break;
                    case BlockType.Picture:
                        ParsePicture(body, pos, ref h.Tags);
                        break;
                    default: break;                                                // padding, application, cuesheet: skipped
                }
                pos += len;
            }
            if (!sawInfo) return h;                                                // STREAMINFO MUST be first (§8.2); a file without one is faulty
            h.FirstFrame = pos;
            h.Complete = true;
            h.Valid = true;
            return h;
        }

        static bool ParseStreamInfo(ReadOnlySpan<byte> b, out StreamInfo si)
        {
            si = default;
            var r = new BitReader(b, 0);
            si.MinBlock = (ushort)r.Read(16);
            si.MaxBlock = (ushort)r.Read(16);
            si.MinFrame = r.Read(24);
            si.MaxFrame = r.Read(24);
            si.SampleRate = (int)r.Read(20);
            si.Channels = (byte)(r.Read(3) + 1);
            si.Bps = (byte)(r.Read(5) + 1);
            si.TotalSamples = (long)r.ReadLong(36);
            b.Slice(18, 16).CopyTo(si.Md5);
            // §8.2: 16 ≤ min ≤ max ≤ 65535; rate 1..655350 (0 is legal only for a non-audio stream we do not play).
            return si.MinBlock >= 16 && si.MaxBlock >= si.MinBlock
                && si.SampleRate is >= 1 and <= 655_350 && si.Channels <= MaxChannels && si.Bps is >= 4 and <= 32;
        }

        static int ParseSeekTable(ReadOnlySpan<byte> b, Span<SeekPoint> into)
        {
            int n = 0;
            for (int i = 0; i + 18 <= b.Length && n < into.Length; i += 18)
            {
                ulong sample = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b[i..]);
                if (sample == ulong.MaxValue || sample > long.MaxValue) continue;   // placeholder (§8.5.1)
                ulong offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b[(i + 8)..]);
                if (offset > long.MaxValue) continue;
                ushort samples = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(b[(i + 16)..]);
                into[n++] = new SeekPoint((long)sample, (long)offset, samples);
            }
            return n;
        }

        /// <summary>§8.6, little-endian lengths. Keys are ASCII and case-insensitive; the FIRST '=' splits. Only the
        /// six keys the row needs are kept; everything else is skipped by length.</summary>
        static void ParseVorbisComment(ReadOnlySpan<byte> b, int baseOffset, ref Tags tags)
        {
            int pos = 0;
            if (!ReadLeU32(b, ref pos, out uint vendorLen) || vendorLen > (uint)(b.Length - pos)) return;
            pos += (int)vendorLen;
            if (!ReadLeU32(b, ref pos, out uint count)) return;
            for (uint i = 0; i < count; i++)
            {
                if (!ReadLeU32(b, ref pos, out uint len) || len > (uint)(b.Length - pos)) return;
                ReadOnlySpan<byte> field = b.Slice(pos, (int)len);
                int eq = field.IndexOf((byte)'=');
                if (eq > 0)
                {
                    ReadOnlySpan<byte> key = field[..eq];
                    var value = new ByteRange(baseOffset + pos + eq + 1, (int)len - eq - 1);
                    if (KeyIs(key, "TITLE"u8)) tags.Title = value;
                    else if (KeyIs(key, "ARTIST"u8)) tags.Artist = value;
                    else if (KeyIs(key, "ALBUM"u8)) tags.Album = value;
                    else if (KeyIs(key, "ALBUMARTIST"u8) || KeyIs(key, "ALBUM ARTIST"u8)) tags.AlbumArtist = value;
                    else if (KeyIs(key, "DATE"u8)) tags.Date = value;
                    else if (KeyIs(key, "TRACKNUMBER"u8)) tags.TrackNumber = value;
                }
                pos += (int)len;
            }
        }

        static bool KeyIs(ReadOnlySpan<byte> key, ReadOnlySpan<byte> name)
        {
            if (key.Length != name.Length) return false;
            for (int i = 0; i < key.Length; i++)
            {
                byte k = key[i];
                if (k >= (byte)'a' && k <= (byte)'z') k -= 32;
                if (k != name[i]) return false;
            }
            return true;
        }

        static bool ReadLeU32(ReadOnlySpan<byte> b, ref int pos, out uint v)
        {
            v = 0;
            if (pos + 4 > b.Length) return false;
            v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
            pos += 4;
            return true;
        }

        /// <summary>§8.8, big-endian. The first picture is kept; a later FRONT COVER (kind 3) replaces it.</summary>
        static void ParsePicture(ReadOnlySpan<byte> b, int baseOffset, ref Tags tags)
        {
            int pos = 0;
            if (!ReadBeU32(b, ref pos, out uint kind)) return;
            if (!ReadBeU32(b, ref pos, out uint mimeLen) || mimeLen > (uint)(b.Length - pos)) return;
            var mime = new ByteRange(baseOffset + pos, (int)mimeLen); pos += (int)mimeLen;
            if (!ReadBeU32(b, ref pos, out uint descLen) || descLen > (uint)(b.Length - pos)) return;
            pos += (int)descLen;
            pos += 16;                                                            // width, height, depth, colours: not needed for a thumbnail
            if (!ReadBeU32(b, ref pos, out uint dataLen) || dataLen > (uint)(b.Length - pos)) return;
            if (tags.PictureData.IsEmpty || kind == 3)
            {
                tags.PictureKind = kind;
                tags.PictureMime = mime;
                tags.PictureData = new ByteRange(baseOffset + pos, (int)dataLen);
            }
        }

        static bool ReadBeU32(ReadOnlySpan<byte> b, ref int pos, out uint v)
        {
            v = 0;
            if (pos + 4 > b.Length) return false;
            v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b[pos..]);
            pos += 4;
            return true;
        }
    }
}
```

`ParseHeaders` deliberately does not stop at the first frame if a block is truncated: an 80 KiB Spotify head or a
64 KiB local read holds every block a real file has except a large PICTURE (xiph subset file 50 is 16 MB of PICTURE)
— the caller sees `Complete = false`, reads to `pos + len` and calls again. The tags of a file with a 10 MB cover
cost one extra read, never an allocation.

### 3.5 The frame header, the four code tables, CRC-8

```csharp
public static partial class Playback
{
    public static partial class Flac
    {
        /// <summary>What a frame header says (§9.1). <see cref="SampleNumber"/> is ABSOLUTE for both blocking
        /// strategies: fixed-size files carry a frame number and the decoder multiplies by the STREAMINFO min block
        /// (Symphonia <c>calc_sync_info</c>, parser.rs:566-584).</summary>
        public struct FrameHeader
        {
            public int BlockSize;                   // samples per channel
            public int SampleRate;
            public byte Channels;                   // 1..8
            public byte Assignment;                 // 0 independent, 1 left/side, 2 side/right, 3 mid/side
            public byte Bps;
            public bool Variable;
            public long SampleNumber;
            public int HeaderBytes;                 // sync .. CRC-8 inclusive
        }

        public enum HeaderResult : byte { Ok, NotSync, Reserved, BadCrc, Truncated, Mismatch }

        /// <summary>The cheap gate before the CRC-8 parse, for the sync scanner and the seek probe (Symphonia
        /// <c>is_likely_frame_header</c>, frame.rs:225-267). Four bytes: the sync, and no reserved code in the
        /// block-size / rate / channel / bps nibbles.</summary>
        public static bool LooksLikeHeader(ReadOnlySpan<byte> b)
        {
            if (b.Length < 4) return false;
            if (b[0] != 0xFF || (b[1] & 0xFC) != 0xF8) return false;
            int block = b[2] >> 4, rate = b[2] & 0xF, chan = b[3] >> 4, bps = (b[3] >> 1) & 7;
            return block != 0 && rate != 0xF && chan < 0xB && bps != 3 && (b[3] & 1) == 0;
        }

        /// <summary>Parse one frame header at <paramref name="b"/>[0]. STREAMINFO fills the "0000 = from streaminfo"
        /// codes and is the cross-check for rate / channels / bps (a mismatch is a false sync, not a format change —
        /// Symphonia <c>strict_frame_header_check</c>, parser.rs:586-647).</summary>
        public static HeaderResult ParseFrameHeader(ReadOnlySpan<byte> b, in StreamInfo si, out FrameHeader h)
        {
            h = default;
            if (b.Length < 6) return HeaderResult.Truncated;
            if (b[0] != 0xFF || (b[1] & 0xFC) != 0xF8) return HeaderResult.NotSync;
            h.Variable = (b[1] & 1) != 0;
            int blockCode = b[2] >> 4, rateCode = b[2] & 0xF, chanCode = b[3] >> 4, bpsCode = (b[3] >> 1) & 7;
            if (blockCode == 0 || rateCode == 0xF || chanCode > 0xA || bpsCode == 3 || (b[3] & 1) != 0) return HeaderResult.Reserved;

            int pos = 4;
            // The coded number comes BEFORE the block-size / rate extension bytes (§9.1, Symphonia frame.rs:103-136).
            if (!ReadCodedNumber(b, ref pos, out ulong number)) return b.Length < pos + 1 ? HeaderResult.Truncated : HeaderResult.Reserved;
            if (h.Variable) { if (number > 0xF_FFFF_FFFF) return HeaderResult.Reserved; h.SampleNumber = (long)number; }
            else { if (number > 0x7FFF_FFFF) return HeaderResult.Reserved; h.SampleNumber = (long)number * si.MinBlock; }

            switch (blockCode)
            {
                case 1: h.BlockSize = 192; break;
                case >= 2 and <= 5: h.BlockSize = 576 << (blockCode - 2); break;
                case 6: if (pos + 1 > b.Length) return HeaderResult.Truncated; h.BlockSize = b[pos++] + 1; break;
                case 7:
                    if (pos + 2 > b.Length) return HeaderResult.Truncated;
                    h.BlockSize = ((b[pos] << 8) | b[pos + 1]) + 1; pos += 2;
                    break;
                default: h.BlockSize = 256 << (blockCode - 8); break;                  // 8..15
            }
            switch (rateCode)
            {
                case 0: h.SampleRate = si.SampleRate; break;
                case 1: h.SampleRate = 88_200; break;   case 2: h.SampleRate = 176_400; break;  case 3: h.SampleRate = 192_000; break;
                case 4: h.SampleRate = 8_000; break;    case 5: h.SampleRate = 16_000; break;   case 6: h.SampleRate = 22_050; break;
                case 7: h.SampleRate = 24_000; break;   case 8: h.SampleRate = 32_000; break;   case 9: h.SampleRate = 44_100; break;
                case 10: h.SampleRate = 48_000; break;  case 11: h.SampleRate = 96_000; break;
                case 12: if (pos + 1 > b.Length) return HeaderResult.Truncated; h.SampleRate = b[pos++] * 1000; break;
                case 13:
                    if (pos + 2 > b.Length) return HeaderResult.Truncated;
                    h.SampleRate = (b[pos] << 8) | b[pos + 1]; pos += 2; break;
                default:
                    if (pos + 2 > b.Length) return HeaderResult.Truncated;
                    h.SampleRate = ((b[pos] << 8) | b[pos + 1]) * 10; pos += 2; break;   // 14
            }
            if (chanCode <= 7) { h.Channels = (byte)(chanCode + 1); h.Assignment = 0; }
            else { h.Channels = 2; h.Assignment = (byte)(chanCode - 7); }             // 8 → 1 L/S, 9 → 2 S/R, 10 → 3 M/S
            h.Bps = bpsCode switch { 0 => si.Bps, 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, _ => 32 };

            if (pos + 1 > b.Length) return HeaderResult.Truncated;
            if (Crc8(b[..pos]) != b[pos]) return HeaderResult.BadCrc;
            h.HeaderBytes = pos + 1;

            // The strict check: a valid CRC on a header that contradicts STREAMINFO is a false sync inside audio data.
            if (h.SampleRate != si.SampleRate || h.Channels != si.Channels || h.Bps != si.Bps
                || h.BlockSize > si.MaxBlock || h.BlockSize < 1) return HeaderResult.Mismatch;
            return HeaderResult.Ok;
        }

        /// <summary>§9.1.5: UTF-8-shaped, up to 7 bytes / 36 bits (Symphonia <c>utf8_decode_be_u64</c>). False on an
        /// invalid lead or continuation byte, which the sync scanner treats as "not a header".</summary>
        public static bool ReadCodedNumber(ReadOnlySpan<byte> b, ref int pos, out ulong value)
        {
            value = 0;
            if (pos >= b.Length) return false;
            byte lead = b[pos++];
            int extra;
            if (lead < 0x80) { value = lead; return true; }
            else if ((lead & 0xE0) == 0xC0) { value = (ulong)(lead & 0x1F); extra = 1; }
            else if ((lead & 0xF0) == 0xE0) { value = (ulong)(lead & 0x0F); extra = 2; }
            else if ((lead & 0xF8) == 0xF0) { value = (ulong)(lead & 0x07); extra = 3; }
            else if ((lead & 0xFC) == 0xF8) { value = (ulong)(lead & 0x03); extra = 4; }
            else if ((lead & 0xFE) == 0xFC) { value = (ulong)(lead & 0x01); extra = 5; }
            else if (lead == 0xFE) { value = 0; extra = 6; }
            else return false;
            for (int i = 0; i < extra; i++)
            {
                if (pos >= b.Length) return false;
                byte c = b[pos++];
                if ((c & 0xC0) != 0x80) return false;
                value = (value << 6) | (ulong)(c & 0x3F);
            }
            return true;
        }

        // ── CRC-8 (§9.1.8, poly x^8+x^2+x+1 = 0x07, init 0) and CRC-16 (§9.3, poly x^16+x^15+x^2+1 = 0x8005, init 0) ──
        // Tables are built once, at type init, from the polynomial — no 256-entry literal to get wrong.

        static readonly byte[] s_crc8 = BuildCrc8();
        static readonly ushort[] s_crc16 = BuildCrc16();

        static byte[] BuildCrc8()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int c = i;
                for (int k = 0; k < 8; k++) c = (c & 0x80) != 0 ? ((c << 1) ^ 0x07) & 0xFF : (c << 1) & 0xFF;
                t[i] = (byte)c;
            }
            return t;
        }

        static ushort[] BuildCrc16()
        {
            var t = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                int c = i << 8;
                for (int k = 0; k < 8; k++) c = (c & 0x8000) != 0 ? ((c << 1) ^ 0x8005) & 0xFFFF : (c << 1) & 0xFFFF;
                t[i] = (ushort)c;
            }
            return t;
        }

        public static byte Crc8(ReadOnlySpan<byte> b)
        {
            byte c = 0;
            foreach (byte x in b) c = s_crc8[c ^ x];
            return c;
        }

        public static ushort Crc16(ReadOnlySpan<byte> b)
        {
            ushort c = 0;
            foreach (byte x in b) c = (ushort)((c << 8) ^ s_crc16[(c >> 8) ^ x]);
            return c;
        }
    }
}
```

The CRC-16 runs over the whole frame after it is decoded (the decoder knows the frame's byte length only then), a
second pass over ≤ 16 KiB that the frame's bytes are still in cache for. Symphonia's slicing-by-8 table
(`crc16.rs:316-339`) is an optimisation for a 16 MB-per-frame worst case that the subset forbids; one table is
enough here and is what libFLAC's `crc.c` does.

### 3.6 The four subframes: CONSTANT, VERBATIM, FIXED, LPC

`DecodeSubframe` writes one channel's block into `samples` (length = block size). Residuals are decoded **in place**
from index `order` onward and the predictor is restored over them (Symphonia decodes into the same buffer,
`decoder.rs:429-511`; libFLAC does the same in `stream_decoder.c`). Wasted bits are shifted back at the end.

```csharp
public static partial class Playback
{
    public static partial class Flac
    {
        public enum FrameResult : byte { Ok, Overrun, Reserved, BadResidual, BadCrc16, Unsupported }

        /// <summary>One channel's subframe (§9.2). <paramref name="bps"/> already includes the side channel's extra bit.</summary>
        static FrameResult DecodeSubframe(ref BitReader r, int bps, Span<int> samples, Span<int> coefs)
        {
            if (r.ReadBit()) return FrameResult.Reserved;                          // the zero pad bit (§9.2.1)
            int type = (int)r.Read(6);
            int wasted = 0;
            if (r.ReadBit()) wasted = r.ReadUnary() + 1;                            // §9.2.2: unary (k−1)
            if (wasted >= bps) return FrameResult.Reserved;
            bps -= wasted;

            FrameResult res;
            if (type == 0) res = DecodeConstant(ref r, bps, samples);
            else if (type == 1) res = DecodeVerbatim(ref r, bps, samples);
            else if ((type & 0x38) == 0x08 && (type & 7) <= 4) res = DecodeFixed(ref r, bps, type & 7, samples);
            else if ((type & 0x20) != 0) res = DecodeLpc(ref r, bps, (type & 0x1F) + 1, samples, coefs);
            else return FrameResult.Reserved;
            if (res != FrameResult.Ok) return res;
            if (r.Overrun) return FrameResult.Overrun;

            if (wasted > 0) ShiftLeft(samples, wasted);                              // §9.2.2: "add k least-significant zero bits"
            return FrameResult.Ok;
        }

        static FrameResult DecodeConstant(ref BitReader r, int bps, Span<int> s)
        {
            int v = r.ReadSigned(bps);
            s.Fill(v);
            return FrameResult.Ok;
        }

        static FrameResult DecodeVerbatim(ref BitReader r, int bps, Span<int> s)
        {
            for (int i = 0; i < s.Length; i++) s[i] = r.ReadSigned(bps);
            return FrameResult.Ok;
        }

        /// <summary>§9.2.5. Warm-up samples verbatim, residual in place, then the fixed polynomial restored in place.
        /// Long arithmetic for orders 2-4 as Symphonia does (<c>fixed_predict</c>, decoder.rs:663-710): a 32-bit
        /// side channel's order-4 sum can exceed 32 bits before the residual brings it back.</summary>
        static FrameResult DecodeFixed(ref BitReader r, int bps, int order, Span<int> s)
        {
            if (order > s.Length) return FrameResult.Reserved;
            for (int i = 0; i < order; i++) s[i] = r.ReadSigned(bps);
            FrameResult res = DecodeResidual(ref r, order, s);
            if (res != FrameResult.Ok) return res;
            switch (order)
            {
                case 0: break;
                case 1:
                    for (int i = 1; i < s.Length; i++) s[i] += s[i - 1];
                    break;
                case 2:
                    for (int i = 2; i < s.Length; i++) s[i] += (int)(2L * s[i - 1] - s[i - 2]);
                    break;
                case 3:
                    for (int i = 3; i < s.Length; i++) s[i] += (int)(3L * s[i - 1] - 3L * s[i - 2] + s[i - 3]);
                    break;
                default:
                    for (int i = 4; i < s.Length; i++) s[i] += (int)(4L * s[i - 1] - 6L * s[i - 2] + 4L * s[i - 3] - s[i - 4]);
                    break;
            }
            return FrameResult.Ok;
        }

        /// <summary>§9.2.6. Coefficients are read into the caller's 32-slot scratch (P8: no per-frame array);
        /// a negative shift is forbidden by the RFC and rejected as Symphonia rejects it (decoder.rs:507-510).</summary>
        static FrameResult DecodeLpc(ref BitReader r, int bps, int order, Span<int> s, Span<int> coefs)
        {
            if (order > s.Length) return FrameResult.Reserved;
            for (int i = 0; i < order; i++) s[i] = r.ReadSigned(bps);
            int precision = (int)r.Read(4) + 1;
            if (precision == 16) return FrameResult.Reserved;                       // 0b1111 forbidden
            int shift = r.ReadSigned(5);
            if (shift < 0) return FrameResult.Unsupported;
            for (int i = 0; i < order; i++) coefs[i] = r.ReadSigned(precision);
            FrameResult res = DecodeResidual(ref r, order, s);
            if (res != FrameResult.Ok) return res;
            RestoreLpc(s, coefs[..order], shift, bps + precision + System.Numerics.BitOperations.Log2((uint)order) + 1);
            return FrameResult.Ok;
        }

        /// <summary>The recurrence s[i] += (Σ coef[j] · s[i−1−j]) >> shift. It is a recurrence — sample i needs sample
        /// i−1 — so it does not vectorise across samples; what varies is the accumulator width. libFLAC's rule
        /// (<c>lpc.c</c>, <c>FLAC__lpc_restore_signal</c> vs <c>_wide</c>): when bps + precision + log2(order) fits
        /// in 32 bits the products cannot overflow an int and the int path runs; otherwise the long path. Orders
        /// 1..12 (the whole streamable subset at ≤ 48 kHz, §7) are unrolled through a fixed-count inner loop the JIT
        /// unrolls itself; the generic loop covers 13..32 (96 kHz+ files).</summary>
        static void RestoreLpc(Span<int> s, ReadOnlySpan<int> c, int shift, int sumBits)
        {
            int order = c.Length;
            if (sumBits <= 32)
            {
                switch (order)
                {
                    case 1: for (int i = 1; i < s.Length; i++) s[i] += (c[0] * s[i - 1]) >> shift; break;
                    case 2: for (int i = 2; i < s.Length; i++) s[i] += (c[0] * s[i - 1] + c[1] * s[i - 2]) >> shift; break;
                    case 3: for (int i = 3; i < s.Length; i++) s[i] += (c[0] * s[i - 1] + c[1] * s[i - 2] + c[2] * s[i - 3]) >> shift; break;
                    case 4: for (int i = 4; i < s.Length; i++) s[i] += (c[0] * s[i - 1] + c[1] * s[i - 2] + c[2] * s[i - 3] + c[3] * s[i - 4]) >> shift; break;
                    case 8:
                        for (int i = 8; i < s.Length; i++)
                            s[i] += (c[0] * s[i - 1] + c[1] * s[i - 2] + c[2] * s[i - 3] + c[3] * s[i - 4]
                                   + c[4] * s[i - 5] + c[5] * s[i - 6] + c[6] * s[i - 7] + c[7] * s[i - 8]) >> shift;
                        break;
                    case 12:
                        for (int i = 12; i < s.Length; i++)
                            s[i] += (c[0] * s[i - 1] + c[1] * s[i - 2] + c[2] * s[i - 3] + c[3] * s[i - 4]
                                   + c[4] * s[i - 5] + c[5] * s[i - 6] + c[6] * s[i - 7] + c[7] * s[i - 8]
                                   + c[8] * s[i - 9] + c[9] * s[i - 10] + c[10] * s[i - 11] + c[11] * s[i - 12]) >> shift;
                        break;
                    default:
                        for (int i = order; i < s.Length; i++)
                        {
                            int sum = 0;
                            for (int j = 0; j < order; j++) sum += c[j] * s[i - 1 - j];
                            s[i] += sum >> shift;
                        }
                        break;
                }
                return;
            }
            for (int i = order; i < s.Length; i++)
            {
                long sum = 0;
                for (int j = 0; j < order; j++) sum += (long)c[j] * s[i - 1 - j];
                s[i] += (int)(sum >> shift);
            }
        }
    }
}
```

Why not `Vector128` in `RestoreLpc`: the dot product for sample `i` reads `s[i−1]`, which the previous iteration just
wrote, so the only vectorisable work is the per-sample dot product itself, and for order ≤ 12 the horizontal add
costs as much as the scalar sum (libFLAC's `lpc_intrin_sse41.c` does exactly this shuffle dance and is the
encoder's win, not the decoder's; Symphonia ships no intrinsics at all and relies on the fixed-trip-count loop —
`decoder.rs:716-752`). The P15 rule — SIMD above ~16 elements — is honoured where the elements are independent:
§3.8's decorrelation and float conversion.

### 3.7 The Rice residual

```csharp
public static partial class Playback
{
    public static partial class Flac
    {
        /// <summary>§9.2.7. Partitions are 2^order equal slices of the block; the first is short by the predictor
        /// order. The escape (parameter all ones) switches a partition to raw signed samples of the given width.</summary>
        static FrameResult DecodeResidual(ref BitReader r, int predictorOrder, Span<int> s)
        {
            int method = (int)r.Read(2);
            if (method > 1) return FrameResult.Reserved;
            int paramBits = method == 0 ? 4 : 5;
            int escape = (1 << paramBits) - 1;
            int partitionOrder = (int)r.Read(4);
            int partitions = 1 << partitionOrder;
            int perPartition = s.Length >> partitionOrder;
            if (perPartition << partitionOrder != s.Length) return FrameResult.BadResidual;      // block not divisible
            if (perPartition < predictorOrder) return FrameResult.BadResidual;                    // first partition would be negative

            int pos = predictorOrder;
            for (int p = 0; p < partitions; p++)
            {
                int count = p == 0 ? perPartition - predictorOrder : perPartition;
                int param = (int)r.Read(paramBits);
                Span<int> part = s.Slice(pos, count);
                if (param == escape)
                {
                    int width = (int)r.Read(5);
                    if (width == 0) part.Clear();                                                // xiph vector 64: "escape code zero"
                    else for (int i = 0; i < count; i++) part[i] = r.ReadSigned(width);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        uint q = (uint)r.ReadUnary();
                        uint u = (q << param) | r.Read(param);
                        part[i] = (int)(u >> 1) ^ -(int)(u & 1);                                  // zigzag (Symphonia rice_signed_to_i32)
                    }
                }
                pos += count;
                if (r.Overrun) return FrameResult.Overrun;
            }
            return FrameResult.Ok;
        }
    }
}
```

The Rice loop is the decoder's hot path — on a 16-bit 44.1 kHz stereo file it runs 88,200 times a second, each
iteration one `ReadUnary` (a `LeadingZeroCount` and a shift) and one `Read`. That is why the reader is a `ref
struct` passed by `ref` and why nothing in it can throw or allocate; the `Overrun` check is once per partition.

### 3.8 Stereo decorrelation, wasted bits, and the float conversion — the two `Vector128` sites

```csharp
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;

public static partial class Playback
{
    public static partial class Flac
    {
        /// <summary>§4.2. In place over the two channel runs. Left/side: R = L − S. Side/right: L = S + R.
        /// Mid/side: mid = (M &lt;&lt; 1) | (S &amp; 1); L = (mid + S) &gt;&gt; 1; R = (mid − S) &gt;&gt; 1.</summary>
        public static void Decorrelate(byte assignment, Span<int> ch0, Span<int> ch1)
        {
            int n = ch0.Length;
            int i = 0;
            if (n >= 16 && Vector128.IsHardwareAccelerated)                                   // P15: above ~16 elements
            {
                ref int a = ref MemoryMarshal.GetReference(ch0);
                ref int b = ref MemoryMarshal.GetReference(ch1);
                var one = Vector128.Create(1);
                for (; i <= n - Vector128<int>.Count; i += Vector128<int>.Count)
                {
                    var x = Vector128.LoadUnsafe(ref a, (nuint)i);
                    var y = Vector128.LoadUnsafe(ref b, (nuint)i);
                    switch (assignment)
                    {
                        case 1: (x - y).StoreUnsafe(ref b, (nuint)i); break;                         // ch1 = side → right
                        case 2: (x + y).StoreUnsafe(ref a, (nuint)i); break;                         // ch0 = side → left
                        default:
                            var mid = Vector128.ShiftLeft(x, 1) | (y & one);
                            Vector128.ShiftRightArithmetic(mid + y, 1).StoreUnsafe(ref a, (nuint)i);
                            Vector128.ShiftRightArithmetic(mid - y, 1).StoreUnsafe(ref b, (nuint)i);
                            break;
                    }
                }
            }
            for (; i < n; i++)                                                                 // the scalar tail, and the whole thing on a non-SIMD host
            {
                switch (assignment)
                {
                    case 1: ch1[i] = ch0[i] - ch1[i]; break;
                    case 2: ch0[i] += ch1[i]; break;
                    default:
                        int mid = (ch0[i] << 1) | (ch1[i] & 1);
                        int side = ch1[i];
                        ch0[i] = (mid + side) >> 1;
                        ch1[i] = (mid - side) >> 1;
                        break;
                }
            }
        }

        static void ShiftLeft(Span<int> s, int bits)
        {
            int i = 0;
            if (s.Length >= 16 && Vector128.IsHardwareAccelerated)
            {
                ref int a = ref MemoryMarshal.GetReference(s);
                for (; i <= s.Length - Vector128<int>.Count; i += Vector128<int>.Count)
                    Vector128.ShiftLeft(Vector128.LoadUnsafe(ref a, (nuint)i), bits).StoreUnsafe(ref a, (nuint)i);
            }
            for (; i < s.Length; i++) s[i] <<= bits;
        }

        /// <summary>Planar int → interleaved float, scaled by 1 / 2^(bps−1) (full scale = ±1.0; 24-bit divides by
        /// 8,388,608 — the constant go-librespot corrected to in v0.8.0) times the caller's linear gain
        /// (normalization, applied here so the engine's ReplayGain stays unity, as 0.2.9 did). Stereo interleave is
        /// a scalar zip after a vector scale: the scale is the arithmetic, the zip is a store pattern.</summary>
        public static void ToFloat(ReadOnlySpan<int> ch0, ReadOnlySpan<int> ch1, int bps, float gain, Span<float> interleaved)
        {
            float scale = gain / (float)(1L << (bps - 1));
            int n = ch0.Length;
            int i = 0;
            if (n >= 16 && Vector128.IsHardwareAccelerated)
            {
                var k = Vector128.Create(scale);
                ref int a = ref MemoryMarshal.GetReference(ch0);
                ref int b = ref MemoryMarshal.GetReference(ch1);
                for (; i <= n - Vector128<int>.Count; i += Vector128<int>.Count)
                {
                    var l = Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref a, (nuint)i)) * k;
                    var r = Vector128.ConvertToSingle(Vector128.LoadUnsafe(ref b, (nuint)i)) * k;
                    int o = i << 1;
                    interleaved[o] = l[0]; interleaved[o + 1] = r[0];
                    interleaved[o + 2] = l[1]; interleaved[o + 3] = r[1];
                    interleaved[o + 4] = l[2]; interleaved[o + 5] = r[2];
                    interleaved[o + 6] = l[3]; interleaved[o + 7] = r[3];
                }
            }
            for (; i < n; i++) { interleaved[i << 1] = ch0[i] * scale; interleaved[(i << 1) + 1] = ch1[i] * scale; }
        }

        /// <summary>Mono, and 3..8-channel files (local only; Spotify serves stereo): a mono source is duplicated,
        /// a multichannel source is downmixed to L/R by the RFC §9.1.3 channel order (FL FR FC LFE BL BR SL SR):
        /// L = FL + 0.707·FC + 0.707·(BL + SL), R likewise — the same weights the WAV decoder's conform uses in
        /// spirit (it only does mono/stereo) and what libFLAC's <c>flac -d</c> does not do at all (it writes N
        /// channels); we play stereo devices, so a downmix is the honest choice for a file the user dropped.</summary>
        public static void ToFloatMulti(ReadOnlySpan<int> planar, int channels, int block, int bps, float gain, Span<float> interleaved)
        {
            float scale = gain / (float)(1L << (bps - 1));
            const float k = 0.7071f;
            for (int i = 0; i < block; i++)
            {
                float l, r;
                if (channels == 1) { l = r = planar[i] * scale; }
                else
                {
                    l = planar[i] * scale; r = planar[block + i] * scale;
                    if (channels >= 3) { float c = planar[2 * block + i] * scale * k; l += c; r += c; }
                    if (channels >= 5) { l += planar[(channels == 5 ? 3 : 4) * block + i] * scale * k; r += planar[(channels == 5 ? 4 : 5) * block + i] * scale * k; }
                    if (channels >= 7) { l += planar[(channels - 2) * block + i] * scale * k; r += planar[(channels - 1) * block + i] * scale * k; }
                }
                interleaved[i << 1] = l; interleaved[(i << 1) + 1] = r;
            }
        }
    }
}
```

`Vector128.ShiftLeft(Vector128<int>, int)`, `ShiftRightArithmetic`, `ConvertToSingle` and the `&`/`+`/`−`
operators are all in `System.Runtime.Intrinsics` for .NET 8+ and lower to SSE2 / AdvSimd on both shipping
architectures; `IsHardwareAccelerated` is the gate the engine's own gain stage uses (`DspStages.cs:75`). The
scalar tails are the arm64-without-NEON fallback the rule demands, and they are also the whole path on blocks
under 16 samples (xiph subset file 03 is block size 16).

### 3.9 Seeking

Three tiers, in Symphonia's order (`demuxer.rs:249-393`) with libFLAC's estimate instead of a plain midpoint
(`stream_decoder.c`, `seek_to_absolute_sample_`: linear interpolation over the bracket, backed off by one max frame):

1. **Seek table** (a local file that has one): the last point with `Sample ≤ target` gives a byte offset that IS a
   frame start; decode forward from it.
2. **Bracketed search** (a Spotify file — §1.4 says the files are plain libFLAC output; whether Spotify's encoder
   writes a SEEKTABLE is unknown and cannot be assumed; a file with none, or a `TotalSamples` of 0, gets this):
   `lo = FirstFrame, hi = Length`, `loSample = 0, hiSample = TotalSamples`. Probe at
   `pos = lo + (target − loSample) × (hi − lo) / (hiSample − loSample) − MaxFrame`, clamp to `[lo, hi)`, scan for
   the next valid header (`LooksLikeHeader` → `ParseFrameHeader == Ok`, then CRC-16 of that frame to reject a false
   sync inside audio); if `header.SampleNumber ≤ target < SampleNumber + BlockSize` we are done; else narrow the
   bracket to the side the target is on and repeat. Each probe is one `IMediaByteSource.Seek` + a ≤ 128 KiB read
   (`CtrStream.ChunkBytes`); the interpolation lands within a few frames on the first or second probe for constant
   bit-rate-ish audio and is bounded at 32 probes before falling to tier 3.
3. **Linear**: decode forward from the current position discarding blocks until the target's frame.

The decoder's own state across a seek is nothing (Symphonia: `reset` is a no-op, "no state is stored between
packets"); the SHELL discards its byte window and the resampler resets (`LinearResampler.Reset()`), exactly as
`WavAudioDecoder.Seek` does (`AudioDecode.cs:211-224`). Inside the found frame, `skip = target − SampleNumber`
samples are dropped from the first `Read` — a seek is sample-exact, never "the start of the nearest frame".

```csharp
public static partial class Playback
{
    public static partial class Flac
    {
        /// <summary>Find the next plausible frame header at or after <paramref name="from"/> in <paramref name="win"/>:
        /// sync bytes, no reserved codes, CRC-8 valid, STREAMINFO-consistent. Returns −1 when none is in the span.
        /// The CRC-16 check of the WHOLE frame is the caller's — it needs the frame's end, which needs a decode.</summary>
        public static int FindHeader(ReadOnlySpan<byte> win, int from, in StreamInfo si, out FrameHeader h)
        {
            h = default;
            for (int i = from; i + 4 <= win.Length; i++)
            {
                if (win[i] != 0xFF || (win[i + 1] & 0xFC) != 0xF8) continue;             // Symphonia sync_frame's widened 16-bit test
                if (!LooksLikeHeader(win[i..])) continue;
                HeaderResult r = ParseFrameHeader(win[i..], si, out h);
                if (r == HeaderResult.Ok) return i;
                if (r == HeaderResult.Truncated) return -1;                              // need more bytes, not a different offset
            }
            return -1;
        }

        /// <summary>The next probe position for the bracketed search (tier 2). Pure, tested on its own.</summary>
        public static long EstimateOffset(long lo, long loSample, long hi, long hiSample, long target, uint maxFrame)
        {
            if (hiSample <= loSample || hi <= lo) return lo;
            long pos = lo + (long)((double)(target - loSample) / (hiSample - loSample) * (hi - lo));
            pos -= maxFrame > 0 ? maxFrame : 16 * 1024;
            return Math.Clamp(pos, lo, Math.Max(lo, hi - 1));
        }
    }
}
```

### 3.10 The frame decoder, CRC-16, and the MD5 oracle

```csharp
public static partial class Playback
{
    public static partial class Flac
    {
        /// <summary>The decoded block: planar samples, one run of <see cref="BlockSize"/> per channel inside the
        /// decoder's buffer, at the frame's bit depth (NOT up-shifted: the MD5 is over natural-depth samples,
        /// Symphonia validate.rs:36-69 / decoder.rs:231-242).</summary>
        public readonly ref struct Block
        {
            public readonly ReadOnlySpan<int> Planar;
            public readonly int BlockSize;
            public readonly int Channels;
            public readonly int Bps;
            public readonly long SampleNumber;
            public Block(ReadOnlySpan<int> planar, int block, int channels, int bps, long sample)
            { Planar = planar; BlockSize = block; Channels = channels; Bps = bps; SampleNumber = sample; }
            public ReadOnlySpan<int> Channel(int c) => Planar.Slice(c * BlockSize, BlockSize);
        }

        /// <summary>The stateless-across-frames decoder. Owns exactly two buffers, sized once from STREAMINFO:
        /// <c>MaxBlock × Channels</c> ints for the planar samples and 32 ints for LPC coefficients. Reused across
        /// seeks and, by the SHELL, across tracks of the same shape (a second <c>Open</c> with a smaller or equal
        /// MaxBlock × Channels allocates nothing).</summary>
        public sealed class Decoder
        {
            int[] _pcm = [];
            readonly int[] _coefs = new int[32];
            StreamInfo _si;
            int _channelStride;

            public StreamInfo Info => _si;

            /// <summary>Size the buffers for this stream. The one allocation site after construction.</summary>
            public void Open(in StreamInfo si)
            {
                _si = si;
                _channelStride = si.MaxBlock;
                int need = si.MaxBlock * si.Channels;
                if (_pcm.Length < need) _pcm = new int[need];
            }

            /// <summary>Decode the frame whose sync byte is <paramref name="win"/>[0]. On <see cref="FrameResult.Ok"/>,
            /// <paramref name="consumed"/> is the frame's byte length (header .. CRC-16) and <paramref name="block"/>
            /// views the samples. <see cref="FrameResult.Overrun"/> means the window ended inside the frame: refill
            /// and call again with the same start (no state was kept). Any other result: skip one byte and resync.</summary>
            public FrameResult DecodeFrame(ReadOnlySpan<byte> win, out int consumed, out Block block)
            {
                consumed = 0;
                block = default;
                HeaderResult hr = ParseFrameHeader(win, _si, out FrameHeader h);
                if (hr == HeaderResult.Truncated) return FrameResult.Overrun;
                if (hr != HeaderResult.Ok) return FrameResult.Reserved;
                if (h.BlockSize > _si.MaxBlock || h.Channels != _si.Channels) return FrameResult.Reserved;

                var r = new BitReader(win, h.HeaderBytes);
                Span<int> pcm = _pcm.AsSpan(0, h.BlockSize * h.Channels);
                for (int c = 0; c < h.Channels; c++)
                {
                    // §4.2: the side channel carries one extra bit — L/S: channel 1; S/R: channel 0; M/S: channel 1.
                    int bps = h.Bps + ((h.Assignment == 1 && c == 1) || (h.Assignment == 2 && c == 0) || (h.Assignment == 3 && c == 1) ? 1 : 0);
                    FrameResult fr = DecodeSubframe(ref r, bps, pcm.Slice(c * h.BlockSize, h.BlockSize), _coefs);
                    if (fr != FrameResult.Ok) return fr;
                }
                r.AlignToByte();
                if (r.Overrun) return FrameResult.Overrun;
                int end = r.BytePosition;
                if (end + 2 > win.Length) return FrameResult.Overrun;
                ushort crc = (ushort)((win[end] << 8) | win[end + 1]);
                if (Crc16(win[..end]) != crc) return FrameResult.BadCrc16;

                if (h.Assignment != 0) Decorrelate(h.Assignment, pcm[..h.BlockSize], pcm.Slice(h.BlockSize, h.BlockSize));
                consumed = end + 2;
                block = new Block(pcm, h.BlockSize, h.Channels, h.Bps, h.SampleNumber);
                return FrameResult.Ok;
            }
        }

        /// <summary>The correctness oracle (§8.2, Symphonia validate.rs:36-98): every sample of every channel,
        /// interleaved, signed, little-endian, in ceil(bps/8) bytes, hashed with MD5 and compared to STREAMINFO. Used
        /// by the tests and by the <c>--flac-probe</c> CLI arm; never by the pump. <c>IncrementalHash</c> allocates
        /// once per verifier, <c>AppendData(ReadOnlySpan)</c> never.</summary>
        public sealed class Md5Verifier : IDisposable
        {
            readonly System.Security.Cryptography.IncrementalHash _md5 =
                System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
            byte[] _pack = [];

            public void Append(in Block b)
            {
                int bytesPer = (b.Bps + 7) >> 3;
                int need = b.BlockSize * b.Channels * bytesPer;
                if (_pack.Length < need) _pack = new byte[need];
                int o = 0;
                for (int i = 0; i < b.BlockSize; i++)
                    for (int c = 0; c < b.Channels; c++)
                    {
                        int v = b.Planar[c * b.BlockSize + i];
                        for (int k = 0; k < bytesPer; k++) _pack[o++] = (byte)(v >> (8 * k));      // little-endian, low bytes
                    }
                _md5.AppendData(_pack.AsSpan(0, need));
            }

            public bool Matches(in StreamInfo si)
            {
                Span<byte> digest = stackalloc byte[16];
                _md5.GetHashAndReset(digest);
                return digest.SequenceEqual(si.Md5);
            }

            public void Dispose() => _md5.Dispose();
        }
    }
}
```

### 3.11 The allocation plan (P8), stated as a table

| Buffer | Owner | Size | When |
|---|---|---|---|
| `int[] _pcm` planar samples | `Flac.Decoder` | `MaxBlock × Channels` (4096 × 2 = 32 KiB for Spotify; 65535 × 8 = 2 MB for the worst local file) | `Open`; grows only if a later file is larger |
| `int[32] _coefs` | `Flac.Decoder` | 128 B | construction |
| CRC tables | `Flac` (static) | 256 B + 512 B | type init |
| `byte[] _win` byte window | `FlacAudioDecoder` (SHELL, §4) | `max(64 KiB, MaxFrame)` — a subset frame at 24-bit/4608 samples is < 32 KiB | `TryOpen`; grows once if `MaxFrame` says so |
| `float[] _conformed` interleaved floats at source rate | `FlacAudioDecoder` (SHELL) | `MaxBlock × 2` | `TryOpen` |
| `SeekPoint[1024]` | `FlacAudioDecoder` (SHELL) | 24 KiB | `TryOpen`, kept |
| `LinearResampler` | engine | its own two-frame history | `TryOpen`, only when rates differ |
| `BitReader`, `Block`, `FrameHeader`, `StreamInfo` | stack | — | per call |
| MD5 packing buffer | `Md5Verifier` | `MaxBlock × Channels × 4` | tests / probe only |

Nothing else. The allocation gate (§7.3) decodes 200 frames after two warm-up frames and asserts
`GC.GetAllocatedBytesForCurrentThread()` moved by zero — the same shape as `DecodeTests.cs:596-612`.

---

## 4. The pipeline — `Playback/Playback.Audio.cs` (SHELL, owner H, Wave 3)

Plan §4.9 gives `Playback.Audio.cs` "pump over `FluentGpu.Media`, `AudioSource ×5`, gapless/crossfade;
`SilentSink` for `--fake`" at 2,250 lines. FLAC adds **one class** to it — `FlacAudioDecoder`, ≈ 220 lines — and
this section states the shape the five sources and the decoder factory take so the FLAC adapter has something to
plug into. It is written against the engine as it is (§1.2); nothing here needs an engine change.

### 4.1 Bit depth and rate through the graph

```
  source          decoder output              engine decode edge              device
  ──────          ──────────────              ──────────────────              ──────
  FLAC 16/44.1 ─▶ int (±32767)  ─▶ ×1/32768 ─▶ f32 44.1k ─▶ LinearResampler ─▶ f32 48k ─▶ WASAPI shared (float32 mix)
  FLAC 24/44.1 ─▶ int (±8388607) ─▶ ×1/8388608 ─▶ f32 44.1k ─▶ LinearResampler ─▶ f32 48k ─▶ same
  FLAC 24/96   ─▶ int            ─▶ ×1/8388608 ─▶ f32 96k   ─▶ LinearResampler ─▶ f32 48k ─▶ same     (local files)
  FLAC 16/48   ─▶ int            ─▶ ×1/32768   ─▶ f32 48k   ─▶ (resampler elided) ─▶ f32 48k ─▶ same
```

- **24-bit reaches the device as float32**, not as 24-bit PCM. In shared mode the device's mix format IS float32
  on every default Windows endpoint; a device whose mix reports 24-bit PCM gets the engine's per-block
  `ToInt24` conversion (`WasapiFormatNegotiation.cs:99-113`). There is no exclusive path (§1.2; §9 Q3).
- **Rate**: the device rate wins, once, per session — the same rule as Vorbis. 44.1 → 48 kHz is the common
  case on a Windows laptop (the mix format defaults to 48 kHz), and it is linear interpolation. This is the
  engine's existing floor and this plan does not raise it; the honest label for the badge is the SOURCE format
  ("FLAC 24/44.1"), never a claim about the output. A sinc resampler is engine work and is §9 Q2.
- **Gain**: `Opened.GainDb` (from `default_file_normalization_params`, −14 LUFS target, −1 dBTP cap — §1.1) is
  converted to linear once at `TryOpen` and multiplied in `ToFloat`'s scale constant (§3.8) — one multiply per
  sample, folded into the conversion that happens anyway. `Platform.Keys.NormalizationEnabled` (default true)
  gates it; off ⇒ gain 1. The engine's `ReplayGainInfo` stays `default` (unity), as in 0.2.9.
- **Gapless**: `Gapless = new GaplessInfo(0, 0, ToMix(TotalSamples), TailKnown: true)` when STREAMINFO carries a
  total (always, for a libFLAC-encoded file; 0 = unknown ⇒ `GaplessInfo.None` and the engine falls back to the
  declared duration). A FLAC has no encoder delay or padding — `LeadIn = TrailPad = 0` is the truth, not a
  guess. `PcmAudioPlayer.BuildTrimmedVoice` wraps the voice in a `TrimmingSource` so the butt-join ends on the
  last real sample; `VoiceScheduler` joins at `ExactFrames` (§1.2). Two FLACs of the same album therefore join
  sample-exact, which is the one place lossless is audibly better than Vorbis in this pipeline.
- **The level meter / DSP**: nothing FLAC-specific. The tap is `TapBlock` on the master bus after the EQ
  (`PcmAudioPlayer.cs:1450`), demand-gated; the deck faces' VU (ch 23) read `AudioLevelMailbox` through the
  engine's `Levels` signal exactly as 0.2.9 did (`FluentMediaAudioHost.cs:656-660`). A FLAC voice and a Vorbis
  voice are indistinguishable at the tap — float32 at the mix rate.

### 4.2 The five sources and the decoder factory

Plan §4.9 sketches `AudioSource : IDisposable { long Length; int Read(long offset, Span<byte> into); }` — a byte
source keyed by absolute offset. That is `IMediaByteSource` with the position folded into `Seek` + `Read`, so the
plan's five are written AS engine byte sources and the pump hands them to `PcmAudioPlayer` through `PullSource`:

```csharp
public static partial class Playback
{
    public static partial class Audio
    {
        // The routing table IS this switch (plan §4.9, 5.10): no provider registry.
        static IMediaByteSource Open(Track t, CancellationToken ct, out Opened opened) => t.Uri.Provider switch
        {
            EntityProvider.Spotify => SpotifySource(t, ct, out opened),           // Spotify.Audio.Open → CtrStream → StreamByteSource
            EntityProvider.Local => LocalSource(t, out opened),                    // engine FileByteSource; format sniffed from the first bytes
            EntityProvider.Module => Modules.Host.Open(t.Uri, ct, out opened),     // owner T; a Stream → StreamByteSource
            EntityProvider.WaveePodcast => ExternalSource(t, ct, out opened),      // plain https, no key (Spotify.Audio.Open's ExternalUrl arm)
            _ => throw new NotSupportedException(),
        };
        // The fifth "source" is not a byte source: SilentSink (ch 31 GAP 6) is an IAudioEndpoint that consumes
        // nothing while Playback.Host's ticker advances position from the frame clock — selected when Platform.Args.Fake.

        /// <summary>What the pump knows about an opened track beyond its bytes. Format is what the decoder factory
        /// switches on; Label is what the stage badge / deck faces print (ch 21 §10 item 77, ch 23 W18/W19).</summary>
        public readonly record struct Opened(Spotify.Audio.Format Format, long DurationMs, float GainDb, string Label);

        static IAudioDecoder CreateDecoder(MixFormat mix, Spotify.Audio.Format format, float gainDb) => format switch
        {
            Spotify.Audio.Format.Flac or Spotify.Audio.Format.Flac24 => new FlacAudioDecoder(gainDb),
            Spotify.Audio.Format.Mp3 => new Mp3AudioDecoder(gainDb),               // NLayer, ported from _old/…/SampleSource.cs:33-47
            _ => new VorbisAudioDecoder(gainDb),                                   // vendored NVorbis, ported from SampleSource.cs:21-30
        };
        // PcmAudioPlayer is constructed ONCE with decoderFactory: mix => _pending.Decoder — the factory reads the
        // decoder the pump chose for the track it is about to open (the factory's only argument is the mix format).
    }
}
```

A local file's `Format` comes from the first bytes, not the extension — `fLaC` ⇒ Flac (`Bps` from STREAMINFO ⇒
the label), `OggS` ⇒ Vorbis, `ID3`/`0xFFEx` ⇒ Mp3 (0.2.9's `SniffModuleKind`, `FluentMediaAudioHost.ModuleStream.cs:93-114`).
The extension gate stays the user-facing rule (§6).

### 4.3 `FlacAudioDecoder : IAudioDecoder` — the adapter, in full

```csharp
using FluentGpu.Media.Playback;          // IAudioDecoder, IMediaByteSource, MixFormat, DecodedInfo, GaplessInfo, LinearResampler

public static partial class Playback
{
    public static partial class Audio
    {
        /// <summary>The engine-facing FLAC decoder: a byte window over an <see cref="IMediaByteSource"/>, the CORE
        /// <see cref="Flac.Decoder"/> over the window, float conversion + gain, and the engine's resampler. Runs on
        /// the engine's decode-ahead thread; blocks in <see cref="IMediaByteSource.Read"/> and nowhere else.</summary>
        public sealed class FlacAudioDecoder : IAudioDecoder
        {
            const int WindowBytes = 64 * 1024;
            const int SeekPointCapacity = 1024;
            const int MaxProbes = 32;

            readonly Flac.Decoder _dec = new();
            readonly Flac.SeekPoint[] _seek = new Flac.SeekPoint[SeekPointCapacity];
            readonly float _gainLinear;
            IMediaByteSource? _src;
            MixFormat _target;
            Flac.StreamInfo _si;
            int _seekCount;
            long _firstFrame;               // byte offset of the first frame in the SOURCE
            byte[] _win = new byte[WindowBytes];
            long _winStart;                 // source offset of _win[0]
            int _winLen;                    // valid bytes
            int _cursor;                    // next frame's sync byte, relative to _win[0]
            float[] _conformed = [];        // interleaved stereo floats at the SOURCE rate; [0.._hold) unread
            int _hold;
            int _skipSamples;               // samples to drop after a seek (the target was inside the frame)
            long _samplePos;                // next source sample to be emitted
            bool _eof;
            LinearResampler? _resampler;

            public FlacAudioDecoder(float gainDb)
                => _gainLinear = Platform.Settings.Get(Platform.Keys.NormalizationEnabled) ? MathF.Pow(10f, gainDb / 20f) : 1f;

            public GaplessInfo Gapless { get; private set; } = GaplessInfo.None;

            public bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info)
            {
                info = default;
                _src = src;
                _target = target;
                if (!src.TryOpen(new DataSpec { Position = 0, Length = -1 })) return false;
                _winStart = 0; _winLen = 0; _cursor = 0;
                if (!Fill()) return false;

                Flac.Headers h = Flac.ParseHeaders(_win.AsSpan(0, _winLen), _seek);
                while (!h.Valid && !h.Complete && _winLen == _win.Length)           // a huge PICTURE block: grow once, read on
                {
                    Array.Resize(ref _win, _win.Length * 2);
                    if (!Fill()) return false;
                    h = Flac.ParseHeaders(_win.AsSpan(0, _winLen), _seek);
                }
                if (!h.Valid) return false;
                _si = h.Info;
                _seekCount = h.SeekPointCount;
                _firstFrame = h.FirstFrame;
                _cursor = h.FirstFrame;
                if (_si.MaxFrame > _win.Length) Array.Resize(ref _win, (int)Math.Min(_si.MaxFrame * 2, 16 * 1024 * 1024));
                _dec.Open(_si);
                if (_conformed.Length < _si.MaxBlock * 2) _conformed = new float[_si.MaxBlock * 2];
                _resampler = _si.SampleRate != target.SampleRate ? new LinearResampler(_si.SampleRate, target.SampleRate, target.Channels) : null;

                long mixTotal = _si.TotalSamples > 0 ? ToMix(_si.TotalSamples) : -1;
                Gapless = mixTotal >= 0 ? new GaplessInfo(0, 0, mixTotal, TailKnown: true) : GaplessInfo.None;
                var codec = new MediaContentType(Container.Flac, CodecId.None, CodecId.Flac);
                info = new DecodedInfo(codec, new MixFormat(_si.SampleRate, _si.Channels),
                    TimeSpan.FromMilliseconds(_si.DurationMs), default);
                return true;
            }

            long ToMix(long srcFrames) => (long)Math.Round((double)srcFrames * _target.SampleRate / _si.SampleRate);
            long ToSrc(long mixFrames) => (long)Math.Round((double)mixFrames * _si.SampleRate / _target.SampleRate);

            /// <summary>Top up the window from the source. Keeps the unread tail, reads until full or EOF/error.</summary>
            bool Fill()
            {
                if (_cursor > 0 && _cursor < _winLen) { _win.AsSpan(_cursor, _winLen - _cursor).CopyTo(_win); _winStart += _cursor; _winLen -= _cursor; _cursor = 0; }
                else if (_cursor >= _winLen) { _winStart += _winLen; _winLen = 0; _cursor = 0; }
                while (_winLen < _win.Length)
                {
                    int n = _src!.Read(_win.AsSpan(_winLen));
                    if (n < 0) return false;
                    if (n == 0) break;
                    _winLen += n;
                }
                return _winLen > 0;
            }

            /// <summary>Decode the next frame into <see cref="_conformed"/>. False at EOF or on an unrecoverable
            /// error. A corrupt frame is skipped by one byte and the sync scan resumes — 0.2.9 latched the first
            /// zero read as EOF (PrefetchingReadStream.cs:16-25); here a bad frame costs one resync, never the track.</summary>
            bool NextBlock()
            {
                while (true)
                {
                    if (_cursor + 16 > _winLen && !Fill()) return false;
                    ReadOnlySpan<byte> win = _win.AsSpan(_cursor, _winLen - _cursor);
                    int at = Flac.FindHeader(win, 0, _si, out _);
                    if (at < 0) { if (_winLen == _win.Length) _cursor = _winLen; else return false; continue; }
                    _cursor += at;
                    win = _win.AsSpan(_cursor, _winLen - _cursor);
                    Flac.FrameResult fr = _dec.DecodeFrame(win, out int consumed, out Flac.Block block);
                    if (fr == Flac.FrameResult.Overrun)
                    {
                        if (_winLen - _cursor >= _win.Length) { _cursor++; continue; }       // a "frame" longer than the window is a false sync
                        if (!Fill()) return false;
                        continue;
                    }
                    if (fr != Flac.FrameResult.Ok) { _cursor++; continue; }                   // bad CRC / reserved: resync one byte on
                    _cursor += consumed;
                    _samplePos = block.SampleNumber;
                    if (block.Channels == 2) Flac.ToFloat(block.Channel(0), block.Channel(1), block.Bps, _gainLinear, _conformed);
                    else Flac.ToFloatMulti(block.Planar, block.Channels, block.BlockSize, block.Bps, _gainLinear, _conformed);
                    _hold = block.BlockSize;
                    if (_skipSamples > 0)
                    {
                        int drop = Math.Min(_skipSamples, _hold);
                        _conformed.AsSpan(drop * 2, (_hold - drop) * 2).CopyTo(_conformed);
                        _hold -= drop; _skipSamples -= drop; _samplePos += drop;
                        if (_hold == 0) continue;
                    }
                    return true;
                }
            }

            public int Read(Span<float> dst)
            {
                if (_src is null || _eof) return 0;
                int ch = _target.Channels;                                              // 2 for every session the app opens
                int want = dst.Length / ch;
                if (want <= 0) return 0;

                if (_hold == 0 && !NextBlock()) { _eof = true; return 0; }

                if (_resampler is { IsActive: true } rs)
                {
                    ResampleResult rr = rs.Process(_conformed.AsSpan(0, _hold * ch), _hold, dst);
                    int unread = _hold - rr.Consumed;
                    if (unread > 0 && rr.Consumed > 0) _conformed.AsSpan(rr.Consumed * ch, unread * ch).CopyTo(_conformed);
                    _hold = unread;
                    _samplePos += rr.Consumed;
                    return rr.Produced;
                }

                int frames = Math.Min(want, _hold);
                _conformed.AsSpan(0, frames * ch).CopyTo(dst);
                int rest = _hold - frames;
                if (rest > 0) _conformed.AsSpan(frames * ch, rest * ch).CopyTo(_conformed);
                _hold = rest;
                _samplePos += frames;
                return frames;
            }

            /// <summary>Seek to a MIX-domain frame (the engine's contract). Returns the frame actually reached in
            /// the mix domain, or −1. Sample-exact: the remainder inside the found frame is dropped on the next
            /// <see cref="Read"/>.</summary>
            public long Seek(long frame)
            {
                if (_src is null) return -1;
                long target = Math.Clamp(ToSrc(frame), 0, _si.TotalSamples > 0 ? _si.TotalSamples : long.MaxValue);
                _hold = 0; _eof = false; _skipSamples = 0;
                _resampler?.Reset();

                long pos = _firstFrame, posSample = 0;
                for (int i = _seekCount - 1; i >= 0; i--)                                  // tier 1
                    if (_seek[i].Sample <= target) { pos = _firstFrame + _seek[i].Offset; posSample = _seek[i].Sample; break; }

                if (_seekCount == 0 && _si.TotalSamples > 0 && _src.Caps.Seekable && _src.Length is long len)   // tier 2
                {
                    long lo = _firstFrame, hi = len, loS = 0, hiS = _si.TotalSamples;
                    for (int probe = 0; probe < MaxProbes && hi - lo > 2 * (long)Math.Max(_si.MaxFrame, 16 * 1024); probe++)
                    {
                        long guess = Flac.EstimateOffset(lo, loS, hi, hiS, target, _si.MaxFrame);
                        if (!Probe(guess, out long at, out Flac.FrameHeader h)) break;
                        if (h.SampleNumber <= target && target < h.SampleNumber + h.BlockSize) { pos = at; posSample = h.SampleNumber; break; }
                        if (h.SampleNumber > target) { hi = at; hiS = h.SampleNumber; }
                        else { lo = at + 1; loS = h.SampleNumber + h.BlockSize; }
                        pos = h.SampleNumber <= target ? at : pos;
                        posSample = h.SampleNumber <= target ? h.SampleNumber : posSample;
                    }
                }

                // tier 3 (and the tail of tiers 1-2): land on `pos`, decode forward, drop the remainder inside the
                // frame that holds the target. NextBlock has already converted the frame into _conformed, so the
                // drop is a span shift here; _skipSamples is only for the case where the seek landed exactly on it.
                _src.Seek(pos);
                _winStart = pos; _winLen = 0; _cursor = 0;
                _samplePos = posSample;
                while (true)
                {
                    if (!NextBlock()) { _eof = true; return -1; }
                    if (_samplePos + _hold > target)
                    {
                        int drop = (int)(target - _samplePos);
                        if (drop > 0) { _conformed.AsSpan(drop * 2, (_hold - drop) * 2).CopyTo(_conformed); _hold -= drop; _samplePos += drop; }
                        break;
                    }
                    _hold = 0;                                                             // a whole frame before the target: discard it
                }
                return ToMix(target);
            }

            /// <summary>One probe of the bracketed search: seek the source, fill, find the first valid header and
            /// decode that one frame (the CRC-16 is the false-sync rejector). Answers the frame's source offset.</summary>
            bool Probe(long offset, out long at, out Flac.FrameHeader h)
            {
                at = -1; h = default;
                _src!.Seek(offset);
                _winStart = offset; _winLen = 0; _cursor = 0;
                if (!Fill()) return false;
                int from = 0;
                while (true)
                {
                    int found = Flac.FindHeader(_win.AsSpan(0, _winLen), from, _si, out h);
                    if (found < 0) return false;
                    Flac.FrameResult fr = _dec.DecodeFrame(_win.AsSpan(found, _winLen - found), out _, out _);
                    if (fr == Flac.FrameResult.Ok) { at = _winStart + found; _cursor = found; return true; }
                    if (fr == Flac.FrameResult.Overrun) { if (_winLen < _win.Length && !Fill()) return false; from = found; continue; }
                    from = found + 1;
                }
            }
        }
    }
}
```

Two notes on that adapter. **The window IS the read-ahead.** `IMediaByteSource.Read` blocks on the CDN when the
`CtrStream` chunk is not resident, on the decode-ahead thread, behind a 1 s ring — the same firewall 0.2.9 built
`PrefetchingReadStream` for. Owner H ports that class's never-return-zero invariant into the Spotify byte source
(`Read` returns 0 only at true EOF); the adapter above trusts it. **A seek on a Spotify FLAC is 2-4 range requests**
in practice (interpolation lands within a frame or two of the target on a ~700-1,400 kbit/s file), each a 128 KiB
chunk the `CtrStream` already fetches; the bound is 32 probes. 0.2.9's answer was a silent no-op.

### 4.4 What the badge and the deck faces print

`Opened.Label` is built once per open from `Format` + STREAMINFO: `"FLAC"`, `"FLAC 24-bit"`, and for a local file
`"FLAC 16/44.1"` / `"FLAC 24/96"` (ch 04 W15 shows "FLAC 16/44"; ch 21 §10 item 77 wants the quality badge centred
between the times and gone on a foreign device; ch 23 W18/W19 print "FLAC · 320" / "320 kbps  FLAC  STEREO" from
`StreamFormat` / `StreamBitrateKbps`). The bitrate for a FLAC is `average_bitrate` from `ExtendedAudioFile`
(kind 5, `audio_files_extension.proto:17-22`) when the catalogue carried it, else the file's own
`Length × 8 / DurationMs`. `Playback.Host` publishes it on the existing `StreamFormat` signal; nothing new in the UI.

---

## 5. The catalogue side — "is this track available lossless", and where the user chooses

### 5.1 What answers the question

Extension kind **5 `AUDIO_FILES`** on the **`spotify:audio:<base62(original_audio.uuid)>`** entity (§1.1, §1.4).
Not `TRACK_V4` (no FLAC rows), not the session (no capability flag), not the account tier. The answer is per
track and per account: a Premium account in a launched market gets `FLAC_FLAC` and/or `FLAC_FLAC_24BIT` rows plus
`average_bitrate` per row and `default_file_normalization_params`; an account without lossless gets the same
Ogg/AAC ladder `TRACK_V4` already gave. So "lossless available" is a catalogue fact that costs one extra
extended-metadata POST per track — batched 300 per POST like every other kind (P4) — and it is exactly the payload
the drawer's format ladder shows (ch 01 DATA GAP 1 and 14: "`EdgeTable<FormatEdge> TrackFormats` (payload
`{int FormatId, int AvgBitrate, byte AvailableOnDevice}`)").

### 5.2 What exists today and what is missing (`Entities/Track.cs`, `Entities/Edges.cs`)

| Needed | Today | Gap |
|---|---|---|
| The audio uuid to derive `spotify:audio:` from | `TrackV4` decode stages Identity + cold groups; `original_audio` (field 24) is dropped | **add** `Column<UInt128> OriginalAudio` (cold; 16 B/row; zero = none) staged by `Decode.TrackV4` under a new `TrackFields.Files` known bit |
| The per-track ladder | nothing — `Edges` has no formats edge (`grep TrackFormats` is empty); ch 01 GAP 1 asks for it | **add** `EdgeTable<FormatEdge> TrackFormats` (parent = track slot; "the row is the payload", like `TrackTags`) |
| A cheap "has lossless" bit for a row / the versions row's badge | none | **add** `TrackFlags.Lossless = 1 << 24` and `Lossless24 = 1 << 25` in a new `FilesMask`, set at commit from the edge payload — one load + mask for any surface that wants a badge, no edge walk |
| The field group the planner asks for | `TrackFields.All = … | Publishing` | **add** `Files = 1 << 17` (kind 5 on the audio entity), NOT in `Row` (a list never demands it — ch 01 §7: the drawer fetches on expand, and a play asks `Spotify.Audio.Open`, which fetches it itself) |
| The user's per-track override (ch 01 GAP 14) | none | `Platform.Keys.FormatOverrides` (a settings string `uri=formatId;…`, like GAP 11's) — **session-scoped in 0.2.9**, kept session-scoped: a `Dictionary<int, byte>` on `Playback.Host`, no key |

```csharp
// Entities/Track.cs — the additions (owner A's file; a request to A, three lines each)
[Flags] public enum TrackFields : uint { …, Publishing = 1 << 16,
    /// <summary>Extension kind 5 on the derived `spotify:audio:` entity: the format ladder (`Edges.TrackFormats`)
    /// and the lossless bits. Needs <see cref="TrackTable.OriginalAudio"/> first — the planner asks for Files only
    /// on rows whose Identity is known and whose OriginalAudio is non-zero.</summary>
    Files = 1 << 17,
    All = Identity | PlayCount | Year | Availability | Isrc | Canonical | Audio | Tags | Video | Publishing | Files }

[Flags] public enum TrackFlags : uint { …,
    // Files group
    /// <summary>The catalogue offered a 16-bit FLAC for this account.</summary>
    Lossless = 1 << 24,
    /// <summary>…and a 24-bit one.</summary>
    Lossless24 = 1 << 25,
    FilesMask = Lossless | Lossless24 }

public sealed class TrackTable : Table { …
    /// <summary>`Track.original_audio.uuid` (field 24), packed; 0 = the wire gave none (a local, a podcast, a very
    /// old row). The key to the `spotify:audio:` entity and therefore to <see cref="TrackFields.Files"/>.</summary>
    public Column<UInt128> OriginalAudio; }

// Entities/Edges.cs — the payload and the table (owner B's file; a request to B)
/// <summary>One rung of a track's format ladder (kind 5). `FormatId` is the wire enum (16 = FLAC, 22 = FLAC 24-bit,
/// 0-2 Ogg, 8-9 AAC…); `Kbps` = average_bitrate / 1000 (0 = unknown, sorts last — 0.2.9's rule).</summary>
public readonly record struct FormatEdge(byte FormatId, ushort Kbps);
public sealed partial class Edges { public readonly EdgeTable<FormatEdge> TrackFormats = new(); }
```

```csharp
// Spotify/Spotify.Decode.cs — the fold (owner E's file; a request to E, ≈ 60 lines)
public static partial class Spotify { public static partial class Decode {
    /// <summary>AUDIO_FILES (kind 5): the ladder as a FormatEdge run on the track row, and the two lossless bits.
    /// The envelope's entity_uri is the AUDIO uri, so the caller passes the track's StagedId it asked for.</summary>
    public static void AudioFiles(ReadOnlySpan<byte> proto, in StagedId track, Staging s)
    {
        ref StagedTrack row = ref s.Tracks.RowFor(in track, Authority.Full, (uint)TrackFields.Files);
        EdgeRun run = s.Edges.Begin(EdgeKind.TrackFormats, in track);
        var r = new ProtoReader(proto);
        while (r.Next(out int field))
        {
            if (field != 1) { r.Skip(); continue; }                         // files = 1; normalization params are the ladder's, read at open (F)
            ProtoReader ext = r.Sub();                                        // ExtendedAudioFile
            byte formatId = 0; ushort kbps = 0; bool hasFile = false;
            while (ext.Next(out int f))
            {
                if (f == 1) { ProtoReader file = ext.Sub(); while (file.Next(out int g)) { if (g == 2) formatId = (byte)file.Varint(); else if (g == 1) hasFile = file.Bytes().Length == 20; else file.Skip(); } }
                else if (f == 4) kbps = (ushort)Math.Min(ext.Varint() / 1000, ushort.MaxValue);
                else ext.Skip();
            }
            if (!hasFile) continue;
            run.Add(new FormatEdge(formatId, kbps));
            if (formatId == 16) row.Flags |= (uint)TrackFlags.Lossless;
            else if (formatId == 22) row.Flags |= (uint)TrackFlags.Lossless24;
        }
        run.End();
    }
} }
```

The commit (owner A's `Entities.Commit` for tracks, `Track.cs:352-475`) gains one arm: `Files` known ⇒ write
`FilesMask` bits and `ReplaceRun` the `TrackFormats` edge. The planner (owner C's `Fetch.cs`) gains one route:
`wanted & Files` ⇒ derive the audio uri from `OriginalAudio` (the base62 encode `Spotify.Audio.LosslessMetadata`
already does at `:334-337`, moved to `Entities.Base62`'s side as `EntityId.WriteAudioUri`) and batch kind 5 on it.
Rows with `OriginalAudio == 0` are marked `Files`-known-empty at commit so they are never re-asked (P3).

**Who demands `Files`.** The drawer, on expand (ch 01 GAP 1: "the drawer only mounts on an expand"); the Album and
Playlist pages do **not** put it in their model demand — a 300-row playlist would be 300 audio-uri POSTs for a bit
nobody paints in a row. If Christos wants a lossless badge in the row (§9 Q4), `Files` moves into `Row` for the
album page only (an album's tracks are one batch anyway).

### 5.3 The drawer's format ladder (`Entities/Track.Drawer.cs`, owner M, Wave 5) and the labels

Ch 01 W14 is the contract: radio items "`<label>   <n> kbps`", enabled = available on this device, "Use my default
quality" resets. In 0.3 the ladder reads `Edges.TrackFormats.Payload(slot)` (a `ReadOnlySpan<FormatEdge>`, zero
alloc) sorted by `Kbps` descending, labels from one pure table in `Track.cs`'s rule section (owner M):

| `FormatId` | label | kbps shown |
|---|---|---|
| 0 / 1 / 2 | OGG Vorbis 96 / 160 / 320 | 96 / 160 / 320 |
| 3 / 4 / 5 / 6 | MP3 256 / 320 / 160 / 96 | as named |
| 8 / 9 | AAC 24 / 48 | 24 / 48 |
| 16 | **FLAC** | `Kbps` (≈ 700) |
| 22 | **FLAC 24-bit** | `Kbps` (≈ 1,400) — the case 0.2.9's `FormatLabel` lacked (§1.3) |
| other | "Format {n}" | `Kbps` or "—" — never hidden (0.2.9's rule: hiding would silently shrink the ladder) |

Enabled = `Rung(FormatOf(id)) >= 0` (the decoder stack can open it — AAC rows render disabled, exactly the
`AvailableOnDevice` bit 0.2.9 computed), and a FLAC row is additionally disabled while `Platform.Network.Metered`
and the metered cap is below Lossless, with the reason caption "Not on a metered connection" (ch 29 §8's
`NetworkPolicy.EffectiveQuality` is the single source; §5.5). Selecting a row sets the session override; the next
`Playback.Open` of that uri passes `Quality.Lossless` (or the rung the id maps to) to `Spotify.Audio.Open(uri,
quality, …)` — the overload exists (`Spotify.Audio.cs:724`).

The versions row's own label ("This track · 6:09 … FLAC 16/44", ch 04 W15) prints the highest rung the ladder
holds for the self row: `Lossless24` ⇒ "FLAC 24/44.1", `Lossless` ⇒ "FLAC 16/44.1", else the Ogg top. The rate
is not on the wire; 44.1 is what Spotify serves (§1.4) and it is printed as a constant with that citation in the
code comment.

### 5.4 Settings: the fourth rung (`Screens/Settings.cs` + `+Settings.UI.Playback.cs`, owner R, Wave 6)

Ch 27 §2 W7 records the 0.2.9 decision: three rungs, "re-offering it later is one entry per array". This is that
entry. Both audio-quality combos (streaming, and "On metered connections") gain a fourth item:

```
 ┌ Streaming quality                          ┌ Very high            ▾ ┐280 ┐
 │                                              Normal      Equivalent to about 96 kbps
 │                                              High        Equivalent to about 160 kbps
 │                                              Very high   Equivalent to about 320 kbps
 │                                              Lossless    FLAC, up to 24-bit/44.1 kHz. About 1 GB an hour;
 │                                                          only where your subscription includes it.
 ┌ 📡 On metered connections  Not metered     ┌ High                 ▾ ┐280 ┐
 │                                              (the same four items, same descriptions)
```

- Stored as the existing int (`playback.quality` 0..3, `playback.quality.meteredCap` 0..3); the `i < 0 || i > 2`
  guards ch 27 cites (`:330, :366`) become `> 3`. Defaults unchanged: 2 and 1 — Spotify's own default is off
  (§1.4) and Wavee's stays VeryHigh320.
- No entitlement gate on the row (§5.1: entitlement is not knowable up front). The description says "only where
  your subscription includes it", and the ladder's fallback (`PickRung`, `Spotify.Audio.cs:169-183`) already plays
  the top Ogg rung when the account gets no FLAC — the user hears no failure, only 320.
- `SettingsCatalog` (ch 27 §9.1) gains no row: the rung is an item inside an existing row, so the per-section
  glyph invariant is untouched. Parity items 33a/33b in ch 27 §10 are restated: "FOUR items — Normal / High /
  Very High / Lossless"; the audit log records the divergence with this plan as the reason and the issue number
  (§8: one GitHub issue for the whole feature, `(#n)` on the CHANGELOG bullet).

### 5.5 The bandwidth rule — what the chapters say, and the cite

Ch 29 §8 rule 12 (`29-cross-cutting.md:167-170`): "**A metered link changes two visible things and nothing else.**
The streaming quality cap (`min(userQuality, meteredCap)`, `NetworkPolicy.cs:95-105`) and the protected-video
height cap …; prefetch is deferred silently. Unknown cost is **unmetered-conservative** — a failed probe never
throttles playback." And its §2 D block (`:951-967`): "THE 0.3 DECISION: keep it invisible … the CORE split must
take the cost as a PARAMETER (`EffectiveQuality(user, cap, in NetworkCost)`)". Ch 27 §2 W7 binds the metered
combo "straight to the live `NetworkPolicy.MeteredQualityCap` signal". Ch 14 has no audio-quality rule (its one
metered line is the update toast's `Actions = Metered ? [Retry] : …`, `14-os-surfaces.md:735`).

So the rule for lossless is the existing rule with one more rung: **on a metered link the effective quality is
`min(user, meteredCap)`, the cap defaults to High, and lossless therefore never plays on a metered link unless the
user sets the metered cap to Lossless** — the opt-in ch 27's combo already is. `Platform.NetworkPolicy.EffectiveQuality`
(owner S, Wave 6, CORE, ch 29 §9.10) clamps `0..3` instead of `0..2`; `Spotify.Audio.PreferredQuality()` (`:717-721`)
reads through it instead of the raw key — the one-line request to F in §8. No new setting, no new chip, no toast:
the Settings ▸ Playback status line ("Not metered" / "Metered · …") is the only visible consequence, as ch 29
decided. Wi-Fi vs cellular is not a distinction Windows makes reliably (`NetworkCostType` is the OS's word), so the
plan does not split the rung by transport the way the mobile app does (§9 Q1).

---

## 6. Local FLAC files (`Platform/Modules.cs` `PlayableLinks`, ch 15 §2, ch 09 §6)

Ch 15 `:914-918`: a dropped `.mp3 / .ogg / .flac` builds a **complete** synthetic track (`TrackFlags.Local`,
title = the file name without its extension, "duration from a fail-soft header probe") and plays it; anything
else is the "That file can't be played. Wavee plays .mp3, .ogg, .flac and .mp4 files." toast (`localFile.rejected`,
ch 09 `:1144`). Nothing in either chapter asks for tags — 0.2.9 explicitly chose the file name (§1.3). This plan
keeps that as the **row's title** and adds the minimum that costs nothing once the header is parsed anyway:

| Need | Where it comes from | Cost |
|---|---|---|
| Duration | `Flac.ParseHeaders(first 64 KiB).Info.DurationMs` — the same function the decoder runs at open; 0 when `TotalSamples` is 0 (a live capture) and the row shows "–:–" until the decoder reaches EOF | one 64 KiB read |
| Title / artist / album for the row | `Tags.Title / Artist / Album` byte ranges → interned on the UI thread at commit (`Staging` text arena, P14). **Only when the user turned it on**: `Platform.Keys.LocalFileTags` (bool, default **false**) keeps 0.2.9's "the file name is honest" rule as the default and lets a tagged library opt in (§9 Q5) | none beyond the read |
| The cover | `Tags.PictureData` — a range into the file; the row's `Image` is `wavee:local:art:<b64url(path)>` and `Platform.Host` serves it by re-reading that range on the image thread (the engine's image decoder takes bytes; the range is at most one PICTURE block, capped at 16 MB by the metadata length field) | one ranged file read on demand |
| The format label | `Info.Bps` / `Info.SampleRate` → "FLAC 24/96" | — |

```csharp
// Platform/Modules.cs (CORE, owner T) — PlayableLinks gains the probe's PURE half; the file read is Modules.Host's
public static partial class Modules
{
    public static partial class PlayableLinks
    {
        public static bool IsSupportedAudioFile(ReadOnlySpan<char> path)
            => path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);

        /// <summary>What the first bytes of a local file say. `Flac` fills duration and tags from the header; the
        /// other two are answered by their own decoders' header reads (NVorbis `TotalTime`, NLayer `Duration`),
        /// ported from `_old/…/LocalAudioDurationProbe.cs` and unchanged by this plan.</summary>
        public readonly record struct LocalProbe(Spotify.Audio.Format Format, long DurationMs, int SampleRate, byte Bps, Playback.Flac.Tags Tags);

        public static LocalProbe ProbeFlac(ReadOnlySpan<byte> head)
        {
            Span<Playback.Flac.SeekPoint> none = default;
            Playback.Flac.Headers h = Playback.Flac.ParseHeaders(head, none);
            if (!h.Valid) return default;
            var format = h.Info.Bps > 16 ? Spotify.Audio.Format.Flac24 : Spotify.Audio.Format.Flac;
            return new LocalProbe(format, h.Info.DurationMs, h.Info.SampleRate, h.Info.Bps, h.Tags);
        }
    }
}
```

The decoder for a local FLAC is the same `FlacAudioDecoder` over the engine's `FileByteSource` (§4.2); the file's
own seek table (libFLAC writes one by default every 10 s) makes tier 1 the common path. Files up to 8 channels and
32-bit decode; >2 channels are downmixed (§3.8 `ToFloatMulti`), which is the honest behaviour for a stereo device
and the one thing 0.2.9's FlacBox path would have got wrong (it interleaved N channels into a stereo mix).
Non-subset files (block 65535, 768 kHz — xiph `uncommon/`) decode too; nothing in the decoder assumes the subset,
only the buffer sizes read from STREAMINFO.

---

## 7. Tests and fixtures (`src/apps/Wavee.Tests/FlacTests.cs`, `Fixtures/flac/`)

### 7.1 Which xiph vectors to vendor

`https://github.com/ietf-wg-cellar/flac-test-files`, **CC0-1.0** (vendoring needs no notice, but `Fixtures/flac/
README.md` names the source and the commit). `Wavee.Tests.csproj:29` already copies `Fixtures\**\*` to the output.
The pick is the smallest file that exercises each decoder path; total ≈ **5.2 MB**:

| File (subset/) | Bytes | What it pins |
|---|--:|---|
| `01 - blocksize 4096.flac` | 549,954 | the Spotify shape: 16-bit, 44.1 k, stereo, fixed block 4096, libFLAC — the baseline for every other fact |
| `03 - blocksize 16.flac` | 959,681 | the minimum block; the `Vector128` paths are **skipped** (n < 16 is the scalar tail) — the `n >= 16` gate in §3.8 |
| `07 - blocksize 725.flac` | 588,018 | an odd block size: partitions that are not powers of two are rejected (`BadResidual`), and this file proves a legal odd size decodes |
| `11 - partition order 8.flac` | 510,583 | the subset's maximum Rice partition order |
| `12 - qlp precision 15 bit.flac` | 492,334 | the maximum coefficient precision → the `long` LPC path (`sumBits > 32`) |
| `13 - qlp precision 2 bit.flac` | 530,759 | the minimum precision → the `int` LPC path |
| `14 - wasted bits.flac` | 231,596 | `ShiftLeft` after the subframe |
| `15 - only verbatim subframes.flac` | 889,862 | VERBATIM |
| `16 - partition order 8 containing escaped partitions.flac` | 471,502 | the Rice escape |
| `17 - all fixed orders.flac` | 589,534 | FIXED 0..4 |
| `22 - 12 bit per sample.flac` | 277,942 | 12-bit: bps code 2, MD5 packing rounds up to 2 bytes |
| `23 - 8 bit per sample.flac` | 181,470 | 8-bit: 1-byte MD5 packing, bps code 1 |
| `24 - variable blocksize file created with flake revision 264.flac` | 2,321,064 | variable block size: the sample number is in the header, `MinBlock ≠ MaxBlock` — **the one large file, kept because nothing smaller exercises variable blocking** |
| `28 - high resolution audio, default settings.flac` | 3,476,937 | **24-bit / 96 kHz** — the Spotify 24-bit path's bps and the resampler's decimation case. Kept for 24-bit; if 8.7 MB total is too much, `61-63 predictor overflow check 16/20/24-bit` (117,020 / 156,871 / 204,107) cover 24-bit at a tenth the size and are ADDED regardless for the overflow property |
| `37 - 20 bit per sample.flac` | 2,385,998 | bps code 5 (20-bit): 3-byte MD5 packing with sign extension |
| `45 - no total number of samples set.flac` | 562,343 | `TotalSamples = 0` ⇒ `GaplessInfo.None`, duration unknown, seek tier 2 disabled |
| `46 - no min-max framesize set.flac` | 424,137 | `MaxFrame = 0` ⇒ the window never grows from STREAMINFO, `EstimateOffset`'s 16 KiB fallback |
| `47 - only STREAMINFO.flac` | 333,761 | a file with no seek table ⇒ tier 2 (the Spotify shape for seeking) |
| `60 - mono audio.flac` | 47,782 | 1 channel → duplicated to L/R |
| `61 / 62 / 63 - predictor overflow check, 16/20/24-bit` | 478,000 | the int-vs-long LPC accumulator decision at exactly the edge |
| `64 - rice partitions with escape code zero.flac` | 89,138 | escape width 0 ⇒ zeros (the `part.Clear()` arm) |
| `uncommon/10 - file starting at frame header.flac` | 466,375 | no `fLaC`, no STREAMINFO: `ParseHeaders` invalid, `FindHeader` still syncs — the "local file that is really a raw frame dump" case is rejected honestly with the toast, not crashed on |
| `faulty/wrong max blocksize.flac`, `faulty/blocksize 65536.flac` | 253,087 | a frame larger than STREAMINFO says ⇒ `Reserved`, one-byte resync, no buffer overrun (the fuzz-adjacent fact) |

Not vendored: the 8-channel (42 MB), 384 kHz (10 MB), the 16-17 MB "extremely large" block files, and 55 (87 MB).
Multichannel downmix (§3.8) is pinned by `38 - 3.0 channels` (63,893) and `41 - 5.1 channels` (171,527) — small,
added. The picture/tag parse is pinned by `56 - JPG PICTURE` (708,441) and `51 - extremely large VORBISCOMMENT`
(17 MB — **not** vendored; a synthetic 200-comment block written by the test covers the loop).

### 7.2 The MD5 oracle — the one test that proves the decoder

```csharp
// Wavee.Tests/FlacTests.cs — every vectored file, one Theory
[Theory]
[MemberData(nameof(SubsetFiles))]
public void Decodes_bit_exact_against_the_streaminfo_md5(string file)
{
    byte[] bytes = File.ReadAllBytes(Path.Combine("Fixtures", "flac", file));
    Playback.Flac.Headers h = Playback.Flac.ParseHeaders(bytes, stackalloc Playback.Flac.SeekPoint[64]);
    Assert.True(h.Valid);
    var dec = new Playback.Flac.Decoder(); dec.Open(h.Info);
    using var md5 = new Playback.Flac.Md5Verifier();
    int pos = h.FirstFrame; long samples = 0;
    while (pos < bytes.Length)
    {
        int at = Playback.Flac.FindHeader(bytes, pos, h.Info, out _);
        if (at < 0) break;
        var fr = dec.DecodeFrame(bytes.AsSpan(at), out int consumed, out Playback.Flac.Block block);
        Assert.Equal(Playback.Flac.FrameResult.Ok, fr);
        Assert.Equal(samples, block.SampleNumber);
        md5.Append(in block);
        samples += block.BlockSize; pos = at + consumed;
    }
    if (h.Info.TotalSamples > 0) Assert.Equal(h.Info.TotalSamples, samples);
    if (h.Info.HasMd5) Assert.True(md5.Matches(in h.Info), file + ": MD5 mismatch");
}
```

The MD5 is over natural-depth samples, so a wrong stereo decorrelation, a wrong wasted-bits shift, an off-by-one
in a fixed predictor or a Rice escape all fail this one fact — Symphonia (`validate.rs`) and libFLAC (`flac -t`)
use the same oracle. `Assert.Equal(samples, block.SampleNumber)` is the coded-number and fixed-block-multiply
check for free.

### 7.3 The other facts

| Test | What it pins |
|---|---|
| `Decoding_two_hundred_frames_allocates_nothing` | two warm-up frames (the `Open` and the first `DecodeFrame`), then `GC.GetAllocatedBytesForCurrentThread()` around 200 frames of `01`, delta 0 — the P8 gate, the shape of `DecodeTests.cs:596-612` |
| `Crc8_and_crc16_match_the_rfc_check_values` | `Crc8("123456789"u8) == 0xF4` (poly 0x07, init 0, no reflection: the catalogue's plain CRC-8 check value), `Crc16("123456789"u8) == 0xFEE8` (CRC-16/UMTS = poly 0x8005, init 0, no reflect — https://reveng.sourceforge.io/crc-catalogue/16.htm#crc.cat.crc-16-umts); and: flipping one byte inside a frame of `01` yields `BadCrc16`; flipping a byte inside the header yields `BadCrc`/`Reserved` — never `Ok` |
| `Frame_header_tables_match_rfc_9639` | a Theory over the 16 block-size codes, 16 rate codes, 11 channel codes, 8 bps codes with hand-built 4-byte headers + CRC-8: expected values from §9.1.1-9.1.4; the reserved codes answer `Reserved` |
| `Coded_number_reads_seven_byte_values` | 36-bit sample numbers round-trip; a bad continuation byte is `false` |
| `Seek_lands_on_the_exact_sample` | for `47 - only STREAMINFO` (no seek table) and `01` (with): `FlacAudioDecoder` over `BytesByteSource`, `Seek(mix frame)` then `Read`, compare the first 64 floats to a linear decode's floats at the same position; also `Seek` to a sample inside the last frame, and past the end (→ EOF, no exception) |
| `Seek_probe_count_is_bounded` | a counting `IMediaByteSource` over `01`: a seek to 70 % takes ≤ 4 probes (interpolation) and never more than 32 |
| `Estimate_offset_is_monotonic_and_clamped` | the pure `EstimateOffset` |
| `Rice_escape_zero_yields_silence` | `64` decodes and matches MD5 (a decoder that reads 0-width samples as garbage fails here) |
| `Wasted_bits_shift_left` | `14` matches MD5 |
| `Mid_side_odd_side_samples_round_correctly` | a synthetic 32-sample block through `Decorrelate(3, …)` with odd side values, both the vector and the scalar path (the scalar path is forced by a 15-sample slice) |
| `ToFloat_scales_full_scale_to_unity` | `1 << (bps−1) − 1` → 0.99997 for 16 and 24; `−(1 << (bps−1))` → −1.0 |
| `Headers_parse_tags_and_picture` | `56`: `Tags.PictureMime` is `image/jpeg`, the range starts with `FF D8`; a synthetic VORBIS_COMMENT with `title=` / `TITLE=` / `Album Artist=` |
| `Truncated_headers_report_incomplete_not_invalid` | `ParseHeaders(bytes[..40])` → `Complete = false, Valid = false`; the caller's grow-and-retry loop in `TryOpen` is pinned with a 3-block synthetic file fed 16 bytes at a time |
| `Faulty_files_resync_without_overrun` | the two `faulty/` files: every frame result is `Ok` or a rejection, never an exception, and the decoder reaches EOF |
| `Multichannel_downmixes_to_stereo` | `38` and `41`: `ToFloatMulti` output has no NaN and peak ≤ 1.0 × (1 + 2 × 0.707) |
| `Ladder_labels_cover_every_wire_format` (owner M, `TrackTests.cs`) | the label table in §5.3, including 22 |
| `Effective_quality_caps_lossless_on_metered` (owner S, `PlatformTests.cs`) | `EffectiveQuality(3, 1, metered) == 1`, `EffectiveQuality(3, 3, metered) == 3`, `EffectiveQuality(3, 1, unknownCost) == 3` |
| `AudioFiles_decode_stages_the_ladder_and_the_bits` (owner E, `DecodeTests.cs`) | a captured kind-5 envelope (the `exx.har` payload the pipeline guide cites, `Fixtures/spotify/audio-files.bin`) → 5 `FormatEdge`s incl. `(16, 700)`, `Lossless` set, `Lossless24` clear |
| `Ctr_validates_a_flac_head` (owner F, `SpotifyAudioTests.cs`) | `Validates` accepts a self-encrypted `fLaC` prefix at offset 0 and an `OggS` at `0xa7` |
| `Key_skips_the_ap_for_flac` (owner F) | with a fake AP that answers `Rejected`, a `Flac` open never latches `ApKeysDisabled` |

The `--flac-probe <file>` arm in `Screens/Diagnostics.Probe.cs` (owner S, 40 lines) runs §7.2's loop on any file
and prints STREAMINFO, the frame count, the MD5 verdict, decode throughput and the seek-probe count — the
field tool for a user's "this FLAC does not play" report. No env var (CLAUDE.md).

---

## 8. The work split

### 8.1 Files, owners, waves, budgets — in §2's style, disjoint from in-flight owners

| File | Concern | Owner | Wave | Lines | Source |
|---|---|---|---|---|---|
| `+Playback/Playback.Audio.Flac.cs` | **CORE**: `BitReader`, `ParseHeaders` (STREAMINFO / SEEKTABLE / VORBIS_COMMENT / PICTURE), `ParseFrameHeader` + tables + `ReadCodedNumber`, CRC-8/16, the four subframes, `DecodeResidual`, `RestoreLpc`, `Decorrelate` / `ShiftLeft` / `ToFloat` / `ToFloatMulti` (the two `Vector128` sites), `FindHeader`, `EstimateOffset`, `Decoder`, `Md5Verifier` | **U** (new) | starts today; gated before Wave 3 opens | 1,100 | this plan §3 |
| `Wavee.Tests/FlacTests.cs` + `Fixtures/flac/**` (≈ 5.2 MB, CC0) | §7 | U | with the above | 450 | this plan §7 |
| `Playback/Playback.Audio.cs` | `FlacAudioDecoder : IAudioDecoder` (§4.3), the `Opened.Label`, the decoder factory arm | H | 3 | +220 (2,250 → 2,470) | §4 — H's file, H's lines; U hands over the CORE and reviews the adapter |
| `Playback/Playback.Host.cs` | the session-scoped format-override map (§5.2) and the `StreamFormat` publish for FLAC | G | 3 | +30 | §5.2, §4.4 |
| `Entities/Track.cs` | `TrackFields.Files`, `TrackFlags.Lossless/Lossless24/FilesMask`, `Column<UInt128> OriginalAudio`, the commit arm | A | request, Wave 1 file | +40 | §5.2 |
| `Entities/Edges.cs` | `FormatEdge`, `EdgeTable<FormatEdge> TrackFormats` | B | request, Wave 1 file | +12 | §5.2 |
| `Entities/Fetch.cs` | the `Files` route: audio-uri derivation, kind 5 batch, empty-mark for rows without `OriginalAudio` | C | request, Wave 1 file | +50 | §5.2 |
| `Spotify/Spotify.Decode.cs` | `Decode.AudioFiles` + `TrackV4` staging `original_audio` | E | request, Wave 2 file | +70 | §5.2 |
| `Spotify/Spotify.Audio.cs` | the three §1.1 fixes (`skip = 0` for FLAC, `Key(apEligible: false)` for FLAC, `Validates` accepts `fLaC`) + `PreferredQuality` through `NetworkPolicy.EffectiveQuality` | F | request, Wave 2 file | +15 | §1.1, §5.5 |
| `Entities/Track.Drawer.cs` | the ladder over `TrackFormats`, the labels incl. 22, the metered-disabled reason, the versions row's format label | M | 5 (in budget: ch 01 §9 already counts `FormatSplitButton` 129 inside the 700) | 0 net | §5.3 |
| `Screens/Settings.cs`, `+Settings.UI.Playback.cs` | the fourth rung in both combos, `> 3` guards, the description strings, the ch 27 parity restatement | R | 6 | +25 | §5.4 |
| `Platform/Platform.cs` | `NetworkPolicy.EffectiveQuality` clamp `0..3`; `Keys.LocalFileTags` | S | 6 | +5 | §5.5, §6 |
| `Platform/Modules.cs`, `Modules.Host.cs` | `PlayableLinks.ProbeFlac`, the local cover range served as `wavee:local:art:` | T | 6 | +60 | §6 |
| `Screens/Diagnostics.Probe.cs` | `--flac-probe` | S | 6 | +40 | §7.3 |
| `Wavee.csproj` | **remove** `<PackageReference Include="FlacBox" Version="1.0.0" />` (`:174`) and the `NU1701` suppression if nothing else needs it | orchestrator | Wave 6 (with `git rm -r src/apps/_old`) | −2 | §1.3 |
| `assets/loc/en-US.json` | `settings.quality.lossless*`, `detail.versions.meteredReason` | R | 6 | +4 | §5.3, §5.4 |

**Owner U is one subagent** with two files (the CORE decoder and its tests) and **no** file shared with anyone
in flight. Every other line above is a small, named request to that file's owner, listed so the plan's "DISJOINT
files" rule (§5) holds: U never edits `Spotify.Audio.cs`, `Track.cs`, `Edges.cs`, `Decode.cs` or `Playback.Audio.cs`.

### 8.2 What can start today, what waits

```
 today ─────────────────────────────────────────────────────────────────────────────────────▶ Wave 3 ──▶ Wave 5 ──▶ Wave 6
 U:  Playback.Audio.Flac.cs + FlacTests + fixtures          (no dependency on any wave: pure over spans)
 A/B/C/E: the catalogue columns, edge, planner route, kind-5 decode   (Wave 1/2 files, small; can land now)
 F:  the three Spotify.Audio.cs fixes                        (15 lines; can land now — the key latch bug is live today)
                                                              H:  FlacAudioDecoder in Playback.Audio.cs (needs U green)
                                                              G:  override map + StreamFormat publish
                                                                          M:  the drawer ladder (needs A/B/E)
                                                                                     R:  the settings rung
                                                                                     S:  EffectiveQuality 0..3, --flac-probe
                                                                                     T:  local probe + cover
                                                                                     orchestrator: FlacBox removed, CHANGELOG (#n)
```

### 8.3 The gate

- **U's gate (before Wave 3 opens):** `FlacTests` green — every vendored vector bit-exact against its MD5, the
  allocation fact at 0 bytes, seek exact on `01` and `47`, the two faulty files resync; Debug and Release build
  clean (`TreatWarningsAsErrors`); no source-text test (CLAUDE.md).
- **Wave 3's gate gains one line:** the login smoke (orchestrator, network) plays 10 s of a track at
  `Quality.Lossless` on an entitled account through the real pump, the log shows `audio.open fmt=Flac24 rate=44100
  bps=24 gain=-x.x dB` and a seek to 2:00 lands in ≤ 4 probes; on a public-only build the same open logs
  `Fault.NoDeriver` and plays the 320 rung (no latch trip: `ApKeysDisabled` stays false).
- **Wave 5:** ch 01 W14 / ch 04 W15 / ch 05 W14 parity items with the ladder showing FLAC and FLAC 24-bit rows
  where the golden capture showed none — recorded as a deliberate divergence with the issue number, never struck
  silently (G3).
- **Wave 6:** ch 27 items 33a/33b restated to four items; `--fake` still renders Settings ▸ Playback with the
  rung; the perf tour shows no gen2 during a 5-minute lossless listen (the 24-bit stream is ~1.4 Mbit/s, the
  decoder's steady state is zero allocations — the tour proves it end to end).

---

## 9. Open questions — only Christos can answer

1. **Lossless on Wi-Fi by default?** Spotify ships it OFF and per device (§1.4). This plan keeps `playback.quality`
   at VeryHigh320 and `meteredCap` at High, so lossless is opt-in twice (the rung, and the metered cap). Should the
   first launch on an entitled account offer it once (a one-time notice through `Notify`, like the "Lossless is
   available to you" push Spotify sends), or stay silent? Windows cannot tell Wi-Fi from Ethernet reliably and
   `NetworkCostType` is the only cost signal — so "on Wi-Fi" would mean "unmetered", which is what the cap already
   expresses.
2. **The resampler.** Every 44.1 kHz FLAC on a 48 kHz endpoint is linearly interpolated (the engine's M2 choice,
   `LinearResampler.cs:24`). That is the same as Vorbis today and inaudible to most, but it is the one step where
   "lossless" is not. A windowed-sinc SRC is engine work (`..\fluent-gpu`, its own gates) — do you want it planned
   for 0.3, or after?
3. **Bit-perfect / exclusive mode.** Spotify added Exclusive Mode on Windows in March 2026 (§1.4). The engine has
   shared mode only; exclusive mode is a real engine feature (`IsFormatSupported`, a 24-bit PCM sink, device
   ownership UX). Out of scope for 0.3 unless you say otherwise.
4. **A lossless badge in the track row?** No chapter shows one (0.2.9 had none). Adding it means `Files` joins the
   album page's row demand (one kind-5 batch per album) — cheap for albums, not for 300-row playlists. The drawer
   ladder and the versions-row label (§5.3) show it without a row badge; the stage badge shows it while playing.
5. **Local file tags.** 0.2.9 chose the file name deliberately. This plan parses TITLE/ARTIST/ALBUM and the cover
   from the header at zero extra cost but keeps them behind `LocalFileTags` (default off). Default on instead?
6. **The format override's scope.** 0.2.9 kept the per-track override for the session only. Persisting it (a
   settings string) is ch 01 GAP 14's proposal; the plan keeps session scope unless you want it persisted.
7. **The issue number.** CLAUDE.md: every fix references its issue. This is a feature, and it also fixes a live
   defect (the AP-key latch on a FLAC open, §1.1 item 2, which would bite the first lossless user of a deriver
   build). One issue "Lossless (FLAC) playback" with the latch fix as a bullet, or two? The orchestrator drafts;
   nothing is filed by this plan.

---

## 10. Sources

Local, read for this plan: `src/apps/Wavee/Spotify/Spotify.Audio.cs`, `Spotify.Session.cs:1065-1122`,
`Entities/Track.cs`, `Entities/Edges.cs:90-150, 594-634`, `Entities/Entities.cs:64-80, 849-985`,
`Platform/Platform.cs:180-262, 995-1012`, `Wavee.csproj:160-200`, `Wavee.Tests/{FootprintGateTests,
SpotifyAudioTests, DecodeTests:596-618, TestScope}.cs`; `docs/plans/wavee/wavee-0.3-implementation.md` §1, §2,
§3.1, §4.2, §4.7-4.9, §5, §8, §9.6; `docs/plans/wavee/wave2-spotify-surface.md`;
`docs/plans/wavee/spotify-audio-file-pipeline-guide.md` §3-§8; `docs/plans/wavee/gapless-findings.md` §2-§3, fix
design; chapters 01 (`:568-600, :1271, :1284, :1445-1481`), 04 (`:425-462`), 05 (`:585-612`), 09 (`:1138-1150`),
14 (`:735`), 15 (`:905-925`), 21 (`:1744-1752`, `:817`), 23 (`:672-702`), 27 (`:645-675, :2098-2108, :2488-2500,
:1853-1916`), 29 (`:160-172, :949-968, :2706`), 31 (`:1269, :1385, :1398`) of `docs/plans/wavee/wavee-0.3-ui/`;
`relationships_wavee.md` §5.11-5.12; the engine: `Media/Playback/MediaSeams.cs:117-160, 165-212, 275-312`,
`MediaTypes.cs:108-118`, `MediaSource.cs:108-125`, `Audio/AudioDecode.cs:45-275`, `Audio/PcmAudioPlayer.cs:96-150,
224-262, 1400-1529`, `Audio/LinearResampler.cs`, `Audio/AudioFeedThread.cs`, `Audio/AudioClock.cs`,
`Audio/AudioTripwire.cs`, `Audio/DspStages.cs:61-98`, `Audio/RingAudioSource.cs`, `FluentGpu.Windows/Wasapi/
{WasapiAudioDevice,WasapiFormatNegotiation,MmcssProAudio}.cs`, `FluentGpu.Windows/Media/Audio/MfAacDecoder.cs`,
`docs/plans/media-playback-api-spec.md` §5.5; 0.2.9: `src/apps/_old/Wavee/SpotifyLive/Audio/{SampleSource,
AudioFormatProbe,FluentMediaAudioHost,Mp3Gapless,LocalAudioDurationProbe}.cs`, `Backend/Audio/
{SpotifyAudioStream,PrefetchingReadStream}.cs`, `Backend/MediaSources/LocalFileMediaProvider.cs`,
`App/LocalPlayables.cs`, `Components/{TrackVersionsPanel,FormatSplitButton}.cs`,
`SpotifyLive/{LiveTrackResolver,SpotifyTrackExpansionService}.cs`; Symphonia: `symphonia-bundle-flac/src/
{frame,decoder,parser,demuxer,validate}.rs`, `symphonia-core/src/{io/bit,checksum/crc8,checksum/crc16,util}.rs`,
`symphonia-common/src/xiph/audio/flac/mod.rs`, `symphonia-metadata/src/embedded/{flac,vorbis}.rs`; librespot:
`metadata/src/audio/file.rs`, `playback/src/{player,lib}.rs`, `playback/src/decoder/symphonia_decoder.rs`,
`protocol/proto/{metadata,extension_kind,audio_files_extension,connect,media_manifest}.proto`; librespot-java,
fastpotify, SpotiLoad as cited in §1.5.

Web: RFC 9639 https://www.rfc-editor.org/rfc/rfc9639.html; Spotify newsroom 2025-09-10 and 2025-10-08 (§1.4);
https://support.spotify.com/us/article/lossless-audio-quality/; https://support.spotify.com/us/article/audio-quality/;
https://github.com/librespot-org/librespot/issues/1583; https://github.com/librespot-org/librespot/discussions/1578;
https://raw.githubusercontent.com/librespot-org/librespot/dev/protocol/proto/metadata.proto;
https://github.com/devgianlu/go-librespot/commit/ef213e5424c755a1a5dcfe2607e3997de5ea4eb6 and its
`player/player.go`, `player/format.go`, `flac/decoder.go`, releases v0.6.0 / v0.8.0;
https://github.com/ietf-wg-cellar/flac-test-files (CC0-1.0; sizes from the GitHub contents API);
https://learn.microsoft.com/en-us/windows/win32/medfound/audio-subtype-guids;
https://learn.microsoft.com/en-us/windows/win32/medfound/supported-media-formats-in-media-foundation;
https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-mfcreatesourcereaderfrombytestream;
https://strontic.github.io/xcyclopedia/library/clsid_6B0B3E6B-A2C5-4514-8055-AFE8A95242D9.html (the FLAC MFT);
https://www.nuget.org/packages/FlacBox; https://github.com/jdpurcell/SimpleFlac; https://github.com/BunLabs/NAudio.Flac;
https://reveng.sourceforge.io/crc-catalogue/16.htm (CRC-16/UMTS = poly 0x8005 init 0, check 0xFEE8) and
https://reveng.sourceforge.io/crc-catalogue/1-15.htm (CRC-8 poly 0x07 init 0, check 0xF4);
https://www.audioendgame.com/articles/spotify-lossless-in-2026-is-it-actually-hi-fi-and-what-gear-do-you-need;
https://www.notebookcheck.net/Spotify-lossless-is-apparently-not-lossless-on-Windows-but-a-fix-might-be-coming-soon.1128528.0.html;
https://techcrunch.com/2025/11/13/spotify-introduces-a-premium-platinum-plan-with-lossless-access-in-five-markets.
