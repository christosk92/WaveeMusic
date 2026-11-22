# Friend activity: the feed behind the panel

Status: plan, 2026-11-22. Branch `feat/friend-activity` off `release/0.3.13`.

## 1. What is wrong today

The friends panel (`Panel.Friends`, drawn by `ClassicFriends` / `WinUiFriends` / `ZuneFriends`) is finished and reads
`Edges.Friends` under the synthetic parent `Rail.Friends.Feed` plus the coarse `Rail.Friends.State`. Nothing ever
writes either:

- `Rail.Friends.State` starts `Offline` and nobody moves it, so every theme shows "Sign in to see what your friends are
  playing", signed in or not.
- `Rail.Friends.SetActive` / `Rail.Friends.Refresh` (the host seams `PanelHost.SetFriendsActive` and the error face's
  Retry call) are never assigned.
- `Spotify.Api.FriendFeed(connectionId)` exists and `FetchEdge.Friends` routes to `SpclientRoute.FriendFeed`, but no
  caller sends it, no decoder reads the answer, and `Edges.Friends` has no staged form or commit.
- `hm://presence2/user/<id>` dealer pushes arrive and fall through `Connect.OnDealerMessage`'s "somebody else's frame".

0.2.9 had the whole thing (`SpotifyFriendActivityService.cs`, deleted in 99e398d79); this plan ports its behaviour onto
the 0.3 tables.

## 2. The wire (from 0.2.9 and the 2026-09 captures)

```
seed   GET  spclient /presence-view/v2/init-friend-feed/<dealer connection id>      Accept: application/json
       200  { "friends": [ { "timestamp": 1732270000000,
                             "user":  { "uri": "spotify:user:x", "name": "X", "imageUrl": "https://…" },
                             "track": { "uri": "spotify:track:…", "name": "…", "imageUrl": "https://…",
                                        "album":   { "uri": "spotify:album:…",  "name": "…" },
                                        "artist":  { "uri": "spotify:artist:…", "name": "…" },
                                        "context": { "uri": "spotify:playlist:…", "name": "…", "index": 0 } } } ] }
       404  an account that follows nobody: an EMPTY feed, not a failure
delta  dealer MESSAGE  hm://presence2/user/<userId>          (payload-less: it only says "this friend changed")
       GET  spclient /presence-view/v1/user/<userId>  → ONE friend object (same shape as an entry above)
       404 / 403 → the friend is no longer visible: drop the row
```

`timestamp` may be a number or a numeric string. An entry without `user.uri` or `track.uri` is skipped. `imageUrl`
may be spelled `image_url`.

## 3. Behaviour (0.2.9's, kept)

