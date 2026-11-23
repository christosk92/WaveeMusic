// ── Spotify/Spotify.Friends.cs — the friend-activity feed's host (#277) ─────────────────────────────────────────────
//
// Fills `Edges.Friends` (under `Rail.Friends.Feed`) and `Rail.Friends.State`, and assigns the panel's two seams
// (`Rail.Friends.SetActive`, `Rail.Friends.Refresh`). It reads what the desktop client's panel reads (and what spotifast
// reads through librespot):
//
//   feed      POST /listening-activity/v1/feed {"unused":true,"resultLimit":100}  — every followed friend's latest track
//             polled every 15 s while the panel is open, and only then; also on open, a new connection and Retry
//   fallback  GET /presence-view/v1/buddylist  — when the feed fails (it carries its own words)
//   words     the feed names only identities, so the friends' users, tracks and context playlists are ENSURED through
//             the fetch layer; a watch on the track table fills each row's album and artist as tracks land
//   offline   signed out: state Offline and the run cleared (the next account never sees these rows)
//
// (The v2 init-friend-feed + hm://presence2 pushes 0.2.9 used answered a handful of stale rows: replaced.) UI thread
// except the bodies handed to `Api.Run` and the poll timer (which only posts). `--fake` lands the fake feed through the
// real decoder (Entities.Fake.Friends.cs). Plan: docs/plans/wavee/friend-activity-implementation.md.

