using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>Pins the split between the STRICT relation a card's play button toggles on and the LOOSE relation the
/// equalizer reveals on. An artist/album card of the playing track must light up but must NOT own pause/resume.</summary>
public sealed class NowPlayingOverlayMatchTests
{
    const string Context = "spotify:playlist:pl1";
    const string TrackUri = "spotify:track:t1";
    const string AlbumUri = "spotify:album:al1";
    const string ArtistUri = "spotify:artist:ar1";
    const string OtherArtistUri = "spotify:artist:ar2";

    static Track Playing() => new(
        "t1", TrackUri, "T",
        [new ArtistRef("ar1", ArtistUri, "A1"), new ArtistRef("ar2", OtherArtistUri, "A2")],
        new AlbumRef("al1", AlbumUri, "Al"), 0, false, null);

    [Fact]
    public void ArtistOfPlayingTrack_RelatesButDoesNotOwn()
    {
        var t = Playing();
        Assert.True(NowPlayingMatch.RelatesToPlaying(ArtistUri, Context, t));
        Assert.True(NowPlayingMatch.RelatesToPlaying(OtherArtistUri, Context, t));
        Assert.False(NowPlayingMatch.OwnsPlayback(ArtistUri, Context, t));
        Assert.False(NowPlayingMatch.OwnsPlayback(OtherArtistUri, Context, t));
        Assert.False(NowPlayingMatch.MatchesContext(ArtistUri, Context));
    }

    [Fact]
    public void AlbumOfPlayingTrack_RelatesButDoesNotOwn()
    {
        var t = Playing();
        Assert.True(NowPlayingMatch.RelatesToPlaying(AlbumUri, Context, t));
        Assert.False(NowPlayingMatch.OwnsPlayback(AlbumUri, Context, t));
    }

    [Fact]
    public void PlayingContext_OwnsAndRelates()
    {
        var t = Playing();
        Assert.True(NowPlayingMatch.MatchesContext(Context, Context));
        Assert.True(NowPlayingMatch.OwnsPlayback(Context, Context, t));
        Assert.True(NowPlayingMatch.RelatesToPlaying(Context, Context, t));
        // The context alone is enough — no track needed (the moment before the first track resolves).
        Assert.True(NowPlayingMatch.OwnsPlayback(Context, Context, null));
        Assert.True(NowPlayingMatch.RelatesToPlaying(Context, Context, null));
    }

    [Fact]
    public void PlayingTrack_OwnsAndRelates_WithoutBeingTheContext()
    {
        var t = Playing();
        Assert.False(NowPlayingMatch.MatchesContext(TrackUri, Context));
        Assert.True(NowPlayingMatch.MatchesTrack(TrackUri, t));
        Assert.True(NowPlayingMatch.OwnsPlayback(TrackUri, Context, t));
        Assert.True(NowPlayingMatch.RelatesToPlaying(TrackUri, Context, t));
    }

    [Fact]
    public void UnrelatedUri_NeitherOwnsNorRelates()
    {
        var t = Playing();
        Assert.False(NowPlayingMatch.OwnsPlayback("spotify:album:elsewhere", Context, t));
        Assert.False(NowPlayingMatch.RelatesToPlaying("spotify:album:elsewhere", Context, t));
    }

    [Fact]
    public void EmptyUri_NeverMatches()
    {
        var t = Playing();
        Assert.False(NowPlayingMatch.MatchesContext("", Context));
        Assert.False(NowPlayingMatch.MatchesTrack("", t));
        Assert.False(NowPlayingMatch.OwnsPlayback("", Context, t));
        Assert.False(NowPlayingMatch.RelatesToPlaying("", Context, t));
    }

    [Fact]
    public void Idle_NoContextNoTrack_NeverMatches()
    {
        Assert.False(NowPlayingMatch.OwnsPlayback(Context, null, null));
        Assert.False(NowPlayingMatch.RelatesToPlaying(Context, null, null));
        Assert.False(NowPlayingMatch.RelatesToPlaying(ArtistUri, "", null));
    }

    [Fact]
    public void Comparison_IsCaseInsensitive()
    {
        var t = Playing();
        Assert.True(NowPlayingMatch.MatchesContext(Context.ToUpperInvariant(), Context));
        Assert.True(NowPlayingMatch.OwnsPlayback(TrackUri.ToUpperInvariant(), null, t));
        Assert.True(NowPlayingMatch.RelatesToPlaying(ArtistUri.ToUpperInvariant(), null, t));
    }
}
