# Xiph.Org Vorbis test vectors

Conformance material for the hand-rolled Vorbis decoder (`Playback.Audio.Vorbis`) and the Ogg page reader
(`Playback.Audio.Ogg`), from `docs/plans/wavee/playback-smoothness-implementation.md` §5 (WP-4d, issue #167).
`Wavee.Tests.csproj` copies `Fixtures\**\*` to the output directory; `VorbisTests` (the `Xiph_*` and `A_chained_*`
facts, helper `XiphFixture`) and `VorbisFuzzTests` read them from `AppContext.BaseDirectory`.

## Source

Downloaded on 2026-10-02 from **https://people.xiph.org/~xiphmont/test-vectors/vorbis/** (the directory Christopher
"Monty" Montgomery keeps for Xiph.Org; 29 streams, 51 MB in all). Only the six small, distinct ones are kept; each `.ogg`
below is the byte-identical download.

**Licence: none stated.** No licence or README accompanies the directory. The streams are Xiph.Org's published
decoder-conformance material (the same streams other Vorbis decoder projects test against); they are vendored for
that single purpose, unmodified, with this record of where they came from. The owner may want to confirm the terms
with Xiph.Org before the repository is made redistributable beyond its current audience.

## Files

| File | Bytes | SHA-256 (the download) |
|---|--:|---|
| `test-short.ogg` | 29,781 | `183510552403021fb90ce43796fcc88e16c8bb4ae5d0d72a316b8e51afc395fa` |
| `test-short2.ogg` | 147,456 | `6bd3f59e0fa77904edd35c73dadd6558e2a36d78c9d7bc5db223862bf092fcc2` |
| `48k-mono.ogg` | 44,925 | `f51459d9bdd04ca3ec6f6732b8a01efcdc83e5c6fc79c7d5347527bfd4948e9e` |
| `singlemap-test.ogg` | 59,328 | `50d8077608a4192b8f8505aec0217be8b6c25def4068899afc19c17f30e1d521` |
| `unused-mode-test.ogg` | 118,879 | `e27ae6fc2f7c0037c28328801c46d966abcee3050e3ebd40bf097f7986f50f94` |
| `chain-test3.ogg` | 74,706 | `ed039ba775d1b31e805d26d6413a3ef2c6663bf4c5ea47ce1462f8f23c84d8dc` |
| `test-short.s16` | 88,200 | `be0f1fa28dbb88fbf4bb450d05c84659739d12931d509ecb34bf7f4641e17694` |
| `test-short2.s16` | 88,200 | `4ef0485bc85bc9f95fcf6ee3bf87309fed000c0df84b77cfc46b6522d89c6a19` |
| `48k-mono.s16` | 48,000 | `3f6233c6a2b93fb72b943fcd254b1860fdf807fe8678b7207de106011b67b4f0` |
| `singlemap-test.s16` | 88,200 | `cb4aca9304db4462305fe9bc8ab9f0b4a5577cb1a4dd2322147f1e373cfc5226` |
| `unused-mode-test.s16` | 88,200 | `da28c325147e2be6ac1dbc7f69b7215357b26b604a55483b51f46ea8d0f1b3bd` |

Total 875,875 bytes (855 KiB). The `.s16` files are generated (below); everything else is a download.

## What each stream pins

Facts below were read from the headers and pages with an independent parser (a throwaway script), not from the decoder.
All six have a first audio page whose granule equals the frames of its packets (lead-in 0).

| Stream | Shape | Setup | Pages / audio packets / last granule | Why it is here |
|---|---|---|---|---|
| `test-short.ogg` | 44.1 kHz stereo, 256 / 2048 | **floor 0 + residue 0**, 11 codebooks (two scalar books of 4,096 entries, two lookup-type-2 books, the rest type 1) | 8 / 80 / 59,392, EOS | the one real floor-0 + residue-0 stream: an early Xiph encoder, which the libvorbis-era fixtures never produce. 1.35 s, 29 KB |
| `test-short2.ogg` | 44.1 kHz stereo, 256 / 2048, 160 kbit/s nominal | floor 0 + residue 0, 18 codebooks | 36 / 317 / 282,816, **no EOS**, cut mid-page (1,868 trailing bytes) | the same paths at a real bitrate, and a file that ends in a partial page: the reader must answer `NeedMore`, not throw. Its first second is near silence (peak 6 LSB), so the reference window starts at 4.0 s |
| `48k-mono.ogg` | 48 kHz **mono**, **512 / 4096** | floor 1 + **residue 1**, 32 codebooks | 12 / 336 / 515,234, EOS | a block pair no Spotify file uses, mono duplicated to L = R, residue type 1, and an end truncation of 1,502 frames (the packets make 516,736) |
| `singlemap-test.ogg` | 44.1 kHz stereo, **2048 / 2048** | floor 1 + residue 2, **one mode, one mapping** | 16 / 169 / 172,032, no EOS (cut on a page boundary) | `blocksize0 == blocksize1`, a zero-bit mode field, one mapping |
| `unused-mode-test.ogg` | 44.1 kHz stereo, 256 / 2048 | floor 1 + residue 2, **three modes** (mode 2 declared, identical to mode 0), three mappings, 2-bit mode field | 30 / 1,102 / 540,991, EOS | a mode count that is not a power of two. **34 packets (864-897) address mode 3, which the setup never declared**: ffmpeg rejects them ("Index value 3 out of range"), libvorbis skips them. The decoder must answer `BadMode` for those 34 and carry on |
| `chain-test3.ogg` | two logical streams back to back: 44.1 kHz stereo, then 48 kHz mono | link 1 is `test-short.ogg` packet for packet; link 2 is `48k-mono.ogg` | 20 / 80 + 336 | the reader plays the FIRST link and stops at its EOS (Spotify never serves a chain); the PCM must equal `test-short.ogg`'s bit for bit |

