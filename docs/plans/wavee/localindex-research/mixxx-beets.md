# Local index research: Mixxx and beets

For #240 (local music folders, 0.4), plan sections 3.3 to 3.7. Read-only study, nothing built or run.

- Mixxx at `C:\WAVEE\scratch\research\localindex\mixxxdj_mixxx`, commit `825e4519` (2026-11-20). Paths below are relative to that root.
- beets at `C:\WAVEE\scratch\research\localindex\beetbox_beets`, commit `458390ea` (2026-11-21).

Short answer: Mixxx is the better model for **deletes, moves and cover art**. beets is the better model for **the
modified-file rule** (its mtime invariant is short and correct). Neither is fast by design: Mixxx's scanner runs
on one thread, and beets is Python with a manual `update` command. Wavee's planned per-file `(Size, ModifiedTicks)`
check is already better than what either project does. The useful parts to copy are Mixxx's tombstones, its
relocation rule, its cover-location rules and its cover cache. The things to avoid are mostly their known bugs.

---

## 1. Change detection

### Mixxx: a hash of each directory's file names

`src/library/scanner/recursivescandirectorytask.cpp:58-123`. Each directory task lists its children once, feeds
the UTF-8 path of **every supported audio file** into SHA-256, truncates the digest to a 64-bit key, and compares
that key with the saved one:

```cpp
// recursivescandirectorytask.cpp:61-67, 91-119
if (currentFileInfo.isFile()) {
    if (supportedExtensionsMatch.hasMatch()) {
        hasher.addData(currentFile.toUtf8());          // the path only: no size, no mtime
        filesToImport.push_back(currentFileInfo);
...
const mixxx::cache_key_t newHash = mixxx::cacheKeyFromMessageDigest(hasher.result());
const mixxx::cache_key_t prevHash = m_scannerGlobal->directoryHashInDatabase(dirLocation);
if (prevHashExists || m_scanUnhashed) {
    if (prevHash != newHash) {
        // queue ImportFilesTask for this directory
    } else {
        emit directoryUnchanged(dirLocation, presentTrackLocations);
    }
} else {
    m_scannerGlobal->addUnhashedDir(m_dirAccess);     // new dirs go to a second pass
}
```

- **Storage:** table `LibraryHashes(directory_path PRIMARY KEY, hash INTEGER, directory_deleted INTEGER)` plus a
  `needs_verification` column (`res/schema.xml:44-47`, `:161`). The key is stored as a signed int64
  (`src/library/dao/libraryhashdao.cpp:15-19`). The key is the first 8 bytes of the digest
  (`src/util/cache.cpp:8-28`, "FIPS 180-4 ... Truncated Message Digest").
- **Compare:** all hashes are loaded into a `QHash` once per scan (`libraryscanner.cpp:225`) and shared read-only
  through `ScannerGlobal` (`scannerglobal.h:46-48`), so each directory does one in-memory lookup.
- **Two passes:** directories that already have a hash are scanned first, and new directories in a second pass
  (`libraryscanner.cpp:263-266`, `:296-331`). This means that when the same folder is reachable twice (symlinks), the
  same copy always wins. Canonical paths are de-duplicated under a mutex (`scannerglobal.h:58-67`).
- **What the hash misses:** a file **edited in place** keeps its name, so the directory hash does not change and the
  file is **not re-read by the scan**. Tags are re-read only later, when the track is loaded (section 3). `track_locations`
  has a `filesize` column (`res/schema.xml:40`), but the scanner never compares it.
- **Cancel safety:** if the scan was cancelled, the directory's hash is cleared rather than saved, so the next scan
  resumes that directory (`libraryscanner.cpp:625-639`, changelog `#16300`).

### beets: an mtime per item, checked only by `beet update`

`beets/ui/commands/update.py:79-95`. `update` walks the **database items** (not the disk), checks that the file still
exists, and skips the file when its mtime is not newer:

```python
# update.py:79-95
for item in items:
    if not item.path or not os.path.exists(syspath(item.path)):
        ...  item.remove(True)                       # hard delete (section 2)
        continue
    if item.current_mtime() <= item.mtime:
        log.debug("skipping {0.filepath} because mtime is up to date ({0.mtime})", item)
        continue
    item.read()
```

