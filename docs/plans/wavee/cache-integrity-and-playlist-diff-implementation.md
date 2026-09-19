# Cache integrity, persisted playlists with `/diff`, and the request-shape fixes (Wavee 0.3)

Written 2026-09-18 against `C:\WAVEE\wavee-0.3` @ `02f22cce` + the uncommitted 09-18 tree. Every claim about 0.3 was
read in source that day; every 0.2.x pattern is quoted from `C:\wavee\WaveeMusic\src\apps\Wavee` (0.2.10) and the WinUI
app at `0d0429a0` (via `git show`). Evidence for every defect is a log line from the always-on `wire.call` log added the
same evening (`Platform/Platform.Wire.cs`) or a byte-level read of the user's own `library.db`.

Rules baked in: **no legacy paths** (the `library.db` name, the fingerprint-mismatch delete block in `Store.Open`, the
"edges with strings stay off disk" rule, the sidebar's membership-for-a-count ask, the inline `Pump()` and
`LookUp`-style per-row asks are *deleted*); **derived facts live on the model**; **no page-side fetch windows** — and
this plan finally makes the other half true (*the query layer batches*); **no environment switches**; **no source-text
tests**; decisions in pure engine-free classes with tests; **every fix references its issue** (§9); subagents never
build, test or touch git; the orchestrator runs one Debug + one Release build + one test run per wave.
**Not a 1:1 port of 0.2.x**: §2 lists what is kept and what is deliberately *not* copied, with the reason.

---

## 0. The decision, in one screen

| | 0.3 today (proven) | After this plan |
|---|---|---|
| Playlist tracks, rootlist, playlist revision | never on disk (`Store.RegisterLibraryEdges` doc; `PlaylistShape` doc) ⇒ every launch re-reads every visible playlist in full; a `/diff` is impossible across launches | **rows on disk**, the revision written **in the same transaction** as the rows it describes; first ask after a launch is a `/diff` that usually answers "unchanged" |
| `/diff` with ops | refetch in full ("0.3 has no op replayer") | **pure op replayer** (`PlaylistOps.Apply`); any misfit ⇒ full read; field 20 checked **first** |
| Dealer push for one playlist | **dropped on the floor** (`LibraryPushRules.Classify` only knows `/rootlist`) | resident + `stored == parent_revision` ⇒ ops applied in place, zero HTTP; else mark dirty (anti-herd) and `/diff` on next open |
| Boot | sidebar asks the **membership edge** of every visible rootlist playlist to learn a *count* ⇒ ~30 full `GET /playlist/v2/playlist/{id}` before the first click | boot = rootlist `/diff` + nothing; counts come from the persisted row; membership is asked only by a page that shows it |
| Metadata asks | `Pump()` inline at the end of every `Ensure` ⇒ **~120 POSTs/min, median body ~100 B (one uri)** | one `Fetch.Drain()` per UI tick; buckets fill, then go out as one POST (≤ 300); Playback priority stays immediate |
| Before the session is online | planner sends anyway ⇒ unauthenticated requests to `spclient.wg.spotify.com`, **401 ×4-6**, retried | planner **holds** its buckets until Online; the whole boot burst leaves as a few full batches |
| Cache file | `library.db`, shared by name with 0.2.x and every other build; opened **before** the single-instance gate; `Delete` is per-file and lies about success | **`library.<schema>.db`**; opened **after** the gate; rename-then-delete, all three files or none; stale generations reaped |
| Corrupt cache | 13 launches memory-only on 09-18 with no log line | recovery at open (written 09-18) **and mid-session**; one always-on `store.open` line; lost-intent count logged |
| A discarded collection delta | re-asked itself forever (`/collection/v2/delta` ×940) | ledger forfeited ⇒ full walk (written 09-18); `wire.storm` would have caught it in 60 s |

---

## 1. What was proven, and how

1. **The corrupt cache.** `library.db` passes `integrity_check` on its own (4 726 pages, change counter 56, schema cookie
   30). Its `-wal` carries page 1 of a **different database**: 7 701 pages, change counter 7, cookie 29. SQLite replays
   a foreign WAL over the file ⇒ `SQLITE_CORRUPT` at the first pragma. Two databases lived at one path and a WAL
   outlived its file. Mechanisms present in source: `Store.Delete` deletes `.db`/`-wal`/`-shm` one by one, swallows
   failures and returns `!File.Exists(path)` (main file only); any build with a different DDL takes that path; 0.2.x,
   0.3 Debug and 0.3 Release all use `%LOCALAPPDATA%\Wavee\library.db`; `Entities.Boot` (opens/deletes the store) runs at
   `App.cs:69`, the single-instance gate only inside `Shell.Run` (`Shell.Host.cs:149-163`). Eight crash reports on
   09-17/18. The exact interleaving is not recoverable (that log rotated away).
2. **Playlists never diff across launches.** Session `a7ce5209` (a relaunch 3 min after `4b6fd020`): the same ~30
   playlist ids, each `GET /playlist/v2/playlist/{id}` in full, zero `/diff`. `Fetch.FillRevisions` only sends a held
   revision `when edges.PlaylistTracks.State(slot) == EdgeState.Complete`, and nothing restores either.
