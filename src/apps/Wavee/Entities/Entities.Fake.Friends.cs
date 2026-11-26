// ── Entities/Entities.Fake.Friends.cs — the --fake friend-activity feed, as the wire spells it (#277) ─────────────────
//
// `Spotify.Friends` lands this through the REAL decoder (`Spotify.Decode.ListeningFeed`) and commit, so `--fake` and the
// headless inspector exercise the same path a signed-in run does. Like the real listening-activity feed it names only
// identities (`followEntity: {uri, activity}`, no names); the seeded catalogue supplies the words (and the album / artist
// through the track rows), and `SeedFriendSurfaces` seeds the two friends no other surface names, as extended-metadata
// would. Times are relative to `nowMs`: two friends are playing, three are older.

using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class Entities
{
    const string FakeFriendSam = "spotify:user:sam-okafor", FakeFriendJun = "spotify:user:jun-park";

    /// <summary>The fake <c>listening-activity/v1/feed</c> answer at <paramref name="nowMs"/> (unix ms).</summary>
    public static byte[] FakeFriendFeed(long nowMs)
    {
        var b = new StringBuilder(2048);
        b.Append("{\"entities\":[");
        Friend(b, nowMs - 30_000, true, "spotify:user:christos", 4, "spotify:playlist:pl1");
        Friend(b, nowMs - 90_000, true, FakeFriendSam, 19, null);
        Friend(b, nowMs - 12 * 60_000, false, "spotify:user:alex-rivers", 12, "spotify:playlist:pl3");
        Friend(b, nowMs - 3 * 3_600_000, false, "spotify:user:mia-solace", 27, null);
        Friend(b, nowMs - 2 * 86_400_000L, false, FakeFriendJun, 40, "spotify:album:al1");
        b.Length--;                                           // the last comma
        b.Append("]}");
        return Encoding.UTF8.GetBytes(b.ToString());
    }

    /// <summary>One <c>entities[]</c> object as the live feed nests it: <c>{followEntity:{uri, activity:{…}}}</c>, the
    /// activity on track <paramref name="track"/> of the pool.</summary>
    static void Friend(StringBuilder b, long ts, bool playing, string user, int track, string? context)
    {
        b.Append("{\"followEntity\":{\"uri\":\"").Append(user).Append('"');
        b.Append(",\"activity\":{\"entityUri\":\"").Append(TrackUri(track)).Append('"');
        b.Append(",\"timestamp\":\"").Append(DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append('"');
        if (context is not null) b.Append(",\"contextUri\":\"").Append(context).Append('"');
        b.Append(",\"isPlaying\":").Append(playing ? "true" : "false");
        b.Append("}}},");                                  // activity, followEntity, the entity
    }

    /// <summary>The two friends only the feed names: their identity rows, as extended-metadata (178/179) would land them.</summary>
    static partial void SeedFriendSurfaces(long now0)
    {
        _ = now0;
        var s = Staging.Rent();
        try
        {
            ReadOnlySpan<(string Uri, string Name, int Cover)> friends = [(FakeFriendSam, "Sam Okafor", 6), (FakeFriendJun, "Jun Park", 8)];
            foreach (var (uri, name, cover) in friends)
            {
                ref var row = ref s.Users.RowFor(s.AddText(Utf8(uri)), Authority.Seed, (uint)UserFields.Identity);
                row.Name = s.AddText(Utf8(name));
                row.Image = s.AddText(Utf8(Cover(cover)));
            }
            Commit(s);
        }
        finally { Staging.Return(s); }
    }
}
