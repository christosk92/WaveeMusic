# Ogg Vorbis fixtures

Generated on 2026-09-13 with **ffmpeg 8.1.2** (`full_build-www.gyan.dev`, libvorbis encoder) per
`docs/plans/wavee/wavee-0.3-vorbis-implementation.md` §7.1. Every file is regenerable with the command below; the
sources are deterministic (`anoisesrc` seeds, `sine`/`aevalsrc` expressions) and `-fflags +bitexact -flags:a +bitexact`
keeps the muxer's serial and vendor string stable. `Wavee.Tests.csproj` copies `Fixtures\**` to the output directory;
`VorbisTests` and `OggTests` read them from `AppContext.BaseDirectory`.

All six are libvorbis output with a **256 / 2048** block pair, floor 1 and residue 2 (the Spotify shape), a first audio
page whose granule equals the frames of its packets (lead-in 0), and an end truncation on the last page.

| File | Bytes | Pages | Frames (last granule) | EOS trim | What it pins |
|---|--:|--:|--:|--:|---|
| `pink-320.ogg` | 410,992 | 12 | 441,000 | 88 | the VeryHigh rung: 44 books, ~1 s / ~41 KB pages; the oracle, the throughput and allocation facts |
| `pink-96.ogg` | 124,377 | 12 | 441,000 | 88 | the Normal rung: 38 books, 420 KB of VQ lattices, ~12 KB pages |
| `vbr-q8.ogg` | 356,088 | 12 | 441,000 | 88 | strong VBR (white noise gated 0 dB / −60 dB in 2 s halves): the seek estimate's worst case, the second oracle |
| `sine-440.ogg` | 16,867 | 10 | 352,800 | 32 | mono duplicated to L = R; the SNR fact; a comment block with `title` / `artist` |
| `sweep-48k.ogg` | 30,646 | 10 | 384,000 | 128 | 48 kHz; long/short block switching on a 20 Hz – 20 kHz sweep |
| `pages-100ms.ogg` | 413,071 | 89 | 441,000 | 88 | small pages (~4.7 KB, 5-12 packets each): many pages per probe window, one-probe seeks |
| `pink-320.s16` | 529,200 | — | 132,300 | — | the PCM reference: the first 3 s of ffmpeg's own decode of `pink-320.ogg`, s16le stereo |
| `vbr-q8.s16` | 529,200 | — | 132,300 | — | the same for `vbr-q8.ogg` |

Total 2,410,441 bytes. Spanning packets, zero-length packets, sequence holes, foreign serials and bad CRCs are not
fixtures: ffmpeg's muxer produces none of them, so `OggTests` builds those pages in memory with a real CRC.

## Commands

```sh
PINK='anoisesrc=color=pink:seed=11:duration=10:sample_rate=44100:amplitude=0.3'
PINK2='anoisesrc=color=pink:seed=22:duration=10:sample_rate=44100:amplitude=0.3'
BX='-fflags +bitexact -flags:a +bitexact'

ffmpeg -y -f lavfi -i "$PINK" -f lavfi -i "$PINK2" -filter_complex "[0:a][1:a]join=inputs=2:channel_layout=stereo[a]" \
       -map "[a]" -c:a libvorbis -b:a 320k $BX pink-320.ogg
ffmpeg -y -f lavfi -i "$PINK" -f lavfi -i "$PINK2" -filter_complex "[0:a][1:a]join=inputs=2:channel_layout=stereo[a]" \
       -map "[a]" -c:a libvorbis -b:a 96k $BX pink-96.ogg
ffmpeg -y -f lavfi -i "anoisesrc=color=white:seed=33:duration=10:sample_rate=44100:amplitude=0.4" \
       -f lavfi -i "anoisesrc=color=white:seed=44:duration=10:sample_rate=44100:amplitude=0.4" \
       -filter_complex "[0:a][1:a]join=inputs=2:channel_layout=stereo,volume='if(lt(mod(t,4),2),1,0.001)':eval=frame[a]" \
       -map "[a]" -c:a libvorbis -q:a 8 $BX vbr-q8.ogg
ffmpeg -y -f lavfi -i "aevalsrc=exprs=0.5*sin(2*PI*440*t):s=44100:d=8" -c:a libvorbis -q:a 5 \
       -metadata title="Wavee sine fixture" -metadata artist="Wavee" $BX sine-440.ogg
ffmpeg -y -f lavfi -i "aevalsrc=exprs=0.5*sin(2*PI*(20*t+1248.75*t*t))|0.5*sin(2*PI*(20*t+1248.75*t*t)+0.5):s=48000:d=8" \
       -c:a libvorbis -q:a 6 $BX sweep-48k.ogg
ffmpeg -y -f lavfi -i "$PINK" -f lavfi -i "$PINK2" -filter_complex "[0:a][1:a]join=inputs=2:channel_layout=stereo[a]" \
       -map "[a]" -c:a libvorbis -b:a 320k -page_duration 100000 $BX pages-100ms.ogg

# references: ffmpeg's decoder, float → s16 by round-to-nearest (no dither), first 3 s = 529,200 bytes
ffmpeg -y -i pink-320.ogg -f s16le -acodec pcm_s16le full.s16 && head -c 529200 full.s16 > pink-320.s16
ffmpeg -y -i vbr-q8.ogg   -f s16le -acodec pcm_s16le full.s16 && head -c 529200 full.s16 > vbr-q8.s16
```

## Measured with ffmpeg's decoder (the numbers the facts are set from)

- ffmpeg outputs exactly the last page's granule for every file (441,000 / 352,800 / 384,000 frames), and its
  float output rounds to the stored s16 references with a maximum error of 0.5 LSB.
- `sine-440.ogg` decodes to amplitude 0.5038 with **48.4 dB** SNR against the fitted 440 Hz sinusoid; a perceptual
  codec at q5 does not reach the 60 dB the plan guessed, so `Sine_decodes_with_snr_above_40_db` holds 40 dB.
- Blocksize(prev)/4 + blocksize(cur)/4 summed over each page's packets equals that page's granule step on every
  page of every file (the convention the decoder emits in and the seek lands by).
