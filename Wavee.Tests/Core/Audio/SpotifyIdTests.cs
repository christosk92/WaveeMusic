using FluentAssertions;
using Wavee.Core.Audio;
using Xunit;

namespace Wavee.Tests.Core.Audio;

/// <summary>
/// Tests for SpotifyId struct.
/// Validates 128-bit Spotify ID encoding/decoding (base62, base16, URI).
/// </summary>
public class SpotifyIdTests
{
    // Known test values from real Spotify IDs
    private const string TestBase62 = "4iV5W9uYEdYUVa79Axb7Rh";
    private const string TestBase16 = "6d1f91b13f7f4f1bb6a90e6a4b8b3f7d";
    private const string TestTrackUri = "spotify:track:4iV5W9uYEdYUVa79Axb7Rh";

    [Fact]
    public void FromUri_ValidTrackUri_ReturnsCorrectId()
    {
        // ============================================================
        // WHY: Parsing a valid Spotify track URI should extract the
        //      correct SpotifyId with Track type.
        // ============================================================

        // Act
        var id = SpotifyId.FromUri(TestTrackUri);

        // Assert
        id.Type.Should().Be(SpotifyIdType.Track, "URI specifies track type");
        id.ToBase62().Should().Be(TestBase62, "Base62 should match URI ID portion");
    }

    [Fact]
    public void FromUri_ValidAlbumUri_ReturnsAlbumType()
    {
        // ============================================================
        // WHY: Album URIs should be correctly identified as Album type.
        // ============================================================

        // Arrange
        var albumUri = $"spotify:album:{TestBase62}";

        // Act
        var id = SpotifyId.FromUri(albumUri);

        // Assert
        id.Type.Should().Be(SpotifyIdType.Album);
    }

    [Fact]
    public void FromUri_ValidArtistUri_ReturnsArtistType()
    {
        // ============================================================
        // WHY: Artist URIs should be correctly identified as Artist type.
        // ============================================================

        // Arrange
        var artistUri = $"spotify:artist:{TestBase62}";

        // Act
        var id = SpotifyId.FromUri(artistUri);

        // Assert
        id.Type.Should().Be(SpotifyIdType.Artist);
    }

