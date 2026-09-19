# playlist-ops fixtures — SCRUBBED re-encodes (wave D3)

The bodies the op replayer (`Spotify/Spotify.Playlist.Ops.cs`) is tested against: `PlaylistDiffDecodeTests` decodes
each one and replays it over its baseline. Plan: `docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md`
§3.7–§3.10.

**Every `.bin` here is a SCRUBBED RE-ENCODE, never a raw capture** (the owner's decision). The sources are four Fiddler
captures of the OFFICIAL desktop client (1.2.96.518, no Wavee traffic) decoded on 2026-09-18/19; the decoded set lives
outside every repo (`C:\WAVEE\wavee-captures\playlist4-2026-09`, index `README.md` there) and names the account's
playlists, so nothing from it is copied here and no scrub script is kept in the tree.

## What the scrub keeps, and what it replaces

Kept, field for field: every field number (unknown ones included), wire type, field order, repeated count and attribute
presence; every varint — op kinds, indices, lengths, flags, nonces, timestamps; the marker rows of the rootlist; format
tokens (`editorial`, `daylist`, …) and attribute keys; the server host a push names.

Replaced, deterministically and CONSISTENTLY across files (one original → one synthetic value everywhere, so a diff's
`from_revision` still equals the baseline read's revision and a REM's carried row still matches its baseline row):

| What | Becomes |
|---|---|
| playlist / track / episode / artist uris | `spotify:<kind>:<synthetic base62 gid>` (valid 128-bit gids) |
| `item_id` | synthetic bytes of the same length |
| usernames (adder, owner, `ChangeInfo.user`) | `user1` (the account), `user2`… (others); `spotify` kept |
| playlist names, descriptions | `Playlist A`…, `Description A`… (an empty string stays empty) |
| folder markers | `spotify:start-group:<synthetic id>:Folder+A` / `spotify:end-group:<synthetic id>` |
| cover urls, picture ids, format-attribute values, per-item UUIDs | synthetic |
| revisions | the 4-byte COUNTER kept; the 20-byte hash synthetic |

Checked after the scrub (scratch tooling, not in the repo): the re-encoder reproduces every capture byte-for-byte before
it replaces anything; the scrubbed structure equals the capture's; no byte sequence of any original identifier (uris,
item_ids, usernames, names, descriptions, folder ids, revision hashes, picture ids, urls, attribute values) occurs in any
file here; every chain below replays exactly as it did on the raw captures.

## The files

`p1` is the playlist the client EDITED in `reorders.saz`; `p2` the playlist edited on a phone and caught up by the
desktop client in `morediffs.saz` / `more.saz`. Capture names: `reorders.saz` (2026-09-18 20:55), `morediffs.saz`
(21:26), `somemore.saz` (09-19 10:07), `more.saz` (10:17).

