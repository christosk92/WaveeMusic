# FLAC test vectors

Vendored from **https://github.com/ietf-wg-cellar/flac-test-files**, the IETF CELLAR working group's suite for
RFC 9639, at commit `aa7b0c6cf32994c106ae517a08134c28a96ff5b2` (2023-08-03). Licence **CC0-1.0** (public domain
dedication — vendoring needs no notice; this file records the source anyway).

`Wavee.Tests.csproj` copies `Fixtures\**\*` to the output directory, so `FlacTests` reads them from
`AppContext.BaseDirectory`. Every file below is decoded bit-exact against its own STREAMINFO MD5 by
`FlacTests.Decodes_bit_exact_against_the_streaminfo_md5` — the format's own oracle, the same one `flac -t` uses.

The pick is the SMALLEST file that exercises each decoder path (plan
`docs/plans/wavee/wavee-0.3-flac-implementation.md` §7.1, with the substitutions noted at the bottom).

| File | Bytes | What it pins |
|---|--:|---|
| `subset/01 - blocksize 4096.flac` | 549,954 | the Spotify shape: 16-bit, 44.1 kHz, stereo, fixed block 4096, libFLAC. All four channel assignments appear in it (26 independent, 1 left/side, 4 side/right, 45 mid/side), and it carries a SEEKTABLE |
| `subset/12 - qlp precision 15 bit.flac` | 492,334 | maximum coefficient precision → the `long` LPC accumulator |
| `subset/13 - qlp precision 2 bit.flac` | 530,759 | minimum precision → the `int` LPC accumulator |
| `subset/14 - wasted bits.flac` | 231,596 | the wasted-bits unary + `ShiftLeft` after the subframe |
| `subset/16 - partition order 8 containing escaped partitions.flac` | 471,502 | the Rice escape, at the subset's maximum partition order |
| `subset/17 - all fixed orders.flac` | 589,534 | FIXED predictors, orders 0..4 |
| `subset/22 - 12 bit per sample.flac` | 277,942 | 12-bit: bps code 2, MD5 packing rounds up to 2 bytes |
| `subset/23 - 8 bit per sample.flac` | 181,470 | 8-bit: bps code 1, 1-byte MD5 packing; its last block is 5 samples, which is the scalar tail |
| `subset/38 - 3 channels (3.0).flac` | 63,893 | 3 channels → the `ToFloatMulti` downmix |
| `subset/41 - 6 channels (5.1).flac` | 171,527 | 6 channels → the same, with LFE dropped |
| `subset/45 - no total number of samples set.flac` | 562,343 | `TotalSamples = 0`: duration unknown, `GaplessInfo.None`, seek tier 2 disabled |
| `subset/46 - no min-max framesize set.flac` | 424,137 | `MaxFrame = 0` → `EstimateOffset`'s 16 KiB back-off fallback |
| `subset/47 - only STREAMINFO.flac` | 333,761 | no SEEKTABLE — the Spotify shape for seeking (tier 2) |
| `subset/56 - JPG PICTURE.flac` | 708,441 | a 432,368-byte PICTURE block: the parse, and the `Complete = false` grow-and-retry path |
| `subset/60 - mono audio.flac` | 47,782 | 1 channel → duplicated to L/R |
| `subset/61 - predictor overflow check, 16-bit.flac` | 117,020 | the int-vs-long LPC decision at the edge, 16-bit |
| `subset/62 - predictor overflow check, 20-bit.flac` | 156,871 | 20-bit: bps code 5, 3-byte MD5 packing with sign extension |
| `subset/63 - predictor overflow check, 24-bit.flac` | 204,107 | 24-bit (the Spotify Lossless 24 path); its minimum sample is exactly −2^23, so the float scale is pinned at −1.0 |
| `subset/64 - rice partitions with escape code zero.flac` | 89,138 | an escape width of 0 ⇒ a silent partition |
| `uncommon/09 - Rice partition order 15.flac` | 168,445 | beyond the streamable subset: block 32,768 and 32,768 partitions of one sample |
| `faulty/01 - wrong max blocksize.flac` | 108,081 | a frame larger than STREAMINFO admits ⇒ rejected, resync, no buffer overrun |
| `faulty/08 - blocksize 65536.flac` | 145,006 | a STREAMINFO that fails its own range check ⇒ `Headers.Valid == false`, nothing decoded |

Total 6,625,643 bytes (6.3 MiB).

## Substitutions against the plan's §7.1 list

The plan's own list sums to ~17 MB, not the ~5.2 MB its prose states. These swaps keep the coverage and the size:

- **24-bit and 20-bit** come from `61`/`62`/`63` (478 KB together) instead of `28 - high resolution audio`
  (3.5 MB) and `37 - 20 bit per sample` (2.4 MB) — the plan itself offers this swap and adds the trio "regardless".
- **Variable block size** is not vendored: the smallest variable-blocksize vector is `24` at 2.3 MB. Every vector in
  this folder is fixed-blocksize, so `FlacTests` builds variable-blocksize streams itself (`Synthetic`), which also
  covers 32-bit samples, block sizes to 65,535, a 655,350 Hz rate, and each stereo mode as an exact round trip.
- `03 - blocksize 16` (960 KB), `07 - blocksize 725` (588 KB), `11 - partition order 8` (511 KB) and
  `15 - only verbatim subframes` (890 KB) are dropped: `uncommon/09` covers extreme partition orders, `16` covers
  order 8, the synthetic frames cover VERBATIM and short blocks, and the last block of `23` is 5 samples.
- The 8-channel (42 MB), 384 kHz (10 MB), "extremely large" metadata (16-17 MB each) and `55` (87 MB) vectors are
  out, as the plan says.
