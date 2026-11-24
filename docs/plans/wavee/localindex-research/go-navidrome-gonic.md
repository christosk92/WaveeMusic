# Local-index research: Navidrome and gonic (Go music servers)

Read-only study for #240 (`handoff/request-plans/240-local-playlist-files-implementation.md`, sections 3.3 to 3.7).
Nothing was built or run. Paths below are relative to:

- **ND** = `C:\WAVEE\scratch\research\localindex\navidrome_navidrome` (commit `06df4a5`, 2026-11-22; the post-0.55 scanner)
- **GN** = `C:\WAVEE\scratch\research\localindex\sentriz_gonic` (commit `5ac9a8e`, 2026-11-16, version 0.23.0)

Short version: **Navidrome is the model to copy** for change detection (one fingerprint per folder, compared against a
map loaded in one query), for deletes (a `missing` flag, never an immediate delete) and for moves (re-linking missing
rows to new rows by a tag-derived id inside the same scan). **gonic is the cautionary tale**: simple and readable, but
an unseen track is deleted at the end of every scan, cascading stars and play counts, so an unmounted folder wipes
the library.

---

## 1. Enumeration

### Navidrome

- **One walker goroutine, depth-first, post-order** (children are emitted before their parent), entries **sorted by
  name** per folder. `ND scanner/walk_dir_tree.go:75-105` (walkFolder), `:186-207` (fullReadDir sorts).
- Each folder becomes one `folderEntry` with three maps: audio, image, playlist files (`walk_dir_tree.go:168-176`).
  The walk is the producer of a pipeline; tag reading runs in a separate stage (section 6).