The plan guessed that "the Xiph vectors carry no floor 0". They do: `test-short`, `test-short2` and the four `lsp-test*`
streams (not vendored, 262-443 KB) are floor 0 + residue 0. `VorbisTests.Floor0_*` still covers the pure floor-0
renderer against the spec in `double`; these streams add the whole packet path around it, against a reference decoder.

## The references (`*.s16`)

The oracle is **libvorbis itself** (Xiph's reference decoder), reached through ffmpeg 8.1.2
(`full_build-www.gyan.dev`): `-c:a libvorbis` on the INPUT selects the libvorbis decoder; ffmpeg's own native Vorbis
decoder agrees with it to 1 LSB on every stream it can open (it rejects `test-short.ogg` outright, "Codebook lookup type
not supported", which is why libvorbis is the reference). Float → s16 is ffmpeg's round-to-nearest, no dither, which
is the convention of `Fixtures/ogg/README.md`.

A reference is a **window of the decode, not the first 3 s**: 0.5 s (`rate / 2` frames) starting at the frame in the
table. Short on purpose: the plan's rule is "no full-length `.s16`", and these streams are music, so one half second holds
long and short blocks and every floor / residue / window path. The frame count and the end truncation are checked
over the whole link from the granules, not from the reference.

| Reference | Source frame of the window | Channels | Bytes | Window RMS (LSB) |
|---|--:|--:|--:|--:|
| `test-short.s16` | 0 | 2 | 88,200 | 7,302 |
| `test-short2.s16` | 176,400 (4.0 s) | 2 | 88,200 | 1,266 |
| `48k-mono.s16` | 48,000 (1.0 s) | 1 | 48,000 | 1,891 |
| `singlemap-test.s16` | 0 | 2 | 88,200 | 3,432 |
| `unused-mode-test.s16` | 0 | 2 | 88,200 | 3,222 |

No window clips (the largest peak is 29,461) and the unused-mode window ends long before its first invalid packet (packet 864, about 9 s in).

```sh
# one full decode per stream with libvorbis, then cut the window with the table above
ffmpeg -v error -y -c:a libvorbis -i test-short.ogg -f s16le -acodec pcm_s16le full.s16
python -c "import numpy as np; a=np.fromfile('full.s16','<i2'); a[0*2:(0+22050)*2].tofile('test-short.s16')"
#   test-short2   start 176400, 22050 frames, 2 channels      48k-mono   start 48000, 24000 frames, 1 channel
#   singlemap     start 0,      22050 frames, 2 channels      unused-mode start 0,    22050 frames, 2 channels
```

`chain-test3.ogg` has no reference of its own: its first link is `test-short.ogg`, and ffmpeg, which decodes the
first link only (it stops at the channel-count change), produces exactly `test-short`'s 237,568 bytes for it.

## Not vendored

`1.0-test`, `1.0.1-test`, `beta3-test`, `beta4-test`, `bimS-silence`, `chain-test1/2`, `combustion`, `highrate-test`,
`make`, `mono`, `moog`, `one-entry-codebook-test`, `out-of-spec-blocksize`, `rc1/rc2/rc2-2/rc3-test`, `sleepzor`
(0.3 - 7.7 MB each, 45 MB together) and `lsp-test`, `lsp-test2/3/4` (262-443 KB; the same floor-0 paths as `test-short2`).
There is no 5.1, 8-bit or 128 kHz stream in the directory (the audit's §11.4 list was from memory).