3. **Who asks.** `Shell/Sidebar.cs:4464-4467` — `ShouldEnsureCount(...)` ⇒ `s_ensureTracksSlots` ⇒
   `EnsureEdge(FetchEdge.PlaylistTracks, <every visible rootlist playlist>, Visible)`; the only route for that edge is
   the full decorated read (`Fetch.Routes.cs:370`). The pin band does the same (`Sidebar.Host.cs:3086-3097`).
4. **One-uri POSTs.** 122 `extended-metadata` POSTs in 62 s, median `sent=107`. `Fetch.Plan`/`PlanEdge`/`Continue` all end
   in `Pump()`; the only coalescing is accidental back-pressure at `MaxInFlight = 4`. Per-row callers:
   `Sidebar.Host.cs:3144/3164/3175`, `Playback.Host.Context.cs:1083`, `Queue.UI.cs:122-125`, `Rail.UI.cs:457-458`,
   `Home.cs:1188`, and `Playback.Host.cs:680-697` (`EnsureRow`, one POST per Connect delta).
5. **Boot 401s.** `Fetch.Pump` tests only `s_providers[...] is null`; `AccessToken()` is null before the welcome so the
   header is simply omitted; `SpclientBaseUrl()` falls back to `spclient.wg.spotify.com` until the `Hosts` event.
6. **A per-playlist dealer push is ignored** (`Spotify.Encode.cs:803-826`).
7. `Settings ▸ Storage ▸ Clear metadata` is dead: `Settings.Host.cs:77` `ClearMetadataCache` is never assigned.

---

## 2. Patterns from the older apps — kept, and deliberately not copied

**Kept** (each justified by a 0.2.x incident):
- Membership as **ordered rows** `(position, item_id, item_uri, added_by, added_at, chart…)`, the **raw revision written
  in the same transaction** (`SqliteColdStore.ReplaceMembership`: "a torn write can never leave a half-applied membership").
- **One revision gate**: only a well-formed revision may be stored, on every writer (0.2.x lesson: a rootlist push's uri
  bytes were persisted as a revision and "would keep failing every equality gate forever").
- **Never persist a revision whose ops/contents you did not apply** — head-only pushes, resync-flagged answers, torn
  applies mark *dirty* and leave the revision alone.
- The **replayer rules**: indices relative to preceding ops in the batch; `add_first > add_last > from_index`; keyed
  REM/MOV by `item_id`; per-row identity check on an index REM; any misfit throws ⇒ full read.
- **Correctness = revision + `/diff` on open and on reconnect; the dealer is an optimisation.** Boot reads the rootlist
  and nothing else. First open per session revalidates (freshness stamps are memory-only); then a 5-minute window; a
  stale baseline blocks the open on the diff for **≤ 1500 ms** so yesterday's copy is never painted-then-swapped.
- **Rolling identities** (`format == daylist` / a daylist window): an "unchanged" says nothing about the header — re-read
  it after every non-refetch outcome.
- After a diff, **hydrate only the added uris**. Metadata asks are list-shaped, chunked at 300 on uri boundaries.
- "A changed revision can carry byte-identical contents" — compare rows structurally before treating contents as a change.

**Not copied:**
- 0.2.x **never checks `changes_require_resync` on a `/diff` answer** and adopts `contents` on it — the exact shape of
  0.3's bug A1 (the 50-track Eurodance Mix committed as 0). 0.3's `DiffVerdict` already checks field 20; the replayer
  path inherits that check and it is pinned by a test.
- Header, membership and revision in **different transactions/stores**; the rootlist revision as a separate statement.
  Here the revision rides the membership transaction, and the header's persisted `TrackCount` is written in it too.
- **No `truncated`/paging handling.** 0.3 reads in pages: a partial window never stores a revision and never reads
  `Complete`.
- Persisting optimistic rows under the old revision (safe there only because of a durable outbox; 0.3's pending state
  is the in-memory `EdgePending` column) — **only settled membership is written**.
- No account isolation, no schema identity check, no corrupt-file handling at open, no checkpoint at shutdown. (The
  `Store.cs` comment crediting 0.2.9 with a memory-only fallback is not supported by its source; it is removed.)
- WinUI's blob-per-playlist (atomic by construction, but a one-track add re-serialises 10k items and nothing can index it).

---

## 3. The design

### 3.1 The file (wave D1)

```csharp
// Entities/Store.cs
/// <summary>The cache file's name carries the schema it holds: a build with a different DDL opens a DIFFERENT file and
/// never touches this one. Nothing is ever deleted to make room at open — the 2026-09-18 corruption was a fresh
/// database created beside a WAL that survived a per-file delete.</summary>
public static string FileName => $"library.{Fingerprint(Ddl()):x16}.db";     // after RegisterShapes()
public const string FileGlob = "library.*.db*";                              // Storage census, reaper, setup witness
```
- `App.Main`: acquire the single-instance gate **before** `Store.Use` (hoist the acquisition out of `Shell.Run`; `Run`
  takes the already-held gate; the harness arms that skip the gate still reach `Store.Use`). A second launch hands off
  and exits without ever opening a store.
- `Store.Open`: the fingerprint-mismatch block is **deleted** (a name cannot mismatch). `ReadFingerprint` stays for the
  foreign/corrupt verdicts; the open/pragma-stage `SQLITE_CORRUPT|NOTADB` recovery written on 09-18 stays.
- `Delete` becomes all-or-nothing, rename-first (a rename fails atomically while any handle is held):

