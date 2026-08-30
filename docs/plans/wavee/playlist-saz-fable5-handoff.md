# Spotify playlist operations SAZ handoff (Fable 5)

Self-contained paste for Claude Fable 5. Do **not** implement unless asked. This document is the decoded wire + Wavee gap map from two Fiddler captures taken 2026-08-15.

## 0. Purpose

This is a complete research handoff of two official-Spotify-desktop playlist-editing sessions, captured while Wavee was also live on the same account. The human will paste this into Fable 5 so that model can implement playlist-wire alignment without re-reading the SAZs.

| Field | Value |
|---|---|
| Date | 2026-08-15 |
| Owner / user id | `<user-id>` |
| Official desktop | Spotify 1.2.95.453 (`spotify-app-version` 129500453), pid **10160**, UA `Spotify/129500453 Win32_ARM64/Windows 10 (10.0.26300; ARM)` plus xpui CEF `Chrome/146.0.7680.179 Spotify/1.2.95.453` |
| Wavee | spoofed `Spotify/129300667 Win32_ARM64/Windows 10 (10.0.26300.0; ARM)`, pid **13440** |
| Capture A | `playlist_operations.saz` — 14:20:41–14:22:17 +02 (~96s), **481 sessions**, file prefixes **048–528** |
| Capture B | `somemoreplaylists.saz` — 14:34:21–14:35:28 +02 (~67s), **134 sessions**, file prefixes **001–134** |
| Dealer archive (parallel) | `c:\Users\<user>\AppData\Local\Wavee\Logs\dealer\dealer-20260815.idx.ndjson` (27 484 B, 206 rows) + sibling `dealer-20260815.bin` (219 037 B). Decoded vs both HTTP windows — see §9. |

Wavee traffic in both captures is almost entirely **reads** (gzip rootlist GETs, JSON permission/base on `284sizy9BLThJBjc0JQypw` / P2, audio 206, melody/time, one proto gabo). **All 29 writes** (25 in A + 4 in B) are official desktop pid 10160.

## 1. Methodology (copy-pasteable)

### 1.1 SAZ layout

A `.saz` is a zip. After extract:

- `raw/{prefix}_c.txt` — HTTP request (status line + headers + body)
- `raw/{prefix}_s.txt` — HTTP response
- `raw/{prefix}_m.xml` — Fiddler SessionTimers + `x-processinfo` (`spotify:10160` / `wavee:13440`) + `SessionID`

**File prefix ≠ Fiddler SID.** Capture A starts at prefix `048` / SID `100` and ends at prefix `528` / SID `939`. Always cite **both**. Catalogs live at:

- `C:\Users\<user>\AppData\Local\Temp\playlist_operations_saz\catalog.json` (+ `catalog.txt`)
- `C:\Users\<user>\AppData\Local\Temp\somemoreplaylists_saz\catalog.json`

Do not invent IDs. §4.Z / §5.Z list every catalog row.

### 1.2 Decompress

Sniff magic; do **not** trust `Content-Encoding` alone (chunked + gzip often arrives as `00 00 00 01` framed gzip that naive readers mis-label).

| Magic | Encoding | How |
|---|---|---|
| `28 B5 2F FD` | zstd | `Wavee.Backend.Spotify.SpotifyZstd.MaybeDecompressZstd` — **NOT** .NET auto-zstd (truncates multi-frame). Unwrap first; if content-size header missing, stream via `DecompressionStream`. |
| `1F 8B` | gzip | stdlib gzip |
| `00 00 00 01` then gzip | gzip+chunked / HTTP2-ish frame | strip 4-byte frame, then gzip. Wavee JSON permission bodies in A look like this on the wire (`216B magic=3030303030303031`) and decompress to `{"revision":"o5umTmROwTQ=","permissionLevel":"VIEWER"}`. |

Many playlist / XM / pathfinder **responses** are zstd (A: 120 resp-ce:zstd). Wavee rootlist GETs are gzip+chunked.

### 1.3 `/changes` Content-Type lie

`POST …/playlist/v2/{playlist\|user/{user}/rootlist}/changes` is advertised as `Content-Type: application/x-www-form-urlencoded` but the body is **raw protobuf** `ListChanges`. Catalogs often tag `reqKind=other` for this reason. Desktop also sends:

- `spotify-playlist-sync-reason: CAk=` (base64 varint **9**) = normal edit
- `spotify-playlist-sync-reason: CAw=` (base64 varint **12**) = create-via-`/changes`
- `spotify-apply-lenses: auto` on playlist (not rootlist) `/changes`

### 1.4 How to decode

