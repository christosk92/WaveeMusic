# MP3 fixture

`sine-440.mp3` (48,502 bytes, SHA-256 `59029b9ffbd7ddf2bd7b1287b077bd9ddc0edf29c39abcc9a532437c7e65c3fc`) is read by
`Mp3DecoderTests` through the app's own seam, `Playback.Audio.Mp3AudioDecoder` (NLayer 1.15.0, local files only), from
`AppContext.BaseDirectory` (`Wavee.Tests.csproj` copies `Fixtures\**\*`). Generated on 2026-10-02 with **ffmpeg 8.1.2**
(`full_build-www.gyan.dev`, libmp3lame); the source is a deterministic `aevalsrc` expression and `+bitexact` keeps the
muxer's strings stable, so regenerating gives the same bytes (verified: same SHA-256 twice).

```sh
ffmpeg -y -f lavfi -i "aevalsrc=exprs=0.5*sin(2*PI*440*t):s=44100:d=6" -c:a libmp3lame -b:a 64k -ac 1 \
       -fflags +bitexact -flags:a +bitexact sine-440.mp3
```

6 s of a 440 Hz sine, 44.1 kHz **mono**, 64 kbit/s CBR, MPEG-1 Layer III, with a 20-byte ID3v2 header, then the
Xing/"Info" frame carrying the LAME tag. Mono on purpose: it keeps the file small and exercises the adapter's
mono -> stereo conform; the rate matches the usual 44.1 kHz mix, and a 48 kHz mix exercises the linear resampler.

## Numbers the tests pin (read with ffmpeg, ffprobe and NLayer directly)

| Fact | Value |
|---|---|
| MPEG audio frames (the Info tag's frame count; the Info frame itself is not counted) | 231 |
| Samples NLayer decodes | 266,112 = 231 x 1,152 |
| LAME tag encoder delay / padding | 576 / 936 |
| Exact length (`frames x 1152 - delay - padding`) | 264,600 = 6.000 s |
| `Mp3Tag.ToGapless` at 44.1 kHz | lead-in 1,105 (576 + the decoder's 529), trail 407 (936 - 529), exact 264,600 |
| Frequency (zero-crossing estimate, both ffmpeg's decoder and NLayer) | 440.0000 Hz |
| Amplitude (least-squares fit at 440 Hz; nominal 0.5) | **0.4750** from ffmpeg's decoder and from NLayer alike: the encoded file holds 0.475, not the decoder, so the facts allow 0.465 - 0.485 |
| SNR against the fitted sinusoid | 80.7 dB at 44.1 kHz, 75.3 dB after the linear 44.1 -> 48 kHz resample |

ffmpeg's own decode (delay and padding trimmed) is 264,600 samples, i.e. exactly the tag's exact length: the numbers
the adapter reports are the ones ffmpeg trims.

## NLayer, measured

NLayer 1.15.0 allocates about **40 KB per decoded MPEG frame** (measured on .NET Framework 4.8 from PowerShell with
`GC.GetAllocatedBytesForCurrentThread` around `MpegFile.ReadSamples`: 10 / 50 / 100 frames cost 41.1-41.3 KB each, of
which about 1.4 KB is the script's own call overhead; the first frame costs 120 KB): roughly 8 MB for 200 frames,
1.5 MB/s of gen-0 garbage at real time. The figure is the library's own code, so it is not expected to differ on .NET 10. The plan's "two hundred frames allocate < 64 KiB" cannot hold for
this codec, so `Mp3DecoderTests` asserts a ceiling of 64 KiB per frame instead and prints the measurement; the
adapter's own contribution (one reused `float[]` per decoder) is below the noise.

## NLayer's `Position`

`MpegFile.Position` is in BYTES of float samples (`Length` = 1,064,448 = 266,112 x 4), not in samples. A seek lands on an
MPEG frame boundary near the target, not on it: the samples that remain after setting `Position = target x 4` give the
landing, measured for targets 1,000 / 44,100 / 132,300 / 200,000 / 260,000 as 0 / 44,928 / 132,480 / 200,448 / 260,352
(a few hundred samples either side, never more than one frame, 1,152). `Mp3DecoderTests.Seek_lands_near_the_target` allows
+-3,072 samples around the target. On 2026-10-02 `Mp3AudioDecoder.Seek` set
`Position = frame x channels` (samples, not bytes), which lands about four times too early; the fact pins the correction
(`x sizeof(float)`).
