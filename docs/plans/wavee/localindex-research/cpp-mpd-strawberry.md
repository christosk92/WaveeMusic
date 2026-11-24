# Local index research: MPD and Strawberry (C/C++)

Read-only study for Wavee 0.4 local music folders (plan: `handoff/request-plans/240-local-playlist-files-implementation.md`
sections 3.3 to 3.7). Nothing was built or run.

Citation legend:
- `MPD:` = `C:\WAVEE\scratch\research\localindex\MusicPlayerDaemon_MPD` (shallow clone, HEAD `e55b35f`, NEWS top = 0.25 unreleased, 0.24.20 of 2026-11-20)
- `SB:` = `C:\WAVEE\scratch\research\localindex\strawberrymusicplayer_strawberry` (HEAD `757afa3`, Changelog top = 1.2.31 of 2026-10-03)

Where a statement is my inference rather than something the code or docs say, it is marked **(inferred)**.

---

## 0. The two designs in one paragraph each

**MPD** keeps the whole library as an in-memory tree (`Directory` → `Song`), walked by one idle-priority "update" thread
(`MPD:src/db/update/Service.cxx:98-130`). Every update re-reads every directory and stats every entry; only a file whose
mtime (truncated to whole seconds) is unchanged skips its tag read. Anything not seen in the walk is purged at once.
After a walk that changed anything, the whole tree is rewritten as one gzip'd text file. Live watching is Linux inotify
only, off by default.

