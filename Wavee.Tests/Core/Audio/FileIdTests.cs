using FluentAssertions;
using Wavee.Core.Audio;
using Xunit;

namespace Wavee.Tests.Core.Audio;

/// <summary>
/// Tests for FileId struct.
/// Validates 20-byte audio file identifier handling.
/// </summary>
public class FileIdTests
{
    // Test file ID (20 bytes as hex = 40 characters)
    private const string TestHex = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void FromBytes_ValidBytes_CreatesFileId()
    {
        // ============================================================
        // WHY: Creating from exactly 20 bytes should succeed.
        // ============================================================

        // Arrange
        var bytes = new byte[20];
        for (int i = 0; i < 20; i++)
            bytes[i] = (byte)i;

        // Act
        var fileId = FileId.FromBytes(bytes);

        // Assert
        fileId.IsValid.Should().BeTrue();
        fileId.Raw.Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public void FromBytes_WrongLength_ThrowsArgumentException()
    {
        // ============================================================
        // WHY: FileId must be exactly 20 bytes.
        // ============================================================

        // Act
        Action actTooShort = () => FileId.FromBytes(new byte[10]);
        Action actTooLong = () => FileId.FromBytes(new byte[25]);
        Action actEmpty = () => FileId.FromBytes(Array.Empty<byte>());

        // Assert
        actTooShort.Should().Throw<ArgumentException>()
            .WithMessage("*20 bytes*");
        actTooLong.Should().Throw<ArgumentException>()
            .WithMessage("*20 bytes*");
        actEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromBytes_NullArray_ThrowsArgumentNullException()
    {
        // ============================================================
        // WHY: Null bytes must be rejected.
        // ============================================================

        // Act
        Action act = () => FileId.FromBytes((byte[])null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToBase16_ReturnsLowercaseHex()
    {
        // ============================================================
        // WHY: Base16 representation should be lowercase hex, 40 chars.
        // ============================================================

        // Arrange
        var bytes = Convert.FromHexString(TestHex);
        var fileId = FileId.FromBytes(bytes);

        // Act
        var hex = fileId.ToBase16();

        // Assert
        hex.Should().HaveLength(40, "Hex is 40 characters for 20 bytes");
        hex.Should().MatchRegex("^[0-9a-f]+$", "Should be lowercase hex only");
        hex.Should().Be(TestHex);
    }

    [Fact]
    public void FromBase16_ValidHex_CreatesFileId()
    {
        // ============================================================
        // WHY: Parsing valid hex should create correct FileId.
        // ============================================================

        // Act
        var fileId = FileId.FromBase16(TestHex);

        // Assert
        fileId.IsValid.Should().BeTrue();
        fileId.ToBase16().Should().Be(TestHex);
    }

    [Fact]
    public void FromBase16_WrongLength_ThrowsArgumentException()
    {
        // ============================================================
        // WHY: Hex string must be exactly 40 characters.
        // ============================================================

        // Act
        Action actTooShort = () => FileId.FromBase16("0123456789");
        Action actTooLong = () => FileId.FromBase16(TestHex + "00");

        // Assert
        actTooShort.Should().Throw<ArgumentException>()
            .WithMessage("*40 characters*");
        actTooLong.Should().Throw<ArgumentException>()
            .WithMessage("*40 characters*");
    }

    [Fact]
    public void FromBase16_InvalidHex_ThrowsException()
    {
        // ============================================================
        // WHY: Invalid hex characters must be rejected.
        // ============================================================

        // Arrange - 40 chars but with invalid hex character
        var invalidHex = "0123456789abcdef0123456789abcdef0123456z";

        // Act
        Action act = () => FileId.FromBase16(invalidHex);

        // Assert
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void TryFromBase16_ValidHex_ReturnsTrue()
    {
        // ============================================================
        // WHY: TryFromBase16 should return true for valid hex.
        // ============================================================

        // Act
        var result = FileId.TryFromBase16(TestHex, out var fileId);

        // Assert
        result.Should().BeTrue();
        fileId.IsValid.Should().BeTrue();
    }

    [Fact]
    public void TryFromBase16_InvalidHex_ReturnsFalse()
    {
        // ============================================================
        // WHY: TryFromBase16 should return false without throwing.
        // ============================================================

        // Act
        var result = FileId.TryFromBase16("invalid", out var fileId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Equals_SameBytes_ReturnsTrue()
    {
        // ============================================================
        // WHY: FileIds with same bytes should be equal.
        // ============================================================

        // Arrange
        var fileId1 = FileId.FromBase16(TestHex);
        var fileId2 = FileId.FromBase16(TestHex);

        // Assert
        fileId1.Equals(fileId2).Should().BeTrue();
        (fileId1 == fileId2).Should().BeTrue();
        (fileId1 != fileId2).Should().BeFalse();
    }

    [Fact]
    public void Equals_DifferentBytes_ReturnsFalse()
    {
        // ============================================================
        // WHY: FileIds with different bytes should not be equal.
        // ============================================================

        // Arrange
        var fileId1 = FileId.FromBase16(TestHex);
        var fileId2 = FileId.FromBase16("ffffffffffffffffffffffffffffffffffffffff");

        // Assert
        fileId1.Equals(fileId2).Should().BeFalse();
        (fileId1 == fileId2).Should().BeFalse();
    }

    [Fact]
    public void Equals_DefaultFileIds_AreEqual()
    {
        // ============================================================
        // WHY: Default (empty) FileIds should be equal to each other.
        // ============================================================

        // Arrange
        var fileId1 = default(FileId);
        var fileId2 = FileId.Empty;

        // Assert
        fileId1.Equals(fileId2).Should().BeTrue();
    }

    [Fact]
    public void Empty_IsNotValid()
    {
        // ============================================================
        // WHY: Empty/default FileId should report as not valid.
        // ============================================================

        // Assert
        FileId.Empty.IsValid.Should().BeFalse();
        default(FileId).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Raw_ReturnsCopy()
    {
        // ============================================================
        // WHY: Raw should return a copy to prevent mutation.
        // ============================================================

        // Arrange
        var fileId = FileId.FromBase16(TestHex);
        var raw1 = fileId.Raw;

        // Act - modify returned array
        raw1[0] = 0xFF;
        var raw2 = fileId.Raw;

        // Assert
        raw2[0].Should().NotBe(0xFF, "Original should not be modified");
    }

    [Fact]
    public void WriteRaw_WritesToSpan()
    {
        // ============================================================
        // WHY: WriteRaw should write bytes to destination span.
        // ============================================================

        // Arrange
        var fileId = FileId.FromBase16(TestHex);
        Span<byte> destination = stackalloc byte[20];

        // Act
        fileId.WriteRaw(destination);

        // Assert
        destination.ToArray().Should().BeEquivalentTo(fileId.Raw);
    }

    [Fact]
    public void WriteRaw_DestinationTooSmall_ThrowsArgumentException()
    {
        // ============================================================
        // WHY: Destination span must be at least 20 bytes.
        // ============================================================

        // Arrange
        var fileId = FileId.FromBase16(TestHex);
        var destination = new byte[10];

        // Act
        Action act = () => fileId.WriteRaw(destination);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*at least 20 bytes*");
    }

    [Fact]
    public void GetHashCode_SameBytes_ReturnsSameHash()
    {
        // ============================================================
        // WHY: Equal FileIds should have the same hash code.
        // ============================================================

        // Arrange
        var fileId1 = FileId.FromBase16(TestHex);
        var fileId2 = FileId.FromBase16(TestHex);

        // Assert
        fileId1.GetHashCode().Should().Be(fileId2.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsBase16()
    {
        // ============================================================
        // WHY: ToString should return the hex representation.
        // ============================================================

        // Arrange
        var fileId = FileId.FromBase16(TestHex);

        // Assert
        fileId.ToString().Should().Be(TestHex);
    }

    [Fact]
    public void Empty_ToString_ReturnsEmptyString()
    {
        // ============================================================
        // WHY: Empty FileId should return empty string for ToString.
        // ============================================================

        // Assert
        FileId.Empty.ToString().Should().BeEmpty();
    }
}