- `current_mtime()` is `int(os.path.getmtime(...))`, which drops the fraction of the second (`beets/library/models.py:1101-1105`).
- The invariant (`docs/dev/library.rst:93-122`): "If the on-disk metadata differs from the DB metadata, then the
  on-disk mtime must be greater than the DB mtime". So a DB mtime of 0 is always valid. `read()` and `write()`
  set `db_mtime = disk_mtime` (`models.py:943-945`, `:998-1000`). Any change to a DB field resets it to 0
  (`models.py:842-843`). The docs themselves call this "sort of complicated" (`library.rst:96`).
- **New files** are found only by `beet import`. With `incremental`, a directory is skipped when the **tuple of its
  paths** was imported before (`beets/importer/session.py:246-258`). That state is a pickled file
  (`beets/importer/state.py:83-104`). A file added to an already-imported album directory is therefore not seen.

### What an unchanged rescan costs

| | Mixxx | beets `update` |
|---|---|---|
| File system | One directory listing per directory, sorted by name (`recursivescandirectorytask.cpp:38-42`). One regex per name. SHA-256 over the paths. **No file is opened.** | One `exists()` and one `getmtime()` per **database item**. No listing, so new files are not seen. |
| Database | O(n) even when nothing changed: `UPDATE track_locations SET needs_verification = 1` on every row (`trackdao.cpp:1784-1794`), then one `UPDATE ... WHERE location IN (<every present file>)` (`:1796-1810`), then `verifyRemainingTracks` (a stat for each track outside the roots, `:2080-2150`), then `ANALYZE` (`libraryscanner.cpp:450-453`). | One transaction (`update.py:51`). Nothing is written for unchanged items. |
| Threads | `kScannerThreadPoolSize = 1` with "TODO(rryan) make configurable" (`libraryscanner.cpp:24-25`). | `threaded: yes` (`config_default.yaml:121`) only pipelines the import stages. `update` is a plain loop. |

**For Wavee:** on Windows, `FindFirstFileEx`/`FileSystemEnumerable` already returns `Length` and `LastWriteTimeUtc`
for each entry, with no extra stat. So the per-file `(Size, ModifiedTicks)` check in plan 3.6 costs **the same as
Mixxx's name hash**, and it also catches edits in place. A directory-level hash adds nothing on top of it.

---

## 2. Missing, deleted and moved tracks

### Mixxx: mark and sweep, never a hard delete

The scan marks everything unverified, the walk verifies what it sees, and whatever is left is flagged, not removed
(`libraryscanner.cpp:241-255`, `:334-412`):

```cpp
// libraryscanner.cpp:335-340 (comment), 356, 370-381
// At the end of a scan, mark all tracks and directories that weren't
// "verified" as "deleted" (as long as the scan wasn't canceled half way
// through). ... a canceled scan might leave half of your library as unverified.
m_trackDao.markTrackLocationsAsVerified(m_scannerGlobal->verifiedTracks());
if (!m_trackDao.verifyRemainingTracks(m_libraryRootDirs, ...)) return;   // canceled
m_trackDao.markUnverifiedTracksAsDeleted();          // fs_deleted=1, row kept
m_libraryHashDao.markUnverifiedDirectoriesAsDeleted();
```

- `fs_deleted=1` only sets a flag (`trackdao.cpp:1812-1836`). The `library` row, its id, cues and play counts stay.
  Playlists and crates reference `library(id)` (`PlaylistTracks.track_id`, `res/schema.xml:73-77`), so **they
  survive**: the row shows as missing, and the "Missing" view lists `WHERE fs_deleted=1`
  (`src/library/missing_hidden/missingtablemodel.cpp:34`).
- A file that comes back has its flag cleared (`markTrackLocationsAsVerified` sets `fs_deleted=0`, `:1801-1803`). The
  scan summary counts these as "rediscovered" tracks (`libraryscanner.cpp:477-483`).
- **Unplugged drives:** a root that does not exist is skipped (`libraryscanner.cpp:282-285`). But tracks under it
  then remain unverified, and `verifyRemainingTracks` marks **every unverified track under a known root as deleted**
  without a stat (`trackdao.cpp:2114-2122`). The changelog lists several "false missing tracks" fixes (`CHANGELOG.md:649`, `:1266`).
- Only removing a library directory with "purge" deletes rows (`library.cpp:690-700`).

### Mixxx: relocating moved files (`TrackDAO::detectMovedTracks`, `trackdao.cpp:1854-2049`)

The rule: **same file name, duration within 1 s; on a tie, the longest common path suffix wins**. Candidates are only
the files **added during this scan**. The **old** track id is kept, and the new row is deleted.

