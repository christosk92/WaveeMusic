# Radio (song radio / artist radio) — Apple-Music-style "Start radio" design

Status: proposed (design only — not yet implemented). Supersedes the `spotify:station:{track|artist}:{id}`
context-uri assumption used in the first cut of the context-menu radio actions.

## 1. Why the station-uri approach was wrong

The initial menu wiring assumed radio == "play the context uri `spotify:station:track:{id}`", relying on
`ContextResolve.IsInfinite` (`src/apps/Wavee/Backend/ContextResolver.cs:115`) recognising `:station:` so the tail
autoplays forever. That is **not** how Spotify (or WaveeMusic) starts an explicit radio:

- `spotify:station:track:{id}` + `/radio-apollo/v3/tracks/...` is the **autoplay-tail continuation** path
  (`src/apps/Wavee/SpotifyLive/LiveContextResolver.cs:186` `ResolveRadioApolloAsync`) — it is what refills a finished
  context, not what "Start radio" sets as the user-visible context.
- An explicit **"Start radio" / "Go to song radio" / "Go to artist radio"** resolves a **seed → a concrete radio
  playlist** (`spotify:playlist:{id}`), then plays that playlist as the new context.

## 2. The real mechanism — `inspiredby-mix/v2/seed_to_playlist`

Reference: WaveeMusic (`C:\WAVEE\WaveeMusic`).

- **Endpoint** (`src/Wavee/Core/Http/SpClient.cs:984` `GetInspiredByMixPlaylistAsync`):
  `GET https://spclient.wg.spotify.com/inspiredby-mix/v2/seed_to_playlist/{seedUri}?response-format=json`
  - `seedUri` is a literal `spotify:track:{id}` (song radio) or `spotify:artist:{id}` (artist radio). Colons are
    allowed unescaped in the path segment (RFC 3986), exactly as the radio-apollo route already does.
  - Bearer token + `Accept: application/json`.
- **Response JSON** (`src/Wavee/Core/Http/InspiredByMix/InspiredByMixModels.cs`):
  ```json
  { "total": 1, "mediaItems": [ { "uri": "spotify:playlist:37i9dQZF1E8..." } ] }
  ```
  Take `mediaItems[0].uri` → the radio **playlist uri**.
- **Failure handling** (mirror `SpClient.cs`): `404` → `null` (no radio available); `401` → token-refresh path;
  `>=500` → server error. `null`/empty `mediaItems` → no radio.
- The resolved playlist uri is then a **normal playlist context** — fully handled by the existing
  `/context-resolve/v1/{uri}` path (`LiveContextResolver.ResolveAsync`). Its natural end triggers the existing
  autoplay tail, so "endlessness" still works without any `:station:` special-casing at the "Start radio" boundary.

## 3. Desired UX — Apple-Music-style start (user requirement)

"Start radio" must **not** interrupt what is currently playing. Instead it sets the radio as the new context and lets
the current track finish first:

1. **Nothing playing** → resolve the radio playlist, play it immediately from index 0.
2. **A track is playing** → resolve the radio playlist, set it as the new context/up-next **without restarting audio**;
   when the current track ends, playback flows into the radio. Specifically:
   - If the currently-playing track **is** the radio's first track (the classic song-radio case — the seed track
     sits at `radio[0]`): let the current track play to completion, then **skip `radio[0]`** and continue at
     `radio[1]` (no double-play of the same song).
   - If the currently-playing track is **not** in the radio list (artist radio, or a station that doesn't lead with
     the seed): let the current track finish, then start at `radio[0]`.
3. Show a "Radio started" toast with an **"Open playlist"** action that navigates to the radio playlist page.

Reference implementation of exactly this behaviour: WaveeMusic
`PlaybackStateService.StartRadioAsync` (`src/Wavee.UI.WinUI/Data/Contexts/PlaybackStateService.cs:1723`) →
`PlaybackOrchestrator.SwitchToContextAfterCurrentAsync` (`src/Wavee/Audio/PlaybackOrchestrator.cs:933`). WaveeMusic
parks the queue cursor at the current track's index when it is in the radio context (MoveNext on track-end advances
past it) or at `-1` when it is not (MoveNext lands on `radio[0]`), and drops user-hidden tracks from the seed.

## 4. Mapping onto fluent-gpu's session model

fluent-gpu's `PlaybackSession` (`src/apps/Wavee/Backend/Playback.cs`) keeps a single invariant: `_cursor` points at the
**current** track (`SetContext` clamps `startIndex` to `[0, count-1]`, `_cur = _context[_cursor]`). A raw `-1` park is
not expressible there. Re-express WaveeMusic's two cases so the cursor still points at a real current track (so no
audio reload happens):