- `fullReadDir` survives a directory that keeps failing mid-read (it bails when the same error repeats; issue #1164),
  returning what it read so far (`walk_dir_tree.go:182-207`).
- **Symlinks:** followed by default (`scanner.followsymlinks=true`, `ND conf/configuration.go:1063`); a link is
  classified by its final target's extension; loops cut at 40 hops (`walk_dir_tree.go:246-297`).
- **Ignore rules** (`walk_dir_tree.go:313-351`): a fixed blocklist (`$RECYCLE.BIN`, `#snapshot`, `@Recycle`, `@eaDir`,
  `@Recently-Snapshot`, `.git`, `.streams`, `lost+found`, case-insensitive); dot files always; dot folders unless
  `IgnoreDotFolders=false` (note the careful rule: `..foo` / `...Album` are *not* hidden). Plus gitignore-style
  `.ndignore` files, held as a **stack pushed and popped while descending** (`ND scanner/ignore_checker.go:18-52`),
  and optional `.nomedia` (`respectnomedia`, default false).
- **Unreadable child dirs** are skipped with a warning (`isDirReadable`, `walk_dir_tree.go:299-311`).
- **Extensions:** anything whose MIME type is `audio/*` except playlists (`ND model/file_types.go`, mime table in
  `ND resources/mime_types.yaml`); images are `image/*`; playlists `.m3u .m3u8 .nsp`.

```go
// ND scanner/walk_dir_tree.go:149-176 (trimmed)
if isIgnoredEntry(entry.Name(), isDir) { continue }
if isDir && isDirReadable(ctx, job.fs, entryPath) {
    children = append(children, entryPath); folder.numSubFolders++
} else {
    fileInfo, err := entry.Info()
    if fileInfo.ModTime().After(folder.modTime) { folder.modTime = fileInfo.ModTime() }
    switch {
    case model.IsAudioFile(name):     folder.audioFiles[entry.Name()] = entry
    case model.IsValidPlaylist(name): folder.playlistFiles[entry.Name()] = entry
    case model.IsImageFile(name):     folder.imageFiles[entry.Name()] = entry
        folder.imagesUpdatedAt = utils.TimeNewest(folder.imagesUpdatedAt, fileInfo.ModTime(), folder.modTime)
    }
}
```

### gonic

- Plain `filepath.WalkDir` per music dir, single-threaded, lexical order (`GN scanner/scanner.go:96-103`). The walk
  callback only acts on directories; each directory is then **read a second time** with `os.ReadDir` in `scanDir`
  (`scanner.go:281-317`).
- Symlinked dirs are followed by re-walking the resolved target (`symWalk`, `scanner.go:1152-1158`), no loop guard.
- Ignore: a `.gonicignore` marker file skips the folder and its subtree (`scanner.go:37-38, 261-264, 1147-1150`), an
  optional regex `excludePattern` (`:266-269, 296-299`), macOS `._` files (`:304-307`).
- Extensions (case-insensitive): `.mp3 .flac .aac .m4a .m4b .ogg .opus .wma .wav .wv .ape`
  (`GN tags/taglib/taglib.go:17-23`). Covers are recognised by name via `wrtag/coverparse` (`scanner.go:309-312`).

---

## 2. Change detection on a rescan

### Navidrome: one fingerprint per folder (the key idea)

At scan start the job loads **every folder's previous fingerprint in one query** into a map
(`GetFolderUpdateInfo`, `ND scanner/phase_1_folders.go:64-69`; `ND persistence/folder_repository.go:105-135`, which
returns only non-missing folders). The walker pops each visited folder out of that map
(`phase_1_folders.go:111-129`); whatever is left at the end was not seen (section 3).

The fingerprint is an MD5 over the folder mtime, counts, and **every audio, image and playlist file's name, size and
mtime**, sorted (`ND scanner/folder_entry.go:106-151`). It is stored in `folder.hash` (migration
`ND db/migrations/20250701010101_add_folder_hash.go`). The note at `:112` is a lesson: *"dropping it re-hashes every
folder"*. Changing the recipe forces a full re-read, so the recipe is frozen.

```go
// ND scanner/folder_entry.go:72-77
func (f *folderEntry) isOutdated() bool {
    if f.job.lib.FullScanInProgress && f.updTime.Before(f.job.lib.LastScanStartedAt) {
        return true
    }
    return f.prevHash != f.hash()
}
```

An unchanged folder is dropped **in the producer** (`phase_1_folders.go:185-201`): no DB query, no tag read, no
per-file compare. So a quick rescan of an unchanged library costs: one SELECT of all folders, a directory listing of
every folder (with size and mtime per file, which `ReadDir` already returns on Windows), and one MD5 per folder.

Inside a changed folder, files are compared **per file** against the DB rows of that folder only
(`phase_1_folders.go:230-267`): re-read when new, when the library is in a full scan, when `mtime > stored mtime`,
or when the row was missing.

```go
// ND scanner/phase_1_folders.go:250-266
for afPath, af := range entry.audioFiles {
    fullPath := path.Join(entry.path, afPath)
    dbTrack, foundInDB := dbTracks[fullPath]
    if !foundInDB || entry.job.lib.FullScanInProgress {
        filesToImport[fullPath] = dbTrack
    } else {
        info, err := af.Info()
        ...
        if info.ModTime().After(dbTrack.UpdatedAt) || dbTrack.Missing {
            filesToImport[fullPath] = dbTrack
        }
    }
    delete(dbTracks, fullPath)
}
```

Note the per-file test is `mtime After`, and size is not compared per file. A file restored from a backup with an
*older* mtime makes the folder outdated (the hash includes size and mtime) but is then **not** re-read.

- **Full scan** = re-read every tag (`FullScanInProgress`), which is what users run after an upgrade.
- **Resume:** `library.full_scan_in_progress` and `last_scan_started_at` (migration
  `20241026183640_support_new_scanner.go:35-38`). An interrupted full scan is restarted as a full scan
  (`ND scanner/scanner.go:123-141`); a folder already persisted after `LastScanStartedAt` is not re-read
  (`isOutdated` above). An interrupted quick scan resumes for free because a folder's hash is written in the same
  transaction as its tracks.
- **Speed:** no number in the repo. README only says "Handles very large music collections" (`ND README.md:55`).
  There is a benchmark harness (`scanner/scanner_benchmark_test.go`) but no recorded result.

### gonic

- No folder fingerprint. Every folder visit does `FirstOrCreate` on the parent album row, a find/save on the folder's
  album row and a `SELECT` of its tracks (`GN scanner/scanner.go:319-348`), then one `times.Stat` per audio file
  (`:359-381`). So an unchanged library still costs at least 3 DB round trips per folder, two directory reads, and
  one extra stat per file.
- Per-file test: `isFull || new || mtime.After(track.UpdatedAt)` (`scanner.go:372`). `UpdatedAt` is **gorm's write
  timestamp**, not the file's mtime, so it compares the file clock against the DB clock (fragile on network shares
  and with restored old files).
