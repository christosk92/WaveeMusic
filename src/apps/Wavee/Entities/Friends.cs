// ── Entities/Friends.cs — CORE: the friend-activity relation's staged form and its commit (#277) ─────────────────────
//
// `Edges.Friends` is ONE run under the synthetic parent `Rail.Friends.Feed`. A worker cannot resolve slots (C1), so a
// decoded friend crosses as five identities and a time; the commit resolves them and rewrites the run newest first. A seed
// REPLACES the run; a presence delta UPSERTS or REMOVES one user's row. Session-only: never persisted (Store.cs §6). The
// host that fills it is `Spotify.Friends` (Spotify.Friends.cs); plan: docs/plans/wavee/friend-activity-implementation.md.

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>What a staged friends batch does to the run.</summary>
public enum FriendsOp : byte { None, Replace, Upsert, Remove }

/// <summary>One decoded friend: identities as the wire gave them (the commit resolves them). The commit orders the run.</summary>
public struct StagedFriend
{
    public StagedId User, Track, Album, Artist, Context;
    public long TimestampMs;
}

/// <summary>The batch's header: what it does to the run, and the user an Upsert replaces or a Remove drops. A one-row list
/// (not a field) so <see cref="Staging.Reset"/> clears it with every other list.</summary>
public struct StagedFriendsBatch
{
    public FriendsOp Op;
    public StagedId User;
}

public sealed partial class Staging
{
    StagedList<StagedFriend>? _friends;
    StagedList<StagedFriendsBatch>? _friendsBatch;
    /// <inheritdoc cref="StagedFriend"/>
    public StagedList<StagedFriend> Friends => _friends ??= Register(new StagedList<StagedFriend>());
    internal StagedList<StagedFriend>? StagedFriends => _friends;
    internal StagedList<StagedFriendsBatch>? StagedFriendsBatches => _friendsBatch;

    /// <summary>Say what this batch does to the friends run (once per batch; a later call overrides).</summary>
    public void FriendsBatch(FriendsOp op, in StagedId user = default)
    {
        var list = _friendsBatch ??= Register(new StagedList<StagedFriendsBatch>());
        list.Clear();
        ref var b = ref list.Add();
        b.Op = op;
        b.User = user;
    }

    /// <summary>What this batch does to the friends run (<see cref="FriendsOp.None"/> when nobody said).</summary>
    public FriendsOp FriendsOpOf => _friendsBatch is { Count: > 0 } b ? b[0].Op : FriendsOp.None;
}

public static partial class Entities
{
    // UI thread only (C1); grown to the widest feed seen and never again (P8).
    static int[] s_friendTargets = new int[32];
    static FriendEdge[] s_friendPayload = new FriendEdge[32];

    /// <summary>Land a friends batch after the rows it names: a Replace rewrites the run from the staged friends; an
    /// Upsert keeps every held row but the named user's and adds the staged one; a Remove keeps every held row but the
    /// named user's. The run is sorted newest first and settles Complete, empty included.</summary>
    static partial void CommitFriends(Staging s)
    {
        if (s.StagedFriendsBatches is not { Count: > 0 } batches) return;
        var batch = batches[0];
        if (batch.Op == FriendsOp.None) return;
        var staged = s.StagedFriends is { } f ? f.Span : default;
        var table = Current.Edges.Friends;
        var held = table.Payload(Rail.Friends.Feed);

        GrowFriends(held.Length + staged.Length);
        int n = 0;
        int dropUser = batch.User.IsEmpty ? Table.None : s.Slot(Current.Users, in batch.User);
        if (batch.Op != FriendsOp.Replace)
            foreach (ref readonly var e in held)
                if (e.UserSlot != dropUser) s_friendPayload[n++] = e;

        foreach (ref readonly var x in staged)
        {
            int user = s.Slot(Current.Users, in x.User);
            int track = s.Slot(Current.Tracks, in x.Track);
            if (user == Table.None || track == Table.None) continue;
            int album = x.Album.IsEmpty ? 0 : s.Slot(Current.Albums, in x.Album);
            int artist = x.Artist.IsEmpty ? 0 : s.Slot(Current.Artists, in x.Artist);
            // The context slot is a PLAYLIST slot (Rail.Friends.TargetOf); an album or artist context falls back to the
            // album / artist the row already names.
            int context = x.Context.IsEmpty || x.Context.Kind(s) != EntityKind.Playlist ? 0 : s.Slot(Current.Playlists, in x.Context);
            // A seed that names one user twice keeps the newest.
            int dup = -1;
            for (int i = 0; i < n; i++) if (s_friendPayload[i].UserSlot == user) { dup = i; break; }
            var edge = new FriendEdge(user, x.TimestampMs, track, album, artist, context);
            if (dup < 0) s_friendPayload[n++] = edge;
            else if (edge.TimestampMs > s_friendPayload[dup].TimestampMs) s_friendPayload[dup] = edge;
        }

        var rows = s_friendPayload.AsSpan(0, n);
        rows.Sort(static (a, b) => b.TimestampMs.CompareTo(a.TimestampMs));
        for (int i = 0; i < n; i++) s_friendTargets[i] = rows[i].UserSlot;
        table.Replace(Rail.Friends.Feed, s_friendTargets.AsSpan(0, n), rows, EdgeState.Complete, n);
    }

    static void GrowFriends(int n)
    {
        if (n <= s_friendTargets.Length) return;
        int size = Math.Max(n, s_friendTargets.Length * 2);
        s_friendTargets = new int[size];
        s_friendPayload = new FriendEdge[size];
    }
}