using System.Globalization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Spotify
{
    public static class Friends
    {
        /// <summary>The poll cadence while the panel is open (the desktop client's).</summary>
        public const int PollMs = 15_000;
        /// <summary>How many friends one answer may carry.</summary>
        const int Limit = 100;

        static bool s_attached, s_active, s_inflight, s_again;
        static uint s_generation;                        // bumped on sign-out: an answer from before it is dropped
        static int s_lastRows = -1;
        static Timer? s_poll;
        static ReactiveRuntime? s_runtime;
        static Effect? s_refill;
        static bool s_flushPosted;
        static readonly Action s_flush = static () => { s_flushPosted = false; s_runtime?.Flush(); };

        /// <summary>Assign the panel's seams and start the album/artist watch, once. Idempotent; called from the panel's
        /// props builder so the app and the headless inspector both attach it before the panel first mounts.</summary>
        public static void Attach()
        {
            if (s_attached) return;
            s_attached = true;
            Rail.Friends.SetActive = SetActive;
            Rail.Friends.Refresh = static () => Load("retry");
            s_runtime = new ReactiveRuntime { FrameRequested = RequestFlush };
            s_refill = new Effect(s_runtime, WatchTracks);
            if (Platform.Args.Fake) LandFake();
        }

        static void RequestFlush()
        {
            if (s_flushPosted) return;
            s_flushPosted = true;
            Post(s_flush);
        }

        /// <summary>Tracked: the scope, the friends run and the track table. A track row that landed may give a friend's
        /// row its album and artist; posts the refill (a watch never writes a table inline).</summary>
        static void WatchTracks()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Tracks.Changed.Value;
            _ = scope.Edges.Friends.Changed.Value;
            Post(static () => { if (Entities.RefillFriends()) Entities.Publish(); });
        }

        static void SetActive(bool active)
        {
            s_active = active;
            if (active)
            {
                s_poll ??= new Timer(static _ => Post(static () => { if (s_active) Load("poll"); }), null, Timeout.Infinite, Timeout.Infinite);
                s_poll.Change(PollMs, PollMs);
                Load("shown");
            }
            else s_poll?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>UI thread (Session's AnnounceDevice effect): a NEW dealer connection — back online, so a visible panel reloads.</summary>
        public static void OnConnection()
        {
            if (s_attached && s_active) Load("connection");
        }

        /// <summary>UI thread (Session's SignedOut effect): no feed, and no rows left for the next account.</summary>
        public static void OnSignedOut()
        {
            s_generation++;
            s_lastRows = -1;
            if (Entities.Current.Edges.Friends.Count(Rail.Friends.Feed) > 0)
            {
                Entities.Current.Edges.Friends.Clear(Rail.Friends.Feed);
                Entities.Publish();
            }
            Rail.Friends.State.Value = Rail.Friends.FeedState.Offline;
        }

        // ── the load ────────────────────────────────────────────────────────────────────────────────────────────────

        static void Load(string why)
        {
            if (Platform.Args.Fake) { LandFake(); return; }
            if (!Current.IsOnline)
            {
                if (Entities.Current.Edges.Friends.Count(Rail.Friends.Feed) == 0) Rail.Friends.State.Value = Rail.Friends.FeedState.Offline;
                return;
            }
            if (s_inflight) { s_again |= why != "poll"; return; }   // one request at a time; a user's ask runs once more after it
            s_inflight = true;
            if (Entities.Current.Edges.Friends.Count(Rail.Friends.Feed) == 0) Rail.Friends.State.Value = Rail.Friends.FeedState.Loading;

            uint epoch = Entities.Current.Epoch, generation = s_generation;
            long nowMs = Playback.UnixNowMs();
            bool queued = Api.Run(() =>
            {
                var s = Staging.Rent();
                s.Epoch = epoch;
                var report = new Report { Why = why };
                try
                {
                    var answer = Api.ListeningActivity(Limit, CancellationToken.None);
                    report.Status = answer.Status;
                    if (answer.Ok)
                    {
                        report.Ok = Decode.ListeningFeed(answer.Bytes, s, nowMs, out report.Entries, out report.Skipped);
                        if (!report.Ok) report.Shape = Decode.FriendFeedShape(answer.Bytes);
                    }
                    if (!report.Ok)
                    {
                        // The fallback: the buddy list, which carries its own words.
                        var buddies = Api.FriendBuddylist(CancellationToken.None);
                        report.Source = "buddylist " + buddies.Status.ToString(CultureInfo.InvariantCulture);
                        if (buddies.Ok) report.Ok = Decode.FriendFeed(buddies.Bytes, s, out report.Entries, out report.Skipped);
                    }
                }
                catch (Exception ex) { Log.Warn("friends", "load failed", ex); }
                Post(() => Land(generation, s, report));
            });
            if (!queued) { s_inflight = false; LandFailure(); }
        }

        /// <summary>What one load learned, for the always-on line: counts, statuses and key names only.</summary>
        sealed class Report
        {
            public string Why = "", Source = "", Skipped = "", Shape = "";
            public int Status, Entries;
            public bool Ok;
        }

        static void Land(uint generation, Staging s, Report report)
        {
            try
            {
                s_inflight = false;
                if (generation != s_generation) return;          // signed out while it flew
                if (!report.Ok)
                {
                    Log.Warn("friends", "feed answered " + report.Status.ToString(CultureInfo.InvariantCulture)
                        + (report.Shape.Length > 0 ? " shape={" + report.Shape + "}" : "")
                        + (report.Source.Length > 0 ? " fallback=" + report.Source : "") + " why=" + report.Why);
                    LandFailure();
                    return;
                }
                Entities.Commit(s);
                Entities.Publish();
                Rail.Friends.State.Value = Rail.Friends.FeedState.Ready;
                EnsureWords();

                int rows = Entities.Current.Edges.Friends.Count(Rail.Friends.Feed);
                var (dropped, dropKinds) = Entities.LastFriendsDropped;
                // A poll that changed nothing says nothing (every 15 s); everything else is one line.
                if (report.Why != "poll" || rows != s_lastRows || dropped > 0)
                    Log.Info("friends", "feed landed rows=" + rows.ToString(CultureInfo.InvariantCulture)
                        + " entries=" + report.Entries.ToString(CultureInfo.InvariantCulture)
                        + " dropped=" + dropped.ToString(CultureInfo.InvariantCulture) + (dropKinds.Length > 0 ? " (" + dropKinds + ")" : "")
                        + (report.Skipped.Length > 0 ? " skipped=[" + report.Skipped + "]" : "")
                        + " source=" + (report.Source.Length > 0 ? report.Source : "listening-activity") + " why=" + report.Why);
                s_lastRows = rows;
            }
            finally
            {
                Staging.Return(s);
                if (s_again) { s_again = false; Load("again"); }
            }
        }

        /// <summary>A failed load keeps the rows it held (rows win, <c>Rail.Friends.SurfaceFor</c>); with none, the error face.</summary>
        static void LandFailure()
        {
            if (Entities.Current.Edges.Friends.Count(Rail.Friends.Feed) == 0) Rail.Friends.State.Value = Rail.Friends.FeedState.Error;
        }

        /// <summary>The words the feed does not carry: each friend's name and avatar, each track's title, cover and artists,
        /// each context playlist's name — one batch per table through the fetch layer (it skips what is already known).</summary>
        static void EnsureWords()
        {
            var run = Entities.Current.Edges.Friends.Payload(Rail.Friends.Feed);
            if (run.IsEmpty) return;
            var users = new User[run.Length];
            var tracks = new Track[run.Length];
            var contexts = new List<Playlist>(run.Length);
            for (int i = 0; i < run.Length; i++)
            {
                users[i] = new User(run[i].UserSlot);
                tracks[i] = new Track(run[i].TrackSlot);
                if (run[i].ContextSlot > 0) contexts.Add(new Playlist(run[i].ContextSlot));
            }
            Entities.Ensure(users, UserFields.Identity);
            Entities.Ensure(tracks, TrackFields.Identity);
            if (contexts.Count > 0) Entities.Ensure(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(contexts), PlaylistFields.Identity);
        }

        // ── --fake ──────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The fake feed, through the real decoder and commit: its times are relative to now, so two rows are live.</summary>
        static void LandFake()
        {
            var s = Staging.Rent();
            try
            {
                s.Epoch = Entities.Current.Epoch;
                long now = Playback.UnixNowMs();
                if (!Decode.ListeningFeed(Entities.FakeFriendFeed(now), s, now, out _, out _)) return;
                Entities.Commit(s);
                Entities.Publish();
                Rail.Friends.State.Value = Rail.Friends.FeedState.Ready;
            }
            finally { Staging.Return(s); }
        }
    }
}
