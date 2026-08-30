# Wavee Connect remote-play — manual live smoke test (parity sign-off)

The headless harness (`dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj`, 343 green) proves the command/state logic. This
is the **live** sign-off that exercises the real Spotify service end-to-end. Audio is intentionally silent (the real
`IAudioHost` is a separate milestone) — what we verify here is the **control plane**: the device appears, is controllable,
controls others, state syncs, and a network drop is *visible*.

## Run

From the repo root (`C:\WAVEE\fluent-gpu`):

```
dotnet run --project src/apps/Wavee/Wavee.csproj -- --connect-live
```

The probe (`LiveSessionHost.RunAsync`) logs in (stored cred or device-code), brings up the live dealer socket, swaps the
live backend in (`svc.GoLive`), prints the now-playing the UI bridge sees + the realtime-link status, and listens for 90 s.
You need a second device (phone/web Spotify) signed into the **same account**, **Premium**.

## Steps & expected observations

| # | Action (on your phone/web) | Expected in the probe log | Verifies |
|---|---|---|---|
| 0 | — (just after login) | `dealer connected (…)` · `realtime link: Connecting` → `Online` · `put-state (NewConnection …)` | Device announce (Phase A/C/E) |
| 1 | Open the **device picker** | "Wavee" appears in the list | Device registration |
| 2 | **Transfer** to Wavee + play a **playlist** | `connect command: Play` · `bridge now-playing: <track> — playing` · queue populates | Inbound play + **context-resolve** (Phase A) |
| 3 | Play an **album**, then **Liked Songs** (sorted) | same, with the right first track / order | Context-resolve across types + collection sort (Phase A) |
| 4 | **Pause / seek / next / prev** | `connect command: {Pause,SeekTo,SkipNext,SkipPrev}` + a `put-state (PlayerStateChanged …)` each | Inbound verbs + **PutState on every change** (Phase B/C) |
| 5 | Toggle **shuffle / repeat** | `connect command: Set{Shuffling,Repeating}…` + put-state; the phone reflects it back | Options + state sync (Phase B/C) |
| 6 | **Add to queue** from the phone | `connect command: AddToQueue`; the track shows in up-next | Queue verbs (Phase B) |
| 7 | From **Wavee** (UI), with the phone active, hit play/pause | the **phone** reacts (outbound forward) | Outbound envelope (Phase D) |
| 8 | **Transfer away** to the phone | `put-state (BecameInactive, active=False)` | Clean hand-off (Phase C) |
| 9 | Toggle **airplane mode** ~5 s, then back | `realtime link: Reconnecting` → `Online` (or `dealer half-open … forcing reconnect` if the socket went half-open) | **Socket observability + retry** (Phase E) |

## Pass criteria
- Wavee appears in the picker and accepts every inbound verb (each logs a `connect command` + a `put-state`).
- Play-by-context populates the right tracks/order (queue is non-empty, correct first track).
- Controlling the phone *from* Wavee works (step 7).
- A network drop surfaces as `Reconnecting` and recovers to `Online` on its own (step 9) — never a silent stale state.

## Known boundaries (not failures)
- **No audio** — the silent host produces no sound; "playing" means the command/queue/state ran. Real decode/output is the
  separate `IAudioHost` milestone.
- **Single-track add-to-queue** carries duration-only metadata; very large contexts eager-load the first ~8 pages
  (lazy `LoadMore` is wired but not yet auto-triggered on queue-drain).
- Inbound `update_context`/`set_queue` are honored; remote queue *reorder* (Move/Remove targeting another device) is local-only.
