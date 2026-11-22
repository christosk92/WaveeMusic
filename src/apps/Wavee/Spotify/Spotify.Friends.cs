// ── Spotify/Spotify.Friends.cs — the friend-activity feed's host (#277) ─────────────────────────────────────────────
//
// Fills `Edges.Friends` (under `Rail.Friends.Feed`) and `Rail.Friends.State`, and assigns the panel's two seams
// (`Rail.Friends.SetActive`, `Rail.Friends.Refresh`). The behaviour is 0.2.9's `SpotifyFriendActivityService`:
//
//   seed     GET /presence-view/v2/init-friend-feed/<dealer connection id>   (404 = follows nobody: an EMPTY feed)
//            on: the panel shown while Idle / Offline / Error · a NEW connection id · Retry · 30 min without news
//   delta    dealer hm://presence2/user/<id>  →  GET /presence-view/v1/user/<id>  →  upsert that row (404/403: drop it)
//            a newer push for the same friend supersedes the older fetch; a fresh seed supersedes every pending one
//   offline  signed out / no connection id: state Offline and the run cleared (the next account never sees these rows)
//
// UI thread except the bodies handed to `Api.Run` and `OnDealerPush` (dealer thread, which only posts). `--fake` lands
// the fake feed through the real decoder (Entities.Fake.Friends.cs). Plan: docs/plans/wavee/friend-activity-implementation.md.

