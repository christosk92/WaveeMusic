# Connect Dealer wire notes

This note records protocol behavior inferred from the sanitized `all.saz` and playback-remote evidence. It intentionally
contains no access tokens, connection IDs, device IDs, account IDs, or captured URIs.

## Inbound boundary

- Player operations arrive as Dealer `REQUEST` frames below `hm://connect-state/v1/player/command`.
- Volume arrives separately as a Dealer `MESSAGE` on `hm://connect-state/v1/connect/volume`; it is not a player command.
- A REQUEST is acknowledged after successful parse and bounded-queue admission. Execution remains ordered on one worker
  and its result is reported through logs and subsequent PutState publication.
- Exact `(sender device ID, message ID)` replays are acknowledged but executed once.
- Correlation logs retain the numeric message ID and hashed sender/session/command identifiers.

## Volume body

The MESSAGE payload is a small protobuf. Field 1 is the integer volume on Spotify's `0..65535` scale. Unknown fields are
skipped. The capture-shaped fixture:

```text
08 A6 8D 01 1A 00 22 04 77 6C 61 6E
```

decodes field 1 as `18086` (about 27.6%). Applying an inbound value updates the active host and local projection, then
publishes `VolumeChanged`; it does not send a second outbound volume command.

## Play and update_context

- Embedded `context.pages` are authoritative only when they contain actual rows.
- Stub or empty pages trigger a URI resolve and cannot replace a resident context with a one-row queue.
- Context row counts are entirely data-driven. The observed playlist length is evidence, not a cap or constant.
- `update_context` preserves the current row by UID/URI, stable queue identity, history, user queue, and options.
- A context refresh that omits the playing row keeps that row as an external current item until the next advance.
- Resolve generations discard late completions, and identical context rows become metadata-only/no-op updates.

## Transfer inner state

The transfer command's base64 `data` field carries a protobuf `TransferState`. The decoder consumes:

- playback current track, context, position, paused state and timestamp;
- session ID;
- shuffle/repeat options;
- transferred queue rows and queue-current state;
- restore flags from both the inner options and outer command.

A missing URI is derived from the 16-byte GID using Spotify Base62. A transfer is committed as one session snapshot before
the host is loaded, preventing intermediate queue and player-state publications.

## PutState ordering

PutState requests are serialized. Each request snapshots:

- the local playback and queue state;
- its monotonically increasing message ID;
- the most recent inbound sender device ID and message ID.

The snapshot is built before waiting for the previous request, so the response cluster is folded in the same order as the
state events. `422` after `BecameInactive` is treated as a terminal soft acknowledgement; no blind retry is issued.

## Recovery behavior

- A failed video open gets one source recovery attempt, then falls back to the audio host at the best-known position.
- Retired host subscriptions are generation-checked, so late error/ended signals cannot affect the replacement host.
- The account-wide AP audio-key failure switch is guarded by one probe gate. Concurrent file requests share the failing
  probe and then proceed through the fallback path without a duplicate disable storm.
- A failed/empty autoplay prefetch can make one forced autoplay retry at track end; it does not immediately collapse the
  active context into an inactive/PutState rejection chain.

## Logging needed for future captures

Keep the following structured fields on receive, completion, state publication, and recovery edges:

- endpoint, message ID, hashed sender, hashed command/intent ID, hashed session ID;
- queue/context generation, row count, current UID/URI hash, and outcome;
- transfer decode source (inner state versus cluster fallback), restore flags, and resumed position;
- volume integer and normalized value;
- PutState message ID, reason, active flag, originating command IDs, HTTP status, and response size;
- media-host generation, retired-signal drops, video recovery/fallback, AP probe leader/follower, and autoplay attempt.

Payloads, bearer tokens, raw device IDs, raw account IDs, and connection IDs must remain excluded.
