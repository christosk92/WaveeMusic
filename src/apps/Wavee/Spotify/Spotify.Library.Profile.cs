// ── Spotify/Spotify.Library.Profile.cs — following a user profile ──────────────────────────────────────────────────────
//
// Role: SHELL (the write; the model half is Entities/User.Follow.cs)
// Owner: D3 (profile pages)
// Spec: docs/plans/wavee/profile-pages-implementation.md Appendix D §3.4

using InfoBarSeverity = FluentGpu.Controls.InfoBarSeverity;
using Loc = FluentGpu.Localization.Loc;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Library
    {
        static readonly HashSet<(uint Epoch, int Slot)> s_followPending = new();

        /// <summary>Follow or unfollow a user profile (Controls.FollowToggle via User.LibrarySeam). Optimistic
        /// (UserFollowWrite), one write per user at a time, a no-op on your own profile.
        /// <para><b>NO SETTLE REFRESH</b> (the SetCollaborative toggle-bounce precedent, Spotify.Api.Playlist.cs): a profile
        /// GET a few hundred ms after the write can come back from a replica that has not seen it and flip the button back.
        /// The 200 is the answer; the next mount's Invalidate (ProfileAsk) reconciles the exact follower count.</para></summary>
        public static void FollowUser(string userUri, bool follow)
        {
            var scope = Entities.Current;
            if (scope is null || string.IsNullOrEmpty(userUri)) return;
            UserTable users = scope.Users;
            if (!users.TryGetSlot(userUri.AsSpan(), out int slot) || slot == scope.MeSlot) return;
            var user = new User(slot);
            if (user.IsCurrentUser || user.IsFollowedByViewer == follow) return;
            if (s_followPending.Contains((scope.Epoch, slot))) return;          // a double click lands the first click
            if (!CanWrite(out _, out _)) { Unavailable(); return; }
            EntityId id = users.Id[slot];
            FollowSnapshot snap = UserFollowWrite.Apply(users, slot, follow);
            Entities.Publish();
            s_followPending.Add((scope.Epoch, slot));
            string bareId = Api.UsernameOf(userUri);
            var net = Net;
            bool queued = net.Run(() =>
            {
                Api.Result r = net.FollowUsers(bareId, follow);
                bool ok = r.Ok && Api.ProfileFollowAnswer.WriteSucceeded(r.Body, follow);
                int status = r.Status;
                Post(() => SettleFollow(scope, slot, id, follow, snap, ok, status));
            });
            if (!queued) SettleFollow(scope, slot, id, follow, snap, ok: false, status: 0);
        }

        static void SettleFollow(Scope scope, int slot, EntityId id, bool follow, FollowSnapshot snap, bool ok, int status)
        {
            s_followPending.Remove((scope.Epoch, slot));
            if (!ReferenceEquals(scope, Entities.Current)) return;                       // C7
            UserTable users = scope.Users;
            if (slot <= Table.None || slot >= users.Count || users.Id[slot] != id) return;  // recycled since the click
            if (ok) UserFollowWrite.Confirm(users, slot, in snap);
            else
            {
                UserFollowWrite.Revert(users, slot, in snap);
                Log.Warn("library", "follow write refused (" + (follow ? "follow" : "unfollow") + ", status " + status + ")");
                Notify.Say(Loc.Get(status == 0 ? "drag.libraryUnavailable" : "library.writeFailed"), InfoBarSeverity.Error,
                           dedupeKey: "library.write-failed");
            }
            Entities.Publish();
        }
    }
}
