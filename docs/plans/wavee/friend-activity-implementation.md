# Friend activity: the feed behind the panel (#277)

Status: implemented on `feat/friend-activity` (base `release/0.4.0`): feed + subscribe + push, no poll, no fallback.

## 1. What was wrong

The friends panel (`Panel.Friends`, drawn by `ClassicFriends` / `WinUiFriends` / `ZuneFriends`) was finished and read
`Edges.Friends` under the synthetic parent `Rail.Friends.Feed` plus the coarse `Rail.Friends.State`. Nothing wrote either:
`State` started `Offline`, so every theme said "Sign in to see what your friends are playing", signed in or not; the host
seams `Rail.Friends.SetActive` / `Refresh` were never assigned; and nothing sent or decoded a friend feed.

## 2. What the user needs, and what the official client does

The panel answers "what are my friends listening to right now?". That answer must be **live** (a change shows within
seconds), **true** (the real start time; nothing stale shown as current), **honest** (one clear state per situation:
"Sign in" only when signed out) and **free** (no work while nobody is looking; back off when Spotify asks).

The official desktop client (1.3.10.2586, two launches captured with the panel visible) does exactly this, and nothing
else, for the panel:

```
 app starts, dealer websocket connects
   │  ~3 s after start, ONCE
   ├── POST spclient /listening-activity/v1/feed  {"unused":true,"resultLimit":100}
   │◄── 200 {"entities":[{"followEntity":{ uri, activity }}]}       (or {"entities":[]}: no subscribe follows)
   │  +1.1 s, every user the feed returned
   ├── POST /listening-activity/v1/subscribe
   │     {"subscriptions":[{"username":"<friend>","pusher":{"connectionId":"<my dealer connection>"}}]}
   │◄── 200 { activities:[…], subscriptionExpirationTimestamp: now + 48 h }
   │  names: one extended-metadata batch (users 178/179, context playlists 205, tracks 10)
   │  from here on: NO more requests for the panel (130 s observed). Changes arrive as dealer pushes.
   ◄═══ dealer push: <friend> started a new track
```

It never calls `presence-view/v1/buddylist`, `init-friend-feed`, `/v1/users` or `user-profile-view` for the panel, and it
never polls. So neither does Wavee: no fallback source (the buddy list fails for the same reasons the feed does, and
answered 4 days-old rows where the feed answered 16 live ones), no timer.

The feed must go WITHOUT the default metrics/salt query (`product=0&country=…&salt=…`): the service answers 404 to a
request that carries them (observed 2026-11-22; the note stays in `Api.ListeningActivity`).

## 3. The wire, strictly

```
{ "entities": [ { "followEntity" | "userEntity": {
                    "uri": "spotify:user:…",
                    "activity": { "entityUri": "spotify:track:…", "contextUri": "spotify:playlist:…"?,
                                  "timestamp": "2026-11-22T18:04:11.123Z", "isPlaying": true? } } } ] }
```

| Field | Seen | Decoder |
|---|---|---|
| `followEntity` | the official client's capture | read |
| `userEntity` | a Wavee account, 2026-11-22 | read (same slot: one gRPC oneof) |
| `activity` nested in the user | both | read; a sibling `activity` is not read |
| `entityUri`, `contextUri`, `isPlaying` | both | read |
| `timestamp` | always ISO 8601 UTC with ms | one `DateTimeOffset` parse; an unreadable one skips the entity |
| `name` / `imageUrl` on the user | never | not read: names come from extended-metadata |

An entity in another shape is skipped and named in the log (`unknownEntity(<key>)`); an answer without an `entities`
array is a failed load (`feed failing status=200 unknown shape (n bytes)`).

`timestamp` is when the activity STARTED (18 s before the answer in the capture, with `isPlaying: true`). It is kept as
it is; `isPlaying` travels as `FriendEdge.Playing`. The run sorts playing first, then newest, then by user slot (stable).
The themes draw "now" and the equalizer from the flag (`bool live = f.Playing;`), never from the time.

## 4. Behaviour