using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static class Friends
    {
        /// <summary>Re-seed after this long with the panel visible and no seed or push heard (0.2.9's watchdog).</summary>
        public const long WatchdogMs = 30 * 60_000L;

        static readonly byte[] PresencePrefix = "hm://presence2/user/"u8.ToArray();

        static bool s_attached, s_active;
        static string s_seedFor = "";                 // the connection id of the newest seed: older answers are dropped
        static long s_lastHeardMs = long.MinValue / 2; // the newest seed or push, on the app's monotonic clock
        static int s_gen;
        static readonly Dictionary<string, int> s_userGen = new(StringComparer.Ordinal);   // per-friend: the newest fetch wins

        /// <summary>Assign the panel's seams, once. Idempotent; called from the panel's props builder so the app and the
        /// headless inspector both attach it before the panel first mounts.</summary>
        public static void Attach()
        {
            if (s_attached) return;
            s_attached = true;
            Rail.Friends.SetActive = SetActive;
            Rail.Friends.Refresh = static () => Seed("retry");
            if (Platform.Args.Fake) LandFake();
        }

        static void SetActive(bool active)
        {
            s_active = active;
            if (!active) return;
            if (Rail.Friends.State.Peek() is Rail.Friends.FeedState.Idle or Rail.Friends.FeedState.Offline or Rail.Friends.FeedState.Error)
                Seed("shown");
            else if (Playback.FrameNowMs() - s_lastHeardMs >= WatchdogMs) Seed("watchdog");
        }

        /// <summary>UI thread (Session's AnnounceDevice effect): a NEW dealer connection id. The feed is tied to the id it
        /// was asked for, so a visible or populated feed re-seeds.</summary>
        public static void OnConnection()
        {
            if (!s_attached) return;
            if (s_active || Entities.Current.Edges.Friends.Count(Rail.Friends.Feed) > 0) Seed("connection");
        }

        /// <summary>UI thread (Session's SignedOut effect): no feed, and no rows left for the next account.</summary>
        public static void OnSignedOut()
        {
            s_seedFor = "";
            s_userGen.Clear();
            if (Entities.Current.Edges.Friends.Count(Rail.Friends.Feed) > 0)
            {
                Entities.Current.Edges.Friends.Clear(Rail.Friends.Feed);
                Entities.Publish();
            }
            Rail.Friends.State.Value = Rail.Friends.FeedState.Offline;
        }

        /// <summary>Dealer thread: is this MESSAGE a presence push? Taken (true) when it is; the fetch is posted to the UI
        /// thread, which owns the generations.</summary>
        public static bool OnDealerPush(ReadOnlySpan<byte> uri)
        {
            if (!uri.StartsWith(PresencePrefix)) return false;
            var tail = uri[PresencePrefix.Length..];
            if (tail.IsEmpty) return true;
            string userId = Encoding.UTF8.GetString(tail);
            Post(() => FetchUser(userId));
            return true;
        }

        // ── the seed ────────────────────────────────────────────────────────────────────────────────────────────────

        static void Seed(string why)
        {
            if (Platform.Args.Fake) { LandFake(); return; }
            string conn = ConnectionId();
            if (conn.Length == 0 || !Current.IsOnline)
            {
                Rail.Friends.State.Value = Rail.Friends.FeedState.Offline;
                return;
            }
            s_seedFor = conn;
            s_lastHeardMs = Playback.FrameNowMs();
            s_userGen.Clear();                                   // a fresh seed carries every friend a pending fetch would
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
                    else if (answer.Status == 404) { s.FriendsBatch(FriendsOp.Replace); ok = true; }
                }
                catch (Exception ex) { Log.Warn("friends", "seed failed", ex); }
                Post(() => LandSeed(conn, s, ok, status, why));
            });
            if (!queued) LandFailure();
        }

        static void LandSeed(string conn, Staging s, bool ok, int status, string why)
        {
            try
            {
                if (!string.Equals(conn, s_seedFor, StringComparison.Ordinal)) return;   // a newer seed went out, or signed out
                if (!ok)
                {
                    Log.Warn("friends", "seed answered " + status.ToString(CultureInfo.InvariantCulture) + " why=" + why);
                    LandFailure();
                    return;
                }
                Entities.Commit(s);
                Entities.Publish();
                Rail.Friends.State.Value = Rail.Friends.FeedState.Ready;
                Log.Info("friends", "seed landed rows=" + Entities.Current.Edges.Friends.Count(Rail.Friends.Feed).ToString(CultureInfo.InvariantCulture)
                    + " why=" + why);
            }
            finally { Staging.Return(s); }
        }

        /// <summary>A failed seed keeps the rows it held (rows win, <c>Rail.Friends.SurfaceFor</c>); with none, the error face.</summary>
        static void LandFailure()
        {
            if (Entities.Current.Edges.Friends.Count(Rail.Friends.Feed) == 0) Rail.Friends.State.Value = Rail.Friends.FeedState.Error;
        }

        // ── one friend's delta ──────────────────────────────────────────────────────────────────────────────────────

        static void FetchUser(string userId)
        {
            if (s_seedFor.Length == 0) return;                  // no feed yet: the seed will carry this friend
            s_lastHeardMs = Playback.FrameNowMs();
            int gen = ++s_gen;
            s_userGen[userId] = gen;
            uint epoch = Entities.Current.Epoch;
            byte[] userUri = Encoding.UTF8.GetBytes("spotify:user:" + userId);
            bool queued = Api.Run(() =>
            {
                var s = Staging.Rent();
                s.Epoch = epoch;
                try
                {
                    var answer = Api.FriendPresence(userId, CancellationToken.None);
                    if (answer.Ok) Decode.FriendOne(answer.Bytes, userUri, s);
                    else if (answer.Status is 404 or 403) s.FriendsBatch(FriendsOp.Remove, new StagedId(s.AddText(userUri)));
                    else Log.Info("friends", "presence answered " + answer.Status.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex) { Log.Info("friends", "presence fetch failed: " + ex.Message); }
                Post(() => LandUser(userId, gen, s));
            });
            if (!queued) s_userGen.Remove(userId);
        }

        static void LandUser(string userId, int gen, Staging s)
        {
            try
            {
                if (!s_userGen.TryGetValue(userId, out int newest) || newest != gen) return;   // superseded
                s_userGen.Remove(userId);
                if (s.FriendsOpOf == FriendsOp.None) return;
                Entities.Commit(s);
                Entities.Publish();
                Rail.Friends.State.Value = Rail.Friends.FeedState.Ready;
            }
            finally { Staging.Return(s); }
        }

        // ── --fake ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The fake feed, through the real decoder and commit: its times are relative to now, so two rows are live.</summary>
        static void LandFake()
        {
            var s = Staging.Rent();
            try
            {
                s.Epoch = Entities.Current.Epoch;
                if (!Decode.FriendFeed(Entities.FakeFriendFeed(Playback.UnixNowMs()), s)) return;
                Entities.Commit(s);
                Entities.Publish();
                Rail.Friends.State.Value = Rail.Friends.FeedState.Ready;
            }
            finally { Staging.Return(s); }
        }
    }
}
