// ── Spotify/Spotify.Friends.cs — the friend-activity feed's host (#277) ─────────────────────────────────────────────
//
// Fills `Edges.Friends` (under `Rail.Friends.Feed`) and `Rail.Friends.State`, and assigns the panel's two seams
// (`Rail.Friends.SetActive`, `Rail.Friends.Refresh`). It does what the official desktop client (1.3.10.2586, captured)
// does for its panel, and nothing more:
//
//   feed       POST /listening-activity/v1/feed {"unused":true,"resultLimit":100} — every followed friend's latest
//              activity, once: when the panel is shown, on a new dealer connection while it is shown, and on Retry
//   subscribe  POST /listening-activity/v1/subscribe — every user the feed named, on THIS dealer connection; it lives
//              48 h. Sent again on a new connection id, for a user it does not cover yet, and before it expires.
//   push       a friend's change arrives as a dealer MESSAGE on `hm://listening-activity/…` (observed on Wavee's own
//              connection, 2026-11-25). The push is the SIGNAL, not the data: a panel someone can see loads the feed again,
//              pushes inside `PushSettleMs` folded into one load. One nobody can see (unmounted, or the window minimised,
//              hidden or covered: `OnWindowHidden`) only notes it was missed, and loads once when it is seen again.
//   words      the feed names only identities, so the friends' users, tracks and context playlists are ENSURED through
//              the fetch layer (extended-metadata); a watch on the track table fills each row's album and artist
//   failure    rows kept (rows win, `Rail.Friends.SurfaceFor`), the error face with none; the next try waits for the
//              answer's Retry-After or a doubling back-off, and only while someone can see the panel. Retry skips our
//              own back-off (the user asked); a server's Retry-After it waits out, on the skeleton. Logged on a change of
//              state (ok → failing, failing → ok), never per try.
//   session    the panel says what the shell's profile chip says (`Shell.FoldAuth`, `WatchSession`): "Sign in" only with
//              no stored credential (a launch without one, a sign-out, a rejected login); the error face when the session
//              gave up; the skeleton while it connects. A sign-out clears the run, so the next account never sees it.
//
// No poll and no fallback source: the official client has neither (two launches captured, 130 s with the panel open and no
// second request); a feed request follows a push, never a clock. UI thread except the bodies handed to `Api.Run` and the two timers (which only post). `--fake` lands the
// fake feed through the real decoder (Entities.Fake.Friends.cs). Plan: docs/plans/wavee/friend-activity-implementation.md.