```cpp
// trackdao.cpp:1877-1888, 1921-1923
// NOTE: Successors are identified by filename and duration (in seconds). ... tolerance of 1 second
"WHERE track_locations.location IN (%3) AND "       // %3 = addedTracks of this scan
"filename=:filename AND "
"ABS(duration - :duration) < 1 AND "
"fs_deleted=0"
// rather use duration then filesize as an indicator of changes. The filesize
// can change by adding more ID3v2 tags
```

```cpp
// trackdao.cpp:1957-1967: tie-break by longest common path suffix
// 'Music/Abba-1981-Greatest Hits/1-Waterloo.mp3' prefers
// 'Music/Abba/Greatest Hits/Waterloo-1.mp3' over 'Music/Falko/Track1.mp3'
if (newTrackLocationSuffixMatch < nextSuffixMatch) { ... }
```

Then (`:2006-2042`): `DELETE FROM library WHERE id=:newid`, `UPDATE library SET location=:newloc WHERE id=:oldid`,
`DELETE FROM track_locations WHERE id=:oldLocId`. Playlist and crate links follow because the id did not change.

- **Limits:** a renamed file (different name) is never relocated. With no new tracks the step is skipped entirely:
  "We can only guarantee for new tracks that the user has not edited metadata ... TODO(xxx) resolve old duplicates"
  (`:1868-1873`). It never uses a content hash.
- **Moving a whole folder by hand:** `DirectoryDAO::relocateDirectory` rewrites every location prefix in one pass
  (`directorydao.cpp:211-341`). It has its own warning: "It can fail in mysterious ways for example if a track in the
  oldDirectory also has a zombie track location in newDirectory" (`:265-268`).

### beets: no tombstones and no move detection

- `update` **hard-deletes** a missing item: `item.remove(True)` (`update.py:81-87`). With `delete=True` and the last
  item gone, the album is removed with `delete=True` too, which **deletes the album art file** (`models.py:1131-1157`,
  `:414-418`). beets has no "missing" state.
- The FAQ says that if you moved music outside beets, you should "Move the music back", or "Delete your database and
  re-create it", or edit SQLite by hand (`docs/faq.rst:225-230`).
- Playlists (m3u, through the playlist plugin) follow paths through `item_moved` and `item_removed` events
  (`beetsplug/playlist.py:114-123`). That only helps for moves beets makes itself.

---

## 3. Modified files: which wins

### Mixxx: the database wins by default

- The scan imports tags **once**, for new tracks (`trackdao.cpp:937-939`, `UpdateTrackFromSourceMode::Once`).
- When a track is loaded from the DB, it is re-imported only if the user turned on "sync track metadata": then the
  mode becomes `Newer` (`trackdao.cpp:1558-1576`).
- `Newer` compares the file's mtime with the stored `source_synchronized_ms` (`track/trackrecord.cpp:140-180`):
  `getSourceSynchronizedAt() < fileSourceSynchronizedAt` means Outdated, so re-import. If the timestamp is missing
  (tracks from before schema v37), the status is "Unknown" and the re-import is skipped "to prevent overwriting
  precious user data" (`sources/soundsourceproxy.h:135-143`).
- An mtime of 0 (1970) is treated as "unknown" (`sources/metadatasource.cpp:14-27`).
- **Protections when re-importing:** a missing tag does not blank an existing DB value
  (`soundsourceproxy.cpp:668-672`). A partial import only merges extra fields (`:749-765`). Cover art the user chose
  is never replaced (`:700-713`).
- If the file is newer than the last sync and the user exports, Mixxx overwrites the file and logs a warning
  (`track/track.cpp:1732-1757`).

### beets: the file wins on `update`

- Any DB edit resets `mtime` to 0 (`models.py:842-843`). So a `modify` without `--write` is **overwritten from the
  file** on the next `update` (`0 < disk mtime`).
- There is one hard-coded exception, for albumartist (`update.py:104-113`). Users can narrow what is copied with
  `-F` / `-e` fields (`update.py:57-75`).
- `update` copies album-level fields from the **first track** of the album into all its tracks
  (`update.py:142-157`; the docs warn about this at `docs/reference/cli.rst:390-396`).

**For Wavee:** the index is read-only (no tag editing), so the file always wins. The comparison must be `!=` on
`(size, ticks)`, not `<=` on whole seconds. With `<=`, a file replaced by an older copy (restored from a backup, or
copied with its mtime kept) is missed, and so is an edit within the same second. FAT/exFAT drives have a 2-second
mtime resolution, so a size check is needed as well.