- **Current track present in radio at index `k`** → set context = `radioTracks`, cursor = `k`. `_cur` becomes that
  entry; its uri equals the still-playing track so the audio host is **not** reloaded. On track-end, `Next()` advances
  to `k+1` → the duplicate seed is naturally skipped.
- **Current track absent from radio** → set context = `[current] ++ radioTracks`, cursor = `0`. `_cur` stays the
  playing track (no reload); on track-end `Next()` yields `radioTracks[0]`.

This is the fluent-gpu-idiomatic equivalent of the `-1` park and preserves the "cursor == current" invariant the rest
of the reducer/snapshot code relies on. Hidden-track filtering should reuse whatever the context path already applies
(match `FilterHidden` semantics).

## 5. Architecture — how it rides the existing seams

This feature needs **no new transport, no new IPC verb, and no new caching engine** — every moving part maps onto a
seam that already exists. The four touch points:

### 5.1 Seed→playlist resolution rides `IContextResolver`

`IContextResolver` (`src/apps/Wavee/Backend/ContextResolver.cs:77`) is *already* the proto-free "opaque server thing →
tracks" seam, and it already carries the sibling algorithmic verbs `ResolveAutoplayAsync` / `ResolveAutopodcastAsync`.
Add the radio-seed verb here rather than inventing a parallel seam:

```csharp
// IContextResolver (Backend, proto-free)
Task<string?> ResolveRadioSeedAsync(string seedUri, CancellationToken ct = default);   // → spotify:playlist:{id} or null
```

- **Live impl** goes in `LiveContextResolver` (`src/apps/Wavee/SpotifyLive/LiveContextResolver.cs`) — it already holds the
  `ITransport` and the streaming-JSON house style. Model it on the existing `ResolveRadioApolloAsync` (line 186):
  `await _transport.Request(Channel.Spclient, $"/inspiredby-mix/v2/seed_to_playlist/{seedUri}?response-format=json", default, ct)`
  then read `mediaItems[0].uri` with a `Utf8JsonReader` (proto-free, no full-doc alloc — the same choice
  `ContextJson.Parse` makes). If you want SWR + in-flight dedup for repeat "Start radio" clicks, wrap it in a
  `Resource<string,string?>` exactly like the existing `_artistCache` (line 39) — but a plain GET is fine (the seed
  → playlist mapping is cheap and the heavy metadata is cached downstream by `MetadataService`).
- **Offline/fake impl**: `EmptyContextResolver` (`ContextResolver.cs:135`) returns `Task.FromResult<string?>(null)`
  → the UI shows a graceful "couldn't start radio". Tests inject a fake returning a canned playlist uri.
- **Wiring**: `LiveSessionHost` already installs `LiveContextResolver`; no extra registration beyond the new method.

### 5.2 `IPlaybackPlayer.StartRadioAsync` — exactly 4 implementers

Add `Task<string?> StartRadioAsync(string seedUri, string? displayName = null, CancellationToken ct = default)` to
`IPlaybackPlayer` (`src/apps/Wavee.Core/Playback/Playback.cs`). It **returns the resolved radio playlist uri** (or `null`
on failure) so the UI/action layer can raise the "Radio started → Open playlist" toast with navigation — the backend
controller is engine/UI-free and has no `go` callback, so the toast belongs at the caller (§7), not inside the
controller. The interface has exactly four backings today — grep `: IPlaybackPlayer`:
- `PlaybackController` (`src/apps/Wavee/Backend/PlaybackController.cs`) — the real one (§5.3).
- `SwitchablePlayer` (`src/apps/Wavee/Backend/Switchable.cs:68`) — delegates to its inner player (one-line forward).
- `UnsupportedPlaybackPlayer` (`src/apps/Wavee.Core/Playback/UnsupportedPlayback.cs:13`) — the idle/pre-login fake; no-op
  or device-prompt, matching how its other verbs behave.
- `RecordingPlayer` (`src/apps/Wavee.Tests/Actions/ActionsTestShims.cs`) — records the call for the action tests.

No AudioHost IPC change: `StartRadioAsync` is orchestration over the resolver + session; the out-of-process host only
ever sees the existing `Play`/`Stop` verbs (via `LoadAndPlayCurrentAsync`), and only in the nothing-playing branch.

### 5.3 `PlaybackController.StartRadioAsync` — reuse `LocalPlaySpecAsync` + a session park

```
StartRadioAsync(seed, name) -> playlistUri?:
  playlistUri = await _contexts.ResolveRadioSeedAsync(seed)          // §5.1
  if playlistUri is null: return null                               // caller shows "couldn't start radio"
  if _session.Current is null:
      await LocalPlaySpecAsync(ContextSpec.ForUri(playlistUri), ct)  // existing immediate path (controller:786)
  else:
      resolved = await _contexts.ResolveAsync(ContextSpec.ForUri(playlistUri), ct)   // same resolve the play path uses
      radio = resolved.Tracks
      _session.SwitchContextAfterCurrent(resolved.ContextUri ?? playlistUri, radio)  // §5.4 — NO host reload
      _projection.SetContextMetadata(resolved.Metadata)              // "Playing from …" line, mirrors SetQueueContext (:770)
      EmitSnap(_session.Snapshot(), EvKind.QueueChanged)             // publish up-next; same emit the queue verbs use
  return playlistUri                                                 // caller raises the Open-playlist toast (§7)
```

