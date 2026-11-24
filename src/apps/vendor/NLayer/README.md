# NLayer (vendored)

The managed MPEG audio decoder Wavee plays MP3 with. MIT licence (`LICENSE`), by Mark Heath, Andrew Ward and
contributors: https://github.com/naudio/NLayer.

**Source:** upstream `main` at v3.0.1 (82f7a92), the files of `NLayer/` unchanged except for the changes below
(proposed upstream as https://github.com/naudio/NLayer/pull/63).
**Why vendored:** Wavee was on the NuGet package 1.15.0, whose bit reservoir dropped a Layer III frame (and decoded
the next one as noise) whenever the main data before it filled the 8192-byte ring exactly; real MP3s hit that
about every 50 s. Upstream fixed that in 3.0.1, but 3.0.1's own gapless trimming and seeking would have
replaced the dropped frame with a 12 ms tail cut. These changes are proposed upstream; go back to the package
(`<PackageReference Include="NLayer" … />` in `Wavee.csproj`) once a release carries them, and delete this folder.

## Local changes (`MpegFile.cs`, `Decoder/MpegStreamReader.cs`, `Decoder/MpegFrame.cs`, `Decoder/VBRInfo.cs`, `Decoder/BitReservoir.cs`)

1. **The LAME decoder delay.** A LAME tag's `delay` field is the encoder's delay only; the LAME spec has the decoder
   skip `delay + 528 + 1` and trim `padding − 529` at the end. 3.0.1 skipped `delay` only, so its output began with
   529 samples of filterbank warm-up and stopped 529 samples before the real end. The trimmed length is unchanged
   (clamped when the padding is under 529, so it never exceeds what can be read). Layer III only.
2. **FFmpeg's LAME tag.** libmp3lame through FFmpeg writes the same LAME tag layout under the encoder name
   `Lavc…`/`Lavf…`; 3.0.1 accepted only `LAME`, so those files were not trimmed at all. Delay and padding come from
   one source (the LAME tag, else iTunSMPB).
3. **Exact seeks.** `Position` counts in the trimmed timeline and a seek lands on exactly the requested sample. The
   pre-roll is sized from the stream (main_data_begin reaches 511 bytes back for MPEG-1, 255 for MPEG-2/2.5: up to
   25 frames at 8 kbps) and walks by each frame's own length; 3.0.1's single frame often dropped the target frame
   and resumed a frame late. Seeks past the end throw consistently.
4. **Bit reservoir.** A frame whose main data reaches back past what was written since `Reset()` is refused instead
   of being decoded from stale bytes (a burst of noise after a seek).

Checked against ffmpeg 8.1.2 (scratch, never committed): every test and corpus MP3 decodes to ffmpeg's exact
length; seeks match ffmpeg to float rounding (≤ 3e-7), and 345 seeks per file in 32 kbps / 48 kHz, 8 kbps / 24 kHz
mono, MPEG-2 22 kHz, VBR and 320 kbps files are bit-identical to a sequential decode. Reviewed independently before
the upstream PR.
