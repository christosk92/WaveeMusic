# Wavee Playback Command Arbitration — Business Rules & Edge Cases

The rules that decide, for every playback intent, **where it goes** (local silent host vs. forwarded to the
active Connect device) and **what it does** across the possible world-states. Grounded in WaveeMusic's
`ConnectCommandExecutor` + `PlaybackStateManager.ResumeAsync` (ghost resume) and librespot's spirc, plus
edge cases we should handle that aren't explicit there. `[R]` = confirmed in the reference, `[+]` = added by us.

---

## 1. The routing spine

```
target = cluster.ActiveDeviceId      // who is the active player right now
self   = our device id
LOCAL  ⇔  target is empty  OR  target == self  OR  the command targets a wavee:local: uri
REMOTE ⇔  otherwise (forward the command to `target`)
```
`[R]` Applies to **every** verb, not just transport. The current code's `_localActive` flag is wrong — "no
active device" must route **LOCAL** (we take over), never "forward to nobody". The cluster is the single
source of truth for who's active; we don't need a local flag.

`[+]` Corollary: there is no "we haven't played yet so forward" state. Fresh app, nobody active → every
intent is local.

---

## 2. World-states (what `cluster` + our local host look like)

| State | activeDeviceId | cluster track | our host | meaning |
|---|---|---|---|---|
| **Cold** | empty | none | empty | fresh account / never played anywhere |
| **Ghost** | empty | present (usually paused) | empty | someone paused then went inactive, or we restarted and the cloud remembers the last session |
| **We-active** | self | present | loaded | we are the player |
| **Remote-active** | other | present | empty (or stale) | another device is the player; we're a viewer/controller |
| **Handing-off** | transitioning | present | loaded→stopping | transfer in flight |

---

## 3. Per-command behaviour (× world-state)

### Play(context, index/trackUri)
- **Remote-active** → `[R]` forward a `play` command to `target` (rich envelope: context + pages + skip_to +
  play_origin). Playback starts on the remote.
- **Local / Cold / Ghost** → `[R]` resolve the context → local host load+play → we become active (→ §4 re-PutState).
- `[R]` `wavee:local:` uris **force local** regardless of active device.
- `[+]` Empty context (resolves to 0 tracks) → no-op + surface a soft error; never clear the current player.
- `[+]` Play the **already-playing** context's *play button* → treat as resume/pause toggle, not a restart.
  Play a **specific track row** in the current context → seek-to-that-track (skip within context), not reload.
- `[R]` Premium gate: a Free account blocks on-demand play (Wavee is premium-only anyway).

### Resume (the play button while paused)
- **We-active, host has a track** → `[R]` `host.Resume()`.
- **Local + host empty + cluster has a track (Ghost)** → `[R]` **GHOST RESUME**: seed context/track/position
  **and prev/next queue** from the cluster snapshot → local play. This is the user's example (paused, nobody
  active, hit play → take over here).
  - `[R]` `userInitiated` (a real button press) **skips** the freshness/paused guards.
  - `[R]` Auto-resume (not user-initiated) guards: skip if cluster state >30 s old, or status≠Playing;
    compensate position by elapsed time; skip if the track "would have ended".
- **Remote-active** → `[R]` forward `resume`.
- **Cold** (no cluster track) → `[+]` resume the user's last local context if we have one; else no-op (button
  is effectively disabled / shows nothing to resume).
- `[R]` Restriction `disallow_resuming` → ignore + disable the control.

### Pause
- **Local & playing** → `[R]` `host.Pause()`. **Remote-active** → forward `pause`.
- `[+]` Pause while **already paused / nothing playing** → no-op. Critically, **pause never ghost-resumes** —
  only resume/play/transfer-to-self do. (So "paused + nobody active + hit *pause*" = no-op; it's *play* that
  takes over.)
- `[R]` Restriction `disallow_pausing` → ignore.

### Next / Previous
- **Local** → `[R]` `queue.Next/Prev` + host load. **Remote** → forward `skip_next`/`skip_prev`.
- `[+]` **Previous** semantics: within the first ~3 s of the track → go to the previous track; after 3 s →
  restart the current track (Spotify behaviour). At the first track with repeat-context → wrap to last.
- `[+]` **Next** at end-of-context → if the context is infinite (station/radio/autoplay) or autoplay is on,
  request more tracks; else stop and emit Ended. Repeat-context → wrap to 0. Repeat-one → an *explicit* Next
  still advances (repeat-one only auto-repeats on natural end).
- `[R]` Restrictions `disallow_skipping_next` / `disallow_skipping_prev` (ads, first/last track) → disable.

