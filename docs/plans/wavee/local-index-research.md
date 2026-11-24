# Local music index for Wavee 0.4: what the best players do, and the design we take

Research for #240 (local files), 2026-11-23. The owner's brief: indexing must be **very fast**, handle **deleted,
modified, moved and renamed** files well, and **extract album art** (a must). Ten community projects were cloned and
read (nothing was built or run), and the Windows file-system APIs were researched. The five detailed reports sit
beside this file:

| Report | Projects | Strongest lesson |
|---|---|---|
| [`go-navidrome-gonic.md`](go-navidrome-gonic.md) | Navidrome, gonic (Go servers) | A **per-folder fingerprint** skips unchanged folders before any lookup; a **missing** state, never a delete; moves paired by tags |
| [`cpp-mpd-strawberry.md`](cpp-mpd-strawberry.md) | MPD, Strawberry (C++ players) | Compare times **exactly** (MPD's Windows bug); **unavailable + 60-day grace** (Strawberry); equal (size, mtime) = a free move |
| [`mixxx-beets.md`](mixxx-beets.md) | Mixxx, beets | **Tombstones** cleared only after a completed scan; **relocation** by file name + duration; **cover rules** and a cover cache keyed by a strong hash |
| [`tag-readers.md`](tag-readers.md) | atldotnet, lofty-rs, music-metadata, spotifast | **Write our own readers**: every library reads the cover bytes even when told to skip them; record the cover's location instead |
| [`windows-filesystem.md`](windows-filesystem.md) | Win32 / NT docs and benchmarks | **`NtQueryDirectoryFile`** returns size, times and a 128-bit file id in batches without opening files; the **unprivileged USN journal**; OneDrive **placeholders must never be opened** |

The clones are in `C:\WAVEE\scratch\research\localindex\` (read-only reference; no code is copied into Wavee).

## The ten rules (what every good scanner agrees on, and where they differ)

1. **Never open a file to learn that it did not change.** Everything needed (size, times, id, attributes) comes from
   the directory listing. All ten projects do this; MPD on Windows stats twice, Strawberry lists twice: avoid.
2. **Compare exactly, never "newer than".** Navidrome and gonic use "mtime after", MPD truncated to seconds and paid
   with a Windows bug where every file looked modified. Wavee: `(Size, LastWriteTicks, ChangeTicks) != stored`.
3. **Skip whole folders.** Navidrome's fingerprint (a hash over the sorted name, size and mtime of every entry in a
   folder) lets a warm rescan of an unchanged 50k library do one comparison per folder. A folder's own mtime is
   **not** enough (Strawberry admits it misses in-place retags).
4. **Missing is a state, not a delete.** Navidrome (`missing`), Strawberry (`unavailable` + `lastseen`, purged after
   60 days unless a playlist uses it), Mixxx (`fs_deleted`) all keep the row. gonic and beets delete, and lose play
   counts and playlist links. Never purge after a partial, cancelled or failed walk, and never for an offline root.
5. **A move is detected, not re-imported.** In order of strength: the same NTFS file id on the same volume; the same
   (size, mtime) at a new path in the same scan (Strawberry, free); the same tags-derived key (Navidrome); the same
   file name with a duration within 1 s, longest shared path tail wins (Mixxx). The old identity survives.
6. **A watcher is a hint, the scan is the truth.** No project trusts its watcher alone. Overflow means "rescan the
   root", which a fingerprinted rescan makes cheap. Debounce: 2-5 s in the projects; Windows SMB needs at least 10 s.
7. **Read tags in one or two reads, and never read the picture.** A cover is 100 KB to 2 MB, most of a tag's bytes.
   Every library studied still reads it. Record its offset and length; read it when it is first shown.
8. **Covers: embedded front first, then a fixed folder-name order.** Mixxx and Navidrome agree on fixed names
   (`cover`, `folder`, `front`, `album`, the album title) and never decode candidates to choose (Strawberry does,
   and is slow). Navidrome also looks in the parent of `CD1`/`Disc 2` folders.
9. **Cache covers by a hash of their bytes**, thumbnails only, failures remembered (Mixxx moved from a 16-bit hash to
   SHA-256 after wrong covers appeared; Navidrome keys by content hash so a changed image simply gets a new key).
10. **Save as you go.** Strawberry throws a whole scan away when it is stopped. Navidrome writes a folder's
    fingerprint only together with its files, so an interrupted scan resumes; and never writes it when one file
    failed (that file would never be retried).

## The design for Wavee

### Enumeration: our own NT walker
- `NtQueryDirectoryFile` through `LibraryImport` (NativeAOT-clean), class `FileIdExtdDirectoryInformation` (128-bit
  file id, `ChangeTime`, reparse tag) with a per-volume fallback to `FileIdBothDirectoryInformation`; 64 KB pooled
  buffers; subdirectories opened relative to their parent handle. .NET's `FileSystemEnumerable` uses the same call but
  a 4 KB buffer and drops the file id and `ChangeTime`.
- Skip `$RECYCLE.BIN`, `System Volume Information`, hidden and system folders, and reparse-point **directories**
  (never follow junctions or symlinks). Do **not** skip reparse-point **files**: OneDrive placeholders are reparse
  points (the plan's `AttributesToSkip = ReparsePoint` would hide cloud music).
- Workers per root by medium (seek-penalty query, `DriveType`, UNC): SSD 8, HDD 1-2, USB 2-4, SMB 4-8. Past 8 the
  measured throughput drops.

### What the index stores
- **Folder record:** root id, relative path, file id, fingerprint (64-bit hash over the sorted `(name, size,
  LastWriteTicks, ChangeTicks)` of its audio and image entries), the folder images present (from the same listing),
  scan state.
- **File record:** a stable `LocalId` (64-bit, assigned at first sight, never reused), root id, relative path,
  `(volumeSerial, fileId128)`, `Size`, `LastWriteTicks`, `ChangeTicks` (dropped from the key on FAT/exFAT and SMB
  servers that report 0), attributes (cloud bits), format, duration and its precision (exact or estimated), tags
  (title, artist values, album, album artist, track, disc, year), the **cover location** `(offset, length, mime,
  encoding raw | unsync | base64, picture type)`, `State` (present | missing since T | cloud-only), `LastSeen`.
- **Format on disk:** a compact versioned binary file (length-prefixed UTF-8, a string table for repeated artist and
  album names) beside `audiofiles.tsv`, written temp-file + atomic replace, plus an **append-only change log** so a
  one-file change does not rewrite 10 MB; compacted when idle. Still a cache outside the entity store (the store is
  reaped on every schema change). About 8-10 MB in RAM at 50k tracks.

### Change detection, deletes and moves
1. Load the index (background; the UI is usable at once from the saved state).
2. **Catch up:** on local NTFS, read the unprivileged USN journal (`FSCTL_READ_UNPRIVILEGED_USN_JOURNAL`, Windows 10
   1607+, no admin) from the stored `(JournalId, NextUsn)`: each record's parent id marks a folder dirty. Journal id
   changed, entries purged, or the call refused: a full warm walk instead. The call is undocumented, so it sits
   behind a capability probe and the walker is always the correct fallback.
3. **Walk** the dirty folders (or all of them): list, compute the fingerprint, compare. Equal: done, nothing read.
   Different: diff the entries; unchanged files keep their record; changed, new or vanished files go to step 4.
4. **Pair vanished with new** before reading anything: same volume + file id (NTFS/ReFS) = moved or renamed; else the
   same `(size, LastWriteTicks, extension)` = moved (FAT/exFAT/SMB). A pair keeps its `LocalId` and tags. **Same path
   with a new file id** = a safe-save by a tag editor: a modification.
5. **Read** the new and changed files (tag readers below). Then a second pairing pass for files that changed *and*
   moved: the same tags-derived key, else the same file name with a duration within 1 s, longest shared path tail
   first (Mixxx).
6. **Mark** what is still unpaired as `missing since now`, only when its folder was listed successfully and its root is
   online. An offline root marks nothing (Settings shows "Drive offline"). Missing records are purged after 60 days
   unless the Local Files list or a playlist row still points at them; a reappearing file at the same path comes back
   without a tag read when its `(size, times)` are unchanged.
7. **Commit per folder:** a folder's fingerprint is written only together with its file records, and only when every
   file in it was read; an interrupted scan resumes where it stopped.

### Live updates
- One `ReadDirectoryChangesExW` (extended info: file id, size, times) per root, recursive; events only mark the
  nearest existing folder dirty. Debounce 300-500 ms local, 10 s SMB (the redirector caches directory listings for
  10 s). Overflow or an error: reopen with backoff and rescan the root (cheap with fingerprints).
- exFAT/FAT/USB: `ReadDirectoryChangesW` and a warm rescan on device arrival. SMB: the watcher as a hint, a rescan
  on reconnect and resume, and a quiet reconcile every 30-60 minutes. Resume from sleep: journal catch-up or rescan.
- Open watch handles block "safe removal" of a USB drive: release them on `DBT_DEVICEQUERYREMOVE`.

### Cloud files (OneDrive, Dropbox)
- `RtlSetProcessPlaceholderCompatibilityMode(PHCM_EXPOSE_PLACEHOLDERS)` at start, so placeholders show their real
  attributes. A file with `RECALL_ON_DATA_ACCESS | RECALL_ON_OPEN | OFFLINE` is **never opened by the scanner**
  (opening downloads it). It is indexed from its path, size and times: title from the file name, length unknown,
  state `cloud-only`. When it is downloaded (played, or pinned "Always keep on this device") the attribute change
  marks it dirty and its tags are read. An opt-in setting may later allow "download to read tags".

### Tag readers (our own, about 1,200-1,500 lines)
- No library fits: atldotnet is not AOT-clean (IL3050 under warnings-as-errors), pulls a charset-detector package of
  unclear licence, reopens files and reads covers; lofty and music-metadata read covers too. We take their **ideas**
  only (lofty's tail order ID3v1 -> Lyrics3 -> APE with the `APETAGEX` collision check and its best-attempt policy;
  music-metadata's VBRI at offset 36 and CBR estimate; Ogg last-page reverse search; Opus pre-skip).
- Per file: `File.OpenHandle` + `RandomAccess.Read` of a **64 KB head** into a pooled buffer, at most **one tail read**
  (160 bytes for MP3 ID3v1/APE, up to 64 KB for the Ogg last page), one or two seeks (an MP4 `moov` after `mdat`, a
  FLAC picture before the comments). Zero-copy span parsing of only the wanted fields. The per-format table is in
  `tag-readers.md` §5.
- A bad field empties that field, never fails the file. A file that fails keeps its folder dirty (retried next scan).
- ID3 text encoding 0 decoded as **Windows-1252** (a 32-entry table over Latin-1; no CodePages package), with a
  counter of files that used high bytes.
- MP3 length: our own Xing/VBRI frame-count read; `Mp3Tag.TryProbe` only for the gapless trim (it returns false
  without the LAME fields and ignores VBRI). Estimated lengths are flagged.
- Concurrency: SSD 8-16 reads in flight, HDD 1, USB 2-4, SMB 8-16. Handles are closed on a small pool: Defender makes
  `CloseHandle` cost 1-10 ms. No memory-mapped reads (a USB or SMB read error becomes a crash).

### Album art (in 0.4)
- **At scan:** record the embedded front cover's location (picture type 3, else the first picture) and the folder's
  image names from the listing. No picture byte is read and nothing is decoded.
- **Resolution order per track:** embedded front cover -> a folder image by fixed name (`cover`, `folder`, `front`,
  `album`, the album title; jpg/jpeg/png/webp) -> the parent folder's, when the folder looks like a disc folder
  (`CD1`, `Disc 2`) -> the only image in the folder -> none (the existing placeholder).
- **On first display** (a queue off the UI thread, duplicate requests merged): one ranged read of the picture bytes,
  base64 or unsync decoded when needed, hashed (xxHash3-64 over the encoded bytes) and written once to
  `Cache\art\<hash>.<ext>` **without re-encoding**. The row's art source is then that file's `file:///` path, which
  the engine's image pipeline already loads and downsizes (`DefaultImageFetcher` reads plain paths; the Windows media
  controls and toasts take `file:///` too). Tracks on one album share one file. A failure or "no art" is remembered
  per `(LocalId, size, times)` so it is not retried every frame.
