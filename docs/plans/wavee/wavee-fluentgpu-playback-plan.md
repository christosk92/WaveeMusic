I have full grounding from the evidence bundle, both drafts, all three critiques, and the gap map. This is a synthesis-and-authoring task — no repo spelunking needed since the critics already verified every seam and wire fact against the bundle. Producing the final plan directly.

---

# Wavee FluentGpu — End‑to‑End Spotify Playback Implementation Plan

**Target repo:** `C:\wavee\fluent-gpu\app`
**Fidelity target:** Spotify desktop `129300667` (1.2.93.667), Win32_ARM64 — state reporting, play registration, and resume all indistinguishable from the native client, fitted to the FluentGpu single‑writer / NativeAOT model.
**Ground truth:** byte‑exact decode of `total.saz` / `spotify_real.saz` / `desktop_capture_2.saz`. Where a proto field number, endpoint, header, or encoding is cited, it is the wire value, not a guess.
**Delivery bias:** get correctly‑reported music **audible first (M0→M1)**, then the play‑count plane (M2), then resume (M3), then fidelity siblings, podcasts, and ads.

---

## 1. Executive summary + the three reporting planes

The gap map is accurate: this repo is **not a stub**. The Connect control plane (dealer WS, connection‑id, PutState publisher, remote‑command routing), context/autoplay resolution, and the full local audio decrypt/decode/output chain (storage‑resolve → PlayPlay VM → AES‑128‑CTR → NVorbis/FLAC → WASAPI) already exist and are wired. The genuine engineering is narrow and concentrated: (a) the **gabo play‑registration plane** (`GaboTelemetry.ReportPlay` is a log‑only stub, `TelemetryProjection` only fires on `Started`/`TrackChanged` with no segments/durations/reasons); (b) **herodotus resume points**; (c) **podcast/episode playback**; and (d) **field‑level fidelity hardening** of the already‑working PutState and audio planes. Three independent reporting planes carry playback state to Spotify, and conflating them is the single most common design error:

**State plane — connect‑state PutState.** `PUT {ap}-spclient.spotify.com/connect-state/v1/devices/{device_id}`, body a gzipped `PutStateRequest` (via non‑standard `X-Transfer-Encoding: gzip`), response an authoritative brotli `Cluster`. This is the Connect **reply/visibility** plane: it makes the device controllable, shows now‑playing/queue/greyed transport buttons on other Connect devices, and carries the position **anchor** (not a live counter). It is **event‑driven on state edges** (play/pause/seek/track/context/queue/volume), never polled, and **does not count a play**. It is already wired through `DeviceStatePublisher` (single writer); the work is field parity, not new plumbing.

**Play‑registration plane — gabo `RawCoreStream`.** `POST spclient.wg.spotify.com/gabo-receiver-service/v3/events/` (trailing slash, **`wg` host**), `application/x-protobuf`, standard `content-encoding: gzip`, success = HTTP 200 empty body. This is the native libspotify‑core telemetry channel and the **only** plane that increments play counts and populates Recently Played. It must not be confused with the JSON `gew4-spclient…/gabo-receiver-service/v3/events` channel (no trailing slash, `context_sdk={4.0.3, javascript}`) which is web‑UI analytics and counts nothing. A play is registered by a `RawCoreStream` + at least one `RawCoreStreamSegment{is_last=1}` + a clean `ContentIntegrity`, batched (100 events / 125 kB / 300 s heartbeat) and posted with genuine anti‑fraud fields. This plane is **entirely missing** and is the headline build.

**Resume plane — herodotus.** `POST {ap}-spclient.spotify.com/herodotus/spotify.resumption.v1.{ResumePointRevisionService,CurrentStateService}/…`, bare‑protobuf request (`application/x-protobuf`), bare‑protobuf response that is `Content-Type: application/grpc` **but NOT gRPC‑length‑framed** (parse the body directly; read `grpc-status: 0` from HTTP headers). It carries two payloads demuxed by top‑level `entity_uri`: **play‑history heads** (`spotify:list:play-history:v1`, `value.item_uri=#6`, no position — written when an item **becomes current**) and **episode resume‑points** (`spotify:episode:…`, `value.resume_point.position=#2` in **microseconds** — written on leaving an episode). It seeds the "Recently Played" rail and cross‑device episode resume. Tracks never carry a resume‑point. This plane is **entirely missing**.

---

## 2. End‑to‑end play sequence (numbered, with exact endpoints + firing point)

This is the native‑core order the projections must reproduce. Endpoints already implemented are marked **[exists]**; net‑new marked **[new]**.

1. **Session bootstrap (once, at login).** `GET apresolve.spotify.com/?type=accesspoint&type=spclient&type=dealer` (no auth, gzip JSON) → pick `gew4-spclient` / `gew4-dealer`. **[exists]**
2. **Tokens.** Bearer (login5/keymaster) + `client-token` (`clienttoken.spotify.com`, `client_token.proto`). Both mandatory on every plane, refreshed independently. **[exists]**
3. **Dealer WebSocket, then announce.** Connect `wss://gew4-dealer.spotify.com` → capture connection‑id from the `hm://pusher/v1/connections/` hello header → base64 into `X-Spotify-Connection-Id` on the first `PUT /connect-state/v1/devices/{device_id}`. **A PUT without a live dealer id is rejected.** **[exists]**
4. **Command origination → mint `command_id`.** A play/transport command is originated **either locally** (user `clickrow`/`playbtn`) **or remotely** (inbound dealer command down `hm://connect-state/v1/player/command`). At the instant the command is minted, mint a 16‑byte `command_id` and record it in `PlaybackIds` (see §7). This drives `RawCoreStream #63`, `CorePlaybackCommandCorrelation #2`, and `AudioResolve #4`. **[new wiring; local case is the dominant one for a desktop client]**
5. **Context resolution.** `GET /playlist/v2/{album|playlist|show}/{id}` → `SelectedListContent` (ordered URIs + `item_id`); then `POST /extended-metadata/v0/extended-metadata` TRACK_V4 (kind 10) to get `AudioFile.file_id` per URI. **[exists]**
6. **Load current track → mint `PlaybackIds`.** In `PlaybackController.LoadAndPlayCurrentAsync` (`:730`) mint `playback_id` (16B), `stream_id` (16B), set `media_id` = the 20‑byte audio gid, carry the already‑minted `command_id`. **[new]**
7. **Audio acquisition fan‑out (critical path = E2 ∥ E3).** In parallel: `GET heads-fa-tls13.spotifycdn.com/head/{fileId}` (128 KB plaintext fast‑start) ‖ `POST {ap}-spclient/playplay/v1/key/{fileId}` (obfuscated key) ‖ `GET {ap}-spclient/storage-resolve/v2/files/audio/interactive/1/{fileId}?product=0` (3 signed CDN URLs). Then ranged `GET {cdnurl}` over HTTP/3, AES‑128‑CTR decrypt, `OggS@0xA7` go/no‑go, decode. **[exists]**
8. **Playback‑start telemetry (gabo, buffered).** On `EvKind.Started`: emit `CorePlaybackCommandCorrelation`, `AudioResolve`, `AudioFileSelection`, `ContentIntegrity`, `AudioSessionEvent{open}`, `BoomboxPlaybackSession`, `HeadFileDownload`, `Download`, open segment 1. **[new]**
9. **PutState edge.** `PUT /connect-state/v1/devices/{device_id}` with `put_state_reason=PLAYER_STATE_CHANGED(4)`, fresh anchor, `is_active=1`. **[exists — harden fields]**
10. **Herodotus play‑history head (track becomes current).** On `Started`/`TrackChanged`, write a play‑history head (`entity_uri="spotify:list:play-history:v1"`, `value.item_uri=#6`). Coalesced into `BatchCreateResumePointRevisions`. **[new]**
11. **During playback.** One `RawCoreStreamSegment` per pause/resume/seek split; PutState re‑anchors on each edge; herodotus writes nothing mid‑track for tracks. **[new segments; PutState exists]**
12. **Approaching context end → autoplay.** `POST /context-resolve/v1/autoplay` (protobuf body, `Content-Type: application/x-www-form-urlencoded` decoy) → new URIs → TRACK_V4 again. Look‑ahead prefetch uses `storage-resolve/.../interactive_prefetch/1/{fileId}` + `net-fortune?...request_type=interactive_prefetch`. **[exists]**
13. **End of track.** `AudioSessionEvent{close, reason=endplay}` → final `RawCoreStreamSegment{is_last=1, reason_end="endplay"}` → **`RawCoreStream`** (the play record) → `ContentIntegrity` → `AudioRouteSegmentEnd`. Batch flushes on the first of 100 events / 125 kB / heartbeat. **[new]**
14. **Episode leave (episodes only).** On pause/skip/track‑change/close, write the episode **resume‑point** (`entity_uri=episode`, `value.resume_point.position` µs) **and** its play‑history head in the same `BatchCreateResumePointRevisions`. **[new]**

---

## 3. Per‑subsystem implementation sections

