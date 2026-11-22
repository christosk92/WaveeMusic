// ── Entities/Entities.Fake.Friends.cs — the --fake friend-activity feed, as the wire spells it (#277) ─────────────────
//
// `Spotify.Friends` lands this through the REAL decoder (`Spotify.Decode.FriendFeed`) and commit, so `--fake` and the
// headless inspector exercise the same path a signed-in run does. Catalogue rows (the seeded tracks, albums, artists,
// playlists and users) are named by URI ONLY: a friend answer is staged Thin, which outranks the seed, so a name here
// would rename the catalogue. Only the two friends the seed does not know carry their own names. Times are relative to
// `nowMs`: two rows are live (within `Rail.Friends.LiveWindowMs`), three are older.

using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class Entities
{
    /// <summary>The fake <c>init-friend-feed</c> answer at <paramref name="nowMs"/> (unix ms).</summary>
    public static byte[] FakeFriendFeed(long nowMs)
    {
        var b = new StringBuilder(2048);
        b.Append("{\"friends\":[");
        Friend(b, nowMs - 30_000, "spotify:user:christos", null, 4, "spotify:playlist:pl1");
        Friend(b, nowMs - 90_000, "spotify:user:sam-okafor", "Sam Okafor", 19, null);
        Friend(b, nowMs - 12 * 60_000, "spotify:user:alex-rivers", null, 12, "spotify:playlist:pl3");
        Friend(b, nowMs - 3 * 3_600_000, "spotify:user:mia-solace", null, 27, null);
        Friend(b, nowMs - 2 * 86_400_000L, "spotify:user:jun-park", "Jun Park", 40, "spotify:album:al1");
        b.Length--;                                           // the last comma
        b.Append("]}");
        return Encoding.UTF8.GetBytes(b.ToString());
    }

    /// <summary>One friend object: track <paramref name="track"/> of the pool, its album and artist by the seed's own
    /// spread (track i sits on album i mod 13), and an optional context.</summary>
    static void Friend(StringBuilder b, long ts, string user, string? userName, int track, string? context)
    {
        b.Append("{\"timestamp\":").Append(ts.ToString(CultureInfo.InvariantCulture));
        b.Append(",\"user\":{\"uri\":\"").Append(user).Append('"');
        if (userName is not null) b.Append(",\"name\":\"").Append(userName).Append('"');
        b.Append("},\"track\":{\"uri\":\"").Append(TrackUri(track)).Append('"');
        b.Append(",\"album\":{\"uri\":\"").Append(AlbumUri(track % AlbumCount)).Append("\"}");
        b.Append(",\"artist\":{\"uri\":\"").Append(ArtistUri(track)).Append("\"}");
        if (context is not null) b.Append(",\"context\":{\"uri\":\"").Append(context).Append("\",\"index\":0}");
        b.Append("}},");
    }
}