| Event | The host does |
|---|---|
| Launch, signed in | `State` starts `Loading` (never "Sign in"); a launch with no stored credential reads `Offline` |
| The session changes (`WatchSession`) | the panel says what the profile chip says (`Shell.FoldAuth`): no stored credential → `Offline` ("Sign in"); a session that gave up (`Failed`) → the error face, rows kept; connecting again → `Loading` |
| Panel mounted (`SetActive(true)`) | load the feed |
| The window minimised, hidden or covered (`OnWindowHidden`, from the shell root's `InputHooks.WindowOccluded`) | nobody is looking: retries stop, and a connection, renewal or retry that comes now is owed, not loaded |
| The window seen again | one load if something was owed; else the words a push named meanwhile are ensured; nothing otherwise (bringing Wavee back to the front costs no request) |
| A new dealer connection (AnnounceDevice) | `Offline` → `Loading` (signed in again); a panel someone can see loads the feed, any other owes it |
| Retry (error face) | load the feed now: our own back-off gives way to the user. A server's Retry-After does not: the panel shows the skeleton and loads when it ends |
| Feed lands | replace the run; `Ensure` users, tracks, context playlists (Identity); then subscribe |
| Subscribe | every user of the run on this connection id; skipped when the standing subscription covers them on this connection and is more than 1 h from expiry |
| `hm://listening-activity/` push | that friend's row is replaced in place from the push (`Friends.ApplyPush`, §5) and the run re-sorts; no request. The new track's words are ensured now when someone can see the panel, else on the next look |
| Subscription near expiry | a timer at expiry − 1 h loads again (feed, then re-subscribe) while someone can see the panel; otherwise owed |
| Feed fails (429 / 5xx / transport / unknown shape) | rows kept, the error face with none; next try after Retry-After, else 15 s doubling to 15 min, and only while someone can see the panel |
| A track row lands | `Entities.RefillFriends` gives the friend's row its album and first artist (a tracked watch, posted) |
| Non-track activity (an episode) | skipped (the panel's row lane is a track lane) and named in the log |
| Signed out (`OnSignedOut`) | the run cleared, timers stopped, subscription forgotten, `Offline`; an answer from before it is dropped |

Always-on lines, on a change only: `[friends] feed landed rows= entries= dropped= skipped=[…] why=` per landed load (a
show, a connection, a Retry: a handful per session), `feed failing status= … retryIn=` once when it starts failing,
`feed recovered after n failed loads`, `subscribed users= expiresIn=`, `subscribe failed status=` once per status,
`push applied in place` once per session (later pushes are routine), `push not read: <reason>` once per distinct reason.

Requests: one feed per show, connection, renewal or Retry, one subscribe per connection, and NONE per push; against 240
an hour for a 15 s poll whether anything changed or not.

## 5. <a id="push"></a>The push

Wavee subscribes with its OWN dealer connection id (`Spotify.ConnectionId()`), so the pushes land on Wavee's socket. A
Debug run on 2026-11-25 (17 friends, panel open) logged them on **`hm://listening-activity/`**, about one a minute as
friends changed songs. The frames were captured on 2026-11-26 (a Wavee on a copy of a signed-in profile; eight pushes over
two runs, track changes and a pause):

```
{ "type": "message", "uri": "hm://listening-activity/", "headers": { "Content-Type": "application/json" },
  "payloads": [ { "username": "<bare id>", "accountId": "<10 chars>",
                  "activity": { "uri": "spotify:track:…", "isPlaying": true,
                                "timestamp": 1795719468690, "contextUri": "spotify:playlist:…" } } ] }
```

- The payload is a JSON OBJECT. `DealerFrame.ReadPayloads` decodes base64 strings only and skips objects (the
  social-connect rule), so `Friends.OnDealerPush` takes the RAW frame (`Spotify.Connect.cs`) and reads it itself.
- It is spelled unlike the feed: the track is `uri` (the feed's `entityUri`) and `timestamp` is unix ms as a NUMBER (the
  feed's is an ISO string). `Decode.ListeningPush` reads exactly this, strictly; anything else is a logged skip.
- A pause pushes the same track and start time with `isPlaying:false`; the next song pushes `isPlaying:true` and a new
  start time. So the row's Playing flag is corrected by the push, never left stale.
- The topic is the bare `hm://listening-activity/`; the user is in the body. Subscribing to the account's own username
  brought no push in 30 min of its own playback: only friends' changes arrive.

`Friends.ApplyPush` (UI thread) stages each activity as the feed does (thin user, track and playlist rows) and commits it
with `Staging.FriendsPushed`: `Entities.CommitFriends` keeps the run and replaces that user's row (a user it lacks is
added), then re-sorts playing first, then newest. The push's bare username is matched to the run's own `spotify:user:` uri
(the feed may escape it). No feed request follows a push.

## 6. Files

| File | What |
|---|---|
| `Entities/Friends.cs` (new) | `StagedFriend` (user, track, context, start, playing), `Staging.Friends` / `FriendsFeedAnswered` / `FriendsPushed`, `Entities.CommitFriends` (replace, or upsert for a push), `RefillFriends` |
| `Entities/Edges.cs` | `FriendEdge.Playing` (defaulted: additive) |
| `Entities/Entities.cs` | `CommitFriends(staging)` after `CommitRecents` |
| `Entities/Fetch.Routes.cs` | the dead `SpclientRoute.FriendFeed` and its `FetchEdge.Friends` mapping removed (the host fetches, not the edge layer) |
| `Spotify/Spotify.Decode.Friends.cs` (new) | `ListeningFeed` (strict), `ListeningSubscription` (the expiry), `ListeningPush` (the dealer frame, strict) |
| `Spotify/Spotify.Api.cs` | `ListeningActivity(limit)`, `ListeningSubscribe(connectionId, usernames)` (the v2 init call is gone) |
| `Spotify/Spotify.Friends.cs` (new) | the host: seams, feed, subscribe, back-off, ensure, refill watch, sign-out |
| `Spotify/Spotify.Session.cs` | `Friends.OnConnection()` on AnnounceDevice, `Friends.OnSignedOut()` on SignedOut |
| `Spotify/Spotify.Connect.cs` | a dealer MESSAGE nobody else took goes to `Friends.OnDealerPush` with its raw frame (§5) |
| `Screens/Diagnostics.Inspect.Friends.cs` (new) | the inspector's `friends` verb: read the run; `push=` lands a frame in the captured shape, `raw=` a captured frame file, through `ApplyPush` |
| `Shell/Rail.cs` | `State` starts `Loading`; `LiveWindowMs` / `IsLive` removed |
| `Shell/Rail.Wiring.cs` | `Spotify.Friends.Attach()` in the panel's props builder (app and headless inspector alike) |
| `Shell/Shell.UI.cs` | the shell root hands `InputHooks.WindowOccluded` to `Spotify.Friends.OnWindowHidden` |
| `Themes/*/Structure/Views/Panel/*Friends.cs` | `bool live = f.Playing;` |
| `Entities/Entities.Fake.Friends.cs` (new) | the `--fake` feed in the wire's shape (`followEntity`, no names) through the real decoder; seeds the two friends no other surface names |

## 7. Verification

- Debug build clean (`TreatWarningsAsErrors`) against the engine pin (`engine.lock`).
- Inspector (`--fake`): WinUI (`click key=winui.titlebar.friends`), Classic (`click key=chrome-friends`) and Zune at Huge
  (`click key=queue`, `click key=zune-panel-pivot:Friends`) render the five friends, Christos and Sam playing (the
  equalizer) above Alex 12 min, Mia 3 hr, Jun 2 d; names from the seeded user rows, album / artist off the track rows.
  `lint` has no error; `trace 40 prefix=fr:` shows no snaps, jumps or remounts.
- Inspector pushes (WinUI, Classic, Zune): `friends push=jun-park track=spotify:track:tr3 trace=60 prefix=fr:` moves Jun
  (2 d) to the top, playing; `friends push=christos track=spotify:track:tr4 playing=false agoMs=30000` keeps Christos'
  start time and drops him below the still-playing Sam; `friends raw=<a captured pause frame>` lands that friend paused at
  their real start time. Each trace: no snaps, no remounts; `lint` clean.

## 8. Not in this change

The listening-activity audience (`/listening-activity/v1/audience`) and the "share my listening
activity" privacy setting (`/profile-privacy/v2/read-settings`); episodes in the panel.
