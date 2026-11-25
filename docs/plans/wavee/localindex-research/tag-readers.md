# Tag readers for the 0.4 local index: atldotnet, lofty-rs, music-metadata and spotifast

Research for #240 (`handoff/request-plans/240-local-playlist-files-implementation.md` §3.4 matching, §3.5 tag reading).
New requirements: **album art** and **"crazy fast" indexing**. This was a read-only study. Nothing was built or run.

Repos (under `C:\WAVEE\scratch\research\localindex\`), short names used below:

| Short | Repo | Commit | Licence |
|---|---|---|---|
| `atl/` | `Zeugma440_atldotnet\ATL\` | `81a3486a` (2026-11-23), v7.22.0 | MIT (`LICENSE`), but pulls in `Ude.NetStandard` (see 1.1) |
| `lofty/` | `Serial-ATA_lofty-rs\lofty\src\` | `700bf53d` (2026-11-22), v0.25.5 | MIT OR Apache-2.0 (`Cargo.toml:16`) |
| `mm/` | `Borewit_music-metadata\lib\` | `2b6274ca` (2026-11-22) | MIT (`LICENSE.txt`) |
| `sf/` | `crmne_spotifast\src\` | `eaaf3c6c` (2026-11-17) | MIT; its matching lives in librespot (see 4) |

---

## 1. atldotnet (C#)

### 1.1 Licence and dependencies

- The library itself is MIT (`LICENSE`, `ATL.csproj:26` `PackageLicenseExpression MIT`).
- `ATL.csproj:67` references **`Ude.NetStandard` 1.2.0**. Ude is a port of Mozilla's universal charset detector, so
  its licence is most likely the Mozilla tri-licence (MPL 1.1 / GPL 2 / LGPL 2.1); verify on NuGet before relying on
  this. The only caller is CUE-sheet reading (`atl/Utils/Utils.cs:705-716`, used from
  `CatalogDataReaders/BinaryLogic/Cue.cs:82`), so it is dead weight for us, but a package reference brings it in anyway.
- `Zomp.SyncMethodGenerator` (`ATL.csproj:68`) is build-time only (`PrivateAssets all`).

### 1.2 NativeAOT and trimming

- **The csproj declares no trim or AOT support.** It targets `net6.0` only (`ATL.csproj:3`) and has no
  `IsAotCompatible`, `IsTrimmable` or `EnableTrimAnalyzer`. A grep for `RequiresUnreferencedCode`,
  `DynamicallyAccessedMembers` and `IsAotCompatible` finds nothing. ILC will analyse it as an unannotated assembly.
- Reflection and dynamic code:
  - `Enum.GetValues(typeof(TagType))` at `atl/AudioData/AudioDataManager.cs:237,254`, and
    `Enum.GetValues(typeof(PictureInfo.PIC_TYPE))` at `atl/AudioData/IO/MKA.cs:733`. Under AOT this is
    `RequiresDynamicCode` (IL3050).
  - `Assembly.GetExecutingAssembly().GetName()` at `atl/Version.cs:16`, which is harmless.
  - `nativePicCode.GetType().Name` at `atl/Entities/PictureInfo.cs:264,314`, which is harmless; `object` is boxed.
- Wavee sets `TreatWarningsAsErrors` (`WaveeMusic/Directory.Build.props:21`), and the trim/AOT analysers only run on
  publish (`:30`). An unannotated package that produces warnings would show up only in the **Release** publish, as
  an IL2104/IL3053 "assembly produced warnings" error: the build that must not surprise anyone.
- `Regex`: five interpreted, non-source-generated patterns, wrapped in `Lazy<Regex>`. Four are in
  `atl/AudioData/Utils/TrackUtils.cs:15-21` and parse track numbers like `"3/12"` and years. One is in
  `atl/Entities/LyricsInfo.cs:18`. They are AOT-safe but pull the Regex interpreter into the binary.
- Encodings: `Encoding.GetEncoding("ISO-8859-1")` (`Utils.cs:28`) is built in, with no CodePages needed. The only
  other `GetEncoding` call is `getEncodingCached(cdet.Charset)` (`Utils.cs:694-697`), fed by Ude, and it **throws**
  for windows-125x or Shift_JIS unless `CodePagesEncodingProvider` is registered. That path is CUE-only.
  - The consequence: ID3v1 and ID3v2 encoding-0 text are always decoded as Latin-1, with no code-page guessing.
- Statics: `Settings` is a static class of mutable public fields (`atl/Settings.cs`), and
  `AudioDataManager.bufferSize` / `fileOptions` are statics too (`AudioDataManager.cs:21-22`). They are process-global.
  `AudioDataIOFactory.GetInstance()` takes a `lock` on every call (`AudioDataIOFactory.cs:85-96`).

### 1.3 Format coverage

The README table (`README.md:99-129`) covers: MP3/MP2/MP1 (ID3v1, ID3v2.2-2.4, APE), AAC/MP4/M4A/M4B/ALAC (ilst),
FLAC, Ogg Vorbis/Opus/FLAC/Speex, WAV (LIST/INFO, `id3 `, BEXT and others), AIFF/AIFC (+ ID3v2), WMA/ASF, APE, MPC,
WavPack, TTA, TAK, OptimFROG, DSF/DFF, MKA/WebM, CAF, plus tracker, chiptune and MIDI formats. It is the broadest
reader of the four.

### 1.4 How a read works, and how many bytes it touches

- The entry point is `new Track(path)` (`atl/Entities/Track.cs:32-38`), which calls `Update()`, then `new AudioFileIO`.
- `AudioFileIO` picks the reader by extension, tries up to `MAX_ALTERNATES` readers, and **reopens the file** for
  each attempt. If all fail, it sniffs the stream, then reopens once more (`atl/AudioData/AudioFileIO.cs:33-62`).
- `AudioDataManager.ReadFromFile` opens a `FileStream(bufferSize 2048, FileOptions.RandomAccess)`
  (`AudioDataManager.cs:382`), then `read()` runs these steps **in this order for every file**
  (`AudioDataManager.cs:547-600`):
  1. ID3v1: seek to `End-128` and read 128 bytes (`atl/AudioData/IO/ID3v1.cs:278-284`).
  2. ID3v2 probe: read 32 bytes at offset 0. If it is valid, parse the whole tag.
  3. APE: read the 32-byte footer before any ID3v1 (`atl/AudioData/IO/APEtag.cs:209-215`).
  4. The native reader, `audioDataIO.Read(...)`, which **always runs**. There is no "tags only" mode: audio
     properties are always read.
- Native readers wrap the stream in a `BufferedBinaryReader` that **allocates `new byte[Settings.FileBufferSize]`,
  64 KiB by default, per instance** (`atl/Utils/BufferedBinaryReader.cs:62-66`), and fills it in 64 KiB reads.
  A seek outside the buffer refills it at the target (`:118-160`). In practice every format reads at least one 64 KiB
  block after the tags, plus the tail reads above.
  - MP3: from the end of ID3v2 it searches for the first frame. A blind byte-by-byte `0xFF` search may scan up to
    **30% of the file** on a damaged file (`atl/AudioData/IO/MPEGaudio.cs:539-541`). It reads Xing/VBRI and stops
    unless `Settings.MP3_parseExactDuration` is set (`:656-676`). Duration is
    `frames × samplesPerFrame / rate`, else `AudioDataSize / avgBitrate` (`:464-472`). No LAME encoder delay or
    padding is applied.
  - MP4: `navigateToAtom` walks sibling atoms with seeks and skips `mdat` (`atl/AudioData/IO/MP4.cs:1798-1830`). It
    reads **the whole `stsz` table**, 4 bytes per AAC frame (about 40 KB for 4 minutes), just to label the file
    CBR or VBR (`MP4.cs:960-990`). Every atom name becomes a Latin-1 `string`, which allocates.
  - Ogg: it copies **every page of the comment packet into a `MemoryStream`** (`atl/AudioData/IO/Ogg.cs:631-647`).
    Duration comes from the last page's granule, found by seeking `MAX_PAGE_SIZE*0.75` from the end and walking
    forward (`Ogg.cs:496-560`).
  - FLAC: it walks the metadata blocks, parses VORBIS_COMMENT, seeks past PADDING, and reads only the header of a
    PICTURE block (`atl/AudioData/IO/FLAC.cs:204-221`).
- `Settings.ReadAllMetaFrames = true` by default (`Settings.cs:78`). Every frame we don't need is still decoded to a
  string and stored in `AdditionalFields`.
- `Settings.OutputStacktracesToConsole = true` by default (`Settings.cs:44`). A broken file writes to stdout.

### 1.5 Pictures

Pictures are lazy, but in an expensive way:

```csharp
// atl/Entities/Track.cs:448-456
private IList<PictureInfo> getEmbeddedPictures()
{
    if (null != currentEmbeddedPictures) return currentEmbeddedPictures;
    currentEmbeddedPictures = new List<PictureInfo>();
    initialEmbeddedPictures.Clear();
    Update(true);                      // a SECOND full open + parse of the file, this time reading pictures
    return currentEmbeddedPictures;
}
```

- The first access to `track.EmbeddedPictures` re-runs the whole read with `readEmbeddedPictures=true`. It loads
  **every** picture into a `byte[]`, then copies each one (`new PictureInfo(picInfo, false)`) and hashes it with
  FNV-1a (`Track.cs:481-490`, `PictureInfo.cs:323-328`).
- There is no "front cover only" option, and no API returns a picture's **offset and length** for a later ranged read.
- Skipping when pictures are off:
  - ID3v2 seeks past APIC (`atl/AudioData/IO/ID3v2.cs:1180-1210`), **except** when the tag or frame is
    unsynchronised. Then it decodes the picture anyway (`isUnsynchronized` forces the read at `:1180`).
  - MP4 `covr` is seeked past (`MP4.cs:1638-1646`).
  - FLAC PICTURE is seeked past.
  - Ogg `METADATA_BLOCK_PICTURE` is **base64-decoded in full before the `ReadPictures` check**:
    `SetPictureItem` copies and decodes the field (`atl/AudioData/IO/VorbisTag.cs:228-245`), and `ReadPicture`
    only then returns early (`:274-276`). An Ogg with a 1 MB cover costs a 1.3 MB copy and decode per scan.

### 1.6 Performance settings (`atl/Settings.cs`)

| Setting | Effect |
|---|---|
| `FileBufferSize` (`:24`) | 64 KiB, the per-reader buffer allocation |
| `ForceDiskIO` (`:31`) | write-side only |
| `ReadAllMetaFrames` (`:78`) | set it to `false` for a scan |
| `MP3_parseExactDuration` (`:251`) | `false` is the fast default; `true` walks every frame |
| `AudioDataManager.SetBufferSize` / `SetFileOptions` (`AudioDataManager.cs:28-40`) | the FileStream knobs |

There is no parallel or batch API, and no "properties off" or "tags off" switch.

### 1.7 Verdict on atldotnet

The MIT licence makes copying parts legal (keep the notice). As a **dependency**, though, it brings a probably
tri-licensed charset detector, an unannotated `net6.0` assembly with IL3050 sites that would fail our warnings-as-errors
Release publish, five Regexes, and a design that works against "crazy fast":

- several opens per file;
- a 64 KiB `byte[]` per reader;
- string allocation per frame and atom;
- the full `stsz` table read;
- Ogg pictures decoded on every scan;
- pictures read by a second full parse, with no offset handed back.

Its per-format code is long (41 k lines total) because it also writes. The read paths are interleaved with
write-zone bookkeeping (`PrepareForWriting`, `structureHelper.AddZone`), so porting a reader means untangling it.
**Use it as a reference for edge cases, not as a dependency or a code source.**

---

## 2. lofty-rs (Rust)

### 2.1 Read strategy: `ParseOptions` (`lofty/config/parse_options.rs`)

```rust
// parse_options.rs:57-66
pub const fn new() -> Self {
    Self {
        read_properties: true,          // false: no duration/bitrate work at all
        read_tags: true,                // false: tags are seeked past, not parsed
        parsing_mode: Self::DEFAULT_PARSING_MODE,   // BestAttempt
        max_junk_bytes: Self::DEFAULT_MAX_JUNK_BYTES, // 1024: how far to hunt for a tag/frame sync in junk
        read_cover_art: true,           // false: APIC / covr / PICTURE / METADATA_BLOCK_PICTURE skipped
        implicit_conversions: true,     // e.g. TYER+TDAT+TIME → TDRC, gnre → ©gen
    }
}
```

- `ParsingMode` (`:190-222`) has three levels:
  - `Strict`: any non-compliant input fails the file.
  - `BestAttempt` (the default): an undecodable text field becomes empty, and an unknown sample rate becomes 0.
  - `Relaxed`: drops bad fields and ignores most non-fatal errors.
- That is the right model for a scanner: never fail the whole file over one bad frame.
- Format detection reads at most **36 bytes** (`lofty/probe.rs:330-336`), after skipping a leading ID3v2 when
  present.

### 2.2 Where it looks for tags (MPEG, `lofty/mpeg/read.rs`)

- **Start of file** (`:29-72`):
  - Skip zero padding.
  - Loop over 4-byte headers. `ID3` parses the tag, or seeks `header.size` when `read_tags` is false; it also
    skips the 10-byte footer if the flag is set. **Duplicate ID3v2 tags have their frames merged** (`:53-59`).
  - `APETAGEX` at the start is also accepted (`:73-101`).
- **Junk before audio** (`:109-143`): find the first frame sync confirmed by a second header (`find_next_frame`,
  `:238-265`). If no ID3v2 was found and the first frame is not at 0, look for an ID3v2 **inside the junk** within
  `max_junk_bytes` (`lofty/id3/mod.rs:224-270`).
- **End of file**, in this order (`:152-196`):
  1. ID3v1: seek to `End-131` and read 8 bytes. `"TAG"` at offset 3 means ID3v1. **`APETAGEX` there means it is
     an APE footer, not ID3v1** (`lofty/id3/mod.rs:178-200`).
  2. Lyrics3 v1/v2, which sits before ID3v1 (`id3/mod.rs:48-140`).
  3. An APE footer: the 32 bytes before Lyrics3/ID3v1. If found, seek back to the tag start.
  4. The resulting position is the `last_frame_offset` used by the duration estimate.

### 2.3 Duration (MPEG, `lofty/mpeg/properties.rs`)

- With a Xing/Info/VBRI header: `samples_per_frame × frames / rate` (`:151-169`). No LAME delay or padding is
  applied.
- Without one: it **searches backwards from the end of the audio for the last frame header** matching the first
  (`:187-209`). Then `duration = (last_frame_end − first_frame) × 8 / bitrate` (`:211-230`). The source admits VBR
  without a header is still treated as CBR (`:222-227`).

### 2.4 Pictures

- With `read_cover_art=false`:
  - ID3v2 APIC is skipped (`lofty/id3/v2/frame/read.rs:61-64`), but `skip_frame` does it with
    `io::copy(reader, sink)` (`:302-311`). The bytes are **still read**, because the frame reader is `Read` only.
  - `find_id3v2` reads the **whole tag** into a `Vec` (`id3/mod.rs:257-259`).
  - MP4 `ilst` is read whole into memory (`lofty/mp4/ilst/read.rs:30-31`), and `covr` is then skipped
    in memory (`:47-53`).
  - FLAC PICTURE blocks are not loaded (`lofty/flac/read.rs:78-82`).
  - Ogg `METADATA_BLOCK_PICTURE` is skipped before decoding (`lofty/ogg/tag/read.rs:105-108`).
- **Lesson**: "skip covers" in lofty, as in music-metadata, means *don't parse*, not *don't read*. Covers are
  usually most of the tag bytes, so a fast scanner must seek past the picture payload itself (see 5).
- MP4 bounds the reader to `moov` (`lofty/mp4/read/mod.rs:62-66`), so `mdat` is never read.
- Ogg duration: `rfind("OggS")` from the end with a `MAX_CONTENT_SIZE/4` window (`lofty/ogg/mod.rs:48-60`), with
  retries on false positives. Opus subtracts `pre_skip` (`lofty/ogg/opus/properties.rs:107,134-147`). Vorbis uses
  last granule − first granule (`ogg/vorbis/properties.rs:113-124`).

---

## 3. music-metadata (TypeScript)

### 3.1 Options (`mm/type.ts:1511-1525`)

- `duration` (default `false`): "parse the whole media file if required to determine the duration".
- `skipCovers` (default `false`): skip parsing covers.
- `skipPostHeaders`: don't look for trailing tags (ID3v1, APE, Lyrics3).

### 3.2 Tokenizer approach

- `parseFile` opens a random-access `strtok3` file tokenizer (`mm/parseFile.ts:21-24`). Parsers pull fixed-size
  tokens (`readToken`) and `ignore(n)` whatever they don't need. On a random-access tokenizer, `ignore` is a
  position bump. strtok3 is not vendored in the clone, so that detail is from its documented behaviour.
- Trailing tags are found first by `scanAppendingHeaders` (`mm/core.ts:168-177`): ID3v1 (128 bytes), then the
  Lyrics3 length, then the APE footer. This is the same order as lofty.
- **But ID3v2 is still read whole**: `parseId3Data` calls `readBuffer(tokenizer, dataLen)` on the full tag body,
  cover included (`mm/id3v2/ID3v2Parser.ts:141-150`). `skipCovers` only skips the APIC decode (`:292`).
  - MP4: `skipCovers` skips the `covr` data parse (`mm/mp4/MP4Parser.ts:622`). `mdat` is `ignore`d
    (`:1144-1157`).
  - FLAC: `skipCovers` at `mm/flac/FlacParser.ts:295`.
  - Ogg Vorbis: `mm/ogg/vorbis/VorbisStream.ts:108`.

### 3.3 MP3 duration without a full scan (`mm/mpeg/MpegParser.ts`)

- **Frame 1** (`:259-265`):
  - VBRI is peeked at the fixed offset 36. Only 58 bytes are read and the seek table is never walked; duration is
    `frames × spf / rate` (`:306-330`).
  - Then the side information is skipped and the 4-byte tag read: `Info` (CBR), `Xing` (VBR) or `LAME`
    (`:397-425`).
  - `readXingInfoHeader`: duration is `numFrames × spf / rate` when the Xing `frames` and `bytes` flags are set
    (`:462-465`).
- **Frame 4** (`:267-283`): without `duration:true`, it stops the stream there (`return true; // Enforce duration not
  enabled`). Three or four frames have been read, about 2 KB of audio.
- CBR, or no Xing (`finalize`, `:96-107`): `round(mpegSize / frameSize) × spf / rate`, where `mpegSize` is the file
  size minus the first-frame offset minus 128 if ID3v1 is present. It does not subtract APE or Lyrics3.
- **Don't copy**: line 458 sets `duration = lame.extended.music_length / 1000`. The LAME tag's "music length" is a
  **byte** count (per the cited mp3infotag spec), not milliseconds. It is masked whenever a Xing `numFrames` follows
  (`:462`).
  - Neither music-metadata nor lofty nor ATL applies LAME encoder delay or padding. **Wavee already does**:
    `Mp3Tag.TryProbe`, `Playback.Audio.cs:2803-2866`.
- Ogg (`mm/ogg/OggParser.ts:178-209`):
  - Without `duration`, it stops after page 12 once metadata is complete.
  - With it, Opus does a tail lookup (`peekOpusTail`), but Vorbis **reads forward through every page**
    (`durationOnLastPage`). That is the slow path; lofty's and ATL's seek-from-end is the right one.
- Cover choice: `selectCover` prefers a picture typed `front`/`cover`/`cover (front)`, else the first
  (`mm/core.ts:158-166`).

---

## 4. spotifast (Rust): the #240 reference

spotifast does **not** match local files itself. It calls librespot's lookup and keys on the URI string:

```rust
// sf/backend.rs:52-59
fn local_file_uris(folder: &Path) -> HashMap<String, PathBuf> {
    librespot_playback::local_file::create_local_file_lookup(&[folder.to_path_buf()])
        .iter().map(|(uri, path)| (uri.to_uri(), path.to_path_buf())).collect()
}
// sf/app.rs:15360-15367: a playlist row's uri is parsed and re-serialised the same way, then looked up exactly
pub(crate) fn local_file_key(uri: &str) -> String { /* SpotifyUri::from_uri(uri).to_uri() */ }
```

### 4.1 Where the matching code is

The librespot crates come from the fork `crmne/librespot` at rev `8ba22cd4` (`sf/../Cargo.toml:392-398`; "the local
file lookup made public (#1830)", `:381`). That source is not in the clone. I read
`playback/src/local_file.rs` at that rev from raw.githubusercontent (one fetch). What it does:

- **Walk**: recursive `read_dir` over the given roots. `is_dir()` follows symlinks and there is **no cycle guard**.
  Extensions are `mp3`, `mp4`, `m4p` and `flac`, compared case-insensitively. A `read_dir` error aborts that
  subtree. The order is unsorted.
- **Tags**: Symphonia probe, reading only `StandardTagKey::Artist`, `Album` and `TrackTitle`.
  - **No album artist.**
  - A repeated key keeps the **last** value (`replace`), so a FLAC with two `ARTIST` fields keys on the second.
  - A missing tag becomes an **empty** string. The file-name-as-title fallback is **not** applied (per the fetched
    source), so untagged files that the official client lists under their file name never match.
- **Seconds**: `time_base.calc_time(n_frames).seconds`, **truncated**. The file is an error if `n_frames` is
  unknown.
- **URI**: each part is `form_urlencoded::byte_serialize(utf8)`. There is no trim, case fold or Unicode
  normalisation.
- **Match**: a `HashMap` with exact equality, so no tolerance at all, not even 1 s. Two files with the same URI
  overwrite each other, and the last visited wins, which is nondeterministic.
- spotifast's own test confirms the exactness:
  - `The%20Artist` versus `The+Artist` for the same parts is **not** playable (`sf/app.rs:31703-31723`).
  - The comment there reads "The player matches the parts as the file's tags spell them".

### 4.2 Indexing and caching

- **No persistent index.** `ScanLocalFiles` runs a full walk and probe on `spawn_blocking` at every start
  (`sf/app.rs:1540-1541`) and when the folder changes (`:12081-12091`, which also restarts the engine).
- One folder only. There is no watcher and no incremental pass.
- The result is an `Arc<HashMap<uri, path>>` held in memory (`sf/app.rs:778-783`, `sf/backend.rs:2350-2364`).
- **Cover art is read lazily, per row**:
  - `local_art(uri)` returns a `spotifast-local-art:<path>` URL (`sf/app.rs:12120-12123`).
  - The image loader then re-probes the file with Symphonia, keeping the **front cover over any other picture**
    (`sf/local_tags.rs:44-89`).
  - It caps the size (`MAX_ART_BYTES`), writes the bytes to the disk image cache, and remembers files with no cover
    in a `coverless` set so they are not probed again (`sf/images.rs:515-540`).
- Lyrics: a sidecar `.lrc` first, then `USLT` or the Vorbis `LYRICS` field (`sf/local_tags.rs:97-115`).

### 4.3 What our ladder (§3.4) has that spotifast lacks, and the reverse

- Ours is strictly more tolerant and more correct:
  - decoded comparison (spotifast fails on `+` versus `%20`);
  - the file-name title rule;
  - the second exact key for joined multi-value artists;
  - ±1/±2 s tolerance;
  - a loose tier;
  - a deterministic `Pick` for duplicates;
  - a persistent incremental index with a watcher.
- Worth taking from spotifast:
  - **The front-cover preference** (also music-metadata's `selectCover`).
  - **The "coverless" memo**, so a file without art is never re-read for art.
  - **Art cached separately from the index**, on first display rather than at scan.
- Optional: one more exact-map key built from the **last** artist value, which is librespot's rule. It costs one
  string per multi-artist file and covers playlists made by librespot-based clients. This is the only place
  spotifast's behaviour could produce a URI that our keys miss.
- Not worth copying: the `m4p` extension (FairPlay, so unplayable), the symlink walk without a cycle guard, and
  full Symphonia probes for metadata.

---

## 5. Where tags and pictures live, and the minimum bytes per field

"Touch" means bytes that must be read. Everything else is reached by seeking. "Head" means one read at offset 0.
**Recommended I/O per file: one `RandomAccess.Read` of the head (64 KiB, into a pooled buffer), plus at most one
tail read (≤ 64 KiB) and one or two seeks.** The `(size, mtime)` check happens before any of this.

| Format | Title / artist / album / album artist / track | Duration | Embedded picture | Minimum touch, typical file |
|---|---|---|---|---|
| **MP3, ID3v2.3/2.4** | ID3v2 at offset 0: a 10-byte header (syncsafe size, flags: unsync, ext header, footer), then frames with a 10-byte header (`TIT2` `TPE1` `TALB` `TPE2` `TRCK`; also `TYER`/`TDRC`, `TLEN`). Text encodings 0 = Latin-1, 1 = UTF-16+BOM, 2 = UTF-16BE (v2.4), 3 = UTF-8 (v2.4); multiple values are NUL-separated in v2.4. v2.4 frame sizes are syncsafe, v2.3 sizes are plain. | First MPEG frame after the tag (4-byte header), then Xing/Info at 4 + side info (MPEG1 17/32, MPEG2 9/17 bytes), or VBRI at +36. Xing gives `frames` → `frames × spf / rate`; LAME delay/padding at Xing + 21 (Wavee `Mp3Tag`). Without them: (file − ID3v2 − tail tags) × 8 / bitrate, an estimate. | `APIC` frame: encoding, mime, picture type (3 = front), description, then data. Record **offset + length** and never read the data at scan. | 10 + every frame header up to the last wanted text frame + those payloads (usually < 4 KiB, text frames first) + about 200 bytes at the first audio frame. If APIC comes before the text frames, seek past it using the frame size. **Tag-level unsync in v2.3** (rare) shifts every boundary: read the whole tag and de-unsync it. A v2.4 frame with the unsync flag: decode that frame only. |
| **MP3, ID3v2.2** | 6-byte frame headers, 24-bit sizes: `TT2` `TP1` `TAL` `TP2` `TRK` `TYE` | as above | `PIC` with a 3-char image format (`JPG`/`PNG`) instead of a mime | as above |
| **MP3, ID3v1 / APE / Lyrics3** | ID3v1 is the last 128 bytes: `TAG`, title 30, artist 30, album 30, year 4, comment 30 (v1.1: byte 125 = 0, byte 126 = track), genre. No album artist. The APE v2 footer is the 32 bytes before ID3v1/Lyrics3 (`APETAGEX`). Check `End-131` for an `APETAGEX` collision (lofty). | n/a | APE `Cover Art (Front)` is a binary item (rare) | **Tail: one 160-byte read at `End-160`** covers ID3v1 + the APE footer. Needed only as an ID3v1 fallback and to size the CBR estimate. |
| **FLAC** | Optional illegal ID3v2 first (skip it). Then `fLaC`, then metadata blocks with a 4-byte header (last flag, type, 24-bit length). VORBIS_COMMENT (type 4) is little-endian: vendor, count, `KEY=value` UTF-8 entries: `TITLE` `ARTIST` `ALBUM` `ALBUMARTIST` (also `ALBUM ARTIST`) `TRACKNUMBER`. | STREAMINFO (type 0, always first, 34 bytes): rate (20 bit) and total samples (36 bit). If total = 0, the length is unknown. | PICTURE (type 6): picture type, mime, desc, w/h/depth/colours, then data length and data. Record the offset. | 4 + 38 (STREAMINFO) + 4 per block header + the comment block. Seek past SEEKTABLE, PADDING, APPLICATION and PICTURE. Usually within the 64 KiB head **unless a PICTURE comes before the comments**; then it is one seek. |
| **Ogg Vorbis / Opus** | Page 1 holds the ID header (Vorbis rate at +12; `OpusHead`: pre-skip at +10, output rate). The comment packet (`\x03vorbis` / `OpusTags`) starts on page 2 and **may span pages**. Same `KEY=value` as FLAC. | **Last page's granule position**: search `OggS` backwards from the end (the last page is ≤ 65 307 bytes). Vorbis: granule ÷ rate. Opus: (granule − pre-skip) ÷ 48 000. | `METADATA_BLOCK_PICTURE` is **base64** of a FLAC PICTURE inside the comment, which makes it the costly case (`COVERART` is legacy). Record the field's offset and length across pages, and decode at art time. | Head: page 1 + page 2... up to the last wanted comment. Comments are length-prefixed, so skip a picture field by summing lacing values across page headers (27 + segment table each) without reading the bodies. Tail: **one read of up to 64 KiB at the end**. |
| **MP4 / M4A** | Top-level boxes: `ftyp`, `moov` (**may sit after `mdat`**; then seek using the `mdat` size, which can be 64-bit `size == 1`). `moov/udta/meta` is a full box (4 bytes version/flags; some files omit them) then `ilst`: `©nam` `©ART` `©alb` `aART` `trkn` (data: 2 pad, 2 number, 2 total), each item's `data` box with type (1 = UTF-8). `----` freeform items carry `mean`/`name`. | `moov/trak/mdia/mdhd`: timescale + duration (v0 32-bit, v1 64-bit). Prefer it over `mvhd`. An `elst` and iTunSMPB give gapless; optional. | `ilst/covr/data` (type 13 = JPEG, 14 = PNG; several `data` boxes = several images). Record the offset. | Box headers (8/16 bytes) to find `moov` (1 seek if it is at the end), then descend **only** `trak/mdia/mdhd` and `udta/meta/ilst`, seeking past `stbl` (the big sample tables) and `covr` data. Typically < 8 KiB touched. |
| **WAV** | RIFF/WAVE chunks (8-byte header, even padding). `LIST`/`INFO`: `INAM` title, `IART` artist, `IPRD` album, `ITRK`/`IPRT` track; no album artist. An `id3 `/`ID3 ` chunk carries a full ID3v2 (often **after** `data`). | `fmt ` (byte rate) and the `data` size → size ÷ byte rate. RF64/`ds64` when the size is 0xFFFFFFFF. | APIC inside the `id3 ` chunk | Chunk headers only, seeking past `data`. A trailing `id3 `/`LIST` costs one seek. |
| **AIFF / AIFC** | Big-endian FORM chunks: `NAME`, `AUTH`, `ANNO`, `(c) `; an `ID3 ` chunk carries ID3v2 (most taggers write that) | `COMM`: numSampleFrames + an 80-bit extended sample rate | APIC inside `ID3 ` | Chunk headers, seeking past `SSND` |
| **WMA / ASF** | The Header Object (GUID 16 + size 8 + count 4 + 2) at offset 0 holds: Content Description (lengths, then UTF-16LE title/author...); Extended Content Description (`WM/AlbumTitle`, `WM/AlbumArtist`, `WM/TrackNumber` as DWORD or string); Header Extension, then Metadata Library (large or extra pictures). | File Properties: play duration (100 ns) − preroll (ms) | `WM/Picture`: type byte, 32-bit length, mime, desc (UTF-16), data | The Header Object alone: its size is known after 30 bytes. Skip `WM/Picture` payloads by length. Wavee has no WMA decoder, so this only matters for counting unplayable files. |
| **ADTS .aac** | An optional ID3v2 at the start | No header: size ÷ (average frame bytes × rate / 1024) from the first few frames, an estimate | APIC in that ID3v2 | Head only |

Notes:

- Unplayable formats (WAV, Opus, WMA per plan D7) still deserve title, artist and length, because Settings counts
  them and the match log names them. The WAV, Opus and AIFF readers above are small.
- `TLEN` (ID3) is often wrong; use it only as a last resort.
- **Duration precision versus the URI**: the official client's seconds are truncated (plan §3.4). Gapless-trimmed
  length (Wavee's `Mp3Tag` total) and raw `frames × spf` differ by about 50 ms, so they can disagree by 1 s at a
  boundary. The ±1 s tier-1 tolerance absorbs this.
- Caveat on the plan's row "`Mp3Tag.TryProbe` total samples when a Xing/Info block is there":
  - `TryProbe` **returns false when the LAME delay/padding is absent** (`Playback.Audio.cs:2862`), so a Xing
    file without a LAME extension would fall to the bitrate estimate.
  - It only looks for sync in the first 192 bytes after the tag (`:2827`).
  - It ignores VBRI.
  - The scan should keep its own small Xing/VBRI frame-count read and use `TryProbe` only for the gapless trim.

---

## 6. Correctness traps the readers taught

- **ID3 encoding 0 is Latin-1 by spec, but often a local code page** (CP1251 or CP1252 punctuation at 0x80-0x9F,
  Shift-JIS, GBK). All three libraries decode it as Latin-1.
  - Matching compares against the official client's decoded text, and how it decodes these files is unknown.
  - Cheap hedge: decode encoding 0 as **Windows-1252** (a 32-entry table over Latin-1, no CodePages package), and
    log a counter of files with high bytes in encoding-0 frames.
- **Multi-value artists**: ID3v2.4 uses NUL separators; v2.3 writers use `/` or `;` by convention (ATL splits v2.3
  on `/` when `ID3v2_separatev2v3Values`, `Settings.cs:189`); Vorbis repeats the `ARTIST` key; MP4 uses one string.
  Store the first, the last (the librespot rule) and the joined form, per plan §3.4.
- **Duplicate or embedded ID3v2**: merge frames (lofty, `mpeg/read.rs:53-59`); ID3v2 can be buried in junk.
- **The `APETAGEX` / `TAG` collision at the tail** (lofty, `id3/mod.rs:188-192`).
- **InvariantGlobalization**: Wavee sets `InvariantGlobalization=true` (`WaveeMusic/Directory.Build.props:13`).
  The plan's `Exact`/`Loose` keys use NFKC/NFD via `string.Normalize` and `ToUpperInvariant`. Confirm `Normalize`
  behaves in this mode on the NativeAOT build (one log probe) before relying on it. It is outside this study, but it
  sits on the same path.

---

## 7. Recommendation: write our own small readers (the plan's §3.5), extended, borrowing ideas and no code

| | atldotnet as a dependency | Port atldotnet parts | Own small readers |
|---|---|---|---|
| Licence | MIT, but pulls in `Ude.NetStandard` (probably MPL/GPL/LGPL) | MIT, with the notice kept | ours |
| AOT / warnings-as-errors | unannotated `net6.0`; `Enum.GetValues(Type)` (IL3050) would fail the Release publish; Regex interpreter | fixable while porting | Span-based, no reflection, no Regex |
| Speed | multiple opens, a 64 KiB `byte[]` per reader, per-frame strings, the whole `stsz`, Ogg covers decoded every scan, covers by a second full parse | the read paths are tangled with write-zone code, so a port is a rewrite | one open, one pooled head read, one optional tail read, zero-copy span parsing, covers never read at scan |
| Art | no offset API, all pictures loaded | same | store `(offset, length, mime, encoding: raw / unsync / base64)` per file and read the front cover with one ranged read |
| Coverage | everything | what we port | MP3 (ID3v2.2-2.4 + v1), FLAC, Ogg Vorbis/Opus, M4A, AAC, WAV/AIFF (ID3 chunk + INFO), with WMA header only for counting. Roughly 1,200 to 1,500 lines, reusing `Flac.ParseHeaders`, `VorbisComments`, `Mp4`, `Mp3Tag` |

**Decision: write our own.** The reasons, in order:

1. **Speed is in the I/O shape, and none of the libraries has the right shape for an indexer.** All of them read the
   cover bytes, or the whole tag or ilst, even when told to skip covers: ATL for unsync and Ogg, lofty through
   `io::copy` to sink and whole-tag `Vec`s, music-metadata through the whole-tag `readBuffer`.
   - With covers of 100 KB to 2 MB per file, reading them is most of the bytes of a scan.
   - Our readers seek past them and record where they are.
2. **AOT and warnings-as-errors.** ATL would need suppressions or a fork, plus a charset-detector package we never
   call.
3. **Licence hygiene.** Own code avoids the Ude question entirely. Lofty and music-metadata are MIT/Apache and fine
   to learn from.
4. **Correctness where we need it** (matching, §3.4) comes from a few rules the libraries show. Take these ideas:
   - **lofty**: the tail order ID3v1 → Lyrics3 → APE with the `APETAGEX` check; the `BestAttempt` policy (a bad
     frame empties a field, never fails the file); merging duplicate ID3v2 tags; Ogg last-page `rfind`; Opus
     `pre_skip`.
   - **music-metadata**: VBRI at the fixed offset 36 with no seek-table walk; stopping MP3 parsing after the first
     frame; the CBR estimate from file size minus tags.
   - **spotifast**: front cover first; a coverless memo; art read and cached on first display.
   - **ATL**: the 30% frame-search cap is a warning. Cap the sync search instead (lofty's `max_junk_bytes`, 1 KiB,
     plus one confirmation frame).

Shape for "crazy fast":

- `File.OpenHandle` + `RandomAccess.Read(handle, span, offset)` into an `ArrayPool` 64 KiB buffer. No `FileStream`,
  no `BinaryReader`, no per-frame strings. Decode only the five or six wanted fields.
- Enumerate with `FileSystemEnumerable`, which hands back size and mtime with no extra stat per file. Skip
  unchanged `(size, mtime)` before opening.
- Pictures: the index stores the front cover's location. The UI reads it on first display, decodes a thumbnail and
  caches it per album (one decode for all tracks on that album). Files without a picture are remembered as such.
  Folder art (`cover.jpg`, `folder.jpg`) is a cheap fallback, since the folder listing is already in hand.
- Open question for the owner: the plan's single `BelowNormal` thread suits HDDs and network shares. On an SSD, a
  first scan of 50,000 files is bound by open() latency, and 2 to 4 workers would cut it several times over. Worth
  deciding per drive (`DriveType.Fixed` plus a seek-penalty query) or just capping at 2.