**Strawberry** keeps the library in SQLite (`songs`, `directories`, `subdirectories`). A watcher object on its own
idle-priority thread walks per-subdirectory, skipping a subdirectory whose mtime equals the stored one on the
startup/periodic incremental scan. Files that vanish are by default marked `unavailable` (not deleted), stamped
`lastseen`, and expired after 60 days unless a playlist references them. Optional Chromaprint fingerprints ("song
tracking", off by default) let a moved/renamed file keep its row. It has its own watchers for Linux (inotify) and
Windows (`FindFirstChangeNotification`, one handle per directory), with overflow recovery.

---

## 1. Enumeration

### MPD
- Recursive descent, one directory at a time, single thread (`MPD:src/db/update/Walk.cxx:343-403`). Per entry: name
  validity check, `.mpdignore` match, symlink policy, a `GetInfo` (stat), then dispatch to file/dir handlers.
- **Windows: one extra stat per entry.** The Windows `DirectoryReader` iterates with `FindFirstFile` (`WIN32_FIND_DATA`,
  `MPD:src/fs/DirectoryReader.hxx:17-19`) but `LocalDirectoryReader::GetInfo` throws that data away and stats the path
  again: `return Stat(base_fs / reader.GetEntry(), follow);` (`MPD:src/storage/plugins/LocalStorage.cxx:160-163`).
- **Ignore rules:** `.mpdignore` per directory, glob patterns, inherited by children (`MPD:src/db/update/Walk.cxx:319-341,
  361-365`; `ExcludeList.cxx:404-448`). Matching is on the bare file name only ("XXX include full path name in check",
  `ExcludeList.cxx:426`). Extension gate: a file is a song only if a decoder plugin claims the suffix
  (`UpdateSong.cxx:100-110`).
- **Symlinks / loops:** `follow_inside_symlinks` / `follow_outside_symlinks` (`Walk.cxx:259-317`); loop detection by
  comparing (inode, device) up the ancestor chain (`Walk.cxx:159-189`); hard depth cap `MAX_DIRECTORY_DEPTH = 64`
  (`Walk.hxx:68`, used `Walk.cxx:224-229`).
- **Windows specifics:** all of the symlink logic, the loop check and the access check are compiled out
  (`Walk.cxx:163,264,309-316`; `UpdateIO.cxx:184-190` "CheckAccess() is useless on WIN32"); inode/device are forced to 0
  (`LocalStorage.cxx:76-81`); symlink config ignored (`Config.cxx:10-20`). So on Windows a junction loop is only stopped
  by the depth-64 cap. NEWS: "flac: fix scanning files with non-ASCII names on Windows" (`MPD:NEWS:602`); the fix is
  visible in `FlacDecoderPlugin.cxx:58-75` (narrow path open fails, fall back to a wide `InputStream`).

### Strawberry
- Per-subdirectory, depth-first, single watcher thread at `QThread::IdlePriority` plus Linux idle I/O class
  (`SB:src/collection/collectionmanager.cpp:115-125`).
- **Two enumerations per scan.** Before scanning, `FilesCountForPath` walks the whole tree just to size the progress bar
  (`SB:src/collection/collectionwatcher.cpp:2157-2227`), then `ScanSubdirectory` walks it again with `QDirIterator`
  (`:718-772`). Each entry also builds a fresh `QFileInfo(child_filepath)` (`:726`), i.e. another stat, and
  `QStorageInfo` is queried per directory to reject `proc/sysfs/tmpfs/devtmpfs` (`:660-680`;
  `SB:src/constants/filesystemconstants.h:25-29`).
- **Classification by extension during listing:** `.lrc` and `.cue` set aside, images collected as art candidates,
  `kRejectedExtensions` (a block list: tmp, tar, gz, bz2, …; `SB:src/core/song.cpp:260`) and `qt_temp` skipped,
  **everything else is a candidate media file** handed to TagLib (`collectionwatcher.cpp:747-770`). Changelog 1.2.17:
  "Optimize the collection scanning process by deferring media file validation from the initial directory scan (#1954)".
- **Hidden directories** are skipped only in the counting pass (`:2214`), not by the scan itself.
- **Symlinks / junctions:** a link into an already-watched root is skipped (`:667-672`); a link (symlink *or Windows
  junction*) to an ancestor is detected by walking up and comparing directories as filesystem objects, not strings,
  because of case-insensitive filesystems (`:1137-1171`, comment at `:1139` "Junctions on Windows are links to
  directories too, but they're not symbolic links").
- **Unicode normalization:** DB songs are keyed by NFC path so an NFD-on-disk name still matches its row instead of being
  duplicated (`:469-488, 1821-1856`). Mostly a macOS/SMB problem, but cheap insurance.

---

## 2. Change detection on rescan

### MPD: file mtime only, every directory listed every time
```cpp
// MPD:src/db/update/UpdateSong.cxx:17-27
/* Truncate to seconds to match the DB round-trip through time_t.
 * This is the precision stored in the database file. */
static bool CompareMtimeCoarse(time_point info_mtime, time_point song_mtime) noexcept {
	return to_time_t(info_mtime) == to_time_t(song_mtime);
}
```
- Unchanged file: `song->mark = true`, no I/O beyond the stat (`UpdateSong.cxx:90-93`). Changed: re-read tags
  (`:78-89`). `rescan` (`walk_discard`) re-reads everything (`MPD:doc/protocol.rst:1400-1404`).
- Directory mtime is stored (`Walk.cxx:399`, saved `DirectorySave.cxx:72-74`) but **never used to skip a directory**;
  only virtual directories (archives, CUE containers) use it (`VirtualDirectory.cxx`, `MakeVirtualDirectoryIfModified`).
  So an unchanged rescan costs one readdir per directory plus one stat per entry (two syscalls per entry on Windows,
  see 1), and zero tag reads.
- **Precision bug lesson:** NEWS 0.24.14 (2026-08-13) "Windows: fix last-modified check during database update"
  (`MPD:NEWS:324,357`). The coarse compare above is that fix **(inferred from the comment and timing)**: NTFS mtimes
  have 100 ns precision, the saved DB has whole seconds, so before the fix every file looked modified after a restart and
  every update re-read the whole library.
- Subdirectory tracking: the tree itself (children list); `mark` flags do the diff (`Walk.cxx:86-97, 99-138`).
- No numbers in the repo.

### Strawberry: subdirectory mtime gate, then per-file mtime
```cpp
// SB:src/collection/collectionwatcher.cpp:695-699
if (!t->ignores_mtime() && !force_noincremental && t->is_incremental() && path_mtime != 0 &&
    subdir.mtime == path_mtime && !songs_missing_fingerprint && !songs_missing_loudness_characteristics) {
  // The directory hasn't changed since last time
  t->AddToProgress(files_count);
  return;
}
```
- Every subdirectory (not only the root) is a row in `subdirectories(directory_id, path, mtime)`; the incremental scan
  iterates that list (`:2119-2155`) so it does not need to recurse to find them.
- Inside a changed directory: a file is "changed" if `mtime != max(file_mtime, cue_mtime)`, or its CUE/LRC changed, or
  its chosen cover file or that file's mtime changed (`:935-944`). Only then TagLib runs.
- A file whose mtime changed but whose re-read tags are equal is a "touched" song: only `mtime` is written
  (`:1592-1677`; `collectionbackend.cpp:980-998`). Cheap and avoids churn downstream.
- **The gate's blind spot, stated in the code:** a directory's mtime changes when entries are added, removed or renamed,
  not when a file inside is rewritten in place. The overflow path says it outright: "Not an incremental scan, which
  skips directories with an unchanged modification time, so changes to files in them would be missed"
  (`:1991-1992`). Watcher-triggered rescans pass `subdir.mtime = 0` to bypass the gate (`:1979-1983`). So a re-tag
  done while Strawberry was closed, written in place, is missed until a full rescan **(inferred; consistent with the
  comment)**.
- **What an unchanged startup scan still costs:** `FilesCountForSubdirs` enumerates every subdirectory fully *before*
  the gate is checked (`:621-629, 2229-2241`), and the gate itself does a `QFileInfo` per subdirectory. So the gate saves
  tag reads and DB compares, not enumeration.
- No numbers in the repo.

---

## 3. Deleted files and folders

### MPD: purge immediately, no grace
- Anything not marked during the walk is deleted from the tree (`Walk.cxx:99-138`); a stat failure deletes the name
  (`:388-392`); a subdirectory that cannot be opened is deleted with everything under it
  (`UpdateDirectoryChild` → `if (!UpdateDirectory(...)) editor.LockDeleteDirectory(subdir);`, `Walk.cxx:249-250`).
- Unreachable **root**: `Walk` returns early and changes nothing (`Walk.cxx:527-541`). Unreachable **subtree** (a
  subfolder on an unplugged drive, a junction to an offline share): looks empty or unopenable, so it is purged
  **(inferred from the code paths above)**.
- Removal is propagated to the play queue on the main thread through `UpdateRemoveService` (`Remove.cxx:9-52`).
- Mounts (`mount` command for NFS/SMB/UPnP storages) are never purged by a walk: "mount points are always preserved"
  (`Walk.cxx:62-64, 103-105`; NEWS "simple: don't purge mount points on update/rescan").
- **Cancellation risk (my reading, not verified at runtime):** `UpdateDirectory` stops its read loop on `cancel`
  (`Walk.cxx:370`) but still calls `PurgeDeletedFromDirectory` (`:397`), and the service then saves if anything was
  modified (`Service.cxx:112-122`). Entries not yet visited are unmarked (`UnmarkAllIn`, `:367`), so they would be
  purged and the purge saved. Lesson regardless: never purge after a cancelled or partial walk.

### Strawberry: "unavailable" with a 60-day expiry, playlist-referenced rows never expire
- Default `mark_songs_unavailable = true`, `expire_unavailable_songs_days = 60`
  (`SB:src/constants/collectionsettings.h:66-68`; `collectionwatcher.cpp:138-139, 256-263`).
- A file not found on disk is queued in `deleted_songs` (`collectionwatcher.cpp:1102-1111`); at commit it becomes
  `SongsUnavailable` instead of `SongsDeleted` (`:419-427`).
```sql
-- SB:src/collection/collectionbackend.cpp:1055-1061 (comment abridged)
-- A song which becomes unavailable is last seen now, otherwise the scan which marks it unavailable expires it right
-- away when the previous scan was long ago, e.g. when ... the drive with the music isn't mounted yet.
UPDATE songs SET lastseen = CASE WHEN unavailable = 0 THEN :lastseen ELSE lastseen END, unavailable = 1 WHERE ROWID = :id
```
- After an incremental/full scan, available songs get `lastseen = now` and expiry runs (`collectionbackend.cpp:2472-2490`).
  Expiry deletes only unavailable rows older than N days **and not referenced by any playlist item**
  (`:2492-2515`, `LEFT JOIN playlist_items ... WHERE playlist_items.collection_id IS NULL`).
- Coming back: an unavailable song whose file reappears unchanged is restored without re-reading tags
  (`collectionwatcher.cpp:969-974`; Changelog 1.2.17 "Avoid re-scan of restored songs unless mtime is changed (#1819)").
- Unmounted root: the root path is never put in `deleted_subdirs` (`:1119-1121`), so its subdirectory list survives;
  Changelog 1.2.17 "Fixed collection scan not finding new directories in the top level collection directory when the
  mountpoint is restored (#1914)". There is no explicit "drive offline" state: an absent root simply turns every song
  unavailable, which the grace period absorbs.
- Overflow scans deliberately do not run expiry first, so a song is not expired "right away, because it was last seen
  long ago" (`:1948-1957`).

---

## 4. Modified files, moves and renames

### MPD
- No identity across a move: a moved file is "deleted" at its old path and "added" at its new one, with a new `added`
  timestamp (`UpdateSong.cxx:54-77`; `added` is set only on first sight, `:68`). Re-tag detection is purely the coarse
  mtime compare (section 2). Size is not compared.

### Strawberry
- **By path first** (NFC-tolerant, `collectionwatcher.cpp:831, 1835-1856`).
- **By fingerprint second, only when "song tracking" is on** (default off, `collectionsettings.h:65`): a file with no
  path match is fingerprinted with Chromaprint and looked up by `songs.fingerprint`; a row matches if it is the same
  path or its old path no longer exists (`collectionwatcher.cpp:1011-1070, 1858-1887`). The row keeps its id, so play
  counts, ratings and playlist links survive a move or rename (`AddChangedSong` logs "file path from …", `:1602-1605`).
  The old path, scanned in the same transaction, is de-duplicated at commit (`:398-415`).
- **Cost of that identity:** Chromaprint decodes audio through GStreamer, up to `kMaxPcmSeconds = 120` with a 30 s
  timeout (`SB:src/engine/chromaprinter.cpp:52-59`). Turning tracking on also forces `mark_songs_unavailable` on
  (`collectionwatcher.cpp:256`) and makes any directory with an un-fingerprinted song bypass the mtime gate (`:682-699`).
- **Moves the app itself makes** (Organise) update the row directly through `CollectionBackend::SongPathChanged`
  instead of waiting for the scan (`collectionbackend.cpp:534-548`).
- **Directory rename:** only the parent gets a change notification, so the scan of the parent checks every previously
  known child and rescans the ones that vanished (`collectionwatcher.cpp:708-716`); a renamed directory reachable twice
  in one transaction is scanned once (`:642-647`).
- Re-tag detection: mtime (section 2), then a field-by-field compare decides "changed" versus "touched"
  (`:1592-1677`). User data (play count, rating) is merged from the old row (`:1551`).

---

## 5. Live watching

### MPD (Linux only)
- `auto_update` default off, "Only implemented on Linux" (`MPD:doc/mpd.conf.5.rst:185-197`).
- One inotify watch per directory, recursive registration (`InotifyUpdate.cxx:147-199`), mask
  `IN_ATTRIB|IN_CLOSE_WRITE|IN_CREATE|IN_DELETE|IN_DELETE_SELF|IN_MOVE|IN_MOVE_SELF` (`:32-39`). It waits for
  `IN_CLOSE_WRITE` rather than `IN_MODIFY`, so a half-written file is not read (`:244-248`).
- **Debounce 5 s** and path coalescing: a queued parent swallows its children, a new parent replaces queued children
  (`InotifyQueue.cxx:297-374`). If the update queue (32 entries, `Queue.hxx:62`) is full, retry after 5 s (`:313-321`).
- **Watch limit hit:** `TryAddWatch` failure is logged ("Failed to register …") and that directory is simply not
  watched; no fallback, no rescan (`InotifyUpdate.cxx:178-191`). No `IN_Q_OVERFLOW` handling either.

### Strawberry
- Platform watchers (`collectionwatcher.cpp:69-75, 126-132`): inotify on Linux, a custom Windows watcher, Qt's
  `QFileSystemWatcher` elsewhere. Changelog 1.2.20: "New filesystem watcher on Linux and Windows which also detects
  file changes within directories" (Qt's watcher reports directory entry changes, not in-place file writes).
- **Windows watcher:** `FindFirstChangeNotification(path, FALSE /*not recursive*/, FILE_NAME|DIR_NAME|ATTRIBUTES|SIZE|
  LAST_WRITE)`, **one handle per directory**, `WaitForMultipleObjects` in worker threads of at most
  `MAXIMUM_WAIT_OBJECTS` (64, one used for a wake event) each (`SB:src/core/filesystemwatcherwin.cpp:38, 70-117`;
  `filesystemwatcherwinthread.cpp:52-69, 110-163`). It reports only "this directory changed", not which file. A library
  of 6,000 album folders therefore needs about 95 threads and 6,000 kernel handles **(inferred arithmetic)**. Paths are
  case-folded for dedup (`filesystemwatcherwin.cpp:40-51`). A failed re-arm drops and re-adds the watch if the
  directory still exists (`:168-198`).
- **Debounce 2 s**, single-shot timer restarted on every event; queued paths per root (`collectionwatcher.cpp:156-157,
  1889-1904`). Can be paused while the app moves files (`:2080-2091`; `collectionmanager.cpp:127-128`).
- **Overflow:** inotify `IN_Q_OVERFLOW` → `Overflow()` (`filesystemwatcherinotify.cpp:171-223`) → re-add every watch
  and run a full non-incremental scan (`collectionwatcher.cpp:1906-1916, 1943-2007`). The Windows watcher has no
  overflow concept because it carries no per-file events.
- **Watch limit hit:** `inotify_add_watch` failure is logged and skipped (`filesystemwatcherinotify.cpp:75-79`); no
  fallback.
- **Safety net:** a periodic incremental scan once a day when monitoring and startup scan are on
  (`collectionwatcher.cpp:159-160, 287-294, 2105-2113`).

---

## 6. Tag reading

### MPD
- No TagLib. Each decoder plugin has `scan_file`/`scan_stream`; tags and duration come from the codec library's
  metadata path (`MPD:src/TagFile.cxx:16-92`): libFLAC metadata chain for FLAC (`FlacDecoderPlugin.cxx:58-90`), the MP3
  plugin parses the first frame and Xing/LAME, and falls back to **file size ÷ first-frame bitrate** when there is no
  Xing frame count (`MadDecoderPlugin.cxx:672-690, 696-746`), rejecting implausible Xing counts (NEWS 0.24.14 "mad:
  ignore implausible Xing frame counts"). Same estimate rule as Wavee's plan 3.5.
- **Pictures are skipped during the scan** via a want-mask: `TagHandler::WantPicture()` (`MPD:src/tag/Handler.hxx:52-55`)
  is checked in the ID3, Vorbis comment and FLAC picture paths (`Id3Scan.cxx:262`, `VorbisComments.cxx:54`,
  `FlacStreamMetadata.cxx:90`). Only `readpicture` asks for `WANT_PICTURE` (`FileCommands.cxx:395-453`).
- In-process, on the update thread, sequential. No crash isolation.

### Strawberry
- TagLib via `TagLib::FileRef` over a read-only `FileStream` opened with the **wide** path on Windows
  (`SB:src/tagreader/tagreadertaglib.cpp:338-366, 642-687`). Default `FileRef` audio properties (`Average`), so TagLib
  decides how far it reads per format. Falls back to the game-music reader (`TagReaderGME`) when TagLib cannot open it
  (`tagreaderclient.cpp:343-353`).
- During the scan, the embedded picture is **not decoded**; the reader only sets `art_embedded = true` when a front
  cover / "Other" picture exists (`tagreadertaglib.cpp:500-521, 789-791, 913, 985`). Bytes are pulled later by
  `LoadCoverDataBlocking` (section 7).
- **The separate process is gone.** Strawberry inherited Clementine's out-of-process `strawberry-tagreader` worker pool
  (protobuf IPC), capped it at 2 processes (Changelog line 1500, an early 0.x release), saw crashes in it ("Fix possible
  tagreader (messagereply) crashes", "Fixed tagreader crash with empty APE tags", Changelog 1113, 1194), and removed it
  in **1.2.1 (2024-11-21)**: "Removed external tagreader and protobuf dependency" (Changelog 463, 490). The repo does
  not state why it existed; the Clementine rationale was crash isolation from TagLib on malformed files **(inferred,
  from heritage, not from this repo)**.
- **Threading now:** one `TagReaderClient` moved to its own thread (`SB:src/core/application.cpp:131-135`) with a request
  queue (`tagreaderclient.cpp:122-200`), but the **scanner calls `ReadFileBlocking` directly on the watcher thread**
  (`collectionwatcher.cpp:1540, 1578`), so scanning is single-threaded. Per-file read/write locks keep a read from
  overlapping a tag write ("locked by its path, and by its identity", `SB:src/tagreader/tagreaderfilelocks.h:33-36`).
- No numbers on bytes read.

---

## 7. Album art

### MPD (lazy, client-driven, no cache)
- `albumart URI`: looks in the song's directory for, in order, `cover.png`, `cover.jpg`, `cover.jxl`, `cover.webp`
  (`MPD:src/command/FileCommands.cxx:172-209`), streamed to the client in chunks by offset; the open stream is cached per
  client so successive chunks don't repeat the search (`:271-287`). CUE virtual songs resolve to the real directory
  (`:290-330`).
- `readpicture URI`: rescans the file's tags with `WANT_PICTURE` and returns the embedded picture (`:395-453`).
- No thumbnails, no index of art, nothing at scan time.

### Strawberry (choose at scan time, load lazily, thumbnail cache)
- **Scan time:** images in the same directory (extensions jpg, jpeg, jp2, png, gif, tiff, tif, webp plus anything Qt
  can decode, `collectionwatcher.cpp:97-112`) are collected while listing (`:763-765`). The chosen path is stored per
  song as `art_automatic`, with its mtime in `art_automatic_mtime` so a cover replaced under the same name is detected
  (`:940-944, 1085-1091`; schema `art_automatic`, `art_automatic_mtime`).
- **Choosing among several files:** filters in priority order, default `front`, then `cover` (substring, case
  insensitive), first filter with any hit wins; then the **largest image by pixel count**, found by fully decoding each
  candidate with `QImage(path)` (`:252, 2009-2053`). Only the song's own directory is searched (`:2061-2078`); no parent
  or `Scans/`/`Artwork/` subfolder search.
- **Load order at display time** is a user setting, default `art_unset, art_embedded, art_manual, art_automatic`
  (`SB:src/covermanager/albumcoverloaderoptions.cpp:35-61`): embedded wins over the folder file by default. Embedded
  bytes are read through the tag reader on demand (`albumcoverloader.cpp:365-376`); folder files are read whole and
  decoded (`:411-439`). Scaling happens in the loader thread (`:275-297`).
- **Thumbnails:** the collection view asks for the first song of an album and caches the scaled result in
  `QPixmapCache` (160 MB default) and optionally a disk cache (`QNetworkDiskCache`, 360 MB, **off by default**), keyed
  by source + album container key, stored as **XPM** (`SB:src/collection/collectionmodel.cpp:1052-1190`;
  `collectionsettings.h:53-54, 77`). A failed load caches the placeholder so it is not retried every paint
  (`:1166-1169`). Invalidation: `ClearItemPixmapCache` when the album's songs change (`:1064-1084`); a thumbnail for
  another DPI is replaced, so one per album (`:1090-1093`).

---

## 8. Storage

### MPD: one text file, rewritten whole
- Format: header block (format version, MPD version, fs charset, enabled tag list), then nested
  `directory:`/`begin:`/`song_begin:`/`end:` blocks with `mtime:` per directory and per song
  (`MPD:src/db/plugins/simple/DatabaseSave.cxx:22-52`, `DirectorySave.cxx:64-94`). A tag-list or charset mismatch, or
  an unknown format, **discards the database** and forces a full rescan (`DatabaseSave.cxx:84-121`). This is the same
  "cache, rebuilt if foreign" stance as Wavee's `localindex.tsv`.
- Save = prune empty directories, sort, write through gzip (on by default), commit (`SimpleDatabasePlugin.cxx:333-377`).
  Commit is atomic: `O_TMPFILE`+`linkat` on Linux, temp file + `MoveFileEx` on Windows
  (`MPD:src/io/FileOutputStream.cxx:312-399`; NEWS "fix database/state file saving on Windows").
- Saved only when the walk modified something or the file is missing (`Service.cxx:115-122`).
- In memory: songs are added to the live tree under the DB lock as they are read (`UpdateSong.cxx:70-73`), so clients
  see progress immediately; string tags are pooled (`src/tag/Pool.cxx`).

### Strawberry: SQLite
```sql
-- SB:data/schema/schema.sql:9-19 and selected songs columns (:20-116), index :1610
CREATE TABLE directories    (path TEXT NOT NULL, subdirs INTEGER NOT NULL);
CREATE TABLE subdirectories (directory_id INTEGER NOT NULL, path TEXT NOT NULL, mtime INTEGER NOT NULL);
-- songs: directory_id, url, beginning, cue_path, filetype, filesize, mtime, ctime, unavailable, lastseen,
--        fingerprint, art_embedded, art_automatic, art_automatic_mtime, art_manual, art_unset, lrc_mtime, ... (~95 cols)
CREATE UNIQUE INDEX idx_songs_url_beginning_cue_path ON songs (url, beginning, IFNULL(cue_path, ''));
```
- No index on `subdirectories` at all; lookups are by `directory_id` (full scan of a small table).
- **Batching:** the scan accumulates everything for one collection root in a `ScanTransaction` and commits in its
  destructor (`collectionwatcher.cpp:326-335, 383-466`); each commit becomes a handful of signals, each handled in one
  SQLite transaction with prepared statements (`collectionbackend.cpp:550-630, 675-720, 980-998, 1050-1078`). A failing
  statement skips that song rather than rolling back the batch (`:718-719`).
- **Stop = lose the scan:** "If we're stopping then don't commit the transaction" (`collectionwatcher.cpp:328-331`), and
  there is no intermediate commit, so an interrupted first scan of a large root saves nothing.

---

## 9. Known user complaints (from the repos)

Strawberry Changelog (most recent first):
- "Prevent duplicate songs with identical URL (#1341)"; "Re-point playlist items that references duplicated songs
  before removal (#2168)"; "Preserve user data before deleting duplicate songs (#2168)" (1.2.2x). Duplicates were a
  long-running problem: also #1341 at 1.0.x "possible duplication of song entries after organizing", "duplicating songs
  in the DB when organizing songs between 2 different collection directories".
- "Fixed collection watcher skipping symlinked directories"; "Fixed collection subdirectory path handling when renaming
  directories" (1.2.20).
- "Avoid re-scan of restored songs unless mtime is changed (#1819)" (1.2.17).
- "Optimize the collection scanning process by deferring media file validation … (#1954)"; "Fixed collection scan not
  finding new directories … when the mountpoint is restored (#1914)" (1.2.17).
- "(Unix) Fixed collection scanner so it ignores special filesystem paths (/sys, /proc, /run, etc) (#1615)".
- "Fixed scan transaction being started when 'Update the collection when Strawberry starts' option is unchecked (#1469)".
- "Fixed the wrong embedded cover being picked for FLAC/Ogg files with multiple untagged ('Other') pictures (#2250)";
  "Read Vorbis/FLAC 'Other' embedded covers if front cover is not available (#1793)".
- "Fixed aborting collection scan when Strawberry exists to avoid hang on exit"; "Fixed resuming collection scan when
  adding a new directory after collection scan was aborted".
- "Make collection watcher ignore files with 'qt_temp' filename and tmp extension" (editors' temp files triggering scans).
- "Turned off 'Grey out unavailable songs in playlists on startup' by default" (that check was expensive at startup).
- macOS: "Fixed filesystem watcher to correctly pick up changed collection directories"; earlier "Fixed collection
  watcher to unwatch deleted directories".

MPD NEWS:
- Windows "fix last-modified check during database update" (0.24.14) — see section 2.
- "flac: fix scanning files with non-ASCII names on Windows".
- "inotify: fix use-after-free bug", "inotify: fix crash bug", "inotify: trigger update after symlink was created",
  "inotify: obey '.mpdignore' files".
- "simple: purge songs and virtual directories for unavailable plugins on update" (0.22.2).
- "ffmpeg: fix end-of-file check (update stuck at empty files)".
- `auto_update` remains Linux-only; Windows users get no live updates at all.

---

## What Wavee should copy (ranked)

1. **Compare mtime at the precision you store it, and store it at full precision.** MPD shipped a Windows bug where
   every file looked modified after a restart because 100 ns NTFS times were compared to saved whole seconds. Wavee's
   `(Size, ModifiedTicks)` key is right; make sure the TSV round-trips the exact `long` ticks (`LastWriteTimeUtc.Ticks`),
   never a formatted date, and add size as MPD does not.
2. **Take size and mtime from the enumeration entry, not from a second stat.** MPD's Windows reader discards
   `WIN32_FIND_DATA` and stats again; Strawberry builds a fresh `QFileInfo` per entry and enumerates twice for a progress
   bar. In .NET use `FileSystemEnumerable<T>` with a transform over `ref FileSystemEntry` (`Length`,
   `LastWriteTimeUtc`, `Attributes` come straight from `FindFirstFileEx`/`NtQueryDirectoryFile` data). One pass, no
   pre-count: report "files seen" as it goes (plan 3.6 already does).
3. **Never purge on a partial or failed walk; use an "unavailable" state with a grace period.** Strawberry's model is
   the best of the two: a missing file becomes `unavailable` with `lastseen = now` (set on the transition, not at the
   next scan), comes back without a tag read when it reappears unchanged, and expires after 60 days. For Wavee: keep the
   entry with an `Available=false` flag and a `MissingSinceTicks`; an unreadable root (plan 3.6 "offline") keeps every
   entry untouched; a cancelled scan writes nothing that removes entries. Strawberry also never expires a row a playlist
   references; for Wavee the analogue is "never expire an entry that is the current match of a noted
   `spotify:local:` row" (cheap: `LocalLibrary.NoteItem` already tracks them).
4. **Move/rename identity without audio fingerprints.** Strawberry needs Chromaprint (120 s of decoded audio, off by
   default) to keep a row across a move. Wavee doesn't need row identity: matching is by tags + duration, so a moved file
   matches the same playlist row as soon as it is indexed. What Wavee should copy is the cheap part: when a new path
   appears in the same scan in which an old path vanished, and `(Size, ModifiedTicks)` are equal, **reuse the old
   entry's tags without reading the file** (a rename or move within the library is then free). Size+mtime pairs are
   unique enough for this; fall back to a read on ambiguity.
5. **Debounce and coalesce watcher events by directory, and treat watcher loss as "rescan that root".** MPD: 5 s, parent
   swallows children; Strawberry: 2 s single-shot restarted on each event, rescans bypass the dir-mtime gate. Wavee's
   2 s per folder is right; add MPD's coalescing (a queued ancestor swallows descendants) and Strawberry's overflow rule:
   `FileSystemWatcher.Error` (buffer overflow) means a full rescan of that root, not a log line.
6. **One recursive watcher per root, not one per directory.** Strawberry's Windows watcher spends a kernel handle per
   directory and a thread per 63 directories and still can't say which file changed. .NET `FileSystemWatcher` with
   `IncludeSubdirectories = true` is one `ReadDirectoryChangesW` per root with file names; set `InternalBufferSize` to
   64 KB (the max for network paths), filter `FileName | DirectoryName | LastWrite | Size`. This is what plan 3.6
   already says; keep it.
7. **Do not read pictures during the scan; record only that one exists.** MPD's want-mask and Strawberry's
   `art_embedded` flag both skip picture bytes at scan time. Wavee's ID3 reader already plans to skip `APIC` by seeking;
   also record `HasEmbeddedArt` (and for FLAC the PICTURE block offset/length, for MP4 the `covr` atom offset/length) so
   the later art load is one seek + one read, no re-parse.
8. **Choose the folder image at scan time, decode it lazily.** Strawberry stores `art_automatic` + its mtime per song,
   so a replaced `cover.jpg` is detected by a stat. Copy that with a deterministic name priority instead of decoding
   every candidate to compare pixels: exact `cover`, `folder`, `front`, `album`, then any name containing
   front/cover, then the largest file by byte size (free from the enumeration entry). Then a per-album thumbnail on
   disk keyed by `(source path, size, mtime, offset)` so a changed source invalidates itself.
9. **Atomic save of the index file.** MPD writes to a temp file and `MoveFileEx`es it over; Wavee's plan already does
   temp + `File.Move(overwrite: true)`. Keep the "foreign header → ignore and rebuild" rule MPD and the plan share.
10. **Idle priority on the scan thread, cancellable at file boundaries, pausable while the app itself writes files.**
    Both do this (MPD `SetThreadIdlePriority`, Strawberry `IdlePriority` + idle I/O class + `SetRescanPaused`). On
    Windows also consider `THREAD_MODE_BACKGROUND_BEGIN` for the scan thread, which lowers I/O priority too (.NET's
    `ThreadPriority.BelowNormal` does not).
11. **Wide paths and NFC-tolerant comparisons.** MPD's FLAC non-ASCII bug and Strawberry's NFC keys: open every file
    through .NET (always UTF-16), and when comparing an old index path with a new one, compare
    `OrdinalIgnoreCase` on Windows and tolerate NFC/NFD (SMB shares from macOS).
12. **Junction/loop safety.** Plan 3.6 skips `ReparsePoint`, which is simpler than both and avoids Strawberry's
    ancestor-identity walk. Keep it; log once per skipped junction so a user with a junctioned music folder learns why.

## What to avoid

- **Purging immediately on a missing file or unreadable subfolder** (MPD `Walk.cxx:249-250, 397`): an unplugged USB
  drive under a library folder empties it, and with MPD's save-on-modify that is persisted.
- **Purging after a cancelled walk** (MPD reading of `Walk.cxx:367-397` + `Service.cxx:112-122`).
- **A directory-mtime gate as the only change test.** In-place re-tags don't move a directory's mtime (Strawberry says
  so at `collectionwatcher.cpp:1991-1992`). If Wavee ever adds a dir-mtime shortcut, use it only for "did entries
  change", and still compare file `(Size, ModifiedTicks)` from the single enumeration pass, which is already cheap.
- **Enumerating twice** (Strawberry's progress pre-count) and **stat'ing twice per entry** (MPD on Windows, Strawberry's
  `QFileInfo`).
- **A block list of extensions** (Strawberry hands every non-image, non-blocked file to TagLib). Wavee's allow list
  (`.mp3 .flac .ogg .oga .m4a .mp4 .aac .wav .opus`) is right.
- **Decoding every candidate cover to pick the biggest** (Strawberry `PickBestArt`, `QImage(path)` per file at scan time).
- **One-transaction-per-root with no checkpoints** (Strawberry): a 50,000-file first scan interrupted at 90% saves
  nothing. Publish to the in-memory index and save the TSV every N files or every few seconds during a first scan, as
  MPD's live tree effectively does.
- **Per-directory OS watch handles** (Strawberry Windows, MPD/Strawberry inotify) and **silently ignoring a failed
  watch** (both log and move on). If Wavee's watcher faults, mark the folder "updates on start and Rescan now" in
  Settings (plan 3.6 already says this) rather than only logging.
- **An out-of-process tag reader.** Strawberry ran one for years and removed it in 1.2.1 along with protobuf; the IPC
  itself crashed. Wavee's own managed header parsers (no native TagLib) don't need crash isolation: wrap each file in a
  try/catch, bound every length field against the file size, and cap allocations (e.g. refuse a tag frame larger than
  16 MB). That gets the isolation without the process.
- **Audio fingerprinting for identity** (Chromaprint, 120 s decode per file) — far too slow for "crazy fast", and Wavee's
  metadata matching doesn't need it.