```csharp
/// <summary>All three files or none. Each is RENAMED aside first; only a fully renamed set is deleted. A set that will
/// not move is left exactly as it was and the caller empties it through SQLite instead (DropEverything).</summary>
static bool Delete(string path)
{
    string tag = ".dead-" + Guid.NewGuid().ToString("N")[..8];
    Span<bool> moved = stackalloc bool[3];
    ReadOnlySpan<string> suffixes = ["-wal", "-shm", ""];                     // the main file LAST
    for (int i = 0; i < 3; i++)
    {
        string f = path + suffixes[i];
        if (!File.Exists(f)) { moved[i] = true; continue; }
        try { File.Move(f, f + tag); moved[i] = true; }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (!moved[i]) { Restore(path, suffixes, moved, tag); return false; }
    }
    foreach (string s in suffixes) TryDelete(path + s + tag);                 // best effort; the reaper gets leftovers
    return true;
}
```
- `StoreFiles.Reap(folder, currentName)` (pure planner + shell executor): at boot, after the gate, delete every
  `library.*` set that is not the current name **and** the legacy `library.db` set **and** any `.dead-*` leftovers —
  each through the same all-or-nothing `Delete`; whatever will not move is left for next time. Pure rule
  `StoreFiles.Plan(names, current) → sets to reap`, tested.
- **Mid-session recovery:** `Fault(step, ex)` gains a verdict — `StoreHealth.OnFault(code, alreadyRecovered)` →
  `Recover | Count`. `Recover` (once per process): stop the store thread, close both connections, `Delete`, re-`Boot`,
  log `store.recovered step=… lostIntents=n`. The intent count is read **before** the close when the file still answers;
  otherwise `lostIntents=unknown`. A second corruption in the same process stays memory-only and says so.
- One always-on line per open: `store.open file=… pages=… walBytes=… fingerprint=… outcome=opened|created|recreated:<why>|memory-only:<why> ms=…`.
- `Settings ▸ Storage ▸ Clear metadata` is wired to `Store.DropCatalog()` (close → `Delete` → `Boot`), `StorageCensus`
  and `Setup.DiskWitnesses` use `FileGlob` (or every install reads as fresh after the rename), `Diagnostics.Probe` uses `FileName`.

### 3.2 Playlists on disk (wave D2)

The schema-named file makes a DDL change free (a new name, the old file reaped), so the text goes in real columns —
not squeezed into `edge.child`:

```sql
CREATE TABLE IF NOT EXISTS list_head(scope_id INT NOT NULL, list TEXT NOT NULL, revision TEXT NOT NULL, total INT NOT NULL,
  written_at INT NOT NULL, PRIMARY KEY(scope_id, list)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS list_item(scope_id INT NOT NULL, list TEXT NOT NULL, position INT NOT NULL, uri TEXT NOT NULL,
  item_id TEXT, added_at INT, added_by TEXT, chart_status INT, chart_pos INT, chart_prev INT, flags INT,
  kind INT, depth INT, folder_id TEXT, folder_name TEXT,
  PRIMARY KEY(scope_id, list, position)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS ix_list_item_uri ON list_item(scope_id, uri);
```
`list` is the playlist uri, or `rootlist:<user>` (the last four columns are the rootlist's marker stream; one table,
because a rootlist *is* a playlist4 list). Account isolation is the existing `scope_id`. `added_by` is **text** — the
in-memory edge holds a user *slot*, which means nothing after a restart; `item_id`, `folder_id`, `folder_name` are
re-interned by the applier on the UI thread (the Pins precedent, `Store.cs:763-782`), never a round-tripped `StringId`.

Write — one transaction, store thread:
```csharp
/// <summary>A list's SETTLED membership and the revision it is true at, together or not at all. Refused (returns false,
/// writes nothing) for: a partial window (truncated, or fewer rows than the head's length), an unparseable revision,
/// a list with EdgePending rows, a resync-flagged answer. PURE gate: ListWrite.MayPersist(...).</summary>
public static bool SaveList(Scope scope, EntityId list, string revision, ReadOnlySpan<ListRow> rows, int headLength);
```
v1 rewrites the list's rows inside the transaction (0.2.x did, for 10k rows, on a background thread without complaint);
`SaveListDelta(ops)` — `UPDATE position` ranges from the replayed ops — is the recorded follow-up, not a v1 blocker.
`playlist.track_count` is updated in the same transaction; `PlaylistShape.Load`'s "`TrackCount == 0` ⇒ unknown" mask is
removed (a restored empty list is a real answer). `PlaylistShape`'s doc paragraph about `Revision` being "left to the
network's next answer" is deleted — the revision now lives in `list_head`, **not** on the row, so a row write can never
advance it.

Read — the edge door gets the disk leg it never had (`Fetch.Edges.PlanEdge` has no `Store.Read` today):
`PlanEdge` → for a list relation whose parent is `Unknown` and not yet disk-asked this session (a new in-memory
`Column<byte>` beside `_asked`) → `Store.ReadList` on the store thread → the applier (UI thread) lands rows + `Complete`
+ `Total` + `Playlist.RevisionId`, **then** the network leg is planned — which, with a revision now held, is the `/diff`.
The rootlist is warmed in `Store.Warm` beside the five library relations (it has one parent: `me`); playlists are read
per parent on first ask — never all at boot.