### Seek(ms)
- **Local** → `[R]` `host.Seek(clamp 0..duration)`. **Remote** → forward `seek_to`.
- `[+]` Seek ≥ duration → clamp just below duration (don't accidentally trigger Ended/Next).
- `[R]` Restriction `disallow_seeking` (ads) → ignore. Premium gate: Free can't seek.

### SetVolume(0..1)
- **Local** → `[R]` `host.SetVolume`. **Remote** → `[R]` forward `set_volume` with the 0..65535 value.
- `[+]` Setting volume > 0 while muted → unmute. Remote device advertising `disable_volume` → ignore + hide
  the slider. Volume is **device-scoped** (each device row has its own), so the slider follows the *active* device.

### Shuffle / Repeat
- **Local** → `[R]` drive `QueueCore`. **Remote** → forward.
- `[R]` Remote **repeat must SPLIT** into `set_repeating_track` + `set_repeating_context`, and always send
  **both** an explicit complete mode (so Track→Off / Track→Context can't leave the target stuck).
- `[R]` Restrictions `disallow_toggling_shuffle` / `..._repeat_context` / `..._repeat_track` → disable.

### Add-to-Queue / Play-Next
- **Local** → `[R]` queue bucket (play-next = head; add = tail / post-context). **Remote** → `[R]` `set_queue`
  with the full user-queue snapshot (Connect has no post-context bucket → add collapses to tail).
- `[+]` Add-to-queue / play-next while **nothing is playing** → start playing that track (Spotify behaviour),
  not silently enqueue into the void.
- `[R]` Batch (multi-select) → ONE local mutation / ONE remote `set_queue`, never one command per track
  (looping floods the cluster publisher and freezes the UI).

### Transfer(to deviceX)
- **X == self** → `[R]` **ghost resume** (NOT the HTTP transfer endpoint — `from=self/to=self` returns 400);
  this takes over playback here and makes us active.
- **X == another** → `[R]` forward a `transfer` command (with `restore_paused` = keep-playing vs pause) **and
  STOP the local host** so we don't double-play (in proxy-only mode the "another device active" handler may
  not fire).
- `[+]` X == the already-active device → no-op.

### SkipToQueueItem / ReorderQueue
- `[R]` **Local only** — the `set_queue` wire format can't express post-context/context-tail ordering, so a
  remote reorder/skip-to is rejected and the UI hides the affordance when playing remotely.

---

## 4. Lifecycle / state-transition rules

- `[+]` **On local play start** → re-PutState `is_active=true` + our `player_state`, so the cluster and other
  controllers see us as the active player. (Today's gap: we never re-announce on local change → the
  `DeviceStateProjection`; until it lands, others won't see our local playback, though local control is correct
  because empty-active-device routes local.)
- `[R]` **On "another device became active"** (cluster.activeDeviceId flips to a non-self device while we were
  playing) → **stop the local host immediately** (prevent double audio).
- `[R]` **On transfer-away success** → stop the local host.
- `[+]` **On track Ended (local)** → auto-advance (Next); at true end-of-context → autoplay-request or stop+Ended.
- `[+]` **On dealer reconnect** → connection_id changes (null → new); re-announce (re-PutState NewConnection),
  and if we were locally active, re-assert is_active.
- `[+]` **On AP/dealer drop mid-command** → the command fails fast; surface a recoverable error; retry on
  reconnect for idempotent intents.

---

## 5. Reconciliation (optimistic local write vs. cluster truth) — partly built (§Stage D)

- `[R]` Local command → optimistic UI immediately + `NoteLocalCommand()`; a *stale* cluster push within the
  in-flight window must not revert it.
- `[+]` When **remote-active**, the cluster is the **only** truth — don't apply local optimistic state at all
  (we're a pure viewer). Optimism applies only when we are (becoming) the active device.
- `[R]` Our **own** PutState echo (`X-Wavee-Echo:self`) must not be treated as a foreign change.

---

## 6. Capability / gating rules

- `[R]` Premium-only: Free → block on-demand play/seek (Wavee gates at login already).
- `[R]` Per-track **restrictions** from the cluster gate the controls (the `disallow_*` lists in §3): map them
  to `CanSkipNext/CanSkipPrev/CanSeek/CanPause/CanResume/CanToggleShuffle/CanToggleRepeat` on the projection,
  so the UI disables rather than failing at command time. Ads typically disallow skip+seek.
- `[+]` `is_controllable` / `can_play` device capabilities gate whether we even *offer* control of a device row.

---

## 7. Error / unavailable handling

- `[R]` No active device **and** no local host → drop with a `DeviceUnavailable` error.
- `[R]` Remote command times out (no ack within the per-endpoint budget) → `Unavailable`.
- `[+]` Track resolve yields no playable file (even via alternatives) → skip to Next (or error if the whole
  context is unplayable).
- `[+]` Forwarded command to a device that has since **disappeared** → `DeviceNotFound` → refresh the roster +
  fall back to local if appropriate.
- `[R]` Per-endpoint ack policy: play/queue/transfer ~2.5 s, transport ~1.5 s, option toggles ~1.2 s; transport
  commands generally **don't wait** for the dealer ack (the HTTP 200 is the confirmation) to keep controls snappy.

---

## 8. Inbound (we are the Connect target) — symmetric

- `[R]` When we're active, inbound REQUEST commands drive our **local** host with the same per-command
  semantics (HandleRemoteCommand), always acking within 10 s (received≠succeeded).
- `[+]` Inbound `transfer` **to us** → become active + ghost-resume/start. Inbound `set_volume` → set host
  volume + reflect. Inbound `play` with a context → resolve + local play.

---

## 9. What we implement now vs. defer

**Implement now (high-confidence, matches the reference):** the routing spine (§1, drop `_localActive`);
ghost resume on resume/self-transfer (§3 Resume, with the userInitiated guards); volume/shuffle/repeat
routing incl. the remote repeat-split; PlayAsync remote-forward; transfer self=ghost / away=forward+stop;
"another device active → stop local"; end-of-context Ended; the add-to-queue-when-idle-starts-playing rule.

**Defer (needs the PutStateProjection or richer wire work, noted in the doc):** re-PutState `is_active`+player_state
on local change (so others see us); full restriction→Can* gating surfaced from real cluster restrictions; the
rich remote `play` envelope (pages/play_origin parity); autoplay/radio continuation; prev-3s and
repeat-one-vs-explicit-next refinements (wire once the queue model is widened).