---

## 4. Tag reading

### Mixxx

- **Library:** TagLib, one concrete `TagLib::X::File` per format (`sources/metadatasourcetaglib.cpp:117-280`). It
  reads **one tag type per file**, in a fixed order: "Only the first available tag type is read and data in
  subsequent tags is ignored" (`:111-115`).
  - MP3: ID3v2, then APE, then ID3v1 (`:123-147`). Note: "TagLib::MPEG::File::tag() returns a valid pointer even if
    neither an ID3v2 nor an ID3v1 tag is present!" (`:138-141`).
  - FLAC: VorbisComment, then ID3v2, then the picture list as a cover fallback (`:171-199`).
- **Audio properties:** TagLib's file constructors read them by default (its default read style); Mixxx asks for
  `audioProperties()` from every file it reads (`track/taglib/trackmetadata_file.cpp:162-197`). Later, the stream
  info measured by the decoder replaces these "unreliable and imprecise" values (`soundsourceproxy.cpp:684-686`, `:790-803`).
- **Cover in the same read:** for a new track the import asks for the cover too (`pCoverImg`,
  `soundsourceproxy.cpp:691-713`). That **decodes the embedded image into a QImage** during the scan
  (`trackmetadata_id3v2.cpp:602-640`, picture-type preference at `:50-55`).
- **Untagged files:** Mixxx parses "Artist - Title" out of the file name when both tags are empty
  (`soundsourceproxy.cpp:805-836`).
- **Threading:** directory tasks run on a `QThreadPool` of **one** thread. `addNewTrack` is a queued signal to the
  `LibraryScanner` QThread (`libraryscanner.cpp:111-114`, `:612-615`), so every tag read happens **serially on the
  scanner thread** (`:683-719`). All inserts share one SQL transaction (`trackdao.cpp:411-421`, `:547-564`).
- **Partial reads:** none beyond what TagLib does. It does not skip `APIC` frames when the cover is not needed.

### beets

- **Library:** the `mediafile` package (`pyproject.toml:31`), built on mutagen. `Item.read()` opens a `MediaFile` and
  copies every `_media_fields` attribute (`models.py:920-947`). `mf.art` returns the **raw bytes** of the first image
  and does not decode it (`beetsplug/_utils/art.py:33-41`).
- **Threading:** the import pipeline is threaded per stage (`importer/session.py:229-239`). Files are read inside
  `read_tasks` / `ImportTaskFactory.album`, one after another (`importer/tasks.py:1405-1427`).

---

## 5. Cover art

### Mixxx: where the cover comes from

Embedded art is used first. Folder images are used only when there is none (`src/library/coverartutils.cpp:186-210`).
For folder images, the rules are in `selectCoverArtForTrack` (`:104-184`):

```cpp
// coverartutils.cpp:121-127
// If there is a single image then we use it unconditionally. Otherwise
// we use the priority order described in PreferredCoverType. Notably,
// if there are multiple image files in the folder we require they match
// one of the name patterns -- otherwise we run the risk of picking an
// arbitrary image that happens to be in the same folder
```

- **Priority** (`coverartutils.h:37-45`, case-insensitive whole-basename compare, `:132-171`):
  1. the track's base name, or its full file name (`song.mp3.jpg`)
  2. the album tag
  3. `cover`
  4. `front`
  5. `album`
  6. `folder`
  7. otherwise **none**
- **Extensions:** jpg, jpeg, png, gif, bmp (`:29-37`). Only the track's own folder is searched (`:74-92`).
- **Embedded picture types:** FrontCover, then Media, then Illustration, then Other (`trackmetadata_id3v2.cpp:50-55`).

### Mixxx: CoverInfo, the hash and the digest

