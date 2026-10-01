# Connect commands as the official client sends them (capture 2026-10-01)

What a Fiddler capture of the official desktop client (1.2.96.518, acting only as a controller) and Wavee side by side
proved about the connect-state command wire. Shapes are written by hand with metadata stripped; no tokens, no device
ids. Status column: **verified** = seen in the capture, **unverified** = Wavee's own spelling, never exercised there.

## Outbound HTTP commands (`POST /connect-state/v1/player/command/from/{self}/to/{target}`)

Headers: bearer, client-token, identity tuple, `Accept-Language`, `X-Spotify-Connection-Id`, form content type,
`X-Transfer-Encoding: gzip` (body gzipped), **no** `Accept`. Answer: `{"ack_id":"..."}`. Envelope on every command:
`{command{endpoint, ..., logging_params{...}}, connection_type:"wlan", intent_id}`.

| Command | Endpoint spelling | Body specifics | Status |
|---|---|---|---|
| Skip button | `next_track` (7 of 7) | `options{override_restrictions,only_for_local_device,system_initiated}` (all false), **no** `track`, `logging_params` | verified; Wavee sends it (`Connect.NextTrack` with an empty track) |
| Queue-row click | `next_track` | as above plus `track{uri,uid,metadata}`; the owner advances its own queue to that row | verified; `NextTrackBody` |
| Previous button | `skip_prev` | `Connect.Command` body | unverified (no capture), left as is |
| `add_to_queue` | `add_to_queue` | `track{uri,uid:"",metadata:{}}`, `options`, `logging_params` | verified |
| `set_queue` | `set_queue` | `queue_revision` bare number, `prev_tracks[]`, `next_tracks[]` (rows `{uri,uid,metadata,provider:"queue"/"context",removed[],blocked[],restrictions{22 disallow_*}}`, new rows `uid:""` + `metadata{is_queued:"true"}`), `options`, `logging_params`; a `spotify:delimiter` row separates queue from context tail | verified |
| `play` | `play` | context (+ pages of up to 50 tracks), `play_origin`, `prepare_play_options{skip_to{track_uri,track_uid,track_index}}`, `play_options`, `logging_params` (no `command_received_time`) | verified for the official envelope; Wavee's `PlayBody` is URI-only and unchanged (unverified against the gateway) |

## Transfer (`POST /connect-state/v1/connect/transfer/from/{caller}/to/{target}`)

* `from` is the **caller's own device id** even when the caller is not the owner (3 of 3). Wavee: `Connect.Transfer(target)`.
* Headers as a command but the body is **not** gzipped and the request adds the bare word `Accept: protobuf`
  (`HeaderSet.AcceptBareProtobuf`).
* Body, in this key order: `{"options":{"restore_paused":"restore","restore_position":"extrapolate","restore_track":"only_current","license":"premium"},"transfer_intent_id":"<32hex>","command_id":"<32hex>","interaction_id":"<guid>"}`.
* Answer `{"ack_id":"..."}`; the **cluster update's field 3 (`ack_id`) echoes it** once applied. Wavee decodes it into
  `ClusterDelta.AckId` / `ClusterFrame.AckId` (FNV-1a hash) and `Playback.TransferTookEffect` uses it, with the new
  owner, so a refused/lost POST answer never toasts "transfer failed" over a transfer the cluster already confirmed.

## Inbound over the dealer (`hm://connect-state/v1/player/command`)

Payload `{message_id:uint32, sent_by_device_id, command{...}}`; Wavee answers `{"type":"reply","key":key,"payload":{"success":true}}`.

| HTTP command | Dealer form | Wavee handling |
|---|---|---|
| `next_track` (plain) | `skip_next`, no track | one step forward |
| `next_track` + `track{uri,uid}` | `skip_next` + the same `track` | **jump** to the named row ahead of the cursor: uid first, then uri (`Queue.SkipTarget`); rows jumped over stay behind the cursor; no match: one step, logged. (Before: always one row, wrong song for rows 2 and 4-5.) |
| `set_queue` | `queue_revision` becomes a JSON string | decoded as a uint64 |
| `transfer` | `endpoint:"transfer"`, `options{... restore_track:"always_play_something", retain_session}`, `from_device_identifier`, `data` = base64 TransferState protobuf | current track and queue rows are gid-only (`uri:""`) |

`message_id` is a **uint32** on the wire (live ids ~1.955e9 on 2026-10-01, so ids >= 2^31 are days away). It is carried
as `uint` end to end and echoed exactly as `last_command_message_id` on the next put-state.

## Picker opened

Both are sent in the same millisecond when the official client's device picker opens (3 of 3):

* `PUT /connect-state/v1/devices/{self}?wake-devices=false`, put_state_reason **6** (`PICKER_OPENED`), `is_active=false`,
  device info only. Wavee: `Playback.PickerOpened()` announces with reason 6 and the query (`RequestArgs.Flag` on
  `ConnectStatePut`); the player half stays empty while another device owns playback.
* `POST /connect-state/v1/cluster/wake-devices`, empty body (`Content-Length: 0`). Wavee: `Connect.WakeDevices`.

Wavee rate-limits the pair to once per 5 s (`Connect.PickerDue`) and sends nothing without a dealer connection.

## Known differences left alone

* The official picker PUT carries no `player_state` at all; Wavee's encoder always opens an (empty) `player_state` with a timestamp.
* `has_been_playing_for_ms` appears after the first skip and is held until the next one; the server derives cluster field 11 from it (unverified whether it matters).