| File | Source | What it is | What it proves |
|---|---|---|---|
| `p1-push-r29-mov-0-1-2.bin` | reorders.saz, dealer push 1 | `MOV{0,1,2}`, parent r28 | with push 2 and requests r28/r29: `to_index` is PRE-REMOVAL (`at = to − length` forward) |
| `p1-push-r30-mov-3-1-1.bin` | reorders.saz, dealer push 2 | `MOV{3,1,1}` | the backward move that pins where push 1's row landed |
| `p1-push-r31-rem-1-1.bin` | reorders.saz, dealer push 3 | `REM{1,1}` + the removed row | an echoed REM carries its row (identity check has data) |
| `p1-push-r32-rem-x3.bin` | reorders.saz, dealer push 4 | `REM{2,1}` · `REM{4,2}` · `REM{10,1}` | ops in one push are SEQUENTIAL |
| `p1-push-r33-mov-5-8-0.bin` | reorders.saz, dealer push 5 | `MOV{5,8,0}` | an 8-row keyed add_first move echoes as ONE backward block move |
| `p1-push-r34-mov-x3.bin` | reorders.saz, dealer push 6 | `MOV{6,1,4}` · `MOV{8,1,5}` · `MOV{10,1,6}` | a non-contiguous 3-row keyed move echoes as three sequential moves |
| `p1-push-r35-head-only.bin` | reorders.saz, dealer push 7 | `new_revision` only | the 50-row ADD's push is HEAD-ONLY: no ops, no parent — never stored |
| `p1-push-r36-add-123.bin` | reorders.saz, dealer push 8 | `ADD{123}` + the new row | over `p1-read-r35` it lands EXACTLY on `p1-read-r36` (real baseline, real result) |
| `p1-req-r28-mov-after-item.bin` | reorders.saz, session 421 request | keyed `MOV{items, add_after_item}` | ground truth for push 1 |
| `p1-req-r29-mov-after-item.bin` | reorders.saz, session 424 request | keyed `MOV{items, add_after_item}` | ground truth for push 2 |
| `p1-req-r30-rem-keyed.bin` | reorders.saz, session 428 request | keyed `REM{items, items_as_key}` | ground truth for push 3 |
| `p1-req-r31-rem-keyed-x3.bin` | reorders.saz, session 452 request | three keyed REMs in one delta | ground truth for push 4 |
| `p1-req-r32-mov-add-first-x8.bin` | reorders.saz, session 458 request | keyed `MOV{8 items, add_first}` | ground truth for push 5 |
| `p1-req-r33-mov-after-item-x3.bin` | reorders.saz, session 467 request | keyed `MOV{3 items, add_after_item}` | ground truth for push 6 |
| `p1-req-r35-add-after-item.bin` | reorders.saz, session 511 request | keyed `ADD{items, add_after_item}` (`Add` field 7) | the anchor ADD: over `p1-read-r35` it also lands on `p1-read-r36` |
| `p1-changes-r35-resync.bin` | reorders.saz, session 498 response | `/changes` answer with `changes_require_resync` | a resync-flagged answer is never replayed |
| `p1-read-r35.bin` | reorders.saz, session 500 | full read, rev 35, 198 rows | real baseline for push 8 / request 511; its first 13 rows end the reorders chain |
| `p1-read-r36.bin` | reorders.saz, session 514 | full read, rev 36, 199 rows | real result of push 8 |
| `p2-read-r6.bin` | morediffs.saz, session 85 | full read, rev 6, 4 rows | real baseline for 6→11 |
| `p2-diff-r6-r11.bin` | morediffs.saz, session 435 | `/diff` 6→11: `MOV{0,1,2}` · `REM{3,1}` · `ADD{3}` · `ADD{4}` · `ADD{5}` | a multi-revision gap is ONE flat sequential positional list; the REM's row matches the baseline by item_id |
| `p2-diff-r11-r11-empty.bin` | somemore.saz, session 46 | `/diff` answer, from == to, 0 ops | the "unchanged" diff (the 304 equivalent that carries a body) |
| `p2-diff-r11-r14.bin` | more.saz, session 57 | `/diff` 11→14: `MOV{0,3,5}` · `REM{0,2}` · `UPDATE_LIST_ATTRIBUTES` | forward block move (only the pre-removal reading is in range), a 2-row REM, a rename + first description (old `no_value` = description) in one diff |
| `rootlist-read-r134.bin` | reorders.saz, session 37 | rootlist full read, rev 134, 37 entries | real baseline for 134→135 |
| `rootlist-diff-r134-r135.bin` | morediffs.saz, session 58 | `/diff` 134→135: `ADD{0}` | rootlist ADD at 0; lands exactly on `rootlist-read-r135` |
| `rootlist-read-r135.bin` | somemore.saz, session 36 | rootlist full read, rev 135, 38 entries | real result of 134→135 and real baseline for 135→137 |
| `rootlist-diff-r135-r137.bin` | more.saz, session 58 | `/diff` 135→137: `ADD{0, [start-group, end-group]}` · `MOV{2,1,1}` | create folder = ONE ADD of TWO markers; a move into it is a plain MOV between them; lands exactly on `rootlist-read-r137` |
| `rootlist-read-r137.bin` | more.saz, session 41 | rootlist full read, rev 137, 40 entries | real result of 135→137; every playlist entry's meta item carries fields 7 and 9, a folder marker's is empty |
| `editorial-push-head-only.bin` | more.saz, dealer push 1 | an editorial list's daily refresh: counter 0, no parent, no ops | head-only; revision counters are not ordered across refreshes |
| `listen-later-diff-contents.bin` | more.saz, session 55 | `/diff` answered with full `contents` (2 episodes) | a `/diff` may answer a snapshot; `ListAttributes` 16 = 1, `SelectedListContent` 23 = 0 |

## Derived expectations (not captures)

No capture read `p2` at revision 11 or 14 in full, so these two lists are DERIVED — the rows of `p2-read-r6` replayed by
the captured diffs under the rules above — and they are the only expectations here that are not a real read:

| File | Derived from | Cross-checked by |
|---|---|---|
| `p2-r11.expected.txt` | `p2-read-r6` + `p2-diff-r6-r11` | the diff's REM carries exactly the baseline row at index 3 (by item_id) |
| `p2-r14.expected.txt` | `p2-r11.expected.txt` + `p2-diff-r11-r14` | `MOV{0,3,5}` is in range only in pre-removal coordinates, and `REM{0,2}` carries exactly the two rows that then sit at 0 and 1 |

One `uri item_id` per line.

## Replay chains with a real baseline AND a real result

- rootlist 134 → 135 → 137 (`rootlist-read-r134` + two diffs ⇒ `rootlist-read-r135`, `rootlist-read-r137`).
- `p1` 35 → 36, twice: the positional echo (`p1-push-r36-add-123`) and the keyed request (`p1-req-r35-add-after-item`).
- `p1` 28 → 34 (the reorders session): over one state built from the rows the requests name (plus the real r35's rows
  11 and 12 between them), every push and the keyed request it echoes land the same rows, four index REMs pass their
  identity checks, and the result is the real `p1-read-r35`'s first 13 rows.