    [Theory]
    [InlineData("invalid:track:abc")]
    [InlineData("spotify")]
    [InlineData("")]
    public void FromUri_InvalidPrefix_ThrowsArgumentException(string invalidUri)
    {
        // ============================================================
        // WHY: Invalid URI formats must be rejected with a clear error.
        // ============================================================

        // Act
        Action act = () => SpotifyId.FromUri(invalidUri);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromUri_NullUri_ThrowsArgumentException()
    {
        // ============================================================
        // WHY: Null URI must be rejected.
        // ============================================================

        // Act
        Action act = () => SpotifyId.FromUri(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromBase62_ValidString_ReturnsCorrectBytes()
    {
        // ============================================================
        // WHY: Base62 decoding must produce correct 128-bit value.
        // ============================================================

        // Act
        var id = SpotifyId.FromBase62(TestBase62);

        // Assert
        id.ToBase62().Should().Be(TestBase62, "Round-trip should preserve value");
        var raw = id.ToRaw();
        raw.Should().HaveCount(SpotifyId.RawLength, "Raw ID is 16 bytes");
    }

    [Fact]
    public void FromBase62_InvalidLength_ThrowsArgumentException()
    {
        // ============================================================
        // WHY: Base62 IDs must be exactly 22 characters.
        // ============================================================

        // Act
        Action actTooShort = () => SpotifyId.FromBase62("abc");
        Action actTooLong = () => SpotifyId.FromBase62("4iV5W9uYEdYUVa79Axb7RhX");

        // Assert
        actTooShort.Should().Throw<ArgumentException>();
        actTooLong.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromBase62_InvalidCharacter_ThrowsArgumentException()
    {
        // ============================================================
        // WHY: Invalid characters must be rejected.
        // ============================================================

        // Arrange - replace valid char with invalid one
        var invalidBase62 = "4iV5W9uYEdYUVa79Axb7R!"; // '!' is not in base62

        // Act
        Action act = () => SpotifyId.FromBase62(invalidBase62);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToBase62_RoundTrip_PreservesValue()
    {
        // ============================================================
        // WHY: Converting to base62 and back should preserve the original value.
        // ============================================================

        // Arrange
        var original = SpotifyId.FromBase62(TestBase62, SpotifyIdType.Track);

        // Act
        var base62 = original.ToBase62();
        var restored = SpotifyId.FromBase62(base62, SpotifyIdType.Track);

        // Assert
        base62.Should().Be(TestBase62);
        restored.Should().Be(original);
    }

    [Fact]
    public void ToBase16_ReturnsLowercaseHexString()
    {
        // ============================================================
        // WHY: Base16 representation should be lowercase hex, 32 chars.
        // ============================================================

        // Arrange
        var id = SpotifyId.FromBase62(TestBase62);

        // Act
        var hex = id.ToBase16();

        // Assert
        hex.Should().HaveLength(32, "Hex is 32 characters for 16 bytes");
        hex.Should().MatchRegex("^[0-9a-f]+$", "Should be lowercase hex only");
    }

    [Fact]
    public void FromBase16_ValidHex_RoundTrips()
    {
        // ============================================================
        // WHY: Base16 parsing and encoding should round-trip correctly.
        // ============================================================

        // Arrange
        var id1 = SpotifyId.FromBase62(TestBase62);
        var hex = id1.ToBase16();

        // Act
        var id2 = SpotifyId.FromBase16(hex);

        // Assert
        id2.ToBase62().Should().Be(id1.ToBase62());
    }

    [Fact]
    public void Type_TrackUri_ReturnsTrack()
    {
        // ============================================================
        // WHY: Type property should reflect the URI type.
        // ============================================================

        // Act
        var id = SpotifyId.FromUri(TestTrackUri);

        // Assert
        id.Type.Should().Be(SpotifyIdType.Track);
    }

    [Fact]
    public void ToUri_ReturnsCorrectFormat()
    {
        // ============================================================
        // WHY: ToUri should produce a valid Spotify URI.
        // ============================================================

        // Arrange
        var id = SpotifyId.FromUri(TestTrackUri);

        // Act
        var uri = id.ToUri();

        // Assert
        uri.Should().Be(TestTrackUri);
    }

    [Fact]
    public void Equals_SameId_ReturnsTrue()
    {
        // ============================================================
        // WHY: Equal IDs should be considered equal.
        // ============================================================

        // Arrange
        var id1 = SpotifyId.FromBase62(TestBase62, SpotifyIdType.Track);
        var id2 = SpotifyId.FromBase62(TestBase62, SpotifyIdType.Track);

        // Assert
        id1.Equals(id2).Should().BeTrue();
        (id1 == id2).Should().BeTrue();
        (id1 != id2).Should().BeFalse();
    }

    [Fact]
    public void Equals_DifferentId_ReturnsFalse()
    {
        // ============================================================
        // WHY: Different IDs should not be considered equal.
        // ============================================================

        // Arrange
        var id1 = SpotifyId.FromBase62(TestBase62, SpotifyIdType.Track);
        var id2 = SpotifyId.FromBase62("0000000000000000000000", SpotifyIdType.Track);

        // Assert
        id1.Equals(id2).Should().BeFalse();
        (id1 == id2).Should().BeFalse();
    }

    [Fact]
    public void Equals_SameIdDifferentType_ReturnsFalse()
    {
        // ============================================================
        // WHY: Same bytes but different type should not be equal.
        // ============================================================

        // Arrange
        var id1 = SpotifyId.FromBase62(TestBase62, SpotifyIdType.Track);
        var id2 = SpotifyId.FromBase62(TestBase62, SpotifyIdType.Album);

        // Assert
        id1.Equals(id2).Should().BeFalse();
    }

    [Fact]
    public void TryFromUri_ValidUri_ReturnsTrue()
    {
        // ============================================================
        // WHY: TryFromUri should return true for valid URIs.
        // ============================================================

        // Act
        var result = SpotifyId.TryFromUri(TestTrackUri, out var id);

        // Assert
        result.Should().BeTrue();
        id.ToBase62().Should().Be(TestBase62);
    }

    [Fact]
    public void TryFromUri_InvalidUri_ReturnsFalse()
    {
        // ============================================================
        // WHY: TryFromUri should return false for invalid URIs without throwing.
        // ============================================================

        // Act
        var result = SpotifyId.TryFromUri("invalid", out var id);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void FromRaw_ValidBytes_CreatesId()
    {
        // ============================================================
        // WHY: Creating from raw bytes should work correctly.
        // ============================================================

        // Arrange
        var bytes = new byte[16];
        bytes[0] = 0x01;
        bytes[15] = 0xFF;

        // Act
        var id = SpotifyId.FromRaw(bytes);

        // Assert
        var raw = id.ToRaw();
        raw.Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public void FromRaw_WrongLength_ThrowsArgumentException()
    {
        // ============================================================
        // WHY: Raw bytes must be exactly 16 bytes.
        // ============================================================

        // Act
        Action actTooShort = () => SpotifyId.FromRaw(new byte[10]);
        Action actTooLong = () => SpotifyId.FromRaw(new byte[20]);

        // Assert
        actTooShort.Should().Throw<ArgumentException>();
        actTooLong.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetHashCode_SameId_ReturnsSameHash()
    {
        // ============================================================
        // WHY: Equal IDs should have the same hash code.
        // ============================================================

        // Arrange
        var id1 = SpotifyId.FromBase62(TestBase62, SpotifyIdType.Track);
        var id2 = SpotifyId.FromBase62(TestBase62, SpotifyIdType.Track);

        // Assert
        id1.GetHashCode().Should().Be(id2.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsUri()
    {
        // ============================================================
        // WHY: ToString should return the URI representation.
        // ============================================================

        // Arrange
        var id = SpotifyId.FromUri(TestTrackUri);

        // Assert
        id.ToString().Should().Be(TestTrackUri);
    }
}