The DB stores, per track: `coverart_source` (UNKNOWN/GUESSED/USER_SELECTED), `coverart_type` (NONE/METADATA/FILE),
`coverart_location` (a **file name relative to the track's folder**), `coverart_color`, `coverart_digest` and
`coverart_hash` (`trackdao.cpp:2217-2226`; the enums are frozen "STORED IN THE DATABASE", `coverart.h:38-56`).

```cpp
// coverart.cpp:64-73
void CoverInfoRelative::setImageDigest(const QImage& image) {
    color = CoverImageUtils::extractBackgroundColor(image);   // image scaled to 1x1, smooth
    m_imageDigest = CoverImageUtils::calculateDigest(image);  // SHA-256 over DECODED pixels
    m_legacyHash = calculateLegacyHash(image);                // 16-bit qChecksum (old)
```

- **Digest:** SHA-256 over `image.constBits()` (`src/util/imageutils.cpp:7`, `:13-23`).
- **Cache key:** the first 8 bytes of the digest, or the old 16-bit hash when there is no digest
  (`coverart.h:94-100`).
- **Colour:** the image scaled to 1×1 with smooth scaling (`imageutils.cpp:25-38`). It is used for the placeholder.
- **Why the digest exists:** the 16-bit hash showed the wrong cover on collisions (changelog `:2507`, "Prevent wrong
  cover art display due to hash conflicts").
- **Invalidation:** the digest changes when the pixels change. A FILE cover whose file is gone gives
  `ErrorFilePathDoesNotExist` (`coverart.cpp:146-149`). A METADATA cover that is no longer in the file resets the
  CoverInfo, so the next request guesses again (`coverartcache.cpp:225-239`, issue #15199).
- **User choice is never overwritten:** see the warning at `trackdao.cpp:2161-2166` and `soundsourceproxy.cpp:700-709`.
- **Back-fill:** after a scan, `detectCoverArtForTracksWithoutCover` fills `coverart_source IS NULL OR 0`. It collects
  the rows first so it does not block the DB (issue #7713, `:2192-2193`), and it caches the folder listing per
  directory (`CoverInfoGuesser`, `coverartutils.cpp:199-205`).

### Mixxx: lazy loading and the thumbnail cache (`src/library/coverartcache.cpp`)

- `requestCover` looks in `QPixmapCache` first, keyed by `"CoverArtCache_<key>_<width>"` (`:17-20`, `:71-96`).
  On a miss it starts a `QtConcurrent::run(loadCover)` on the global pool, and a `QFutureWatcher` brings the result
  back to the GUI thread (`:171-183`).
- **In-flight de-duplication by key:** "to avoid loading the same picture again while we are loading it. This fixes
  also #11131 on Windows where simultaneous open the same file from two threads fails" (`:154-163`).
- The image is resized on the worker (`:219-224`), and the QPixmap is created on the GUI thread (`:287-290`).
- **Only resized covers are cached.** "Large cover art wastes space in our cache and will likely uncache a lot of the
  small covers we need in the library table" (`:291-304`). The cache limit is 32 MB (`src/main.cpp:59`, `:111`).
- **Failure:** the cover is replaced by a placeholder filled with the stored colour, "to avoid high CPU load"
  (issue #9974, `:279-286`).

### beets: fetchart and embedart

- **fetchart source order (default):** `filesystem, coverart, itunes, amazon, albumart, cover_art_url`
  (`beetsplug/fetchart.py:1303-1310`). The first valid candidate wins (`:1549-1571`); `local_only` skips remote
  sources (`:1550`).
- **Filesystem source** (`fetchart.py:1016-1092`):
  - Only the album folder is searched, not subfolders (`:1047-1048`).
  - Images are sorted by which `cover_names` they contain (default `cover, front, art, album, folder`, `:1301`) as a
    **substring**, with a word-boundary regex for "well-named" files (`:1020-1030`, `:1038-1076`).
  - Then the configured `fallback` image, then **any image** unless `cautious` (`:1080-1092`). This is looser than
    Mixxx's rule.
- **Storage:** one art file per **album** (`album.artpath`), not per track.
- **embedart** writes the image into the audio files (`beetsplug/_utils/art.py:44-92`). Options: only JPEG/PNG,
  `ifempty`, `maxwidth`, a `compare_threshold` similarity check. `extractart` writes the first embedded image out
  (`:171-200`). **Wavee must not copy this**: it modifies the user's files.

---

## 6. Performance tricks and published numbers

**No published numbers in either repo.** No benchmark, no tracks-per-second figure. Mixxx only instruments: a
`ScopedTimer` on each task (`recursivescandirectorytask.cpp:24`, `importfilestask.cpp:24`) and a summary at the end
of each scan (duration, unchanged/scanned directories, new, moved, missing; `libraryscanner.cpp:494-505`). beets'
changelog says only "much faster" for incremental skips (`docs/changelog.rst:4469-4470`).

**Tricks worth noting:**

- Mixxx loads all known locations and directory hashes into memory once per scan (`libraryscanner.cpp:208`, `:225`),
  so lookups during the walk never touch the DB.
- Mixxx uses one write transaction for the whole scan, with prepared statements (`trackdao.cpp:411-430`).
- Mixxx sorts each directory by name "to increase chance that files are sorted sensible" (`recursivescandirectorytask.cpp:40-41`).
- Mixxx collects cover-candidate files during the same directory walk (`:74-78`) and passes them to
  `ImportFilesTask`. They are **never used**: `m_possibleCovers` is stored (`importfilestask.cpp:19`) and nothing
  reads it. Meanwhile `updateTrackFromSource` builds a fresh `CoverInfoGuesser` **per track**
  (`soundsourceproxy.cpp:870-878`), which lists the folder again for every new track that has no embedded art.
- Mixxx skips blacklisted folders (iTunes "Album Artwork", `_Serato_`; `scannerutil.h:10-28`).
- beets compares mtimes before opening any file (`update.py:90`).

**Costs Mixxx pays for each new track during the scan:**

- the TagLib parse, including audio properties;
- a full decode of the embedded image;
- SHA-256 over the decoded pixels;
- a 1×1 smooth scale for the colour;
- a 16-bit checksum over the pixels;
- all of it serial, on one thread.

---

## 7. Known complaints (code comments, changelogs, docs)

### Mixxx

- The scanner has one thread, with a TODO to make that configurable (`libraryscanner.cpp:24-25`).
- **Cancel** did not stop queued `addNewTrack` signals (#14940, `libraryscanner.cpp:686-693`). A cancelled scan
  resumed wrongly until its hashes were cleared (#16300).
- **False missing tracks:** case-insensitive paths, symlink duplicates and "db inconsistency" (#13533, #14513;
  `trackdao.cpp:2116-2120`). There is also a cleanup for a bug where `directory == location`
  (`libraryscanner.cpp:247-250`).
- `cleanUpDatabase` "deletes more directory entries than necessary" (FIXME, `libraryscanner.cpp:49-53`).
- Relocation is skipped when the scan added no tracks; "TODO(xxx) resolve old duplicates" (`trackdao.cpp:1868-1873`).
- `relocateDirectory` "can fail in mysterious ways" with zombie locations (`directorydao.cpp:265-268`).
- **Cover art:**
  - collisions of the 16-bit hash showed the wrong cover (changelog `:2507`);
  - an infinite loop and high CPU when a metadata cover failed to load (#9974, #15199);
  - opening the same file from two threads at once fails on Windows (#11131).
- TagLib reports a tag that is not there (`metadatasourcetaglib.cpp:138-141`).

### beets

- Out-of-band edits are only seen if they change the mtime (`docs/reference/cli.rst:372-375`). The mtime is whole
  seconds and compared with `<=` (`models.py:1101-1105`, `update.py:90`).
- No move detection; the FAQ recommends deleting the database (`faq.rst:225-230`).
- `update` on a vanished last track deletes the album's art file (`update.py:85` → `models.py:1145`, `:414-418`).
- Incremental import is keyed by directory, so new files in an imported directory are skipped
  (`importer/session.py:256-258`).
- "No-op" modifications used to reset mtimes and cause needless writes (changelog `:3045-3048`).

---

## What Wavee should copy (ranked)

1. **Keep plan 3.6's `(Size, ModifiedTicks)` check, compared with `!=`, taken from the enumeration itself.** This is
   strictly better than Mixxx's name-only hash, which misses edits in place, and beets' whole-second `<=`, which misses
   same-second edits and older replacements. Read both values from the `FileSystemEnumerable` entry, not from a second
   `FileInfo` stat. An unchanged rescan then costs one directory listing per folder, opens no file, and does no write.
   Skip the save when nothing changed.
2. **Tombstones instead of dropping entries (Mixxx `fs_deleted`).**
   - A file that is not seen stays in the index with a `MissingSince` stamp and is excluded from matching.
   - Sweep only after a scan **completes for that root**: never on cancel, never for an offline root. Mixxx learned
     both lessons through bugs (#16300, false missing).
   - Purge tombstones after N clean scans or N days.
   - This makes item 3 possible, and it lets a drive that comes back restore the same entries with no re-read.
3. **Move and rename detection that never re-reads (an improvement on Mixxx's rule).** For each path that is new
   in this scan, try in order:
   1. A tombstone with the **same `(Size, ModifiedTicks)`** (and the same extension): reuse its tags without opening
      the file. A move or rename on the same volume keeps both values.
   2. Mixxx's rule: **same file name, duration within 1 s**, ties broken by the **longest common path suffix**
      (`trackdao.cpp:1877-1967`). This one needs a tag read.

   Carry the old identity over. For `spotify:local:` rows nothing has to change: they match by tags, so a moved file
   matches again by itself. The path-based `wavee:local:file:<base64(path)>` ids (plan 3.2) do change, so keep an
   `old id → new id` remap for queues, history and the Local Files list. Log one line per relocation, as Mixxx does.
4. **Mixxx's folder-cover rule over beets'.** Use embedded art first, in the order FrontCover > Media > Illustration
   > Other. Otherwise:
   - one image in the folder: use it;
   - several images: track base name > album tag > `cover` > `front` > `album` > `folder`, each compared to the whole
     base name, ignoring case;
   - otherwise **no cover**. Never use beets' "any image" fallback.

   Take the folder's image list **from the same enumeration** that finds the audio files: Mixxx collects it and then
   throws it away.
5. **Store a cover reference, not pixels.** Per entry: `CoverKind` (None / Embedded / File), the relative file name
   for File, a 64-bit **content key**, and an average colour. Two improvements on Mixxx:
   - hash the **encoded bytes** (the APIC/PICTURE/`covr` payload or the image file), not decoded pixels, so the scan
     never decodes an image;
   - record the embedded picture's **offset and length** in the file, so a later load is one seek and one read
     instead of a tag parse.

   Album tracks usually share identical embedded bytes, so the key de-duplicates: one decode and one thumbnail per
   album.
6. **Mixxx's cover cache:** load on a worker, de-duplicate in-flight loads by key (this also avoids Windows #11131),
   cache only thumbnails at the size requested, and keep a byte budget. On failure, show a placeholder in the stored
   colour and **remember the failure**, so a row never retries every frame (#9974, #15199).
7. **Safety guards:** a root that is missing or unreadable keeps its entries and is marked offline (Mixxx
   `libraryscanner.cpp:282`; beets refuses to continue without a prompt, `update.py:177-181`). The plan already does
   this, so keep it. Compare paths with `OrdinalIgnoreCase` and skip reparse points; Mixxx's false-missing bugs came
   from case and symlink duplicates.
8. **A scan summary line in the log, as Mixxx prints it:** duration, folders unchanged/changed, new, moved, missing,
   rediscovered, unreadable. It answers "the scan is slow" and "my file is grey" reports without a debugger.

## What to avoid

- **A directory hash of names only** (Mixxx). It cannot see an edited file, and on Windows a per-file size and mtime
  costs the same.
- **Hard deletes on rescan** (beets `update`). They lose identity, make a move look like delete plus add, and in
  beets they even delete the art file.
- **Decoding and hashing cover pixels during the scan** (Mixxx: decode, SHA-256 over pixels, 1×1 smooth scale, 16-bit
  checksum for each new track). Hash the encoded bytes and decode only when a cover is first shown.
- **Listing a folder once per track for cover guessing** (Mixxx's fresh `CoverInfoGuesser` per track). Do it once
  per folder.
- **A 16-bit cover hash** (Mixxx's legacy hash). It produced wrong covers. Use at least 64 bits.
- **Whole-second mtime with `<=`** (beets). Use ticks plus size, compared with `!=`.
- **Incremental scans keyed by directory** (beets `taghistory`). They skip new files in known directories.
- **Splitting "Artist - Title" out of untagged file names** (Mixxx `soundsourceproxy.cpp:805-836`). The official
  client uses the **whole** file name without the extension as the title (plan 3.4), so a split would break matching.
- **Tag reading serialised on one thread with the database** (Mixxx). Wavee has no DB in the loop: read tags on the
  scan thread (or a small bounded set of workers for a first scan of a large library on an SSD) and build entries in
  memory. The single atomic TSV save the plan already specifies is right.
- **Writing to the user's files** (beets embedart, Mixxx export). Wavee's index is read-only. Keep it that way.
- **Relying on a file watcher alone.** Neither project uses one: Mixxx has rescan-on-startup and a manual rescan
  (`library_prefs.cpp:16-19`), beets only manual commands. A cheap full rescan is the safety net under the plan's
  `FileSystemWatcher`. Treat a watcher overflow or error as "rescan that root".
