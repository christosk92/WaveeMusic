# Windows file-system research for the 0.4 local index

Scope: how Wavee (C#/.NET NativeAOT, unpackaged and MSIX full-trust, no admin) should enumerate, watch,
identify and read a music library of a few hundred to 100k+ files on NTFS SSD/HDD, exFAT/FAT32 USB,
SMB shares and OneDrive folders. Research only, no code. Researched 2026-11-23.

How much to trust each claim:
- **[doc]**: Microsoft documentation or the Windows SDK header (`winioctl.h`, SDK 10.0.26100) says so.
- **[measured]**: a third party measured it. The source is named. These are not our numbers.
- **[inferred]**: my reasoning or common practice that no source here confirms. Measure it before relying on it.

---

## 1. Enumerating a tree without admin

### What each API returns without opening each file

| API | Per-entry data, from one batched call per directory | Notes |
|---|---|---|
| .NET `Directory.EnumerateFiles` / `FileSystemEnumerable<T>` | Name, attributes, `Length`, creation/access/write times (`FileSystemEntry`) | Calls **`NtQueryDirectoryFile` with `FileFullDirectoryInformation`** **[doc: runtime source]**. Default buffer is **4 KB** (`StandardBufferSize = 4096`), adjustable through `EnumerationOptions.BufferSize` (minimum 1 KB). It opens subdirectories handle-relative (`NtCreateFile`) and recurses through a queue on one thread. The NT data also carries `ChangeTime`, `EaSize` and, for reparse points, the reparse tag, but `FileSystemEntry` exposes neither the file id, `ChangeTime` nor the tag. |
| `FindFirstFileExW(FindExInfoBasic, FIND_FIRST_EX_LARGE_FETCH)` | `WIN32_FIND_DATAW`: attributes, three times, size, name, reparse tag in `dwReserved0`. With `FindExInfoBasic` no 8.3 name is generated | `LARGE_FETCH` means "uses a larger buffer for directory queries" (Win7+) **[doc]**. No file id and no ChangeTime. |
| `GetFileInformationByHandleEx(FileIdBothDirectoryInfo)` | `FILE_ID_BOTH_DIR_INFO`: name, short name, 4 times (incl. **ChangeTime**), EndOfFile, AllocationSize, attributes, EaSize, **64-bit FileId** | "No specific access rights are required to query this information" **[doc]**. Works on SMB 3 and ReFS **[doc]**. |
| `GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)` (0x13) | `FILE_ID_EXTD_DIR_INFO`: as above plus **128-bit FileId** and an explicit **ReparsePointTag**, no short name | Needed on ReFS, where the 64-bit id "is not guaranteed to be unique" **[doc]**. Support on FAT and third-party SMB servers is unverified, so fall back to `FileIdBothDirectoryInfo` on `ERROR_INVALID_PARAMETER`/`STATUS_INVALID_INFO_CLASS` and cache the result per volume **[inferred]**. |
| `NtQueryDirectoryFile(Ex)` with the same classes | Same structures. Lets you pick the buffer, restart scans and use handle-relative opens | This is what both of the above call. Through LibraryImport it is the most direct and AOT-friendly path. |

**Bottom line:** one `NtQueryDirectoryFile` / `GetFileInformationByHandleEx` call per directory with a
64 KB buffer and `FileIdExtdDirectoryInfo` (falling back to `FileIdBothDirectoryInfo`) returns the
whole change-detection key for every child without a per-file open: **size, LastWriteTime, ChangeTime,
attributes (incl. cloud-placeholder bits), reparse tag and file id**. .NET's enumerator gets the same data
from the kernel but discards the id and ChangeTime, so Wavee needs its own small P/Invoke walker.

### Measured numbers
- Per-file stat versus directory enumeration: `GetFileAttributesEx` per file is **~50–113× slower** than
  `FindFirstFileEx(Basic)` for 1k–5k files (e.g. 5,000 files: 0.676 s vs 0.006 s) **[measured: cppstories]**.
  The speedyleion `git status` study (~90k files) found per-file `GetFileAttributesExA`/`stat` ≈ **1.6 s**
  against **0.77 s** even for `NtQueryDirectoryFile` returning *one entry per call* **[measured]**.
  **Never stat files one by one.**
- `FileIdExtdDirectoryInfo` batch against a plain `FindFirstFileExW` walk on `C:\Windows` (264k entries, 68k
  dirs): **5,334 ms vs 6,429 ms**. On a generated 1M-file tree: 207 vs 266 ms **[measured: dusage PR #99]**.
- **The per-directory open dominates:** 1M entries in 1,000 dirs ran at **3.8M entries/s**, while `C:\Windows`
  (about 4 entries per dir) ran at **41k entries/s** **[measured: dusage]**. Music libraries average about 10–15
  files per album folder, so they sit closer to the fast end.
- Buffer size: 64 KB, 256 KB and 1 MB were within noise of each other **[measured: dusage]**. Use 64 KB. That is
  also the SMB-safe size.
- `FIND_FIRST_EX_LARGE_FETCH`: ~25% faster on flat or wide trees, 8% slower on the deep real tree **[measured: dusage]**.
- **Parallelism:** 1→8 threads took the 1M tree from 328 to 141 ms and `C:\Windows` from 2,448 to 752 ms. Both
  **plateaued at 8 workers** and got 6–13% *slower* beyond that (a latency ceiling, not a CPU one) **[measured: dusage,
  NVMe-class i7-13700KF, media type not stated]**.
- Cold versus warm cache with the same walker: 600k files **4,086 → 627 ms**, 286k files **3,566 → 378 ms**
  **[measured: rilldisk]**. A cold walk is about 6–10× a warm one.
- Reading the MFT directly is about 70× faster cold (1.2M files: 4.9 s vs 350 s) but **needs admin**
  (`\\.\C:` with `GENERIC_READ` returns error 5) **[measured: rilldisk, dusage]**. It is not an option for Wavee.

---

## 2. The NTFS USN change journal

- The documented FSCTLs (`FSCTL_QUERY_USN_JOURNAL`, `FSCTL_READ_USN_JOURNAL`, `FSCTL_ENUM_USN_DATA`) take a
  **volume handle** (`\\.\X:`), and the docs say "you must have system administrator privileges" **[doc]**.
  `READ_USN_JOURNAL` is **not supported over SMB** **[doc]**. It is NTFS only (ReFS has its own journal, V3 records).
- **An unprivileged path exists:** `FSCTL_READ_UNPRIVILEGED_USN_JOURNAL` = `CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 234,
  METHOD_NEITHER, FILE_ANY_ACCESS)` (0x903AB), defined for `_WIN32_WINNT_WIN10_RS1` (1607) in the SDK header
  **[doc: winioctl.h]**. Microsoft Learn has no page for it. A standard-user spike on Windows 11 (26100/26200) found **[measured: PeekBuddy spike 0001]**:
  - It works with any of three handles: the volume GUID path with a trailing backslash opened **as a directory**;
    `\\.\C:` opened with **`FILE_TRAVERSE`** only; or `C:\` opened with **backup semantics**.
  - On those handles `FSCTL_QUERY_USN_JOURNAL`, `FSCTL_GET_NTFS_VOLUME_DATA` and **`FSCTL_READ_FILE_USN_DATA`** also work unelevated.
  - **Records carry FRN, ParentFRN, Reason and timestamps, but the name is always empty**, even for an admin token.
    Records for ACL-denied directories still arrive (without names).
  - Change-to-readable latency on NTFS: **p99 3.4–5.9 ms**. An overlapped read parks at the journal head, and
    `CancelIoEx` ends it (error 995).
  - Not tested there: Windows 10 22H2, Dev Drive, 100k-directory FRN maps.
  - Their verdict was NO-GO **for their product** because they needed names. **For Wavee the lack of names matters
    much less:** the index already stores every directory's file id, so `ParentFRN` → "rescan this directory" (or
    `FRN` → known track → `OpenFileById` + `GetFinalPathNameByHandle`) is enough **[inferred]**.
- **Journal identity and wrap** **[doc]**: the journal has a 64-bit `UsnJournalID`. NTFS "restamps a change journal
  with a new identifier" when records may be unusable (for example after a volume went through another NTFS version,
  dual boot or removable media). Store `(volume serial, JournalID, NextUsn)` per volume. On start, query: if the ID
  changed, or the stored USN is below `FirstUsn`/`LowestValidUsn` (the read fails with `ERROR_JOURNAL_ENTRY_DELETED`),
  the window is lost, so do a full warm rescan of that volume. `ERROR_JOURNAL_NOT_ACTIVE` or
  `ERROR_JOURNAL_DELETE_IN_PROGRESS` → no journal, use the walk. The default max size is typically **32 MB**
  **[doc: voidtools page]**, so on a busy system drive it can wrap within days. Expect wrap to be common
  after long gaps.
- **How Everything is fast:** it indexes all local fixed NTFS volumes and keeps up through the USN journal
  **[doc: voidtools]**. The initial build reads the MFT, which needs admin (its service). FAT, exFAT and network
  drives get **folder indexing with periodic rescans (daily at 03:00 by default)** plus a 64 KB change buffer, and
  "fast rescan" only re-enumerates folders whose modified date changed (NTFS-like semantics only) **[doc]**.
  An unprivileged app can copy all of this except the MFT read: a parallel directory walk for the initial build,
  then the unprivileged journal or `ReadDirectoryChangesW` to stay current.

---

## 3. Live watching (`ReadDirectoryChangesW` / `FileSystemWatcher`)

- **Buffer:** the kernel allocates the change buffer on the first call and keeps it for the handle's lifetime.
  On overflow the call "still returns true" with **0 bytes** (or fails with **`ERROR_NOTIFY_ENUM_DIR`**), and you
  "should compute the changes by enumerating the directory or subtree" **[doc]**. Over the network a buffer
  **> 64 KB fails with `ERROR_INVALID_PARAMETER`** **[doc]**. .NET `InternalBufferSize` is non-paged memory,
  max 64 KB for network paths **[doc]**.
- **Names:** "it is unspecified" whether you get the 8.3 or the long name, and it can differ between events for
  the same file **[doc/archived MS blog]**. Normalise through the parent directory plus a rescan of that directory.
- **Moves and renames:** a rename within one directory arrives as a `RENAMED_OLD_NAME`/`RENAMED_NEW_NAME` pair.
  Moving a folder into the watched tree reports **only the folder** as new, not its contents **[doc: FSW]**.
  A move across directories shows up as remove plus add (multiple events) **[doc: FSW "several OnChanged and some
  OnCreated and OnDeleted"]**, so only a file id can pair them.
- **`ReadDirectoryChangesExW` + `ReadDirectoryNotifyExtendedInformation`** (Win10 1709+): each event carries
  **FileId, ParentFileId, size, all four times and attributes** **[doc]**. That pairs a cross-directory remove
  and add into a move with no extra I/O. "Currently supported only for the NTFS file system" **[doc]**, so use
  the plain call on FAT, exFAT and SMB.
- **SMB:** `ReadDirectoryChangesW` is supported over SMB 3 **[doc]**. A recursive watch is one CHANGE_NOTIFY per
  handle, so watch the **root with `bWatchSubtree=TRUE`, not one handle per folder**. A user reports UNC watches
  failing after **~512 folders** with `ERROR_NO_SYSTEM_RESOURCES` (1450) **[measured: MS Q&A, unexplained]**.
  How reliable recursive notifications are on Samba or NAS servers depends on the server (inotify is
  per-directory) **[inferred]**. Treat SMB events as hints and reconcile periodically.
- **SMB client caches can make a rescan stale:** the directory cache (10 s), file info cache (10 s) and
  file-not-found cache (5 s) are all on by default **[doc]**. Debounce SMB rescans by at least 10 s after an
  event, or rescan twice.
- **FAT, exFAT and USB:** notifications work locally. Disks are watched "as long as they are not switched or removed"
  **[doc: FSW]**. **An open watch or enumeration handle on a USB volume blocks safe removal.** Register
  `CM_Register_Notification(CM_NOTIFY_FILTER_TYPE_DEVICEHANDLE)` (no HWND needed) and close the handles on
  query-remove **[doc: device-removal docs, inferred for the watcher case]**.
- **Sleep and hibernate:** a local handle survives, and no changes happen while the machine is asleep. But a USB
  drive can be edited on another PC, and an SMB session is often **deleted server-side during sleep**, with the
  mapped drive unreachable briefly after resume **[doc: KB 2907020]**. After a network error the watcher handle is
  dead, not quiet **[inferred]**. Subscribe to `PowerRegisterSuspendResumeNotification` and network-change events;
  on resume or reconnect, reopen the watchers with backoff and reconcile (USN catch-up on NTFS, directory-mtime
  rescan elsewhere).
- **Recommended pattern:** one overlapped `ReadDirectoryChanges(Ex)W` per library root (subtree) on an IOCP thread,
  a 64 KB buffer, collect the **dirty directory set** (not file events), debounce 300–500 ms locally and 2–10 s on
  SMB, then **re-enumerate only the dirty directories** and diff against the DB. On overflow
  (`ERROR_NOTIFY_ENUM_DIR` or 0 bytes), mark the whole root dirty and run a warm rescan, which is cheap (§10).
  Events are triggers. **The directory diff is the truth.**

---

## 4. File identity across moves and renames

- **NTFS:** "a file keeps the same file ID until it is deleted" **[doc]**. A rename or move *within the volume*
  keeps the id, so it can be detected as a move: the id disappears from A and appears in B with the same volume
  serial. Ids can be reused after delete (the MFT sequence number in the upper 16 bits makes reuse visible). **The
  safe-save trap:** `ReplaceFile` and temp-plus-rename saves give the path the *replacement's* id **[doc]**. Many
  tag editors save this way, so "same path, new id" must mean *modified*, not *different track*.
- **ReFS:** use the **128-bit** id. The 64-bit one is not unique **[doc]**.
- **FAT and exFAT:** the id is "generated from the first cluster of the containing directory and the byte offset"
  of the entry, so it **changes on move**, can change on rename (longer name) and through third-party defrag
  **[doc]**. No identity across moves. Use heuristics.
- **SMB:** the server passes its ids through. `FileId` "MUST be set to zero" if the server can't supply unique ids
  **[doc: MS-SMB]**. Samba derives ids from inodes, which get **reused** after delete and caused real client
  cache-poisoning bugs; vfs_fruit now returns 0 **[measured: samba-technical]**. Ids from a Windows server's NTFS
  are stable. Treat an SMB id as a hint only, and always confirm with size plus mtime.
- **Volume identity:** a volume GUID path (`\\?\Volume{GUID}\`) survives drive-letter changes on *this machine*.
  It is assigned when the volume is first seen or formatted **[doc]**, so a USB stick gets a different GUID on
  another PC. The **volume serial number** (format-time) is portable. Key a root by
  `(volume serial, GUID path, relative path)`, show the current letter, and re-resolve the letter through
  `GetVolumePathNamesForVolumeNameW` when the volume appears.
- **Move-detection rule [inferred]:**
  1. Same path → same track (update when the key changed).
  2. Path gone + new path with the same `(volume serial, file id)` → **move** (NTFS/ReFS; SMB only if size+mtime also match).
  3. Path gone + new path with the same `(size, LastWriteTime, extension)` within the same batch → **probable move**
     (FAT/exFAT/SMB). Confirm with a cheap tag hash when the read is local.
  4. Otherwise delete and add. Keep deleted tracks as **tombstones** for a grace period (for example 30 days), so a
     move done in two app sessions, or a drive coming back, restores play counts, likes and playlists.

---

## 5. Cloud placeholders (OneDrive Files On-Demand)

- **Attributes** **[doc]**:
  - `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` 0x00400000: "not fully present locally… reading the file… will cause
    at least some of the file… to be fetched".
  - `FILE_ATTRIBUTE_RECALL_ON_OPEN` 0x00040000: "no physical representation… only appears in directory enumeration classes".
  - `FILE_ATTRIBUTE_OFFLINE` 0x1000.
  - `FILE_ATTRIBUTE_PINNED` 0x00080000 ("always keep on this device").
  - `FILE_ATTRIBUTE_UNPINNED` 0x00100000 ("free up space").
  - The reparse tag (`IO_REPARSE_TAG_CLOUD_*`) also arrives in the enumeration data.
- **Visibility:** "Most Windows applications see exposed placeholders by default". Windows may *disguise* them for
  specific compatibility-shimmed apps **[doc]**. `RtlSetProcessPlaceholderCompatibilityMode(PHCM_EXPOSE_PLACEHOLDERS)`
  (ntdll, 1803+) forces the exposed view **[doc]**. Call it once at startup so the bits are always visible.
  **It does not block hydration.**
- **There is no public per-process "don't hydrate" API.** `CfSetProcessHydrationPolicy` does not exist in the
  26100 SDK's `cfapi.h` (checked). The CfApi team's answer: hydration policy = `max(app policy, provider policy)`,
  no documented way for an app to set its policy, and partial hydration is not implemented by any provider
  **[doc/MS Q&A]**. The only blocks are user- or admin-side (the OneDrive per-app "block downloads" prompt or
  policy) **[doc/community]**. `FILE_FLAG_OPEN_NO_RECALL` is documented "for use by remote storage systems" (HSM)
  **[doc]**. Whether cldflt honours it for reads is unverified. Do not rely on it.
- **Available without hydration:** everything the directory enumeration returns (**real size, all times,
  attributes, file id**), because the placeholder carries the metadata **[doc: placeholder cookbook says they
  "contain file metadata and thumbnail images"]**. Thumbnails can come from the shell for placeholders
  **[doc]**. Music tags are not available.
- **Policy for Wavee [inferred]:**
  - Index a placeholder as a track from its **path and filename** (artist/album from folder structure, title from
    the filename) with a "cloud" flag. **Never open its data stream during a scan.**
  - Read the tags when the file becomes local, either on first play (the hydration happens anyway) or when the user
    pins the folder ("Always keep on this device" sets PINNED and hydrates). Hydration changes the attributes, and
    the watcher or journal sees that, so the dirty directory is rescanned and the tags are read.
  - Offer an explicit opt-in "Download cloud files to read their tags" with a size estimate.
  - Partially hydrated files (RECALL_ON_DATA_ACCESS while some ranges are local): `CfGetPlaceholderRangeInfo` can
    tell whether the header range is on disk **[doc: cfapi.h]**. That is optional polish.

---

## 6. Windows Search indexer and the shell thumbnail cache as shortcuts

- **Windows.Storage query:** `QueryOptions` with `IndexerOption.OnlyUseIndexer` = "use only indexed content and
  ignore content that has not been indexed". `OnlyUseIndexerAndOptimizeForIndexedProperties` (1709+) returns only
  the prefetched properties "for faster results" **[doc]**. `UseIndexerWhenAvailable` silently falls back to the
  file system (slow, it opens files).
- **OLE DB `SystemIndex`:** Windows Search SQL (`SELECT System.ItemPathDisplay, System.Music.Artist,
  System.Media.Duration … FROM SystemIndex WHERE SCOPE='file:C:/Music'`) through `Search.CollatorDSO` works from
  desktop apps, packaged or not **[doc]**. Under NativeAOT, `System.Data.OleDb` (built-in COM interop) is very
  likely unusable, so raw OLE DB COM through ComWrappers would be needed **[inferred]**.
- **Coverage problems:**
  - Only indexed locations are covered (by default the user profile libraries).
  - **Network shares are not indexed on the client.** Libraries search them live, and the index lives on the
    server if anywhere **[doc/community]**.
  - USB drives and custom folders are not indexed unless the user adds them.
  - The WSearch service can be disabled, and the index lags behind new files.
- **Speed:** no published numbers were found. **[inferred]** One SQL query returning 50k rows of indexed
  properties is plausibly a few seconds, but it is only as complete and as fresh as the index.
- **Verdict:** it is not a dependable source of truth. Use it at most as an *optional first-paint accelerator* for
  folders under an indexed scope, then verify with our own scan. Recommend skipping it for 0.4.
- **Thumbnails:** `IShellItemImageFactory::GetImage` with **`SIIGBF_INCACHEONLY`** "allows access to the disk, but
  only to retrieve a cached item… does not extract" **[doc]**. `SIIGBF_THUMBNAILONLY` avoids the icon fallback.
  `SIIGBF_MEMORYONLY` touches no disk. This is a cheap art source for files Explorer has already thumbnailed
  (including placeholders), with `IThumbnailCache` / `WTS_INCACHEONLY` as the lower-level equivalent. Extraction
  must stay off the UI thread **[doc]**. Wavee's own embedded-art extraction stays primary.

---

## 7. Reading a few bytes fast

- **What is expensive is open and close, not the read.** Antivirus filter drivers hook open and close. Defender
  scanning made **`CloseHandle` take 1–10+ ms** while other calls took tens of µs. Moving `CloseHandle` to a
  thread pool gave **>3× faster file writing** (Mercurial and rustup do this) **[measured: G. Szorc]**. That
  measurement was for writes, which trigger a scan on close. A read-only open is probably cheaper, but the
  first-time scan-on-open of each file still costs **[inferred]**. Measure on a Defender machine. Dev Drive
  "performance mode" makes scans asynchronous ("up to 30%" faster builds) **[doc]**, but music folders are not on
  Dev Drives.
- **Flags [doc]:**
  - `FILE_FLAG_SEQUENTIAL_SCAN` is a hint for reading *large files* sequentially. It triggers aggressive read-ahead.
    For a 4–64 KB header read that read-ahead is wasted I/O, especially on HDD and SMB **[inferred]**. Use either no
    hint or `FILE_FLAG_RANDOM_ACCESS` plus exact-size reads.
  - `FILE_FLAG_NO_BUFFERING` needs sector-aligned buffers and offsets and bypasses the cache. It avoids evicting the
    user's working set during a 50k-file cold scan, but makes the later playback read uncached. Not worth the
    complexity **[inferred]**.
- **Memory-mapped files: avoid them for header reads.** Creating the section costs extra, faults happen at 4 KB
  granularity, and an I/O error on SMB or USB removal surfaces as an `EXCEPTION_IN_PAGE_ERROR` access violation
  instead of an error code, which is fatal in NativeAOT **[inferred]**. Use one `ReadFile` into a pooled buffer.
- **Read strategy [inferred]:**
  - Read the **first 16 KB** in one call: the ID3v2 header gives the tag size, and the FLAC/Ogg/Opus metadata
    blocks and MP4 `ftyp` are at the start.
  - Follow up only when needed: an MP4 `moov` at the end, a big ID3 tag, or an APEv2/ID3v1 trailer (read the
    last 128 B–32 KB).
  - **Skip embedded picture payloads** during the scan. Record the offset and length (APIC, FLAC PICTURE, `covr`)
    and read them lazily for art.
- **Concurrency per medium:**
  - NVMe/SATA SSD: enumeration 8 workers (plateau, §1); open+read **8–16 in flight** **[inferred]**.
  - HDD: **1–2** for both. Process directories in order, read files in each directory sequentially, and consider
    sorting by file id (NTFS MFT order roughly follows creation and locality) **[inferred]**. Detect HDD with
    `IOCTL_STORAGE_QUERY_PROPERTY(StorageDeviceSeekPenaltyProperty)` on `\\.\X:` opened with **`dwDesiredAccess=0`**
    (works without admin per community reports; can return "unknown" on some NVMe) **[doc + community]**.
  - USB flash and exFAT: 2–4 **[inferred]**.
  - SMB: latency-bound, so **8–16** outstanding opens/reads and **4–8** directory queries. Size reads so that
    50k × read size fits the link (50k × 16 KB ≈ 800 MB ≈ 7 s on gigabit) **[inferred]**.
- Do the closes on a small dedicated close pool everywhere **[measured technique: Szorc]**.

---

## 8. Removable and network drives

- **Identify a root** by volume serial + volume GUID path + relative path (§4), and remember the last drive letter
  for display. Resolve letters through `GetVolumePathNamesForVolumeNameW`. Use `GetVolumeInformationByHandleW`
  for the serial and the file-system name (NTFS/exFAT/FAT32/ReFS, which picks the capability set) and
  `GetDriveTypeW` for removable or remote **[doc]**.
- **Offline is not deleted.** If the root is unreachable (`ERROR_NOT_READY`, `ERROR_PATH_NOT_FOUND` at the root,
  `ERROR_BAD_NETPATH`, `ERROR_NETNAME_DELETED`), mark the root **offline** and grey out its tracks. **Never diff an
  unreachable root against the DB.** Rescan on device arrival (`CM_Register_Notification` interface or volume
  arrival) and on network reconnect.
- **Dead SMB shares hang:** the classic `SessTimeout` default is **60 s**, and Win7+ extends it to **4×** when
  `ExtendedSessTimeout` is unset (about 240 s). A vanished share takes "around 60 seconds" to error **[doc: MS
  openspecification blog, troubleshooting]**. **[inferred]** Mitigations:
  - Never touch a remote root on a UI or shared thread.
  - Pre-flight with a TCP connect to port 445 and a 1–2 s timeout before scanning.
  - Run network I/O on dedicated threads with `CancelSynchronousIo`/`CancelIoEx` and a watchdog.
  - Mark the root offline after the first timeout and back off exponentially.
- Mapped letters are per-logon session and can be missing in an elevated or other session. Store the **UNC path**
  (`WNetGetUniversalNameW`) rather than `Z:` **[inferred]**.

---

## 9. MSIX

- Wavee's MSIX is a **full-trust** desktop app (`runFullTrust` / `mediumIL`), not an AppContainer. Win32 file APIs
  then have the **user's normal access** to any folder, network share and USB drive. `ReadDirectoryChangesW`, the
  enumeration APIs and the unprivileged USN FSCTL behave as unpackaged **[doc: "write outside the package —
  allowed if the user has permissions"; inferred for the FSCTL]**.
- **`broadFileSystemAccess`** is a restricted capability for the **Windows.Storage APIs** in apps without full
  file-system access (UWP or AppContainer). It is gated by Settings › Privacy › File system and off by default
  since October 2018 **[doc]**. Wavee does **not** need it as long as it uses Win32 file APIs.
- **AppData virtualization (1903+)** applies to "virtualized apps" **[doc]**: *new* files and folders created
  under `%LOCALAPPDATA%`/`%APPDATA%` go to a per-user, per-package private location, existing files are modified in
  place, and opens look in the private location first. So the packaged build's index DB and art cache land in
  `…\Packages\<PFN>\LocalCache\…`. An unpackaged→packaged migration still *reads* the old DB, but new files
  diverge. Windows 11 can opt out (`unvirtualizedResources` + `desktop6:FileSystemWriteVirtualization`), or use
  `ApplicationData.Current.LocalFolder` explicitly **[doc + inferred]**. Package files are read-only; never write
  next to the exe.

---

## 10. Recommended Windows design for Wavee

**Enumeration**
- A custom NativeAOT walker on `NtQueryDirectoryFile` (LibraryImport), handle-relative subdirectory opens,
  64 KB pooled buffers, class `FileIdExtdDirectoryInformation` with a per-volume fallback to
  `FileIdBothDirectoryInformation`.
- Skip `$RECYCLE.BIN`, `System Volume Information`, hidden/system folders and reparse-point directories
  (junctions and symlinks: never follow, to avoid cycles), plus non-audio extensions before any further work.
- Workers per root by medium: SSD 8, HDD 1–2, USB 2–4, SMB 4–8 directory queries.

**Change-detection key (per file, all from the enumeration)**
- `(size, LastWriteTime, ChangeTime, attributes&cloudbits)`. ChangeTime catches tag editors that preserve mtime.
  On FAT/exFAT and some SMB servers ChangeTime may be 0 or mirror mtime, so drop it from the key there.
- Per directory, store its `LastWriteTime` and file id. A directory whose mtime is unchanged still needs
  enumerating, because a file's size or mtime change does not touch the parent's mtime. Only Everything's
  "fast rescan" trades that away.

**Watching and fallback**
- NTFS local: on start, catch up with **`FSCTL_READ_UNPRIVILEGED_USN_JOURNAL`** from the stored
  `(JournalID, NextUsn)`. ParentFRN → dirty directory, then rescan the dirty directories. If the ID changed, the
  USN was purged or the FSCTL is unsupported → a full warm rescan. Live: `ReadDirectoryChangesExW` (extended info)
  per root, or tail the journal. Gate the journal path behind a capability probe and keep the walker as the
  always-correct fallback: it is undocumented.
- exFAT, FAT and USB: `ReadDirectoryChangesW` (subtree) and a full warm rescan on mount or arrival.
- SMB: `ReadDirectoryChangesW` (subtree, ≤ 64 KB buffer) as a hint, debounce of at least 10 s (redirector caches),
  a rescan on reconnect or resume, and a periodic background reconcile (for example every 30–60 min while the
  app is open).
- Overflow (`ERROR_NOTIFY_ENUM_DIR` or 0 bytes) → rescan the root. A watcher error → reopen with backoff, then rescan.

**Identity**
- Track rows keyed by `(rootId, relativePath)` with `(volumeSerial, fileId128)` stored alongside.
- Move = same file id on the same volume (NTFS/ReFS), or size+mtime+extension in the same batch (FAT/exFAT/SMB).
- Same path with a new id = a modification (safe-save).
- 30-day tombstones keep user data across moves, drive swaps and renames.

**Cloud**
- `PHCM_EXPOSE_PLACEHOLDERS` at startup.
- Files with `RECALL_ON_DATA_ACCESS|RECALL_ON_OPEN|OFFLINE` are indexed from path, size and times only, flagged as
  cloud, and never opened by the scanner.
- Tags are read after hydration (play or pin), detected through the attribute change. An opt-in "download to read
  tags" setting.

**Reads**
- One 16 KB `ReadFile` plus targeted follow-ups. No mmap, no `SEQUENTIAL_SCAN` for headers, closes on a pool.
- Picture payloads are recorded, not read.
- Tag-read concurrency: SSD 8–16, HDD 1, USB 2–4, SMB 8–16.
- Process in directory order and publish results per album folder so the UI fills progressively.

**Target numbers for a 50k-file library (about 4–5k album folders)**
These are design targets from the measurements above, extrapolated **[inferred]**. Measure them on the
owner's machines.

| Scenario | NVMe/SATA SSD | HDD (7200 rpm) | SMB NAS (gigabit) | USB 3 exFAT |
|---|---|---|---|---|
| Cold enumeration only | ≤ 1.5 s | 10–40 s (directory seeks) | 3–8 s | 3–10 s |
| **Cold full scan** (enumeration + tag reads) | **≤ 20 s** (≥ 2.5k files/s) | 3–6 min (seek-bound) | 30–90 s | 1–3 min |
| **Warm unchanged rescan** (enumeration + DB diff, no reads) | **≤ 300 ms** (≥ 150k entries/s at 8 workers) | ≤ 1 s if metadata is cached, else 10–30 s | 2–5 s | 1–3 s |
| Startup with the USN journal intact and nothing changed | **≤ 50 ms** (query + empty read) | ≤ 50 ms | n/a | n/a |
| **Single-file change → UI updated** | **≤ 1 s** (event < 10 ms, debounce 300–500 ms, rescan of one dir + one tag read < 10 ms) | ≤ 1.5 s | 10–15 s (debounce for SMB caches) | ≤ 1.5 s |

Also budget for: first tracks visible in **< 2 s** on any medium (progressive, per folder); the warm rescan never
blocks playback; memory for the in-RAM path→row map ≈ 50k × ~150 B ≈ 8 MB.

---

## Sources

- .NET enumerator implementation (NtQueryDirectoryFile, FileFullDirectoryInformation, 4 KB buffer): https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.Private.CoreLib/src/System/IO/Enumeration/FileSystemEnumerator.Windows.cs
- Custom directory enumeration in .NET Core 2.1 (Kuhne): https://learn.microsoft.com/en-us/archive/blogs/jeremykuhne/custom-directory-enumeration-in-net-core-2-1
- FindFirstFileExW (LARGE_FETCH, SMB support): https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirstfileexw
- GetFileInformationByHandleEx (info classes, SMB/ReFS support): https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex
- FILE_ID_BOTH_DIR_INFO (no access rights needed; FAT id instability): https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_both_dir_info
- FILE_ID_EXTD_DIR_INFO: https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_extd_dir_info
- BY_HANDLE_FILE_INFORMATION (NTFS/FAT/ReFS id semantics, ReplaceFile): https://learn.microsoft.com/en-us/windows/win32/api/fileapi/ns-fileapi-by_handle_file_information
- FILE_ID_INFO: https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info
- Per-file attribute query vs FindFirstFileEx, measured: https://www.cppstories.com/2024/cpp-query-file-attribs-faster/
- NtQueryDirectoryFile vs per-file stat (git status study): https://speedyleion.github.io/rust/git/2021/03/03/speeding-up-windows-file-status-lookup.html
- dusage Windows benchmark (FileIdExtd vs FindFirstFileExW, buffers, LARGE_FETCH, thread scaling): https://github.com/RusticStack/dusage/pull/99
- rilldisk walk/MFT benchmark (cold vs warm, MFT needs admin): https://github.com/lord007tn/rilldisk/pull/12
- FSCTL_READ_USN_JOURNAL (volume handle, not over SMB): https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_read_usn_journal
- Change journal operations: https://learn.microsoft.com/en-us/windows/win32/FileIO/change-journal-operations
- Using the change journal identifier (admin statement, restamping, wrap): https://learn.microsoft.com/en-us/windows/win32/fileio/using-the-change-journal-identifier
- Unprivileged USN spike (handles, no names, latency): https://github.com/nobuddyorg/PeekBuddy/blob/main/docs/spikes/0001-unprivileged-usn.md and https://github.com/nobuddyorg/PeekBuddy/issues/31
- libUSNJournal (FILE_TRAVERSE + unprivileged FSCTL): https://github.com/FaultyPine/libUSNJournal
- Meziantou, reading the USN journal in .NET (unprivileged mode, names may be missing): https://www.meziantou.net/reading-usn-journal-with-dotnet.htm
- Everything indexes (USN, folder rescans, 64 KB buffer, fast rescan): https://www.voidtools.com/support/everything/indexes/
- ReadDirectoryChangesW (overflow, 64 KB network limit, ERROR_NOTIFY_ENUM_DIR): https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-readdirectorychangesw
- ReadDirectoryChangesExW (extended info, NTFS only, 1709+): https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-readdirectorychangesexw
- FILE_NOTIFY_EXTENDED_INFORMATION: https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-file_notify_extended_information
- FileSystemWatcher remarks (buffer, moves, network 64 KB): https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher
- "ReadDirectoryChangesW is evil" (short/long names): https://learn.microsoft.com/en-us/archive/blogs/ericgu/readdirectorychangesw-is-evil
- UNC watch limit (~512, error 1450): https://learn.microsoft.com/en-us/answers/questions/1189410/file-watching-is-limited-in-number-with-unc-path-e
- SMB2 client redirector caches (10 s / 5 s / 10 s): https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-7/ff686200(v=ws.10)
- SMB session deleted during sleep (KB 2907020): https://support.microsoft.com/kb/2907020
- SMB timeouts (SessTimeout 60 s, ×4 extended): https://learn.microsoft.com/en-us/archive/blogs/openspecification/smb-2-x-and-smb-3-0-timeouts-in-windows and https://learn.microsoft.com/en-us/archive/blogs/openspecification/cifs-and-smb-timeouts-in-windows
- Share-gone ~60 s error: https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/negotiate-session-setup-tree-connect-fails
- SMB FileId zero/uniqueness (MS-SMB): https://msdn.microsoft.com/en-us/ms-smb/fbae2fc3-7ff2-4437-b0b0-9536a73bb6ea
- Samba duplicate file ids / cache poisoning: https://lists.samba.org/archive/samba-technical/2021-December/137023.html
- File attribute constants (RECALL_ON_*, PINNED, UNPINNED, OFFLINE): https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants
- RtlSetProcessPlaceholderCompatibilityMode: https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-rtlsetprocessplaceholdercompatibilitymode
- Placeholder files cookbook (metadata and thumbnails in placeholders, shell data model): https://learn.microsoft.com/en-us/windows/compatibility/placeholder-files
- CfApi hydration policy max(app, provider), no app-side API, no partial hydration: https://learn.microsoft.com/en-us/answers/questions/1285988/cfapi-how-to-hydrate-placeholder-file-partially
- OneDrive per-app download block: https://www.winhelponline.com/blog/onedrive-demand-block-automatic-file-downloads/
- CreateFileW flags (SEQUENTIAL_SCAN, OPEN_NO_RECALL, NO_BUFFERING): https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew
- Defender and CloseHandle cost (Szorc, "Surprisingly Slow"): https://gregoryszorc.com/blog/2021/04/06/surprisingly-slow/
- Defender performance mode / Dev Drive: https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-endpoint-antivirus-performance-mode and https://blogs.windows.com/windowsdeveloper/2023/06/01/dev-drive-performance-security-and-control-for-developers/
- IndexerOption: https://learn.microsoft.com/en-us/uwp/api/windows.storage.search.indexeroption
- Windows Search SQL / SystemIndex: https://learn.microsoft.com/en-us/windows/win32/search/-search-sql-windowssearch-entry
- Network locations are not indexed on the client: https://learn.microsoft.com/en-us/answers/questions/520588/windows-client-doesnt-use-windows-server-index-whe and https://learn.microsoft.com/en-us/archive/blogs/seanearp/how-do-i-index-network-shares-in-vista
- IShellItemImageFactory::GetImage (SIIGBF_INCACHEONLY etc.): https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitemimagefactory-getimage
- Naming a volume (volume GUID paths): https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-volume
- SSD detection via seek penalty: https://devblogs.microsoft.com/oldnewthing/20201023-00/?p=104395 and https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-device_seek_penalty_descriptor
- Device removal with open handles: https://learn.microsoft.com/en-us/windows/win32/devio/processing-a-request-to-remove-a-device and https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_register_notification
- Packaged desktop app file-system behaviour (AppData redirection): https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes
- File access permissions / broadFileSystemAccess: https://learn.microsoft.com/en-us/windows/uwp/files/file-access-permissions
- Local SDK header checked: `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\winioctl.h` (FSCTL_READ_UNPRIVILEGED_USN_JOURNAL, RS1) and `um\cfapi.h` (no process hydration-policy API)
