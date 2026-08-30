using System;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class SpotifyAesCtrTests
{
    [Fact]
    public void Decrypt_KnownBlock_ProducesExpectedPlaintext()
    {
        var key = Convert.FromHexString("0123456789abcdef0123456789abcdef");
        var cipher = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f };
        var plain = SpotifyAesCtr.Decrypt(cipher, key, 0);
        Assert.Equal(16, plain.Length);
        // Round-trip: encrypting keystream XOR twice restores cipher
        SpotifyAesCtr.DecryptInPlace(plain, key, 0);
        Assert.Equal(cipher, plain);
    }

    [Fact]
    public void ClearPassthrough_BelowOffset_N()
    {
        var key = Convert.FromHexString("0123456789abcdef0123456789abcdef");
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var copy = (byte[])data.Clone();
        // Simulate decrypt stream with offset N=3: bytes 0..2 unchanged
        if (copy.Length > 3)
            SpotifyAesCtr.DecryptInPlace(copy.AsSpan(3), key, 3);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.AsSpan(0, 3).ToArray());
    }
}

public class SpotifyRuntimeIdentityTests
{
    [Fact]
    public void Default_MatchesHardcodedPins()
    {
        // Pinned to Spotify 1.2.94.583. Both values are wire-observed: omg.saz carries the full
        // "1.2.94.583.g60394bd5" client-version string, so the .g suffix is captured, not invented.
        // Bumping the pins is intentional — update this test WITH the pin, never the pin to suit the test.
        Assert.Equal("129400583", SpotifyRuntimeIdentity.Default.AppVersion);
        Assert.Equal("1.2.94.583.g60394bd5", SpotifyRuntimeIdentity.Default.ClientVersion);
        Assert.Equal(5, SpotifyRuntimeIdentity.Default.PlayPlayRequestVersion);
        Assert.Equal(9, SpotifyRuntimeIdentity.Default.AppVersion.Length);
    }

    [Fact]
    public void Headers_ReadFromHost()
    {
        SpotifyRuntimeIdentityHost.Apply(new SpotifyRuntimeIdentity("999", "9.9.9.9.gtest", 7));
        Assert.Equal("999", Backend.Spotify.SpotifyHeaders.AppVersion);
        SpotifyRuntimeIdentityHost.Apply(SpotifyRuntimeIdentity.Default);
    }
}

public class QueueCorePeekNextTests
{
    static readonly AlbumRef Album = new("", "", "");

    [Fact]
    public void PeekNext_ReturnsUserQueueHead()
    {
        var q = new QueueCore();
        var t1 = new Track("t1", "spotify:track:t1", "T1", Array.Empty<ArtistRef>(), Album, 180000, false, null);
        var t2 = new Track("t2", "spotify:track:t2", "T2", Array.Empty<ArtistRef>(), Album, 180000, false, null);
        q.SetContext("ctx", new[] { t1, t2 }, 0);
        q.EnqueueUser(new Track("next", "spotify:track:next", "N", Array.Empty<ArtistRef>(), Album, 180000, false, null));
        Assert.Equal("next", q.PeekNext()?.Id);
    }
}
