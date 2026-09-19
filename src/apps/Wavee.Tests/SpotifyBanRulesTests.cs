using Wavee;
using Xunit;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

public class SpotifyBanRulesTests
{
    [Fact]
    public void An_explicit_track_ban_excludes_the_track_without_artist_metadata()
        => Assert.True(SpotifyBanRules.Blocked("spotify:track:a", [], new HashSet<string> { "spotify:track:a" }, new HashSet<string>()));

    [Fact]
    public void Any_banned_artist_excludes_a_multi_artist_track()
        => Assert.True(SpotifyBanRules.Blocked("spotify:track:a", ["spotify:artist:allowed", "spotify:artist:banned"],
            new HashSet<string>(), new HashSet<string> { "spotify:artist:banned" }));

    [Fact]
    public void Unrelated_bans_do_not_exclude_an_episode_or_track()
        => Assert.False(SpotifyBanRules.Blocked("spotify:episode:a", [], new HashSet<string> { "spotify:track:a" },
            new HashSet<string> { "spotify:artist:banned" }));

    [Fact]
    public void A_collection_delta_removes_unbanned_items_and_retains_the_rest()
    {
        var snapshot = new HashSet<string> { "spotify:track:old", "spotify:track:keep" };
        Spotify.Library.ApplyBanItems(snapshot,
        [
            new Col.CollectionItem { Uri = "spotify:track:old", IsRemoved = true },
            new Col.CollectionItem { Uri = "spotify:track:new" },
        ]);
        Assert.DoesNotContain("spotify:track:old", snapshot);
        Assert.Contains("spotify:track:keep", snapshot);
        Assert.Contains("spotify:track:new", snapshot);
    }

    [Fact]
    public void Applying_the_same_delta_twice_is_idempotent()
    {
        var snapshot = new HashSet<string>();
        Col.CollectionItem[] delta = [new() { Uri = "spotify:artist:banned" }];
        Spotify.Library.ApplyBanItems(snapshot, delta);
        Spotify.Library.ApplyBanItems(snapshot, delta);
        Assert.Single(snapshot);
    }
}
