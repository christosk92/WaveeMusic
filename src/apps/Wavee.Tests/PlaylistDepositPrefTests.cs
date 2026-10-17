// ── Wavee.Tests/PlaylistDepositPrefTests.cs — the "recently filed into" MRU as a typed pref (C16, X41) ────────────────
//
// The ORDER and the codec are `PlaylistDepositTargets`' (PlaylistDepositTargetsTests); these facts pin the settings
// door: the persisted MRU reads back newest first, a re-file into the front playlist costs no settings write, and a
// uri that is not a real Spotify playlist never reaches the store.

using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class PlaylistDepositPrefTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public PlaylistDepositPrefTests() => Platform.UseSettings(_store);
    public void Dispose() => Platform.UseSettings(null);

    const string A = "spotify:playlist:aaa", B = "spotify:playlist:bbb";

    [Fact]
    public void Nothing_filed_yet_is_an_empty_list()
        => Assert.Empty(PlaylistDepositPref.Recents());

    [Fact]
    public void Remembering_persists_newest_first()
    {
        Assert.True(PlaylistDepositPref.Remember(A));
        Assert.True(PlaylistDepositPref.Remember(B));

        Assert.Equal(new[] { B, A }, PlaylistDepositPref.Recents());
        Assert.Equal(B + "\n" + A, _store.Get(Platform.Keys.PlaylistDepositRecents));
    }

    [Fact]
    public void Re_filing_into_the_front_playlist_costs_no_settings_write()
    {
        PlaylistDepositPref.Remember(A);
        uint before = Platform.SettingsEpoch;

        Assert.False(PlaylistDepositPref.Remember(A));

        Assert.Equal(before, Platform.SettingsEpoch);
    }

    [Fact]
    public void Re_filing_into_an_older_playlist_moves_it_back_to_the_front()
    {
        PlaylistDepositPref.Remember(A);
        PlaylistDepositPref.Remember(B);

        Assert.True(PlaylistDepositPref.Remember(A));

        Assert.Equal(new[] { A, B }, PlaylistDepositPref.Recents());
    }

    [Theory]
    [InlineData("wavee:playlist:local")]
    [InlineData("spotify:collection:tracks")]
    [InlineData("spotify:album:xyz")]
    [InlineData("")]
    public void A_uri_that_is_not_a_spotify_playlist_is_never_stored(string uri)
    {
        Assert.False(PlaylistDepositPref.Remember(uri));
        Assert.False(_store.WasWritten(Platform.Keys.PlaylistDepositRecents));
    }
}