- **Claimed speed** (`GN README.md:17`): *"~50k tracks, initial scan takes about 10m, and about 6s after
  incrementally"*. The 0.19 changelog says moving to WASM taglib and WASM sqlite should improve scan speed
  (`GN CHANGELOG.md:175`).

---

## 3. Deleted files and folders

### Navidrome: a `missing` flag, kept forever by default

- Tracks left in a folder's DB map after the walk are **marked missing**, not deleted
  (`ND scanner/phase_1_folders.go:269-270, 444-460`; `ND persistence/mediafile_repository.go:460-475`).
- Folders never visited are marked missing **with all their tracks** in `finalize`, and only when the phase
  succeeded: *"A failed phase may not have walked every folder, and unvisited ones must not be marked missing"*
  (`phase_1_folders.go:520-547`).
- **Purge** is a setting: `scanner.purgemissing = never` by default, `always` or `full` (only after a full scan)
  (`ND conf/configuration.go:1066`; `ND scanner/phase_2_missing_tracks.go:349-378`). Users purge by hand from a
  "Missing files" admin page (`ND ui/src/missing/`; the warning text *"This will remove permanently any references
  to them, including their play counts and ratings"*, `ND ui/src/i18n/en.json:626`).
- **Offline library root:** `storage.FS()` stats the root and errors when it is gone (`ND core/storage/local/local.go:53-59`);
  the job for that library is not created and the library is marked failed (`phase_1_folders.go:39-46`), so nothing
  is marked missing. A watcher target folder that vanished between event and scan is skipped and handled as missing
  later (`walk_dir_tree.go:37-44`). Not handled: a root that exists but is **empty** (an unmounted mount point): every
  folder is then marked missing, but because nothing is deleted the rows come back when the drive returns.
- **Coming back:** a missing row whose file reappears at the same path is re-imported (`dbTrack.Missing`, above) and
  upserted **by path**, keeping its random id (`mediafile_repository.go:189-197`;
  `ND persistence/sql_base_repository.go:618-630`). So annotations (plays, stars, ratings), bookmarks and playlist
  rows, all keyed by media file id, survive.
- **Playlists** keep the rows of missing tracks and hide them on read (`ND persistence/playlist_repository.go:160-162`).
  GC only removes playlist rows whose media file is really gone (`ND persistence/persistence.go:263-275`).

### gonic: immediate delete

- At the end of every full walk, every track id not seen is deleted in chunks (`GN scanner/scanner.go:895-915`),
  same for albums (`:917-937`). Stars, ratings and plays are `ON DELETE CASCADE` on the track
  (`GN db/db.go:371-379, 548-552`), so they go with it.
- An unreadable or vanished music dir only appends an error (`scanCallback`, `scanner.go:246-249`), then the clean
  still runs, so **a dir that disappears mid-run deletes every track under it**. Startup only checks the paths exist
  (`GN cmd/gonic/gonic.go:131, 551-556`).
- Playlists are `.m3u` files of paths (`GN playlist/playlist.go`), so they survive a disappearance as text; the rows
  whose path no longer resolves are skipped on read.

---

## 4. Modified, moved and renamed files

### Navidrome: stable row id + a tag-derived PID + move detection in phase 2

- **Modified (retag):** mtime changes, the row is re-imported and upserted by path, so the id stays
  (`mediafile_repository.go:189-197`). If the album id changes (album tags edited), album annotations and
  `created_at` are carried to the new album id (`phase_1_folders.go:309-319, 477-506`).
- **Identity:** each track has a **PID**, a hash of a configurable tag recipe, default
  `musicbrainz_trackid | albumid,discnumber,tracknumber,title` (first non-empty group wins;
  `ND model/metadata/persistent_ids.go:20-60`; defaults in `ND scanner/README.md:459-460`). No content hash, no
  acoustic fingerprint. There is also a whole-record hash of all tags and audio properties, excluding id, path and
  timestamps (`ND model/mediafile.go:231-249`).
- **Move vs delete+add:** after phase 1, phase 2 loads all missing rows plus rows **created during this scan** that
  share a PID with a missing one, ordered by PID (`mediafile_repository.go:496-522`), and matches within each group:
  1. exact: whole-record hash equal (same tags and audio, new path);
  2. the only missing and the only new row for that PID;
  3. same file name without extension (catches a re-encode, `.flac` to `.mp3`; `ND utils/files.go:19-22`).

  A match **rewrites the new row with the old id and old `created_at`, and deletes the new row**, so the move keeps
  annotations, playlist entries and "recently added" order (`phase_2_missing_tracks.go:115-191, 275-338`). Each new
  row is consumed once (`:117-120`). Multi-library: a second tier searches other libraries by MusicBrainz track id,
  then by properties (`:231-273`).

```go
// ND scanner/phase_2_missing_tracks.go:293-308 (inside the transaction)
moved := target
moved.CreatedAt = missing.CreatedAt   // moved tracks don't appear in "Recently Added"
moved.ID = missing.ID                 // keep annotations and references
if err := tx.MediaFile().Put(ctx, &moved); err != nil { ... }
if err := tx.MediaFile().Delete(ctx, target.ID); err != nil { ... }
```

  Because missing rows are kept forever, a file deleted today and copied back next week is still re-linked.

### gonic

- A track's identity is `(album_id, filename)` where the album row *is* the folder path
  (`GN db/db.go:264-267, 387-392`). A rename or move is a delete plus an add: stars, ratings and play counts are lost.
- Retag: mtime newer than the DB write time, then the row is updated in place (`scanner.go:372, 646-693`).

---

## 5. Live watching

### Navidrome

- Library storage uses `rjeczalik/notify` with a **recursive** watch on the library root (`path/...`), event channel
  buffered 500 (`ND core/storage/local/watcher.go:14-49`); on Windows all events plus attribute changes
  (`watch_events_windows.go:5`). The comment admits it *"does not work on some networked mounts and Windows with
  WSL2"* (`watcher.go:15-16`).
- Each event is mapped to the **nearest existing ancestor folder** (`resolveFolderPath`, `ND scanner/watcher.go:282-310`),
  ignored-path rules re-applied (`:254-274, 323-355`), and pushed to one central loop.
- **Debounce:** a timer reset on every event, `Scanner.WatcherWait` default **5 s** (`ND consts/consts.go:115`);
  targets deduplicated in a set; on fire, a **selective scan of just those folders** (`DevSelectiveWatcher=true`,
  `ND conf/configuration.go:1142`). If a scan is already running, it re-arms for 3 x the wait (`watcher.go:74-114`).

```go
// ND scanner/watcher.go:125-137
case notification := <-w.watcherNotify:
    trigger.Reset(w.triggerWait)   // debounce
    target := model.ScanTarget{LibraryID: lib.ID, FolderPath: folderPath}
    if _, exists := targets[target]; exists { continue }
    targets[target] = struct{}{}
```

- **Overflow:** not handled. `notify` drops events when its channel is full; nothing notices. Fallbacks:
  `ScanOnStartup=true` and an optional cron `Scanner.Schedule` (off by default; `conf/configuration.go:1055-1058`).

### gonic

- `fsnotify`, which is **not recursive**: every directory is added one by one at start (`GN scanner/scanner.go:131-150,
  220-243`), and new directories are re-walked to add watches (`:173-179`).
- Batch window **10 s** (`:138`). Create/Write events collect the parent folder and rescan only those folders
  (`:165-189, 197-209`); **any Remove triggers a full scan-and-clean** (`:192-196`; changelog fix #637,
  `GN CHANGELOG.md:122`). Rename and Chmod are ignored. Watcher errors (including fsnotify's overflow error) are only
  logged (`:211-212`). Fallback: `-scan-interval` minutes (`GN cmd/gonic/gonic.go:76, 474-483`); watcher off by
  default (`:78`).

---

## 6. Tag reading

### Navidrome

- **TagLib compiled to WASM** and run under `wazero` (pure Go, no cgo): `go.senan.xyz/taglib` replaced by a Navidrome
  fork (`ND go.mod:6, 58, 62`; `ND adapters/gotaglib/gotaglib.go:1-12`). Opened as a stream with
  `ReadStyleFast` and the file name as a format hint (`gotaglib.go:127-153`).
- Bytes read: whatever TagLib's fast style reads: the tag blocks and stream headers; **the whole ID3v2 tag, pictures
  included, is parsed**, though only `len(props.Images) > 0` is kept (`gotaglib.go:115-121`). No byte cap.
- **Parallelism:** files are read sequentially within a folder, in chunks of 200 (`phase_1_folders.go:288-325`);
  up to `DevScannerThreads` = **5** folders at once (`phase_1_folders.go:215-222`; `conf/configuration.go:1141`);
  DB writes are a single stage with concurrency 1, one transaction per folder.
- **Bad files:** a WASM panic is recovered (`debug.SetPanicOnFault(true)`, `gotaglib.go:62-73`); an unreadable file is
  logged and skipped, the folder continues (`gotaglib.go:34-44`). Side effect: the folder hash is still saved, so the
  skipped file is not retried until something else in the folder changes.
- Optional subprocess scanner (`DevExternalScanner=true` by default) to keep scan memory out of the server
  (`ND scanner/README.md:82-150`).

### gonic

- The same `go.senan.xyz/taglib` (WASM; `GN go.mod:34`), but it opens each file **twice**: `ReadProperties`, then
  `ReadTags` (`GN tags/taglib/taglib.go:25-46`). `ffprobe` fallback with build tag `nowasm`.
- Sequential, one file at a time. Tags are read **before** opening the DB transaction (`scanner.go:387-400`; fix
  for sqlite lock contention, `GN CHANGELOG.md:121`).
- **Bad files:** the first unreadable file returns an error for the whole folder (`scanner.go:394-398`), so no new
  track in that folder is imported. Rows already in the DB are protected because they were marked seen first
  (`:344-348`).

---

## 7. Album art

### Navidrome (rewritten 2026: content-addressed, queue-driven, lazy)

- **Scan time:** only records whether a track has a picture (`HasCoverArt`, `ND model/metadata/map_mediafile.go:69`),
  the album's first track with art (`EmbedArtPath`, `ND model/mediafile.go:~383-395`), and each folder's image file
  names plus `images_updated_at` (`folder` table). It **enqueues** album and artist ids into `artwork_queue` in the
  same transaction as the folder (`phase_1_folders.go:462-473`); a full scan only enqueues missing ones. A re-imported
  track's own art row is deleted so it re-resolves lazily (`:436-442`). Image-only changes in a folder are detected by
  diffing the image list and timestamp (`folder_entry.go:79-92`; `ND scanner/image_changes.go`).
- **Priority** (default `cover.*, folder.*, front.*, embedded, external`, `ND conf/configuration.go:1009`), walked in
  `resolveAlbum` (`ND core/artwork/resolve.go:200-243`). Folder images are sorted so a name without a numeric suffix
  wins (`cover.jpg` over `cover.1.jpg`), then shallower paths (`ND core/artwork/folders_album.go:131-148`). The
  **album root parent** above disc subfolders (`Album/CD1`, `Album/CD2`) is searched too, unless it holds audio of
  other albums (`folders_album.go:60-102`).
- **Embedded:** TagLib's picture list; picks *cover front* by regex over the picture type, else the first
  (`ND core/artwork/sources.go:59-117`); ffmpeg as a fallback (`:119-146`). Formats: whatever TagLib exposes (ID3v2
  APIC, FLAC PICTURE, MP4 `covr`, Vorbis `METADATA_BLOCK_PICTURE`, ASF).
- **Storage:** tables `artwork(hash, mime, width, height, size_bytes, blur_hash, thumb_hash, dominant_color)`,
  `item_artwork(item_kind, item_id, image_type, hash, source, source_path, ref_mtime, ...)` and `artwork_queue`
  (`ND db/migrations/20260722023032_add_artwork_tables.sql:1-41`). The hash is **xxh3-64 of the bytes**
  (`ND core/artwork/image_store.go:48-54`). Folder images are served from the original file; embedded art is copied
  into a sharded content-addressed store `ab/cd/<hash>.jpg` written via temp file + rename (`image_store.go:69-105`;
  `ND core/artwork/processor.go:335-343`). Blurhash, thumbhash and dominant colour are computed once at resolution.
- **Resized variants:** a size-bounded file cache (`ImageCacheSize` 100 MB default, `conf/configuration.go:984`)
  keyed by **content hash + size + square + encoder quality** (`ND core/artwork/image_cache.go:47-51`;
  `artwork.go:37-40`). Identical art on many albums shares one variant, and changed art gets a new key, so there is no
  invalidation step.
- **Invalidation of the source:** `item_artwork.ref_mtime` is the backing file's mtime at resolution; serving
  compares it and, on a mismatch, returns `errStaleSource` and re-resolves (`ND core/artwork/artwork.go:195-228`,
  `:165-192`). A cache hit never opens the source, so the scan-time re-enqueue is what catches most edits.
- A request before resolution is served "provisionally" from local sources without writing state
  (`artwork.go:230-240`). Worker concurrency `max(2, NumCPU/2)` (`conf/configuration.go:1134`).

### gonic (simple, request-time)

- Scan records one cover file name per folder (`coverparse.BestBetween`, `GN scanner/scanner.go:309-312`) and, when
  `scan-embedded-cover-enabled` (default true), the **first track's** id as the album's embedded-cover track if it has
  a picture (`:562-567, 575-596`). The folder file wins over embedded (`GN server/ctrlsubsonic/spec/construct_by_tags.go:68-72`).
- On request: decode, **never upscale**, Lanczos resize, save as `<id>-<size>.<format>` keeping the source format,
  default 600 px (`GN server/ctrlsubsonic/handlers_raw.go:57-157, 159-161`).
- **Freshness:** the cached file is valid while its mtime is not older than the source's mtime **or ctime**
  (`handlers_raw.go:85-95, 176-179`); a hit touches the cached file for LRU eviction; an optional size cap and eject
  interval (`GN cmd/gonic/gonic.go:109-110, 459-467`). For embedded art the "source" is the audio file.

---

## 8. Database and batching

### Navidrome (SQLite)

- `folder(id = md5(libId:relPath), library_id, path, name, parent_id, missing, num_audio_files, num_playlists,
  image_files jsonb, images_updated_at, hash, updated_at, created_at)` (migration `20241026183640_support_new_scanner.go:106-126`;
  `ND model/folder.go:18-52`). `media_file` gains `folder_id`, `pid`, `missing`, `birth_time`, with indexes on each
  (`:219-247`).
- **One transaction per folder** (`WithTxRetry`, `phase_1_folders.go:348-371`), written by a single writer stage;
  missing-flag updates chunked by 200 (`mediafile_repository.go:460-494`).
- **Partial results:** each folder commit is visible at once; albums are rebuilt in phase 3; the UI gets throttled
  `ScanStatus` events (`ND scanner/controller.go:344-386`) and one `RefreshResource` broadcast at the end when
  something changed (`controller.go:252-256`). If a folder fails to persist, the walk is cancelled
  (`phase_1_folders.go:355-359`).

### gonic (SQLite via gorm)

- `albums` = folders (`root_dir, left_path, right_path` unique, `cover`, `embedded_cover_track_id`), `tracks` unique on
  `(album_id, filename)` (`GN db/db.go:260-300, 382-410`).
- One transaction per folder after tags are read (`scanner.go:402-421`); deletes chunked at the end (`:912-914`).
  Partial results visible per folder; the UI just shows "scanning".

---

## 9. Clever bits and complaints

Clever (Navidrome):
- The fingerprint map popped during the walk gives "unchanged" and "unseen" in one pass, with no per-folder query.
- "Unvisited means missing" only when the walk succeeded; failures never cascade into data loss.
- Moves keep the **old** id and created_at; the matching is limited to rows new in this scan, so it is cheap.
- Content-addressed art: dedup across albums and free cache invalidation; mtime re-check of the source on resolve.
- Sorted, natural-order cover choice; album-root parent for multi-disc folders.
- `isDotEntry` treats `...Album` as a real name; `fullReadDir` survives a looping directory error.

Clever (gonic): read tags outside the DB transaction; mark existing rows seen before anything can fail; never
upscale cached covers; ctime as a second freshness signal.

Complaints and pitfalls found in the repo text:
- Navidrome watcher: *"does not work on some networked mounts and Windows with WSL2"* (`ND core/storage/local/watcher.go:15-16`).
- Navidrome purge warning: removing missing files *"will remove permanently ... their play counts and ratings"*
  (`ND ui/src/i18n/en.json:626`), i.e. users keep missing rows around for a reason.
- Navidrome full-scan cost: the PID or tag config change forces a full rescan (`ND scanner/README.md:35`;
  `phase_1_folders.go:82-91`).
- gonic changelog fixes: Remove events not resetting the batch timer (#637), sqlite lock contention during scans
  (#678), created_at from birth time (#693), clearing metadata of emptied albums (#638), macOS `._` files (#657),
  non-lowercase extensions like `.Mp3` (`GN CHANGELOG.md:95, 121-123, 169, 410`).

---

## What Wavee should copy (ranked)

1. **A per-folder fingerprint as the unit of skipping.** Store, per folder, a hash of the sorted
   `(name, size, mtime)` of its audio and image files (plus subfolder count). On rescan, enumerate with
   `FileSystemEnumerable`/`EnumerationOptions` (size and mtime come free from `FindFirstFileEx`), hash, compare with the
   saved value, and skip the folder outright on a match: no per-file lookups, no tag reads. This is what makes a 50k
   rescan "crazy fast" (directory listing bound). Freeze the hash recipe and put a version in the file header
   (Navidrome's `folder_entry.go:112` note). For #240 3.3 this means the TSV should hold **folder lines with their
   fingerprint followed by that folder's file lines**, not a flat list that has to be diffed file by file.
2. **Per-file change key compared by inequality:** re-read when `size != old.Size || mtime != old.ModifiedTicks`.
   Both projects use "mtime is after", which misses a file restored with an older mtime (Navidrome) or compares against
   a DB write time (gonic). The plan's `(Size, ModifiedTicks)` key is right; make sure it is `!=`.
3. **Never delete on "not seen"; mark missing, and only for folders the scan actually walked.** A root that cannot be
   opened keeps its entries (plan 3.6 already says so). Add the guard Navidrome lacks: a root that opens but lists
   nothing, or loses more than a large share of its files at once, is treated as offline, not as deleted. Keep missing
   entries (with a last-seen date) for a long grace period or until "Remove missing" in Settings; they cost ~200 bytes.
4. **Write a folder's fingerprint only together with its entries**, so an interrupted or cancelled scan resumes for
   free on the next run (Navidrome's per-folder transaction). For the TSV: a cancelled scan saves what it has, folders
   not reached keep their old lines and old fingerprints.
5. **Watcher: one recursive watch per root, map each event to the nearest existing folder, debounce 5 s into a set of
   target folders, then scan just those folders.** If a scan is running, re-arm. Treat a watcher buffer overflow
   (`FileSystemWatcher.Error` with `InternalBufferOverflowException`) as "quick-scan the whole root", which neither
   project does. Keep scan-at-start as the safety net (the plan's 2 s debounce is fine; 5 s batches big copies better).
6. **Art: resolve lazily, address by content.** At scan time keep only `HasPicture` per file and, per folder, the image
   file names. On first display, pick `cover.* > folder.* > front.* > embedded` (natural sort, names without numeric
   suffix first, then the album-root parent for `CD1/CD2` folders), hash the bytes (xxh3 or similar), store under
   `ab/cd/<hash>.<ext>`, and cache resized variants keyed by `hash + size`; remember the source path and mtime and
   re-resolve when it changes. For embedded art pick the *front cover* picture type, else the first. Compute the
   dominant colour once when the art is stored (Wavee's theme tint can reuse it).
7. **Move handling for free via tags.** Wavee matches playlist rows by tags and length, so a moved or renamed file
   matches again after the next scan without any PID machinery. Copy only the cheap part: when one scan sees a file
   vanish and an equal one appear (same size, same tags, same length), log it as a move so the "why is this row grey"
   log line stays explainable.
8. **Pipeline shape:** one walker thread producing folders, a few tag readers (3 to 5, below normal priority) working
   folder by folder, one writer that swaps results into `LocalIndex` per batch. Read tags outside the index lock
   (gonic #678). Never let one bad file stop its folder; record it, and do not let it be skipped forever (retry it when
   its own size or mtime changes, not only when the folder changes).
9. **Ignore list:** `$RECYCLE.BIN`, `System Volume Information`, dot files and dot folders (but not `..`-prefixed
   names), `@eaDir`, `._*` resource forks, plus a marker file (`.nomedia` or a Wavee one) to exclude a subtree.

## What to avoid

- gonic's **delete-on-unseen with cascades**: one unmounted folder erases tracks, stars and play counts.
- Per-folder DB queries, two directory reads per folder, a separate stat per file, and two opens per file (gonic).
- Comparing file mtime to the time the index was written, or using "after" instead of "not equal".
- A "full scan" that re-reads every tag as the user-facing "Rescan now"; make the default quick and keep a separate
  "rebuild index" for corruption or a format change.
- Saving a folder's fingerprint when a file in it failed, which silently strands that file (Navidrome).
- Parsing whole ID3v2 tags including pictures during the scan (TagLib fast mode still does); the plan's own reader
  that seeks past `APIC` is better.
- Trusting the watcher alone: both projects document it failing on network mounts; Navidrome also drops events on
  overflow silently.
- Navidrome's scale of machinery (subprocess scanner, artwork queue with retries and breakers, cross-library moves):
  built for a multi-user server, not needed in a desktop client with one index file.
- Windows check for the plan (not from these repos, to verify on a machine): plan 3.6 skips `ReparsePoint`. OneDrive
  Files On-Demand files carry a reparse point; skipping them hides synced music, and reading a cloud-only one
  downloads it. Prefer skipping on `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` / `Offline` and following junctions with a
  loop guard, as Navidrome does for symlinks.
