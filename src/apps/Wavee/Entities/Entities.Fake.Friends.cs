// ── Entities/Entities.Fake.Friends.cs — the --fake friend-activity feed, as the wire spells it (#277) ─────────────────
//
// `Spotify.Friends` lands this through the REAL decoder (`Spotify.Decode.ListeningFeed`) and commit, so `--fake` and the
// headless inspector exercise the same path a signed-in run does. Like the real listening-activity feed it names only
// identities; the seeded catalogue supplies the words (and the album / artist through the track rows). The two friends the
// seed does not know carry their own names. Times are relative to `nowMs`: two friends are playing, three are older.

using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class Entities
{
    /// <summary>The fake <c>listening-activity/v1/feed</c> answer at <paramref name="nowMs"/> (unix ms).</summary>
    public static byte[] FakeFriendFeed(long nowMs)
    {
        var b = new StringBuilder(2048);
        b.Append("{\"entities\":[");
        Friend(b, nowMs - 30_000, true, "spotify:user:christos", null, 4, "spotify:playlist:pl1");
        Friend(b, nowMs - 90_000, true, "spotify:user:sam-okafor", "Sam Okafor", 19, null);
        Friend(b, nowMs - 12 * 60_000, false, "spotify:user:alex-rivers", null, 12, "spotify:playlist:pl3");
        Friend(b, nowMs - 3 * 3_600_000, false, "spotify:user:mia-solace", null, 27, null);
        Friend(b, nowMs - 2 * 86_400_000L, false, "spotify:user:jun-park", "Jun Park", 40, "spotify:album:al1");
        b.Length--;                                           // the last comma
        b.Append("]}");
        return Encoding.UTF8.GetBytes(b.ToString());
    }

    /// <summary>One <c>entities[]</c> object as the live feed nests it: <c>{userEntity:{uri, name?, activity:{…}}}</c>, the
    /// activity on track <paramref name="track"/> of the pool.</summary>
    static void Friend(StringBuilder b, long ts, bool playing, string user, string? userName, int track, string? context)
    {
        b.Append("{\"userEntity\":{\"uri\":\"").Append(user).Append('"');
        if (userName is not null) b.Append(",\"name\":\"").Append(userName).Append('"');
        b.Append(",\"activity\":{\"entityUri\":\"").Append(TrackUri(track)).Append('"');
        b.Append(",\"timestamp\":\"").Append(DateTimeOffset.FromUnixTimeMilliseconds(ts).ToString("O", CultureInfo.InvariantCulture)).Append('"');
        if (context is not null) b.Append(",\"contextUri\":\"").Append(context).Append('"');
        b.Append(",\"isPlaying\":").Append(playing ? "true" : "false");
        b.Append("}}},");                                  // activity, userEntity, the entity
    }
}