| Event | Feed does |
|---|---|
| Panel shown (`SetActive(true)`) with a connection id, state Idle / Offline / Error | seed |
| A NEW dealer connection id (`SessionEffects.AnnounceDevice`) while signed in | seed (a fresh connection invalidates the old feed) |
| Signed out / no connection id | `Offline`, rows cleared (a different account must not see the last one's friends) |
| `hm://presence2/user/<id>` push | fetch that one friend; a newer push for the same id cancels the older fetch |
| Panel visible and no push for 30 min | re-seed (watchdog) |
| Error face's Retry | seed |
| Seed answers while a newer seed is in flight | dropped (generation = the connection id it was sent for) |
| Seed fails with rows held | rows stay (`SurfaceFor`: rows win), state `Error` only when empty |

Rows are sorted newest first. `--fake` seeds the same JSON through the real decoder, so the inspector exercises it.

## 4. Files

| File | New / edit | What |
|---|---|---|
| `Entities/Friends.cs` | new (CORE) | `StagedFriend`, `Staging.Friends`, `Entities.CommitFriends` (replace / upsert / remove) |
| `Entities/Entities.cs` | edit | `CommitFriends(staging);` after `CommitRecents`, plus the partial declaration |
| `Spotify/Spotify.Decode.Friends.cs` | new | `Decode.FriendFeed(json, s)`, `Decode.FriendOne(json, s)` |
| `Spotify/Spotify.Api.cs` | edit | `Api.FriendPresence(userId)` |
| `Spotify/Spotify.Friends.cs` | new | the host: seed, deltas, watchdog, state, the `Rail.Friends` seams |
| `Spotify/Spotify.Connect.cs` | edit | `|| Friends.OnDealerPush(message.Uri)` in the `handledLocally` chain |
| `Spotify/Spotify.Session.cs` | edit | `Friends.OnConnection()` beside `Connect.AnnounceDevice()`; `Friends.OnSignedOut()` on the sign-out effect |
| `Entities/Entities.Fake.Friends.cs` | new | the `--fake` feed as wire JSON |
| `Shell/Rail.cs` | edit | the doc comment (no longer "no host") |

No theme or contract file changes: the views already read everything below. `contract: none`.

## 5. Code

### 5.1 `Entities/Friends.cs`

```csharp
// ── Entities/Friends.cs — the friend-activity relation's staged form and its commit ────────────────────────────────
//
// `Edges.Friends` is ONE run under the synthetic parent `Rail.Friends.Feed`. A worker cannot resolve slots (C1), so a
// decoded friend crosses as five identities and a time; the commit resolves them and rewrites the run. A seed REPLACES
// the run; a presence delta UPSERTS or REMOVES one user's row. Session-only: never persisted (Store.cs §6).

namespace Wavee;

/// <summary>What a staged friends batch does to the run.</summary>
public enum FriendsOp : byte { None, Replace, Upsert, Remove }

/// <summary>One decoded friend: identities as the wire gave them (the commit resolves them), newest-first order is the commit's.</summary>
public struct StagedFriend
{
    public StagedId User, Track, Album, Artist, Context;
    public long TimestampMs;
}

public sealed partial class Staging
{
    StagedList<StagedFriend>? _friends;
    /// <inheritdoc cref="StagedFriend"/>
    public StagedList<StagedFriend> Friends => _friends ??= Register(new StagedList<StagedFriend>());
    internal StagedList<StagedFriend>? StagedFriends => _friends;

    /// <summary>What the batch does to the run; <see cref="FriendsOp.None"/> lands nothing.</summary>
    public FriendsOp FriendsOp;
    /// <summary>The user a <see cref="FriendsOp.Remove"/> drops (and an Upsert's row replaces): uri text in the arena.</summary>
    public StagedId FriendsUser;
}

public static partial class Entities
{
    // UI thread only (C1); grown to the widest feed seen (P8).
    static int[] s_friendTargets = new int[32];
    static FriendEdge[] s_friendPayload = new FriendEdge[32];

    static partial void CommitFriends(Staging s)
    {
        if (s.FriendsOp == FriendsOp.None) return;
        var staged = s.StagedFriends is { } f ? f.Span : default;
        var table = Current.Edges.Friends;
        var held = table.Payload(Rail.Friends.Feed);

        int n = 0;
        Grow(held.Length + staged.Length);
        int dropUser = s.FriendsUser.IsEmpty ? Table.None : s.Slot(Current.Users, in s.FriendsUser);

        // Keep the held rows an Upsert / Remove does not touch; a Replace keeps none.
        if (s.FriendsOp != FriendsOp.Replace)
            foreach (ref readonly var e in held)
                if (e.UserSlot != dropUser) s_friendPayload[n++] = e;

        foreach (ref readonly var x in staged)
        {
            int user = s.Slot(Current.Users, in x.User);
            int track = s.Slot(Current.Tracks, in x.Track);
            if (user == Table.None || track == Table.None) continue;
            int album = x.Album.IsEmpty ? 0 : s.Slot(Current.Albums, in x.Album);
            int artist = x.Artist.IsEmpty ? 0 : s.Slot(Current.Artists, in x.Artist);
            // The context slot is a PLAYLIST slot (Rail.Friends.TargetOf); an album or artist context falls back to
            // the album / artist the row already names.
            int context = x.Context.IsEmpty || x.Context.Kind(s) != EntityKind.Playlist ? 0 : s.Slot(Current.Playlists, in x.Context);
            s_friendPayload[n++] = new FriendEdge(user, x.TimestampMs, track, album, artist, context);
        }

        var rows = s_friendPayload.AsSpan(0, n);
        rows.Sort(static (a, b) => b.TimestampMs.CompareTo(a.TimestampMs));
        for (int i = 0; i < n; i++) s_friendTargets[i] = rows[i].UserSlot;
        table.Replace(Rail.Friends.Feed, s_friendTargets.AsSpan(0, n), rows, EdgeState.Complete, n);
    }

    static void Grow(int n)
    {
        if (n <= s_friendTargets.Length) return;
        int size = Math.Max(n, s_friendTargets.Length * 2);
        s_friendTargets = new int[size];
        s_friendPayload = new FriendEdge[size];
    }
}
```

(The sort's lambda is static and the comparison allocates nothing; `held` is read before `Replace` rewrites it, and
`s_friendPayload` is a copy, so the overlap is safe.)

### 5.2 `Spotify/Spotify.Decode.Friends.cs`

```csharp
public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary><c>init-friend-feed</c>'s <c>{ "friends": [...] }</c> → a REPLACE batch. An unreadable body stages
        /// nothing and answers false (the host keeps what it held).</summary>
        public static bool FriendFeed(ReadOnlySpan<byte> json, Staging s)
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
            s.FriendsOp = FriendsOp.Replace;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("friends"u8)) { SkipValue(ref r); continue; }
                if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return true;
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    Friend(ref r, s);
            }
            return true;
        }

        /// <summary><c>presence-view/v1/user/&lt;id&gt;</c>'s one friend → an UPSERT batch for <paramref name="userUri"/>
        /// (a body with no usable friend becomes a REMOVE).</summary>
        public static void FriendOne(ReadOnlySpan<byte> json, ReadOnlySpan<byte> userUri, Staging s)
        {
            s.FriendsUser = new StagedId(s.AddText(userUri));
            var r = new Utf8JsonReader(json);
            int before = s.Friends.Count;
            if (r.Read() && r.TokenType == JsonTokenType.StartObject) Friend(ref r, s);
            s.FriendsOp = s.Friends.Count > before ? FriendsOp.Upsert : FriendsOp.Remove;
        }

        /// <summary>One friend object (the reader sits on its StartObject) → the thin rows its words fill and one
        /// <see cref="StagedFriend"/>; an entry without <c>user.uri</c> or <c>track.uri</c> stages nothing.</summary>
        static void Friend(ref Utf8JsonReader r, Staging s)
        {
            if (r.TokenType != JsonTokenType.StartObject) { SkipValue(ref r); return; }
            TextRef userUri = default, userName = default, userImage = default;
            TextRef trackUri = default, trackName = default, trackImage = default;
            TextRef albumUri = default, albumName = default, artistUri = default, artistName = default;
            TextRef contextUri = default, contextName = default;
            long ts = 0;
            for (int o = r.CurrentDepth; Next(ref r, o);)
            {
                if (r.ValueTextEquals("timestamp"u8)) ts = Int64Loose(ref r);
                else if (r.ValueTextEquals("user"u8)) Named(ref r, s, out userUri, out userName, out userImage);
                else if (r.ValueTextEquals("track"u8))
                {
                    for (int t = Fields(ref r); t >= 0 && Next(ref r, t);)
                    {
                        if (r.ValueTextEquals("uri"u8)) trackUri = Text(ref r, s);
                        else if (r.ValueTextEquals("name"u8)) trackName = Text(ref r, s);
                        else if (r.ValueTextEquals("imageUrl"u8) || r.ValueTextEquals("image_url"u8)) trackImage = Text(ref r, s);
                        else if (r.ValueTextEquals("album"u8)) Named(ref r, s, out albumUri, out albumName, out _);
                        else if (r.ValueTextEquals("artist"u8)) Named(ref r, s, out artistUri, out artistName, out _);
                        else if (r.ValueTextEquals("context"u8)) Named(ref r, s, out contextUri, out contextName, out _);
                        else SkipValue(ref r);
                    }
                }
                else SkipValue(ref r);
            }
            if (userUri.IsEmpty || trackUri.IsEmpty) return;

            var user = new StagedId(userUri);
            ref var u = ref s.Users.RowFor(user, Authority.Thin, (uint)UserFields.Identity);
            u.Name = userName;
            u.Image = userImage;

            var album = albumUri.IsEmpty ? default : new StagedId(albumUri);
            if (!album.IsEmpty && !albumName.IsEmpty)
                s.Albums.RowFor(album, Authority.Thin, (uint)AlbumFields.Title).Title = albumName;
            var artist = artistUri.IsEmpty ? default : new StagedId(artistUri);
            if (!artist.IsEmpty && !artistName.IsEmpty)
                s.Artists.RowFor(artist, Authority.Thin, (uint)ArtistFields.Name).Name = artistName;
            var context = contextUri.IsEmpty ? default : new StagedId(contextUri);
            if (!context.IsEmpty && !contextName.IsEmpty && context.Kind(s) == EntityKind.Playlist)
                s.Playlists.RowFor(context, Authority.Thin, (uint)PlaylistFields.Title).Title = contextName;

            var track = new StagedId(trackUri);
            ref var t2 = ref s.Tracks.RowFor(track, Authority.Thin, (uint)(TrackFields.Title | TrackFields.Image));
            t2.Title = trackName;
            t2.Image = trackImage;
            t2.AlbumUri = album;

            ref var f = ref s.Friends.Add();
            f = new StagedFriend { User = user, Track = track, Album = album, Artist = artist, Context = context, TimestampMs = ts };
        }
    }
}
```

`Named` reads `{uri, name, imageUrl|image_url}`; `Int64Loose` takes a number or a numeric string. Both are a few lines
beside the decoder; `Next` / `Fields` / `SkipValue` / `Text` are the shared JSON walkers the other decoders use. The
exact field-group names (`TrackFields.Title`, `ArtistFields.Name`, …) are checked against the enums at implementation;
where a group is wider than one word, the thin row claims only what it writes, as the home cards do.

### 5.3 `Spotify/Spotify.Api.cs`

```csharp
/// <summary>One friend's presence (a <c>hm://presence2/user/&lt;id&gt;</c> push says it changed). 404 / 403 = that
/// friend is no longer visible.</summary>
public static Result FriendPresence(string userId, CancellationToken ct)
{
    Span<char> path = stackalloc char[256];
    var w = new PathWriter(path);
    w.Append("/presence-view/v1/user/");
    w.AppendEscaped(userId);
    return Get(w.Written, ApiHost.Spclient, CommonJson, ct);
}
```

### 5.4 `Spotify/Spotify.Friends.cs` (the host)

```csharp
public static partial class Spotify
{
    /// <summary>The friend-activity feed's host (G6): fills <c>Edges.Friends</c> and <c>Rail.Friends.State</c>, and
    /// assigns the panel's two seams. UI thread except the bodies passed to <c>Api.Run</c>.</summary>
    public static class Friends
    {
        const string PresencePrefix = "hm://presence2/user/";
        const long WatchdogMs = 30 * 60_000L;

        static bool s_active;
        static string s_seedFor = "";          // the connection id of the newest seed (stale answers are dropped)
        static long s_lastHeardMs;             // the newest seed or push (the watchdog's window)
        static readonly Dictionary<string, int> s_userGen = new(StringComparer.Ordinal);   // coalesces per-user fetches
        static int s_gen;

        /// <summary>Boot: assign the panel seams. Called once beside the other hosts' wiring.</summary>
        public static void Attach()
        {
            Rail.Friends.SetActive = SetActive;
            Rail.Friends.Refresh = static () => Seed("retry");
            if (Platform.Args.Fake) { LandFake(); return; }
            Rail.Friends.State.Value = Rail.Friends.FeedState.Offline;
        }

        static void SetActive(bool active)
        {
            s_active = active;
            if (!active) return;
            if (Rail.Friends.State.Peek() is Rail.Friends.FeedState.Idle or Rail.Friends.FeedState.Offline or Rail.Friends.FeedState.Error)
                Seed("shown");
            else if (Playback.FrameNowMs() - s_lastHeardMs >= WatchdogMs) Seed("watchdog");
        }

        /// <summary>A NEW dealer connection id (Session's AnnounceDevice effect): the old feed is tied to the old id.</summary>
        public static void OnConnection() { if (s_active || Current.Edges.Friends.Count(Rail.Friends.Feed) > 0) Seed("connection"); }

        /// <summary>Signed out: no feed, and no stale rows for the next account.</summary>
        public static void OnSignedOut()
        {
            s_seedFor = "";
            s_userGen.Clear();
            Entities.Current.Edges.Friends.Clear(Rail.Friends.Feed);
            Rail.Friends.State.Value = Rail.Friends.FeedState.Offline;
        }

        /// <summary>Dealer thread hands every unread MESSAGE here first; true = it was a presence push (taken).</summary>
        public static bool OnDealerPush(ReadOnlySpan<byte> uri)
        {
            if (!uri.StartsWith("hm://presence2/user/"u8)) return false;
            string userId = Encoding.UTF8.GetString(uri["hm://presence2/user/"u8.Length..]);
            if (userId.Length > 0) Post(() => FetchUser(userId));
            return true;
        }

        static void Seed(string why)
        {
            if (Platform.Args.Fake) { LandFake(); return; }
            string conn = Session.ConnectionId();
            if (conn.Length == 0 || !Current.IsOnline) { Rail.Friends.State.Value = Rail.Friends.FeedState.Offline; return; }
            s_seedFor = conn;
            s_lastHeardMs = Playback.FrameNowMs();
            s_userGen.Clear();                       // a fresh seed supersedes the pending per-user answers
            if (Entities.Current.Edges.Friends.Count(Rail.Friends.Feed) == 0) Rail.Friends.State.Value = Rail.Friends.FeedState.Loading;
            uint epoch = Entities.Current.Epoch;
            bool queued = Api.Run(() =>
            {
                var s = Staging.Rent();
                s.Epoch = epoch;
                int status = 0;
                bool ok = false;
                try
                {
                    var answer = Api.FriendFeed(conn, CancellationToken.None);
                    status = answer.Status;
                    if (answer.Ok) ok = Decode.FriendFeed(answer.Bytes, s);
                    else if (answer.Status == 404) { s.FriendsOp = FriendsOp.Replace; ok = true; }   // follows nobody: empty
                }
                catch (Exception ex) { Log.Warn("friends", "seed failed", ex); }
                Post(() => LandSeed(conn, s, ok, status, why));
            });
            if (!queued) LandFailure();
        }

        static void LandSeed(string conn, Staging s, bool ok, int status, string why)
        {
            bool owned = false;
            try
            {
                if (!string.Equals(conn, s_seedFor, StringComparison.Ordinal)) return;   // a newer seed went out
                if (!ok) { Log.Warn("friends", "seed answered " + status); LandFailure(); return; }
                Entities.Commit(s);
                Entities.Publish();
                int n = Entities.Current.Edges.Friends.Count(Rail.Friends.Feed);
                Rail.Friends.State.Value = Rail.Friends.FeedState.Ready;
                Log.Info("friends", "seed landed rows=" + n + " why=" + why);
            }
            finally { if (!owned) Staging.Return(s); }
        }

        static void LandFailure()
        {
            if (Entities.Current.Edges.Friends.Count(Rail.Friends.Feed) == 0) Rail.Friends.State.Value = Rail.Friends.FeedState.Error;
        }

        static void FetchUser(string userId)
        {
            if (s_seedFor.Length == 0) return;      // no feed yet: the seed will carry this friend
            s_lastHeardMs = Playback.FrameNowMs();
            int gen = ++s_gen;
            s_userGen[userId] = gen;
            uint epoch = Entities.Current.Epoch;
            byte[] userUri = Encoding.UTF8.GetBytes("spotify:user:" + userId);
            Api.Run(() =>
            {
                var s = Staging.Rent();
                s.Epoch = epoch;
                try
                {
                    var answer = Api.FriendPresence(userId, CancellationToken.None);
                    if (answer.Ok) Decode.FriendOne(answer.Bytes, userUri, s);
                    else if (answer.Status is 404 or 403) { s.FriendsUser = new StagedId(s.AddText(userUri)); s.FriendsOp = FriendsOp.Remove; }
                }
                catch (Exception ex) { Log.Info("friends", "presence fetch failed: " + ex.Message); }
                Post(() =>
                {
                    try
                    {
                        if (!s_userGen.TryGetValue(userId, out int held) || held != gen) return;   // superseded
                        s_userGen.Remove(userId);
                        if (s.FriendsOp == FriendsOp.None) return;
                        Entities.Commit(s);
                        Entities.Publish();
                        Rail.Friends.State.Value = Rail.Friends.FeedState.Ready;
                    }
                    finally { Staging.Return(s); }
                });
            });
        }

        static void LandFake() { /* Entities.Fake.Friends.cs: the wire JSON through Decode.FriendFeed, committed */ }
    }
}
```

The watchdog rides `SetActive` and the panel's own 30 s tick (`UseInterval(..., Rail.Friends.TickMs)` already re-renders
the rows for the relative times): the host exposes `Friends.Tick()` that re-seeds when `WatchdogMs` passed, and the
`FriendsProps.SetActive` seam calls it on show. No timer thread.