The crucial reuse: fluent-gpu already advances into the next context row on track-end through
`AudioHostSignalKind.Ended → AutoAdvanceAsync → LocalNextAsync → _session.Next()`
(`PlaybackController.cs:1406/1410/885`). Because §5.4 leaves `_cur` and the audio stream untouched, the current track
plays out and that **existing** Ended path is exactly what flows playback into the parked radio — we add no new
end-of-track logic. Do the mutation under the same `_lock` the other local verbs take (`LocalPlayTracksAsync:847`).

### 5.4 `PlaybackSession.SwitchContextAfterCurrent` — the one new primitive

`PlaybackSession` (`src/apps/Wavee/Backend/Playback.cs`) keeps the invariant "`_cursor` points at `_cur`" and `SetContext`
clamps `startIndex` to `[0,count-1]` (line 43), so a WaveeMusic-style `-1` park is not expressible. Add one primitive
that preserves the invariant (see §4):

```csharp
public void SwitchContextAfterCurrent(string uri, IReadOnlyList<QueuedTrack> radioTracks)
// current in radioTracks at k  → _context = radioTracks; _cursor = k   (Next() → k+1, skips the duplicate seed)
// current not in radioTracks   → _context = [current] ++ radioTracks; _cursor = 0  (Next() → radioTracks[0])
// _cur identity unchanged in BOTH → the audio host is not reloaded; only ContextUri + up-next change.
```

It reuses the existing `_context`/`_original`/`_userQueue` fields and the existing `Next()` semantics — it is a new
*entry point*, not a new queue model. Hidden-track filtering (if any) should match what the resolve path already
applies before handing tracks over.

## 6. Actions & UI wiring (deferred until §5 lands)

The reserved ids already exist (`src/apps/Wavee/Actions/ActionId.cs`: `GoToSongRadio`, `GoToArtistRadio`). When the player
surface exists:

- **`TrackActions.GoToSongRadio`** (single track): `Execute` awaits `StartRadioAsync(track.Uri, track.Title)` and
  raises the §7 toast off the returned playlist uri. Enabled for a single `spotify:track:*` target with a player
  present. Row in `Menus.Tracks` after the go-to cluster. Icon: a new `ActionIcons.Radio` → `Mdl.RadioTower`
  (`src/apps/Wavee/Design/Glyphs.cs:22`). Loc `menu.goToSongRadio`.
- **`ContainerActions.GoToArtistRadio`** (artist target): `StartRadioAsync(artistUri, artistName)` + §7 toast. Row in
  `Menus.Card`'s artist shape. Loc `menu.goToArtistRadio`.