using System.Globalization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Spotify
{
    public static class Friends
    {
        /// <summary>How many friends one answer may carry (the desktop client's ask).</summary>
        const int Limit = 100;
        /// <summary>The first wait after a failure with no Retry-After; doubled per failure up to <see cref="BackoffMaxMs"/>.</summary>
        const int BackoffFirstMs = 15_000;
        const int BackoffMaxMs = 15 * 60_000;
        /// <summary>Renew the subscription this long before it expires.</summary>
        const long RenewAheadMs = 60 * 60_000L;
        /// <summary>The lifetime the capture showed, for an answer whose expiry does not read.</summary>
        const long CapturedLifetimeMs = 48 * 3_600_000L;
        const string UserPrefix = "spotify:user:";
        /// <summary>Pushes this close together (a friend skipping through tracks, several friends at once) are one load.</summary>
        const int PushSettleMs = 2_000;

        static bool s_attached, s_active, s_inflight, s_again;
        static uint s_generation;                        // bumped on sign-out: an answer from before it is dropped
        static int s_failures;                           // consecutive failed loads; 0 = the last one landed
        static long s_holdUntilMs;                       // no feed request before this (Retry-After or the back-off)
        static bool s_hidden;                            // the window is minimised, hidden or covered (OnWindowHidden)
        static bool s_missed = true;                     // a push, a connection, a renewal or a retry came while nobody looked
        static bool s_holdIsServer;                      // the hold is the answer's Retry-After (Retry waits it out), not ours
        static Timer? s_retry, s_renew, s_push;
        static int s_pushes;                             // pushes folded into the pending load (the dealer thread counts)

        // The subscription as the service last confirmed it.
        static bool s_subInflight;
        static string s_subConnection = "";
        static string[] s_subUsers = [];
        static long s_subExpiresMs;
        static int s_subFailStatus = -1;                 // the last failed subscribe's status, so a repeat is not logged

        static ReactiveRuntime? s_runtime;
        static Effect? s_refill, s_session;
        static bool s_flushPosted;
        static readonly Action s_flush = static () => { s_flushPosted = false; s_runtime?.Flush(); };

        /// <summary>Assign the panel's seams and start the album/artist watch, once. Idempotent; called from the panel's
        /// props builder so the app and the headless inspector both attach it before the panel first mounts.</summary>
        public static void Attach()
        {
            if (s_attached) return;
            s_attached = true;
            Rail.Friends.SetActive = SetActive;
            Rail.Friends.Refresh = Retry;
            s_runtime = new ReactiveRuntime { FrameRequested = RequestFlush };
            s_refill = new Effect(s_runtime, WatchTracks);
            s_session = new Effect(s_runtime, WatchSession);
            if (Platform.Args.Fake) LandFake();
            else if (NoAccount) Rail.Friends.State.Value = Rail.Friends.FeedState.Offline;
        }

        /// <summary>Nothing to resume: no stored credential, the shell's own rule for "signed out" (<c>Shell.FoldAuth</c>). A
        /// launch without one never calls <c>Spotify.Login</c>, so the session's fault cannot tell.</summary>
        static bool NoAccount => !Platform.HasStoredCredential();

        /// <summary>Someone can see the panel: it is mounted and its window is not minimised, hidden or covered.</summary>
        static bool Looking => s_active && !s_hidden;

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

        /// <summary>Tracked: the session's phase and fault. Posts the panel's answer to them (a watch never writes inline).</summary>
        static void WatchSession()
        {
            _ = Status.Value;
            _ = Fault.Value;
            Post(s_landSession);
        }

        /// <summary>The panel says what the profile chip says (<c>Shell.FoldAuth</c>): "Sign in" with nothing to resume, the
        /// error face for a session that gave up (rows kept), and the skeleton again once it connects.</summary>
        static readonly Action s_landSession = static () =>
        {
            if (Platform.Args.Fake || !s_attached) return;
            var state = Rail.Friends.State.Peek();
            switch (Shell.FoldAuth(Status.Peek(), Fault.Peek(), !NoAccount))
            {
                case Shell.AuthState.SignInRequired:
                    if (Rows == 0) Rail.Friends.State.Value = Rail.Friends.FeedState.Offline;
                    break;
                case Shell.AuthState.Offline:
                    // A stored credential and a session that is down. Before the launch's login folds, the phase still reads
                    // Offline: only a session that GAVE UP is an outage.
                    if (Status.Peek() == SessionPhase.Failed) LandFailure();
                    break;
                default:   // Connecting, Live: a (re)connect owes a load (OnConnection), so the panel waits on the skeleton
                    if (Rows == 0 && state is Rail.Friends.FeedState.Offline or Rail.Friends.FeedState.Error)
                        Rail.Friends.State.Value = Rail.Friends.FeedState.Loading;
                    break;
            }
        };

        static void SetActive(bool active)
        {
            s_active = active;
            if (active) Load("shown");
            else StopRetry();   // nobody is looking: no retries
        }

        /// <summary>UI thread (the shell root's watch on <c>InputHooks.WindowOccluded</c>): the window was minimised, hidden or
        /// covered (true), or can be seen again (false). Seen again, a mounted panel that missed something loads once; a
        /// window that is merely brought back to the front costs nothing.</summary>
        public static void OnWindowHidden(bool hidden)
        {
            if (s_hidden == hidden) return;
            s_hidden = hidden;
            if (!s_attached || !s_active) return;
            if (hidden) StopRetry();
            else if (s_missed) Load("seen");
        }

        /// <summary>A retry that was due goes with the viewer; the next look owes it.</summary>
        static void StopRetry()
        {
            if (s_failures > 0) s_missed = true;
            s_retry?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>UI thread (Session's AnnounceDevice effect): a NEW dealer connection. Signed in again if the panel read
        /// "Sign in"; a panel someone can see reloads (the load re-subscribes on the new connection id), any other owes it.</summary>
        public static void OnConnection()
        {
            if (!s_attached) return;
            if (Rail.Friends.State.Value == Rail.Friends.FeedState.Offline) Rail.Friends.State.Value = Rail.Friends.FeedState.Loading;
            LoadOrOwe("connection");
        }

        /// <summary>Load now when someone can see the panel; otherwise note it, so the next look loads once.</summary>
        static void LoadOrOwe(string why)
        {
            if (Looking) Load(why);
            else s_missed = true;
        }

        /// <summary>The error face's Retry. The user asked, so our own back-off gives way; a server's Retry-After does not, but
        /// the panel shows it is on its way (the skeleton) instead of a button that seems to do nothing.</summary>
        static void Retry()
        {
            if (!s_holdIsServer) s_holdUntilMs = 0;
            else if (Playback.UnixNowMs() < s_holdUntilMs && Rows == 0) Rail.Friends.State.Value = Rail.Friends.FeedState.Loading;
            Load("retry");
        }

        /// <summary>DEALER THREAD: a MESSAGE frame nobody else took. A <c>hm://listening-activity/</c> push (a friend's
        /// activity changed) arms the settle timer and answers true; anything else answers false.</summary>
        public static bool OnDealerPush(ReadOnlySpan<byte> uri)
        {
            if (!uri.StartsWith("hm://listening-activity/"u8)) return false;
            if (Interlocked.Increment(ref s_pushes) == 1)
            {
                var timer = Volatile.Read(ref s_push);
                if (timer is null)
                {
                    var made = new Timer(static _ => Post(s_settled), null, Timeout.Infinite, Timeout.Infinite);
                    timer = Interlocked.CompareExchange(ref s_push, made, null) ?? made;
                    if (!ReferenceEquals(timer, made)) made.Dispose();
                }
                timer.Change(PushSettleMs, Timeout.Infinite);
            }
            return true;
        }

        static readonly Action s_settled = static () =>
        {
            int pushes = Interlocked.Exchange(ref s_pushes, 0);
            if (pushes > 0 && s_attached) LoadOrOwe("push");   // nobody looking: one load when someone does
        };

        /// <summary>UI thread (Session's SignedOut effect): no feed, no subscription and no rows left for the next account.</summary>
        public static void OnSignedOut()
        {
            s_generation++;
            s_failures = 0;
            s_holdUntilMs = 0;
            s_holdIsServer = false;
            s_missed = true;
            s_again = false;
            s_retry?.Change(Timeout.Infinite, Timeout.Infinite);
            s_renew?.Change(Timeout.Infinite, Timeout.Infinite);
            s_subConnection = "";
            s_subUsers = [];
            s_subExpiresMs = 0;
            s_subFailStatus = -1;
            if (Entities.Current.Edges.Friends.Count(Rail.Friends.Feed) > 0)
            {
                Entities.Current.Edges.Friends.Clear(Rail.Friends.Feed);
                Entities.Publish();
            }
            Rail.Friends.State.Value = Rail.Friends.FeedState.Offline;
        }

        static int Rows => Entities.Current.Edges.Friends.Count(Rail.Friends.Feed);

        // ── the feed ────────────────────────────────────────────────────────────────────────────────────────────────

        static void Load(string why)
        {
            if (Platform.Args.Fake) { LandFake(); return; }
            if (NoAccount) { Rail.Friends.State.Value = Rail.Friends.FeedState.Offline; return; }
            var session = Current;
            if (!session.IsOnline)
            {
                // Signed in but not connected: a session that gave up is an outage (the error face, rows kept); one still
                // connecting loads on its connection (OnConnection).
                if (session.Phase == SessionPhase.Failed) LandFailure();
                return;
            }
            if (s_inflight) { s_again = true; return; }   // one request at a time; the ask runs once more after it
            long now = Playback.UnixNowMs();
            if (now < s_holdUntilMs)
            {
                // Inside Retry-After or the back-off: Retry (or a show) waits for it rather than asking early.
                ArmRetry(s_holdUntilMs - now);
                return;
            }
            s_inflight = true;
            s_missed = false;
            if (Rows == 0) Rail.Friends.State.Value = Rail.Friends.FeedState.Loading;

            uint epoch = Entities.Current.Epoch, generation = s_generation;
            bool queued = Api.Run(() =>
            {
                var s = Staging.Rent();
                s.Epoch = epoch;
                var report = new Report { Why = why };
                try
                {
                    var answer = Api.ListeningActivity(Limit, CancellationToken.None);
                    report.Status = answer.Status;
                    report.RetryAfterSeconds = answer.RetryAfterSeconds;
                    report.Bytes = answer.Length;
                    if (answer.Ok) report.Ok = Decode.ListeningFeed(answer.Bytes, s, out report.Entries, out report.Skipped);
                }
                catch (Exception ex) { report.Error = ex.GetType().Name; }
                Post(() => Land(generation, s, report));
            });
            if (!queued) { s_inflight = false; Failed(new Report { Why = why, Error = "notQueued" }); }
        }

        /// <summary>What one load learned, for the always-on line: counts, statuses and key names only.</summary>
        sealed class Report
        {
            public string Why = "", Skipped = "", Error = "";
            public int Status, Entries, Bytes, RetryAfterSeconds;
            public bool Ok;
        }

        static void Land(uint generation, Staging s, Report report)
        {
            try
            {
                s_inflight = false;
                if (generation != s_generation) return;          // signed out while it flew
                if (!report.Ok) { Failed(report); return; }

                if (s_failures > 0)
                    Log.Info("friends", "feed recovered after " + s_failures.ToString(CultureInfo.InvariantCulture) + " failed loads");
                s_failures = 0;
                s_holdUntilMs = 0;
                s_holdIsServer = false;
                s_retry?.Change(Timeout.Infinite, Timeout.Infinite);

                Entities.Commit(s);
                Entities.Publish();
                Rail.Friends.State.Value = Rail.Friends.FeedState.Ready;
                EnsureWords();

                var (dropped, dropKinds) = Entities.LastFriendsDropped;
                // A push-driven load is routine (one per friend's change): it says nothing unless something is off.
                if (report.Why != "push" || dropped > 0 || report.Skipped.Length > 0)
                    Log.Info("friends", "feed landed rows=" + Rows.ToString(CultureInfo.InvariantCulture)
                        + " entries=" + report.Entries.ToString(CultureInfo.InvariantCulture)
                        + " dropped=" + dropped.ToString(CultureInfo.InvariantCulture) + (dropKinds.Length > 0 ? " (" + dropKinds + ")" : "")
                        + (report.Skipped.Length > 0 ? " skipped=[" + report.Skipped + "]" : "") + " why=" + report.Why);
                Subscribe();
            }
            finally
            {
                Staging.Return(s);
                if (s_again) { s_again = false; Load("again"); }
            }
        }

        /// <summary>A failed load: the rows stay (the error face with none), the next try waits for Retry-After or the
        /// back-off, and the log hears about it once, when the feed starts failing.</summary>
        static void Failed(Report report)
        {
            s_failures++;
            long delay = report.RetryAfterSeconds > 0 ? report.RetryAfterSeconds * 1000L
                : Math.Min((long)BackoffMaxMs, (long)BackoffFirstMs << Math.Min(s_failures - 1, 10));
            s_holdUntilMs = Playback.UnixNowMs() + delay;
            s_holdIsServer = report.RetryAfterSeconds > 0;
            if (s_failures == 1)
                Log.Warn("friends", "feed failing status=" + report.Status.ToString(CultureInfo.InvariantCulture)
                    + (report.Status is >= 200 and < 300 ? " unknown shape (" + report.Bytes.ToString(CultureInfo.InvariantCulture) + " bytes)" : "")
                    + (report.Error.Length > 0 ? " error=" + report.Error : "")
                    + " retryIn=" + (delay / 1000).ToString(CultureInfo.InvariantCulture) + "s why=" + report.Why);
            LandFailure();
            if (Looking) ArmRetry(delay);
            else s_missed = true;
        }

        /// <summary>A failed load keeps the rows it held (rows win, <c>Rail.Friends.SurfaceFor</c>); with none, the error face.</summary>
        static void LandFailure()
        {
            if (Rows == 0) Rail.Friends.State.Value = Rail.Friends.FeedState.Error;
        }

        static void ArmRetry(long delayMs)
        {
            s_retry ??= new Timer(static _ => Post(static () => LoadOrOwe("backoff")), null, Timeout.Infinite, Timeout.Infinite);
            s_retry.Change(Math.Clamp(delayMs, 1L, CapturedLifetimeMs), Timeout.Infinite);   // a wild Retry-After cannot overflow the timer
        }

        // ── the subscription ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>After a landed feed: subscribe this dealer connection to every user it named, unless the standing
        /// subscription already covers them on this connection and is not near its expiry. An empty feed subscribes nothing.</summary>
        static void Subscribe()
        {
            if (s_subInflight) return;
            string connection = ConnectionId();
            if (connection.Length == 0) return;
            var run = Entities.Current.Edges.Friends.Payload(Rail.Friends.Feed);
            if (run.IsEmpty) return;
            var users = new string[run.Length];
            int n = 0;
            for (int i = 0; i < run.Length; i++)
            {
                string uri = new User(run[i].UserSlot).Uri.Text;
                if (uri.StartsWith(UserPrefix, StringComparison.Ordinal)) users[n++] = Uri.UnescapeDataString(uri[UserPrefix.Length..]);
            }
            if (n == 0) return;
            if (n < users.Length) Array.Resize(ref users, n);

            long now = Playback.UnixNowMs();
            if (connection == s_subConnection && now < s_subExpiresMs - RenewAheadMs && Covers(s_subUsers, users)) return;

            s_subInflight = true;
            uint generation = s_generation;
            bool queued = Api.Run(() =>
            {
                int status = 0;
                long expires = 0;
                try
                {
                    var answer = Api.ListeningSubscribe(connection, users, CancellationToken.None);
                    status = answer.Status;
                    if (answer.Ok) expires = Decode.ListeningSubscription(answer.Bytes);
                }
                catch (Exception) { status = -2; }
                Post(() => LandSubscribe(generation, connection, users, status, expires));
            });
            if (!queued) s_subInflight = false;
        }

        static void LandSubscribe(uint generation, string connection, string[] users, int status, long expiresMs)
        {
            s_subInflight = false;
            if (generation != s_generation) return;
            if (status is < 200 or >= 300)
            {
                // The next feed (a show, a new connection, Retry) asks again; logged once per distinct failure.
                s_subConnection = "";
                if (status != s_subFailStatus)
                    Log.Warn("friends", "subscribe failed status=" + status.ToString(CultureInfo.InvariantCulture)
                        + " users=" + users.Length.ToString(CultureInfo.InvariantCulture));
                s_subFailStatus = status;
                return;
            }
            long now = Playback.UnixNowMs();
            bool read = expiresMs > now;
            s_subConnection = connection;
            s_subUsers = users;
            s_subExpiresMs = read ? expiresMs : now + CapturedLifetimeMs;
            s_subFailStatus = -1;
            Log.Info("friends", "subscribed users=" + users.Length.ToString(CultureInfo.InvariantCulture)
                + " expiresIn=" + ((s_subExpiresMs - now) / 3_600_000L).ToString(CultureInfo.InvariantCulture) + "h"
                + (read ? "" : " (expiry unread: the captured 48 h assumed)"));
            s_renew ??= new Timer(static _ => Post(static () => { if (s_attached) LoadOrOwe("renew"); }), null, Timeout.Infinite, Timeout.Infinite);
            s_renew.Change(Math.Clamp(s_subExpiresMs - RenewAheadMs - now, 60_000L, CapturedLifetimeMs), Timeout.Infinite);
        }

        static bool Covers(string[] held, string[] wanted)
        {
            foreach (string w in wanted)
                if (Array.IndexOf(held, w) < 0) return false;
            return true;
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

        /// <summary>The fake feed, through the real decoder and commit: its times are relative to now, two friends playing.</summary>
        static void LandFake()
        {
            var s = Staging.Rent();
            try
            {
                s.Epoch = Entities.Current.Epoch;
                if (!Decode.ListeningFeed(Entities.FakeFriendFeed(Playback.UnixNowMs()), s, out _, out _)) return;
                Entities.Commit(s);
                Entities.Publish();
                Rail.Friends.State.Value = Rail.Friends.FeedState.Ready;
            }
            finally { Staging.Return(s); }
        }
    }
}
