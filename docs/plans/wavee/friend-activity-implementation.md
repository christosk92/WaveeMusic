# Friend activity: the feed behind the panel (#277)

Status: implemented 2026-11-22 on `feat/friend-activity` (base `release/0.3.13`).

## 1. What was wrong

The friends panel (`Panel.Friends`, drawn by `ClassicFriends` / `WinUiFriends` / `ZuneFriends`) was finished and read
`Edges.Friends` under the synthetic parent `Rail.Friends.Feed` plus the coarse `Rail.Friends.State`. Nothing wrote either:
`State` started `Offline`, so every theme said "Sign in to see what your friends are playing", signed in or not; the host
seams `Rail.Friends.SetActive` / `Refresh` were never assigned; and nothing sent or decoded a friend feed.

## 2. The wire, and what did not work

| Request | Result on a real account (2026-11-22) |
|---|---|
| `GET /presence-view/v2/init-friend-feed/<connection id>` (0.2.9's seed, + `hm://presence2/user/<id>` deltas) | 4 entries, days old |
| `GET /presence-view/v1/buddylist` | the same 4 entries |
| **`POST /listening-activity/v1/feed`** body `{"unused":true,"resultLimit":100}` | **16 entries, live** |

The listening-activity feed is what the desktop client's panel reads (spotifast reads it through its librespot fork,
`get_listening_activity`, MIT). It must go WITHOUT the default metrics/salt query (`product=0&country=…&salt=…`): the
service answers 404 to a request that carries them. Its answer, as captured:

```
{ "entities": [ { "userEntity": { "uri": "spotify:user:…",
                                  "activity": { "entityUri": "spotify:track:…", "contextUri": "spotify:playlist:…",
                                                "isPlaying": true, "timestamp": "<ISO 8601>" } } } ] }
```

The activity is nested INSIDE the user (spotifast's parser reads it as a sibling; the decoder accepts both). The answer
names no titles: the rows are staged by identity and their words come through the fetch layer.

## 3. Behaviour

| Event | The host does |
|---|---|
| Panel shown (`SetActive(true)`) | load now, then every 15 s while it stays open (only then) |
| Retry (error face) / a new dealer connection with the panel open | load |
| Feed fails | fall back to the buddy list (it carries its own words); with neither, rows stay or the error face shows |
| Feed lands | replace the run, newest first, one row per user; `Ensure` users (Identity), tracks (Identity), context playlists (Identity) |
| A track row lands | `Entities.RefillFriends` gives the friend's row its album and first artist (a tracked watch, posted) |
| `isPlaying` | the row's time is "now": it sorts first and shows live, as the desktop client draws it |
| Non-track activity (an episode) | skipped (the panel's row lane is a track lane) and named in the log |
| Signed out | the run cleared, `Offline`; an answer from before the sign-out is dropped |

One always-on line per load that changed something: `[friends] feed landed rows= entries= dropped= skipped=[…] source= why=`
(counts, statuses and key names only; the first skipped entity's key tree when one is skipped).

## 4. Files

| File | What |
|---|---|
| `Entities/Friends.cs` (new) | `StagedFriend`, `Staging.Friends` / `FriendsFeedAnswered`, `Entities.CommitFriends`, `RefillFriends` |
| `Entities/Entities.cs` | `CommitFriends(staging)` after `CommitRecents` |
| `Spotify/Spotify.Decode.Friends.cs` (new) | `ListeningFeed`, `FriendFeed` (buddy list), the log's `KeyTree` / `FriendFeedShape` |
| `Spotify/Spotify.Api.cs` | `ListeningActivity(limit)`, `FriendBuddylist()` (the v2 init call is gone) |
| `Spotify/Spotify.Friends.cs` (new) | the host: seams, poll, fallback, ensure, refill watch, sign-out |
| `Spotify/Spotify.Session.cs` | `Friends.OnConnection()` on AnnounceDevice, `Friends.OnSignedOut()` on SignedOut |
| `Shell/Rail.Wiring.cs` | `Spotify.Friends.Attach()` in the panel's props builder (app and headless inspector alike) |
| `Entities/Entities.Fake.Friends.cs` (new) | the `--fake` feed in the wire's own shape, through the real decoder |

No theme or contract change: the views already read everything. `contract: none`.

## 5. Verification

- Debug build clean; the inspector (`--fake`) renders five friends in WinUI, Classic and Zune, two live, album / artist
  read off the seeded track rows; a row click opens its context playlist.
- Signed in (Release build): `feed landed rows=16 entries=16 dropped=0 source=listening-activity`.

## 6. Not in this change

The listening-activity audience (`/listening-activity/v1/audience`) and the "share my listening activity" privacy setting
(`/profile-privacy/v2/read-settings`); episodes in the panel.