- **Artist hero "Artist Radio" pill.** `ArtistPage.cs:215` currently passes plain `Play` as the `radio` callback to
  `Banner` (so the pill just replays the artist context — the bug). Wire it to a callback that awaits
  `svc.Player.StartRadioAsync(uri, a.Name)` and raises the §7 toast (it has `go` in scope as `Body(...)`'s parameter),
  and pass it as the 5th `Banner` arg. Pill markup is `ArtistPage.Hero.cs:66` / `ArtistRadioPill` (already uses
  `Mdl.RadioTower`).

## 7. "Radio started" infobar toast + navigate-to-seeded-playlist

Use the app's existing infobar-style toast — `Toasts.Show` (`src/apps/Wavee/Components/ToastHost.cs:21`), which already
carries an **action button** and auto-dismisses after 6s (`ToastHost.AutoDismissMs`). This is the exact
`(message, severity, actionLabel, onAction)` pattern `Menus.AddTo` uses for its "Added to playlist → Go to playlist"
toast, and it is the fluent-gpu equivalent of WaveeMusic's `ShowRadioStartedNotification`
(`PlaybackStateService.cs:1785`, "Radio has started" + "Open playlist").

Raise it at the **action layer** (which holds `c.S.Go`), keyed off the playlist uri `StartRadioAsync` returns:

```csharp
// inside TrackActions.GoToSongRadio / ContainerActions.GoToArtistRadio / the ArtistPage pill callback
var uri = await c.S.Svc.Player.StartRadioAsync(seedUri, displayName);   // returns the radio playlist uri
if (uri is null) { Toasts.Show(Loc.Get(Strings.Menu.RadioUnavailable), ToastSeverity.Caution); return; }
Toasts.Show(Loc.Get(Strings.Menu.RadioStarted), ToastSeverity.Success,
    actionLabel: Loc.Get(Strings.Menu.OpenRadioPlaylist),
    onAction: () => c.S.Go?.Invoke("pl:" + uri, displayName ?? Loc.Get(Strings.Menu.Radio)));
```

- The action label routes through the same `go("pl:" + uri, name)` scheme every other "open a playlist" affordance
  uses (`Menus.AddTo`, `ActionRules.RouteFor`), so it lands on the seeded radio playlist's detail page.
- The **notification center** (`NotificationCenterBridge`) is fed by durable sources (activity log / social /
  what's-new), not ad-hoc app events, so a one-off "radio started" belongs in the transient Toast host, not there.
- New loc keys under `menu`: `radioStarted` ("Radio started"), `openRadioPlaylist` ("Open playlist"),
  `radioUnavailable` ("Couldn't start radio"), and `radio` ("Radio", the fallback playlist title). Additive only.

## 8. Edge cases

- **No playlist returned** (404 / empty `mediaItems`): toast "Couldn't start radio", no context change.
- **Offline / fake session**: `IRadioSeedResolver` fake returns `null` → graceful no-op toast.
- **Remote device active**: "switch after current" is a local-session op; on a remote/viewer session fall back to the
  device-prompt / immediate `PlayContext` path (match how the queue verbs already behave with no local device).
- **Hidden tracks**: filter them out of the resolved radio tracks before parking (WaveeMusic does this so hidden
  tracks don't sneak in via the seed).
- **Endless tail**: the radio playlist ends normally → the existing autoplay/radio-apollo continuation keeps it going;
  no `:station:` handling needed at the start boundary.

## 9. Testing

- Seed→playlist parse: `mediaItems[0].uri` extraction, empty/absent `mediaItems` → `null`, 404 → `null` (drive the
  streaming parser directly, engine-free — same pattern as the `ContextJson`/context-resolve tests).
- Session park (`SwitchContextAfterCurrent`): current-in-radio → cursor at its index, `Next()` skips the duplicate;
  current-not-in-radio → prepended, `Next()` yields `radio[0]`; audio identity (`_cur`) unchanged in both.
- Controller: nothing-playing → immediate play; playing → no reload + correct up-next; `null` playlist → no context
  change + failure toast.

## 10. File-change checklist (for the implementation PR)

Seam / backend:
- `src/apps/Wavee/Backend/ContextResolver.cs` — add `IContextResolver.ResolveRadioSeedAsync` (the seam), and
  `EmptyContextResolver` returns `null` (offline/fake). No new interface — it rides the existing resolver.
- `src/apps/Wavee/SpotifyLive/LiveContextResolver.cs` — live `inspiredby-mix` GET + `Utf8JsonReader` parse of
  `mediaItems[0].uri` (model on `ResolveRadioApolloAsync`). No `LiveSessionHost` change (resolver already installed).
- `src/apps/Wavee.Core/Playback/Playback.cs` — `IPlaybackPlayer.StartRadioAsync`.
- `src/apps/Wavee/Backend/PlaybackController.cs` — `StartRadioAsync` = resolve seed → immediate (`LocalPlaySpecAsync`) or
  switch-after-current + `EmitSnap`/`SetContextMetadata` + toast. No new AudioHost IPC verb.
- `src/apps/Wavee/Backend/Playback.cs` — the `SwitchContextAfterCurrent` session primitive (§5.4).
- `src/apps/Wavee/Backend/Switchable.cs` — `SwitchablePlayer.StartRadioAsync` forwards to inner.
- `src/apps/Wavee.Core/Playback/UnsupportedPlayback.cs` — `UnsupportedPlaybackPlayer.StartRadioAsync` no-op/prompt.

Actions / UI (reserved `ActionId.GoToSongRadio` / `GoToArtistRadio` already exist):
- `src/apps/Wavee/Actions/{TrackActions,ContainerActions}.cs` — the two radio actions calling `Player.StartRadioAsync`.
- `src/apps/Wavee/Actions/{ActionIcons,Menus}.cs` — `ActionIcons.Radio` (`Mdl.RadioTower`) + the menu rows.
- `src/apps/Wavee/Features/Detail/ArtistPage.cs:215` — replace the `Play` radio callback with `StartRadioAsync(uri, name)`.
- `src/apps/Wavee/assets/loc/en-US.json` — `menu.goToSongRadio`, `menu.goToArtistRadio`, `menu.radioStarted`,
  `menu.openRadioPlaylist`, `menu.radioUnavailable`, `menu.radio` (additive only).

Tests:
- `src/apps/Wavee.Tests/` — the seed→playlist parser (engine-free), the `SwitchContextAfterCurrent` park (current-in vs
  current-out), the controller flow (immediate vs after-current vs null-playlist), and `RecordingPlayer.StartRadioAsync`
  recording for the action tests.