### 3.3 `/diff`, replayed (wave D3)

`DiffVerdict` today: 304 / `up_to_date` / no ops ⇒ unchanged; ops **or** field 20 ⇒ full read; clean `contents` ⇒ adopt;
509 / other ⇒ full read. New arm: **ops and not field 20 ⇒ replay**.

```csharp
// Spotify/Spotify.Playlist.Ops.cs — CORE, pure, no tables
public static class PlaylistOps
{
    public enum Kind : byte { Add, Rem, Mov, UpdateItem, UpdateList }
    public readonly record struct Op(Kind Kind, int From, int Length, int To, bool AddFirst, bool AddLast, bool ItemsAsKey, int ItemsStart, int ItemsCount);

    /// <summary>Apply a diff's ops to a list, each against the state the PRECEDING ops produced. Returns false — and
    /// leaves <paramref name="list"/> untouched — on any misfit: an index out of range, an index REM whose carried items
    /// do not match the rows it names (by item_id, else uri), a keyed REM/MOV whose item_id is not there, an op shape
    /// this build does not express (add_before_item). The caller then reads the list in full.</summary>
    public static bool TryApply(List<ListRow> list, ReadOnlySpan<Op> ops, ReadOnlySpan<ListRow> opItems);

    /// <summary>The acceptance check 0.2.x never had. The wire gives a diff answer NO `length` (§3.8), so the check is
    /// arithmetic over what was applied: the list must end at baseline + adds − removes.</summary>
    public static bool Reconciles(int baselineCount, int replayedCount, int added, int removed) => replayedCount == baselineCount + added - removed;
}
```
Accept only when `TryApply && Reconciles` **and** the answer's `to_revision` is well-formed; then `SaveList` + stage the
edge + hydrate only the added uris. Otherwise full read. A rolling identity re-asks its header after any non-refetch outcome.

Freshness — pure `ListFreshness.Decide(hasBaseline, dirty, revalidatedAtMs, nowMs, rolling)` →
`Paint | PaintThenRevalidate | RevalidateThenPaint(≤1500 ms) | FullRead`. Stamps are memory-only (first open per session
revalidates). The playlist page, the library pane and the queue's context all go through it.

Dealer — `LibraryPushRules.Classify` learns `hm://playlist/v2/playlist/{id}`; pure
`ListPush.Decide(storedRev, parentRev, newRev, hasOps, resident, pendingLocal, open)` →
`Drop(echo) | MarkDirty | ApplyInPlace | RevalidateNow`. `ApplyInPlace` = the same `TryApply` + `SaveList`, zero HTTP.
A head-only push never stores a revision. The rootlist topic arrives twice (v2 and non-v2): dedupe by revision.

### 3.4 The sidebar stops reading playlists (wave D2, same owner as the projection)