### 5.5 Edits

```csharp
// Spotify.Connect.cs, OnDealerMessage's fall-through:
bool handledLocally = Playback.OnSpeedSettings(message.Uri, …)
    || Telemetry.OnDealerProgress(message.Uri, …)
    || Friends.OnDealerPush(message.Uri);

// Spotify.Session.cs, the effect sink:
if ((fx & SessionEffects.AnnounceDevice) != 0) { Connect.AnnounceDevice(); Friends.OnConnection(); }
// … and on SessionEffects.SignedOut: Friends.OnSignedOut();

// Entities.cs, Commit(): after CommitRecents(staging);
CommitFriends(staging);
// and beside the other partial declarations:
static partial void CommitFriends(Staging s);
```

### 5.6 `Entities/Entities.Fake.Friends.cs`

Five friends in the wire's own JSON (two live, three older), naming fake-catalogue tracks (`spotify:track:tr3` …) and
playlists so the rows link and open. `LandFake` stamps their timestamps relative to `Platform.Clock.SeedEpoch`, decodes
through `Decode.FriendFeed`, commits, and sets `Ready`.

## 6. The panel, unchanged (WinUI shown; Classic and Zune draw the same rows)

```
┌ Friend activity ───────────────── ✕ ┐
│ (◉) Christos          ▮▮▮  Now      │   live: accent track line + equalizer
│     Strobe · Nova Kite              │
│     Late night drive                │   context (playlist), else the album
│ ( ) Alex Rivers            12 min   │
│     Dalkom Cafe · Mia Solace        │
│     Northern Static                 │
│ ( ) Mia Solace             3 hr     │
│     …                               │
└─────────────────────────────────────┘
```

## 7. Verification

- Debug build clean.
- Inspector, `--fake` (the server's default): open the friends panel in each theme
  (`mount theme=winui|classic|zune`, the title bar's friends button), `render`, look: five rows, two live.
- `lint` on the open panel; `trace` the open.
- The real path (signed-in `dotnet run`): the startup log shows `friends seed landed rows=N why=shown` after opening
  the panel; a friend changing song produces a presence push and the row moves to the top.

## 8. Not in this change

The listening-activity audience (`/listening-activity/v1/audience`) and the "share my listening activity" privacy
setting (`/profile-privacy/v2/read-settings`) are separate surfaces; a follow-up if wanted.
