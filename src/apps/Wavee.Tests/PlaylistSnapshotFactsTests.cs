using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class PlaylistSnapshotFactsTests
{
    [Theory]
    [InlineData(null, "-")]
    [InlineData(new byte[] { }, "-")]
    [InlineData(new byte[] { 0x00, 0xAB }, "00ab")]
    [InlineData(new byte[] { 0x00, 0xCD, 0xFF }, "00cd")]
    public void ShortRev_TakesTheFirstTwoBytesAsLowerHex(byte[]? rev, string expected)
        => Assert.Equal(expected, PlaylistSnapshotFacts.ShortRev(rev));

    [Fact]
    public void CoverId_UsesTheImageIdSpanNotTheFullUrl()
    {
        var image = new Image("https://i.scdn.co/image/ab67616d0000b273e86f30ec6f14a30f1cf9bb9d");
        Assert.Equal("ab67616d0000b273e86f30ec6f14a30f1cf9bb9d", PlaylistSnapshotFacts.CoverId(image));
        Assert.Equal("-", PlaylistSnapshotFacts.CoverId(null));
    }

    [Fact]
    public void NameChanged_IsOrdinalAndTreatsNullAsEmpty()
    {
        Assert.True(PlaylistSnapshotFacts.NameChanged("Tuesday morning", "Wednesday afternoon"));
        Assert.False(PlaylistSnapshotFacts.NameChanged("daylist", "daylist"));
        Assert.True(PlaylistSnapshotFacts.NameChanged(null, "Wednesday afternoon"));
        Assert.False(PlaylistSnapshotFacts.NameChanged(null, ""));
    }

    [Fact]
    public void HeadUid_IsTheFirstMembershipItemId()
    {
        Assert.Equal("", PlaylistSnapshotFacts.HeadUid([]));
        Assert.Equal("uid-1", PlaylistSnapshotFacts.HeadUid(
        [
            new PlaylistMember("uid-1", "spotify:track:a", null, 0),
            new PlaylistMember("uid-2", "spotify:track:b", null, 0),
        ]));
    }
}