Compile Wavee protos (`src/apps/Wavee/SpotifyLive/Protos/*.proto`, `csharp_namespace` as declared; generated C# is **not** checked in — Grpc.Tools → `obj`). Or parse with `Google.Protobuf` against the same `.proto` files.

| Endpoint | Request message | Response message |
|---|---|---|
| `POST /playlist/v2/playlist/{id}/changes` | `ListChanges` | `SelectedListContent` |
| `POST /playlist/v2/user/{user}/rootlist/changes` | `ListChanges` | `SelectedListContent` (short; revision + resulting_revisions) |
| `GET /playlist/v2/playlist/{id}` | empty | `SelectedListContent` |
| `GET /playlist/v2/playlist/{id}/diff` | empty | `SelectedListContent` (diff in field 6) or **304** |
| `GET /playlist/v2/user/{user}/rootlist` | empty | `SelectedListContent` |
| `GET /playlist/v2/user/{user}/rootlist/diff` | empty | `SelectedListContent` |
| `GET /playlist/v2/list/recents/main/diff` | empty | `SelectedListContent` |
| `GET /popcount/v2/playlist/{id}/count` | empty | `PlaylistPopcount` |
| `GET /playlist-permission/…/permission/base` | empty | desktop: `Permission` proto; Wavee: JSON |
| `POST /playlist-permission/…/permission/base/level` | `SetPermissionLevelRequest` | `SetPermissionResponse` |
| `GET /playlist-permission/…/permission/members` | empty | `GetMemberPermissionsResponse` (empty body = empty map) |
| `POST /extended-metadata/v0/extended-metadata` | `BatchedEntityRequest` | `BatchedExtensionResponse` |
| `POST /playlist/v2/playlist` (Wavee create; **absent** here) | `ListUpdateRequest` | `CreateListReply` |

`ApplyPlaylistSignals` / `ListUpdateRequest` create path were **unused** in these captures.

### 1.5 Revision format

24 bytes = **4-byte big-endian counter** + 20-byte hash. Printed `{n},{hex}` (40 hex chars). Query encoding: comma as `%2C`. A bare comma **509**s the gateway (`PlaylistFetcher.FetchPlaylistDiffAsync` already `%2C`-encodes). Sentinel create base: counter `0` + ASCII `root` padded → printed `0,726f6f74` + 16 zero bytes (`726f6f74` = `root`).

### 1.6 URI ↔ path

- `spotify:playlist:{id}` → `/playlist/v2/playlist/{id}`
- rootlist → `/playlist/v2/user/{user}/rootlist`
- Folder name lives in `spotify:start-group:{id}:{urlencoded-name}` where `+` = space. End marker is `spotify:end-group:{id}` (no name). Rename = index REM of the start-group row + ADD of the same id with a new name; **do not touch the end-group**; keep the original create timestamp on the ADD item.

### 1.7 How we split the work

1. **Inventory** — hosts, roles, unique endpoints, encodings (`catalog.txt` / `inventory.txt`).
2. **Writes** — every `POST …/changes` (25 + 4). Decode `ListChanges` + `SelectedListContent`.
3. **Reads** — full GET / diff / rootlist / popcount / permission / recents / XM. Correlate to the write that just landed.
4. **JSON** — pathfinder, extender, assisted-curation, grants, gabo `action_name`s.
5. **Proto catalog** — field numbers vs Wavee `.proto` (especially Mov field 4 and ChangeInfo field 6).

## 2. Proto → endpoint lookup

Wavee has **40** protos under `src/apps/Wavee/SpotifyLive/Protos` (generated C# not checked in; Grpc.Tools → `obj`): `authentication`, `audio_attributes`, `audio_files_extension`, `autoplay`, `client_token`, `collection2v2` (unused in these captures), `connect`, `connectivity`, `devices`, `entity_extension_data`, `event_sender_envelope`, `event_sender_events`, `extended_metadata`, `extension_descriptor`, `extension_kind`, `herodotus`, `keyexchange`, `l5_client_info`, `l5_code`, `l5_credentials`, `l5_hashcash`, `l5_identifiers`, `l5_login5`, `l5_user_info`, `lean_metadata`, `list_metadata_v2`, `media`, `metadata`, `player`, `playlist4_external`, `playlist_permission`, `popcount`, `prerelease`, `recents_group_metadata`, `recommended_playlists`, `storage-resolve`, `three_band_waveforms`, `transfer_state`, `video_associations`, `visual_identity_trait`. No mercury/follow protos — playlist follow is rootlist ADD/REM.

Playlist-relevant field maps:

### 2.1 `playlist4_external.proto` → `Wavee.Protocol.Playlist`

`Op.Kind` (field 1):

| Value | Name | Payload field |
|---|---|---|
| 0 | `KIND_UNKNOWN` | — |
| 2 | `ADD` | `add = 2` (`Add`) |
| 3 | `REM` | `rem = 3` (`Rem`) |
| 4 | `MOV` | `mov = 4` (`Mov`) |
| 5 | `UPDATE_ITEM_ATTRIBUTES` | `update_item_attributes = 5` |
| 6 | `UPDATE_LIST_ATTRIBUTES` | `update_list_attributes = 6` |

`Add`: `from_index=1`, `repeated Item items=2`, `add_last=4`, `add_first=5`.

`Rem`: `from_index=1`, `length=2`, `repeated Item items=3`, `items_as_key=7`. Keyed REM sets `items_as_key=true` and puts URIs in `items` (no index). Index REM (rootlist delete P3) sets `from`+`length` **and** still carries the uri in `items`.

`Mov` **as checked in** (`playlist4_external.proto` lines 92–96):

```
message Mov {
    required int32 from_index = 1;
    required int32 length = 2;
    required int32 to_index = 3;
}
```

**Missing field 4.** Desktop item-keyed MOV (A 148/154/498) puts `repeated Item items = 4` on `Mov`. Wavee's decoder therefore reports `from=0 length=0 to=0` for those bodies. Rootlist MOVs in these captures are **index** MOV (fields 1–3 only) and decode cleanly.

`ChangeInfo` **as checked in**:

```
optional string user = 1;
optional int64 timestamp = 2;
optional bool admin = 3;
optional bool undo = 4;
optional bool redo = 5;
optional bool merge = 6;        // WRONG TYPE
optional bool compressed = 7;
optional bool migration = 8;
optional int32 split_id = 9;
optional SourceInfo source = 10;
```

Desktop field 6 is a **varint sequence nonce** (B: 16, 17, 11, 18), **not** `bool merge`. Desktop **omits** `ListChanges.want_resulting_revisions` / `want_sync_result` / `ListChanges.nonces` on these writes. Wavee `PlaylistWireMapper.BuildChanges` sets both want_* flags, writes `ChangeInfo.merge=true` (bool), and puts a random nonce on `ListChanges.nonces` (field 6 of ListChanges — a different message).

`ListChanges`: `base_revision=1`, `repeated Delta deltas=2`, `want_resulting_revisions=3`, `want_sync_result=4`, `repeated int64 nonces=6`.

`SelectedListContent`: `revision=1`, `length=2`, `attributes=3`, `contents=5`, `diff=6`, `sync_result=7`, `resulting_revisions=8`, `multiple_heads=9`, `up_to_date=10`, `nonces=14`, `timestamp=15`, `owner_username=16`, `abuse_reporting_enabled=17`, `capabilities=18`.

Create (Wavee-only path, unused here): `ListUpdateRequest` / `CreateListReply`.

Dealer push: `PlaylistModificationInfo` (`uri=1`, `new_revision=2`, `parent_revision=3`, `ops=4`) and `RootlistModificationInfo` (`new_revision=1`, `parent_revision=2`, `ops=3`).

### 2.2 `playlist_permission.proto` → `Wavee.Protocol.Playlist`

`PermissionLevel`: `UNKNOWN=0`, `BLOCKED=1`, `VIEWER=2`, `CONTRIBUTOR=3`.

`Permission`: `revision=1` (bytes; ASCII `default` or 8 raw bytes), `permission_level=2`.

`SetPermissionLevelRequest`: `permission_level=1` only (no revision). Wire: `08 01` BLOCKED, `08 02` VIEWER.

Grant JSON is **not** this proto. Body `{"permission":{"permissionLevel":"CONTRIBUTOR"},"ttlMs":604800000}`. Response adds `token` (32 hex), `ttlMs` as **string**, `createdAt`.

### 2.3 `popcount.proto` → `Wavee.Protocol.Popcount`

`PlaylistPopcount`: `sint64 signed_count=1`, `uint32 status=2`, **`uint64 count=7`** (THE count), `uint32 flag=8` (editorial/DJ). Every sample in these captures: `08 00 10 01 38 00` → count **0**.

### 2.4 XM

`extended_metadata.proto`: `BatchedEntityRequest` / `BatchedExtensionResponse`.
`extension_kind.proto`: `LIST_METADATA_V2 = 205`. Desktop **did not request kind 205** in these captures. Wavee should not invent it.

Collection2v2 is **not** in these captures. No mercury/follow protos: playlist follow/unfollow **is** rootlist ADD/REM.

## 3. Identity map

| Alias | Playlist id | Name / notes |
|---|---|---|
| P1 | `6EVbQZBiAg9zHzMjChxvRd` | starts **Daily Mix 1 (2)** (len 50→52 after two ADDs); Capture B renamed to **`updated playlist name`**. Revs **2→10** (A), **10→11** (B). |
| P2 | `6QbD3n4hCF6uP8jqyiDsS5` | created in A as **My Playlist #9**; 1 track `4mqfe9XrgEOSsofvq5MyjR`. |
| P3 | `4vkIrispQ6gcMNIojGPd0L` | created in A as **My Playlist #10**; **deleted from rootlist** (A 281). No full GET. |
| Wavee library | `284sizy9BLThJBjc0JQypw` | **우울해**. Wavee polls JSON permission VIEWER/`o5umTmROwTQ=` after every rootlist change. |
| Folder inner | `edb339e10aebcf38` | created A 164 as `New+Folder`; B 037 renamed `named+folder+update`. |
| Folder outer | `3dd9e795c88ae3e4` | created A 172; B 128 renamed `root+folder+updated+name`. |

Hosts:

- `gew4-spclient.spotify.com` — playlist-v2, permission GET/level, popcount, XM, gabo JSON, rootlist
- `spclient.wg.spotify.com` — extender, assisted-curation, permission-grant (+ OPTIONS), gabo proto `/events/`
- `api-partner.spotify.com` — pathfinder `/pathfinder/v2/query`

## 4. Capture A — playlist_operations.saz

~96s, 14:20:41–14:22:17 +02, **481 sessions**, prefixes 048–528. Processes: official 10160 dominates writes + most reads; Wavee 13440 = 15 gzip rootlist GETs + JSON perm on 우울해 + late melody/gabo.

### 4.1 User story

1. Open P1 (Daily Mix 1 (2)). Extender + assisted-curation recommend tracks. Add two extender tracks (`0hqj5JBnFt1BHEz2UCFwrl`, `5kPpA4aMFeAQnahSnTIOi4`). Keyed REM one row. Item-keyed MOV twice.
2. Create folder `New Folder` (`edb339e1…`), MOV a playlist into it, create second folder (`3dd9e795…`).
3. Create P2 **My Playlist #9** via `/changes` + `0,726f6f74` (not `POST /playlist/v2/playlist`). Permission 404 then Viewer/default. Rootlist ADD at index 2. Add track `4mqfe9XrgEOSsofvq5MyjR`.
4. Create P3 **My Playlist #10** the same way (709 ms). Rootlist ADD. Shuffle rootlist with a burst of MOVs (including a 3-delta write). Delete P3 (index REM with uri).
5. Navigate home (pathfinder `home` + `feedBaselineLookup`, CDN covers, 24× 304 on `37i9*` diffs). More rootlist MOVs. Reopen P1: item-keyed MOV + two keyed REMs. P1 revs 2→10. Rootlist 71→89.

Gabo (541 events): `add_to_playlist`, `create_folder`, `create_playlist`, `delete_playlist` (plus impressions / `ui_reveal`).

### 4.2 Decoded writes (all 25, all `spotify:10160`)

Playlist `/changes` (11):

| Prefix | SID | Target | Op | Notes |
|---|---|---|---|---|
| 111 | 242 | P1 | ADD | `0hqj5JBnFt1BHEz2UCFwrl` (from extender 073/084) |
| 129 | 266 | P1 | ADD | `5kPpA4aMFeAQnahSnTIOi4` |
| 143 | 287 | P1 | keyed REM | `Rem.items_as_key` + uri |
| 148 | 298 | P1 | item-keyed MOV | **Mov field 4 = repeated Item**; Wavee proto missing → decoder 0/0/0 |
| 154 | 313 | P1 | item-keyed MOV | same |
| 178 | 527 | P2 | UPDATE_LIST | create: base `0,726f6f74`, name `My Playlist #9`, sync-reason **12** (`CAw=`) |
| 210 | 563 | P2 | ADD | `4mqfe9XrgEOSsofvq5MyjR` |
| 218 | 581 | P3 | UPDATE_LIST | create: base `0,726f6f74`, name `My Playlist #10`, **709 ms** |
| 498 | 896 | P1 | item-keyed MOV | Mov field 4 again |
| 512 | 914 | P1 | keyed REM | |
| 515 | 919 | P1 | keyed REM | |

Rootlist `/changes` (14):

| Prefix | SID | Op | Notes |
|---|---|---|---|
| 164 | 464 | ADD start+end | `spotify:start-group:edb339e10aebcf38:New+Folder` + matching end-group |
| 168 | 499 | MOV | file into inner folder |
| 172 | 510 | ADD start+end | `3dd9e795c88ae3e4` |
| 187 | 536 | ADD | P2 at index 2 |
| 228 | 591 | ADD | P3 |
| 240 | 604 | MOV | |
| 249 | 615 | MOV | |
| 252 | 619 | MOV | |
| 257 | 625 | MOV | 363 ms |
| 265 | 642 | MOV | **3 deltas** in one ListChanges |
| 272 | 651 | MOV | |
| 281 | 665 | index REM | delete P3; uri present on Rem.items |
| 466 | 852 | MOV | 3 deltas |
| 477 | 872 | MOV | |

P1 revision ladder 2→10. Rootlist 71→89.

### 4.3 Read correlation

- **Wavee 15 gzip rootlist GETs** (052, 165, 169, 173, 188, 235, 241, 250, 253, 258, 266, 273, 282, 467, 478) — each follows a rootlist write within ~70 ms. This is Wavee’s `PlaylistFetcher.FetchRootlistAsync` / `RootlistOps` decorate=`revision,attributes,length,owner,capabilities,picture`.
- Official `/diff` on the playlist just written returns **200** with `from_revision == to_revision` and **0 ops** — not `up_to_date`, not 304. 304 is reserved for `37i9*` editorial diffs (24 of them).
- Full GET P1: 058 / 123 / 135 — name `Daily Mix 1 (2)`, length 50→51→52. Full GET P2: 486 — `My Playlist #9`, 1 track. **No full GET P3.**
- Popcount: always 6 B proto, count 0.
- Permission: desktop proto `0a 07 64 65 66 61 75 6c 74 10 02` = Viewer/`default`. Wavee JSON on 우울해 VIEWER/`o5umTmROwTQ=`. Fresh-create 404 (181 P2, 217 P3) then Viewer/default.
- Recents: 284 `GET /playlist/v2/list/recents/main/diff`. Wavee `RecentsFetcher` uses `/list/recents/page` + `/page/diff`.
- XM: 57 POSTs; **no kind 205**.
- 24× 304 on `37i9*` diffs during the home flood (378–417, 457, 463, 465, …).

### 4.4 JSON / GraphQL (12 pathfinder)

| Prefix | operationName | sha256Hash | Wavee |
|---|---|---|---|
| 069, 159, 214, 424 | `fetchExtractedColors` | `36e90fcaea00d47c695fce31874efeb2519b97d4cd0ee1abfb4f8dc9348596ea` | Wavee uses `getDynamicColorsByUris` / `f0f11294…` instead |
| 090, 096 | `trackPreview` | `fc26ffc7a1a4f93bd4c2d705649f7dba1de34005b3dc2915549847a9959405d8` | **missing** in Wavee |
| 286, 422 | `lookupChildEntities` | `91ce02e32b19123de231dc8de91fe4b9ab84eca087d4c015549308d77fbb6d10` | **missing** |
| 293, 423 | `feedBaselineLookup` | `a950fb7c4ecdcaf2aad2f3ca9ee9c3aa4b9c43c97e1d07d05148c4d355bea7fc` | **MATCH** `PathfinderOps.FeedBaselineLookupHash` |
| 294, 307 | `home` | `76243c78b0e20ecdbe41b794dec8cbe73f75e585b0a7201b8d2e84578412847a` | Wavee `PathfinderOps.HomeHash` = `9052ac65…` (**mismatch**; comment in PathfinderClient already notes this) |

Extender 073/084 contain the two added tracks. Assisted-curation 086 → 5 show URIs (`2Slq5m2zq8pmAyuiXgc8kK`, `4bQWpGINXNVraZheXSwNna`, `4nWWkORsA650CoeCeAIi2K`, `40ygvasZaqVMMBkgYoUy8C`, `26pHRpjRJSHlsYLzuyxPmd`).

### 4.Z Complete session catalog (481 / prefixes 048–528)

Every row from `playlist_operations_saz/catalog.json`. Semantic one-liner only where proto/JSON was decoded.

```
048 sid=100 CONNECT 200 gew4-spclient.spotify.com:443 (tunnel) req=protobuf/3443b resp=protobuf/709b enc=- role=tls-tunnel proc=wavee:13440
049 sid=103 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160 — popcount P1 count=0 (f7)
050 sid=104 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160 — perm proto P1 Viewer/default
051 sid=105 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/95b resp=protobuf/858b enc=resp-ce:zstd role=other proc=spotify:10160 — XM batch
052 sid=106 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7156b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip full rootlist GET (rev ~71)
053 sid=107 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/216b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee JSON perm 우울해 VIEWER/o5umTmROwTQ=
054 sid=110 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
055 sid=111 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/members req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160 — perm members empty
056 sid=112 GET 200 gew4-spclient.spotify.com /socialgraph/v4/<user-id>/is-following??limit=1000 req=empty/0b resp=empty/0b enc=- role=other proc=spotify:10160 — socialgraph is-following
057 sid=113 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
058 sid=114 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd req=empty/0b resp=protobuf/1775b enc=resp-ce:zstd role=playlist-read proc=spotify:10160 — full GET P1 Daily Mix 1 (2) len=50
059 sid=115 CONNECT 200 gew4-spclient.spotify.com:443 (tunnel) req=protobuf/6504b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
060 sid=116 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/1116b resp=protobuf/1786887b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
061 sid=117 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
062 sid=118 OPTIONS 200 gew4-spclient.spotify.com /quicksilver/v2/messages??ctv_type=web-modal&trigger=spotify%3Aplaylist%3A%3F&action=DISMISS&action=URL&action=EXTERNAL_URL&locale=en&trig_type=URI req=empty/0b resp=empty/0b enc=- role=other proc=spotify:10160
063 sid=119 GET 200 gew4-spclient.spotify.com /quicksilver/v2/messages??ctv_type=web-modal&trigger=spotify%3Aplaylist%3A%3F&action=DISMISS&action=URL&action=EXTERNAL_URL&locale=en&trig_type=URI req=empty/0b resp=json/2b enc=- role=other proc=spotify:10160
064 sid=120 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
065 sid=121 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
066 sid=123 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/80b resp=protobuf/147b enc=- role=other proc=spotify:10160
067 sid=129 CONNECT 200 spclient.wg.spotify.com:443 (tunnel) req=protobuf/6500b resp=protobuf/718b enc=- role=tls-tunnel proc=spotify:10160
068 sid=130 CONNECT 200 api-partner.spotify.com:443 (tunnel) req=protobuf/6404b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
069 sid=131 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/375b resp=json/127b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder fetchExtractedColors 36e90fca…
070 sid=132 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
071 sid=133 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
072 sid=135 OPTIONS 200 spclient.wg.spotify.com /playlistextender/extendp/ req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160 — OPTIONS extender
073 sid=136 POST 200 spclient.wg.spotify.com /playlistextender/extendp/ req=json/91b resp=protobuf/3001b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — extender P1 numResults=20 contains 0hqj5JBnFt1BHEz2UCFwrl + 5kPpA4aMFeAQnahSnTIOi4
074 sid=137 CONNECT 200 spclient.wg.spotify.com:443 (tunnel) req=protobuf/6404b resp=protobuf/718b enc=- role=tls-tunnel proc=spotify:10160
075 sid=138 POST 200 spclient.wg.spotify.com /gabo-receiver-service/v3/events/ req=protobuf/761b resp=empty/0b enc=req-ce:gzip role=telemetry proc=spotify:10160
076 sid=171 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/25919b resp=json/13b enc=- role=telemetry proc=spotify:10160
077 sid=172 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/2802b resp=json/13b enc=- role=telemetry proc=spotify:10160
078 sid=173 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
079 sid=174 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
080 sid=175 OPTIONS 200 spclient.wg.spotify.com /assisted-curation/v1/recommendations/curation/uri req=empty/0b resp=empty/0b enc=- role=other proc=spotify:10160 — OPTIONS assisted-curation
081 sid=176 CONNECT 200 spclient.wg.spotify.com:443 (tunnel) req=protobuf/6694b resp=protobuf/718b enc=- role=tls-tunnel proc=spotify:10160
082 sid=177 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
083 sid=178 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
084 sid=179 POST 200 spclient.wg.spotify.com /playlistextender/extendp/ req=json/91b resp=protobuf/3653b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — extender P1 numResults=25 same two tracks
085 sid=180 CONNECT 200 spclient.wg.spotify.com:443 (tunnel) req=protobuf/6405b resp=protobuf/718b enc=- role=tls-tunnel proc=spotify:10160
086 sid=181 POST 200 spclient.wg.spotify.com /assisted-curation/v1/recommendations/curation/uri req=json/114b resp=json/359b enc=resp-ce:gzip,resp-te:chunked role=other proc=spotify:10160 — assisted-curation → 5 show URIs
087 sid=182 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1F5p3rmiWPIYgZ/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
088 sid=184 OPTIONS 200 spclient.wg.spotify.com /widevine-license/v1/application-certificate req=empty/0b resp=empty/0b enc=- role=other proc=spotify:10160
089 sid=185 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/193b resp=protobuf/5586b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
090 sid=186 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/1156b resp=json/1330b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder trackPreview fc26ffc7… n=25
091 sid=187 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/585b resp=protobuf/659b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
092 sid=188 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/658b resp=protobuf/1284b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
093 sid=189 POST 200 spclient.wg.spotify.com /playlistextender/extendp/ req=json/715b resp=protobuf/4083b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — extender P1 skip 25
094 sid=190 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/732b resp=protobuf/986b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
095 sid=191 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/738b resp=protobuf/2116b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
096 sid=201 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/2131b resp=json/2440b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder trackPreview n=50
097 sid=202 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/586b resp=protobuf/661b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
098 sid=203 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/606b resp=protobuf/1843b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
099 sid=204 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/730b resp=protobuf/2111b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
100 sid=205 CONNECT 200 gew4-spclient.spotify.com:443 (tunnel) req=protobuf/6696b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
101 sid=206 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/737b resp=protobuf/984b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
102 sid=207 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/671b resp=protobuf/980b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
103 sid=208 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/679b resp=protobuf/2221b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
104 sid=209 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/677b resp=protobuf/2225b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
105 sid=210 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/666b resp=protobuf/969b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
106 sid=237 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/9047b resp=json/13b enc=- role=telemetry proc=spotify:10160
107 sid=238 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
108 sid=239 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
109 sid=240 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
110 sid=241 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
111 sid=242 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/changes req=other/140b resp=protobuf/208b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE P1 ADD 0hqj5JBnFt1BHEz2UCFwrl (extender track)
112 sid=243 POST 200 spclient.wg.spotify.com /playlistextender/extendp/ req=json/590b resp=protobuf/2234b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — extender P1 skip 20 numResults=11
113 sid=245 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
114 sid=246 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
115 sid=247 CONNECT 200 gew4-spclient.spotify.com:443 (tunnel) req=protobuf/6694b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
116 sid=248 CONNECT 200 gew4-spclient.spotify.com:443 (tunnel) req=protobuf/6791b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
117 sid=249 CONNECT 200 gew4-spclient.spotify.com:443 (tunnel) req=protobuf/6406b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
118 sid=250 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/105b resp=protobuf/39133b enc=resp-ce:zstd role=other proc=spotify:10160
119 sid=251 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/105b resp=protobuf/39133b enc=resp-ce:zstd role=other proc=spotify:10160
120 sid=252 CONNECT 200 gew4-spclient.spotify.com:443 (tunnel) req=protobuf/6502b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
121 sid=253 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
122 sid=254 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
123 sid=259 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd req=empty/0b resp=protobuf/1808b enc=resp-ce:zstd role=playlist-read proc=spotify:10160 — full GET P1 Daily Mix 1 (2) len=51
124 sid=261 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
125 sid=262 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
126 sid=263 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
127 sid=264 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
128 sid=265 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/188b resp=protobuf/248b enc=resp-ce:zstd role=other proc=spotify:10160
129 sid=266 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/changes req=other/140b resp=protobuf/208b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE P1 ADD 5kPpA4aMFeAQnahSnTIOi4 (extender track)
130 sid=267 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/188b resp=protobuf/246b enc=resp-ce:zstd role=other proc=spotify:10160
131 sid=268 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
132 sid=269 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
133 sid=270 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
134 sid=271 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
135 sid=275 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd req=empty/0b resp=protobuf/1844b enc=resp-ce:zstd role=playlist-read proc=spotify:10160 — full GET P1 Daily Mix 1 (2) len=52
136 sid=280 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/1529b resp=json/13b enc=- role=telemetry proc=spotify:10160
137 sid=281 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/27057b resp=json/13b enc=- role=telemetry proc=spotify:10160
138 sid=282 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/29320b resp=json/13b enc=- role=telemetry proc=spotify:10160
139 sid=283 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/27228b resp=json/13b enc=- role=telemetry proc=spotify:10160
140 sid=284 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/26497b resp=json/13b enc=- role=telemetry proc=spotify:10160
141 sid=285 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/25330b resp=json/13b enc=- role=telemetry proc=spotify:10160
142 sid=286 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/8602b resp=json/13b enc=- role=telemetry proc=spotify:10160
143 sid=287 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/changes req=other/254b resp=protobuf/211b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE P1 keyed REM
144 sid=288 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
145 sid=289 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
146 sid=290 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
147 sid=291 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
148 sid=298 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/changes req=other/290b resp=protobuf/207b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE P1 item-keyed MOV (Mov field 4; Wavee proto missing → decoder 0/0/0)
149 sid=299 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
150 sid=300 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
151 sid=301 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
152 sid=302 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
153 sid=303 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/diff??revision=6%2Cd99f40458c2253c80e3c54754bc5819313a1cfc7&handlesContent=&hint_revision=6%2Cd99f40458c2253c80e3c54754bc5819313a1cfc7 req=empty/0b resp=protobuf/199b enc=resp-ce:zstd role=playlist-read proc=spotify:10160 — P1 /diff rev=6 hint=6 200 from==to 0 ops
154 sid=313 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/changes req=other/133b resp=protobuf/210b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE P1 item-keyed MOV (Mov field 4)
155 sid=317 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
156 sid=318 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
157 sid=319 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
158 sid=320 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
159 sid=322 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/375b resp=json/123b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder fetchExtractedColors mosaic fallback
160 sid=323 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/25070b resp=json/13b enc=- role=telemetry proc=spotify:10160
161 sid=332 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/22493b resp=json/13b enc=- role=telemetry proc=spotify:10160
162 sid=372 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/26375b resp=json/13b enc=- role=telemetry proc=spotify:10160
163 sid=373 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/18144b resp=json/13b enc=- role=telemetry proc=spotify:10160
164 sid=464 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/197b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist folder create start+end edb339e10aebcf38:New+Folder
165 sid=467 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7202b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET (tracks new folder)
166 sid=478 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/118b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
167 sid=498 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/1559b resp=json/13b enc=- role=telemetry proc=spotify:10160
168 sid=499 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/85b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist MOV into folder
169 sid=503 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7130b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET
170 sid=505 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/187b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
171 sid=507 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/diff??revision=73%2Ce179565cd45ac2d11d45be22fd162b16e465c245&handlesContent=&hint_revision=72%2Cf9465fcd9b6c740300ae010e4492fdf7436edad8 req=empty/0b resp=protobuf/80b enc=- role=playlist-read proc=spotify:10160 — official rootlist /diff 73 hint 72 200 0 ops
172 sid=510 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/197b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist second folder 3dd9e795c88ae3e4
173 sid=511 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7212b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET
174 sid=512 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/127b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
175 sid=516 POST 200 spclient.wg.spotify.com /gabo-receiver-service/v3/events/ req=protobuf/675b resp=empty/0b enc=req-ce:gzip role=telemetry proc=spotify:10160
176 sid=525 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/16747b resp=json/13b enc=- role=telemetry proc=spotify:10160
177 sid=526 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
178 sid=527 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/changes req=other/83b resp=protobuf/209b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE create P2 via /changes base 0,726f6f74 UPDATE_LIST My Playlist #9 sync-reason CAw= (12)
179 sid=528 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
180 sid=529 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
181 sid=530 GET 404 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160 — perm P2 404 (playlist just minted)
182 sid=531 CONNECT 200 gew4-spclient.spotify.com:443 (tunnel) req=protobuf/6792b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
183 sid=532 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
184 sid=533 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
185 sid=534 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
186 sid=535 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
187 sid=536 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/135b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist ADD P2 idx 2
188 sid=537 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7403b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET (P2 visible)
189 sid=538 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
190 sid=539 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/198b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
191 sid=540 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/diff??revision=1%2C70fc7b826aa18d65848b077b3dcf80ff1fb8eb8f&handlesContent= req=empty/0b resp=protobuf/202b enc=resp-ce:zstd role=playlist-read proc=spotify:10160 — P2 /diff rev=1 200 from==to 0 ops
192 sid=541 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
193 sid=542 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
194 sid=543 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/members req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
195 sid=544 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/95b resp=protobuf/311b enc=resp-ce:zstd role=other proc=spotify:10160
196 sid=545 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
197 sid=546 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/members req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
198 sid=547 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
199 sid=548 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
200 sid=549 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/80b resp=protobuf/147b enc=- role=other proc=spotify:10160
201 sid=550 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/4759b resp=json/13b enc=- role=telemetry proc=spotify:10160
202 sid=551 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
203 sid=552 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
204 sid=554 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/4317b resp=json/13b enc=- role=telemetry proc=spotify:10160
205 sid=558 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
206 sid=559 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
207 sid=560 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
208 sid=561 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
209 sid=562 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/112b resp=protobuf/43186b enc=resp-ce:zstd role=other proc=spotify:10160
210 sid=563 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/changes req=other/140b resp=protobuf/207b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE P2 ADD 4mqfe9XrgEOSsofvq5MyjR
211 sid=564 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
212 sid=565 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
213 sid=568 POST 200 spclient.wg.spotify.com /playlistextender/extendp/ req=json/91b resp=protobuf/2844b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — extender P2 numResults=20
214 sid=569 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/251b resp=json/127b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder fetchExtractedColors image
215 sid=576 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/16670b resp=json/13b enc=- role=telemetry proc=spotify:10160
216 sid=578 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/3050b resp=json/13b enc=- role=telemetry proc=spotify:10160
217 sid=580 GET 404 gew4-spclient.spotify.com /playlist-permission/v1/playlist/4vkIrispQ6gcMNIojGPd0L/permission/base req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160 — perm P3 404 (just minted)
218 sid=581 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/4vkIrispQ6gcMNIojGPd0L/changes req=other/84b resp=protobuf/209b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE create P3 via /changes base 0,726f6f74 UPDATE_LIST My Playlist #10 709ms
219 sid=582 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
220 sid=583 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
221 sid=584 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
222 sid=585 CONNECT 200 gew4-spclient.spotify.com:443 (tunnel) req=protobuf/6791b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
223 sid=586 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
224 sid=587 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
225 sid=588 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
226 sid=589 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/81b resp=protobuf/173b enc=resp-ce:zstd role=other proc=spotify:10160
227 sid=590 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/4vkIrispQ6gcMNIojGPd0L/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
228 sid=591 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/135b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist ADD P3
229 sid=592 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/4vkIrispQ6gcMNIojGPd0L/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
230 sid=593 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/4vkIrispQ6gcMNIojGPd0L/permission/members req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
231 sid=594 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/95b resp=protobuf/311b enc=resp-ce:zstd role=other proc=spotify:10160
232 sid=595 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/4vkIrispQ6gcMNIojGPd0L/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
233 sid=596 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/4vkIrispQ6gcMNIojGPd0L/permission/members req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
234 sid=597 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/4vkIrispQ6gcMNIojGPd0L/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
235 sid=598 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7238b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET (P3 visible)
236 sid=599 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/4vkIrispQ6gcMNIojGPd0L/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
237 sid=600 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/156b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
238 sid=601 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/80b resp=protobuf/147b enc=- role=other proc=spotify:10160
239 sid=603 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/4vkIrispQ6gcMNIojGPd0L/diff??revision=1%2C398040e2a19d84cb2f13ecf75a8c320527fa0965&handlesContent= req=empty/0b resp=protobuf/202b enc=resp-ce:zstd role=playlist-read proc=spotify:10160 — P3 /diff rev=1 200 from==to 0 ops
240 sid=604 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/85b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist MOV
241 sid=605 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7432b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET
242 sid=606 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/296b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
243 sid=607 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/diff??revision=77%2Cc35279bf16caccd018522af367764acb519b987a&handlesContent=&hint_revision=76%2C605874b0fcc3798802bf347eadb37594b816fc2a req=empty/0b resp=protobuf/80b enc=- role=playlist-read proc=spotify:10160 — official rootlist /diff 77 hint 76
244 sid=608 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
245 sid=609 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/members req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
246 sid=610 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
247 sid=611 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/diff??revision=7%2C12606d674a28d18595f85d73b0ae39aafd6f0eba&handlesContent= req=empty/0b resp=protobuf/202b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
248 sid=612 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
249 sid=615 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/85b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist MOV
250 sid=616 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7460b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET
251 sid=617 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/136b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
252 sid=619 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/85b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist MOV
253 sid=620 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7479b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET
254 sid=622 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/206b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
255 sid=623 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/diff??revision=79%2C1f8ab625b253bfb66359e2390c363c89c66bb50d&handlesContent=&hint_revision=78%2C7f8aded1664ab5a6b3714d529b22faf5ffb37ef9 req=empty/0b resp=protobuf/80b enc=- role=playlist-read proc=spotify:10160 — official rootlist /diff 79 hint 78
256 sid=624 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/12867b resp=json/13b enc=- role=telemetry proc=spotify:10160
257 sid=625 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/85b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist MOV 363ms
258 sid=627 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7376b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET
259 sid=628 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/203b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
260 sid=630 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/artistmix/4SpbR6yFEvexJuaBpgAU5p/en req=empty/0b resp=image/35628b enc=resp-ce:gzip role=cdn proc=spotify:10160
261 sid=631 CONNECT 200 seed-mix-image.spotifycdn.com:443 (tunnel) req=protobuf/6411b resp=protobuf/668b enc=- role=tls-tunnel proc=spotify:10160
262 sid=632 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/artistmix/7hr9W3IjXcm3UlLY7guLk5/en req=empty/0b resp=image/31277b enc=resp-ce:gzip role=cdn proc=spotify:10160
263 sid=633 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/daily/2/ab6761610000e5ebe2e8e7ff002a4afda1c7147e/en req=empty/0b resp=image/27136b enc=resp-ce:gzip role=cdn proc=spotify:10160
264 sid=635 GET 200 seed-mix-image.spotifycdn.com /v6/img/desc/Nostalgia%202000s/en/default req=empty/0b resp=image/15919b enc=resp-ce:gzip role=cdn proc=spotify:10160
265 sid=642 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/191b resp=protobuf/106b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE rootlist MOV (3 deltas)
266 sid=643 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7497b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET
267 sid=644 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/175b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
268 sid=645 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/diff??revision=83%2C920547bffb13ba50af58aa7e655891b604914abe&handlesContent=&hint_revision=80%2Cec59b9646cd6752ae524c326577e339642fa3cf0 req=empty/0b resp=protobuf/80b enc=- role=playlist-read proc=spotify:10160 — official rootlist /diff 83 hint 80
269 sid=646 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/4vkIrispQ6gcMNIojGPd0L/diff??revision=1%2C398040e2a19d84cb2f13ecf75a8c320527fa0965&handlesContent= req=empty/0b resp=protobuf/202b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
270 sid=647 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/4vkIrispQ6gcMNIojGPd0L/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
271 sid=648 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/4vkIrispQ6gcMNIojGPd0L/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
272 sid=651 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/85b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist MOV
273 sid=652 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7393b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET
274 sid=653 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/341b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
275 sid=654 POST 200 spclient.wg.spotify.com /gabo-receiver-service/v3/events/ req=protobuf/515b resp=empty/0b enc=req-ce:gzip role=telemetry proc=spotify:10160
276 sid=657 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/9193b resp=json/13b enc=- role=telemetry proc=spotify:10160
277 sid=660 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/4vkIrispQ6gcMNIojGPd0L/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
278 sid=661 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/4vkIrispQ6gcMNIojGPd0L/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
279 sid=662 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/4vkIrispQ6gcMNIojGPd0L/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
280 sid=663 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/4vkIrispQ6gcMNIojGPd0L/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
281 sid=665 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/126b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist index REM delete P3 (uri present)
282 sid=666 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7384b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET (P3 gone)
283 sid=667 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/295b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
284 sid=668 GET 200 gew4-spclient.spotify.com /playlist/v2/list/recents/main/diff??revision=0%2C00000000b29bd9095cf55330191db7f497e0411f&handlesContent= req=empty/0b resp=protobuf/1569b enc=resp-ce:zstd role=playlist-read proc=spotify:10160 — official recents /list/recents/main/diff (Wavee uses /page)
285 sid=669 OPTIONS 200 gew4-spclient.spotify.com /quicksilver/v2/messages??ctv_type=web-modal&trigger=spotify%3Ahome&action=DISMISS&action=URL&action=EXTERNAL_URL&locale=en&trig_type=URI req=empty/0b resp=empty/0b enc=- role=other proc=spotify:10160
286 sid=670 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/305b resp=json/550b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder lookupChildEntities 91ce02e3… n=3
287 sid=671 OPTIONS 200 spclient.wg.spotify.com /clip-transcript/v1/transcripts/spotify%3Aepisode%3A2sVo2KAArUGkx3jN12X5Sn??offsets.start=190.000s&offsets.end=250.000s req=empty/0b resp=empty/0b enc=- role=other proc=spotify:10160
288 sid=672 POST 200 gew4-spclient.spotify.com /ads/v3/ads??slots=hpto req=json/2185b resp=protobuf/15116b enc=resp-ce:zstd role=other proc=spotify:10160
289 sid=673 CONNECT 200 api-partner.spotify.com:443 (tunnel) req=protobuf/6598b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
290 sid=674 GET 200 gew4-spclient.spotify.com /quicksilver/v2/messages??ctv_type=web-modal&trigger=spotify%3Ahome&action=DISMISS&action=URL&action=EXTERNAL_URL&locale=en&trig_type=URI req=empty/0b resp=json/2b enc=- role=other proc=spotify:10160
291 sid=675 GET 200 spclient.wg.spotify.com /clip-transcript/v1/transcripts/spotify%3Aepisode%3A2sVo2KAArUGkx3jN12X5Sn??offsets.start=190.000s&offsets.end=250.000s req=empty/0b resp=json/2403b enc=resp-ce:gzip,resp-te:chunked role=other proc=spotify:10160
292 sid=676 CONNECT 200 api-partner.spotify.com:443 (tunnel) req=protobuf/6693b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
293 sid=677 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/1026b resp=json/17199b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder feedBaselineLookup a950fb7c… MATCH Wavee
294 sid=678 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/323b resp=json/41465b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder home 76243c78… Wavee HomeHash differs
295 sid=679 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/radio/track/2vFzC766RC2m36ywFyj09S/en req=empty/0b resp=image/27156b enc=resp-ce:gzip role=cdn proc=spotify:10160
296 sid=680 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/daily/3/ab6761610000e5eb5a1c7aa77551b62c9cce74c9/en req=empty/0b resp=image/33724b enc=resp-ce:gzip role=cdn proc=spotify:10160
297 sid=681 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/daily/5/ab6761610000e5eba0e9073506303eaa13d8b510/en req=empty/0b resp=image/28112b enc=resp-ce:gzip role=cdn proc=spotify:10160
298 sid=682 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/dw/cover/en req=empty/0b resp=image/21712b enc=resp-ce:gzip role=cdn proc=spotify:10160
299 sid=683 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000bada/dt/v1/img/release-radar-v4/0du5cEVh5yTK9QJze8zA0C/en req=empty/0b resp=image/315063b enc=resp-ce:gzip role=cdn proc=spotify:10160
300 sid=684 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/radio/track/4zRZAmBQP8vhNPf9i9opXt/en req=empty/0b resp=image/24239b enc=resp-ce:gzip role=cdn proc=spotify:10160
301 sid=685 CONNECT 200 lexicon-assets.spotifycdn.com:443 (tunnel) req=protobuf/6507b resp=protobuf/718b enc=- role=tls-tunnel proc=spotify:10160
302 sid=686 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/radio/artist/7AaGbSgUxJFuZ49VvclNH6/en req=empty/0b resp=image/24180b enc=resp-ce:gzip role=cdn proc=spotify:10160
303 sid=687 GET 304 lexicon-assets.spotifycdn.com /DJ-Beta-CoverArt-300.jpg req=empty/0b resp=empty/0b enc=- role=cdn proc=spotify:10160
304 sid=688 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/radio/artist/7hr9W3IjXcm3UlLY7guLk5/en req=empty/0b resp=image/21031b enc=resp-ce:gzip role=cdn proc=spotify:10160
305 sid=689 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/topic/twenty_twenties/6SKusTjOAPsTZ6kareKQdm/en req=empty/0b resp=image/33853b enc=resp-ce:gzip role=cdn proc=spotify:10160
306 sid=690 GET 200 pickasso.spotifycdn.com /image/ab67c0de0000deef/dt/v1/img/radio/track/0V82wcNlunw76nvvmPL9tk/en req=empty/0b resp=image/31663b enc=resp-ce:gzip role=cdn proc=spotify:10160
307 sid=691 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/323b resp=json/43655b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder home 76243c78…
308 sid=692 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E38wY9VFrwrWy/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
309 sid=693 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E39B2ECSfJBmK/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
310 sid=694 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E35L9PKtiQN8O/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
311 sid=695 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E35HMZkgUHOT0/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
312 sid=696 CONNECT 200 gew4-spclient.spotify.com:443 (tunnel) req=protobuf/6504b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
313 sid=697 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EYkqdzj48dyYq/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
314 sid=698 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E37Twn3WxoAWE/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
315 sid=699 CONNECT 200 gew4-spclient.spotify.com:443 (tunnel) req=protobuf/6600b resp=protobuf/709b enc=- role=tls-tunnel proc=spotify:10160
316 sid=700 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZEVXcKDbGa6CckPI/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
317 sid=701 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZEVXbp4UblnlhiEI/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
318 sid=702 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E4AgGLUsynqyF/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
319 sid=703 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E8JeqOyGKUWRj/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
320 sid=704 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E4opKNcF2wERR/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
321 sid=705 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E4x2U7TuxADyl/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
322 sid=706 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E4y3o8BDiBs4n/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
323 sid=707 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E4AaP7vnb6vS2/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
324 sid=708 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E4prHx56nBXlr/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
325 sid=709 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E4lTW4YheD7nV/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
326 sid=710 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E4m5aoXVdJrlE/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
327 sid=711 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E8KCEzY7x4w4G/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
328 sid=712 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/2pnt79m93NytfAj2lByLlQ/permission/base req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
329 sid=713 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E8UAJTtmEhOXe/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
330 sid=714 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIdh6MgVIhb8B/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
331 sid=715 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX5g856aiKiDS/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
332 sid=716 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIZDxu28y8bpW/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
333 sid=717 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIXGra9DhHCqh/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
334 sid=718 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E37Yqr2urjnJt/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
335 sid=719 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E8Ri8hM6zKcdg/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
336 sid=720 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX0018ciYu6bM/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
337 sid=721 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E8OugjVd3vicC/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
338 sid=722 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EQnsJ0xmvpihE/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
339 sid=723 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E8OpR3bZBXBqF/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
340 sid=724 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1E4pCHrIABRuCc/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
341 sid=725 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DXaQpIUzyByme/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
342 sid=726 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWUoY6Ih7vsxr/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
343 sid=727 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/34Xv1hxN6wZ2i47QBQmRT9/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
344 sid=728 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EVHGWrwldPRtj/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
345 sid=729 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EQncLwOalG3K7/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
346 sid=730 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EQpesGsmIyqcW/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
347 sid=731 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EQqedj0y9Uwvu/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
348 sid=732 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EQuzaJLsfioU9/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
349 sid=733 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIZyWUMEWzefG/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
350 sid=734 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EVKuMoAJjoTIw/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
351 sid=735 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIZjqd8fFrYpm/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
352 sid=736 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EQcAnxYY2ZXzJ/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
353 sid=737 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWUxUko6rcfsK/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
354 sid=738 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWYPwGkJoztcR/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
355 sid=739 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWVJyzEwVacEu/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
356 sid=740 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX1rUSgDt83Z2/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
357 sid=741 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DXcx1szy2g67M/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
358 sid=742 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWZLcGGC0HJbc/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
359 sid=743 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX4je779Ww5L2/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
360 sid=744 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX79N7YUDFu8f/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
361 sid=745 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX6Z0nWFAx7KL/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
362 sid=746 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWSY75PtDqTkW/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
363 sid=747 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIhmSBwUDxg84/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
364 sid=748 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIgQnNDX2DOQP/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
365 sid=749 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIgWKm3HbrZYe/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
366 sid=750 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIfTjl2Hyh4B7/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
367 sid=751 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIfdXyCbOQndQ/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
368 sid=752 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DXdGHPXiRsW3u/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
369 sid=753 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX1uHCeFHcn8X/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
370 sid=754 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX5Ejj0EkURtP/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
371 sid=755 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX4WYpdgoIcn6/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
372 sid=756 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DXbYM3nMM0oPk/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
373 sid=757 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DXd0DyosUBZQ7/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
374 sid=758 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX3YSRoSdA634/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
375 sid=759 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWUXxc8Mc6MmJ/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
376 sid=760 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWYWddJiPzbvb/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
377 sid=761 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DXbrUpGvoi3TS/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
378 sid=762 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1E35L9PKtiQN8O/diff??revision=0%2C01ac3cbd9458f5932526669540d3143e5e4762a1&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
379 sid=763 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EQcAnxYY2ZXzJ/diff??revision=0%2Cca4feb61a4ebb6b91bf042ea82637e4a0b6fb9fe&handlesContent= req=empty/0b resp=protobuf/2359b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
380 sid=764 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DXcx1szy2g67M/diff??revision=0%2C53fa52805b032c6474576df9b598c3ed0ffebdaa&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
381 sid=765 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1E37Twn3WxoAWE/diff??revision=0%2C2c0d39a42991d153a27c37515cf8327db06be64a&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
382 sid=766 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1E35HMZkgUHOT0/diff??revision=0%2C971ded47110b332d8f5f98d5e9a1c8cc3627790b&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
383 sid=767 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/451b resp=protobuf/437b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
384 sid=768 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/5213b resp=protobuf/20534b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
385 sid=769 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/294b resp=protobuf/1282b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
386 sid=770 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EYkqdzj48dyYq/diff??revision=0%2C726f6f7400000000000000000000000000000000&handlesContent= req=empty/0b resp=protobuf/662b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
387 sid=771 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZEVXcKDbGa6CckPI/diff??revision=1786312800%2C0000000093d959038971dbac93ec88861eba1d42&handlesContent= req=empty/0b resp=protobuf/3264b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
388 sid=772 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZEVXbp4UblnlhiEI/diff??revision=0%2C80f4ef1142b106c697d71dc774797e68de75ccc1&handlesContent= req=empty/0b resp=protobuf/1809b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
389 sid=773 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1E4AgGLUsynqyF/diff??revision=0%2C909aae68bc4f2edad1c55e31744b8ea3d19d52b2&handlesContent= req=empty/0b resp=protobuf/2345b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
390 sid=774 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1E4opKNcF2wERR/diff??revision=0%2Cc79baafbae65aef7857b5223f15f588fdebc47cd&handlesContent= req=empty/0b resp=protobuf/2293b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
391 sid=775 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1E4y3o8BDiBs4n/diff??revision=0%2C35773fbc5286a96d8295429ef16a16fcae59bc7c&handlesContent= req=empty/0b resp=protobuf/2333b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
392 sid=776 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1E4AaP7vnb6vS2/diff??revision=0%2C86468cf05dc33f88c851b40f96dafebdf3a2f5b7&handlesContent= req=empty/0b resp=protobuf/2353b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
393 sid=777 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DXbrUpGvoi3TS/diff??revision=0%2C19d237fa5a550746477eba46d74dac1cd778a70a&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
394 sid=778 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1E8KCEzY7x4w4G/diff??revision=0%2Ca31748182185da40573315ed1b6618963bd308d8&handlesContent= req=empty/0b resp=protobuf/2317b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
395 sid=779 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EVHGWrwldPRtj/diff??revision=0%2C15d2870f147eca7f7f514e4cd29d22ed00145f5a&handlesContent= req=empty/0b resp=protobuf/2448b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
396 sid=780 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EQncLwOalG3K7/diff??revision=0%2Cf2c89f3ee7c2a2788a5d4ba8380782aa5ad1028e&handlesContent= req=empty/0b resp=protobuf/2497b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
397 sid=781 GET 200 aet.spotify.com /v2/t?p=<aet> req=empty/0b resp=empty/0b enc=- role=other proc=spotify:10160
398 sid=782 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EQpesGsmIyqcW/diff??revision=0%2C9b32181e43e614a23b8ba6a86d006865cc1d5fcb&handlesContent= req=empty/0b resp=protobuf/2224b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
399 sid=783 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EQqedj0y9Uwvu/diff??revision=0%2Ca4274709dc226b0ccc1bba534feb54003dd716f6&handlesContent= req=empty/0b resp=protobuf/2470b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
400 sid=784 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DX3YSRoSdA634/diff??revision=0%2Cfb3bccb2da566cd4d5affc6d4ce2533f71b0130d&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
401 sid=785 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EQuzaJLsfioU9/diff??revision=0%2C42ebcf6312073f59cfb52700cebf6f0d3c050c3a&handlesContent= req=empty/0b resp=protobuf/2240b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
402 sid=786 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EVKuMoAJjoTIw/diff??revision=0%2Cdcb3d32a419c99c67a824934711fa791de229905&handlesContent= req=empty/0b resp=protobuf/2476b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
403 sid=787 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EIZjqd8fFrYpm/diff??revision=0%2Cf81cbf30922afeae0d5cb3cec52e9b766500dc60&handlesContent= req=empty/0b resp=protobuf/2304b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
404 sid=788 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DWYPwGkJoztcR/diff??revision=0%2Cf354d29f5abec9b3b7bfcb8ca0d565f9a63c1568&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
405 sid=789 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DWZLcGGC0HJbc/diff??revision=0%2C0573c60165b78360cdb7deb36a76a3008f0479b7&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
406 sid=790 GET 200 aet.spotify.com /v2/t?p=<aet> req=empty/0b resp=empty/0b enc=- role=other proc=spotify:10160
407 sid=791 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DX1rUSgDt83Z2/diff??revision=0%2Cc22c641c1a9a54368f6eb7119c54058228b7694a&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
408 sid=792 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DWVJyzEwVacEu/diff??revision=0%2Cdc8b0f79bedc52f9ad659f61723704438f0d9303&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
409 sid=793 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DX4je779Ww5L2/diff??revision=0%2Cdb3a2460ae85d8fdb436710511cff57cae70974a&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
410 sid=794 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EIgQnNDX2DOQP/diff??revision=0%2C6a257e5b5f6867483c21e9b27aab138bba9cca4a&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
411 sid=795 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EIhmSBwUDxg84/diff??revision=0%2C2c19cd2c1d40338d75210678a4389280197ac153&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
412 sid=796 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EIgWKm3HbrZYe/diff??revision=0%2C239aa6838308fb9ed320c091a78ee481fe47c20b&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
413 sid=797 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EIfdXyCbOQndQ/diff??revision=0%2Cdfc7094e91a6bcaed0c54be6385c4cf4e60e6890&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
414 sid=798 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DXdGHPXiRsW3u/diff??revision=0%2C221485ea27a0f8bc4a116e3f9c7f7454fccc560e&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
415 sid=799 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DX1uHCeFHcn8X/diff??revision=0%2Cf396fd28973a15f9839114117e550182cbd67870&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
416 sid=800 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DX4WYpdgoIcn6/diff??revision=0%2Cc739eaf38010c35cfb3910de27d6b35dacc1d6d6&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
417 sid=801 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DWUXxc8Mc6MmJ/diff??revision=0%2C846d0a19e315f77f2fa73a2338132f46a25ef30c&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
418 sid=803 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/193b resp=protobuf/382b enc=resp-ce:zstd role=other proc=spotify:10160
419 sid=805 CONNECT 200 image-cdn-ak.spotifycdn.com:443 (tunnel) req=protobuf/6505b resp=protobuf/718b enc=- role=tls-tunnel proc=spotify:10160
420 sid=806 CONNECT 200 image-cdn-fa.spotifycdn.com:443 (tunnel) req=protobuf/6504b resp=protobuf/668b enc=- role=tls-tunnel proc=spotify:10160
421 sid=807 CONNECT 200 daylist.spotifycdn.com:443 (tunnel) req=protobuf/6211b resp=protobuf/718b enc=- role=tls-tunnel proc=spotify:10160
422 sid=808 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/461b resp=json/769b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder lookupChildEntities n=7
423 sid=809 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/1026b resp=json/16719b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder feedBaselineLookup a950fb7c…
424 sid=810 POST 200 api-partner.spotify.com /pathfinder/v2/query req=json/1045b resp=json/269b enc=resp-ce:zstd role=other proc=spotify:10160 — pathfinder fetchExtractedColors 10 images
425 sid=811 CONNECT 200 image-cdn-fa.spotifycdn.com:443 (tunnel) req=protobuf/6698b resp=protobuf/668b enc=- role=tls-tunnel proc=spotify:10160
426 sid=812 GET 200 image-cdn-fa.spotifycdn.com /image/ab67616100005174c45b5b51508a9d2c15c250df req=empty/0b resp=image/29367b enc=- role=cdn proc=spotify:10160
427 sid=813 GET 304 daylist.spotifycdn.com /playlist-covers-mix/en/afternoon_default.jpg req=empty/0b resp=empty/0b enc=- role=cdn proc=spotify:10160
428 sid=814 GET 200 image-cdn-ak.spotifycdn.com /image/ab67706f000000029dd728e50045942b894f0d35 req=empty/0b resp=image/23384b enc=- role=cdn proc=spotify:10160
429 sid=815 GET 200 image-cdn-fa.spotifycdn.com /image/ab676161000051747299fecc45211132432d7d8c req=empty/0b resp=image/43294b enc=- role=cdn proc=spotify:10160
430 sid=816 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
431 sid=817 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
432 sid=818 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EP6YuccBxUcC1/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
433 sid=819 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=protobuf/12b enc=- role=playlist-read proc=spotify:10160
434 sid=820 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWSBi5svWQ9Nk/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
435 sid=821 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX6oMvmbu4tmz/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
436 sid=822 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX6MUrG3NBYtM/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
437 sid=823 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWUppGmuwT9c7/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
438 sid=824 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWXHyhanaNMoy/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
439 sid=825 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DWTwCImwcYjDL/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
440 sid=826 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1DX19xRtMyA5LM/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
441 sid=827 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIhrKu07W6FWB/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
442 sid=828 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DX19xRtMyA5LM req=empty/0b resp=protobuf/2689b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
443 sid=829 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DWSBi5svWQ9Nk/diff??revision=0%2C4b608fb5da939790e3bef2c0dd2adc5fca7ccf41&handlesContent= req=empty/0b resp=protobuf/2309b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
444 sid=830 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DX6MUrG3NBYtM/diff??revision=0%2C4540e1fc98e6393da2a52958668af509b68d5145&handlesContent= req=empty/0b resp=protobuf/3739b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
445 sid=831 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DWUppGmuwT9c7 req=empty/0b resp=protobuf/4123b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
446 sid=832 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DWXHyhanaNMoy req=empty/0b resp=protobuf/3181b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
447 sid=833 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/4216b resp=protobuf/12945b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
448 sid=834 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIfxw8oghwfcN/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
449 sid=835 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIdDn5P759aRj/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
450 sid=836 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIguyCzHJlUGq/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
451 sid=837 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/37i9dQZF1EIgG2NEOhqsD7/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
452 sid=838 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/302b resp=protobuf/921b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
453 sid=839 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/129b resp=protobuf/131b enc=- role=other proc=spotify:10160
454 sid=840 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/219b resp=protobuf/1559b enc=resp-ce:zstd role=other proc=spotify:10160
455 sid=841 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/947b resp=protobuf/16870b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
456 sid=842 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1DWTwCImwcYjDL/diff??revision=0%2Cfeadcfebef393f3b361a3b0b86057996013cbbd5&handlesContent= req=empty/0b resp=protobuf/2521b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
457 sid=843 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EIhrKu07W6FWB/diff??revision=0%2Cbf520f5c095e1166a419108ff0d28fad9c38a7ba&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
458 sid=844 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EIfxw8oghwfcN/diff??revision=0%2C262db9c1328f8729e2dd9db0eb8d56b23c9e5abf&handlesContent= req=empty/0b resp=protobuf/2099b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
459 sid=845 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/948b resp=protobuf/16876b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
460 sid=846 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/1062b resp=protobuf/21422b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
461 sid=847 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/1221b resp=protobuf/22484b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
462 sid=848 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/2083b resp=protobuf/43474b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
463 sid=849 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EIdDn5P759aRj/diff??revision=0%2C105c5386b3b66481a13055f60d51c8ccda9d0099&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
464 sid=850 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EIguyCzHJlUGq/diff??revision=0%2C0d93cf7918286c3c3c9868a670a087b13d51c73c&handlesContent= req=empty/0b resp=protobuf/2098b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
465 sid=851 GET 304 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EIgG2NEOhqsD7/diff??revision=0%2C805aa02a03dd99758969c349f1ba5e78c6f88761&handlesContent= req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
466 sid=852 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/191b resp=protobuf/106b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE rootlist MOV (3 deltas)
467 sid=853 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7349b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET
468 sid=862 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/77b resp=protobuf/697b enc=resp-ce:zstd role=other proc=spotify:10160
469 sid=863 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/259b resp=protobuf/884b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
470 sid=864 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/95b resp=protobuf/90b enc=- role=other proc=spotify:10160
471 sid=865 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/320b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
472 sid=867 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
473 sid=868 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/99b resp=protobuf/90b enc=- role=other proc=spotify:10160
474 sid=869 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/diff??revision=7%2C12606d674a28d18595f85d73b0ae39aafd6f0eba&handlesContent= req=empty/0b resp=protobuf/202b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
475 sid=870 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
476 sid=871 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/9875b resp=json/13b enc=- role=telemetry proc=spotify:10160
477 sid=872 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=other/85b resp=protobuf/113b enc=- role=playlist-mutation proc=spotify:10160 — WRITE rootlist MOV
478 sid=873 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist??decorate=revision,attributes,length,owner,capabilities,picture req=empty/0b resp=protobuf/7390b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440 — Wavee gzip rootlist GET
479 sid=874 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/284sizy9BLThJBjc0JQypw/permission/base req=empty/0b resp=json/341b enc=resp-ce:gzip,resp-te:chunked role=playlist-read proc=wavee:13440
480 sid=875 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/public/v3/events req=json/1717b resp=json/13b enc=- role=telemetry proc=spotify:10160
481 sid=876 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/30768b resp=json/13b enc=- role=telemetry proc=spotify:10160
482 sid=877 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/9907b resp=json/13b enc=- role=telemetry proc=spotify:10160
483 sid=878 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
484 sid=879 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
485 sid=880 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/members req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
486 sid=881 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5 req=empty/0b resp=protobuf/336b enc=resp-ce:zstd role=playlist-read proc=spotify:10160 — full GET P2 My Playlist #9 1 track (no full GET P3)
487 sid=882 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
488 sid=884 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/diff??revision=7%2C12606d674a28d18595f85d73b0ae39aafd6f0eba&handlesContent= req=empty/0b resp=protobuf/202b enc=resp-ce:zstd role=playlist-read proc=spotify:10160
489 sid=885 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
490 sid=886 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
491 sid=887 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
492 sid=888 POST 200 spclient.wg.spotify.com /playlistextender/extendp/ req=json/91b resp=protobuf/2984b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — extender P1 numResults=20
493 sid=890 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
494 sid=891 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
495 sid=892 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
496 sid=894 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=protobuf/3146b resp=protobuf/135014b enc=req-ce:gzip,resp-ce:zstd role=other proc=spotify:10160
497 sid=895 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/1529b resp=json/13b enc=- role=telemetry proc=spotify:10160
498 sid=896 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/changes req=other/240b resp=protobuf/209b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE P1 item-keyed MOV (Mov field 4)
499 sid=897 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
500 sid=898 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
501 sid=899 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/members req=empty/0b resp=empty/0b enc=- role=playlist-read proc=spotify:10160
502 sid=900 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
503 sid=901 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/27037b resp=json/13b enc=- role=telemetry proc=spotify:10160
504 sid=906 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/27785b resp=json/13b enc=- role=telemetry proc=spotify:10160
505 sid=907 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/26377b resp=json/13b enc=- role=telemetry proc=spotify:10160
506 sid=908 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/diff??revision=8%2Cf4863814582da451a93b90e51b3b38f921deac50&handlesContent=&hint_revision=8%2Cf4863814582da451a93b90e51b3b38f921deac50 req=empty/0b resp=protobuf/202b enc=resp-ce:zstd role=playlist-read proc=spotify:10160 — P1 /diff rev=8 hint=8 200 from==to 0 ops
507 sid=909 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/28486b resp=json/13b enc=- role=telemetry proc=spotify:10160
508 sid=910 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/23813b resp=json/13b enc=- role=telemetry proc=spotify:10160
509 sid=911 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/3199b resp=json/13b enc=- role=telemetry proc=spotify:10160
510 sid=912 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
511 sid=913 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
512 sid=914 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/changes req=other/133b resp=protobuf/210b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE P1 keyed REM
513 sid=917 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=protobuf/6b enc=- role=playlist-read proc=spotify:10160
514 sid=918 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=protobuf/11b enc=- role=playlist-read proc=spotify:10160
515 sid=919 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/changes req=other/133b resp=protobuf/208b enc=resp-ce:zstd role=playlist-mutation proc=spotify:10160 — WRITE P1 keyed REM
516 sid=923 POST 200 spclient.wg.spotify.com /gabo-receiver-service/v3/events/ req=protobuf/999b resp=empty/0b enc=req-ce:gzip role=telemetry proc=spotify:10160
517 sid=924 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/29025b resp=json/13b enc=- role=telemetry proc=spotify:10160
518 sid=925 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/27701b resp=json/13b enc=- role=telemetry proc=spotify:10160
519 sid=926 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/26384b resp=json/13b enc=- role=telemetry proc=spotify:10160
520 sid=927 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/27697b resp=json/13b enc=- role=telemetry proc=spotify:10160
521 sid=928 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/25515b resp=json/13b enc=- role=telemetry proc=spotify:10160
522 sid=929 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/22716b resp=json/13b enc=- role=telemetry proc=spotify:10160
523 sid=930 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=json/11479b resp=json/13b enc=- role=telemetry proc=spotify:10160
524 sid=935 CONNECT 200 spclient.wg.spotify.com:443 (tunnel) req=protobuf/3441b resp=protobuf/718b enc=- role=tls-tunnel proc=wavee:13440
525 sid=936 POST 200 spclient.wg.spotify.com /gabo-receiver-service/v3/events/ req=protobuf/2201b resp=empty/0b enc=req-ce:gzip role=telemetry proc=wavee:13440 — Wavee gabo proto /events/
526 sid=937 GET 200 gew4-spclient.spotify.com /melody/v1/time req=empty/0b resp=json/27b enc=- role=other proc=wavee:13440
527 sid=938 GET 200 gew4-spclient.spotify.com /melody/v1/time req=empty/0b resp=json/27b enc=- role=other proc=wavee:13440
528 sid=939 GET 200 gew4-spclient.spotify.com /melody/v1/time req=empty/0b resp=json/27b enc=- role=other proc=wavee:13440
```

## 5. Capture B — somemoreplaylists.saz

~67s, 14:34:21–14:35:28 +02, **134 sessions**. Continuation of the same desktop session (rootlist already at rev 89). No pathfinder, no extender. Wavee is playing audio (28× GET `/audio/{sha1}` 206) and still polling rootlist + P2 JSON permission.

### 5.1 Four writes (all `spotify:10160`)

| Prefix | SID | Path | Op | Rev | ChangeInfo field 6 |
|---|---|---|---|---|---|
| 037 | 47 | rootlist/changes | folder rename **inner**: REM `from=2 len=1` + ADD `spotify:start-group:edb339e10aebcf38:named+folder+update` (keep create timestamp; do not touch end-group) | 89→90 | 16 |
| 049 | 63 | rootlist/changes | MOV `from=0 len=1 to=3` (P1 into inner folder) | 90→91 | 17 |
| 063 | 79 | playlist/P1/changes | UPDATE_LIST name `updated playlist name`. zstd SLC; **name not in reply** | P1 10→11 | 11 |
| 128 | 180 | rootlist/changes | folder rename **outer**: REM `from=0` + ADD `spotify:start-group:3dd9e795c88ae3e4:root+folder+updated+name` | 91→92 | 18 |

Desktop omits `want_*` and `ListChanges.nonces`. No track ADD/REM/MOV in B.

### 5.2 Permission / grants

- 078 `POST …/permission/base/level` proto `08 01` **BLOCKED**. Subsequent GETs: BLOCKED / `3b907c0d…`.
- 108 `POST …/permission/base/level` proto `08 02` **VIEWER**. GETs flip to VIEWER / `b5483d8c…`.
- Pre-toggle GETs were Viewer/`default`.
- Grants 090 / 099 / 115 / 117: JSON `{"permission":{"permissionLevel":"CONTRIBUTOR"},"ttlMs":604800000}` on **`spclient.wg.spotify.com`**, plus OPTIONS 089. Wavee `PlaylistMutationSource.CreateContributorInviteAsync` sends the **same body** on **`Channel.Spclient`** (`gew4-spclient`), no OPTIONS.
- Wavee P2 JSON: VIEWER / `ZGVmYXVsdA==` (base64 `"default"`) — `PlaylistPermissionClient.DefaultRevisionSentinel`.

Gabo: `rename_playlist`, `make_playlist_private`, `make_playlist_public`, copy-to-clipboard invite.

### 5.Z Complete session catalog (134 / prefixes 001–134)

Every row from `somemoreplaylists_saz/catalog.json`. Includes CONNECT, CDN audio 206, gabo, OPTIONS, Fiddler `/fc/latest`.

```
001 sid=1 GET 200 api.getfiddler.com /fc/latest req=empty/0b resp=text/plain/2939b enc=resp-ce:gzip,resp-te:chunked,chunked,gzip role=other proc=(none) — Fiddler auto-update GET /fc/latest
002 sid=2 CONNECT 200 Tunnel to (tunnel audio-ak.spotifycdn.com) req=-/3441b resp=-/718b enc=- role=tls proc=wavee:13440
003 sid=3 CONNECT 200 Tunnel to (tunnel gew4-spclient.spotify.com) req=-/6504b resp=-/709b enc=- role=tls proc=spotify:10160
004 sid=5 CONNECT 200 Tunnel to (tunnel gew4-spclient.spotify.com) req=-/6504b resp=-/709b enc=- role=tls proc=spotify:10160
005 sid=6 CONNECT 200 Tunnel to (tunnel gew4-spclient.spotify.com) req=-/6215b resp=-/709b enc=- role=tls proc=spotify:10160
006 sid=7 CONNECT 200 Tunnel to (tunnel gew4-spclient.spotify.com) req=-/6600b resp=-/709b enc=- role=tls proc=spotify:10160
007 sid=8 CONNECT 200 Tunnel to (tunnel gew4-spclient.spotify.com) req=-/6406b resp=-/709b enc=- role=tls proc=spotify:10160
008 sid=9 CONNECT 200 Tunnel to (tunnel gew4-spclient.spotify.com) req=-/6503b resp=-/709b enc=- role=tls proc=spotify:10160
009 sid=10 CONNECT 200 Tunnel to (tunnel gew4-spclient.spotify.com) req=-/6215b resp=-/709b enc=- role=tls proc=spotify:10160
010 sid=13 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
011 sid=14 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/586b resp=application/protobuf/560b enc=req-ce:gzip,resp-ce:zstd,req-gzip-magic,resp-zstd-magic,zstd-header-or-magic,gzip role=XM proc=spotify:10160
012 sid=15 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/588b resp=application/protobuf/560b enc=req-ce:gzip,resp-ce:zstd,req-gzip-magic,resp-zstd-magic,zstd-header-or-magic,gzip role=XM proc=spotify:10160
013 sid=16 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/585b resp=application/protobuf/560b enc=req-ce:gzip,resp-ce:zstd,req-gzip-magic,resp-zstd-magic,zstd-header-or-magic,gzip role=XM proc=spotify:10160
014 sid=17 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/585b resp=application/protobuf/560b enc=req-ce:gzip,resp-ce:zstd,req-gzip-magic,resp-zstd-magic,zstd-header-or-magic,gzip role=XM proc=spotify:10160
015 sid=18 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/27560b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
016 sid=19 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/586b resp=application/protobuf/560b enc=req-ce:gzip,resp-ce:zstd,req-gzip-magic,resp-zstd-magic,zstd-header-or-magic,gzip role=XM proc=spotify:10160
017 sid=20 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/587b resp=application/protobuf/560b enc=req-ce:gzip,resp-ce:zstd,req-gzip-magic,resp-zstd-magic,zstd-header-or-magic,gzip role=XM proc=spotify:10160
018 sid=21 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/members req=empty/0b resp=application/x-protobuf/0b enc=zstd-header-or-magic role=permission proc=spotify:10160
019 sid=23 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
020 sid=24 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/583b resp=application/protobuf/560b enc=req-ce:gzip,resp-ce:zstd,req-gzip-magic,resp-zstd-magic,zstd-header-or-magic,gzip role=XM proc=spotify:10160
021 sid=25 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=application/x-protobuf/11b enc=zstd-header-or-magic role=permission proc=spotify:10160
022 sid=26 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/586b resp=application/protobuf/560b enc=req-ce:gzip,resp-ce:zstd,req-gzip-magic,resp-zstd-magic,zstd-header-or-magic,gzip role=XM proc=spotify:10160
023 sid=27 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/diff req=empty/0b resp=application/x-protobuf/200b enc=resp-ce:zstd,resp-zstd-magic,zstd-header-or-magic role=playlist-read proc=spotify:10160
024 sid=28 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/28085b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
025 sid=29 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/104b resp=application/protobuf/532b enc=resp-ce:zstd,resp-zstd-magic,zstd-header-or-magic role=XM proc=spotify:10160
026 sid=30 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/104b resp=application/protobuf/532b enc=resp-ce:zstd,resp-zstd-magic,zstd-header-or-magic role=XM proc=spotify:10160
027 sid=31 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/583b resp=application/protobuf/560b enc=req-ce:gzip,resp-ce:zstd,req-gzip-magic,resp-zstd-magic,zstd-header-or-magic,gzip role=XM proc=spotify:10160
028 sid=32 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=application/x-protobuf/11b enc=zstd-header-or-magic role=permission proc=spotify:10160
029 sid=33 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
030 sid=34 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=application/x-protobuf/11b enc=zstd-header-or-magic role=permission proc=spotify:10160
031 sid=35 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/32713b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
032 sid=39 CONNECT 200 Tunnel to (tunnel audio-cf.spotifycdn.com) req=-/3249b resp=-/641b enc=- role=tls proc=wavee:13440
033 sid=40 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
034 sid=41 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
035 sid=45 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
036 sid=46 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/109b resp=application/protobuf/102b enc=zstd-header-or-magic role=XM proc=spotify:10160
037 sid=47 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=application/x-www-form-urlencoded/160b resp=application/x-protobuf/113b enc=zstd-header-or-magic role=rootlist proc=spotify:10160 — WRITE folder rename INNER: REM from=2 len=1 + ADD start-group edb339e1…:named+folder+update (keep create ts). rev 89→90 nonce=16
038 sid=48 CONNECT 200 Tunnel to (tunnel gew4-spclient.spotify.com) req=-/3443b resp=-/709b enc=- role=tls proc=wavee:13440
039 sid=49 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist req=empty/0b resp=application/octet-stream/9761b enc=resp-ce:gzip,resp-te:chunked,chunked,gzip role=rootlist proc=wavee:13440 — Wavee gzip rootlist GET
040 sid=50 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=application/json; charset=utf-8/159b enc=resp-ce:gzip,resp-te:chunked,chunked,gzip role=permission proc=wavee:13440 — Wavee JSON perm P2 VIEWER/ZGVmYXVsdA==
041 sid=51 CONNECT 200 Tunnel to (tunnel gew4-spclient.spotify.com) req=-/2771b resp=-/709b enc=- role=tls proc=wavee:13440
042 sid=52 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=application/json; charset=utf-8/156b enc=resp-ce:gzip,resp-te:chunked,chunked,gzip role=permission proc=wavee:13440
043 sid=55 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
044 sid=57 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/3976b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
045 sid=59 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
046 sid=60 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/11b enc=zstd-header-or-magic role=permission proc=spotify:10160
047 sid=61 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/diff req=empty/0b resp=application/x-protobuf/202b enc=resp-ce:zstd,resp-zstd-magic,zstd-header-or-magic role=playlist-read proc=spotify:10160
048 sid=62 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
049 sid=63 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=application/x-www-form-urlencoded/85b resp=application/x-protobuf/113b enc=zstd-header-or-magic role=rootlist proc=spotify:10160 — WRITE rootlist MOV from=0 len=1 to=3 (P1 into inner folder). 90→91 nonce=17
050 sid=64 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist req=empty/0b resp=application/octet-stream/9850b enc=resp-ce:gzip,resp-te:chunked,chunked,gzip role=rootlist proc=wavee:13440 — Wavee gzip rootlist GET
051 sid=65 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=application/json; charset=utf-8/201b enc=resp-ce:gzip,resp-te:chunked,chunked,gzip role=permission proc=wavee:13440
052 sid=66 CONNECT 200 Tunnel to (tunnel gew4-spclient.spotify.com) req=-/2771b resp=-/709b enc=- role=tls proc=wavee:13440
053 sid=67 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/6QbD3n4hCF6uP8jqyiDsS5/diff req=empty/0b resp=application/x-protobuf/429b enc=resp-ce:gzip,resp-te:chunked,chunked,gzip role=playlist-read proc=wavee:13440
054 sid=68 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1E8RrQBpL2fW7p/diff req=empty/0b resp=application/x-protobuf/2445b enc=resp-ce:gzip,resp-te:chunked,chunked,gzip role=playlist-read proc=wavee:13440
055 sid=69 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=application/json; charset=utf-8/256b enc=resp-ce:gzip,resp-te:chunked,chunked,gzip role=permission proc=wavee:13440
056 sid=70 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/diff req=empty/0b resp=application/x-protobuf/80b enc=zstd-header-or-magic role=rootlist proc=spotify:10160 — official rootlist /diff
057 sid=71 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
058 sid=74 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
059 sid=75 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
060 sid=76 CONNECT 200 Tunnel to (tunnel spclient.wg.spotify.com) req=-/6502b resp=-/718b enc=- role=tls proc=spotify:10160
061 sid=77 POST 200 spclient.wg.spotify.com /gabo-receiver-service/v3/events/ req=application/x-protobuf/619b resp=-/0b enc=req-ce:gzip,req-gzip-magic,zstd-header-or-magic,gzip role=telemetry proc=spotify:10160
062 sid=78 POST 200 spclient.wg.spotify.com /gabo-receiver-service/public/v3/events/ req=application/x-protobuf/497b resp=-/0b enc=req-ce:gzip,req-gzip-magic,zstd-header-or-magic,gzip role=telemetry proc=spotify:10160
063 sid=79 POST 200 gew4-spclient.spotify.com /playlist/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/changes req=application/x-www-form-urlencoded/106b resp=application/x-protobuf/210b enc=resp-ce:zstd,resp-zstd-magic,zstd-header-or-magic role=playlist-mutation proc=spotify:10160 — WRITE P1 UPDATE_LIST name=updated playlist name. 10→11 nonce=11. zstd SLC, name not in reply
064 sid=80 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
065 sid=81 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/11b enc=zstd-header-or-magic role=permission proc=spotify:10160
066 sid=83 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
067 sid=84 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/11b enc=zstd-header-or-magic role=permission proc=spotify:10160
068 sid=85 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/24149b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
069 sid=86 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/28357b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
070 sid=87 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/29999b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
071 sid=88 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/3476b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
072 sid=89 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
073 sid=91 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
074 sid=93 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
075 sid=94 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/11b enc=zstd-header-or-magic role=permission proc=spotify:10160
076 sid=95 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
077 sid=97 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
078 sid=99 POST 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base/level req=application/x-www-form-urlencoded/2b resp=application/x-protobuf/14b enc=zstd-header-or-magic role=permission proc=spotify:10160 — WRITE perm /base/level proto 08 01 BLOCKED
079 sid=100 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/12b enc=zstd-header-or-magic role=permission proc=spotify:10160 — perm GET flips BLOCKED/3b907c0d…
080 sid=101 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
081 sid=102 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/members req=empty/0b resp=application/x-protobuf/0b enc=zstd-header-or-magic role=permission proc=spotify:10160
082 sid=103 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/12b enc=zstd-header-or-magic role=permission proc=spotify:10160
083 sid=104 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
084 sid=105 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/10868b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
085 sid=106 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
086 sid=107 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/12b enc=zstd-header-or-magic role=permission proc=spotify:10160
087 sid=108 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
088 sid=109 CONNECT 200 Tunnel to (tunnel spclient.wg.spotify.com) req=-/6502b resp=-/718b enc=- role=tls proc=spotify:10160
089 sid=110 OPTIONS 200 spclient.wg.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission-grant req=empty/0b resp=-/0b enc=zstd-header-or-magic role=permission proc=spotify:10160 — OPTIONS permission-grant CORS (xpui)
090 sid=111 POST 200 spclient.wg.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission-grant req=application/json;charset=UTF-8/66b resp=application/json; charset=utf-8/249b enc=resp-ce:gzip,resp-te:chunked,chunked,zstd-header-or-magic,gzip role=permission proc=spotify:10160 — GRANT JSON CONTRIBUTOR ttlMs=604800000 on wg (Wavee body matches, host differs)
091 sid=112 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/80b resp=application/protobuf/75b enc=zstd-header-or-magic role=XM proc=spotify:10160
092 sid=114 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
093 sid=115 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/12b enc=zstd-header-or-magic role=permission proc=spotify:10160
094 sid=116 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
095 sid=117 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
096 sid=118 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/12b enc=zstd-header-or-magic role=permission proc=spotify:10160
097 sid=120 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
098 sid=121 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/1585b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
099 sid=123 POST 200 spclient.wg.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission-grant req=application/json;charset=UTF-8/66b resp=application/json; charset=utf-8/371b enc=resp-ce:gzip,resp-te:chunked,chunked,zstd-header-or-magic,gzip role=permission proc=spotify:10160 — GRANT JSON CONTRIBUTOR ttlMs=604800000
100 sid=124 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
101 sid=125 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/12b enc=zstd-header-or-magic role=permission proc=spotify:10160
102 sid=126 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
103 sid=130 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/7341b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
104 sid=134 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
105 sid=141 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
106 sid=142 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/12b enc=zstd-header-or-magic role=permission proc=spotify:10160
107 sid=143 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/1584b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
108 sid=145 POST 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base/level req=application/x-www-form-urlencoded/2b resp=application/x-protobuf/14b enc=zstd-header-or-magic role=permission proc=spotify:10160 — WRITE perm /base/level proto 08 02 VIEWER
109 sid=146 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
110 sid=147 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/12b enc=zstd-header-or-magic role=permission proc=spotify:10160 — perm GET flips VIEWER/b5483d8c…
111 sid=148 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
112 sid=149 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/12b enc=zstd-header-or-magic role=permission proc=spotify:10160
113 sid=150 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
114 sid=151 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
115 sid=155 POST 200 spclient.wg.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission-grant req=application/json;charset=UTF-8/66b resp=application/json; charset=utf-8/243b enc=resp-ce:gzip,resp-te:chunked,chunked,zstd-header-or-magic,gzip role=permission proc=spotify:10160 — GRANT JSON CONTRIBUTOR ttlMs=604800000
116 sid=156 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
117 sid=157 POST 200 spclient.wg.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission-grant req=application/json;charset=UTF-8/66b resp=application/json; charset=utf-8/254b enc=resp-ce:gzip,resp-te:chunked,chunked,zstd-header-or-magic,gzip role=permission proc=spotify:10160 — GRANT JSON CONTRIBUTOR ttlMs=604800000
118 sid=158 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
119 sid=159 GET 200 gew4-spclient.spotify.com /popcount/v2/playlist/6EVbQZBiAg9zHzMjChxvRd/count req=empty/0b resp=application/x-protobuf/6b enc=zstd-header-or-magic role=popcount proc=spotify:10160
120 sid=160 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/base req=empty/0b resp=application/x-protobuf/12b enc=zstd-header-or-magic role=permission proc=spotify:10160
121 sid=161 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/12891b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
122 sid=164 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/3131b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
123 sid=165 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
124 sid=166 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
125 sid=176 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
126 sid=178 POST 200 gew4-spclient.spotify.com /gabo-receiver-service/v3/events req=application/json/3976b resp=application/json/13b enc=zstd-header-or-magic role=telemetry proc=spotify:10160
127 sid=179 GET 206 audio-ak.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
128 sid=180 POST 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist/changes req=application/x-www-form-urlencoded/166b resp=application/x-protobuf/113b enc=zstd-header-or-magic role=rootlist proc=spotify:10160 — WRITE folder rename OUTER: REM from=0 + ADD 3dd9e795…:root+folder+updated+name. 91→92 nonce=18
129 sid=181 GET 200 gew4-spclient.spotify.com /playlist/v2/user/<user-id>/rootlist req=empty/0b resp=application/octet-stream/9772b enc=resp-ce:gzip,resp-te:chunked,chunked,gzip role=rootlist proc=wavee:13440 — Wavee gzip rootlist GET
130 sid=182 GET 200 gew4-spclient.spotify.com /playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base req=empty/0b resp=application/json; charset=utf-8/104b enc=resp-ce:gzip,resp-te:chunked,chunked,gzip role=permission proc=wavee:13440
131 sid=183 CONNECT 200 Tunnel to (tunnel gew4-spclient.spotify.com) req=-/2771b resp=-/709b enc=- role=tls proc=wavee:13440
132 sid=184 GET 200 gew4-spclient.spotify.com /playlist/v2/playlist/37i9dQZF1EP6YuccBxUcC1/diff req=empty/0b resp=application/x-protobuf/101b enc=- role=playlist-read proc=wavee:13440
133 sid=186 POST 200 gew4-spclient.spotify.com /extended-metadata/v0/extended-metadata req=application/protobuf/99b resp=application/protobuf/90b enc=zstd-header-or-magic role=XM proc=spotify:10160
134 sid=191 GET 206 audio-cf.spotifycdn.com /audio/b8bc1f56ce9b88dfc07ea33450c20fd5705a9a0f req=empty/0b resp=application/octet-stream/65536b enc=- role=cdn proc=wavee:13440
```

## 6. Wavee vs desktop gap matrix

| Area | Desktop (these SAZs) | Wavee | Verdict |
|---|---|---|---|
| Create playlist | `POST /playlist/v2/playlist/{newId}/changes` + base `0,726f6f74` + UPDATE_LIST name + sync-reason 12; then rootlist ADD | `PlaylistMutationSource.CreatePlaylistAsync` → `POST /playlist/v2/playlist` + `ListUpdateRequest` / `CreateListReply`, then `RootlistOps.PostRootlistOpsAsync` ADD | **Different wire** |
| Item MOV | Mov fields 1–3 **plus field 4 `repeated Item`** | `PlaylistWireMapper.ToWireOp` emits index-only Mov; proto has no field 4 | **Different wire / proto gap** |
| Item REM | keyed REM (`items_as_key` + uri) on P1 | `RemoveRowsAsync` builds index REM (items attached but `ItemsAsKey` only on unfollow path) | **Different wire** |
| Folder create | rootlist ADD start-group + end-group | no API; UI locked deferred (`Menus.cs` “locked decision 9”, `SidebarPaneSlot` no “New folder”) | **Missing + UI locked** |
| Folder rename | index REM + ADD same id new name; keep timestamp; don’t touch end-group | same lock | **Missing + UI locked** |
| ChangeInfo field 6 | varint sequence nonce (16/17/11/18) | proto `bool merge`; Wavee sets `Merge=true` and puts nonce on `ListChanges.nonces` | **Proto wrong + different envelope** |
| want_* / ListChanges.nonces | desktop **omits** both want flags and ListChanges.nonces | `BuildChanges` / `BuildRootlistChanges` set both want_* + random `Nonces.Add` | **Different envelope** (server still 200s) |
| Permission GET | proto `Permission` (`application/x-protobuf`), no Accept:json | `PlaylistPermissionClient.GetBasePermissionAsync` JSON Accept | **Different wire** (both work; Wavee JSON on 우울해 / P2) |
| Permission set | `POST …/permission/base/level` proto `SetPermissionLevelRequest` (2 bytes) | `SetBasePermissionAsync` JSON `POST …/permission/base` with revision + level | **Different path + codec** |
| Grant | JSON body identical; host **wg** + OPTIONS | same body; host **gew4-spclient**; no OPTIONS | **Body match, host differs** |
| Recents | `/playlist/v2/list/recents/main/diff` | `RecentsFetcher` `/list/recents/page` + `/page/diff` | **Different path** |
| XM kind 205 | unused | proto has `LIST_METADATA_V2=205`; do not start sending it | **OK if unused** |
| Pathfinder home | hash `76243c78…` | `PathfinderOps.HomeHash` = `9052ac65…` | **Hash mismatch** |
| Pathfinder feedBaselineLookup | `a950fb7c…` | `PathfinderOps.FeedBaselineLookupHash` same | **Match** |
| trackPreview / lookupChildEntities / assisted-curation | present | **missing** | **Missing** |
| Extender | `POST wg /playlistextender/extendp/` JSON + zstd JSON resp | `PlaylistExtenderClient.ExtendAsync` same path/body via `Channel.SpclientWg` + `SpotifyZstd` | **Implemented** (shape matches 073) |
| Popcount | proto field 7 | `SpotifyPlaylistPopcountService` + `popcount.proto` | **Implemented** (not called in these Wavee sessions) |
| Follow | rootlist ADD/REM | `RootlistOps` follow/unfollow | **Implemented** (same idea) |
| Dealer apply | (see §9) | `DealerRouter.OnPlaylist` → `PlaylistModificationInfo` / `RootlistModificationInfo` | **Implemented; HTTP↔dealer mismatch PENDING** |

## 7. Implementation notes for Fable 5

Do **not** implement unless asked. When asked, the proto edits are:

1. **`Mov`** in `playlist4_external.proto`: add `repeated Item items = 4;` (item-keyed move; fields 1–3 remain for index moves / rootlist). Update `PlaylistWireMapper.ToWireOp` and the decoder so item-keyed MOV round-trips. Rootlist MOVs in these captures stay index-only.
2. **`ChangeInfo` field 6**: change `optional bool merge = 6` to a varint (e.g. `optional int64 sequence = 6` or a new name). Desktop uses it as a per-session nonce (B: 16, 17, 11, 18). Do not keep writing `Merge = true`.
3. Create-via-`/changes` + `0,726f6f74` + sync-reason 12 is the desktop mint. Wavee’s `POST /playlist/v2/playlist` still 200s today; migrating is a product decision, not a proto typo.
4. Folder rename/create stay **UI-locked** (`Menus.cs` locked decision 9) until the human lifts that. The wire is fully known from A 164/172 and B 037/128.
5. Permission: desktop proto GET + `/base/level` POST. Wavee JSON GET/POST `/permission/base` is a parallel working dialect. Grants: keep the nested JSON body; host wg vs gew4 is the only delta.
6. Do not add XM kind 205, trackPreview, lookupChildEntities, or assisted-curation unless a later capture proves Wavee needs them.
7. Zero managed alloc in engine phases 6–13 still applies; this work is Backend/SpotifyLive, not the GPU frame loop.

## 8. File references

### SAZs (do not modify)

- `c:\Users\<user>\Documents\Fiddler2\Captures\playlist_operations.saz`
- `c:\Users\<user>\Documents\Fiddler2\Captures\somemoreplaylists.saz`

### Extracts + catalogs

- `C:\Users\<user>\AppData\Local\Temp\playlist_operations_saz\catalog.json`
- `C:\Users\<user>\AppData\Local\Temp\playlist_operations_saz\catalog.txt`
- `C:\Users\<user>\AppData\Local\Temp\playlist_operations_saz\extracted`
- `C:\Users\<user>\AppData\Local\Temp\somemoreplaylists_saz\catalog.json`
- `C:\Users\<user>\AppData\Local\Temp\somemoreplaylists_saz\extracted`
- Dealer: `c:\Users\<user>\AppData\Local\Wavee\Logs\dealer\dealer-20260815.idx.ndjson`

### Protos

- `c:\WAVEE\fluent-gpu\src\apps\Wavee\SpotifyLive\Protos\playlist4_external.proto`
- `c:\WAVEE\fluent-gpu\src\apps\Wavee\SpotifyLive\Protos\playlist_permission.proto`
- `c:\WAVEE\fluent-gpu\src\apps\Wavee\SpotifyLive\Protos\popcount.proto`
- `c:\WAVEE\fluent-gpu\src\apps\Wavee\SpotifyLive\Protos\extended_metadata.proto`
- `c:\WAVEE\fluent-gpu\src\apps\Wavee\SpotifyLive\Protos\extension_kind.proto` (`LIST_METADATA_V2 = 205`)

All 40 protos in that folder. Generated C# is not checked in.

### Wavee symbols (path + type)

| Symbol | Path |
|---|---|
| `PlaylistMutationSource` | `src/apps/Wavee/Backend/Playlists/PlaylistMutationSource.cs` — `CreatePlaylistAsync`, `AddTracksAsync`, `RemoveRowsAsync`, `MoveRowsAsync`, `MoveRootlistItemAsync`, `UpdateDetailsAsync`, `CreateContributorInviteAsync` |
| `PlaylistFetcher` | `src/apps/Wavee/Backend/Playlists/PlaylistFetcher.cs` — `FetchPlaylistAsync`, `FetchRootlistAsync`, `FetchPlaylistDiffAsync` |
| `RootlistOps` | `src/apps/Wavee/Backend/Playlists/RootlistOps.cs` — `TryPostRootlistOpsAsync`, `BootstrapRootlistAsync`, `FindPlaylistIndex` |
| `PlaylistPermissionClient` | `src/apps/Wavee/Backend/Playlists/PlaylistPermissionClient.cs` — `GetBasePermissionAsync`, `SetBasePermissionAsync`, `DefaultRevisionSentinel` |
| `PlaylistExtenderClient` | `src/apps/Wavee/Backend/Playlists/PlaylistExtenderClient.cs` — `ExtendAsync` |
| `PlaylistWireMapper` | `src/apps/Wavee/Backend/Playlists/PlaylistWireMapper.cs` — `BuildChanges`, `BuildRootlistChanges`, `BuildCreateListRequest`, `ToWireOp` |
| `PathfinderClient` / `PathfinderOps` | `src/apps/Wavee/SpotifyLive/PathfinderClient.cs` — `HomeHash`, `FeedBaselineLookupHash` |
| `SpotifyZstd` | `src/apps/Wavee/Backend/Spotify/SpotifyZstd.cs` — `MaybeDecompressZstd` |
| `RecentsFetcher` | `src/apps/Wavee/Backend/Playlists/RecentsFetcher.cs` — `/list/recents/page` |
| `DealerRouter.OnPlaylist` | `src/apps/Wavee/Backend/Realtime/DealerRouter.cs` |
| Folder UI lock | `src/apps/Wavee/Actions/Menus.cs` (~line 673), `src/apps/Wavee/Features/Sidebar/Pane/SidebarPaneSlot.cs` (~line 1050) |

## 9. Dealer vs HTTP (state-mismatch)

Decoded from Wavee's own archive against both SAZ windows. Do not soften the rootlist corruption: every rootlist push in this log is the wrong proto, parsed as the wrong message, and the second copy of each pair writes URI bytes into the stored revision.

### 9.1 Clock

| Fact | Value |
|---|---|
| Index | `c:\Users\<user>\AppData\Local\Wavee\Logs\dealer\dealer-20260815.idx.ndjson` (27 484 B, **206** rows) |
| Frames | sibling `dealer-20260815.bin` (219 037 B) |
| `t` | **UTC unix-ms** (not local) |
| Filename date | **local** (`20260815`). First row `t=1786784909948` = 2026-08-15 **09:08:29.948 UTC** = **11:08:29.948 +02**. Last `t=1786798372340`. |
| Capture A window | ≈ `1786796441000`–`1786796537000` (14:20:41–14:22:17 +02) |
| Capture B window | ≈ `1786797261000`–`1786797328000` (14:34:21–14:35:28 +02) |

Session-long traffic (pusher, connect-state) sits outside these windows. Playlist rows are under `hm://playlist/` and `hm://playlist-permission/`.

### 9.2 Archive schema

Each idx row: `typ` / `uri` / `handled` / `n` / `off` (plus `t`). Bytes `[off, off+n)` in the `.bin` are the raw dealer JSON `{headers,payloads,type,uri}`. `payloads[0]` is **base64 protobuf**.

`handled` is `DealerArchive.IsHandled` — **prefix match only** (`src/apps/Wavee/Diagnostics/DealerArchive.cs` ~68–88):

- `hm://pusher/v1/connections/`
- `hm://connect-state/v1/cluster`
- `hm://connect-state/v1/connect/volume`
- `hm://playlist/`
- `hm://collection/`
- `hm://presence2/user/`

**`hm://playlist-permission/` is NOT in `IsHandled`.** Both permission/state frames in B are `handled=false`.

All-day playlist-ish topic counts in this file: 18 rootlist v2 + 18 rootlist non-v2 (always a pair), 11 P1 v2, 2 P2 v2, 2 P3 v2, 2 permission/state, plus editorial `37i9*` heads.

### 9.3 Handlers

`DealerRouter.OnEvent` / `OnPlaylist` — `src/apps/Wavee/Backend/Realtime/DealerRouter.cs`:

- Topic starts with `hm://playlist/` → `OnPlaylist`. **`hm://playlist-permission/` is ignored** (falls off `OnEvent` with no other arm).
- Topic **ends with `/rootlist`** (v2 or not) → `RootlistModificationInfo.Parser.ParseFrom`. Fields: `new_revision=1`, `parent_revision=2`, `ops=3`.
- Else → `PlaylistModificationInfo.Parser.ParseFrom`. Fields: `uri=1`, `new_revision=2`, `parent_revision=3`, `ops=4`. URI from `info.Uri` or `PlaylistUriFromTopic`.
- Parse fail → **`catch { return; }` silent drop.**
- Enqueue `SyncCommand(SyncKind.RootlistPush|PlaylistPush)` with `PlaylistWireMapper.MapOps`.

`LibrarySync.RootlistPushAsync` — `src/apps/Wavee/Backend/Sync/LibrarySync.cs` ~277:

- If `stored != null && parentRev != null && ops != null && BytesEqual(stored, parentRev)` → `PlaylistDiffApplier.Apply` + `SetRootlist(..., newRev)`.
- Else → **full GET** `FetchRootlistAsync`. This is the Wavee rootlist “poll” in Fiddler (A: 15 gzip GETs, each ~30–70 ms after a desktop rootlist `/changes`).

`LibrarySync.PlaylistPushAsync` — same file ~443:

1. Echo-drop if `stored == newRev`.
2. Signal shape: `newRev` length 24, no parent, no ops → `MarkFullRefresh` / fetch if open / else dirty.
3. Parent match + resident membership → `Apply` + hydrate added URIs; `UPDATE_LIST` also refreshes the header.
4. Else open → revalidate; closed → dirty.

`PlaylistWireMapper.MapOps` — `src/apps/Wavee/Backend/Playlists/PlaylistWireMapper.cs` ~39:

- **No `deleted_by_owner`.** `PatchOf` reads name / description / picture / collaborative only (`ListAttributes` field 6 is dropped).
- **MOV is index-only** (`FromIndex` / `Length` / `ToIndex`). `Mov.items = 4` is absent from Wavee's proto, so an item-keyed HTTP MOV decodes as 0/0/0. Dealer never sent field 4 in this log (see §9.5 / §9.7).

`MapOps` on an empty `RepeatedField` returns an **empty list, not null.** `ops is not null` in `RootlistPushAsync` is therefore true for every 78 B rootlist push.

### 9.4 CRITICAL — rootlist proto mismatch

**Every rootlist push in this archive is 78 B `PlaylistModificationInfo` fields 1–2 only** (`uri` = `spotify:user:<user-id>:rootlist`, `new_revision` = 24 B head). **No `parent_revision`. No `ops`.**

Each head arrives **twice in the same millisecond**:

| Topic | idx `n` (JSON wrapper) | Payload |
|---|---|---|
| `hm://playlist/v2/user/<user-id>/rootlist` | 219 | 78 B PMI |
| `hm://playlist/user/<user-id>/rootlist` | 216 | **identical** 78 B PMI |

Wavee keys the branch on `EndsWith("/rootlist")` and parses **both** as `RootlistModificationInfo`:

| Wire (PMI) | RMI field Wavee reads | Result |
|---|---|---|
| field 1 URI string (54 B) | `NewRevision` | URI bytes stored as the “new revision” |
| field 2 real 24 B head | `ParentRevision` | real rev treated as parent |
| (absent) | `Ops` | empty list |

`RootlistPushAsync` then:

1. **First of the pair.** `stored` is the previous real head (from bootstrap / last GET). `parentRev` is the *new* real head. They differ → **full GET**. Tree and stored revision converge. This is every Wavee rootlist GET in the SAZ.
2. **Second of the pair (same ms, same 78 B).** GET just wrote `stored ==` the real head, which is now `parentRev`. `ops` is empty-but-not-null. Condition hits. Apply is a no-op on members. `SetRootlist(..., newRev)` writes **URI string bytes** as the stored rootlist revision. **Corruption.**

Next pair: stored is URI bytes, misparsed parent is the next real head → mismatch → GET (tree OK again) → second copy corrupts again. After the last pair of a burst, stored revision **stays** the URI bytes until the next rootlist push.

Do not treat this as “Wavee polls rootlist.” The GET is the fallback of a misparsed head-only PMI. The poll is a symptom; the second apply is the bug.

### 9.5 Event tables

Times below are **local +02**. `Δ` is dealer `t` minus HTTP `/changes` start. Long HTTP (P3 create 709 ms, rootlist 257 363 ms) delays the echo by the same amount; after the response the echo is still ~30–80 ms. **Every HTTP `/changes` 200 in A and B had a dealer echo.** Intermediate rootlist revs **81–82** and **86–87** never appear on dealer — they are folded into heads **83** and **88** (HTTP 265 and 466 are 3-delta `ListChanges`).

#### P1 `6EVbQZBiAg9zHzMjChxvRd`

| Local | HTTP | Parent → new | Dealer ops | Notes |
|---|---|---|---|---|
| 14:20:40.962 | (no `/changes`; lens) | `0,726f6f74` (8 B) → `1,263a5b60…` | `UPDATE_LIST` name `Daily Mix 1 (2)` | PMI, not a user edit |
| 14:20:41.074 | — | (none) → `2,5e935cab…` | none (69 B head-only) | Wavee **signal shape** → full refresh / dirty |
| 14:20:49.811 | 111 Δ56 ms | 2 → `3,e8d6e272…` | `ADD from=50` `0hqj5JBnFt1BHEz2UCFwrl` | matches HTTP ADD |
| 14:20:51.650 | 129 Δ51 ms | 3 → `4,5e8ce271…` | `ADD from=51` `5kPpA4aMFeAQnahSnTIOi4` | matches HTTP ADD |
| 14:20:56.163 | 143 Δ51 ms | 4 → `5,fbbdc277…` | **3× REM** `from=33/39/48` + uri (`3Sfb…`, `4dMG…`, `0hqj5…`); `items_as_key` unset | HTTP catalogued as one keyed REM; dealer delivered three index+uri REMs |
| 14:20:59.913 | 148 Δ79 ms | 5 → `6,d99f4045…` | **positional MOV** `{from=33,len=1,to=32}` + `{35,2,33}`; `items4=0` | HTTP was **item-keyed MOV** (Mov field 4). Dealer rewrote. |
| 14:21:03.764 | 154 Δ87 ms | 6 → `7,12606d67…` | positional MOV `{2,1,0}`; `items4=0` | HTTP item-keyed → dealer positional |
| 14:22:04.310 | 498 Δ42 ms | 7 → `8,f4863814…` | 3× positional MOV `{7,1,49}` `{8,1,49}` `{38,1,49}` | HTTP item-keyed → dealer positional |
| 14:22:05.824 | 512 Δ39 ms | 8 → `9,e7e727c1…` | REM `from=48` `37S86pw74OH8j96ZmMnrpR` | HTTP keyed REM; dealer index+uri |
| 14:22:07.802 | 515 Δ43 ms | 9 → `10,6fd020b3…` | REM `from=18` `7mFigNlS2dsKMhcmJyfpeg` | same |
| 14:34:43.608 | B 063 Δ58 ms | 10 → `11,c08dbb64…` | `UPDATE_LIST` name `updated playlist name` | matches HTTP; SLC reply omitted the name |

P1 ladder on dealer: 1 (lens) → 2 (head) → 3…11. Same heads as HTTP 2→10 (A) and 10→11 (B).

#### P2 `6QbD3n4hCF6uP8jqyiDsS5`

| Local | HTTP | Parent → new | Dealer ops |
|---|---|---|---|
| 14:21:14.591 | 178 Δ94 ms | `0,726f6f74` → `1,70fc7b82…` | `UPDATE_LIST` name `My Playlist #9` |
| 14:21:20.432 | 210 Δ47 ms | 1 → `2,16de7082…` | `ADD from=0` `4mqfe9XrgEOSsofvq5MyjR` |

Create-via-`/changes` **does** produce a dealer `PlaylistModificationInfo` on `hm://playlist/v2/playlist/{id}`. Wavee is subscribed. The gap is Wavee's own create path (`POST /playlist/v2/playlist` + `ListUpdateRequest`), not a missing topic.

#### P3 `4vkIrispQ6gcMNIojGPd0L`

| Local | HTTP | Parent → new | Dealer ops |
|---|---|---|---|
| 14:21:26.955 | 218 Δ712 ms (HTTP itself 709 ms) | `0,726f6f74` → `1,398040e2…` | `UPDATE_LIST` name `My Playlist #10` |
| 14:21:47.923 | **no playlist `/changes`** | 1 → `2,16dceb57…` | `UPDATE_LIST` **`deleted_by_owner=true`** (`ListAttributes` field 6; raw values `\n\x020\x01` / `0a023001`) |

Delete is **not** a playlist `/changes`. HTTP 281 is a **rootlist** index REM (uri present). Dealer announces the tombstone on the playlist topic as `UPDATE_LIST deleted_by_owner=true`. Wavee `PatchOf` **drops the flag**. The sidebar tree drops P3 only if the subsequent rootlist GET (A 282) omits the URI.

#### Rootlist `spotify:user:<user-id>:rootlist`

Every row: 78 B PMI, no ops, v2+non-v2 pair, Wavee RMI-misparse as in §9.4.

| Local | HTTP | Dealer head | Folded |
|---|---|---|---|
| 14:20:40.992 | (session / P1 lens; Wavee GET 052) | **71** `29056acc…` | |
| 14:21:07.161 | 164 Δ39 ms | **72** `f9465fcd…` | folder `edb339e1…` |
| 14:21:08.538 | 168 Δ50 ms | **73** `e179565c…` | MOV into folder |
| 14:21:12.169 | 172 Δ58 ms | **74** `fab43fec…` | folder `3dd9e795…` |
| 14:21:14.591 | 187 Δ53 ms | **75** `a6708a08…` | ADD P2 (same ms as P2 create PMI) |
| 14:21:26.325/331 | 228 Δ37 ms | **76** `605874b0…` | ADD P3 |
| 14:21:27.584/586 | 240 Δ62 ms | **77** `c35279bf…` | MOV |
| 14:21:30.733 | 249 Δ58 ms | **78** `7f8aded1…` | MOV |
| 14:21:32.979/983 | 252 Δ42 ms | **79** `1f8ab625…` | MOV |
| 14:21:35.090 | 257 Δ365 ms | **80** `ec59b964…` | MOV (HTTP 363 ms) |
| 14:21:40.472 | 265 Δ35 ms | **83** `920547bf…` | **81–82 folded** (HTTP 3 deltas) |
| 14:21:43.075/076 | 272 Δ34 ms | **84** `e81f8c19…` | MOV |
| 14:21:47.904/905 | 281 Δ32 ms | **85** `8f605f1b…` | index REM P3; P3 `deleted_by_owner` PMI 18 ms later |
| 14:21:51.711/716 | 466 Δ72 ms | **88** `8b589354…` | **86–87 folded** (HTTP 3 deltas) |
| 14:21:53.875 | 477 Δ37 ms | **89** `038ff395…` | MOV |
| 14:34:32.044 | B 037 Δ45 ms | **90** `57bca12a…` | inner folder rename |
| 14:34:36.929 | B 049 Δ39 ms | **91** `c3fa4153…` | MOV `from=0 len=1 to=3` |
| 14:35:25.673 | B 128 Δ44 ms | **92** `b4700ed1…` | outer folder rename |

Dealer never carries the REM+ADD folder-rename ops or the MOV indices. Wavee only sees a new head → GET. Folder rename is therefore **GET-converged**, not applied in place. After the second copy of each pair, stored rev is the URI string until the next GET.

#### Permission P1

| Local | HTTP | Topic | `handled` | Payload |
|---|---|---|---|---|
| 14:34:53.602 | B 078 Δ41 ms `POST …/base/level` `08 01` BLOCKED | `hm://playlist-permission/v1/playlist/6EVbQZBiAg9zHzMjChxvRd/permission/state` | **false** | `PermissionStatePub`: base revision `3b907c0d29c940a3`, level **1 BLOCKED** |
| 14:35:07.409 | B 108 Δ40 ms `08 02` VIEWER | same topic | **false** | revision `b5483d8c3aaaa1c8`, level **2 VIEWER** |

Both dropped: not in `IsHandled`, not in `OnEvent`. Four CONTRIBUTOR grants (B 090 / 099 / 115 / 117, JSON `ttlMs=604800000` on `spclient.wg.spotify.com`) produced **no dealer topic**.

### 9.6 Dropped topics

In this file, `handled=false` playlist-related frames are exactly the two `hm://playlist-permission/…/permission/state` rows. Other unhandled traffic (herodotus, empty `uri`) is out of scope.

Not present at all (so not “dropped” — never published):

- any `hm://playlist-permission/…/permission-grant`
- any dealer copy of HTTP item-keyed `Mov.items`
- any dealer copy of rootlist ops (ADD/REM/MOV/folder rename)
- rootlist heads 81, 82, 86, 87

`37i9*` editorial heads during the A home flood (14:21:51) are `handled=true` 69 B PMI (uri + new rev 0, no parent, no ops). Wavee classifies them as **signal shape**.

### 9.7 Mismatch matrix

| Fact | HTTP (desktop) | Dealer | Wavee |
|---|---|---|---|
| Rootlist push shape | `/changes` with ops + parent + new rev | 78 B PMI **uri + new_revision only**, twice (v2 + non-v2) | Parsed as RMI → URI becomes `NewRevision`, real rev becomes `ParentRevision`, ops empty. First copy GET (tree OK). Second copy empty apply + **corrupts stored revision**. |
| Rootlist 81–82, 86–87 | Present as intermediate deltas inside 265 / 466 | **Absent** (folded into 83 / 88) | GET lands on the folded head; OK if GET body is the head |
| Folder rename | REM+ADD on `/changes` (B 037 / 128) | Head-only 90 / 92 | No in-place apply; tree OK only via GET |
| P3 delete | Rootlist index REM only (281). **No** playlist `/changes` | Playlist `UPDATE_LIST deleted_by_owner=true` (1→2) | Flag dropped. Tree drops P3 only if GET omits URI |
| Permission base | POST `08 01` then `08 02` | `permission/state` BLOCKED then VIEWER | **Dropped** (`handled=false`, no router arm) |
| CONTRIBUTOR grants | 4× HTTP POST | **No topic** | Cannot wait on dealer; HTTP response is the only ack |
| Item-keyed MOV | A 148 / 154 / 498 `Mov` field 4 = repeated Item | **Positional MOV** (fields 1–3, `items4=0`) | `MapOps` index-only matches dealer, not HTTP. HTTP decoder still 0/0/0 |
| Keyed REM | A 143 / 512 / 515 `items_as_key` | Index+uri REM, `items_as_key` unset (143 = **3** REMs) | `MapOps` can apply if parent matches; echo-drop if Wavee already stored `newRev` from a local write (not the case here — Wavee is the watcher) |
| Create `0,726f6f74` | `/changes` + UPDATE_LIST name | Same PMI on `hm://playlist/v2/playlist/{id}` | Topic is subscribed. Wavee's *own* create still uses `POST /playlist/v2/playlist` (different wire; §6) |
| Head-only playlist PMI | — | P1 rev 2; all `37i9*` | Treated as signal regeneration (gate 2 in `PlaylistPushAsync`) |
| Echo timing | `/changes` 200 | +30–80 ms (plus HTTP duration) | Echo-drop only if stored already equals `newRev` |

### 9.8 Recommended fixes (describe only)

No `FG_*` flags. Ship as the default path.

1. **Parse rootlist 78 B as `PlaylistModificationInfo`.** Detect PMI vs RMI: field 1 is UTF-8 `spotify:user:…:rootlist` (or any `spotify:` URI), not a 24 B revision. v2 and non-v2 topics carry the same message.
2. **Do not apply empty-ops + mismatched `newRev`.** A head-only rootlist push is a revision hint: GET if `stored != newRev`, no-op if already equal. Never `SetRootlist` with a non-24-byte `newRev`. Never treat URI bytes as a revision.
3. **Dedup the v2 + non-v2 pair** (same `t` / same 24 B head / same 78 B). One enqueue per head.
4. **Map `deleted_by_owner`.** When true, remove the playlist from the rootlist tree and drop the local snapshot even if no rootlist REM has arrived yet. `PatchOf` must not silently discard `ListAttributes` field 6.
5. **Handle `hm://playlist-permission/`.** Add the prefix to `IsHandled` and an `OnEvent` arm; parse `PermissionStatePub` and apply BLOCKED / VIEWER. Grants have no dealer topic — do not wait for one after `permission-grant`.
6. **Treat dealer MOV as positional.** HTTP item-keyed MOV is rewritten on the wire before dealer. `MapOps` index-only is correct for dealer; the proto hole (`Mov.items = 4`) remains an HTTP encode/decode bug, not a dealer-apply bug.
7. **Do not classify every 24 B / no-parent / no-ops playlist PMI as a signal regeneration.** That shape is also “new head, fetch or dirty” (P1 rev 2, `37i9*`). Signal apply stays on the explicit tuning path.

Verify by replaying this archive: after each rootlist pair, `RootlistRevision()` must still be the 24 B head (71…92), never the URI string; P3 must disappear on the `deleted_by_owner` push; P1 permission must flip BLOCKED → VIEWER without a GET.

---

## Errata (verified 2026-08-15)

The following handoff claims were **verified against the raw captures on 2026-08-15 and found wrong**; the plan
(`docs/plans/wavee/` playlist wire + sync rework) and the skill doc supersede them. The rest of this document stands.
Everything below was checked byte-for-byte against `playlist_operations.saz` / `somemoreplaylists.saz` raw sessions
and `dealer-20260815.idx.ndjson`, and is now pinned by the goldens in `src/apps/Wavee.Tests/Fixtures/playlist-wire/`.

### E.1 — Claims that are wrong

| Handoff claim | Reality |
|---|---|
| Desktop **omits** `want_resulting_revisions` / `want_sync_result` / `ListChanges.nonces` | **Every** one of 32 writes ends `18 01 20 01 30 <n>` — all three present. Wavee's `BuildChanges` envelope was already wire-correct. |
| `ChangeInfo` field 6 is a varint nonce (16/17/11/18) | Mis-attributed. `Delta.info` = `{user(1), timestamp(2)}` **only**. The varint is `ListChanges.nonces(6)`: a **per-list monotonic counter per app session** (P1: 1..11, rootlist: 1..18, P2: 1,2, P3: 1), echoed back in `SelectedListContent.nonces(14)`. `ChangeInfo.merge=6` is unrelated. |
| Wavee sets `Merge=true` | Wavee set `Admin=true, Undo=true, Merge=true` on playlist writes; the rootlist path set none. Desktop sends **neither**. |
| Item-keyed MOV = `Mov.items=4` only | `Mov { repeated Item items=4; Item add_before_item=5; Item add_after_item=6; bool add_first=7; bool add_last=8 }` — identical to librespot's modern proto. Fields 1–3 are **absent** on item-keyed MOVs; items carry only `attributes.item_id(12)`. Items land immediately **after** `add_after_item`; `add_first`/`add_last` are the ends. The dealer echo is rewritten to positional `Mov{1,2,3}`. |
| Create base is 24 B `0,726f6f74` + 16 zeros | **8 bytes**: `00000000 726f6f74`. The dealer echoes the same 8 B as `parent_revision`. |
| A 143 = one keyed REM | **Three** `Op{REM}` in **one Delta**, one URI each, `items_as_key=true`, `Item{uri, attrs{item_id}}`. The server resolves all three against the base revision. |
| Folder rename REM "still carries the uri" | `REM{from_index, length=1}` with **no items**; the ADD re-inserts the start marker at the same index with the new name and the marker's **original create timestamp**. One Delta, two ops. |
| 29 writes / capture A starts at prefix 048 | Capture A raw holds **528 sessions from 001**; **001–047 are uncatalogued** and contain the whole create recipe (see E.2). **32 writes** total. The "lens UPDATE_LIST" in §9.5 is HTTP 031. |
| Popcount always 0 | 3 samples show `count=1, flag=1`. |
| XM kind 205 | Desktop uses kind **225** for playlist URIs (even pre-create). |
| §7 "proto edits needed" for permission | `PermissionStatePub`, `SetPermissionLevelRequest`, `SetPermissionResponse`, `PermissionGrant*` **already exist** in `playlist_permission.proto`, and `ListAttributes.deleted_by_owner=6` already exists in `playlist4_external.proto`. Only call sites/mappers were missing. |
| Folders "no API" | The folder read model (`RootlistTreeBuilder`) and folder-aware **MOV** (`RootlistOps.TryBuildMove`) were already shipped; only create/rename/delete were missing. The `Menus.cs` lock text was stale on "move". |

Since verification, the code has changed accordingly: `ChangeInfo` is user+timestamp only, MOV/REM are item-keyed,
ADD carries client-minted `item_id`s, create goes through `/changes` against the 8-B base, permission uses the proto
dialect, and folder create/rename/delete are implemented.

### E.2 — Confirmed facts the handoff lacked

- **Sessions 001–047 (capture A) are uncatalogued** and hold the create recipe: `031` = create via `POST
  /playlist/v2/playlist/{clientMintedId}/changes` (sync-reason `CAw=` / 12, base `00000000726f6f74`, one
  `UPDATE_LIST{name}`); `042` = the rootlist `ADD` of the new playlist with `attrs{ts, public=1}`; `046` = a 50-track
  `ADD add_last=1` with a per-item 8-byte `item_id` (sync-reason `CAI=` / 2). Any analysis that starts at prefix 048
  misses the entire create path.
- **The server keeps client-minted `item_id`s.** A 046 minted 50 ids; the next full GET (A 058) returned all 50
  byte-identical, and the keyed MOV in A 148 addressed rows by those same ids. Keyed ops are therefore safe to build
  on, and an optimistically inserted row has a stable identity the instant it appears.
- **Keyed MOV field numbers**: `items=4`, `add_before_item=5` (declared, never observed), `add_after_item=6`,
  `add_first=7`, `add_last=8`; fields 1–3 unset. See E.1.
- **The create base is 8 bytes** and is a wire value only — it must never be stored as a revision. See E.1.
- **The rootlist dealer push is a head-only `PlaylistModificationInfo`** (uri + 24-B `new_revision`, no parent, no
  ops) delivered **twice** (v2 + non-v2 topics), not a `RootlistModificationInfo`. §9.4 diagnosed the mismatch; the
  shape above is the confirmed one.
- **`hm://playlist-permission/…/permission/state`** carries `PermissionStatePub` with **no uri** — the playlist comes
  from the topic. Grants have no dealer topic at all.
- **Compression**: playlist `/changes` responses are always zstd; rootlist `/changes` is uncompressed for a
  single-delta write and zstd for multi-delta. Sniff the magic, never trust `Content-Encoding`.

### E.3 — Where the current truth lives

`.claude/skills/wavee/wavee-playlist-mutations/SKILL.md` — the maintained wire-contract tables, the dealer gate
trees, invariants I1–I8, the golden-fixture manifest and the remaining desktop-unverified shapes (folder delete).
Read it, not this document, before changing the write path.