- **Warm-up:** after a scan, an idle-priority pass extracts the covers of the first screenfuls of Local Files and of
  albums in recently played playlists, so the list rarely shows a placeholder.
- **Invalidation:** a changed file gets new times, so its cover location is re-read; a changed image gets a new hash.
  A sweep after each scan deletes cache files no record points at.
- An engine seam (a custom image source that reads bytes on demand) is not needed for 0.4; the cache file route
  works with every consumer.

### Instrumentation and gates
- One summary log line per scan: folders listed, folders skipped by fingerprint, files read, bytes read, moves paired,
  missing marked, cloud-only, errors, milliseconds per phase.
- A Diagnostics section and an inspector verb that run a scan of a fixture folder and print those numbers, so the
  targets below are checked on real machines, not assumed.

### Targets (50k files, ~4-5k album folders; extrapolated from published measurements, to be measured)

| Scenario | NVMe/SATA SSD | HDD | SMB NAS (1 GbE) | USB 3 exFAT |
|---|---|---|---|---|
| Cold full scan (list + tag reads) | <= 20 s | 3-6 min | 30-90 s | 1-3 min |
| Warm rescan, nothing changed | <= 300 ms | <= 1 s cached, else 10-30 s | 2-5 s | 1-3 s |
| Start-up, journal intact, nothing changed | <= 50 ms | <= 50 ms | n/a | n/a |
| One file changed -> row updated | <= 1 s | <= 1.5 s | 10-15 s | <= 1.5 s |
| First tracks visible during a cold scan | < 2 s | < 2 s | < 2 s | < 2 s |

## Checks before building (none of these is verified yet)
1. `FSCTL_READ_UNPRIVILEGED_USN_JOURNAL` as a standard user on the owner's Windows 11 build, packaged and unpackaged.
2. `string.Normalize` (NFKC/NFD for the matching keys) under `InvariantGlobalization=true` on the NativeAOT build.
3. `ChangeTime` behaviour on exFAT, FAT32 and the owner's NAS (Samba): reported, zero, or a mirror of mtime.
4. The cost of Defender on first open of 50k files on the owner's machine (decides the handle-close pool size).
5. MSIX: AppData redirection for the cache folder in the packaged build.
