// ── Entities/Friends.cs — CORE: the friend-activity relation's staged form and its commit (#277) ─────────────────────
//
// `Edges.Friends` is ONE run under the synthetic parent `Rail.Friends.Feed`. A worker cannot resolve slots (C1), so a
// decoded friend crosses as five identities and a time; the commit resolves them and rewrites the run newest first. Every
// answer is the WHOLE feed, so a batch always replaces the run. The listening-activity feed names only the user, the
// track and the context, so a friend's album and artist come from the TRACK row once it has loaded: the commit takes
// them when the row already knows them, and `RefillFriends` fills the rest as track rows land. Session-only: never
// persisted (Store.cs §6). The host is `Spotify.Friends`; plan: docs/plans/wavee/friend-activity-implementation.md.

using FluentGpu.Foundation;

namespace Wavee;

/// <summary>One decoded friend: identities as the wire gave them (the commit resolves them); an empty album or artist is
/// read off the track row.</summary>
public struct StagedFriend
{
    public StagedId User, Track, Album, Artist, Context;
    public long TimestampMs;
}

public sealed partial class Staging
{
    StagedList<StagedFriend>? _friends;
    StagedList<StagedFriend>? _friendsMarker;
    /// <inheritdoc cref="StagedFriend"/>
    public StagedList<StagedFriend> Friends => _friends ??= Register(new StagedList<StagedFriend>());
    internal StagedList<StagedFriend>? StagedFriends => _friends;

    /// <summary>This batch IS the friends feed (an empty one included): the commit replaces the run. A one-row list rather
    /// than a field, so <see cref="Reset"/> clears it with every other list.</summary>
    public void FriendsFeedAnswered()
    {
        var m = _friendsMarker ??= Register(new StagedList<StagedFriend>());
        m.Clear();
        m.Add();
    }

    internal bool AnswersFriends => _friendsMarker is { Count: > 0 };
}

public static partial class Entities
{
    /// <summary>The last friends commit's drops: staged friends whose user or track did not resolve, as <c>count</c> and
    /// the kinds of the ids that failed (<c>track:Episode</c>), for the host's always-on seed line.</summary>
    public static (int Count, string Kinds) LastFriendsDropped { get; private set; }

    // UI thread only (C1); grown to the widest feed seen and never again (P8).
    static int[] s_friendTargets = new int[32];
    static FriendEdge[] s_friendPayload = new FriendEdge[32];

    /// <summary>Land a friends feed after the rows it names: the run becomes the staged friends, one per user (the
    /// newest), newest first, Complete (empty included).</summary>
    static partial void CommitFriends(Staging s)
    {
        if (!s.AnswersFriends) return;
        var staged = s.StagedFriends is { } f ? f.Span : default;
        GrowFriends(staged.Length);
        int n = 0, dropped = 0;
        string dropKinds = "";
        foreach (ref readonly var x in staged)
        {
            int user = s.Slot(Current.Users, in x.User);
            int track = s.Slot(Current.Tracks, in x.Track);
            if (user == Table.None || track == Table.None)
            {
                dropped++;
                string kind = user == Table.None ? "user:" + x.User.Kind(s) : "track:" + x.Track.Kind(s);
                if (!dropKinds.Contains(kind, StringComparison.Ordinal)) dropKinds += (dropKinds.Length > 0 ? "," : "") + kind;
                continue;
            }
            int album = x.Album.IsEmpty ? 0 : s.Slot(Current.Albums, in x.Album);
            int artist = x.Artist.IsEmpty ? 0 : s.Slot(Current.Artists, in x.Artist);
            // The context slot is a PLAYLIST slot (Rail.Friends.TargetOf); an album or artist context falls back to the
            // album / artist the row names.
            int context = x.Context.IsEmpty || x.Context.Kind(s) != EntityKind.Playlist ? 0 : s.Slot(Current.Playlists, in x.Context);
            var edge = FromTrack(new FriendEdge(user, x.TimestampMs, track, album, artist, context));
            int dup = -1;
            for (int i = 0; i < n; i++) if (s_friendPayload[i].UserSlot == user) { dup = i; break; }
            if (dup < 0) s_friendPayload[n++] = edge;
            else if (edge.TimestampMs > s_friendPayload[dup].TimestampMs) s_friendPayload[dup] = edge;
        }
        LastFriendsDropped = (dropped, dropKinds);
        LandFriends(n);
    }

    /// <summary>Fill every row's missing album / artist from its track row, now that more track rows may have loaded.
    /// Rewrites the run only when a row gained one; answers whether it did. UI thread (C1).</summary>
    public static bool RefillFriends()
    {
        var held = Current.Edges.Friends.Payload(Rail.Friends.Feed);
        if (held.IsEmpty) return false;
        GrowFriends(held.Length);
        bool changed = false;
        for (int i = 0; i < held.Length; i++)
        {
            var e = FromTrack(held[i]);
            changed |= e != held[i];
            s_friendPayload[i] = e;
        }
        if (changed) LandFriends(held.Length);
        return changed;
    }

    /// <summary>The track row's album and first artist, where the edge has none.</summary>
    static FriendEdge FromTrack(FriendEdge e)
    {
        if (e.TrackSlot <= 0 || (e.AlbumSlot > 0 && e.ArtistSlot > 0)) return e;
        var track = new Track(e.TrackSlot);
        int album = e.AlbumSlot > 0 ? e.AlbumSlot : track.AlbumSlot;
        int artist = e.ArtistSlot;
        if (artist <= 0 && track.ArtistSlots is { Length: > 0 } artists) artist = artists[0];
        return e with { AlbumSlot = album, ArtistSlot = artist };
    }

    static void LandFriends(int n)
    {
        var rows = s_friendPayload.AsSpan(0, n);
        rows.Sort(static (a, b) => b.TimestampMs.CompareTo(a.TimestampMs));
        for (int i = 0; i < n; i++) s_friendTargets[i] = rows[i].UserSlot;
        Current.Edges.Friends.Replace(Rail.Friends.Feed, s_friendTargets.AsSpan(0, n), rows, EdgeState.Complete, n);
    }

    static void GrowFriends(int n)
    {
        if (n <= s_friendTargets.Length) return;
        int size = Math.Max(n, s_friendTargets.Length * 2);
        s_friendTargets = new int[size];
        s_friendPayload = new FriendEdge[size];
    }
}