Each subsystem lists: exact HTTP call(s) + headers, the protobuf message and concrete fields (real names/numbers), the `.proto` files to add, and the C# seam it plugs into.

### 3.0 Transport prerequisites (blocking for every new plane)

Every plane depends on transport behaviors the current `ITransport` / `LiveDealerTransport` must guarantee.

- **Host split.** The play channel goes to `spclient.wg.spotify.com`, **not** the AP‑affinity `gew4-spclient`. `Channel.Spclient` maps to one host today. Add `Channel.SpclientWg` (or a per‑route absolute‑host override) so `POST /gabo-receiver-service/v3/events/` resolves to `spclient.wg.spotify.com`. Herodotus, connect‑state, storage‑resolve, playplay, extended‑metadata all stay on `{ap}-spclient` (`Channel.Spclient`).
- **Request‑body compression, two dialects.** (a) Standard gzip (`content-encoding: gzip`, `content-length` = gzipped size): gabo POST, extended‑metadata request. (b) Non‑standard `X-Transfer-Encoding: gzip` (`content-length` = gzipped size; generic libs won't auto‑apply): the connect‑state PUT body, gated by `Capabilities.supports_gzip_pushes=true`.
- **Response decompression.** Handle **brotli** (connect‑state Cluster has no content‑type, always `br`), **multi‑frame zstd** (extended‑metadata / playlist‑v2 / pathfinder — iterate frames until input exhausted; one‑shot truncates at ~64 KB), gzip, deflate. `Accept-Encoding: gzip, deflate, br, zstd` everywhere.
- **Header kit (mandatory pair, every spclient/gabo/herodotus call):** `Authorization: Bearer` **and** `client-token` (separately minted; missing/stale → 401 regardless of a valid Bearer), plus `App-Platform: Win32_ARM64`, `Spotify-App-Version: 129300667`, matching native `User-Agent: Spotify/129300667 Win32_ARM64/Windows 10 (10.0.26300; ARM)`. All flow from `SpotifyClientIdentity`; confirm the middleware stamps them on the new channels.
- **`X-Spotify-Connection-Id`** goes on **every connect‑state PUT** only — never on herodotus, gabo, or context/metadata.
- **Seam:** `Wavee/Backend/Transport.cs` (`ITransport.Request(Channel, route, body, ct, method, headers)` `:38`; `ITransport.Publish` `:45`). Add the `Channel` enum member; confirm `headers` carries `content-encoding`.

### 3.1 Session bootstrap + dealer WebSocket (see also §4)

**Status: exists — audit only.** Endpoints per §2 steps 1–3. Audit checklist (fidelity, no new code unless a gap):
- `DeviceInfo`: `client_id=65b708073fc0480ea92a077233ca87bd`, `spirc_version=3.2.6`, `device_software_version=1.2.93.667.g…`, `brand="spotify"`, `model="PC laptop"`, `device_type=1 COMPUTER`, `license` from the real entitlement, `volume` on the **0–65535** scale.
- `Capabilities.supports_gzip_pushes=true` plus `supports_transfer_command`, `supports_command_request`, `command_acks`, `supports_playlist_v2`, `supports_hifi{fully_supported,user_eligible,device_supported}`, `supported_audio_quality=5`, and the 15‑entry `supported_types` list. Preserve unknown newer capability bools (`#33–#38`) round‑trip.
- **Seam:** `LiveSessionHost.cs`, `LiveDealerTransport.cs`, `Connect.cs`, `ConnectStateBuilder.cs`. Confirm bootstrap order = apresolve → login5 → client‑token → dealer connect → connection‑id → announce PUT.

### 3.2 Context resolution → concrete playable track list

**Status: exists for tracks — verify + thread `decision_id` into queue metadata (not into RawCoreStream).**

- **A** `GET /playlist/v2/{type}/{id}` → `SelectedListContent` (`playlist4_external.proto`, zstd multi‑frame). Read `#1 revision` (= `mc-etag` → `/diff?revision=`), `#2 length`, `#5 contents.items[].uri` + `ItemAttributes.item_id (#12)` (stable dedupe/reorder key — use it, not array index). Headers: `x-accept-list-items: audio-track, audio-episode, video-episode, audiobook`, `spotify-apply-lenses: auto`, `spotify-playlist-sync-reason: CAwQAQ==`, `Cache-Control: no-store`.
- **B** `POST /extended-metadata/v0/extended-metadata` → `BatchedEntityRequest` (**gzip request body**, `client-feature-id: track_metadata_loader`, `Content-Type: application/protobuf`). Header `{country, catalogue="premium", task_id=16 random bytes}`; one `EntityRequest{#1 entity_uri, #2 query=[ExtensionQuery{#1 extension_kind=10 TRACK_V4, #2 etag}]}` per URI, ~40/batch. Response `BatchedExtensionResponse`: **dispatch on `Any.type_url`**, not requested kind. `type.googleapis.com/spotify.metadata.Track` → `metadata.proto Track`: pull `#1 gid`, `#12 file[]{file_id(20B), format}` (the reason B is mandatory), `#7 duration` (**zigzag sint32**), `#11 restriction`/`#19 availability`. Two‑way etag caching: response `EntityExtensionDataHeader.etag (#2)` → next `ExtensionQuery.etag`; honor `cache_ttl_in_seconds`.
- **D** Autoplay near context end: `POST /context-resolve/v1/autoplay`, body = `AutoplayContextRequest{#1 context_uri, #2[] recent_track_uri}` serialized as protobuf but sent `Content-Type: application/x-www-form-urlencoded` (decoy — don't URL‑encode). Response JSON `pages[].tracks[]{uri, uid, metadata.decision_id}`. **Carry `decision_id` (`ssp~…`, identical across a page) into the queue/context metadata for recsys/UI attribution ONLY — do NOT echo it into `RawCoreStream #48` (see §7 anti‑fraud; desktop sends #48 empty).**
- **Seam:** `LiveContextResolver.cs` (autoplay `:95/:124`, radio‑apollo `:152`), `ContextResolver.cs:76`. Confirm the multi‑frame zstd loop is applied.
- **Decoration to SKIP** (see §8): all `pathfinder/v2/query`, `socialgraph/is-following`, `popcount`, `playlist-permission/*` (the N+1 storm), `playlist-publish/subscription`, and the JSON gabo Ubi/Km/SemanticMetric stream. None gate audio or play counts.

### 3.3 Audio acquisition + decrypt

**Status: real — two proto tolerances + constant fixes.** Full chain per §2 step 7 already in `LiveTrackResolver.ResolveBodyAsync (:135)`, `SpotifyAudioStream.cs`, `SpotifyAesCtr.cs`, `PlayPlayLicenseClient.cs`, `PlayPlayKnownPacks`.

- **E2 `POST {ap}-spclient/playplay/v1/key/{fileId}`** → `PlayPlayLicenseRequest{#1 version=5, #2 token=<16B, per‑(build AND arch), DLL‑hash‑pinned>, #4 interactivity=1 INTERACTIVE, #5 content_type=1 AUDIO_TRACK (2 AUDIO_EPISODE / 3 AUDIO_AD), #6 timestamp=<Unix SECONDS>}`. `Content-Type: application/x-www-form-urlencoded` is a decoy (body is protobuf). Response `PlayPlayLicenseResponse{#1 obfuscated_key(16B), #2 aux(4B, undocumented — tolerate)}`. Deobfuscate the 16‑byte key through the native PlayPlay VM pinned by **SHA‑256 of the exact `Spotify.dll`** → `[0,16)` = AES‑128 key.
- **E3 storage‑resolve** → `StorageResolveResponse{#1 result(0 CDN/1 STORAGE/3 RESTRICTED — handle non‑zero before deref), #2 cdnurl[×3] fa→cf→ak signed/expiring, #4 fileid, #5 ttl_seconds(=86400, undocumented — tolerate)}`. **Prefetch is a distinct request_type:** look‑ahead uses `.../interactive_prefetch/1/{fileId}` + `net-fortune?...request_type=interactive_prefetch`, and **prefetch skips heads‑fa and skips supports_drm**. Do not reuse a prefetch resolve as the interactive one.
- **E4 CDN body** → `0xA7` header + AES‑128‑CTR ciphertext. Decrypt with `PublicIv = 72e067fb ddcbcf77 ebe8bc64 3f630d93`, big‑endian whole‑file counter from offset 0. Assert `OggS` at `0xA7` (wrong PlayPlay pin ⇒ wrong key ⇒ fails).
- **E1 heads‑fa** `GET heads-fa-tls13.spotifycdn.com/head/{fileId}` → 131072 B plaintext Ogg fast‑start (**`OggS` at offset 0, no `0xA7`, unencrypted** — different layout; don't feed it to the encrypted‑body parser). Carries `x-playback-id`.
- **Proto fixes** (`Wavee/SpotifyLive/Protos/`): add `optional bytes aux = 2;` to `playplay.proto`; add `optional int64 ttl_seconds = 5;` to `storage-resolve.proto`. Timestamp is **seconds** (ms → ~90 B body → server rejects; keep clock synced via the wired `GET /melody/v1/time`).
- **Seam:** `LiveTrackResolver.cs`, `SpotifyAudioStream.cs`, `PlayPlayLicenseClient.cs`.

### 3.4 State reporting (PutState) — see §6.
### 3.5 Play registration (gabo RawCoreStream) — see §7.
### 3.6 Resume points (herodotus) — see §8.
### 3.7 Podcast/episode + ads — see §8.

---

## 4. Session bootstrap + dealer WebSocket (connection‑id linkage to PutState)

**Status: exists — documented for completeness; verify only.**

1. `GET apresolve.spotify.com/?type=accesspoint&type=spclient&type=dealer` (no auth) → `gew4-spclient` / `gew4-dealer`. Fired once at login before anything else.
2. Bearer (login5/keymaster) + `client-token` (`clienttoken.spotify.com`, `client_token.proto`). Both refresh independently.
3. **Dealer WebSocket first, PUT second.** `wss://gew4-dealer.spotify.com` mints the connection id, delivered on the `hm://pusher/v1/connections/` hello frame header. Its base64 value decodes to `{connection GUID}+dealer+tcp://{dealer AP}:{port}+{64‑hex token}`, e.g. `e2db9300-…+dealer+tcp://0ab157e9.ip.gew4.spotify.net:5700+5185BF22…14B`. This id **must** be echoed as `X-Spotify-Connection-Id` on every connect‑state PUT so the backend correlates the HTTP state report with the live push channel and can route `ClusterUpdate` back. A PUT without it is rejected / the device is invisible.
4. Clock sync: `GET /melody/v1/time` (wired via `SpotifyServerClock`, `LiveConnect.cs:44`) — reuse to correct the PlayPlay timestamp (Unix seconds) and gabo `context_time`.
- **Seam:** ordering enforced by `LiveConnect` / `Connect.cs` (`ConnectService`) / `LiveDealerTransport.cs`. Confirm `LiveSessionHost` bootstrap order = apresolve → login5 → client‑token → dealer connect → connection‑id → announce PUT. No structural change.

---

## 5. Audio path: storage‑resolve → PlayPlay key unwrap → CDN → AES‑CTR → decode

The full chain is already real; this section pins the decrypt specifics and the two branches (music vs episode share the same code once a `file_id`/gid is produced).

1. From `Track.file[]` (metadata §3.2‑B) pick a `format` your decoder supports (ladder 96/160/320; format choice = which `file_id` you resolve). The 20‑byte `file_id` feeds both E2 and E3.
2. **E2 key:** `PlayPlayLicenseRequest{version=5, token, interactivity=INTERACTIVE, content_type, timestamp=<seconds>}` → `PlayPlayLicenseResponse{obfuscated_key(16B), aux(4B)}`. The 16 bytes are **not** an AES key until run through the version‑pinned native VM (`PlayPlayKnownPacks`, keyed by exact `Spotify.dll` SHA‑256). Output `[0,16)` = AES‑128 key.
3. **E3 URLs:** `StorageResolveResponse` → 3 signed CDN URLs (fa→cf→ak), 24 h expiry; re‑resolve on 403/expiry; handle `result != CDN`.
4. **E4 decrypt:** ranged HTTP/3 `GET {cdnurl}` → `0xA7` Spotify header + AES‑128‑CTR ciphertext. `SpotifyAesCtr.DecryptInPlace` with `PublicIv = 72e067fb ddcbcf77 ebe8bc64 3f630d93`, big‑endian block counter over the whole file from offset 0. **Go/no‑go:** decrypt `[0,0xC0)` and assert `OggS` at offset `0xA7`.
5. **Decode:** Vorbis (vendored NVorbis) / FLAC → EQ/limiter/crossfade → WASAPI, in the out‑of‑process supervised audio host.
6. **E5 net‑fortune** (non‑blocking): send measured `bandwidth` + desired `bitrate=160000`; treat response field #2 (~1.4 Mbps allowance) as the format cap.
- **Episode branch (§8):** if the episode yields an external `http(s)` URL (`PlaybackTrait #1`), stream it directly — **no storage‑resolve, no PlayPlay, no DRM** — needs an MP3 decoder path in `AudioPlayEngine`. If it yields `AUDIO_FILES` gids, this exact chain runs with `content_type=AUDIO_EPISODE(2)`.

---

## 6. State reporting (PutState): message, triggers, cadence, is_active/put_state_reason

**Status: exists — harden to field parity. Two concrete divergences to fix: remove `#11`, unify `playback_id`.**

**Endpoint:** `PUT {ap}-spclient.spotify.com/connect-state/v1/devices/{device_id}`, body `PutStateRequest` **gzipped via `X-Transfer-Encoding: gzip`** (`Content-Length` = gzipped size), `Content-Type: application/protobuf`, `X-Spotify-Connection-Id: <base64>`. Response = `connectstate.Cluster`, **brotli, no `Content-Type`** — always brotli‑decode, never switch on CT. This is `ITransport.Publish`.

**`PutStateRequest` top‑level (`connect.proto`) — fields to send:** `#2 device`, `#3 member_type=2 CONNECT_STATE`, `#4 is_active`, `#5 put_state_reason`, `#7 last_command_sent_by_device_id`, `#8 last_command_message_id` (**stable within session**), `#9 started_playing_at` (**stable within session**), `#12 client_side_timestamp` (fresh per PUT).
**Fields NEVER sent by this client version — must NOT be emitted:** `#1 callback_url`, `#6 message_id`, **`#11 has_been_playing_for_ms`**, `#13 only_write_player_state`. **[FIX — MAJOR]** The existing `DeviceStatePublisher` currently emits `#11 has_been_playing_for_ms` (gap map §A); this is a concrete wire divergence from 1.2.93 desktop. Stop emitting it. Position is conveyed only via the `PlayerState` anchor.

**`PlayerState` (`player.proto`) — fields to get right:** `#1 timestamp` (anchor) + `#10 position_as_of_timestamp` (**omit when 0**, proto3), `#12 is_playing`/`#13 is_paused`, `#9 playback_speed=1.0`; `#2 context_uri`/`#3 context_url`; `#5 play_origin{feature_identifier, feature_version, referrer_identifier}`; `#7 track (ProvidedTrack{uri, uid, metadata{title, album_title, image_*_url, …}, provider="context"})`; **`#8 playback_id` — must equal the gabo `playback_id`** (unify via `PlaybackIds`, §7); `#11 duration`; `#16 options`; `#17 restrictions` (`disallow_*` — drives greyed transport buttons); `#20 next_tracks`/`#19 prev_tracks`; `#21 context_metadata`; `#23 session_id`; `#24 queue_revision`; `#32 playback_quality{bitrate_level, target_bitrate_level, hifi_status}`. Preserve unknown newer fields round‑trip (`PlayerState #33/#35/#38`, `Restrictions #24–31`, `ContextPlayerOptions` map entries, `Cluster #13`).

**`PlayOrigin.feature_version` [FIX — parity].** Source from `SpotifyClientIdentity.XpuiSnapshotVersion`, but the codebase constant is stale (`2026-05-06_…`). The captured wire value is **`xpui-snapshot_2026-07-01_1782890476915_7b5cc0c`**. Update the pinned constant to the captured value for byte‑parity (and keep it a single constant consistent with the `Spotify-App-Version` build story).

**Position model (no polling):** store `(timestamp, position_as_of_timestamp, is_playing, playback_speed)`; observers extrapolate `pos = position_as_of_timestamp + (server_now − timestamp)·speed` when playing. Re‑anchor **only on a state edge**. Correct skew against Cluster `#9 server_timestamp_ms`.

**Triggers / `put_state_reason` (edge‑driven, never polled):** first announce after dealer connect = `SPIRC_HELLO(1)` (or `NEW_CONNECTION(9)` on reconnect); play/pause/seek/track/context/queue change = `PLAYER_STATE_CHANGED(4)` (the only reason seen during playback in the capture — all 22 PUTs); volume = `VOLUME_CHANGED(5)`; hand‑off = `BECAME_INACTIVE(7)`; expect `PUT_STATE_RATE_LIMITED(12)` if you coalesce after backoff. Map from `EvKind`: `Started/Resumed/Paused/Seeked/TrackChanged/QueueChanged/OptionsChanged → 4`, `VolumeChanged → 5`, `BecameInactive → 7`.

**Ack fields:** when a command arrives down the dealer, copy sender device id → `#7`, message_id → `#8` into the next PUT; keep both stable within the session (regenerating looks fraudulent / breaks command‑ack correlation).

**Cluster response is authoritative:** adopt `active_device_id`, honor `needs_state_updates`, and if `need_full_player_state` is set re‑push a complete `PlayerState`.

- **Seam:** `DeviceStatePublisher.cs` (single writer, fed from `_extra[]` in `LiveConnect.cs:66`), `ConnectStateBuilder.cs`, `OutboundEnvelope.cs`. Gaps to close: **remove `#11`**, **unify `playback_id` with `PlaybackIds`**, ensure gzip+`X-Transfer-Encoding` and brotli response decode, guarantee `Restrictions`/`next_tracks`/`context_metadata`, `volume` on 0–65535.

---

### 6.1 Connect-state PlayerState fidelity & the queue-row model

This subsection is the field-level expansion of M1: the cluster captures (artist play, generated mix, autoplay/radio, music video, podcast) show `next_tracks`/`prev_tracks` are **not a `Track[]`** — they are a heterogeneous **row list** with control rows, iteration markers, and per-row identity that must survive round-trip. Getting this wrong is the single most common Connect-state parity bug (flattening rows into `Track[]` loses exactly the native shape). All of this is Plane A (state); none of it touches gabo/herodotus/audio.

**Typed queue rows (new model).** A row is one of:
- **`Playable`** — `spotify:track:*`, `spotify:episode:*`, `spotify:local:*`. The only kind the audio cursor advances onto.
- **`Delimiter`** — `spotify:delimiter` (`uid` like `delimiter0`/`delimiter-1`). Hidden boundary between iterations/pages. Carries `metadata.hidden="true"`, `actions.advancing_past_track="pause"`, `actions.skipping_next_past_track="pause"`, and `removed:["context/delimiter"]`. **Never playable.**
- **`PageMarker`** — `spotify:meta:page:N`. Hidden in-band marker that a page boundary exists (**not** end-of-context). Parse `N`. **Never playable.**

Playback skips non-`Playable` rows; **PutState publishing must emit every retained row verbatim**, in order, with original `uri`/`uid`/`provider`/`metadata`. Concrete rule: hydrate/audio-resolve only `Playable` rows — a `Delimiter`/`PageMarker` must never reach track-metadata or storage-resolve.

**Continuation is `next_page_url`, not the `PageMarker` URI. [FIX — MAJOR]** The autoplay / radio-apollo response is `{pages:[{tracks:[{uri,uid,metadata.decision_id}], next_page_url}]}`. `next_page_url` is a mercury cursor — `hm://radio-apollo/v3|v5/tracks/spotify:station:...?salt=…&autoplay=true&count=50&isVideo=false&prev_tracks=<csv>&pageNum=N&minimal=true`. **Treat it opaque and replay it exactly** (it encodes the prev-tracks window and page number); do not rebuild it. `spotify:meta:page:1` in the cluster is the marker that this boundary is loaded; the actual fetch target is `next_page_url`. Wire into the existing `LiveContextResolver.LoadMoreAsync` seam; when the cursor/lookahead reaches the page boundary, fetch, append rows as `provider="autoplay"`, then update continuation state. Add dedupe bookkeeping keyed by `(context, session, provider, pageNum)` so repeated markers don't loop.

**Per-context-kind resolution (context_uri is NOT the seed URI).**
- **Artist play** resolves to `spotify:list:popular-release-segments-main-roles:artist_<id>` via `GET /playlist/v2/list/popular-release-segments-main-roles/artist_<id>` — a **zstd `SelectedListContent`** (`playlist4_external.proto`; ~50 track URIs, sparse rows, `item.attributes.item_id` **hex-lowercase → the row `uid`**). But `play_origin.feature_identifier` stays `"artist"` (referrer `"search"`). `context_metadata` carries `format_list_type="popular-release-segments-main-roles"`, `reporting.uri="spotify:artist:<id>"`, `total_number_of_tracks`, `play_count`. **[FIX]** Decode with a **streaming zstd** reader, not `Decompressor.Unwrap` — some list frames carry no content-size header and `Unwrap` returns 0 bytes.
- **Autoplay station flip (music only).** When a music seed context exhausts, `POST /context-resolve/v1/autoplay` (body: seed context URI + prev-track window, `Content-Type: application/x-www-form-urlencoded`) → the queue **context flips** to `spotify:station:artist:<id>` / `spotify:station:playlist:<id>`, and appended rows get `provider="autoplay"` + `metadata.autoplay.is_autoplay="true"` with **empty per-row `context_uri`/`entity_uri`** (native does not stamp the station onto each row). The top-level `player_state.context_uri` may lag the original context while rows already advertise the station — **do not relabel the whole context just because autoplay rows do.**
- **Podcast/episode is its own context and does NOT station-flip.** A `spotify:episode:*` context keeps its original `context_uri`; autoplay up-next comes from `/context-resolve/v1/autopodcast` and falls back to music `/autoplay` when the podcast queue ends. (Confirms the plan's "bare item is its own context" for episodes — but the station flip is a music-seed behavior, not universal.)

**`iteration` = the repeat/wrap encoding.** Per-row `metadata.iteration`: **`-1`** = previous pass, **`0`** = current pass, **`1`** = wrapped/repeated. On repeat-context, native re-lists the whole context after a delimiter with those rows marked `hidden="true"` and `iteration="1"`. So `iteration` must be preserved from the wire, never synthesized as `"0"`.

**Per-row identity to preserve (don't overwrite server rows; synthesize only for locally-minted rows).** Generate **once per context load**, stamped on every row: `interaction_id`, `page_instance_id` (dashed UUIDs — distinct from the compact `playback_id`). Preserve from the wire: `decision_id` (`ssp~…`), `entity_uri`, `context_uri`, `view_index` (context order index as a string — separate from queue index), `added_by_username`, `highlight_id`, `video_rendering_enabled`, `is_explicit`, `is_19_plus`, `track_player`. Row `uid` is the server/context uid (or `item_id` for list contexts) — **never mint it locally** for a server row.

**decision_id is NOT the RawCoreStream `#48` — keep both.** The `ssp~…` `decision_id` on cluster/autoplay rows is real recsys attribution and belongs in row metadata. It is a **different field** from the gabo `RawCoreStream #48`, which stays empty (§7.12 point 10). Preserving the queue-row `decision_id` and sending an empty `#48` are simultaneously correct — do not reconcile one to the other.

**Video rows (meta-support only; no video player yet).** Preserve `track_player="video"`, `media.type` (`"video"` for music video, `"mixed"` for video podcast), `media.manifest_id`, `is_backgroundable`, and **`save_track.uri`** (the canonical audio track a heart/library action must save — prefer it when present). **Trap:** `options.modes.video_persistence="VIDEO"` persists even on plain-audio tracks, so **derive media type from `track.metadata.track_player`, never from that mode.** Video/mixed rows may omit `entity_uri`/`view_index`/`iteration` — don't force audio-style fields onto them. `playback_speed=0.0` on a music-video track means "speed unsupported," not paused — derive playing state from `is_playing`/`is_paused`.

**Additional session fields to populate** (proto already supports several via `context_restrictions`/`PlayerState`): `queue_revision` (bump on queue-shape change — remote clients diff on it), `session_id` (stable per session/context), `session_command_id`, `playback_quality` (`high`/`cached_file` for local desktop, `very_high`/`best_matching` for web — set per the real selected file), `signals`, and the richer `restrictions`/`disallow_setting_modes`/`disallow_signals` maps for radio/autoplay (e.g. `disallow_toggling_shuffle_reasons:["radio"]`). Fields with no proto slot yet (`audio_stream`, `state_type`, `options.modes` map, newer `disallow_*`) — add or preserve-unknown; do not invent field numbers.

- **Seam:** widen `QueuedRef`/`QueuedTrack`/`QueueEntry`/`RemoteTrack` to carry `provider` + `metadata` + row-kind (in progress per the working diff); `ContextJson.cs` parser to retain provider/metadata/control rows; `LiveContextResolver.cs` for `playlist/v2/list` + `next_page_url` continuation; `ConnectStateBuilder.cs`/`DeviceStatePublisher.cs` to emit all rows with preserved metadata and synthesize context-scoped `interaction_id`/`page_instance_id` once; `PlaybackController` queue cursor to skip non-`Playable` rows while keeping them in prev/next. Reference "done-right" queue/row handling: `C:\WAVEE\WaveeMusic\src\Wavee\Audio` + `Connect`.

---

## 7. Play registration (RawCoreStream plane): events, batching, ID lifecycle, anti‑fraud checklist

**Status: STUB → build in full (the main work).** This plane counts plays. `GaboTelemetry.ReportPlay (:19)` only logs; `TelemetryProjection` only fires on `Started/TrackChanged` (`Telemetry.cs:39`) with no end‑of‑track, segments, or reasons.

### 7.0 Threading contract (single‑writer / AOT fit)

Wavee's playback runs on a single owner thread (the `PlaybackController` event loop) that `Emit`s a `PlaybackEvent` stream (`Playback.cs:266`, `EvKind` `:265`), fanned to passive `IPlaybackProjection`s (`PlaybackController.cs:900`, `Emit → _projection.OnEvent + _extra[i].OnEvent`). Every new reporting plane obeys this:
- **No new threads, no locks on the hot path.** Projections receive events **synchronously** on the controller thread, capture a **POD snapshot**, and hand it to an **async, bounded, drain‑on‑timer mailbox sender** (a `Channel<T>` per plane). The controller thread never awaits network I/O.
- **All wire I/O via `ITransport.Request`.** No plane opens its own `HttpClient`.
- **AOT‑clean protobuf** under `Wavee/SpotifyLive/Protos/` via the existing generator. No reflection serializers.
- **Zero‑alloc bias:** pre‑size POD event records, reuse gzip buffers; the batch encoder runs off‑thread so frame phases 6–13 stay alloc‑free.

### 7.1 The shared ID model (`PlaybackIds` POD)

Anti‑fraud depends on these being distinct and consistently joined. Mint a single `PlaybackIds` POD and carry it on the `Track`/event so **PutState and gabo agree on `playback_id`** (today `DeviceStatePublisher` mints its own — unify).

| ID | Size | Minted when | Joins |
|---|---|---|---|
| `playback_id` | 16 B | per track load (`LoadAndPlayCurrentAsync :730`) | RawCoreStream #1, every segment #1, ContentIntegrity #1, AudioSession #4, Boombox #1, Download/HeadFileDownload #2, **PutState PlayerState #8** |
| `command_id` | 16 B → hex32 | **when a play/transport command is originated — local user action OR remote dealer command** | RawCoreStream #63, CorePlaybackCommandCorrelation #2, **AudioResolve #4** |
| `media_id` | 20 B | = the audio gid from metadata | RawCoreStream #4, Download #1, HeadFileDownload #1 |
| `stream_id` | 16 B → hex32 | per stream | RawCoreStream #53, segment #22 |
| `session_id` | base62 | per playback session | PutState PlayerState #23 |
| `sequence_id` | 20 B | per gabo **batch** (constant across the POST) | every EventEnvelope #4 in that POST |
| `sequence_number` | int64 | global monotonic, **not reset per track, persisted across restarts** | EventEnvelope #5 |

**[FIX — MAJOR, both critics]** `command_id` is minted on **any** command origination. For a desktop client the dominant case is a **local** click (`reason_start="playbtn"/"clickrow"`, `source_start="search"/"artist"/"playlist"`), not an inbound dealer command. Mint it in the controller at the moment the play/transport command is created (§2 step 4), regardless of origin, and carry it forward so `RawCoreStream #63`, `CorePlaybackCommandCorrelation #2`, and `AudioResolve #4` all agree. Do **not** source it only from the dealer, or local plays ship with an empty `#63` and fail the three‑way join (a non‑genuine signal).

### 7.2 Protos to add (`Wavee/SpotifyLive/Protos/`)

- **`event_sender_envelope.proto`** — `PublishEventsRequest{ repeated EventEnvelope event = 1; bool suppress_persist = 2; }`, `EventEnvelope{ string event_name = 2; repeated EventFragment event_fragment = 3; bytes sequence_id = 4; int64 sequence_number = 5; }`, `EventFragment{ string name = 1; bytes data = 2; }`, `PublishEventsResponse{ repeated EventError = … }` (error path only), `EventError{ index, transient, reason }`, and the 8 context messages (`ClientId`, `InstallationId`, `ApplicationDesktop`, `DeviceDesktop`, `Time`, `MonotonicClock`, `Sdk`, `ClientContextId`).
- **`event_sender_events.proto`** — `RawCoreStream` (§7.4), `RawCoreStreamSegment` (§7.5), `ContentIntegrity`, and the M4 siblings (§7.6). Port field numbers verbatim, including the schema trap **#38 = play_type, #44 = core_bundle** (not the reverse), and include the proto‑absent wire fields.

### 7.3 Envelope + 8 context fragments (`GaboEnvelopeFactory` / `GaboContext`, new)

Each `EventEnvelope`: `event_fragment[0] = {name:"message", data:<serialized event>}`, then the **8 context fragments attached verbatim** (identical every envelope):

| fragment | message | value |
|---|---|---|
| `context_client_id` | `ClientId` | `#1 value = 65b708073fc0480ea92a077233ca87bd` |
| `context_installation_id` | `InstallationId` | `#1 value =` per‑install GUID (**persist once**) |
| `context_application_desktop` | `ApplicationDesktop` | `#1 version_string="1.2.93.667"`, `#2 version_code=129300667`, `#3 session_id=<app session>` |
| `context_device_desktop` | `DeviceDesktop` | `#1 platform_type="windows"`, `#2 device_manufacturer`, `#3 device_model`, **`#4 device_id = Windows machine SID S-1-5-21-…`**, `#5 os_version="10.0.26300"` |
| `context_time` | `Time` | `#1 value =` per‑event Unix ms |
| `context_monotonic_clock` | `MonotonicClock` | `#1 id=9`, `#2 value=<ms since boot>` |
| `context_sdk` | `Sdk` | `#1 version_name="0.9.4-rl-essopt-loginsend-onlinesend-bcdsend-heartbeat300.0s/30.0s-modern-payload125kB-batch100"`, `#2 type="cpp"` |
| `context_client_context_id` | `ClientContextId` | empty |

The exact `context_sdk.version_name` string and `type="cpp"` are anti‑fraud fingerprints — copy verbatim (the JS SDK strings `4.0.3`/`javascript` mark the non‑counting channel). Build from `SpotifyClientIdentity` + a new `GaboContext` (SID via Win32; installation_id persisted in app‑data).

### 7.4 Endpoint, encoding, headers

`POST spclient.wg.spotify.com/gabo-receiver-service/v3/events/` (**trailing slash, `wg` host**), `Content-Type: application/x-protobuf`, **`content-encoding: gzip`** (standard gzip, `content-length` = gzipped size). Success = **HTTP 200, empty body** — only parse the body on error. `Authorization: Bearer` + `client-token` both mandatory. Route via `transport.Request(Channel.SpclientWg, "/gabo-receiver-service/v3/events/", gzip(bytes), method:"POST", headers:{content-encoding:gzip})`.

### 7.5 `RawCoreStream` — the play‑count event (fields to populate)

Minimum viable to register a play, plus parity fields (byte‑exact map from play‑registration §4):

`#1 playback_id`(16B), `#4 media_id`(20B gid), `#5 media_type="audio"`, `#9 source_start`, `#10 reason_start` (`clickrow`/`playbtn`/`trackdone`/`fwdbtn`/`backbtn`), `#11 source_end`, `#12 reason_end` (`endplay`/`trackdone`/`fwdbtn`/`backbtn`/`remote`), `#13 playback_start_time`(ms), `#14 ms_played` + `#15 ms_played_nominal`, **`#22 audio_format` (derived — see below)**, `#23 play_context` (context uri), `#24 content_uri` (track uri), `#28 provider="context"`, `#29 referrer`, `#37 core_version=6004800000000000`, `#38 play_type="full"`, **`#39 is_assumed_premium` (derived — see below)**, `#44 core_bundle="local"`, `#45 playback_stack="boombox"`, **`#48 decision_id=""` and `#49 play_context_decision_id=""`** (see anti‑fraud), `#63 command_id`(hex32), `#69 playback_stack_secondary="boombox"`, `#71 orchestration_stack="context-player"`.

**Wire fields the proto omits but desktop sends (emit for byte‑parity):** `#2 parent_playback_id` = all‑zero 16B, `#53 stream_id` (desktop DOES fill it), `#56` = end/next Unix‑ms timestamp, `#59 next_content_uri_base62` (22‑char base62 of the next queued track), and the device trio **`#78="spotify"`, `#79="PC laptop"`, `#80="computer"`**.

**[FIX — MINOR, derive not hardcode]:**
- `#39 is_assumed_premium` = derive from the real entitlement (`DeviceInfo.license == "premium"` → 1, else 0). Hardcoding premium on a free session is a lie and would also require the ads path.
- `#22 audio_format` = derive from the **actually‑selected** `AudioFile.format` (the 96/160/320 ladder result), e.g. `OGG_VORBIS_160 → "Vorbis 160 kbps"`, `OGG_VORBIS_320 → "Vorbis 320 kbps"`. Reporting "160 kbps" when 320 was fetched is inconsistent with the stream.

### 7.6 `RawCoreStreamSegment` — one per pause/resume/seek split

`#1 playback_id`, `#2 start_position`/`#3 end_position`/`#4 ms_played`(ms), `#5 reason_start` (`pause`)/`#6 reason_end` (`end-while-paused`/`endplay`), `#7 playback_speed=1.0`, `#8/#9` start/end Unix‑ms, `#10 is_seek`/`#11 is_pause`, `#12 sequence_id` (UInt64.Max sentinel or counter), `#13 media_type`, `#15 content_uri`, `#16/#17` monotonic ms, `#18 is_last` (→1 on final, with `reason_end="endplay"`), `#19 provider` (`context`/`autoplay`), `#20 playback_stack`, `#22 stream_id`, `#23 page_instance_id`, `#24 interaction_id`, `#25 play_context`, `#28 sequence_id_internal` (increments 1,2,…), plus device trio **`#79/#80/#81`** = `"spotify"/"PC laptop"/"computer"`.

### 7.7 `ContentIntegrity` — the anti‑rip gate (mandatory, one clean per track)

`#1 playback_id`, `#2 ripping_categories=0`, `#3 is_ripping_faster_than_rt=0`. **The server drops any play whose `ContentIntegrity.playback_id` is missing or flagged.** This is what makes the play count.

### 7.8 M4 fidelity siblings (co‑batched; optional for counting, on for anti‑fraud parity)

Enumerate all co‑batched siblings the desktop sends every play (fidelity backlog — required for the byte‑diff gate, not for counting). Add to `event_sender_events.proto`:
- `AudioSessionEvent` (open: `#1 event="open"`, `#4 playback_id`, `#7 seek_position=0`, `#8 paused=0`, `#9 speed=1`, `#10/11/13=UInt64.Max`; close: `#1 event="close"`, `#4 playback_id`, `#5 reason="endplay"`, `#6 feature_identifier="unknown"`).
- `CorePlaybackCommandCorrelation` (`#1 playback_id`, `#2 command_id`).
- `AudioResolve` **[FIX — add; was omitted]** (`#1 playback_id`, `#2 resolve_ms`, `#3 content_uri`, **`#4 command_id` must equal RawCoreStream #63**).
- `AudioFileSelection` **[FIX — add]** (`#1 playback_id`, `#2 reason="best matching bitrate"` / `"external url"`, `#3 selected_bitrate`, `#5 quality`, `#6 target_bitrate`).
- `BoomboxPlaybackSession` (audio) / `BetamaxPlaybackSession` (video) — setup timings (`#1 playback_id`, `#2 audio_key_ms`, `#3 resolve_ms`, `#4 total_setup_ms`, `#5 buffering_ms`, `#12 duration_ms`, `#13 preset`, `#17 first_play`).
- `Download` (`#1 file_id`, `#2 playback_id`, `#7 file_size`, `#10 bytes_downloaded`, `#11 realm="usic"`, `#30 cdn_uri_scheme="https"`, `#31 cdn_domain="audio-fa.scdn.co"`; unmeasured = `UInt64.Max`/`-1.0`).
- `HeadFileDownload` (`#1 file_id`, `#2 playback_id`, `#4 cdn_domain="heads-fa-tls13.spotifycdn.com"`, `#5 head_file_size=131072`, `#11 http_result=200`, `#16 request_type="interactive"`).
- `AudioRouteSegmentEnd`, `AudioDriverInfo`/`WasapiAudioDriverInfo`, `AdOpportunityEvent` (premium → `"PASS"`/`ad_slot_disabled` but still sent), `CoreShuffleStateEvent`, `CoreAutoplayLoadingResults`, `AdvanceStuck`, `CacheReport`/`CacheRealmReport`, `ImageDownload`, `ResumptionRevisionUpdate`/`ResumptionProgressUpdate` (podcasts) — enumerated as fidelity backlog; ship after the RawCoreStream family. **Reproduce sentinels** (`UInt64.MaxValue`="not measured", `-1.0`="unmeasured float", all‑zero `parent_playback_id`="none") — never send 0.

### 7.9 The feeder projection (`RawCoreStreamProjection`, new — the state machine)

`TelemetryProjection` is too thin. Add a dedicated projection to `_extra[]` (built in `LiveConnect.cs:64‑68`, alongside `telemetry` + `_publisher`). Extend `EvKind`/`PlaybackEvent` (`Playback.cs:265‑266`) to carry segment boundaries + real transport position (via `EmitState`, `PlaybackController.cs:909`) and reason codes (the controller knows whether an advance was `trackdone` vs `fwdbtn`):
- `Started` → mint/adopt `PlaybackIds`, open segment 1, emit `CorePlaybackCommandCorrelation`, `AudioResolve`, `AudioFileSelection`, `ContentIntegrity`, `AudioSessionEvent{open}`, `Boombox`, `HeadFileDownload`, `Download`.
- `Paused/Resumed/Seeked` → close current segment (`is_pause`/`is_seek`), open next; accumulate `ms_played` from **real transport position** (never from track duration).
- `Ended/TrackChanged` → close final segment `is_last=1 reason_end="endplay"`, emit `RawCoreStream` with summed `ms_played`, `AudioSessionEvent{close}`, `AudioRouteSegmentEnd`.
Each emitted event → the batch encoder (§7.10). Projection runs on the controller thread (POD only); encoder/sender off‑thread.

### 7.10 Batch + flush policy (`GaboBatcher`, new)

Buffer envelopes; one 20‑byte `sequence_id` per batch + a global monotonic `sequence_number` per event (persisted across restarts). Flush on the first of: **100 events** (`batch100`), **125 kB uncompressed** (`payload125kB`), or **300 s / 30 s heartbeat**. A single POST mixes multiple tracks/event types in `context_time` order; late events for an already‑reported play are normal.

### 7.11 401 / retry on the report planes **[FIX — MAJOR]**

Because a successful gabo POST is a 200 with empty body, a **401 is the only failure signal**, and a dropped batch = uncounted plays. On 401 from `/gabo-receiver-service/v3/events/` (or herodotus): **refresh `client-token` (and Bearer if needed), re‑stamp headers, and re‑POST the buffered batch before discarding it** — do not treat 401 as fire‑and‑forget. On an error body, key retry on `EventError.transient`. Both `Bearer` and `client-token` are mandatory and refresh independently.

### 7.12 Anti‑fraud / consistency checklist (fields that must be genuine so the play COUNTS)

1. **`ContentIntegrity` emitted, clean** (`ripping_categories=0`, `is_ripping_faster_than_rt=0`) per track — missing/flagged ⇒ play dropped from Recently Played. The single most important gate.
2. **`ms_played` reflects real wall‑clock playback** — accumulated from actual transport position across segments, not track duration. Faster‑than‑RT accumulation is exactly what ContentIntegrity guards. Sub‑threshold (~30 s / meaningful fraction) plays legitimately don't count.
3. **Distinct ID kinds, never conflated:** `playback_id`(16B) joins all siblings **and PutState PlayerState #8** (evidence: `0d708d36…` identical in state‑reporting §2d and play‑registration §4); `command_id`(#63, minted local‑or‑remote) joins `CorePlaybackCommandCorrelation`/`AudioResolve`; `media_id`(20B)=gid; `stream_id`(#53) its own value. Reproduce sentinels.
4. **`context_device_desktop.device_id` = the real Windows machine SID** (`S-1-5-21-…`) — stable fingerprint; rotating it trips device‑anomaly detection.
5. **`context_sdk.version_name` = the exact `0.9.4-rl-…batch100` string, `type="cpp"`** — identifies the native core, not the JS SDK.
6. **`is_assumed_premium` derived from real entitlement; `audio_format` derived from the actually‑selected file** (not hardcoded).
7. **Device trio `#78/#79/#80="spotify"/"PC laptop"/"computer"`** (and segment `#79/#80/#81`) — undeclared in the proto but on the wire.
8. **Header/build/arch consistency:** `App-Platform` + `Spotify-App-Version` + `User-Agent` all agree and match the arch the Bearer + client‑token were minted for (ARM64 tokens with a Win32_x86_64 UA would flag). PlayPlay `token` is per‑(build AND arch), DLL‑hash‑pinned; `timestamp` in Unix seconds.
9. **PutState session constants stable within a session:** `started_playing_at (#9)`, `last_command_message_id (#8)`; gabo `sequence_id` per batch; monotonic `sequence_number` persisted across restarts. `installation_id` persisted once.
10. **`decision_id` (#48/#49) sent EMPTY to match desktop.** **[FIX — MAJOR, both critics]** Desktop leaves `RawCoreStream #48` empty even when autoplay supplied a `decision_id`; the schema note ties `#48` to a storage‑resolve session pointer, not the autoplay recsys token. Do **not** fabricate an `ssp~` value into `#48`. Carry the autoplay `decision_id` in the queue/context metadata for recsys/UI attribution only. Populate `#48` only if you actually hold the storage‑resolve session pointer.
11. **herodotus:** client sets `create_time` only; position in µs at field 2; play‑history head only for tracks, head + resume‑point for episodes.

- **Seam:** replace `GaboTelemetry.cs`; new `GaboContext`, `GaboEnvelopeFactory`, `RawCoreStreamProjection`, `GaboBatcher` under `Wavee/SpotifyLive/`; extend `Playback.cs` `EvKind`/`PlaybackEvent`; register the projection in `LiveConnect.cs:64‑68`. Reference "done‑right": `C:\WAVEE\WaveeMusic\src\Wavee\Connect\Events\RawCoreStream*.cs`, `GaboEnvelopeFactory.cs`.

---

## 8. Resume points/herodotus, podcast/episode + ads deltas, decoration to skip

### 8.1 Herodotus (resume points + play‑history heads) — MISSING

**Endpoints** (all `POST`, `{ap}-spclient`, request body bare protobuf `Content-Type: application/x-protobuf`; response bare protobuf, `Content-Type: application/grpc` **but NOT gRPC‑framed** — parse directly, read `grpc-status: 0` from HTTP headers):
- `/herodotus/spotify.resumption.v1.ResumePointRevisionService/CreateResumePointRevision` — one head.
- `/herodotus/…/BatchCreateResumePointRevisions` — the common path (`{repeated CreateResumePointRevisionRequest requests = 2}`); coalesced heads + episode resume‑points.
- `/herodotus/…/ListResumePointRevisions` — `{entity_uri=2, limit=3=500}` → `{repeated CurrentStateRevision revisions=1}`; post‑write rail refresh.
- `/herodotus/spotify.resumption.v1.CurrentStateService/ListCurrentStates` — startup delta sync; `{limit=2=1000, filter=4=<CEL>}` where CEL = `cs.resume_point_revisions.exists(revision, revision.update_time > timestamp('<lastSyncIso>'))`. Usually returns empty (200, len 0).

**Proto fixes** (`herodotus_current_state.proto` is wrong in 3 ways — generating from it emits position 0 + empty heads):
- `CurrentStateValue` needs **`string item_uri = 6`** (the play‑history current‑item pointer). Without it, heads are empty and Recently Played never populates.
- `ResumePoint.position` is **field 2, in microseconds** (int64) — **not** `position_seconds=field 1 uint32`. Multiply seconds ×1,000,000 (97 s → 97000000). Empty `resume_point` = position cleared/0.
- Add `BatchCreateResumePointRevisions{Request,Response}` and `ListResumePointRevisions{Request,Response}` messages. `CreateResumePointRevisionRequest{ string entity_uri = 2; CurrentStateRevision revision = 4 }`; `CurrentStateRevision{ revision_id=1, value=2, create_time=3, update_time=4 }`.

**Two write paths, demuxed by `entity_uri`:**
1. **Play‑history head** (tracks AND episodes): `entity_uri="spotify:list:play-history:v1"` (a fixed synthetic list uri — never resolve as content), `value.item_uri (#6)` = played uri, no resume_point. **[FIX — MINOR, both critics]** Write the head when the item **becomes current** — on `Started`/`TrackChanged` — **not on `Paused`** (a track pause is not a becoming‑current event; writing a head on `Paused` produces a redundant/incorrect revision). Cover the **first‑track/fresh‑context start** (no preceding `TrackChanged`). `create_time` = the instant it became current (client UTC).
2. **Episode resume‑point:** `entity_uri="spotify:episode:…"`, `value.resume_point.position` = µs. Written on **leaving** an episode (`Paused`/`Ended`/skip/close). Emit the episode's play‑history head **and** its resume‑point in the same batch (evidence: session 361). Tracks never get a resume‑point.

**IDs/authority:** client sets `create_time` only; server assigns `revision_id` + `update_time` (may precede `create_time` from clock skew — don't assert ordering; a batch shares one server `update_time`).

**Read/restore (startup):** `ListCurrentStates` with the CEL filter and/or `ListResumePointRevisions(entity="spotify:list:play-history:v1", limit=500)` to seed the rail; seek episodes to `resume_point.position µs` on open — feed into `PlaybackController.GhostResumeAsync (:707)` and `LoadAndPlayCurrentAsync (:730)`.

**Incognito:** suppress `play-history:v1` heads while still writing episode resume‑points (match `EventService.SuppressPlayHistory`).

- **Seam:** new `HerodotusClient` + `ResumePointProjection` (fed from the event log, added to `_extra[]`) under `Wavee/SpotifyLive/`; read‑seam in `PlaybackController.cs`. Reference: `C:\WAVEE\WaveeMusic\src\Wavee\Core\Http\SpClient.cs`.

### 8.2 Podcast / episode playback — track‑only today → add episode path

Two delivery models under `spotify:episode:` — you must inspect the metadata before choosing the audio path.

**Resolve:**
1. `GET /context-resolve/v1/{episodeUri}?include_video=true` (`accept: application/json`) → single‑item `Context`.
2. `POST /extended-metadata` requesting **178 IDENTITY_TRAIT**, **212 PLAYBACK_TRAIT**, **5 AUDIO_FILES** (modern client uses `spotify.contentagnostic.v2.*Trait` + `spotify.extendedmetadata.audiofiles.AudioFilesExtensionResponse`, **not** the deprecated `EPISODE_V4(12)`/`SHOW_V4(11)`; `metadata/4/episode` is never called). Dispatch on `Any.type_url`.

**Branch:**
- **External / RSS episode** (audio‑only, e.g. Episode A `3kwvTyYX…`): `PlaybackTrait #1` = a direct `http(s)` url (e.g. `sphinx.acast.com/…/media.mp3`). **Stream it directly** — plain HTTP range GETs, **no storage‑resolve, no PlayPlay, no DRM**. Optionally `net-fortune?...content_type=external-show-audio`. Reports as **plain audio / `boombox` semantics** (audio `media_type`, `playback_stack="boombox"`). Needs an MP3 decoder path in `AudioPlayEngine`.
- **Spotify‑hosted audio episode:** `AudioFiles` gids + format → **reuse the existing `ResolveBodyAsync` storage‑resolve + PlayPlay path** (entity‑agnostic once a gid exists; `content_type=AUDIO_EPISODE(2)`).
- **Video / OSFP episode** (e.g. Episode B `3OaTg39…`, Spotify‑hosted video podcast): `GET /manifests/v9/json/sources/{sourceId}/options/supports_drm` → AAC audio profile + AVC video ladder, 4‑s segments keyed by `encoding_id`; segment fetch. **Only** this hosted‑video branch uses the harmony/Betamax gabo constants. **[FIX — MINOR]** Gate `playback_stack="harmony"`, `media_type="video"`, `provider="autoplay"`, `BetamaxPlaybackSession` on the hosted‑video branch **only** — the external RSS mp3 branch uses audio/boombox. Defer video unless supporting video podcasts.

**Episode resume** via §8.1 (µs, distinct sub‑message from play‑history heads). Autoplay falls back to **music** (`/context-resolve/v1/autoplay`) when the podcast queue ends; `/context-resolve/v1/autopodcast` seeds the up‑next.

- **Proto/model work:** add the inner `Any` payloads (`IdentityTrait`, `PlaybackTrait`, `AudioFilesExtensionResponse`), or hand‑decode the two load‑bearing fields (PlaybackTrait url string; AudioFiles gid+format).
- **Seam:** add `spotify:episode:` handling to `LiveTrackResolver.FetchMetaAsync (:78)`; relax the `spotify:track:` gates in `LiveContextResolver.cs (~:101/:209)`; add an entity‑kind flag to `Wavee.Core/Domain/Models.cs Track` (`QueueCore` is entity‑agnostic, no change); guard lyrics (`LiveSessionHost.cs:325`) to skip episodes. Reference: `C:\WAVEE\WaveeMusic\src\Wavee\Audio\TrackResolver.cs`.

### 8.3 Ads deltas (free tier) — skip for a Premium‑style client

`podcast-ap4p/leavebehinds/ads`, `ads/v2/config` (fingerprints via base64 `user_agent`), and extended‑metadata kinds 20 `PODCAST_AD_SEGMENTS`/108 `PODCAST_SPONSORED_CONTENT`/113 `COMPANION_CONTENT`. A premium token returns empty/no slots — omit. Handle 404 on a kind as "no data," not an error. Chapters (`playlist/v2/list/podcast-chapters`), transcripts (`transcript-read-along/v2`), and subtitles (double‑signed WebVTT `__token__` + `fauth` JWT — server‑minted, not client‑forgeable) are optional/cosmetic.

### 8.4 Decoration calls to SKIP (explicit — none on the audio path; a track plays to completion with zero of these)

- **Pathfinder / APQ** (`api-partner.spotify.com/pathfinder/v2/query`): `recentSearches`, `saveRecentSearches`, `searchSuggestions`, `searchTopResultsList`, `fetchExtractedColors` (Wavee extracts accent on GPU), `getCommentsForEntity`, `queryNpvEpisode`/`queryNpvArtist`, `getAlbum`/`queryAlbumMerch`, `queryArtistOverview`, `getTrack`. Implement only as lazy, view‑scoped, cancellable fetches if/when those UI surfaces exist; the canonical playback tracklist comes from `playlist/v2` + context‑resolve, **not** `getAlbum`.
- **MISC — skip outright:** `melody/v1/msg/batch`, `socialgraph/is-following`, `popcount`, `playlist-permission/*` (do **not** replicate the per‑row N+1 fan‑out storm), `playlist-publish/subscription`, and all gabo **Ubi/Km/SemanticMetric JSON** analytics on `gew4-spclient/gabo-receiver-service/v3/events` (no trailing slash — distinct from the play plane's `wg` host + trailing slash).

### 8.5 melody stance **[FIX — MINOR, both critics]**

`melody/v1/msg/batch jssdk_playback_start` is the **web JSSDK's own** video/canvas/DASH telemetry, distinct from the native gabo events. Wavee uses the **native audio path**, so melody is **not required** — continue‑listening is served by **herodotus** and counts by **gabo RawCoreStream**. **Skip melody entirely for both tracks and episodes** (the bundle self‑contradicts on this — podcast‑ads §5.4 says "required," decoration §4a says skip; the rigorous decoration census wins). Noted here so no one re‑adds it as "required."

---

## 9. Milestone build order (audible‑first) with acceptance checks

| Milestone | Deliverable | New/changed | Acceptance gate |
|---|---|---|---|
| **M0 — Transport + proto fixes + IDs** | `Channel.SpclientWg`; two gzip dialects; multi‑frame zstd + brotli decode; `playplay.aux`, `storage-resolve.ttl_seconds`; confirm `supports_gzip_pushes`; mint unified `PlaybackIds`; update `feature_version` constant to `xpui-snapshot_2026-07-01_1782890476915_7b5cc0c`. | `Transport.cs`, `Protos/playplay.proto`, `storage-resolve.proto`, `PlaybackController.cs`, `SpotifyClientIdentity.cs`. | A music track still plays end‑to‑end; PutState `PlayerState #8` == the minted `PlaybackIds.playback_id`. |
| **M1 — PutState fidelity** | Remove `#11 has_been_playing_for_ms`; full `PlayerState` parity; gzip + `X-Transfer-Encoding`; brotli response decode; correct `put_state_reason` mapping + `SPIRC_HELLO` on connect; `Restrictions`/`next_tracks`/`context_metadata`; volume 0–65535. | `DeviceStatePublisher.cs`, `ConnectStateBuilder.cs`, `OutboundEnvelope.cs`. | Real Spotify remote shows Wavee as active device with correct now‑playing + queue + greyed controls; PutState body diffs clean vs capture (no `#11`). |
| **M2 — Gabo core (play count)** | `event_sender_*` protos; `GaboContext`/`GaboEnvelopeFactory`/`GaboBatcher`; `RawCoreStreamProjection` emitting RawCoreStream + Segment + ContentIntegrity + AudioSession to `wg…/events/`; extend `EvKind`/`PlaybackEvent`; **local‑or‑remote `command_id`**; 401‑refresh‑and‑retry; `#48/#49` empty; `is_assumed_premium`/`audio_format` derived. | new protos + 4 new classes; replace `GaboTelemetry.cs`; `Playback.cs`; register in `LiveConnect.cs`. | A >30 s track appears in the real account's Recently Played and the play count increments (validate against the live account — cannot be unit‑checked). |
| **M3 — Herodotus resume** | Fixed `herodotus_current_state.proto` (`item_uri #6`, µs `position #2`, Batch/List messages); `HerodotusClient` (unframed‑grpc parse); `ResumePointProjection` writing **track head on becoming‑current**, episode head+resume‑point on leave; startup read → seed rail + episode seek; incognito suppression. | new proto + 2 classes; read‑seam in `PlaybackController.cs`. | "Recently played" rail seeds from `ListResumePointRevisions`; a reopened episode seeks to its stored µs position cross‑device. |
| **M4 — Gabo fidelity siblings** | Co‑batched siblings incl. **`AudioResolve` (#4 == RawCoreStream #63)** + `AudioFileSelection`, Boombox, Download, HeadFileDownload, CorePlaybackCommandCorrelation, AdOpportunity, AudioDriverInfo, AudioRouteSegmentEnd; device‑trio + reserved wire fields; flush policy (100/125 kB/300 s); sentinels; `sequence_number` persisted. | `event_sender_events.proto` additions; `RawCoreStreamProjection`. | Wire capture of Wavee ≈ byte‑identical event set to desktop for one track (byte‑diff gate catches `#78/#79/#80`, sentinels, `#11`). |
| **M5 — Episode audio** | Episode branch in `LiveTrackResolver` (IDENTITY/PLAYBACK/AUDIO_FILES traits); relax playability gates; hosted → `AUDIO_EPISODE` PlayPlay; external → direct MP3 range GET (no DRM) in `AudioPlayEngine`; entity‑kind flag on `Track`; prefetch resolve variant respected. | `LiveTrackResolver.cs`, `LiveContextResolver.cs`, `AudioPlayEngine.cs`, `Models.cs`, trait protos. | Both a Spotify‑hosted episode and an Acast/RSS external‑mp3 episode play from `spotify:episode:` URIs, report (audio/boombox for external), and resume. |
| **M6 — autopodcast + episode resume polish** | `/context-resolve/v1/autopodcast` up‑next; autoplay→music fallback; episode resume‑point write on leave verified. | `LiveContextResolver.cs`, `ResumePointProjection`. | Episode queue continues; episode resumes cross‑device at last µs position. |
| **M7 — Ads / decoration (optional)** | Free‑tier ad path (or correct absence under premium); decoration skip‑list documented and honored. | optional. | Free‑tier ads resolve or are correctly absent; no permission/popcount N+1 storm. |

**The critical audible path is M0→M1 only.** M2 is the first user‑visible gap (plays don't register). Everything after M3 is fidelity/breadth. Ads, transcripts/subtitles, video‑podcast manifests, and Pathfinder decoration are out of scope except cosmetic UI later.

**Validation regime:** proto round‑trip unit tests (headless, alloc‑gated) for every new message; a `RawCoreStreamProjection` state‑machine test driven by a synthetic `EvKind` sequence (play→pause→seek→resume→trackdone) asserting segment splits, summed `ms_played`, `is_last`, reason codes; herodotus µs/field‑6 encoding; batch flush triggers; a **wire‑diff** test that captures Wavee's own SAZ and byte‑compares the gabo POST + PutState body against the bundle (the only way to catch `#78/#79/#80`, sentinels, and the removed `#11`); a **live‑account** check (play >30 s → Recently Played + play count; episode → cross‑device resume); and a threading check that projections never await on the controller thread and the alloc tripwire (phases 6–13) stays green.

---

## 10. Risks / open questions

1. **What the capture could NOT show — raw ranged audio GETs (E4).** The encrypted CDN body rides HTTP/3/QUIC (`alt-svc: h3`) and never hit the TLS‑MITM proxy. Only the `heads-fa` fast‑start chunk (HTTP/1.1) was captured. Range semantics, retry/mirror behavior, and byte‑range sizing are inferred from the existing (working) `SpotifyAudioStream` implementation, not from wire evidence. If E4 validation is needed, force HTTP/1.1 (block UDP/443).
2. **What the capture could NOT show — dealer WS frames.** All 27 spclient CONNECTs are TLS tunnels; the dealer WebSocket (`ClusterUpdate`, inbound player commands, the pusher hello) is not decodable HTTP. The connection‑id linkage and command‑ack fields are reconstructed from the PUT bodies and the existing wired implementation. Inbound command → `command_id` minting on the remote path is inferred; the local path (dominant) is well‑evidenced.
3. **PlayPlay token arch drift.** The audio‑DRM capture is a **1.2.88.483‑x64 build emulated on ARM** (`x64[native:ARM]`); the target is 1.2.93.667‑ARM64. The token is per‑(build AND arch), DLL‑hash‑pinned — the `PlayPlayKnownPacks` table must carry the exact 1.2.93.667‑ARM64 token (`025614bf…` per memory) and VM VA offsets, keyed by the shipping `Spotify.dll` hash. Verify the pin before M0 sign‑off.
4. **`ms_played` counting threshold is server‑side and unstated.** ~30 s / "meaningful fraction" is the documented heuristic; the exact threshold and how partial/skipped plays are credited can only be confirmed against the live account. Short abandoned plays (session 057, ~1.5 s) legitimately do not count.
5. **APQ hash rotation.** If Wavee ever adds Pathfinder decoration UI, the build‑pinned sha256 hashes rotate; the safe long‑term path is to send full query text with `persistedQuery` omitted rather than pin hashes. Out of scope for playback.
6. **`#48 decision_id` semantics.** The capture shows `#48` empty despite autoplay supplying a `decision_id`; the schema note ties `#48` to a storage‑resolve session pointer. We send empty. If a future capture shows a populated `#48`, revisit whether the storage‑resolve pointer (not the autoplay recsys token) is the source.
7. **Proto version drift (client 1.2.93 vs vendored 1.2.52).** Many wire fields are newer than the vendored protos (`Capabilities #33–38`, `PlayerState #33/#35/#38`, `Restrictions #24–31`, extended‑metadata kinds >215, `Any.type_url`s). Preserve unknown fields round‑trip everywhere and never hard‑fail on an unknown kind or type_url.
8. **Herodotus framing trap (documented, resolved).** The bundle self‑contradicts: podcast‑ads §6 says "strip the 5‑byte gRPC frame header," but the authoritative byte‑exact resume‑history §1/§6 proves the response is **NOT** gRPC‑framed (body starts at `0a 88 01…`, `grpc-status` in HTTP headers). We follow the authoritative decode — parse bare protobuf, do not strip 5 bytes. Any downstream editor must not "correct" this toward the podcast‑ads statement (it would inject a parse bug).