`ShouldEnsureCount` is deleted: a count is a **row** fact (`PlaylistFields.TrackCount`, now persisted with the list), and
a playlist the user has never opened shows no count rather than costing a full read. `needsMembership` (a cover-less
playlist's mosaic) keeps the edge ask — but goes through the disk leg first, and is `Prefetch`, not `Visible`.
Same split in `Sidebar.Host.CollectListedPinAsks`. *(A header-only wire read — `decorate=…` with `length=0` — would give
counts for unopened playlists; whether the gateway honours it is unknown, so it is a §8 experiment the user runs with the
wire log, not a dependency.)*

### 3.5 The planner batches, and waits for the session (wave D4)

```csharp
// Entities/Fetch.cs
static bool s_drainOwed;
/// <summary>Called ONCE per UI tick by the host (beside NextWakeAt's caller). Every Ensure of the tick has landed in
/// its bucket by now, so a bucket leaves as one request — the "query layer batches" half of P4 that never existed:
/// Pump() ran inline at the end of every Plan, so four Ensures in one tick were four one-uri POSTs.</summary>
public static void Drain() { if (!s_drainOwed) return; s_drainOwed = false; Pump(); }

/// <summary>May this provider send right now? False holds its buckets — never drops them — so the boot burst leaves
/// as a few full batches when the session comes Online instead of as unauthenticated requests that 401.</summary>
public static Func<EntityProvider, bool>? CanSend { get; set; }
```
- `Plan`/`Continue`/`PlanEdge`: `Pump()` → `s_drainOwed = true`, except `FetchPriority.Playback`, which still pumps
  inline (the now-playing row must not wait a frame). `Answer`/`Failed` keep their `Pump()`.
- `Bucket`'s key drops `priority` (the `Demand` keeps the max): the sidebar's Visible identity ask and
  `EnsureRootlistRows`' Prefetch ask for the same rows stop being two requests.
- `Spotify.Library.Install`: `Fetch.CanSend = p => p != EntityProvider.Spotify || Spotify.Current.IsOnline;`
  `SyncNow` adds `Fetch.Pump()` after `Fetch.Resume()`. The disk leg is **not** gated (disk before network).
- Per-row callers become span asks: pins (`Sidebar.Host.cs:3144/3164/3175` → the existing `_pinEnsure*` lists), restored
  episodes (`Playback.Host.Context.cs:1083`), queue contexts (`Queue.UI.cs:122-125`), the rail (`Rail.UI.cs:457-458`).
- New always-on line per request the planner sends: `fetch.send subject= kind= edge= rows= need= prio= waitedMs=` and
  `fetch.answer ticket= status= ms= rows= unfilled=` — the `wire.call` line says *what* went out; this says *why*.

### 3.6 Already written on 09-18 (unbuilt — wave D0 verifies them)
`Store.Open` pragma-stage recovery + `IsUnreadableFile`; `Library.ForfeitLedger` + relation-scoped delta counting;
`Platform.Wire` (six clients + images seam + AP/dealer sends + `wire.storm`).

## 3.7 Evidence from `reorders.saz` (2026-09-18 20:55, official desktop client 1.2.96.518 — no Wavee traffic in it)

514 sessions / 38 s, decoded against `playlist4_external.proto`. Decoded trees, 168 binary bodies (8 `/changes`
requests, 151 responses, **8 dealer `PlaylistModificationInfo` pushes**) and the scripts are kept permanently in
`C:\WAVEE\wavee-captures\playlist4-2026-09\saz\` (index: `C:\WAVEE\wavee-captures\playlist4-2026-09\README.md`); they are **not in the repo** — they name the user's playlists, so the user picks which become
`Wavee.Tests/Fixtures/playlist-ops/`.

What it settles:
- **Clients WRITE keyed, the service ECHOES positional.** Requests: `MOV{items, add_after_item}` / `MOV{items, add_first}`,
  `REM{items, items_as_key}`, `ADD{items, add_last}`. Dealer echoes: `MOV{from_index,length,to_index}` with **no items**;
  `REM{from_index,length}` **with** the removed items (so the replayer's per-row identity check has data);
  `ADD{from_index, items}`. The replayer must therefore be solid on the POSITIONAL forms first; keyed forms matter for
  our own optimistic apply.
- **Ops in one delta are sequential**: a 3-op REM echoed as `from 2`, `from 4 len 2`, `from 10`, each against the state
  the previous one left. A contiguous 8-row keyed move collapses to ONE positional MOV; a non-contiguous 3-row move
  becomes THREE.
- **`changes_require_resync` is true for every ADD and false for every MOV/REM**, and the official client obeys it with
  an immediate full GET. A 50-row ADD's dealer push carries **no ops and no `parent_revision`** — a head-only push
  (⇒ `MarkDirty`, never store the head). A 1-row ADD's push does carry a positional ADD. So: replay pushes that carry
  ops; never expect to replay our own ADD's response.
- **`/diff` answers seen:** trivial `diff` (from == to, 0 ops) · bare **304, empty body**, with no conditional header sent
  (the gateway keys it off `revision=`) · a **full `contents` snapshot** on `/diff` for some editorial lists even when the
  named revision was current. Never `up_to_date`, never `multiple_heads=true`, never `contents` beside a `diff`.
  `diff.from_revision` always equalled the requested revision (the §3.3 guard holds).
- **NOT in the capture: a `/diff` answer with real ops.** Every diff was 0-op because the asking client had made the
  edits itself. Needed before D3: edit the playlist on the phone while the desktop client is CLOSED, then open it with
  Fiddler running — that is the one shape still unobserved.
- **The official client `/diff`s its whole rootlist at startup**: 91 `/diff` GETs in 38 s, mostly 304s. So "boot reads
  nothing" (§2) is stricter than Spotify's own client. Decision: keep boot silent, then revalidate stored lists at
  `Prefetch` priority through the per-tick drain, a few at a time, **only for lists that have a baseline on disk** — a 304
  costs ~nothing and the dirty set stays honest without a dealer.
- The official client is itself chatty (171 `extended-metadata` POSTs in 38 s). Wavee's one-uri POSTs are still a
  planner defect (§1.4), but "fewer than the official client" is the honest D4 yardstick, not zero.

Proto gaps to close (owner L2, `Protos/playlist4_external.proto`, with a comment citing this capture):
`Add.add_after_item = 7` (Item; observed — an anchor exactly like `Mov.add_after_item`) and, by symmetry,
`Add.add_before_item = 6` (unobserved — declare as such); `SelectedListContent` field 23 (varint 0/1);
`ListAttributes` 16; `MetaItem` 7 and 9 (every rootlist entry); `ItemAttributes` 17. Unknown fields must keep
round-tripping — nothing is guessed. `PlaylistModificationInfo` 5/6/8/9 confirmed as already documented.
Replayer test cases straight from the capture: the MOV-then-REM of the same row one second apart; the 3-op sequential REM;
the 8-row contiguous move; the 3-row non-contiguous move; the anchor ADD resolving to index 123; the ops-less bulk-ADD push.

## 3.8 Evidence from `morediffs.saz` (21:26, official client as a pure OBSERVER — 846 sessions, no `/changes`)

The missing shape was captured: **two `/diff` answers with real ops** (`C:\WAVEE\wavee-captures\playlist4-2026-09\saz2\fixtures\`:
`058_resp_rootlist_diff_ops_134_135.bin`, `435_resp_playlist_2en3Nu8L99Vkdf48tmS0i3_diff_ops_6_11.bin`), plus the
baseline full read of that playlist at revision 6 (session 85: 4 rows).

- **A multi-revision gap comes back as ONE flat, sequential, POSITIONAL op list.** 6→11 =
  `MOV{from 0,len 1,to 2}` · `REM{from 3,len 1, items=[the removed row, with item_id+added_by+timestamp]}` ·
  `ADD{from 3}` · `ADD{from 4}` · `ADD{from 5}` (each ADD carries its full item). No keyed forms, no `items_as_key`,
  no anchors — server-generated diffs look exactly like the dealer echoes. The REM's echoed row matched baseline[3]
  by `item_id`; the ADDs' indices step 3,4,5 — sequential application, confirmed.
- **`length` is NEVER set on a diff-bearing answer** (0 of 24) — only on full `contents`. So `PlaylistOps.Reconciles`
  (§3.3) has nothing to compare against on the wire. It is replaced by the checks the wire does support:
  `from_revision == stored revision` (held in all 196 `/diff` requests across both captures), every REM's carried rows
  must match the rows it removes **by `item_id`**, every index in range, and the stored `total` must equal
  `baseline + adds − removes` after the apply. Any miss ⇒ full read.
- **The MOV index convention is settled** (from the two captures together — the second alone could not): `to_index` is
  in **pre-removal coordinates** ("insert before original index `to`"), i.e. `at = to > from ? to − length : to` —
  the rule 0.2.x's `PlaylistDiffApplier` used. Proof, raw bytes of `reorders.saz`: the client asked
  *move 2tpW after 7xoU* → echo `MOV{0,1,2}`; then *move 3QGs after 7xoU* → echo `MOV{3,1,1}`. The second is a backward
  move (both conventions agree: final index 1), so 7xoU sat at index 0 and 2tpW at index 1 after the first move — yet
  the first echo said `to=2`. Final = 2 − 1. The 8-row block `MOV{5,8,0}` is consistent. **This pair is test case #1.**
- `/diff` outcomes in this capture: 82 × 304 (all editorial, client already current), 88 × full `contents` (the request
  named `revision=0,…` — no baseline), 22 × empty diff, 2 × ops. `hint_revision` appears only on re-fetches that follow
  a dealer head-only push, and always equals `revision`.
- **All 20 dealer pushes for editorial playlists were head-only** (`new_revision`, no `parent_revision`, 0 ops) — the
  daily refresh. ⇒ `ListPush.Decide` = `MarkDirty`; never adopt the head.
- **`SelectedListContent` field 23** (varint): `1` on every user-owned playlist, `0` on every editorial/algorithmic one
  and on `recents` — 119 of 119, no exceptions; absent on the rootlist. Declare it (unnamed semantics, the correlation in
  the comment); it is a candidate input for "is this a rolling/served list" but nothing depends on it until proven.
- Rootlist full reads answer `Content-Type: application/octet-stream` (not `x-protobuf`). 0.3 keys on the route, not the
  content type — verify in D2 that the rootlist decode does not filter on it.
- Still unobserved: `UPDATE_ITEM_ATTRIBUTES` / `UPDATE_LIST_ATTRIBUTES` in a diff, a 509, `up_to_date`,
  `multiple_heads=true`, and a MOV with `length > 1` moving FORWARD. The replayer implements the forward block move by the
  same rule and the fixture suite says which cases are capture-proven and which are rule-derived.

## 3.9 `somemore.saz` (2026-09-19 10:07) — a negative control

621 sessions / 16 s, official client, **browsing only: no `/changes`, 0 ops anywhere**, no Wavee traffic. Nothing new and
nothing contradicted: 129 `/diff` calls answered 304, empty diff, or full `contents` for `revision=0,…`; all 51 dealer
pushes were head-only editorial refreshes (no `parent_revision`, 0 ops); no new unknown fields; no 4xx/5xx/509 on any
playlist route. So the still-unobserved list in §3.8 stands, and D3 treats each as **rule-derived, full-read on any
doubt**: a forward MOV with `length > 1`, `UPDATE_ITEM_ATTRIBUTES` / `UPDATE_LIST_ATTRIBUTES`, a REM with `length > 1`
inside a `/diff`, rootlist REM/MOV/folder ops and rootlist pushes, 509, `up_to_date`, `multiple_heads`.
The always-on `wire.call` log plus a D3 `list.replay ops= kinds= verdict=applied|fullread:<why>` line is how those get
observed in the field — every full-read fallback names the op shape that caused it.

## 3.10 `more.saz` (2026-09-19 10:17) — the gaps, closed (phone edits, desktop client catching up cold)

361 sessions / 17 s, official client, no `/changes`, no Wavee traffic. Two ops-bearing `/diff` answers
(`C:\WAVEE\wavee-captures\playlist4-2026-09\saz4\fixtures\057_…_diff_ops_11_14.bin`, `058_resp_rootlist_diff_ops_135_137.bin`):

- **Playlist 11→14, ONE flat diff mixing three user actions:** `MOV{from 0, len 3, to 5}` (the first forward block move
  seen) · `REM{from 0, len 2, items=[both removed rows, in list order, full attributes]}` ·
  `UPDATE_LIST_ATTRIBUTES{new: name='renamed', description='added description'; old: name='My playlist #10',
  no_value=[LIST_DESCRIPTION]}`. So a catch-up diff can carry row ops AND a header op together — the replayer applies
  the row ops and the header op lands `Identity` on the playlist ROW in the same commit (`no_value[]` = "this attribute
  was unset", never an empty string). The diff answer's top-level `attributes` is **absent**, like `length`.
- **The forward block move obeys the pre-removal rule — by arithmetic.** The list at rev 11 has 6 rows (derived from the
  rev-6 baseline + the 6→11 ops of §3.8). `to_index = 5` cannot be a final start index for a 3-row block in a 6-row list
  (max 3); it is only meaningful as "before original index 5" ⇒ insert at `5 − 3 = 2`. The following `REM{0,2}` carries
  exactly the two rows that then sit at 0 and 1 (`33Dq…/d990aea6…`, `3kYb…/6b48de8d…`) — consistent. *(Both conventions
  leave those two rows at the head, so the REM check alone would not have decided it; the index range does.)*
- **Rootlist 135→137, VERIFIED AGAINST REAL DATA** (orchestrator replay: baseline = the rev-135 full read from
  `somemore.saz`, 38 rows; final = the rev-137 full read here, 40 rows): `ADD{from 0, items=[start-group:57110fdc926452ef:New+Folder,
  end-group:57110fdc926452ef]}` then `MOV{2,1,1}` ⇒ **exact match**, count = 38 + 2. So: **create folder = ONE ADD with
  TWO items** (start + end marker, adjacent); **move a playlist into a folder = a plain MOV to between the markers**.
  Rootlist rows have no `item_id` — identity for rootlist REM checks is the uri.
- Still unobserved after four captures: `UPDATE_ITEM_ATTRIBUTES`, a folder rename/delete, a **live rootlist dealer push**
  (its topic, its message shape, whether it arrives twice — the desktop socket always connected after the edits), 509,
  `up_to_date`, `multiple_heads`. Each is rule-derived + full-read-on-doubt + named in `list.replay`'s verdict (§3.9).
  The rootlist-push shape is the one that matters for D3's L3: until a capture shows it, a rootlist push only ever
  `MarkDirty`s (one `/rootlist/diff`), never applies in place.

**Replayer test matrix (capture-proven unless marked):** forward MOV len 1 (`reorders`: `{0,1,2}` then `{3,1,1}`) ·
backward block MOV `{5,8,0}` · three sequential non-contiguous MOVs · three sequential REMs · multi-revision flat diff
6→11 (MOV+REM+3×ADD, over the real rev-6 baseline) · 11→14 (forward block MOV + REM len 2 + list-attribute op) · rootlist
ADD×2-items + MOV (real baseline AND real result) · rootlist ADD at 0 · head-only pushes (never store) · resync-flagged
`/changes` answers · `/diff` answering full contents · *(rule-derived)* `UPDATE_ITEM_ATTRIBUTES`, keyed forms for our own
optimistic apply, `add_before_item`.

---

## 4. Files and owners

| File | Change | Owner |
|---|---|---|
| `Entities/Store.cs` | `FileName`/`FileGlob`, `Open` minus the mismatch block, `Delete` rename-first, `store.open`, mid-session recovery, `DropCatalog`, `list_head`/`list_item` DDL, `SaveList`/`ReadList`, rootlist in `Warm`, doc fixes | **S1** |
| **new** `Entities/Store.Files.cs` | `StoreFiles.Plan/Reap`, `StoreHealth.OnFault` (pure) | S1 |
| `App.cs`, `Shell/Shell.Host.cs` | gate acquired in `Main` before `Store.Use`; path from `Store.FileName` | **S2** |
| `Screens/Settings.Host.cs`, `Screens/Setup.Host.cs`, `Screens/Diagnostics.Probe.cs` | glob / name / `ClearMetadataCache` wired | S2 |
| `Entities/Fetch.Edges.cs`, `Entities/Edges.cs` | the edge door's disk leg, the disk-asked mark, applier re-intern + AddRef parity with `CommitRootlist` | **L1** |
| `Entities/Playlist.cs` | `TrackCount` mask removed, shape doc, revision sourced from the list head | L1 |
| **new** `Spotify/Spotify.Playlist.Ops.cs` | `PlaylistOps`, `ListFreshness`, `ListPush`, `ListWrite.MayPersist` (pure) | **L2** |
| `Spotify/Spotify.Api.Library.cs`, `Spotify.Api.Playlist.cs`, `Spotify.Decode.cs` (the list decode only) | the replay arm, ops decode, `SaveList` call, added-uris hydration, rolling header re-ask | L2 |
| `Spotify/Spotify.Encode.cs` (`LibraryPushRules`), `Spotify/Spotify.Library.cs` | per-playlist push, dedupe, dirty set, reconnect revalidate of open + dirty only | **L3** |
| `Shell/Sidebar.cs`, `Shell/Sidebar.Host.cs` | count from the row; membership ask only for a mosaic, Prefetch | **U1** |
| `Entities/Playlist.Page.cs`, the library pane, `Entities/Queue.UI.cs` | `ListFreshness` on open; span asks | U1 |
| `Entities/Fetch.cs` | `Drain`, `CanSend`, bucket key, `fetch.send/answer` lines | **F1** |
| host frame tick (the `NextWakeAt` caller), `Spotify/Spotify.Library.cs` (`Install`/`SyncNow` only) | `Fetch.Drain()` per tick; the Online gate | F1 |
| `Shell/Sidebar.Host.cs` (pins only), `Playback/Playback.Host.Context.cs`, `Shell/Rail.UI.cs` | per-row → span | **F2** |

L3 and F1 both touch `Spotify.Library.cs` — different methods, sequenced (F1 lands in D4 after L3's D3). U1 and F2 both
touch `Sidebar.Host.cs` — U1 owns `CollectListedPinAsks`, F2 owns `ResolveLivePin`; sequenced the same way.

---

## 5. Waves and gates

**D0 — make tonight's tree green (orchestrator).** Debug + Release builds, `Wavee.Tests`, engine gates in `fluent-gpu-pin`.
Nothing below starts on a red tree.

**D1 — the file (S1, S2).** *Gate:* a launch creates `library.<fp>.db`; the legacy set and any `.dead-*` are reaped; a
second launch of another build never opens a store (log: no `store.open` before the hand-off); garbage bytes in the
current file ⇒ `store.open outcome=recreated:unreadable:26`; a corrupt page injected mid-session ⇒ one `store.recovered`.

**D2 — lists on disk (L1, U1).** *Gate (wire log):* cold launch ⇒ one rootlist read, **zero** `/playlist/v2/playlist/{id}`
before a click; open a playlist ⇒ one full read; relaunch, open it again ⇒ **one `/diff`**, rows painted from disk.

**D3 — replay, freshness, dealer (L2, L3, U1).** *Gate:* add a track on the phone with the playlist open ⇒ no HTTP, the
row appears, `list_head.revision` advanced; with it closed ⇒ nothing until opened, then one `/diff` and one metadata POST
carrying only the added uri; a fixture-driven resync-flagged answer ⇒ full read, revision untouched.

**D4 — the planner (F1, F2).** *Gate:* the same 60 s that produced 122 POSTs produces an order of magnitude fewer with
median body ≫ 1 uri; no request before `logged in`; zero 401s at boot; no `wire.storm`.

**D5 — docs + issues (orchestrator):** `Store.cs`/`Fetch.cs` headers, the playlist chapter's readiness rules, CHANGELOG, issues (§9).

---

## 6. Tests (pure; none reads production source)

`StoreFilesTests` (plan names the legacy set, other fingerprints, `.dead-*`; never the current set) · `StoreTests` +=
rename-first delete leaves a held set untouched, garbage file recovers (written), a foreign WAL beside a good file
recovers, `DropCatalog` · `StoreHealthTests` (recover once, then count) · `ListWriteTests` (`MayPersist` refuses partial
windows / pending rows / resync / bad revisions) · `StoreListTests` (round-trip: rows + revision + total, text re-interned,
`added_by` survives a restart as the same user, scope isolation, an aborted transaction leaves the old head **and** rows) ·
`PlaylistOpsTests` (every op form; ordering within a batch; index REM identity mismatch; keyed REM absent; `add_before_item`
refused; failure leaves the list untouched; `Reconciles`) · `PlaylistDiffDecodeTests` (captured `/diff` fixtures incl. the
Eurodance resync answer) · `ListFreshnessTests` · `ListPushTests` (echo, head-only never stores, parent mismatch ⇒ dirty,
pending local ⇒ dirty) · `SidebarProjectionTests` += no membership ask for a count · `FetchDrainTests` (N Ensures in a tick ⇒
one batch; Playback is immediate; a held provider keeps its rows and releases them in one batch; priority merge) ·
`WireRulesTests` (written) · a fake-provider seam test that **a discarded collection delta's next ask is a full walk**.

## 7. Risks

- **The disk leg is a new ordering**: the row's disk read and the list's disk read must both land before the network
  ask, or the first ask after a launch is a full read anyway (correct, just not the prize). Gate D2 measures exactly this.
- **A poisoned persisted list** is forever (0.2.x lessons 1 and 4). Mitigations: `MayPersist`, `Reconciles`, the revision
  gate, and a `list_head.written_by` build stamp so a future decoder fix can invalidate lists written before it.
- **`Drain` adds ≤ one frame of latency** to non-playback asks (8 ms at 120 Hz) — and removes the queueing behind
  one-uri requests that `MaxInFlight = 4` causes today, which is the likelier cause of the 3.5 s playlist load. Measure.
- A rename-based `Delete` can leave `.dead-*` files when a handle outlives the process; the reaper owns them.

## 8. Experiments for the user (wire log, no code)
1. Does `GET /playlist/v2/playlist/{id}?decorate=revision,length,attributes&from=0&length=0` answer a header without
   items? If yes, unopened playlists can show counts for one tiny request each (Prefetch, batched by the drain).
2. Revisit one playlist inside a session today: is there a `/diff`? (Pins whether the in-session path works at all.)

## 9. Issues (filed with `gh` only after the user approves each call)
1. Cache file shared by name across builds; opened before the instance gate; `Delete` not atomic ⇒ foreign WAL corruption — D1.
2. No mid-session store recovery; no `store.open` line; "Clear metadata" dead — D1.
3. Playlist tracks / rootlist / revision never persisted ⇒ no `/diff` across launches — D2.
4. Sidebar forces a full read of every visible playlist for a count — D2.
5. `/diff` ops refetch instead of replay; per-playlist dealer pushes dropped — D3.
6. Planner sends one-uri POSTs (no per-tick drain) and sends before the session is Online (boot 401s) — D4.
7. Collection delta re-ask loop (fixed 09-18) — D0. 8. No outbound request log (added 09-18) — D0.
